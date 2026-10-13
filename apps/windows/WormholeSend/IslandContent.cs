using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WormholeSend;

/// The content of one phase, inside the island. Built when the layout
/// changes (see `IslandView.ContentKey`), updated in place otherwise, e.g.
/// on every progress tick. Mirrors the views in apps/macos/.../IslandView.swift.
abstract class PhaseContent : DockPanel
{
    protected PhaseContent() => LastChildFill = true;

    public virtual void Update(IslandModel model, Phase phase) { }

    public static PhaseContent Build(IslandModel model, Phase phase, bool compactQueued) => phase switch
    {
        Phase.Hint => new HintContent(model.HintText),
        Phase.Target when model.DefaultDevice is { } device =>
            new TargetContent(device, model.Devices.Where(d => !d.IsDefault).ToList()),
        Phase.Picker when model.Devices.Count == 1 => new SingleTargetContent(model.Devices[0], model.DragLabel),
        Phase.Picker => new PickerContent(model.Devices, model.DragLabel),
        Phase.Unavailable u => new StatusLine(Glyphs.NoNetwork, Palette.Secondary, u.Text),
        Phase.Sending when model.Transfer is { } t =>
            new ProgressContent(t.Device, $"发送到 {t.Device.Name}", model.CancelTransfer),
        Phase.Queued when compactQueued => new CompactQueued(),
        Phase.Queued q => new QueuedContent(q.Position, model.CancelTransfer),
        Phase.Done when model.Transfer is { } t =>
            new StatusLine(Glyphs.CheckFilled, Palette.Success, $"已送达 {t.Device.Name}", t.Label),
        Phase.Failed f => new FailedContent(f.Reason, model.Retry),
        Phase.Refused r => new StatusLine(Glyphs.Blocked, Palette.Warning, $"{r.Name} {r.Reason}，未发送"),
        Phase.Receiving when model.Transfer is { } t => new ReceivingContent(t),
        Phase.Received when model.Transfer is { } t => new ReceivedContent(t, model.RevealReceived),
        Phase.PairRequest p => new PairRequestContent(p, model.AnswerPair),
        Phase.PairWaiting p => new StatusLine(Glyphs.Clock, Palette.Secondary, $"等待 {p.Alias} 确认…"),
        Phase.Notice { Success: true } n => new StatusLine(Glyphs.CheckFilled, Palette.Success, n.Text),
        Phase.Notice n => new StatusLine(Glyphs.WarningFilled, Palette.Warning, n.Text),
        Phase.Interrupted i => new StatusLine(Glyphs.WarningFilled, Palette.Warning, "传输中断", i.Reason),
        _ => new EmptyContent(),
    };
}

sealed class EmptyContent : PhaseContent;

sealed class HintContent : PhaseContent
{
    public HintContent(string text)
    {
        // As tall as the text looks: the black hole fills about half its grid.
        var icon = new Image { Source = AppIcon.BlackHole, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var label = Ui.Text(text, 12.5, Palette.HintText, FontWeights.Medium);
        // Chinese glyphs sit low in their line; a pixel up they look centered.
        label.RenderTransform = new TranslateTransform(0, -1);
        var row = Ui.Row(7, icon, label);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(row);
        // The content height of the hint (IslandModel.BodySizeFor), so the
        // row sits in the middle of the capsule, not below the top padding.
        Height = 22;
        VerticalAlignment = VerticalAlignment.Top;
    }
}

/// The default device on top; along the bottom edge the strip leading to
/// the other devices (its height is `Spec.MoreStripHeight` with padding).
sealed class TargetContent : PhaseContent
{
    public TargetContent(Device device, IReadOnlyList<Device> others)
    {
        var strip = Ui.Dock(new MoreStrip(others), Dock.Bottom);
        strip.Margin = new Thickness(0, 0, 0, 8);
        Children.Add(strip);

        var titles = Ui.Column(3,
            Ui.Text($"松手发送到 {device.Name}", 14, Palette.Text, FontWeights.SemiBold),
            Ui.Text("默认设备", 11, Palette.Secondary));
        titles.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Row(12, new Avatar(device, 44, highlighted: true), titles);
        row.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(row);
    }
}

sealed class MoreStrip : Border
{
    public MoreStrip(IReadOnlyList<Device> others)
    {
        Height = 30;
        CornerRadius = new CornerRadius(10);
        Background = Palette.Strip;
        Padding = new Thickness(10, 0, 10, 0);

        var faces = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (device, index) in others.Take(3).Select((d, i) => (d, i)))
        {
            var face = new Grid { Width = 20, Height = 20, Margin = new Thickness(index == 0 ? 0 : -6, 0, 0, 0) };
            face.Children.Add(new Ellipse { Fill = Palette.Tile, Stroke = Palette.Island, StrokeThickness = 1.5 });
            face.Children.Add(Ui.Icon(device.Glyph, 9, device.Online ? Palette.Text : Palette.Tertiary));
            faces.Children.Add(face);
        }

