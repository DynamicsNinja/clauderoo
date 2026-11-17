using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core;

/// <summary>What an import did. Skipped and NeedSecret entries are ready-to-print text, "name: reason".</summary>
public record ImportResult(IReadOnlyList<string> Created, IReadOnlyList<string> Updated,
    IReadOnlyList<string> Skipped, IReadOnlyList<string> NeedSecret);

public partial class ProfileManager
{
    internal const int ExportSchemaVersion = 1;
    private const string AutoSwitchKey = "autoSwitch";

    private static readonly JsonSerializerOptions TransferJson = new() { WriteIndented = true };

    /// <summary>
    /// Export as JSON text: provider settings, env (only when asked), overrides, start folder and folder mappings.
    /// Never any secret, login or home flag. <paramref name="names"/> null means every non-home profile.
    /// </summary>
    public string Export(IReadOnlyList<string>? names, bool includeEnv)
    {
        var cfg = Config;
        var chosen = names is { Count: > 0 }
            ? names.Distinct().Select(n => (Name: n, Entry: Entry(cfg, n))).ToList()
            : cfg.Profiles.Where(kv => !kv.Value.Home).Select(kv => (Name: kv.Key, Entry: kv.Value)).ToList();

        var profiles = new JsonArray();
        foreach (var (name, e) in chosen)
        {
            var o = new JsonObject { ["name"] = name };
            if (e.Home)
            {
                // Named explicitly: exported as a plain login profile, with nothing of its own.
                o["provider"] = Providers.Login;
                profiles.Add(o);
                continue;
            }
            o["provider"] = Providers.Of(e);
            o["vertex"] = e.Vertex == null ? null : new JsonObject
            {
                ["project"] = e.Vertex.Project,
                ["region"] = e.Vertex.Region,
                ["sonnetModel"] = e.Vertex.SonnetModel,
                ["opusModel"] = e.Vertex.OpusModel,
                ["haikuModel"] = e.Vertex.HaikuModel,
            };
            if (includeEnv)
            {
                var env = new JsonObject();
                foreach (var kv in e.Env.OrderBy(k => k.Key, StringComparer.Ordinal)) env[kv.Key] = kv.Value;
                o["env"] = env;
            }
            o["overrides"] = new JsonArray(e.Overrides.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            o["startDir"] = e.StartDir;
            profiles.Add(o);
        }

        var exported = chosen.Select(c => c.Name).ToHashSet();
        var folders = new JsonArray();
        foreach (var kv in cfg.Folders.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Where(kv => exported.Contains(kv.Value)))
            folders.Add(new JsonObject { ["dir"] = kv.Key, ["profile"] = kv.Value });

        JsonNode? autoSwitch = null;
        if (cfg.Extra != null && cfg.Extra.TryGetValue(AutoSwitchKey, out var a) && a.ValueKind != JsonValueKind.Null)
            autoSwitch = JsonNode.Parse(a.GetRawText());

        var root = new JsonObject
        {
            ["schemaVersion"] = ExportSchemaVersion,
            ["exportedAt"] = now().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["profiles"] = profiles,
            ["folders"] = folders,
            ["autoSwitch"] = autoSwitch,
        };
        return root.ToJsonString(TransferJson);
    }

    /// <summary>Writes <see cref="Export"/> to a file (atomically). An existing file is kept unless <paramref name="force"/>.</summary>
    public void ExportToFile(string file, IReadOnlyList<string>? names, bool includeEnv, bool force)
    {
        var full = Path.GetFullPath(file);
        if (File.Exists(full) && !force)
            throw new ClauderooException($"{full} already exists. Use --force to overwrite it.");
        var json = Export(names, includeEnv);
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(full, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ClauderooException($"Couldn't write {full}: {e.Message}");
        }
    }

    /// <summary>
    /// Creates (or with <paramref name="force"/>, updates) the profiles in an export, then adds its folder mappings.
    /// Existing profiles are left untouched without force. Secret-based profiles come in with the login provider.
    /// </summary>
    public ImportResult Import(string json, bool force)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ClauderooException("That file is not a clauderoo export (not valid JSON)."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var sv)
                || sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out var schema))
                throw new ClauderooException("That file is not a clauderoo export (no schemaVersion).");
            if (schema > ExportSchemaVersion)
                throw new ClauderooException($"This export is from a newer clauderoo (schema {schema}).");

            var created = new List<string>();
            var updated = new List<string>();
            var skipped = new List<string>();
            var needSecret = new List<string>();
            var startDirs = new Dictionary<string, string?>();

            if (root.TryGetProperty("profiles", out var plist) && plist.ValueKind == JsonValueKind.Array)
                foreach (var p in plist.EnumerateArray())
                    ImportProfile(p, force, created, updated, skipped, needSecret, startDirs);

            // Everything below edits config directly; one load and one save, and only when something changed.
            var cfg = Config;
            var dirty = false;
            foreach (var (name, dir) in startDirs)
                if (cfg.Profiles.TryGetValue(name, out var pe) && pe.StartDir != dir) { pe.StartDir = dir; dirty = true; }

            if (root.TryGetProperty("folders", out var flist) && flist.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in flist.EnumerateArray())
                {
                    var dir = Str(f, "dir");
                    var profile = Str(f, "profile");
                    if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(profile)) continue;
                    if (!cfg.Profiles.ContainsKey(profile))
                    {
                        skipped.Add($"folder {dir}: profile '{profile}' doesn't exist here");
                        continue;
                    }
                    string key;
                    try { key = FolderMap.Normalize(dir); }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        skipped.Add($"folder {dir}: not a valid path");
                        continue;
                    }
                    var existing = cfg.Folders.Where(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (existing.Count > 0)
                    {
                        if (existing.Any(k => k.Value == profile)) continue;
                        if (!force)
                        {
                            skipped.Add($"folder {key}: already mapped to {existing[0].Value}");
                            continue;
                        }
                        foreach (var k in existing) cfg.Folders.Remove(k.Key);
                    }
                    cfg.Folders[key] = profile;
                    dirty = true;
                }
            }

            // Passed through untouched until the auto-switch feature gives it a typed home; never overwrites.
            if (root.TryGetProperty(AutoSwitchKey, out var auto) && auto.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && (cfg.Extra == null || !cfg.Extra.ContainsKey(AutoSwitchKey)))
            {
                cfg.Extra ??= new Dictionary<string, JsonElement>();
                cfg.Extra[AutoSwitchKey] = auto.Clone();
                dirty = true;
            }

            if (dirty) ConfigStore.Save(paths, cfg);
            return new ImportResult(created, updated, skipped, needSecret);
        }
    }

    private void ImportProfile(JsonElement p, bool force, List<string> created, List<string> updated,
        List<string> skipped, List<string> needSecret, Dictionary<string, string?> startDirs)
    {
        var name = Str(p, "name") ?? "";
        try { ProfileName.Validate(name); }
        catch (ClauderooException e) { skipped.Add($"{name}: {e.Message}"); return; }

        var cfg = Config;
        var exists = cfg.Profiles.TryGetValue(name, out var current);
        if (exists && current!.Home) { skipped.Add($"{name}: the home profile can't be changed by an import"); return; }
        if (exists && !force) { skipped.Add($"{name}: already exists (use --force to update it)"); return; }

        // Validate everything first, so a bad profile is skipped before anything is created.
        var provider = Str(p, "provider") ?? Providers.Login;
        VertexSettings? vertex = null;
        switch (provider)
        {
            case Providers.Login or Providers.ApiKey or Providers.Token:
                break;
            case Providers.Vertex:
                if (p.TryGetProperty("vertex", out var v) && v.ValueKind == JsonValueKind.Object
                    && !string.IsNullOrWhiteSpace(Str(v, "project")) && !string.IsNullOrWhiteSpace(Str(v, "region")))
                    vertex = new VertexSettings
                    {
                        Project = Str(v, "project")!, Region = Str(v, "region")!,
                        SonnetModel = Str(v, "sonnetModel"), OpusModel = Str(v, "opusModel"), HaikuModel = Str(v, "haikuModel"),
                    };
                else { skipped.Add($"{name}: vertex needs a project and a region"); return; }
                break;
            default:
                skipped.Add($"{name}: unknown provider '{provider}'");
                return;
        }

        Dictionary<string, string>? envVars = null;
        if (p.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            envVars = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in env.EnumerateObject())
            {
                try { ProviderOverlay.ValidateEnvName(kv.Name); }
                catch (ClauderooException e) { skipped.Add($"{name}: {e.Message}"); return; }
                envVars[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString()! : kv.Value.ToString();
            }
        }

        var overrides = new List<string>();
        if (p.TryGetProperty("overrides", out var ov) && ov.ValueKind == JsonValueKind.Array)
            foreach (var o in ov.EnumerateArray())
                if (o.ValueKind == JsonValueKind.String && SharedItems.IsKnown(o.GetString()!)) overrides.Add(o.GetString()!);

        if (!exists) Add(name);

        // A secret-based profile keeps a secret it already has; otherwise it starts on login and is listed as needing one.
        switch (provider)
        {
            case Providers.ApiKey when exists && HasApiKey(name):
            case Providers.Token when exists && HasToken(name):
                break;
            case Providers.ApiKey or Providers.Token:
                SetProvider(name, Providers.Login);
                needSecret.Add(provider == Providers.ApiKey
                    ? $"{name}: set the key with `cr provider {name} apikey`"
                    : $"{name}: set the token with `cr provider {name} token`");
                break;
            case Providers.Vertex:
                SetProvider(name, Providers.Vertex, vertex);
                break;
            default:
                SetProvider(name, Providers.Login);
                break;
        }

        if (envVars != null) ReplaceEnv(name, envVars);

        var have = Get(name).Overrides.ToList();
        foreach (var item in overrides.Where(i => !have.Contains(i))) Own(name, item);
        if (exists)
            foreach (var item in have.Where(i => !overrides.Contains(i))) Share(name, item);

        startDirs[name] = Str(p, "startDir");
        (exists ? updated : created).Add(name);
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
