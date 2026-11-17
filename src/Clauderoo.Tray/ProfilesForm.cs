using Clauderoo.Core;
using Microsoft.Win32;

namespace Clauderoo.Tray;

// The Profiles window: pick each profile's provider and env vars. Thin over ProfileManager.
// Layout is driven by auto-sizing panels (no fixed pixel positions), so it fits at any display scaling.
// Provider and sign-in words come from TrayText, so the window and the tray say the same thing.
sealed class ProfilesForm : Form
{
    private const string BaseTitle = "clauderoo — Profiles";
    private const string RestartText = "Running sessions pick up changes when restarted.";
    private const string CheckText = "&Check setup";
    private const string SaveAndCheckText = "Save and &check";

    private const string WhatsSharedText =
        "Your home profile is your normal Claude setup. clauderoo shares these from it with every other profile and " +
        "keeps them in step while it runs:\n\n" +
        "•  settings (settings.json), CLAUDE.md and keybindings\n" +
        "•  plugins, skills, hooks, agents, commands and output styles\n" +
        "•  MCP servers\n\n" +
        "Each profile keeps its own sign-in, provider and environment variables. Change a shared thing in your home " +
        "profile and every profile gets it.";

    /// <summary>A profile's last check, kept for the life of the tray process (closing the window keeps it).</summary>
    private sealed record CheckRecord(DateTime At, string Provider, CheckStatus? Status, string Message, IReadOnlyList<CheckItem>? Items);
    private static readonly Dictionary<string, CheckRecord> LastChecks = new(StringComparer.OrdinalIgnoreCase);

