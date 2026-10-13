using System.Text.Json;

namespace WormholeSend;

/// The core's events (core/src/event.rs, design doc 4.2). The fake backend
/// emits the same ones, so the island behaves alike with and without a core.
abstract record CoreEvent
{
    public static CoreEvent? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var e = document.RootElement;
        string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        string? N(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long L(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        bool B(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        bool Sending() => S("direction") == "send";

        return S("event") switch
        {
            "device.updated" => new DeviceUpdated(S("id"), S("alias"), N("deviceType"), B("online"), B("paired")),
            "transfer.started" => new TransferStarted(S("id"), Sending(), S("deviceId"), S("alias"), (int)L("files"), L("total")),
            "transfer.progress" => new TransferProgress(S("id"), Sending(), S("deviceId"), L("bytes"), L("total")),
            "transfer.finished" => new TransferFinished(
                S("id"), Sending(), S("deviceId"), S("alias"), S("status"), (int)L("filesDone"), L("bytes"),
                e.TryGetProperty("savedPaths", out var paths) && paths.ValueKind == JsonValueKind.Array
                    ? paths.EnumerateArray().Select(p => p.GetString() ?? "").ToList()
                    : [],
                N("error")),
            "transfer.queued" => new TransferQueued(S("id"), S("deviceId"), (int)L("position")),
            "receive.waiting" => new ReceiveWaiting((int)L("count")),
            "receive.rejected" => new ReceiveRejected(S("deviceId"), S("alias"), S("ip")),
            "pair.incoming" => new PairIncoming(S("pairId"), S("deviceId"), S("alias")),
            "pair.code" => new PairCode(S("pairId"), S("deviceId"), S("alias"), S("code")),
            "pair.result" => new PairResult(S("pairId"), S("deviceId"), S("alias"), B("ok"), N("reason")),
            "device.unpaired" => new DeviceUnpaired(S("id"), S("alias")),
            "error" => new CoreError(S("message")),
            _ => null,
        };
    }
}

sealed record DeviceUpdated(string Id, string Alias, string? DeviceType, bool Online, bool Paired) : CoreEvent;
sealed record TransferStarted(string Id, bool Sending, string DeviceId, string Alias, int Files, long Total) : CoreEvent;
sealed record TransferProgress(string Id, bool Sending, string DeviceId, long Bytes, long Total) : CoreEvent;
/// Status: done, failed, cancelled, rejected (403), busy (409), offline.
sealed record TransferFinished(
    string Id, bool Sending, string DeviceId, string Alias, string Status, int FilesDone, long Bytes,
    IReadOnlyList<string> SavedPaths, string? Error) : CoreEvent;
sealed record TransferQueued(string Id, string DeviceId, int Position) : CoreEvent;
sealed record ReceiveWaiting(int Count) : CoreEvent;
sealed record ReceiveRejected(string DeviceId, string Alias, string Ip) : CoreEvent;
sealed record PairIncoming(string PairId, string DeviceId, string Alias) : CoreEvent;
sealed record PairCode(string PairId, string DeviceId, string Alias, string Code) : CoreEvent;
sealed record PairResult(string PairId, string DeviceId, string Alias, bool Ok, string? Reason) : CoreEvent;
sealed record DeviceUnpaired(string Id, string Alias) : CoreEvent;
sealed record CoreError(string Message) : CoreEvent;
