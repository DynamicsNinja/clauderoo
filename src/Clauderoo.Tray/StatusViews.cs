using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>A label that wraps to a width set from outside (TableLayoutPanel doesn't wrap AutoSize labels reliably).</summary>
internal interface IWrapping
{
    void WrapTo(int width);
}

/// <summary>A label whose preferred size is its text wrapped to the width given to <see cref="WrapTo"/> (0 high when empty).</summary>
internal class WrapLabel : Label, IWrapping
{
    private int _wrapWidth = 1;

    public WrapLabel()
    {
        AutoSize = false;
        UseMnemonic = false;
    }

    public void WrapTo(int width)
    {
        if (_wrapWidth == width) return;
        _wrapWidth = width;
        Refit();
    }

    protected void Refit() => Size = GetPreferredSize(Size.Empty);

    public override Size GetPreferredSize(Size proposedSize) =>
        new(_wrapWidth, Math.Max(MinimumSize.Height,
            Text.Length == 0 ? 0 : base.GetPreferredSize(new Size(_wrapWidth, 0)).Height));

    protected override void OnTextChanged(EventArgs e)
    {
        // Base first: it drops Label's cached preferred size, which still describes the old text.
        base.OnTextChanged(e);
        Refit();
    }
}

/// <summary>A <see cref="WrapLabel"/> with an optional status glyph drawn left of its first line.</summary>
internal sealed class GlyphLabel : WrapLabel
{
    private Glyph? _glyph;
    private Color _ink;

    /// <summary>Glyph size in logical (96 DPI) pixels.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int GlyphSize { get; init; } = 14;

    /// <summary>Sets glyph, colour (text and glyph) and text in one go; a null glyph leaves no gap.</summary>
    public void Show(Glyph? glyph, Color color, string text)
    {
        _glyph = glyph;
        _ink = color;
        ForeColor = color;
        UpdateIndent();
        Text = text;
        Refit();
        Invalidate();
    }

    /// <summary>Sets only the glyph (keeps text and text colour).</summary>
    public void SetGlyph(Glyph? glyph, Color ink)
    {
        _glyph = glyph;
        _ink = ink;
        UpdateIndent();
        Refit();
        Invalidate();
    }

    private void UpdateIndent() =>
        Padding = new Padding(_glyph == null ? 0 : LogicalToDeviceUnits(GlyphSize + 6), 0, 0, 0);

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateIndent();
        Refit();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_glyph is not { } g || Text.Length == 0) return;
        var bmp = Glyphs.Get(g, GlyphSize, DeviceDpi, _ink);
        var line = TextRenderer.MeasureText(e.Graphics, "Ag", Font, Size.Empty, TextFormatFlags.NoPrefix).Height;
        var y = Math.Max(0, (line - bmp.Height) / 2);
        e.Graphics.DrawImage(bmp, new Rectangle(0, y, bmp.Width, bmp.Height));
    }
}

/// <summary>The wrapping status line that can carry one link (e.g. "Removed work.  Undo").</summary>
internal sealed class WrapLinkLabel : LinkLabel, IWrapping
{
    private int _wrapWidth = 1;

    public WrapLinkLabel()
    {
        AutoSize = false;
        UseMnemonic = false;
        LinkArea = new LinkArea(0, 0);
    }

    /// <summary>Shows <paramref name="text"/>, followed by <paramref name="link"/> as a link when given.</summary>
    public void Show(string text, Color color, string? link = null)
    {
        LinkArea = new LinkArea(0, 0);
        ForeColor = color;
        if (link == null || text.Length == 0)
        {
            Text = text;
            return;
        }
        Text = text + "  " + link;
        LinkArea = new LinkArea(text.Length + 2, link.Length);
        Size = GetPreferredSize(Size.Empty); // the link changes the measured height
    }

    public void WrapTo(int width)
    {
        if (_wrapWidth == width) return;
        _wrapWidth = width;
        Size = GetPreferredSize(Size.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        Text.Length == 0
            ? new Size(_wrapWidth, 0)
            : new Size(_wrapWidth, base.GetPreferredSize(new Size(_wrapWidth, 0)).Height);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = GetPreferredSize(Size.Empty);
    }
}

/// <summary>
/// The setup checklist: one owner-drawn row per item (status glyph in colour, bold label, grey wrapped hint), failures
/// first. Its height follows the content up to <see cref="MaxContentHeight"/>, then it scrolls. Each row's accessible
/// text (and Copy) is the CLI line from <see cref="CheckItemFormatter"/>.
/// </summary>
internal sealed class CheckListView : ListBox
{
    private sealed record Row(CheckItem Item)
    {
        public override string ToString() => CheckItemFormatter.Format(Item);
    }

    private List<CheckItem> _items = [];
    private Font? _bold;
    private int _measuredWidth = -1;

    /// <summary>Height cap in device pixels.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int MaxContentHeight { get; set; } = 200;