    private readonly ProfileManager _pm;
    private readonly Action<string> _startSession;
    private readonly Action? _showProblems;
    private readonly WriteGate? _gate;
    private readonly Action? _autoSwitchChanged;
    private Action? _resultLink;   // what the link at the end of the result line does (Undo, Open Problems…)
    private readonly Gcloud _gcloud = new();
    private readonly ToolTip _tips = new();

    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill, IntegralHeight = false, Margin = new Padding(0, 0, 0, 6), DrawMode = DrawMode.OwnerDrawFixed,
    };
    private readonly FlowLayoutPanel _leftButtons;
    private readonly Button _new = Btn("&New…");
    private readonly Button _rename = Btn("Rena&me…");
    private readonly Button _remove = Btn("Remo&ve");
    private readonly Button _autoSwitch = Btn("Auto-s&witch…");

    // Home: an explanation instead of disabled controls.
    private readonly TableLayoutPanel _homePanel;
    private readonly GlyphLabel _homeHeading = new() { GlyphSize = 16, Margin = new Padding(0, 0, 0, 4) };
    private readonly WrapLabel _homeText = new() { Margin = new Padding(0, 0, 0, 2) };
    private readonly LinkLabel _whatsShared = new() { Text = "What's shared?", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    private readonly GlyphLabel _homeAccount = new() { Margin = new Padding(0, 0, 0, 8) };

    private readonly TableLayoutPanel _providerRow;
    private readonly RadioButton _rbLogin = Radio(TrayText.AccessKey(TrayText.ClaudeLogin, "login"));
    private readonly RadioButton _rbVertex = Radio(TrayText.AccessKey(TrayText.Vertex, "x AI"));
    private readonly RadioButton _rbApiKey = Radio(TrayText.AccessKey(TrayText.ApiKey, "API"));

    private readonly TableLayoutPanel _vertexPanel = FieldGrid();
    private readonly TextBox _project = Field("e.g. my-gcp-project");
    private readonly TextBox _region = Field("e.g. eu or europe-west1");
    private readonly TextBox _sonnet = Field("e.g. claude-sonnet-5-5");
    private readonly TextBox _opus = Field("empty = Claude's default");
    private readonly TextBox _haiku = Field("empty = Claude's default");
    private readonly WrapLabel _vertexHelp = new()
    {
        Text = "Your Google Cloud admin can tell you the project and region. Leave a model empty to use Claude's default.",
        ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6),
    };

    private readonly TableLayoutPanel _apiKeyPanel = FieldGrid();
    private readonly TextBox _apiKey = Field("");

    private readonly TableLayoutPanel _loginPanel = FieldGrid();
    private readonly Label _account = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 0, 3), UseMnemonic = false };

    // Usage: a bar per window, the plan and "as of", and Refresh (Claude login); otherwise why it isn't tracked.
    private readonly UsageView? _usageView;
    private readonly GroupBox _usageGroup = new()
    {
        Text = "Usage", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(8, 4, 8, 4),
    };
    private readonly TableLayoutPanel _usageRows = Table(3);
    // Right margin: the group's border and padding, so the wrapped lines stay inside it.
    private readonly WrapLabel _usageInfo = new() { ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 20, 2) };
    private readonly GlyphLabel _usageStatus = new() { Margin = new Padding(0, 2, 20, 2) };
    private readonly Button _usageRefresh = Btn("Re&fresh");

    // Required fields that are still empty (provider part).
    private readonly GlyphLabel _fieldError = new() { Margin = new Padding(0, 0, 0, 6) };

    private readonly Button _check = Btn(CheckText);
    private readonly Button _copy = Btn("Cop&y results");
    private readonly Button _signIn = Btn("Sign &in to Google…");
    private readonly GlyphLabel _checkStatus = new() { Margin = new Padding(0, 0, 0, 6) };
    private readonly CheckListView _checkList = new()
    {
        Margin = new Padding(0, 0, 0, 6), Visible = false,
    };
    private readonly FlowLayoutPanel _checkRow;
    private readonly TableLayoutPanel _root;
    private readonly Panel _right;
    private readonly TableLayoutPanel _top;
    private readonly FlowLayoutPanel _envButtons;

    private readonly Label _envLabel = new() { Text = "Environment variables", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly WrapLabel _envHint = new()
    {
        Text = "Extra environment variables for this profile (e.g. HTTPS_PROXY). Provider settings above are added automatically.",
        ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 3),
    };
    private readonly DataGridView _env = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
        AllowUserToResizeRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        BackgroundColor = SystemColors.Window, Margin = new Padding(0, 0, 0, 3),
    };
    private readonly Button _envDelete = Btn("Delete varia&ble");
    private readonly GlyphLabel _envError = new() { Margin = new Padding(0, 3, 0, 0) };

    private readonly WrapLinkLabel _result = new()
    {
        Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 3),
    };
    private readonly Label _restartNote = new()
    {
        Text = RestartText, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 12, 0),
    };
    private readonly Label _unsaved = new()
    {
        Text = "Unsaved changes", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText,
        Margin = new Padding(6, 0, 6, 0), Visible = false,
    };
    private readonly Button _setDefault = Btn("Set as &default");
    private readonly Button _start = Btn("S&tart session");
    private readonly Button _save = Btn("&Save");

    private readonly System.Windows.Forms.Timer _runningTimer = new() { Interval = 3000 };
    private Dictionary<string, int> _running = new(StringComparer.Ordinal);
    private volatile bool _runningBusy; // a background Running() read is in flight
    private Icon? _icon;
    private Font? _boldFont;
    private bool _highContrast = SystemInformation.HighContrast;

    private ProfileInfo? _currentInfo;
    private string? _current;      // profile shown in the editor
    private bool _currentIsHome;
    private bool _currentIsDefault;
    private bool _hasSavedKey;     // the current profile has an API key stored
    private string _loadedState = "";
    private bool _loading;         // suppresses change handlers while filling controls
    private bool _switching;       // suppresses re-entrant selection changes
    private string? _undoName;     // removed profile the status line offers to restore
    private bool _showRestart;     // the last action saved the current profile (the restart note may apply)
    private string? _checkingFor;  // profile a check is running for (one at a time)
    private int _checkSeq;         // bumped when a check is cancelled: its result is dropped
    private CancellationTokenSource? _checkCts; // the running setup check, if any
    private Task? _checkTask;      // its background task (completes once its claude process is killed)
    private CheckProgressModel? _checkProgress; // its live checklist (rows fill in as each check finishes)
    private int _baseMinHeight;    // minimum window height for the content measured at startup

    /// <param name="showProblems">Opens the tray's Problems window; without it a result lists problem titles only.</param>
    /// <param name="usage">The tray's usage readings and Refresh; without it the Usage group says nothing is known yet.</param>
    /// <param name="gate">The tray's write gate: the window's writes take turns with the tray's (syncs, auto-switch).</param>
    /// <param name="autoSwitchChanged">Called after the Auto-switch settings were saved (the tray decides again).</param>
    public ProfilesForm(ProfileManager pm, Action<string> startSession, Action? showProblems = null, UsageView? usage = null,
        WriteGate? gate = null, Action? autoSwitchChanged = null)
    {
        _pm = pm;
        _startSession = startSession;
        _showProblems = showProblems;
        _usageView = usage;
        _gate = gate;
        _autoSwitchChanged = autoSwitchChanged;

        Text = BaseTitle;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(12);
        ShowInTaskbar = true;
        try { Icon = _icon = AppIcons.Load(AppIconState.Normal, _highContrast); }
        catch (Exception) { /* keep the default icon */ }

        // Left: profile list + New / Rename / Remove.
        _list.MinimumSize = new Size(230, 200);
        _list.AccessibleName = "Profiles";
        var leftButtons = _leftButtons = Flow(FlowDirection.LeftToRight);
        leftButtons.Controls.AddRange([_new, _rename, _remove]);
        // Auto-switch applies to every profile, so it sits under the list rather than in the editor.
        _autoSwitch.Margin = new Padding(0, 6, 6, 0);
        _tips.SetToolTip(_autoSwitch, "Switch the default profile when it nears a usage limit.");
        var left = Table(1);
        left.Dock = DockStyle.Fill;
        left.Margin = new Padding(0, 0, 12, 0);
        left.RowCount = 3;
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(_list, 0, 0);
        left.Controls.Add(leftButtons, 0, 1);
        left.Controls.Add(_autoSwitch, 0, 2);

        // Right: the home explanation, or the provider choice.
        _homePanel = Table(1);
        _homePanel.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _homePanel.Margin = new Padding(0, 0, 0, 6);
        _homePanel.Controls.Add(_homeHeading, 0, 0);
        _homePanel.Controls.Add(_homeText, 0, 1);
        _homePanel.Controls.Add(_whatsShared, 0, 2);
        _homePanel.Controls.Add(_homeAccount, 0, 3);

        var radios = Flow(FlowDirection.TopDown);
        radios.Controls.AddRange([_rbLogin, _rbVertex, _rbApiKey]);
        _providerRow = Table(2);
        // Each label sits right before its control in the tab order, so its mnemonic moves focus there.
        _providerRow.Controls.Add(new Label { Text = "Pr&ovider:", AutoSize = true, Margin = new Padding(0, 4, 12, 0) }, 0, 0);
        _providerRow.Controls.Add(radios, 1, 0);
        _providerRow.Margin = new Padding(0, 0, 0, 6);

        AddField(_vertexPanel, "&Project:", _project);
        AddField(_vertexPanel, "&Region:", _region);
        AddField(_vertexPanel, "Sonn&et model:", _sonnet);
        AddField(_vertexPanel, "Op&us model:", _opus);
        AddField(_vertexPanel, "&Haiku model:", _haiku);
        _vertexPanel.Margin = new Padding(0, 0, 0, 2);
        foreach (var tb in new[] { _project, _region, _sonnet, _opus, _haiku, _apiKey })
            tb.MinimumSize = new Size(240, 0);
        _apiKey.UseSystemPasswordChar = true;
        AddField(_apiKeyPanel, "API &key:", _apiKey);
        AddField(_loginPanel, "Account:", _account);

        // Usage group: rows of name | bar | value, then the plan / "as of" line, a status line and Refresh.
        _usageRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _usageRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _usageRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _usageRows.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _usageRefresh.Margin = new Padding(0, 2, 0, 2);
        var usageBody = Table(1);
        usageBody.Dock = DockStyle.Fill;
        usageBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Control[] usageParts = [_usageRows, _usageInfo, _usageStatus, _usageRefresh];
        usageBody.RowCount = usageParts.Length;
        for (var i = 0; i < usageParts.Length; i++)
        {
            usageBody.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            usageBody.Controls.Add(usageParts[i], 0, i);
        }
        _usageGroup.Controls.Add(usageBody);

        _checkRow = Flow(FlowDirection.LeftToRight);
        _checkRow.Margin = new Padding(0, 0, 0, 4);
        _checkRow.Controls.AddRange([_check, _copy, _signIn]);

        var envButtons = _envButtons = Flow(FlowDirection.LeftToRight);
        envButtons.Controls.Add(_envDelete);
        _env.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 40 });
        _env.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value", FillWeight = 60 });
        _env.MinimumSize = new Size(380, 140);

        // Right column: provider part on top (auto-sized), env buttons at the bottom, the grid fills the rest.
        // Explicit cells: auto-placement skips invisible controls, which would shift rows when panels swap.
        var top = _top = Table(1);
        top.Dock = DockStyle.Top;
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Control[] topRows =
        [
            _homePanel, _providerRow, _vertexPanel, _vertexHelp, _apiKeyPanel, _loginPanel, _usageGroup, _fieldError,
            _checkRow, _checkStatus, _checkList, _envLabel, _envHint,
        ];
        top.RowCount = topRows.Length;
        for (var i = 0; i < topRows.Length; i++)
        {
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            top.Controls.Add(topRows[i], 0, i);
        }
        // Below it: the grid takes the rest, then its error line and buttons.
        envButtons.Padding = new Padding(0, 3, 0, 0);
        _env.Dock = DockStyle.Fill;
        var envArea = Table(1);
        envArea.AutoSize = false;
        envArea.Dock = DockStyle.Fill;
        envArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        envArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        envArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        envArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        envArea.Controls.Add(_env, 0, 0);
        envArea.Controls.Add(_envError, 0, 1);
        envArea.Controls.Add(envButtons, 0, 2);
        var right = _right = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        right.Controls.Add(envArea); // docked last, so it fills what the top leaves
        right.Controls.Add(top);

        // Bottom: status line with optional Undo (wraps, spans the window), then the note and the actions.
        _result.Visible = false;
        _result.Margin = new Padding(0, 8, 0, 0);
        var bottomButtons = Flow(FlowDirection.LeftToRight);
        bottomButtons.Anchor = AnchorStyles.Right;
        bottomButtons.Controls.AddRange([_setDefault, _start, _unsaved, _save]);
        var bottom = Table(2);
        bottom.Dock = DockStyle.Fill;
        bottom.Margin = new Padding(0, 8, 0, 0);
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.Controls.Add(_restartNote, 0, 0);
        bottom.Controls.Add(bottomButtons, 1, 0);

        var root = _root = Table(2);
        root.Dock = DockStyle.Fill;
        root.AutoSize = false;
        root.RowCount = 3;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        root.Controls.Add(_result, 0, 1);
        root.SetColumnSpan(_result, 2);
        root.Controls.Add(bottom, 0, 2);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);

        // All Padding/Margin/MinimumSize literals above are logical (96 DPI) units: scale them once.
        ScaleLiterals(this);
        ApplyDpiResources();
        AcceptButton = _save;

        // Size the window from its content, measured with the largest provider panel and every optional line showing.
        _homePanel.Visible = _apiKeyPanel.Visible = _loginPanel.Visible = false;
        _unsaved.Visible = true;
        var rightWidth = Math.Max(_vertexPanel.GetPreferredSize(Size.Empty).Width, _env.MinimumSize.Width);
        foreach (var w in new[] { _vertexHelp, _envHint }) w.WrapTo(rightWidth - w.Margin.Horizontal);
        var topPref = top.GetPreferredSize(Size.Empty);
        right.Size = new Size(rightWidth,
            topPref.Height + envButtons.GetPreferredSize(Size.Empty).Height + envButtons.Padding.Vertical + _env.MinimumSize.Height);
        var pref = root.GetPreferredSize(Size.Empty);
        ClientSize = new Size(pref.Width + Padding.Horizontal, pref.Height + Padding.Vertical);
        MinimumSize = Size;
        _baseMinHeight = MinimumSize.Height;
        _unsaved.Visible = false;
        _restartNote.Visible = false;
        // The grid gives up height to long status lines before the window has to grow (EnsureFits).
        _env.MinimumSize = new Size(_env.MinimumSize.Width, LogicalToDeviceUnits(70));
        _checkList.MaxContentHeight = LogicalToDeviceUnits(190);

        // Status labels wrap to the width of their column.
        WrapTo(top, _homeHeading, _homeText, _homeAccount, _vertexHelp, _fieldError, _checkStatus, _checkList, _envHint,
            _usageInfo, _usageStatus);
        WrapTo(right, _envError);
        WrapTo(root, _result);
        // SizeChanged: a wrapped label takes its new height after TextChanged has been raised.
        foreach (var c in new Control[] { _checkStatus, _fieldError, _envError, _homeText, _checkList, _usageGroup })
            c.SizeChanged += (_, _) => EnsureFits();
        _result.TextChanged += (_, _) =>
        {
            _result.Visible = _result.Text.Length > 0;
            EnsureFits();
        };
        _result.SizeChanged += (_, _) => EnsureFits();
        _result.LinkClicked += (_, _) => { if (_resultLink is { } link) Guard(link); };
        _whatsShared.LinkClicked += (_, _) =>
            MessageBox.Show(this, WhatsSharedText, "What's shared?", MessageBoxButtons.OK, MessageBoxIcon.Information);

        _list.DrawItem += DrawProfile;
        _list.SelectedIndexChanged += (_, _) => OnListSelection();
        _new.Click += (_, _) => Guard(NewProfile);
        _rename.Click += (_, _) => Guard(RenameProfile);
        _remove.Click += (_, _) => Guard(RemoveProfile);
        _autoSwitch.Click += async (_, _) => await EditAutoSwitchAsync();
        foreach (var rb in new[] { _rbLogin, _rbVertex, _rbApiKey })
            rb.CheckedChanged += (_, _) =>
            {
                if (!rb.Checked) return;
                UpdateProviderPanels();
                OnEdited();
            };
        foreach (var tb in new[] { _project, _region, _sonnet, _opus, _haiku, _apiKey })
            tb.TextChanged += (_, _) => OnEdited();
        // Commit each keystroke so validation and the Save state follow typing in the grid.
        _env.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_env.IsCurrentCellDirty) _env.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _env.CellValueChanged += (_, _) => OnEdited();
        _env.RowsAdded += (_, _) => OnEdited();
        _env.RowsRemoved += (_, _) => OnEdited();
        _check.Click += (_, _) => RunCheck();
        _copy.Click += (_, _) => CopyResults();
        _signIn.Click += (_, _) => Guard(_gcloud.OpenSignIn);
        _envDelete.Click += (_, _) => DeleteEnvRows();
        _setDefault.Click += async (_, _) => await SetDefaultAsync();
        _start.Click += (_, _) => Guard(StartSession);
        _save.Click += (_, _) => Save();
        _runningTimer.Tick += (_, _) => RefreshRunning();
        _usageRefresh.Click += async (_, _) => await RefreshUsageAsync();
        if (_usageView != null) _usageView.Changed += ShowUsage;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Reload(null);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshRunning();
        _runningTimer.Start();
    }

    // ---------- DPI / theme dependent resources ----------

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpiResources();
        _checkList.MaxContentHeight = LogicalToDeviceUnits(190);
    }

    // Button glyphs, the bold fonts and the list's row height, for the current DPI and theme.
    private void ApplyDpiResources()
    {
        var dpi = DeviceDpi;
        foreach (var (b, g) in new (Button, Glyph)[]
                 {
                     (_new, Glyph.Add), (_rename, Glyph.Rename), (_remove, Glyph.Delete), (_check, Glyph.Diagnostic),
                     (_signIn, Glyph.Person), (_envDelete, Glyph.Remove), (_setDefault, Glyph.StarOutline),
                     (_start, Glyph.Play), (_save, Glyph.Save), (_usageRefresh, Glyph.Refresh), (_autoSwitch, Glyph.Settings),
                 })
        {
            b.Image = Glyphs.Get(g, 16, dpi, SystemColors.ControlText);
            b.ImageAlign = ContentAlignment.MiddleLeft;
            b.TextImageRelation = TextImageRelation.ImageBeforeText;
        }

        var old = _boldFont;
        _boldFont = new Font(Font, FontStyle.Bold);
        _save.Font = _boldFont; // the primary action (also the AcceptButton)
        _homeHeading.Font = _boldFont;
        old?.Dispose();

        var line = TextRenderer.MeasureText("Ag", _boldFont).Height;
        _list.ItemHeight = Math.Min(255, 2 * line + LogicalToDeviceUnits(12));
        _list.Invalidate();
    }

    // High Contrast switched on or off while the window is open: new icon, re-inked glyphs and status colours.
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || SystemInformation.HighContrast == _highContrast) return;
                _highContrast = SystemInformation.HighContrast;
                var oldIcon = _icon;
                try { Icon = _icon = AppIcons.Load(AppIconState.Normal, _highContrast); }
                catch (Exception) { _icon = oldIcon; oldIcon = null; }
                oldIcon?.Dispose();
                ApplyDpiResources();
                if (_currentIsHome && _currentInfo != null) ShowHome(_currentInfo);
                ShowCheckRecord();
                OnEdited();
                _checkList.Invalidate();
                _list.Invalidate();
            });
        }
        catch (InvalidOperationException) { /* handle went away meanwhile */ }
    }

    // ---------- profile list ----------

    /// <summary>Reloads the profile list, selecting <paramref name="select"/> (or the current/first profile).</summary>
    public void Reload(string? select)
    {
        IReadOnlyList<ProfileInfo> profiles;
        try { profiles = _pm.List(); }
        catch (Exception e) { ShowError(e.Message); return; }

        RefreshRunning();
        select ??= _current;
        _switching = true;
        try
        {
            _list.Items.Clear();
            foreach (var p in profiles) _list.Items.Add(new ListItem(p));
            var idx = profiles.ToList().FindIndex(p => p.Name == select);
            _list.SelectedIndex = idx >= 0 ? idx : (profiles.Count > 0 ? 0 : -1);
        }
        finally { _switching = false; }
        LoadProfile((_list.SelectedItem as ListItem)?.Info);
    }

    private void OnListSelection()
    {
        if (_switching) return;
        var target = (_list.SelectedItem as ListItem)?.Info;
        if (target?.Name == _current) return;
        if (_undoName == null) ClearResult(); // a save made by ConfirmLeave reports below; Undo stays offered
        if (!ConfirmLeave())
        {
            SelectInList(_current);
            return;
        }
        // Saving may have reloaded the list; re-select the target by name.
        SelectInList(target?.Name);
        LoadProfile((_list.SelectedItem as ListItem)?.Info);
    }

    private void SelectInList(string? name)
    {
        _switching = true;
        try
        {
            for (var i = 0; i < _list.Items.Count; i++)
                if (((ListItem)_list.Items[i]).Info.Name == name) { _list.SelectedIndex = i; return; }
        }
        finally { _switching = false; }
    }

    private sealed record ListItem(ProfileInfo Info)
    {
        // The accessible name of the row (the visible row is owner-drawn).
        public override string ToString() =>
            Info.Name + (Info.IsDefault ? ", default" : "") + (Info.IsHome ? ", home" : "") + " — " + TrayText.Describe(Info);
    }

    private static Glyph ProviderGlyph(string provider) => provider switch
    {
        Providers.Vertex => Glyph.Cloud,
        Providers.ApiKey => Glyph.Key,
        _ => Glyph.Person,
    };

    private const TextFormatFlags RowMeasure = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
    private const TextFormatFlags RowText =
        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

    // The profile's last check, if it was made for the provider it uses now.
    private static CheckRecord? RecordFor(string name, string provider) =>
        LastChecks.TryGetValue(name, out var r) && r.Provider == provider ? r : null;

    private static string When(DateTime at) => at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("d MMM HH:mm");

    // Two lines per profile: bold name + ★ Default / ⌂ Home badges; provider glyph + details, last check, "● 1 running".
    private void DrawProfile(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var info = ((ListItem)_list.Items[e.Index]).Info;
        var g = e.Graphics;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
        var muted = selected ? SystemColors.HighlightText : SystemColors.GrayText;
        using (var b = new SolidBrush(selected ? SystemColors.Highlight : SystemColors.Window)) g.FillRectangle(b, e.Bounds);

        var dpi = _list.DeviceDpi;
        int D(int v) => _list.LogicalToDeviceUnits(v);
        var bold = _boldFont ?? _list.Font;
        var r = Rectangle.FromLTRB(e.Bounds.Left + D(6), e.Bounds.Top + D(5), e.Bounds.Right - D(6), e.Bounds.Bottom - D(5));
        var half = r.Height / 2;
        var line1 = new Rectangle(r.Left, r.Top, r.Width, half);
        var line2 = new Rectangle(r.Left, r.Top + half, r.Width, r.Height - half);
        var gap = D(4);

        // Draws glyph + text right-aligned ending at x; returns the new right edge.
        int Badge(int x, Rectangle line, Glyph glyph, Color ink, string text, Color textColor)
        {
            var tw = text.Length == 0 ? 0 : TextRenderer.MeasureText(g, text, _list.Font, Size.Empty, RowMeasure).Width + 1;
            var img = Glyphs.Get(glyph, 12, dpi, ink);
            var left = x - tw - (tw > 0 ? gap : 0) - img.Width;
            g.DrawImage(img, new Rectangle(left, line.Top + (line.Height - img.Height) / 2, img.Width, img.Height));
            if (tw > 0)
                TextRenderer.DrawText(g, text, _list.Font, new Rectangle(left + img.Width + gap, line.Top, tw, line.Height), textColor, RowText);
            return left - D(10);
        }

        // Line 1: badges from the right, then the name in what is left.
        var right1 = line1.Right;
        if (info.IsHome) right1 = Badge(right1, line1, Glyph.Home, fore, "Home", fore);
        if (info.IsDefault) right1 = Badge(right1, line1, Glyph.Star, selected ? fore : StatusColors.Accent, "Default", fore);
        TextRenderer.DrawText(g, info.Name, bold, new Rectangle(line1.Left, line1.Top, Math.Max(0, right1 - line1.Left), line1.Height), fore, RowText);

        // Line 2 from the right: running count, last check; then provider glyph + details.
        var right2 = line2.Right;
        if (_running.TryGetValue(info.Name, out var n) && n > 0)
        {
            var text = $"● {n} running";
            var tw = TextRenderer.MeasureText(g, text, _list.Font, Size.Empty, RowMeasure).Width + 1;
            TextRenderer.DrawText(g, text, _list.Font, new Rectangle(right2 - tw, line2.Top, tw, line2.Height),
                selected ? fore : StatusColors.Ok, RowText);
            right2 -= tw + D(10);
        }
        if (!info.IsHome && RecordFor(info.Name, info.Provider) is { } rec)
        {
            var (glyph, color) = rec.Status is { } s ? CheckListView.Look(s) : (Glyph.Info, StatusColors.Muted);
            right2 = Badge(right2, line2, glyph, selected ? fore : color,
                rec.Status == CheckStatus.Ok ? When(rec.At) : "", muted);
        }
        var icon = Glyphs.Get(ProviderGlyph(info.Provider), 12, dpi, muted);
        g.DrawImage(icon, new Rectangle(line2.Left, line2.Top + (line2.Height - icon.Height) / 2, icon.Width, icon.Height));
        var tx = line2.Left + icon.Width + gap;
        TextRenderer.DrawText(g, TrayText.Describe(info), _list.Font, new Rectangle(tx, line2.Top, Math.Max(0, right2 - tx), line2.Height), muted, RowText);

        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    // Re-reads, off the UI thread, which profiles have clauderoo-started sessions running (timer, reloads). The sessions
    // file is read under a machine-wide lock that can take seconds; when it can't tell, the last shown counts stay.
    private void RefreshRunning()
    {
        if (_runningBusy || IsDisposed || !IsHandleCreated) return;
        _runningBusy = true;
        var sessions = _pm.Sessions;
        Task.Run(() =>
        {
            IReadOnlyList<RunningSession>? now;
            try { now = sessions.TryRunning(); }
            catch (Exception) { now = null; }
            try
            {
                BeginInvoke(() =>
                {
                    _runningBusy = false;
                    if (now != null && !IsDisposed) ApplyRunning(now);
                });
            }
            catch (InvalidOperationException) { _runningBusy = false; /* window closed meanwhile */ }
        });
    }

    private void ApplyRunning(IReadOnlyList<RunningSession> sessions)
    {
        var now = sessions.GroupBy(s => s.Profile).ToDictionary(grp => grp.Key, grp => grp.Count(), StringComparer.Ordinal);
        if (now.Count == _running.Count && now.All(kv => _running.TryGetValue(kv.Key, out var v) && v == kv.Value)) return;
        _running = now;
        _list.Invalidate();
        UpdateRestartNote();
    }

    // Shown after a successful Save of a profile that has sessions running.
    private void UpdateRestartNote() =>
        _restartNote.Visible = _showRestart && _current != null && _running.TryGetValue(_current, out var n) && n > 0;

    // ---------- editor ----------

    private void LoadProfile(ProfileInfo? info)
    {
        _loading = true;
        try
        {
            if (info?.Name != _current) _showRestart = false;
            _currentInfo = info;
            _current = info?.Name;
            _currentIsHome = info?.IsHome ?? false;
            _currentIsDefault = info?.IsDefault ?? false;
            _project.Text = _region.Text = _sonnet.Text = _opus.Text = _haiku.Text = _apiKey.Text = "";
            _apiKey.PlaceholderText = "Paste the key (sk-ant-…)";
            _env.Rows.Clear();
            _hasSavedKey = false;

            if (info == null)
            {
                _rbLogin.Checked = true;
                _account.Text = "";
            }
            else
            {
                _account.Text = info.Email ?? "Not signed in — start a session to sign in.";
                if (info.IsHome) ShowHome(info);
                else
                {
                    var entry = _pm.Get(info.Name);
                    var v = entry.Vertex;
                    if (v != null)
                    {
                        _project.Text = v.Project;
                        _region.Text = v.Region;
                        _sonnet.Text = v.SonnetModel ?? "";
                        _opus.Text = v.OpusModel ?? "";
                        _haiku.Text = v.HaikuModel ?? "";
                    }
                    _hasSavedKey = _pm.HasApiKey(info.Name);
                    if (_hasSavedKey) _apiKey.PlaceholderText = "•••• saved (type to replace)";
                    foreach (var kv in _pm.GetEnv(info.Name).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                        _env.Rows.Add(kv.Key, kv.Value);
                }
                var provider = info.IsHome ? Providers.Login : info.Provider;
                (provider switch
                {
                    Providers.Vertex => _rbVertex,
                    Providers.ApiKey => _rbApiKey,
                    _ => _rbLogin,
                }).Checked = true;
            }

            _start.Enabled = info != null;
            UpdateRestartNote();
            ShowUsage();
            UpdateProviderPanels();
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
        finally
        {
            _loading = false;
        }
        _loadedState = State();
        ShowCheckRecord();
        OnEdited();
    }

    private void ShowHome(ProfileInfo info)
    {
        var where = string.Equals(info.Dir.TrimEnd('\\'), _pm.Paths.DefaultHomeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? "~/.claude"
            : info.Dir;
        _homeHeading.Show(Glyph.Home, SystemColors.ControlText, $"{info.Name} is your home profile");
        _homeText.Text = $"This is your own Claude setup ({where}). clauderoo shares its settings, plugins, skills, hooks, " +
                         "agents, commands and MCP servers with your other profiles; it always uses your Claude login.";
        _homeAccount.Show(Glyph.Person, info.Email != null ? SystemColors.ControlText : SystemColors.GrayText,
            info.Email != null ? $"Signed in as {info.Email}" : "Not signed in — start a session to sign in.");
    }

    // ---------- usage ----------

    /// <summary>
    /// The Usage group for the profile shown: a bar per window with its value and reset time, the plan and "as of",
    /// and a status line when the last check didn't work. Other providers: why usage isn't tracked. Follows the
    /// saved provider, not an unsaved radio choice.
    /// </summary>
    private void ShowUsage()
    {
        if (IsDisposed) return;
        var info = _currentInfo;
        _usageGroup.Visible = info != null;
        if (info == null) return;

        _usageGroup.SuspendLayout();
        try
        {
            var old = _usageRows.Controls.Cast<Control>().ToList();
            _usageRows.Controls.Clear();
            foreach (var c in old) c.Dispose();
            _usageRows.RowStyles.Clear();
            _usageRows.RowCount = 0;

            var provider = info.IsHome ? Providers.Login : info.Provider;
            var tracked = provider == Providers.Login;
            _usageRefresh.Visible = tracked && _usageView != null;
            if (!tracked)
            {
                _usageRows.Visible = false;
                _usageInfo.Text = TrayText.UsageNotTracked(provider);
                _usageStatus.Show(null, StatusColors.Muted, "");
                return;
            }

            var now = DateTimeOffset.Now;
            var reading = _usageView?.Readings.GetValueOrDefault(info.Name);
            var last = _usageView?.Results.GetValueOrDefault(info.Name);
            _usageRows.Visible = reading is { Windows.Count: > 0 };
            if (reading != null)
                foreach (var w in reading.Windows) AddUsageRow(w, now);

            _usageInfo.Text = reading != null
                ? (reading.Plan is { } plan ? $"Plan: {plan} · " : "") + "as of " + TrayText.UsageTime(reading.FetchedAt, now)
                : info.Email == null ? "Not signed in — start a session to sign in."
                : "Not checked yet.";
            if (TrayText.UsageStatus(last) is { } why)
                _usageStatus.Show(Glyph.Warning, StatusColors.Warn, why);
            else
                _usageStatus.Show(null, StatusColors.Muted, "");
        }
        finally
        {
            _usageGroup.ResumeLayout(true);
            EnsureFits();
        }
    }

    private void AddUsageRow(UsageWindow w, DateTimeOffset now)
    {
        var row = _usageRows.RowCount++;
        _usageRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var name = TrayText.UsageWindowName(w.Name);
        var value = TrayText.UsageValue(w, now);
        _usageRows.Controls.Add(new Label
        {
            Text = name, AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = false,
            Margin = new Padding(0, LogicalToDeviceUnits(3), LogicalToDeviceUnits(12), LogicalToDeviceUnits(3)),
        }, 0, row);
        _usageRows.Controls.Add(new UsageBar
        {
            Percent = w.Percent, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            MinimumSize = new Size(LogicalToDeviceUnits(120), LogicalToDeviceUnits(10)),
            Size = new Size(LogicalToDeviceUnits(120), LogicalToDeviceUnits(10)),
            Margin = new Padding(0, LogicalToDeviceUnits(3), 0, LogicalToDeviceUnits(3)),
            AccessibleName = $"{name} usage: {value}",
        }, 1, row);
        _usageRows.Controls.Add(new Label
        {
            Text = value, AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = false,
            Margin = new Padding(LogicalToDeviceUnits(12), LogicalToDeviceUnits(3), 0, LogicalToDeviceUnits(3)),
        }, 2, row);
    }

    // The fetch runs off the UI thread; the new readings arrive through the UsageView's Changed event.
    private async Task RefreshUsageAsync()
    {
        if (_usageView == null) return;
        _usageRefresh.Enabled = false;
        try { await _usageView.Refresh(); }
        catch (Exception) { /* a failed check shows as the status line */ }
        finally
        {
            if (!IsDisposed) _usageRefresh.Enabled = true;
        }
    }

    /// <summary>A flat bar: green below 70 %, amber from 70 %, red from 90 % (<see cref="TrayText.UsageLevelOf"/>).</summary>
    private sealed class UsageBar : Control
    {
        private double _percent;

        public UsageBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Percent
        {
            get => _percent;
            set { _percent = Math.Clamp(value, 0, 100); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var back = new SolidBrush(SystemColors.Window)) e.Graphics.FillRectangle(back, r);
            var ink = TrayText.UsageLevelOf(_percent) switch
            {
                UsageLevel.High => StatusColors.Fail,
                UsageLevel.Warn => StatusColors.Warn,
                _ => StatusColors.Ok,
            };
            var fill = (int)Math.Round(r.Width * _percent / 100.0);
            if (fill > 0)
                using (var b = new SolidBrush(ink)) e.Graphics.FillRectangle(b, new Rectangle(r.X, r.Y, fill, r.Height));
            using var border = new Pen(SystemColors.ControlDark);
            e.Graphics.DrawRectangle(border, r);
        }
    }

    private string SelectedProvider =>
        _rbVertex.Checked ? Providers.Vertex : _rbApiKey.Checked ? Providers.ApiKey : Providers.Login;

    // Shows what fits the selected profile: the home explanation, or the provider fields, checks and env vars.
    private void UpdateProviderPanels()
    {
        SuspendLayout();
        var editable = _current != null && !_currentIsHome;
        var p = SelectedProvider;
        _homePanel.Visible = _current != null && _currentIsHome;
        _providerRow.Visible = editable;
        _vertexPanel.Visible = _vertexHelp.Visible = editable && p == Providers.Vertex;
        _apiKeyPanel.Visible = editable && p == Providers.ApiKey;
        _loginPanel.Visible = editable && p == Providers.Login;
        _fieldError.Visible = _checkRow.Visible = _checkStatus.Visible = editable;
        _signIn.Visible = p == Providers.Vertex;
        _envLabel.Visible = _envHint.Visible = _env.Visible = _envButtons.Visible = _envError.Visible = editable;
        _save.Visible = editable;
        _env.AllowUserToAddRows = editable;
        if (!_loading) ShowCheckRecord(); // a running check keeps running; its result is filed under its profile
        ResumeLayout(true);
        EnsureFits();
    }

    // Everything the user can edit, as one comparable string (dirty tracking).
    private string State()
    {
        var parts = new List<string>
        {
            SelectedProvider, _project.Text, _region.Text, _sonnet.Text, _opus.Text, _haiku.Text, _apiKey.Text,
        };
        foreach (var (name, value) in EnvRows()) parts.Add(name + "=" + value);
        return string.Join("\u0001", parts);
    }

    private bool IsDirty => _current != null && !_currentIsHome && State() != _loadedState;

    // After any edit: validation messages, the Save and Check buttons, the "Unsaved changes" marks.
    private void OnEdited()
    {
        if (_loading) return;
        var dirty = IsDirty;
        var fieldsOk = ValidateFields();
        var envOk = ValidateEnv();
        Text = BaseTitle + (dirty ? " •" : "");
        _unsaved.Visible = dirty;
        _save.Enabled = dirty && fieldsOk && envOk;
        _check.Text = dirty ? SaveAndCheckText : CheckText;
        UpdateActionStates();
    }

    // Required provider fields; shows what is missing under them.
    private bool ValidateFields()
    {
        string? missing = null;
        if (_current != null && !_currentIsHome)
        {
            if (SelectedProvider == Providers.Vertex)
            {
                var noProject = string.IsNullOrWhiteSpace(_project.Text);
                var noRegion = string.IsNullOrWhiteSpace(_region.Text);
                if (noProject || noRegion)
                    missing = noProject && noRegion ? "Enter a project and a region to save."
                        : noProject ? "Enter a project to save." : "Enter a region to save.";
            }
            else if (SelectedProvider == Providers.ApiKey && string.IsNullOrWhiteSpace(_apiKey.Text) && !_hasSavedKey)
                missing = "Paste the API key to save.";
        }
        if (missing == null) _fieldError.Show(null, StatusColors.Muted, "");
        else _fieldError.Show(Glyph.Info, StatusColors.Muted, missing);
        return missing == null;
    }

    // Env rows: names follow the rules, aren't provider-managed, aren't listed twice. Marks bad rows with an error icon.
    private bool ValidateEnv()
    {
        string? first = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (DataGridViewRow r in _env.Rows)
        {
            if (r.IsNewRow) continue;
            var name = (r.Cells[0].Value as string ?? "").Trim();
            var value = r.Cells[1].Value as string ?? "";
            string? error = null;
            if (name.Length == 0)
            {
                if (value.Length > 0) error = "Give this variable a name.";
            }
            else if (ProviderOverlay.ReservedEnv.Contains(name))
                error = $"{name} is set by the provider above and can't be set here.";
            else
            {
                try { ProviderOverlay.ValidateEnvName(name); }
                catch (ClauderooException) { error = $"'{name}' isn't a valid name: use letters, digits and _, not starting with a digit."; }
                if (error == null && !seen.Add(name)) error = $"{name} is listed twice.";
            }
            r.Cells[0].ErrorText = error ?? "";
            first ??= error;
        }
        if (first == null) _envError.Show(null, StatusColors.Muted, "");
        else _envError.Show(Glyph.Error, StatusColors.Fail, first);
        return first == null;
    }

    private List<(string Name, string Value)> EnvRows()
    {
        var rows = new List<(string, string)>();
        foreach (DataGridViewRow r in _env.Rows)
        {
            if (r.IsNewRow) continue;
            var name = (r.Cells[0].Value as string ?? "").Trim();
            var value = r.Cells[1].Value as string ?? "";
            if (name.Length == 0 && value.Length == 0) continue;
            rows.Add((name, value));
        }
        return rows;
    }

    private void DeleteEnvRows()
    {
        var rows = _env.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow)
            .Where(r => r is { IsNewRow: false }).Distinct().ToList();
        if (rows.Count == 0 && _env.CurrentRow is { IsNewRow: false } cur) rows.Add(cur);
        foreach (var r in rows) _env.Rows.Remove(r!);
    }

    // ---------- save ----------

    /// <summary>Saves the editor; false (after showing why) when nothing was saved.</summary>
    private bool Save()
    {
        if (_current == null || _currentIsHome) return true;
        _env.EndEdit();
        ClearResult();
        var name = _current;
        var provider = SelectedProvider;

        // Validate everything before the first write, so a failure changes nothing. The messages are inline already.
        if (!ValidateFields() || !ValidateEnv())
        {
            SetResult("Not saved: fix the fields marked above first.", StatusColors.Fail);
            return false;
        }
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (n, v) in EnvRows()) env[n] = v;
        try
        {
            if (provider != Providers.ApiKey && _pm.HasApiKey(name) &&
                MessageBox.Show(this, $"Switching {name} away from API key deletes its saved key. Continue?", "clauderoo",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return false;
        }
        catch (Exception e)
        {
            ShowError(e.Message);
            return false;
        }

        var notices = new List<string>();
        var problems = new List<string>();
        var providerSaved = false;
        try
        {
            var vertex = provider == Providers.Vertex
                ? new VertexSettings
                {
                    Project = _project.Text, Region = _region.Text,
                    SonnetModel = _sonnet.Text, OpusModel = _opus.Text, HaikuModel = _haiku.Text,
                }
                : null;
            var apiKey = provider == Providers.ApiKey && !string.IsNullOrWhiteSpace(_apiKey.Text) ? _apiKey.Text.Trim() : null;
            Write(() =>
            {
                Collect(_pm.SetProvider(name, provider, vertex, apiKey), notices, problems);
                providerSaved = true;
                Collect(_pm.ReplaceEnv(name, env), notices, problems);
            });
        }
        catch (Exception e) // file/sync errors too, not only ClauderooException: never let them reach the tray
        {
            if (!providerSaved)
            {
                ShowError(e.Message);
                return false;
            }
            // The provider is stored but the env vars aren't: show the saved state, keep the unsaved rows to retry.
            var unsaved = EnvRows();
            Reload(name);
            _env.Rows.Clear();
            foreach (var (n, v) in unsaved) _env.Rows.Add(n, v);
            var message = $"Provider saved, but environment variables failed: {e.Message}";
            ShowReport([message, .. notices.Distinct()], problems, failed: true);
            ShowError(message);
            return false;
        }

        Reload(name);
        ShowReport([problems.Count > 0 ? "Saved, with problems:" : "Saved.", .. notices.Distinct()], problems);
        _showRestart = true;
        UpdateRestartNote();
        return true;
    }

    private static void Collect(SyncReport r, List<string> notices, List<string> problems)
    {
        notices.AddRange(r.Notices);
        problems.AddRange(r.Problems);
    }

    /// <summary>Asks to save unsaved changes; false when the user cancelled (or the save failed).</summary>
    private bool ConfirmLeave()
    {
        if (!IsDirty) return true;
        var answer = MessageBox.Show(this, $"Save changes to {_current}?", "clauderoo",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => Save(),
            DialogResult.No => true,
            _ => false,
        };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.S:
                if (_save.Visible && _save.Enabled) Save();
                return true;
            case Keys.Control | Keys.N:
                Guard(NewProfile);
                return true;
            case Keys.F2 when _list.Focused: // elsewhere (e.g. the env grid) F2 keeps its own meaning
                if (_rename.Enabled) Guard(RenameProfile);
                return true;
            case Keys.Control | Keys.Enter:
                if (_start.Enabled) Guard(StartSession);
                return true;
            case Keys.Delete when _list.Focused:
                if (_remove.Enabled) Guard(RemoveProfile);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !ConfirmLeave()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _runningTimer.Stop();
        AbortCheck();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (_usageView != null) _usageView.Changed -= ShowUsage;
            AbortCheck(); // the tray may dispose the window without closing it
            _runningTimer.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            // After base: the form still uses its icon and fonts while it is torn down.
            _icon?.Dispose();
            _boldFont?.Dispose();
        }
    }

    // Stops a running setup check without touching controls (the window is going away).
    private void AbortCheck()
    {
        _checkSeq++;
        _checkCts?.CancelAsync(); // callbacks run off the UI thread
        _checkCts = null;
        _checkProgress?.Cancel();
    }

    // ---------- checks ----------

    private void RunCheck()
    {
        if (_current == null || _currentIsHome || _checkingFor != null) return;
        ClearResult();
        if (IsDirty) // Check tests what is saved: save first
        {
            if (!_save.Enabled)
            {
                _checkStatus.Show(Glyph.Info, StatusColors.Muted, "Fill in the fields marked above, then Check setup.");
                return;
            }
            if (!Save()) return;
        }
        var name = _current;
        var provider = SelectedProvider;
        if (provider == Providers.Vertex)
        {
            RunSetupCheck(name);
            return;
        }

        var seq = _checkSeq;
        _checkingFor = name;
        ShowCheckRecord();
        UpdateActionStates();
        // Can take a moment: run off the UI thread, then marshal the result back with BeginInvoke.
        Task.Run(() =>
        {
            CheckResult result;
            try { result = _pm.Check(name); }
            catch (Exception e) { result = new CheckResult(false, e.Message); }
            try
            {
                BeginInvoke(() =>
                {
                    if (seq != _checkSeq) return; // cancelled (profile renamed/removed, window closing)
                    LastChecks[name] = new CheckRecord(DateTime.Now, provider,
                        !result.Ok ? CheckStatus.Fail : result.Verified ? CheckStatus.Ok : null, SignInWords(result.Message), null);
                    CheckFinished(name);
                });
            }
            catch (InvalidOperationException) { /* window closed meanwhile */ }
        });
    }

    // Core's login check says "logged in"; the window says "signed in" everywhere.
    private static string SignInWords(string s) =>
        s.Replace("Not logged in", "Not signed in").Replace("Logged in", "Signed in").Replace("logged in", "signed in");

    // Vertex: the full checklist (gcloud, sign-in, IAM, leftover vars, a tiny prompt per model).
    private void RunSetupCheck(string name)
    {
        var seq = _checkSeq;
        var cts = _checkCts = new CancellationTokenSource();
        var ct = cts.Token;
        var live = _checkProgress = new CheckProgressModel();
        // Each row fills in as its check finishes; posted through the same queue as the final result, so in order.
        var progress = new UiProgress(this, item =>
        {
            if (seq != _checkSeq) return; // cancelled
            live.Apply(item);
            if (_current == name && _checkingFor == name) ShowCheckRecord();
        });
        _checkingFor = name;
        ShowCheckRecord();
        UpdateActionStates(); // Rename/Remove would move the folder `claude -p` runs in
        _checkTask = Task.Run(async () =>
        {
            IReadOnlyList<CheckItem>? items = null;
            string? error = null;
            try { items = await _pm.CheckSetupAsync(name, ct, progress).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e) { error = e.Message; }
            try
            {
                BeginInvoke(() =>
                {
                    // The CTS is only touched on the UI thread, so cancelling and disposing never race.
                    if (_checkCts == cts) _checkCts = null;
                    cts.Dispose();
                    if (seq != _checkSeq) return; // cancelled: drop the result
                    if (_checkProgress == live) _checkProgress = null;
                    if (items != null) LastChecks[name] = Summarise(items);
                    else if (error != null) LastChecks[name] = new CheckRecord(DateTime.Now, Providers.Vertex, CheckStatus.Fail, error, null);
                    CheckFinished(name);
                });
            }
            catch (InvalidOperationException) { cts.Dispose(); /* window closed meanwhile */ }
        });
    }

    // Runs each report on the UI thread through the window's own BeginInvoke queue (FIFO with the final result).
    private sealed class UiProgress(Control owner, Action<CheckItem> apply) : IProgress<CheckItem>
    {
        public void Report(CheckItem value)
        {
            try { owner.BeginInvoke(() => apply(value)); }
            catch (InvalidOperationException) { /* window closed meanwhile */ }
        }
    }

    // A check ended (its result, if any, is filed under the profile it was started for).
    private void CheckFinished(string name)
    {
        if (_checkingFor == name) _checkingFor = null;
        UpdateActionStates();
        _list.Invalidate();
        if (_current == name) ShowCheckRecord();
    }

    private static CheckRecord Summarise(IReadOnlyList<CheckItem> items)
    {
        var fails = items.Count(i => i.Status == CheckStatus.Fail);
        var warns = items.Count(i => i.Status == CheckStatus.Warn);
        var (status, message) =
            fails > 0 ? (CheckStatus.Fail, fails == 1 ? "1 problem found." : $"{fails} problems found.")
            : warns > 0 ? (CheckStatus.Warn, warns == 1 ? "Works, with 1 warning." : $"Works, with {warns} warnings.")
            : (CheckStatus.Ok, "Setup looks good.");
        return new CheckRecord(DateTime.Now, Providers.Vertex, status, message, items);
    }

    // The current profile's check: running, or the last result if it was made for the provider now selected.
    private void ShowCheckRecord()
    {
        if (_current == null || _currentIsHome)
        {
            _checkStatus.Show(null, StatusColors.Muted, "");
            ShowCheckList(null);
            return;
        }
        if (_checkingFor == _current)
        {
            if (SelectedProvider == Providers.Vertex && _checkProgress is { Total: > 0 } live)
            {
                _checkStatus.Show(Glyph.Info, StatusColors.Muted, live.StatusText);
                ShowCheckList(live.Rows, live: true);
            }
            else
            {
                _checkStatus.Show(Glyph.Info, StatusColors.Muted,
                    SelectedProvider == Providers.Vertex ? "Checking… (tests a tiny prompt per model)" : "Checking…");
                ShowCheckList(null);
            }
            UpdateActionStates();
            return;
        }
        if (RecordFor(_current, SelectedProvider) is not { } rec)
        {
            _checkStatus.Show(null, StatusColors.Muted, "");
            ShowCheckList(null);
            UpdateActionStates();
            return;
        }
        var (glyph, color) = rec.Status is { } s ? CheckListView.Look(s) : (Glyph.Info, StatusColors.Muted);
        _checkStatus.Show(glyph, color, $"{rec.Message}  Last checked {When(rec.At)}.");
        ShowCheckList(rec.Items);
        UpdateActionStates();
    }

    private void ShowCheckList(IReadOnlyList<CheckItem>? items, bool live = false)
    {
        var show = items is { Count: > 0 } && _current != null && !_currentIsHome;
        if (show) _checkList.SetItems(items!, live);
        if (_checkList.Visible == show) return;
        _checkList.Visible = show;
        EnsureFits();
    }

    private void CopyResults()
    {
        if (_current == null || RecordFor(_current, SelectedProvider) is not { } rec) return;
        var text = $"{_current}: {rec.Message} (checked {When(rec.At)})";
        if (rec.Items is { Count: > 0 } items)
            text += Environment.NewLine + string.Join(Environment.NewLine, items.Select(CheckItemFormatter.Format));
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
    }

    /// <summary>
    /// Cancels a check running for <paramref name="name"/> and waits (at most ~3 s) for its task to end; on cancellation
    /// the claude runner kills its process tree before the task completes, so the profile folder is released.
    /// </summary>
    private void StopCheckAndWait(string name)
    {
        if (_checkingFor != name) return;
        var running = _checkCts != null ? _checkTask : null;
        _checkSeq++;
        _checkCts?.CancelAsync(); // callbacks run off the UI thread
        _checkCts = null;
        _checkProgress?.Cancel(); // rows still waiting read "Cancelled", never stuck on "Checking…"
        _checkingFor = null;
        UpdateActionStates();
        if (running == null) return;
        // The task never needs the UI thread to finish (ConfigureAwait(false); results are posted with BeginInvoke).
        try { running.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* the task catches its own exceptions; nothing to report */ }
    }

    private void UpdateActionStates()
    {
        var checkingHere = _checkingFor != null && _checkingFor == _current;
        _rename.Enabled = _current != null && !checkingHere;
        _setDefault.Enabled = _current != null && !_currentIsDefault;
        _remove.Enabled = _current != null && !_currentIsHome && !_currentIsDefault && !checkingHere;
        _check.Enabled = _checkingFor == null;
        _copy.Enabled = _current != null && !checkingHere && RecordFor(_current, SelectedProvider) != null;
        // A disabled button shows no tooltip itself: put the reason on its panel too.
        var why = _current == null ? null
            : _currentIsHome ? "The home profile can't be removed."
            : _currentIsDefault ? "Choose another default first."
            : null;
        _tips.SetToolTip(_remove, why);
        _tips.SetToolTip(_leftButtons, why);
        _remove.AccessibleDescription = why;
    }

    // ---------- New / Rename / Remove / Undo / Start ----------

    private IEnumerable<string> ProfileNames() => _list.Items.Cast<ListItem>().Select(i => i.Info.Name);

    private void NewProfile()
    {
        if (!ConfirmLeave()) return;
        ClearResult();
        var name = InputDialog.Ask("New profile", "Profile name:", this, null,
            InputDialog.ProfileNameRule(ProfileNames()), InputDialog.ProfileNameHint, lowercase: true);
        if (string.IsNullOrEmpty(name)) return;
        var report = Write(() => _pm.Add(name));
        Reload(name);
        ShowResult(report, $"Profile '{name}' created. Start a session in it to sign in.");
    }

    private void RenameProfile()
    {
        if (_current == null || !ConfirmLeave()) return;
        ClearResult();
        var oldName = _current;
        if (BlockIfRunning(oldName, "renamed")) return;
        var newName = InputDialog.Ask("Rename profile", $"New name for '{oldName}':", this, oldName,
            InputDialog.ProfileNameRule(ProfileNames(), oldName), InputDialog.ProfileNameHint, lowercase: true);
        if (string.IsNullOrEmpty(newName)) return;
        // A running setup check may have `claude -p` open in the old folder: stop it before the folder moves.
        StopCheckAndWait(oldName);
        SyncReport report;
        try
        {
            report = Write(() => _pm.Rename(oldName, newName));
        }
        catch (SessionsRunningException e)
        {
            ShowBlocked(e.Profile, e.Count, "renamed");
            return;
        }
        catch (ClauderooException e) when (e.Message.Contains("--force"))
        {
            if (MessageBox.Show(this, WithoutCli(e.Message) + Environment.NewLine + Environment.NewLine + "Rename anyway?", "clauderoo",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (BlockIfRunning(oldName, "renamed")) return; // force skips Core's session check: check again
            report = Write(() => _pm.Rename(oldName, newName, force: true));
        }
        if (LastChecks.Remove(oldName, out var rec)) LastChecks[newName] = rec;
        _loadedState = State(); // the editor's profile is gone under its old name: nothing left to save
        _current = null;
        Reload(newName);
        ShowResult(report, $"Profile '{oldName}' renamed to '{newName}'.");
    }

    private void RemoveProfile()
    {
        if (_current == null || _currentIsHome || _currentIsDefault) return;
        ClearResult();
        var name = _current;
        if (BlockIfRunning(name, "removed")) return;
        if (MessageBox.Show(this, $"Remove profile '{name}'?{Environment.NewLine}{Environment.NewLine}" +
                                  "You can undo this here until you do something else.", "clauderoo",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        StopCheckAndWait(name);
        try
        {
            Write(() => _pm.Remove(name));
        }
        catch (SessionsRunningException e)
        {
            ShowBlocked(e.Profile, e.Count, "removed");
            return;
        }
        catch (ClauderooException e) when (e.Message.Contains("--force"))
        {
            if (MessageBox.Show(this, WithoutCli(e.Message) + Environment.NewLine + Environment.NewLine + "Remove anyway?", "clauderoo",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (BlockIfRunning(name, "removed")) return; // force skips Core's session check: check again
            Write(() => _pm.Remove(name, force: true));
        }
        LastChecks.Remove(name);
        _loadedState = State(); // removed: nothing left to save
        _current = null;
        Reload(null);
        _undoName = name;
        _result.Show($"Removed {name}.", SystemColors.ControlText, "Undo");
        _resultLink = UndoRemove;
    }

    private void UndoRemove()
    {
        var name = _undoName;
        if (name == null || !ConfirmLeave()) return;
        ClearResult();
        var report = Write(() => _pm.Restore(name));
        Reload(name);
        ShowResult(report, $"Restored {name}.");
    }

    // True (after saying why) when clauderoo-started sessions of the profile are running, or when that can't be told.
    private bool BlockIfRunning(string name, string verb)
    {
        IReadOnlyList<RunningSession>? running;
        try { running = _pm.Sessions.TryRunning(name); }
        catch (Exception) { running = null; }
        if (running == null)
        {
            MessageBox.Show(this, "clauderoo couldn't check for running sessions — try again in a moment.", "clauderoo",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        if (running.Count == 0) return false;
        ShowBlocked(name, running.Count, verb);
        return true;
    }

    private void ShowBlocked(string name, int count, string verb)
    {
        var sessions = count == 1 ? "a Claude session" : $"{count} Claude sessions";
        MessageBox.Show(this,
            $"{name} can't be {verb} while {sessions} started from clauderoo {(count == 1 ? "is" : "are")} running in it." +
            $"{Environment.NewLine}{Environment.NewLine}Close {(count == 1 ? "it" : "them")}, then try again.",
            "clauderoo", MessageBoxButtons.OK, MessageBoxIcon.Information);
        RefreshRunning();
    }

    // Core's refusals end with a CLI hint ("…, or run 'clauderoo remove x --force'."); the window asks instead.
    private static string WithoutCli(string message)
    {
        var i = message.IndexOf(", or run '", StringComparison.Ordinal);
        return i < 0 ? message : message[..i] + ".";
    }

    private void StartSession()
    {
        if (_current == null || !ConfirmLeave()) return;
        ClearResult();
        _startSession(_current);
    }

    /// <summary>
    /// A default chosen by hand: on a worker thread inside the tray's write gate (SetDefault syncs), and noted as a
    /// manual hold so auto-switch doesn't move straight off it.
    /// </summary>
    private async Task SetDefaultAsync()
    {
        if (_current == null || _currentIsDefault || !ConfirmLeave()) return;
        ClearResult();
        var name = _current;
        try
        {
            var report = await RunWriteAsync(() => _pm.SetDefaultByUser(name));
            if (IsDisposed) return;
            if (!IsDirty) Reload(name);
            ShowResult(report, $"Default is now {name}. New terminals will use it.");
        }
        catch (Exception e)
        {
            if (!IsDisposed) ShowError(e.Message);
        }
    }

    /// <summary>
    /// The Auto-switch dialog. Saved straight to the config inside the write gate (read, change and save in one go):
    /// it doesn't touch the profile being edited. The tray then decides again with the new settings.
    /// </summary>
    private async Task EditAutoSwitchAsync()
    {
        try
        {
            ClearResult();
            var cfg = _pm.Config;
            var profiles = cfg.Profiles.Select(kv => (kv.Key, Providers.Of(kv.Value))).ToList();
            if (AutoSwitchDialog.Edit(this, cfg.AutoSwitch ?? new AutoSwitchSettings(), profiles) is not { } edited) return;
            await RunWriteAsync(() =>
            {
                var fresh = _pm.Config; // read again: the tray may have changed the default while the dialog was open
                fresh.AutoSwitch = edited;
                ConfigStore.Save(_pm.Paths, fresh);
                return true;
            });
            if (IsDisposed) return;
            SetResult(TrayText.AutoSwitchSummary(edited), SystemColors.ControlText);
            _autoSwitchChanged?.Invoke();
        }
        catch (Exception e)
        {
            if (!IsDisposed) ShowError(e.Message);
        }
    }

    /// <summary>A write on a worker thread, waiting its turn in the tray's write gate (the window never blocks).</summary>
    private Task<T> RunWriteAsync<T>(Func<T> write) => _gate != null ? _gate.RunAsync(write) : Task.Run(write);

    /// <summary>
    /// A write on the UI thread, as before, inside the tray's write gate. The gate is only tried, never waited on:
    /// while another write runs (a sync, an automatic switch) this throws <see cref="TrayText.Busy"/>, shown as a message.
    /// </summary>
    private T Write<T>(Func<T> write)
    {
        if (_gate == null) return write();
        using var held = _gate.TryEnter() ?? throw new ClauderooException(TrayText.Busy);
        return write();
    }

    private void Write(Action write) => Write(() => { write(); return true; });

    private void ShowResult(SyncReport report, string message) =>
        ShowReport([message, .. report.Notices], report.Problems);

    /// <summary>
    /// The result line: <paramref name="lines"/> (plain sentences), then each problem's plain title (never the raw
    /// text, which speaks to the command line). The details and fixes are in the tray's Problems window, linked when
    /// there is one.
    /// </summary>
    private void ShowReport(IEnumerable<string> lines, IEnumerable<string> problems, bool failed = false)
    {
        var titles = ProblemTitles(problems);
        var text = string.Join(Environment.NewLine, lines.Concat(titles));
        var color = failed || titles.Count > 0 ? StatusColors.Fail : SystemColors.ControlText;
        _undoName = null;
        if (titles.Count > 0 && _showProblems != null)
        {
            _result.Show(text, color, "Open Problems…", Environment.NewLine);
            _resultLink = _showProblems;
        }
        else
        {
            _result.Show(text, color);
            _resultLink = null;
        }
    }

    /// <summary>The plain titles of raw sync problems, each once.</summary>
    internal static List<string> ProblemTitles(IEnumerable<string> problems) =>
        problems.Select(p => ProblemTranslator.Translate(p).Title).Distinct().ToList();

    private void SetResult(string text, Color color)
    {
        _undoName = null;
        _resultLink = null;
        _result.Show(text, color);
    }

    // Every action starts by clearing the previous result, which also ends the offer to undo a remove.
    private void ClearResult()
    {
        SetResult("", SystemColors.ControlText);
        _showRestart = false;
        UpdateRestartNote();
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { ShowError(e.Message); }
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "clauderoo", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>Re-reads profiles from disk unless the user has unsaved edits.</summary>
    public void RefreshList()
    {
        if (!IsDirty) Reload(null);
    }

    /// <summary>Shows the window, restoring and bringing it to the front.</summary>
    public void ShowAndActivate(bool reload)
    {
        if (reload) RefreshList();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    // ---------- layout helpers ----------

    private void ScaleLiterals(Control c)
    {
        Padding Scale(Padding p) => new(LogicalToDeviceUnits(p.Left), LogicalToDeviceUnits(p.Top),
            LogicalToDeviceUnits(p.Right), LogicalToDeviceUnits(p.Bottom));
        c.Margin = Scale(c.Margin);
        c.Padding = Scale(c.Padding);
        c.MinimumSize = LogicalToDeviceUnits(c.MinimumSize);
        foreach (Control child in c.Controls) ScaleLiterals(child);
    }

    // Labels that wrap to the width of their column (TableLayoutPanel doesn't wrap AutoSize labels reliably).
    private static void WrapTo(Control column, params Control[] labels)
    {
        void Fit()
        {
            foreach (var l in labels) ((IWrapping)l).WrapTo(Math.Max(1, column.ClientSize.Width - l.Margin.Horizontal));
            column.PerformLayout();
        }
        foreach (var l in labels)
        {
            l.AutoSize = false;
            l.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            l.TextChanged += (_, _) => Fit();
        }
        column.SizeChanged += (_, _) => Fit();
        Fit();
    }

    // Grows the window (within the screen) when wrapped status text or the check list needs more room than it
    // has, and keeps the minimum height in step so the window can't then be shrunk over the content.
    private void EnsureFits()
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
        _root.PerformLayout();
        var need = _top.GetPreferredSize(new Size(_right.ClientSize.Width, 0)).Height
                   + (_env.Visible ? _envButtons.Height + _envError.Height + _env.MinimumSize.Height : 0);
        var grow = need - _right.ClientSize.Height;
        var max = Screen.FromControl(this).WorkingArea.Height;
        var height = Height;
        MinimumSize = new Size(MinimumSize.Width, Math.Min(max, Math.Max(_baseMinHeight, height + grow)));
        if (grow > 0) Height = Math.Min(height + grow, max);
    }

    private static Button Btn(string text) =>
        new() { Text = text, AutoSize = true, MinimumSize = new Size(80, 0), Margin = new Padding(0, 0, 6, 0) };

    private static RadioButton Radio(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };

    private static TextBox Field(string cue) => new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 2), PlaceholderText = cue,
    };

    private static TableLayoutPanel Table(int columns) => new()
    {
        ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
        Padding = Padding.Empty,
    };

    private static FlowLayoutPanel Flow(FlowDirection direction) => new()
    {
        FlowDirection = direction, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
        Margin = Padding.Empty, Padding = Padding.Empty,
    };

    private static TableLayoutPanel FieldGrid()
    {
        var t = Table(2);
        t.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        t.Margin = new Padding(0, 0, 0, 6);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static void AddField(TableLayoutPanel grid, string label, Control field)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 12, 3) }, 0, row);
        grid.Controls.Add(field, 1, row);
    }
}
