using System.Windows;
using System.Windows.Media;

namespace WormholeSend;

/// Timings and sizes from the interaction spec (design doc, section 2.3).
/// Both apps use the same values (apps/macos/.../Spec.swift). Sizes are in
/// device-independent pixels, scaled per monitor.
static class Spec
{
    public static readonly TimeSpan DoneHold = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan RefusedHold = TimeSpan.FromMilliseconds(1800);
    public static readonly TimeSpan FailedHold = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ReceivedHold = TimeSpan.FromSeconds(5);
    /// The queued state collapses to its compact form after this long.
    public static readonly TimeSpan QueuedHint = TimeSpan.FromSeconds(3);
    /// The core declines a LocalSend device's request unanswered for this
    /// long (ASK_TIMEOUT in core/src/receive.rs); its decline button counts down to it.
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// The drop area extends this far around the visible island.
    public const double HitOutset = 24;
    /// The "其他设备" strip at the bottom of the target island. Dragging down
    /// into it (or below it) opens the device list; nothing requires
    /// lingering, as on macOS.
    public const double MoreStripHeight = 40;
    public const double PickerWidth = 420;
    /// Distance from the screen top of the floating capsule.
    public const double CapsuleTop = 8;

    /// The sensor along the top edge of every screen. Windows has no global
    /// drag events without hooks, so the island shows when a file drag
    /// enters this strip.
    public static readonly Size SensorSize = new(240, 6);

    /// Each window is fixed in size, large enough for the largest state; the
    /// island is drawn at its top center.
    public static readonly Size PanelSize = new(560, 260);

    /// SwiftUI's spring(response: 0.45, dampingFraction: 0.78).
    public const double SpringResponse = 0.45;
    public const double SpringDamping = 0.78;
}

static class Palette
{
    public static readonly Brush Island = Freeze(Colors.Black);
    public static readonly Brush Text = Freeze(Colors.White);
    public static readonly Brush Secondary = Gray(0.62);
    public static readonly Brush Tertiary = Gray(0.45);
    public static readonly Brush Tile = Gray(0.16);
    public static readonly Brush TileHot = Gray(0.13);
    public static readonly Brush Track = Gray(0.22);
    public static readonly Brush Strip = Gray(0.1);
    public static readonly Brush StripText = Gray(0.8);
    public static readonly Brush HintText = Gray(0.86);
    public static readonly Brush Accent = Rgb(0.04, 0.52, 1.0);
    public static readonly Brush Success = Rgb(0.19, 0.82, 0.35);
    public static readonly Brush Warning = Rgb(1.0, 0.62, 0.04);

    /// Alpha 1/255: invisible, but layered windows only route drags and
    /// clicks to pixels that are not fully transparent. Much more shows as a
    /// grey frame on white backgrounds (0.012 already does on macOS).
    public static readonly Brush HitOnly = Freeze(Color.FromArgb(1, 0, 0, 0));

    static Brush Gray(double white) => Rgb(white, white, white);

    static Brush Rgb(double r, double g, double b) =>
        Freeze(Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255)));

    static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// Segoe Fluent Icons (Windows 11), falling back to Segoe MDL2 Assets
/// (Windows 10), standing in for the SF Symbols of the macOS app.
static class Glyphs
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public const string Desktop = "";         // desktopcomputer
    public const string Laptop = "";          // laptopcomputer
    public const string Tablet = "";          // ipad
    public const string Phone = "";           // iphone
    public const string ChevronDown = "";     // chevron.down
    public const string NoNetwork = "";       // wifi.slash
    public const string CheckFilled = "";     // checkmark.circle.fill
    public const string Blocked = "";         // nosign
    public const string Clock = "";           // clock.fill
    public const string WarningFilled = "";   // exclamationmark.triangle.fill
    public const string CloseFilled = "";     // xmark.circle.fill
    public const string Document = "";        // doc.fill
    public const string Folder = "";          // folder.fill

    // The tray menu, after the macOS menu's symbols.
    public const string Star = "";            // star
    public const string Link = "";            // link.badge.plus
    public const string FolderOpen = "";      // folder
    public const string Settings = "";        // gearshape
    public const string Power = "";           // power
    public const string Volume = "";          // speaker.wave.2
    public const string Devices = "";
    public const string Send = "";
}

static class Fonts
{
    // Not Segoe UI Variable: WPF can't pick weights from variable fonts.
    public static readonly FontFamily Ui = new("Segoe UI, Microsoft YaHei UI");
}
