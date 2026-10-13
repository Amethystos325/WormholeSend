//! The language of what the core says to the user: errors, the reasons of
//! `pair.result` and `transfer.finished`, the label of `receive.request`.
//! The shell sets it (core-ffi's `language`); Chinese until then. `tr!`
//! picks a text, written out in all three languages where it is used.

use std::sync::atomic::{AtomicU8, Ordering};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Lang {
    #[default]
    Zh,
    En,
    Ja,
}

static CURRENT: AtomicU8 = AtomicU8::new(0);

impl Lang {
    /// "zh", "ja" or "en", or a language tag such as "zh-Hans-CN" or
    /// "ja-JP"; any other language is English.
    pub fn parse(tag: &str) -> Lang {
        let tag = tag.to_ascii_lowercase();
        if tag == "zh" || tag.starts_with("zh-") {
            Lang::Zh
        } else if tag == "ja" || tag.starts_with("ja-") {
            Lang::Ja
        } else {
            Lang::En
        }
    }

    pub fn code(self) -> &'static str {
        match self {
            Lang::Zh => "zh",
            Lang::En => "en",
            Lang::Ja => "ja",
        }
    }
}

/// For the whole process: messages made from now on use it.
pub fn set(lang: Lang) {
    CURRENT.store(lang as u8, Ordering::Relaxed);
}

pub fn current() -> Lang {
    match CURRENT.load(Ordering::Relaxed) {
        1 => Lang::En,
        2 => Lang::Ja,
        _ => Lang::Zh,
    }
}

/// `tr!("中文", "English", "日本語", args…)`: the text in the current
/// language, formatted as `format!` does (inline `{name}` works too).
#[macro_export]
macro_rules! tr {
    ($zh:literal, $en:literal, $ja:literal $(, $arg:expr)* $(,)?) => {
        match $crate::i18n::current() {
            $crate::i18n::Lang::Zh => format!($zh $(, $arg)*),
            $crate::i18n::Lang::En => format!($en $(, $arg)*),
            $crate::i18n::Lang::Ja => format!($ja $(, $arg)*),
        }
    };
}
