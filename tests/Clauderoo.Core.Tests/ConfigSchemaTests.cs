using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

/// <summary>config.json's schemaVersion: legacy files, step-by-step migration, and files from a newer clauderoo.</summary>
public class ConfigSchemaTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);

    private static void WriteConfig(TempHome home, string json)
    {
        Directory.CreateDirectory(home.Paths.Root);
        File.WriteAllText(home.Paths.ConfigFile, json);
    }

    private static string Legacy(TempHome home) => $$"""
        { "defaultProfile": "personal", "homeDir": {{JsonValue.Create(home.Paths.DefaultHomeDir).ToJsonString()}},
          "homeJson": {{JsonValue.Create(home.Paths.DefaultHomeJson).ToJsonString()}},
          "profiles": { "personal": { "home": true } } }
        """;

    private static string Newer(TempHome home) => $$"""
        { "schemaVersion": 7, "defaultProfile": "personal", "homeDir": {{JsonValue.Create(home.Paths.DefaultHomeDir).ToJsonString()}},
          "homeJson": {{JsonValue.Create(home.Paths.DefaultHomeJson).ToJsonString()}},
          "profiles": { "personal": { "home": true, "futureField": 1 } }, "futureTop": { "a": [1, 2] } }
        """;

    [Fact]
    public void NewConfig_IsWrittenWithTheCurrentVersion()
    {
        using var home = new TempHome();
        new ProfileManager(home.Paths, new FakeEnv(), () => Now).Init();
        var root = JsonNode.Parse(File.ReadAllText(home.Paths.ConfigFile))!.AsObject();
        Assert.Equal(ConfigStore.CurrentSchemaVersion, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal(1, ConfigStore.CurrentSchemaVersion);
    }

    [Fact]
    public void LegacyFile_WithoutAVersion_IsReadAsVersion1_AndStampedOnSave()
    {
        using var home = new TempHome();
        WriteConfig(home, Legacy(home));
        var cfg = ConfigStore.Load(home.Paths)!;
        Assert.Equal(1, cfg.SchemaVersion);
        Assert.Equal("personal", cfg.DefaultProfile);
        Assert.Null(cfg.Extra); // schemaVersion is a known field, not an unknown one

        ConfigStore.Update(home.Paths, c => { c.Terminal = "cmd"; });
        var root = JsonNode.Parse(File.ReadAllText(home.Paths.ConfigFile))!.AsObject();
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal("cmd", root["terminal"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_OlderFile_RunsEachStepInOrder()
    {
        var steps = new Dictionary<int, Action<JsonObject>>
        {
            // v1 -> v2: "terminal" was renamed (pretend); v2 -> v3: a default is filled in.
            [1] = o => { o["terminal"] = o["term"]?.DeepClone(); o.Remove("term"); },
            [2] = o => { o["defaultProfile"] ??= "filled"; },
        };
        var cfg = ConfigStore.Parse("""{ "term": "cmd" }""", current: 3, steps)!;
        Assert.Equal(3, cfg.SchemaVersion);
        Assert.Equal("cmd", cfg.Terminal);
        Assert.Equal("filled", cfg.DefaultProfile);
        Assert.Null(cfg.Extra);

        // A version 2 file only runs the second step.
        var v2 = ConfigStore.Parse("""{ "schemaVersion": 2, "term": "kept-unknown" }""", current: 3, steps)!;
        Assert.Equal("filled", v2.DefaultProfile);
        Assert.Equal("wt", v2.Terminal);
        Assert.True(v2.Extra!.ContainsKey("term"));
    }

    [Fact]
    public void Parse_MissingStep_Throws()
    {
        var ex = Assert.Throws<ClauderooException>(() =>
            ConfigStore.Parse("""{ "schemaVersion": 1 }""", current: 2, new Dictionary<int, Action<JsonObject>>()));
        Assert.Contains("can't upgrade", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"one\"")]
    public void Load_InvalidVersion_Throws(string version)
    {
        using var home = new TempHome();
        WriteConfig(home, $$"""{ "schemaVersion": {{version}} }""");
        var ex = Assert.Throws<ClauderooException>(() => ConfigStore.Load(home.Paths));
        Assert.Contains("schemaVersion", ex.Message);
    }

    [Fact]
    public void NewerFile_IsStillRead_WithUnknownFieldsKept()
    {
        using var home = new TempHome();
        WriteConfig(home, Newer(home));
        var cfg = ConfigStore.Load(home.Paths)!;
        Assert.Equal(7, cfg.SchemaVersion);
        Assert.Equal("personal", cfg.DefaultProfile);
        Assert.True(cfg.Extra!.ContainsKey("futureTop"));
        Assert.True(cfg.Profiles["personal"].Extra!.ContainsKey("futureField"));
        // Reads through the manager work too.
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now);
        Assert.Single(pm.List());
    }

    [Fact]
    public void NewerFile_WritesAreRefused_AndTheFileIsUntouched()
    {
        using var home = new TempHome();
        WriteConfig(home, Newer(home));
        var before = File.ReadAllText(home.Paths.ConfigFile);

        var ex = Assert.Throws<ClauderooException>(() => ConfigStore.Update(home.Paths, c => { c.Terminal = "cmd"; }));
        Assert.Contains("newer clauderoo", ex.Message);
        Assert.Contains("format 7", ex.Message);
        Assert.Contains("nothing was saved", ex.Message);

        // A blind save can't stamp it back down either, even with a fresh object.
        Assert.Throws<ClauderooException>(() => ConfigStore.Save(home.Paths, new ClauderooConfig()));
        Assert.Throws<ClauderooException>(() => ConfigStore.Save(home.Paths, ConfigStore.Load(home.Paths)!));
        Assert.Equal(before, File.ReadAllText(home.Paths.ConfigFile));
    }

    [Fact]
    public void NewerFile_CliCommandThatWrites_FailsWithTheMessage()
    {
        using var home = new TempHome();
        WriteConfig(home, Newer(home));
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now);
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(["map", "personal", home.Dir], o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        Assert.Equal(1, code);
        Assert.Contains("newer clauderoo", e.ToString());

        // Reading commands keep working.
        o = new StringWriter();
        Assert.Equal(0, Commands.Run(["list"], o, new StringWriter(), pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd"));
        Assert.Contains("personal", o.ToString());
    }
}
