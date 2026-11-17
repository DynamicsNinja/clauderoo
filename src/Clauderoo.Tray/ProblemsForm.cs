using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>What a Fix did: Ok (with a plain confirmation) or not (with what's still wrong).</summary>
internal sealed record FixOutcome(bool Ok, string Message);

/// <summary>
/// The current sync problems (kept by the tray until the next sync): a list of plain-language titles, one line saying
/// what the Fix button will do (or what to do instead), the original text behind "Show technical details".
/// </summary>
internal sealed class ProblemsForm : Form
{
    const int GlyphSize = 16;

    readonly Func<Problem, Task<FixOutcome>> _fix;
    readonly Action _openProfiles;
    readonly Icon _icon;
    readonly Font _bold;

    readonly Label _summary = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill, Margin = new Padding(0),
    };
    readonly FlowLayoutPanel _empty = new()
    {
        AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Fill,
        Margin = new Padding(0), Padding = new Padding(4, 12, 4, 12), Visible = false,
    };
    readonly PictureBox _emptyGlyph = new() { SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 8, 0) };
    readonly Label _emptyText = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label _explain = new() { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
    readonly Button _fixButton = new() { AutoSize = true, Margin = new Padding(0, 8, 8, 0), Visible = false };
    readonly Button _copy = new() { Text = "&Copy details", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
    readonly LinkLabel _toggle = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 8, 0, 0) };
    readonly Label _status = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 0), Visible = false };
    readonly Label _detailsHeading = new() { Text = "What clauderoo saw", AutoSize = true, Margin = new Padding(0, 12, 0, 4), Visible = false };
    readonly TextBox _details = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        MinimumSize = new Size(0, 72), Margin = new Padding(0), Visible = false,
    };
    readonly LinkLabel _manage = new() { Text = "Open Manage profiles", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Button _close = new() { Text = "Close", AutoSize = true, Anchor = AnchorStyles.Right, DialogResult = DialogResult.Cancel };
    readonly Panel _actions;
    readonly RowStyle _detailsRow = new(SizeType.AutoSize);

    IReadOnlyList<Problem> _problems = [];
    bool _showTechnical;
    bool _updating;
    bool _busy;

    public ProblemsForm(Func<Problem, Task<FixOutcome>> fix, Action openProfiles)
    {
        _fix = fix;
        _openProfiles = openProfiles;

        Text = "clauderoo — Problems";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Padding = new Padding(12);
        _icon = AppIcons.Load(AppIconState.Normal, SystemInformation.HighContrast);
        Icon = _icon;
        CancelButton = _close;

        _list.Columns.Add("Problem");
        _list.Columns.Add("Profile");
        _list.AccessibleName = "Problems";
        _details.AccessibleName = "What clauderoo saw";
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating) SetStatus(null, false);
            ShowSelected();
        };
        _list.SizeChanged += (_, _) => FitColumns();

        _empty.Controls.AddRange([_emptyGlyph, _emptyText]);

        _fixButton.Click += async (_, _) => await FixSelected();
        _copy.Click += (_, _) =>
        {
            if (Selected is { } p) Clipboard.SetText(CopyText(p));
        };
        _toggle.LinkClicked += (_, _) =>
        {
            _showTechnical = !_showTechnical;
            ShowSelected();
        };
        _manage.LinkClicked += (_, _) => _openProfiles();
        _close.Click += (_, _) => Close();

        var actionRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
        actionRow.Controls.AddRange([_fixButton, _copy, _toggle]);
        _actions = actionRow;

        var bottom = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_manage, 0, 0);
        bottom.Controls.Add(_close, 1, 0);

        // The list and the empty / can't-check state share one cell.
        var listHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), MinimumSize = new Size(420, 120) };
        listHost.Controls.Add(_list);
        listHost.Controls.Add(_empty);

        Control[] rows = [_summary, listHost, _explain, actionRow, _status, _detailsHeading, _details, bottom];
        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = rows.Length, Dock = DockStyle.Fill, Margin = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows.Length; i++)
        {
            var style = rows[i] == listHost ? new RowStyle(SizeType.Percent, 60)
                : rows[i] == _details ? _detailsRow
                : new RowStyle(SizeType.AutoSize);
            root.RowStyles.Add(style);
            root.Controls.Add(rows[i], 0, i);
        }
        Controls.Add(root);

        // Literals above are logical (96 DPI) units.
        ScaleLiterals(this);
        _detailsHeading.Font = _bold = new Font(Font, FontStyle.Bold);

        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = LogicalToDeviceUnits(new Size(GlyphSize, GlyphSize)) };
        images.Images.Add(Glyphs.Get(Glyph.Warning, GlyphSize, DeviceDpi, StatusColors.Warn)); // copied into the list
        images.Images.Add(Glyphs.Get(Glyph.Error, GlyphSize, DeviceDpi, StatusColors.Fail));
        _list.SmallImageList = images;
        Disposed += (_, _) => images.Dispose();

        ClientSize = LogicalToDeviceUnits(new Size(620, 420));
        MinimumSize = LogicalToDeviceUnits(new Size(480, 340));
        Resize += (_, _) => WrapLabels();
        WrapLabels();
        SetProblems([]);
    }

    Problem? Selected => _list.Visible && _list.SelectedIndices.Count == 1 ? _problems[_list.SelectedIndices[0]] : null;

    /// <summary>Shows <paramref name="problems"/>, keeping the selection on the same problem when it's still there.</summary>
    public void SetProblems(IReadOnlyList<Problem> problems)
    {
        var keep = Selected is { } sel ? TrayText.Key(sel) : null;
        _problems = problems;
        _updating = true;
        try
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var p in problems)
            {
                var row = new ListViewItem(p.Title, TrayText.IsSevere(p) ? 1 : 0);
                row.SubItems.Add(p.Profile);
                _list.Items.Add(row);
            }
            _list.EndUpdate();

            var any = problems.Count > 0;
            _summary.Text = any
                ? $"{TrayText.Problems(problems.Count)} from the last sync. Select one to see what clauderoo can do."
                : "clauderoo checks your profiles every time Claude's settings change.";
            ShowEmpty(!any, Glyph.Ok, StatusColors.Ok, "No problems — every profile is in sync.");

            if (any)
            {
                var index = keep == null ? 0 : Math.Max(0, problems.ToList().FindIndex(p => TrayText.Key(p) == keep));
                _list.Items[index].Selected = true;
                _list.Items[index].Focused = true;
            }
        }
        finally { _updating = false; }
        ShowSelected();
        FitColumns();
    }

    /// <summary>The settings can't be loaded, so there's nothing to list: say so instead of "No problems".</summary>
    public void ShowConfigError(string headline)
    {
        _problems = [];
        _updating = true;
        _list.Items.Clear();
        _updating = false;
        _summary.Text = headline + ".";
        SetStatus(null, null);
        ShowEmpty(true, Glyph.Error, StatusColors.Fail,
            "Problems can't be checked until this is sorted out. Click the clauderoo tray icon for options.");
        ShowSelected();
    }

    void ShowEmpty(bool empty, Glyph glyph, Color colour, string text)
    {
        _list.Visible = !empty;
        _empty.Visible = empty;
        _emptyGlyph.Image = Glyphs.Get(glyph, 24, DeviceDpi, colour);
        _emptyText.Text = text;
    }

    void ShowSelected()
    {
        var p = Selected;
        _explain.Visible = _actions.Visible = p != null;
        _explain.Text = p == null ? "" : TrayText.Explain(p);
        _fixButton.Visible = p?.Fix != null;
        if (p?.Fix is { } fix) _fixButton.Text = TrayText.FixLabel(fix);
        _fixButton.Enabled = _copy.Enabled = !_busy;
        _toggle.Text = _showTechnical ? "Hide technical details" : "Show technical details";
        var technical = p != null && _showTechnical;
        _detailsHeading.Visible = _details.Visible = technical;
        _detailsRow.SizeType = technical ? SizeType.Percent : SizeType.AutoSize;
        _detailsRow.Height = technical ? 40 : 0;
        _details.Text = p?.Detail ?? "";
    }

    async Task FixSelected()
    {
        if (Selected is not { } p || _busy) return;
        _busy = true;
        UseWaitCursor = true;
        SetStatus("Fixing…", null);
        ShowSelected();
        try
        {
            var outcome = await _fix(p);
            SetStatus(outcome.Message, outcome.Ok);
        }
        catch (Exception e)
        {
            SetStatus("Couldn't fix it: " + e.Message, false);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            if (!IsDisposed) ShowSelected();
        }
    }

    /// <summary>The result line under the buttons; null hides it. ok: true green, false red, null neutral.</summary>
    void SetStatus(string? text, bool? ok)
    {
        if (IsDisposed) return;
        _status.Visible = text != null;
        _status.Text = text ?? "";
        _status.ForeColor = ok switch
        {
            true => StatusColors.Ok,
            false => StatusColors.Fail,
            _ => SystemColors.ControlText,
        };
    }

    static string CopyText(Problem p) =>
        p.Title + (p.Profile.Length > 0 ? $"\r\nProfile: {p.Profile}" : "") + "\r\nWhat clauderoo saw:\r\n" + p.Detail;

    void FitColumns()
    {
        if (_list.Columns.Count < 2) return;
        var profile = LogicalToDeviceUnits(120);
        _list.Columns[1].Width = profile;
        _list.Columns[0].Width = Math.Max(LogicalToDeviceUnits(120), _list.ClientSize.Width - profile);
    }

    void WrapLabels()
    {
        var width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
        foreach (var l in new[] { _summary, _explain, _status }) l.MaximumSize = new Size(width, 0);
        _emptyText.MaximumSize = new Size(Math.Max(1, width - LogicalToDeviceUnits(48)), 0);
    }

    void ScaleLiterals(Control c)
    {
        Padding Scale(Padding p) => new(LogicalToDeviceUnits(p.Left), LogicalToDeviceUnits(p.Top),
            LogicalToDeviceUnits(p.Right), LogicalToDeviceUnits(p.Bottom));
        c.Margin = Scale(c.Margin);
        c.Padding = Scale(c.Padding);
        c.MinimumSize = LogicalToDeviceUnits(c.MinimumSize);
        foreach (Control child in c.Controls) ScaleLiterals(child);
    }

    /// <summary>Shows the window, restoring and bringing it to the front.</summary>
    public void ShowAndActivate()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _bold.Dispose();
        }
        base.Dispose(disposing);
    }
}
