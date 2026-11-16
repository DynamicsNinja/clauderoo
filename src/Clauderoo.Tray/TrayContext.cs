using System.Diagnostics;
using Clauderoo.Core;
using Timer = System.Windows.Forms.Timer;

namespace Clauderoo.Tray;

sealed class TrayContext : ApplicationContext
{
    private static readonly string[] SharedFileNames = ["settings.json", "CLAUDE.md", "keybindings.json"];

    private readonly ClauderooPaths _paths = ClauderooPaths.ForCurrentUser();
    private readonly ProfileManager _pm;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Timer _debounce = new() { Interval = 2000 };
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Icon _appIcon;
    private readonly SynchronizationContext _ui;
    private string? _watchedKey;

    public TrayContext()
    {
        if (SynchronizationContext.Current == null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;
        _pm = new ProfileManager(_paths, new WindowsEnvStore(), () => DateTime.Now);
        _appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();

        _menu.Opening += (_, _) => RebuildMenu();
        _icon = new NotifyIcon { Icon = _appIcon, Text = "clauderoo", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) Guard(StartDefault); };

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Guard(() =>
            {
                var cfg = _pm.Config;
                EnsureWatchers(cfg);
                Show(new Syncer(cfg, _paths, () => DateTime.Now).SyncAll());
            });
        };

        try
        {
            EnsureWatchers(_pm.Config);
        }
        catch (Exception e)
        {
            Balloon(e.Message, warning: true);
        }
    }

    // (Re)creates the watchers when there are none or the config's home paths changed.
    private void EnsureWatchers(ClauderooConfig cfg)
    {
        var key = cfg.HomeDir + "|" + cfg.HomeJson;
        if (_watchers.Count > 0 && key == _watchedKey) return;

        DisposeWatchers();
        _watchedKey = key;

        if (Directory.Exists(cfg.HomeDir))
            AddWatcher(cfg.HomeDir, "*", n => SharedFileNames.Contains(n, StringComparer.OrdinalIgnoreCase));

        var jsonDir = Path.GetDirectoryName(cfg.HomeJson);
        if (!string.IsNullOrEmpty(jsonDir) && Directory.Exists(jsonDir))
            AddWatcher(jsonDir, ".claude.json", _ => true);
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    private void AddWatcher(string dir, string filter, Func<string, bool> accept)
    {
        var w = new FileSystemWatcher(dir)
        {
            Filter = filter,
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        w.Changed += (_, e) => { if (e.Name != null && accept(e.Name)) Kick(); };
        w.Created += (_, e) => { if (e.Name != null && accept(e.Name)) Kick(); };
        w.Deleted += (_, e) => { if (e.Name != null && accept(e.Name)) Kick(); };
        w.Renamed += (_, e) => { if (e.Name != null && accept(e.Name)) Kick(); };
        w.Error += (_, _) => OnWatcherError();
        w.EnableRaisingEvents = true;
        _watchers.Add(w);
    }

    // Overflow or a deleted watched dir: drop the watchers (recreated by EnsureWatchers) and resync.
    private void OnWatcherError()
    {
        try
        {
            _ui.Post(_ => { DisposeWatchers(); _watchedKey = null; Kick(); }, null);
        }
        catch (Exception) { /* shutting down */ }
    }

    // Watcher events arrive on thread-pool threads; post to the UI thread to (re)start the timer.
    private void Kick()
    {
        try
        {
            _ui.Post(_ => { _debounce.Stop(); _debounce.Start(); }, null);
        }
        catch (Exception) { /* shutting down */ }
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        IReadOnlyList<ProfileInfo> profiles;
        try
        {
            EnsureWatchers(_pm.Config);
            profiles = _pm.List();
        }
        catch (Exception e)
        {
            Balloon(e.Message, warning: true);
            _menu.Items.Add("Exit", null, (_, _) => ExitThread());
            return;
        }

        foreach (var p in profiles)
        {
            var text = (p.IsDefault ? "● " : "") + p.Name + (p.Email != null ? $"  ({p.Email})" : "");
            var item = new ToolStripMenuItem(text.Replace("&", "&&"));
            var name = p.Name;
            item.DropDownItems.Add("Start session", null, (_, _) => Guard(() => StartSession(name, null)));
            item.DropDownItems.Add("Start session in folder…", null, (_, _) => Guard(() => StartInFolder(name)));
            item.DropDownItems.Add("Set as default", null, (_, _) => Guard(() => _pm.SetDefault(name)));
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("New profile…", null, (_, _) => Guard(NewProfile));
        _menu.Items.Add("Open profiles folder", null, (_, _) => Guard(() =>
        {
            Directory.CreateDirectory(_paths.ProfilesDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.ProfilesDir}\"") { UseShellExecute = true });
        }));
        var auto = new ToolStripMenuItem("Start with Windows");
        try { auto.Checked = AutoStart.IsOn(); } catch (Exception) { }
        auto.Click += (_, _) => Guard(() => AutoStart.Set(!auto.Checked, Environment.ProcessPath!));
        _menu.Items.Add(auto);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    private void StartDefault()
    {
        var cfg = _pm.Config;
        var name = cfg.DefaultProfile ?? cfg.HomeProfileName
                   ?? throw new ClauderooException("No default profile is set.");
        StartSession(name, null);
    }

    private void StartInFolder(string name)
    {
        using var dlg = new FolderBrowserDialog { Description = $"Start a '{name}' session in folder" };
        if (dlg.ShowDialog() == DialogResult.OK) StartSession(name, dlg.SelectedPath);
    }

    private void StartSession(string name, string? dir)
    {
        var cfg = _pm.Config;
        dir ??= _pm.Get(name).StartDir
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Launcher.Start(Launcher.NewWindow(cfg, _paths, name, dir, [], Launcher.WtAvailable()));
    }

    private void NewProfile()
    {
        var name = InputDialog.Ask("New profile", "Profile name (a-z, 0-9, -):");
        if (string.IsNullOrEmpty(name)) return;
        Show(_pm.Add(name));
        Balloon($"Profile '{name}' created. Run 'claude' in it to log in.", warning: false);
    }

    private void Show(SyncReport report)
    {
        foreach (var n in report.Notices) Balloon(n, warning: false);
        foreach (var p in report.Problems) Balloon(p, warning: true);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { Balloon(e.Message, warning: true); }
    }

    private void Balloon(string text, bool warning)
    {
        try
        {
            _icon.ShowBalloonTip(5000, "clauderoo", text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeWatchers();
            _debounce.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _appIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
