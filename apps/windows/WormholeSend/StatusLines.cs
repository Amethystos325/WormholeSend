using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WormholeSend;

/// A one-line state's icon, what it says, and what may follow on the right
/// (giving way in the middle, a label, or at its end, a text).
sealed record StatusText(string Glyph, Brush Color, string Text, string? Trailing = null, bool TrimEnd = false);

/// The one-line states: what they say, and how wide that makes the island.
/// As on macOS, it is as wide as the text, from the state's usual width up
/// to the window's; past that the text shrinks to 85 %, then gives way at
/// its end. The model sizes the island (and its drop area) by this, the
/// contents draw it.
static class StatusLines
{
    const double MaxWidth = 528;
    const double Shrunk = 0.85;
    /// Padding on both sides; slack for the pixel rounding of Display mode.
    const double Padding = 32, Slack = 4;
    const double TrailingMax = 140;

    public static StatusText? For(IslandModel model, Phase phase) => phase switch
    {
        Phase.Unavailable u => new(Glyphs.NoNetwork, Palette.Secondary, u.Text),
        Phase.Done when model.Transfer is { } t => new(Glyphs.CheckFilled, Palette.Success,
            L.T($"已送达 {t.Device.Name}", $"Delivered to {t.Device.Name}", $"{t.Device.Name} に送信しました"), t.Label),
        Phase.Refused { NeedsRepair: true } r => new(Glyphs.Blocked, Palette.Warning,
            L.T($"{r.Name} 需要重新配对，未发送", $"{r.Name} needs re-pairing. Not sent", $"{r.Name} は再ペアリングが必要です。送信していません")),
        Phase.Refused r => new(Glyphs.Blocked, Palette.Warning,
            L.T($"{r.Name} 离线，未发送", $"{r.Name} is offline. Not sent", $"{r.Name} はオフラインです。送信していません")),
        Phase.PairWaiting p => new(Glyphs.Clock, Palette.Secondary,
            L.T($"等待 {p.Alias} 确认…", $"Waiting for {p.Alias} to confirm…", $"{p.Alias} の確認を待っています…")),
        // A notice's detail is the start of a text: it gives way at its end.
        Phase.Notice { Success: true } n => new(Glyphs.CheckFilled, Palette.Success, n.Text, n.Detail, TrimEnd: true),
        Phase.Notice n => new(Glyphs.WarningFilled, Palette.Warning, n.Text, n.Detail, TrimEnd: true),
        Phase.Interrupted i => new(Glyphs.WarningFilled, Palette.Warning, L.T("传输中断", "Transfer interrupted", "転送が中断されました"), i.Reason),
        _ => null,
    };

    public static string FailedText(string reason) =>
        L.T($"发送失败：{reason}", $"Couldn't send: {reason}", $"送信失敗：{reason}");

    public static string RetryTitle => L.T("重试", "Retry", "再試行");

    /// The island's width for a one-line state, and the scale of its text;
    /// null for the other states.
    public static (double Width, double Scale)? Fit(IslandModel model, Phase phase)
    {
        Func<double, double>? content = phase switch
        {
            // The black hole and the hint.
            Phase.Hint => scale => 20 + 7 + Measure(model.HintText, 12.5 * scale, FontWeights.Medium),
            Phase.Failed f => scale => 16 + 10 + Measure(FailedText(f.Reason), 13 * scale, FontWeights.Medium)
                + (f.CanRetry ? 8 + 24 + Measure(RetryTitle, 12, FontWeights.SemiBold) : 0),
            _ when For(model, phase) is { } line => scale => 17 + 10 + Measure(line.Text, 13 * scale, FontWeights.Medium)
                + (line.Trailing is { } trailing ? 8 + Math.Min(TrailingMax, Measure(trailing, 11 * scale, FontWeights.Normal)) : 0),
            _ => null,
        };
        if (content is null) return null;
        var usual = phase switch
        {
            Phase.Hint => 260,
            Phase.Unavailable => 300,
            _ => 340,
        };
        var natural = Padding + content(1) + Slack;
        return natural <= MaxWidth ? (Math.Max(usual, Math.Ceiling(natural)), 1) : (MaxWidth, Shrunk);
    }

    static readonly Dictionary<(string, double, FontWeight, FontFamily), double> widths = [];

    static double Measure(string text, double size, FontWeight weight)
    {
        var font = Fonts.Ui;
        if (widths.TryGetValue((text, size, weight, font), out var width)) return width;
        if (widths.Count > 256) widths.Clear();
        width = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black, 1)
            .WidthIncludingTrailingWhitespace;
        widths[(text, size, weight, font)] = width;
        return width;
    }
}
