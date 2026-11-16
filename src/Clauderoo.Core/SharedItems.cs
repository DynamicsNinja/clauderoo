namespace Clauderoo.Core;

public static class SharedItems
{
    public static readonly string[] Dirs = ["plugins", "skills", "hooks", "agents", "commands", "output-styles"];
    public static readonly string[] Files = ["settings.json", "CLAUDE.md", "keybindings.json"];
    public const string McpServers = "mcpServers";

    public static bool IsDir(string item) => Dirs.Contains(item);

    public static bool IsKnown(string item) => IsDir(item) || Files.Contains(item) || item == McpServers;
}
