using System.Text;
using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class TokenProviderTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);
    private const string Token = "sk-ant-oat01-SECRET-TOKEN-xyz";
    private const string TokenVar = "CLAUDE_CODE_OAUTH_TOKEN";

    private static (ProfileManager pm, TempHome home, FakeSecretStore tokens, FakeSecretStore keys) Make()
    {
        var home = new TempHome();
        var tokens = new FakeSecretStore();
        var keys = new FakeSecretStore();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, keys, new FakeGcloud(), tokens)
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, home, tokens, keys);
    }

    private static (int code, string o, string e, LaunchSpec? inPlace, LaunchSpec? tab) Run(ProfileManager pm,
        Func<string?>? readSecret, bool wt, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        LaunchSpec? inPlace = null, tab = null;
        var code = Commands.Run(args, o, e, pm, s => { tab = s; return 0; }, s => { inPlace = s; return 0; },
            wtAvailable: wt, claudePath: "claude.cmd", readSecret: readSecret);
        return (code, o.ToString(), e.ToString(), inPlace, tab);
    }

    private static string AllText(string dir) => string.Concat(
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Encoding.Latin1.GetString(File.ReadAllBytes(f))));

    [Fact]
    public void SetProvider_Token_SavesEncrypted_NotInSettings()
    {
        using var home = new TempHome();
        // The public constructor wires the real DPAPI token store.
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        File.WriteAllText(Path.Combine(home.Paths.DefaultHomeDir, "settings.json"), "{\"theme\":\"dark\"}");
        pm.Init();
        pm.Add("work");
        pm.SetProvider("work", Providers.Token, token: Token);

        var dir = home.Paths.ProfileDir("work");
        Assert.Equal(Providers.Token, pm.Get("work").Provider);
        Assert.True(File.Exists(Path.Combine(dir, DpapiSecretStore.TokenFileName)));
        Assert.Equal(Token, pm.GetToken("work"));
        Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(dir, "settings.json")));
        // Nowhere under clauderoo's root in plain text (UTF-8 or UTF-16), config.json included.
        var all = AllText(home.Paths.Root);
        Assert.DoesNotContain(Token, all);
        Assert.DoesNotContain(string.Join("\0", Token.ToCharArray()), all);
    }

    [Theory]
    [InlineData("sk-ant-api03-not-a-token")]
    [InlineData("hello")]
    [InlineData("   ")]
    public void SetProvider_Token_BadPrefix_Throws(string bad)
    {
        var (pm, home, tokens, _) = Make(); using var _h = home;
        var ex = Assert.Throws<ClauderooException>(() => pm.SetProvider("work", Providers.Token, token: bad));
        if (!string.IsNullOrWhiteSpace(bad))
        {
            Assert.Equal("That doesn't look like a Claude token (it should start with sk-ant-oat). Create one with 'claude setup-token'.",
                ex.Message);
            Assert.DoesNotContain(bad, ex.Message);
        }
        Assert.Null(pm.Get("work").Provider);
        Assert.Null(tokens.Load(home.Paths.ProfileDir("work")));
    }

    [Fact]
    public void SetProvider_Token_KeepsSavedTokenWhenNoneGiven()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        pm.SetProvider("work", Providers.Token);
        Assert.Equal(Token, pm.GetToken("work"));
    }

    [Fact]
    public void SwitchToLogin_DeletesToken()
    {
        var (pm, home, tokens, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        pm.SetProvider("work", Providers.Login);
        Assert.Null(tokens.Load(home.Paths.ProfileDir("work")));
        Assert.Null(pm.GetToken("work"));
    }

    [Fact]
    public void SwitchToApiKey_DeletesToken_AndTokenDeletesApiKey()
    {
        var (pm, home, tokens, keys) = Make(); using var _h = home;
        var dir = home.Paths.ProfileDir("work");
        pm.SetProvider("work", Providers.Token, token: Token);
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-ant-api03-k");
        Assert.Null(tokens.Load(dir));
        pm.SetProvider("work", Providers.Token, token: Token);
        Assert.Null(keys.Load(dir));
    }

    [Fact]
    public void Remove_TokenProfile_DeletesToken()
    {
        var (pm, home, tokens, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        pm.Remove("work");
        Assert.Contains(Path.Combine(home.Paths.RemovedDir, "work-20261117-100000"), tokens.Deleted);
    }

    [Fact]
    public void RunInPlace_TokenProfile_InjectsEnv()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        var r = Run(pm, null, true, "run", "work", home.Dir);
        Assert.Equal(0, r.code);
        Assert.Equal(Token, r.inPlace!.ExtraEnv![TokenVar]);
        Assert.Equal("work", r.inPlace.ExtraEnv[WhichProfile.SessionVar]);
        Assert.DoesNotContain(Token, string.Join(" ", r.inPlace.Args));
        Assert.DoesNotContain(Token, r.inPlace.ToString());
        Assert.DoesNotContain(Token, r.o + r.e);
    }

    [Fact]
    public void RunInPlace_LoginProfile_NoTokenEnv()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        var r = Run(pm, null, true, "start", "work", home.Dir);
        Assert.False(r.inPlace!.ExtraEnv!.ContainsKey(TokenVar));
    }

    [Fact]
    public void RunInPlace_IsolatedTokenProfile_InjectsEnv_NotInSettings()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        var r = Run(pm, null, true, "start", "work", home.Dir, "--isolated");
        Assert.Equal(0, r.code);
        Assert.Equal(Token, r.inPlace!.ExtraEnv![TokenVar]);
        Assert.Equal(home.Paths.IsolatedDir("work"), r.inPlace.ConfigDirEnv);
        Assert.DoesNotContain(Token, AllText(home.Paths.IsolatedDir("work")));
        Assert.DoesNotContain("First isolated run", r.o); // a token profile needs no login
    }

    [Fact]
    public void RunInPlace_TokenProfile_NoTokenSaved_Fails()
    {
        var (pm, home, tokens, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        tokens.Delete(home.Paths.ProfileDir("work"));
        var r = Run(pm, null, true, "start", "work", home.Dir);
        Assert.Equal(1, r.code);
        Assert.Null(r.inPlace);
        Assert.Contains("cr provider work token", r.e);
    }

    [Fact]
    public void NewWindow_TokenProfile_NoTokenInArgs()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        foreach (var wt in new[] { true, false })
        {
            var r = Run(pm, null, wt, "start", "work", home.Dir, "--tab");
            Assert.Equal(0, r.code);
            Assert.Null(r.inPlace);
            var spec = r.tab!;
            Assert.DoesNotContain(Token, string.Join(" ", spec.Args));
            Assert.DoesNotContain(Token, spec.ToString());
            Assert.True(spec.ExtraEnv is null || !spec.ExtraEnv.ContainsKey(TokenVar));
            Assert.DoesNotContain(Token, r.o + r.e);
        }
    }

    [Fact]
    public void LaunchSpec_ToString_HidesExtraEnvValues()
    {
        var spec = new LaunchSpec("claude.cmd", [], @"C:\p", null, "work",
            new Dictionary<string, string> { [TokenVar] = Token });
        Assert.DoesNotContain(Token, spec.ToString());
        Assert.Contains(TokenVar, spec.ToString());
    }

    [Fact]
    public void StartInfo_InheritedToken_Removed_UnlessInjected()
    {
        Environment.SetEnvironmentVariable(TokenVar, "sk-ant-oat01-inherited");
        try
        {
            var plain = Launcher.StartInfo(new LaunchSpec("claude.cmd", [], @"C:\", null, "work",
                new Dictionary<string, string>()));
            Assert.False(plain.Environment.ContainsKey(TokenVar));
            var injected = Launcher.StartInfo(new LaunchSpec("claude.cmd", [], @"C:\", null, "work",
                new Dictionary<string, string> { [TokenVar] = Token }));
            Assert.Equal(Token, injected.Environment[TokenVar]);
        }
        finally { Environment.SetEnvironmentVariable(TokenVar, null); }
    }

    [Fact]
    public void Env_Set_OauthToken_Rejected()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        Assert.Contains(TokenVar, ProviderOverlay.ReservedEnv);
        var r = Run(pm, null, true, "env", "work", "set", $"{TokenVar}={Token}");
        Assert.Equal(1, r.code);
        Assert.Contains($"Environment variable {TokenVar} is managed by the provider and cannot be set.", r.e);
        Assert.DoesNotContain(Token, r.o + r.e);
        Assert.DoesNotContain(TokenVar, pm.GetEnv("work").Keys);
    }

    [Fact]
    public void Overlay_StripsHandWrittenOauthToken()
    {
        var settings = JsonNode.Parse($"{{\"env\":{{\"{TokenVar}\":\"{Token}\",\"KEEP\":\"1\"}}}}")!.AsObject();
        var o = ProviderOverlay.Build("work", new ProfileEntry { Provider = Providers.Token, Env = { [TokenVar] = Token } }, "x.exe");
        Assert.Empty(o.Env);
        Assert.Empty(o.Top);
        var merged = ProviderOverlay.Apply(settings, o, [], []);
        Assert.DoesNotContain(Token, merged.ToJsonString());
        Assert.Equal("1", merged["env"]!["KEEP"]!.GetValue<string>());
    }

    [Fact]
    public void Status_DefaultTokenProfile_Warns()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        pm.SetDefault("work");
        var s = pm.Status();
        Assert.Contains("work signs in with a token; start it with 'cr start work' (plain 'claude' isn't signed in).", s.Problems);
        pm.SetDefault("personal");
        Assert.DoesNotContain(pm.Status().Problems, p => p.Contains("signs in with a token"));
    }

    [Fact]
    public void Provider_Token_Cli_ReadsSecret()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        var r = Run(pm, () => "  " + Token + "  ", true, "provider", "work", "token");
        Assert.Equal(0, r.code);
        Assert.Equal(Providers.Token, pm.Get("work").Provider);
        Assert.Equal(Token, pm.GetToken("work"));
        Assert.DoesNotContain(Token, r.o + r.e);

        var bad = Run(pm, () => "sk-ant-api03-nope", true, "provider", "work", "token");
        Assert.Equal(1, bad.code);
        Assert.Contains("sk-ant-oat", bad.e);
        Assert.DoesNotContain("sk-ant-api03-nope", bad.e);
    }

    [Fact]
    public void List_ShowsToken_AndJsonHasNoSecret()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        var r = Run(pm, null, true, "list");
        Assert.Contains(r.o.Split(Environment.NewLine), l => l.Contains("work") && l.Contains("token"));
        var j = Run(pm, null, true, "list", "--json");
        Assert.Contains("\"provider\":\"token\"", j.o);
        Assert.DoesNotContain(Token, j.o + j.e);
        var st = Run(pm, null, true, "status", "--json");
        Assert.DoesNotContain(Token, st.o + st.e);
    }

    [Fact]
    public void Check_TokenProfile()
    {
        var (pm, home, tokens, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        var ok = pm.Check("work");
        Assert.True(ok.Ok);
        Assert.False(ok.Verified);
        Assert.DoesNotContain(Token, ok.Message);
        tokens.Delete(home.Paths.ProfileDir("work"));
        Assert.False(pm.Check("work").Ok);
    }

    [Fact]
    public void Dpapi_TokenStore_OwnFile_AndKindInMessage()
    {
        using var home = new TempHome();
        var store = new DpapiSecretStore(DpapiSecretStore.TokenFileName, "token");
        store.Save(home.Dir, Token);
        Assert.True(File.Exists(Path.Combine(home.Dir, ".clauderoo-token")));
        Assert.False(File.Exists(Path.Combine(home.Dir, ".clauderoo-apikey")));
        Assert.Equal(Token, store.Load(home.Dir));
        File.WriteAllBytes(Path.Combine(home.Dir, ".clauderoo-token"), [1, 2, 3]);
        var ex = Assert.Throws<ClauderooException>(() => store.Load(home.Dir));
        Assert.Equal("The saved token can't be read on this account. Enter it again.", ex.Message);
    }

    [Fact]
    public void Restore_TokenProfile_SaysEnterTokenAgain()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        pm.Remove("work");
        var r = pm.Restore("work");
        Assert.Contains(r.Notices, n => n.Contains("token") && n.Contains("again"));
    }
}
