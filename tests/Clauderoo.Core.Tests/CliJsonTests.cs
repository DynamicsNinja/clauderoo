using System.Text.Json;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliJsonTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);

    private static (ProfileManager pm, TempHome home) Make(bool vertex = false)
    {
        var home = new TempHome();
        var pm = vertex
            ? new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud(),
                new FakeMachineEnv(), new FakeIam(), new FakeClaude()) { HelperExe = @"C:\x\clauderoo.exe" }
            : new ProfileManager(home.Paths, new FakeEnv(), () => Now);
        pm.Init();
        pm.Add("work");
        if (vertex)
            pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "proj-1", Region = "europe-west1" });
        return (pm, home);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd",
            hookCancel: _ => new NoHook());
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void List_Json_HasSchemaVersionAndProfiles()
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, "list", "--json");
        Assert.Equal(0, r.code);
        using var doc = JsonDocument.Parse(r.o);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        var profiles = doc.RootElement.GetProperty("profiles");
        Assert.Equal("personal", profiles[0].GetProperty("name").GetString());
        Assert.True(profiles[0].GetProperty("isHome").GetBoolean());
        Assert.Equal("login", profiles[0].GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Array, profiles[1].GetProperty("overrides").ValueKind);
    }

    [Fact]
    public void Status_Json()
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, "status", "--json");
        Assert.Equal(0, r.code);
        using var doc = JsonDocument.Parse(r.o);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("defaultProfile", out _));
        Assert.True(root.TryGetProperty("userEnv", out _));
        Assert.True(root.TryGetProperty("envMatchesDefault", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("problems").ValueKind);
    }

    [Fact]
    public void Which_Json()
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, "which", "--json");
        Assert.Equal(0, r.code);
        using var doc = JsonDocument.Parse(r.o);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("profile").GetString()));
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("source").GetString()));
        Assert.False(doc.RootElement.GetProperty("isolated").GetBoolean());
    }

    [Fact]
    public void Maps_Json()
    {
        var (pm, home) = Make(); using var _h = home;
        var dir = Path.Combine(home.Paths.Root, "proj");
        Directory.CreateDirectory(dir);
        pm.Map("work", dir);
        var r = Run(pm, "maps", "--json");
        Assert.Equal(0, r.code);
        using var doc = JsonDocument.Parse(r.o);
        var f = doc.RootElement.GetProperty("folders")[0];
        Assert.Equal("work", f.GetProperty("profile").GetString());
        Assert.Equal(FolderMap.Normalize(dir), f.GetProperty("dir").GetString(), ignoreCase: true);
    }

    [Fact]
    public void Maps_Json_Empty_IsEmptyArray()
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, "maps", "--json");
        using var doc = JsonDocument.Parse(r.o);
        Assert.Equal(0, doc.RootElement.GetProperty("folders").GetArrayLength());
    }

    [Fact]
    public void UnknownProfile_Json_ErrorOnStderr_Exit1()
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, "check", "nope", "--json");
        Assert.Equal(1, r.code);
        Assert.Equal("", r.o);
        using var doc = JsonDocument.Parse(r.e);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public void Check_Json_NonVertex_HasOkAndMessage()
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, "check", "work", "--json");
        using var doc = JsonDocument.Parse(r.o);
        Assert.True(doc.RootElement.TryGetProperty("ok", out _));
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public void Check_Json_Vertex_OnlyJsonOnStdout()
    {
        var (pm, home) = Make(vertex: true); using var _h = home;
        var r = Run(pm, "check", "work", "--json");
        using var doc = JsonDocument.Parse(r.o); // would throw if progress lines leaked into stdout
        var items = doc.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);
        Assert.Equal("gcloud", items[0].GetProperty("key").GetString());
        Assert.True(items[0].TryGetProperty("status", out _));
        Assert.True(items[0].TryGetProperty("label", out _));
        Assert.True(doc.RootElement.TryGetProperty("ok", out _));
    }

    [Fact]
    public void JsonOut_Write_FormatsDatesAsUtcIso()
    {
        var w = new StringWriter();
        JsonOut.Write(w, new { At = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(2)) });
        using var doc = JsonDocument.Parse(w.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-11-17T10:00:00Z", doc.RootElement.GetProperty("at").GetString());
    }
}
