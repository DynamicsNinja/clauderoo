namespace Clauderoo.Core;

/// <summary>
/// The one-time command that registers clauderoo's MCP server with Claude Code. clauderoo only shows it (CLI
/// `cr mcp install` and the window's "Use from Claude…"): it never runs it and never writes ~/.claude.json.
/// </summary>
public static class McpRegistration
{
    public const string Explanation = "Run this once in a terminal. Every profile gets it through the shared MCP servers.";

    /// <summary>The command for a given cr.exe path.</summary>
    public static string Command(string crExe) => $"claude mcp add --scope user clauderoo -- \"{crExe}\" mcp";

    /// <summary>cr.exe next to the exe <see cref="HelperExePath"/> picks (the installed one when present).</summary>
    public static string CrExe(string localAppData, string fallbackExe) =>
        Path.Combine(Path.GetDirectoryName(HelperExePath.Resolve(localAppData, fallbackExe))!, Installer.ShortAlias);

    public static string CrExe() => Path.Combine(Path.GetDirectoryName(HelperExePath.Resolve())!, Installer.ShortAlias);

    /// <summary>The command for this machine's cr.exe.</summary>
    public static string Command() => Command(CrExe());
}
