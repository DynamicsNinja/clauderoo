namespace Clauderoo.Core.Tests;

public class EditorLaunchTests
{
    /// <summary>A fake VS Code install: bin\code.cmd as VS Code writes it, Code.exe and the versioned cli.js.</summary>
    internal static (string Bin, string Exe, string Js) FakeVsCode(string root, string name = "code", string exeName = "Code.exe")
    {
        var bin = Path.Combine(root, "bin");
        var js = Path.Combine(root, "69047191fc", "resources", "app", "out", "cli.js");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.GetDirectoryName(js)!);
        var exe = Path.Combine(root, exeName);
        File.WriteAllText(exe, "");
        File.WriteAllText(js, "");
        File.WriteAllText(Path.Combine(bin, name), "#!/usr/bin/env sh"); // the extensionless shell script is ignored
        File.WriteAllText(Path.Combine(bin, name + ".cmd"),
            "@echo off\r\nsetlocal\r\nset VSCODE_DEV=\r\nset ELECTRON_RUN_AS_NODE=1\r\n" +
            $"\"%~dp0..\\{exeName}\" \"%~dp0..\\69047191fc\\resources\\app\\out\\cli.js\" %*\r\n" +
            "IF %ERRORLEVEL% NEQ 0 EXIT /b %ERRORLEVEL%\r\nendlocal\r\n");
        return (bin, exe, js);
    }

    private static ClauderooConfig Cfg() => new()
    {
        Profiles = new()
        {
            ["personal"] = new ProfileEntry { Home = true },
            ["work"] = new ProfileEntry(),
        },
    };

    [Fact]
    public void Resolve_CodeOnPath_RunsCliJsWithoutCmd()
    {
        using var home = new TempHome();
        var (bin, exe, js) = FakeVsCode(Path.Combine(home.Dir, "Microsoft VS Code"));
        var path = string.Join(';', Path.Combine(home.Dir, "empty"), bin);
        var c = EditorLaunch.Resolve("code", path, ".COM;.EXE;.BAT;.CMD");
        Assert.Equal(exe, c.FileName, ignoreCase: true);
        Assert.Equal(new[] { js }, c.PrefixArgs);
        Assert.True(c.RunAsNode);
    }

    [Fact]
    public void Resolve_ExplicitPaths()
    {
        using var home = new TempHome();
        var (bin, exe, js) = FakeVsCode(Path.Combine(home.Dir, "cur sor"), "cursor", "Cursor.exe");
        var viaCmd = EditorLaunch.Resolve(Path.Combine(bin, "cursor.cmd"), "", ".EXE;.CMD");
        Assert.Equal(exe, viaCmd.FileName, ignoreCase: true);
        Assert.Equal(js, viaCmd.PrefixArgs[0], ignoreCase: true);
        var viaExe = EditorLaunch.Resolve(exe, "", ".EXE;.CMD");
        Assert.Equal(exe, viaExe.FileName);
        Assert.Empty(viaExe.PrefixArgs);
        Assert.False(viaExe.RunAsNode);
        // No extension: PATHEXT is tried.
        Assert.Equal(exe, EditorLaunch.Resolve(Path.Combine(bin, "cursor"), "", ".EXE;.CMD").FileName, ignoreCase: true);
    }

    [Fact]
    public void Resolve_NotFound_ClearMessage()
    {
        using var home = new TempHome();
        var e = Assert.Throws<ClauderooException>(() => EditorLaunch.Resolve("code", Path.Combine(home.Dir, "empty"), ".EXE;.CMD"));
        Assert.Contains("VS Code ('code') was not found on PATH", e.Message);
        Assert.Contains("Cursor", Assert.Throws<ClauderooException>(() => EditorLaunch.Resolve("cursor", "", ".CMD")).Message);
    }

    [Fact]
    public void Resolve_UnrecognisedLauncher_Throws()
    {
        using var home = new TempHome();
        var cmd = Path.Combine(home.Dir, "weird.cmd");
        File.WriteAllText(cmd, "@echo off\r\nstart something.exe %*\r\n");
        var e = Assert.Throws<ClauderooException>(() => EditorLaunch.Resolve(cmd, "", ".CMD"));
        Assert.Contains("--editor", e.Message);
    }

    [Fact]
    public void Spec_CarriesTheProfileEnvironment()
    {
        using var home = new TempHome();
        var ed = new EditorCommand(@"C:\VS Code\Code.exe", [@"C:\VS Code\x\cli.js"], true);
        var s = EditorLaunch.Spec(Cfg(), home.Paths, "work", @"C:\My Projects\a&b", ed);
        Assert.Equal(@"C:\VS Code\Code.exe", s.FileName);
        Assert.Equal(new[] { @"C:\VS Code\x\cli.js", "--new-window", @"C:\My Projects\a&b" }, s.Args);
        Assert.Equal(home.Paths.ProfileDir("work"), s.ConfigDirEnv);
        Assert.Equal("work", s.ExtraEnv!["CLAUDEROO_PROFILE"]);
        Assert.Equal("1", s.ExtraEnv["ELECTRON_RUN_AS_NODE"]);
        Assert.Null(s.Profile); // not a claude session: never recorded as one
        Assert.Equal(@"C:\My Projects\a&b", s.WorkingDir);
    }

    [Fact]
    public void Spec_HomeProfile_ClearsConfigDir_AndPlainExeIsNotNode()
    {
        using var home = new TempHome();
        var s = EditorLaunch.Spec(Cfg(), home.Paths, "personal", null, new EditorCommand(@"C:\e\Code.exe", [], false));
        Assert.Null(s.ConfigDirEnv);
        Assert.Equal(new[] { "--new-window" }, s.Args);
        Assert.False(s.ExtraEnv!.ContainsKey("ELECTRON_RUN_AS_NODE"));
        Assert.Throws<ClauderooException>(() => EditorLaunch.Spec(Cfg(), home.Paths, "nope", null, new EditorCommand("x", [], false)));
    }

    [Fact]
    public void DropEnv_RemovesTheCallingEditorsAndSessionsMarkers()
    {
        using var home = new TempHome();
        var s = EditorLaunch.Spec(Cfg(), home.Paths, "work", @"C:\p", new EditorCommand(@"C:\e\Code.exe", [@"C:\e\cli.js"], true));
        // VSCODE_IPC_HOOK_CLI (set in VS Code's own terminal) would hand the folder to the window we're run from.
        foreach (var name in new[] { "VSCODE_IPC_HOOK_CLI", "vscode_pid", "ELECTRON_NO_ATTACH_CONSOLE", "ELECTRON_RUN_AS_NODE",
                     "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" })
            Assert.True(s.DropEnv!(name), name);
        foreach (var name in new[] { "PATH", "CLAUDE_CODE_GIT_BASH_PATH", "ANTHROPIC_MODEL", "HOME" })
            Assert.False(s.DropEnv!(name), name);

        // DropEnv runs before ExtraEnv, so the profile's own values survive it.
        var psi = Launcher.StartInfo(s);
        Assert.Equal("1", psi.Environment["ELECTRON_RUN_AS_NODE"]);
        Assert.Equal(home.Paths.ProfileDir("work"), psi.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("work", psi.Environment["CLAUDEROO_PROFILE"]);
        Assert.Equal(@"C:\e\Code.exe", psi.FileName);
        Assert.Equal(new[] { @"C:\e\cli.js", "--new-window", @"C:\p" }, psi.ArgumentList);
    }
}
