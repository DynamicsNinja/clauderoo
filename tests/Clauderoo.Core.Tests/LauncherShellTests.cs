using System.Runtime.InteropServices;
using System.Text;

namespace Clauderoo.Core.Tests;

/// <summary>Terminal (wt / conhost) and shell (cmd / pwsh / powershell) choices for new session windows.</summary>
public class LauncherShellTests
{
    private const string Exe = @"C:\Program Files\clauderoo\clauderoo.exe";
    private static readonly Func<string, bool> AllOnPath = _ => true;

    private static ClauderooConfig Cfg(string terminal = "wt", string? shell = null) => new()
    {
        Terminal = terminal,
        Shell = shell,
        Profiles = new()
        {
            ["personal"] = new ProfileEntry { Home = true },
            ["work"] = new ProfileEntry(),
        },
    };

    private static LaunchSpec New(TempHome home, ClauderooConfig cfg, string profile = "work", string dir = @"C:\proj",
        IReadOnlyList<string>? args = null, bool wt = true, string exe = Exe, bool isolated = false) =>
        Launcher.NewWindow(cfg, home.Paths, profile, dir, args ?? [], wt, exe, isolated, AllOnPath);

    /// <summary>The PowerShell script behind -EncodedCommand.</summary>
    private static string Script(LaunchSpec s)
    {
        var i = s.Args.ToList().IndexOf("-EncodedCommand");
        Assert.True(i > 0);
        Assert.Equal(i + 2, s.Args.Count); // the script is the last argument
        return Encoding.Unicode.GetString(Convert.FromBase64String(s.Args[i + 1]));
    }

    // PowerShell's single-quoted literal: a quote (any of its forms) followed by another stands for the second one.
    private static readonly char[] Quotes = ['\'', '\u2018', '\u2019', '\u201A', '\u201B'];

    private static string ReadLiteral(string text, ref int i)
    {
        Assert.Contains(text[i], Quotes);
        i++;
        var sb = new StringBuilder();
        while (true)
        {
            var c = text[i];
            if (Quotes.Contains(c))
            {
                if (i + 1 < text.Length && Quotes.Contains(text[i + 1])) { sb.Append(text[i + 1]); i += 2; continue; }
                i++;
                return sb.ToString();
            }
            sb.Append(c);
            i++;
        }
    }

    /// <summary>The value of the literal that follows <paramref name="marker"/> in the script; the rest of the line must be <paramref name="tail"/>.</summary>
    private static string LiteralAfter(string script, string marker, string tail = "")
    {
        // Searched in the whole script, not per line: a literal may hold a newline.
        var at = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{marker}' not in the script");
        Assert.Equal(-1, script.IndexOf(marker, at + 1, StringComparison.Ordinal));
        var i = at + marker.Length;
        var value = ReadLiteral(script, ref i);
        Assert.StartsWith(tail, script[i..]);
        return value;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string cmdLine, out int argc);

