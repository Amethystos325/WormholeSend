using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WormholeSend;

/// The app's own device icons (assets/device-icons.svg), drawn like the tray
/// icon: a 24-unit grid, 1.8-unit strokes with round caps and joins. The
/// macOS app draws the same geometry (DeviceGlyph.swift).
enum DeviceGlyph { Desktop, Laptop, Phone, Tablet, Web, Server }

static class DeviceGlyphs
{
    const double LineWidth = 1.8;

    /// From LocalSend's deviceType. Laptops and tablets don't announce
    /// themselves apart from desktops and phones.
    public static DeviceGlyph For(string? deviceType) => deviceType switch
    {
        "mobile" => DeviceGlyph.Phone,
        "web" => DeviceGlyph.Web,
        "server" or "headless" => DeviceGlyph.Server,
        _ => DeviceGlyph.Desktop,
    };

    /// The strokes, and the dots filled as wide as the strokes, on the
    /// 24-unit grid.
    public static (Geometry Strokes, Geometry Dots) Outline(DeviceGlyph glyph)
    {
        var strokes = new GeometryGroup();
        var dots = new GeometryGroup();
        void Box(double x, double y, double width, double height, double radius) =>
            strokes.Children.Add(new RectangleGeometry(new Rect(x, y, width, height), radius, radius));
        void Line(double x1, double y1, double x2, double y2) =>
            strokes.Children.Add(new LineGeometry(new Point(x1, y1), new Point(x2, y2)));
        void Dot(double x, double y) =>
            dots.Children.Add(new EllipseGeometry(new Point(x, y), LineWidth / 2, LineWidth / 2));
        switch (glyph)
        {
            case DeviceGlyph.Desktop:
                Box(3, 4, 18, 12, 2.5);
                Line(12, 16, 12, 19.5);
                Line(8.5, 20, 15.5, 20);
                break;
            case DeviceGlyph.Laptop:
                Box(5, 5.5, 14, 10, 2);
                Line(2.5, 18.5, 21.5, 18.5);
                break;
            case DeviceGlyph.Phone:
                Box(7, 2.5, 10, 19, 3);
                Line(11, 18.5, 13, 18.5);
                break;
            case DeviceGlyph.Tablet:
                Box(4.5, 3, 15, 18, 2.5);
                Line(11, 18, 13, 18);
                break;
            case DeviceGlyph.Web:
                strokes.Children.Add(new EllipseGeometry(new Point(12, 12), 8.5, 8.5));
                strokes.Children.Add(new EllipseGeometry(new Point(12, 12), 3.6, 8.5));
                Line(3.5, 12, 20.5, 12);
                break;
            case DeviceGlyph.Server:
                Box(4, 4, 16, 7, 2);
                Box(4, 13, 16, 7, 2);
                Dot(8, 7.5);
                Dot(8, 16.5);
                break;
        }
        strokes.Freeze();
        dots.Freeze();
        return (strokes, dots);
    }
}

/// A device glyph `size` pixels square.
sealed class DeviceIcon : Grid
{
    readonly Path strokes;
    readonly Path dots;

    public DeviceIcon(DeviceGlyph glyph, double size, Brush? brush = null)
    {
        Width = Height = size;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        var (outline, filled) = DeviceGlyphs.Outline(glyph);
        var scale = new ScaleTransform(size / 24, size / 24);
        scale.Freeze();
        strokes = new Path
        {
            Data = Scaled(outline, scale),
            StrokeThickness = 1.8 * size / 24,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        dots = new Path { Data = Scaled(filled, scale) };
        Children.Add(strokes);
        Children.Add(dots);
        if (brush is not null) Brush = brush;
    }

    public Brush Brush
    {
        set
        {
            strokes.Stroke = value;
            dots.Fill = value;
        }
    }

    /// In a theme's brush, such as the menu's text color.
    public void SetBrushResource(object key)
    {
        strokes.SetResourceReference(Shape.StrokeProperty, key);
        dots.SetResourceReference(Shape.FillProperty, key);
    }

    static Geometry Scaled(Geometry geometry, Transform scale)
    {
        var group = new GeometryGroup { Transform = scale };
        group.Children.Add(geometry);
        group.Freeze();
        return group;
    }
}

/// A device glyph as a WPF-UI icon, for the cards of the pairing and
/// settings windows: in the card's text color, which follows the theme.
sealed class DeviceSymbolIcon(DeviceGlyph glyph, double size = 24) : Wpf.Ui.Controls.IconElement
{
    DeviceIcon? icon;

    protected override UIElement InitializeChildren()
    {
        icon = new DeviceIcon(glyph, size);
        if (Foreground is { } brush) icon.Brush = brush;
        return icon;
    }

    protected override void OnForegroundChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnForegroundChanged(e);
        if (icon is not null && e.NewValue is Brush brush) icon.Brush = brush;
    }
}
