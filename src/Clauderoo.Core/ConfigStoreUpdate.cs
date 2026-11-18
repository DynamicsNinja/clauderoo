using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core;

/// <summary>
/// Cross-process writes and format versions for config.json.
/// <para>
/// <b>Locking.</b> The CLI, the tray, the MCP server and <c>cr auto --once</c> can all change config.json at once. Every
/// change goes through <see cref="Update"/> (or <see cref="UpdateWhen"/>): it takes the machine-wide mutex
/// <c>Local\clauderoo-config-&lt;hash of the config path&gt;</c>, re-reads the file, applies the change, writes it
/// atomically and lets go, so no writer works from a stale copy. Plain reads (<see cref="Load"/>) take no lock: the file
/// is always replaced in one move, so a reader sees either the old or the new file, never half of one.
/// </para>
/// <para>
/// <b>Lock order.</b> The config lock is a leaf: nothing takes another clauderoo lock while holding it. The change
/// passed to <see cref="Update"/> only edits the config in memory: no syncing, no env vars, no other state files, and
/// no nested <see cref="Update"/> (that throws). The other named mutexes (<c>clauderoo-sessions</c>,
/// <c>clauderoo-autoswitch</c>, <c>clauderoo-usage</c>) are leaves too and never touch config.json while held. The
/// tray's in-process <c>WriteGate</c> is outermost: it may be held while the config lock is taken, never the reverse.
/// The log file's mutex is the innermost of all: it may be taken while any other lock is held, and nothing is taken
/// while it is held. With every lock a leaf (or the gate on the outside) no two processes can wait on each other.
/// </para>
/// <para>
/// <b>Format versions.</b> config.json carries <c>schemaVersion</c> (<see cref="CurrentSchemaVersion"/>; a file
/// without it is a version 1 file). An older file is migrated in memory when it is read, one step at a time, and saved
/// in the new format on the next write. A file from a newer clauderoo is still read (fields this version doesn't know
/// are kept in <c>Extra</c>) with a warning in the log, but writes are refused with a clear message: this version can't
/// know what the newer format expects, so it never writes over it (or stamps it back down to an older version).
/// </para>
/// </summary>
public static partial class ConfigStore
{
    /// <summary>The config.json format this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>How long a writer waits for another process's change to finish.</summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Steps that bring an older file up to date, keyed by the version they upgrade from (step N turns an N file into
    /// an N+1 file). Empty while version 1 is the only one.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, Action<JsonObject>> Migrations = new Dictionary<int, Action<JsonObject>>();

    [ThreadStatic] private static int t_updating;
    private static readonly HashSet<string> WarnedNewer = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Changes config.json safely across processes: takes the config lock, reads the file fresh, runs
    /// <paramref name="change"/> on it, saves it atomically and lets go. If <paramref name="change"/> throws, nothing is
    /// saved. Throws <see cref="ClauderooException"/> when clauderoo isn't set up, when the lock can't be had in time,
    /// or when the file is from a newer clauderoo. <paramref name="change"/> must only edit the config in memory.
    /// </summary>
    public static T Update<T>(ClauderooPaths p, Func<ClauderooConfig, T> change, [CallerMemberName] string action = "")
    {
        var result = default(T)!;
        UpdateCore(p, cfg => { result = change(cfg); return true; }, action);
        return result;
    }

    /// <inheritdoc cref="Update{T}"/>
    /// <returns>The config as saved.</returns>
    public static ClauderooConfig Update(ClauderooPaths p, Action<ClauderooConfig> change, [CallerMemberName] string action = "")
    {
        ClauderooConfig saved = null!;
        UpdateCore(p, cfg => { change(cfg); saved = cfg; return true; }, action);
        return saved;
    }

    /// <summary>Like <see cref="Update"/>, but saves only when <paramref name="change"/> returns true. Returns whether it saved.</summary>
    public static bool UpdateWhen(ClauderooPaths p, Func<ClauderooConfig, bool> change, [CallerMemberName] string action = "") =>
        UpdateCore(p, change, action);

    /// <summary>Writes the first config.json (init), under the config lock; refuses when one appeared meanwhile.</summary>
    internal static void Create(ClauderooPaths p, ClauderooConfig c) =>
        WithLock(p, LockTimeout, () =>
        {
            if (File.Exists(p.ConfigFile)) throw new ClauderooException("clauderoo is already initialised.");
            Write(p, c);
            AppLog.Info("Settings created (init).");
            return true;
        });

