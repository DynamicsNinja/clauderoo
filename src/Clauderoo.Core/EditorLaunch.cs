using System.Text.RegularExpressions;

namespace Clauderoo.Core;

/// <summary>How to start an editor: the exe and the arguments before ours (VS Code's cli.js when run as Node).</summary>
public sealed record EditorCommand(string FileName, IReadOnlyList<string> PrefixArgs, bool RunAsNode);

/// <summary>
/// Opens VS Code (or Cursor, or another VS Code-based editor) with a profile's environment, so the Claude Code
/// extension in that window uses the profile. VS Code's <c>code.cmd</c> is never run through cmd: the exe and script
/// it starts are read from it and run directly, so a folder name can't be re-parsed by cmd.
/// </summary>
public static partial class EditorLaunch
{
    public const string VsCode = "code";
    public const string Cursor = "cursor";

    // code.cmd ends with: "%~dp0..\Code.exe" "%~dp0..\<commit>\resources\app\out\cli.js" %*
    [GeneratedRegex(@"""%~dp0(?<exe>[^""]+?\.exe)""\s+""%~dp0(?<js>[^""]+?\.js)""", RegexOptions.IgnoreCase)]
    private static partial Regex LauncherLine();

    /// <summary>Variables that would make the new editor attach to the editor (or Claude session) we were run from.</summary>
    public static bool Drops(string name) =>
        name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDE_CODE_ENTRYPOINT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDE_CODE_SSE_PORT", StringComparison.OrdinalIgnoreCase);

    public static EditorCommand Resolve(string editor) =>
        Resolve(editor, Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD");

    /// <summary>
    /// <paramref name="editor"/> is a name on PATH ("code", "cursor") or a path to the exe or its .cmd launcher.
    /// </summary>
    public static EditorCommand Resolve(string editor, string path, string pathExt)
    {
        var file = Find(editor, path, pathExt) ?? throw new ClauderooException(NotFound(editor));
        var ext = Path.GetExtension(file);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return new EditorCommand(file, [], false);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            return FromLauncher(file);
        throw new ClauderooException($"Can't start '{file}': give the editor's .exe or its .cmd launcher.");
    }

    private static string NotFound(string editor) => editor switch
    {
        VsCode => "VS Code ('code') was not found on PATH. Install VS Code with \"Add to PATH\" ticked, or give its path with --editor.",
        Cursor => "Cursor ('cursor') was not found on PATH. Install Cursor's shell command, or give its path with --editor.",
        _ => $"Editor '{editor}' was not found.",
    };

    private static string? Find(string editor, string path, string pathExt)
    {
        var exts = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> Candidates(string f) =>
            Path.HasExtension(f) ? [f] : exts.Select(e => f + e);
        if (editor.IndexOfAny(['\\', '/']) >= 0 || Path.IsPathRooted(editor))
            return Candidates(Path.GetFullPath(editor)).FirstOrDefault(File.Exists);
        foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var c in exts.Select(e => editor + e))
            {
                try
                {
                    var f = Path.Combine(d.Trim('"'), c);
                    if (File.Exists(f)) return f;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    // Does what the launcher script does (ELECTRON_RUN_AS_NODE=1 "<exe>" "<cli.js>" args), without cmd.
    private static EditorCommand FromLauncher(string cmdFile)
    {
        string text;
        try { text = File.ReadAllText(cmdFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ClauderooException($"Couldn't read {cmdFile}: {e.Message}");
        }
        var m = LauncherLine().Match(text);
        var dir = Path.GetDirectoryName(cmdFile)!;
        if (m.Success)
        {
            var exe = Path.GetFullPath(Path.Combine(dir, m.Groups["exe"].Value));
            var js = Path.GetFullPath(Path.Combine(dir, m.Groups["js"].Value));
            if (File.Exists(exe) && File.Exists(js)) return new EditorCommand(exe, [js], true);
        }
        throw new ClauderooException(
            $"Couldn't work out how to start the editor from {cmdFile}. Give the editor's .exe with --editor instead.");
    }

    /// <summary>
    /// The launch for <paramref name="profile"/>: its config folder and session marker, exactly as `run` sets them
    /// (the caller adds a token profile's token with WithToken). <paramref name="dir"/> null opens an empty window.
    /// </summary>
    public static LaunchSpec Spec(ClauderooConfig cfg, ClauderooPaths paths, string profile, string? dir, EditorCommand editor)
    {
        // InPlace checks the profile and gives the same config folder and CLAUDEROO_PROFILE as `run`.
        var run = Launcher.InPlace(cfg, paths, profile, dir ?? Environment.CurrentDirectory, [], claudePath: editor.FileName);
        var env = new Dictionary<string, string>(run.ExtraEnv!, StringComparer.Ordinal);
        if (editor.RunAsNode) env["ELECTRON_RUN_AS_NODE"] = "1";
        // A new window: an already open window would keep its own environment (see the README).
        List<string> args = [.. editor.PrefixArgs, "--new-window"];
        if (dir is not null) args.Add(dir);
        return new LaunchSpec(editor.FileName, args, run.WorkingDir, run.ConfigDirEnv, ExtraEnv: env, DropEnv: Drops);
    }
}
