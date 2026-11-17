using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>What the tray menu shows: profiles, the current problems and running sessions per profile.</summary>
internal sealed record TrayMenuState(
    IReadOnlyList<ProfileInfo> Profiles,
    IReadOnlyList<Problem> Problems,
    IReadOnlyDictionary<string, int> Running,
    bool AutoStartOn)
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
    public Action Exit { get; init; } = () => { };
}

/// <summary>Builds the tray menu. The default profile is bold (double-click starts it) and marked with a star.</summary>
internal static class TrayMenu
{
    const int GlyphSize = 16;
    const int StarSize = 12;

    /// <summary>Tag on the default profile's item: the renderer draws the star in the check column.</summary>
    sealed class DefaultMark;

    public static void Build(ContextMenuStrip menu, TrayMenuState s, TrayMenuActions a)
    {
        Reset(menu);
        var dpi = menu.DeviceDpi;
        var items = menu.Items;

        items.Add(Header(TrayText.Header(s.Problems.Count), menu.Font));
        items.Add(new ToolStripSeparator());

        foreach (var p in s.Profiles)
            items.Add(ProfileItem(p, s.Running.GetValueOrDefault(p.Name), menu, a, dpi));

        items.Add(new ToolStripSeparator());
        if (s.Problems.Count > 0)
        {
            var severe = s.Problems.Any(TrayText.IsSevere);
            var problems = Item($"{s.Problems.Count} &{(s.Problems.Count == 1 ? "problem" : "problems")}…",
                Glyphs.Get(severe ? Glyph.Error : Glyph.Warning, GlyphSize, dpi, severe ? StatusColors.Fail : StatusColors.Warn),
                a.ShowProblems);
            problems.AccessibleName = $"{TrayText.Problems(s.Problems.Count)}, show them";
            items.Add(problems);
        }
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
    public static void BuildFallback(ContextMenuStrip menu, string headline, string detail, bool offerSetUp, TrayMenuActions a)
    {
        Reset(menu);
        var dpi = menu.DeviceDpi;
        var items = menu.Items;
        var head = Header(headline, menu.Font);
        head.Image = Glyphs.Get(Glyph.Error, GlyphSize, dpi, StatusColors.Fail);
        items.Add(head);
        items.Add(new ToolStripMenuItem(detail) { Enabled = false });
        items.Add(new ToolStripSeparator());
        if (offerSetUp) items.Add(Item("&Set up clauderoo", Neutral(Glyph.Play, dpi), a.SetUp));
        items.Add(Item("&Retry", Neutral(Glyph.Refresh, dpi), a.Retry));
        items.Add(Item("Open &Manage profiles…", Neutral(Glyph.Settings, dpi), a.ShowProfiles));
        items.Add(new ToolStripSeparator());
        items.Add(Item("E&xit clauderoo (stops syncing)", Neutral(Glyph.Exit, dpi), a.Exit));
    }

    static ToolStripMenuItem ProfileItem(ProfileInfo p, int running, ContextMenuStrip menu, TrayMenuActions a, int dpi)
    {
        var name = p.Name;
        var label = TrayText.ProfileLabel(p);
        var item = new ToolStripMenuItem(label.Replace("&", "&&"))
        {
            Image = Neutral(p.Provider switch
            {
                Providers.Vertex => Glyph.Cloud,
                Providers.ApiKey => Glyph.Key,
                _ => Glyph.Person,
            }, dpi),
            AccessibleName = label + (p.IsDefault ? ", default" : "") + (running > 0 ? ", " + TrayText.Running(running) : ""),
            ToolTipText = p.IsDefault ? "Default profile: double-click the tray icon to start it." : null,
        };
        if (p.IsDefault)
        {
            item.Font = Bold(menu.Font);
            item.Tag = new DefaultMark();
        }
        // Clicking the profile itself starts it; the arrow opens the other actions. (Click on a cascading item
        // still fires; the submenu also opens on hover, so both stay reachable.)
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
        new(text) { Enabled = false, Font = Bold(font), AccessibleRole = AccessibleRole.StaticText };

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
        if (menu.Renderer is not StarRenderer) menu.Renderer = new StarRenderer();
    }

    static Font? _bold;

    // One bold font for the life of the app (menus never dispose fonts they're given).
    static Font Bold(Font baseFont)
    {
        if (_bold == null || _bold.Size != baseFont.Size || _bold.FontFamily.Name != baseFont.FontFamily.Name)
            _bold = new Font(baseFont, FontStyle.Bold);
        return _bold;
    }

    /// <summary>Draws the default profile's star in the check column, left of the provider glyph.</summary>
    sealed class StarRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            base.OnRenderItemImage(e);
            if (e.Item.Tag is not DefaultMark) return;
            var r = e.ImageRectangle;
            var dpi = e.Item.Owner?.DeviceDpi ?? 96;
            // The check column ends where the image column starts; a slightly smaller star fits it with a gap.
            var star = Glyphs.Get(Glyph.Star, StarSize, dpi, e.Item.Enabled ? StatusColors.Accent : SystemColors.GrayText);
            var gap = Math.Max(1, (int)Math.Round(2 * dpi / 96.0));
            var left = Math.Max(0, (int)Math.Round(3 * dpi / 96.0)); // clear of the selection border
            var x = left + Math.Max(0, (r.X - gap - left - star.Width) / 2);
            e.Graphics.DrawImage(star, new Rectangle(x, r.Y + (r.Height - star.Height) / 2, star.Width, star.Height));
        }
    }
}
