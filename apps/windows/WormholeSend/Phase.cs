namespace WormholeSend;

/// The island's phases (design doc, section 2.1), the same as `Phase` in
/// apps/macos/.../IslandModel.swift, plus the pairing request and the
/// short notices the macOS spike doesn't have yet.
abstract record Phase
{
    public sealed record Idle() : Phase;
    /// A file drag is in progress, but not over the island.
    public sealed record Hint() : Phase;
    /// Over the island with the default device online: release sends to
    /// it, dragging down into the "其他设备" strip opens the list.
    public sealed record Target() : Phase;
    public sealed record Picker(int? Hot) : Phase;
    public sealed record Unavailable(string Text) : Phase;
    public sealed record Sending() : Phase;
    public sealed record Queued(int Position) : Phase;
    /// Sending to a LocalSend device: its user has to accept first.
    public sealed record AwaitingAcceptance(string Alias) : Phase;
    public sealed record Done() : Phase;
    public sealed record Failed(string Reason) : Phase;
    /// Released over a device that can't take files ("离线", "需要重新配对").
    public sealed record Refused(string Name, string Reason = "离线") : Phase;
    public sealed record Receiving() : Phase;
    public sealed record Received() : Phase;
    /// An incoming transfer stopped: the sender cancelled or the connection
    /// broke; the partial files are gone. The reason shows on the right.
    public sealed record Interrupted(string Reason) : Phase;
    /// Another device asks to pair: compare the code, then accept or decline.
    public sealed record PairRequest(string PairId, string Alias, string Code) : Phase;
    /// Accepted here; the other side still has to confirm.
    public sealed record PairWaiting(string Alias) : Phase;
    /// A LocalSend device asks to send files (`Label`, `Total` bytes), or a
    /// text message (`Text`), which is copied when accepted.
    public sealed record ReceiveRequest(string RequestId, string Alias, string Label, long Total, string? Text) : Phase;
    /// A short message: "已与 … 配对", "… 已解除配对", "已复制到剪贴板"
    /// with the start of the text as `Detail`.
    public sealed record Notice(string Text, bool Success, string? Detail = null) : Phase;

    /// Phases in which the island is the drop target under the cursor.
    public bool IsOverIsland => this is Target or Picker or Unavailable;

    /// Phases in which the island takes drags: these draw the hit halo.
    public bool AcceptsDrag => this is Hint or Target or Picker or Unavailable;

    /// For the log, in the macOS app's spelling.
    public string Describe() => this switch
    {
        Picker p => $"picker(hot: {p.Hot?.ToString() ?? "nil"})",
        Unavailable u => $"unavailable(\"{u.Text}\")",
        Queued q => $"queued(position: {q.Position})",
        Failed f => $"failed(\"{f.Reason}\")",
        Refused r => $"refused(\"{r.Name}\")",
        PairRequest p => $"pairRequest(\"{p.Alias}\", {p.Code})",
        PairWaiting p => $"pairWaiting(\"{p.Alias}\")",
        AwaitingAcceptance a => $"awaitingAcceptance(\"{a.Alias}\")",
        ReceiveRequest r => $"receiveRequest(\"{r.Alias}\", \"{r.Label}\")",
        Notice n => $"notice(\"{n.Text}\")",
        Interrupted i => $"interrupted(\"{i.Reason}\")",
        _ => char.ToLowerInvariant(GetType().Name[0]) + GetType().Name[1..],
    };
}
