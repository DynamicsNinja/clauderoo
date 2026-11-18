using Clauderoo.Cli;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

[Collection(WinFormsCollection.Name)]
public class McpRegistrationTests
{
    [Fact]
    public void Command_ExactText()
    {
        Assert.Equal(@"claude mcp add --scope user clauderoo -- ""C:\Users\Jo Doe\AppData\Local\clauderoo\cr.exe"" mcp",
            McpRegistration.Command(@"C:\Users\Jo Doe\AppData\Local\clauderoo\cr.exe"));
    }

    [Fact]
    public void CrExe_InstalledWhenPresent_ElseNextToRunningExe()
    {
        using var home = new TempHome();
        var local = Path.Combine(home.Dir, "Local");
        var fallback = Path.Combine(home.Dir, "build", "clauderoo.exe");
        Assert.Equal(Path.Combine(home.Dir, "build", "cr.exe"), McpRegistration.CrExe(local, fallback));

        Directory.CreateDirectory(Path.Combine(local, "clauderoo"));
        File.WriteAllText(Path.Combine(local, "clauderoo", "clauderoo.exe"), "");
        Assert.Equal(Path.Combine(local, "clauderoo", "cr.exe"), McpRegistration.CrExe(local, fallback));
    }

    [Fact]
    public void CliAndWindow_ShowTheSameCommand()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => DateTime.Now);
        var o = new StringWriter();
        Commands.Run(["mcp", "install"], o, new StringWriter(), pm, _ => 0, _ => 0, clipboard: _ => true);
        Assert.Contains(McpRegistration.Command(), o.ToString());
        Assert.Contains(McpRegistration.Explanation, o.ToString());

        using var dlg = new UseFromClaudeDialog(McpRegistration.Command(), _ => true);
        Assert.Equal(McpRegistration.Command(), dlg.CommandText);
        Assert.Contains(McpRegistration.Explanation, dlg.ExplanationText);
    }

    [Fact]
    public void Dialog_Copy_UsesClipboard_FailureIsShown()
    {
        string? copied = null;
        using (var dlg = new UseFromClaudeDialog("cmd text", s => { copied = s; return true; }))
        {
            dlg.Copy();
            Assert.Equal("cmd text", copied);
            Assert.Contains("Copied", dlg.StatusText);
        }
        using (var dlg = new UseFromClaudeDialog("cmd text", _ => false))
        {
            dlg.Copy();
            Assert.Contains("couldn't", dlg.StatusText, StringComparison.OrdinalIgnoreCase);
        }
    }
}
