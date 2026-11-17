using System.Collections;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Clauderoo.Core;

/// <summary>
/// <see cref="Pending"/> and <see cref="Running"/> only appear in progress reports, never in a finished checklist:
/// Pending is a planned row not decided yet, Running a model probe that has started.
/// </summary>
public enum CheckStatus { Ok, Warn, Fail, Skipped, Pending, Running }

/// <param name="Detail">Raw technical text (e.g. the last line of an error); shown on demand, never as the label.</param>
public record CheckItem(CheckStatus Status, string Label, string? Hint, string? Detail = null)
{
    /// <summary>Stable per check ("gcloud", "signin", "gac", "iam", "leftovers", "claude", "model:sonnet", …), so a
    /// progress report can replace the row it updates.</summary>
    public string? Key { get; init; }
}

/// <summary>One checklist line, shared by `clauderoo check` and the Profiles window.</summary>
public static class CheckItemFormatter
{
    public static string Format(CheckItem item) => item.Status switch
    {
        CheckStatus.Pending => "⋯ checking " + Subject(item.Label) + "…",
        CheckStatus.Running => "⋯ testing " + Subject(item.Label) + "…",
        _ => Final(item),
    };

    // "The default model" reads "testing the default model…"; model ids are left as they are.
    private static string Subject(string label) =>
        label.StartsWith("The ", StringComparison.Ordinal) ? "the " + label[4..] : label;

    private static string Final(CheckItem item) =>
        item.Status switch
        {
            CheckStatus.Ok => "✓ ",
            CheckStatus.Warn => "⚠ ",
            CheckStatus.Fail => "✗ ",
            _ => "– ",
        } + item.Label + (string.IsNullOrEmpty(item.Hint) ? "" : "  → " + item.Hint)
          + (string.IsNullOrEmpty(item.Detail) ? "" : "  (" + item.Detail + ")");
}

public interface IMachineEnv
{
    IReadOnlyDictionary<string, string> User();
    IReadOnlyDictionary<string, string> Machine();
    string? Process(string name);
}

public sealed class WindowsMachineEnv : IMachineEnv
{
    public IReadOnlyDictionary<string, string> User() => Read(EnvironmentVariableTarget.User);
    public IReadOnlyDictionary<string, string> Machine() => Read(EnvironmentVariableTarget.Machine);
    public string? Process(string name) => Environment.GetEnvironmentVariable(name);

    private static Dictionary<string, string> Read(EnvironmentVariableTarget target)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables(target))
            d[(string)e.Key] = e.Value as string ?? "";
        return d;
    }
}

public interface IIamTester
{
    Task<(bool? allowed, string? error)> HasPermissionAsync(string project, string token, string permission, CancellationToken ct);
}

public sealed class HttpIamTester : IIamTester
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient Http;

    public HttpIamTester() { Http = SharedHttp; }
    internal HttpIamTester(HttpMessageHandler handler) { Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }; }

    public async Task<(bool? allowed, string? error)> HasPermissionAsync(string project, string token, string permission, CancellationToken ct)
    {
        try
        {
            var url = $"https://cloudresourcemanager.googleapis.com/v1/projects/{Uri.EscapeDataString(project)}:testIamPermissions";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { permissions = new[] { permission } }),
                    Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            // 403: the account may not even test permissions on the project, so it lacks the role.
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) return (false, null);
            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var has = doc.RootElement.TryGetProperty("permissions", out var arr) && arr.ValueKind == JsonValueKind.Array
                && arr.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == permission);
            return (has, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var msg = e.Message;
            if (token.Length > 0) msg = msg.Replace(token, "***");
            return (null, e.GetType().Name + ": " + msg);
        }
    }
}

public interface IClaudeRunner
{
    string? Find();
    Task<(int exit, string stdout, string stderr)> RunAsync(string claudePath, IReadOnlyList<string> args,
        string configDir, TimeSpan timeout, CancellationToken ct);
}

public sealed class ProcessClaudeRunner : IClaudeRunner
{
    public string? Find()
    {
        try { return Launcher.ResolveClaude(); }
        catch (ClauderooException) { return null; }
    }

