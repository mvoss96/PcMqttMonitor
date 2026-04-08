using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Generates app.ico with 16×16, 32×32, 48×48 and 256×256 resolutions.
// Run: dotnet run --project tools/genicon
// Output: app.ico in the project root.

var outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\app.ico"));

var sizes = new[] { 16, 32, 48, 256 };
var bitmaps = sizes.Select(DrawBitmap).ToArray();
var pngs    = bitmaps.Select(ToPng).ToArray();

WriteIco(outPath, bitmaps, pngs);

foreach (var b in bitmaps) b.Dispose();

Console.WriteLine($"Written: {outPath}");

// ── Drawing ───────────────────────────────────────────────────────────────────

static Bitmap DrawBitmap(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    // Windows blue bars
    using var brush = new SolidBrush(Color.FromArgb(0, 120, 212));

    int m       = Math.Max(1, size / 9);
    int gap     = Math.Max(1, size / 14);
    int barW    = (size - 2 * m - 2 * gap) / 3;
    int usableH = size - 2 * m;
    int bottom  = size - m;

    float[] ratios = { 0.38f, 0.63f, 0.90f };
    for (int i = 0; i < 3; i++)
    {
        int h = Math.Max(2, (int)(usableH * ratios[i]));
        int x = m + i * (barW + gap);
        int r = Math.Clamp(barW / 3, 1, Math.Min(barW / 2, h / 2));
        FillRoundRect(g, brush, x, bottom - h, barW, h, r);
    }

    return bmp;
}

static void FillRoundRect(Graphics g, Brush brush, int x, int y, int w, int h, int r)
{
    using var path = new GraphicsPath();
    path.AddArc(x,             y,             r * 2, r * 2, 180, 90);
    path.AddArc(x + w - r * 2, y,             r * 2, r * 2, 270, 90);
    path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2,   0, 90);
    path.AddArc(x,             y + h - r * 2, r * 2, r * 2,  90, 90);
    path.CloseFigure();
    g.FillPath(brush, path);
}

static byte[] ToPng(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

// ── ICO writer ────────────────────────────────────────────────────────────────

static void WriteIco(string path, Bitmap[] bitmaps, byte[][] pngs)
{
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var w = new BinaryWriter(stream);

    // ICONDIR
    w.Write((short)0);
    w.Write((short)1);
    w.Write((short)bitmaps.Length);

    // ICONDIRENTRY (16 bytes each)
    int dataOffset = 6 + bitmaps.Length * 16;
    for (int i = 0; i < bitmaps.Length; i++)
    {
        // 0 means 256 in the ICO spec
        w.Write((byte)(bitmaps[i].Width  == 256 ? 0 : bitmaps[i].Width));
        w.Write((byte)(bitmaps[i].Height == 256 ? 0 : bitmaps[i].Height));
        w.Write((byte)0);    // colour count (0 = truecolour)
        w.Write((byte)0);    // reserved
        w.Write((short)1);   // colour planes
        w.Write((short)32);  // bits per pixel
        w.Write(pngs[i].Length);
        w.Write(dataOffset);
        dataOffset += pngs[i].Length;
    }

    foreach (var png in pngs)
        w.Write(png);
}
