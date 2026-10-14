using System.Windows;
using System.Windows.Media;

namespace WormholeSend.Tests;

/// The device icons (assets/device-icons.svg).
public class DeviceGlyphTests
{
    [Theory]
    [InlineData("mobile", "Phone")]
    [InlineData("web", "Web")]
    [InlineData("server", "Server")]
    [InlineData("headless", "Server")]
    [InlineData("desktop", "Desktop")]
    [InlineData(null, "Desktop")]
    public void FromLocalSendDeviceTypes(string? deviceType, string glyph) =>
        Assert.Equal(Enum.Parse<DeviceGlyph>(glyph), DeviceGlyphs.For(deviceType));

    /// What the strokes cover, round caps included, on the 24-unit grid.
    [Theory]
    [InlineData("Desktop", 2.1, 3.1, 21.9, 20.9)]
    [InlineData("Laptop", 1.6, 4.6, 22.4, 19.4)]
    [InlineData("Phone", 6.1, 1.6, 17.9, 22.4)]
    [InlineData("Tablet", 3.6, 2.1, 20.4, 21.9)]
    [InlineData("Web", 2.6, 2.6, 21.4, 21.4)]
    [InlineData("Server", 3.1, 3.1, 20.9, 20.9)]
    public void StaysOnTheGrid(string glyph, double left, double top, double right, double bottom)
    {
        var (strokes, dots) = DeviceGlyphs.Outline(Enum.Parse<DeviceGlyph>(glyph));
        var pen = new Pen(Brushes.Black, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var bounds = strokes.GetRenderBounds(pen);
        bounds.Union(dots.Bounds);
        Assert.Equal(new Rect(new Point(left, top), new Point(right, bottom)), Round(bounds));
    }

    [Fact]
    public void OnlyTheServerHasDots()
    {
        foreach (var glyph in Enum.GetValues<DeviceGlyph>())
            Assert.Equal(glyph == DeviceGlyph.Server, !DeviceGlyphs.Outline(glyph).Dots.Bounds.IsEmpty);
    }

    static Rect Round(Rect rect) => new(
        new Point(Math.Round(rect.Left, 1), Math.Round(rect.Top, 1)), new Point(Math.Round(rect.Right, 1), Math.Round(rect.Bottom, 1)));
}
