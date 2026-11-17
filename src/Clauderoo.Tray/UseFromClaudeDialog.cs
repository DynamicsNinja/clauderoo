using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>
/// "Use from Claude…": shows the one-time `claude mcp add` command (the same text `cr mcp install` prints) with a Copy
/// button. It only shows and copies: it never runs the command and never writes ~/.claude.json.
/// Layout is driven by auto-sizing panels (no fixed pixel positions), so it fits at any display scaling.
/// </summary>
sealed class UseFromClaudeDialog : Form
{
    private readonly TextBox _command;
    private readonly Label _explanation;
    private readonly GlyphLabel _status = new() { GlyphSize = 14 };
    private readonly Func<string, bool> _copyToClipboard;
    private readonly Font _mono;

    /// <param name="command">The command text (<see cref="McpRegistration.Command()"/>).</param>
    /// <param name="copyToClipboard">Puts text on the clipboard; true when it worked. Tests inject it.</param>
    internal UseFromClaudeDialog(string command, Func<string, bool>? copyToClipboard = null)
    {
        _copyToClipboard = copyToClipboard ?? SetClipboard;
        Text = "Use clauderoo from Claude";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Scaled(12, 12, 12, 12);

        var width = LogicalToDeviceUnits(480);
        _explanation = new Label
        {
            Text = McpRegistration.Explanation, AutoSize = true, MaximumSize = new Size(width, 0),
            Margin = Scaled(0, 0, 0, 6), UseMnemonic = false,
        };
        // Read-only and wrapped, so the whole command is visible however long the install path is.
        _mono = new Font("Consolas", Font.SizeInPoints);
        _command = new TextBox
        {
            Text = command, ReadOnly = true, Multiline = true, WordWrap = true, Dock = DockStyle.Fill,
            Margin = Scaled(0, 0, 0, 3), AccessibleName = "Command to run", Font = _mono, BackColor = SystemColors.Window,
        };
        var textHeight = TextRenderer.MeasureText(command, _mono, new Size(width - LogicalToDeviceUnits(10), 0),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        _command.MinimumSize = new Size(width, textHeight + LogicalToDeviceUnits(10));
        _command.Height = _command.MinimumSize.Height;
        // One line is always reserved for "Copied", so the dialog doesn't jump.
        _status.Margin = Scaled(0, 0, 0, 12);
        _status.MinimumSize = new Size(0, TextRenderer.MeasureText("Ag", Font).Height);
        _status.WrapTo(width);
        var details = new Label
        {
            Text = "clauderoo adds the tools list_profiles, usage, set_default and delegate. Nothing is run or changed until you run the command yourself.",
            AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = StatusColors.Muted, Margin = Scaled(0, 0, 0, 12),
            UseMnemonic = false,
        };

        var copy = new Button { Text = "&Copy", AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(80), 0) };
        var close = new Button
        {
            Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true,
            MinimumSize = new Size(LogicalToDeviceUnits(80), 0), Margin = Scaled(6, 0, 0, 0),
        };
        copy.Click += (_, _) => Copy();
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill,
            Margin = Padding.Empty, WrapContents = false,
        };
        buttons.Controls.AddRange([close, copy]);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            MinimumSize = new Size(width, 0),
        };
        layout.Controls.Add(_explanation);
        layout.Controls.Add(_command);
        layout.Controls.Add(_status);
        layout.Controls.Add(details);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = copy;
        CancelButton = close;
        Shown += (_, _) => { _command.Focus(); _command.SelectAll(); };
    }

    internal string CommandText => _command.Text;
    internal string ExplanationText => _explanation.Text;
    internal string StatusText => _status.Text;

    internal void Copy()
    {
        bool ok;
        try { ok = _copyToClipboard(_command.Text); }
        catch (Exception) { ok = false; }
        if (ok) _status.Show(Glyph.Ok, StatusColors.Ok, "Copied. Paste it into a terminal and run it once.");
        else _status.Show(Glyph.Warning, StatusColors.Warn, "Couldn't copy to the clipboard. Select the command and copy it yourself.");
    }

    private static bool SetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException) { return false; } // clipboard busy
    }

    private Padding Scaled(int l, int t, int r, int b) =>
        new(LogicalToDeviceUnits(l), LogicalToDeviceUnits(t), LogicalToDeviceUnits(r), LogicalToDeviceUnits(b));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _mono.Dispose(); // our own font, after the box that used it; Glyphs images are cached, never disposed
    }

    public static void ShowFor(IWin32Window owner)
    {
        using var dlg = new UseFromClaudeDialog(McpRegistration.Command());
        dlg.ShowDialog(owner);
    }
}
