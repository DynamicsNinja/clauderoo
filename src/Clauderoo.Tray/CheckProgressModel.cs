using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>
/// The live checklist of a running setup check: the planned rows in check order, each replaced (by
/// <see cref="CheckItem.Key"/>) as its progress report arrives. Rows not decided yet carry a muted
/// "Checking…" / "Testing…" hint for <see cref="CheckListView"/>.
/// </summary>
internal sealed class CheckProgressModel
{
    private readonly List<CheckItem> _rows = [];
    private bool _cancelled;

    public IReadOnlyList<CheckItem> Rows => _rows;
    public int Total => _rows.Count;
    public int Done => _rows.Count(r => r.Status is not (CheckStatus.Pending or CheckStatus.Running));
    public string StatusText => $"Checking… {Done} of {Total} done";

    public void Apply(CheckItem item)
    {
        if (_cancelled) return;
        var shown = item.Status switch
        {
            CheckStatus.Pending => item with { Hint = "Checking…" },
            CheckStatus.Running => item with { Hint = "Testing…" },
            _ => item,
        };
        var i = item.Key == null ? -1 : _rows.FindIndex(r => r.Key == item.Key);
        if (i >= 0) _rows[i] = shown;
        else _rows.Add(shown);
    }

    /// <summary>The check was stopped: rows still waiting read "– Cancelled"; later reports are ignored.</summary>
    public void Cancel()
    {
        _cancelled = true;
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Status is CheckStatus.Pending or CheckStatus.Running)
                _rows[i] = _rows[i] with { Status = CheckStatus.Skipped, Hint = "Cancelled", Detail = null };
    }
}
