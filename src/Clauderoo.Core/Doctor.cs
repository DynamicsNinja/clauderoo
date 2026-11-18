using System.Diagnostics;
using System.Text.Json;

namespace Clauderoo.Core;

/// <summary>Seams for <see cref="Doctor"/>. Anything left null uses the real machine.</summary>
public sealed class DoctorOptions
{
    /// <summary>claude's path on PATH, or null.</summary>
    public Func<string?>? FindClaude { get; set; }
    /// <summary>`claude --version`'s first line for a path, or null when it couldn't be read.</summary>
    public Func<string, CancellationToken, Task<string?>>? ClaudeVersion { get; set; }
    public Func<bool>? WtAvailable { get; set; }
    public Func<string?>? FindGcloud { get; set; }
    /// <summary>Where `clauderoo install` puts the program (default %LOCALAPPDATA%\clauderoo).</summary>
    public string? InstallDir { get; set; }
    /// <summary>The raw user PATH (default: HKCU\Environment).</summary>
    public Func<string>? UserPath { get; set; }
}

/// <summary>
/// `cr doctor`: a read-only checklist of the machine and clauderoo's own state. It never writes anything (no sync,
/// no config save) and never runs claude other than `claude --version`.
/// </summary>
public static class Doctor
{
    public const string McpServerName = "clauderoo";

    private sealed record Plan(string Key, string Subject, Func<CancellationToken, Task<CheckItem>> Run);

    /// <param name="progress">Every planned row as Pending first (stable keys), then each result as soon as it's known,
    /// from one loop, never concurrently.</param>
    /// <returns>The results in plan order.</returns>
    public static async Task<IReadOnlyList<CheckItem>> RunAsync(ClauderooPaths paths, IEnvStore env, DoctorOptions? options = null,
        CancellationToken ct = default, IProgress<CheckItem>? progress = null)
    {
        var o = options ?? new DoctorOptions();
        ClauderooConfig? cfg = null;
        string? configError = null;
        try { cfg = ConfigStore.Load(paths); }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException) { configError = e.Message; }

        var plan = new List<Plan>
        {
            new("config", "clauderoo settings", _ => Task.FromResult(ConfigItem(paths, cfg, configError))),
            new("claude", "Claude Code", c => ClaudeItem(o, c)),
            new("wt", "Windows Terminal", _ => Task.Run(() => WtItem(o), CancellationToken.None)),
        };
        if (cfg != null && cfg.Profiles.Values.Any(e => !e.Home && Providers.Of(e) == Providers.Vertex))
            plan.Add(new("gcloud", "gcloud", _ => Task.Run(() => GcloudItem(o), CancellationToken.None)));
        var installDir = o.InstallDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clauderoo");
        plan.Add(new("install", "clauderoo install folder", _ => Task.FromResult(InstallItem(installDir))));
        plan.Add(new("path", "clauderoo on PATH", _ => Task.Run(() => PathItem(o, installDir), CancellationToken.None)));
        if (cfg != null)
        {
            plan.Add(new("home", "home profile folder", _ => Task.FromResult(HomeItem(cfg))));
            plan.Add(new("mcp", "clauderoo MCP server", _ => Task.Run(() => McpItem(cfg), CancellationToken.None)));
            plan.Add(new("default", "default profile", _ => Task.FromResult(DefaultItem(cfg, paths, env))));
            foreach (var (name, entry) in cfg.Profiles)
                if (!entry.Home)
                {
                    var n = name;
                    plan.Add(new("links:" + n, $"{n}'s shared folders", _ => Task.Run(() => LinksItem(cfg, paths, n, entry), CancellationToken.None)));
                }
        }

        foreach (var p in plan) progress?.Report(new CheckItem(CheckStatus.Pending, p.Subject, null) { Key = p.Key });

