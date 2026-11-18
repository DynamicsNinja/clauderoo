using System.Diagnostics;

namespace Clauderoo.Core.Tests;

/// <summary>ConfigStore.Update: the cross-process config lock that keeps concurrent writers from losing changes.</summary>
public class ConfigLockingTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);

    private static TempHome Initialised()
    {
        var home = new TempHome();
        new ProfileManager(home.Paths, new FakeEnv(), () => Now).Init();
        return home;
    }

    [Fact]
    public void Update_ParallelThreads_KeepEveryChange()
    {
        using var home = Initialised();
        const int writers = 8, each = 25;
        var threads = Enumerable.Range(0, writers).Select(w => new Thread(() =>
        {
            for (var i = 0; i < each; i++)
                ConfigStore.Update(home.Paths, c => { c.Folders[$@"C:\thread-{w}\{i}"] = "personal"; });
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var cfg = ConfigStore.Load(home.Paths)!;
        Assert.Equal(writers * each, cfg.Folders.Count);
        for (var w = 0; w < writers; w++)
            for (var i = 0; i < each; i++)
                Assert.True(cfg.Folders.ContainsKey($@"C:\thread-{w}\{i}"), $"lost thread-{w}\\{i}");
    }

    [Fact]
    public void Update_ParallelProcesses_KeepEveryChange()
    {
        using var home = Initialised();
        var writer = Path.Combine(AppContext.BaseDirectory, "Clauderoo.ConfigWriter.exe");
        Assert.True(File.Exists(writer), $"test helper missing: {writer}");
        const int processes = 4, each = 25;

        var started = Enumerable.Range(0, processes).Select(n =>
        {
            var psi = new ProcessStartInfo(writer) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.ArgumentList.Add(home.Dir);
            psi.ArgumentList.Add($"p{n}");
            psi.ArgumentList.Add(each.ToString());
            return Process.Start(psi)!;
        }).ToList();
        // This process writes at the same time, so the lock is contended both ways.
        for (var i = 0; i < each; i++)
            ConfigStore.Update(home.Paths, c => { c.Folders[$@"C:\proc-here\{i}"] = "personal"; });
        foreach (var p in started)
        {
            using (p)
            {
                var stderr = p.StandardError.ReadToEnd();
                Assert.True(p.WaitForExit(60_000), "writer process hung");
                Assert.True(p.ExitCode == 0, $"writer failed: {stderr}");
            }
        }

        var cfg = ConfigStore.Load(home.Paths)!;
        Assert.Equal((processes + 1) * each, cfg.Folders.Count);
        for (var n = 0; n < processes; n++)
            for (var i = 0; i < each; i++)
                Assert.True(cfg.Folders.ContainsKey($@"C:\proc-p{n}\{i}"), $"lost proc-p{n}\\{i}");
    }

    [Fact]
    public void ProfileManagers_MappingInParallel_KeepEveryMapping()
    {
        using var home = Initialised();
        var dirs = Enumerable.Range(0, 20).Select(i => Path.Combine(home.Dir, "proj", $"d{i}")).ToList();
        // A separate ProfileManager per writer, like separate cr processes and the tray.
        Parallel.ForEach(dirs, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            d => new ProfileManager(home.Paths, new FakeEnv(), () => Now).Map("personal", d));
        var maps = new ProfileManager(home.Paths, new FakeEnv(), () => Now).Maps();
        Assert.Equal(dirs.Count, maps.Count);
    }

    [Fact]
    public void Update_ReadsTheFileFresh_NotTheCallersCopy()
    {
        using var home = Initialised();
        var stale = ConfigStore.Load(home.Paths)!;
        ConfigStore.Update(home.Paths, c => { c.Folders[@"C:\a"] = "personal"; });
        // A writer that loaded before the change above still keeps it.
        ConfigStore.Update(home.Paths, c => { c.Terminal = stale.Terminal + "-x"; });
        var cfg = ConfigStore.Load(home.Paths)!;
        Assert.Equal("personal", cfg.Folders[@"C:\a"]);
        Assert.Equal("wt-x", cfg.Terminal);
    }

    [Fact]
    public void Update_ReturnsTheChangeResult()
    {
        using var home = Initialised();
        var n = ConfigStore.Update(home.Paths, c => { c.Folders[@"C:\a"] = "personal"; return c.Folders.Count; });
        Assert.Equal(1, n);
        var saved = ConfigStore.Update(home.Paths, c => { c.Terminal = "cmd"; });
        Assert.Equal("cmd", saved.Terminal);
    }

    [Fact]
    public void Update_ChangeThrows_NothingSaved()
    {
        using var home = Initialised();
        var before = File.ReadAllText(home.Paths.ConfigFile);
        Assert.Throws<ClauderooException>(() => ConfigStore.Update(home.Paths, c =>
        {
            c.Folders[@"C:\a"] = "personal";
            throw new ClauderooException("no");
        }));
        Assert.Equal(before, File.ReadAllText(home.Paths.ConfigFile));
        // The lock was let go.
        ConfigStore.Update(home.Paths, c => { c.Terminal = "cmd"; });
    }

    [Fact]
    public void UpdateWhen_False_LeavesTheFileAlone()
    {
        using var home = Initialised();
        var before = File.GetLastWriteTimeUtc(home.Paths.ConfigFile);
        var text = File.ReadAllText(home.Paths.ConfigFile);
        Assert.False(ConfigStore.UpdateWhen(home.Paths, c => { c.Terminal = "changed"; return false; }));
        Assert.Equal(text, File.ReadAllText(home.Paths.ConfigFile));
        Assert.Equal(before, File.GetLastWriteTimeUtc(home.Paths.ConfigFile));
        Assert.True(ConfigStore.UpdateWhen(home.Paths, c => { c.Terminal = "changed"; return true; }));
        Assert.Equal("changed", ConfigStore.Load(home.Paths)!.Terminal);
    }

    [Fact]
    public void Update_Nested_Throws()
    {
        using var home = Initialised();
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigStore.Update(home.Paths, _ =>
        {
            ConfigStore.Update(home.Paths, c => { c.Terminal = "inner"; });
        }));
        Assert.Contains("nested", ex.Message);
        Assert.Equal("wt", ConfigStore.Load(home.Paths)!.Terminal);
    }

    [Fact]
    public void Update_NotInitialised_SaysRunInit()
    {
        using var home = new TempHome();
        var ex = Assert.Throws<ClauderooException>(() => ConfigStore.Update(home.Paths, c => { c.Terminal = "x"; }));
        Assert.Contains("clauderoo init", ex.Message);
        Assert.False(File.Exists(home.Paths.ConfigFile));
    }

    [Fact]
    public void WithLock_HeldElsewhere_TimesOutWithAClearMessage()
    {
        using var home = Initialised();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => ConfigStore.WithLock(home.Paths, TimeSpan.FromSeconds(5), () =>
        {
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return true;
        }));
        holder.Start();
        try
        {
            Assert.True(holding.Wait(TimeSpan.FromSeconds(10)));
            var ex = Assert.Throws<ClauderooException>(() =>
                ConfigStore.WithLock(home.Paths, TimeSpan.FromMilliseconds(200), () => true));
            Assert.Contains("still saving settings", ex.Message);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
        Assert.True(ConfigStore.WithLock(home.Paths, TimeSpan.FromSeconds(1), () => true));
    }

    [Fact]
    public void Update_AfterAHolderDied_StillSaves()
    {
        using var home = Initialised();
        // A thread that exits holding the mutex abandons it, as a killed process would.
        var t = new Thread(() => new Mutex(false, ConfigStore.MutexName(home.Paths)).WaitOne());
        t.Start();
        t.Join();
        ConfigStore.Update(home.Paths, c => { c.Terminal = "after"; });
        Assert.Equal("after", ConfigStore.Load(home.Paths)!.Terminal);
    }

    [Fact]
    public void MutexName_IsPerConfigFile_AndIgnoresCase()
    {
        var a = new ClauderooPaths(@"C:\Users\someone");
        var b = new ClauderooPaths(@"C:\USERS\SOMEONE");
        var c = new ClauderooPaths(@"C:\Users\other");
        Assert.Equal(ConfigStore.MutexName(a), ConfigStore.MutexName(b));
        Assert.NotEqual(ConfigStore.MutexName(a), ConfigStore.MutexName(c));
        Assert.StartsWith(@"Local\clauderoo-config-", ConfigStore.MutexName(a));
    }

    [Fact]
    public void Init_WhenAConfigAppearedMeanwhile_Refuses()
    {
        using var home = Initialised();
        var ex = Assert.Throws<ClauderooException>(() =>
            ConfigStore.Create(home.Paths, new ClauderooConfig { DefaultProfile = "other" }));
        Assert.Equal("clauderoo is already initialised.", ex.Message);
        Assert.Equal("personal", ConfigStore.Load(home.Paths)!.DefaultProfile);
    }
}
