using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

// A saved API key or Claude token is kept when a profile switches provider, reused when it switches back, and only
// deleted by Forget or by removing the profile.
public class KeptSecretTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);
    private const string Key = "sk-ant-api03-KEPT-KEY-abc";
    private const string Token = "sk-ant-oat01-KEPT-TOKEN-xyz";

    private sealed class Ctx : IDisposable
    {
        public TempHome Home = new();
        public FakeSecretStore Keys = new();
        public FakeSecretStore Tokens = new();
        public ProfileManager Pm = null!;
        public string Dir => Home.Paths.ProfileDir("work");
        public void Dispose() => Home.Dispose();
    }

    private static Ctx Make()
    {
        var c = new Ctx();
        c.Pm = new ProfileManager(c.Home.Paths, new FakeEnv(), () => Now, c.Keys, new FakeGcloud(), c.Tokens)
        { HelperExe = @"C:\x\clauderoo.exe" };
        c.Pm.Init();
        c.Pm.Add("work");
        return c;
    }

    // The real DPAPI stores, for operations that move the profile folder (the fakes are keyed by path).
    private static (ProfileManager pm, TempHome home) MakeReal()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new DpapiSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, home);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, Func<string?>? readSecret, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd", readSecret: readSecret);
        return (code, o.ToString(), e.ToString());
    }

    private static string? NoRead() => throw new InvalidOperationException("must not read a secret");

    // ---------- Core ----------

    [Fact]
    public void ForgetSecret_DeletesOnlyThatKind_KeepsProvider()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        c.Pm.SetProvider("work", Providers.Token, token: Token);
        Assert.True(c.Pm.ForgetSecret("work", Providers.ApiKey));
        Assert.False(c.Keys.Exists(c.Dir));
        Assert.True(c.Tokens.Exists(c.Dir));
        Assert.Equal(Providers.Token, c.Pm.Get("work").Provider);
        Assert.False(c.Pm.ForgetSecret("work", Providers.ApiKey)); // nothing left to forget
        // The token in use can be forgotten too; the check then says it is missing.
        Assert.True(c.Pm.ForgetSecret("work", Providers.Token));
        Assert.Equal(Providers.Token, c.Pm.Get("work").Provider);
        Assert.False(c.Pm.Check("work").Ok);
    }

    [Fact]
    public void ForgetSecret_RefusesHomeAndOtherKinds()
    {
        using var c = Make();
        Assert.Throws<ClauderooException>(() => c.Pm.ForgetSecret("personal", Providers.ApiKey));
        var e = Assert.Throws<ClauderooException>(() => c.Pm.ForgetSecret("work", Providers.Vertex));
        Assert.Contains("apikey", e.Message);
    }

    [Fact]
    public void SecretSavedAt_ByKind_SeesAKeptSecret()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        c.Keys.Times[c.Dir] = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        c.Pm.SetProvider("work", Providers.Login);
        Assert.Null(c.Pm.SecretSavedAt("work")); // the provider in use has no secret
        Assert.Equal(c.Keys.Times[c.Dir], c.Pm.SecretSavedAt("work", Providers.ApiKey));
        Assert.Null(c.Pm.SecretSavedAt("work", Providers.Token));
    }

    [Theory]
    [InlineData(Providers.Login)]
    [InlineData(Providers.ApiKey)]
    public void KeptOldToken_NoAgeWarning_UnlessTheTokenIsInUse(string provider)
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.Token, token: Token);
        c.Tokens.Times[c.Dir] = new DateTimeOffset(Now.AddDays(-400));
        Assert.Single(c.Pm.TokenAgeProblems()); // in use: warned
        if (provider == Providers.ApiKey) c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        else c.Pm.SetProvider("work", Providers.Login);
        Assert.Empty(c.Pm.TokenAgeProblems());
        var check = c.Pm.Check("work");
        Assert.Null(check.Warning);
        Assert.DoesNotContain("token", check.Message, StringComparison.OrdinalIgnoreCase);
        var status = Run(c.Pm, null, "status");
        Assert.DoesNotContain("token", status.o + status.e, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeptKey_NotReportedByCheck_OfALoginProfile()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        c.Pm.SetProvider("work", Providers.Login);
        var r = c.Pm.Check("work");
        Assert.DoesNotContain("key", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ClauderooException>(() => c.Pm.CheckSecretAsync("work").GetAwaiter().GetResult());
    }

    [Fact]
    public void Remove_DeletesKeptSecretsToo()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        c.Pm.SetProvider("work", Providers.Token, token: Token);
        c.Pm.SetProvider("work", Providers.Login);
        c.Pm.Remove("work");
        var moved = Path.Combine(c.Home.Paths.RemovedDir, "work-20261118-100000");
        Assert.Contains(moved, c.Keys.Deleted);
        Assert.Contains(moved, c.Tokens.Deleted);
    }

    [Fact]
    public void Remove_ThenRestore_ComesBackWithoutAnySecret()
    {
        var (pm, home) = MakeReal();
        using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        pm.SetProvider("work", Providers.Login);
        pm.Remove("work");
        pm.Restore("work");
        Assert.False(pm.HasApiKey("work"));
        Assert.False(File.Exists(Path.Combine(home.Paths.ProfileDir("work"), DpapiSecretStore.FileName)));
        Assert.False(File.Exists(Path.Combine(home.Paths.ProfileDir("work"), DpapiSecretStore.FileName + DpapiSecretStore.MetaSuffix)));
    }

    [Fact]
    public void Rename_CarriesKeptSecret_SwitchBackStillWorks()
    {
        var (pm, home) = MakeReal();
        using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        pm.SetProvider("work", Providers.Login);
        pm.Rename("work", "job");
        Assert.True(pm.HasApiKey("job"));
        pm.SetProvider("job", Providers.ApiKey);
        Assert.Equal(Key, pm.GetApiKey("job"));
    }

    [Fact]
    public void Clone_NeverCopiesAKeptSecret()
    {
        var (pm, home) = MakeReal();
        using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        pm.SetProvider("work", Providers.Login);
        var r = pm.Clone("work", "copy");
        Assert.Null(r.SecretToSet);
        Assert.False(pm.HasApiKey("copy"));
        Assert.False(pm.HasToken("copy"));
        Assert.Empty(Directory.GetFiles(home.Paths.ProfileDir("copy"), DpapiSecretStore.FileName + "*"));
        Assert.Empty(Directory.GetFiles(home.Paths.ProfileDir("copy"), DpapiSecretStore.TokenFileName + "*"));
    }

    [Theory]
    [InlineData(Providers.ApiKey)]
    [InlineData(Providers.Token)]
    public void Import_SecretProvider_ReusesAKeptSecret(string kind)
    {
        using var c = Make();
        if (kind == Providers.ApiKey) c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        else c.Pm.SetProvider("work", Providers.Token, token: Token);
        c.Pm.SetProvider("work", Providers.Login);
        var r = c.Pm.Import("{\"schemaVersion\":1,\"profiles\":[{\"name\":\"work\",\"provider\":\"" + kind + "\"}]}", force: true);
        Assert.Equal(["work"], r.Updated);
        Assert.Empty(r.NeedSecret);
        Assert.Empty(r.KeptProvider!);
        Assert.Equal(kind, c.Pm.Get("work").Provider);
    }

    [Fact]
    public void Import_OverALoginProfileWithAKeptKey_ChangesItsProvider()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        c.Pm.SetProvider("work", Providers.Login);
        var json = "{\"schemaVersion\":1,\"profiles\":[{\"name\":\"work\",\"provider\":\"vertex\",\"vertex\":{\"project\":\"p\",\"region\":\"r\"}}]}";
        var r = c.Pm.Import(json, force: true);
        Assert.Empty(r.KeptProvider!);
        Assert.Equal(Providers.Vertex, c.Pm.Get("work").Provider);
        Assert.True(c.Keys.Exists(c.Dir)); // still kept
        // A token import with no kept token still asks for one.
        r = c.Pm.Import("{\"schemaVersion\":1,\"profiles\":[{\"name\":\"work\",\"provider\":\"token\"}]}", force: true);
        Assert.Single(r.NeedSecret);
        Assert.Equal(Providers.Login, Providers.Of(c.Pm.Get("work")));
    }

    // ---------- CLI ----------

    [Fact]
    public void Cli_ProviderApiKey_EmptyAnswer_ReusesKeptKey()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        c.Keys.Times[c.Dir] = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, Run(c.Pm, null, "provider", "work", "login").code);
        var r = Run(c.Pm, () => "", "provider", "work", "apikey");
        Assert.Equal(0, r.code);
        Assert.Contains("Using the saved API key, saved on 2026-03-01.", r.o);
        Assert.Equal(Key, c.Pm.GetApiKey("work"));
        Assert.DoesNotContain(Key, r.o + r.e);
        // End of input (stdin redirected from nothing) does the same.
        Run(c.Pm, null, "provider", "work", "login");
        Assert.Equal(0, Run(c.Pm, () => null, "provider", "work", "apikey").code);
        Assert.Equal(Key, c.Pm.GetApiKey("work"));
    }

    [Fact]
    public void Cli_ProviderApiKey_TypedKey_ReplacesKeptKey()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        c.Pm.SetProvider("work", Providers.Login);
        var r = Run(c.Pm, () => "sk-ant-api03-NEW", "provider", "work", "apikey");
        Assert.Equal(0, r.code);
        Assert.DoesNotContain("Using the saved", r.o);
        Assert.Equal("sk-ant-api03-NEW", c.Pm.GetApiKey("work"));
    }

    [Fact]
    public void Cli_Keep_UsesSavedWithoutReading_AndFailsWithoutOne()
    {
        using var c = Make();
        var none = Run(c.Pm, NoRead, "provider", "work", "token", "--keep");
        Assert.Equal(1, none.code);
        Assert.Contains("No Claude token is saved for 'work'", none.e);
        Assert.Equal(Providers.Login, Providers.Of(c.Pm.Get("work")));

        c.Pm.SetProvider("work", Providers.Token, token: Token);
        c.Pm.SetProvider("work", Providers.Login);
        var r = Run(c.Pm, NoRead, "provider", "work", "token", "--keep");
        Assert.Equal(0, r.code);
        Assert.Contains("Using the saved Claude token", r.o);
        Assert.Equal(Providers.Token, c.Pm.Get("work").Provider);
        Assert.DoesNotContain(Token, r.o + r.e);
    }

    [Fact]
    public void Cli_New_NeedsANewSecret_NeverFallsBackToTheSavedOne()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        c.Pm.SetProvider("work", Providers.Login);
        var empty = Run(c.Pm, () => "", "provider", "work", "apikey", "--new");
        Assert.Equal(1, empty.code);
        Assert.Contains("Enter an API key.", empty.e);
        Assert.Equal(Providers.Login, Providers.Of(c.Pm.Get("work")));
        var r = Run(c.Pm, () => "sk-ant-api03-NEW", "provider", "work", "apikey", "--new");
        Assert.Equal(0, r.code);
        Assert.Equal("sk-ant-api03-NEW", c.Pm.GetApiKey("work"));

        var both = Run(c.Pm, NoRead, "provider", "work", "apikey", "--new", "--keep");
        Assert.Equal(1, both.code);
        Assert.Contains("Usage:", both.e);
    }

    [Fact]
    public void Cli_ProviderApiKey_NoSavedKey_EmptyAnswer_Fails()
    {
        using var c = Make();
        var r = Run(c.Pm, () => "", "provider", "work", "apikey");
        Assert.Equal(1, r.code);
        Assert.Contains("Enter an API key.", r.e);
    }

    [Fact]
    public void Cli_Provider_UnknownProfile_FailsBeforeAskingForASecret()
    {
        using var c = Make();
        var r = Run(c.Pm, NoRead, "provider", "nope", "apikey");
        Assert.Equal(1, r.code);
        Assert.DoesNotContain("must not read", r.e);
    }

    [Fact]
    public void Cli_ReusedOldToken_WarnsAboutItsAge()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.Token, token: Token);
        c.Tokens.Times[c.Dir] = new DateTimeOffset(Now.AddDays(-340));
        c.Pm.SetProvider("work", Providers.Login);
        var r = Run(c.Pm, NoRead, "provider", "work", "token", "--keep");
        Assert.Equal(0, r.code);
        Assert.Contains("warning: work's Claude token was saved on", r.e);
        Assert.DoesNotContain(Token, r.o + r.e);
    }

    [Fact]
    public void Cli_Forget_DeletesTheSecret_AndSaysWhenItIsInUse()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        var r = Run(c.Pm, null, "forget", "work", "apikey");
        Assert.Equal(0, r.code);
        Assert.Contains("Deleted the saved API key for 'work'.", r.o);
        Assert.Contains("'work' still uses apikey", r.o);
        Assert.False(c.Keys.Exists(c.Dir));

        var again = Run(c.Pm, null, "forget", "work", "apikey");
        Assert.Equal(0, again.code);
        Assert.Contains("No API key is saved for 'work'. Nothing was changed.", again.o);

        c.Pm.SetProvider("work", Providers.Token, token: Token);
        c.Pm.SetProvider("work", Providers.Login);
        var kept = Run(c.Pm, null, "forget", "work", "token");
        Assert.Equal(0, kept.code);
        Assert.DoesNotContain("still uses", kept.o);
        Assert.False(c.Tokens.Exists(c.Dir));

        Assert.Equal(1, Run(c.Pm, null, "forget", "work").code);
        Assert.Equal(1, Run(c.Pm, null, "forget", "work", "vertex").code);
        Assert.Equal(1, Run(c.Pm, null, "forget", "personal", "apikey").code);
    }

    [Fact]
    public void Cli_Apikey_NeverPrintsAKeptKey()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        c.Pm.SetProvider("work", Providers.Login);
        var r = Run(c.Pm, null, "apikey", "work");
        Assert.Equal(1, r.code);
        Assert.Equal("", r.o);
        Assert.DoesNotContain(Key, r.e);
    }
}
