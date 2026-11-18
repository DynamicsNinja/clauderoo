using System.Text;
using Clauderoo.Core;

namespace Clauderoo.Cli;

/// <summary>
/// Tab completion for PowerShell and Bash. The scripts are generated from <see cref="Commands.KnownCommands"/> and
/// <see cref="Words"/>, so they can't drift from the real command list; profile names come from the hidden
/// `cr __complete profiles` at completion time (it reads config.json and changes nothing).
/// </summary>
public static class Completion
{
    /// <summary>Commands that complete a profile name as an argument.</summary>
    public static readonly HashSet<string> TakesProfile =
    [
        "start", "run", "map", "default", "own", "share", "remove", "rename", "provider", "env", "check", "apikey",
        "usage", "sessions", "resume", "export",
    ];

    /// <summary>The flags and fixed words each visible command completes (every command has an entry, even if empty).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Words = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["init"] = ["--name", "--home"],
        ["add"] = [],
        ["list"] = ["--json"],
        ["start"] = ["--tab", "--isolated"],
        ["run"] = ["--tab", "--isolated"],
        ["map"] = [],
        ["unmap"] = [],
        ["maps"] = ["--json"],
        ["which"] = ["--json"],
        ["sessions"] = ["--json"],
        ["resume"] = ["--as", "--isolated", "--tab"],
        ["default"] = ["--clear"],
        ["own"] = [.. SharedItems.Dirs, .. SharedItems.Files, SharedItems.McpServers],
        ["share"] = [.. SharedItems.Dirs, .. SharedItems.Files, SharedItems.McpServers],
        ["remove"] = ["--force"],
        ["restore"] = [],
        ["rename"] = ["--force"],
        ["provider"] = ["login", "vertex", "apikey", "token", "--project", "--region", "--sonnet", "--opus", "--haiku"],
        ["env"] = ["set", "unset", "--json"],
        ["export"] = ["--no-env", "--force"],
        ["import"] = ["--force"],
        ["check"] = ["--live", "--json"],
        ["apikey"] = [],
        ["usage"] = ["--json"],
        ["auto"] = ["on", "off", "--threshold", "--order", "--once", "--json", "--schedule", "--every"],
        ["status"] = ["--json"],
        ["mcp"] = ["install"],
        ["tray"] = [],
        ["manage"] = [],
        ["autostart"] = ["on", "off"],
        ["install"] = [],
        ["version"] = [],
        ["upgrade"] = ["--check"],
        ["uninstall"] = ["--yes", "--purge"],
        ["doctor"] = ["--json"],
        ["completion"] = ["powershell", "bash"],
    };

    /// <summary>The commands offered at the first position: every known command except the hidden ones, plus help.</summary>
    public static IEnumerable<string> VisibleCommands =>
        Commands.KnownCommands.Where(c => !c.StartsWith("__", StringComparison.Ordinal)).Append("help");

    private static readonly string[] Names = ["cr", "clauderoo", "cr.exe", "clauderoo.exe"];

    public static string PowerShell()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# clauderoo tab completion for PowerShell. Add to your profile:  cr completion powershell | Out-String | Invoke-Expression");
        sb.AppendLine("$__crCommands = @(" + string.Join(", ", VisibleCommands.Select(Ps)) + ")");
        sb.AppendLine("$__crWords = @{");
        foreach (var (cmd, words) in Words)
            sb.AppendLine($"    {Ps(cmd)} = @({string.Join(", ", words.Select(Ps))})");
        sb.AppendLine("}");
        sb.AppendLine("$__crProfileCommands = @(" + string.Join(", ", TakesProfile.Order(StringComparer.Ordinal).Select(Ps)) + ")");
        sb.AppendLine("Register-ArgumentCompleter -Native -CommandName " + string.Join(", ", Names.Select(Ps)) + " -ScriptBlock {");
        sb.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        sb.AppendLine("    $words = @($commandAst.CommandElements | ForEach-Object { $_.ToString() })");
        sb.AppendLine("    $index = $words.Count");
        sb.AppendLine("    if ($wordToComplete -ne '') { $index = $words.Count - 1 }");
        sb.AppendLine("    if ($index -le 1) { $candidates = $__crCommands }");
        sb.AppendLine("    else {");
        sb.AppendLine("        $cmd = $words[1]");
        sb.AppendLine("        $candidates = @($__crWords[$cmd])");
        sb.AppendLine("        if ($__crProfileCommands -contains $cmd) {");
        sb.AppendLine("            $exe = $words[0]");
        sb.AppendLine("            $candidates += @(& $exe __complete profiles 2>$null)");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    $candidates | Where-Object { $_ -and $_ -like \"$wordToComplete*\" } | ForEach-Object {");
        sb.AppendLine("        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string Bash()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# clauderoo tab completion for Bash (Git Bash). Add to ~/.bashrc:  eval \"$(cr completion bash | tr -d '\\r')\"");
        sb.AppendLine("_clauderoo() {");
        sb.AppendLine("    local cur=\"${COMP_WORDS[COMP_CWORD]}\"");
        sb.AppendLine("    if [ \"$COMP_CWORD\" -le 1 ]; then");
        sb.AppendLine($"        COMPREPLY=( $(compgen -W \"{string.Join(" ", VisibleCommands)}\" -- \"$cur\") )");
        sb.AppendLine("        return");
        sb.AppendLine("    fi");
        sb.AppendLine("    local words=\"\"");
        sb.AppendLine("    case \"${COMP_WORDS[1]}\" in");
        foreach (var (cmd, words) in Words)
            if (words.Length > 0) sb.AppendLine($"        {cmd}) words=\"{string.Join(" ", words)}\" ;;");
        sb.AppendLine("    esac");
        sb.AppendLine("    case \"${COMP_WORDS[1]}\" in");
        sb.AppendLine("        " + string.Join("|", TakesProfile.Order(StringComparer.Ordinal)) +
                      ") words=\"$words $(\"${COMP_WORDS[0]}\" __complete profiles 2>/dev/null | tr -d '\\r')\" ;;");
        sb.AppendLine("    esac");
        sb.AppendLine("    COMPREPLY=( $(compgen -W \"$words\" -- \"$cur\") )");
        sb.AppendLine("}");
        sb.AppendLine("complete -F _clauderoo " + string.Join(" ", Names));
        return sb.ToString();
    }

    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
}
