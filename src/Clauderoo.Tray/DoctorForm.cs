using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>
/// "Check this PC…": the same read-only checklist as `cr doctor` (Claude Code, Windows Terminal, gcloud, the install
/// folder and PATH, the MCP server, the home folder, the default profile, every profile's shared-folder links), with
/// each row filling in as its check finishes. It changes nothing.
/// </summary>
internal sealed class DoctorForm : Form
{
    private readonly ClauderooPaths _paths;
    private readonly IEnvStore _env;
    private readonly Icon _icon;
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    private readonly CheckListView _list = new() { Margin = new Padding(0) };
    private readonly Button _again = new() { Text = "&Check again", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
    private readonly Button _copy = new() { Text = "Cop&y results", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
    private readonly Button _close = new() { Text = "Close", AutoSize = true, Margin = new Padding(0, 8, 0, 0), DialogResult = DialogResult.Cancel };
    private CancellationTokenSource? _cts;
    private int _seq;

    public DoctorForm(ClauderooPaths paths, IEnvStore env)
    {
        _paths = paths;
        _env = env;
        Text = "clauderoo — Check this PC";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Padding = new Padding(12);
        _icon = AppIcons.Load(AppIconState.Normal, SystemInformation.HighContrast);
        Icon = _icon;
        CancelButton = _close;
        MinimumSize = new Size(LogicalToDeviceUnits(480), LogicalToDeviceUnits(320));
        Size = new Size(LogicalToDeviceUnits(560), LogicalToDeviceUnits(460));
        _list.AccessibleName = "Check results";

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, Margin = new Padding(0) };
        buttons.Controls.AddRange([_again, _copy, _close]);
        table.Controls.Add(_status, 0, 0);
        table.Controls.Add(_list, 0, 1);
        table.Controls.Add(buttons, 0, 2);
        Controls.Add(table);

        _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        table.SizeChanged += (_, _) => FitList(table);
        _again.Click += (_, _) => Start();
        _copy.Click += (_, _) => _list.CopyAll();
        _close.Click += (_, _) => Close();
        Shown += (_, _) => { FitList(table); Start(); };
    }

    private void FitList(TableLayoutPanel table)
    {
        _list.WrapTo(Math.Max(100, table.ClientSize.Width - _list.Margin.Horizontal));
        _list.MaxContentHeight = Math.Max(LogicalToDeviceUnits(120), table.ClientSize.Height - _status.Height - _again.Height - LogicalToDeviceUnits(40));
        _list.PerformLayout();
    }

    private void Start()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var seq = ++_seq;
        var model = new CheckProgressModel();
        _again.Enabled = false;
        _status.Text = "Checking…";
        var progress = new Progress(this, item =>
        {
            if (seq != _seq) return;
            model.Apply(item);
            _status.Text = model.StatusText;
            _list.SetItems(model.Rows, live: true);
        });
        _ = Task.Run(async () =>
        {
            IReadOnlyList<CheckItem>? items = null;
            string? error = null;
            try { items = await Doctor.RunAsync(_paths, _env, null, cts.Token, progress).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception e) { error = e.Message; }
            try
            {
                BeginInvoke(() =>
                {
                    if (seq != _seq) return;
                    _again.Enabled = true;
                    if (items == null) { _status.Text = error ?? "Cancelled."; return; }
                    var fails = items.Count(i => i.Status == CheckStatus.Fail);
                    var warns = items.Count(i => i.Status == CheckStatus.Warn);
                    _status.Text = fails > 0 ? $"{fails} problem{(fails == 1 ? "" : "s")} found."
                        : warns > 0 ? $"Works, with {warns} warning{(warns == 1 ? "" : "s")}."
                        : "Everything looks good.";
                    _list.SetItems(items);
                });
            }
            catch (InvalidOperationException) { /* window closed meanwhile */ }
        });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        _seq++;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _icon.Dispose(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }

    // Reports are posted through the window's own queue, in order, before the final result.
    private sealed class Progress(Control owner, Action<CheckItem> apply) : IProgress<CheckItem>
    {
        public void Report(CheckItem value)
        {
            try { owner.BeginInvoke(() => apply(value)); }
            catch (InvalidOperationException) { /* window closed meanwhile */ }
        }
    }
}
