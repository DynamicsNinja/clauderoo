using System.Globalization;
using Clauderoo.Core;

namespace Clauderoo.Cli;

/// <summary>'removed' (list removed copies) and 'purge' (permanently delete them).</summary>
internal static class RemovedCommands
{
    public const string PurgeUsage = "Usage: clauderoo purge [<name>] [--at <id>] [--older-than <age>] [--all] [--yes] [--json]";

    public static int Removed(List<string> pos, bool json, TextWriter output, ProfileManager pm)
    {
        if (pos.Count > 1) throw new ClauderooException("Usage: clauderoo removed [name] [--json]");
        var name = pos.Count == 1 ? pos[0] : null;
        if (name != null) ProfileName.Validate(name);
        var copies = pm.ListRemoved(name);
        if (json)
        {
            JsonOut.Write(output, new
            {
                Removed = copies.Select(Payload),
                TotalBytes = copies.Sum(c => c.Size),
            });
            return 0;
        }
        if (copies.Count == 0)
        {
            output.WriteLine(name == null ? "No removed profiles." : $"No removed copies of '{name}'.");
            return 0;
        }
        foreach (var line in Lines(copies)) output.WriteLine(line);
        output.WriteLine($"{Count(copies.Count)}, {RemovedProfiles.FormatSize(copies.Sum(c => c.Size))} in {pm.Paths.RemovedDir}.");
        output.WriteLine("Restore one with 'clauderoo restore <name> --at <id>'; delete permanently with 'clauderoo purge'.");
        return 0;
    }

    public static int Purge(List<string> pos, bool json, TextWriter output, TextWriter err, ProfileManager pm, Func<string?> readLine)
    {
        var yes = pos.Remove("--yes");
        var all = pos.Remove("--all");
        var at = TakeValue(pos, "--at", PurgeUsage);
        var olderText = TakeValue(pos, "--older-than", PurgeUsage);
        if (pos.Count > 1 || pos.Any(p => p.StartsWith('-'))) throw new ClauderooException(PurgeUsage);
        var name = pos.Count == 1 ? pos[0] : null;
        TimeSpan? olderThan = olderText != null ? RemovedProfiles.ParseAge(olderText) : null;

        var copies = pm.SelectRemoved(name, at, olderThan, all);
        if (copies.Count == 0)
        {
            if (json) JsonOut.Write(output, new { Deleted = Array.Empty<object>(), FreedBytes = 0L, Failed = Array.Empty<object>() });
            else output.WriteLine("No removed copies match. Nothing was deleted.");
            return 0;
        }

        if (!yes)
        {
            // stdout carries only the payload with --json, so there is no room for a question.
            if (json) throw new ClauderooException("'purge --json' deletes without asking, so it needs --yes.");
            output.WriteLine("These removed copies will be deleted permanently:");
            foreach (var line in Lines(copies)) output.WriteLine("  " + line);
            output.Write($"Delete {Count(copies.Count)} ({RemovedProfiles.FormatSize(copies.Sum(c => c.Size))})? This can't be undone. [y/N] ");
            var answer = readLine()?.Trim();
            if (answer == null)
            {
                output.WriteLine();
                throw new ClauderooException("Nothing was deleted. Run it again with --yes to delete without asking.");
            }
            if (!(answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine("Nothing was deleted.");
                return 0;
            }
        }

        var r = pm.Purge(copies);
        if (json)
        {
            JsonOut.Write(output, new
            {
                Deleted = r.Deleted.Select(Payload),
                FreedBytes = r.Freed,
                Failed = r.Failed.Select(f => new { f.Copy.Name, f.Copy.Id, f.Message }),
            });
        }
        else
        {
            output.WriteLine($"Deleted {Count(r.Deleted.Count)}, freed {RemovedProfiles.FormatSize(r.Freed)}.");
            foreach (var f in r.Failed) err.WriteLine(f.Message);
        }
        return r.Failed.Count > 0 ? 1 : 0;
    }

    private static object Payload(RemovedCopy c) => new
    {
        c.Name, c.Id, c.RemovedAt, SizeBytes = c.Size, HasProfile = c.CanRestore, HasIsolated = c.IsolatedDir != null,
    };

    private static string Count(int n) => n == 1 ? "1 copy" : $"{n} copies";

    private static IEnumerable<string> Lines(IReadOnlyList<RemovedCopy> copies)
    {
        var nameW = copies.Max(c => c.Name.Length);
        var sizes = copies.Select(c => RemovedProfiles.FormatSize(c.Size)).ToList();
        var sizeW = sizes.Max(s => s.Length);
        return copies.Select((c, i) => string.Join("  ",
            c.Name.PadRight(nameW),
            c.RemovedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            c.Id,
            sizes[i].PadLeft(sizeW),
            !c.CanRestore ? "(isolated folder only)" : c.IsolatedDir != null ? "(with isolated folder)" : "").TrimEnd());
    }

    private static string? TakeValue(List<string> pos, string flag, string usage)
    {
        var i = pos.IndexOf(flag);
        if (i < 0) return null;
        if (i + 1 >= pos.Count) throw new ClauderooException($"{flag} needs a value. {usage}");
        var v = pos[i + 1];
        pos.RemoveRange(i, 2);
        return v;
    }

    /// <summary>Removes "--at &lt;id&gt;" from <paramref name="pos"/> for 'restore'.</summary>
    public static string? TakeAt(List<string> pos, string usage) => TakeValue(pos, "--at", usage);
}
