using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WormholeSend;

/// Everything drawn in one window: the sensor along the screen top, and the
/// island, a black capsule that morphs between the phases with each phase's
/// content inside. Only the window on the active screen shows the island.
sealed class IslandView : Grid
{
    readonly IslandModel model;
    readonly Border halo;
    readonly Border island;
    readonly Canvas layers = new();
    readonly RectangleGeometry clip = new();
    readonly Spring width;
    readonly Spring height;
    Border? currentLayer;
    PhaseContent? current;
    string currentKey = "idle";
    bool isActiveScreen;
    bool shown;
    bool animating;
    TimeSpan lastFrame;

    public IslandView(IslandModel model)
    {
        this.model = model;
        Width = Spec.PanelSize.Width;
        Height = Spec.PanelSize.Height;
        var idle = model.BodySizeFor(new Phase.Idle());
        width = new Spring(idle.Width);
        height = new Spring(idle.Height);

        Children.Add(new Border
        {
            Width = Spec.SensorSize.Width,
            Height = Spec.SensorSize.Height,
            Background = Palette.HitOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        });
        // Near-invisible halo while a drag is in progress: it extends the
        // drop area by `Spec.HitOutset` (see `Palette.HitOnly`).
        halo = new Border
        {
            Background = Palette.HitOnly,
            CornerRadius = new CornerRadius(32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(halo);
        island = new Border
        {
            Background = Palette.Island,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Spec.CapsuleTop, 0, 0),
            Opacity = 0,
            Clip = clip,
            Child = layers,
        };
        island.MouseEnter += (_, _) => { if (isActiveScreen) model.Hovering = true; };
        island.MouseLeave += (_, _) => { if (isActiveScreen) model.Hovering = false; };
        island.MouseLeftButtonUp += (_, _) => { if (isActiveScreen) model.IslandTapped(); };
        Children.Add(island);

        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        ApplyFrame();
        model.PropertyChanged += ModelChanged;
        FileChip.DragOutEnded += DragOutEnded;
    }

    /// The window closes: stop following the model.
    public void Detach()
    {
        model.PropertyChanged -= ModelChanged;
        FileChip.DragOutEnded -= DragOutEnded;
        if (animating) CompositionTarget.Rendering -= OnFrame;
        animating = false;
    }

    void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    void DragOutEnded()
    {
        if (!isActiveScreen) return;
        var pointer = Mouse.GetPosition(island);
        model.Hovering = new Rect(island.RenderSize).Contains(pointer);
    }

    /// Whether this window's screen shows the island; the others only keep
    /// their sensor.
    public bool IsActiveScreen
    {
        get => isActiveScreen;
        set
        {
            if (isActiveScreen == value) return;
            isActiveScreen = value;
            Refresh();
        }
    }

    void Refresh()
    {
        var phase = isActiveScreen ? model.Phase : new Phase.Idle();
        var compact = phase is Phase.Queued && model.QueueCollapsed;
        var size = model.BodySizeFor(phase);
        var key = ContentKey(phase, compact);
        if (key != currentKey) Swap(key, phase, compact, size);
        current?.Update(model, phase);
        halo.Visibility = phase.AcceptsDrag ? Visibility.Visible : Visibility.Collapsed;
        Show(phase is not Phase.Idle);
        width.Target = size.Width;
        height.Target = size.Height;
        StartAnimating();
    }

    /// Changes when the content layout changes, not on every progress tick.
    string ContentKey(Phase phase, bool compact) => phase switch
    {
        Phase.Picker => model.Targets.Count == 1 ? "single" : $"picker:{string.Join(",", model.Targets.Select(d => d.Id))}",
        Phase.Queued => compact ? "queued-compact" : "queued",
        // The others' values (a reason, a name) are fixed for the phase.
        _ => phase.Describe(),
    };

    /// Cross-fades to the new phase's content, which also grows slightly from
    /// the top, as SwiftUI's opacity + scale(0.96, anchor: .top) transition.
    void Swap(string key, Phase phase, bool compact, Size size)
    {
        currentKey = key;
        if (currentLayer is { } old)
        {
            old.IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.15));
            fade.Completed += (_, _) => layers.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, fade);
        }
        current = PhaseContent.Build(model, phase, compact);
        var scale = new ScaleTransform(0.96, 0.96);
        var layer = new Border
        {
            Width = size.Width,
            Height = size.Height,
            Padding = compact ? new Thickness(12, 0, 12, 0) : new Thickness(16, 12, 16, 0),
            Child = current,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0),
            RenderTransform = scale,
        };
        layers.Children.Add(layer);
        currentLayer = layer;
        Canvas.SetLeft(layer, (width.Value - size.Width) / 2);
        Ui.Animate(layer, OpacityProperty, 1, 0.25);
        Ui.Animate(scale, ScaleTransform.ScaleXProperty, 1, 0.35);
        Ui.Animate(scale, ScaleTransform.ScaleYProperty, 1, 0.35);
    }

    void Show(bool show)
    {
        if (show == shown) return;
        shown = show;
        // Fade out a little late, after the island started shrinking back.
        var animation = show
            ? new DoubleAnimation(1, TimeSpan.FromSeconds(0.12))
            : new DoubleAnimation(0, TimeSpan.FromSeconds(0.2)) { BeginTime = TimeSpan.FromSeconds(0.3) };
        island.BeginAnimation(OpacityProperty, animation);
    }

    void StartAnimating()
    {
        if (animating || (width.IsSettled && height.IsSettled)) return;
        animating = true;
        lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime;
        var seconds = lastFrame == TimeSpan.Zero ? 1.0 / 60 : (time - lastFrame).TotalSeconds;
        if (seconds <= 0) return; // called again for the same frame
        lastFrame = time;
        var moving = width.Step(seconds) | height.Step(seconds);
        ApplyFrame();
        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame;
            animating = false;
        }
    }

    void ApplyFrame()
    {
        var w = Math.Max(1, width.Value);
        var h = Math.Max(1, height.Value);
        var radius = Math.Min(24, h / 2);
        island.Width = w;
        island.Height = h;
        island.CornerRadius = new CornerRadius(radius);
        clip.Rect = new Rect(0, 0, w, h);
        clip.RadiusX = clip.RadiusY = radius;
        halo.Width = w + 2 * Spec.HitOutset;
        halo.Height = h + Spec.HitOutset;
        foreach (FrameworkElement layer in layers.Children)
            Canvas.SetLeft(layer, (w - layer.Width) / 2);
    }
}
