using System.Diagnostics;
using System.Runtime.InteropServices;
using Clauderoo.Core;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace Clauderoo.Tray;

sealed class TrayContext : ApplicationContext
{
    private static readonly string[] SharedFileNames = ["settings.json", "CLAUDE.md", "keybindings.json"];
    private static readonly TimeSpan NoticeGap = TimeSpan.FromSeconds(10);

    private readonly ClauderooPaths _paths = ClauderooPaths.ForCurrentUser();
    private readonly ProfileManager _pm;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Timer _debounce = new() { Interval = 2000 };
    private readonly Timer _runningTimer = new() { Interval = 15000 };
    private readonly Timer _clickTimer = new() { Interval = Math.Max(100, SystemInformation.DoubleClickTime) };
    private readonly Timer _noticeTimer = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SynchronizationContext _ui;
    private readonly OwnerWindow _owner;
    private readonly ProblemTracker _alerted = new();
    private readonly EventWaitHandle _manageSignal;
    private string? _watchedKey;
    private ProfilesForm? _profilesForm;
    private ProblemsForm? _problemsForm;

    // Current state: kept between syncs, shown by the icon, tooltip, menu and Problems window.
    private IReadOnlyList<Problem> _problems = [];
    private IReadOnlyList<ProfileInfo> _profiles = [];
    private IReadOnlyDictionary<string, int> _running = new Dictionary<string, int>();
    private (string Headline, string Detail, bool Exists)? _configError;

    private Icon? _trayIcon;
    private (AppIconState State, bool HighContrast, Size Size)? _trayIconKey;

    private readonly List<string> _pendingNotices = [];
    private DateTime _lastBalloon = DateTime.MinValue;
    private bool _balloonOpensProblems;

