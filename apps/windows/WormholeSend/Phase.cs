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
    /// A short message: "已与 … 配对", "… 已解除配对".
    public sealed record Notice(string Text, bool Success) : Phase;

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
        Notice n => $"notice(\"{n.Text}\")",
        Interrupted i => $"interrupted(\"{i.Reason}\")",
        _ => char.ToLowerInvariant(GetType().Name[0]) + GetType().Name[1..],
    };
}
