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

    /// <summary>
    /// Shows <paramref name="text"/>, followed by <paramref name="link"/> as a link when given (after
    /// <paramref name="separator"/>: two spaces, or a new line).
    /// </summary>
    public void Show(string text, Color color, string? link = null, string separator = "  ")
    {
        LinkArea = new LinkArea(0, 0);
        ForeColor = color;
        // LinkLabel's link positions count "\r\n" as one character: use "\n" so the indices below match.
        text = text.Replace("\r\n", "\n");
        separator = separator.Replace("\r\n", "\n");
        if (link == null || text.Length == 0)
        {
            Text = text;
            return;
        }
        Text = text + separator + link;
        LinkArea = new LinkArea(text.Length + separator.Length, link.Length);
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
/// first, passed checks folded into one "N checks passed" row that expands on click, and the raw technical detail of a
/// row behind its own "Show details" link. Rows are as tall as their wrapped text. The control's height follows the
/// content up to <see cref="MaxContentHeight"/>, then it scrolls. Each row's accessible name (and Copy) is the CLI line
/// from <see cref="CheckItemFormatter"/>.
/// </summary>
internal sealed class CheckListView : Control, IWrapping
{
    /// <summary>Takes the width of its column (set by the owner, like the wrapping labels).</summary>
    public void WrapTo(int width)
    {
        if (Width != width) Width = width;
    }

    private int _desiredHeight;

    // The layout asks for this: the column's width (set through WrapTo) and the content height, capped.
    public override Size GetPreferredSize(Size proposedSize) => new(Width, _desiredHeight);

    private sealed class Row
    {
        public CheckItem? Item;        // null: the "N checks passed" summary row
        public int OkCount;
        public bool DetailOpen;
        public Rectangle Bounds;       // in content coordinates (before scrolling)
        public Rectangle Toggle;       // the clickable part: the summary row, or the details link
        public int LabelH, HintH, LinkH, DetailH;
    }

    private const TextFormatFlags Wrap =
        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private readonly VScrollBar _bar = new() { Dock = DockStyle.Right, Visible = false };
    private readonly List<Row> _rows = [];
    private List<CheckItem> _items = [];
    private readonly HashSet<CheckItem> _openDetails = [];
    private bool _okExpanded;
    private int _contentHeight;
    private int _focus = -1;
    private Font? _bold;

    /// <summary>Height cap in device pixels.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int MaxContentHeight { get; set; } = 200;

    public CheckListView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Setup check results";
        Controls.Add(_bar);
        _bar.ValueChanged += (_, _) => Invalidate();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Cop&y results", null, (_, _) => CopyAll());
        ContextMenuStrip = menu;
    }

    public int Count => _items.Count;

    private bool _live;

    /// <param name="live">
    /// A check still running: rows stay in check order (each fills in where it is) and passed checks aren't folded.
    /// Otherwise problems come first and passed checks fold into one row.
    /// </param>
    public void SetItems(IEnumerable<CheckItem> items, bool live = false)
    {
        var list = items.ToList();
        if (live && _live) _items = list; // a row filled in: keep focus and open details
        else if (live != _live || !list.SequenceEqual(_items))
        {
            if (_live && !live) _bar.Value = 0; // the finished list starts at the top (problems first)
            _live = live;
            // Stable sort: keeps the check order within a status.
            _items = live ? list : list.OrderBy(i => Rank(i.Status)).ToList();
            _openDetails.Clear();
            _okExpanded = false;
            _focus = -1;
        }
        Relayout();
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
        CheckStatus.Pending or CheckStatus.Running => (Glyph.More, StatusColors.Muted),
        _ => (Glyph.Remove, StatusColors.Muted),
    };

    /// <summary>All results as CLI lines (with details), for the clipboard.</summary>
    public string ResultsText => string.Join(Environment.NewLine, _items.Select(CheckItemFormatter.Format));

    public void CopyAll()
    {
        if (_items.Count == 0) return;
        try { Clipboard.SetText(ResultsText); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: nothing to do */ }
    }

    private Font Bold => _bold ??= new Font(Font, FontStyle.Bold);
    private int Pad => LogicalToDeviceUnits(4);
    private int GlyphPx => (int)Math.Round(16 * DeviceDpi / 96.0);
    private int TextLeft => Pad + GlyphPx + LogicalToDeviceUnits(6);
    private int Border => 1;
    private int TextWidth => Math.Max(20, Width - 2 * Border - TextLeft - Pad - SystemInformation.VerticalScrollBarWidth);

    private static string SummaryText(int n, bool open) => (n == 1 ? "1 check passed" : $"{n} checks passed") + (open ? "  ▾" : "  ▸");
    private static string LinkText(bool open) => open ? "Hide details" : "Show details";

    // Rebuilds the visible rows and their heights for the current width, then sizes the control.
    private void Relayout()
    {
        _rows.Clear();
        if (_live)
        {
            foreach (var i in _items) _rows.Add(new Row { Item = i, DetailOpen = _openDetails.Contains(i) });
            LayoutRows();
            return;
        }
        var fails = _items.Where(i => i.Status is CheckStatus.Fail or CheckStatus.Warn).ToList();
        var oks = _items.Where(i => i.Status == CheckStatus.Ok).ToList();
        var rest = _items.Where(i => i.Status is not (CheckStatus.Fail or CheckStatus.Warn or CheckStatus.Ok)).ToList();
        foreach (var i in fails) _rows.Add(new Row { Item = i, DetailOpen = _openDetails.Contains(i) });
        if (oks.Count >= 2)
        {
            _rows.Add(new Row { OkCount = oks.Count });
            if (_okExpanded) foreach (var i in oks) _rows.Add(new Row { Item = i, DetailOpen = _openDetails.Contains(i) });
        }
        else foreach (var i in oks) _rows.Add(new Row { Item = i, DetailOpen = _openDetails.Contains(i) });
        foreach (var i in rest) _rows.Add(new Row { Item = i, DetailOpen = _openDetails.Contains(i) });
        LayoutRows();
    }

    // Measures the rows built by Relayout, then sizes the control.
    private void LayoutRows()
    {
        var w = TextWidth;
        var y = 0;
        var line = TextRenderer.MeasureText("Ag", Font, Size.Empty, TextFormatFlags.NoPadding).Height;
        foreach (var r in _rows)
        {
            if (r.Item is not { } item)
            {
                r.LabelH = Math.Max(GlyphPx, TextRenderer.MeasureText(SummaryText(r.OkCount, _okExpanded), Bold, new Size(w, int.MaxValue), Wrap).Height);
                r.Bounds = new Rectangle(0, y, Width, 2 * Pad + r.LabelH);
                r.Toggle = r.Bounds;
            }
            else
            {
                r.LabelH = Math.Max(GlyphPx, TextRenderer.MeasureText(item.Label, Bold, new Size(w, int.MaxValue), Wrap).Height);
                r.HintH = string.IsNullOrEmpty(item.Hint) ? 0 : TextRenderer.MeasureText(item.Hint, Font, new Size(w, int.MaxValue), Wrap).Height;
                r.LinkH = string.IsNullOrEmpty(item.Detail) ? 0 : line + LogicalToDeviceUnits(2);
                r.DetailH = r.DetailOpen && r.LinkH > 0
                    ? TextRenderer.MeasureText(item.Detail, Font, new Size(w, int.MaxValue), Wrap).Height : 0;
                r.Bounds = new Rectangle(0, y, Width, 2 * Pad + r.LabelH + r.HintH + r.LinkH + r.DetailH);
                if (r.LinkH > 0)
                {
                    var lw = TextRenderer.MeasureText(LinkText(r.DetailOpen), Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                    r.Toggle = new Rectangle(TextLeft + Border, y + Pad + r.LabelH + r.HintH, lw, r.LinkH);
                }
                else r.Toggle = Rectangle.Empty;
            }
            y += r.Bounds.Height;
        }
        _contentHeight = y;

        _desiredHeight = Math.Min(y + 2 * Border, MaxContentHeight);
        Height = _desiredHeight;
        var view = Math.Max(1, ClientSize.Height - 2 * Border);
        _bar.Visible = y > view;
        _bar.Maximum = Math.Max(0, y - 1);
        _bar.LargeChange = view;
        _bar.SmallChange = Math.Max(1, line);
        if (_bar.Value > Math.Max(0, y - view)) _bar.Value = Math.Max(0, y - view);
        if (!_bar.Visible) _bar.Value = 0;
        else if (_live) KeepWaitingRowsInView(view);
        if (_focus >= _rows.Count) _focus = _rows.Count - 1;
        Invalidate();
    }

    // While a check runs, scrolls (as little as needed) so the rows still checking are on screen, finished rows above
    // them giving way first.
    private void KeepWaitingRowsInView(int view)
    {
        var waiting = _rows.Where(r => r.Item?.Status is CheckStatus.Pending or CheckStatus.Running).ToList();
        if (waiting.Count == 0) return;
        var maxValue = Math.Max(0, _bar.Maximum - _bar.LargeChange + 1);
        var want = Math.Clamp(waiting[^1].Bounds.Bottom - view, 0, waiting[0].Bounds.Top);
        if (_bar.Value < want) _bar.Value = Math.Min(want, maxValue);
        else if (_bar.Value > waiting[0].Bounds.Top) _bar.Value = Math.Min(waiting[0].Bounds.Top, maxValue);
    }

    private int Scroll => _bar.Visible ? _bar.Value : 0;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SystemColors.Window);
        var clip = new Rectangle(Border, Border, Width - 2 * Border - (_bar.Visible ? _bar.Width : 0), Height - 2 * Border);
        g.SetClip(clip);
        var w = TextWidth;
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            var top = r.Bounds.Top - Scroll + Border;
            if (top > Height || top + r.Bounds.Height < 0) continue;
            var x = Border + TextLeft;
            var (glyph, color) = r.Item is { } it ? Look(it.Status) : Look(CheckStatus.Ok);
            var bmp = Glyphs.Get(glyph, 16, DeviceDpi, color);
            g.DrawImage(bmp, new Rectangle(Border + Pad, top + Pad, bmp.Width, bmp.Height));
            var lineH = TextRenderer.MeasureText(g, "Ag", Bold, Size.Empty, TextFormatFlags.NoPadding).Height;
            var labelTop = top + Pad + Math.Max(0, (GlyphPx - lineH) / 2);
            if (r.Item is not { } item)
            {
                TextRenderer.DrawText(g, SummaryText(r.OkCount, _okExpanded), Bold, new Rectangle(x, labelTop, w, r.LabelH), SystemColors.WindowText, Wrap);
            }
            else
            {
                TextRenderer.DrawText(g, item.Label, Bold, new Rectangle(x, labelTop, w, r.LabelH), SystemColors.WindowText, Wrap);
                var y = top + Pad + r.LabelH;
                if (r.HintH > 0) TextRenderer.DrawText(g, item.Hint, Font, new Rectangle(x, y, w, r.HintH), SystemColors.GrayText, Wrap);
                y += r.HintH;
                if (r.LinkH > 0)
                {
                    using var link = new Font(Font, FontStyle.Underline);
                    TextRenderer.DrawText(g, LinkText(r.DetailOpen), link, new Rectangle(x, y, w, r.LinkH),
                        SystemInformation.HighContrast ? SystemColors.HotTrack : Color.FromArgb(0x00, 0x5A, 0x9E), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    y += r.LinkH;
                }
                if (r.DetailH > 0) TextRenderer.DrawText(g, item.Detail, Font, new Rectangle(x, y, w, r.DetailH), SystemColors.GrayText, Wrap);
            }
            if (Focused && i == _focus)
                ControlPaint.DrawFocusRectangle(g, new Rectangle(Border, top, clip.Width, r.Bounds.Height));
        }
        g.ResetClip();
        ControlPaint.DrawBorder(g, ClientRectangle, SystemColors.ControlDark, ButtonBorderStyle.Solid);
    }

    private int RowAt(Point p)
    {
        var y = p.Y - Border + Scroll;
        for (var i = 0; i < _rows.Count; i++)
            if (y >= _rows[i].Bounds.Top && y < _rows[i].Bounds.Bottom) return i;
        return -1;
    }

    private bool OnToggle(int i, Point? at)
    {
        var r = _rows[i];
        if (r.Item == null)
        {
            _okExpanded = !_okExpanded;
            Relayout();
            return true;
        }
        if (r.LinkH == 0) return false;
        if (at is { } p && !r.Toggle.Contains(p.X, p.Y - Border + Scroll)) return false;
        if (!_openDetails.Remove(r.Item)) _openDetails.Add(r.Item);
        Relayout();
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var i = RowAt(e.Location);
        if (i < 0) return;
        _focus = i;
        if (e.Button == MouseButtons.Left) OnToggle(i, e.Location);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = RowAt(e.Location);
        var hot = i >= 0 && (_rows[i].Item == null || (_rows[i].LinkH > 0 && _rows[i].Toggle.Contains(e.X, e.Y - Border + Scroll)));
        Cursor = hot ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_bar.Visible) return;
        var max = Math.Max(0, _bar.Maximum - _bar.LargeChange + 1);
        _bar.Value = Math.Clamp(_bar.Value - e.Delta / 120 * 3 * _bar.SmallChange, 0, max);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Control | Keys.C:
                CopyAll();
                e.Handled = true;
                break;
            case Keys.Up when _rows.Count > 0:
                _focus = Math.Max(0, _focus - 1);
                ScrollToFocus();
                e.Handled = true;
                break;
            case Keys.Down when _rows.Count > 0:
                _focus = Math.Min(_rows.Count - 1, _focus + 1);
                ScrollToFocus();
                e.Handled = true;
                break;
            case Keys.Space when _focus >= 0:
                OnToggle(_focus, null);
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    private void ScrollToFocus()
    {
        if (_focus < 0) return;
        var r = _rows[_focus].Bounds;
        var view = ClientSize.Height - 2 * Border;
        if (_bar.Visible)
        {
            if (r.Top < _bar.Value) _bar.Value = r.Top;
            else if (r.Bottom > _bar.Value + view) _bar.Value = Math.Min(Math.Max(0, _bar.Maximum - _bar.LargeChange + 1), r.Bottom - view);
        }
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.Focus, _focus);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_focus < 0 && _rows.Count > 0) _focus = 0;
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private int _laidOutWidth = -1;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width != _laidOutWidth)
        {
            _laidOutWidth = Width;
            if (_items.Count > 0) Relayout();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        _bold?.Dispose();
        _bold = null;
        base.OnFontChanged(e);
        if (_items.Count > 0) Relayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (_items.Count > 0) Relayout();
    }

    // ---- accessibility: one list item per visible row ----

    private string RowName(Row r) => r.Item is { } i
        ? CheckItemFormatter.Format(i)
        : SummaryText(r.OkCount, _okExpanded).TrimEnd(' ', '▸', '▾') + (_okExpanded ? " (expanded)" : " (collapsed)");

    protected override AccessibleObject CreateAccessibilityInstance() => new ListAccessible(this);

    private sealed class ListAccessible(CheckListView owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner._rows.Count;
        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner._rows.Count ? new RowAccessible(owner, this, index) : null;
        public override AccessibleObject? GetFocused() =>
            owner.Focused && owner._focus >= 0 ? GetChild(owner._focus) : base.GetFocused();
    }

    private sealed class RowAccessible(CheckListView owner, AccessibleObject parent, int index) : AccessibleObject
    {
        public override string? Name => index < owner._rows.Count ? owner.RowName(owner._rows[index]) : null;
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override AccessibleObject Parent => parent;
        public override AccessibleStates State =>
            AccessibleStates.Focusable | (owner.Focused && owner._focus == index ? AccessibleStates.Focused : 0);
        public override Rectangle Bounds
        {
            get
            {
                if (index >= owner._rows.Count) return Rectangle.Empty;
                var b = owner._rows[index].Bounds;
                return owner.RectangleToScreen(new Rectangle(0, b.Top - owner.Scroll + owner.Border, owner.Width, b.Height));
            }
        }
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
