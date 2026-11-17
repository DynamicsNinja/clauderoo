using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clauderoo.Core;

namespace Clauderoo.Cli;

/// <summary>
/// `cr mcp`: a Model Context Protocol server speaking line-delimited JSON-RPC 2.0 on stdin/stdout. Nothing but protocol
/// messages is ever written to <c>output</c>; diagnostics go to <c>log</c> (stderr). No result, error or log line
/// carries a token, a stored key or the contents of a .credentials.json.
/// </summary>
public sealed class McpServer(ProfileManager pm, UsageClient usage,
    Func<LaunchSpec, TimeSpan, CancellationToken, Task<(int code, string stdout, string stderr)>> runCapture,
    Func<DateTimeOffset>? now = null, Func<string, string?>? getEnv = null, string? claudePath = null,
    string? defaultCwd = null, TextWriter? log = null)
{
    public const string ProtocolVersion = "2025-06-18";
    public static readonly TimeSpan DelegateTimeout = TimeSpan.FromMinutes(10);
    /// <summary>Each of a delegated run's stdout and stderr is cut to this many characters in the result.</summary>
    public const int MaxStreamChars = 100 * 1024;
    /// <summary>The empty MCP config a delegated run gets, so it can't call back into this server.</summary>
    public const string EmptyMcpConfig = """{"mcpServers":{}}""";

    // The usage tool may be called in a loop: a profile is fetched at most once per FetchGap, a cached reading younger
    // than that is served as is, and a rate limit is honoured for at least MinRateLimit.
    private static readonly TimeSpan FetchGap = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinRateLimit = TimeSpan.FromMinutes(10);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<string, string?> _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
    private readonly TextWriter _log = log ?? Console.Error;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _usageLock = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _lastFetch = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Status, string? Note)> _lastOutcome = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();

    // Relaxed escaping keeps apostrophes and non-ASCII readable; quotes and control characters are still escaped, so
    // every message stays on one line. stdout is UTF-8.
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class InvalidParamsException(string message) : Exception(message);

    private sealed record ToolResult(string Text, bool IsError);

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        var running = new List<Task>();
        while (true)
        {
            string? line;
            try { line = await input.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await Log("mcp: ignored a line that isn't JSON").ConfigureAwait(false);
                await Send(output, Error(null, -32700, "Parse error"), ct).ConfigureAwait(false);
                continue;
            }
            if (msg is not JsonObject obj)
            {
                await Send(output, Error(null, -32600, "Invalid Request"), ct).ConfigureAwait(false);
                continue;
            }

            var method = obj["method"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
            var prms = obj["params"] as JsonObject;
            // A notification (no id) never gets a response, not even an error.
            if (!obj.TryGetPropertyValue("id", out var idNode))
            {
                if (method == "notifications/cancelled" && prms?["requestId"] is { } rid
                    && _inflight.TryGetValue(rid.ToJsonString(), out var cts))
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                continue;
            }
            var id = idNode?.DeepClone();
            if (method == null)
            {
                await Send(output, Error(id, -32600, "Invalid Request"), ct).ConfigureAwait(false);
                continue;
            }

            if (method == "tools/call" && prms?["name"] is JsonValue nv && nv.TryGetValue<string>(out var tn) && tn == "delegate")
            {
                // A delegated run can take minutes: it runs in the background so other requests are still answered.
                // Its launch spec is built here, on the reader loop, so config reads never race a set_default.
                var key = id?.ToJsonString() ?? "null";
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                JsonObject? immediate = null;
                Task<ToolResult>? work = null;
                try { work = StartDelegate(prms["arguments"] as JsonObject, cts.Token); }
                catch (Exception e) { immediate = ToolErrorResponse(id, e); }
                if (work == null)
                {
                    cts.Dispose();
                    await Send(output, immediate!, ct).ConfigureAwait(false);
                    continue;
                }
                _inflight[key] = cts;
                running.Add(Task.Run(async () =>
                {
                    JsonObject resp;
                    try { resp = Result(id, ToolJson(await work.ConfigureAwait(false))); }
                    catch (Exception e) { resp = ToolErrorResponse(id, e); }
                    finally
                    {
                        _inflight.TryRemove(key, out _);
                        cts.Dispose();
                    }
                    await Send(output, resp, CancellationToken.None).ConfigureAwait(false);
                }, CancellationToken.None));
                continue;
            }

            JsonObject response;
            try { response = await Handle(id, method, prms, ct).ConfigureAwait(false); }
            catch (Exception e) { response = ToolErrorResponse(id, e); }
            await Send(output, response, ct).ConfigureAwait(false);
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task<JsonObject> Handle(JsonNode? id, string method, JsonObject? prms, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "clauderoo", ["version"] = Version() },
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = Tools() });
            case "tools/call":
            {
                var name = prms?["name"] is JsonValue v && v.TryGetValue<string>(out var n) ? n : null;
                var args = prms?["arguments"] as JsonObject;
                var result = name switch
                {
                    "list_profiles" => ListProfiles(),
                    "usage" => await UsageTool(OptionalString(args, "profile"), ct).ConfigureAwait(false),
                    "set_default" => SetDefault(RequiredString(args, "profile")),
                    _ => throw new InvalidParamsException($"Unknown tool '{name}'."),
                };
                return Result(id, ToolJson(result));
            }
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    // Tool failures are results with isError; malformed calls are JSON-RPC errors; anything unexpected shows its type
    // only, since its message wasn't written with secrecy in mind.
    private JsonObject ToolErrorResponse(JsonNode? id, Exception e) => e switch
    {
        InvalidParamsException => Error(id, -32602, e.Message),
        ClauderooException or IOException or UnauthorizedAccessException => Result(id, ToolJson(new ToolResult(e.Message, true))),
        _ => LogAndWrap(id, e),
    };

    private JsonObject LogAndWrap(JsonNode? id, Exception e)
    {
        _ = Log($"mcp: unexpected {e.GetType().Name}");
        return Result(id, ToolJson(new ToolResult($"Unexpected error ({e.GetType().Name}).", true)));
    }

    private static JsonArray Tools()
    {
        static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
        static JsonObject Tool(string name, string description, JsonObject props, params string[] required) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["additionalProperties"] = false,
            },
        };
        return
        [
            Tool("list_profiles",
                "List clauderoo's Claude profiles: provider, account or project, whether it's the default, running " +
                "sessions and the last saved usage. Uses no quota.",
                new JsonObject()),
            Tool("usage",
                "Show 5-hour and 7-day usage limits for one profile, or all of them. Uses no quota. Readings are cached: a " +
                "profile is checked at most once every 2 minutes, and longer when the service is rate limiting.",
                new JsonObject { ["profile"] = Str("Profile name. Leave out for every profile.") }),
            Tool("set_default",
                "Make a profile the default for new terminals (plain 'claude'). Running sessions keep their profile.",
                new JsonObject { ["profile"] = Str("Profile name.") }, "profile"),
            Tool("delegate",
                "Run a one-shot prompt with 'claude -p' under another profile and return its output. It uses the TARGET " +
                "account's quota. It runs with that profile's own permissions and allow-list: anything needing approval " +
                "is denied, since it can't ask. No MCP servers are loaded. Times out after 10 minutes. It refuses the " +
                "profile this session already runs under.",
                new JsonObject
                {
                    ["profile"] = Str("Profile to run under."),
                    ["prompt"] = Str("The prompt."),
                    ["cwd"] = Str("Folder to run in. Defaults to the server's working folder."),
                },
                "profile", "prompt"),
        ];
    }

    // ---- list_profiles ----

    private ToolResult ListProfiles()
    {
        var cache = LoadCache();
        var arr = new JsonArray();
        foreach (var i in pm.List())
        {
            arr.Add(new JsonObject
            {
                ["name"] = i.Name,
                ["provider"] = i.Provider,
                ["account"] = i.Provider == Providers.Login ? i.Email : null,
                ["detail"] = i.ProviderDetail,
                ["isDefault"] = i.IsDefault,
                ["runningSessions"] = pm.Sessions.Running(i.Name).Count,
                ["cachedUsage"] = cache.TryGetValue(i.Name, out var r) ? ReadingJson(r) : null,
            });
        }
        return new ToolResult(new JsonObject { ["profiles"] = arr }.ToJsonString(Compact), false);
    }

    // ---- usage ----

    private async Task<ToolResult> UsageTool(string? profile, CancellationToken ct)
    {
        List<string> names;
        if (profile != null) { pm.Get(profile); names = [profile]; }
        else names = pm.Config.Profiles.Keys.ToList();

        var rows = new JsonArray();
        await _usageLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var name in names) rows.Add(await UsageRow(name, ct).ConfigureAwait(false));
        }
        finally { _usageLock.Release(); }
        return new ToolResult(new JsonObject { ["profiles"] = rows }.ToJsonString(Compact), false);
    }

    private async Task<JsonObject> UsageRow(string name, CancellationToken ct)
    {
        var entry = pm.Get(name);
        var provider = entry.Home ? Providers.Login : Providers.Of(entry);
        if (provider != Providers.Login)
            return Row(name, "notTracked", "none", null, new UsageResult.NotTracked(provider).Message);

        var cached = LoadCache().GetValueOrDefault(name);
        var t = _now();
        if (_blockedUntil.TryGetValue(name, out var until) && t < until)
            return Row(name, "rateLimited", "cache", cached,
                $"The usage service is rate limiting requests; showing the last saved reading. Next check after {Iso(until)}.");
        if (cached != null && t - cached.FetchedAt < FetchGap)
            return Row(name, "ok", "cache", cached, null);
        if (_lastFetch.TryGetValue(name, out var last) && t - last < FetchGap)
        {
            var (status, note) = _lastOutcome.GetValueOrDefault(name, ("ok", null));
            return Row(name, status, "cache", cached,
                ((note == null ? "" : note + " ") + "Checked less than 2 minutes ago; showing the last saved reading.").Trim());
        }

        _lastFetch[name] = t;
        var r = await usage.FetchAsync(name, pm.ConfigDirOf(name), entry, ct).ConfigureAwait(false);
        JsonObject row;
        switch (r)
        {
            case UsageResult.Ok ok:
                try { new UsageCache(pm.Paths).Put(ok.Reading); }
                catch (IOException) { await Log("mcp: couldn't save usage").ConfigureAwait(false); }
                row = Row(name, "ok", "live", ok.Reading, null);
                break;
            case UsageResult.RateLimited rl:
            {
                var wait = rl.RetryAfter < MinRateLimit ? MinRateLimit : rl.RetryAfter;
                _blockedUntil[name] = t + wait;
                row = Row(name, "rateLimited", "cache", cached,
                    $"The usage service is rate limiting requests; showing the last saved reading. Next check after {Iso(t + wait)}.");
                break;
            }
            case UsageResult.Failed f:
                row = Row(name, "failed", "cache", cached, f.Message); // already scrubbed of the token
                break;
            case UsageResult.Unavailable:
                row = Row(name, "unavailable", "cache", cached, "The usage service answered with something unexpected.");
                break;
            case UsageResult.Expired:
                row = Row(name, "expired", "none", null, $"Sign-in expired. Start a session with 'cr start {name}' to refresh it.");
                break;
            case UsageResult.NotSignedIn:
                row = Row(name, "notSignedIn", "none", null, "Not signed in.");
                break;
            case UsageResult.NotTracked nt:
                row = Row(name, "notTracked", "none", null, nt.Message);
                break;
            default:
                row = Row(name, "unavailable", "none", null, "Unknown usage result.");
                break;
        }
        _lastOutcome[name] = (row["status"]!.GetValue<string>(), row["message"]?.GetValue<string>());
        return row;
    }

    private static JsonObject Row(string name, string status, string source, UsageReading? reading, string? message)
    {
        var o = new JsonObject { ["name"] = name, ["status"] = status, ["source"] = reading == null ? "none" : source };
        if (reading != null)
        {
            var r = ReadingJson(reading);
            foreach (var key in r.Select(kv => kv.Key).ToList())
            {
                var v = r[key];
                r.Remove(key); // a node can only have one parent
                o[key] = v;
            }
        }
        o["message"] = message;
        return o;
    }

    private static JsonObject ReadingJson(UsageReading r) => new()
    {
        ["plan"] = r.Plan,
        ["fetchedAt"] = Iso(r.FetchedAt),
        ["windows"] = new JsonArray(r.Windows.Select(w => (JsonNode)new JsonObject
        {
            ["name"] = w.Name,
            ["percent"] = w.Percent,
            ["resetsAt"] = w.ResetsAt is { } at ? Iso(at) : null,
        }).ToArray()),
    };

    private IReadOnlyDictionary<string, UsageReading> LoadCache()
    {
        try { return new UsageCache(pm.Paths).Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new Dictionary<string, UsageReading>(); }
    }

    // ---- set_default ----

    private ToolResult SetDefault(string profile)
    {
        var report = pm.SetDefault(profile);
        var sb = new StringBuilder($"Default is now {pm.Config.DefaultProfile}. New terminals use it; running sessions keep their profile.");
        foreach (var p in report.Problems) sb.Append("\nwarning: ").Append(p);
        return new ToolResult(sb.ToString(), false);
    }

    // ---- delegate ----

    /// <summary>The arguments a delegated run gets. Never a permission override; always no MCP servers.</summary>
    public static IReadOnlyList<string> DelegateArgs(string prompt) =>
        ["-p", prompt, "--output-format", "text", "--strict-mcp-config", "--mcp-config", EmptyMcpConfig];

    // Characters cmd.exe acts on when it re-parses a .cmd/.bat command line, where quoting can't be trusted.
    private static readonly char[] BatchUnsafe = ['"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n'];

    private Task<ToolResult> StartDelegate(JsonObject? args, CancellationToken ct)
    {
        var profile = RequiredString(args, "profile");
        var prompt = RequiredString(args, "prompt");
        var cwdArg = OptionalString(args, "cwd");
        pm.Get(profile); // unknown profile: a plain error

        var caller = _getEnv(WhichProfile.SessionVar);
        if (!string.IsNullOrEmpty(caller) && string.Equals(caller, profile, StringComparison.OrdinalIgnoreCase))
            throw new ClauderooException(
                $"This session already runs under {profile}; delegate to a different profile.");

        var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(cwdArg) ? defaultCwd ?? Environment.CurrentDirectory : cwdArg);
        if (!Directory.Exists(dir)) throw new ClauderooException($"Folder not found: {dir}");

        var spec = Launcher.InPlace(pm.Config, pm.Paths, profile, dir, DelegateArgs(prompt), claudePath);
        var ext = Path.GetExtension(spec.FileName);
        if ((ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            && prompt.IndexOfAny(BatchUnsafe) >= 0)
            throw new ClauderooException(
                "This claude is a .cmd script, which can't safely receive a prompt containing quotes, line breaks or any of % ! ^ & | < >. " +
                "Rephrase the prompt, or install Claude Code's native build (claude.exe).");

        // A token profile's token is decrypted here, into the child's environment only.
        spec = pm.WithToken(spec, profile);
        return RunDelegate(profile, spec, ct);
    }

    private async Task<ToolResult> RunDelegate(string profile, LaunchSpec spec, CancellationToken ct)
    {
        var secret = spec.ExtraEnv?.GetValueOrDefault(ProfileManager.TokenEnvVar);
        string Clean(string s) => Secrets.Scrub(s, secret);

        int code;
        string stdout, stderr;
        try
        {
            (code, stdout, stderr) = await runCapture(spec, DelegateTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new ToolResult($"The run under {profile} didn't finish within 10 minutes and was stopped.", true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ToolResult($"The run under {profile} was cancelled.", true);
        }
        catch (Exception e)
        {
            await Log($"mcp: delegate to {profile} failed to run ({e.GetType().Name})").ConfigureAwait(false);
            return new ToolResult(Clean($"Couldn't run claude under {profile}: {e.Message}"), true);
        }

        var sb = new StringBuilder();
        if (code != 0) sb.Append($"claude exited with code {code.ToString(CultureInfo.InvariantCulture)}.\n\n");
        sb.Append(Truncate(Clean(stdout)));
        if (!string.IsNullOrWhiteSpace(stderr)) sb.Append("\n\n--- stderr ---\n").Append(Truncate(Clean(stderr)));
        return new ToolResult(sb.ToString(), code != 0);
    }

    private static string Truncate(string s) =>
        s.Length <= MaxStreamChars ? s : s[..MaxStreamChars] + "\n[output truncated to 100 KB]";

    /// <summary>
    /// The real runner: starts the spec with its stdin closed, captures both streams (bounded), records the session, and
    /// kills the whole process tree when the timeout or cancellation hits. A timeout surfaces as TimeoutException.
    /// </summary>
    public static Func<LaunchSpec, TimeSpan, CancellationToken, Task<(int code, string stdout, string stderr)>> ProcessRunner(
        SessionTracker sessions) => async (spec, timeout, ct) =>
    {
        var psi = Launcher.StartInfo(spec);
        psi.RedirectStandardInput = true; // never the server's own stdin, which carries the protocol
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.CreateNoWindow = true;

        using var p = Process.Start(psi) ?? throw new ClauderooException($"Failed to start {spec.FileName}.");
        if (spec.Profile != null)
        {
            try { sessions.Record(spec.Profile, p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
        try { p.StandardInput.Close(); } catch (IOException) { }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var outTask = ReadCapped(p.StandardOutput, linked.Token);
        var errTask = ReadCapped(p.StandardError, linked.Token);
        try
        {
            await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var so = await outTask.WaitAsync(linked.Token).ConfigureAwait(false);
            var se = await errTask.WaitAsync(linked.Token).ConfigureAwait(false);
            return (p.ExitCode, so, se);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) throw new TimeoutException();
            throw;
        }
    };

    // Keeps a little more than the result shows (so it can say "truncated") and drains the rest so the child never blocks.
    private static async Task<string> ReadCapped(StreamReader r, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        int n;
        try
        {
            while ((n = await r.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
                if (sb.Length <= MaxStreamChars) sb.Append(buf, 0, Math.Min(n, MaxStreamChars + 1 - sb.Length));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
        return sb.ToString();
    }

    // ---- JSON-RPC plumbing ----

    private static string? OptionalString(JsonObject? args, string name)
    {
        if (args?[name] is not { } node) return null;
        return node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : throw new InvalidParamsException($"'{name}' must be a string.");
    }

    private static string RequiredString(JsonObject? args, string name) =>
        OptionalString(args, name) is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new InvalidParamsException($"'{name}' is required.");

    private static JsonObject ToolJson(ToolResult r) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.Text }),
        ["isError"] = r.IsError,
    };

    private static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private async Task Send(TextWriter output, JsonObject message, CancellationToken ct)
    {
        var text = message.ToJsonString(Compact); // one line: JSON escapes any newline inside strings
        await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(text + "\n").ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task Log(string line)
    {
        try
        {
            await _log.WriteLineAsync(line).ConfigureAwait(false);
            await _log.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    private static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Version() =>
        typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0";
}
