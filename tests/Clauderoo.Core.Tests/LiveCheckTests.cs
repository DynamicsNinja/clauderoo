using System.Diagnostics;
using System.Windows.Forms;
using Clauderoo.Cli;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

// The Profiles window's live check: it runs `clauderoo check <name> --live --json` hidden and reads the JSON back.
[Collection(WinFormsCollection.Name)]
public class LiveCheckTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);
    private const string Key = "sk-ant-api03-LIVE-KEY-abc";
    private const string Token = "sk-ant-oat01-LIVE-TOKEN-xyz";

    private sealed class FakeKeyTester : IApiKeyTester
    {
        public (bool? valid, string? error) Result { get; set; } = (true, null);
        public Task<(bool? valid, string? error)> TestAsync(string apiKey, Uri baseUrl, CancellationToken ct) => Task.FromResult(Result);
    }

    private static (ProfileManager pm, TempHome home, FakeKeyTester tester) Make()
    {
        var home = new TempHome();
        var tester = new FakeKeyTester();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud(), new FakeSecretStore())
        { HelperExe = @"C:\x\clauderoo.exe", KeyTester = tester };
        pm.Init();
        pm.Add("work");
        return (pm, home, tester);
    }

    // Stands in for the hidden clauderoo.exe: runs the real CLI in-process with the arguments the tray passes.
    private static LiveCheck.Runner InProcess(ProfileManager pm, List<ProcessStartInfo>? seen = null) => (psi, ct) =>
    {
        seen?.Add(psi);
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run([.. psi.ArgumentList], o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        return Task.FromResult((code, o.ToString(), e.ToString()));
    };

    [Fact]
    public void StartInfo_HiddenCheckLiveJson_NameOnly()
    {
        var psi = LiveCheck.StartInfo(@"C:\x\clauderoo.exe", "work");
        Assert.Equal(["check", "work", "--live", "--json"], psi.ArgumentList);
        Assert.True(psi.CreateNoWindow);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput && psi.RedirectStandardError);
    }

    [Fact]
    public void Parse_ChecklistPayload()
    {
        const string json = "{\"schemaVersion\":1,\"ok\":false,\"verified\":true,\"message\":\"1 check(s) failed.\",\"items\":[" +
                            "{\"key\":\"saved\",\"label\":\"API key saved on 2026-11-01\",\"status\":\"ok\",\"hint\":null,\"detail\":null}," +
                            "{\"key\":\"live\",\"label\":\"The Anthropic API rejected the key\",\"status\":\"fail\",\"hint\":\"Create a new key\",\"detail\":\"HTTP 401\"}," +
                            "{\"key\":\"x\",\"label\":\"Odd\",\"status\":\"weird\"}," +
                            "{\"key\":\"y\",\"status\":\"ok\"}]}";
        var (items, error) = LiveCheck.Parse(1, json + "\r\n", "");
        Assert.Null(error);
        Assert.Equal(3, items!.Count); // the row without a label is dropped
        Assert.Equal(("saved", CheckStatus.Ok), (items[0].Key, items[0].Status));
        Assert.Equal(CheckStatus.Fail, items[1].Status);
        Assert.Equal("Create a new key", items[1].Hint);
        Assert.Equal("HTTP 401", items[1].Detail);
        Assert.Equal(CheckStatus.Warn, items[2].Status); // unknown status: shown, never as a pass
    }

    [Theory]
    [InlineData(1, "", "{\"schemaVersion\":1,\"error\":\"Profile 'nope' not found.\"}", "Profile 'nope' not found.")]
    [InlineData(1, "", "garbage", "The check couldn't run.")]
    [InlineData(0, "not json", "", "The check gave no result.")]
    [InlineData(0, "{\"schemaVersion\":1,\"items\":[]}", "", "The check gave no result.")]
    [InlineData(-1, "", "", "The check didn't finish within 2 minutes.")]
    public void Parse_Errors(int exit, string stdout, string stderr, string expected)
    {
        var (items, error) = LiveCheck.Parse(exit, stdout, stderr);
        Assert.Null(items);
        Assert.Equal(expected, error);
    }

    [Fact]
    public async Task RunAsync_ThroughTheRealCli_KeyAccepted()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        var (items, error) = await LiveCheck.RunAsync("work", CancellationToken.None, @"C:\x\clauderoo.exe", InProcess(pm));
        Assert.Null(error);
        Assert.Equal(["saved", "live"], items!.Select(i => i.Key));
        Assert.All(items!, i => Assert.Equal(CheckStatus.Ok, i.Status));
        Assert.DoesNotContain(items!, i => (i.Label + i.Hint + i.Detail).Contains(Key));
    }

    [Fact]
    public async Task RunAsync_ThroughTheRealCli_LoginProfile_GivesAnError()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        // A login profile's check --live is the plain check: no items, not ok.
        var (items, error) = await LiveCheck.RunAsync("work", CancellationToken.None, @"C:\x\clauderoo.exe", InProcess(pm));
        Assert.Null(items);
        Assert.NotNull(error);
        var unknown = await LiveCheck.RunAsync("nope", CancellationToken.None, @"C:\x\clauderoo.exe", InProcess(pm));
        Assert.Null(unknown.Items);
        Assert.Contains("nope", unknown.Error);
    }

    [Fact]
    public async Task RunAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LiveCheck.RunAsync("work", cts.Token, @"C:\x\clauderoo.exe",
            (_, _) => Task.FromResult((0, "{\"items\":[{\"label\":\"x\",\"status\":\"ok\"}]}", ""))));
    }

    [Fact]
    public async Task RunAsync_MissingExe_SaysSo()
    {
        var (items, error) = await LiveCheck.RunAsync("work", CancellationToken.None, @"C:\nowhere\clauderoo.exe");
        Assert.Null(items);
        Assert.Contains("reinstall", error);
    }

    // ---------- the window ----------

    private static void PumpUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Assert.True(done(), "timed out");
    }

    [Fact]
    public void Window_LiveCheckBox_OnlyForKeyAndTokenProfiles_WithItsNote()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        using var form = new ProfilesForm(pm, _ => { });
        form.Reload("work");
        Assert.True(form.LiveCheckShownForTest);
        Assert.False(form.LiveNoteShownForTest);
        form.LiveCheckBoxForTest.Checked = true;
        Assert.True(form.LiveNoteShownForTest);
        Assert.Contains("tiny prompt", form.LiveNoteForTest.Text);
        Assert.Contains("quota", form.LiveNoteForTest.Text);
        form.SelectProviderForTest(Providers.ApiKey);
        Assert.Contains("no quota", form.LiveNoteForTest.Text);
        form.SelectProviderForTest(Providers.Login);
        Assert.False(form.LiveCheckShownForTest);
        Assert.False(form.LiveNoteShownForTest);
        form.SelectProviderForTest(Providers.Vertex);
        Assert.False(form.LiveCheckShownForTest);
    }

    [Fact]
    public void Window_KeptSecret_SaysItWillBeUsed_AndOffersForget()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        pm.SetProvider("work", Providers.Login);
        using var form = new ProfilesForm(pm, _ => { });
        form.Reload("work");
        Assert.False(form.ForgetShownForTest); // login is selected
        form.SelectProviderForTest(Providers.ApiKey);
        Assert.Equal("", form.ApiKeyBoxForTest.Text); // never prefilled with the secret
        Assert.Contains("leave empty to use it", form.ApiKeyBoxForTest.PlaceholderText);
        Assert.True(form.ForgetShownForTest);
        Assert.Equal("Forget the saved key", form.ForgetLinkForTest.Text);
        form.SelectProviderForTest(Providers.Token); // no token saved
        Assert.False(form.ForgetShownForTest);
        Assert.DoesNotContain("leave empty", form.TokenBoxForTest.PlaceholderText);
    }

    [Fact]
    public void Window_LiveCheck_RunsTheCli_AndShowsItsRows()
    {
        var (pm, home, tester) = Make();
        using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        tester.Result = (false, "HTTP 401: the key was rejected");
        var seen = new List<ProcessStartInfo>();
        using var form = new ProfilesForm(pm, _ => { });
        _ = form.Handle; // BeginInvoke needs one
        form.UseLiveCheckRunnerForTest(@"C:\x\clauderoo.exe", InProcess(pm, seen));
        form.Reload("work");
        form.LiveCheckBoxForTest.Checked = true;
        form.RunCheckForTest();
        PumpUntil(() => !form.CheckRunningForTest);

        Assert.Equal(["check", "work", "--live", "--json"], Assert.Single(seen).ArgumentList);
        var items = form.LastCheckItemsForTest("work")!;
        Assert.Equal(["saved", "live"], items.Select(i => i.Key));
        Assert.Equal(CheckStatus.Fail, items[1].Status);
        Assert.Equal("1 problem found.", form.LastCheckMessageForTest("work"));
        Assert.DoesNotContain(items, i => (i.Label + i.Hint + i.Detail).Contains(Key));
    }

    [Fact]
    public void Window_CheckWithoutLive_StaysOffline()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        var seen = new List<ProcessStartInfo>();
        using var form = new ProfilesForm(pm, _ => { });
        _ = form.Handle;
        form.UseLiveCheckRunnerForTest(@"C:\x\clauderoo.exe", InProcess(pm, seen));
        form.Reload("work");
        form.RunCheckForTest();
        PumpUntil(() => !form.CheckRunningForTest);
        Assert.Empty(seen);
        Assert.Null(form.LastCheckItemsForTest("work"));
        Assert.Contains("not verified", form.LastCheckMessageForTest("work"));
    }
}
