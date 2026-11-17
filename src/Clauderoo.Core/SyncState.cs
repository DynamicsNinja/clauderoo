using System.Text.Json;

namespace Clauderoo.Core;

public class SyncState
{
    public const string FileName = ".clauderoo-sync.json";

    public Dictionary<string, string> Hashes { get; set; } = new();

    /// <summary>Env names clauderoo wrote into settings.json for this profile's provider/env overlay.</summary>
    public List<string> ManagedEnv { get; set; } = [];

    /// <summary>Top-level settings.json keys clauderoo wrote for this profile's provider overlay.</summary>
    public List<string> ManagedTop { get; set; } = [];

    /// <summary>
    /// Model env values clauderoo last wrote into settings.json (key -> value); null in state written before this was
    /// recorded. A session's change is measured against these, so a stale file never overrides a newer saved config.
    /// </summary>
    public Dictionary<string, string>? ManagedModels { get; set; }

    public static SyncState Load(string profileDir)
    {
        var file = Path.Combine(profileDir, FileName);
        if (!File.Exists(file)) return new SyncState();
        try
        {
            var s = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(file)) ?? new SyncState();
            s.Hashes ??= new();
            s.ManagedEnv ??= [];
            s.ManagedTop ??= [];
            return s;
        }
        catch (JsonException)
        {
            return new SyncState();
        }
    }

    public void Save(string profileDir)
    {
        Directory.CreateDirectory(profileDir);
        var file = Path.Combine(profileDir, FileName);
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
