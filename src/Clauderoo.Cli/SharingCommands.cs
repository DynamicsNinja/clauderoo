using Clauderoo.Core;

namespace Clauderoo.Cli;

/// <summary>exclude, include and shared: what each profile gets from the home profile.</summary>
internal static class SharingCommands
{
    public static readonly string[] Names = ["exclude", "include", "shared"];

    public const string Usage =
        "  exclude <name> [<item>/<entry>]     leave one entry of a shared item out of a profile, e.g. skills/foo,\n" +
        "                                      mcpServers/github or plugins/name@marketplace (no entry: list them)\n" +
        "  include <name> <item>/<entry>       share an excluded entry with the profile again\n" +
        "  shared [list]                       list the shared files and folders\n" +
        "  shared add <name> [--dir|--file]    share another file or folder of the home profile with every profile\n" +
        "  shared remove <name>                stop sharing an item added with 'shared add'";

    public static int Run(string cmd, List<string> pos, bool json, TextWriter output, TextWriter err, ProfileManager pm)
    {
        switch (cmd)
        {
            case "exclude" when pos.Count == 1:
            {
                var ex = pm.Get(pos[0]).Exclude; // unknown profile -> error
                if (ex.Count == 0) output.WriteLine($"{pos[0]} gets everything shared from the home profile.");
                foreach (var x in ex) output.WriteLine(x);
                return 0;
            }
            case "exclude":
            {
                var a = Need(pos, 2, "clauderoo exclude <name> <item>/<entry>");
                Print(pm.Exclude(a[0], a[1]), output, err);
                output.WriteLine(a[1].StartsWith(SharedItems.Plugins + "/", StringComparison.Ordinal)
                    ? $"{a[0]} now has {a[1]} turned off."
                    : $"{a[0]} now leaves out {a[1]}.");
                return 0;
            }
            case "include":
            {
                var a = Need(pos, 2, "clauderoo include <name> <item>/<entry>");
                Print(pm.Include(a[0], a[1]), output, err);
                output.WriteLine($"{a[0]} now gets {a[1]} from the home profile again.");
                return 0;
            }
            default:
                return Shared(pos, json, output, err, pm);
        }
    }

    private static int Shared(List<string> pos, bool json, TextWriter output, TextWriter err, ProfileManager pm)
    {
        var sub = pos.Count == 0 ? "list" : pos[0];
        if (json && sub != "list") throw new ClauderooException("Only 'shared list' supports --json.");
        switch (sub)
        {
            case "list":
            {
                if (pos.Count > 1) throw new ClauderooException("Usage: clauderoo shared list [--json]");
                var items = pm.SharedList();
                if (json)
                {
                    JsonOut.Write(output, new
                    {
                        Items = items.Select(i => new
                        {
                            i.Name, Kind = i.Kind.ToString().ToLowerInvariant(), i.BuiltIn, i.InHome,
                        }),
                    });
                    return 0;
                }
                var w = items.Max(i => i.Name.Length);
                foreach (var i in items)
                {
                    var kind = i.Kind switch { SharedKind.Folder => "folder", SharedKind.File => "file", _ => "MCP servers" };
                    var tail = (i.BuiltIn ? "" : "added") + (i.InHome ? "" : (i.BuiltIn ? "" : ", ") + "not in home yet");
                    output.WriteLine($"{i.Name.PadRight(w)}  {kind,-11}  {tail}".TrimEnd());
                }
                return 0;
            }
            case "add":
            {
                var dir = pos.Remove("--dir");
                var file = pos.Remove("--file");
                if (dir && file) throw new ClauderooException("Use --dir or --file, not both.");
                var a = Need(pos, 2, "clauderoo shared add <name> [--dir|--file]");
                Print(pm.AddShared(a[1], dir ? SharedKind.Folder : file ? SharedKind.File : null), output, err);
                output.WriteLine($"Every profile now shares {a[1]} from the home profile.");
                return 0;
            }
            case "remove":
            {
                var a = Need(pos, 2, "clauderoo shared remove <name>");
                Print(pm.RemoveShared(a[1]), output, err);
                output.WriteLine($"{a[1]} is no longer shared.");
                return 0;
            }
            default:
                throw new ClauderooException("Usage: clauderoo shared [list | add <name> [--dir|--file] | remove <name>]");
        }
    }

    private static void Print(SyncReport r, TextWriter output, TextWriter err)
    {
        foreach (var n in r.Notices) output.WriteLine(n);
        foreach (var p in r.Problems) err.WriteLine(p);
    }

    private static List<string> Need(List<string> pos, int count, string usage) =>
        pos.Count == count ? pos : throw new ClauderooException($"Usage: {usage}");
}
