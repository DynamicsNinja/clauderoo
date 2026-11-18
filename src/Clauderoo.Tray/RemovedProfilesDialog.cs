using System.Globalization;
using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>The dialog's text. Pure.</summary>
internal static class RemovedText
{
    public static string When(RemovedCopy c) => c.RemovedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string Count(int n) => n == 1 ? "1 removed copy" : $"{n} removed copies";

    public static string Summary(IReadOnlyList<RemovedCopy> copies) =>
        copies.Count == 0
            ? "No removed profiles."
            : $"{Count(copies.Count)}, {RemovedProfiles.FormatSize(copies.Sum(c => c.Size))} in all.";

    public static string ConfirmDelete(IReadOnlyList<RemovedCopy> copies)
    {
        var nl = Environment.NewLine;
        var shown = copies.Take(8).Select(c => $"  {c.Name}, removed {When(c)} ({RemovedProfiles.FormatSize(c.Size)})");
        var more = copies.Count > 8 ? $"{nl}  …and {copies.Count - 8} more" : "";
        return $"Permanently delete {Count(copies.Count)} ({RemovedProfiles.FormatSize(copies.Sum(c => c.Size))})?{nl}{nl}" +
               string.Join(nl, shown) + more + $"{nl}{nl}" +
               "Their logins and conversations are deleted too. This can't be undone. Folders shared from your home " +
               "profile aren't touched.";
    }

    public static string Purged(PurgeResult r) =>
        $"Deleted {Count(r.Deleted.Count)}, freed {RemovedProfiles.FormatSize(r.Freed)}.";
}

