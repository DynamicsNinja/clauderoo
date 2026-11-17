using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>When to fetch a profile's usage next. Pure.</summary>
internal sealed class PollPolicy
{
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Active = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MinRetryAfter = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    /// <summary>A Retry-After beyond this is cut to it (a delay must stay well inside Task.Delay's range).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromDays(1);
    public const int BackoffAfter = 3;

    /// <summary>
    /// Every 5 min, every 2 min with a running session. A 429's Retry-After is honoured with at least 10 min. From the
    /// 3rd straight failure the interval doubles with each one, up to 30 min.
    /// </summary>
    public TimeSpan NextDelay(string profile, bool hasSession, int failures, TimeSpan? retryAfter)
    {
        if (retryAfter is { } ra)
            return ra < MinRetryAfter ? MinRetryAfter : ra > MaxRetryAfter ? MaxRetryAfter : ra;
        var interval = hasSession ? Active : Idle;
        if (failures < BackoffAfter) return interval;
        var doublings = Math.Min(failures - BackoffAfter + 1, 10); // 2^10 × 2 min is far past the cap
        var backoff = interval * (1 << doublings);
        return backoff > MaxBackoff ? MaxBackoff : backoff;
    }
}

/// <summary>A profile the poller fetches: snapshotted on the UI thread, so the poller never touches ProfileManager.</summary>
internal sealed record PollTarget(string Name, string ConfigDir, ProfileEntry Entry, bool HasSession);

