using System.Globalization;
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

    /// Chinese glyphs sit low in their lines in the fonts Windows uses; a
    /// pixel up, a line of them looks centered beside an icon.
    public static T Centered<T>(T text) where T : UIElement
    {
        text.RenderTransform = new TranslateTransform(0, -1);
        return text;
    }

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

/// Text shortened in the middle when it doesn't fit, as SwiftUI's
/// truncationMode(.middle): a long name gives way, "and 2 more files" stays.
sealed class MiddleText : Decorator
{
    readonly string full;
    readonly TextBlock text;

    public MiddleText(string text, double size, Brush brush)
    {
        full = text;
        this.text = Ui.Text(text, size, brush);
        this.text.TextTrimming = TextTrimming.None;
        Child = this.text;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        text.Text = Fit(constraint.Width);
        text.Measure(constraint);
        return text.DesiredSize;
    }

    string Fit(double width)
    {
        if (double.IsInfinity(width) || WidthOf(full) <= width) return full;
        // Starts of the text elements, so no surrogate pair is split; keep as
        // many as fit, half from each end.
        var starts = StringInfo.ParseCombiningCharacters(full);
        string Keep(int count)
        {
            var head = (count + 1) / 2;
            var tail = count / 2;
            return full[..starts[head]].TrimEnd() + "…" + (tail > 0 ? full[starts[starts.Length - tail]..].TrimStart() : "");
        }
        var (low, high, best) = (0, starts.Length - 1, "…");
        while (low <= high)
        {
            var count = (low + high) / 2;
            var candidate = Keep(count);
            if (WidthOf(candidate) <= width)
            {
                best = candidate;
                low = count + 1;
            }
            else
                high = count - 1;
        }
        return best;
    }

    double WidthOf(string value) => new FormattedText(
        value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
}

/// A device avatar: the device type on a deep-space plate, a green dot when
/// online. The device a drop goes to is ringed in the disk's warm light.
sealed class Avatar : Grid
{
    readonly Ellipse ring;

    public Avatar(Device device, double size = 40, bool highlighted = false)
    {
        Width = Height = size;
        Children.Add(new Ellipse { Fill = Palette.Plate });
        // A shape's stroke lies inside its bounds, as SwiftUI's strokeBorder.
        ring = new Ellipse { Stroke = Palette.Accent, StrokeThickness = Math.Max(1.2, size * 0.036) };
        Children.Add(ring);
        Children.Add(new DeviceIcon(device.Glyph, size * 0.46, device.Usable ? Palette.Cream : Palette.Tertiary));
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
        set => ring.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// Filled with the accretion disk's gradient, across the part done.
sealed class ProgressLine : Grid
{
    readonly Border fill;
    double value;

    public ProgressLine(double height)
    {
        Height = height;
        Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Palette.Track });
        fill = new Border { CornerRadius = new CornerRadius(height / 2), Background = Palette.Disk, HorizontalAlignment = HorizontalAlignment.Left };
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
        ToolTip = L.T("取消", "Cancel", "キャンセル");
        VerticalAlignment = VerticalAlignment.Center;
        Click += action;
    }

    protected override void OnPressedChanged() => Opacity = Pressed ? 0.6 : 1;
}

sealed class PillButton : Pressable
{
    readonly TextBlock label;

    public PillButton(string title, Action action, Brush? fill = null, Brush? text = null)
    {
        label = Ui.Text(title, 12, text ?? Palette.Text, FontWeights.SemiBold);
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        Child = label;
        Padding = new Thickness(12, 3, 12, 4);
        Background = fill ?? Palette.Tile;
        VerticalAlignment = VerticalAlignment.Center;
        // Half the height: WPF shrinks a larger radius to the width and the
        // height separately, which rounds the ends into an oval.
        SizeChanged += (_, e) => CornerRadius = new CornerRadius(e.NewSize.Height / 2);
        Click += action;
    }

    public string Title
    {
        get => label.Text;
        set => label.Text = value;
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
