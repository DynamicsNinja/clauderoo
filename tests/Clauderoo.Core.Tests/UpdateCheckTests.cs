using System.Net;

namespace Clauderoo.Core.Tests;

public class UpdateCheckTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UpdateCheck_DueOncePerDay()
    {
        var s = new UpdateState();
        Assert.True(UpdateCheck.IsDue(s, T0));                       // never checked
        s.LastCheckUtc = T0;
        Assert.False(UpdateCheck.IsDue(s, T0.AddHours(23)));
        Assert.True(UpdateCheck.IsDue(s, T0.AddHours(24)));
        Assert.True(UpdateCheck.IsDue(s, T0.AddDays(3)));
        Assert.True(UpdateCheck.IsDue(s, T0.AddHours(-1)));           // clock went backwards: don't wait forever
        s.CheckEnabled = false;
        Assert.False(UpdateCheck.IsDue(s, T0.AddDays(3)));
    }

    [Fact]
    public void NewerThan_ComparesPaddedVersions()
    {
        var s = new UpdateState { LatestVersion = "1.2.0" };
        Assert.Null(UpdateCheck.NewerThan(s, "1.2"));
        Assert.Equal(new Version(1, 2, 0), UpdateCheck.NewerThan(s, "1.1.9"));
        Assert.Null(UpdateCheck.NewerThan(new UpdateState(), "1.0.0"));
    }

    [Fact]
    public void Store_RoundTrips_AndDamagedFileReadsAsDefaults()
    {
        using var home = new TempHome();
        var store = UpdateStore.For(home.Paths);
        Assert.True(store.Load().CheckEnabled);
        store.Save(new UpdateState { LastCheckUtc = T0, LatestVersion = "2.0.0", CheckEnabled = false });
        var back = store.Load();
        Assert.Equal(T0, back.LastCheckUtc);
        Assert.Equal("2.0.0", back.LatestVersion);
        Assert.False(back.CheckEnabled);
        File.WriteAllText(Path.Combine(home.Paths.Root, "update.json"), "{ not json");
        Assert.True(store.Load().CheckEnabled);
    }

    private sealed class Http(Func<HttpResponseMessage> r) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(r());
    }

    [Fact]
    public async Task RunAsync_RecordsLatest_AndKeepsOldOnFailure()
    {
        const string json = "{\"tag_name\":\"v1.5.0\",\"assets\":["
            + "{\"name\":\"clauderoo-1.5.0-win-x64.zip\",\"browser_download_url\":\"https://x.test/a.zip\"},"
            + "{\"name\":\"clauderoo-1.5.0-win-x64.zip.sha256\",\"browser_download_url\":\"https://x.test/a.sha\"}]}";
        var ok = new Updater(new Http(() => new(HttpStatusCode.OK) { Content = new StringContent(json) }), "me/clauderoo");
        var s = await UpdateCheck.RunAsync(new UpdateState(), ok, T0, default);
        Assert.Equal("1.5.0", s.LatestVersion);
        Assert.Equal("v1.5.0", s.LatestTag);
        Assert.Equal(T0, s.LastCheckUtc);

        var down = new Updater(new Http(() => throw new HttpRequestException("offline")), "me/clauderoo");
        var s2 = await UpdateCheck.RunAsync(s, down, T0.AddDays(1), default);
        Assert.Equal("1.5.0", s2.LatestVersion);
        Assert.Equal(T0.AddDays(1), s2.LastCheckUtc);
    }
}
