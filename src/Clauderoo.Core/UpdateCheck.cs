using System.Text.Json;

namespace Clauderoo.Core;

/// <summary>What the tray remembers about update checks, kept in %USERPROFILE%\.clauderoo\update.json.</summary>
public sealed class UpdateState
{
    /// <summary>When the last check finished (UTC).</summary>
    public DateTime? LastCheckUtc { get; set; }
    /// <summary>The newest release version seen, or null when none / up to date.</summary>
    public string? LatestVersion { get; set; }
    public string? LatestTag { get; set; }
    /// <summary>The version a balloon was already shown for, so each release is announced once.</summary>
    public string? NotifiedVersion { get; set; }
    /// <summary>False when the user turned the daily check off in Advanced.</summary>
    public bool CheckEnabled { get; set; } = true;
}

public static class UpdateCheck
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Pure: a check is due when checks are on and none ran in the last 24 hours (or the stored time is in the future).</summary>
    public static bool IsDue(UpdateState state, DateTime nowUtc)
    {
        if (!state.CheckEnabled) return false;
        if (state.LastCheckUtc is not { } last) return true;
        return last > nowUtc || nowUtc - last >= Interval;
    }

    /// <summary>The newer version recorded in the state, if it is newer than <paramref name="current"/>.</summary>
    public static Version? NewerThan(UpdateState state, string current)
    {
        var cur = AppVersion.Parse(current);
        var latest = AppVersion.Parse(state.LatestVersion);
        return cur != null && latest != null && latest > cur ? latest : null;
    }

    /// <summary>Pure: a check result replaces the stored state, but a toggle the user flipped while it ran wins.</summary>
    public static UpdateState Merge(UpdateState result, UpdateState live)
    {
        result.CheckEnabled = live.CheckEnabled;
        return result;
    }

    /// <summary>A copy that is safe to serialize on another thread.</summary>
    public static UpdateState Snapshot(UpdateState s) => new()
    {
        LastCheckUtc = s.LastCheckUtc, LatestVersion = s.LatestVersion, LatestTag = s.LatestTag,
        NotifiedVersion = s.NotifiedVersion, CheckEnabled = s.CheckEnabled,
    };

    /// <summary>Asks GitHub for the latest release and returns the updated state. A network failure keeps the old result and tries again tomorrow.</summary>
    public static async Task<UpdateState> RunAsync(UpdateState state, Updater updater, DateTime nowUtc, CancellationToken ct)
    {
        var next = new UpdateState
        {
            LastCheckUtc = nowUtc, LatestVersion = state.LatestVersion, LatestTag = state.LatestTag,
            NotifiedVersion = state.NotifiedVersion, CheckEnabled = state.CheckEnabled,
        };
        try
        {
            var found = await updater.LatestAsync(ct);
            next.LatestVersion = found?.Version.ToString();
            next.LatestTag = found?.Tag;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        return next;
    }
}

/// <summary>Reads and writes update.json. A missing or damaged file reads as the defaults.</summary>
public sealed class UpdateStore(string file)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static UpdateStore For(ClauderooPaths paths) => new(Path.Combine(paths.Root, "update.json"));

    public UpdateState Load()
    {
        try { return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(file)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(UpdateState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(state, Json));
    }
}
