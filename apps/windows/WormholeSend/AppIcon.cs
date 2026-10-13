using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace WormholeSend;

/// The app icon, assets/app-icon.ico (made from app-icon.svg on the Mac by
/// make-icons.sh). It is also the exe's icon, which Windows shows for every
/// window in the taskbar and Alt+Tab; this puts it in the title bars too, and
/// its black hole, without the plate, on the island's hint.
static class AppIcon
{
    const double Size = 16;

    static BitmapDecoder? decoder;

    /// Puts the icon in `bar`, using the frame drawn for the window's DPI.
    public static void Show(TitleBar bar, Window window)
    {
        var icon = new ImageIcon { Width = Size, Height = Size };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        void Update() => icon.Source = Frame(Size * VisualTreeHelper.GetDpi(window).DpiScaleX);
        Update();
        window.DpiChanged += (_, _) => Update();
        bar.Icon = icon;
    }

    /// The smallest frame with at least `pixels` pixels, so it is scaled down
    /// a little at most: an .ico in WPF otherwise shows its first frame.
    static BitmapSource Frame(double pixels)
    {
        decoder ??= BitmapDecoder.Create(
            new Uri("pack://application:,,,/app-icon.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frames = decoder.Frames.OrderBy(frame => frame.PixelWidth).ToList();
        return frames.FirstOrDefault(frame => frame.PixelWidth >= pixels - 0.5) ?? frames[^1];
    }

    /// The black hole of app-icon.svg in its colors, without the plate, on
    /// its 24-unit grid: the disk's far half behind the ring, cut where it
    /// would cross the hole, and the near half in front, with a gap cut out
    /// of the ring around it (the masks of the SVG).
    public static DrawingImage BlackHole { get; } = DrawBlackHole();

    static DrawingImage DrawBlackHole()
    {
        Geometry Stroke(Geometry path, double width) => path.GetWidenedPathGeometry(
            new Pen(Brushes.Black, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        Geometry Arc(SweepDirection sweep) => new PathGeometry([new PathFigure(new Point(-10.6, 0),
            [new ArcSegment(new Point(10.6, 0), new Size(10.6, 4), 0, false, sweep, true)], false)]);
        // With y down, clockwise from the left end goes over the top: the far half.
        var far = Arc(SweepDirection.Clockwise);
        var near = Arc(SweepDirection.Counterclockwise);

        var disk = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(-10.6, 0),
            EndPoint = new Point(10.6, 0),
            GradientStops =
            [
                new GradientStop(Color.FromRgb(0xF2, 0x68, 0x3C), 0),
                new GradientStop(Color.FromRgb(0xFF, 0xB5, 0x47), 0.55),
                new GradientStop(Color.FromRgb(0xFF, 0xE3, 0xA3), 1),
            ],
        };
        var ring = new SolidColorBrush(Color.FromRgb(0xF7, 0xF3, 0xEA));

        var hole = new DrawingGroup
        {
            Transform = new TransformGroup { Children = [new RotateTransform(-22), new TranslateTransform(12, 12)] },
        };
        hole.Children.Add(new GeometryDrawing(disk, null,
            Geometry.Combine(Stroke(far, 1.8), new EllipseGeometry(new Point(), 8.2, 8.2), GeometryCombineMode.Exclude, null)));
        hole.Children.Add(new GeometryDrawing(ring, null,
            Geometry.Combine(Stroke(new EllipseGeometry(new Point(), 5, 5), 2), Stroke(near, 4.4), GeometryCombineMode.Exclude, null)));
        hole.Children.Add(new GeometryDrawing(disk, null, Stroke(near, 1.8)));

        // The whole grid, so the image is laid out as the 24-unit square.
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        drawing.Children.Add(hole);
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