    /// <summary>How Windows (and .NET's argv) splits a command line, the way clauderoo.exe will see it.</summary>
    private static string[] Argv(string argString)
    {
        var p = CommandLineToArgvW("x.exe " + argString, out var argc);
        try
        {
            var r = new string[argc - 1];
            for (var k = 1; k < argc; k++) r[k - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, k * IntPtr.Size))!;
            return r;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private static string[] RunArgs(LaunchSpec s) => Argv(LiteralAfter(Script(s), "-ArgumentList ", " -NoNewWindow -PassThru).WaitForExit()"));

    // ---------- terminal ----------

    [Fact]
    public void Conhost_ForcesConsoleWindow_EvenWithWt()
    {
        using var home = new TempHome();
        var s = New(home, Cfg("conhost"));
        Assert.Equal("cmd.exe", s.FileName);
        Assert.Equal(new[] { "/c", "start", "Claude · work", "/D", @"C:\proj", "cmd", "/k", Exe, "run", "work" }, s.Args);
    }

    [Fact]
    public void Wt_NotInstalled_FallsBackToConsole()
    {
        using var home = new TempHome();
        Assert.Equal("cmd.exe", New(home, Cfg("wt"), wt: false).FileName);
        Assert.Equal("cmd.exe", New(home, Cfg("wt", "pwsh"), wt: false).FileName);
    }

    [Fact]
    public void UnknownTerminalOrShell_UseTheDefaults()
    {
        using var home = new TempHome();
        var s = New(home, Cfg("kitty", "fish"));
        Assert.Equal("wt.exe", s.FileName);
        Assert.Equal("cmd", s.Args[7]);
        Assert.Equal(TerminalChoice.Wt, TerminalChoice.TerminalOf(Cfg("kitty")));
        Assert.Equal(TerminalChoice.Cmd, TerminalChoice.ShellOf(Cfg(shell: null)));
    }

    [Fact]
    public void Cmd_Explicit_SameAsDefault()
    {
        using var home = new TempHome();
        Assert.Equal(New(home, Cfg()).Args, New(home, Cfg("wt", "cmd")).Args);
    }

    // ---------- PowerShell: shapes ----------

    [Theory]
    [InlineData("pwsh", "pwsh.exe")]
    [InlineData("powershell", "powershell.exe")]
    public void PowerShell_Wt_NewTab(string shell, string shellExe)
    {
        using var home = new TempHome();
        var s = New(home, Cfg("wt", shell));
        Assert.Equal("wt.exe", s.FileName);
        Assert.Equal(new[] { "-w", "0", "new-tab", "--title", "Claude · work", "-d", @"C:\proj", shellExe, "-NoExit", "-NoLogo", "-EncodedCommand" },
            s.Args.Take(11));
        Assert.Equal(home.Paths.ProfileDir("work"), s.ConfigDirEnv);
        Assert.Equal(@"C:\proj", s.WorkingDir);
    }

    [Theory]
    [InlineData("pwsh", "pwsh.exe")]
    [InlineData("powershell", "powershell.exe")]
    public void PowerShell_Conhost_StartsConsoleWithoutCmdParsingTheFolder(string shell, string shellExe)
    {
        using var home = new TempHome();
        var s = New(home, Cfg("conhost", shell), dir: @"C:\a&b %x% (1)");
        Assert.Equal("cmd.exe", s.FileName);
        Assert.Equal(new[] { "/c", "start", "Claude · work", shellExe, "-NoExit", "-NoLogo", "-EncodedCommand" }, s.Args.Take(7));
        Assert.DoesNotContain(@"C:\a&b %x% (1)", s.Args); // the folder is the working directory, never parsed by cmd
        Assert.Equal(@"C:\a&b %x% (1)", s.WorkingDir);
        Assert.Equal(@"C:\a&b %x% (1)", LiteralAfter(Script(s), "Set-Location -LiteralPath "));
    }

    [Fact]
    public void PowerShell_Script_ExactText()
    {
        using var home = new TempHome();
        var work = home.Paths.ProfileDir("work");
        var script = Script(New(home, Cfg("wt", "pwsh")));
        Assert.Equal(string.Join("\n",
            $"$env:CLAUDE_CONFIG_DIR = '{work}'",
            "$env:CLAUDEROO_PROFILE = $null",
            "$env:CLAUDEROO_ISOLATED = $null",
            @"Set-Location -LiteralPath 'C:\proj'",
            @"(Start-Process -FilePath 'C:\Program Files\clauderoo\clauderoo.exe' -ArgumentList 'run work' -NoNewWindow -PassThru).WaitForExit()"),
            script);
    }

    [Fact]
    public void PowerShell_Home_ClearsConfigDir()
    {
        using var home = new TempHome();
        var s = New(home, Cfg("wt", "pwsh"), profile: "personal");
        Assert.StartsWith("$env:CLAUDE_CONFIG_DIR = $null\n", Script(s));
        Assert.Null(s.ConfigDirEnv);
        Assert.Equal(new[] { "run", "personal" }, RunArgs(s));
    }

    [Fact]
    public void PowerShell_Isolated_PassesFlagAndIsolatedDir()
    {
        using var home = new TempHome();
        var s = New(home, Cfg("wt", "powershell"), isolated: true, args: ["--model", "opus"]);
        Assert.Equal(new[] { "run", "work", "--isolated", "--", "--model", "opus" }, RunArgs(s));
        Assert.Equal(home.Paths.IsolatedDir("work"), LiteralAfter(Script(s), "$env:CLAUDE_CONFIG_DIR = "));
    }

    [Fact]
    public void PowerShell_PathsWithSpacesAndQuotes_AreLiterals()
    {
        using var home = new TempHome();
        const string exe = @"C:\Users\O'Brien\My Apps (x86)\clauderoo.exe";
        const string dir = @"C:\Users\O'Brien\My Projects\it’s";
        var s = New(home, Cfg("wt", "pwsh"), dir: dir, exe: exe);
        var script = Script(s);
        Assert.Equal(exe, LiteralAfter(script, "-FilePath ", " -ArgumentList "));
        Assert.Equal(dir, LiteralAfter(script, "Set-Location -LiteralPath "));
        Assert.Contains(@"'C:\Users\O''Brien\My Projects\it’’s'", script); // the typographic quote is doubled too
        Assert.Equal(dir, s.Args[6]); // -d gets the folder as one argument
    }

    [Fact]
    public void PowerShell_Wt_FolderWithSemicolon_SkipsDashD()
    {
        using var home = new TempHome();
        var s = New(home, Cfg("wt", "pwsh"), dir: @"C:\a;b");
        Assert.DoesNotContain("-d", s.Args);
        Assert.DoesNotContain(s.Args, a => a.Contains(';'));
        Assert.Equal(@"C:\a;b", LiteralAfter(Script(s), "Set-Location -LiteralPath "));
    }

    // ---------- PowerShell: the cmd restrictions are lifted, injection is not possible ----------

    [Theory]
    [InlineData("a\"b")]
    [InlineData("%PATH%")]
    [InlineData("a;b")]
    [InlineData("hi!")]
    [InlineData("it's")]
    [InlineData("‘smart’ ‚quotes‛")]
    [InlineData("'; calc; '")]
    [InlineData("$(calc)")]
    [InlineData("$env:USERNAME")]
    [InlineData("`n `$x")]
    [InlineData("foo&calc|x<y>z^")]
    [InlineData("hello world")]
    [InlineData("C:\\trailing\\")]
    [InlineData("C:\\with space\\")]
    [InlineData("\\\"quoted\\\"")]
    [InlineData("line1\nline2")]
    [InlineData("")]
    public void PowerShell_AnyArgument_ReachesClaudeUnchanged(string arg)
    {
        using var home = new TempHome();
        foreach (var terminal in new[] { "wt", "conhost" })
            foreach (var shell in new[] { "pwsh", "powershell" })
            {
                var s = New(home, Cfg(terminal, shell), args: ["-p", arg, "--model", "opus"]);
                Assert.Equal(new[] { "run", "work", "--", "-p", arg, "--model", "opus" }, RunArgs(s));
                // Nothing in the arguments reaches wt or cmd: the script travels as base64.
                Assert.DoesNotContain(s.Args, a => a.Contains(';') || a.Contains('%') || a.Contains('"'));
            }
    }

    [Fact]
    public void PowerShell_Injection_StaysInsideTheLiteral()
    {
        using var home = new TempHome();
        var s = New(home, Cfg("wt", "pwsh"), args: ["'; Remove-Item C:\\ -Recurse; '"]);
        var script = Script(s);
        Assert.Equal(5, script.Split('\n').Length); // no extra statement
        Assert.Contains("'run work -- \"''; Remove-Item C:\\ -Recurse; ''\"'", script);
    }

    [Fact]
    public void Cmd_StillRejectsWhatCmdCantCarry()
    {
        using var home = new TempHome();
        Assert.Throws<ClauderooException>(() => New(home, Cfg("wt", "cmd"), args: ["a;b"]));
        Assert.Throws<ClauderooException>(() => New(home, Cfg("conhost"), args: ["a b"]));
        Assert.Throws<ClauderooException>(() => New(home, Cfg("wt", "cmd"), dir: @"C:\a%b"));
    }

    [Fact]
    public void PowerShell_NotInstalled_ClearError()
    {
        using var home = new TempHome();
        var e = Assert.Throws<ClauderooException>(() =>
            Launcher.NewWindow(Cfg("wt", "pwsh"), home.Paths, "work", @"C:\p", [], true, Exe, onPath: _ => false));
        Assert.Contains("pwsh.exe", e.Message);
        Assert.Contains("cr config shell cmd", e.Message);
        string? asked = null;
        Launcher.NewWindow(Cfg("wt", "powershell"), home.Paths, "work", @"C:\p", [], true, Exe, onPath: x => { asked = x; return true; });
        Assert.Equal("powershell.exe", asked);
    }

    [Fact]
    public void PowerShell_TooLong_Throws()
    {
        using var home = new TempHome();
        Assert.Throws<ClauderooException>(() => New(home, Cfg("wt", "pwsh"), args: [new string('x', 20000)]));
    }

    [Fact]
    public void PowerShell_UnknownProfile_Throws()
    {
        using var home = new TempHome();
        Assert.Throws<ClauderooException>(() => New(home, Cfg("wt", "pwsh"), profile: "nope"));
    }

    // ---------- helpers ----------

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("‘x’", "'‘‘x’’'")]
    [InlineData("$x `y \"z\"", "'$x `y \"z\"'")]
    public void PsQuote_DoublesEveryQuoteForm(string value, string expected) =>
        Assert.Equal(expected, Launcher.PsQuote(value));

