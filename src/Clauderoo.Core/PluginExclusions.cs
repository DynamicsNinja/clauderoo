using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core;

/// <summary>
/// Leaving a plugin out of a profile. Claude Code keeps every installed plugin in one place (plugins\
/// installed_plugins.json, known_marketplaces.json, marketplaces\ and cache\, all with absolute paths), and turns each
/// one on or off with "enabledPlugins" in settings.json. So the plugins folder stays one shared junction and an excluded
/// plugin is written as <c>"enabledPlugins": { "&lt;id&gt;": false }</c> into the profile's copy of settings.json.
/// </summary>
public static class PluginExclusions
{
    /// <summary>
    /// The plugin ids ("name@marketplace") home knows: installed ones (installed_plugins.json) and those its settings
    /// turn on or off. Read only, and an unreadable file just counts as empty.
    /// </summary>
    public static IReadOnlyList<string> KnownIds(string homeDir, JsonObject? homeSettings = null)
    {
        var ids = new List<string>();
        void Add(string id)
        {
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        }
        try
        {
            homeSettings ??= ReadObject(Path.Combine(homeDir, "settings.json"));
            if (homeSettings?["enabledPlugins"] is JsonObject enabled)
                foreach (var (k, _) in enabled) Add(k);
        }
        catch (ArgumentException) { } // duplicate keys surface lazily: treat the file as unreadable
        try
        {
            if (ReadObject(Path.Combine(homeDir, SharedItems.Plugins, "installed_plugins.json"))?["plugins"] is JsonObject installed)
                foreach (var (k, _) in installed) Add(k);
        }
        catch (ArgumentException) { }
        return ids;
    }

    /// <summary>
    /// The ids to turn off for the profile's exclusions: an exclusion names a plugin either fully ("name@marketplace")
    /// or by name alone, which then covers that name from every marketplace.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string> excluded, IReadOnlyList<string> known)
    {
        var off = new List<string>();
        foreach (var x in excluded)
        {
            var hits = known.Where(id => Matches(id, x)).ToList();
            if (hits.Count == 0 && x.Contains('@')) hits.Add(x); // not installed (yet): off once it is
            foreach (var id in hits)
                if (!off.Contains(id, StringComparer.OrdinalIgnoreCase)) off.Add(id);
        }
        return off;
    }

    public static bool Matches(string id, string exclusion) =>
        string.Equals(id, exclusion, StringComparison.OrdinalIgnoreCase)
        || (!exclusion.Contains('@') && id.StartsWith(exclusion + "@", StringComparison.OrdinalIgnoreCase));

    private static JsonObject? ReadObject(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonNode.Parse(fs) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
