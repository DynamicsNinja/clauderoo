using System.Collections;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Clauderoo.Core;

public enum CheckStatus { Ok, Warn, Fail, Skipped }

public record CheckItem(CheckStatus Status, string Label, string? Hint);

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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

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

    public async Task<(int exit, string stdout, string stderr)> RunAsync(string claudePath, IReadOnlyList<string> args,
        string configDir, TimeSpan timeout, CancellationToken ct)
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
        };
        if (isCmd)
        {
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(claudePath);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

        using var proc = Process.Start(psi) ?? throw new ClauderooException("Couldn't start claude.");
        proc.StandardInput.Close();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return (-1, "", "timed out after 60 s");
        }
        return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }
}

public partial class ProfileManager
{
    private static readonly string[] LeftoverPrefixes = ["ANTHROPIC_", "CLAUDE_CODE_", "CLOUD_ML_", "VERTEX_REGION_"];
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

    public async Task<IReadOnlyList<CheckItem>> CheckSetupAsync(string name, CancellationToken ct = default)
    {
        var cfg = Config;
        var entry = NonHome(cfg, name);
        if (Providers.Of(entry) != Providers.Vertex || entry.Vertex == null)
            throw new ClauderooException($"Profile '{name}' doesn't use Vertex AI.");
        var v = entry.Vertex;
        var dir = paths.ProfileDir(name);
        var items = new List<CheckItem>();

        var models = new[] { v.SonnetModel, v.OpusModel, v.HaikuModel }
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!).ToList();
        var modelLabels = models.Count == 0
            ? new List<string> { "Default model answers" }
            : models.Select(m => $"{m} answers").ToList();
        var iamLabel = $"Vertex AI permission on {v.Project}";
        const string GcloudLabel = "gcloud installed", SignInLabel = "Google sign-in for Claude",
            LeftoverLabel = "No leftover provider variables", ClaudeLabel = "Claude Code installed";

        if (gcloud.Find() == null)
        {
            items.Add(new(CheckStatus.Fail, GcloudLabel, "Install it: winget install Google.CloudSDK, then open a new terminal."));
            foreach (var l in new[] { SignInLabel, Gac, iamLabel, LeftoverLabel, ClaudeLabel }.Concat(modelLabels))
                items.Add(new(CheckStatus.Skipped, l, null));
            return items;
        }
        items.Add(new(CheckStatus.Ok, GcloudLabel, null));

        ct.ThrowIfCancellationRequested();
        string? token = null;
        var (exit, stdout, stderr) = gcloud.Run("auth", "application-default", "print-access-token");
        if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            token = stdout.Trim();
            items.Add(new(CheckStatus.Ok, SignInLabel, null));
        }
        else
        {
            var line = Scrub(LineOf(stderr, first: true), stdout.Trim());
            items.Add(new(CheckStatus.Fail, line.Length > 0 ? $"{SignInLabel}: {line}" : SignInLabel,
                "Run: gcloud auth application-default login (tick every box on the consent page)."));
        }

        items.Add(CheckGac());

        if (token == null)
            items.Add(new(CheckStatus.Skipped, iamLabel, null));
        else
        {
            var (allowed, error) = await iam.HasPermissionAsync(v.Project, token, "aiplatform.endpoints.predict", ct).ConfigureAwait(false);
            if (allowed == true) items.Add(new(CheckStatus.Ok, iamLabel, null));
            else if (allowed == false)
                items.Add(new(CheckStatus.Fail, iamLabel,
                    $"Ask your GCP admin for Vertex AI User (roles/aiplatform.user) on {v.Project}."));
            else
                items.Add(new(CheckStatus.Warn, Scrub($"Could not check Vertex AI permission: {error}", token), null));
        }

        var leftovers = new List<string>();
        foreach (var (scope, vars) in new[] { ("User", machineEnv.User()), ("Machine", machineEnv.Machine()) })
            foreach (var k in vars.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                if (!k.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase)
                    && LeftoverPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    leftovers.Add($"{k} ({scope})");
        items.Add(leftovers.Count == 0
            ? new(CheckStatus.Ok, LeftoverLabel, null)
            : new(CheckStatus.Warn, $"Leftover variables: {string.Join(", ", leftovers)}",
                "Remove them in Windows settings › Environment variables, then restart terminals."));

        var claude = claudeRunner.Find();
        if (claude == null)
        {
            items.Add(new(CheckStatus.Fail, ClaudeLabel, "Install Claude Code, then open a new terminal."));
            foreach (var l in modelLabels) items.Add(new(CheckStatus.Skipped, l, null));
            return items;
        }
        items.Add(new(CheckStatus.Ok, ClaudeLabel, null));
        if (token == null)
        {
            foreach (var l in modelLabels) items.Add(new(CheckStatus.Skipped, l, null));
            return items;
        }

        var runs = models.Count == 0 ? new List<string?> { null } : models.Select(m => (string?)m).ToList();
        for (var i = 0; i < runs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var args = new List<string> { "-p", "Reply with exactly: OK" };
            if (runs[i] != null) { args.Add("--model"); args.Add(runs[i]!); }
            var (mx, mo, me) = await claudeRunner.RunAsync(claude, args, dir, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (mx == 0 && mo.Contains("OK"))
                items.Add(new(CheckStatus.Ok, modelLabels[i], null));
            else
            {
                var last = Scrub(LineOf(mo + "\n" + me, first: false), token);
                if (last.Length > 200) last = last[..200];
                items.Add(new(CheckStatus.Fail, last.Length > 0 ? $"{modelLabels[i]}: {last}" : modelLabels[i],
                    $"Check the model is enabled in Model Garden and served in region {v.Region}."));
            }
        }
        return items;
    }

    private CheckItem CheckGac()
    {
        string? path = null, scope = null;
        var p = machineEnv.Process(Gac);
        if (!string.IsNullOrWhiteSpace(p)) { path = p; scope = "Process"; }
        else if (TryGet(machineEnv.User(), Gac, out var u)) { path = u; scope = "User"; }
        else if (TryGet(machineEnv.Machine(), Gac, out var m)) { path = m; scope = "Machine"; }
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
