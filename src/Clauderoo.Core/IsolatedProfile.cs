using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core;

public partial class ProfileManager
{
    /// <summary>
    /// Creates (if missing) the isolated config folder for <paramref name="profile"/> and writes only the provider
    /// overlay into its settings.json (other keys the user added there are kept), plus a small state file
    /// (<see cref="SyncState.FileName"/>) naming the keys it wrote. The folder never gets junctions or
    /// copies of shared files, and no credential is ever copied into it. FirstRun is true for a login profile that has
    /// not signed in inside the folder yet (no .claude.json).
    /// </summary>
    public (string Dir, bool FirstRun) PrepareIsolated(string profile)
    {
        var cfg = Config;
        var entry = Entry(cfg, profile);
        var dir = paths.IsolatedDir(profile);
        Directory.CreateDirectory(dir);

        var overlay = entry.Home ? new Overlay(new Dictionary<string, string>(), new Dictionary<string, string>())
            : ProviderOverlay.Build(profile, entry, HelperExe);
        var file = Path.Combine(dir, "settings.json");
        JsonObject current = [];
        if (File.Exists(file))
        {
            try { current = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? []; }
            catch (JsonException) { throw new ClauderooException($"{file} is not valid JSON; fix or delete it."); }
        }
        // What clauderoo wrote last time is recorded in the folder, so a variable removed with `cr env` goes away too.
        var state = SyncState.Load(dir);
        var merged = ProviderOverlay.Apply(current, overlay, state.ManagedEnv, state.ManagedTop);
        AtomicFile.WriteAllText(file, merged.ToJsonString());
        state.ManagedEnv = [.. overlay.Env.Keys];
        state.ManagedTop = [.. overlay.Top.Keys];
        state.Save(dir);

        var login = entry.Home || Providers.Of(entry) == Providers.Login;
        return (dir, login && !File.Exists(Path.Combine(dir, ".claude.json")));
    }
}
