using Clauderoo.Core;

namespace Clauderoo.Cli;

/// <summary>config (terminal and shell), wt-profiles and code: where sessions and editors open.</summary>
internal static class TerminalCommands
{
    public const string ConfigUsage = "Usage: clauderoo config [terminal <wt|conhost>] [shell <cmd|pwsh|powershell>] [--json]";
    public const string WtProfilesUsage = "Usage: clauderoo wt-profiles [--remove]";
    public const string CodeUsage = "Usage: clauderoo code [name] [dir] [--cursor | --editor <exe>]";

    /// <summary>Commands that change the profile list: an existing Windows Terminal fragment follows them.</summary>
    private static readonly string[] ProfileListCommands = ["add", "remove", "rename", "restore", "import"];

    public static int Config(List<string> pos, bool json, TextWriter output, ProfileManager pm)
    {
        string? terminal = null, shell = null;
        for (var i = 0; i < pos.Count; i += 2)
        {
            if (i + 1 >= pos.Count) throw new ClauderooException(ConfigUsage);
            switch (pos[i])
            {
                case "terminal": terminal = pos[i + 1]; break;
                case "shell": shell = pos[i + 1]; break;
                default: throw new ClauderooException(ConfigUsage);
            }
        }
        if (terminal != null || shell != null) pm.SetLaunchSettings(terminal, shell);

        var cfg = pm.Config;
        var t = TerminalChoice.TerminalOf(cfg);
        var s = TerminalChoice.ShellOf(cfg);
        if (json)
        {
            JsonOut.Write(output, new { Terminal = t, Shell = s });
            return 0;
        }
        output.WriteLine($"terminal: {t}  (new sessions open in {TerminalChoice.TerminalName(t)}"
                         + (t == TerminalChoice.Wt ? ", or a console window when it isn't installed)" : ")"));
        output.WriteLine($"shell:    {s}  ({TerminalChoice.ShellName(s)})");
        return 0;
    }

    public static int WtProfiles(List<string> pos, TextWriter output, ProfileManager pm)
    {
        var remove = pos.Remove("--remove");
        if (pos.Count > 0) throw new ClauderooException(WtProfilesUsage);
        var file = pm.WtFragmentFile ?? WtFragment.DefaultFile();
        if (remove)
        {
            output.WriteLine(WtFragment.Remove(file)
                ? "Removed the clauderoo profiles from Windows Terminal. Open a new Windows Terminal window to see the change."
                : "There were no clauderoo profiles in Windows Terminal.");
            return 0;
        }
        var n = WtFragment.Write(file, pm.Config, pm.Paths, pm.HelperExe);
        output.WriteLine($"Added {n} profile(s) to Windows Terminal ({file}).");
        output.WriteLine("Open a new Windows Terminal window: they are in the new-tab dropdown as \"Claude · <name>\". "
                         + "clauderoo keeps them up to date when you add, rename or remove profiles.");
        return 0;
    }

    public static int Code(List<string> pos, List<string> extra, TextWriter output, ProfileManager pm,
        Func<LaunchSpec, int> launch)
    {
        if (extra.Count > 0) throw new ClauderooException(CodeUsage);
        var editor = EditorLaunch.VsCode;
        if (pos.Remove("--cursor")) editor = EditorLaunch.Cursor;
        var at = pos.IndexOf("--editor");
        if (at >= 0)
        {
            if (at + 1 >= pos.Count || editor != EditorLaunch.VsCode) throw new ClauderooException(CodeUsage);
            editor = pos[at + 1];
            pos.RemoveRange(at, 2);
        }
        if (pos.Count > 2 || pos.Any(p => p.StartsWith("--", StringComparison.Ordinal))) throw new ClauderooException(CodeUsage);

        var cfg = pm.Config;
        var (profile, dir) = ProfileAndFolder(cfg, pos, output, CodeUsage);
        var cmd = EditorLaunch.Resolve(editor);
        // A token profile's token goes into the editor's environment only, as `run` does it.
        var spec = pm.WithToken(EditorLaunch.Spec(cfg, pm.Paths, profile, dir, cmd), profile);
        var app = editor == EditorLaunch.Cursor ? "Cursor" : editor == EditorLaunch.VsCode ? "VS Code" : Path.GetFileNameWithoutExtension(cmd.FileName);
        var running = IsRunning(cmd.FileName);
        launch(spec);
        output.WriteLine($"Opened {dir} in {app} with {profile}.");
        // A running editor builds a new window's environment on top of the one it was first started with: what this
        // profile sets wins, but what it leaves unset (home has no CLAUDE_CONFIG_DIR, only token profiles set a token)
        // can carry over from that first start.
        if (running)
            output.WriteLine($"{app} was already running. If that window signs in as another profile, quit {app} completely and run this again. " +
                             "A folder that's already open keeps the profile it was opened with.");
        return 0;
    }

    private static bool IsRunning(string exe)
    {
        try
        {
            var ps = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>The profile and folder as `start` picks them: a named profile, else the folder's mapping, else the default.</summary>
    private static (string Profile, string Dir) ProfileAndFolder(ClauderooConfig cfg, List<string> pos, TextWriter output, string usage)
    {
        string profile;
        string folderArg;
        if (pos.Count > 0 && cfg.Profiles.ContainsKey(pos[0]))
        {
            profile = pos[0];
            folderArg = pos.Count > 1 ? pos[1] : Environment.CurrentDirectory;
        }
        else
        {
            if (pos.Count == 2 || (pos.Count == 1 && !Directory.Exists(pos[0])))
                throw new ClauderooException($"Unknown profile '{pos[0]}'. Known: {string.Join(", ", cfg.Profiles.Keys)}.");
            folderArg = pos.Count == 1 ? pos[0] : Environment.CurrentDirectory;
            if (FolderMap.Resolve(cfg, folderArg) is { } h)
            {
                profile = h.Profile;
                output.WriteLine($"Using {profile} (mapped from {h.MappedDir})");
            }
            else profile = cfg.DefaultProfile
                ?? throw new ClauderooException("No profile given and no folder mapping or default applies. " + usage);
        }
        var dir = Path.GetFullPath(folderArg);
        if (!Directory.Exists(dir)) throw new ClauderooException($"Folder not found: {dir}");
        return (profile, dir);
    }

    /// <summary>After a command that changed the profile list, an existing Windows Terminal fragment follows it.</summary>
    public static void AfterCommand(string[] args, ProfileManager pm)
    {
        if (args.Length == 0 || !ProfileListCommands.Contains(args[0])) return;
        try { pm.RefreshWtFragment(pm.Config); }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
