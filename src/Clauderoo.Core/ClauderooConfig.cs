using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clauderoo.Core;

public class ProfileEntry
{
    public bool Home { get; set; }
    public List<string> Overrides { get; set; } = [];
    /// <summary>Entries of shared items this profile leaves out, as "item/entry" (e.g. "skills/foo"; see SharedItems).</summary>
    public List<string> Exclude { get; set; } = [];
    public string? StartDir { get; set; }
    public string? Provider { get; set; }
    public VertexSettings? Vertex { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>Fields this version doesn't know (written by a newer clauderoo); kept so saving doesn't drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class VertexSettings
{
    public string Project { get; set; } = "";
    public string Region { get; set; } = "";
    public string? SonnetModel { get; set; }
    public string? OpusModel { get; set; }
    public string? HaikuModel { get; set; }
}

public static class Providers
{
    public const string Login = "login";
    public const string Vertex = "vertex";
    public const string ApiKey = "apikey";
    /// <summary>A long-lived Claude token from `claude setup-token`, passed to Claude as CLAUDE_CODE_OAUTH_TOKEN.</summary>
    public const string Token = "token";

    public static string Of(ProfileEntry e) => string.IsNullOrEmpty(e.Provider) ? Login : e.Provider;

    /// <summary>The provider's name for people: "Claude login", "Google Vertex AI", "API key", "Claude token".</summary>
    public static string DisplayName(string provider) => provider switch
    {
        Vertex => "Google Vertex AI",
        ApiKey => "API key",
        Token => "Claude token",
        _ => "Claude login",
    };
}

public class ClauderooConfig
{
    /// <summary>The config.json format version (see <see cref="ConfigStore.CurrentSchemaVersion"/>); missing means 1.</summary>
    public int SchemaVersion { get; set; } = ConfigStore.CurrentSchemaVersion;

    public string? DefaultProfile { get; set; }
    public string Terminal { get; set; } = "wt";
    public string HomeDir { get; set; } = "";
    public string HomeJson { get; set; } = "";
    public Dictionary<string, ProfileEntry> Profiles { get; set; } = new();
    public AutoSwitchSettings? AutoSwitch { get; set; }

    /// <summary>Normalized full folder path -> profile name (see FolderMap).</summary>
    public Dictionary<string, string> Folders { get; set; } = new();

    /// <summary>Folders and files of the home profile shared on top of the built-in ones (see SharedItems).</summary>
    public List<string> ExtraSharedDirs { get; set; } = [];
    public List<string> ExtraSharedFiles { get; set; } = [];

    /// <summary>Fields this version doesn't know (written by a newer clauderoo); kept so saving doesn't drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public string? HomeProfileName =>
        Profiles.FirstOrDefault(p => p.Value.Home).Key;
}

public static partial class ConfigStore
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
            return Parse(p, File.ReadAllText(p.ConfigFile));
        }
        catch (JsonException)
        {
            throw new ClauderooException($"config.json is not valid JSON: {p.ConfigFile}");
        }
    }

    /// <summary>Best-effort: writes one profile's settings (no secrets live in a ProfileEntry).</summary>
    internal static void WriteEntry(string file, ProfileEntry e)
    {
        try { AtomicFile.WriteAllText(file, JsonSerializer.Serialize(e, Options)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static ProfileEntry? ReadEntry(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<ProfileEntry>(File.ReadAllText(file), Options) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Replaces config.json with <paramref name="c"/> as is, under the config lock. For a load-change-save use
    /// <see cref="Update"/>, which re-reads the file inside the lock so another process's change isn't lost.
    /// </summary>
    public static void Save(ClauderooPaths p, ClauderooConfig c) =>
        WithLock(p, LockTimeout, () =>
        {
            RefuseNewer(p, c.SchemaVersion);
            RefuseNewer(p, DiskSchemaVersion(p));
            Write(p, c);
            return true;
        });

    private static void Write(ClauderooPaths p, ClauderooConfig c)
    {
        Directory.CreateDirectory(p.Root);
        AtomicFile.WriteAllText(p.ConfigFile, JsonSerializer.Serialize(c, Options));
    }
}
