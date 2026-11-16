namespace Clauderoo.Core.Tests;

public class InstallerTests
{
    private const string Dir = @"C:\Users\x\AppData\Local\clauderoo";

    [Fact]
    public void Empty_ReturnsDir() => Assert.Equal(Dir, Installer.AppendToPath("", Dir));

    [Fact]
    public void PreservesPercentEntries()
    {
        var raw = @"%USERPROFILE%\.dotnet\tools;C:\bin";
        Assert.Equal(raw + ";" + Dir, Installer.AppendToPath(raw, Dir));
    }

    [Fact]
    public void AlreadyPresent_Raw_ReturnsNull() =>
        Assert.Null(Installer.AppendToPath(@"C:\bin;" + Dir.ToLowerInvariant() + @"\", Dir));

    [Fact]
    public void AlreadyPresent_Expanded_ReturnsNull()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
        Assert.Null(Installer.AppendToPath(@"C:\bin;%LOCALAPPDATA%\clauderoo", Path.Combine(local, "clauderoo")));
    }

    [Fact]
    public void TrailingSemicolon_NoDoubleSeparator() =>
        Assert.Equal(@"C:\bin;" + Dir, Installer.AppendToPath(@"C:\bin;", Dir));
}
