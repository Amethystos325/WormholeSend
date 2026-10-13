//! The receive queue (design doc, section 5.4). The transfer service takes
//! one upload session at a time; senders take a ticket from the control
//! service first and wait their turn, first come first served.

use crate::control::dto::QueueStatus;
use crate::event::CoreEvent;
use std::collections::VecDeque;
use std::sync::Mutex;
use std::time::{Duration, Instant};
use tokio::sync::{Notify, mpsc};

const MAX_WAITING: usize = 8;
/// Once it is a sender's turn, only it may start a session for this long.
const RESERVATION: Duration = Duration::from_secs(10);
/// A ticket that has not been polled for this long is dropped (the sender
/// went away).
const TICKET_TTL: Duration = Duration::from_secs(35);
/// The longest a single long poll waits.
const LONG_POLL: Duration = Duration::from_secs(30);

pub(crate) struct ReceiveQueue {
    state: Mutex<State>,
    changed: Notify,
    events: mpsc::UnboundedSender<CoreEvent>,
}

#[derive(Default)]
struct State {
    /// Who may send now: reserved after their turn came, or in a session.
    holder: Option<Holder>,
    waiting: VecDeque<Ticket>,
    reported: usize,
}

struct Holder {
    fingerprint: String,
    ticket: String,
    /// `Some` while reserved, `None` once the session started.
    reserved_until: Option<Instant>,
}

struct Ticket {
    id: String,
    fingerprint: String,
    last_poll: Instant,
}

impl ReceiveQueue {
    pub(crate) fn new(events: mpsc::UnboundedSender<CoreEvent>) -> Self {
        Self {
            state: Mutex::new(State::default()),
            changed: Notify::new(),
            events,
        }
    }

    /// Takes a ticket. `None` when the queue is full.
    pub(crate) fn join(&self, fingerprint: &str) -> Option<QueueStatus> {
        let mut state = self.state.lock().unwrap();
        self.expire(&mut state);
        if let Some(holder) = state.holder.as_ref().filter(|h| h.fingerprint == fingerprint) {
            return Some(ready(&holder.ticket));
        }
        if let Some(index) = state.waiting.iter().position(|t| t.fingerprint == fingerprint) {
            state.waiting[index].last_poll = Instant::now();
            return Some(waiting(&state.waiting[index].id, index + 1));
        }
        let ticket = uuid::Uuid::new_v4().to_string();
        if state.holder.is_none() && state.waiting.is_empty() {
            state.holder = Some(Holder {
                fingerprint: fingerprint.to_string(),
                ticket: ticket.clone(),
                reserved_until: Some(Instant::now() + RESERVATION),
            });
            return Some(ready(&ticket));
        }
        if state.waiting.len() >= MAX_WAITING {
            return None;
        }
        state.waiting.push_back(Ticket {
            id: ticket.clone(),
            fingerprint: fingerprint.to_string(),
            last_poll: Instant::now(),
        });
        let position = state.waiting.len();
        self.report(&mut state);
        Some(waiting(&ticket, position))
    }

    /// Long poll: returns as soon as it is this ticket's turn or its position
    /// improved, else after [`LONG_POLL`] with the current position. `None`
    /// for an unknown ticket.
    pub(crate) async fn wait(&self, ticket: &str, fingerprint: &str) -> Option<QueueStatus> {
        let deadline = tokio::time::Instant::now() + LONG_POLL;
        let mut first_position = None;
        loop {
            let notified = self.changed.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            let position = {
                let mut state = self.state.lock().unwrap();
                self.expire(&mut state);
                if let Some(holder) = state.holder.as_ref().filter(|h| h.ticket == ticket) {
                    return Some(ready(&holder.ticket));
                }
                let index = state
                    .waiting
                    .iter()
                    .position(|t| t.id == ticket && t.fingerprint == fingerprint)?;
                state.waiting[index].last_poll = Instant::now();
                if index == 0 && state.holder.is_none() {
                    let next = state.waiting.pop_front().expect("ticket at the front");
                    state.holder = Some(Holder {
                        fingerprint: next.fingerprint,
                        ticket: next.id.clone(),
                        reserved_until: Some(Instant::now() + RESERVATION),
                    });
                    self.report(&mut state);
                    return Some(ready(&next.id));
                }
                index + 1
            };
            // Someone ahead left: report the new position right away.
            match first_position {
                None => first_position = Some(position),
                Some(first) if position < first => return Some(waiting(ticket, position)),
                Some(_) => {}
            }
            tokio::select! {
                _ = &mut notified => {}
                // Reservations and stale tickets expire without a notification.
                _ = tokio::time::sleep(Duration::from_secs(1)) => {}
                _ = tokio::time::sleep_until(deadline) => return Some(waiting(ticket, position)),
            }
        }
    }

    /// The sender gave up waiting, or gave back its turn.
    pub(crate) fn leave(&self, ticket: &str, fingerprint: &str) {
        let mut state = self.state.lock().unwrap();
        state.waiting.retain(|t| !(t.id == ticket && t.fingerprint == fingerprint));
        if state
            .holder
            .as_ref()
            .is_some_and(|h| h.ticket == ticket && h.fingerprint == fingerprint && h.reserved_until.is_some())
        {
            state.holder = None;
        }
        self.report(&mut state);
        drop(state);
        self.changed.notify_waiters();
    }

