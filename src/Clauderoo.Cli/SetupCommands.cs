using Clauderoo.Core;

namespace Clauderoo.Cli;

/// <summary>Profile setup commands: startdir, clone (and add --from) and home.</summary>
public static class SetupCommands
{
    public static readonly string[] Names = ["startdir", "clone", "home"];

    public static int StartDir(List<string> pos, TextWriter output, ProfileManager pm)
    {
        const string usage = "Usage: clauderoo startdir <name> [<dir> | --clear]";
        if (pos.Count is < 1 or > 2) throw new ClauderooException(usage);
        var name = pos[0];
        if (pos.Count == 1)
        {
            var current = pm.Get(name).StartDir;
            output.WriteLine(current != null
                ? $"{name} starts in {current}."
                : $"{name} has no start folder: sessions started from the tray open in your user folder.");
            return 0;
        }
        if (pos[1] == "--clear")
        {
            pm.SetStartDir(name, null);
            output.WriteLine($"Cleared {name}'s start folder.");
            return 0;
        }
        if (pos[1].StartsWith("--")) throw new ClauderooException(usage);
        pm.SetStartDir(name, pos[1]);
        output.WriteLine($"{name} now starts in {pm.Get(name).StartDir}.");
        return 0;
    }

    /// <summary>`clone &lt;source&gt; &lt;new&gt;`, and `add &lt;new&gt; --from &lt;source&gt;`.</summary>
    public static int Clone(string source, string name, TextWriter output, TextWriter err, ProfileManager pm)
    {
        var r = pm.Clone(source, name);
        foreach (var n in r.Report.Notices) output.WriteLine(n);
        foreach (var p in r.Report.Problems) err.WriteLine(p);
        output.WriteLine($"Created '{name}' from '{source}' with its provider, environment variables, owned items and start folder.");
        output.WriteLine("Not copied: the sign-in, conversations, API key or token, and folder mappings. Owned items start empty.");
        if (r.SecretToSet is { } secret)
            output.WriteLine(secret == Providers.ApiKey
                ? $"'{source}' uses an API key: '{name}' uses the Claude login until you set its key with 'cr provider {name} apikey'."
                : $"'{source}' uses a Claude token: '{name}' uses the Claude login until you set its token with 'cr provider {name} token'.");
        else if (Providers.Of(pm.Get(name)) == Providers.Login)
            output.WriteLine($"Run 'clauderoo start {name}' and sign in.");
        return 0;
    }

    public static int Home(List<string> pos, TextWriter output, TextWriter err, ProfileManager pm)
    {
        if (pos.Count > 1) throw new ClauderooException("Usage: clauderoo home [<path>]");
        if (pos.Count == 0)
        {
            var cfg = pm.Config;
            output.WriteLine($"Home folder: {cfg.HomeDir}{(Directory.Exists(cfg.HomeDir) ? "" : " (missing)")}");
            output.WriteLine($"Home .claude.json: {cfg.HomeJson}{(File.Exists(cfg.HomeJson) ? "" : " (missing)")}");
            return 0;
        }
        var r = pm.SetHome(pos[0]);
        foreach (var n in r.Report.Notices) output.WriteLine(n);
        foreach (var p in r.Report.Problems) err.WriteLine("warning: " + p);
        output.WriteLine($"The home profile ({pm.Config.HomeProfileName}) now uses {r.HomeDir}. Every profile was synced again.");
        if (!r.HomeJsonExists)
            err.WriteLine($"warning: {r.HomeJson} doesn't exist yet, so MCP servers aren't shared until Claude creates it.");
        return 0;
    }
}
