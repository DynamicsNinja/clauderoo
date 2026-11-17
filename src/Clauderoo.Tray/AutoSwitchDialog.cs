using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>The Auto-switch dialog's order list. Pure.</summary>
internal static class AutoSwitchOrder
{
    /// <summary>
    /// The profiles the engine can switch to: those in <paramref name="order"/> first (ticked, in that order), then the
    /// rest (unticked) in list order. Names no longer in the list are dropped.
    /// </summary>
    public static List<(string Name, bool Checked)> Items(IReadOnlyList<string> order, IEnumerable<(string Name, string Provider)> profiles)
    {
        var all = profiles.Where(p => p.Provider is Providers.Login or Providers.Vertex or Providers.ApiKey).Select(p => p.Name).ToList();
        var listed = order.Where(all.Contains).Distinct().ToList();
        return [.. listed.Select(n => (n, true)), .. all.Where(n => !listed.Contains(n)).Select(n => (n, false))];
    }

    /// <summary>The ticked names, in order. None ticked is an empty order: every Claude-login profile, in list order.</summary>
    public static List<string> ToOrder(IEnumerable<(string Name, bool Checked)> items) =>
        items.Where(i => i.Checked).Select(i => i.Name).ToList();

    /// <summary>Moves the item at <paramref name="index"/> by <paramref name="delta"/>; returns its new index.</summary>
    public static int Move(List<(string Name, bool Checked)> items, int index, int delta)
    {
        var to = index + delta;
        if (index < 0 || index >= items.Count || to < 0 || to >= items.Count) return Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
        (items[index], items[to]) = (items[to], items[index]);
        return to;
    }
}

/// <summary>
/// Auto-switch settings: on/off, the threshold (6–100) and the order of profiles to switch to.
/// Layout is driven by auto-sizing panels (no fixed pixel positions), so it fits at any display scaling.
/// </summary>
sealed class AutoSwitchDialog : Form
{
    private const string OrderHint =
        "Tick the profiles to switch to, in order. With none ticked, every Claude-login profile is tried in list order. " +
        "Google Vertex AI and API-key profiles have no limits to check, so they're used only when ticked.";

    private readonly CheckBox _enabled = new() { Text = "Switch the default profile &automatically", AutoSize = true };
    private readonly NumericUpDown _threshold = new() { Minimum = 6, Maximum = 100 };
    private readonly CheckedListBox _order = new() { IntegralHeight = false, CheckOnClick = true };
    private readonly Button _up;
    private readonly Button _down;
    private readonly Dictionary<string, string> _providers;
    private List<(string Name, bool Checked)> _items;

    private AutoSwitchDialog(AutoSwitchSettings s, IReadOnlyList<(string Name, string Provider)> profiles)
    {
        _providers = profiles.ToDictionary(p => p.Name, p => p.Provider);
        _items = AutoSwitchOrder.Items(s.Order, profiles);

        Text = "Auto-switch";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Scaled(12, 12, 12, 12);

        var width = LogicalToDeviceUnits(360);
        Button Btn(string text) => new()
        {
            Text = text, AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(80), 0), Margin = Scaled(0, 0, 0, 6),
        };

        _enabled.Checked = s.Enabled;
        _enabled.Margin = Scaled(0, 0, 0, 8);

        _threshold.Value = Math.Clamp(s.Threshold, 6, 100);
        _threshold.Width = LogicalToDeviceUnits(64);
        _threshold.Margin = Scaled(0, 0, 6, 0);
        _threshold.AccessibleName = "Switch at, percent of a limit";
        var thresholdRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = Scaled(0, 0, 0, 10), Padding = Padding.Empty,
        };
        thresholdRow.Controls.Add(new Label { Text = "Switch &at:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Scaled(0, 3, 6, 0) });
        thresholdRow.Controls.Add(_threshold);
        thresholdRow.Controls.Add(new Label
        {
            Text = "% of the 5-hour or 7-day limit", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Scaled(0, 3, 0, 0),
        });

        var orderLabel = new Label { Text = "&Order:", AutoSize = true, Margin = Padding.Empty };
        var hint = new Label
        {
            Text = OrderHint, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = SystemColors.GrayText,
            Margin = Scaled(0, 2, 0, 4), UseMnemonic = false,
        };

        _order.AccessibleName = "Order";
        _order.Margin = Scaled(0, 0, 6, 0);
        var rows = Math.Clamp(_items.Count, 4, 8);
        _order.Size = new Size(width - LogicalToDeviceUnits(92), _order.ItemHeight * rows + LogicalToDeviceUnits(6));

        _up = Btn("Move &up");
        _down = Btn("Move &down");
        var moveButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        moveButtons.Controls.AddRange([_up, _down]);
        var orderRow = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Scaled(0, 0, 0, 12),
            Padding = Padding.Empty,
        };
        orderRow.Controls.Add(_order, 0, 0);
        orderRow.Controls.Add(moveButtons, 1, 0);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(80), 0) };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true,
            MinimumSize = new Size(LogicalToDeviceUnits(80), 0), Margin = Scaled(6, 0, 0, 0),
        };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill,
            Margin = Padding.Empty, WrapContents = false,
        };
        buttons.Controls.AddRange([cancel, ok]);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            MinimumSize = new Size(width, 0),
        };
        layout.Controls.Add(_enabled);
        layout.Controls.Add(thresholdRow);
        layout.Controls.Add(orderLabel);
        layout.Controls.Add(hint);
        layout.Controls.Add(orderRow);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;

        Fill(0);
        _order.SelectedIndexChanged += (_, _) => UpdateMoveButtons();
        // The new state isn't applied yet while ItemCheck runs: keep the model in step with it.
        _order.ItemCheck += (_, e) =>
        {
            if (e.Index >= 0 && e.Index < _items.Count) _items[e.Index] = (_items[e.Index].Name, e.NewValue == CheckState.Checked);
        };
        _up.Click += (_, _) => MoveSelected(-1);
        _down.Click += (_, _) => MoveSelected(+1);
    }

    private Padding Scaled(int l, int t, int r, int b) =>
        new(LogicalToDeviceUnits(l), LogicalToDeviceUnits(t), LogicalToDeviceUnits(r), LogicalToDeviceUnits(b));

    private string Display(string name) =>
        name + " — " + TrayText.ProviderName(_providers.GetValueOrDefault(name, Providers.Login));

    private void Fill(int select)
    {
        _order.BeginUpdate();
        _order.Items.Clear();
        foreach (var (name, on) in _items) _order.Items.Add(Display(name), on);
        if (_items.Count > 0) _order.SelectedIndex = Math.Clamp(select, 0, _items.Count - 1);
        _order.EndUpdate();
        UpdateMoveButtons();
    }

    private void MoveSelected(int delta)
    {
        var at = _order.SelectedIndex;
        if (at < 0) return;
        var to = AutoSwitchOrder.Move(_items, at, delta);
        Fill(to);
        _order.Focus();
    }

    private void UpdateMoveButtons()
    {
        var at = _order.SelectedIndex;
        _up.Enabled = at > 0;
        _down.Enabled = at >= 0 && at < _items.Count - 1;
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>; the edited settings, or null when cancelled.</summary>
    public static AutoSwitchSettings? Edit(IWin32Window owner, AutoSwitchSettings current, IReadOnlyList<(string Name, string Provider)> profiles)
    {
        using var dlg = new AutoSwitchDialog(current, profiles);
        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
        return new AutoSwitchSettings
        {
            Enabled = dlg._enabled.Checked,
            Threshold = (int)dlg._threshold.Value,
            Order = AutoSwitchOrder.ToOrder(dlg._items),
        };
    }
}
