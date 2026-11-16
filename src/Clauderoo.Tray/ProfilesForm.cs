using Clauderoo.Core;

namespace Clauderoo.Tray;

// The Profiles window: pick each profile's provider and env vars. Thin over ProfileManager.
// Layout is driven by auto-sizing panels (no fixed pixel positions), so it fits at any display scaling.
sealed class ProfilesForm : Form
{
    private const string HomeMessage = "The home profile always uses its own Claude login.";

    private readonly ProfileManager _pm;
    private readonly Action<string> _startSession;
    private readonly Gcloud _gcloud = new();

    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill, IntegralHeight = false, Margin = new Padding(0, 0, 0, 6),
    };
    private readonly Button _new = Btn("New…");
    private readonly Button _remove = Btn("Remove");

    private readonly WrapLabel _homeNote = new()
    {
        Text = HomeMessage, AutoSize = true, Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 0, 0, 8), Visible = false,
    };
    private readonly RadioButton _rbLogin = Radio("Claude login");
    private readonly RadioButton _rbVertex = Radio("Google Vertex AI");
    private readonly RadioButton _rbApiKey = Radio("API key");

    private readonly TableLayoutPanel _vertexPanel = FieldGrid();
    private readonly TextBox _project = Field();
    private readonly TextBox _region = Field();
    private readonly TextBox _sonnet = Field();
    private readonly TextBox _opus = Field();
    private readonly TextBox _haiku = Field();

    private readonly TableLayoutPanel _apiKeyPanel = FieldGrid();
    private readonly TextBox _apiKey = Field();

    private readonly TableLayoutPanel _loginPanel = FieldGrid();
    private readonly Label _account = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 0, 3) };

    private readonly Button _check = Btn("Check sign-in");
    private readonly Button _signIn = Btn("Sign in to Google…");
    private readonly WrapLabel _checkStatus = new()
    {
        AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 6),
    };
    private readonly FlowLayoutPanel _checkRow;
    private readonly TableLayoutPanel _root;
    private readonly Panel _right;
    private readonly TableLayoutPanel _top;
    private readonly FlowLayoutPanel _envButtons;

    private readonly Label _envLabel = new() { Text = "Environment variables", AutoSize = true, Margin = new Padding(0, 6, 0, 3) };
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
    private readonly Button _envDelete = Btn("Delete variable");

    private readonly WrapLabel _result = new()
    {
        AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 3),
    };
    private readonly Button _start = Btn("Start session");
    private readonly Button _save = Btn("Save");

    private string? _current;      // profile shown in the editor
    private bool _currentIsHome;
    private string _loadedState = "";
    private bool _loading;         // suppresses change handlers while filling controls
    private bool _switching;       // suppresses re-entrant selection changes
    private int _checkSeq;         // ignores stale async check results

    public ProfilesForm(ProfileManager pm, Action<string> startSession)
    {
        _pm = pm;
        _startSession = startSession;

        Text = "clauderoo — Profiles";
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(12);
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (Exception) { }

        // Left: profile list + New / Remove.
        _list.MinimumSize = new Size(LogicalToDeviceUnits(170), LogicalToDeviceUnits(200));
        var leftButtons = Flow(FlowDirection.LeftToRight);
        leftButtons.Controls.AddRange([_new, _remove]);
        var left = Table(1);
        left.Dock = DockStyle.Fill;
        left.Margin = new Padding(0, 0, 12, 0);
        left.RowCount = 2;
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(_list, 0, 0);
        left.Controls.Add(leftButtons, 0, 1);

        // Right: provider choice.
        var radios = Flow(FlowDirection.TopDown);
        radios.Controls.AddRange([_rbLogin, _rbVertex, _rbApiKey]);
        var providerRow = Table(2);
        providerRow.Controls.Add(new Label { Text = "Provider:", AutoSize = true, Margin = new Padding(0, 4, 12, 0) }, 0, 0);
        providerRow.Controls.Add(radios, 1, 0);
        providerRow.Margin = new Padding(0, 0, 0, 6);

        AddField(_vertexPanel, "Project:", _project);
        AddField(_vertexPanel, "Region:", _region);
        AddField(_vertexPanel, "Sonnet model (optional):", _sonnet);
        AddField(_vertexPanel, "Opus model (optional):", _opus);
        AddField(_vertexPanel, "Haiku model (optional):", _haiku);
        foreach (var tb in new[] { _project, _region, _sonnet, _opus, _haiku, _apiKey })
            tb.MinimumSize = new Size(LogicalToDeviceUnits(240), 0);
        _apiKey.UseSystemPasswordChar = true;
        AddField(_apiKeyPanel, "API key:", _apiKey);
        AddField(_loginPanel, "Account:", _account);

        _checkRow = Flow(FlowDirection.LeftToRight);
        _checkRow.Controls.AddRange([_check, _signIn]);

        var envButtons = _envButtons = Flow(FlowDirection.LeftToRight);
        envButtons.Controls.Add(_envDelete);
        _env.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 40 });
        _env.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value", FillWeight = 60 });
        _env.MinimumSize = new Size(LogicalToDeviceUnits(380), LogicalToDeviceUnits(140));

        // Right column: provider part on top (auto-sized), env buttons at the bottom, the grid fills the rest.
        // Explicit cells: auto-placement skips invisible controls, which would shift rows when panels swap.
        var top = _top = Table(1);
        top.Dock = DockStyle.Top;
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Control[] topRows = [_homeNote, providerRow, _vertexPanel, _apiKeyPanel, _loginPanel, _checkRow, _checkStatus, _envLabel];
        top.RowCount = topRows.Length;
        for (var i = 0; i < topRows.Length; i++)
        {
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            top.Controls.Add(topRows[i], 0, i);
        }
        envButtons.Dock = DockStyle.Bottom;
        envButtons.Padding = new Padding(0, 3, 0, 0);
        _env.Dock = DockStyle.Fill;
        var right = _right = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        right.Controls.Add(_env); // docked last, so it fills what the others leave
        right.Controls.Add(envButtons);
        right.Controls.Add(top);

        // Bottom: save/remove result (wraps, spans the window), then the note and Start session / Save.
        _result.Visible = false;
        _result.Margin = new Padding(0, 8, 0, 0);
        var restartNote = new Label
        {
            Text = "Running sessions pick up changes when restarted.", AutoSize = true,
            Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 12, 0),
        };
        var bottomButtons = Flow(FlowDirection.LeftToRight);
        bottomButtons.Anchor = AnchorStyles.Right;
        bottomButtons.Controls.AddRange([_start, _save]);
        var bottom = Table(2);
        bottom.Dock = DockStyle.Fill;
        bottom.Margin = new Padding(0, 8, 0, 0);
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.Controls.Add(restartNote, 0, 0);
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

        // Size the window from its content, measured with the largest provider panel showing.
        _vertexPanel.Visible = true;
        _apiKeyPanel.Visible = _loginPanel.Visible = false;
        var topPref = top.GetPreferredSize(Size.Empty);
        right.Size = new Size(Math.Max(topPref.Width, _env.MinimumSize.Width),
            topPref.Height + envButtons.GetPreferredSize(Size.Empty).Height + envButtons.Padding.Vertical + _env.MinimumSize.Height);
        var pref = root.GetPreferredSize(Size.Empty);
        ClientSize = new Size(pref.Width + Padding.Horizontal, pref.Height + Padding.Vertical);
        MinimumSize = Size;
        // The grid gives up height to long status lines before the window has to grow (EnsureFits).
        _env.MinimumSize = new Size(_env.MinimumSize.Width, LogicalToDeviceUnits(70));

        // Status labels wrap to the width of their column.
        WrapTo(top, _homeNote, _checkStatus);
        WrapTo(root, _result);
        _checkStatus.TextChanged += (_, _) => EnsureFits();
        _result.TextChanged += (_, _) =>
        {
            _result.Visible = _result.Text.Length > 0;
            EnsureFits();
        };

        _list.SelectedIndexChanged += (_, _) => OnListSelection();
        _new.Click += (_, _) => Guard(NewProfile);
        _remove.Click += (_, _) => Guard(RemoveProfile);
        foreach (var rb in new[] { _rbLogin, _rbVertex, _rbApiKey })
            rb.CheckedChanged += (_, _) => { if (rb.Checked) UpdateProviderPanels(); };
        _check.Click += (_, _) => RunCheck();
        _signIn.Click += (_, _) => Guard(_gcloud.OpenSignIn);
        _envDelete.Click += (_, _) => DeleteEnvRows();
        _start.Click += (_, _) => Guard(StartSession);
        _save.Click += (_, _) => Save();

        Reload(null);
    }

    // ---------- profile list ----------

    /// <summary>Reloads the profile list, selecting <paramref name="select"/> (or the current/first profile).</summary>
    public void Reload(string? select)
    {
        IReadOnlyList<ProfileInfo> profiles;
        try { profiles = _pm.List(); }
        catch (Exception e) { ShowError(e.Message); return; }

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
        _result.Text = ""; // a save made by ConfirmLeave reports below
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
        public override string ToString() => Info.IsHome ? $"{Info.Name} (home)" : Info.Name;
    }

    // ---------- editor ----------

    private void LoadProfile(ProfileInfo? info)
    {
        _loading = true;
        try
        {
            _checkSeq++;
            _checkStatus.Text = "";
            _current = info?.Name;
            _currentIsHome = info?.IsHome ?? false;
            _project.Text = _region.Text = _sonnet.Text = _opus.Text = _haiku.Text = _apiKey.Text = "";
            _apiKey.PlaceholderText = "";
            _env.Rows.Clear();

            if (info == null)
            {
                _rbLogin.Checked = true;
                _account.Text = "";
            }
            else
            {
                _account.Text = info.Email ?? "(not logged in — Start session to sign in)";
                if (!info.IsHome)
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
                    if (_pm.HasApiKey(info.Name)) _apiKey.PlaceholderText = "•••• saved";
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

            var editable = info is { IsHome: false };
            _homeNote.Visible = info is { IsHome: true };
            foreach (Control c in new Control[] { _rbLogin, _rbVertex, _rbApiKey, _vertexPanel, _apiKeyPanel, _env, _envDelete, _save })
                c.Enabled = editable;
            _env.AllowUserToAddRows = editable;
            _env.DefaultCellStyle.BackColor = editable ? SystemColors.Window : SystemColors.Control;
            _remove.Enabled = editable;
            _start.Enabled = info != null;
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
    }

    private string SelectedProvider =>
        _rbVertex.Checked ? Providers.Vertex : _rbApiKey.Checked ? Providers.ApiKey : Providers.Login;

    private void UpdateProviderPanels()
    {
        SuspendLayout();
        var p = SelectedProvider;
        _vertexPanel.Visible = p == Providers.Vertex;
        _apiKeyPanel.Visible = p == Providers.ApiKey;
        _loginPanel.Visible = p == Providers.Login;
        _checkRow.Visible = p != Providers.Login && !_currentIsHome;
        _check.Text = p == Providers.Vertex ? "Check sign-in" : "Check";
        _signIn.Visible = p == Providers.Vertex;
        if (!_loading) { _checkSeq++; _checkStatus.Text = ""; }
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
        var name = _current;
        var provider = SelectedProvider;

        // Validate everything before the first write, so a failure changes nothing.
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var (n, v) in EnvRows())
            {
                if (n.Length == 0) throw new ClauderooException("Each environment variable needs a name.");
                ProviderOverlay.ValidateEnvName(n);
                if (!env.TryAdd(n, v)) throw new ClauderooException($"Environment variable '{n}' is listed twice.");
            }
            if (provider == Providers.Vertex && (string.IsNullOrWhiteSpace(_project.Text) || string.IsNullOrWhiteSpace(_region.Text)))
                throw new ClauderooException("Vertex needs a project and a region.");
            if (provider == Providers.ApiKey && string.IsNullOrWhiteSpace(_apiKey.Text) && !_pm.HasApiKey(name))
                throw new ClauderooException("Enter an API key.");
        }
        catch (ClauderooException e)
        {
            ShowError(e.Message);
            return false;
        }

        var notices = new List<string>();
        var problems = new List<string>();
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
            Collect(_pm.SetProvider(name, provider, vertex, apiKey), notices, problems);
            Collect(_pm.ReplaceEnv(name, env), notices, problems);
        }
        catch (ClauderooException e)
        {
            ShowError(e.Message);
            return false;
        }

        Reload(name);
        var lines = new List<string> { problems.Count > 0 ? "Saved, with problems:" : "Saved." };
        lines.AddRange(notices.Distinct());
        lines.AddRange(problems.Distinct());
        _result.ForeColor = problems.Count > 0 ? Color.Firebrick : SystemColors.ControlText;
        _result.Text = string.Join(Environment.NewLine, lines);
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !ConfirmLeave()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    // ---------- checks ----------

    private void RunCheck()
    {
        if (_current == null) return;
        var name = _current;
        var provider = SelectedProvider;
        if (provider == Providers.ApiKey && _apiKey.Text.Length > 0)
        {
            SetCheckStatus(true, "Key entered — Save to store it.");
            return;
        }
        string saved;
        try { saved = Providers.Of(_pm.Get(name)); }
        catch (Exception e) { SetCheckStatus(false, e.Message); return; }
        if (saved != provider)
        {
            SetCheckStatus(false, "Save first, then check.");
            return;
        }

        var seq = ++_checkSeq;
        _check.Enabled = false;
        _checkStatus.ForeColor = SystemColors.GrayText;
        _checkStatus.Text = "Checking…";
        // gcloud can take seconds: run off the UI thread, then marshal the result back with BeginInvoke.
        Task.Run(() =>
        {
            CheckResult result;
            try { result = _pm.Check(name); }
            catch (Exception e) { result = new CheckResult(false, e.Message); }
            try
            {
                BeginInvoke(() =>
                {
                    _check.Enabled = true;
                    if (seq == _checkSeq) SetCheckStatus(result.Ok, result.Message);
                });
            }
            catch (InvalidOperationException) { /* window closed meanwhile */ }
        });
    }

    private void SetCheckStatus(bool ok, string message)
    {
        _checkStatus.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        _checkStatus.Text = (ok ? "✓ " : "✗ ") + message;
    }

    // ---------- New / Remove / Start ----------

    private void NewProfile()
    {
        if (!ConfirmLeave()) return;
        var name = InputDialog.Ask("New profile", "Profile name (a-z, 0-9, -):")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return;
        var report = _pm.Add(name);
        Reload(name);
        ShowResult(report, $"Profile '{name}' created. Start a session in it to log in.");
    }

    private void RemoveProfile()
    {
        if (_current == null || _currentIsHome) return;
        var name = _current;
        if (MessageBox.Show(this, $"Remove profile '{name}'? Its folder is moved to profiles\\_removed.", "clauderoo",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            _pm.Remove(name);
        }
        catch (ClauderooException e) when (e.Message.Contains("--force"))
        {
            if (MessageBox.Show(this, e.Message + Environment.NewLine + Environment.NewLine + "Remove anyway?", "clauderoo",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _pm.Remove(name, force: true);
        }
        _loadedState = State(); // removed: nothing left to save
        _current = null;
        Reload(null);
        _result.ForeColor = SystemColors.ControlText;
        _result.Text = $"Profile '{name}' removed.";
    }

    private void StartSession()
    {
        if (_current == null || !ConfirmLeave()) return;
        _startSession(_current);
    }

    private void ShowResult(SyncReport report, string message)
    {
        var lines = new List<string> { message };
        lines.AddRange(report.Notices);
        lines.AddRange(report.Problems);
        _result.ForeColor = report.Problems.Count > 0 ? Color.Firebrick : SystemColors.ControlText;
        _result.Text = string.Join(Environment.NewLine, lines);
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

    // Labels that wrap to the width of their column (TableLayoutPanel doesn't wrap AutoSize labels reliably).
    private static void WrapTo(Control column, params WrapLabel[] labels)
    {
        void Fit()
        {
            foreach (var l in labels) l.WrapTo(Math.Max(1, column.ClientSize.Width - l.Margin.Horizontal));
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

    /// <summary>A label whose preferred size is its text wrapped to the width given to <see cref="WrapTo"/> (0 high when empty).</summary>
    private sealed class WrapLabel : Label
    {
        private int _wrapWidth = 1;

        public void WrapTo(int width)
        {
            if (_wrapWidth == width) return;
            _wrapWidth = width;
            Size = GetPreferredSize(Size.Empty);
        }

        public override Size GetPreferredSize(Size proposedSize) =>
            Text.Length == 0
                ? new Size(_wrapWidth, 0)
                : new Size(_wrapWidth, base.GetPreferredSize(new Size(_wrapWidth, 0)).Height);

        protected override void OnTextChanged(EventArgs e)
        {
            Size = GetPreferredSize(Size.Empty);
            base.OnTextChanged(e);
        }
    }

    // Grows the window (within the screen) when wrapped status text needs more room than it has.
    private void EnsureFits()
    {
        if (!IsHandleCreated) return;
        _root.PerformLayout();
        var need = _top.GetPreferredSize(new Size(_right.ClientSize.Width, 0)).Height
                   + _envButtons.Height + _env.MinimumSize.Height;
        var grow = need - _right.ClientSize.Height;
        if (grow <= 0) return;
        var max = Screen.FromControl(this).WorkingArea.Height;
        Height = Math.Min(Height + grow, max);
    }

    private static Button Btn(string text) =>
        new() { Text = text, AutoSize = true, MinimumSize = new Size(80, 0), Margin = new Padding(0, 0, 6, 0) };

    private static RadioButton Radio(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };

    private static TextBox Field() => new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 2) };

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
