using System.Net;
using System.Text;
using Clauderoo.Cli;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

/// <summary>
/// End to end: a distinctive token planted in a profile's .credentials.json must not come out of any usage path
/// (client, poller, cache, CLI including `which --json` and `list --json`), whatever the service answers. A Claude-token
/// profile's stored token (DPAPI-encrypted on disk) must not come out either, in plain text, anywhere.
/// </summary>
public class TokenLeakTests
{
    // Three distinct canaries: a profile's sign-in, the home profile's sign-in, and a token profile's stored token.
    private const string Token = "sk-ant-oat01-LEAKCANARY-7f3e9a1b2c4d5e6f8a9b";
    private const string HomeToken = "sk-ant-oat01-HOMECANARY-1a2b3c4d5e6f7a8b9c0d";
    private const string StoredToken = "sk-ant-oat01-TOKNCANARY-9z8y7x6w5v4u3t2s1r0q";
    private static readonly DateTime Clock = new(2026, 11, 17, 12, 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(f(r));
    }

    // The service echoes back the token the request carried (the account's own one).
    private static string Tok(HttpRequestMessage r) => r.Headers.Authorization?.Parameter ?? "";

    private static string Echo(HttpRequestMessage r) =>
        $"{{\"error\":\"bad token\",\"auth\":\"{r.Headers.Authorization}\",\"token\":\"{Tok(r)}\"}}";

