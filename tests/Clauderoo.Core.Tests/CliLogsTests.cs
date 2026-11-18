using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliLogsTests
{
    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        return (code, o.ToString(), e.ToString());
    }

    private static ProfileManager Pm(TempHome home) => new(home.Paths, new FakeEnv(), () => DateTime.Now);

    [Fact]
    public void Path_PrintsTheLogFile()
    {
        using var home = new TempHome();
        var r = Run(Pm(home), "logs", "--path");
        Assert.Equal(0, r.code);
        Assert.Equal(Path.Combine(home.Paths.Root, "logs", "clauderoo.log"), r.o.Trim());
    }

    [Fact]
    public void NoLogYet_SaysSo_EvenBeforeInit()
    {
        using var home = new TempHome();
        var r = Run(Pm(home), "logs");
        Assert.Equal(0, r.code);
        Assert.Contains("Nothing logged yet", r.o);
        Assert.Equal("", r.e);
    }

    [Fact]
    public void Tail_PrintsTheLastLines()
    {
        using var home = new TempHome();
        var log = new RollingLog(AppLog.PathFor(home.Paths), "cli");
        for (var i = 0; i < 60; i++) log.Write("INFO", $"line {i}");

        var r = Run(Pm(home), "logs", "--tail", "2");
        Assert.Equal(0, r.code);
        var lines = r.o.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("line 58", lines[0]);
        Assert.EndsWith("line 59", lines[1]);

        var all = Run(Pm(home), "logs").o.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(50, all.Length); // the default
    }

    [Theory]
    [InlineData("--tail")]
    [InlineData("--tail", "0")]
    [InlineData("--tail", "x")]
    [InlineData("--bogus")]
    public void BadArguments_Fail(params string[] rest)
    {
        using var home = new TempHome();
        var r = Run(Pm(home), ["logs", .. rest]);
        Assert.Equal(1, r.code);
        Assert.NotEqual("", r.e);
    }

    [Fact]
    public void Help_ListsLogs()
    {
        using var home = new TempHome();
        Assert.Contains("logs [--path] [--tail N]", Run(Pm(home), "help").o);
    }
}
