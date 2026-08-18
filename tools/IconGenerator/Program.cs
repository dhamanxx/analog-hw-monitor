using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace IconGenerator;

/// <summary>
/// Draws the application's analog-dial icon and packs it into a hand-written,
/// multi-size .ico container.
///
/// Why hand-written: <see cref="Icon.Save"/> can only ever emit the single
/// image the .NET <see cref="Icon"/> object was constructed from. A real .ico
/// file is a small container format — one directory followed by N independent
/// image payloads — and nothing in System.Drawing builds that container for
/// you. So each size is rendered as its own 32bpp ARGB bitmap, encoded to PNG
/// (which the .ico format has allowed as a payload since Vista, and which is
/// the only sane way to keep a 256x256 image with alpha out of the legacy
/// bitmap+AND-mask encoding), and the six PNGs are wrapped in an ICONDIR by
/// hand.
///
/// Usage: icongen &lt;normal.ico&gt; &lt;warning.ico&gt;
/// </summary>
internal static class Program
{
    /// <summary>
    /// Every standard size Windows asks for an icon at: taskbar/tray at 16 and
    /// 32, Explorer's list and tile views at 48 and 64, and the jumbo/extra-
    /// large thumbnails at 128 and 256.
    /// </summary>
    private static readonly int[] Sizes = { 16, 32, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: icongen <normal.ico> <warning.ico>");
            return 1;
        }

        WriteIcon(args[0], warning: false);
        WriteIcon(args[1], warning: true);

