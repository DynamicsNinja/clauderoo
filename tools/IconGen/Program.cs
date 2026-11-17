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
    static readonly Color Mono = Color.Black;   // app-hc*.ico; the app tints them to SystemColors.WindowText

    static readonly Color ErrorFill = Hex("#C42B1C");
    static readonly Color WarnFill = Hex("#F7C300");
    static readonly Color WarnOutline = Hex("#5C4500");
    static readonly Color RunFill = Hex("#107C10");
    static readonly Color BadgeRing = Color.White;



    static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(FindRepoRoot(), "src", "Clauderoo.Tray", "Icons");
        Directory.CreateDirectory(outDir);

        // Colour set plus a monochrome (High Contrast) set; each with no badge and the three state badges.
        var files = new (Badge? Badge, bool Mono, string Name)[]
        {
            (null, false, "app.ico"),
            (Badge.Warning, false, "state-warning.ico"),
            (Badge.Error, false, "state-error.ico"),
            (Badge.Running, false, "state-running.ico"),
            (null, true, "app-hc.ico"),
            (Badge.Warning, true, "app-hc-warning.ico"),
            (Badge.Error, true, "app-hc-error.ico"),
            (Badge.Running, true, "app-hc-running.ico"),
        };

        var failures = 0;
        foreach (var (badge, mono, name) in files)
        {
            var frames = new List<IcoFile.Frame>();
            foreach (var size in Sizes)
            {
                using var bmp = Draw(badge, mono, size);
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

    static Bitmap Draw(Badge? badge, bool mono, int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        var l = LayoutFor(s);

        using (var g = NewGraphics(bmp))
        {
            if (mono)
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

        DrawC(bmp, l.Front, mono ? Mono : Ink);
        if (badge is { } kind) DrawBadge(bmp, s, kind, mono);
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

    // Symbol masks for the small badges, hand-drawn on the pixel grid (the 9 px × has 2 px arms).
    static readonly Dictionary<(Badge, int), string[]> BadgeMasks = new()
    {
        [(Badge.Error, 7)] =
        [
            ".......",
            ".:...:.",
            "..#.#..",
            "...#...",
            "..#.#..",
            ".:...:.",
            ".......",
        ],
        [(Badge.Error, 9)] =
        [
            ".........",
            ".........",
            "..##.##..",
            "...###...",
            "....#....",
            "...###...",
            "..##.##..",
            ".........",
            ".........",
        ],
        [(Badge.Running, 7)] =
        [
            ".......",
            "..#+...",
            "..##o..",
            "..####.",
            "..##o..",
            "..#+...",
            ".......",
        ],
        [(Badge.Running, 9)] =
        [
            ".........",
            ".........",
            "...#+....",
            "...##o...",
            "...####..",
            "...##o...",
            "...#+....",
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

    // Shapes differ so the state reads without colour: error = circle with ×, warning = triangle with !,
    // running = rounded square with ▶. In the monochrome set the ring and the symbol are knocked out (transparent).
    static void DrawBadge(Bitmap bmp, int s, Badge kind, bool mono)
    {
        var b = BadgeSize(s);
        var x = s - b;
        var y = s - b;
        var ring = Math.Max(1f, (float)Math.Round(b / 14.0));

        using (var shape = BadgeShape(kind, x, y, b))
        {
            using (var ringMask = Mask(s, g =>
            {
                using var pen = new Pen(Color.White, 2 * ring) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, shape);
                g.FillPath(Brushes.White, shape);
            }))
            {
                if (mono) Erase(bmp, ringMask); else Composite(bmp, ringMask, BadgeRing);
            }

            using var g = NewGraphics(bmp);
            using (var fill = new SolidBrush(mono ? Mono : kind switch { Badge.Error => ErrorFill, Badge.Warning => WarnFill, _ => RunFill }))
                g.FillPath(fill, shape);
            if (!mono && kind == Badge.Warning && b >= 11)   // too small for an outline below 11 px; the ring separates it
            {
                using var inset = BadgeShape(kind, x, y, b, inset: 1);
                using var outline = new Pen(WarnOutline, 1f) { LineJoin = LineJoin.Round };
                g.DrawPath(outline, inset);
            }
        }

        using var symbol = SymbolMask(kind, s, x, y, b);
        if (mono) Erase(bmp, symbol);
        else Composite(bmp, symbol, kind == Badge.Warning ? Color.Black : Color.White);
    }

    static Bitmap SymbolMask(Badge kind, int s, int x, int y, int b)
    {
        if (BadgeMasks.TryGetValue((kind, b), out var mask))
        {
            var m = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            Stamp(m, mask, x, y, Color.White);
            return m;
        }

        return Mask(s, g =>
        {
            var w = Math.Max(1.5f, b * 0.14f);
            switch (kind)
            {
                case Badge.Error:
                {
                    var c = new PointF(x + b / 2f, y + b / 2f);
                    var k = b * 0.2f;
                    using var pen = new Pen(Color.White, w) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                    g.DrawLine(pen, c.X - k, c.Y - k, c.X + k, c.Y + k);
                    g.DrawLine(pen, c.X - k, c.Y + k, c.X + k, c.Y - k);
                    break;
                }
                case Badge.Running:
                    g.FillPolygon(Brushes.White, new[]
                    {
                        new PointF(x + b * 0.34f, y + b * 0.24f),
                        new PointF(x + b * 0.34f, y + b * 0.76f),
                        new PointF(x + b * 0.78f, y + b * 0.50f),
                    });
                    break;
                case Badge.Warning:
                {
                    var cx = x + b / 2f;
                    g.FillRectangle(Brushes.White, cx - w / 2, y + b * 0.34f, w, b * 0.34f);
                    g.FillRectangle(Brushes.White, cx - w / 2, y + b * 0.74f, w, w);
                    break;
                }
            }
        });
    }

    static GraphicsPath BadgeShape(Badge kind, float x, float y, float b, float inset = 0)
    {
        var p = new GraphicsPath();
        switch (kind)
        {
            case Badge.Warning:
                p.AddPolygon(new[]
                {
                    new PointF(x + b / 2f, y + inset),
                    new PointF(x + b - inset, y + b - inset),
                    new PointF(x + inset, y + b - inset),
                });
                break;
            case Badge.Running:
                p.Dispose();
                return RoundedRect(new RectangleF(x + inset, y + inset, b - 2 * inset, b - 2 * inset), Math.Max(1f, b * 0.22f));
            default:
                p.AddEllipse(x + inset, y + inset, b - 2 * inset, b - 2 * inset);
                break;
        }
        return p;
    }

    // ---------------------------------------------------------------- helpers

    // A white-on-transparent coverage mask, drawn anti-aliased.
    static Bitmap Mask(int s, Action<Graphics> draw)
    {
        var m = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = NewGraphics(m);
        draw(g);
        return m;
    }

    // Paints color over the bitmap with the mask's alpha as coverage.
    static void Composite(Bitmap bmp, Bitmap mask, Color color)
    {
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var a = mask.GetPixel(x, y).A / 255f;
            if (a > 0) Blend(bmp, x, y, color, a);
        }
    }

    // Removes coverage: alpha *= (1 - mask alpha).
    static void Erase(Bitmap bmp, Bitmap mask)
    {
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var a = mask.GetPixel(x, y).A / 255f;
            if (a <= 0) continue;
            var d = bmp.GetPixel(x, y);
            bmp.SetPixel(x, y, Color.FromArgb((int)Math.Round(d.A * (1 - a)), d.R, d.G, d.B));
        }
    }

    // Source-over blend of a pixel mask onto the bitmap.
    static void Stamp(Bitmap bmp, string[] mask, int x0, int y0, Color color)
    {
        for (var y = 0; y < mask.Length; y++)
        for (var x = 0; x < mask[y].Length; x++)
        {
            var a = mask[y][x] switch { '#' => 1f, 'o' => 0.8f, '+' => 0.55f, ':' => 0.3f, _ => 0f };
            var px = x0 + x;
            var py = y0 + y;
            if (a <= 0 || px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height) continue;
            Blend(bmp, px, py, color, a);
        }
    }

    static void Blend(Bitmap bmp, int px, int py, Color color, float a)
    {
        var d = bmp.GetPixel(px, py);
        var da = d.A / 255f;
        var oa = a + da * (1 - a);
        int Mix(int src, int dst) => (int)Math.Round((src * a + dst * da * (1 - a)) / oa);
        bmp.SetPixel(px, py, Color.FromArgb((int)Math.Round(oa * 255), Mix(color.R, d.R), Mix(color.G, d.G), Mix(color.B, d.B)));
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
