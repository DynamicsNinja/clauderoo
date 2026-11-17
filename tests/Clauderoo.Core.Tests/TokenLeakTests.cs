using System.Net;
using System.Text;
using Clauderoo.Cli;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

/// <summary>
/// End to end: a distinctive token planted in a profile's .credentials.json must not come out of any usage path
/// (client, poller, cache, CLI), whatever the service answers. `cr which` is added to this test when that branch merges.
/// </summary>
public class TokenLeakTests
{
    private const string Token = "sk-ant-oat01-LEAKCANARY-7f3e9a1b2c4d5e6f8a9b";
    private static readonly DateTime Clock = new(2026, 11, 17, 12, 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(f(r));
    }

    private static string Echo(HttpRequestMessage r) =>
        $"{{\"error\":\"bad token\",\"auth\":\"{r.Headers.Authorization}\",\"token\":\"{Token}\"}}";

    private static IEnumerable<(string Name, Handler Handler)> Answers()
    {
        yield return ("200", new Handler(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                { "five_hour": { "utilization": 42, "resets_at": "2026-11-17T15:40:00Z" },
                  "seven_day": { "utilization": 63, "resets_at": "2026-11-20T09:00:00Z" },
                  "limits": [ { "kind": "model", "scope": { "model": { "display_name": "Opus {{Token}}" } }, "percent": 71 } ],
                  "echo": "{{r.Headers.Authorization}}", "token": "{{Token}}" }
                """),
        }));
        yield return ("401", new Handler(r => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(Echo(r)) }));
        yield return ("429", new Handler(r => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(Echo(r)) }));
        yield return ("500", new Handler(r => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(Echo(r)) }));
        yield return ("throws", new Handler(r => throw new HttpRequestException($"connection reset while sending {r.Headers.Authorization} ({Token})")));
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public async Task TokenLeak_EndToEnd()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Clock);
        pm.Init();
        pm.Add("work");
        var dir = pm.ConfigDirOf("work");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".credentials.json"),
            $$"""
            { "claudeAiOauth": { "accessToken": "{{Token}}", "refreshToken": "refresh-{{Token}}",
              "expiresAt": {{Now.AddHours(2).ToUnixTimeMilliseconds()}}, "subscriptionType": "max",
              "rateLimitTier": "default_claude_max_5x" } }
            """);

        var seen = new StringBuilder();
        void Capture(string what, Func<string> run)
        {
            try { seen.AppendLine(what).AppendLine(run()); }
            catch (Exception e) { seen.AppendLine(what + " threw").AppendLine(e.ToString()); }
        }

        foreach (var (name, handler) in Answers())
        {
            using var client = new UsageClient(handler, () => Now, "1.0");

            // the client itself
            Capture($"[{name}] client", () => client.FetchAsync("work", dir, pm.Get("work"), CancellationToken.None)
                .GetAwaiter().GetResult().ToString());

            // the tray poller, writing to the cache
            using (var poller = new UsagePoller(
                       (t, ct) => client.FetchAsync(t.Name, t.ConfigDir, t.Entry, ct), new UsageCache(home.Paths), () => Now))
            {
                poller.SetTargets([new PollTarget("work", dir, pm.Get("work"), false)]);
                poller.Updated += r => seen.AppendLine(string.Join("\n", r.Select(kv => kv.Key + "=" + kv.Value)));
                try { await poller.RefreshNowAsync(); }
                catch (Exception e) { seen.AppendLine(e.ToString()); }
            }

            // the tray's words for what the poller found
            var view = new UsageView();
            Capture($"[{name}] tray", () =>
            {
                var r = client.FetchAsync("work", dir, pm.Get("work"), CancellationToken.None).GetAwaiter().GetResult();
                view.Apply(new Dictionary<string, UsageResult> { ["work"] = r });
                var reading = view.Readings.GetValueOrDefault("work");
                return TrayText.UsageSuffix(reading) + "\n" + string.Join("\n", TrayText.UsageRows(reading, r, Now));
            });

            // the CLI
            foreach (var args in new[] { new[] { "usage" }, ["usage", "work"], ["usage", "--json"], ["usage", "work", "--json"], ["list"] })
                Capture($"[{name}] cr {string.Join(' ', args)}", () =>
                {
                    var o = new StringWriter();
                    var e = new StringWriter();
                    var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd",
                        hookCancel: _ => new NoopDisposable(), usage: client);
                    return $"exit {code}\n{o}\n{e}";
                });
        }

        var text = seen.ToString();
        Assert.Contains("42%", text); // the 200 answer was read and shown
        Assert.Contains("expired", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("LEAKCANARY", text);

        // nothing under the user's folder holds it, except the planted credential itself
        Assert.True(File.Exists(Path.Combine(home.Paths.Root, "usage.json")));
        foreach (var file in Directory.EnumerateFiles(home.Dir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == ".credentials.json") continue;
            Assert.DoesNotContain("LEAKCANARY", File.ReadAllText(file));
        }
    }
}
