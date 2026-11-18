using System.Diagnostics;
using System.Text.Json;
using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>
/// The Profiles window's live check of an API key or Claude token. It runs `clauderoo check &lt;name&gt; --live --json`
/// hidden, like the tray's VS Code item: the CLI is the only place a token profile's token is read, so the tray never
/// decrypts a secret itself. The JSON holds no secret (Core scrubs every row); its rows come back as check items.
/// </summary>
internal static class LiveCheck
{
    /// <summary>The whole run, `claude -p` included (its own limit is 60 s), before the CLI is stopped.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Runs the CLI: exit code, stdout, stderr. Tests swap it.</summary>
    public delegate Task<(int Exit, string Stdout, string Stderr)> Runner(ProcessStartInfo psi, CancellationToken ct);

    public static ProcessStartInfo StartInfo(string exe, string profile)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in new[] { "check", profile, "--live", "--json" }) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// The check's rows, or a plain error when there are none. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is cancelled (the CLI and its `claude -p` are stopped).
    /// </summary>
    public static async Task<(IReadOnlyList<CheckItem>? Items, string? Error)> RunAsync(string profile, CancellationToken ct,
        string? exe = null, Runner? run = null)
    {
        exe ??= HelperExePath.Resolve();
        if (run == null && !File.Exists(exe)) return (null, "clauderoo isn't installed properly — reinstall it.");
        var (exit, stdout, stderr) = await (run ?? RunProcessAsync)(StartInfo(exe, profile), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Parse(exit, stdout, stderr);
    }

    /// <summary>
    /// Reads the CLI's answer: the checklist payload on stdout (exit 0 or 1), else its {"error": …} line on stderr.
    /// </summary>
    public static (IReadOnlyList<CheckItem>? Items, string? Error) Parse(int exit, string stdout, string stderr)
    {
        if (exit == -1) return (null, $"The check didn't finish within {(int)Timeout.TotalMinutes} minutes.");
        if (Json(stdout) is { } doc)
            using (doc)
                if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<CheckItem>();
                    foreach (var i in arr.EnumerateArray())
                    {
                        if (i.ValueKind != JsonValueKind.Object || Str(i, "label") is not { } label) continue;
                        var status = Enum.TryParse<CheckStatus>(Str(i, "status"), ignoreCase: true, out var s)
                                     && s is not (CheckStatus.Pending or CheckStatus.Running) ? s : CheckStatus.Warn;
                        items.Add(new CheckItem(status, label, Str(i, "hint"), Str(i, "detail")) { Key = Str(i, "key") });
                    }
                    if (items.Count > 0) return (items, null);
                }
        if (Json(stderr) is { } errDoc)
            using (errDoc)
                if (Str(errDoc.RootElement, "error") is { Length: > 0 } message) return (null, message);
        return (null, exit == 0 ? "The check gave no result." : "The check couldn't run.");
    }

    private static JsonDocument? Json(string text)
    {
        // The payload is one line; anything before it (there shouldn't be any) is skipped.
        var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line == null) return null;
        try
        {
            var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Exit -1: stopped after Timeout. Cancelling stops the CLI and the `claude -p` it started.
    private static async Task<(int, string, string)> RunProcessAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var p = Process.Start(psi) ?? throw new ClauderooException("Couldn't start clauderoo.");
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try { await p.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return (-1, "", "");
        }
        return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
