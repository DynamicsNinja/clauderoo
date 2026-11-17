using Clauderoo.Cli;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

// Live progress for the Vertex setup check: the planned rows first, then each result as it is decided; model results
// in the order the models answer, while the returned list stays in config order.
public class CheckProgressTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);
    private const string Sonnet = "claude-sonnet-4-6", Opus = "claude-opus-4-8", Haiku = "claude-haiku-4-5";

    private sealed class Ctx : IDisposable
    {
        public TempHome Home = new();
        public FakeGcloud Gcloud = new();
        public FakeMachineEnv Env = new();
        public FakeIam Iam = new();
        public IClaudeRunner Claude = new FakeClaude();
        public ProfileManager Pm = null!;
        public void Dispose() => Home.Dispose();
    }

    private static Ctx Make(IClaudeRunner? claude = null, bool models = true)
    {
        var c = new Ctx();
        if (claude != null) c.Claude = claude;
        c.Pm = new ProfileManager(c.Home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), c.Gcloud, c.Env, c.Iam, c.Claude)
        { HelperExe = @"C:\x\clauderoo.exe" };
        c.Pm.Init();
        c.Pm.Add("work");
        c.Pm.SetProvider("work", Providers.Vertex, models
            ? new VertexSettings { Project = "proj-1", Region = "europe-west1", SonnetModel = Sonnet, OpusModel = Opus, HaikuModel = Haiku }
            : new VertexSettings { Project = "proj-1", Region = "europe-west1" });
        return c;
    }

    private sealed class ListProgress : IProgress<CheckItem>
    {
        private readonly List<CheckItem> _items = [];
        public void Report(CheckItem value) { lock (_items) _items.Add(value); }
        public List<CheckItem> Items { get { lock (_items) return [.. _items]; } }
    }

    // Each model's probe waits on its own TaskCompletionSource, so the test decides the order they answer in.
    private sealed class HeldClaude : IClaudeRunner
    {
        public readonly Dictionary<string, TaskCompletionSource<(int, string, string)>> Held = new()
        {
            [Sonnet] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [Opus] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [Haiku] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public string? Find() => @"C:\fake\claude.cmd";
        public Task<(int exit, string stdout, string stderr)> RunAsync(string claudePath, IReadOnlyList<string> args,
            string configDir, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            var model = args[args.ToList().IndexOf("--model") + 1];
            return Held[model].Task.WaitAsync(ct);
        }
    }

    private static async Task Until(Func<bool> cond)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    private static bool IsFinal(CheckItem i) => i.Status is not (CheckStatus.Pending or CheckStatus.Running);

    [Fact]
    public async Task Progress_PlannedRowsFirst_AllPending_WithStableKeys()
    {
        using var c = Make();
        var p = new ListProgress();
        await c.Pm.CheckSetupAsync("work", default, p);
        var plan = p.Items.Take(9).ToList();
        Assert.All(plan, i => Assert.Equal(CheckStatus.Pending, i.Status));
        Assert.Equal(["gcloud", "signin", "gac", "iam", "leftovers", "claude", "model:sonnet", "model:opus", "model:haiku"],
            plan.Select(i => i.Key));
        Assert.Equal([Sonnet, Opus, Haiku], plan.Skip(6).Select(i => i.Label));
    }

    [Fact]
    public async Task Progress_EveryItemHasAKey_FinalListKeysMatchPlan()
    {
        using var c = Make();
        var p = new ListProgress();
        var r = await c.Pm.CheckSetupAsync("work", default, p);
        Assert.All(p.Items, i => Assert.False(string.IsNullOrEmpty(i.Key)));
        Assert.Equal(p.Items.Take(9).Select(i => i.Key), r.Select(i => i.Key));
    }

    [Fact]
    public async Task Progress_ModelsRunningTogether_ResultsInFinishOrder_ReturnedInConfigOrder()
    {
        var claude = new HeldClaude();
        using var c = Make(claude);
        var p = new ListProgress();
        var check = c.Pm.CheckSetupAsync("work", default, p);
        await Until(() => claude.Started == 3);

        // All three probes are announced as running before any answer.
        var running = p.Items.Where(i => i.Status == CheckStatus.Running).ToList();
        Assert.Equal(["model:sonnet", "model:opus", "model:haiku"], running.Select(i => i.Key));
        Assert.Equal([Sonnet, Opus, Haiku], running.Select(i => i.Label));
        Assert.Equal(6, p.Items.Count(IsFinal)); // the non-model checks are already done

        // Answer in reverse order; each result is reported as soon as its model answers.
        foreach (var (model, key) in new[] { (Haiku, "model:haiku"), (Opus, "model:opus"), (Sonnet, "model:sonnet") })
        {
            claude.Held[model].SetResult((0, "OK\n", ""));
            await Until(() => p.Items.Any(i => i.Key == key && IsFinal(i)));
        }
        var r = await check.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["model:haiku", "model:opus", "model:sonnet"],
            p.Items.Where(i => IsFinal(i) && i.Key!.StartsWith("model:")).Select(i => i.Key));
        Assert.Equal([$"{Sonnet} answers", $"{Opus} answers", $"{Haiku} answers"], r.TakeLast(3).Select(i => i.Label));
        Assert.All(r, i => Assert.True(IsFinal(i)));
    }

    [Fact]
    public async Task Progress_NoRunningBeforePlan_NoModelResultBeforeAllRunning()
    {
        using var c = Make();
        var p = new ListProgress();
        await c.Pm.CheckSetupAsync("work", default, p);
        var items = p.Items;
        var lastRunning = items.FindLastIndex(i => i.Status == CheckStatus.Running);
        var firstModelResult = items.FindIndex(i => IsFinal(i) && i.Key!.StartsWith("model:"));
        Assert.True(lastRunning < firstModelResult);
    }

    public static TheoryData<string> SkipPaths => ["gcloud-missing", "token-fail", "claude-missing"];

    [Theory]
    [MemberData(nameof(SkipPaths))]
    public async Task Progress_SkipPaths_EveryPlannedRowResolved(string path)
    {
        using var c = Make();
        if (path == "gcloud-missing") c.Gcloud.Path = null;
        if (path == "token-fail") c.Gcloud.Result = (1, "", "ERROR: not logged in");
        if (path == "claude-missing") ((FakeClaude)c.Claude).Path = null;
        var p = new ListProgress();
        var r = await c.Pm.CheckSetupAsync("work", default, p);

        var last = p.Items.GroupBy(i => i.Key).Select(g => g.Last()).ToList();
        Assert.Equal(9, last.Count);
        Assert.All(last, i => Assert.True(IsFinal(i), $"{i.Key} left {i.Status}"));
        Assert.Equal(r, p.Items.Where(IsFinal));
    }

    [Fact]
    public async Task Progress_DefaultModel_KeyAndLabel()
    {
        using var c = Make(models: false);
        var p = new ListProgress();
        await c.Pm.CheckSetupAsync("work", default, p);
        var running = Assert.Single(p.Items, i => i.Status == CheckStatus.Running);
        Assert.Equal("model:default", running.Key);
        Assert.Equal("\u22EF testing the default model\u2026", CheckItemFormatter.Format(running));
    }

    [Fact]
    public void Formatter_Running()
    {
        Assert.Equal("\u22EF testing claude-opus-4-8\u2026",
            CheckItemFormatter.Format(new(CheckStatus.Running, "claude-opus-4-8", null) { Key = "model:opus" }));
    }

    // ---------- CLI ----------

    [Fact]
    public async Task Cli_PrintsTestingLines_ThenResultsInFinishOrder()
    {
        var claude = new HeldClaude();
        using var c = Make(claude);
        var o = new StringWriter();
        string Out() { lock (o) return o.ToString(); }
        var run = Task.Run(() => Commands.Run(["check", "work"], o, new StringWriter(), c.Pm, _ => 0, _ => 0,
            wtAvailable: true, claudePath: "claude.cmd", hookCancel: _ => new NoHook()));
        await Until(() => claude.Started == 3 && Out().Contains("testing " + Haiku));

        foreach (var model in new[] { Opus, Haiku, Sonnet })
        {
            claude.Held[model].SetResult((0, "OK\n", ""));
            await Until(() => Out().Contains($"{model} answers"));
        }
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));

        var lines = Out().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
        var tail = lines.SkipWhile(l => !l.StartsWith("\u22EF")).ToList();
        Assert.Equal(new[]
        {
            $"\u22EF testing {Sonnet}\u2026", $"\u22EF testing {Opus}\u2026", $"\u22EF testing {Haiku}\u2026",
            $"\u2713 {Opus} answers", $"\u2713 {Haiku} answers", $"\u2713 {Sonnet} answers",
        }, tail);
        // The non-model lines come before, once each, and nothing about pending rows is printed.
        Assert.Single(lines, l => l == "\u2713 gcloud installed");
        Assert.DoesNotContain(lines, l => l.Contains("checking", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1 + 6 + 3 + 3, lines.Count); // header, six checks, three "testing", three results
    }

    // ---------- window model ----------

    private static CheckItem P(string key, string label) => new(CheckStatus.Pending, label, null) { Key = key };

    [Fact]
    public void Window_Plan_ShowsEveryRowChecking_InPlanOrder()
    {
        var m = new CheckProgressModel();
        m.Apply(P("gcloud", "gcloud installed"));
        m.Apply(P("model:sonnet", Sonnet));
        Assert.Equal(2, m.Total);
        Assert.Equal(0, m.Done);
        Assert.All(m.Rows, r => { Assert.Equal(CheckStatus.Pending, r.Status); Assert.Equal("Checking\u2026", r.Hint); });
        Assert.Equal("Checking\u2026 0 of 2 done", m.StatusText);
    }

    [Fact]
    public void Window_RowsReplacedByKey_RunningShowsTesting_ResultsCountDone()
    {
        var m = new CheckProgressModel();
        foreach (var i in new[] { P("gcloud", "gcloud installed"), P("model:sonnet", Sonnet), P("model:opus", Opus) })
            m.Apply(i);
        m.Apply(new CheckItem(CheckStatus.Ok, "gcloud installed", null) { Key = "gcloud" });
        m.Apply(new CheckItem(CheckStatus.Running, Sonnet, null) { Key = "model:sonnet" });
        m.Apply(new CheckItem(CheckStatus.Running, Opus, null) { Key = "model:opus" });
        m.Apply(new CheckItem(CheckStatus.Fail, $"{Opus} didn't answer", "hint", "raw") { Key = "model:opus" });

        Assert.Equal(["gcloud", "model:sonnet", "model:opus"], m.Rows.Select(r => r.Key)); // plan order kept
        Assert.Equal(CheckStatus.Ok, m.Rows[0].Status);
        Assert.Equal(CheckStatus.Running, m.Rows[1].Status);
        Assert.Equal("Testing\u2026", m.Rows[1].Hint);
        Assert.Equal(new CheckItem(CheckStatus.Fail, $"{Opus} didn't answer", "hint", "raw") { Key = "model:opus" }, m.Rows[2]);
        Assert.Equal("Checking\u2026 2 of 3 done", m.StatusText);
    }

    [Fact]
    public void Window_Cancel_LeavesNoPendingOrRunningRows()
    {
        var m = new CheckProgressModel();
        foreach (var i in new[] { P("gcloud", "gcloud installed"), P("claude", "Claude Code installed"), P("model:sonnet", Sonnet) })
            m.Apply(i);
        m.Apply(new CheckItem(CheckStatus.Ok, "gcloud installed", null) { Key = "gcloud" });
        m.Apply(new CheckItem(CheckStatus.Running, Sonnet, null) { Key = "model:sonnet" });
        m.Cancel();

        Assert.All(m.Rows, r => Assert.True(IsFinal(r)));
        Assert.Equal(CheckStatus.Ok, m.Rows[0].Status);
        Assert.All(m.Rows.Skip(1), r => { Assert.Equal(CheckStatus.Skipped, r.Status); Assert.Equal("Cancelled", r.Hint); });
        // Results arriving after the cancel change nothing.
        m.Apply(new CheckItem(CheckStatus.Ok, $"{Sonnet} answers", null) { Key = "model:sonnet" });
        Assert.Equal(CheckStatus.Skipped, m.Rows[2].Status);
    }
}
