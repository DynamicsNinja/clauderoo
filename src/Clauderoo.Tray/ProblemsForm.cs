using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>
/// The current sync problems (kept by the tray until the next sync): a list of plain-language titles, the original
/// text on demand, and a Fix button when clauderoo can fix the problem itself.
/// </summary>
internal sealed class ProblemsForm : Form
{
    const int GlyphSize = 16;

    readonly Action<Problem> _fix;
    readonly Action _openProfiles;
    readonly Icon _icon;

    readonly Label _summary = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill, MinimumSize = new Size(420, 120),
        Margin = new Padding(0),
    };
    readonly FlowLayoutPanel _empty = new()
    {
        AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Fill,
        Margin = new Padding(0), Padding = new Padding(4, 12, 4, 12), Visible = false,
    };
    readonly Label _detailsHeading = new() { Text = "Details", AutoSize = true, Margin = new Padding(0, 12, 0, 4) };
    readonly TextBox _details = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        MinimumSize = new Size(0, 96), Margin = new Padding(0), TabStop = true,
    };
    readonly Button _fixButton = new() { AutoSize = true, Margin = new Padding(0, 8, 8, 0), Visible = false };
    readonly Button _copy = new() { Text = "&Copy details", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
    readonly Label _noFix = new()
    {
        Text = "clauderoo can't fix this one by itself. Copy the details and send them to whoever helps you with clauderoo.",
        AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 12, 0, 0), Visible = false,
    };
    readonly LinkLabel _manage = new() { Text = "Open Manage profiles", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Button _close = new() { Text = "Close", AutoSize = true, Anchor = AnchorStyles.Right, DialogResult = DialogResult.Cancel };

    readonly RowStyle _detailsRow = new(SizeType.Percent, 45);
    readonly Font _bold;

    IReadOnlyList<Problem> _problems = [];

    public ProblemsForm(Action<Problem> fix, Action openProfiles)
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
        _details.AccessibleName = "Details";
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.SizeChanged += (_, _) => FitColumns();

        var ok = new PictureBox { SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 8, 0) };
        var emptyText = new Label { Text = "No problems — every profile is in sync.", AutoSize = true, Anchor = AnchorStyles.Left };
        _empty.Controls.AddRange([ok, emptyText]);

        _fixButton.Click += (_, _) => FixSelected();
        _copy.Click += (_, _) =>
        {
            if (Selected is { } p) Clipboard.SetText(CopyText(p));
        };
        _manage.LinkClicked += (_, _) => _openProfiles();
        _close.Click += (_, _) => Close();

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
        actions.Controls.AddRange([_fixButton, _copy]);

        var bottom = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_manage, 0, 0);
        bottom.Controls.Add(_close, 1, 0);

        // The list and the empty state share one cell.
        var listHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), MinimumSize = new Size(420, 120) };
        listHost.Controls.Add(_list);
        listHost.Controls.Add(_empty);

        Control[] rows = [_summary, listHost, _detailsHeading, _details, actions, _noFix, bottom];
        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = rows.Length, Dock = DockStyle.Fill, Margin = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows.Length; i++)
        {
            var style = rows[i] == listHost ? new RowStyle(SizeType.Percent, 55)
                : rows[i] == _details ? _detailsRow
                : new RowStyle(SizeType.AutoSize);
            root.RowStyles.Add(style);
            root.Controls.Add(rows[i], 0, i);
        }
        Controls.Add(root);

        // Literals above are logical (96 DPI) units.
        ScaleLiterals(this);
        _detailsHeading.Font = _bold = new Font(Font, FontStyle.Bold);
        ok.Image = Glyphs.Get(Glyph.Ok, 24, DeviceDpi, StatusColors.Ok);

        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = LogicalToDeviceUnits(new Size(GlyphSize, GlyphSize)) };
        images.Images.Add(Glyphs.Get(Glyph.Warning, GlyphSize, DeviceDpi, StatusColors.Warn)); // copied into the list
        images.Images.Add(Glyphs.Get(Glyph.Error, GlyphSize, DeviceDpi, StatusColors.Fail));
        _list.SmallImageList = images;
        Disposed += (_, _) => images.Dispose();

        ClientSize = LogicalToDeviceUnits(new Size(620, 440));
        MinimumSize = LogicalToDeviceUnits(new Size(480, 360));
        Resize += (_, _) => WrapLabels();
        WrapLabels();
        SetProblems([]);
    }

    Problem? Selected => _list.SelectedIndices.Count == 1 ? _problems[_list.SelectedIndices[0]] : null;

    /// <summary>Shows <paramref name="problems"/>, keeping the selection on the same problem when it's still there.</summary>
    public void SetProblems(IReadOnlyList<Problem> problems)
    {
        var keep = Selected?.Detail;
        _problems = problems;
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
            ? $"{TrayText.Problems(problems.Count)} from the last sync. Select one to see what happened."
            : "clauderoo checks your profiles every time Claude's settings change.";
        _list.Visible = any;
        _empty.Visible = !any;
        _detailsHeading.Visible = _details.Visible = _copy.Visible = any;
        // Empty: the "No problems" cell takes the room the details would have used.
        _detailsRow.SizeType = any ? SizeType.Percent : SizeType.AutoSize;

        if (any)
        {
            var index = keep == null ? 0 : Math.Max(0, problems.ToList().FindIndex(p => p.Detail == keep));
            _list.Items[index].Selected = true;
            _list.Items[index].Focused = true;
        }
        ShowSelected();
        FitColumns();
    }

    void ShowSelected()
    {
        var p = Selected;
        _details.Text = p == null ? "" : DetailText(p);
        _fixButton.Visible = p?.Fix != null;
        if (p?.Fix is { } fix) _fixButton.Text = TrayText.FixLabel(fix);
        _noFix.Visible = p != null && p.Fix == null;
        _copy.Enabled = p != null;
    }

    void FixSelected()
    {
        if (Selected is not { } p) return;
        UseWaitCursor = true;
        _fixButton.Enabled = false;
        try
        {
            _fix(p);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "clauderoo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
            _fixButton.Enabled = true;
        }
    }

    static string DetailText(Problem p) =>
        p.Title + (p.Profile.Length > 0 ? $"\r\n\r\nProfile: {p.Profile}" : "") + "\r\n\r\nWhat clauderoo saw:\r\n" + p.Detail;

    static string CopyText(Problem p) => DetailText(p).Replace("\r\n\r\n", "\r\n");

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
        foreach (var l in new[] { _summary, _noFix }) l.MaximumSize = new Size(width, 0);
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
