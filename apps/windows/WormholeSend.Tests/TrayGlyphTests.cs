using System.IO;
using Drawing = System.Drawing;

namespace WormholeSend.Tests;

/// The black hole tray icon (assets/tray-icon.svg).
public class TrayGlyphTests
{
    static readonly Drawing.Color Orange = Drawing.Color.FromArgb(255, 158, 10);

    /// Alpha at a point of the 24-unit grid.
    static int Alpha(Drawing.Bitmap bitmap, float x, float y) =>
        bitmap.GetPixel((int)(x * bitmap.Width / 24), (int)(y * bitmap.Height / 24)).A;

    [Fact]
    public void DrawsTheRingAndTheDiskWithGaps()
    {
        using var bitmap = TrayGlyph.Render(48, Drawing.Color.Black, null);
        Assert.Equal(0, Alpha(bitmap, 0.5f, 0.5f));     // corner
        Assert.Equal(0, Alpha(bitmap, 12, 12));          // the hole of the ring
        Assert.True(Alpha(bitmap, 12, 7.05f) > 200);     // top of the ring
        Assert.True(Alpha(bitmap, 22, 8.2f) > 100);      // right end of the disk, tilted up
        Assert.Equal(0, Alpha(bitmap, 12, 2));           // above everything

        // Nothing but black, at any alpha.
        for (var y = 0; y < 48; y++)
            for (var x = 0; x < 48; x++)
                Assert.Equal(0, bitmap.GetPixel(x, y).ToArgb() & 0xFFFFFF);

        Save(bitmap, "glyph-48.png");
    }

    [Fact]
    public void TheBadgeIsOrangeWithAGapAroundIt()
    {
        using var bitmap = TrayGlyph.Render(48, Drawing.Color.White, Orange);
        var center = bitmap.GetPixel(40, 34);
        Assert.Equal((255, 255, 158, 10), (center.A, center.R, center.G, center.B));
        Save(bitmap, "glyph-48-badge.png");
        using var small = TrayGlyph.Render(16, Drawing.Color.White, null);
        Save(small, "glyph-16.png");
    }

    [Fact]
    public void TheIconCarriesEverySize()
    {
        using var icon = TrayGlyph.Icon(Drawing.Color.White, null);
        using var small = new Drawing.Icon(icon, 16, 16);
        using var large = new Drawing.Icon(icon, 48, 48);
        Assert.Equal(16, small.Width);
        Assert.Equal(48, large.Width);
    }

    /// For a look: %TEMP%\WormholeSendIcon.
    static void Save(Drawing.Bitmap bitmap, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "WormholeSendIcon");
        Directory.CreateDirectory(dir);
        bitmap.Save(Path.Combine(dir, name), Drawing.Imaging.ImageFormat.Png);
    }
}
