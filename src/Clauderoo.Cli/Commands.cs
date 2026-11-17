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
  usage [name] [--json]               show 5-hour and 7-day usage limits (Claude-login profiles)
  auto [on|off] [--threshold N] [--order a,b,c] [--once] [--json]
                                      switch the default profile when its usage reaches the threshold
  status                              show the default profile and any problems
  tray                                start the tray app
  manage                              open the profile manager window
  autostart on|off                    start the tray app at login
  install                             copy clauderoo to %LOCALAPPDATA%\clauderoo and add it to PATH
  help                                show this help";

    public static int Run(string[] args, TextWriter output, TextWriter err, ProfileManager pm,
        Func<LaunchSpec, int> launch, bool? wtAvailable = null) =>
        Run(args, output, err, pm, launch, launch, wtAvailable);

    public static int Run(string[] args, TextWriter output, TextWriter err, ProfileManager pm,
        Func<LaunchSpec, int> startNewWindow, Func<LaunchSpec, int> runInPlace, bool? wtAvailable = null, string? claudePath = null,
        Func<string?>? readSecret = null, Func<CancellationTokenSource, IDisposable>? hookCancel = null,
        UsageClient? usage = null, Func<DateTimeOffset>? clock = null)
    {
        var json = WantsJson(args);
        try
        {
            return Dispatch(args, output, err, pm, startNewWindow, runInPlace, wtAvailable, claudePath, readSecret ?? ReadSecretFromConsole,
                hookCancel ?? HookConsoleCancel, usage, clock ?? (() => DateTimeOffset.UtcNow));
        }
        catch (SessionsRunningException e)
        {
            if (json) { WriteJsonError(err, $"{e.Message} (use --force to do it anyway)"); return 1; }
            err.WriteLine($"{e.Message} (use --force to do it anyway)");
            return 1;
        }
        catch (ClauderooException e)
        {
            if (json) { WriteJsonError(err, e.Message); return 1; }
            err.WriteLine(e.Message);
            return 1;
        }
        catch (Exception e)
        {
            if (json) { WriteJsonError(err, $"Unexpected error: {e.Message}"); return 2; }
            err.WriteLine($"Unexpected error: {e.Message}");
            return 2;
        }
    }

    private static int Dispatch(string[] args, TextWriter output, TextWriter err, ProfileManager pm,
        Func<LaunchSpec, int> startNewWindow, Func<LaunchSpec, int> runInPlace, bool? wtAvailable, string? claudePath, Func<string?> readSecret,
        Func<CancellationTokenSource, IDisposable> hookCancel, UsageClient? usage, Func<DateTimeOffset> clock)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            output.WriteLine(Usage);
            return 0;
        }

        var cmd = args[0];
        string[] known = ["init", "add", "list", "start", "run", "default", "own", "share", "remove", "restore", "rename",
            "status", "tray", "autostart", "install", "provider", "env", "check", "apikey", "manage", "usage", "auto"];
        if (!known.Contains(cmd))
            throw new ClauderooException($"Unknown command '{cmd}'. Run 'clauderoo help'.");

        // Commands that work without an initialised config skip the pre-sync.
        // 'status' syncs itself. A failing pre-sync must never make a command fail.
        if (cmd is not ("init" or "install" or "autostart" or "tray" or "manage" or "status" or "apikey"))
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
                foreach (var line in FormatList(pm.List(), LoadUsageCache(pm))) output.WriteLine(line);
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
                    Print(pm.SetDefaultByUser(null), output, err);
                    output.WriteLine($"Default cleared ({pm.Config.DefaultProfile} is the default). Open a new terminal for plain 'claude' to use it.");
                }
                else
                {
                    Print(pm.SetDefaultByUser(Need(pos, 1, "clauderoo default <name> | --clear")[0]), output, err);
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
            case "usage":
                return UsageCmd(pos, output, err, pm, hookCancel, usage);
            case "auto":
                return AutoCmd(pos, output, err, pm, hookCancel, usage, clock);
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

    private static IEnumerable<string> FormatList(IReadOnlyList<ProfileInfo> infos, IReadOnlyDictionary<string, UsageReading> cache)
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
        string Use(ProfileInfo i) => i.Provider == Providers.Login && cache.TryGetValue(i.Name, out var r) ? ShortUsage(r) : "";
        var useW = infos.Max(i => Use(i).Length);
        foreach (var i in infos)
        {
            var tail = i.IsHome ? "(home)"
                : i.Overrides.Count > 0 ? "overrides: " + string.Join(", ", i.Overrides) : "";
            yield return $"{(i.IsDefault ? "*" : " ")} {i.Name.PadRight(nameW)}  {Who(i).PadRight(emailW)}  {(useW > 0 ? Use(i).PadRight(useW) + "  " : "")}{tail}".TrimEnd();
        }
    }
    // "42%/63%" (5h/7d), "-" for a missing window, blank when neither is known.
    private static string ShortUsage(UsageReading r)
    {
        string P(string name) => r.Windows.FirstOrDefault(w => w.Name == name) is { } w
            ? $"{Math.Round(w.Percent, MidpointRounding.AwayFromZero):0}%" : "-";
        var s = $"{P("5h")}/{P("7d")}";
        return s == "-/-" ? "" : s;
    }

    private static IReadOnlyDictionary<string, UsageReading> LoadUsageCache(ProfileManager pm)
    {
        try { return new UsageCache(pm.Paths).Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new Dictionary<string, UsageReading>(); }
    }

    private static List<string> PickOne(ProfileManager pm, string name) { pm.Get(name); return [name]; }

    private sealed record UsageRow(string Name, string Status, UsageReading? Reading, string? Note);

    private static int UsageCmd(List<string> pos, TextWriter output, TextWriter err, ProfileManager pm,
        Func<CancellationTokenSource, IDisposable> hookCancel, UsageClient? injected)
    {
        const string usage = "clauderoo usage [name] [--json]";
        var json = pos.Remove("--json");
        if (pos.Count > 1) throw new ClauderooException($"Usage: {usage}");
        var names = pos.Count == 1
            ? PickOne(pm, pos[0])
            : pm.Config.Profiles.Keys.ToList();

        // Program.cs owns the one client (built on UsageClient.CreateHandler) and disposes it; tests inject a fake.
        var client = injected ?? throw new InvalidOperationException("No usage client was given.");
        var cache = new UsageCache(pm.Paths);
        var rows = new List<UsageRow>();
        using var cts = new CancellationTokenSource();
        using (hookCancel(cts))
        {
            foreach (var name in names)
            {
                UsageResult r;
                try
                {
                    r = client.FetchAsync(name, pm.ConfigDirOf(name), pm.Get(name), cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    if (json) WriteJsonError(err, "Cancelled.");
                    else err.WriteLine("Cancelled.");
                    return 1;
                }
                rows.Add(ToRow(name, r, cache, err));
            }
        }

        if (json) WriteUsageJson(rows, output);
        else foreach (var line in FormatUsage(rows)) output.WriteLine(line);
        return rows.Any(r => r.Reading == null && r.Status is "failed" or "unavailable" or "rateLimited") ? 1 : 0;
    }

    // Ok readings are cached; RateLimited, Failed and Unavailable fall back to the cached reading (flagged stale by
    // the Note).
    private static UsageRow ToRow(string name, UsageResult r, UsageCache cache, TextWriter err)
    {
        switch (r)
        {
            case UsageResult.Ok ok:
                try { cache.Put(ok.Reading); }
                catch (IOException e) { err.WriteLine($"warning: couldn't save usage: {e.Message}"); }
                return new UsageRow(name, "ok", ok.Reading, null);
            case UsageResult.RateLimited or UsageResult.Failed or UsageResult.Unavailable:
            {
                var (status, why) = r switch
                {
                    UsageResult.RateLimited => ("rateLimited", "The usage service is rate limiting requests."),
                    UsageResult.Failed f => ("failed", f.Message), // already scrubbed of the token
                    _ => ("unavailable", "The usage service answered with something unexpected."),
                };
                UsageReading? cached = null;
                try { if (cache.Load().TryGetValue(name, out var c)) cached = c; } catch (IOException) { }
                return new UsageRow(name, status, cached, why);
            }
            case UsageResult.Expired:
                return new UsageRow(name, "expired", null, $"Sign-in expired. Start a session with 'cr start {name}' to refresh it.");
            case UsageResult.NotSignedIn:
                return new UsageRow(name, "notSignedIn", null, "Not signed in.");
            case UsageResult.NotTracked nt:
                return new UsageRow(name, "notTracked", null, $"Not tracked ({nt.Provider} has no subscription limits).");
            default:
                throw new InvalidOperationException($"Unknown usage result {r.GetType().Name}.");
        }
    }

    private static IEnumerable<string> FormatUsage(List<UsageRow> rows)
    {
        foreach (var row in rows)
        {
            var plan = row.Reading?.Plan;
            yield return plan == null ? row.Name : $"{row.Name} ({plan})";
            if (row.Reading != null)
                foreach (var w in row.Reading.Windows)
                    yield return $"  {w.Name,-6} {Math.Round(w.Percent, MidpointRounding.AwayFromZero),3:0}%"
                        + (w.ResetsAt is { } at ? $"  resets {ResetText(at)}" : "");
            if (row.Note != null) yield return "  " + row.Note;
            if (row.Reading != null && row.Status != "ok")
                yield return $"  as of {row.Reading.FetchedAt.ToLocalTime():HH:mm} (last saved reading)";
        }
    }

    private static string ResetText(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : local.ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    private const string AutoUsage = "clauderoo auto [on|off] [--threshold N] [--order a,b,c] [--once] [--json]";

    // Exit codes for --once: 0 switched, 2 nothing to do, 3 no target (1 is an error, as everywhere).
    private static int AutoCmd(List<string> pos, TextWriter output, TextWriter err, ProfileManager pm,
        Func<CancellationTokenSource, IDisposable> hookCancel, UsageClient? injected, Func<DateTimeOffset> clock)
    {
        var json = pos.Remove("--json");
        var once = pos.Remove("--once");
        bool? enable = null;
        int? threshold = null;
        List<string>? order = null;
        for (var i = 0; i < pos.Count; i++)
        {
            switch (pos[i])
            {
                case "on": enable = true; break;
                case "off": enable = false; break;
                case "--threshold":
                    if (i + 1 >= pos.Count) throw new ClauderooException($"Usage: {AutoUsage}");
                    if (!int.TryParse(pos[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
                        || n is < 6 or > 100)
                        throw new ClauderooException("The threshold must be a whole number from 6 to 100 (switching needs a 5-point margin below it).");
                    threshold = n;
                    break;
                case "--order":
                    if (i + 1 >= pos.Count) throw new ClauderooException($"Usage: {AutoUsage}");
                    order = pos[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    foreach (var name in order) pm.Get(name); // throws for an unknown profile
                    break;
                default: throw new ClauderooException($"Usage: {AutoUsage}");
            }
        }

        var cfg = pm.Config;
        if (enable != null || threshold != null || order != null)
        {
            var s = cfg.AutoSwitch ??= new AutoSwitchSettings();
            if (enable != null) s.Enabled = enable.Value;
            if (threshold != null) s.Threshold = threshold.Value;
            if (order != null) s.Order = order;
            ConfigStore.Save(pm.Paths, cfg);
        }

        if (once) return AutoOnce(output, err, pm, hookCancel, injected, clock, json);

        if (json) throw new ClauderooException("--json only applies to 'auto --once'.");
        var cur = cfg.AutoSwitch ?? new AutoSwitchSettings();
        output.WriteLine($"Auto-switch is {(cur.Enabled ? "on" : "off")}.");
        output.WriteLine($"Threshold: {cur.Threshold}%");
        output.WriteLine($"Order: {(cur.Order.Count > 0 ? string.Join(", ", cur.Order) : "profile list order")}");
        return 0;
    }

    private static int AutoOnce(TextWriter output, TextWriter err, ProfileManager pm,
        Func<CancellationTokenSource, IDisposable> hookCancel, UsageClient? injected, Func<DateTimeOffset> clock, bool json)
    {
        var client = injected ?? throw new InvalidOperationException("No usage client was given.");
        var cache = new UsageCache(pm.Paths);
        var cfg = pm.Config;
        var usage = new Dictionary<string, UsageReading>();
        using (var cts = new CancellationTokenSource())
        using (hookCancel(cts))
        {
            foreach (var (name, entry) in cfg.Profiles)
            {
                if (Providers.Of(entry) != Providers.Login) continue;
                UsageResult r;
                try { r = client.FetchAsync(name, pm.ConfigDirOf(name), entry, cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    if (json) WriteJsonError(err, "Cancelled.");
                    else err.WriteLine("Cancelled.");
                    return 1;
                }
                // A failed fetch falls back to the cached reading; the engine ignores it once it is no longer fresh.
                if (ToRow(name, r, cache, err).Reading is { } reading) usage[name] = reading;
            }
        }

        var now = clock();
        var states = pm.AutoSwitchState;
        var state = states.Load();
        var from = cfg.DefaultProfile;
        var decision = AutoSwitch.Decide(cfg, usage, now, state);

        string action, reason;
        string? to = null;
        int code;
        switch (decision)
        {
            case Decision.SwitchTo sw:
                Print(pm.SetDefault(sw.Profile), output, err);
                (action, to, reason, code) = ("switched", sw.Profile, sw.Reason, 0);
                break;
            case Decision.NoTarget nt:
                (action, reason, code) = ("noTarget", nt.Reason, 3);
                break;
            case Decision.Recovered rec:
                (action, reason, code) = ("none", $"{rec.Profile} has room again.", 2);
                break;
            default:
                (action, reason, code) = ("none", cfg.AutoSwitch is { Enabled: true } ? "Nothing to do." : "Auto-switch is off.", 2);
                break;
        }

        // Next owns the flag transitions, so it runs after every decision (it also resets the no-target notice).
        states.Save(AutoSwitch.Next(state, decision, from, usage, now, cfg.AutoSwitch));

        if (json)
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, action, from, to, reason },
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true }));
        else
        {
            output.WriteLine(reason);
            if (to != null) output.WriteLine($"New terminals now use {to}.");
            else if (action == "noTarget") output.WriteLine("No other profile has room.");
        }
        return code;
    }

    // --json before any "--" means every error must be JSON on stderr.
    private static bool WantsJson(string[] args)
    {
        var end = Array.IndexOf(args, "--");
        return args.Take(end < 0 ? args.Length : end).Contains("--json");
    }

    // The one place a JSON error is written, next to WriteUsageJson so both can move onto a shared helper.
    private static void WriteJsonError(TextWriter err, string message) =>
        err.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, error = message }));

    // The one place usage JSON is written, so it can be moved onto the shared JSON helper.
    private static void WriteUsageJson(List<UsageRow> rows, TextWriter output)
    {
        static string? Iso(DateTimeOffset? t) => t?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var payload = new
        {
            schemaVersion = 1,
            profiles = rows.Select(r => new
            {
                name = r.Name,
                status = r.Status,
                plan = r.Reading?.Plan,
                fetchedAt = Iso(r.Reading?.FetchedAt),
                windows = (r.Reading?.Windows ?? []).Select(w => new { name = w.Name, percent = w.Percent, resetsAt = Iso(w.ResetsAt) }).ToList(),
                // Additive: why a failed or unavailable row has no fresh reading (scrubbed of the token).
                message = r.Status is "failed" or "unavailable" ? r.Note : null,
            }).ToList(),
        };
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
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
