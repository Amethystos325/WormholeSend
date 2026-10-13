using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WormholeSend;

/// Small building blocks of the island contents.
static class Ui
{
    public static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null) => new()
    {
        Text = text,
        FontFamily = Fonts.Ui,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Icon(string glyph, double size, Brush brush) => new()
    {
        Text = glyph,
        FontFamily = Glyphs.Font,
        FontSize = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// "45%", with digits of equal width so the label doesn't jitter.
    public static TextBlock Percent()
    {
        var text = Text("0%", 11.5, Palette.Secondary);
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }

    public static string FormatPercent(double value) => $"{(int)(Math.Clamp(value, 0, 1) * 100)}%";

    public static T Dock<T>(T element, Dock dock) where T : UIElement
    {
        DockPanel.SetDock(element, dock);
        return element;
    }

    public static StackPanel Row(double spacing, params UIElement[] children) => Stack(Orientation.Horizontal, spacing, children);

    public static StackPanel Column(double spacing, params UIElement[] children) => Stack(Orientation.Vertical, spacing, children);

    static StackPanel Stack(Orientation orientation, double spacing, UIElement[] children)
    {
        var stack = new StackPanel { Orientation = orientation };
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement element)
            {
                var m = element.Margin;
                element.Margin = orientation == Orientation.Horizontal
                    ? new Thickness(m.Left + spacing, m.Top, m.Right, m.Bottom)
                    : new Thickness(m.Left, m.Top + spacing, m.Right, m.Bottom);
            }
            stack.Children.Add(children[i]);
        }
        return stack;
    }

    public static void Animate(DependencyObject target, DependencyProperty property, double to, double seconds, double delay = 0)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (target is IAnimatable animatable) animatable.BeginAnimation(property, animation);
    }
}

/// A device avatar: a circle with the device type, a green dot when online.
sealed class Avatar : Grid
{
    readonly Ellipse circle = new();

    public Avatar(Device device, double size = 40, bool highlighted = false)
    {
        Width = Height = size;
        Children.Add(circle);
        Children.Add(Ui.Icon(device.Glyph, size * 0.45, device.Online ? Palette.Text : Palette.Tertiary));
        if (device.Online && size >= 36)
        {
            Children.Add(new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Palette.Success,
                Stroke = Palette.Island,
                StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }
        Highlighted = highlighted;
    }

    public bool Highlighted
    {
        set => circle.Fill = value ? Palette.Accent : Palette.Tile;
    }
}

sealed class ProgressLine : Grid
{
    readonly Border fill;
    double value;

    public ProgressLine(double height, Brush brush)
    {
        Height = height;
        Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Palette.Track });
        fill = new Border { CornerRadius = new CornerRadius(height / 2), Background = brush, HorizontalAlignment = HorizontalAlignment.Left };
        Children.Add(fill);
        SizeChanged += (_, _) => Layout();
    }

    public double Value
    {
        set
        {
            this.value = Math.Clamp(value, 0, 1);
            Layout();
        }
    }

    void Layout() => fill.Width = ActualWidth * value;
}

/// A clickable area that reacts on the first click without taking focus;
/// the windows never activate.
class Pressable : Border
{
    public event Action? Click;
    protected bool Pressed { get; private set; }

    public Pressable()
    {
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
    }

    protected virtual void OnPressedChanged() { }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Pressed = true;
        OnPressedChanged();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var inside = IsMouseCaptured && IsMouseOver;
        ReleaseMouseCapture();
        e.Handled = true;
        if (inside) Click?.Invoke();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        Pressed = false;
        OnPressedChanged();
    }

    public void Invoke() => Click?.Invoke();

    /// A button to screen readers and UI Automation, named by its text or
    /// its tooltip.
    protected override AutomationPeer OnCreateAutomationPeer() => new PressablePeer(this);

    sealed class PressablePeer(Pressable owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetNameCore() =>
            owner.Child is TextBlock { Text: var text } && !string.IsNullOrEmpty(text) ? text : owner.ToolTip as string ?? base.GetNameCore();

        public override object? GetPattern(PatternInterface pattern) =>
            pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);

        public void Invoke() => owner.Invoke();
    }
}

sealed class CancelButton : Pressable
{
    public CancelButton(Action action)
    {
        Child = Ui.Icon(Glyphs.CloseFilled, 16, Palette.Tertiary);
        ToolTip = "取消";
        VerticalAlignment = VerticalAlignment.Center;
        Click += action;
    }

    protected override void OnPressedChanged() => Opacity = Pressed ? 0.6 : 1;
}

sealed class PillButton : Pressable
{
    public PillButton(string title, Action action, Brush? fill = null)
    {
        Child = Ui.Text(title, 12, Palette.Text, FontWeights.SemiBold);
        Padding = new Thickness(12, 3, 12, 4);
        CornerRadius = new CornerRadius(999);
        Background = fill ?? Palette.Tile;
        VerticalAlignment = VerticalAlignment.Center;
        Click += action;
    }

    protected override void OnPressedChanged() => Opacity = Pressed ? 0.7 : 1;
}

sealed class LinkButton : Pressable
{
    public LinkButton(string title, Action action)
    {
        Child = Ui.Text(title, 12, Palette.Accent);
        VerticalAlignment = VerticalAlignment.Center;
        Click += action;
    }

    protected override void OnPressedChanged() => Opacity = Pressed ? 0.6 : 1;
}
