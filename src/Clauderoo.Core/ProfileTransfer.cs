using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core;

/// <summary>
/// What an import did. Skipped, NeedSecret, KeptProvider and EnvNames entries are ready-to-print text, "name: detail".
/// Notices holds the sync notices and problems raised along the way; Mapped holds "dir → profile" for each folder mapping added;
/// EnvNames lists the environment variable names (never values) each imported profile now has; AutoSwitchImported says the
/// export's auto-switch settings were taken over (only ever when this machine had none, and always turned off).
/// </summary>
public record ImportResult(IReadOnlyList<string> Created, IReadOnlyList<string> Updated,
    IReadOnlyList<string> Skipped, IReadOnlyList<string> NeedSecret, IReadOnlyList<string>? KeptProvider = null,
    IReadOnlyList<string>? Notices = null, IReadOnlyList<string>? Mapped = null, IReadOnlyList<string>? EnvNames = null,
    bool AutoSwitchImported = false)
{
    /// <summary>The summary line for <see cref="AutoSwitchImported"/>, shared by the CLI and the tray.</summary>
    public const string AutoSwitchImportedText = "Auto-switch settings imported (off — turn on with 'cr auto on' or the tray)";

    /// <summary>True when the import created or updated a profile, added a folder mapping or set up auto-switch.</summary>
    public bool ChangedAnything => Created.Count + Updated.Count + (Mapped?.Count ?? 0) > 0 || AutoSwitchImported;
}

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
                foreach (var kv in e.Env.Where(k => !ProviderOverlay.ReservedEnv.Contains(k.Key)).OrderBy(k => k.Key, StringComparer.Ordinal)) env[kv.Key] = kv.Value;
                o["env"] = env;
            }
            o["overrides"] = new JsonArray(e.Overrides.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            if (e.Exclude.Count > 0) o["exclude"] = new JsonArray(e.Exclude.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            o["startDir"] = e.StartDir;
            profiles.Add(o);
        }

        var exported = chosen.Select(c => c.Name).ToHashSet();
        var folders = new JsonArray();
        foreach (var kv in cfg.Folders.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Where(kv => exported.Contains(kv.Value)))
            folders.Add(new JsonObject { ["dir"] = kv.Key, ["profile"] = kv.Value });

        // The settings only (enabled, threshold, order). The engine's state (autoswitch.json: last switch, switched-from,
        // manual hold, notified flags) belongs to this machine and is never exported.
        JsonNode? autoSwitch = cfg.AutoSwitch is { } a
            ? new JsonObject
            {
                ["enabled"] = a.Enabled,
                ["threshold"] = a.Threshold,
                ["order"] = new JsonArray(a.Order.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            }
            : null;

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

    /// <summary>The largest file <see cref="ImportFile"/> reads; real exports are a few KB.</summary>
    internal const long MaxImportBytes = 1024 * 1024;

    /// <summary>Reads an export file and imports it. Refuses files over 1 MB; IO errors become plain messages.</summary>
    public ImportResult ImportFile(string path, bool force)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ClauderooException($"Not a valid file path: {path}");
        }
        string text;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) throw new ClauderooException($"File not found: {full}");
            if (info.Length > MaxImportBytes)
                throw new ClauderooException($"{full} is too big to be a clauderoo export (over 1 MB).");
            text = File.ReadAllText(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ClauderooException($"Couldn't read {full}: {e.Message}");
        }
        return Import(text, force);
    }

    /// <summary>
    /// Creates (or with <paramref name="force"/>, updates) the profiles in an export, then adds its folder mappings.
    /// Existing profiles are left untouched without force. Secret-based profiles come in with the login provider.
    /// Owned items are only ever added: an import never shares a profile's own copies back.
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

            var log = new ImportLog();

            if (root.TryGetProperty("profiles", out var plist) && plist.ValueKind == JsonValueKind.Array)
                foreach (var p in plist.EnumerateArray())
                    ImportProfileSafe(p, force, log);

            // Everything below edits config directly; one locked load and save, and only when something changed.
            var skipped = log.Skipped;
            var autoImported = false;
            ConfigStore.UpdateWhen(paths, cfg =>
            {
                var dirty = false;
                foreach (var (name, dir) in log.StartDirs)
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
                        log.Mapped.Add($"{key} → {profile}");
                        dirty = true;
                    }
                }

                // Auto-switch settings are taken over only when this machine has none: an import never changes them.
                if (cfg.AutoSwitch == null && root.TryGetProperty(AutoSwitchKey, out var auto) && auto.ValueKind == JsonValueKind.Object)
                {
                    if (ReadAutoSwitch(auto, cfg, skipped, log.Notices) is { } settings)
                    {
                        cfg.AutoSwitch = settings;
                        autoImported = dirty = true;
                    }
                }

                return dirty;
            });
            return new ImportResult(log.Created, log.Updated, skipped, log.NeedSecret, log.Kept.Distinct().ToList(),
                log.Notices, log.Mapped, log.EnvNames, autoImported);
        }
    }

    private sealed class ImportLog
    {
        public List<string> Created { get; } = [];
        public List<string> Updated { get; } = [];
        public List<string> Skipped { get; } = [];
        public List<string> NeedSecret { get; } = [];
        public List<string> Kept { get; } = [];
        public List<string> Notices { get; } = [];
        public List<string> Mapped { get; } = [];
        public List<string> EnvNames { get; } = [];
        public Dictionary<string, string?> StartDirs { get; } = [];

        public void Collect(SyncReport r)
        {
            Notices.AddRange(r.Notices);
            Notices.AddRange(r.Problems);
        }

        /// <summary>Drops the "name: …" entries a failed profile left behind.</summary>
        public void Forget(string name)
        {
            Created.Remove(name);
            Updated.Remove(name);
            StartDirs.Remove(name);
            NeedSecret.RemoveAll(s => s.StartsWith(name + ":", StringComparison.Ordinal));
            Kept.RemoveAll(s => s.StartsWith(name + ":", StringComparison.Ordinal));
            EnvNames.RemoveAll(s => s.StartsWith(name + ":", StringComparison.Ordinal));
        }
    }

    // One profile's failure must not abort the rest: it is reported in Skipped and the import carries on.
    private void ImportProfileSafe(JsonElement p, bool force, ImportLog log)
    {
        var added = false;
        try { ImportProfile(p, force, log, ref added); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var name = Str(p, "name") ?? "";
            log.Forget(name);
            log.Skipped.Add(added ? $"{name}: created but not finished: {e.Message}" : $"{name}: failed - {e.Message}");
        }
    }

    private void ImportProfile(JsonElement p, bool force, ImportLog log, ref bool added)
    {
        var skipped = log.Skipped;
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
        var hasOverrides = p.TryGetProperty("overrides", out var ov) && ov.ValueKind == JsonValueKind.Array;
        if (hasOverrides)
            foreach (var o in ov.EnumerateArray())
                if (o.ValueKind == JsonValueKind.String && SharedItems.IsKnown(cfg, o.GetString()!)) overrides.Add(o.GetString()!);

        if (!exists)
        {
            log.Collect(Add(name));
            added = true;
        }

        // A profile signed in with a stored secret keeps its sign-in: an import never changes how it signs in.
        var currentProvider = exists ? Providers.Of(current!) : Providers.Login;
        var holdsSecret = currentProvider switch
        {
            Providers.ApiKey => HasApiKey(name),
            Providers.Token => HasToken(name),
            _ => false,
        };
        if (holdsSecret && provider != currentProvider)
            log.Kept.Add($"{name}: kept its {(currentProvider == Providers.ApiKey ? "API key" : "token")}; change the provider yourself");
        switch (provider)
        {
            case var _ when holdsSecret:
                break;
            // A secret kept from before a provider switch is used again.
            case Providers.ApiKey when exists && HasApiKey(name):
            case Providers.Token when exists && HasToken(name):
                log.Collect(SetProvider(name, provider));
                break;
            case Providers.ApiKey or Providers.Token:
                log.Collect(SetProvider(name, Providers.Login));
                log.NeedSecret.Add(provider == Providers.ApiKey
                    ? $"{name}: set the key with `cr provider {name} apikey`"
                    : $"{name}: set the token with `cr provider {name} token`");
                break;
            case Providers.Vertex:
                log.Collect(SetProvider(name, Providers.Vertex, vertex));
                break;
            default:
                log.Collect(SetProvider(name, Providers.Login));
                break;
        }

        if (envVars != null)
        {
            log.Collect(ReplaceEnv(name, envVars));
            if (envVars.Count > 0)
                log.EnvNames.Add($"{name}: {string.Join(", ", envVars.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
        }

        // Owned items are only added. An import never shares a profile's own copies back, even with force.
        var have = Get(name).Overrides.ToList();
        foreach (var item in overrides.Where(i => !have.Contains(i))) Own(name, item);

        // Exclusions too are only added, never removed.
        if (p.TryGetProperty("exclude", out var ex) && ex.ValueKind == JsonValueKind.Array)
            foreach (var x in ex.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.String || Get(name).Exclude.Contains(x.GetString()!)) continue;
                try { log.Collect(Exclude(name, x.GetString()!)); }
                catch (ClauderooException e) { log.Notices.Add($"{name}: {x.GetString()} not excluded: {e.Message}"); }
            }

        // Only a startDir present in the file is applied, and only one that exists on this machine.
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("startDir", out var sd)
            && sd.ValueKind is JsonValueKind.String or JsonValueKind.Null)
        {
            var dir = sd.ValueKind == JsonValueKind.String ? sd.GetString() : null;
            if (string.IsNullOrWhiteSpace(dir)) log.StartDirs[name] = null;
            else if (IsUsableStartDir(dir)) log.StartDirs[name] = dir;
            else log.Notices.Add($"{name}: start folder {dir} doesn't exist on this machine, so it wasn't set.");
        }

        (exists ? log.Updated : log.Created).Add(name);
    }

    /// <summary>
    /// The export's autoSwitch block as typed settings, always off: threshold (6 to 100, as `cr auto` allows) and the order,
    /// keeping only names that exist after the import. Null (with a Skipped line) when the threshold isn't valid.
    /// </summary>
    private static AutoSwitchSettings? ReadAutoSwitch(JsonElement auto, ClauderooConfig cfg, List<string> skipped, List<string> notices)
    {
        // Never turned on by an import, whatever the file's "enabled" says: switching the default is the user's call on
        // this machine (cr auto on, or the tray).
        var s = new AutoSwitchSettings { Enabled = false };
        if (auto.TryGetProperty("threshold", out var th) && th.ValueKind != JsonValueKind.Null)
        {
            if (th.ValueKind != JsonValueKind.Number || !th.TryGetInt32(out var t) || t is < 6 or > 100)
            {
                skipped.Add($"auto-switch: threshold {th} isn't a whole number from 6 to 100");
                return null;
            }
            s.Threshold = t;
        }
        if (auto.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Array)
        {
            var names = order.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).ToList();
            var unknown = names.Where(n => !cfg.Profiles.ContainsKey(n)).Distinct().ToList();
            s.Order = names.Where(cfg.Profiles.ContainsKey).Distinct().ToList();
            if (unknown.Count > 0)
                notices.Add($"auto-switch: left out of the order, no such profile here: {string.Join(", ", unknown)}");
        }
        return s;
    }

    private static bool IsUsableStartDir(string dir)
    {
        try { return Path.IsPathFullyQualified(dir) && Directory.Exists(dir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
