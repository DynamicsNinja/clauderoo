using System.Net;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

public class UsagePollerTests
{
    private static readonly PollPolicy Policy = new();

    [Fact]
    public void PollPolicy_ActiveProfileEvery2Min()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), Policy.NextDelay("work", hasSession: true, failures: 0, retryAfter: null));
        Assert.Equal(TimeSpan.FromMinutes(5), Policy.NextDelay("work", hasSession: false, failures: 0, retryAfter: null));
        // fewer than 3 straight failures keep the normal interval
        Assert.Equal(TimeSpan.FromMinutes(5), Policy.NextDelay("work", false, 2, null));
    }

    [Fact]
    public void PollPolicy_RetryAfterMin10()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), Policy.NextDelay("work", true, 0, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromMinutes(10), Policy.NextDelay("work", false, 0, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(45), Policy.NextDelay("work", false, 0, TimeSpan.FromMinutes(45)));
        // a huge Retry-After is honoured but capped at a day (Task.Delay can't wait for ever)
        Assert.Equal(TimeSpan.FromDays(1), Policy.NextDelay("work", false, 0, TimeSpan.FromDays(400)));
    }

    [Fact]
    public void PollPolicy_BackoffCapsAt30()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), Policy.NextDelay("work", false, 3, null));
        Assert.Equal(TimeSpan.FromMinutes(20), Policy.NextDelay("work", false, 4, null));
        Assert.Equal(TimeSpan.FromMinutes(30), Policy.NextDelay("work", false, 5, null));
        Assert.Equal(TimeSpan.FromMinutes(4), Policy.NextDelay("work", true, 3, null));
        Assert.Equal(TimeSpan.FromMinutes(30), Policy.NextDelay("work", true, 9, null));
        Assert.Equal(TimeSpan.FromMinutes(30), Policy.NextDelay("work", false, int.MaxValue, null));
    }

    private static UsageReading Reading(string name, DateTimeOffset at, double five) =>
        new(name, at, "Pro", [new UsageWindow("5h", five, null)]);

    [Fact]
    public async Task Poller_RefreshNow_CachesOk_RaisesUpdated()
    {
        using var home = new TempHome();
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var cache = new UsageCache(home.Paths);
        var calls = 0;
        using var poller = new UsagePoller((t, _) =>
        {
            calls++;
            return Task.FromResult<UsageResult>(t.Name == "work"
                ? new UsageResult.Ok(Reading("work", now, 42))
                : new UsageResult.Expired());
        }, cache, () => now);
        poller.SetTargets([new PollTarget("work", @"C:\x\work", new ProfileEntry(), false),
            new PollTarget("old", @"C:\x\old", new ProfileEntry(), false)]);
        IReadOnlyDictionary<string, UsageResult>? seen = null;
        poller.Updated += r => seen = r;

        await poller.RefreshNowAsync();

        Assert.Equal(2, calls);
        Assert.NotNull(seen);
        Assert.IsType<UsageResult.Ok>(seen!["work"]);
        Assert.IsType<UsageResult.Expired>(seen["old"]);
        Assert.Equal(42, cache.Load()["work"].Windows[0].Percent);
        Assert.False(cache.Load().ContainsKey("old"));
    }

    [Fact]
    public async Task Poller_OnlyDueProfiles_RateLimitHonouredEvenOnRefresh()
    {
        using var home = new TempHome();
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var calls = new List<string>();
        using var poller = new UsagePoller((t, _) =>
        {
            calls.Add(t.Name);
            return Task.FromResult<UsageResult>(t.Name == "busy"
                ? new UsageResult.RateLimited(TimeSpan.FromMinutes(1))
                : new UsageResult.Ok(Reading(t.Name, now, 10)));
        }, new UsageCache(home.Paths), () => now);
        poller.SetTargets([new PollTarget("work", "d", new ProfileEntry(), false),
            new PollTarget("busy", "d", new ProfileEntry(), false)]);

        await poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(["work", "busy"], calls);

        // 3 minutes later: nothing due (5 min for work, at least 10 min for the rate-limited one)
        now = now.AddMinutes(3);
        calls.Clear();
        await poller.PollDueAsync(CancellationToken.None);
        Assert.Empty(calls);

        // a session starts in work: it's due after 2 minutes
        poller.SetTargets([new PollTarget("work", "d", new ProfileEntry(), true),
            new PollTarget("busy", "d", new ProfileEntry(), false)]);
        await poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(["work"], calls);

        // Refresh now fetches everything except the profile still inside its Retry-After wait
        calls.Clear();
        await poller.RefreshNowAsync();
        Assert.Equal(["work"], calls);

        now = now.AddMinutes(8); // 11 min after the 429
        calls.Clear();
        await poller.RefreshNowAsync();
        Assert.Equal(["work", "busy"], calls);
    }

    [Fact]
    public async Task Poller_FetchThrows_CountsAsFailure_NoCrash()
    {
        using var home = new TempHome();
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        using var poller = new UsagePoller((_, _) => throw new InvalidOperationException("boom"),
            new UsageCache(home.Paths), () => now);
        poller.SetTargets([new PollTarget("work", "d", new ProfileEntry(), false)]);
        IReadOnlyDictionary<string, UsageResult>? seen = null;
        poller.Updated += r => seen = r;
        await poller.RefreshNowAsync();
        var f = Assert.IsType<UsageResult.Failed>(seen!["work"]);
        Assert.DoesNotContain("boom", f.Message); // raw exception text never reaches the UI
    }

    [Fact]
    public async Task Poller_ProfileRemovedDuringFetch_NoCacheEntry()
    {
        using var home = new TempHome();
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var cache = new UsageCache(home.Paths);
        UsagePoller? poller = null;
        using var p = poller = new UsagePoller((t, _) =>
        {
            poller!.SetTargets([]); // the profile is removed while its fetch is in flight
            return Task.FromResult<UsageResult>(new UsageResult.Ok(Reading(t.Name, now, 42)));
        }, cache, () => now);
        poller.SetTargets([new PollTarget("gone", "d", new ProfileEntry(), false)]);
        IReadOnlyDictionary<string, UsageResult>? seen = null;
        poller.Updated += r => seen = r;

        await poller.RefreshNowAsync();

        Assert.False(cache.Load().ContainsKey("gone"));
        Assert.False(seen?.ContainsKey("gone") ?? false);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("notTracked")]
    [InlineData("notSignedIn")]
    public async Task Poller_ExpiredDoesNotBackOff(string kind)
    {
        using var home = new TempHome();
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var calls = 0;
        UsageResult result = kind switch
        {
            "expired" => new UsageResult.Expired(),
            "notTracked" => new UsageResult.NotTracked(Providers.Vertex),
            _ => new UsageResult.NotSignedIn(),
        };
        using var poller = new UsagePoller((_, _) => { calls++; return Task.FromResult(result); },
            new UsageCache(home.Paths), () => now);
        poller.SetTargets([new PollTarget("work", "d", new ProfileEntry(), false)]);

        // Many passes in a row, each 5 minutes apart: never stretched past the normal interval.
        for (var i = 1; i <= 6; i++)
        {
            await poller.PollDueAsync(CancellationToken.None);
            Assert.Equal(i, calls);
            now = now.AddMinutes(5);
        }
    }

    [Fact]
    public async Task Poller_FailuresBackOff_SessionStartResets()
    {
        using var home = new TempHome();
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var calls = 0;
        using var poller = new UsagePoller((_, _) => { calls++; return Task.FromResult<UsageResult>(new UsageResult.Failed("down")); },
            new UsageCache(home.Paths), () => now);
        poller.SetTargets([new PollTarget("work", "d", new ProfileEntry(), false)]);

        for (var i = 0; i < 5; i++) await poller.RefreshNowAsync(); // 5 straight failures: 30 min back-off
        Assert.Equal(5, calls);
        now = now.AddMinutes(5);
        await poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(5, calls); // backed off

        // a session starts: the slate is clean, so it's due at the 2-minute active interval
        poller.SetTargets([new PollTarget("work", "d", new ProfileEntry(), true)]);
        await poller.PollDueAsync(CancellationToken.None);
        Assert.Equal(6, calls);
    }

    [Fact]
    public void UsageView_KeepsLastReadingWhenFetchFails()
    {
        var at = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        var view = new UsageView();
        view.Seed(new Dictionary<string, UsageReading> { ["work"] = Reading("work", at, 33) });
        view.Apply(new Dictionary<string, UsageResult> { ["work"] = new UsageResult.RateLimited(TimeSpan.FromMinutes(10)) });
        Assert.Equal(33, view.Readings["work"].Windows[0].Percent);
        Assert.IsType<UsageResult.RateLimited>(view.Results["work"]);

        view.Apply(new Dictionary<string, UsageResult> { ["work"] = new UsageResult.Ok(Reading("work", at.AddHours(1), 50)) });
        Assert.Equal(50, view.Readings["work"].Windows[0].Percent);
        // a seed arriving after a live reading doesn't overwrite it
        view.Seed(new Dictionary<string, UsageReading> { ["work"] = Reading("work", at, 33) });
        Assert.Equal(50, view.Readings["work"].Windows[0].Percent);
    }

    [Fact]
    public void UsageView_CheckedAt_MovesOnlyForProfilesFetchedAgain()
    {
        var t = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var view = new UsageView(() => t);
        var homeResult = new UsageResult.NotSignedIn();
        view.Apply(new Dictionary<string, UsageResult> { ["ifc"] = new UsageResult.Expired(), ["home"] = homeResult });
        Assert.Equal(t, view.CheckedAt["ifc"]);

        // Refresh: ifc is fetched again and is still expired (an equal value, a new object); home wasn't fetched
        var first = t;
        t = t.AddMinutes(5);
        view.Apply(new Dictionary<string, UsageResult> { ["ifc"] = new UsageResult.Expired(), ["home"] = homeResult });
        Assert.Equal(t, view.CheckedAt["ifc"]);
        Assert.Equal(first, view.CheckedAt["home"]);
    }
}
