using System.Diagnostics;
using Clauderoo.Core;

namespace Clauderoo.Cli;

public static class Commands
{
    private const string Usage = @"Usage: clauderoo <command> [options]   (short form: cr <command>)

  init [--name <n>] [--home <path>]   set up clauderoo around your existing ~/.claude
  add <name>                          create a new profile
  list                                list profiles
  start <name> [dir] [-- args]        open Claude for a profile in a new terminal window
  run <name> [-- args]                run Claude for a profile in this terminal
  default <name> | --clear            set (or clear) the default profile for plain 'claude'
  own <name> <item>                   keep a profile's own copy of a shared item
  share <name> <item>                 go back to sharing an item from the home profile
  remove <name> [--force]             remove a profile (its folder is moved, not deleted)
  status                              show the default profile and any problems
  tray                                start the tray app
  autostart on|off                    start the tray app at login
  install                             copy clauderoo to %LOCALAPPDATA%\clauderoo and add it to PATH
  help                                show this help";

    public static int Run(string[] args, TextWriter output, TextWriter err, ProfileManager pm,
        Func<LaunchSpec, int> launch, bool? wtAvailable = null) =>
        Run(args, output, err, pm, launch, launch, wtAvailable);

    public static int Run(string[] args, TextWriter output, TextWriter err, ProfileManager pm,
        Func<LaunchSpec, int> startNewWindow, Func<LaunchSpec, int> runInPlace, bool? wtAvailable = null, string? claudePath = null)
    {
        try
        {
            return Dispatch(args, output, err, pm, startNewWindow, runInPlace, wtAvailable, claudePath);
        }
        catch (ClauderooException e)
        {
            err.WriteLine(e.Message);
            return 1;
        }
        catch (Exception e)
        {
            err.WriteLine($"Unexpected error: {e.Message}");
            return 2;
        }
    }