        var dock = new DockPanel();
        dock.Children.Add(Ui.Dock(faces, Dock.Left));
        var hint = Ui.Row(4, Ui.Text("往下拖动选择", 11, Palette.Secondary), Ui.Icon(Glyphs.ChevronDown, 10, Palette.Secondary));
        hint.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(Ui.Dock(hint, Dock.Right));
        var title = Ui.Text("其他设备", 11.5, Palette.StripText, FontWeights.Medium);
        title.Margin = new Thickness(8, 0, 4, 0);
        dock.Children.Add(title);
        Child = dock;
    }
}

sealed class SingleTargetContent : PhaseContent
{
    public SingleTargetContent(Device device, string label)
    {
        var avatar = Ui.Dock(new Avatar(device, 50, highlighted: true), Dock.Left);
        avatar.Margin = new Thickness(0, 0, 14, 0);
        var titles = Ui.Column(4,
            Ui.Text($"松手发送到 {device.Name}", 15, Palette.Text, FontWeights.SemiBold),
            new MiddleText(label, 11.5, Palette.Secondary));
        titles.VerticalAlignment = VerticalAlignment.Center;
        // A dock rather than a row, so the label knows its width.
        var row = new DockPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(avatar);
        row.Children.Add(titles);
        Children.Add(row);
    }
}

sealed class PickerContent : PhaseContent
{
    readonly List<DeviceTile> tiles = [];

    public PickerContent(IReadOnlyList<Device> devices, string label)
    {
        var title = Ui.Dock(new MiddleText($"把 {label} 发送到", 11, Palette.Secondary), Dock.Top);
        title.Margin = new Thickness(4, 0, 0, 6);
        Children.Add(title);

        var grid = new UniformGrid { Rows = 1, Columns = devices.Count, VerticalAlignment = VerticalAlignment.Top };
        for (var i = 0; i < devices.Count; i++)
        {
            var tile = new DeviceTile(devices[i])
            {
                Margin = new Thickness(i == 0 ? 0 : 3, 0, i == devices.Count - 1 ? 0 : 3, 0),
            };
            tiles.Add(tile);
            grid.Children.Add(tile);
        }
        Children.Add(grid);
    }

    public override void Update(IslandModel model, Phase phase)
    {
        var hot = (phase as Phase.Picker)?.Hot;
        for (var i = 0; i < tiles.Count; i++) tiles[i].Hot = hot == i;
    }
}

sealed class DeviceTile : Border
{
    readonly Device device;
    readonly Avatar avatar;
    readonly ScaleTransform scale = new(1, 1);
    bool hot;

    public DeviceTile(Device device)
    {
        this.device = device;
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(4, 9, 4, 9);
        Background = Brushes.Transparent;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = scale;

        avatar = new Avatar(device);
        var name = Ui.Text(device.Name, 12, device.Online ? Palette.Text : Palette.Secondary, FontWeights.Medium);
        var status = Ui.Text(Status, 10.5, Palette.Secondary);
        foreach (var text in new[] { name, status }) text.HorizontalAlignment = HorizontalAlignment.Center;
        var column = Ui.Column(5, avatar, name, status);
        column.HorizontalAlignment = HorizontalAlignment.Center;
        Child = column;
    }

    string Status => device.NeedsRepair ? "需重新配对" : (device.Online, device.IsDefault) switch
    {
        (true, true) => "在线 · 默认",
        (true, false) => "在线",
        (false, true) => "离线 · 默认",
        _ => "离线",
    };

    public bool Hot
    {
        set
        {
            if (hot == value) return;
            hot = value;
            Background = !value ? Brushes.Transparent : device.Online ? Palette.Tile : Palette.TileHot;
            avatar.Highlighted = value && device.Online;
            var to = value && device.Online ? 1.05 : 1;
            Ui.Animate(scale, ScaleTransform.ScaleXProperty, to, 0.15);
            Ui.Animate(scale, ScaleTransform.ScaleYProperty, to, 0.15);
        }
    }
}

