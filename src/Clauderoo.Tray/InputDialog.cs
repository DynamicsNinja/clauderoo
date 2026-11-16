namespace Clauderoo.Tray;

// Layout is driven by auto-sizing panels (no fixed pixel positions), so it fits at any display scaling.
sealed class InputDialog : Form
{
    private readonly TextBox _box = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 12) };

    private InputDialog(string title, string prompt)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill,
            Margin = Padding.Empty, WrapContents = false,
        };
        buttons.Controls.AddRange([cancel, ok]);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            MinimumSize = new Size(LogicalToDeviceUnits(300), 0),
        };
        layout.Controls.Add(new Label { Text = prompt, AutoSize = true, Margin = Padding.Empty });
        layout.Controls.Add(_box);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    public static string? Ask(string title, string prompt)
    {
        using var dlg = new InputDialog(title, prompt);
        dlg.TopMost = true;
        return dlg.ShowDialog() == DialogResult.OK ? dlg._box.Text.Trim() : null;
    }
}
