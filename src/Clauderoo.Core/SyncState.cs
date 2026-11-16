using System.Text.Json;

namespace Clauderoo.Core;

public class SyncState
{
    public const string FileName = ".clauderoo-sync.json";

    public Dictionary<string, string> Hashes { get; set; } = new();

    public static SyncState Load(string profileDir)
    {
        var file = Path.Combine(profileDir, FileName);
        if (!File.Exists(file)) return new SyncState();
        try
        {
            return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(file)) ?? new SyncState();
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
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, overwrite: true);
    }
}