        Console.WriteLine($"Wrote {args[0]} and {args[1]}.");
        return 0;
    }

    private static void WriteIcon(string path, bool warning)
    {
        var pngs = new List<byte[]>(Sizes.Length);
        foreach (var size in Sizes)
        {
            using var bitmap = DrawDial(size, warning);
            pngs.Add(Encode(bitmap));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, BuildIco(Sizes, pngs));
    }

    /// <summary>
    /// Renders the dial at <paramref name="size"/> pixels square. Every
    /// measurement below is a fraction of <paramref name="size"/>, never a
    /// literal pixel count, which is what keeps the same drawing code legible
    /// from a 16px tray icon up to a 256px thumbnail: the proportions of
    /// bezel, face and needle stay identical, only the scale changes.
    ///
    /// Revision note (fix round 1): the scale arc and tick marks that used to
    /// sit between bezel and needle are gone. At 16px they did not read as a
    /// scale — they merged into one pale blur against the bezel and drowned
    /// the needle out. The shape is simplified at every size, not just the
    /// small ones, so there is one consistent mark instead of a detailed
    /// large icon and a separately-tuned small one: a heavy two-tone bezel
    /// ring, a dominant needle, and a small hub. See the report for the
    /// before/after description of what this looks like at 16px.
    /// </summary>
    private static Bitmap DrawDial(int size, bool warning)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var s = (float)size;
        var half = s / 2f;
        var center = new PointF(half, half);

        // A small margin keeps the anti-aliased edge of the outer bezel from
        // being clipped by the bitmap bounds at small sizes.
        var margin = s * 0.03f;
        var outerRadius = half - margin;

        // A heavier bezel ring than before: the silhouette now has to carry
        // the whole "this is a physical meter" read by itself, with no scale
        // or ticks left to reinforce it.
        var bezelThickness = Math.Max(1.6f, s * 0.16f);
        var faceRadius = outerRadius - bezelThickness;

        // Bezel and face separate by a strong light/dark step rather than by
        // fine detail: a light bezel, a near-black face, and a dark contour
        // stroke right at the outer edge so the silhouette holds up against
        // both light and dark backgrounds.
        var bezelColor = Color.FromArgb(255, 235, 237, 241);
        var contourColor = Color.FromArgb(255, 70, 73, 80);
        var faceColor = Color.FromArgb(255, 16, 18, 24);
        var needleColor = Color.FromArgb(255, 230, 50, 40);
        var hubColor = Color.FromArgb(255, 238, 239, 243);

        var contourWidth = Math.Max(1f, s * 0.035f);
        FillCircle(g, center, outerRadius, bezelColor);
        StrokeCircle(g, center, outerRadius - contourWidth / 2f, contourColor, contourWidth);
        FillCircle(g, center, faceRadius, faceColor);

        // The needle's resting angle: 150 degrees is the classic panel-meter
        // "zero" position (lower-left, in GDI+'s 0-at-3-o'clock,
        // clockwise-positive convention) and it travels 240 degrees to
        // "full scale" at the lower-right. Resting at 70% of that sweep — off
        // to one side rather than dead centered — is what reads as an
        // instrument mid-reading instead of a clock or a logo. The scale
        // itself is no longer drawn, but the angle math still describes where
        // the needle sits relative to it.
        const float startAngle = 150f;
        const float sweepAngle = 240f;
        var needleAngle = startAngle + sweepAngle * 0.70f;

        // The needle is now the dominant feature: it reaches most of the way
        // to the face's edge and is noticeably thick, so a handful of pixels
        // at 16px still read as "a needle pointing somewhere" rather than a
        // stray red speck. The hub shrinks to the smallest size that still
        // reads as a pivot rather than competing with the needle for
        // attention.
        var needleLength = faceRadius * 0.92f;
        var needleHalfWidth = Math.Max(1.4f, s * 0.12f);
        var hubRadius = Math.Max(1f, s * 0.07f);
        DrawNeedle(g, center, needleAngle, needleLength, needleHalfWidth, hubRadius * 0.6f, needleColor);
        FillCircle(g, center, hubRadius, hubColor);

        if (warning)
        {
            DrawWarningBadge(g, half);
        }

        return bitmap;
    }

    /// <summary>
    /// An amber, dark-outlined badge overlapping the lower-right edge of the
    /// dial. Its size and position are fractions of the bitmap's half-size,
    /// so — like the dial itself — it keeps its proportions from 16px to
    /// 256px and stays inside the canvas at every size.
    /// </summary>
    private static void DrawWarningBadge(Graphics g, float half)
    {
        var badgeRadius = half * 0.32f;
        var offset = half * 0.52f;
        var badgeCenter = new PointF(half + offset, half + offset);
        var outlineWidth = Math.Max(1f, half * 2f * 0.035f);

        var amberFill = Color.FromArgb(255, 255, 176, 32);
        var amberOutline = Color.FromArgb(255, 45, 32, 10);

        FillCircle(g, badgeCenter, badgeRadius, amberFill);
        StrokeCircle(g, badgeCenter, badgeRadius - outlineWidth / 2f, amberOutline, outlineWidth);
    }

    private static void DrawNeedle(
        Graphics g, PointF center, float angleDegrees, float length, float halfWidth, float baseRadius, Color color)
    {
        var radians = angleDegrees * MathF.PI / 180f;
        var dir = new PointF(MathF.Cos(radians), MathF.Sin(radians));
        var perp = new PointF(-dir.Y, dir.X);

        var basePoint = new PointF(center.X + dir.X * baseRadius, center.Y + dir.Y * baseRadius);
        var tip = new PointF(center.X + dir.X * length, center.Y + dir.Y * length);
        var left = new PointF(basePoint.X + perp.X * halfWidth, basePoint.Y + perp.Y * halfWidth);
        var right = new PointF(basePoint.X - perp.X * halfWidth, basePoint.Y - perp.Y * halfWidth);

        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new[] { left, tip, right });
    }

    private static void FillCircle(Graphics g, PointF center, float radius, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
    }

    private static void StrokeCircle(Graphics g, PointF center, float radius, Color color, float width)
    {
        using var pen = new Pen(color, width);
        g.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>
    /// Hand-writes an .ico container: a 6-byte ICONDIR, then one 16-byte
    /// ICONDIRENTRY per image, then the PNG payloads themselves, in the same
    /// order as the entries. Each entry's offset is measured from the start
    /// of the file, so it must account for the header AND every entry, not
    /// just the payloads that precede it.
    /// </summary>
    private static byte[] BuildIco(IReadOnlyList<int> sizes, IReadOnlyList<byte[]> pngs)
    {
        const int iconDirSize = 6;
        const int iconDirEntrySize = 16;
        var count = sizes.Count;
        var headerSize = iconDirSize + iconDirEntrySize * count;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: 1 = icon
        writer.Write((ushort)count);

        var offset = headerSize;
        for (var i = 0; i < count; i++)
        {
            var size = sizes[i];
            var png = pngs[i];

            // A dimension of 256 does not fit in a byte, so the format
            // reserves 0 in that slot to mean "256".
            writer.Write((byte)(size == 256 ? 0 : size)); // width
            writer.Write((byte)(size == 256 ? 0 : size)); // height
            writer.Write((byte)0);  // color count: 0, not palette-based
            writer.Write((byte)0);  // reserved
            writer.Write((ushort)1);  // color planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);

            offset += png.Length;
        }

        foreach (var png in pngs)
        {
            writer.Write(png);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