    private static bool UpdateCore(ClauderooPaths p, Func<ClauderooConfig, bool> change, string action)
    {
        if (t_updating > 0)
            throw new InvalidOperationException("ConfigStore.Update can't be nested: the outer save would undo the inner one.");
        return WithLock(p, LockTimeout, () =>
        {
            t_updating++;
            try
            {
                var cfg = Load(p) ?? throw new ClauderooException("Run 'clauderoo init' first.");
                RefuseNewer(p, cfg.SchemaVersion);
                if (!change(cfg)) return false;
                Write(p, cfg);
                AppLog.Info($"Settings saved ({(string.IsNullOrEmpty(action) ? "update" : action)}).");
                return true;
            }
            finally { t_updating--; }
        });
    }

    /// <summary>The config lock's name: one per config file, so separate setups (and tests) never wait on each other.</summary>
    internal static string MutexName(ClauderooPaths p)
    {
        var full = Path.GetFullPath(p.ConfigFile).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
        return $@"Local\clauderoo-config-{hash}";
    }

    /// <summary>Runs <paramref name="work"/> holding the config lock (re-entrant on the same thread).</summary>
    internal static T WithLock<T>(ClauderooPaths p, TimeSpan timeout, Func<T> work)
    {
        using var mutex = new Mutex(false, MutexName(p));
        var held = false;
        try
        {
            try { held = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException)
            {
                // The last holder exited mid-change. The file is only ever replaced in one move, so it is whole.
                held = true;
                AppLog.Warn("A clauderoo process exited while saving settings; carrying on with the saved file.");
            }
            if (!held)
            {
                AppLog.Warn("Timed out waiting for another clauderoo process to finish saving settings.");
                throw new ClauderooException(
                    $"Another clauderoo window or command is still saving settings (waited {timeout.TotalSeconds:0.#} s). Try again in a moment.");
            }
            return work();
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    private static void RefuseNewer(ClauderooPaths p, int version)
    {
        if (version > CurrentSchemaVersion)
            throw new ClauderooException(NewerMessage(p, version) + " Update clauderoo to change settings; nothing was saved.");
    }

    private static string NewerMessage(ClauderooPaths p, int version) =>
        $"{p.ConfigFile} was written by a newer clauderoo (format {version}; this version knows up to {CurrentSchemaVersion}).";

    /// <summary>The version of the file on disk, or 0 when there is none or it can't be read.</summary>
    private static int DiskSchemaVersion(ClauderooPaths p)
    {
        try
        {
            if (!File.Exists(p.ConfigFile)) return 0;
            return JsonNode.Parse(File.ReadAllText(p.ConfigFile)) is JsonObject o ? VersionOf(o) : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ClauderooException) { return 0; }
    }

    private static int VersionOf(JsonObject o)
    {
        if (!o.TryGetPropertyValue("schemaVersion", out var v) || v == null) return 1; // written before versions existed
        if (v is JsonValue jv && jv.TryGetValue<int>(out var n) && n >= 1) return n;
        if (v is JsonValue d && d.TryGetValue<double>(out var x) && x >= 1 && x <= int.MaxValue && x == Math.Floor(x)) return (int)x;
        throw new ClauderooException("config.json has an invalid schemaVersion (expected a whole number, 1 or more).");
    }

    private static ClauderooConfig? Parse(ClauderooPaths p, string json) =>
        Parse(json, CurrentSchemaVersion, Migrations, v => WarnNewerOnce(p, v));

    /// <summary>Reads config JSON, migrating an older format step by step up to <paramref name="current"/>.</summary>
    internal static ClauderooConfig? Parse(string json, int current, IReadOnlyDictionary<int, Action<JsonObject>> steps,
        Action<int>? newer = null)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false });
        if (node is not JsonObject o) return node?.Deserialize<ClauderooConfig>(Options);

        var version = VersionOf(o);
        if (version > current) newer?.Invoke(version);
        for (; version < current; version++)
        {
            if (!steps.TryGetValue(version, out var step))
                throw new ClauderooException($"config.json is in format {version}, which this version can't upgrade.");
            step(o);
            o["schemaVersion"] = version + 1;
        }
        o["schemaVersion"] = version;
        return o.Deserialize<ClauderooConfig>(Options);
    }

    private static void WarnNewerOnce(ClauderooPaths p, int version)
    {
        lock (WarnedNewer)
            if (!WarnedNewer.Add(p.ConfigFile)) return;
        AppLog.Warn(NewerMessage(p, version) + " Reading it anyway; changes are refused until clauderoo is updated.");
    }
}
