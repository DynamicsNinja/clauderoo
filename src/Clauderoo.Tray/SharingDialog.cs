using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>The words the Sharing window uses for a shared item's state.</summary>
internal static class SharingText
{
    public static string Kind(SharedKind k) => k switch
    {
        SharedKind.Folder => "Folder",
        SharedKind.File => "File",
        _ => "MCP servers",
    };

    public static string State(SharedItemStatus s) => s.State switch
    {
        ShareState.Owned => "Own copy",
        ShareState.Excluding when s.Item == SharedItems.Plugins =>
            s.Exclusions.Count == 1 ? "Shared, 1 plugin turned off" : $"Shared, {s.Exclusions.Count} plugins turned off",
        ShareState.Excluding => $"Shared, leaving out {s.Exclusions.Count}",
        _ => "Shared from home",
    };

    public static string Detail(SharedItemStatus s) => s.State switch
    {
        ShareState.Owned => "Not updated from home",
        ShareState.Excluding => string.Join(", ", s.Exclusions),
        _ => "",
    };

    public static string PickerHelp(string profile, string item) => item switch
    {
        SharedItems.Plugins =>
            $"Ticked plugins stay on for {profile}. Unticked ones are turned off in {profile}'s settings.json; " +
            "they stay installed for your other profiles.",
        SharedItems.McpServers => $"Ticked MCP servers are shared with {profile}. Unticked ones are left out of {profile}.",
        _ => $"Ticked entries of {item} are shared with {profile}. Unticked ones are left out of {profile}; " +
             "home keeps them.",
    };
}

// Per profile: how each shared item reaches it, with Own / Share again and the choice of what's left out.
// Thin over ProfileManager (SharingOf, Own, Share, SetExclusions); every write goes through the caller's write gate.
// Layout is driven by auto-sizing panels (no fixed pixel positions), so it fits at any display scaling.
sealed class SharingDialog : Form
{
    private const string RestartText = "Running sessions pick up changes when restarted.";

