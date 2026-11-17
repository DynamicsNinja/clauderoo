using System.Diagnostics;
using System.Text.Json;

namespace Clauderoo.Core;

public record RunningSession(string Profile, int Pid, DateTime Started);

/// <summary>Thrown by Rename/Remove while clauderoo-started sessions of the profile are running.</summary>
public sealed class SessionsRunningException(string profile, int count)
    : ClauderooException($"{profile} has {count} running session(s) started by clauderoo. Close them first.")
{
    public string Profile { get; } = profile;
    public int Count { get; } = count;
}

/// <summary>
/// Tracks the Claude sessions clauderoo itself started, in %root%\sessions.json. A session counts as running only while
/// its pid is alive AND that process has the start time recorded (so a reused pid is never mistaken for a session).
/// Sessions started outside clauderoo are not detected. Start times are stored in UTC. Every read-modify-write runs
/// under the machine-wide mutex Local\clauderoo-sessions so the CLI and tray never lose each other's entries.
/// </summary>
public sealed class SessionTracker
{
    private sealed record Entry(string Profile, int Pid, long StartTicks);
    private enum State { Alive, Dead, Unknown }

    private const string MutexName = @"Local\clauderoo-sessions";
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ClauderooPaths _paths;
    private readonly Func<int, (bool alive, DateTime? start)> _probe;

    public SessionTracker(ClauderooPaths paths, Func<int, (bool alive, DateTime? start)>? probe = null)
    {
        _paths = paths;
        _probe = probe ?? DefaultProbe;
    }

    private string FilePath => Path.Combine(_paths.Root, "sessions.json");

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

    public void Record(string profile, int pid, DateTime started) =>
        Locked(() =>
        {
            var list = LoadOrThrow();
            list = list.Where(e => e.Pid != pid && StateOf(e) != State.Dead).ToList();
            list.Add(new Entry(profile, pid, started.ToUniversalTime().Ticks));
            Save(list);
        });

    /// <summary>Live sessions (optionally for one profile). Dead or pid-reused entries are dropped from disk.</summary>
    public IReadOnlyList<RunningSession> Running(string? profile = null)
    {
        IReadOnlyList<RunningSession> result = [];
        try
        {
            Locked(() =>
            {
                var before = LoadOrThrow();
                var states = before.Select(e => (e, s: StateOf(e))).ToList();
                var keep = states.Where(x => x.s != State.Dead).Select(x => x.e).ToList();
                if (keep.Count != before.Count) Save(keep);
                result = states.Where(x => x.s == State.Alive && (profile == null || x.e.Profile == profile))
                    .Select(x => new RunningSession(x.e.Profile, x.e.Pid, new DateTime(x.e.StartTicks, DateTimeKind.Utc).ToLocalTime()))
                    .ToList();
            });
        }
        catch (IOException) { /* can't read the file right now: report nothing, change nothing */ }
        return result;
    }

    /// <summary>Re-keys a profile's entries after a rename.</summary>
    public void Rename(string oldName, string newName) =>
        Locked(() =>
        {
            var list = LoadOrThrow();
            if (!list.Any(e => e.Profile == oldName)) return;
            Save(list.Select(e => e.Profile == oldName ? e with { Profile = newName } : e).ToList());
        });

    private static void Locked(Action action)
    {
        using var mutex = new Mutex(false, MutexName);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Timed out waiting for the sessions file lock.");
            action();
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    private State StateOf(Entry e)
    {
        try
        {
            var (alive, start) = _probe(e.Pid);
            if (!alive) return State.Dead;
            return start is { } s && (s.ToUniversalTime() - new DateTime(e.StartTicks, DateTimeKind.Utc)).Duration() <= StartTolerance
                ? State.Alive
                : State.Dead;
        }
        catch (Exception) { return State.Unknown; } // can't tell: neither report it nor forget it
    }

    // A corrupt file is treated as empty; an unreadable one (IO error) throws so nothing gets overwritten.
    private List<Entry> LoadOrThrow()
    {
        if (!File.Exists(FilePath)) return [];
        string text;
        try { text = File.ReadAllText(FilePath); }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
        try { return JsonSerializer.Deserialize<List<Entry>>(text) ?? []; }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return []; }
    }

    private void Save(List<Entry> list)
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
        }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
    }

    private static (bool alive, DateTime? start) DefaultProbe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return (false, null);
            return (true, p.StartTime);
        }
        catch (ArgumentException) { return (false, null); } // no such pid
        catch (InvalidOperationException) { return (false, null); }
    }
}
