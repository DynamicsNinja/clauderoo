using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Clauderoo.Core;

/// <summary>
/// The persistent log at <c>~/.clauderoo/logs/clauderoo.log</c>: warnings, errors and key actions (settings saved,
/// default changed, auto-switch decisions, sync problems) from the CLI, the tray and the MCP server. Off until a
/// program calls <see cref="Start"/>, so library code and tests write nothing. Logging never throws, and every line is
/// passed through <see cref="Redact"/> first: tokens, API keys and other secrets never reach the file.
/// </summary>
public static class AppLog
{
    private static volatile RollingLog? _current;

    /// <summary>The log file for a clauderoo root.</summary>
    public static string PathFor(ClauderooPaths p) => Path.Combine(p.Root, "logs", RollingLog.FileName);

    /// <summary>Turns logging on for this process. <paramref name="source"/> names the writer: cli, tray or mcp.</summary>
    public static void Start(ClauderooPaths p, string source) => _current = new RollingLog(PathFor(p), source);

    /// <summary>Turns logging off again (tests).</summary>
    internal static void Stop() => _current = null;

    public static void Info(string message) => _current?.Write("INFO", message);
    public static void Warn(string message) => _current?.Write("WARN", message);
    public static void Error(string message) => _current?.Write("ERROR", message);

    // sk-ant-api03-…, sk-ant-oat01-… and any other Anthropic-style key.
    private static readonly Regex AnthropicKey = new(@"sk-ant-[A-Za-z0-9_\-]+", RegexOptions.Compiled);
    // "Authorization: Bearer x", "Bearer x", "Basic x".
    private static readonly Regex AuthScheme = new(@"(?i)\b(bearer|basic)\s+[^\s""',;]+", RegexOptions.Compiled);
    // accessToken: "x", "refresh_token"="x", CLAUDE_CODE_OAUTH_TOKEN=x, ANTHROPIC_API_KEY x, password=x …
    private static readonly Regex SecretField = new(
        @"(?i)(""?[A-Za-z0-9_\-]*(token|secret|password|passwd|api[_\-]?key|apikey|authorization|credential)[A-Za-z0-9_\-]*""?\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;}]+)",
        RegexOptions.Compiled);
    // Long opaque runs (JWT parts, base64 or hex blobs). Path segments and GUIDs are shorter.
    private static readonly Regex OpaqueBlob = new(@"[A-Za-z0-9_\-\+=]{40,}", RegexOptions.Compiled);

    /// <summary>
    /// Removes anything that looks like a secret, and folds the text onto one line so a message can't forge log lines.
    /// Deliberately over-eager: a hidden harmless value costs nothing, a leaked token costs an account.
    /// </summary>
    public static string Redact(string message)
    {
        var s = message.Replace("\r", " ").Replace("\n", " ");
        s = AnthropicKey.Replace(s, "sk-ant-***");
        s = AuthScheme.Replace(s, m => m.Groups[1].Value + " ***");
        s = SecretField.Replace(s, m => m.Groups[1].Value + "***");
        s = OpaqueBlob.Replace(s, "***");
        return s;
    }
}

/// <summary>
/// An append-only text log shared by every clauderoo process, capped at <c>maxBytes</c> per file and
/// <c>files</c> files (clauderoo.log, clauderoo.1.log, clauderoo.2.log). Appends and rolls happen under the
/// machine-wide mutex <c>Local\clauderoo-log-&lt;hash&gt;</c>, the innermost clauderoo lock (nothing else is taken while
/// it is held). The same message from the same process is written at most once every 10 minutes.
/// </summary>
public sealed class RollingLog
{
    public const string FileName = "clauderoo.log";
    public const long DefaultMaxBytes = 1024 * 1024;
    public const int DefaultFiles = 3;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

    private readonly string _file;
    private readonly string _source;
    private readonly long _maxBytes;
    private readonly int _files;
    private readonly string _mutexName;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);

    public RollingLog(string file, string source, long maxBytes = DefaultMaxBytes, int files = DefaultFiles,
        Func<DateTimeOffset>? now = null)
    {
        _file = file;
        _source = source;
        _maxBytes = maxBytes;
        _files = Math.Max(1, files);
        _now = now ?? (() => DateTimeOffset.Now);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToUpperInvariant())))[..16];
        _mutexName = $@"Local\clauderoo-log-{hash}";
    }

    public string File => _file;

    /// <summary>The rolled files, newest first after the live one: clauderoo.1.log, clauderoo.2.log …</summary>
    public static string Rolled(string file, int n) =>
        Path.Combine(Path.GetDirectoryName(file) ?? "", $"{Path.GetFileNameWithoutExtension(file)}.{n}{Path.GetExtension(file)}");

    public void Write(string level, string message)
    {
        try
        {
            var text = AppLog.Redact(message);
            var now = _now();
            lock (_recent)
            {
                var key = level + "|" + text;
                if (_recent.TryGetValue(key, out var last) && now - last < RepeatWindow) return;
                _recent[key] = now;
                if (_recent.Count > 500) _recent.Clear();
            }
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{now:yyyy-MM-dd HH:mm:ss.fffzzz} {level,-5} [{_source} {Environment.ProcessId}] {text}{Environment.NewLine}");
            Locked(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                RollIfFull();
                using var fs = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            });
        }
        catch (Exception) { /* logging is best effort and never breaks the caller */ }
    }

    private void RollIfFull()
    {
        var info = new FileInfo(_file);
        if (!info.Exists || info.Length < _maxBytes) return;
        if (_files == 1) { System.IO.File.Delete(_file); return; }
        var oldest = Rolled(_file, _files - 1);
        if (System.IO.File.Exists(oldest)) System.IO.File.Delete(oldest);
        for (var n = _files - 2; n >= 1; n--)
        {
            var from = Rolled(_file, n);
            if (System.IO.File.Exists(from)) System.IO.File.Move(from, Rolled(_file, n + 1));
        }
        System.IO.File.Move(_file, Rolled(_file, 1));
    }

    private void Locked(Action action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { held = true; }
            if (held) action(); // no lock in time: drop the line rather than make the caller wait
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    /// <summary>The last <paramref name="count"/> lines across the live and rolled files, oldest first.</summary>
    public static IReadOnlyList<string> Tail(string file, int count)
    {
        var lines = new List<string>();
        var files = new List<string> { file };
        for (var n = 1; n < 10; n++) files.Add(Rolled(file, n));
        foreach (var f in files)
        {
            if (lines.Count >= count) break;
            string[] read;
            try
            {
                if (!System.IO.File.Exists(f)) continue;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                read = reader.ReadToEnd().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            lines.InsertRange(0, read.Skip(Math.Max(0, read.Length - (count - lines.Count))));
        }
        return lines;
    }
}