sealed class StatusLine : PhaseContent
{
    public StatusLine(string glyph, Brush color, string text, string? trailing = null)
    {
        var icon = Ui.Dock(Ui.Icon(glyph, 17, color), Dock.Left);
        icon.Margin = new Thickness(0, 0, 10, 0);
        Children.Add(icon);
        if (trailing is not null)
        {
            var label = Ui.Dock(new MiddleText(trailing, 11, Palette.Secondary), Dock.Right);
            label.Margin = new Thickness(8, 0, 0, 0);
            label.MaxWidth = 140;
            Children.Add(label);
        }
        Children.Add(Ui.Text(text, 13, Palette.Text, FontWeights.Medium));
        VerticalAlignment = VerticalAlignment.Top;
        Height = 28;
    }
}

/// The send: its device, the bar, and under it the sizes or files done
/// and the speed.
sealed class ProgressContent : PhaseContent
{
    readonly TextBlock percent = Ui.Percent();
    readonly ProgressLine bar = new(4, Palette.Accent);
    readonly TextBlock detail = Ui.Text("", 11, Palette.Secondary);

    public ProgressContent(Device device, string title, Action onCancel)
    {
        var avatar = Ui.Dock(new Avatar(device, 28, highlighted: true), Dock.Left);
        avatar.Margin = new Thickness(0, 0, 10, 0);
        Children.Add(avatar);
        var cancel = Ui.Dock(new CancelButton(onCancel), Dock.Right);
        cancel.Margin = new Thickness(10, 0, 0, 0);
        Children.Add(cancel);

        var header = new DockPanel();
        header.Children.Add(Ui.Dock(percent, Dock.Right));
        header.Children.Add(Ui.Text(title, 12, Palette.Text, FontWeights.Medium));
        Typography.SetNumeralAlignment(detail, FontNumeralAlignment.Tabular);
        var column = Ui.Column(6, header, bar, detail);
        column.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(column);
        VerticalAlignment = VerticalAlignment.Top;
        Height = 56;
    }

    public override void Update(IslandModel model, Phase phase)
    {
        bar.Value = model.Progress;
        percent.Text = Ui.FormatPercent(model.Progress);
        detail.Text = model.SendDetail;
    }
}

sealed class QueuedContent : PhaseContent
{
    readonly TextBlock title = Ui.Text("", 13, Palette.Text, FontWeights.Medium);

    public QueuedContent(int position, Action onCancel)
    {
        var icon = Ui.Dock(Ui.Icon(Glyphs.Clock, 17, Palette.Warning), Dock.Left);
        icon.Margin = new Thickness(0, 0, 10, 0);
        Children.Add(icon);
        var cancel = Ui.Dock(new CancelButton(onCancel), Dock.Right);
        cancel.Margin = new Thickness(4, 0, 0, 0);
        Children.Add(cancel);
        var column = Ui.Column(2, title, Ui.Text("对方正在接收其他设备的文件，轮到后自动发送", 11, Palette.Secondary));
        column.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(column);
        VerticalAlignment = VerticalAlignment.Top;
        Height = 40;
        SetPosition(position);
    }

    public override void Update(IslandModel model, Phase phase)
    {
        if (phase is Phase.Queued queued) SetPosition(queued.Position);
    }

    void SetPosition(int position) => title.Text = $"排队中 · 前面还有 {position} 个";
}

/// The queued state after a few seconds: a small capsule with a dot.
sealed class CompactQueued : PhaseContent
{
    public CompactQueued()
    {
        Children.Add(Ui.Dock(Ui.Icon(Glyphs.Clock, 12, Palette.Warning), Dock.Left));
        Children.Add(Ui.Dock(new Ellipse { Width = 6, Height = 6, Fill = Palette.Warning, VerticalAlignment = VerticalAlignment.Center }, Dock.Right));
        var title = Ui.Text("排队中", 11.5, Palette.Text, FontWeights.Medium);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(title);
        VerticalAlignment = VerticalAlignment.Center;
    }
}

sealed class FailedContent : PhaseContent
{
    public FailedContent(string reason, Action onRetry)
    {
        var icon = Ui.Dock(Ui.Icon(Glyphs.WarningFilled, 16, Palette.Warning), Dock.Left);
        icon.Margin = new Thickness(0, 0, 10, 0);
        Children.Add(icon);
        Children.Add(Ui.Dock(new PillButton("重试", onRetry), Dock.Right));
        Children.Add(Ui.Text($"发送失败：{reason}", 13, Palette.Text, FontWeights.Medium));
        VerticalAlignment = VerticalAlignment.Top;
        Height = 28;
    }
}

sealed class ReceivingContent : PhaseContent
{
    readonly TextBlock percent = Ui.Percent();
    readonly TextBlock detail = Ui.Text("", 11, Palette.Secondary);
    readonly ProgressLine bar = new(4, Palette.Accent);

