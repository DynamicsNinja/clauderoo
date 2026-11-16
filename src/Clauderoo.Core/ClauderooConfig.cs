using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clauderoo.Core;

public class ProfileEntry
{
    public bool Home { get; set; }
    public List<string> Overrides { get; set; } = [];
    public string? StartDir { get; set; }
}

public class ClauderooConfig
{
    public string? DefaultProfile { get; set; }
    public string Terminal { get; set; } = "wt";
    public string HomeDir { get; set; } = "";
    public string HomeJson { get; set; } = "";
    public Dictionary<string, ProfileEntry> Profiles { get; set; } = new();

    [JsonIgnore]
    public string? HomeProfileName =>
        Profiles.FirstOrDefault(p => p.Value.Home).Key;
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ClauderooConfig? Load(ClauderooPaths p)
    {
        if (!File.Exists(p.ConfigFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<ClauderooConfig>(File.ReadAllText(p.ConfigFile), Options);
        }
        catch (JsonException)
        {
            throw new ClauderooException($"config.json is not valid JSON: {p.ConfigFile}");
        }
    }

    public static void Save(ClauderooPaths p, ClauderooConfig c)
    {
        Directory.CreateDirectory(p.Root);
        AtomicFile.WriteAllText(p.ConfigFile, JsonSerializer.Serialize(c, Options));
    }
}
