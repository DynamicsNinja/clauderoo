using System.Text.Json;

namespace Clauderoo.Core;

/// <summary>
/// The last usage reading per profile, in %root%\usage.json: the time, the plan and the windows, never a token.
/// Every read-modify-write runs under the machine-wide mutex Local\clauderoo-usage and writes atomically.
/// </summary>
public sealed class UsageCache(ClauderooPaths paths)
{
    private sealed record WindowDto(string Name, double Percent, DateTimeOffset? ResetsAt);
    private sealed record ReadingDto(DateTimeOffset FetchedAt, string? Plan, List<WindowDto> Windows);

    private const string MutexName = @"Local\clauderoo-usage";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private string FilePath => Path.Combine(paths.Root, "usage.json");

    public IReadOnlyDictionary<string, UsageReading> Load()
    {
        Dictionary<string, ReadingDto> map = [];
        Locked(() => map = LoadOrThrow());
        return map.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => new UsageReading(kv.Key, kv.Value.FetchedAt, kv.Value.Plan,
            (kv.Value.Windows ?? []).Select(w => new UsageWindow(w.Name, w.Percent, w.ResetsAt)).ToList()));
    }

    public void Put(UsageReading r) =>
        Locked(() =>
        {
            var map = LoadOrThrow();
            map[r.Profile] = new ReadingDto(r.FetchedAt.ToUniversalTime(), r.Plan,
                r.Windows.Select(w => new WindowDto(w.Name, w.Percent, w.ResetsAt?.ToUniversalTime())).ToList());
            Save(map);
        });

    public void Rename(string oldName, string newName) =>
        Locked(() =>
        {
            var map = LoadOrThrow();
            if (!map.Remove(oldName, out var r)) return;
            map[newName] = r;
            Save(map);
        });

    public void Remove(string profile) =>
        Locked(() =>
        {
            var map = LoadOrThrow();
            if (map.Remove(profile)) Save(map);
        });

    private static void Locked(Action action)
    {
        using var mutex = new Mutex(false, MutexName);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Timed out waiting for the usage file lock.");
            action();
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    // A corrupt file is treated as empty; an unreadable one (IO error) throws so nothing gets overwritten.
    private Dictionary<string, ReadingDto> LoadOrThrow()
    {
        if (!File.Exists(FilePath)) return [];
        string text;
        try { text = File.ReadAllText(FilePath); }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
        try { return JsonSerializer.Deserialize<Dictionary<string, ReadingDto>>(text, Json) ?? []; }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return []; }
    }

    private void Save(Dictionary<string, ReadingDto> map)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(map, Json));
        }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
    }
}
