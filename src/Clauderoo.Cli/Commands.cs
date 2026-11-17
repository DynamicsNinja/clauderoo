using System.Diagnostics;
using Clauderoo.Core;

namespace Clauderoo.Cli;

public static class Commands
{
    private const string Usage = @"Usage: clauderoo <command> [options]   (short form: cr <command>)

  init [--name <n>] [--home <path>]   set up clauderoo around your existing ~/.claude
  add <name>                          create a new profile
  list                                list profiles
  start <name> [dir] [-- args]        run Claude for a profile in this terminal (run = same)
  start <name> [dir] --tab [-- args]  open it in a new Windows Terminal tab instead
  default <name> | --clear            set (or clear) the default profile for plain 'claude'
  own <name> <item>                   keep a profile's own copy of a shared item
  share <name> <item>                 go back to sharing an item from the home profile
  remove <name> [--force]             remove a profile (its folder is moved, not deleted)
  restore <name>                      bring back the most recently removed profile of that name
  rename <old> <new> [--force]   rename a profile
  provider <name> login               use the profile's own Claude login (default)
  provider <name> vertex --project <p> --region <r> [--sonnet <m>] [--opus <m>] [--haiku <m>]
                                      use Claude on Google Vertex AI
  provider <name> apikey              use an Anthropic API key (read from stdin or prompted)
  env <name> [set A=1 B=2 | unset A]  show, set or unset a profile's environment variables
  check <name>                        check the profile's sign-in (Vertex: full setup checklist)
  apikey <name>                       print the profile's API key (used by Claude's apiKeyHelper)
  status                              show the default profile and any problems
  tray                                start the tray app
  manage                              open the profile manager window
  autostart on|off                    start the tray app at login
  install                             copy clauderoo to %LOCALAPPDATA%\clauderoo and add it to PATH
  version | --version                 print the clauderoo version
  upgrade [--check]                   install the latest GitHub release (--check only reports)
  uninstall [--yes]                   remove clauderoo from PATH and autostart; your ~/.clauderoo data is kept
  help                                show this help";

    public static int Run(string[] args, TextWriter output, TextWriter err, ProfileManager pm,
        Func<LaunchSpec, int> launch, bool? wtAvailable = null) =>
        Run(args, output, err, pm, launch, launch, wtAvailable);

    public static int Run(string[] args, TextWriter output, TextWriter err, ProfileManager pm,
        Func<LaunchSpec, int> startNewWindow, Func<LaunchSpec, int> runInPlace, bool? wtAvailable = null, string? claudePath = null,
        Func<string?>? readSecret = null, Func<CancellationTokenSource, IDisposable>? hookCancel = null, ReleaseHooks? hooks = null)
    {
        try
        {
            return Dispatch(args, output, err, pm, startNewWindow, runInPlace, wtAvailable, claudePath, readSecret ?? ReadSecretFromConsole,
                hookCancel ?? HookConsoleCancel, hooks);
        }
        catch (SessionsRunningException e)
        {
            err.WriteLine($"{e.Message} (use --force to do it anyway)");
            return 1;
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
        Func<LaunchSpec, int> startNewWindow, Func<LaunchSpec, int> runInPlace, bool? wtAvailable, string? claudePath, Func<string?> readSecret,
        Func<CancellationTokenSource, IDisposable> hookCancel, ReleaseHooks? hooks)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            output.WriteLine(Usage);
            return 0;
        }

        var cmd = args[0] == "--version" ? "version" : args[0];
        string[] known = ["init", "add", "list", "start", "run", "default", "own", "share", "remove", "restore", "rename",
            "status", "tray", "autostart", "install", "provider", "env", "check", "apikey", "manage",
            "version", "upgrade", "uninstall"];
        if (!known.Contains(cmd))
            throw new ClauderooException($"Unknown command '{cmd}'. Run 'clauderoo help'.");

        // Commands that work without an initialised config skip the pre-sync.
        // 'status' syncs itself. A failing pre-sync must never make a command fail.
        if (cmd is not ("init" or "install" or "autostart" or "tray" or "manage" or "status" or "apikey"
            or "version" or "upgrade" or "uninstall"))
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
            case "run":
            {
                // Runs Claude in this terminal; --tab opens a new Windows Terminal tab instead.
                var tab = pos.Remove("--tab");
                if (pos.Count is < 1 or > 2) throw new ClauderooException($"Usage: clauderoo {cmd} <name> [dir] [--tab] [-- args]");
                var dir = pos.Count > 1 ? Path.GetFullPath(pos[1]) : Environment.CurrentDirectory;
                if (!Directory.Exists(dir)) throw new ClauderooException($"Folder not found: {dir}");
                if (!tab)
                    return runInPlace(Launcher.InPlace(pm.Config, pm.Paths, pos[0], dir, claudeArgs, claudePath));
                startNewWindow(Launcher.NewWindow(pm.Config, pm.Paths, pos[0], dir, claudeArgs, wtAvailable ?? Launcher.WtAvailable()));
                return 0;
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
            case "restore":
            {
                var name = Need(pos, 1, "clauderoo restore <name>")[0];
                Print(pm.Restore(name), output, err);
                output.WriteLine($"Restored '{name}' with its settings and provider.");
                return 0;
            }
            case "rename":
            {
                var force = pos.Remove("--force");
                var a = Need(pos, 2, "clauderoo rename <old> <new> [--force]");
                Print(pm.Rename(a[0], a[1], force), output, err);
                output.WriteLine($"Renamed '{a[0]}' to '{a[1]}'. Restart any open {a[0]} sessions with 'cr start {a[1]}'.");
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
            case "manage":
            {
                var exe = TrayExe();
                Process.Start(new ProcessStartInfo(exe, "--manage") { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
                return 0;
            }
            case "provider":
                return ProviderCmd(pos, output, err, pm, readSecret);
            case "env":
                return EnvCmd(pos, output, err, pm);
            case "check":
            {
                var name = Need(pos, 1, "clauderoo check <name>")[0];
                if (pm.Get(name).Provider != Providers.Vertex)
                {
                    var r = pm.Check(name);
                    output.WriteLine(r.Message);
                    return r.Ok ? 0 : 1;
                }
                // The model probes run inside CheckSetupAsync, so announce them first.
                output.WriteLine("Testing models (this sends a tiny prompt per model)…");
                // Ctrl+C stops the check (and kills a running `claude -p`) instead of the process.
                using var cts = new CancellationTokenSource();
                IReadOnlyList<CheckItem> items;
                using (hookCancel(cts))
                {
                    try
                    {
                        // Lines appear as each check finishes, not at the end.
                        items = pm.CheckSetupAsync(name, cts.Token, new LineProgress(output)).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        err.WriteLine("Cancelled.");
                        return 1;
                    }
                }
                return items.Any(i => i.Status == CheckStatus.Fail) ? 1 : 0;
            }
            case "apikey":
            {
                // Run by Claude's apiKeyHelper: stdout must be the key and nothing else.
                var name = Need(pos, 1, "clauderoo apikey <name>")[0];
                var key = pm.GetApiKey(name);
                if (string.IsNullOrEmpty(key))
                {
                    err.WriteLine($"No API key saved for '{name}'.");
                    return 1;
                }
                output.WriteLine(key);
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
            case "version":
                return ReleaseCommands.Version(output, hooks);
            case "upgrade":
                return ReleaseCommands.Upgrade(pos, output, err, hooks);
            case "uninstall":
                return ReleaseCommands.Uninstall(pos, output, pm.Paths, hooks);
            case "install":
            {
                var dir = Installer.Install(AppContext.BaseDirectory);
                output.WriteLine($"Installed to {dir} and added it to your PATH. Open a new terminal to use 'clauderoo' (or the short form 'cr').");
                return 0;
            }
        }
        return 0;
    }

    private static int ProviderCmd(List<string> pos, TextWriter output, TextWriter err, ProfileManager pm, Func<string?> readSecret)
    {
        const string usage = "clauderoo provider <name> login | vertex --project <p> --region <r> [--sonnet <m>] [--opus <m>] [--haiku <m>] | apikey";
        if (pos.Count < 2) throw new ClauderooException($"Usage: {usage}");
        var name = pos[0];
        var kind = pos[1];
        var rest = pos.Skip(2).ToList();
        SyncReport report;
        switch (kind)
        {
            case Providers.Login:
                if (rest.Count > 0) throw new ClauderooException($"Usage: {usage}");
                report = pm.SetProvider(name, Providers.Login);
                break;
            case Providers.Vertex:
            {
                var v = new VertexSettings { Project = "", Region = "" };
                for (var i = 0; i < rest.Count; i += 2)
                {
                    if (i + 1 >= rest.Count) throw new ClauderooException($"Usage: {usage}");
                    var val = rest[i + 1];
                    switch (rest[i])
                    {
                        case "--project": v.Project = val; break;
                        case "--region": v.Region = val; break;
                        case "--sonnet": v.SonnetModel = val; break;
                        case "--opus": v.OpusModel = val; break;
                        case "--haiku": v.HaikuModel = val; break;
                        default: throw new ClauderooException($"Usage: {usage}");
                    }
                }
                report = pm.SetProvider(name, Providers.Vertex, v);
                break;
            }
            case Providers.ApiKey:
            {
                if (rest.Count > 0) throw new ClauderooException($"Usage: {usage}");
                var key = readSecret();
                report = pm.SetProvider(name, Providers.ApiKey, null, string.IsNullOrWhiteSpace(key) ? null : key.Trim());
                break;
            }
            default:
                throw new ClauderooException($"Unknown provider '{kind}'. Known: login, vertex, apikey.");
        }
        Print(report, output, err);
        output.WriteLine($"'{name}' now uses {kind}.");
        return 0;
    }

    private static int EnvCmd(List<string> pos, TextWriter output, TextWriter err, ProfileManager pm)
    {
        const string usage = "clauderoo env <name> [set A=1 B=2 | unset A B]";
        if (pos.Count < 1) throw new ClauderooException($"Usage: {usage}");
        var name = pos[0];
        if (pos.Count == 1)
        {
            foreach (var kv in pm.GetEnv(name).OrderBy(k => k.Key, StringComparer.Ordinal))
                output.WriteLine($"{kv.Key}={kv.Value}");
            return 0;
        }
        var items = pos.Skip(2).ToList();
        if (items.Count == 0) throw new ClauderooException($"Usage: {usage}");
        if (pos[1] == "set")
        {
            var set = new List<KeyValuePair<string, string>>();
            foreach (var it in items)
            {
                var eq = it.IndexOf('=');
                if (eq <= 0) throw new ClauderooException($"Expected NAME=value, got '{it}'.");
                set.Add(new(it[..eq], it[(eq + 1)..]));
            }
            Print(pm.SetEnv(name, set, []), output, err);
            output.WriteLine($"Set {set.Count} variable(s) on '{name}'.");
        }
        else if (pos[1] == "unset")
        {
            Print(pm.SetEnv(name, [], items), output, err);
            output.WriteLine($"Unset {items.Count} variable(s) on '{name}'.");
        }
        else throw new ClauderooException($"Usage: {usage}");
        return 0;
    }

    // Hidden prompt (no echo); a redirected stdin is read as one line.
    private static string? ReadSecretFromConsole()
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine();
        Console.Error.Write("API key: ");
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
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
        string Who(ProfileInfo i) => i.Provider switch
        {
            Providers.Vertex => $"vertex ({i.ProviderDetail})",
            Providers.ApiKey => "apikey",
            _ => i.Email ?? NoLogin,
        };
        var emailW = infos.Max(i => Who(i).Length);
        foreach (var i in infos)
        {
            var tail = i.IsHome ? "(home)"
                : i.Overrides.Count > 0 ? "overrides: " + string.Join(", ", i.Overrides) : "";
            yield return $"{(i.IsDefault ? "*" : " ")} {i.Name.PadRight(nameW)}  {Who(i).PadRight(emailW)}  {tail}".TrimEnd();
        }
    }
    // Writes each checklist line as soon as it's reported (synchronously, unlike Progress<T>): a "⋯ testing" line per
    // model when the probes start, then every result. The planned (Pending) rows are only for the window.
    private sealed class LineProgress(TextWriter output) : IProgress<CheckItem>
    {
        public void Report(CheckItem value)
        {
            if (value.Status == CheckStatus.Pending) return;
            lock (output) output.WriteLine(CheckItemFormatter.Format(value));
        }
    }

    private static IDisposable HookConsoleCancel(CancellationTokenSource cts)
    {
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        Console.CancelKeyPress += handler;
        return new Unhook(() => Console.CancelKeyPress -= handler);
    }

    private sealed class Unhook(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
