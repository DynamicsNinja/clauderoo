// IconGen: draws the clauderoo "stacked profiles" mark as vector paths, hand-tuned on the pixel grid at
// 16/20/24 px, and writes multi-frame PNG-compressed .ico files for the tray app.
//
//   dotnet run --project tools/IconGen               -> writes src/Clauderoo.Tray/Icons/*.ico
//   dotnet run --project tools/IconGen -- <out dir>  -> writes to another directory
//
// Design (see .superpowers/ui-audit-2026-11-17.md, "Icons: deep review", I-1 to I-4):
//   front card #C8643F with a white C and a #8F3F25 rim, back card #F0B8A2 offset up-right;
//   a single rounded square at 16 px; state badges at the bottom right; a monochrome outline variant.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Clauderoo.Tray;

static class Program
{
    static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    static readonly Color FrontFill = Hex("#C8643F");
    static readonly Color BackFill = Hex("#F0B8A2");
    static readonly Color Rim = Hex("#8F3F25");
    static readonly Color Ink = Color.White;
    static readonly Color Mono = Color.Black;   // app-hc.ico; the app tints it to SystemColors.WindowText

    static readonly Color ErrorFill = Hex("#C42B1C");
    static readonly Color WarnFill = Hex("#F7C300");
    static readonly Color WarnOutline = Hex("#5C4500");
    static readonly Color RunFill = Hex("#107C10");
    static readonly Color BadgeRing = Color.White;

    enum Variant { Normal, Warning, Error, Running, HighContrast }

    static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(FindRepoRoot(), "src", "Clauderoo.Tray", "Icons");
        Directory.CreateDirectory(outDir);

        var files = new (Variant Variant, string Name)[]
        {
            (Variant.Normal, "app.ico"),
            (Variant.Warning, "state-warning.ico"),
            (Variant.Error, "state-error.ico"),
            (Variant.Running, "state-running.ico"),
            (Variant.HighContrast, "app-hc.ico"),
        };

        var failures = 0;
        foreach (var (variant, name) in files)
        {
            var frames = new List<IcoFile.Frame>();
            foreach (var size in Sizes)
            {
                using var bmp = Draw(variant, size);
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                frames.Add(new IcoFile.Frame(size, size, ms.ToArray()));
            }
            var path = Path.Combine(outDir, name);
            using (var fs = File.Create(path)) IcoFile.Write(fs, frames);
            failures += SelfCheck(path);
            Console.WriteLine($"wrote {path} ({new FileInfo(path).Length} bytes, {frames.Count} frames)");
        }