/// <summary>
/// Lists the removed copies in profiles\_removed, with Restore (the caller restores the chosen copy once the dialog
/// closes) and Delete permanently (junction-safe, see <see cref="ProfileManager.Purge"/>).
/// </summary>
sealed class RemovedProfilesDialog : Form
{
    private readonly Func<IReadOnlyList<RemovedCopy>> _list;
    private readonly Func<IReadOnlyList<RemovedCopy>, Task<PurgeResult>> _purge;
    private readonly ListView _view = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, Dock = DockStyle.Fill,
        AccessibleName = "Removed profiles",
    };
    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 6), UseMnemonic = false };
    private readonly Button _restore;
    private readonly Button _delete;
    private readonly Button _close;
    private IReadOnlyList<RemovedCopy> _copies = [];
    private bool _busy;

    /// <summary>The copy the user chose to restore; the caller restores it.</summary>
    public RemovedCopy? ToRestore { get; private set; }

    private RemovedProfilesDialog(Func<IReadOnlyList<RemovedCopy>> list, Func<IReadOnlyList<RemovedCopy>, Task<PurgeResult>> purge)
    {
        _list = list;
        _purge = purge;

        Text = "Removed profiles";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        Padding = Scaled(12, 12, 12, 12);
        ClientSize = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(320));
        MinimumSize = new Size(LogicalToDeviceUnits(420), LogicalToDeviceUnits(260));

        Button Btn(string text) => new()
        {
            Text = text, AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(80), 0), Margin = Scaled(0, 0, 6, 0),
        };
        _restore = Btn("&Restore");
        _delete = Btn("&Delete permanently…");
        _close = Btn("Close");
        _close.DialogResult = DialogResult.Cancel;
        _close.Margin = Padding.Empty;

        _view.Columns.Add("Profile", LogicalToDeviceUnits(150));
        _view.Columns.Add("Removed", LogicalToDeviceUnits(140));
        _view.Columns.Add("Size", LogicalToDeviceUnits(90), HorizontalAlignment.Right);
        _view.Columns.Add("", LogicalToDeviceUnits(110));

        var hint = new Label
        {
            Text = "Removed profiles are kept here until you delete them. Restore brings one back with its settings; " +
                   "Delete permanently frees the space.",
            AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(496), 0), ForeColor = SystemColors.GrayText,
            Margin = Scaled(0, 0, 0, 6), UseMnemonic = false,
        };

        var left = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        left.Controls.AddRange([_restore, _delete]);
        var buttons = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(left, 0, 0);
        buttons.Controls.Add(_close, 1, 0);

        var layout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(_view, 0, 1);
        layout.Controls.Add(_summary, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        CancelButton = _close;

        _view.SelectedIndexChanged += (_, _) => UpdateButtons();
        _view.DoubleClick += (_, _) => RestoreSelected();
        _restore.Click += (_, _) => RestoreSelected();
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; }; // a delete is running
        Shown += async (_, _) => await LoadAsync();
        UpdateButtons();
    }

    private Padding Scaled(int l, int t, int r, int b) =>
        new(LogicalToDeviceUnits(l), LogicalToDeviceUnits(t), LogicalToDeviceUnits(r), LogicalToDeviceUnits(b));

    private List<RemovedCopy> Selected() =>
        _view.SelectedItems.Cast<ListViewItem>().Select(i => (RemovedCopy)i.Tag!).ToList();

    private void UpdateButtons()
    {
        var sel = Selected();
        _restore.Enabled = !_busy && sel.Count == 1 && sel[0].CanRestore;
        _delete.Enabled = !_busy && sel.Count > 0;
    }

    private async Task LoadAsync(string? message = null)
    {
        _summary.Text = "Reading removed profiles…";
        IReadOnlyList<RemovedCopy> copies;
        try { copies = await Task.Run(_list); } // sizes walk the folders: off the UI thread
        catch (Exception e)
        {
            if (!IsDisposed) _summary.Text = $"Couldn't read the removed profiles: {e.Message}";
            return;
        }
        if (IsDisposed) return;
        _copies = copies;
        _view.BeginUpdate();
        _view.Items.Clear();
        foreach (var c in copies)
            _view.Items.Add(new ListViewItem([
                c.Name, RemovedText.When(c), RemovedProfiles.FormatSize(c.Size),
                !c.CanRestore ? "isolated folder only" : c.IsolatedDir != null ? "with isolated folder" : "",
            ]) { Tag = c });
        _view.EndUpdate();
        _summary.Text = message == null ? RemovedText.Summary(copies) : message + "  " + RemovedText.Summary(copies);
        UpdateButtons();
    }

    private void RestoreSelected()
    {
        var sel = Selected();
        if (_busy || sel.Count != 1 || !sel[0].CanRestore) return;
        ToRestore = sel[0];
        DialogResult = DialogResult.OK;
    }

    private async Task DeleteSelectedAsync()
    {
        var sel = Selected();
        if (_busy || sel.Count == 0) return;
        if (MessageBox.Show(this, RemovedText.ConfirmDelete(sel), "Delete permanently", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _busy = true;
        UpdateButtons();
        _summary.Text = "Deleting…";
        PurgeResult result;
        try { result = await _purge(sel); }
        catch (Exception e)
        {
            _busy = false;
            if (IsDisposed) return;
            MessageBox.Show(this, e.Message, "clauderoo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            await LoadAsync();
            return;
        }
        _busy = false;
        if (IsDisposed) return;
        if (result.Failed.Count > 0)
            MessageBox.Show(this, string.Join(Environment.NewLine, result.Failed.Select(f => f.Message)), "clauderoo",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        await LoadAsync(RemovedText.Purged(result));
    }

    /// <summary>
    /// Shows the dialog over <paramref name="owner"/>. <paramref name="list"/> runs on a worker thread;
    /// <paramref name="purge"/> must not block the UI thread. Returns the copy to restore, or null.
    /// </summary>
    public static RemovedCopy? Pick(IWin32Window owner, Func<IReadOnlyList<RemovedCopy>> list,
        Func<IReadOnlyList<RemovedCopy>, Task<PurgeResult>> purge)
    {
        using var dlg = new RemovedProfilesDialog(list, purge);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.ToRestore : null;
    }
}
