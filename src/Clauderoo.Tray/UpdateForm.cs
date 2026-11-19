using System.Diagnostics;
using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>
/// "Update now": finds the latest release, downloads and verifies it, installs it, then asks the tray to restart so
/// the new version takes over. Every step shows here; nothing opens a terminal.
/// </summary>
internal sealed class UpdateForm : Form
{
    private readonly string _repo;
    private readonly Action _restart;
    private readonly Icon _icon;
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(1, 0) };
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _detail = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(1, 0) };
    private readonly Button _close = new() { Text = "Cancel", AutoSize = true, Margin = new Padding(0, 8, 0, 0), DialogResult = DialogResult.Cancel };
    private readonly Button _go = new() { Text = "&Update now", AutoSize = true, Margin = new Padding(0, 8, 8, 0), Visible = false };
    private readonly bool _askFirst;
    private readonly Action<ReleaseInfo?> _checked;
    private TaskCompletionSource<bool>? _confirm;
    private readonly CancellationTokenSource _cts = new();
    private bool _installing;

    /// <param name="restart">Runs on the UI thread after a successful install: starts the new tray and exits this one.</param>
    /// <param name="askFirst">"Check for updates now": when a newer version exists, wait for Update now instead of installing.</param>
    /// <param name="checkedLatest">Runs on the UI thread with what GitHub reported (null: no release with a download).</param>
    public UpdateForm(string repo, Action restart, bool askFirst = false, Action<ReleaseInfo?>? checkedLatest = null)
    {
        _repo = repo;
        _restart = restart;
        _askFirst = askFirst;
        _checked = checkedLatest ?? (_ => { });
        Text = "clauderoo — Update";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Padding = new Padding(12);
        _icon = AppIcons.Load(AppIconState.Normal, SystemInformation.HighContrast);
        Icon = _icon;
        CancelButton = _close;
        ClientSize = new Size(LogicalToDeviceUnits(420), LogicalToDeviceUnits(170));
        _bar.AccessibleName = "Update progress";

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        for (var i = 0; i < 3; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Margin = new Padding(0) };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_go);
        _go.Click += (_, _) => { _go.Visible = false; _confirm?.TrySetResult(true); };
        table.Controls.Add(_status, 0, 0);
        table.Controls.Add(_bar, 0, 1);
        table.Controls.Add(_detail, 0, 2);
        table.Controls.Add(buttons, 0, 3);
        Controls.Add(table);
        // The labels wrap at the window's width. The window can't be resized, so it is set once, here: the labels start
        // with a 1-pixel limit and would otherwise draw nothing.
        var wrap = Math.Max(100, ClientSize.Width - Padding.Horizontal - LogicalToDeviceUnits(6));
        _status.MaximumSize = _detail.MaximumSize = new Size(wrap, 0);

        _close.Click += (_, _) => Close();
        Shown += (_, _) => _ = RunAsync();
    }

    private void Step(string status, string detail = "")
    {
        _status.Text = status;
        _detail.Text = detail;
    }

    private void Finish(string status, string detail = "", bool ok = false)
    {
        Step(status, detail);
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = ok ? 100 : 0;
        _close.Text = "Close";
        _close.Enabled = true;
    }

    private async Task RunAsync()
    {
        var current = AppVersion.Parse(AppVersion.Current) ?? new Version(0, 0);
        var tmp = Path.Combine(Path.GetTempPath(), "clauderoo-upgrade-" + Guid.NewGuid().ToString("N"));
        using var handler = new SocketsHttpHandler();
        var updater = new Updater(handler, _repo);
        try
        {
            Step("Looking for the latest version…", $"You have {AppVersion.Current}.");
            var lookup = await updater.LookupAsync(_cts.Token);
            _checked(lookup.Info);
            if (lookup.Info is not { } latest)
            {
                Finish("No update could be found.", lookup.ReleaseExists
                    ? $"The latest release ({lookup.Tag}) has no Windows download."
                    : "No release was found on GitHub.");
                return;
            }
            if (latest.Version <= current)
            {
                Finish($"clauderoo {AppVersion.Current} is up to date.", ok: true);
                return;
            }

            if (_askFirst)
            {
                Step($"clauderoo {latest.Version} is available.", $"You have {AppVersion.Current}. clauderoo restarts after updating.");
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = 0;
                _close.Text = "Not now";
                _go.Visible = true;
                AcceptButton = _go;
                _go.Focus();
                _confirm = new TaskCompletionSource<bool>();
                using (_cts.Token.Register(() => _confirm.TrySetCanceled()))
                    await _confirm.Task;
                _bar.Style = ProgressBarStyle.Marquee;
                _close.Text = "Cancel";
            }

            Step($"Downloading {latest.Tag}…", $"{AppVersion.Current} → {latest.Version}");
            var extracted = await updater.DownloadVerifiedAsync(latest, tmp, _cts.Token);
            var exe = Path.Combine(extracted, "clauderoo.exe");
            if (!File.Exists(exe)) { Finish("The download didn't contain clauderoo.exe.", "Nothing was changed."); return; }

            // Past this point the install runs to the end: cancelling half way would leave a mix of versions.
            _installing = true;
            _close.Enabled = false;
            Step($"Installing {latest.Version}…", "Verified. Replacing the installed files.");
            var (code, output) = await Task.Run(() => RunInstall(exe));
            if (code != 0)
            {
                _installing = false;
                Finish("The update couldn't be installed.", (output.Length > 0 ? output + " " : "") + "The previous version is still in place.");
                return;
            }

            Finish($"Updated to {latest.Version}. Restarting clauderoo…",
                "Open a new terminal to use the new 'cr'. Running Claude sessions keep going.", ok: true);
            _close.Enabled = false;
            await Task.Delay(1500);
            _installing = false;
            _close.Enabled = true;
            _restart();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { /* closed */ }
        catch (ObjectDisposedException) { /* closed */ }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Finish("Couldn't reach GitHub.", "Check your connection and try again. Nothing was changed.");
        }
        catch (Exception e) when (e is ClauderooException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _installing = false;
            Finish("The update failed.", e.Message);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // The new version installs itself (as `cr upgrade` does), with no window; its last line explains a failure.
    private static (int Code, string Output) RunInstall(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("install");
        using var p = Process.Start(psi) ?? throw new ClauderooException("Couldn't start the new installer.");
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var text = (e.Result.Trim().Length > 0 ? e.Result : o.Result).Trim();
        return (p.ExitCode, text.Split('\n').LastOrDefault()?.Trim() ?? "");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _icon.Dispose(); _cts.Dispose(); }
        base.Dispose(disposing);
    }
}