        if (failures > 0) Console.Error.WriteLine($"{failures} self-check failure(s)");
        return failures == 0 ? 0 : 1;
    }

    // Every frame must be present and decode at its size; every frame below 256 must also load at its exact size through
    // System.Drawing.Icon (which never picks the 256 px frame; the shell reads that one from the Win32 resource).
    static int SelfCheck(string path)
    {
        var failures = 0;
        var frames = IcoFile.Read(File.ReadAllBytes(path));
        foreach (var size in Sizes)
        {
            var frame = frames.FirstOrDefault(f => f.Width == size && f.Height == size);
            if (frame.Data == null)
            {
                Console.Error.WriteLine($"{Path.GetFileName(path)}: missing {size}px frame");
                failures++;
                continue;
            }
            using (var png = new Bitmap(new MemoryStream(frame.Data)))
            {
                if (png.Width != size || png.Height != size)
                {
                    Console.Error.WriteLine($"{Path.GetFileName(path)}: {size}px frame decodes as {png.Width}x{png.Height}");
                    failures++;
                }
            }
            if (size >= 256) continue;
            using var icon = new Icon(path, size, size);
            using var bmp = icon.ToBitmap();
            if (icon.Width != size || bmp.Width != size)
            {
                Console.Error.WriteLine($"{Path.GetFileName(path)}: asked for {size}px, got {icon.Width}px");
                failures++;
            }
        }
        return failures;
    }

    // ---------------------------------------------------------------- layout

    sealed record Layout(Rectangle Front, Rectangle? Back, int Radius, int RimWidth);

    static Layout LayoutFor(int s) => s switch
    {
        // Hand-tuned on the pixel grid: integer edges, 1 px rim.
        16 => new Layout(new Rectangle(0, 1, 15, 15), null, 3, 1),
        20 => new Layout(new Rectangle(0, 5, 15, 15), new Rectangle(5, 0, 15, 15), 3, 1),
        24 => new Layout(new Rectangle(0, 6, 18, 18), new Rectangle(6, 0, 18, 18), 4, 1),
        _ => Scaled(s),
    };

    static Layout Scaled(int s)
    {
        var f = (int)Math.Round(0.72 * s);           // front card side
        var d = (int)Math.Round(0.25 * s);           // back card offset (up and right)
        var spare = s - f - d;
        var off = spare / 2;
        var front = new Rectangle(off, s - off - f, f, f);
        var back = new Rectangle(off + d, s - off - f - d, f, f);
        return new Layout(front, back, (int)Math.Round(0.2 * f), Math.Max(1, (int)Math.Round(s / 48.0)));
    }

    // ---------------------------------------------------------------- drawing

    static Bitmap Draw(Variant v, int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        var l = LayoutFor(s);

        using (var g = NewGraphics(bmp))
        {
            if (v == Variant.HighContrast)
            {
                var stroke = Math.Max(1, (int)Math.Round(s / 24.0));
                if (l.Back is { } back)
                {
                    OutlineCard(g, back, l.Radius, stroke);
                    // Knock the front card's area out of the back card's outline before outlining it.
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using (var clear = new SolidBrush(Color.Transparent))
                    using (var p = RoundedRect(l.Front, l.Radius))
                        g.FillPath(clear, p);
                    g.CompositingMode = CompositingMode.SourceOver;
                }
                OutlineCard(g, l.Front, l.Radius, stroke);
            }
            else
            {
                if (l.Back is { } back) FillCard(g, back, l.Radius, l.RimWidth, BackFill);
                FillCard(g, l.Front, l.Radius, l.RimWidth, FrontFill);
            }
        }

        DrawC(bmp, l.Front, v == Variant.HighContrast ? Mono : Ink);

        switch (v)
        {
            case Variant.Warning: DrawBadge(bmp, s, Badge.Warning); break;
            case Variant.Error: DrawBadge(bmp, s, Badge.Error); break;
            case Variant.Running: DrawBadge(bmp, s, Badge.Running); break;
        }
        return bmp;
    }

    static Graphics NewGraphics(Bitmap bmp)
    {
        var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;   // integer coordinates fall on pixel edges
        g.CompositingQuality = CompositingQuality.HighQuality;
        return g;
    }

    static void FillCard(Graphics g, Rectangle r, int radius, int rim, Color fill)
    {
        using (var rimBrush = new SolidBrush(Rim))
        using (var outer = RoundedRect(r, radius))
            g.FillPath(rimBrush, outer);
        var inner = Rectangle.Inflate(r, -rim, -rim);
        using (var fillBrush = new SolidBrush(fill))
        using (var p = RoundedRect(inner, Math.Max(1, radius - rim)))
            g.FillPath(fillBrush, p);
    }

    static void OutlineCard(Graphics g, Rectangle r, int radius, int stroke)
    {
        using var path = RoundedRect(r, radius);
        using var inner = RoundedRect(Rectangle.Inflate(r, -stroke, -stroke), Math.Max(1, radius - stroke));
        using var region = new Region(path);
        region.Exclude(inner);
        using var brush = new SolidBrush(Mono);
        g.FillRegion(brush, region);
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // The C. Hand-drawn pixel masks at the small sizes (2 px stroke), a vector arc above.
    // Mask legend: '#' full, 'o' 80%, '+' 55%, ':' 30%, '.' empty.
    static readonly string[] C15 =
    [
        "..:####:",
        ".o#####o",
        "o#+.....",
        "##......",
        "##......",
        "##......",
        "o#+.....",
        ".o#####o",
        "..:####:",
    ];

    static readonly string[] C18 =
    [
        "...+####+:",
        ".o#######o",
        ".##o:.....",
        "o#+.......",
        "##........",
        "##........",
        "##........",
        "o#+.......",
        ".##o:.....",
        ".o#######o",
        "...+####+:",
    ];

    static void DrawC(Bitmap bmp, Rectangle card, Color color)
    {
        string[]? mask = card.Width switch { 15 => C15, 18 => C18, _ => null };
        if (mask != null)
        {
            var w = mask[0].Length;
            var h = mask.Length;
            Stamp(bmp, mask, card.X + (card.Width - w) / 2, card.Y + (card.Height - h) / 2, color);
            return;
        }

        var f = card.Width;
        var stroke = Math.Max(3f, (float)Math.Round(0.12 * f));
        var outer = 0.60f * f;                       // C height
        var r = (outer - stroke) / 2f;               // pen-centre radius
        var cx = card.X + f / 2f;
        var cy = card.Y + f / 2f;
        using var g = NewGraphics(bmp);
        using var pen = new Pen(color, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, 45, 270);
    }

    // ---------------------------------------------------------------- badges

    enum Badge { Warning, Error, Running }

    static int BadgeSize(int s) => s switch
    {
        16 => 7,
        20 => 9,
        24 => 11,
        32 => 14,
        _ => (int)Math.Round(14.0 * s / 32),
    };

    static readonly Dictionary<(Badge, int), string[]> BadgeMasks = new()
    {
        [(Badge.Error, 7)] =
        [
            ".......",
            ".#...#.",
            "..#.#..",
            "...#...",
            "..#.#..",
            ".#...#.",
            ".......",
        ],
        [(Badge.Error, 9)] =
        [
            ".........",
            ".........",
            "..#...#..",
            "...#.#...",
            "....#....",
            "...#.#...",
            "..#...#..",
            ".........",
            ".........",
        ],
        [(Badge.Running, 7)] =
        [
            ".......",
            "..#....",
            "..##...",
            "..###..",
            "..##...",
            "..#....",
            ".......",
        ],
        [(Badge.Running, 9)] =
        [
            ".........",
            ".........",
            "...#.....",
            "...##....",
            "...###...",
            "...##....",
            "...#.....",
            ".........",
            ".........",
        ],
        [(Badge.Warning, 7)] =
        [
            ".......",
            ".......",
            "...#...",
            "...#...",
            ".......",
            "...#...",
            ".......",
        ],
        [(Badge.Warning, 9)] =
        [
            ".........",
            ".........",
            ".........",
            "....#....",
            "....#....",
            "....#....",
            ".........",
            "....#....",
            ".........",
        ],
    };

    static void DrawBadge(Bitmap bmp, int s, Badge kind)
    {
        var b = BadgeSize(s);
        var x = s - b;
        var y = s - b;
        var ring = Math.Max(1f, (float)Math.Round(b / 14.0));

        using (var g = NewGraphics(bmp))
        using (var shape = BadgeShape(kind, x, y, b))
        {
            using (var ringPen = new Pen(BadgeRing, 2 * ring) { LineJoin = LineJoin.Round })
                g.DrawPath(ringPen, shape);
            using (var fill = new SolidBrush(kind switch { Badge.Error => ErrorFill, Badge.Warning => WarnFill, _ => RunFill }))
                g.FillPath(fill, shape);
            if (kind == Badge.Warning && b >= 11)   // too small for an outline below 11 px; the ring separates it
            {
                using var inset = BadgeShape(kind, x, y, b, inset: 1);
                using var outline = new Pen(WarnOutline, 1f) { LineJoin = LineJoin.Round };
                g.DrawPath(outline, inset);
            }
        }

        var symbol = kind == Badge.Warning ? Color.Black : Color.White;
        if (BadgeMasks.TryGetValue((kind, b), out var mask))
        {
            Stamp(bmp, mask, x, y, symbol);
            return;
        }

        using var g2 = NewGraphics(bmp);
        var w = Math.Max(1.5f, b * 0.14f);
        switch (kind)
        {
            case Badge.Error:
            {
                var c = new PointF(x + b / 2f, y + b / 2f);
                var k = b * 0.2f;
                using var pen = new Pen(symbol, w) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                g2.DrawLine(pen, c.X - k, c.Y - k, c.X + k, c.Y + k);
                g2.DrawLine(pen, c.X - k, c.Y + k, c.X + k, c.Y - k);
                break;
            }
            case Badge.Running:
            {
                using var brush = new SolidBrush(symbol);
                g2.FillPolygon(brush, new[]
                {
                    new PointF(x + b * 0.38f, y + b * 0.27f),
                    new PointF(x + b * 0.38f, y + b * 0.73f),
                    new PointF(x + b * 0.76f, y + b * 0.50f),
                });
                break;
            }
            case Badge.Warning:
            {
                using var brush = new SolidBrush(symbol);
                var cx = x + b / 2f;
                g2.FillRectangle(brush, cx - w / 2, y + b * 0.34f, w, b * 0.34f);
                g2.FillRectangle(brush, cx - w / 2, y + b * 0.74f, w, w);
                break;
            }
        }
    }

    static GraphicsPath BadgeShape(Badge kind, float x, float y, float b, float inset = 0)
    {
        var p = new GraphicsPath();
        if (kind == Badge.Warning)
        {
            p.AddPolygon(new[]
            {
                new PointF(x + b / 2f, y + inset),
                new PointF(x + b - inset, y + b - inset),
                new PointF(x + inset, y + b - inset),
            });
        }
        else
        {
            p.AddEllipse(x + inset, y + inset, b - 2 * inset, b - 2 * inset);
        }
        return p;
    }

    // ---------------------------------------------------------------- helpers

    // Source-over blend of a pixel mask onto the bitmap.
    static void Stamp(Bitmap bmp, string[] mask, int x0, int y0, Color color)
    {
        for (var y = 0; y < mask.Length; y++)
        for (var x = 0; x < mask[y].Length; x++)
        {
            var a = mask[y][x] switch { '#' => 1f, 'o' => 0.8f, '+' => 0.55f, ':' => 0.3f, _ => 0f };
            if (a <= 0) continue;
            var px = x0 + x;
            var py = y0 + y;
            if (px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height) continue;
            var d = bmp.GetPixel(px, py);
            var da = d.A / 255f;
            var oa = a + da * (1 - a);
            int Mix(int src, int dst) => (int)Math.Round((src * a + dst * da * (1 - a)) / oa);
            bmp.SetPixel(px, py, Color.FromArgb((int)Math.Round(oa * 255), Mix(color.R, d.R), Mix(color.G, d.G), Mix(color.B, d.B)));
        }
    }

    static Color Hex(string hex) => ColorTranslator.FromHtml(hex);

    static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "clauderoo.slnx"))) return dir.FullName;
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "clauderoo.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Can't find clauderoo.slnx; pass the output directory as the first argument.");
    }
}
