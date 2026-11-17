using System.Globalization;
using System.Text.Json;

namespace Clauderoo.Core;

/// <summary>The "autoSwitch" block of config.json.</summary>
public class AutoSwitchSettings
{
    public bool Enabled { get; set; }
    public int Threshold { get; set; } = 90;
    private List<string> _order = [];
    /// <summary>A hand-edited <c>"order": null</c> reads as empty.</summary>
    public List<string> Order { get => _order; set => _order = value ?? []; }
}

/// <summary>
/// What the engine remembers between decisions, in %root%\autoswitch.json. The two Notified flags only apply while the
/// default profile is <see cref="FlagsFor"/>; for any other default they read as false (see <see cref="AutoSwitch.Next"/>).
/// <see cref="ManualHold"/> is the profile the user last made the default by hand: while it is the default, nothing
/// switches away from it, until it is back under the threshold (it holds for the current crossing only).
/// </summary>
public record AutoSwitchState(DateTimeOffset? LastSwitch, string? SwitchedFrom, bool NoTargetNotified, bool RecoveredNotified,
    string? FlagsFor = null, string? ManualHold = null);

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
        // The user picked this default by hand: leave it alone until it is back under the threshold (see Next).
        if (state.ManualHold == current) return new Decision.NoAction();

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
            return state.NoTargetNotified && state.FlagsFor == current ? new Decision.NoAction() : new Decision.NoTarget(reason);
        }

        if (!(state.RecoveredNotified && state.FlagsFor == current) && state.SwitchedFrom is { } from && from != current
            && cfg.Profiles.TryGetValue(from, out var fromEntry) && Providers.Of(fromEntry) == Providers.Login
            && usage.TryGetValue(from, out var fr) && HasRoom(fr, now, margin))
            return new Decision.Recovered(from);

        return new Decision.NoAction();
    }

    /// <summary>
    /// The state after a decision was acted on. Owns every flag transition: SwitchTo records LastSwitch/SwitchedFrom and
    /// clears both flags (now scoped to the new default); NoTarget and Recovered set their flag; a changed default clears
    /// both flags; NoTargetNotified clears once the default has a fresh reading back under the threshold, so the next
    /// crossing notifies again. ManualHold clears once the default changes or its fresh reading is back under the
    /// threshold, so a manual choice holds for the current crossing only. Callers save the result with
    /// <see cref="AutoSwitchStore.Save"/>.
    /// </summary>
    public static AutoSwitchState Next(AutoSwitchState s, Decision d, string? currentDefault,
        IReadOnlyDictionary<string, UsageReading> usage, DateTimeOffset now, AutoSwitchSettings? settings)
    {
        if (d is Decision.SwitchTo to)
            return new AutoSwitchState(now, currentDefault, false, false, to.Profile);

        var threshold = settings?.Threshold ?? new AutoSwitchSettings().Threshold;
        if (s.ManualHold != null && (s.ManualHold != currentDefault
            || usage.TryGetValue(s.ManualHold, out var held) && IsFresh(held, now) && Over(held, threshold) == null))
            s = s with { ManualHold = null };

        // Flags belong to the default they were raised for.
        if (s.FlagsFor != currentDefault)
            s = s with { NoTargetNotified = false, RecoveredNotified = false, FlagsFor = currentDefault };

        switch (d)
        {
            case Decision.NoTarget: return s with { NoTargetNotified = true };
            case Decision.Recovered: return s with { RecoveredNotified = true };
        }

        if (s.NoTargetNotified && settings != null && currentDefault != null
            && usage.TryGetValue(currentDefault, out var cur) && IsFresh(cur, now) && Over(cur, settings.Threshold) == null)
            s = s with { NoTargetNotified = false };
        return s;
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

    /// <summary>
    /// The user made <paramref name="profile"/> the default by hand (tray, Profiles window, <c>cr default</c>): auto-switch
    /// leaves it alone for the current crossing. Never called for an automatic switch.
    /// </summary>
    public void NoteManualDefault(string profile) =>
        Update(s => s.ManualHold == profile ? s : s with { ManualHold = profile });

    /// <summary>A renamed profile keeps being the one we switched from (and the one held).</summary>
    public void Rename(string oldName, string newName) =>
        Update(s => s.SwitchedFrom != oldName && s.FlagsFor != oldName && s.ManualHold != oldName ? s : s with
        {
            SwitchedFrom = s.SwitchedFrom == oldName ? newName : s.SwitchedFrom,
            FlagsFor = s.FlagsFor == oldName ? newName : s.FlagsFor,
            ManualHold = s.ManualHold == oldName ? newName : s.ManualHold,
        });

    /// <summary>A removed profile can no longer recover; its flags and hold go with it.</summary>
    public void Remove(string name) =>
        Update(s => s.SwitchedFrom != name && s.FlagsFor != name && s.ManualHold != name ? s : s with
        {
            SwitchedFrom = s.SwitchedFrom == name ? null : s.SwitchedFrom,
            NoTargetNotified = s.FlagsFor == name ? false : s.NoTargetNotified,
            RecoveredNotified = s.FlagsFor == name ? false : s.RecoveredNotified,
            FlagsFor = s.FlagsFor == name ? null : s.FlagsFor,
            ManualHold = s.ManualHold == name ? null : s.ManualHold,
        });

    private void Update(Func<AutoSwitchState, AutoSwitchState> change) =>
        Locked(() =>
        {
            var s = LoadOrThrow();
            var n = change(s);
            if (n != s) SaveUnlocked(n);
        });

    public void Save(AutoSwitchState state) =>
        Locked(() => SaveUnlocked(state));

    private void SaveUnlocked(AutoSwitchState state)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(state, Json));
        }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
    }

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
