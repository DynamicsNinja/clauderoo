using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core;

public class SyncReport
{
    public List<string> Notices { get; } = [];
    public List<string> Problems { get; } = [];
}

public class Syncer(ClauderooConfig cfg, ClauderooPaths paths, Func<DateTime> now)
{
    private readonly Func<DateTime> _now = now;

    /// <summary>Exe path written into apiKeyHelper for apikey profiles.</summary>
    public string HelperExe { get; set; } = HelperExePath.Resolve();

    public SyncReport Sync(string profile) => Sync(profile, justShared: null);

    /// <summary>
    /// Syncs one profile. <paramref name="justShared"/> names an item the user just chose to share again; a backup of
    /// that item's previous copy is then reported as expected rather than as lost changes.
    /// </summary>
    public SyncReport Sync(string profile, string? justShared)
    {
        _justShared = justShared;
        try { return SyncCore(profile); }
        finally { _justShared = null; }
    }

    private string? _justShared;

    private SyncReport SyncCore(string profile)
    {
        var report = new SyncReport();
        if (!cfg.Profiles.TryGetValue(profile, out var entry))
            throw new ClauderooException($"No such profile: {profile}");
        if (entry.Home) return report;

        var dir = paths.ProfileDir(profile);
        Directory.CreateDirectory(dir);
        SyncDirs(profile, entry, dir, report);
        SyncState state;
        try { state = SyncState.Load(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Problems.Add($"{profile}: could not read sync state: {e.Message}");
            return report;
        }
        SyncFiles(profile, entry, dir, state, report);
        try { SyncMcp(profile, entry, dir, state, report); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            report.Problems.Add($"{profile}: could not sync mcpServers: {e.Message}");
        }
        try { state.Save(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Problems.Add($"{profile}: could not save sync state: {e.Message}");
        }
        return report;
    }

    public SyncReport SyncAll()
    {
        var all = new SyncReport();
        foreach (var (name, entry) in cfg.Profiles)
        {
            if (entry.Home) continue;
            try
            {
                var r = Sync(name);
                all.Notices.AddRange(r.Notices);
                all.Problems.AddRange(r.Problems);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ClauderooException)
            {
                all.Problems.Add($"{name}: could not sync: {e.Message}");
            }
        }
        return all;
    }

    private void SyncDirs(string profile, ProfileEntry entry, string dir, SyncReport report)
    {
        foreach (var d in SharedItems.Dirs)
        {
            if (entry.Overrides.Contains(d)) continue;
            var source = Path.Combine(cfg.HomeDir, d);
            if (!Directory.Exists(source)) continue;

            var link = Path.Combine(dir, d);
            var target = Junction.GetTarget(link);
            if (target == null)
            {
                if (File.Exists(link))
                    report.Problems.Add($"{profile}: {d} is a file, not a shared folder");
                else if (Directory.Exists(link))
                    report.Problems.Add($"{profile}: {d} is a real folder, not shared (run 'clauderoo share {profile} {d}' to share it)");
                else
                {
                    try { Junction.Create(link, source); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ClauderooException)
                    {
                        report.Problems.Add($"{profile}: could not sync {d}: {e.Message}");
                    }
                }
                continue;
            }
            if (!string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(source), StringComparison.OrdinalIgnoreCase))
                report.Problems.Add($"{profile}: {d} is linked to {target}, not to {source}");
        }
    }

    private static void CopyAtomic(string source, string target)
    {
        var tmp = AtomicFile.TmpName(target);
        try
        {
            File.Copy(source, tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    private string Stamp => _now().ToString("yyyyMMdd-HHmmss");

    private static string HashBytes(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    private const string Settings = "settings.json";

    private void SyncFiles(string profile, ProfileEntry entry, string dir, SyncState state, SyncReport report)
    {
        foreach (var f in SharedItems.Files)
        {
            try
            {
                if (f == Settings)
                {
                    SyncSettings(profile, entry, dir, state, report);
                    continue;
                }
                if (entry.Overrides.Contains(f)) continue;
                var source = Path.Combine(cfg.HomeDir, f);
                if (!File.Exists(source)) continue;
                SyncCopy(profile, f, dir, source, state, report);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report.Problems.Add($"{profile}: could not sync {f}: {e.Message}");
            }
        }
    }

    /// <summary>Today's path: the profile's file is a byte-for-byte copy of home's.</summary>
    private void SyncCopy(string profile, string f, string dir, string source, SyncState state, SyncReport report) =>
        SyncShared(profile, f, dir, HashFile(source), () => CopyAtomic(source, Path.Combine(dir, f)), state, report);

    /// <summary>
    /// Brings a shared file to the expected content (hash <paramref name="expectedHash"/>). If the session changed it since
    /// clauderoo last wrote it, the session's version is backed up first and a notice is reported.
    /// </summary>
    private void SyncShared(string profile, string f, string dir, string expectedHash, Action write, SyncState state,
        SyncReport report, bool noBackup = false)
    {
        var target = Path.Combine(dir, f);
        if (File.Exists(target))
        {
            var tgtHash = HashFile(target);
            state.Hashes.TryGetValue(f, out var recorded);
            if (tgtHash != recorded && tgtHash != expectedHash && !noBackup)
            {
                var backup = $"{f}.bak-{Stamp}";
                File.Copy(target, Path.Combine(dir, backup), overwrite: true);
                report.Notices.Add(Notice(profile, f, backup));
            }
            if (tgtHash != expectedHash) write();
        }
        else
        {
            write();
        }
        state.Hashes[f] = expectedHash;
    }

    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static byte[] SerializeSettings(JsonObject o) => Encoding.UTF8.GetBytes(o.ToJsonString(SettingsJson));

    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// Parses a JSON object, or null if the text is not one (including duplicate keys). The tree is walked inside the
    /// try because JsonNode otherwise reports duplicate keys lazily (ArgumentException) on first access.
    /// </summary>
    private static JsonObject? ParseObject(string text)
    {
        try
        {
            if (JsonNode.Parse(text, documentOptions: StrictJson) is not JsonObject o) return null;
            Materialize(o);
            return o;
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { return null; }
    }

    private static void Materialize(JsonNode? n)
    {
        if (n is JsonObject o) foreach (var (_, v) in o) Materialize(v);
        else if (n is JsonArray a) foreach (var v in a) Materialize(v);
    }

    /// <summary>True when home's settings carry keys that only a provider overlay may set (spec decision 4).</summary>
    private static bool HasReservedKeys(string homeText)
    {
        var o = ParseObject(homeText);
        if (o == null) return false; // not a JSON object: byte copy, as today
        if (ProviderOverlay.ReservedTop.Any(o.ContainsKey)) return true;
        return o["env"] is JsonObject env && ProviderOverlay.ReservedEnv.Any(env.ContainsKey);
    }

    private static void RecordManaged(SyncState state, Overlay overlay)
    {
        state.ManagedEnv = [.. overlay.Env.Keys];
        state.ManagedTop = [.. overlay.Top.Keys];
        // Vertex: every model key, "" when none was written. Other providers: empty, meaning "no vertex baseline".
        state.ManagedModels = overlay.Env.ContainsKey(VertexMarker)
            ? Models.ToDictionary(m => m.Key, m => overlay.Env.GetValueOrDefault(m.Key) ?? "", StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private const string VertexMarker = "CLAUDE_CODE_USE_VERTEX";

    private static string? BlankToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// What clauderoo last wrote for a model key: (true, value-or-null) when known, (false, _) when clauderoo has no
    /// vertex baseline for it (e.g. the profile was login/apikey at the last write).
    /// </summary>
    private static (bool Known, string? Value) LastWritten(SyncState state, ModelKey m, string? configured)
    {
        if (state.ManagedModels is { } mm)
            return mm.TryGetValue(m.Key, out var w) ? (true, BlankToNull(w)) : (false, null);
        // State from before ManagedModels existed: the configured value, if the last write was a vertex overlay.
        return state.ManagedEnv.Contains(VertexMarker) ? (true, configured) : (false, null);
    }

    private record ModelKey(string Key, string Family, Func<VertexSettings, string?> Get, Action<VertexSettings, string> Set);

    /// <summary>The only provider keys a session may change: Claude's "update model" prompt writes these.</summary>
    private static readonly ModelKey[] Models =
    [
        new("ANTHROPIC_DEFAULT_SONNET_MODEL", "Sonnet", v => v.SonnetModel, (v, s) => v.SonnetModel = s),
        new("ANTHROPIC_DEFAULT_OPUS_MODEL", "Opus", v => v.OpusModel, (v, s) => v.OpusModel = s),
        new("ANTHROPIC_DEFAULT_HAIKU_MODEL", "Haiku", v => v.HaikuModel, (v, s) => v.HaikuModel = s),
    ];

    /// <summary>State hash key for a profile-owned settings.json (kept apart from the shared copy's hash).</summary>
    private const string OwnedSettingsKey = "settings.json (owned)";

    private static string DecodeText(byte[] b)
    {
        using var r = new StreamReader(new MemoryStream(b), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    /// <summary>
    /// Adopts model keys a session changed in a vertex profile's settings.json into the profile config (spec
    /// 2026-11-17). Only when the file differs from what clauderoo last wrote; a key counts as changed when it holds a
    /// non-blank string other than the value clauderoo last wrote, and only while the config still holds that last
    /// written value (a newer config save always wins). Returns the session's parsed file when something was adopted.
    /// </summary>
    private JsonObject? AdoptModels(string profile, ProfileEntry entry, string dir, string hashKey, SyncState state, SyncReport report)
    {
        if (Providers.Of(entry) != Providers.Vertex) return null;
        var file = Path.Combine(dir, Settings);
        if (!File.Exists(file) || !state.Hashes.TryGetValue(hashKey, out var recorded)) return null; // first sync: never
        var bytes = File.ReadAllBytes(file);
        if (HashBytes(bytes) == recorded) return null; // unchanged since clauderoo wrote it: config wins
        if (ParseObject(DecodeText(bytes)) is not { } current || current["env"] is not JsonObject env) return null;

        var changes = new List<(ModelKey Model, string Value)>();
        foreach (var m in Models)
        {
            if (env[m.Key] is not JsonValue jv || !jv.TryGetValue<string>(out var raw) || BlankToNull(raw) is not { } value)
                continue; // removed or blanked: restored, not adopted
            var configured = BlankToNull(entry.Vertex is { } v ? m.Get(v) : null);
            var (known, lastWritten) = LastWritten(state, m, configured);
            if (known && configured == lastWritten && value != lastWritten) changes.Add((m, value));
        }
        if (changes.Count == 0 || !SaveAdopted(profile, entry, changes, report)) return null;

        entry.Vertex ??= new VertexSettings();
        foreach (var (m, value) in changes)
        {
            m.Set(entry.Vertex, value);
            env[m.Key] = value; // trimmed, so a padded value alone doesn't count as an "other" change
            report.Notices.Add($"{profile}: {m.Family} model updated to {value} by Claude.");
        }
        return current;
    }

    /// <summary>
    /// Writes adopted models into the saved config, patching the on-disk copy so a concurrent save of other fields
    /// is kept. A key whose saved value no longer matches this sync's config (saved meanwhile) is left to that newer value.
    /// </summary>
    private bool SaveAdopted(string profile, ProfileEntry entry, List<(ModelKey Model, string Value)> changes, SyncReport report)
    {
        try
        {
            var disk = ConfigStore.Load(paths);
            if (disk == null || !disk.Profiles.TryGetValue(profile, out var de) || Providers.Of(de) != Providers.Vertex)
                return false;
            changes.RemoveAll(c => (de.Vertex is { } dv ? c.Model.Get(dv) : null)
                != (entry.Vertex is { } v ? c.Model.Get(v) : null));
            if (changes.Count == 0) return false;
            de.Vertex ??= new VertexSettings();
            foreach (var (m, value) in changes) m.Set(de.Vertex, value);
            ConfigStore.Save(paths, disk);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ClauderooException)
        {
            report.Problems.Add($"{profile}: could not save the model Claude chose: {e.Message}");
            return false;
        }
    }

    private void SyncSettings(string profile, ProfileEntry entry, string dir, SyncState state, SyncReport report)
    {
        var owned = entry.Overrides.Contains(Settings);
        var adoptedFrom = AdoptModels(profile, entry, dir, owned ? OwnedSettingsKey : Settings, state, report);

        var overlay = ProviderOverlay.Build(profile, entry, HelperExe);
        var noOverlay = overlay.Env.Count == 0 && overlay.Top.Count == 0
            && state.ManagedEnv.Count == 0 && state.ManagedTop.Count == 0;

        if (owned)
        {
            if (noOverlay) return; // owned and nothing to manage: untouched, as today
            SyncOwnedSettings(profile, dir, overlay, state, report);
            return;
        }
        state.Hashes.Remove(OwnedSettingsKey);

        var source = Path.Combine(cfg.HomeDir, Settings);
        var homeExists = File.Exists(source);
        if (noOverlay)
        {
            if (!homeExists) return;
            // Byte-identical to home (today's behaviour) unless home carries provider-reserved keys to drop.
            if (!HasReservedKeys(File.ReadAllText(source)))
            {
                SyncCopy(profile, Settings, dir, source, state, report);
                return;
            }
        }

        JsonObject home;
        if (!homeExists) home = new JsonObject();
        else
        {
            var parsed = ParseObject(File.ReadAllText(source));
            if (parsed == null)
            {
                // Claude may be mid-write: wait once and retry before reporting.
                Thread.Sleep(200);
                parsed = ParseObject(File.ReadAllText(source));
            }
            if (parsed == null)
            {
                report.Problems.Add($"{profile}: home settings.json is not valid JSON; settings not synced");
                return;
            }
            home = parsed;
        }

        JsonObject generated;
        // Home never contains clauderoo-written keys, so nothing previously managed needs removing here.
        try { generated = ProviderOverlay.Apply(home, overlay, [], []); }
        catch (ClauderooException)
        {
            report.Problems.Add($"{profile}: home settings.json \"env\" is not an object; settings not synced");
            return;
        }

        var bytes = SerializeSettings(generated);
        var target = Path.Combine(dir, Settings);
        // When the adopted model keys were the session's only change, there is nothing of its own to back up.
        var onlyModelsChanged = adoptedFrom != null && JsonNode.DeepEquals(adoptedFrom, generated);
        SyncShared(profile, Settings, dir, HashBytes(bytes), () => AtomicFile.WriteAllBytes(target, bytes), state, report,
            noBackup: onlyModelsChanged);
        RecordManaged(state, overlay);
    }

    /// <summary>Test hook: runs just before an owned settings.json is replaced.</summary>
    public Action? BeforeOwnedSettingsCommit { get; set; }

    /// <summary>Applies the overlay in place to a profile-owned settings.json, touching only managed/reserved keys.</summary>
    private void SyncOwnedSettings(string profile, string dir, Overlay overlay, SyncState state, SyncReport report)
    {
        var file = Path.Combine(dir, Settings);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sig = Signature(file);
            JsonObject current;
            byte[]? raw = null;
            if (sig != null)
            {
                raw = File.ReadAllBytes(file);
                var parsed = ParseObject(DecodeText(raw));
                if (parsed == null)
                {
                    report.Problems.Add($"{profile}: settings.json is not valid JSON; provider settings not applied");
                    return;
                }
                current = parsed;
            }
            else current = new JsonObject();

            JsonObject merged;
            try { merged = ProviderOverlay.Apply(current, overlay, state.ManagedEnv, state.ManagedTop); }
            catch (ClauderooException)
            {
                report.Problems.Add($"{profile}: settings.json \"env\" is not an object; provider settings not applied");
                return;
            }

            string? notice = null;
            var final = raw;
            if (sig == null || !JsonNode.DeepEquals(current, merged))
            {
                final = SerializeSettings(merged);
                var tmp = AtomicFile.TmpName(file);
                try
                {
                    // First overlay on a file clauderoo never managed: hand-written provider keys are about to be
                    // replaced, so keep a copy of the user's file.
                    if (sig != null && state.ManagedEnv.Count == 0 && state.ManagedTop.Count == 0
                        && ReplacesReservedKeys(current, merged))
                    {
                        var backup = $"{Settings}.bak-{Stamp}";
                        File.Copy(file, Path.Combine(dir, backup), overwrite: true);
                        notice = $"{profile}: provider settings replaced hand-written provider keys in settings.json (saved {backup}).";
                    }
                    File.WriteAllBytes(tmp, final);
                    BeforeOwnedSettingsCommit?.Invoke();
                    if (Signature(file) != sig)
                    {
                        File.Delete(tmp);
                        continue; // the session wrote the file meanwhile: re-read and redo the merge
                    }
                    File.Move(tmp, file, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    throw;
                }
            }
            if (notice != null) report.Notices.Add(notice);
            RecordManaged(state, overlay);
            state.Hashes[OwnedSettingsKey] = HashBytes(final!);
            return;
        }
        report.Problems.Add($"{profile}: settings.json kept changing; provider settings not applied");
    }

    /// <summary>True when <paramref name="current"/> holds a reserved env/top key whose value differs in <paramref name="merged"/>.</summary>
    private static bool ReplacesReservedKeys(JsonObject current, JsonObject merged)
    {
        foreach (var k in ProviderOverlay.ReservedTop)
            if (current.TryGetPropertyValue(k, out var v) && !JsonNode.DeepEquals(v, merged[k])) return true;
        if (current["env"] is not JsonObject env) return false;
        var mergedEnv = merged["env"] as JsonObject;
        foreach (var k in ProviderOverlay.ReservedEnv)
            if (env.TryGetPropertyValue(k, out var v) && !JsonNode.DeepEquals(v, mergedEnv?[k])) return true;
        return false;
    }

    private string Notice(string profile, string item, string backup) =>
        item == _justShared
            ? $"{profile}: now sharing {item} from {cfg.HomeProfileName}; previous copy saved as {backup}."
            : $"{profile}: your changes to {item} were replaced by {cfg.HomeProfileName}'s (saved {backup}). Use 'clauderoo own {profile} {item}' to keep {profile}'s own.";

    private static string McpHash(JsonNode? node) => HashBytes(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null"));

    /// <summary>Test hook: runs just before the profile .claude.json is replaced.</summary>
    public Action? BeforeMcpCommit { get; set; }

    private static (DateTime, long)? Signature(string file) =>
        File.Exists(file) ? (File.GetLastWriteTimeUtc(file), new FileInfo(file).Length) : null;

    private JsonNode? ReadHomeMcp(string profile, SyncReport report, out bool ok)
    {
        ok = true;
        if (!File.Exists(cfg.HomeJson)) return null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(cfg.HomeJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var node = JsonNode.Parse(fs, documentOptions: StrictJson);
                Materialize(node); // duplicate keys surface here (ArgumentException), not later outside this catch
                return (node as JsonObject)?[SharedItems.McpServers];
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            {
                // Claude may be mid-write: wait once and retry before reporting.
                if (attempt == 0) { Thread.Sleep(200); continue; }
                report.Problems.Add($"{profile}: home .claude.json is not valid JSON; MCP servers not synced");
                ok = false;
                return null;
            }
        }
    }

    private void SyncMcp(string profile, ProfileEntry entry, string dir, SyncState state, SyncReport report)
    {
        const string key = SharedItems.McpServers;
        if (entry.Overrides.Contains(key)) return;

        var homeMcp = ReadHomeMcp(profile, report, out var ok);
        if (!ok) return;
        var homeHash = McpHash(homeMcp);

        var file = Path.Combine(dir, ".claude.json");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sig = Signature(file);
            JsonObject root;
            if (sig != null)
            {
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(file), documentOptions: StrictJson) as JsonObject ?? throw new JsonException();
                    Materialize(root);
                }
                catch (Exception e) when (e is JsonException or ArgumentException)
                {
                    report.Problems.Add($"{profile}: .claude.json is not valid JSON; MCP servers not synced");
                    return;
                }
            }
            else root = new JsonObject();

            var current = root[key];
            var curHash = McpHash(current);
            state.Hashes.TryGetValue(key, out var recorded);
            var hasUserValue = current != null && !(current is JsonObject { Count: 0 });
            string? notice = null;
            if (hasUserValue && curHash != recorded && curHash != homeHash && sig != null)
            {
                var backup = $"mcpServers.bak-{Stamp}.json";
                File.WriteAllText(Path.Combine(dir, backup), current?.ToJsonString() ?? "null");
                notice = Notice(profile, key, backup);
            }

            if (curHash != homeHash || sig == null)
            {
                if (homeMcp == null) root.Remove(key);
                else root[key] = homeMcp.DeepClone();
                var tmp = AtomicFile.TmpName(file);
                try
                {
                    File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    BeforeMcpCommit?.Invoke();
                    if (Signature(file) != sig)
                    {
                        File.Delete(tmp);
                        continue; // Claude wrote the file meanwhile: re-read and redo the merge
                    }
                    File.Move(tmp, file, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    throw;
                }
            }
            if (notice != null) report.Notices.Add(notice);
            state.Hashes[key] = homeHash;
            return;
        }
        report.Problems.Add($"{profile}: .claude.json kept changing; MCP servers not synced");
    }
}