/// <summary>
/// Fetches Claude-login profiles' usage on a background task when each is due (<see cref="PollPolicy"/>), writes Ok
/// readings to <see cref="UsageCache"/> and raises <see cref="Updated"/> with the latest result of every target.
/// <see cref="Updated"/> is raised on a worker thread: the tray marshals it to the UI thread. Never holds a token.
/// </summary>
internal sealed class UsagePoller(
    Func<PollTarget, CancellationToken, Task<UsageResult>> fetch,
    UsageCache cache,
    Func<DateTimeOffset> now,
    PollPolicy? policy = null) : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private sealed class State
    {
        public DateTimeOffset? LastAttempt;
        public int Failures;
        public TimeSpan? RetryAfter;
        public UsageResult? Last;
    }

    private readonly PollPolicy _policy = policy ?? new PollPolicy();
    private readonly object _lock = new();
    private readonly Dictionary<string, State> _state = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _busy = new(1, 1); // one pass at a time (timer or Refresh)
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyList<PollTarget> _targets = [];
    private Task? _loop;

    public event Action<IReadOnlyDictionary<string, UsageResult>>? Updated;

    /// <summary>
    /// The profiles to poll (Claude-login ones). Profiles no longer listed are forgotten. A profile whose session just
    /// started gets a clean slate: its failures no longer stretch the interval.
    /// </summary>
    public void SetTargets(IEnumerable<PollTarget> targets)
    {
        lock (_lock)
        {
            var before = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var t in _targets) before[t.Name] = t.HasSession;
            _targets = targets.ToList();
            foreach (var gone in _state.Keys.Where(k => _targets.All(t => t.Name != k)).ToList()) _state.Remove(gone);
            foreach (var t in _targets)
                if (t.HasSession && before.TryGetValue(t.Name, out var had) && !had && _state.TryGetValue(t.Name, out var s))
                    s.Failures = 0;
        }
    }

    /// <summary>Starts the background loop: a pass now, then one every 30 s for the profiles that are due.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Fetches every target now, except one still inside a rate limit's Retry-After wait.</summary>
    public Task RefreshNowAsync() => Task.Run(() => PassAsync(force: true, _stop.Token));

    /// <summary>One pass over the profiles that are due.</summary>
    internal Task PollDueAsync(CancellationToken ct) => PassAsync(force: false, ct);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PassAsync(force: false, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* never let the loop die; the next tick tries again */ }
            try { await Task.Delay(Tick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PassAsync(bool force, CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<PollTarget> due;
            lock (_lock) due = _targets.Where(t => IsDue(t, force)).ToList();
            if (due.Count == 0) return;

            foreach (var t in due)
            {
                ct.ThrowIfCancellationRequested();
                UsageResult r;
                try { r = await fetch(t, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                // The exception text is dropped: it isn't plain words, and nothing may carry a token to the UI.
                catch (Exception) { r = new UsageResult.Failed("Couldn't check usage just now."); }

                bool stillTarget;
                lock (_lock) stillTarget = _targets.Any(x => x.Name == t.Name);
                // A profile removed while it was being fetched leaves no cache entry behind.
                if (r is UsageResult.Ok ok && stillTarget)
                {
                    try { cache.Put(ok.Reading); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* shown live; cached next time */ }
                }
                Record(t.Name, r);
            }

            Dictionary<string, UsageResult> snapshot;
            lock (_lock)
                snapshot = _state.Where(kv => kv.Value.Last != null).ToDictionary(kv => kv.Key, kv => kv.Value.Last!);
            Updated?.Invoke(snapshot);
        }
        finally { _busy.Release(); }
    }

    private bool IsDue(PollTarget t, bool force)
    {
        if (!_state.TryGetValue(t.Name, out var s) || s.LastAttempt is not { } last) return true;
        var at = now();
        if (s.RetryAfter is { } ra && at < last + _policy.NextDelay(t.Name, t.HasSession, s.Failures, ra)) return false;
        return force || at >= last + _policy.NextDelay(t.Name, t.HasSession, s.Failures, null);
    }

    private void Record(string name, UsageResult r)
    {
        lock (_lock)
        {
            if (!_targets.Any(t => t.Name == name)) return; // removed while it was being fetched
            if (!_state.TryGetValue(name, out var s)) _state[name] = s = new State();
            s.LastAttempt = now();
            s.Last = r;
            s.RetryAfter = r is UsageResult.RateLimited rl ? rl.RetryAfter : null;
            // Only a fetch that went wrong backs off. Expired, NotSignedIn and NotTracked are known states found
            // without (or despite) a working call: the profile is checked again at the normal interval.
            s.Failures = r is UsageResult.Ok or UsageResult.NotSignedIn or UsageResult.Expired or UsageResult.NotTracked
                ? 0
                : s.Failures + 1;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _stop.Dispose();
        // _busy is left to the GC: a Refresh still finishing may release it after this point.
    }
}

/// <summary>
/// What the tray shows about usage, on the UI thread: the latest reading per profile (the cache's until a fetch
/// works, then the live one, kept when a later fetch fails) and the latest fetch result.
/// </summary>
internal sealed class UsageView
{
    private Dictionary<string, UsageReading> _readings = new(StringComparer.Ordinal);
    private Dictionary<string, UsageResult> _results = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, UsageReading> Readings => _readings;
    public IReadOnlyDictionary<string, UsageResult> Results => _results;

    /// <summary>Raised after the readings or results changed.</summary>
    public event Action? Changed;

    /// <summary>Fetches every profile now (the Refresh button); set by the tray.</summary>
    public Func<Task> Refresh { get; set; } = () => Task.CompletedTask;

    /// <summary>Cached readings, for profiles without a newer one.</summary>
    public void Seed(IReadOnlyDictionary<string, UsageReading> cached)
    {
        var next = new Dictionary<string, UsageReading>(_readings, StringComparer.Ordinal);
        foreach (var (name, r) in cached)
            if (!next.TryGetValue(name, out var have) || have.FetchedAt < r.FetchedAt) next[name] = r;
        _readings = next;
        Changed?.Invoke();
    }

    public void Apply(IReadOnlyDictionary<string, UsageResult> results)
    {
        var next = new Dictionary<string, UsageReading>(_readings, StringComparer.Ordinal);
        foreach (var (name, r) in results)
            if (r is UsageResult.Ok ok) next[name] = ok.Reading;
        _readings = next;
        _results = new Dictionary<string, UsageResult>(results, StringComparer.Ordinal);
        Changed?.Invoke();
    }
}
