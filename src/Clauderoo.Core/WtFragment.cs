using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core;

/// <summary>
/// A Windows Terminal settings fragment with one profile per clauderoo profile, so each appears in Windows Terminal's
/// new-tab dropdown. Windows Terminal reads it at start-up; clauderoo only ever writes its own file.
/// </summary>
public static class WtFragment
{
    /// <summary>%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\clauderoo\clauderoo.json</summary>
    public static string DefaultFile() => FileUnder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string FileUnder(string localAppData) =>
        Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments", "clauderoo", "clauderoo.json");

    public static string ProfileName(string profile) => $"Claude · {profile}";

    // Distinct, mid-tone colours that read on both light and dark tab rows.
    private static readonly string[] TabColors =
        ["#D97757", "#4F8EDC", "#3FA66B", "#B565C9", "#D4A72C", "#2BA3A8", "#D9577F", "#7A86D9"];

    /// <summary>The profile's tab colour: the same for a name every time, whatever else is configured.</summary>
    public static string TabColor(string profile)
    {
        // FNV-1a: string.GetHashCode is randomised per process.
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(profile)) h = (h ^ b) * 16777619u;
        return TabColors[h % (uint)TabColors.Length];
    }

    private static readonly Guid Namespace = new("6b1e3c2a-5f0d-4c8e-9a7b-2d4f6e8a0c13");

    /// <summary>A name-based (version 5) GUID, so a profile keeps its Windows Terminal entry between rewrites.</summary>
    public static Guid ProfileGuid(string profile)
    {
        var ns = Namespace.ToByteArray(bigEndian: true);
        var hash = SHA1.HashData([.. ns, .. Encoding.UTF8.GetBytes(profile)]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>The fragment's JSON. Each profile runs the configured shell, which runs `clauderoo run &lt;profile&gt;`.</summary>
    public static string Build(ClauderooConfig cfg, ClauderooPaths paths, string clauderooExe)
    {
        var list = new JsonArray();
        foreach (var (name, entry) in cfg.Profiles)
        {
            var p = new JsonObject
            {
                ["guid"] = ProfileGuid(name).ToString("B"),
                ["name"] = ProfileName(name),
                ["commandline"] = Launcher.ProfileCommandLine(cfg, paths, name, clauderooExe),
                ["tabTitle"] = ProfileName(name),
                ["tabColor"] = TabColor(name),
                ["hidden"] = false,
            };
            if (!string.IsNullOrWhiteSpace(entry.StartDir)) p["startingDirectory"] = entry.StartDir;
            list.Add(p);
        }
        var root = new JsonObject
        {
            ["$help"] = "Written by clauderoo ('cr wt-profiles'). Changes here are overwritten; remove with 'cr wt-profiles --remove'.",
            ["profiles"] = list,
        };
        // Relaxed escaping keeps "·" and the quotes readable; the file is only ever read as JSON.
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + Environment.NewLine;
    }

    /// <summary>Writes the fragment; returns how many profiles it holds.</summary>
    public static int Write(string file, ClauderooConfig cfg, ClauderooPaths paths, string clauderooExe)
    {
        var json = Build(cfg, paths, clauderooExe);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.WriteAllText(file, json);
        return cfg.Profiles.Count;
    }

    /// <summary>Removes the fragment (and its folder, once empty). False when there was none.</summary>
    public static bool Remove(string file)
    {
        if (!File.Exists(file)) return false;
        File.Delete(file);
        var dir = Path.GetDirectoryName(file)!;
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (IOException) { }
        return true;
    }

    /// <summary>
    /// Rewrites the fragment when one exists and is out of date (profiles added, renamed or removed, or another shell).
    /// Best effort: never throws, and never creates a fragment the user hasn't asked for.
    /// </summary>
    public static bool RefreshIfPresent(string? file, ClauderooConfig cfg, ClauderooPaths paths, string clauderooExe)
    {
        try
        {
            if (file is null || !File.Exists(file)) return false;
            var json = Build(cfg, paths, clauderooExe);
            if (File.ReadAllText(file) == json) return false;
            AtomicFile.WriteAllText(file, json);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ClauderooException) { return false; }
    }
}