    /// A paired sender asks to start a session. Allowed when it holds the
    /// turn, or when nobody holds or waits for it.
    pub(crate) fn admit(&self, fingerprint: &str) -> bool {
        let mut state = self.state.lock().unwrap();
        self.expire(&mut state);
        let nobody_waits = state.waiting.is_empty();
        match &mut state.holder {
            Some(holder) if holder.fingerprint == fingerprint => {
                holder.reserved_until = None;
                true
            }
            Some(_) => false,
            None if nobody_waits => {
                state.holder = Some(Holder {
                    fingerprint: fingerprint.to_string(),
                    ticket: uuid::Uuid::new_v4().to_string(),
                    reserved_until: None,
                });
                true
            }
            None => false,
        }
    }

    /// The session of `fingerprint` ended: the next ticket's turn.
    pub(crate) fn session_ended(&self, fingerprint: &str) {
        let mut state = self.state.lock().unwrap();
        if state.holder.as_ref().is_some_and(|h| h.fingerprint == fingerprint) {
            state.holder = None;
        }
        drop(state);
        self.changed.notify_waiters();
    }

    /// Drops expired reservations and stale tickets; call periodically.
    pub(crate) fn sweep(&self) {
        let mut state = self.state.lock().unwrap();
        self.expire(&mut state);
    }

    fn expire(&self, state: &mut State) {
        let now = Instant::now();
        let mut changed = false;
        if state
            .holder
            .as_ref()
            .and_then(|h| h.reserved_until)
            .is_some_and(|until| until <= now)
        {
            state.holder = None;
            changed = true;
        }
        let before = state.waiting.len();
        state.waiting.retain(|t| now.duration_since(t.last_poll) < TICKET_TTL);
        changed |= state.waiting.len() != before;
        if changed {
            self.report(state);
            self.changed.notify_waiters();
        }
    }

    /// Tells the shell how many senders wait, when that changed.
    fn report(&self, state: &mut State) {
        let count = state.waiting.len();
        if count != state.reported {
            state.reported = count;
            let _ = self.events.send(CoreEvent::ReceiveWaiting { count });
        }
    }
}

fn ready(ticket: &str) -> QueueStatus {
    QueueStatus {
        ticket: ticket.to_string(),
        position: 0,
        ready: true,
    }
}

fn waiting(ticket: &str, position: usize) -> QueueStatus {
    QueueStatus {
        ticket: ticket.to_string(),
        position,
        ready: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn queue() -> ReceiveQueue {
        ReceiveQueue::new(mpsc::unbounded_channel().0)
    }

    #[test]
    fn first_sender_is_ready_others_wait_in_order() {
        let q = queue();
        assert!(q.join("A").unwrap().ready);
        let b = q.join("B").unwrap();
        let c = q.join("C").unwrap();
        assert_eq!((b.ready, b.position), (false, 1));
        assert_eq!((c.ready, c.position), (false, 2));
        // Joining again keeps the place.
        assert_eq!(q.join("C").unwrap().ticket, c.ticket);
    }

    #[test]
    fn only_the_holder_is_admitted() {
        let q = queue();
        q.join("A").unwrap();
        q.join("B").unwrap();
        assert!(!q.admit("B"));
        assert!(q.admit("A"));
        assert!(!q.admit("B"));
    }

    #[test]
    fn idle_queue_admits_without_ticket() {
        let q = queue();
        assert!(q.admit("A"));
        assert!(!q.join("B").unwrap().ready);
    }

    #[tokio::test]
    async fn next_ticket_becomes_ready_after_the_session() {
        let q = std::sync::Arc::new(queue());
        q.join("A").unwrap();
        assert!(q.admit("A"));
        let b = q.join("B").unwrap();
        let waiter = tokio::spawn({
            let q = q.clone();
            let ticket = b.ticket.clone();
            async move { q.wait(&ticket, "B").await }
        });
        tokio::time::sleep(Duration::from_millis(50)).await;
        q.session_ended("A");
        let status = waiter.await.unwrap().unwrap();
        assert!(status.ready);
        assert!(q.admit("B"));
    }

    #[tokio::test]
    async fn waiting_reports_a_better_position_early() {
        let q = std::sync::Arc::new(queue());
        q.join("A").unwrap();
        let b = q.join("B").unwrap();
        let c = q.join("C").unwrap();
        let waiter = tokio::spawn({
            let q = q.clone();
            let ticket = c.ticket.clone();
            async move { q.wait(&ticket, "C").await }
        });
        tokio::time::sleep(Duration::from_millis(50)).await;
        q.leave(&b.ticket, "B");
        let status = tokio::time::timeout(Duration::from_secs(2), waiter).await.unwrap().unwrap().unwrap();
        assert_eq!((status.ready, status.position), (false, 1));
    }

    #[test]
    fn leaving_gives_up_the_place() {
        let q = queue();
        q.join("A").unwrap();
        let b = q.join("B").unwrap();
        q.join("C").unwrap();
        q.leave(&b.ticket, "B");
        assert_eq!(q.join("C").unwrap().position, 1);
    }

    #[test]
    fn queue_is_bounded() {
        let q = queue();
        q.join("holder").unwrap();
        for i in 0..MAX_WAITING {
            assert!(q.join(&format!("s{i}")).is_some());
        }
        assert!(q.join("one too many").is_none());
    }
}