        var rows = new CheckItem?[plan.Count];
        var tasks = plan.Select(p => p.Run(ct)).ToList();
        var waiting = new List<Task<CheckItem>>(tasks);
        while (waiting.Count > 0)
        {
            var done = await Task.WhenAny(waiting).ConfigureAwait(false);
            waiting.Remove(done);
            ct.ThrowIfCancellationRequested();
            var i = tasks.IndexOf(done);
            CheckItem item;
            try { item = await done.ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                item = new CheckItem(CheckStatus.Warn, $"Couldn't check {plan[i].Subject}", null, e.GetType().Name + ": " + e.Message);
            }
            rows[i] = item with { Key = plan[i].Key };
            progress?.Report(rows[i]!);
        }
        return rows.Select(r => r!).ToList();
    }

    private static CheckItem ConfigItem(ClauderooPaths paths, ClauderooConfig? cfg, string? error)
    {
        if (cfg != null) return new(CheckStatus.Ok, $"clauderoo is set up ({cfg.Profiles.Count} profile{(cfg.Profiles.Count == 1 ? "" : "s")})", null);
        if (error != null) return new(CheckStatus.Fail, "clauderoo's settings file can't be read", $"Fix or restore {paths.ConfigFile}.", error);
        return new(CheckStatus.Fail, "clauderoo isn't set up yet", "Run 'cr init'.");
    }

    private static async Task<CheckItem> ClaudeItem(DoctorOptions o, CancellationToken ct)
    {
        var path = await Task.Run(() => (o.FindClaude ?? FindClaudeOnPath)(), ct).ConfigureAwait(false);
        if (path == null) return new(CheckStatus.Fail, "Claude Code isn't on PATH", "Install Claude Code, then open a new terminal.");
        var version = await (o.ClaudeVersion ?? ReadClaudeVersion)(path, ct).ConfigureAwait(false);
        return version == null
            ? new(CheckStatus.Warn, "Claude Code is installed, but its version couldn't be read", null, path)
            : new(CheckStatus.Ok, $"Claude Code {version}", null, path);
    }

    private static CheckItem WtItem(DoctorOptions o) => (o.WtAvailable ?? Launcher.WtAvailable)()
        ? new(CheckStatus.Ok, "Windows Terminal installed", null)
        : new(CheckStatus.Warn, "Windows Terminal (wt.exe) not found",
            "--tab and the tray open sessions in a plain console window instead. Install it: winget install Microsoft.WindowsTerminal");

    private static CheckItem GcloudItem(DoctorOptions o) => (o.FindGcloud ?? (() => new Gcloud().Find()))() is { } g
        ? new(CheckStatus.Ok, "gcloud installed (for Vertex profiles)", null, g)
        : new(CheckStatus.Fail, "gcloud not found (Vertex profiles need it)", "Install it: winget install Google.CloudSDK, then open a new terminal.");

    private static CheckItem InstallItem(string installDir) =>
        File.Exists(Path.Combine(installDir, Installer.ShortAlias)) && File.Exists(Path.Combine(installDir, "clauderoo.exe"))
            ? new(CheckStatus.Ok, "clauderoo installed", null, installDir)
            : new(CheckStatus.Warn, $"clauderoo isn't installed in {installDir}",
                "Run 'clauderoo install' from the release or publish folder.");

    private static CheckItem PathItem(DoctorOptions o, string installDir)
    {
        string raw;
        try { raw = (o.UserPath ?? (() => new RegistryUserPath().Read()))(); }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(CheckStatus.Warn, "Couldn't read your PATH", null, e.Message);
        }
        // AppendToPath returns null when the folder is already an entry (raw or expanded).
        return Installer.AppendToPath(raw, installDir) == null
            ? new(CheckStatus.Ok, "clauderoo's folder is on your PATH", null)
            : new(CheckStatus.Warn, "clauderoo's folder isn't on your PATH", "Run 'clauderoo install' again, then open a new terminal.");
    }

    private static CheckItem HomeItem(ClauderooConfig cfg) => Directory.Exists(cfg.HomeDir)
        ? new(CheckStatus.Ok, "Home profile folder exists", null, cfg.HomeDir)
        : new(CheckStatus.Fail, $"The home profile folder is missing ({cfg.HomeDir})",
            "Restore it, or run Claude once without CLAUDE_CONFIG_DIR to recreate it.");

    // Read only: ~/.claude.json (or the home profile's) is never written.
    private static CheckItem McpItem(ClauderooConfig cfg)
    {
        try
        {
            if (!File.Exists(cfg.HomeJson))
                return new(CheckStatus.Warn, "clauderoo's MCP server isn't registered", "Run 'cr mcp install' and follow it.");
            using var fs = new FileStream(cfg.HomeJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object
                || !servers.TryGetProperty(McpServerName, out var server))
                return new(CheckStatus.Warn, "clauderoo's MCP server isn't registered",
                    "Optional: run 'cr mcp install' so Claude can list profiles, read usage and delegate.");
            if (server.ValueKind == JsonValueKind.Object && server.TryGetProperty("command", out var cmd)
                && cmd.ValueKind == JsonValueKind.String && cmd.GetString() is { Length: > 0 } exe
                && Path.IsPathFullyQualified(exe) && !File.Exists(exe))
                return new(CheckStatus.Warn, "clauderoo's MCP server points to a missing program",
                    "Remove it with 'claude mcp remove clauderoo --scope user', then run 'cr mcp install'.", exe);
            return new(CheckStatus.Ok, "clauderoo's MCP server is registered", null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(CheckStatus.Warn, "Couldn't read the home profile's .claude.json", null, e.Message);
        }
    }

    private static CheckItem DefaultItem(ClauderooConfig cfg, ClauderooPaths paths, IEnvStore env)
    {
        var def = cfg.DefaultProfile;
        if (def == null) return new(CheckStatus.Warn, "No default profile", "Pick one with 'cr default <name>'.");
        if (!cfg.Profiles.TryGetValue(def, out var entry))
            return new(CheckStatus.Fail, $"The default profile '{def}' doesn't exist", "Pick another with 'cr default <name>'.");
        var user = env.GetUser("CLAUDE_CONFIG_DIR");
        var expected = entry.Home ? null : paths.ProfileDir(def);
        static string N(string p) => p.TrimEnd('\\', '/').ToUpperInvariant();
        var matches = expected == null ? string.IsNullOrEmpty(user) : !string.IsNullOrEmpty(user) && N(user) == N(expected);
        if (!matches)
            return new(CheckStatus.Warn, $"New terminals don't use the default profile ({def})",
                $"Run 'cr default {def}' to set CLAUDE_CONFIG_DIR again.", string.IsNullOrEmpty(user) ? "CLAUDE_CONFIG_DIR is not set" : "CLAUDE_CONFIG_DIR=" + user);
        if (!entry.Home && !Directory.Exists(expected))
            return new(CheckStatus.Fail, $"The default profile's folder is missing ({expected})", "Run 'cr status' to recreate it.");
        return new(CheckStatus.Ok, $"Default profile is {def}", null);
    }

    // Each shared folder the profile doesn't own should be a junction to the home folder that resolves.
    private static CheckItem LinksItem(ClauderooConfig cfg, ClauderooPaths paths, string name, ProfileEntry entry)
    {
        var dir = paths.ProfileDir(name);
        if (!Directory.Exists(dir))
            return new(CheckStatus.Fail, $"{name}'s folder is missing", "Run 'cr status' to recreate it.", dir);
        var dangling = new List<string>();
        var wrong = new List<string>();
        foreach (var item in SharedItems.Dirs)
        {
            if (entry.Overrides.Contains(item)) continue;
            var link = Path.Combine(dir, item);
            string? target;
            try { target = Junction.GetTarget(link); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { target = null; }
            if (target == null) continue; // not a link (missing, or the sync reports a real folder itself)
            var full = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(dir, target));
            if (!Directory.Exists(full)) dangling.Add($"{item} -> {full}");
            else if (!string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(Path.Combine(cfg.HomeDir, item)),
                         StringComparison.OrdinalIgnoreCase))
                wrong.Add($"{item} -> {full}");
        }
        if (dangling.Count > 0)
            return new(CheckStatus.Fail, $"{name} has broken shared-folder links ({string.Join(", ", dangling.Select(d => d.Split(' ')[0]))})",
                "Restore the home folders they point to, or run 'cr status' to relink them.", string.Join("; ", dangling));
        if (wrong.Count > 0)
            return new(CheckStatus.Warn, $"{name}'s shared folders point outside the home profile ({string.Join(", ", wrong.Select(d => d.Split(' ')[0]))})",
                "Run 'cr status' to relink them.", string.Join("; ", wrong));
        return new(CheckStatus.Ok, $"{name}'s shared folders resolve", null);
    }

    private static string? FindClaudeOnPath()
    {
        try { return Launcher.ResolveClaude(); }
        catch (ClauderooException) { return null; }
    }

    /// <summary>Runs `claude --version` (15 s at most) and returns its first line.</summary>
    public static async Task<string?> ReadClaudeVersion(string claudePath, CancellationToken ct)
    {
        var isCmd = claudePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || claudePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(isCmd ? "cmd.exe" : claudePath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        if (isCmd) psi.Arguments = ProcessClaudeRunner.BuildCmdLine(claudePath, ["--version"]);
        else psi.ArgumentList.Add("--version");
        psi.Environment["DISABLE_AUTOUPDATER"] = "1";
        psi.Environment.Remove(ProfileManager.TokenEnvVar);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.StandardInput.Close();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            _ = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                var text = await outTask.ConfigureAwait(false);
                var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                return p.ExitCode == 0 && !string.IsNullOrEmpty(line) ? line : null;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
