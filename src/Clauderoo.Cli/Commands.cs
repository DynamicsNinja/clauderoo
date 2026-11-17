using System.Diagnostics;
using Clauderoo.Core;

namespace Clauderoo.Cli;

public static class Commands
{
    private const string Usage = @"Usage: clauderoo <command> [options]   (short form: cr <command>)

  init [--name <n>] [--home <path>]   set up clauderoo around your existing ~/.claude
  add <name>                          create a new profile
  list                                list profiles
  start [name] [dir] [-- args]        run Claude for a profile in this terminal (run = same)
                                      no name: use the folder's mapping, else the default
  start [name] [dir] --tab [-- args]  open it in a new Windows Terminal tab instead
  start [name] [dir] --isolated       run with a separate config folder: no shared plugins, skills, hooks or MCP servers
  map <profile> [dir]                 use <profile> for dir (default: here) and everything below it
  unmap [dir]                         remove a folder mapping
  maps                                list folder mappings
  which                               show which profile this terminal is using
  sessions [profile]                  list conversations across profiles, newest first
  resume <id> --as <profile> [--isolated] [--tab] [-- args]
                                      copy a conversation into <profile> and continue it there
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
  provider <name> token               use a Claude token from 'claude setup-token' (read from stdin or prompted)
  env <name> [set A=1 B=2 | unset A]  show, set or unset a profile's environment variables
  export <file> [names...] [--no-env] [--force]
                                      save profile settings to a file (no secrets or logins)
  import <file> [--force]             create profiles from an export (--force updates existing ones)
  check <name>                        check the profile's sign-in (Vertex: full setup checklist)
  apikey <name>                       print the profile's API key (used by Claude's apiKeyHelper)
  usage [name] [--json]               show 5-hour and 7-day usage limits (Claude-login profiles)
  auto [on|off] [--threshold N] [--order a,b,c] [--once] [--json]
                                      switch the default profile when its usage reaches the threshold
  status                              show the default profile and any problems
  --json                              list, status, maps, which, check, usage, sessions and auto --once: print JSON for scripts
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
        Func<string?>? readSecret = null, Func<CancellationTokenSource, IDisposable>? hookCancel = null, ReleaseHooks? hooks = null,
        UsageClient? usage = null, Func<DateTimeOffset>? clock = null)
    {
        try
        {
            Func<string, string?> secretReader = readSecret is null ? ReadSecretFromConsole : _ => readSecret();
            return Dispatch(args, output, err, pm, startNewWindow, runInPlace, wtAvailable, claudePath, secretReader,
                hookCancel ?? HookConsoleCancel, hooks, usage, clock ?? (() => DateTimeOffset.UtcNow));
        }
        catch (SessionsRunningException e)
        {
            var msg = $"{e.Message} (use --force to do it anyway)";
            if (WantsJson(args)) JsonOut.Error(err, msg); else err.WriteLine(msg);
            return 1;
        }
        catch (ClauderooException e)
        {
            if (WantsJson(args)) JsonOut.Error(err, e.Message); else err.WriteLine(e.Message);
            return 1;
        }
        catch (Exception e)
        {
            var msg = $"Unexpected error: {e.Message}";
            if (WantsJson(args)) JsonOut.Error(err, msg); else err.WriteLine(msg);
            return 2;
        }
    }

    /// <summary>The commands that take --json. Any other command given --json gets a plain usage error.</summary>
    private static readonly string[] JsonCommands = ["list", "status", "maps", "which", "check", "usage", "sessions", "auto"];

    // --json before any "--" belongs to clauderoo; after it, it belongs to claude.
    private static bool WantsJson(string[] args) =>
        args.Length > 0 && JsonCommands.Contains(args[0]) && args.TakeWhile(a => a != "--").Skip(1).Contains("--json");

    private static int Dispatch(string[] args, TextWriter output, TextWriter err, ProfileManager pm,
        Func<LaunchSpec, int> startNewWindow, Func<LaunchSpec, int> runInPlace, bool? wtAvailable, string? claudePath,
        Func<string, string?> readSecret, Func<CancellationTokenSource, IDisposable> hookCancel, ReleaseHooks? hooks, UsageClient? usage,
        Func<DateTimeOffset> clock)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            output.WriteLine(Usage);
            return 0;
        }

        var cmd = args[0] == "--version" ? "version" : args[0];
        string[] known = ["init", "add", "list", "start", "run", "default", "own", "share", "remove", "restore", "rename",
            "status", "tray", "autostart", "install", "provider", "env", "check", "apikey", "manage", "map", "unmap", "maps", "which",
            "version", "upgrade", "uninstall", "usage", "auto", "export", "import", "sessions", "resume"];
        if (!known.Contains(cmd))
            throw new ClauderooException($"Unknown command '{cmd}'. Run 'clauderoo help'.");

        var (pos, claudeArgs) = Split(args.Skip(1).ToArray());
        var json = pos.RemoveAll(a => a == "--json") > 0;
        if (json && !JsonCommands.Contains(cmd))
            throw new ClauderooException($"'{cmd}' doesn't support --json.");

        // Commands that work without an initialised config skip the pre-sync.
        // 'status' syncs itself. A failing pre-sync must never make a command fail.
        if (cmd is not ("init" or "install" or "autostart" or "tray" or "manage" or "status" or "apikey"
            or "version" or "upgrade" or "uninstall"))
        {
            // With --json stdout carries only the payload, so sync notices go to stderr.
            try { Print(pm.SyncAll(), json ? err : output, err, "warning: "); }
            catch (Exception e) { err.WriteLine($"warning: sync failed: {e.Message}"); }
        }

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
                if (json)
                {
                    JsonOut.Write(output, new
                    {
                        Profiles = pm.List().Select(i => new
                        {
                            i.Name, i.IsHome, i.IsDefault, i.Provider,
                            Account = i.Email,
                            Detail = i.ProviderDetail,
                            i.Overrides,
                        }),
                    });
                    return 0;
                }
                foreach (var line in FormatList(pm.List(), LoadUsageCache(pm))) output.WriteLine(line);
                return 0;
            case "start":
            case "run":
            {
                // Runs Claude in this terminal; --tab opens a new Windows Terminal tab instead.
                var tab = pos.Remove("--tab");
                var isolated = pos.Remove("--isolated");
                var startUsage = $"Usage: clauderoo {cmd} [name] [dir] [--tab] [--isolated] [-- args]";
                if (pos.Count > 2) throw new ClauderooException(startUsage);
                var cfg = pm.Config;
                string profile;
                string folderArg;
                if (pos.Count > 0 && cfg.Profiles.ContainsKey(pos[0]))
                {
                    profile = pos[0];
                    folderArg = pos.Count > 1 ? pos[1] : Environment.CurrentDirectory;
                }
                else
                {
                    // A first argument that isn't a profile is a folder; an unknown name with a dir is a typo.
                    if (pos.Count == 2 || (pos.Count == 1 && !Directory.Exists(pos[0])))
                        throw new ClauderooException(
                            $"Unknown profile '{pos[0]}'. Known: {string.Join(", ", cfg.Profiles.Keys)}.");
                    folderArg = pos.Count == 1 ? pos[0] : Environment.CurrentDirectory;
                    var hit = FolderMap.Resolve(cfg, folderArg);
                    if (hit is { } h)
                    {
                        profile = h.Profile;
                        output.WriteLine($"Using {profile} (mapped from {h.MappedDir})");
                    }
                    else if (cfg.DefaultProfile is { } def) profile = def;
                    else throw new ClauderooException(
                        "No profile given and no folder mapping or default applies. " + startUsage);
                }
                var dir = Path.GetFullPath(folderArg);
                if (!Directory.Exists(dir)) throw new ClauderooException($"Folder not found: {dir}");
                if (!tab)
                {
                    // A token profile's token is decrypted here, just before launch, into the child's environment only.
                    var spec = pm.WithToken(Launcher.InPlace(cfg, pm.Paths, profile, dir, claudeArgs, claudePath, isolated), profile);
                    if (isolated && pm.PrepareIsolated(profile).FirstRun)
                        output.WriteLine($"First isolated run for {profile}: log in once; later runs reuse it.");
                    return runInPlace(spec);
                }
                // The new tab runs `run <profile> [--isolated]`, which prepares the folder and injects any token itself,
                // so the token never appears on a command line.
                startNewWindow(Launcher.NewWindow(cfg, pm.Paths, profile, dir, claudeArgs, wtAvailable ?? Launcher.WtAvailable(), isolated: isolated));
                return 0;
            }
            case "map":
            {
                if (pos.Count is < 1 or > 2) throw new ClauderooException("Usage: clauderoo map <profile> [dir]");
                var dir = FolderMap.Normalize(pos.Count > 1 ? pos[1] : Environment.CurrentDirectory);
                pm.Map(pos[0], dir);
                output.WriteLine($"Mapped {dir} to {pos[0]}.");
                return 0;
            }
            case "unmap":
            {
                if (pos.Count > 1) throw new ClauderooException("Usage: clauderoo unmap [dir]");
                var dir = FolderMap.Normalize(pos.Count > 0 ? pos[0] : Environment.CurrentDirectory);
                output.WriteLine(pm.Unmap(dir) ? $"Unmapped {dir}." : $"{dir} was not mapped.");
                return 0;
            }
            case "maps":
            {
                var maps = pm.Maps();
                if (json)
                {
                    JsonOut.Write(output, new { Folders = maps.Select(m => new { m.Dir, m.Profile }) });
                    return 0;
                }
                if (maps.Count == 0) output.WriteLine("No folder mappings. Add one with 'clauderoo map <profile> [dir]'.");
                foreach (var m in maps) output.WriteLine($"{m.Dir}  ->  {m.Profile}");
                return 0;
            }
            case "which":
            {
                if (pos.Count > 0) throw new ClauderooException("Usage: clauderoo which");
                var which = WhichProfile.Resolve(pm.Config, pm.Paths, Environment.GetEnvironmentVariable, pm.Env);
                if (json) JsonOut.Write(output, new { which.Profile, which.Source, which.Isolated });
                else output.WriteLine(WhichProfile.Describe(which));
                return 0;
            }
            case "sessions":
                return SessionsCmd(pos, output, pm, json);
            case "resume":
                return ResumeCmd(pos, claudeArgs, output, pm, startNewWindow, runInPlace, wtAvailable, claudePath);
            case "export":
            {
                var force = pos.Remove("--force");
                var noEnv = pos.Remove("--no-env");
                if (pos.Count < 1) throw new ClauderooException("Usage: clauderoo export <file> [profiles...] [--no-env] [--force]");
                var names = pos.Skip(1).ToList();
                pm.ExportToFile(pos[0], names.Count > 0 ? names : null, !noEnv, force);
                output.WriteLine($"Exported to {Path.GetFullPath(pos[0])}. No secrets or logins are included.");
                if (!noEnv)
                    err.WriteLine("warning: environment variable values are included as plain text. Check the file before sharing it, or use --no-env.");
                return 0;
            }
            case "import":
            {
                var force = pos.Remove("--force");
                if (pos.Count != 1) throw new ClauderooException("Usage: clauderoo import <file> [--force]");
                var r = pm.ImportFile(pos[0], force);
                if (r.Created.Count > 0) output.WriteLine($"Created: {string.Join(", ", r.Created)}");
                if (r.Updated.Count > 0) output.WriteLine($"Updated: {string.Join(", ", r.Updated)}");
                void Section(string heading, IReadOnlyList<string>? items)
                {
                    if (items is not { Count: > 0 }) return;
                    output.WriteLine(heading);
                    foreach (var i in items) output.WriteLine("  " + i);
                }
                Section("Folder mappings added:", r.Mapped);
                if (r.AutoSwitchImported) output.WriteLine(ImportResult.AutoSwitchImportedText);
                Section("Environment variables (names only):", r.EnvNames);
                Section("Skipped:", r.Skipped);
                Section("Kept their sign-in:", r.KeptProvider);
                Section("Needs a key or token:", r.NeedSecret);
                Section("Notices:", r.Notices);
                if (!r.ChangedAnything) output.WriteLine("Nothing to import.");
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
                if (json)
                {
                    JsonOut.Write(output, new
                    {
                        s.DefaultProfile,
                        UserEnv = s.UserEnvValue,
                        s.EnvMatchesDefault,
                        s.Problems,
                    });
                    return 0;
                }
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
                return UsageCmd(pos, json, output, err, pm, hookCancel, usage);
            case "auto":
                return AutoCmd(pos, json, output, err, pm, hookCancel, usage, clock);
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
                    if (json) JsonOut.Write(output, new { r.Ok, r.Verified, r.Message, Items = Array.Empty<object>() });
                    else output.WriteLine(r.Message);
                    return r.Ok ? 0 : 1;
                }
                // The model probes run inside CheckSetupAsync, so announce them first.
                if (!json) output.WriteLine("Testing models (this sends a tiny prompt per model)…");
                // Ctrl+C stops the check (and kills a running `claude -p`) instead of the process.
                using var cts = new CancellationTokenSource();
                IReadOnlyList<CheckItem> items;
                using (hookCancel(cts))
                {
                    try
                    {
                        // Lines appear as each check finishes, not at the end.
                        items = pm.CheckSetupAsync(name, cts.Token, json ? null : new LineProgress(output)).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        if (json) JsonOut.Error(err, "Cancelled."); else err.WriteLine("Cancelled.");
                        return 1;
                    }
                }
                var failed = items.Any(i => i.Status == CheckStatus.Fail);
                if (json)
                    JsonOut.Write(output, new
                    {
                        Ok = !failed,
                        Verified = true,
                        Message = failed
                            ? $"{items.Count(i => i.Status == CheckStatus.Fail)} check(s) failed."
                            : "All checks passed.",
                        Items = items.Where(i => i.Status is not (CheckStatus.Pending or CheckStatus.Running)).Select(i => new
                        {
                            i.Key, i.Label, Status = i.Status.ToString().ToLowerInvariant(), i.Hint,
                        }),
                    });
                return failed ? 1 : 0;
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
                return ReleaseCommands.Uninstall(pos, output, pm.Paths, pm.Sessions, hooks);
            case "install":
            {
                var dir = Installer.Install(AppContext.BaseDirectory);
                output.WriteLine($"Installed to {dir} and added it to your PATH. Open a new terminal to use 'clauderoo' (or the short form 'cr').");
                return 0;
            }
        }
        return 0;
    }

    private static int ProviderCmd(List<string> pos, TextWriter output, TextWriter err, ProfileManager pm, Func<string, string?> readSecret)
    {
        const string usage = "clauderoo provider <name> login | vertex --project <p> --region <r> [--sonnet <m>] [--opus <m>] [--haiku <m>] | apikey | token";
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
                var key = readSecret("API key: ");
                report = pm.SetProvider(name, Providers.ApiKey, null, string.IsNullOrWhiteSpace(key) ? null : key.Trim());
                break;
            }
            case Providers.Token:
            {
                if (rest.Count > 0) throw new ClauderooException($"Usage: {usage}");
                var token = readSecret("Token: ");
                report = pm.SetProvider(name, Providers.Token, token: string.IsNullOrWhiteSpace(token) ? null : token.Trim());
                break;
            }
            default:
                throw new ClauderooException($"Unknown provider '{kind}'. Known: {ProfileManager.KnownProviders}.");
        }
        Print(report, output, err);
        output.WriteLine($"'{name}' now uses {kind}.");
        return 0;
    }

    // The first prompt is the user's own text: it goes to this terminal (or the JSON payload) and nowhere else.
    private static int SessionsCmd(List<string> pos, TextWriter output, ProfileManager pm, bool json)
    {
        if (pos.Count > 1) throw new ClauderooException("Usage: clauderoo sessions [profile] [--json]");
        var all = ConversationIndex.List(pm, pos.Count == 1 ? pos[0] : null);
        if (json)
        {
            // FilePath and any message content beyond the first prompt are never included.
            JsonOut.Write(output, new
            {
                Conversations = all.Select(c => new { c.Id, c.Profile, c.Isolated, Folder = c.Cwd, c.When, c.FirstPrompt }),
            });
            return 0;
        }
        if (all.Count == 0)
        {
            output.WriteLine("No conversations found.");
            return 0;
        }
        string Who(Conversation c) => c.Isolated ? $"{c.Profile} (isolated)" : c.Profile;
        var whoW = all.Max(c => Who(c).Length);
        var folderW = Math.Min(60, all.Max(c => (c.Cwd ?? "?").Length));
        foreach (var c in all)
            output.WriteLine(string.Join("  ",
                ConversationIndex.Short(c.Id).PadRight(8),
                Who(c).PadRight(whoW),
                c.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                (c.Cwd ?? "?").PadRight(folderW),
                c.FirstPrompt ?? "").TrimEnd());
        return 0;
    }

    private static int ResumeCmd(List<string> pos, List<string> claudeArgs, TextWriter output, ProfileManager pm,
        Func<LaunchSpec, int> startNewWindow, Func<LaunchSpec, int> runInPlace, bool? wtAvailable, string? claudePath)
    {
        const string usage = "Usage: clauderoo resume <id> --as <profile> [--isolated] [--tab] [-- args]";
        var tab = pos.Remove("--tab");
        var isolated = pos.Remove("--isolated");
        var asAt = pos.IndexOf("--as");
        if (asAt < 0 || asAt + 1 >= pos.Count) throw new ClauderooException(usage);
        var profile = pos[asAt + 1];
        pos.RemoveRange(asAt, 2);
        if (pos.Count != 1) throw new ClauderooException(usage);

        var cfg = pm.Config;
        pm.Get(profile); // unknown profile -> error before anything else
        var c = ConversationIndex.Find(ConversationIndex.List(pm), pos[0]);
        if (string.IsNullOrEmpty(c.Cwd))
            throw new ClauderooException(
                $"Conversation {ConversationIndex.Short(c.Id)} doesn't record its folder; Claude can only resume a conversation from its own folder.");
        if (!Directory.Exists(c.Cwd))
            throw new ClauderooException(
                $"The folder {c.Cwd} no longer exists; Claude can only resume a conversation from its own folder.");

        List<string> args = ["--resume", c.Id, .. claudeArgs];
        // Built before the copy, so a missing token or claude fails before anything is written.
        var spec = tab ? null
            : pm.WithToken(Launcher.InPlace(cfg, pm.Paths, profile, c.Cwd, args, claudePath, isolated), profile);
        var where = isolated ? $"{profile} (isolated)" : profile;
        var shortId = ConversationIndex.Short(c.Id);
        switch (ConversationIndex.CopyTo(pm, c, profile, isolated))
        {
            case CopyOutcome.Copied:
                output.WriteLine($"Copied conversation {shortId} from {c.Profile} to {where}.");
                break;
            case CopyOutcome.BackedUpAndCopied:
                output.WriteLine($"Copied conversation {shortId} from {c.Profile} to {where}; the older copy there was kept as {c.Id}.jsonl.bak-*.");
                break;
            default:
                output.WriteLine($"{where} already has an up-to-date copy of conversation {shortId}.");
                break;
        }

        if (spec != null)
        {
            if (isolated && pm.PrepareIsolated(profile).FirstRun)
                output.WriteLine($"First isolated run for {profile}: log in once; later runs reuse it.");
            return runInPlace(spec);
        }
        // As with start --tab: the tab runs `run <profile>`, which injects any token itself.
        startNewWindow(Launcher.NewWindow(cfg, pm.Paths, profile, c.Cwd, args, wtAvailable ?? Launcher.WtAvailable(), isolated: isolated));
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

    // Hidden prompt (no echo) labelled e.g. "API key: "; a redirected stdin is read as one line.
    private static string? ReadSecretFromConsole(string label)
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine();
        Console.Error.Write(label);
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
            Providers.Token => "token",
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

    private static int UsageCmd(List<string> pos, bool json, TextWriter output, TextWriter err, ProfileManager pm,
        Func<CancellationTokenSource, IDisposable> hookCancel, UsageClient? injected)
    {
        const string usage = "clauderoo usage [name] [--json]";
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
                    if (json) JsonOut.Error(err, "Cancelled.");
                    else err.WriteLine("Cancelled.");
                    return 1;
                }
                rows.Add(ToRow(name, r, cache, err));
            }
        }

        if (json) JsonOut.Write(output, UsagePayload(rows));
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
                return new UsageRow(name, "notTracked", null, nt.Message);
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
    private static int AutoCmd(List<string> pos, bool json, TextWriter output, TextWriter err, ProfileManager pm,
        Func<CancellationTokenSource, IDisposable> hookCancel, UsageClient? injected, Func<DateTimeOffset> clock)
    {
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
                    foreach (var name in order)
                        if (Providers.Of(pm.Get(name)) == Providers.Token) // Get throws for an unknown profile
                            throw new ClauderooException($"'{name}' is a Claude-token profile. Claude-token profiles are never auto-switch targets.");
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
        // This read only says whether to fetch, and which profiles. Decisions use a fresh read after the fetch
        // (RunPass), so a default the user picks meanwhile is never overridden.
        var cfg = pm.Config;
        if (cfg.AutoSwitch is not { Enabled: true })
        {
            const string off = "Auto-switch is off.";
            if (json) JsonOut.Write(output, new { action = "none", from = cfg.DefaultProfile, to = (string?)null, reason = off });
            else output.WriteLine(off);
            return 2;
        }

        var client = injected ?? throw new InvalidOperationException("No usage client was given.");
        var cache = new UsageCache(pm.Paths);
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
                    if (json) JsonOut.Error(err, "Cancelled.");
                    else err.WriteLine("Cancelled.");
                    return 1;
                }
                // A failed fetch falls back to the cached reading; the engine ignores it once it is no longer fresh.
                if (ToRow(name, r, cache, err).Reading is { } reading) usage[name] = reading;
            }
        }

        var pass = AutoSwitchRunner.RunPass(pm, usage, clock());
        if (pass.ReadError is { } readError) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(readError);
        var from = pass.From;

        string action, reason;
        string? to = null;
        int code;
        switch (pass.Decision)
        {
            case Decision.SwitchTo sw:
                // With --json stdout carries only the payload, so the switch's sync notices go to stderr.
                if (pass.Report != null) Print(pass.Report, json ? err : output, err);
                (action, to, reason, code) = ("switched", sw.Profile, sw.Reason, 0);
                break;
            case Decision.NoTarget nt:
                (action, reason, code) = ("noTarget", nt.Reason, 3);
                break;
            case Decision.Recovered rec:
                (action, reason, code) = ("none", $"{rec.Profile} has room again.", 2);
                break;
            default:
                (action, reason, code) = ("none", pass.Settings is { Enabled: true } ? "Nothing to do." : "Auto-switch is off.", 2);
                break;
        }

        // {schemaVersion:1, action, from, to, reason}: JsonOut.Write adds schemaVersion first.
        if (json) JsonOut.Write(output, new { action, from, to, reason });
        else
        {
            output.WriteLine(reason);
            if (to != null) output.WriteLine($"New terminals now use {to}.");
            else if (action == "noTarget") output.WriteLine("No other profile has room.");
        }
        return code;
    }

    // The usage --json payload: {profiles:[{name, status, plan, fetchedAt, windows:[{name, percent, resetsAt}], message}]}.
    // JsonOut.Write adds schemaVersion first and writes the times as UTC "…Z".
    private static object UsagePayload(List<UsageRow> rows) => new
    {
        Profiles = rows.Select(r => new
        {
            r.Name,
            r.Status,
            r.Reading?.Plan,
            FetchedAt = r.Reading?.FetchedAt,
            Windows = (r.Reading?.Windows ?? []).Select(w => new { w.Name, w.Percent, w.ResetsAt }),
            // Additive: why a failed or unavailable row has no fresh reading (scrubbed of the token).
            Message = r.Status is "failed" or "unavailable" ? r.Note : null,
        }),
    };

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
