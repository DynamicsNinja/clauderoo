using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>What the tray menu shows: profiles, the current problems and running sessions per profile.</summary>
internal sealed record TrayMenuState(
    IReadOnlyList<ProfileInfo> Profiles,
    IReadOnlyList<Problem> Problems,
    IReadOnlyDictionary<string, int> Running,
    bool AutoStartOn,
    int RecentCount = 0)
{
    public int RunningTotal => Running.Values.Sum();
    public ProfileInfo? Default => Profiles.FirstOrDefault(p => p.IsDefault) ?? Profiles.FirstOrDefault(p => p.IsHome);

    public static IReadOnlyDictionary<string, int> CountRunning(ProfileManager pm) =>
        pm.Sessions.Running().GroupBy(s => s.Profile).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>The menu's actions, so the menu can be built (and rendered) without a running tray.</summary>
internal sealed class TrayMenuActions
{
    public Action<string> Start { get; init; } = _ => { };
    public Action<string> StartInFolder { get; init; } = _ => { };
    public Action<string> SetDefault { get; init; } = _ => { };
    public Action ShowProblems { get; init; } = () => { };
    public Action ShowProfiles { get; init; } = () => { };
    public Action NewProfile { get; init; } = () => { };
    public Action OpenProfilesFolder { get; init; } = () => { };
    public Action<bool> SetAutoStart { get; init; } = _ => { };
    public Action SetUp { get; init; } = () => { };
    public Action Retry { get; init; } = () => { };
    public Action CopyConfigDetails { get; init; } = () => { };
    public Action ShowConfigFile { get; init; } = () => { };
    public Action Exit { get; init; } = () => { };
}

/// <summary>Builds the tray menu. The default profile is bold (double-click starts it) and marked with a star.</summary>
internal static class TrayMenu
{
    const int GlyphSize = 16;
    const int StarSize = 12;

    /// <summary>Tag on the default profile's item: the renderer draws the star in the check column.</summary>
    sealed class DefaultMark;

    /// <summary>Tag on a profile item: the text after <see cref="MutedTail"/>'s position is drawn grey.</summary>
    sealed record ProfileMark(bool IsDefault, string? MutedTail);

    /// <summary>Tag on a disabled header whose glyph keeps its colour.</summary>
    sealed class ColourGlyph;

    public static void Build(ContextMenuStrip menu, TrayMenuState s, TrayMenuActions a)
    {
        Reset(menu);
        var dpi = menu.DeviceDpi;
        var items = menu.Items;

        if (s.Problems.Count > 0)
        {
            // The header itself is the way into the problems.
            var severe = s.Problems.Any(TrayText.IsSevere);
            var header = Item(TrayText.Header(s.Problems.Count),
                Glyphs.Get(severe ? Glyph.Error : Glyph.Warning, GlyphSize, dpi, severe ? StatusColors.Fail : StatusColors.Warn),
                a.ShowProblems);
            header.Font = Bold(menu.Font);
            header.AccessibleName = $"{TrayText.Problems(s.Problems.Count)}, show them";
            items.Add(header);
        }
        else
        {
            var header = Header(TrayText.Header(0), menu.Font);
            header.Image = Glyphs.Get(Glyph.Ok, GlyphSize, dpi, StatusColors.Ok);
            header.Tag = new ColourGlyph();
            items.Add(header);
            // Without problems the Problems window is reached here, for what changed without a balloon.
            if (s.RecentCount > 0) items.Add(Item("Recent &messages…", null, a.ShowProblems));
        }
        items.Add(new ToolStripSeparator());

        var troubled = s.Problems.Select(p => p.Profile).ToHashSet();
        foreach (var p in s.Profiles)
            items.Add(ProfileItem(p, s.Running.GetValueOrDefault(p.Name), troubled.Contains(p.Name), menu, a, dpi));

        items.Add(new ToolStripSeparator());
        items.Add(Item("&Manage profiles…", Neutral(Glyph.Settings, dpi), a.ShowProfiles));
        items.Add(Item("&New profile…", Neutral(Glyph.Add, dpi), a.NewProfile));

        var advanced = new ToolStripMenuItem("&Advanced");
        PrepareDropDown(advanced, dpi);
        var folder = Item("&Open profiles folder", Neutral(Glyph.Folder, dpi), a.OpenProfilesFolder);
        folder.ToolTipText = "For troubleshooting. Changes made in these folders may be overwritten by sync.";
        advanced.DropDownItems.Add(folder);
        var auto = new ToolStripMenuItem("Start with &Windows") { Checked = s.AutoStartOn };
        auto.Click += (_, _) => a.SetAutoStart(!auto.Checked);
        advanced.DropDownItems.Add(auto);
        items.Add(advanced);

        items.Add(new ToolStripSeparator());
        items.Add(Item("E&xit clauderoo (stops syncing)", Neutral(Glyph.Exit, dpi), a.Exit));
    }

    /// <summary>The menu when the settings can't be loaded: what's wrong in plain words, and what can be done.</summary>
    public static void BuildFallback(ContextMenuStrip menu, ConfigIssue issue, string headline, string detail, TrayMenuActions a)
    {
        Reset(menu);
        var dpi = menu.DeviceDpi;
        var items = menu.Items;
        var head = Header(headline, menu.Font);
        head.Image = Glyphs.Get(Glyph.Error, GlyphSize, dpi, StatusColors.Fail);
        head.Tag = new ColourGlyph();
        items.Add(head);
        items.Add(new ToolStripMenuItem(detail.Replace("&", "&&")) { Enabled = false });
        items.Add(new ToolStripSeparator());
        switch (issue)
        {
            case ConfigIssue.Missing:
                items.Add(Item("&Set up clauderoo", Neutral(Glyph.Play, dpi), a.SetUp));
                break;
            case ConfigIssue.Damaged:
                items.Add(Item("&Copy details", Neutral(Glyph.Info, dpi), a.CopyConfigDetails));
                items.Add(Item("Show the settings &file", Neutral(Glyph.FolderOpen, dpi), a.ShowConfigFile));
                break;
        }
        items.Add(Item("&Retry", Neutral(Glyph.Refresh, dpi), a.Retry));
        items.Add(new ToolStripSeparator());
        items.Add(Item("E&xit clauderoo (stops syncing)", Neutral(Glyph.Exit, dpi), a.Exit));
    }

    static ToolStripMenuItem ProfileItem(ProfileInfo p, int running, bool hasProblems, ContextMenuStrip menu, TrayMenuActions a, int dpi)
    {
        var name = p.Name;
        var label = TrayText.ProfileLabel(p);
        var glyph = p.Provider switch
        {
            Providers.Vertex => Glyph.Cloud,
            Providers.ApiKey => Glyph.Key,
            _ => Glyph.Person,
        };
        var item = new ToolStripMenuItem(label.Replace("&", "&&"))
        {
            // A profile with problems: its provider glyph turns amber.
            Image = Glyphs.Get(glyph, GlyphSize, dpi, hasProblems ? StatusColors.Warn : SystemColors.MenuText),
            AccessibleName = label + (p.IsDefault ? ", default" : "") + (hasProblems ? ", has problems" : "")
                             + (running > 0 ? ", " + TrayText.Running(running) : ""),
            ToolTipText = p.IsDefault ? "Default profile: double-click the tray icon to start it."
                : hasProblems ? "This profile has problems: see the first menu item." : null,
            Tag = new ProfileMark(p.IsDefault, TrayText.IsSignedOut(p) ? TrayText.NotSignedIn : null),
        };
        if (p.IsDefault) item.Font = Bold(menu.Font);
        item.Click += (_, _) =>
        {
            menu.Close(ToolStripDropDownCloseReason.ItemClicked); // a cascading item doesn't close the menu itself
            a.Start(name);
        };

        PrepareDropDown(item, dpi);
        item.DropDown.Font = menu.Font; // the submenu would inherit the bold font otherwise
        var start = Item("&Start session", Neutral(Glyph.Play, dpi), () => a.Start(name));
        if (p.IsDefault) start.Font = Bold(menu.Font);
        item.DropDownItems.Add(start);
        item.DropDownItems.Add(Item("Start in &folder…", Neutral(Glyph.FolderOpen, dpi), () => a.StartInFolder(name)));
        var setDefault = Item(p.IsDefault ? "Already the default" : "Set as &default",
            p.IsDefault ? Glyphs.Get(Glyph.Star, GlyphSize, dpi, StatusColors.Accent) : Neutral(Glyph.StarOutline, dpi),
            () => a.SetDefault(name));
        setDefault.Enabled = !p.IsDefault;
        item.DropDownItems.Add(setDefault);
        if (running > 0)
        {
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add(new ToolStripMenuItem("● " + TrayText.Running(running)) { Enabled = false });
        }
        return item;
    }

    static ToolStripMenuItem Header(string text, Font font) =>
        new(text.Replace("&", "&&")) { Enabled = false, Font = Bold(font), AccessibleRole = AccessibleRole.StaticText };

    static ToolStripMenuItem Item(string text, Image? image, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { Image = image };
        item.Click += (_, _) => onClick();
        return item;
    }

    static Bitmap Neutral(Glyph g, int dpi) => Glyphs.Get(g, GlyphSize, dpi, SystemColors.MenuText);

    static Size ImageSize(int dpi)
    {
        var px = (int)Math.Round(GlyphSize * dpi / 96.0);
        return new Size(px, px);
    }

    static void PrepareDropDown(ToolStripMenuItem item, int dpi)
    {
        item.DropDown.ImageScalingSize = ImageSize(dpi);
        item.DropDown.ShowItemToolTips = true;
    }

    /// <summary>Disposes the previous items (and their submenus) and sets up images, margins and the renderer.</summary>
    static void Reset(ContextMenuStrip menu)
    {
        var old = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var i in old) i.Dispose();
        menu.ImageScalingSize = ImageSize(menu.DeviceDpi);
        menu.ShowImageMargin = true;
        menu.ShowCheckMargin = true; // the star column for the default profile
        menu.ShowItemToolTips = true;
        if (menu.Renderer is not TrayRenderer) menu.Renderer = new TrayRenderer();
    }

    static Font? _bold;

    // One bold font for the life of the app (menus never dispose fonts they're given).
    static Font Bold(Font baseFont)
    {
        if (_bold == null || _bold.Size != baseFont.Size || _bold.FontFamily.Name != baseFont.FontFamily.Name)
            _bold = new Font(baseFont, FontStyle.Bold);
        return _bold;
    }

    /// <summary>
    /// The professional renderer plus: the default profile's star in the check column, "not signed in" in grey, and
    /// header glyphs that keep their colour although the header is disabled.
    /// </summary>
    sealed class TrayRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item.Tag is ColourGlyph && e.Item.Image is { } img)
                e.Graphics.DrawImage(img, e.ImageRectangle); // base would grey it out (the item is disabled)
            else
                base.OnRenderItemImage(e);

            if (e.Item.Tag is not ProfileMark { IsDefault: true }) return;
            var r = e.ImageRectangle;
            var dpi = e.Item.Owner?.DeviceDpi ?? 96;
            // The check column ends where the image column starts; a slightly smaller star fits it with a gap.
            var star = Glyphs.Get(Glyph.Star, StarSize, dpi, e.Item.Enabled ? StatusColors.Accent : SystemColors.GrayText);
            var gap = Math.Max(1, (int)Math.Round(2 * dpi / 96.0));
            var left = Math.Max(0, (int)Math.Round(3 * dpi / 96.0)); // clear of the selection border
            var x = left + Math.Max(0, (r.X - gap - left - star.Width) / 2);
            e.Graphics.DrawImage(star, new Rectangle(x, r.Y + (r.Height - star.Height) / 2, star.Width, star.Height));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Tag is not ProfileMark { MutedTail: { } tail } || !e.Item.Enabled
                || e.Text is not { } text || !text.EndsWith(tail))
            {
                base.OnRenderItemText(e);
                return;
            }
            // Draw the text in two runs: the name and provider in the normal colour, "not signed in" in grey.
            var flags = e.TextFormat | TextFormatFlags.NoPadding;
            var head = text[..^tail.Length];
            var pad = TextRenderer.MeasureText(e.Graphics, "x", e.TextFont, e.TextRectangle.Size, e.TextFormat).Width
                      - TextRenderer.MeasureText(e.Graphics, "x", e.TextFont, e.TextRectangle.Size, flags).Width;
            var r = e.TextRectangle;
            var x = r.X + pad / 2;
            var headWidth = TextRenderer.MeasureText(e.Graphics, head, e.TextFont, r.Size, flags).Width;
            TextRenderer.DrawText(e.Graphics, head, e.TextFont, new Rectangle(x, r.Y, r.Right - x, r.Height), e.TextColor, flags);
            TextRenderer.DrawText(e.Graphics, tail, e.TextFont, new Rectangle(x + headWidth, r.Y, Math.Max(0, r.Right - x - headWidth), r.Height),
                StatusColors.Muted, flags);
        }
    }
}
