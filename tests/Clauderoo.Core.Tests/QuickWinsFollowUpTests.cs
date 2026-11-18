using System.Text.Json;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

/// <summary>A store whose Save can be made to fail, to prove an old secret survives a failed switch.</summary>
public sealed class FailingSaveStore : ISecretStore
{
    private readonly FakeSecretStore inner = new();
    public bool FailSave { get; set; }
    public void Save(string profileDir, string secret)
    {
        if (FailSave) throw new IOException("disk full");
        inner.Save(profileDir, secret);
    }
    public string? Load(string profileDir) => inner.Load(profileDir);
    public void Delete(string profileDir) => inner.Delete(profileDir);
    public bool Exists(string profileDir) => inner.Exists(profileDir);
}

public class QuickWinsFollowUpTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);
    private const string Token = "sk-ant-oat01-SECRET-TOKEN-xyz";

    private static (ProfileManager pm, TempHome home) Make(ISecretStore? keys = null, ISecretStore? tokens = null)
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, keys ?? new FakeSecretStore(), new FakeGcloud(),
            tokens ?? new FakeSecretStore()) { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, home);
    }

    private static (int code, string o, string e, LaunchSpec? inPlace, LaunchSpec? tab) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        LaunchSpec? inPlace = null, tab = null;
        var code = Commands.Run(args, o, e, pm, s => { tab = s; return 0; }, s => { inPlace = s; return 0; },
            wtAvailable: true, claudePath: "claude.cmd", hookCancel: _ => new NoHook());
        return (code, o.ToString(), e.ToString(), inPlace, tab);
    }

    private static string Sub(TempHome home, string name)
    {
        var d = Path.Combine(home.Paths.Root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // --- --json allowlist ---

    [Theory]
    [InlineData("add", "x")]
    [InlineData("start", "work")]
    [InlineData("rename", "work")]
    [InlineData("default", "work")]
    public void Json_NotSupported_PlainUsageError(string cmd, string arg)
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, cmd, arg, "--json");
        Assert.Equal(1, r.code);
        Assert.Equal($"'{cmd}' doesn't support --json.", r.e.Trim());
        Assert.Null(r.inPlace);
        Assert.DoesNotContain("schemaVersion", r.e);
        Assert.False(pm.Config.Profiles.ContainsKey("x"));
    }

    [Fact]
    public void Json_AfterDoubleDash_BelongsToClaude()
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, "run", "work", home.Dir, "--", "--json");
        Assert.Equal(0, r.code);
        Assert.Equal(["--json"], r.inPlace!.Args);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("status")]
    [InlineData("maps")]
    [InlineData("which")]
    public void Json_Allowlisted_Commands_Work(string cmd)
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, cmd, "--json");
        Assert.Equal(0, r.code);
        using var doc = JsonDocument.Parse(r.o);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    // --- check --json shape ---

    [Fact]
    public void Check_Json_NonVertex_SameShape_EmptyItems()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-ant-api03-k");
        var r = Run(pm, "check", "work", "--json");
        Assert.Equal(0, r.code);
        using var doc = JsonDocument.Parse(r.o);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.False(root.GetProperty("verified").GetBoolean());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("message").GetString()));
        Assert.Equal(0, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void Check_Json_Vertex_SameShape_FilledItems()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud(),
            new FakeMachineEnv(), new FakeIam(), new FakeClaude()) { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "proj-1", Region = "europe-west1" });
        var r = Run(pm, "check", "work", "--json");
        using var doc = JsonDocument.Parse(r.o);
        var root = doc.RootElement;
        Assert.Equal(root.GetProperty("ok").GetBoolean() ? 0 : 1, r.code);
        Assert.True(root.GetProperty("verified").GetBoolean());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("message").GetString()));
        Assert.True(root.GetProperty("items").GetArrayLength() > 0);
    }

    // --- restore notice ---

    [Fact]
    public void Restore_TokenProfile_NoticeNamesBothWays()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        pm.Remove("work");
        var r = pm.Restore("work");
        Assert.Contains(r.Notices, n => n.Contains("Manage profiles") && n.Contains("cr provider work token"));
    }

    // --- save before delete ---

    [Fact]
    public void SwitchApiKeyToToken_SaveFails_ApiKeySurvives()
    {
        var tokens = new FailingSaveStore();
        var (pm, home) = Make(tokens: tokens); using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-ant-api03-k");
        tokens.FailSave = true;
        Assert.ThrowsAny<IOException>(() => pm.SetProvider("work", Providers.Token, token: Token));
        Assert.Equal("sk-ant-api03-k", pm.GetApiKey("work"));
        Assert.Equal(Providers.ApiKey, pm.Get("work").Provider);
    }

    [Fact]
    public void SwitchTokenToApiKey_SaveFails_TokenSurvives()
    {
        var keys = new FailingSaveStore();
        var (pm, home) = Make(keys: keys); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        keys.FailSave = true;
        Assert.ThrowsAny<IOException>(() => pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-ant-api03-k"));
        Assert.Equal(Token, pm.GetToken("work"));
        Assert.Equal(Providers.Token, pm.Get("work").Provider);
    }

    [Fact]
    public void SwitchApiKeyToToken_Succeeds_DeletesKey()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-ant-api03-k");
        pm.SetProvider("work", Providers.Token, token: Token);
        Assert.Null(pm.GetApiKey("work"));
        Assert.Equal(Token, pm.GetToken("work"));
    }

    // --- start ---

    [Fact]
    public void Start_MappedFolder_Isolated()
    {
        var (pm, home) = Make(); using var _h = home;
        var mapped = Sub(home, "company");
        pm.Map("work", mapped);
        var r = Run(pm, "start", mapped, "--isolated");
        Assert.Equal(0, r.code);
        Assert.Contains("Using work (mapped from", r.o);
        Assert.Equal("work", r.inPlace!.Profile);
        Assert.Equal(home.Paths.IsolatedDir("work"), r.inPlace.ConfigDirEnv);
        Assert.Equal("1", r.inPlace.ExtraEnv![Launcher.IsolatedVar]);
        Assert.True(Directory.Exists(home.Paths.IsolatedDir("work")));
    }

    [Fact]
    public void Start_MappedFolder_Tab()
    {
        var (pm, home) = Make(); using var _h = home;
        var mapped = Sub(home, "company");
        pm.Map("work", mapped);
        var r = Run(pm, "start", mapped, "--tab");
        Assert.Equal(0, r.code);
        Assert.Null(r.inPlace);
        Assert.Equal("wt.exe", r.tab!.FileName);
        Assert.Equal(home.Paths.ProfileDir("work"), r.tab.ConfigDirEnv);
        Assert.EndsWith("run work", r.tab.Args[^1]);
        Assert.Equal(Path.GetFullPath(mapped), r.tab.WorkingDir);
    }

    [Fact]
    public void Start_MissingNameNoSuchFolder_UnknownProfile()
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, "start", "nosuchprofile");
        Assert.Equal(1, r.code);
        Assert.Contains("Unknown profile 'nosuchprofile'", r.e);
        Assert.Null(r.inPlace);
        Assert.Null(r.tab);
    }

    // --- rename with the real DPAPI token store ---

    [Fact]
    public void Rename_TokenProfile_RealDpapi_TokenMovesWithFolder()
    {
        using var home = new TempHome();
        // The public constructor wires the real DPAPI token store.
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        pm.SetProvider("work", Providers.Token, token: Token);

        pm.Rename("work", "job");

        Assert.False(File.Exists(Path.Combine(home.Paths.ProfileDir("work"), DpapiSecretStore.TokenFileName)));
        Assert.True(File.Exists(Path.Combine(home.Paths.ProfileDir("job"), DpapiSecretStore.TokenFileName)));
        Assert.Equal(Token, pm.GetToken("job"));
        Assert.Equal(Providers.Token, pm.Get("job").Provider);
    }
}