    private static int Dispatch(string[] args, TextWriter output, TextWriter err, ProfileManager pm,
        Func<LaunchSpec, int> startNewWindow, Func<LaunchSpec, int> runInPlace, bool? wtAvailable, string? claudePath)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            output.WriteLine(Usage);
            return 0;
        }

        var cmd = args[0];
        string[] known = ["init", "add", "list", "start", "run", "default", "own", "share", "remove",
            "status", "tray", "autostart", "install"];
        if (!known.Contains(cmd))
            throw new ClauderooException($"Unknown command '{cmd}'. Run 'clauderoo help'.");

        // Commands that work without an initialised config skip the pre-sync.
        // 'status' syncs itself. A failing pre-sync must never make a command fail.
        if (cmd is not ("init" or "install" or "autostart" or "tray" or "status"))
        {
            try { Print(pm.SyncAll(), output, err, "warning: "); }
            catch (Exception e) { err.WriteLine($"warning: sync failed: {e.Message}"); }
        }

        var (pos, claudeArgs) = Split(args.Skip(1).ToArray());

        switch (cmd)
        {
            case "init":
            {
                var name = "personal";
                string? home = null;
                for (var i = 0; i < pos.Count; i++)
                {
                    if (pos[i] == "--name" && i + 1 < pos.Count) name = pos[++i];
                    else if (pos[i] == "--home" && i + 1 < pos.Count) home = pos[++i];
                    else throw new ClauderooException("Usage: clauderoo init [--name <n>] [--home <path>]");
                }
                pm.Init(name, home);
                output.WriteLine($"Initialised. Home profile '{name}' uses your existing Claude folder.");
                return 0;
            }
            case "add":
            {
                var name = Need(pos, 1, "clauderoo add <name>")[0];
                Print(pm.Add(name), output, err);
                output.WriteLine($"Added profile '{name}'. Run 'clauderoo start {name}' and sign in.");
                return 0;
            }
            case "list":
                foreach (var line in FormatList(pm.List())) output.WriteLine(line);
                return 0;
            case "start":
            {
                if (pos.Count is < 1 or > 2) throw new ClauderooException("Usage: clauderoo start <name> [dir] [-- args]");
                var dir = pos.Count > 1 ? Path.GetFullPath(pos[1]) : Environment.CurrentDirectory;
                if (!Directory.Exists(dir)) throw new ClauderooException($"Folder not found: {dir}");
                var spec = Launcher.NewWindow(pm.Config, pm.Paths, pos[0], dir, claudeArgs, wtAvailable ?? Launcher.WtAvailable());
                startNewWindow(spec);
                return 0;
            }
            case "run":
            {
                var name = Need(pos, 1, "clauderoo run <name> [-- args]")[0];
                var spec = Launcher.InPlace(pm.Config, pm.Paths, name, Environment.CurrentDirectory, claudeArgs, claudePath);
                return runInPlace(spec);
            }
            case "default":
            {
                if (pos.Count == 1 && pos[0] == "--clear")
                {
                    Print(pm.SetDefault(null), output, err);
                    output.WriteLine($"Default cleared ({pm.Config.DefaultProfile} is the default). Open a new terminal for plain 'claude' to use it.");
                }
                else
                {
                    Print(pm.SetDefault(Need(pos, 1, "clauderoo default <name> | --clear")[0]), output, err);
                    output.WriteLine($"Default is now {pm.Config.DefaultProfile}. Open a new terminal for plain 'claude' to use it.");
                }
                return 0;
            }
            case "own":
            {
                var a = Need(pos, 2, "clauderoo own <name> <item>");
                pm.Own(a[0], a[1]);
                output.WriteLine($"{a[0]} now owns {a[1]}.");
                return 0;
            }
            case "share":
            {
                var a = Need(pos, 2, "clauderoo share <name> <item>");
                Print(pm.Share(a[0], a[1]), output, err);
                output.WriteLine($"{a[0]} now shares {a[1]} from the home profile.");
                return 0;
            }
            case "remove":
            {
                var force = pos.Remove("--force");
                var name = Need(pos, 1, "clauderoo remove <name> [--force]")[0];
                pm.Remove(name, force);
                output.WriteLine($"Removed '{name}'. Its folder was moved to {pm.Paths.RemovedDir}.");
                return 0;
            }
            case "status":
            {
                var s = pm.Status();
                output.WriteLine($"Default profile: {s.DefaultProfile ?? "(none)"}");
                output.WriteLine($"CLAUDE_CONFIG_DIR (user): {(string.IsNullOrEmpty(s.UserEnvValue) ? "(not set)" : s.UserEnvValue)}");
                output.WriteLine(s.EnvMatchesDefault
                    ? "Environment matches the default profile."
                    : "Environment does NOT match the default profile. Run 'clauderoo default <name>'.");
                foreach (var p in s.Problems) err.WriteLine($"warning: {p}");
                return 0;
            }
            case "tray":
            {
                var exe = TrayExe();
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
                return 0;
            }
            case "autostart":
            {
                var mode = Need(pos, 1, "clauderoo autostart on|off")[0];
                if (mode is not ("on" or "off")) throw new ClauderooException("Usage: clauderoo autostart on|off");
                AutoStart.Set(mode == "on", mode == "on"
                    ? AutoStart.ResolveExe(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TrayExe())
                    : Path.Combine(AppContext.BaseDirectory, "clauderoow.exe"));
                output.WriteLine(mode == "on" ? "Tray app will start at login." : "Tray app will no longer start at login.");
                return 0;
            }
            case "install":
            {
                var dir = Installer.Install(AppContext.BaseDirectory);
                output.WriteLine($"Installed to {dir} and added it to your PATH. Open a new terminal to use 'clauderoo' (or the short form 'cr').");
                return 0;
            }
        }
        return 0;
    }

    private static string TrayExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "clauderoow.exe");
        return File.Exists(exe) ? exe : throw new ClauderooException($"clauderoow.exe not found next to clauderoo.exe ({exe}).");
    }

    private static void Print(SyncReport r, TextWriter output, TextWriter err, string problemPrefix = "")
    {
        foreach (var n in r.Notices) output.WriteLine(n);
        foreach (var p in r.Problems) err.WriteLine(problemPrefix + p);
    }

    // Splits at the first "--": positional args before, claude args after.
    private static (List<string> pos, List<string> claudeArgs) Split(string[] rest)
    {
        var i = Array.IndexOf(rest, "--");
        return i < 0 ? ([.. rest], []) : ([.. rest[..i]], [.. rest[(i + 1)..]]);
    }

    private static List<string> Need(List<string> pos, int count, string usage) =>
        pos.Count == count ? pos : throw new ClauderooException($"Usage: {usage}");

    private static IEnumerable<string> FormatList(IReadOnlyList<ProfileInfo> infos)
    {
        const string NoLogin = "(not logged in)";
        var nameW = infos.Max(i => i.Name.Length);
        var emailW = infos.Max(i => (i.Email ?? NoLogin).Length);
        foreach (var i in infos)
        {
            var tail = i.IsHome ? "(home)"
                : i.Overrides.Count > 0 ? "overrides: " + string.Join(", ", i.Overrides) : "";
            yield return $"{(i.IsDefault ? "*" : " ")} {i.Name.PadRight(nameW)}  {(i.Email ?? NoLogin).PadRight(emailW)}  {tail}".TrimEnd();
        }
    }
}
