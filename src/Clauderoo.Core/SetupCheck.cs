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
        // As in Launcher.StartInfo: an inherited token would sign the probe in as some other account.
        psi.Environment.Remove(ProfileManager.TokenEnvVar);
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
    /// then each result as soon as it is decided, under the same key. Everything runs at once: the instant checks are
    /// reported straight away, the model probes are reported <see cref="CheckStatus.Running"/> together when they start
    /// (before the sign-in token is known), and the sign-in, IAM and model results follow in the order they finish
    /// (a model result never before the sign-in result). Every planned row gets exactly one final item (skipped rows
    /// too) unless the check is cancelled. Reports come from one loop, never concurrently.
    /// </param>
    /// <returns>The final items in plan order (gcloud, sign-in, GAC, IAM, leftovers, Claude, then the models in config
    /// order: Sonnet, Opus, Haiku).</returns>
    public async Task<IReadOnlyList<CheckItem>> CheckSetupAsync(string name, CancellationToken ct = default,
        IProgress<CheckItem>? progress = null)
    {
        var cfg = Config;
        var entry = NonHome(cfg, name);
        if (Providers.Of(entry) != Providers.Vertex || entry.Vertex == null)
            throw new ClauderooException($"Profile '{name}' doesn't use Vertex AI.");
        var v = entry.Vertex;
        var dir = paths.ProfileDir(name);

        // (key, model id or null for the default model, what the probe is called while it runs)
        var models = new[] { ("sonnet", v.SonnetModel), ("opus", v.OpusModel), ("haiku", v.HaikuModel) }
            .Where(m => !string.IsNullOrWhiteSpace(m.Item2))
            .Select(m => (Key: "model:" + m.Item1, Id: m.Item2, Subject: m.Item2!))
            .ToList<(string Key, string? Id, string Subject)>();
        if (models.Count == 0) models.Add(("model:default", null, "The default model"));
        string ModelLabel((string Key, string? Id, string Subject) m) => m.Id == null ? "Default model answers" : $"{m.Id} answers";
        var iamLabel = $"Vertex AI permission on {v.Project}";
        const string GcloudLabel = "gcloud installed", SignInLabel = "Google sign-in for Claude",
            LeftoverLabel = "No leftover provider variables", ClaudeLabel = "Claude Code installed";
        const int GcloudRow = 0, SignInRow = 1, GacRow = 2, IamRow = 3, LeftoverRow = 4, ClaudeRow = 5, FirstModelRow = 6;

        // The plan: every row the check will fill in, so a window can show them all straight away.
        foreach (var (key, label) in new[]
                 {
                     ("gcloud", GcloudLabel), ("signin", SignInLabel), ("gac", Gac), ("iam", iamLabel),
                     ("leftovers", "Leftover provider variables"), ("claude", ClaudeLabel),
                 }.Concat(models.Select(m => (m.Key, m.Subject))))
            progress?.Report(new CheckItem(CheckStatus.Pending, label, null) { Key = key });

        // One slot per planned row, so the returned list keeps the plan order whatever order the results come in.
        var rows = new CheckItem?[FirstModelRow + models.Count];
        void Put(int row, CheckItem item)
        {
            rows[row] = item;
            progress?.Report(item);
        }
        IReadOnlyList<CheckItem> Result() => rows.Select(r => r!).ToList();
        void SkipModels()
        {
            for (var i = 0; i < models.Count; i++)
                Put(FirstModelRow + i, new(CheckStatus.Skipped, ModelLabel(models[i]), null) { Key = models[i].Key });
        }

        if (gcloud.Find() == null)
        {
            Put(GcloudRow, new(CheckStatus.Fail, GcloudLabel, "Install it: winget install Google.CloudSDK, then open a new terminal.") { Key = "gcloud" });
            foreach (var (row, key, l) in new[] { (SignInRow, "signin", SignInLabel), (GacRow, "gac", Gac), (IamRow, "iam", iamLabel),
                         (LeftoverRow, "leftovers", LeftoverLabel), (ClaudeRow, "claude", ClaudeLabel) })
                Put(row, new(CheckStatus.Skipped, l, null) { Key = key });
            SkipModels();
            return Result();
        }
        Put(GcloudRow, new(CheckStatus.Ok, GcloudLabel, null) { Key = "gcloud" });

        ct.ThrowIfCancellationRequested();
        // The probes get their own token, so a failed sign-in can stop them without cancelling the whole check.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probes = new List<Task<(int exit, string stdout, string stderr)>>();
        var waiting = new List<Task>();
        Task<(int exit, string stdout, string stderr)>? signIn = null;
        string? token = null;
        bool signInKnown = false, signInFailed = false;
        Task<(bool? allowed, string? error)>? iamTask = null;
        var held = new List<int>(); // probes that finished before the token was known (their output is scrubbed with it)
        try
        {
            // The sign-in token (a few seconds of gcloud) starts first; everything else runs while it is fetched.
            signIn = SignInAsync();
            waiting.Add(signIn);

            Put(GacRow, CheckGac() with { Key = "gac" });

            var leftovers = new List<string>();
            foreach (var (scope, vars) in new[] { ("User", machineEnv.User()), ("Machine", machineEnv.Machine()) })
                foreach (var k in vars.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    if (IsLeftoverProviderVar(k))
                        leftovers.Add($"{k} ({scope})");
            Put(LeftoverRow, (leftovers.Count == 0
                ? new CheckItem(CheckStatus.Ok, LeftoverLabel, null)
                : new CheckItem(CheckStatus.Warn, $"Leftover variables: {string.Join(", ", leftovers)}",
                    "Remove them in Windows settings › Environment variables, then restart terminals.")) with { Key = "leftovers" });

            var claude = claudeRunner.Find();
            if (claude == null)
            {
                Put(ClaudeRow, new(CheckStatus.Fail, ClaudeLabel, "Install Claude Code, then open a new terminal.") { Key = "claude" });
                SkipModels();
            }
            else
            {
                Put(ClaudeRow, new(CheckStatus.Ok, ClaudeLabel, null) { Key = "claude" });
                ct.ThrowIfCancellationRequested();
                foreach (var m in models)
                    progress?.Report(new CheckItem(CheckStatus.Running, m.Subject, null) { Key = m.Key });
                // Each probe is a full Claude Code start-up (~10 s), so all models are tested at the same time, without
                // waiting for the token. No MCP servers from the profile and no saved session: the probes leave nothing behind.
                foreach (var m in models)
                {
                    var args = new List<string> { "-p", "Reply with exactly: OK", "--strict-mcp-config", "--no-session-persistence" };
                    if (m.Id != null) { args.Add("--model"); args.Add(m.Id); }
                    var probe = claudeRunner.RunAsync(claude, args, dir, TimeSpan.FromSeconds(60), probeCts.Token);
                    probes.Add(probe);
                    waiting.Add(probe);
                }
            }

            // The one reporting loop: each result is reported as soon as it is known, never two at a time.
            while (waiting.Count > 0)
            {
                var done = await Task.WhenAny(waiting).ConfigureAwait(false);
                waiting.Remove(done);
                ct.ThrowIfCancellationRequested(); // Ctrl+C / window closed: report nothing more (the runner kills every probe)

                if (done == signIn)
                {
                    var (exit, stdout, stderr) = await signIn.ConfigureAwait(false);
                    signInKnown = true;
                    if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
                    {
                        token = stdout.Trim();
                        Put(SignInRow, new(CheckStatus.Ok, SignInLabel, null) { Key = "signin" });
                        iamTask = iam.HasPermissionAsync(v.Project, token, "aiplatform.endpoints.predict", ct);
                        waiting.Add(iamTask);
                        foreach (var i in held) Put(FirstModelRow + i, ModelResult(i));
                        held.Clear();
                    }
                    else
                    {
                        var line = Scrub(LineOf(stderr, first: true), stdout.Trim());
                        Put(SignInRow, new(CheckStatus.Fail, SignInLabel,
                            "Run: gcloud auth application-default login (tick every box on the consent page).",
                            line.Length > 0 ? line : null) { Key = "signin" });
                        Put(IamRow, new(CheckStatus.Skipped, iamLabel, null) { Key = "iam" });
                        signInFailed = true;
                        if (probes.Count > 0)
                        {
                            // Without a sign-in no model can answer: stop the probes and skip every model row. A probe
                            // that already finished (held) is skipped too, since its result means nothing without a
                            // sign-in. The still-running ones stay in `waiting`, so we return only once they have stopped.
                            probeCts.Cancel();
                            held.Clear();
                            SkipModels();
                        }
                    }
                }
                else if (done == iamTask)
                    Put(IamRow, IamResult(await iamTask.ConfigureAwait(false), token!));
                else
                {
                    var i = probes.IndexOf((Task<(int, string, string)>)done);
                    if (signInFailed)
                    {
                        // Already reported skipped; the runner rethrows our cancellation, which is expected here.
                        try { await done.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !ct.IsCancellationRequested) { }
                    }
                    else if (!signInKnown)
                    {
                        // A probe error (not an answer) surfaces at once, as before; only answers wait for the token.
                        if (done.IsFaulted) await done.ConfigureAwait(false);
                        held.Add(i);
                    }
                    else Put(FirstModelRow + i, ModelResult(i));
                }
            }
        }
        finally
        {
            // Leaving early (cancelled or an error): stop any probe still running before its token source goes away,
            // and observe whatever is left (the sign-in included) so no task error goes unobserved.
            if (waiting.Count > 0)
            {
                probeCts.Cancel();
                foreach (var t in waiting)
                    _ = t.ContinueWith(x => _ = x.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return Result();

        async Task<(int exit, string stdout, string stderr)> SignInAsync()
        {
            try
            {
                return await Task.Run(() => gcloud.Run("auth", "application-default", "print-access-token"), ct)
                    .WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            }
            catch (TimeoutException) { return (-1, "", "gcloud timed out"); }
        }

        CheckItem IamResult((bool? allowed, string? error) r, string tok)
        {
            if (r.allowed == true) return new(CheckStatus.Ok, iamLabel, null) { Key = "iam" };
            if (r.allowed == false)
                return new(CheckStatus.Fail, iamLabel,
                    $"Ask your GCP admin for Vertex AI User (roles/aiplatform.user) on {v.Project}.") { Key = "iam" };
            var raw = Scrub(r.error ?? "", tok);
            return new(CheckStatus.Warn, "Couldn't check the Vertex AI permission", null, raw.Length > 0 ? raw : null) { Key = "iam" };
        }

        // Only called once the token is known, so the output can be scrubbed of it.
        CheckItem ModelResult(int i)
        {
            var m = models[i];
            var (mx, mo, me) = probes[i].GetAwaiter().GetResult(); // already finished
            if (mx == 0 && mo.Contains("OK"))
                return new(CheckStatus.Ok, ModelLabel(m), null) { Key = m.Key };
            var last = Scrub(LineOf(mo + "\n" + me, first: false), token);
            if (last.Length > 200) last = last[..200];
            return new(CheckStatus.Fail, ModelFailLabel(m.Id ?? "The default model", v.Region, mx, last),
                $"Check the model is enabled in Model Garden and served in region {v.Region}.",
                last.Length > 0 ? last : null) { Key = m.Key };
        }
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

    private static string Scrub(string s, string? token) =>
        string.IsNullOrEmpty(token) ? s : s.Replace(token, "***");

    private static string LineOf(string text, bool first)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : first ? lines[0] : lines[^1];
    }
}