    public TrayContext(bool openProfiles = false)
    {
        if (SynchronizationContext.Current == null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;
        _pm = new ProfileManager(_paths, new WindowsEnvStore(), () => DateTime.Now);
        _owner = new OwnerWindow(() => Guard(() => UpdateStatus(forceIcon: true)));

        _menu.Opening += (_, _) => RebuildMenu();
        _icon = new NotifyIcon { Text = "clauderoo", ContextMenuStrip = _menu };
        UpdateStatus(forceIcon: true);
        _icon.Visible = true;

        // Single left click opens the menu (after the double-click time, so a double-click doesn't flash it);
        // double-click starts the default profile, which is the bold item in the menu.
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { _clickTimer.Stop(); _clickTimer.Start(); } };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); ShowMenuAtCursor(); };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _clickTimer.Stop();
            Guard(StartDefault);
        };
        _icon.BalloonTipClicked += (_, _) => { if (_balloonOpensProblems) Guard(ShowProblems); };
        _icon.BalloonTipClosed += (_, _) => _balloonOpensProblems = false;

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Guard(FullSync);
        };
        _runningTimer.Tick += (_, _) => Guard(() => { RefreshRunning(); UpdateStatus(); });
        _runningTimer.Start();
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); FlushNotices(); };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // `clauderoow.exe --manage` from another process arrives here (on a background thread).
        _manageSignal = ManageSignal.Listen(() => Post(() => Guard(ShowProfiles)));
        if (openProfiles) Post(() => Guard(ShowProfiles));

        // Learn the current problems right away instead of waiting for the first change.
        Post(() => Guard(FullSync));
    }

    // ---------- state ----------

    /// <summary>Loads the config, or records (and shows once) why it can't be loaded.</summary>
    private ClauderooConfig? LoadConfig()
    {
        try
        {
            var cfg = _pm.Config;
            _configError = null;
            return cfg;
        }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException)
        {
            var exists = File.Exists(_paths.ConfigFile);
            var first = _configError == null;
            var (headline, detail) = TrayText.ConfigProblem(exists);
            _configError = (headline, detail, exists);
            _profiles = [];
            _problems = [];
            _problemsForm?.SetProblems(_problems);
            UpdateStatus();
            if (first) Balloon($"{headline}. Click the tray icon for options.", ToolTipIcon.Warning);
            return null;
        }
    }

    /// <summary>Syncs every profile and replaces the problem list with what it found.</summary>
    private void FullSync()
    {
        if (LoadConfig() is not { } cfg) return;
        EnsureWatchers(cfg);
        var report = _pm.SyncAll();
        RefreshProfiles();
        SetProblems(report.ProblemItems);
        QueueNotices(report.Notices);
    }

    private void SetProblems(IReadOnlyList<Problem> problems)
    {
        _problems = problems;
        var fresh = _alerted.NewOnly(problems.Select(p => p.Detail));
        _problemsForm?.SetProblems(problems);
        UpdateStatus();
        if (fresh.Count > 0)
            Balloon(TrayText.ProblemBalloon(problems), ToolTipIcon.Warning, opensProblems: true);
    }

    private void RefreshProfiles()
    {
        try { _profiles = _pm.List(); }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException) { LoadConfig(); }
        RefreshRunning();
        UpdateStatus();
    }

    private void RefreshRunning()
    {
        try { _running = TrayMenuState.CountRunning(_pm); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep the last known counts */ }
    }

    /// <summary>Icon badge and tooltip from the current state.</summary>
    private void UpdateStatus(bool forceIcon = false)
    {
        var total = _running.Values.Sum();
        var state = _configError != null ? AppIconState.Error : TrayText.IconState(_problems, total);
        var key = (state, SystemInformation.HighContrast, SystemInformation.SmallIconSize);
        if (forceIcon || _trayIconKey != key)
        {
            var old = _trayIcon;
            _trayIcon = AppIcons.Load(key.state, key.SmallIconSize, key.HighContrast);
            _trayIconKey = key;
            _icon.Icon = _trayIcon;
            old?.Dispose();
        }

        var text = _configError is { } err
            ? TrayText.Cut($"clauderoo — {err.Headline}. Click for options.")
            : TrayText.Tooltip(_profiles.FirstOrDefault(p => p.IsDefault) ?? _profiles.FirstOrDefault(p => p.IsHome),
                _problems.Count, total);
        if (_icon.Text != text) _icon.Text = text;
    }

    // ---------- menu ----------

    private void ShowMenuAtCursor()
    {
        var pos = Cursor.Position;
        var area = Screen.FromPoint(pos).WorkingArea;
        var above = pos.Y > area.Top + area.Height / 2;
        var left = pos.X > area.Left + area.Width / 2;
        var direction = above
            ? left ? ToolStripDropDownDirection.AboveLeft : ToolStripDropDownDirection.AboveRight
            : left ? ToolStripDropDownDirection.BelowLeft : ToolStripDropDownDirection.BelowRight;
        // The menu closes on an outside click only when our process owns the foreground (as NotifyIcon does itself).
        SetForegroundWindow(_owner.Handle);
        _menu.Show(pos, direction);
    }

    private void RebuildMenu()
    {
        var actions = new TrayMenuActions
        {
            Start = name => Guard(() => StartSession(name, null)),
            StartInFolder = name => Guard(() => StartInFolder(name)),
            SetDefault = name => Guard(() => SetDefault(name)),
            ShowProblems = () => Guard(ShowProblems),
            ShowProfiles = () => Guard(ShowProfiles),
            NewProfile = () => Guard(NewProfile),
            OpenProfilesFolder = () => Guard(OpenProfilesFolder),
            SetAutoStart = on => Guard(() => AutoStart.Set(on, AutoStart.ResolveExe(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.ProcessPath!))),
            SetUp = () => Guard(SetUp),
            Retry = () => Guard(FullSync),
            Exit = ExitThread,
        };

        if (LoadConfig() is { } cfg)
        {
            try { EnsureWatchers(cfg); } catch (Exception e) when (e is IOException or ArgumentException) { }
            RefreshProfiles();
        }
        if (_configError is { } err)
        {
            TrayMenu.BuildFallback(_menu, err.Headline, err.Detail, offerSetUp: !err.Exists, actions);
            return;
        }

        var autoOn = false;
        try { autoOn = AutoStart.IsOn(); } catch (Exception) { }
        TrayMenu.Build(_menu, new TrayMenuState(_profiles, _problems, _running, autoOn), actions);
    }

    // ---------- actions ----------

    // Single Profiles window: opened on first use, brought to the front afterwards.
    private void ShowProfiles()
    {
        if (_profilesForm is { IsDisposed: false })
        {
            _profilesForm.ShowAndActivate(reload: true);
            return;
        }
        if (LoadConfig() == null)
        {
            Balloon($"{_configError!.Value.Headline}. {_configError.Value.Detail}", ToolTipIcon.Warning);
            return;
        }
        var form = new ProfilesForm(_pm, name => StartSession(name, null));
        form.FormClosed += (_, _) =>
        {
            if (_profilesForm == form) _profilesForm = null;
            Kick(); // pick up what was changed there
        };
        _profilesForm = form;
        form.ShowAndActivate(reload: false);
    }

    private void ShowProblems()
    {
        var form = _problemsForm;
        if (form is not { IsDisposed: false })
        {
            form = new ProblemsForm(Fix, () => Guard(ShowProfiles));
            var created = form;
            form.FormClosed += (_, _) => { if (_problemsForm == created) _problemsForm = null; };
            _problemsForm = form;
        }
        form.SetProblems(_problems);
        form.ShowAndActivate();
    }

    private void Fix(Problem p)
    {
        var report = _pm.Fix(p); // throws with a message the window shows
        QueueNotices(report.Notices, now: true);
        FullSync();
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

    // The new terminal runs `clauderoo run`, which records the session itself; the running count follows within 15 s.
    private void StartSession(string name, string? dir)
    {
        var cfg = _pm.Config;
        dir ??= _pm.Get(name).StartDir
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var _ = Launcher.Start(Launcher.NewWindow(cfg, _paths, name, dir, [], Launcher.WtAvailable()));
    }

    private void SetDefault(string name)
    {
        var report = _pm.SetDefault(name);
        RefreshProfiles();
        QueueNotices([$"Default is now {name}. New terminals will use it.", .. report.Notices], now: true);
        Kick();
    }

    private void NewProfile()
    {
        var name = InputDialog.Ask("New profile", "Profile name (a-z, 0-9, -):")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return;
        var report = _pm.Add(name);
        if (_profilesForm is { IsDisposed: false } f) f.RefreshList();
        RefreshProfiles();
        QueueNotices([$"Profile '{name}' created. Start a session in it to sign in.", .. report.Notices], now: true);
        Kick();
    }

    private void OpenProfilesFolder()
    {
        Directory.CreateDirectory(_paths.ProfilesDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.ProfilesDir}\"") { UseShellExecute = true })?.Dispose();
    }

    private void SetUp()
    {
        _pm.Init();
        _configError = null;
        Balloon("clauderoo is set up. Your current Claude login is the 'personal' profile.", ToolTipIcon.Info);
        FullSync();
    }

    // ---------- watchers ----------

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
    private void OnWatcherError() => Post(() => { DisposeWatchers(); _watchedKey = null; Kick(); });

    // Watcher events arrive on thread-pool threads; post to the UI thread to (re)start the timer.
    private void Kick() => Post(() => { _debounce.Stop(); _debounce.Start(); });

    private void Post(Action action)
    {
        try { _ui.Post(_ => action(), null); }
        catch (Exception) { /* shutting down */ }
    }

    // ---------- display changes ----------

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color or UserPreferenceCategory.General)
            Post(() => Guard(() => UpdateStatus())); // reloads the icon when High Contrast changed
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Post(() => Guard(() => UpdateStatus(forceIcon: true)));

    // ---------- balloons ----------

    /// <summary>
    /// Notices (backups kept, model updated…) need no action: combined into one balloon, at most one every 10 s.
    /// <paramref name="now"/>: the user just did something, so answer right away.
    /// </summary>
    private void QueueNotices(IEnumerable<string> notices, bool now = false)
    {
        _pendingNotices.AddRange(notices);
        if (_pendingNotices.Count == 0) return;
        if (now) { FlushNotices(force: true); return; }
        var wait = _lastBalloon + NoticeGap - DateTime.UtcNow;
        if (wait <= TimeSpan.Zero) FlushNotices();
        else if (!_noticeTimer.Enabled)
        {
            _noticeTimer.Interval = Math.Max(1, (int)wait.TotalMilliseconds);
            _noticeTimer.Start();
        }
    }

    private void FlushNotices(bool force = false)
    {
        if (_pendingNotices.Count == 0) return;
        if (!force && DateTime.UtcNow - _lastBalloon < NoticeGap) { QueueNotices([]); return; }
        var text = TrayText.Notices(_pendingNotices);
        _pendingNotices.Clear();
        Balloon(text, ToolTipIcon.Info);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { Balloon(e.Message, ToolTipIcon.Warning); }
    }

    private void Balloon(string text, ToolTipIcon kind, bool opensProblems = false)
    {
        try
        {
            _balloonOpensProblems = opensProblems;
            _lastBalloon = DateTime.UtcNow;
            _icon.ShowBalloonTip(5000, "clauderoo", TrayText.Cut(text, 250), kind);
        }
        catch (Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            DisposeWatchers();
            _profilesForm?.Dispose();
            _problemsForm?.Dispose();
            _manageSignal.Dispose();
            _debounce.Dispose();
            _runningTimer.Dispose();
            _clickTimer.Dispose();
            _noticeTimer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _trayIcon?.Dispose();
            _owner.DestroyHandle();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// An invisible top-level window: brought to the foreground before the menu is shown from a left click (so an
    /// outside click closes it), and told about DPI changes (WM_DPICHANGED) so the tray icon is reloaded.
    /// </summary>
    private sealed class OwnerWindow : NativeWindow
    {
        private const int WmDpiChanged = 0x02E0;
        private readonly Action _dpiChanged;

        public OwnerWindow(Action dpiChanged)
        {
            _dpiChanged = dpiChanged;
            CreateHandle(new CreateParams { Caption = "clauderoo-tray-owner" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDpiChanged) _dpiChanged();
            base.WndProc(ref m);
        }
    }
}
