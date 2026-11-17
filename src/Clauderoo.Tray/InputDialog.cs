using Clauderoo.Core;

namespace Clauderoo.Tray;

// Layout is driven by auto-sizing panels (no fixed pixel positions), so it fits at any display scaling.
sealed class InputDialog : Form
{
    private readonly TextBox _box = new() { Dock = DockStyle.Fill };
    private readonly GlyphLabel _message = new() { GlyphSize = 14 };
    private readonly Button _ok;
    private readonly Func<string, string?>? _validate;
    private readonly string _hint;

    private InputDialog(string title, string prompt, Func<string, string?>? validate, string? hint, bool lowercase)
    {
        _validate = validate;
        _hint = hint ?? "";
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Scaled(12, 12, 12, 12);

        var width = LogicalToDeviceUnits(320);
        _ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(80), 0) };
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
        buttons.Controls.AddRange([cancel, _ok]);

        _box.Margin = Scaled(0, 6, 0, 3);
        _box.CharacterCasing = lowercase ? CharacterCasing.Lower : CharacterCasing.Normal;
        _box.AccessibleName = prompt;
        // One line is always reserved for the hint or the error, so the dialog doesn't jump while typing.
        _message.Margin = Scaled(0, 0, 0, 12);
        _message.MinimumSize = new Size(0, TextRenderer.MeasureText("Ag", Font).Height);
        _message.WrapTo(width);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            MinimumSize = new Size(width, 0),
        };
        layout.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(width, 0), Margin = Padding.Empty, UseMnemonic = false });
        layout.Controls.Add(_box);
        layout.Controls.Add(_message);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = _ok;
        CancelButton = cancel;
        _box.TextChanged += (_, _) => CheckInput();
    }

    private Padding Scaled(int l, int t, int r, int b) =>
        new(LogicalToDeviceUnits(l), LogicalToDeviceUnits(t), LogicalToDeviceUnits(r), LogicalToDeviceUnits(b));

    /// <summary>The trimmed input, or null when it can't be accepted yet. Updates the message line and OK.</summary>
    private string? CheckInput()
    {
        var text = _box.Text.Trim();
        var error = text.Length == 0 ? "" : _validate?.Invoke(text);
        if (string.IsNullOrEmpty(error)) _message.Show(null, StatusColors.Muted, _hint);
        else _message.Show(Glyph.Error, StatusColors.Fail, error);
        _ok.Enabled = error == null;
        return error == null ? text : null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Belt and braces: OK is disabled while the input is invalid; never close with a bad value.
        if (DialogResult == DialogResult.OK && CheckInput() == null) e.Cancel = true;
        base.OnFormClosing(e);
    }

    /// <param name="owner">Window to stack above (e.g. the Profiles window); null from the tray menu.</param>
    /// <param name="initial">Text to prefill (selected, so typing replaces it).</param>
    /// <param name="validate">
    /// Checks the trimmed input as the user types: null = acceptable; "" = not acceptable, nothing to say (e.g. unchanged);
    /// otherwise the message shown under the box. OK stays disabled until the input is acceptable. Empty input is never.
    /// </param>
    /// <param name="hint">Grey help line shown under the box while there is no error.</param>
    /// <param name="lowercase">Lowercases as the user types.</param>
    public static string? Ask(string title, string prompt, IWin32Window? owner = null, string? initial = null,
        Func<string, string?>? validate = null, string? hint = null, bool lowercase = false)
    {
        using var dlg = new InputDialog(title, prompt, validate, hint, lowercase);
        if (!string.IsNullOrEmpty(initial))
        {
            dlg._box.Text = initial;
            dlg._box.SelectAll();
        }
        dlg.CheckInput();
        if (owner == null)
        {
            dlg.TopMost = true;
            return dlg.ShowDialog() == DialogResult.OK ? dlg._box.Text.Trim() : null;
        }
        dlg.StartPosition = FormStartPosition.CenterParent;
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._box.Text.Trim() : null;
    }

    /// <summary>Help line for <see cref="ProfileNameRule"/>.</summary>
    public const string ProfileNameHint = "Lowercase letters, digits and -, e.g. work or client-a.";

    /// <summary>
    /// The profile-name rules for <see cref="Ask"/>: a-z, 0-9 and -; not a name in <paramref name="existing"/>; not a
    /// reserved folder name (profiles\_removed). Entering <paramref name="current"/> again is "unchanged" (OK disabled, no message).
    /// </summary>
    public static Func<string, string?> ProfileNameRule(IEnumerable<string> existing, string? current = null)
    {
        var names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return text =>
        {
            if (current != null && string.Equals(text, current, StringComparison.OrdinalIgnoreCase)) return "";
            if (text.StartsWith('_')) return "Names starting with _ are reserved.";
            try { ProfileName.Validate(text); }
            catch (ClauderooException) { return "Use only lowercase letters, digits and - (no spaces)."; }
            if (names.Contains(text)) return $"There is already a profile named '{text}'.";
            return null;
        };
    }
}
