using System.Globalization;
using System.Text.Json;

namespace Clauderoo.Core;

/// <summary>The "autoSwitch" block of config.json.</summary>
public class AutoSwitchSettings
{
    public bool Enabled { get; set; }
    public int Threshold { get; set; } = 90;
    public List<string> Order { get; set; } = [];
}

/// <summary>What the engine remembers between decisions, in %root%\autoswitch.json.</summary>
public record AutoSwitchState(DateTimeOffset? LastSwitch, string? SwitchedFrom, bool NoTargetNotified, bool RecoveredNotified);

public abstract record Decision
{
    private Decision() { }

    public sealed record NoAction : Decision;
    public sealed record SwitchTo(string Profile, string Reason) : Decision;
    public sealed record NoTarget(string Reason) : Decision;
    /// <summary>The profile we switched away from has room again.</summary>
    public sealed record Recovered(string Profile) : Decision;
}

public static class AutoSwitch
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);
    public const int Hysteresis = 5;

    /// <summary>
    /// Pure decision. Switches when the default's fresh 5h or 7d window reaches the threshold, to the first profile in
    /// order (or list order when empty) with room. NoTarget and Recovered are reported once: the state flags say whether
    /// they were already announced.
    /// </summary>
    public static Decision Decide(ClauderooConfig cfg, IReadOnlyDictionary<string, UsageReading> usage, DateTimeOffset now, AutoSwitchState state)
    {
        var settings = cfg.AutoSwitch;
        var current = cfg.DefaultProfile;
        if (settings is not { Enabled: true } || string.IsNullOrEmpty(current)) return new Decision.NoAction();

        var threshold = settings.Threshold;
        var margin = threshold - Hysteresis;

        if (usage.TryGetValue(current, out var cur) && IsFresh(cur, now) && Over(cur, threshold) is { } hit)
        {
            if (state.LastSwitch is { } last && now - last < Cooldown) return new Decision.NoAction();

            var reason = $"{current} is at {Math.Round(hit.Percent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}% of its {(hit.Name == "5h" ? "5-hour" : "7-day")} limit.";
            var explicitOrder = settings.Order.Count > 0;
            var candidates = explicitOrder ? settings.Order : cfg.Profiles.Keys.ToList();
            foreach (var name in candidates)
            {
                if (name == current || !cfg.Profiles.TryGetValue(name, out var entry)) continue;
                switch (Providers.Of(entry))
                {
                    case Providers.Login:
                        if (usage.TryGetValue(name, out var r) && HasRoom(r, now, margin)) return new Decision.SwitchTo(name, reason);
                        break;
                    case Providers.Vertex or Providers.ApiKey:
                        if (explicitOrder) return new Decision.SwitchTo(name, reason); // no limits to check
                        break;
                    // Any other provider (token, unknown) is never a target.
                }
            }
            return state.NoTargetNotified ? new Decision.NoAction() : new Decision.NoTarget(reason);
        }

        if (!state.RecoveredNotified && state.SwitchedFrom is { } from && from != current
            && cfg.Profiles.TryGetValue(from, out var fromEntry) && Providers.Of(fromEntry) == Providers.Login
            && usage.TryGetValue(from, out var fr) && HasRoom(fr, now, margin))
            return new Decision.Recovered(from);

        return new Decision.NoAction();
    }

    private static bool IsFresh(UsageReading r, DateTimeOffset now) => now - r.FetchedAt <= Freshness;

    // Only the session (5h) and weekly (7d) windows count; per-model windows never trigger or block a switch.
    private static IEnumerable<UsageWindow> Main(UsageReading r) => r.Windows.Where(w => w.Name is "5h" or "7d");

    private static UsageWindow? Over(UsageReading r, int threshold) =>
        Main(r).Where(w => w.Percent >= threshold).OrderByDescending(w => w.Percent).ThenBy(w => w.Name, StringComparer.Ordinal).FirstOrDefault();

    // A reading with no 5h/7d window at all says nothing about room, so it doesn't qualify.
    private static bool HasRoom(UsageReading r, DateTimeOffset now, int margin)
    {
        if (!IsFresh(r, now)) return false;
        var windows = Main(r).ToList();
        return windows.Count > 0 && windows.All(w => w.Percent < margin);
    }
}

/// <summary>%root%\autoswitch.json under the machine-wide mutex Local\clauderoo-autoswitch, written atomically.</summary>
public sealed class AutoSwitchStore(ClauderooPaths paths)
{
    private const string MutexName = @"Local\clauderoo-autoswitch";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static AutoSwitchState Empty => new(null, null, false, false);
    private string FilePath => Path.Combine(paths.Root, "autoswitch.json");

    public AutoSwitchState Load()
    {
        var state = Empty;
        Locked(() => state = LoadOrThrow());
        return state;
    }

    public void Save(AutoSwitchState state) =>
        Locked(() =>
        {
            try
            {
                Directory.CreateDirectory(paths.Root);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(state, Json));
            }
            catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
        });

    private static void Locked(Action action)
    {
        using var mutex = new Mutex(false, MutexName);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Timed out waiting for the auto-switch file lock.");
            action();
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    // A corrupt file is treated as fresh state; an unreadable one throws so nothing gets overwritten.
    private AutoSwitchState LoadOrThrow()
    {
        if (!File.Exists(FilePath)) return Empty;
        string text;
        try { text = File.ReadAllText(FilePath); }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
        try { return JsonSerializer.Deserialize<AutoSwitchState>(text, Json) ?? Empty; }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return Empty; }
    }
}
