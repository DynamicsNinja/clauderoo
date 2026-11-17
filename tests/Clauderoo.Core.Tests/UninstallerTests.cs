using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class UninstallerTests
{
    private sealed class FakePath(string raw) : IUserPath
    {
        public string Raw = raw;
        public int Writes;
        public string Read() => Raw;
        public void Write(string raw) { Raw = raw; Writes++; }
    }

    private sealed class Rig : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "clauderoo-un-" + Guid.NewGuid().ToString("N"));
        public string Install => Path.Combine(Root, "Local", "clauderoo");
        public string Data => Path.Combine(Root, "User", ".clauderoo");
        public FakeEnv Env = new();
        public FakePath Path_;
        public List<bool> AutoStarts = [];
        public List<string> Scheduled = [];

        public Rig(string pathRaw)
        {
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Data);
            File.WriteAllText(System.IO.Path.Combine(Install, "clauderoo.exe"), "x");
            File.WriteAllText(System.IO.Path.Combine(Install, "cr.exe"), "x");
            File.WriteAllText(System.IO.Path.Combine(Data, "config.json"), "{}");
            Path_ = new FakePath(pathRaw);
        }

        public UninstallOptions Options => new()
        {
            Path = Path_,
            SetAutoStart = on => AutoStarts.Add(on),
            Schedule = Scheduled.Add,
            DataDir = Data,
        };

        public void Dispose()
        {
            try
            {
                foreach (var d in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories))
                    if (new DirectoryInfo(d).LinkTarget != null) Directory.Delete(d);
                Directory.Delete(Root, true);
            }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Uninstall_RemovesPathEntryOnly()
    {
        using var rig = new Rig("");
        rig.Path_.Raw = @"%USERPROFILE%\.dotnet\tools;" + rig.Install + @"\;C:\bin";
        Uninstaller.Run(rig.Env, false, rig.Install, rig.Options);
        Assert.Equal(@"%USERPROFILE%\.dotnet\tools;C:\bin", rig.Path_.Raw);
        Assert.Equal([false], rig.AutoStarts);
    }

    [Fact]
    public void Uninstall_PathWithoutEntry_NotRewritten()
    {
        using var rig = new Rig(@"C:\bin;%X%\y");
        Uninstaller.Run(rig.Env, false, rig.Install, rig.Options);
        Assert.Equal(0, rig.Path_.Writes);
        Assert.Equal(@"C:\bin;%X%\y", rig.Path_.Raw);
    }

    [Fact]
    public void Uninstall_ClearDefault_OnlyWhenAsked()
    {
        using var rig = new Rig("");
        rig.Env.User["CLAUDE_CONFIG_DIR"] = @"C:\x";
        Uninstaller.Run(rig.Env, false, rig.Install, rig.Options);
        Assert.Equal(@"C:\x", rig.Env.GetUser("CLAUDE_CONFIG_DIR"));
        Uninstaller.Run(rig.Env, true, rig.Install, rig.Options);
        Assert.Null(rig.Env.GetUser("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public void Uninstall_KeepsClauderooData()
    {
        using var rig = new Rig("");
        Uninstaller.Run(rig.Env, true, rig.Install, rig.Options);
        Assert.True(File.Exists(Path.Combine(rig.Data, "config.json")));
        var cmd = Assert.Single(rig.Scheduled);
        Assert.Equal($"/c timeout 3 & rd /s /q \"{rig.Install}\"", cmd);
        Assert.DoesNotContain(".clauderoo", cmd);
        // running exes are moved aside, not deleted in place
        Assert.False(File.Exists(Path.Combine(rig.Install, "clauderoo.exe")));
        Assert.True(File.Exists(Path.Combine(rig.Install, "clauderoo.exe.old")));
    }

    [Fact]
    public void Uninstall_RefusesFolderWithJunction()
    {
        using var rig = new Rig("");
        var target = Path.Combine(rig.Root, "precious");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Junction.Create(Path.Combine(rig.Install, "link"), target);
        rig.Path_.Raw = rig.Install + ";C:\\bin";

        var e = Assert.Throws<ClauderooException>(() => Uninstaller.Run(rig.Env, true, rig.Install, rig.Options));
        Assert.Contains("junction", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Scheduled);
        Assert.Empty(rig.AutoStarts);
        Assert.Equal(0, rig.Path_.Writes);
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(rig.Install, "clauderoo.exe")));
    }

    [Fact]
    public void Uninstall_RefusesFolderThatContainsData()
    {
        using var rig = new Rig("");
        var opts = rig.Options;
        opts.DataDir = Path.Combine(rig.Install, ".clauderoo");
        Directory.CreateDirectory(opts.DataDir);
        Assert.Throws<ClauderooException>(() => Uninstaller.Run(rig.Env, false, rig.Install, opts));
        Assert.Empty(rig.Scheduled);
    }

    [Fact]
    public void Uninstall_RefusesFolderWithSubdirectory()
    {
        using var rig = new Rig("");
        Directory.CreateDirectory(Path.Combine(rig.Install, "stuff"));
        Assert.Throws<ClauderooException>(() => Uninstaller.Run(rig.Env, false, rig.Install, rig.Options));
        Assert.Empty(rig.Scheduled);
    }

    // CLI

    private static (int code, string o, string e) RunCli(Rig rig, string input, params string[] args)
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => new DateTime(2026, 11, 17));
        var o = new StringWriter();
        var e = new StringWriter();
        var hooks = new ReleaseHooks { Env = rig.Env, InstallDir = rig.Install, Uninstall = rig.Options, ReadLine = () => input };
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, hooks: hooks);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void UninstallCli_Yes_ClearsDefault_PrintsDataLocation()
    {
        using var rig = new Rig("");
        rig.Env.User["CLAUDE_CONFIG_DIR"] = @"C:\x";
        var r = RunCli(rig, "n", "uninstall", "--yes");
        Assert.Equal(0, r.code);
        Assert.Null(rig.Env.GetUser("CLAUDE_CONFIG_DIR"));
        Assert.Contains(rig.Data, r.o);
        Assert.Contains("kept", r.o, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UninstallCli_AnswerNo_KeepsDefault()
    {
        using var rig = new Rig("");
        rig.Env.User["CLAUDE_CONFIG_DIR"] = @"C:\x";
        var r = RunCli(rig, "n", "uninstall");
        Assert.Equal(0, r.code);
        Assert.Equal(@"C:\x", rig.Env.GetUser("CLAUDE_CONFIG_DIR"));
    }
}
