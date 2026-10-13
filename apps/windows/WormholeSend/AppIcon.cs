using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace WormholeSend;

/// The app icon, assets/app-icon.ico (made from app-icon.svg on the Mac by
/// make-icons.sh). It is also the exe's icon, which Windows shows for every
/// window in the taskbar and Alt+Tab; this puts it in the title bars too.
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
}
