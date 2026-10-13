using System.IO;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace WormholeSend;

/// The tray icon: a black hole drawn as its photon ring, the accretion disk
/// tilted across it (assets/tray-icon.svg; the macOS app draws the same in
/// StatusIcon.swift). A 24-unit grid, y down, drawn square.
///
/// The disk's far half hides behind the ring and its near half passes in
/// front, so each part is drawn alone and cut with a gap around what lies
/// over it: the alpha of a part times one minus the alpha of its cut, then
/// the parts laid over each other.
static class TrayGlyph
{
    const float RingRadius = 5, RingWidth = 2;
    const float DiskRx = 10.6f, DiskRy = 4, DiskWidth = 1.8f;
    /// Clearance between the ring and the disk where one passes the other.
    const float Gap = 1.3f;
    const float Tilt = -22;
    static readonly Drawing.PointF BadgeCenter = new(20, 17);
    const float BadgeRadius = 2.9f;

    /// The sizes in the .ico; Windows picks the one for the tray's DPI.
    static readonly int[] Sizes = [16, 20, 24, 32, 36, 40, 48];

    /// The icon in `color`, with a `badge` dot (the queued send) if given.
    public static Drawing.Icon Icon(Drawing.Color color, Drawing.Color? badge)
    {
        var images = Sizes.Select(size =>
        {
            using var bitmap = Render(size, color, badge);
            using var png = new MemoryStream();
            bitmap.Save(png, Drawing.Imaging.ImageFormat.Png);
            return (size, png.ToArray());
        }).ToList();

        // An .ico of PNG images keeps the alpha exactly (Windows Vista+).
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)images.Count);
            var offset = 6 + 16 * images.Count;
            foreach (var (size, data) in images)
            {
                writer.Write((byte)size);
                writer.Write((byte)size);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(data.Length);
                writer.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in images) writer.Write(data);
        }
        ico.Position = 0;
        return new Drawing.Icon(ico);
    }

    public static Drawing.Bitmap Render(int size, Drawing.Color color, Drawing.Color? badge)
    {
        var far = Mask(size, true, g => StrokeDisk(g, DiskWidth, front: false));
        var farCut = Mask(size, true, g => g.FillEllipse(Drawing.Brushes.White, Circle(RingRadius + RingWidth / 2 + Gap + DiskWidth / 2)));
        var ring = Mask(size, true, g =>
        {
            using var pen = Pen(RingWidth);
            g.DrawEllipse(pen, Circle(RingRadius));
        });
        var ringCut = Mask(size, true, g => StrokeDisk(g, DiskWidth + 2 * Gap, front: true));
        var near = Mask(size, true, g => StrokeDisk(g, DiskWidth, front: true));
        var badgeCut = Mask(size, false, g => g.FillEllipse(Drawing.Brushes.White, Circle(BadgeRadius + Gap, BadgeCenter)));
        var dot = Mask(size, false, g => g.FillEllipse(Drawing.Brushes.White, Circle(BadgeRadius, BadgeCenter)));

        var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (var i = 0; i < size * size; i++)
        {
            var glyph = Over(Over(far[i] * (1 - farCut[i]), ring[i] * (1 - ringCut[i])), near[i]);
            var pixel = Drawing.Color.FromArgb(Byte(glyph), color);
            if (badge is { } tint)
            {
                glyph *= 1 - badgeCut[i];
                // The dot over what is left of the glyph.
                var a = Over(dot[i], glyph);
                var mix = a > 0 ? dot[i] / a : 0;
                pixel = Drawing.Color.FromArgb(Byte(a),
                    Lerp(color.R, tint.R, mix), Lerp(color.G, tint.G, mix), Lerp(color.B, tint.B, mix));
            }
            bitmap.SetPixel(i % size, i / size, pixel);
        }
        return bitmap;
    }

    static float Over(float top, float below) => top + below * (1 - top);

    static int Byte(float alpha) => (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255);

    static int Lerp(byte from, byte to, float t) => (int)Math.Round(from + (to - from) * t);

    /// One part's coverage, 0 to 1 per pixel, drawn in white on the 24-unit
    /// grid; `disk` frames the drawing in the disk's tilted coordinates.
    static float[] Mask(int size, bool disk, Action<Drawing.Graphics> draw)
    {
        using var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            g.ScaleTransform(size / 24f, size / 24f);
            if (disk)
            {
                g.TranslateTransform(12, 12);
                g.RotateTransform(Tilt);
            }
            draw(g);
        }
        var alpha = new float[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                alpha[y * size + x] = bitmap.GetPixel(x, y).A / 255f;
        return alpha;
    }

    static Drawing.Pen Pen(float width) => new(Drawing.Color.White, width)
    {
        StartCap = Drawing2D.LineCap.Round,
        EndCap = Drawing2D.LineCap.Round,
    };

    /// Half of the disk; the near half bulges toward the viewer, down on
    /// the grid (y > 0).
    static void StrokeDisk(Drawing.Graphics g, float width, bool front)
    {
        using var pen = Pen(width);
        using var path = new Drawing2D.GraphicsPath();
        path.AddArc(-DiskRx, -DiskRy, 2 * DiskRx, 2 * DiskRy, front ? 0 : 180, 180);
        g.DrawPath(pen, path);
    }

    static Drawing.RectangleF Circle(float radius, Drawing.PointF center = default) =>
        new(center.X - radius, center.Y - radius, 2 * radius, 2 * radius);
}
