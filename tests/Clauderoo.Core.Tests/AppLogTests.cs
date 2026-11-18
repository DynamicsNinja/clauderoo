using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

/// <summary>The rolling log file: format, size cap, repeats, tail, many writers, and never a secret.</summary>
public class AppLogTests
{
    private static string LogIn(TempHome home) => Path.Combine(home.Paths.Root, "logs", RollingLog.FileName);

    [Fact]
    public void Write_AppendsOneLineWithLevelSourceAndPid()
    {
        using var home = new TempHome();
        var log = new RollingLog(LogIn(home), "cli");
        log.Write("WARN", "Sync: work: could not sync");
        log.Write("INFO", "line\r\ntwo");
        var lines = File.ReadAllLines(LogIn(home));
        Assert.Equal(2, lines.Length);
        Assert.Contains($"WARN  [cli {Environment.ProcessId}] Sync: work: could not sync", lines[0]);
        Assert.EndsWith("line  two", lines[1]); // a message can't forge a second line
    }

    [Fact]
    public void Write_RollsAtTheCap_AndKeepsAtMostThreeFiles()
    {
        using var home = new TempHome();
        var file = LogIn(home);
        var log = new RollingLog(file, "cli", maxBytes: 500, files: 3);
        for (var i = 0; i < 100; i++) log.Write("INFO", $"message number {i:000} padded to be a little longer");

        var dir = Path.GetDirectoryName(file)!;
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["clauderoo.1.log", "clauderoo.2.log", "clauderoo.log"], files);
        foreach (var f in Directory.GetFiles(dir)) Assert.True(new FileInfo(f).Length < 500 + 200, f);
        // The newest line is in the live file, older ones in the rolled files, oldest beyond the cap gone.
        Assert.Contains("message number 099", File.ReadAllText(file));
        Assert.DoesNotContain("message number 000", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)));
    }

    [Fact]
    public void Write_SameMessageWithinTenMinutes_IsWrittenOnce()
    {
        using var home = new TempHome();
        var now = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var log = new RollingLog(LogIn(home), "tray", now: () => now);
        log.Write("WARN", "same");
        log.Write("WARN", "same");
        log.Write("INFO", "same"); // another level is another message
        now = now.AddMinutes(11);
        log.Write("WARN", "same");
        Assert.Equal(3, File.ReadAllLines(LogIn(home)).Length);
    }

    [Fact]
    public void Tail_ReadsAcrossRolledFiles_OldestFirst()
    {
        using var home = new TempHome();
        var file = LogIn(home);
        var log = new RollingLog(file, "cli", maxBytes: 300, files: 3);
        for (var i = 0; i < 12; i++) log.Write("INFO", $"entry {i:00} with some padding text");

        var tail = RollingLog.Tail(file, 5);
        Assert.Equal(5, tail.Count);
        Assert.EndsWith("entry 11 with some padding text", tail[^1]);
        Assert.EndsWith("entry 07 with some padding text", tail[0]);
        Assert.Empty(RollingLog.Tail(Path.Combine(home.Dir, "none.log"), 5));
    }

    [Fact]
    public void Write_ManyWritersAtOnce_LoseNoLines()
    {
        using var home = new TempHome();
        var file = LogIn(home);
        // Separate instances, as separate processes would have, sharing one file through the log mutex.
        Parallel.For(0, 8, w =>
        {
            var log = new RollingLog(file, $"w{w}");
            for (var i = 0; i < 25; i++) log.Write("INFO", $"writer {w} line {i}");
        });
        var lines = File.ReadAllLines(file);
        Assert.Equal(200, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"writer \d line \d+$", l));
    }

    [Theory]
    [InlineData("token is sk-ant-oat01-SECRETCANARY-abcdef0123456789", "SECRETCANARY")]
    [InlineData("key sk-ant-api03-SECRETCANARY_xyz-0123", "SECRETCANARY")]
    [InlineData("Authorization: Bearer SECRETCANARY.part.two", "SECRETCANARY")]
    [InlineData("{\"accessToken\":\"SECRETCANARY\",\"expiresAt\":1}", "SECRETCANARY")]
    [InlineData("refresh_token=SECRETCANARY&x=1", "SECRETCANARY")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN=SECRETCANARY", "SECRETCANARY")]
    [InlineData("ANTHROPIC_API_KEY: 'SECRETCANARY'", "SECRETCANARY")]
    [InlineData("password = SECRETCANARY", "SECRETCANARY")]
    [InlineData("blob eyJhbGciOiJIUzI1NiJ9eyJzdWIiOiJTRUNSRVRDQU5BUlkifQ0123456789 end", "eyJhbGciOiJIUzI1NiJ9")]
    public void Redact_RemovesSecrets(string message, string secret)
    {
        var redacted = AppLog.Redact(message);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void Redact_KeepsOrdinaryText()
    {
        const string m = @"Sync: work: could not sync settings.json: access denied to C:\Users\me\.clauderoo\profiles\work (session 0b6c5c1e-8f0e-4a6b-9a52-1f0d2a3b4c5d)";
        Assert.Equal(m, AppLog.Redact(m));
        Assert.Equal("Default profile set to work.", AppLog.Redact("Default profile set to work."));
    }

    [Fact]
    public void LibraryCode_WritesNoLog_UntilAProgramStartsIt()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => DateTime.Now);
        pm.Init();
        pm.Add("work");
        Assert.False(Directory.Exists(Path.Combine(home.Paths.Root, "logs")));
    }
}

/// <summary>The process-wide log, end to end. One class, not run in parallel with itself, owns AppLog.Start.</summary>
[Collection("AppLog")]
public class AppLogEndToEndTests
{
    private const string Token = "sk-ant-oat01-LOGCANARY-0f1e2d3c4b5a69788796a5b4";
    private const string ApiKey = "sk-ant-api03-LOGCANARY-KEY-aa11bb22cc33dd44ee55";

    [Fact]
    public void KeyActionsAreLogged_AndSecretsNeverAre()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => DateTime.Now);
        pm.Init();
        AppLog.Start(home.Paths, "cli");
        try
        {
            pm.Add("work");
            pm.SetDefault("work");
            // A token that fails validation, an API key that is saved, and errors that quote secrets.
            Commands.Run(["provider", "work", "token"], new StringWriter(), new StringWriter(), pm, _ => 0, _ => 0,
                wtAvailable: true, claudePath: "claude.cmd", readSecret: () => "not-a-token-" + Token);
            AppLog.Warn($"Usage: request failed with Authorization: Bearer {Token}");
            AppLog.Error($"Unexpected: {{\"accessToken\":\"{Token}\",\"refreshToken\":\"refresh-{Token}\"}}");
            AppLog.Warn($"env ANTHROPIC_API_KEY={ApiKey} rejected");
        }
        finally { AppLog.Stop(); }

        var text = string.Concat(Directory.GetFiles(Path.Combine(home.Paths.Root, "logs")).Select(File.ReadAllText));
        Assert.Contains("Profile work added.", text);
        Assert.Contains("Default profile set to work.", text);
        Assert.Contains("Settings saved (SetDefault)", text);
        Assert.Contains("cr provider:", text);
        Assert.DoesNotContain("LOGCANARY", text);
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain(ApiKey, text);
    }
}