    internal static string BuildCmdLine(string exe, IReadOnlyList<string> args)
    {
        static string Q(string a) => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c is '&' or '|' or '<' or '>' or '^' or '(' or ')')
            ? "\"" + a + "\"" : a;
        var inner = string.Join(" ", new[] { Q(exe) }.Concat(args.Select(Q)));
        return "/d /s /c \"" + inner + "\"";
    }

    /// <summary>
    /// The probe runs in a fresh, empty temp folder (so claude picks up no project settings, CLAUDE.md or .mcp.json and
    /// writes nothing into the caller's folder) with auto-update off. The folder is not created here.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string claudePath, IReadOnlyList<string> args, string configDir,
        string? tempRoot = null)
    {
        var isCmd = claudePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || claudePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(isCmd ? "cmd.exe" : claudePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.Combine(tempRoot ?? Path.GetTempPath(), "clauderoo-probe-" + Guid.NewGuid().ToString("N")),
        };
        if (isCmd) psi.Arguments = BuildCmdLine(claudePath, args);
        else foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        psi.Environment["DISABLE_AUTOUPDATER"] = "1";
        return psi;
    }

    public async Task<(int exit, string stdout, string stderr)> RunAsync(string claudePath, IReadOnlyList<string> args,
        string configDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = BuildStartInfo(claudePath, args, configDir);
        var workDir = psi.WorkingDirectory;
        try
        {
            Directory.CreateDirectory(workDir);
            return await RunInAsync(psi, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            // Our own fresh folder (no junctions): it holds only what claude wrote. Best effort.
            try { Directory.Delete(workDir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<(int exit, string stdout, string stderr)> RunInAsync(ProcessStartInfo psi, TimeSpan timeout,
        CancellationToken ct)
    {
        var timedOut = $"timed out after {(int)timeout.TotalSeconds} s";

        Process proc;
        try { proc = Process.Start(psi) ?? throw new ClauderooException("Couldn't start claude."); }
        catch (System.ComponentModel.Win32Exception e) { return (-1, "", "Couldn't start claude: " + e.Message); }
        using var _ = proc;
        proc.StandardInput.Close();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var o = await outTask.WaitAsync(cts.Token).ConfigureAwait(false);
            var e = await errTask.WaitAsync(cts.Token).ConfigureAwait(false);
            return (proc.ExitCode, o, e);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return (-1, "", timedOut);
        }
        finally
        {
            // Timeout or cancel (e.g. Ctrl+C in `cr check`): kill the whole tree (cmd.exe, node, ...).
            try
            {
                if (!proc.HasExited || !outTask.IsCompleted || !errTask.IsCompleted)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000); // so it releases the work folder before we delete it
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}

public partial class ProfileManager
{
    // Only names that change Claude Code's provider or auth (not e.g. CLAUDE_CODE_GIT_BASH_PATH).
    private static readonly string[] LeftoverPrefixes = ["ANTHROPIC_", "CLAUDE_CODE_USE_", "CLOUD_ML_", "VERTEX_REGION_"];
    private static readonly System.Text.RegularExpressions.Regex SkipAuthVar = new("^CLAUDE_CODE_SKIP_.*_AUTH$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsLeftoverProviderVar(string name) =>
        !name.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase)
        && (LeftoverPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || SkipAuthVar.IsMatch(name));
    private const string Gac = "GOOGLE_APPLICATION_CREDENTIALS";

    private IMachineEnv machineEnv = new WindowsMachineEnv();
    private IIamTester iam = new HttpIamTester();
    private IClaudeRunner claudeRunner = new ProcessClaudeRunner();

    public ProfileManager(ClauderooPaths paths, IEnvStore env, Func<DateTime> now, ISecretStore secrets,
        IGcloud gcloud, IMachineEnv machineEnv, IIamTester iam, IClaudeRunner claude)
        : this(paths, env, now, secrets, gcloud)
    {
        this.machineEnv = machineEnv;
        this.iam = iam;
        claudeRunner = claude;
    }

    /// <param name="progress">
    /// First gets every planned row as <see cref="CheckStatus.Pending"/> (each with a stable <see cref="CheckItem.Key"/>),
    /// then each result as soon as it is decided, under the same key. The model probes are reported
    /// <see cref="CheckStatus.Running"/> together when they start, and their results in the order the models answer.
    /// Every planned row gets a final item (skipped rows too) unless the check is cancelled.
    /// Reports are made from the checking code itself, never concurrently.
    /// </param>
    /// <returns>The final items in config order (Sonnet, Opus, Haiku for the models).</returns>
    public async Task<IReadOnlyList<CheckItem>> CheckSetupAsync(string name, CancellationToken ct = default,
        IProgress<CheckItem>? progress = null)
    {
        var cfg = Config;
        var entry = NonHome(cfg, name);
        if (Providers.Of(entry) != Providers.Vertex || entry.Vertex == null)
            throw new ClauderooException($"Profile '{name}' doesn't use Vertex AI.");
        var v = entry.Vertex;
        var dir = paths.ProfileDir(name);
        var items = new ReportingList(progress);

        // (key, model id or null for the default model, what the probe is called while it runs)
        var models = new[] { ("sonnet", v.SonnetModel), ("opus", v.OpusModel), ("haiku", v.HaikuModel) }
            .Where(m => !string.IsNullOrWhiteSpace(m.Item2))
            .Select(m => (Key: "model:" + m.Item1, Id: (string?)m.Item2!, Subject: m.Item2!))
            .ToList();
        if (models.Count == 0) models.Add(("model:default", null, "The default model"));
        string ModelLabel((string Key, string? Id, string Subject) m) => m.Id == null ? "Default model answers" : $"{m.Id} answers";
        var iamLabel = $"Vertex AI permission on {v.Project}";
        const string GcloudLabel = "gcloud installed", SignInLabel = "Google sign-in for Claude",
            LeftoverLabel = "No leftover provider variables", ClaudeLabel = "Claude Code installed";

        // The plan: every row the check will fill in, so a window can show them all straight away.
        foreach (var (key, label) in new[]
                 {
                     ("gcloud", GcloudLabel), ("signin", SignInLabel), ("gac", Gac), ("iam", iamLabel),
                     ("leftovers", "Leftover provider variables"), ("claude", ClaudeLabel),
                 }.Concat(models.Select(m => (m.Key, m.Subject))))
            progress?.Report(new CheckItem(CheckStatus.Pending, label, null) { Key = key });

        void SkipModels()
        {
            foreach (var m in models) items.Add(new(CheckStatus.Skipped, ModelLabel(m), null) { Key = m.Key });
        }

        if (gcloud.Find() == null)
        {
            items.Add(new(CheckStatus.Fail, GcloudLabel, "Install it: winget install Google.CloudSDK, then open a new terminal.") { Key = "gcloud" });
            foreach (var (key, l) in new[] { ("signin", SignInLabel), ("gac", Gac), ("iam", iamLabel), ("leftovers", LeftoverLabel), ("claude", ClaudeLabel) })
                items.Add(new(CheckStatus.Skipped, l, null) { Key = key });
            SkipModels();
            return items;
        }
        items.Add(new(CheckStatus.Ok, GcloudLabel, null) { Key = "gcloud" });

        ct.ThrowIfCancellationRequested();
        string? token = null;
        (int exit, string stdout, string stderr) gr;
        try
        {
            gr = await Task.Run(() => gcloud.Run("auth", "application-default", "print-access-token"), ct)
                .WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { gr = (-1, "", "gcloud timed out"); }
        var (exit, stdout, stderr) = gr;
        if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            token = stdout.Trim();
            items.Add(new(CheckStatus.Ok, SignInLabel, null) { Key = "signin" });
        }
        else
        {
            var line = Scrub(LineOf(stderr, first: true), stdout.Trim());
            items.Add(new(CheckStatus.Fail, SignInLabel,
                "Run: gcloud auth application-default login (tick every box on the consent page).",
                line.Length > 0 ? line : null) { Key = "signin" });
        }

        items.Add(CheckGac() with { Key = "gac" });

        if (token == null)
            items.Add(new(CheckStatus.Skipped, iamLabel, null) { Key = "iam" });
        else
        {
            var (allowed, error) = await iam.HasPermissionAsync(v.Project, token, "aiplatform.endpoints.predict", ct).ConfigureAwait(false);
            if (allowed == true) items.Add(new(CheckStatus.Ok, iamLabel, null) { Key = "iam" });
            else if (allowed == false)
                items.Add(new(CheckStatus.Fail, iamLabel,
                    $"Ask your GCP admin for Vertex AI User (roles/aiplatform.user) on {v.Project}.") { Key = "iam" });
            else
            {
                var raw = Scrub(error ?? "", token);
                items.Add(new(CheckStatus.Warn, "Couldn't check the Vertex AI permission", null,
                    raw.Length > 0 ? raw : null) { Key = "iam" });
            }
        }

        var leftovers = new List<string>();
        foreach (var (scope, vars) in new[] { ("User", machineEnv.User()), ("Machine", machineEnv.Machine()) })
            foreach (var k in vars.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                if (IsLeftoverProviderVar(k))
                    leftovers.Add($"{k} ({scope})");
        items.Add((leftovers.Count == 0
            ? new CheckItem(CheckStatus.Ok, LeftoverLabel, null)
            : new CheckItem(CheckStatus.Warn, $"Leftover variables: {string.Join(", ", leftovers)}",
                "Remove them in Windows settings › Environment variables, then restart terminals.")) with { Key = "leftovers" });

        var claude = claudeRunner.Find();
        if (claude == null)
        {
            items.Add(new(CheckStatus.Fail, ClaudeLabel, "Install Claude Code, then open a new terminal.") { Key = "claude" });
            SkipModels();
            return items;
        }
        items.Add(new(CheckStatus.Ok, ClaudeLabel, null) { Key = "claude" });
        if (token == null)
        {
            SkipModels();
            return items;
        }

        ct.ThrowIfCancellationRequested();
        foreach (var m in models)
            progress?.Report(new CheckItem(CheckStatus.Running, m.Subject, null) { Key = m.Key });
        // Each probe is a full Claude Code start-up (~10 s), so all models are tested at the same time.
        // No MCP servers from the profile and no saved session: the probes leave nothing behind.
        var probes = models.Select(m =>
        {
            var args = new List<string> { "-p", "Reply with exactly: OK", "--strict-mcp-config", "--no-session-persistence" };
            if (m.Id != null) { args.Add("--model"); args.Add(m.Id); }
            return claudeRunner.RunAsync(claude, args, dir, TimeSpan.FromSeconds(60), ct);
        }).ToList();

        // Each result is reported as soon as its model answers; the returned list keeps the configured order.
        var results = new CheckItem[models.Count];
        var waiting = probes.ToList();
        while (waiting.Count > 0)
        {
            var done = await Task.WhenAny(waiting).ConfigureAwait(false);
            waiting.Remove(done);
            ct.ThrowIfCancellationRequested(); // Ctrl+C / window closed: report nothing more (the runner kills every probe)
            var i = probes.IndexOf(done);
            var m = models[i];
            var (mx, mo, me) = await done.ConfigureAwait(false);
            if (mx == 0 && mo.Contains("OK"))
                results[i] = new(CheckStatus.Ok, ModelLabel(m), null) { Key = m.Key };
            else
            {
                var last = Scrub(LineOf(mo + "\n" + me, first: false), token);
                if (last.Length > 200) last = last[..200];
                results[i] = new(CheckStatus.Fail, ModelFailLabel(m.Id ?? "The default model", v.Region, mx, last),
                    $"Check the model is enabled in Model Garden and served in region {v.Region}.",
                    last.Length > 0 ? last : null) { Key = m.Key };
            }
            progress?.Report(results[i]);
        }
        items.AddRange(results); // already reported one by one, as each model answered
        return items;
    }

    // A plain label for a failed model probe; the raw error goes to CheckItem.Detail.
    internal static string ModelFailLabel(string model, string region, int exit, string last)
    {
        if (exit == -1 && last.StartsWith("timed out", StringComparison.OrdinalIgnoreCase))
            return $"{model} didn't answer within 60 seconds";
        if (last.Contains("404", StringComparison.Ordinal) || last.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || last.Contains("not available", StringComparison.OrdinalIgnoreCase))
            return $"{model} isn't available in {region} (or your project can't use it)";
        return $"{model} didn't answer";
    }

    private CheckItem CheckGac()
    {
        // User, then Machine, then a value only this process has (set in the terminal that started us).
        string? path = null, scope = null;
        if (TryGet(machineEnv.User(), Gac, out var u)) { path = u; scope = "User"; }
        else if (TryGet(machineEnv.Machine(), Gac, out var m)) { path = m; scope = "Machine"; }
        else
        {
            var p = machineEnv.Process(Gac);
            if (!string.IsNullOrWhiteSpace(p)) { path = p; scope = "Process"; }
        }
        if (path == null) return new(CheckStatus.Ok, "GOOGLE_APPLICATION_CREDENTIALS not set", null);
        return File.Exists(path)
            ? new(CheckStatus.Warn, $"GOOGLE_APPLICATION_CREDENTIALS is set ({scope}) and overrides your gcloud sign-in",
                "Remove it unless you use a service account on purpose.")
            : new(CheckStatus.Fail, $"GOOGLE_APPLICATION_CREDENTIALS points to a missing file ({path}, {scope})",
                "Delete the variable, then close all terminals.");
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> d, string key, out string value)
    {
        foreach (var kv in d)
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
            { value = kv.Value; return true; }
        value = "";
        return false;
    }

    // Collects the checklist and reports each item to the caller's progress as it is added.
    private sealed class ReportingList(IProgress<CheckItem>? progress) : List<CheckItem>
    {
        public new void Add(CheckItem item)
        {
            base.Add(item);
            progress?.Report(item);
        }
    }

    private static string Scrub(string s, string? token) =>
        string.IsNullOrEmpty(token) ? s : s.Replace(token, "***");

    private static string LineOf(string text, bool first)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : first ? lines[0] : lines[^1];
    }
}
