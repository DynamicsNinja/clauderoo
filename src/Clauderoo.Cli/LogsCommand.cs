using Clauderoo.Core;

namespace Clauderoo.Cli;

/// <summary><c>cr logs [--path] [--tail N]</c>: shows the end of the shared clauderoo log, or where it is.</summary>
internal static class LogsCommand
{
    public const string Usage = "clauderoo logs [--path] [--tail N]";
    private const int DefaultTail = 50;

    public static int Run(List<string> pos, TextWriter output, ClauderooPaths paths)
    {
        var showPath = false;
        var tail = DefaultTail;
        for (var i = 0; i < pos.Count; i++)
        {
            switch (pos[i])
            {
                case "--path":
                    showPath = true;
                    break;
                case "--tail":
                    if (i + 1 >= pos.Count
                        || !int.TryParse(pos[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out tail)
                        || tail < 1)
                        throw new ClauderooException("--tail needs a whole number of lines, 1 or more.");
                    break;
                default:
                    throw new ClauderooException($"Usage: {Usage}");
            }
        }

        var file = AppLog.PathFor(paths);
        if (showPath)
        {
            output.WriteLine(file);
            return 0;
        }
        var lines = RollingLog.Tail(file, tail);
        if (lines.Count == 0)
        {
            output.WriteLine($"Nothing logged yet. The log will be at {file}");
            return 0;
        }
        foreach (var line in lines) output.WriteLine(line);
        return 0;
    }
}
