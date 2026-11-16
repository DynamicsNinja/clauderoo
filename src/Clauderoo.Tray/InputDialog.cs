namespace Clauderoo.Tray;

sealed class InputDialog : Form
{
    private readonly TextBox _box = new();

    private InputDialog(string title, string prompt)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(320, 100);

        var label = new Label { Text = prompt, AutoSize = true, Location = new Point(12, 12) };
        _box.Location = new Point(12, 36);
        _box.Width = 296;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(152, 66) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(233, 66) };
        Controls.AddRange([label, _box, ok, cancel]);
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
