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
    // Usage: one long-lived client (and its handler) for the life of the tray, polled on a background task.
    private readonly HttpClientHandler _usageHandler = UsageClient.CreateHandler();
    private readonly UsageClient _usageClient;
    private readonly UsagePoller _poller;
    private readonly UsageView _usage = new();
    private string? _watchedKey;
    private ProfilesForm? _profilesForm;
    private ProblemsForm? _problemsForm;

    // Current state: kept between syncs, shown by the icon, tooltip, menu and Problems window.
    private IReadOnlyList<Problem> _problems = [];
    private IReadOnlyList<ProfileInfo> _profiles = [];
    private IReadOnlyDictionary<string, int> _running = new Dictionary<string, int>();
    private ConfigError? _configError;
    private bool _seeded;        // the first sync's problems are already known (no balloon for them)
    private bool _fixing;        // a Fix runs on a worker thread: full syncs wait
    private bool _countingRunning;
    private bool _autoSwitching;   // an auto-switch pass runs on a worker thread: full syncs wait, passes don't overlap
    private bool _autoSwitchAgain; // new readings arrived during a pass: run another one after it
    private string? _autoSwitchError; // the last pass's failure, shown once

    private Icon? _trayIcon;
    private (AppIconState State, bool HighContrast, Size Size)? _trayIconKey;

    private readonly List<string> _pendingNotices = [];
    private readonly List<string> _recent = []; // for the Problems window's "Recent messages", handed over once
    private const int RecentMax = 20;
    private DateTime _lastBalloon = DateTime.MinValue;
    private Action? _balloonClick; // what clicking the last balloon does (open Problems, make a profile the default…)
    private Point _clickPos;

    private sealed record ConfigError(ConfigIssue Issue, string Headline, string Detail, string Message);

    public TrayContext(bool openProfiles = false)
    {
        if (SynchronizationContext.Current == null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;
        _pm = new ProfileManager(_paths, new WindowsEnvStore(), () => DateTime.Now);
        _owner = new OwnerWindow(() => Guard(() => UpdateStatus(forceIcon: true)));

        var version = typeof(TrayContext).Assembly.GetName().Version?.ToString(3) ?? "0";
        _usageClient = new UsageClient(_usageHandler, () => DateTimeOffset.UtcNow, version);
        var cache = new UsageCache(_paths);
        _poller = new UsagePoller((t, ct) => _usageClient.FetchAsync(t.Name, t.ConfigDir, t.Entry, ct), cache, () => DateTimeOffset.UtcNow);
        // Raised on a worker thread. New readings may move the default (auto-switch).
        _poller.Updated += results => Post(() => Guard(() =>
        {
            _usage.Apply(results);
            _ = AutoSwitchAsync();
        }));
        _usage.Refresh = _poller.RefreshNowAsync;
        // Last known readings first (the cache's lock can take a moment: off the UI thread), then live ones.
        Task.Run(() =>
        {
            try
            {
                var cached = cache.Load();
                Post(() => Guard(() => _usage.Seed(cached)));
            }
            catch (Exception) { /* no cache yet, or locked: the first poll fills it */ }
        });

        _menu.Opening += (_, e) =>
        {
            RebuildMenu();
            e.Cancel = false; // the menu is rebuilt while opening; never let an empty-at-start menu cancel itself
        };
        _icon = new NotifyIcon { Text = "clauderoo", ContextMenuStrip = _menu };
        UpdateStatus(forceIcon: true);
        _icon.Visible = true;

        // Single left click opens the menu (shown after the double-click time, so a double-click doesn't flash it);
        // double-click starts the default profile, which is the bold item in the menu. The foreground is taken at
        // the click itself, while Windows still allows it.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _clickPos = Cursor.Position;
            SetForegroundWindow(_owner.Handle);
            _clickTimer.Stop();
            _clickTimer.Start();
        };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); ShowMenu(_clickPos); };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _clickTimer.Stop();
            Guard(StartDefault);
        };
        _icon.BalloonTipClicked += (_, _) => { if (_balloonClick is { } click) Guard(click); };

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_fixing || _autoSwitching) { _debounce.Start(); return; }
            Guard(FullSync);
        };
        _runningTimer.Tick += async (_, _) => await RefreshRunningInBackground();
        _runningTimer.Start();
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); FlushNotices(); };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // `clauderoow.exe --manage` from another process arrives here (on a background thread).
        _manageSignal = ManageSignal.Listen(() => Post(() => Guard(ShowProfiles)));
        if (openProfiles) Post(() => Guard(ShowProfiles));

        // Learn the current problems right away (quietly: icon and tooltip only) instead of waiting for a change.
        // The first sync also hands the poller its profiles; it starts polling after that.
        Post(() => Guard(FullSync));
        Post(() => Guard(_poller.Start));
    }

    // ---------- state ----------

    /// <summary>
    /// Loads the config, or records why it can't be loaded. Only "isn't set up yet" gets a balloon (once); a damaged or
    /// unreadable file shows on the icon, tooltip and menu. When the config comes back, a sync is started.
    /// </summary>
    private ClauderooConfig? LoadConfig()
    {
        try
        {
            var cfg = _pm.Config;
            if (_configError != null)
            {
                _configError = null;
                UpdateStatus();
                Kick();
            }
            return cfg;
        }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException)
        {
            var issue = !File.Exists(_paths.ConfigFile) ? ConfigIssue.Missing
                : e is ClauderooException ? ConfigIssue.Damaged
                : ConfigIssue.Unreadable;
            var previous = _configError;
            var (headline, detail) = TrayText.ConfigProblem(issue, e.Message);
            _configError = new ConfigError(issue, headline, detail, e.Message);
            _profiles = [];
            _problems = [];
            _problemsForm?.ShowConfigError(headline);
            UpdateStatus();
            if (issue == ConfigIssue.Missing && previous?.Issue != ConfigIssue.Missing)
                Balloon($"{headline}. Click the tray icon to set it up.", ToolTipIcon.Info);
            return null;
        }
    }

    /// <summary>Syncs every profile and replaces the problem list with what it found.</summary>
    private void FullSync()
    {
        if (LoadConfig() is not { } cfg) return;
        EnsureWatchers(cfg);
        var report = _pm.SyncAll();
        var first = !_seeded;
        RefreshProfiles();
        SetProblems(report.ProblemItems);
        // The first sync's notices are what changed while the tray wasn't running: no balloon at startup, but kept
        // for the Problems window's "Recent messages".
        if (first) AddRecent(report.Notices);
        else QueueNotices(report.Notices);
    }

    /// <summary>Shown in the Problems window right away when it's open; otherwise kept until it's next opened.</summary>
    private void AddRecent(IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        if (_problemsForm is { IsDisposed: false, Visible: true } form)
        {
            form.AddRecent(list);
            return;
        }
        _recent.AddRange(list);
        if (_recent.Count > RecentMax) _recent.RemoveRange(0, _recent.Count - RecentMax);
    }

    private void SetProblems(IReadOnlyList<Problem> problems)
    {
        _problems = problems;
        var fresh = _alerted.NewOnly(problems.Select(TrayText.Key));
        _problemsForm?.SetProblems(problems);
        UpdateStatus();
        var first = !_seeded;
        _seeded = true;
        if (fresh.Count > 0 && !first) // problems already there at startup: icon and tooltip only
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
        UpdateUsageTargets();
    }

    /// <summary>Claude-login profiles (home included) are polled for usage; one with a session running more often.</summary>
    private void UpdateUsageTargets() =>
        _poller.SetTargets(_profiles
            .Where(TrayMenuState.TracksUsage)
            .Select(p => new PollTarget(p.Name, p.Dir, new ProfileEntry { Home = p.IsHome },
                _running.GetValueOrDefault(p.Name) > 0)));

    // The 15 s refresh probes processes and waits on the sessions lock: done on a worker thread.
    private async Task RefreshRunningInBackground()
    {
        if (_countingRunning) return;
        _countingRunning = true;
        try
        {
            _running = await Task.Run(() => TrayMenuState.CountRunning(_pm)); // resumes on the UI thread
            UpdateUsageTargets();
            UpdateStatus();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep the last known counts */ }
        catch (Exception) { /* never take the tray down from a timer */ }
        finally { _countingRunning = false; }
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
            ? TrayText.Cut($"{err.Headline}\nClick the icon for options")
            : TrayText.Tooltip(_profiles.FirstOrDefault(p => p.IsDefault) ?? _profiles.FirstOrDefault(p => p.IsHome),
                _problems.Count, total);
        if (_icon.Text != text) _icon.Text = text;
    }

    // ---------- menu ----------

    private void ShowMenu(Point pos)
    {
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
            CopyConfigDetails = () => Guard(() => Clipboard.SetText(
                $"{_configError?.Headline}\r\n{_configError?.Message}\r\nSettings file: {_paths.ConfigFile}")),
            ShowConfigFile = () => Guard(ShowConfigFile),
            Exit = () => Guard(Exit),
            RefreshUsage = () => _ = RefreshUsageAsync(),
            SetAutoSwitch = on => Guard(() => SetAutoSwitch(on)),
        };

        var cfg = LoadConfig();
        if (cfg != null)
        {
            try { EnsureWatchers(cfg); } catch (Exception e) when (e is IOException or ArgumentException) { }
            RefreshProfiles();
        }
        if (_configError is { } err)
        {
            TrayMenu.BuildFallback(_menu, err.Issue, err.Headline, err.Detail, actions);
            return;
        }

        var autoOn = false;
        try { autoOn = AutoStart.IsOn(); } catch (Exception) { }
        TrayMenu.Build(_menu, new TrayMenuState(_profiles, _problems, _running, autoOn, _recent.Count,
            _usage.Readings, _usage.Results, DateTimeOffset.Now, cfg?.AutoSwitch), actions);
    }

    // The fetch runs on a worker thread; the result comes back through the poller's Updated event.
    private async Task RefreshUsageAsync()
    {
        try { await _usage.Refresh(); }
        catch (Exception) { /* shutting down; a failed fetch is shown as the profile's usage status */ }
    }

    /// <summary>
    /// Closes the windows first, so the Profiles window asks about unsaved changes (ExitThread alone would skip its
    /// FormClosing). Cancel there keeps the tray running.
    /// </summary>
    private void Exit()
    {
        _problemsForm?.Close();
        if (_profilesForm is { IsDisposed: false } profiles)
        {
            profiles.Close();
            if (!profiles.IsDisposed && profiles.Visible) return;
        }
        ExitThread();
    }

    // ---------- actions ----------

    /// <summary>The config is needed: shows the plain headline (never the CLI wording) when it can't be loaded.</summary>
    private ClauderooConfig? RequireConfig()
    {
        var cfg = LoadConfig();
        if (cfg == null && _configError is { } err)
            Balloon($"{err.Headline}. Click the tray icon for options.", ToolTipIcon.Warning);
        return cfg;
    }

    // Single Profiles window: opened on first use, brought to the front afterwards.
    private void ShowProfiles()
    {
        if (_profilesForm is { IsDisposed: false })
        {
            _profilesForm.ShowAndActivate(reload: true);
            return;
        }
        if (RequireConfig() == null) return;
        var form = new ProfilesForm(_pm, StartFromProfiles, () => Guard(ShowProblems), _usage);
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
            form = new ProblemsForm(FixAsync, () => Guard(ShowProfiles));
            var created = form;
            form.FormClosed += (_, _) => { if (_problemsForm == created) _problemsForm = null; };
            _problemsForm = form;
        }
        if (_configError is { } err) form.ShowConfigError(err.Headline);
        else form.SetProblems(_problems);
        form.AddRecent(_recent);
        _recent.Clear(); // seen: the window keeps them while it's open
        form.ShowAndActivate();
    }

    /// <summary>
    /// Runs the fix on a worker thread, then re-checks every profile. A fix that worked is reported as fixed even if
    /// the follow-up sync fails; that is said separately.
    /// </summary>
    private async Task<FixOutcome> FixAsync(Problem p)
    {
        SyncReport report;
        _fixing = true;
        try { report = await Task.Run(() => _pm.Fix(p)); } // throws with a message the window shows
        finally { _fixing = false; }

        QueueNotices(report.Notices, now: true);
        try
        {
            FullSync();
        }
        catch (Exception)
        {
            return new FixOutcome(true,
                "Fixed. clauderoo couldn't re-check your profiles just now; it will try again the next time Claude's settings change.");
        }
        return _problems.Any(q => TrayText.Key(q) == TrayText.Key(p))
            ? new FixOutcome(false, "clauderoo tried, but the problem is still there. Copy the details and send them to whoever helps you with clauderoo.")
            : new FixOutcome(true, "Fixed.");
    }

    private void StartDefault()
    {
        if (RequireConfig() is not { } cfg) return;
        var name = cfg.DefaultProfile ?? cfg.HomeProfileName
                   ?? throw new ClauderooException("No default profile is set. Open Manage profiles to choose one.");
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
        if (RequireConfig() is not { } cfg) return;
        dir ??= _pm.Get(name).StartDir
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var _ = Launcher.Start(Launcher.NewWindow(cfg, _paths, name, dir, [], Launcher.WtAvailable()));
    }

    // The Profiles window shows a failed start in a message box: give it the plain words too.
    private void StartFromProfiles(string name)
    {
        try { StartSession(name, null); }
        catch (Exception e)
        {
            var (text, unknown) = TrayText.PlainError(e);
            if (unknown) AddRecent([TrayText.RecentError(e.Message)]);
            throw new ClauderooException(text);
        }
    }

    private void SetDefault(string name)
    {
        var report = _pm.SetDefault(name);
        RefreshProfiles();
        QueueNotices([$"Default is now {name}. New terminals will use it.", .. report.Notices], now: true);
        Kick();
    }

    // ---------- auto-switch ----------

    /// <summary>What a pass decided, and the default before it (the one a switch moved away from).</summary>
    private sealed record AutoSwitchOutcome(Decision Decision, string? From, SyncReport? Report);

    /// <summary>
    /// Runs <see cref="AutoSwitchPass"/> on a worker thread with the latest readings, then shows what it did. One pass
    /// at a time: readings that arrive meanwhile get one more pass afterwards.
    /// </summary>
    private async Task AutoSwitchAsync()
    {
        if (_autoSwitching) { _autoSwitchAgain = true; return; }
        _autoSwitching = true;
        try
        {
            do
            {
                _autoSwitchAgain = false;
                if (_configError != null) return;
                var readings = _usage.Readings; // replaced, never changed, by UsageView: safe to hand over
                AutoSwitchOutcome? outcome;
                try { outcome = await Task.Run(() => AutoSwitchPass(readings)); } // resumes on the UI thread
                catch (Exception e)
                {
                    // The switch failed; nothing was saved, so the next readings try again. The same failure again
                    // only goes to Recent messages: no balloon every few minutes.
                    if (e.Message == _autoSwitchError) AddRecent([TrayText.RecentError(e.Message)]);
                    else ReportError(e);
                    _autoSwitchError = e.Message;
                    return;
                }
                _autoSwitchError = null;
                if (outcome != null) Guard(() => ShowAutoSwitch(outcome));
            } while (_autoSwitchAgain);
        }
        catch (Exception) { /* never take the tray down from a background pass */ }
        finally { _autoSwitching = false; }
    }

    /// <summary>
    /// Worker thread: loads the config and the auto-switch state, decides, applies a switch (SetDefault syncs, which
    /// can take a moment), then saves the next state. Null when the config or state can't be read right now: that
    /// is shown elsewhere, and the next readings try again.
    /// </summary>
    private AutoSwitchOutcome? AutoSwitchPass(IReadOnlyDictionary<string, UsageReading> readings)
    {
        ClauderooConfig cfg;
        AutoSwitchState state;
        try
        {
            cfg = _pm.Config;
            state = _pm.AutoSwitchState.Load();
        }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException) { return null; }

        var now = DateTimeOffset.UtcNow;
        var from = cfg.DefaultProfile;
        var decision = AutoSwitch.Decide(cfg, readings, now, state);
        var report = decision is Decision.SwitchTo to ? _pm.SetDefault(to.Profile) : null; // throws: reported, nothing saved

        var next = AutoSwitch.Next(state, decision, from, readings, now, cfg.AutoSwitch);
        if (next != state)
        {
            try { _pm.AutoSwitchState.Save(next); }
            catch (IOException) { /* best effort: the switch itself is done */ }
        }
        return new AutoSwitchOutcome(decision, from, report);
    }

    private void ShowAutoSwitch(AutoSwitchOutcome o)
    {
        switch (o.Decision)
        {
            case Decision.SwitchTo to:
                RefreshProfiles();
                if (_profilesForm is { IsDisposed: false } form) form.RefreshList();
                Balloon(TrayText.AutoSwitchBalloon(to.Reason, to.Profile), ToolTipIcon.Info);
                if (o.Report != null) QueueNotices(o.Report.Notices); // after the switch balloon, not over it
                Kick();
                break;
            case Decision.NoTarget nt:
                Balloon(TrayText.NoTargetBalloon(nt.Reason, o.From ?? ""), ToolTipIcon.Warning);
                break;
            case Decision.Recovered rec:
                var name = rec.Profile;
                Balloon(TrayText.RecoveredBalloon(name), ToolTipIcon.Info, onClick: () =>
                {
                    if (RequireConfig() is { } cfg && cfg.DefaultProfile != name) SetDefault(name);
                });
                break;
        }
    }

    /// <summary>The Advanced menu's checkbox. Turning it on decides right away with the readings already known.</summary>
    private void SetAutoSwitch(bool on)
    {
        if (RequireConfig() is not { } cfg) return;
        var s = cfg.AutoSwitch ??= new AutoSwitchSettings();
        s.Enabled = on;
        ConfigStore.Save(_paths, cfg);
        QueueNotices([TrayText.AutoSwitchToggled(s)], now: true);
        if (on) _ = AutoSwitchAsync();
    }

    private void NewProfile()
    {
        if (RequireConfig() is not { } cfg) return;
        var name = InputDialog.Ask("New profile", "Profile name:", null, null,
            InputDialog.ProfileNameRule(cfg.Profiles.Keys), InputDialog.ProfileNameHint, lowercase: true);
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

    private void ShowConfigFile()
    {
        var args = File.Exists(_paths.ConfigFile) ? $"/select,\"{_paths.ConfigFile}\"" : $"\"{_paths.Root}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
    }

    private void SetUp()
    {
        try
        {
            _pm.Init();
        }
        catch (ClauderooException e)
        {
            Balloon(TrayText.SetUpFailed(e.Message), ToolTipIcon.Warning);
            return;
        }
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
        catch (Exception e) { ReportError(e); }
    }

    // Core and the launcher speak to the command line: known errors get plain words, the rest a short balloon that
    // opens the Problems window, where the original text is listed under Recent messages.
    private void ReportError(Exception e)
    {
        var (text, unknown) = TrayText.PlainError(e);
        if (unknown) AddRecent([TrayText.RecentError(e.Message)]);
        Balloon(text, ToolTipIcon.Warning, opensProblems: unknown);
    }

    // Every balloon decides for itself what clicking it does: open the Problems window, run onClick, or nothing.
    private void Balloon(string text, ToolTipIcon kind, bool opensProblems = false, Action? onClick = null)
    {
        try
        {
            _balloonClick = onClick ?? (opensProblems ? new Action(ShowProblems) : null);
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
            _poller.Dispose();      // stops the loop first, then the client it uses
            _usageClient.Dispose();
            _usageHandler.Dispose();
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
    /// An invisible top-level window, brought to the foreground before the menu is shown from a left click (so an
    /// outside click closes it). The app is system-DPI-aware (the default; Program.cs doesn't change it), so Windows
    /// doesn't send it WM_DPICHANGED today: scaling changes reach the tray through DisplaySettingsChanged. The
    /// handler stays so the icon also follows the DPI if the app is ever made per-monitor aware.
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