    private readonly ProfileManager _pm;
    private readonly string _profile;
    private readonly Func<Func<SyncReport>, Task<SyncReport>> _write;
    private readonly ListView _items = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, AccessibleName = "Shared items",
    };
    private readonly Button _own = Btn("&Own");
    private readonly Button _share = Btn("&Share again");
    private readonly Button _choose = Btn("&Choose what's shared…");
    private readonly Button _close = Btn("Close");
    private readonly GlyphLabel _result = new() { Margin = new Padding(0, 6, 0, 0) };
    private IReadOnlyList<SharedItemStatus> _status = [];
    private bool _busy;

    internal SharingDialog(ProfileManager pm, string profile, Func<Func<SyncReport>, Task<SyncReport>> write)
    {
        _pm = pm;
        _profile = profile;
        _write = write;
        Text = $"What {profile} shares";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        Padding = Scaled(12, 12, 12, 12);

        var intro = new WrapLabel
        {
            Text = $"{profile} gets these from your home profile. Own gives {profile} its own copy that home no longer " +
                   "updates; Share again goes back to home's (the profile's copy is kept as a backup). Choose what's shared " +
                   $"leaves single skills, agents, MCP servers or plugins out of {profile}.",
            Margin = Scaled(0, 0, 0, 8),
        };
        _items.Columns.Add("Item", LogicalToDeviceUnits(130));
        _items.Columns.Add("Kind", LogicalToDeviceUnits(90));
        _items.Columns.Add("How", LogicalToDeviceUnits(170));
        _items.Columns.Add("Left out", LogicalToDeviceUnits(200));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = Scaled(0, 8, 0, 0),
            Dock = DockStyle.Fill,
        };
        _close.DialogResult = DialogResult.Cancel;
        _close.Margin = Padding.Empty;
        buttons.Controls.AddRange([_own, _share, _choose]);
        var bottom = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(buttons, 0, 0);
        _close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        bottom.Controls.Add(_close, 1, 0);

        var root = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(_items, 0, 1);
        root.Controls.Add(_result, 0, 2);
        root.Controls.Add(bottom, 0, 3);
        Controls.Add(root);

        ClientSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(420));
        MinimumSize = new Size(LogicalToDeviceUnits(480), LogicalToDeviceUnits(320));
        CancelButton = _close;
        void Wrap()
        {
            var w = Math.Max(1, root.ClientSize.Width);
            intro.WrapTo(w);
            _result.WrapTo(w);
            root.PerformLayout();
        }
        root.SizeChanged += (_, _) => Wrap();
        _result.TextChanged += (_, _) => Wrap();

        _items.SelectedIndexChanged += (_, _) => OnSelection();
        _items.DoubleClick += async (_, _) => { if (_choose.Enabled) await ChooseAsync(); };
        _own.Click += async (_, _) => await OwnAsync();
        _share.Click += async (_, _) => await ShareAsync();
        _choose.Click += async (_, _) => await ChooseAsync();
        Load += (_, _) => Wrap();

        Reload(null);
    }

    /// <summary>Shows the window for <paramref name="profile"/> above <paramref name="owner"/>.</summary>
    public static void ShowFor(IWin32Window owner, ProfileManager pm, string profile, Func<Func<SyncReport>, Task<SyncReport>> write)
    {
        using var dlg = new SharingDialog(pm, profile, write);
        dlg.ShowDialog(owner);
    }

    private Padding Scaled(int l, int t, int r, int b) =>
        new(LogicalToDeviceUnits(l), LogicalToDeviceUnits(t), LogicalToDeviceUnits(r), LogicalToDeviceUnits(b));

    private static Button Btn(string text) =>
        new() { Text = text, AutoSize = true, MinimumSize = new Size(80, 0), Margin = new Padding(0, 0, 6, 0) };

    private string? _selected; // the item the buttons act on (kept apart from the ListView, which needs a handle)

    private SharedItemStatus? Selected => _status.FirstOrDefault(s => s.Item == _selected);

    private void Reload(string? select)
    {
        _status = _pm.SharingOf(_profile);
        _selected = _status.Any(s => s.Item == select) ? select : _status.FirstOrDefault()?.Item;
        _loading = true;
        _items.BeginUpdate();
        _items.Items.Clear();
        foreach (var s in _status)
        {
            var row = new ListViewItem([s.Item, SharingText.Kind(s.Kind), SharingText.State(s), SharingText.Detail(s)]) { Tag = s };
            _items.Items.Add(row);
            if (s.Item == _selected) row.Selected = row.Focused = true;
        }
        _items.EndUpdate();
        _loading = false;
        UpdateButtons();
    }

    private bool _loading;

    private void OnSelection()
    {
        if (_loading) return;
        if (_items.SelectedItems.Count == 1 && _items.SelectedItems[0].Tag is SharedItemStatus s) _selected = s.Item;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var s = Selected;
        _own.Enabled = !_busy && s != null && s.State != ShareState.Owned;
        _share.Enabled = !_busy && s?.State == ShareState.Owned;
        _choose.Enabled = !_busy && s is { CanExclude: true } && s.State != ShareState.Owned;
    }

    private async Task OwnAsync()
    {
        if (Selected is not { } s) return;
        await RunAsync(s.Item, () => { _pm.Own(_profile, s.Item); return new SyncReport(); },
            $"{_profile} now has its own {s.Item}.");
    }

    private async Task ShareAsync()
    {
        if (Selected is not { } s) return;
        if (MessageBox.Show(this,
                $"{_profile} will use home's {s.Item} again. Its own copy is kept as a backup next to it.",
                "Share again", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunAsync(s.Item, () => _pm.Share(_profile, s.Item), $"{_profile} now shares {s.Item} from home.");
    }

    private async Task ChooseAsync()
    {
        if (Selected is not { CanExclude: true } s) return;
        IReadOnlyList<string> entries;
        try { entries = _pm.HomeEntries(s.Item); }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException)
        {
            ShowFailure(e.Message);
            return;
        }
        var excluded = ExclusionPicker.Ask(this, SharingText.PickerHelp(_profile, s.Item), s.Item, entries, s.Exclusions);
        if (excluded == null) return;
        await RunAsync(s.Item, () => _pm.SetExclusions(_profile, s.Item, excluded), $"Saved what {_profile} gets from {s.Item}.");
    }

    private async Task RunAsync(string item, Func<SyncReport> write, string done)
    {
        _busy = true;
        UpdateButtons();
        UseWaitCursor = true;
        try
        {
            var report = await _write(write);
            if (IsDisposed) return;
            var titles = report.Problems.Select(p => ProblemTranslator.Translate(p).Title).Distinct().ToList();
            var text = string.Join(Environment.NewLine, new[] { done }.Concat(report.Notices).Concat(titles).Append(RestartText));
            _result.Show(titles.Count > 0 ? Glyph.Warning : Glyph.Ok, titles.Count > 0 ? StatusColors.Fail : SystemColors.ControlText, text);
        }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed) ShowFailure(e.Message);
        }
        finally
        {
            if (!IsDisposed)
            {
                _busy = false;
                UseWaitCursor = false;
                try { Reload(item); }
                catch (Exception e) when (e is ClauderooException or IOException) { ShowFailure(e.Message); }
            }
        }
    }

    private void ShowFailure(string message) => _result.Show(Glyph.Error, StatusColors.Fail, message);

    // Test accessors.
    internal IReadOnlyList<string> RowsForTest =>
        _items.Items.Cast<ListViewItem>().Select(i => string.Join(" | ", i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x => x.Text))).ToList();
    internal void SelectForTest(string item) => Reload(item);
    internal Task OwnForTest() => OwnAsync();
    internal Task RunForTest(string item, Func<SyncReport> write, string done) => RunAsync(item, write, done);
    internal (bool Own, bool Share, bool Choose) ButtonsForTest => (_own.Enabled, _share.Enabled, _choose.Enabled);
    internal string ResultForTest => _result.Text;
}

