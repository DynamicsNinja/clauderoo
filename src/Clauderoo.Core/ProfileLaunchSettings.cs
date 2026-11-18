namespace Clauderoo.Core;

public partial class ProfileManager
{
    /// <summary>
    /// The Windows Terminal fragment kept up to date by <see cref="SyncAll"/> when it exists (see WtFragment).
    /// Null (the default) leaves Windows Terminal alone; the apps set it to <see cref="WtFragment.DefaultFile"/>.
    /// </summary>
    public string? WtFragmentFile { get; set; }

    /// <summary>Rewrites an existing Windows Terminal fragment to match the profiles. Best effort.</summary>
    internal bool RefreshWtFragment(ClauderooConfig cfg) => WtFragment.RefreshIfPresent(WtFragmentFile, cfg, paths, HelperExe);

    /// <summary>Sets where new sessions open and which shell runs them; a null argument leaves that setting as it is.</summary>
    public void SetLaunchSettings(string? terminal, string? shell)
    {
        if (terminal is not null && !TerminalChoice.Terminals.Contains(terminal))
            throw new ClauderooException($"Unknown terminal '{terminal}'. Known: {string.Join(", ", TerminalChoice.Terminals)}.");
        if (shell is not null && !TerminalChoice.Shells.Contains(shell))
            throw new ClauderooException($"Unknown shell '{shell}'. Known: {string.Join(", ", TerminalChoice.Shells)}.");
        var cfg = Config;
        if (terminal is not null) cfg.Terminal = terminal;
        if (shell is not null) cfg.Shell = shell;
        ConfigStore.Save(paths, cfg);
        // The fragment's profiles run the configured shell.
        RefreshWtFragment(cfg);
    }
}
