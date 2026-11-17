using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>
/// "Continue a conversation…": every conversation across the profiles (when, profile, folder, first prompt), a search
/// box and a profile filter, and a "Continue as" choice. The list is built on a background task (it reads files) and
/// shown on the UI thread. Only the first prompt is displayed; nothing else of a conversation is read or shown.
/// </summary>
internal sealed class ConversationsForm : Form
{
    const string AllProfiles = "All profiles";

    readonly Func<Task<IReadOnlyList<Conversation>>> _load;
    readonly Func<IReadOnlyList<ProfileInfo>> _profiles;
    readonly Func<Conversation, ContinueTarget, Task<CopyOutcome>> _continue;
    readonly Icon _icon;

    readonly Label _searchLabel = new() { Text = "&Search", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 0) };
    readonly TextBox _search = new() { Margin = new Padding(0, 0, 16, 0), Width = 240 };
    readonly Label _filterLabel = new() { Text = "&Profile", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 0) };
    readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0), Width = 160 };
    readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), MinimumSize = new Size(480, 160),
    };
    readonly Label _status = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 0), UseMnemonic = false };
    readonly Label _asLabel = new() { Text = "Continue &as", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 0) };
    readonly ComboBox _as = new() { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 0, 12, 0), Width = 200 };
    readonly Button _go = new() { Text = "&Continue", AutoSize = true, Margin = new Padding(0, 0, 8, 0), Enabled = false };
    readonly Button _close = new() { Text = "Close", AutoSize = true, Anchor = AnchorStyles.Right, DialogResult = DialogResult.Cancel };

    IReadOnlyList<Conversation> _all = [];
    IReadOnlyList<Conversation> _shown = [];
    IReadOnlyList<ContinueTarget> _targets = [];
    int _loadId;
    bool _busy;
    bool _filling;
    bool _userPicked; // the user chose a "Continue as" entry: it stops following the selection

    public ConversationsForm(Func<Task<IReadOnlyList<Conversation>>> load, Func<IReadOnlyList<ProfileInfo>> profiles,
        Func<Conversation, ContinueTarget, Task<CopyOutcome>> continueAsync)
    {
        _load = load;
        _profiles = profiles;
        _continue = continueAsync;

        Text = "clauderoo — Continue a conversation";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Padding = new Padding(12);
        _icon = AppIcons.Load(AppIconState.Normal, SystemInformation.HighContrast);
        Icon = _icon;
        CancelButton = _close;

        _list.Columns.Add("When");
        _list.Columns.Add("Profile");
        _list.Columns.Add("Folder");
        _list.Columns.Add("First prompt");
        _list.AccessibleName = "Conversations";
        _search.AccessibleName = "Search conversations";
        _filter.AccessibleName = "Profile filter";
        _as.AccessibleName = "Continue as";
        _list.SizeChanged += (_, _) => FitColumns();

        _search.TextChanged += (_, _) => ApplyFilter();
        _filter.SelectedIndexChanged += (_, _) => { if (!_filling) ApplyFilter(); };
        _list.SelectedIndexChanged += (_, _) => OnSelection();
        _as.SelectionChangeCommitted += (_, _) => _userPicked = true;
        _list.DoubleClick += async (_, _) => await ContinueSelected();
        _go.Click += async (_, _) => await ContinueSelected();
        _close.Click += (_, _) => Close();

        var top = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        top.Controls.AddRange([_searchLabel, _search, _filterLabel, _filter]);

        var asRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
        asRow.Controls.AddRange([_asLabel, _as, _go]);
        var bottom = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(asRow, 0, 0);
        bottom.Controls.Add(_close, 1, 0);

        Control[] rows = [top, _list, _status, bottom];
        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = rows.Length, Dock = DockStyle.Fill, Margin = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows.Length; i++)
        {
            root.RowStyles.Add(rows[i] == _list ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            root.Controls.Add(rows[i], 0, i);
        }
        Controls.Add(root);

        // Literals above are logical (96 DPI) units.
        ScaleLiterals(this);
        _search.Width = LogicalToDeviceUnits(240);
        _filter.Width = LogicalToDeviceUnits(160);
        _as.Width = LogicalToDeviceUnits(200);

        ClientSize = LogicalToDeviceUnits(new Size(860, 480));
        MinimumSize = LogicalToDeviceUnits(new Size(600, 360));
        Resize += (_, _) => WrapStatus();
        WrapStatus();
        FillTargets(null);
        Shown += async (_, _) => await Reload();
    }

    Conversation? Selected => _list.SelectedIndices.Count == 1 ? _shown[_list.SelectedIndices[0]] : null;

    /// <summary>Shows the window, restoring and bringing it to the front, and re-reads the conversations.</summary>
    public async Task ShowAndActivate()
    {
        var wasVisible = Visible;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        if (wasVisible) await Reload(); // first time: Shown does it
    }

    async Task Reload()
    {
        var id = ++_loadId;
        SetStatus("Looking for conversations…", null);
        IReadOnlyList<Conversation> loaded;
        try { loaded = await _load(); } // on a background task; resumes on the UI thread
        catch (Exception e)
        {
            if (id == _loadId && !IsDisposed) SetStatus("Couldn't list conversations: " + e.Message, false);
            return;
        }
        if (id != _loadId || IsDisposed) return;
        _all = loaded;
        FillProfileFilter();
        FillTargets(Selected);
        ApplyFilter();
    }

    void FillProfileFilter()
    {
        var keep = _filter.SelectedItem as string;
        _filling = true;
        try
        {
            _filter.Items.Clear();
            _filter.Items.Add(AllProfiles);
            foreach (var name in _all.Select(c => c.Profile).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                _filter.Items.Add(name);
            _filter.SelectedItem = keep != null && _filter.Items.Contains(keep) ? keep : AllProfiles;
        }
        finally { _filling = false; }
    }

    // Remembers the chosen entry (when it's still offered); otherwise the default for the selected conversation.
    void FillTargets(Conversation? forConversation)
    {
        var previous = _as.SelectedItem as ContinueTarget;
        IReadOnlyList<ProfileInfo> profiles;
        try { profiles = _profiles(); }
        catch (Exception) { profiles = []; }
        _targets = ConversationPicker.Targets(profiles);
        _as.Items.Clear();
        foreach (var t in _targets) _as.Items.Add(t);
        _as.DisplayMember = nameof(ContinueTarget.Label);
        var pick = previous != null ? _targets.FirstOrDefault(t => t.Label == previous.Label) : null;
        pick ??= forConversation != null ? ConversationPicker.DefaultTarget(_targets, forConversation) : _targets.FirstOrDefault();
        if (pick != null) _as.SelectedItem = pick;
    }

    void ApplyFilter()
    {
        var profile = _filter.SelectedItem is string s && s != AllProfiles ? s : null;
        var keep = Selected?.FilePath;
        _shown = ConversationPicker.Filter(_all, _search.Text, profile);
        var now = DateTime.Now;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var c in _shown)
        {
            var row = new ListViewItem(ConversationPicker.WhenLabel(c.When.ToLocalTime().DateTime, now));
            row.SubItems.Add(ConversationPicker.ProfileLabel(c));
            row.SubItems.Add(ConversationPicker.FolderLabel(c));
            row.SubItems.Add(ConversationPicker.PromptLabel(c));
            _list.Items.Add(row);
        }
        var index = keep == null ? -1 : _shown.ToList().FindIndex(c => c.FilePath == keep);
        if (index >= 0)
        {
            _list.Items[index].Selected = true;
            _list.Items[index].EnsureVisible();
        }
        _list.EndUpdate();
        FitColumns();
        OnSelection();
        if (!_busy)
            SetStatus(_all.Count == 0 ? "No conversations found yet."
                : _shown.Count == 0 ? "Nothing matches your search."
                : _shown.Count == _all.Count ? $"{_all.Count} conversations."
                : $"{_shown.Count} of {_all.Count} conversations.", null);
    }

    void OnSelection()
    {
        _go.Enabled = !_busy && Selected != null && _targets.Count > 0;
        // Until the user chooses, "Continue as" follows the selection: another profile than the one it's in.
        if (!_userPicked && Selected is { } c && ConversationPicker.DefaultTarget(_targets, c) is { } pick)
            _as.SelectedItem = pick;
    }

    async Task ContinueSelected()
    {
        if (_busy || Selected is not { } c || _as.SelectedItem is not ContinueTarget target) return;
        _busy = true;
        _go.Enabled = false;
        UseWaitCursor = true;
        SetStatus($"Copying the conversation to {target.Label}…", null);
        try
        {
            var outcome = await _continue(c, target);
            SetStatus(ConversationPicker.ContinuedStatus(target, outcome), true);
        }
        catch (Exception e)
        {
            SetStatus(e.Message, false);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                OnSelection();
            }
        }
    }

    /// <summary>The line under the list; ok: true green, false red, null neutral.</summary>
    void SetStatus(string text, bool? ok)
    {
        if (IsDisposed) return;
        _status.Text = text;
        _status.ForeColor = ok switch
        {
            true => StatusColors.Ok,
            false => StatusColors.Fail,
            _ => SystemColors.ControlText,
        };
    }

    void FitColumns()
    {
        if (_list.Columns.Count < 4) return;
        var when = LogicalToDeviceUnits(120);
        var profile = LogicalToDeviceUnits(110);
        var folder = LogicalToDeviceUnits(220);
        _list.Columns[0].Width = when;
        _list.Columns[1].Width = profile;
        _list.Columns[2].Width = folder;
        _list.Columns[3].Width = Math.Max(LogicalToDeviceUnits(160), _list.ClientSize.Width - when - profile - folder);
    }

    void WrapStatus() => _status.MaximumSize = new Size(Math.Max(1, ClientSize.Width - Padding.Horizontal), 0);

    void ScaleLiterals(Control c)
    {
        Padding Scale(Padding p) => new(LogicalToDeviceUnits(p.Left), LogicalToDeviceUnits(p.Top),
            LogicalToDeviceUnits(p.Right), LogicalToDeviceUnits(p.Bottom));
        c.Margin = Scale(c.Margin);
        c.Padding = Scale(c.Padding);
        c.MinimumSize = LogicalToDeviceUnits(c.MinimumSize);
        foreach (Control child in c.Controls) ScaleLiterals(child);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }
}