    [Fact]
    public void WindowsCommandLine_RoundTrips()
    {
        string[] args = ["a", "", "b c", "d\"e", "f\\", "g h\\", "\\\\server\\share\\", "x\\\"y", "\t", "日本語"];
        Assert.Equal(args, Argv(Launcher.WindowsCommandLine(args)));
    }

    // ---------- Windows Terminal profile command lines ----------

    [Fact]
    public void ProfileCommandLine_Cmd()
    {
        using var home = new TempHome();
        var work = home.Paths.ProfileDir("work");
        Assert.Equal($"cmd.exe /k set CLAUDE_CONFIG_DIR={work}&& set CLAUDEROO_PROFILE=&& set CLAUDEROO_ISOLATED=&& \"{Exe}\" run work",
            Launcher.ProfileCommandLine(Cfg(), home.Paths, "work", Exe));
        Assert.StartsWith("cmd.exe /k set CLAUDE_CONFIG_DIR=&& ", Launcher.ProfileCommandLine(Cfg(), home.Paths, "personal", Exe));
        Assert.Throws<ClauderooException>(() => Launcher.ProfileCommandLine(Cfg(), home.Paths, "work", @"C:\a%b\clauderoo.exe"));
    }

    [Fact]
    public void ProfileCommandLine_PowerShell()
    {
        using var home = new TempHome();
        var line = Launcher.ProfileCommandLine(Cfg("wt", "pwsh"), home.Paths, "work", @"C:\a%b\clauderoo.exe");
        var argv = Argv(line);
        Assert.Equal(new[] { "pwsh.exe", "-NoExit", "-NoLogo", "-EncodedCommand" }, argv.Take(4));
        Assert.StartsWith("pwsh.exe ", line);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(argv[4]));
        Assert.DoesNotContain("Set-Location", script); // Windows Terminal's startingDirectory picks the folder
        Assert.Contains(@"-FilePath 'C:\a%b\clauderoo.exe' -ArgumentList 'run work'", script);
    }
}