/// <summary>Ticks for what's shared, unticked for what's left out. Returns the left-out entries, or null on Cancel.</summary>
sealed class ExclusionPicker : Form
{
    private readonly CheckedListBox _list = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly List<string> _names = [];

    private ExclusionPicker(string help, string item, IReadOnlyList<string> inHome, IReadOnlyList<string> excluded)
    {
        Text = $"Choose what's shared: {item}";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        Padding = new Padding(LogicalToDeviceUnits(12));
        _list.AccessibleName = item;

        var cmp = SharedItems.EntryComparer(item);
        foreach (var n in inHome)
        {
            _names.Add(n);
            _list.Items.Add(n, !excluded.Contains(n, cmp));
        }
        // Left-out entries home doesn't have (any more): shown so they can be shared again.
        foreach (var n in excluded.Where(x => !inHome.Contains(x, cmp)))
        {
            _names.Add(n);
            _list.Items.Add($"{n}  (not in home)", false);
        }

        var helpLabel = new WrapLabel { Text = help, Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(8)) };
        var empty = new Label
        {
            Text = $"Home has nothing in {item} yet.", AutoSize = true, ForeColor = SystemColors.GrayText,
            Visible = _names.Count == 0,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(80), 0) };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true,
            MinimumSize = new Size(LogicalToDeviceUnits(80), 0), Margin = new Padding(LogicalToDeviceUnits(6), 0, 0, 0),
        };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false,
            Margin = new Padding(0, LogicalToDeviceUnits(8), 0, 0),
        };
        buttons.Controls.AddRange([cancel, ok]);

        var root = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(helpLabel, 0, 0);
        root.Controls.Add(empty, 0, 1);
        root.Controls.Add(_list, 0, 2);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);
        root.SizeChanged += (_, _) => { helpLabel.WrapTo(Math.Max(1, root.ClientSize.Width)); root.PerformLayout(); };

        ClientSize = new Size(LogicalToDeviceUnits(420), LogicalToDeviceUnits(380));
        MinimumSize = new Size(LogicalToDeviceUnits(320), LogicalToDeviceUnits(260));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>The entries left unticked.</summary>
    internal List<string> Excluded =>
        _names.Where((_, i) => !_list.GetItemChecked(i)).ToList();

    internal CheckedListBox ListForTest => _list;

    internal static ExclusionPicker CreateForTest(string item, IReadOnlyList<string> inHome, IReadOnlyList<string> excluded) =>
        new("", item, inHome, excluded);

    public static List<string>? Ask(IWin32Window owner, string help, string item, IReadOnlyList<string> inHome,
        IReadOnlyList<string> excluded)
    {
        using var dlg = new ExclusionPicker(help, item, inHome, excluded);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Excluded : null;
    }
}
