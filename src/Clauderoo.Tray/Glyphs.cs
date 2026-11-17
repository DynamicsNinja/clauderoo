using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Clauderoo.Tray;

/// <summary>Icons for menus, buttons and status lists (Segoe Fluent Icons code points; the same ones exist in Segoe MDL2 Assets).</summary>
internal enum Glyph
{
    Play,          // E768  Start session
    FolderOpen,    // E838  Start session in folder…
    Star,          // E735  FavoriteStarFill: the default profile
    Home,          // E80F  the home profile
    Person,        // E77B  Contact: Claude login
    Cloud,         // E753  Google Vertex AI
    Key,           // E8D7  Permissions: API key
    Settings,      // E713  Manage profiles…
    Add,           // E710  New profile…
    Rename,        // E8AC
    Delete,        // E74D
    Warning,       // E7BA
    Error,         // EB90  StatusErrorFull
    Ok,            // EC61  CompletedSolid
    Info,          // E946
    Exit,          // E7E8  PowerButton
    Refresh,       // E72C
    Undo,          // E7A7
    StarOutline,   // E734  FavoriteStar: Set as default
    Check,         // E73E  CheckMark
    Diagnostic,    // E9D9  Check setup
    Folder,        // E8B7  Open profiles folder
    Remove,        // E738  Skipped / Delete variable
    Save,          // E74E
}

/// <summary>
/// Renders <see cref="Glyph"/>s to bitmaps at a given DPI with Segoe Fluent Icons (Windows 11), falling back to
/// Segoe MDL2 Assets (Windows 10) and then to plain text. Grayscale anti-aliasing, so the bitmaps have clean alpha.
/// </summary>
internal static class Glyphs
{
    static readonly FontFamily? IconFamily = FindFamily("Segoe Fluent Icons") ?? FindFamily("Segoe MDL2 Assets");

    /// <summary>The icon font in use, or null when only the text fallback is available.</summary>
    public static string? FontName => IconFamily?.Name;

    public static char CodePoint(Glyph glyph) => glyph switch
    {
        Glyph.Play => '',
        Glyph.FolderOpen => '',
        Glyph.Star => '',
        Glyph.Home => '',
        Glyph.Person => '',
        Glyph.Cloud => '',
        Glyph.Key => '',
        Glyph.Settings => '',
        Glyph.Add => '',
        Glyph.Rename => '',
        Glyph.Delete => '',
        Glyph.Warning => '',
        Glyph.Error => '',
        Glyph.Ok => '',
        Glyph.Info => '',
        Glyph.Exit => '',
        Glyph.Refresh => '',
        Glyph.Undo => '',
        Glyph.StarOutline => '',
        Glyph.Check => '',
        Glyph.Diagnostic => '',
        Glyph.Folder => '',
        Glyph.Remove => '',
        Glyph.Save => '',
        _ => throw new ArgumentOutOfRangeException(nameof(glyph), glyph, null),
    };

    // Used only when neither icon font is installed (pre-Windows 10).
    static string FallbackText(Glyph glyph) => glyph switch
    {
        Glyph.Play => "▶",
        Glyph.FolderOpen or Glyph.Folder => "□",
        Glyph.Star => "★",
        Glyph.StarOutline => "☆",
        Glyph.Home => "⌂",
        Glyph.Person => "☺",
        Glyph.Cloud => "☁",
        Glyph.Key => "•",
        Glyph.Settings => "⚙",
        Glyph.Add => "+",
        Glyph.Rename => "✎",
        Glyph.Delete => "×",
        Glyph.Warning => "!",
        Glyph.Error => "✖",
        Glyph.Ok or Glyph.Check => "✓",
        Glyph.Info => "i",
        Glyph.Exit => "⏻",
        Glyph.Refresh => "↻",
        Glyph.Undo => "↶",
        Glyph.Diagnostic => "✓",
        Glyph.Remove => "–",
        Glyph.Save => "↓",
        _ => "?",
    };

    /// <summary>
    /// A square bitmap of <paramref name="logicalSize"/> × <paramref name="dpi"/>/96 pixels (16 logical at 144 DPI → 24 px)
    /// with the glyph drawn in <paramref name="color"/> (default <see cref="SystemColors.ControlText"/>). The caller owns it.
    /// </summary>
    public static Bitmap Render(Glyph glyph, int logicalSize, int dpi, Color? color = null)
    {
        var px = Math.Max(1, (int)Math.Round(logicalSize * dpi / 96.0));
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color ?? SystemColors.ControlText);

        if (IconFamily is { } family)
        {
            // The icon fonts draw each glyph inside a 1 em square that sits on the baseline; put that square on the bitmap.
            using var font = new Font(family, px, GraphicsUnit.Pixel);
            var ascent = px * family.GetCellAscent(FontStyle.Regular) / (float)family.GetEmHeight(FontStyle.Regular);
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
            g.DrawString(CodePoint(glyph).ToString(), font, brush, 0, px - ascent, format);
        }
        else
        {
            using var font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, px * 0.75f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(FallbackText(glyph), font, brush, new RectangleF(0, 0, px, px), format);
        }
        return bmp;
    }

    static FontFamily? FindFamily(string name)
    {
        using var installed = new InstalledFontCollection();
        return installed.Families.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Fluent status colours (about 4.5:1 or better on the Control background). In High Contrast they become
/// <see cref="SystemColors.WindowText"/> / <see cref="SystemColors.GrayText"/>: the glyph's shape carries the meaning.
/// </summary>
internal static class StatusColors
{
    static readonly Color OkColor = Color.FromArgb(0x0F, 0x7B, 0x0F);
    static readonly Color WarnColor = Color.FromArgb(0x9D, 0x5D, 0x00);
    static readonly Color FailColor = Color.FromArgb(0xC4, 0x2B, 0x1C);
    static readonly Color AccentColor = Color.FromArgb(0xC8, 0x64, 0x3F);

    public static Color Ok => SystemInformation.HighContrast ? SystemColors.WindowText : OkColor;
    public static Color Warn => SystemInformation.HighContrast ? SystemColors.WindowText : WarnColor;
    public static Color Fail => SystemInformation.HighContrast ? SystemColors.WindowText : FailColor;
    /// <summary>Skipped / not checked / info.</summary>
    public static Color Muted => SystemColors.GrayText;
    /// <summary>Brand terracotta for the default-profile star.</summary>
    public static Color Accent => SystemInformation.HighContrast ? SystemColors.WindowText : AccentColor;
}