    public CheckListView()
    {
        DrawMode = DrawMode.OwnerDrawVariable;
        IntegralHeight = false;
        BorderStyle = BorderStyle.FixedSingle;
        var menu = new ContextMenuStrip();
        menu.Items.Add("&Copy results", null, (_, _) => CopyAll());
        ContextMenuStrip = menu;
    }

    public int Count => _items.Count;

    public void SetItems(IEnumerable<CheckItem> items)
    {
        _items = items.OrderBy(i => Rank(i.Status)).ToList(); // stable: keeps the check order within a status
        Rebuild();
    }

    private static int Rank(CheckStatus s) => s switch
    {
        CheckStatus.Fail => 0,
        CheckStatus.Warn => 1,
        CheckStatus.Ok => 2,
        _ => 3,
    };

    public static (Glyph Glyph, Color Color) Look(CheckStatus s) => s switch
    {
        CheckStatus.Ok => (Glyph.Ok, StatusColors.Ok),
        CheckStatus.Warn => (Glyph.Warning, StatusColors.Warn),
        CheckStatus.Fail => (Glyph.Error, StatusColors.Fail),
        _ => (Glyph.Remove, StatusColors.Muted),
    };

    private Font Bold => _bold ??= new Font(Font, FontStyle.Bold);

    private void Rebuild()
    {
        _measuredWidth = Width;
        BeginUpdate();
        Items.Clear();
        foreach (var i in _items) Items.Add(new Row(i));
        EndUpdate();
        var h = 0;
        for (var i = 0; i < Items.Count; i++) h += GetItemHeight(i);
        h = Math.Min(h + 2 * SystemInformation.BorderSize.Height + 2, MaxContentHeight);
        MaximumSize = new Size(0, h);
        MinimumSize = new Size(0, h);
        Height = h;
    }

    // Geometry shared by measuring and drawing (device pixels).
    private int Pad => LogicalToDeviceUnits(4);
    private int GlyphPx => (int)Math.Round(16 * DeviceDpi / 96.0);
    private int TextLeft => Pad + GlyphPx + LogicalToDeviceUnits(6);
    private int TextWidth => Math.Max(20, ClientSize.Width - TextLeft - Pad - SystemInformation.VerticalScrollBarWidth);

    private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private (int Label, int Hint) Measure(CheckItem item)
    {
        var w = TextWidth;
        var label = TextRenderer.MeasureText(item.Label, Bold, new Size(w, int.MaxValue), Wrap).Height;
        var hint = string.IsNullOrEmpty(item.Hint) ? 0
            : TextRenderer.MeasureText(item.Hint, Font, new Size(w, int.MaxValue), Wrap).Height;
        return (Math.Max(label, GlyphPx), hint);
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _items.Count) return;
        var (label, hint) = Measure(_items[e.Index]);
        e.ItemHeight = Math.Min(255, 2 * Pad + label + hint);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _items.Count) return;
        var item = _items[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0 && Focused;
        var back = selected ? SystemColors.Highlight : SystemColors.Window;
        var fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
        var muted = selected ? SystemColors.HighlightText : SystemColors.GrayText;
        using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);

        var (glyph, color) = Look(item.Status);
        var bmp = Glyphs.Get(glyph, 16, DeviceDpi, selected ? SystemColors.HighlightText : color);
        var top = e.Bounds.Top + Pad;
        e.Graphics.DrawImage(bmp, new Rectangle(e.Bounds.Left + Pad, top, bmp.Width, bmp.Height));

        var (labelH, hintH) = Measure(item);
        var x = e.Bounds.Left + TextLeft;
        var lineH = TextRenderer.MeasureText(e.Graphics, "Ag", Bold, Size.Empty, TextFormatFlags.NoPadding).Height;
        var labelTop = top + Math.Max(0, (GlyphPx - lineH) / 2);
        TextRenderer.DrawText(e.Graphics, item.Label, Bold, new Rectangle(x, labelTop, TextWidth, labelH), fore, Wrap);
        if (hintH > 0)
            TextRenderer.DrawText(e.Graphics, item.Hint, Font, new Rectangle(x, top + labelH, TextWidth, hintH), muted, Wrap);
        if ((e.State & DrawItemState.Focus) != 0 && Focused) e.DrawFocusRectangle();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width != _measuredWidth && _items.Count > 0 && IsHandleCreated) Rebuild();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_items.Count > 0) BeginInvoke(Rebuild); // item heights measured before the handle existed are guesses
    }

    protected override void OnFontChanged(EventArgs e)
    {
        _bold?.Dispose();
        _bold = null;
        base.OnFontChanged(e);
        if (_items.Count > 0) Rebuild();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyData == (Keys.Control | Keys.C))
        {
            CopyAll();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void CopyAll()
    {
        if (_items.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, _items.Select(CheckItemFormatter.Format))); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: nothing to do */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bold?.Dispose();
            ContextMenuStrip?.Dispose();
        }
        base.Dispose(disposing);
    }
}
