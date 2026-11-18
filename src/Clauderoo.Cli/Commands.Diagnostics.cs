using System.Text.Json.Nodes;
using Clauderoo.Core;

namespace Clauderoo.Cli;

// doctor, check --live, completion, env --json and auto --schedule: kept apart from Commands.cs so it stays small.
public static partial class Commands
{
    // The checklist --json payload shared by doctor and check --live (the same shape as the Vertex check's).
    private static object ChecklistPayload(IReadOnlyList<CheckItem> items, string okMessage)
    {
        var failed = items.Count(i => i.Status == CheckStatus.Fail);
        return new
        {
            Ok = failed == 0,
            Verified = true,
            Message = failed > 0 ? $"{failed} check(s) failed." : okMessage,
            Items = items.Where(i => i.Status is not (CheckStatus.Pending or CheckStatus.Running)).Select(i => new
            {
                i.Key, i.Label, Status = i.Status.ToString().ToLowerInvariant(), i.Hint, i.Detail,
            }),
        };
    }

    private static int RunChecklist(Func<CancellationToken, IProgress<CheckItem>?, Task<IReadOnlyList<CheckItem>>> run, bool json,
        TextWriter output, TextWriter err, Func<CancellationTokenSource, IDisposable> hookCancel, string okMessage)
    {
        using var cts = new CancellationTokenSource();
        IReadOnlyList<CheckItem> items;
        using (hookCancel(cts))
        {
            try { items = run(cts.Token, json ? null : new LineProgress(output)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                if (json) JsonOut.Error(err, "Cancelled."); else err.WriteLine("Cancelled.");
                return 1;
            }
        }
        var failed = items.Any(i => i.Status == CheckStatus.Fail);
        if (json) JsonOut.Write(output, ChecklistPayload(items, okMessage));
        else if (!failed) output.WriteLine(okMessage);
        return failed ? 1 : 0;
    }

    private static int CheckSecretCmd(string name, bool json, TextWriter output, TextWriter err, ProfileManager pm,
        Func<CancellationTokenSource, IDisposable> hookCancel)
    {
        var token = Providers.Of(pm.Get(name)) == Providers.Token;
        if (!json)
            output.WriteLine(token
                ? "Testing the token (this sends one tiny prompt with Claude Code)…"
                : "Testing the key against the Anthropic API (uses no quota)…");
        return RunChecklist((ct, p) => pm.CheckSecretAsync(name, ct, p), json, output, err, hookCancel, "All checks passed.");
    }

    private static int DoctorCmd(List<string> pos, bool json, TextWriter output, TextWriter err, ProfileManager pm,
        Func<CancellationTokenSource, IDisposable> hookCancel, ReleaseHooks? hooks)
    {
        if (pos.Count > 0) throw new ClauderooException("Usage: clauderoo doctor [--json]");
        return RunChecklist((ct, p) => Doctor.RunAsync(pm.Paths, pm.Env, hooks?.Doctor, ct, p), json, output, err,
            hookCancel, "Everything looks good.");
    }

    private static int CompletionCmd(List<string> pos, TextWriter output)
    {
        const string usage = "Usage: clauderoo completion powershell|bash";
        if (pos.Count != 1) throw new ClauderooException(usage);
        output.Write(pos[0] switch
        {
            "powershell" or "pwsh" => Completion.PowerShell(),
            "bash" => Completion.Bash(),
            _ => throw new ClauderooException(usage),
        });
        return 0;
    }

    // Hidden: called by the completion scripts. Reads config.json only; prints nothing when it can't.
    private static int CompleteCmd(List<string> pos, TextWriter output, ProfileManager pm)
    {
        if (pos.Count != 1 || pos[0] != "profiles") return 1;
        ClauderooConfig? cfg;
        try { cfg = ConfigStore.Load(pm.Paths); }
        catch (Exception e) when (e is ClauderooException or IOException or UnauthorizedAccessException) { return 0; }
        foreach (var n in cfg?.Profiles.Keys ?? Enumerable.Empty<string>()) output.WriteLine(n);
        return 0;
    }

    // {schemaVersion, profile, env:{NAME:value}}: names are kept exactly as set (no camelCase).
    private static int EnvJsonCmd(List<string> pos, TextWriter output, ProfileManager pm)
    {
        if (pos.Count != 1) throw new ClauderooException("Usage: clauderoo env <name> --json (--json only shows the variables)");
        var vars = new JsonObject();
        foreach (var kv in pm.GetEnv(pos[0]).OrderBy(k => k.Key, StringComparer.Ordinal)) vars[kv.Key] = kv.Value;
        JsonOut.Write(output, new { Profile = pos[0], Env = vars });
        return 0;
    }

    private static int AutoScheduleCmd(List<string> pos, bool json, TextWriter output, TextWriter err, ProfileManager pm, ReleaseHooks? hooks)
    {
        const string usage = "Usage: clauderoo auto --schedule on|off [--every <minutes>]";
        if (json) throw new ClauderooException("--json only applies to 'auto --once'.");
        bool? on = null;
        var minutes = AutoSchedule.DefaultMinutes;
        for (var i = 0; i < pos.Count; i++)
        {
            switch (pos[i])
            {
                case "--schedule" when i + 1 < pos.Count && pos[i + 1] is "on" or "off":
                    on = pos[++i] == "on";
                    break;
                case "--every" when i + 1 < pos.Count:
                    if (!int.TryParse(pos[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minutes))
                        throw new ClauderooException("--every must be a whole number of minutes from 1 to 1439.");
                    break;
                default: throw new ClauderooException(usage);
            }
        }
        if (on == null) throw new ClauderooException(usage);
        var scheduler = hooks?.Scheduler ?? new SchtasksScheduler();
        if (on == false)
        {
            output.WriteLine(AutoSchedule.Disable(scheduler)
                ? $"Removed the scheduled task '{AutoSchedule.TaskName}'."
                : $"There was no scheduled task '{AutoSchedule.TaskName}'.");
            return 0;
        }
        var tray = hooks?.TrayExe ?? AutoStart.ResolveExe(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.Combine(AppContext.BaseDirectory, "clauderoow.exe"));
        if (!File.Exists(tray)) throw new ClauderooException($"clauderoow.exe not found ({tray}). Run 'clauderoo install' first.");
        AutoSchedule.Enable(scheduler, tray, minutes);
        output.WriteLine($"Scheduled '{AutoSchedule.TaskName}': it runs 'clauderoo auto --once' every {minutes} minute{(minutes == 1 ? "" : "s")} while you're signed in.");
        if (pm.Config.AutoSwitch is not { Enabled: true })
            err.WriteLine("warning: auto-switch is off, so the task does nothing until you turn it on ('cr auto on').");
        return 0;
    }
}
