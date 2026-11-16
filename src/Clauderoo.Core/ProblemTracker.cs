namespace Clauderoo.Core;

/// <summary>Remembers the problems reported by the previous sync so only new ones are surfaced again.</summary>
public class ProblemTracker
{
    private HashSet<string> _previous = new(StringComparer.Ordinal);

    /// <summary>Returns the problems not present in the previous call, then remembers the current set.</summary>
    public IReadOnlyList<string> NewOnly(IEnumerable<string> problems)
    {
        var current = new HashSet<string>(problems, StringComparer.Ordinal);
        var fresh = current.Where(p => !_previous.Contains(p)).ToList();
        _previous = current;
        return fresh;
    }
}