    public ReceivingContent(Transfer transfer)
    {
        var header = Ui.Dock(new DockPanel(), Dock.Top);
        var avatar = Ui.Dock(new Avatar(transfer.Device, 30), Dock.Left);
        avatar.Margin = new Thickness(0, 0, 10, 0);
        header.Children.Add(avatar);
        header.Children.Add(Ui.Dock(percent, Dock.Right));
        header.Children.Add(Ui.Column(2,
            Ui.Text($"{transfer.Device.Name} 发来 {transfer.FileCount} 个文件", 13, Palette.Text, FontWeights.Medium),
            detail));
        Children.Add(header);
        bar.Margin = new Thickness(0, 10, 0, 0);
        bar.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(bar);
    }

    public override void Update(IslandModel model, Phase phase)
    {
        bar.Value = model.Progress;
        percent.Text = Ui.FormatPercent(model.Progress);
        // Design doc 2.2: senders waiting in this device's queue.
        detail.Text = model.WaitingCount > 0
            ? $"还有 {model.WaitingCount} 个设备等待发送"
            : $"已配对 · 自动保存到 {model.SaveDirLabel}";
    }
}

sealed class ReceivedContent : PhaseContent
{
    public ReceivedContent(Transfer transfer, Action onReveal)
    {
        var header = Ui.Dock(new DockPanel(), Dock.Top);
        header.Children.Add(Ui.Dock(new LinkButton("在资源管理器中显示", onReveal), Dock.Right));
        header.Children.Add(Ui.Row(6,
            Ui.Icon(Glyphs.CheckFilled, 14, Palette.Success),
            Ui.Text($"已收到 {transfer.FileCount} 个文件", 13, Palette.Text, FontWeights.Medium)));
        Children.Add(header);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        foreach (var (path, index) in transfer.Files.Take(3).Select((p, i) => (p, i)))
            chips.Children.Add(new FileChip(path) { Margin = new Thickness(index == 0 ? 0 : 6, 0, 0, 0) });
        if (transfer.Files.Count > 3)
        {
            var more = Ui.Text($"等 {transfer.Files.Count} 项", 11, Palette.Secondary);
            more.Margin = new Thickness(6, 0, 0, 0);
            chips.Children.Add(more);
        }
        Children.Add(chips);
    }
}

/// Another device asks to pair: both screens show the same code.
sealed class PairRequestContent : PhaseContent
{
    public PairRequestContent(Phase.PairRequest request, Action<bool> answer)
    {
        var buttons = Ui.Row(8,
            new PillButton("拒绝", () => answer(false)),
            new PillButton("配对", () => answer(true), Palette.Accent));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 10, 0, 0);
        Children.Add(Ui.Dock(buttons, Dock.Bottom));

        var digits = request.Code.Length == 6 ? $"{request.Code[..3]} {request.Code[3..]}" : request.Code;
        var code = Ui.Text(digits, 24, Palette.Text, FontWeights.SemiBold);
        Typography.SetNumeralAlignment(code, FontNumeralAlignment.Tabular);
        code.Margin = new Thickness(12, 0, 0, 0);
        Children.Add(Ui.Dock(code, Dock.Right));
        var titles = Ui.Column(2,
            Ui.Text($"{request.Alias} 请求配对", 13, Palette.Text, FontWeights.Medium),
            Ui.Text("两边显示的数字一致才配对", 11, Palette.Secondary));
        titles.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(titles);
        VerticalAlignment = VerticalAlignment.Top;
    }
}

/// A received file that can be dragged out of the island.
sealed class FileChip : Border
{
    /// Set while a chip is being dragged, so the island ignores its own drag.
    public static bool DraggingOut { get; private set; }
    /// The drag out ended. The pointer may have left the island meanwhile
    /// without a MouseLeave, as the drag loop had the mouse.
    public static event Action? DragOutEnded;

    readonly string path;
    Point? pressedAt;

    public FileChip(string path)
    {
        this.path = path;
        CornerRadius = new CornerRadius(8);
        Background = Palette.Tile;
        Padding = new Thickness(8, 4, 8, 5);
        Cursor = Cursors.Hand;
        var name = Ui.Text(System.IO.Path.GetFileName(path), 11, Palette.Text);
        name.MaxWidth = 96;
        var glyph = System.IO.Directory.Exists(path) ? Glyphs.Folder : Glyphs.Document;
        Child = Ui.Row(4, Ui.Icon(glyph, 10, Palette.Text), name);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        pressedAt = e.GetPosition(this);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        pressedAt = null;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        pressedAt = null;
        DraggingOut = true;
        try
        {
            DragDrop.DoDragDrop(this, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
        }
        finally
        {
            DraggingOut = false;
            DragOutEnded?.Invoke();
        }
    }
}