    private static IEnumerable<(string Name, Handler Handler)> Answers()
    {
        // The home profile is over the auto-switch threshold and work has room, so `cr auto --once` really switches.
        yield return ("200", new Handler(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                { "five_hour": { "utilization": {{(Tok(r) == HomeToken ? 95 : 42)}}, "resets_at": "2026-11-17T15:40:00Z" },
                  "seven_day": { "utilization": {{(Tok(r) == HomeToken ? 95 : 63)}}, "resets_at": "2026-11-20T09:00:00Z" },
                  "limits": [ { "kind": "model", "scope": { "model": { "display_name": "Opus {{Tok(r)}}" } }, "percent": 71 } ],
                  "echo": "{{r.Headers.Authorization}}", "token": "{{Tok(r)}}" }
                """),
        }));
        yield return ("401", new Handler(r => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(Echo(r)) }));
        yield return ("429", new Handler(r => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(Echo(r)) }));
        yield return ("500", new Handler(r => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(Echo(r)) }));
        yield return ("throws", new Handler(r => throw new HttpRequestException($"connection reset while sending {r.Headers.Authorization} ({Tok(r)})")));
    }

    private static string PlantCredential(string dir, string token)
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, ".credentials.json");
        File.WriteAllText(file,
            $$"""
            { "claudeAiOauth": { "accessToken": "{{token}}", "refreshToken": "refresh-{{token}}",
              "expiresAt": {{Now.AddHours(2).ToUnixTimeMilliseconds()}}, "subscriptionType": "max",
              "rateLimitTier": "default_claude_max_5x" } }
            """);
        return Path.GetFullPath(file);
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
        pm.Add("ci");
        pm.SetProvider("ci", Providers.Token, token: StoredToken);
        var homeName = pm.Config.HomeProfileName!;
        pm.SetDefault(homeName);
        var planted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PlantCredential(pm.ConfigDirOf("work"), Token),
            PlantCredential(pm.ConfigDirOf(homeName), HomeToken),
        };
        string[] profiles = ["work", homeName, "ci"];

        var seen = new StringBuilder();
        void Capture(string what, Func<string> run)
        {
            try { seen.AppendLine(what).AppendLine(run()); }
            catch (Exception e) { seen.AppendLine(what + " threw").AppendLine(e.ToString()); }
        }

        foreach (var (name, handler) in Answers())
        {
            using var client = new UsageClient(handler, () => Now, "1.0");

            foreach (var p in profiles)
            {
                // the client itself
                Capture($"[{name}] client {p}", () => client.FetchAsync(p, pm.ConfigDirOf(p), pm.Get(p), CancellationToken.None)
                    .GetAwaiter().GetResult().ToString());

                // the tray's words for what the poller found
                var view = new UsageView();
                Capture($"[{name}] tray {p}", () =>
                {
                    var r = client.FetchAsync(p, pm.ConfigDirOf(p), pm.Get(p), CancellationToken.None).GetAwaiter().GetResult();
                    view.Apply(new Dictionary<string, UsageResult> { [p] = r });
                    var reading = view.Readings.GetValueOrDefault(p);
                    return TrayText.UsageSuffix(reading) + "\n" + string.Join("\n", TrayText.UsageRows(reading, r, Now));
                });
            }

            // the tray poller, writing to the cache
            using (var poller = new UsagePoller(
                       (t, ct) => client.FetchAsync(t.Name, t.ConfigDir, t.Entry, ct), new UsageCache(home.Paths), () => Now))
            {
                poller.SetTargets(profiles.Select(p => new PollTarget(p, pm.ConfigDirOf(p), pm.Get(p), false)));
                poller.Updated += r => seen.AppendLine(string.Join("\n", r.Select(kv => kv.Key + "=" + kv.Value)));
                try { await poller.RefreshNowAsync(); }
                catch (Exception e) { seen.AppendLine(e.ToString()); }
            }

            // the CLI
            foreach (var args in new[]
                     {
                         new[] { "usage" }, ["usage", "work"], ["usage", "ci"], ["usage", "--json"], ["usage", "work", "--json"],
                         ["usage", "ci", "--json"], ["list"], ["list", "--json"], ["which"], ["which", "--json"],
                         ["auto", "on"], ["auto", "--once"], ["auto", "--once", "--json"],
                     })
                Capture($"[{name}] cr {string.Join(' ', args)}", () =>
                {
                    var o = new StringWriter();
                    var e = new StringWriter();
                    var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd",
                        hookCancel: _ => new NoopDisposable(), usage: client, clock: () => Now);
                    return $"exit {code}\n{o}\n{e}";
                });
        }

        var text = seen.ToString();
        Assert.Contains("42%", text); // the 200 answer was read and shown
        Assert.Contains("expired", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain(HomeToken, text);
        Assert.DoesNotContain("LEAKCANARY", text);
        Assert.DoesNotContain("HOMECANARY", text);
        Assert.DoesNotContain(StoredToken, text);
        Assert.DoesNotContain("TOKNCANARY", text);
        Assert.Contains("Usage isn't tracked for Claude token.", text); // the token profile was asked about, and said so
        Assert.Contains("\"schemaVersion\":1", text); // the --json runs printed JSON
        Assert.True(pm.HasToken("ci")); // the token is really stored (encrypted)
        // auto --once really switched (home was over the threshold), so autoswitch.json was written and is scanned below
        Assert.Contains($"{homeName} is at 95% of its", text);
        Assert.Equal("work", pm.Config.DefaultProfile);
        Assert.Equal(homeName, pm.AutoSwitchState.Load().SwitchedFrom);
        Assert.True(File.Exists(Path.Combine(home.Paths.Root, "autoswitch.json")));

        // nothing under the user's folder holds any token, except the two planted credential files themselves
        // (any other .credentials.json, say a copy, is checked like every other file)
        Assert.True(File.Exists(Path.Combine(home.Paths.Root, "usage.json")));
        var checkedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(home.Dir, "*", SearchOption.AllDirectories))
        {
            if (planted.Contains(Path.GetFullPath(file))) continue;
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("LEAKCANARY", content);
            Assert.DoesNotContain("HOMECANARY", content);
            Assert.DoesNotContain("TOKNCANARY", content); // DPAPI-encrypted on disk, never in plain text
            Assert.DoesNotContain("TOKNCANARY", Encoding.Unicode.GetString(File.ReadAllBytes(file)));
            checkedFiles++;
        }
        Assert.True(checkedFiles > 0);
    }
}
