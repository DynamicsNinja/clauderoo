using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clauderoo.Core;

public static class AppVersion
{
    /// <summary>The informational version (without a "+commit" suffix); "0.0.0-dev" for local builds.</summary>
    public static string Current { get; } = ReadCurrent();

    /// <summary>The GitHub "owner/repo" this build was stamped with, or null for local builds.</summary>
    public static string? Repo { get; } = ReadRepo();

    /// <summary>Parses "1.2.3", "v1.2.3" or "1.2.3-beta" (the suffix is ignored).</summary>
    public static Version? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s.Trim().TrimStart('v', 'V'), @"^\d+(\.\d+){0,3}");
        return m.Success && Version.TryParse(m.Value.Contains('.') ? m.Value : m.Value + ".0", out var v) ? v : null;
    }

    private static string ReadCurrent()
    {
        var v = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(v)) return "0.0.0-dev";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    private static string? ReadRepo()
    {
        var r = typeof(AppVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ClauderooRepo")?.Value;
        return string.IsNullOrWhiteSpace(r) ? null : r.Trim();
    }
}

public record ReleaseInfo(Version Version, string Tag, Uri ZipUrl, Uri ShaUrl);

/// <summary>Finds the latest GitHub release and downloads it, verifying its SHA256. All HTTP goes through the injected handler.</summary>
public sealed class Updater(HttpMessageHandler h, string repo)
{
    private static readonly Regex ZipName = new(@"^clauderoo-.+-win-x64\.zip$", RegexOptions.IgnoreCase);
    private static readonly Regex Hex64 = new(@"\b[0-9a-fA-F]{64}\b");

    private HttpClient NewClient()
    {
        var c = new HttpClient(h, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("clauderoo");
        return c;
    }

    public async Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        using var http = NewClient();
        using var resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            var version = AppVersion.Parse(tag);
            if (tag == null || version == null || !root.TryGetProperty("assets", out var assets)) return null;
            Uri? zip = null, sha = null;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                if (ZipName.IsMatch(name)) zip = uri;
                else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase)
                         && name.StartsWith("clauderoo-", StringComparison.OrdinalIgnoreCase)) sha = uri;
            }
            return zip == null || sha == null ? null : new ReleaseInfo(version, tag, zip, sha);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null; // an unexpected shape: treat as "no usable release"
        }
    }

    /// <summary>Downloads the zip, checks it against the .sha256 file and extracts it; returns the extracted folder.</summary>
    public async Task<string> DownloadVerifiedAsync(ReleaseInfo r, string tempDir, CancellationToken ct)
    {
        const string Failed = "Download failed verification; nothing was changed.";
        byte[] zip;
        string shaText;
        try
        {
            using var http = NewClient();
            zip = await http.GetByteArrayAsync(r.ZipUrl, ct);
            shaText = await http.GetStringAsync(r.ShaUrl, ct);
        }
        catch (HttpRequestException)
        {
            throw new ClauderooException("Couldn't download the update; nothing was changed.");
        }

        var m = Hex64.Match(shaText);
        var actual = Convert.ToHexString(SHA256.HashData(zip));
        if (!m.Success || !string.Equals(m.Value, actual, StringComparison.OrdinalIgnoreCase))
            throw new ClauderooException(Failed);

        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "release.zip");
        var outDir = Path.Combine(tempDir, "extracted");
        await File.WriteAllBytesAsync(zipPath, zip, ct);
        ZipFile.ExtractToDirectory(zipPath, outDir, overwriteFiles: true);
        File.Delete(zipPath);
        return outDir;
    }
}
