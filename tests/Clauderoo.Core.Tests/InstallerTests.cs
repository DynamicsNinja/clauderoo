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

    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "clauderoo-inst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Replace_FileInUse_MovesItAsideAndCopies()
    {
        var d = NewTempDir();
        try
        {
            var src = Path.Combine(d, "new.exe");
            var dest = Path.Combine(d, "clauderoo.exe");
            File.WriteAllText(src, "new");
            File.WriteAllText(dest, "old");
            // Like a running exe: open, not writable by others, but renaming is allowed.
            using (new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Installer.Replace(src, dest);
                Assert.Equal("new", File.ReadAllText(dest));
                Assert.True(File.Exists(dest + ".old"));
            }
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Replace_CopyFails_RestoresOriginal()
    {
        var d = NewTempDir();
        try
        {
            var src = Path.Combine(d, "new.exe");
            var dest = Path.Combine(d, "clauderoo.exe");
            File.WriteAllText(src, "new");
            File.WriteAllBytes(dest, [1, 2, 3, 4]);
            using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None)) // the copy can't read it
            {
                Assert.Throws<ClauderooException>(() => Installer.Replace(src, dest));
            }
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(dest));
            Assert.Empty(Directory.GetFiles(d, "*.old"));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Replace_OldStillInUse_UsesUniqueName()
    {
        var d = NewTempDir();
        try
        {
            var src = Path.Combine(d, "new.exe");
            var dest = Path.Combine(d, "clauderoo.exe");
            File.WriteAllText(src, "new");
            File.WriteAllText(dest, "old");
            File.WriteAllText(dest + ".old", "older");
            using (new FileStream(dest + ".old", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Installer.Replace(src, dest);
                Assert.Equal("new", File.ReadAllText(dest));
                Assert.Equal(2, Directory.GetFiles(d, "*.old").Length);
            }
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Replace_Locked_ThrowsPlainMessage()
    {
        var d = NewTempDir();
        try
        {
            var src = Path.Combine(d, "new.exe");
            var dest = Path.Combine(d, "cr.exe");
            File.WriteAllText(src, "new");
            File.WriteAllText(dest, "old");
            using (new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var e = Assert.Throws<ClauderooException>(() => Installer.Replace(src, dest));
                Assert.Equal("Couldn't replace cr.exe — close clauderoo and any Claude sessions started from it, then try again.", e.Message);
            }
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void DeleteStaleOld_RemovesFreeOnes_KeepsInUse()
    {
        var d = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(d, "a.exe.old"), "x");
            File.WriteAllText(Path.Combine(d, "b.exe.old"), "x");
            File.WriteAllText(Path.Combine(d, "keep.exe"), "x");
            using (new FileStream(Path.Combine(d, "b.exe.old"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Installer.DeleteStaleOld(d);
            }
            Assert.False(File.Exists(Path.Combine(d, "a.exe.old")));
            Assert.True(File.Exists(Path.Combine(d, "b.exe.old")));
            Assert.True(File.Exists(Path.Combine(d, "keep.exe")));
        }
        finally { Directory.Delete(d, true); }
    }
}
