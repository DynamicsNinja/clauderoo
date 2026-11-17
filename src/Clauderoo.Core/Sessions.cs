using System.Diagnostics;
using System.Text.Json;

namespace Clauderoo.Core;

public record RunningSession(string Profile, int Pid, DateTime Started);

/// <summary>
/// Tracks the Claude sessions clauderoo itself started, in %root%\sessions.json. A session counts as running only while
/// its pid is alive AND that process has the start time recorded (so a reused pid is never mistaken for a session).
/// Sessions started outside clauderoo are not detected.
/// </summary>
public sealed class SessionTracker
{
    private sealed record Entry(string Profile, int Pid, long StartTicks);

    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ClauderooPaths _paths;
    private readonly Func<int, (bool alive, DateTime? start)> _probe;
    private readonly object _gate = new();

    public SessionTracker(ClauderooPaths paths, Func<int, (bool alive, DateTime? start)>? probe = null)
    {
        _paths = paths;
        _probe = probe ?? DefaultProbe;
    }

    private string File_ => Path.Combine(_paths.Root, "sessions.json");

    public void Record(string profile, Process p)
    {
        DateTime start;
        try { start = p.StartTime; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return; // already gone: nothing to track
        }
        Record(profile, p.Id, start);
    }

    public void Record(string profile, int pid, DateTime started)
    {
        lock (_gate)
        {
            var list = Prune(Load());
            list.RemoveAll(e => e.Pid == pid);
            list.Add(new Entry(profile, pid, started.Ticks));
            Save(list);
        }
    }

    /// <summary>Live sessions (optionally for one profile). Dead or pid-reused entries are dropped from disk.</summary>
    public IReadOnlyList<RunningSession> Running(string? profile = null)
    {
        lock (_gate)
        {
            var before = Load();
            var live = Prune(before);
            if (live.Count != before.Count) Save(live);
            return live.Where(e => profile == null || e.Profile == profile)
                .Select(e => new RunningSession(e.Profile, e.Pid, new DateTime(e.StartTicks))).ToList();
        }
    }

    private List<Entry> Prune(List<Entry> list) => list.Where(IsAlive).ToList();

    private bool IsAlive(Entry e)
    {
        try
        {
            var (alive, start) = _probe(e.Pid);
            return alive && start is { } s && (s - new DateTime(e.StartTicks)).Duration() <= StartTolerance;
        }
        catch (Exception) { return false; }
    }

    private List<Entry> Load()
    {
        try
        {
            if (!File.Exists(File_)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(File_)) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return []; // unreadable tracking file: treat as nothing running rather than blocking the user
        }
    }

    private void Save(List<Entry> list)
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            AtomicFile.WriteAllText(File_, JsonSerializer.Serialize(list, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static (bool alive, DateTime? start) DefaultProbe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return (false, null);
            return (true, p.StartTime);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return (false, null);
        }
    }
}
