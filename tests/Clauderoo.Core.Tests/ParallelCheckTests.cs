namespace Clauderoo.Core.Tests;

// The Vertex setup check runs everything at once: the sign-in token, the instant checks and the model probes start
// together; IAM waits for the token; a failed sign-in skips IAM and cancels the probes.
public class ParallelCheckTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);
    private const string Sonnet = "claude-sonnet-4-6", Opus = "claude-opus-4-8", Haiku = "claude-haiku-4-5";
    private const string Token = "ya29.SECRET-TOKEN";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static readonly string[] PlanKeys =
        ["gcloud", "signin", "gac", "iam", "leftovers", "claude", "model:sonnet", "model:opus", "model:haiku"];

    private sealed class Ctx : IDisposable
    {
        public TempHome Home = new();
        public FakeGcloud Gcloud = new();
        public FakeIam Iam = new();
        public ProfileManager Pm = null!;
        public void Dispose() => Home.Dispose();
    }

    private static Ctx Make(IClaudeRunner claude)
    {
        var c = new Ctx();
        c.Pm = new ProfileManager(c.Home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), c.Gcloud,
            new FakeMachineEnv(), c.Iam, claude) { HelperExe = @"C:\x\clauderoo.exe" };
        c.Pm.Init();
        c.Pm.Add("work");
        c.Pm.SetProvider("work", Providers.Vertex, new VertexSettings
        {
            Project = "proj-1", Region = "europe-west1", SonnetModel = Sonnet, OpusModel = Opus, HaikuModel = Haiku,
        });
        return c;
    }

    private sealed class ListProgress : IProgress<CheckItem>
    {
        private readonly List<CheckItem> _items = [];
        public void Report(CheckItem value) { lock (_items) _items.Add(value); }
        public List<CheckItem> Items { get { lock (_items) return [.. _items]; } }
    }

    private static bool IsFinal(CheckItem i) => i.Status is not (CheckStatus.Pending or CheckStatus.Running);

    private static async Task Until(Func<bool> cond)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    // Each probe answers per model: a fixed result, or (null) it waits until cancelled.
    private sealed class ScriptedClaude(Func<string, (int, string, string)?> answer) : IClaudeRunner
    {
        public readonly TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FirstFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started, _cancelled;
        public int Started => Volatile.Read(ref _started);
        public int Cancelled => Volatile.Read(ref _cancelled);
        public string? Find() => @"C:\fake\claude.cmd";
        public async Task<(int exit, string stdout, string stderr)> RunAsync(string claudePath, IReadOnlyList<string> args,
            string configDir, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            FirstStarted.TrySetResult();
            var model = args[args.ToList().IndexOf("--model") + 1];
            await Task.Yield();
            if (answer(model) is { } a)
            {
                FirstFinished.TrySetResult();
                return a;
            }
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref _cancelled); throw; }
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task Probes_StartBeforeTheTokenIsKnown()
    {
        var claude = new ScriptedClaude(_ => (0, "OK\n", ""));
        using var c = Make(claude);
        var probeSeenByGcloud = false;
        // The token call only answers once a model probe has started: proves they overlap.
        c.Gcloud.Responder = () =>
        {
            probeSeenByGcloud = claude.FirstStarted.Task.Wait(Wait);
            return (0, Token + "\n", "");
        };
        var r = await c.Pm.CheckSetupAsync("work").WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(probeSeenByGcloud, "no model probe started while the sign-in token was pending");
        Assert.All(r, i => Assert.Equal(CheckStatus.Ok, i.Status));
        Assert.Equal(3, claude.Started);
    }

    [Fact]
    public async Task Iam_StartsOnlyAfterTheToken()
    {
        var claude = new ScriptedClaude(_ => (0, "OK\n", ""));
        using var c = Make(claude);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Gcloud.Responder = () => { release.Task.Wait(TimeSpan.FromSeconds(10)); return (0, Token + "\n", ""); };
        var p = new ListProgress();
        var check = c.Pm.CheckSetupAsync("work", default, p);

        await Until(() => claude.Started == 3);
        await Task.Delay(50);
        Assert.Empty(c.Iam.Tokens); // no token yet, so no IAM call
        Assert.DoesNotContain(p.Items, i => i.Key == "iam" && IsFinal(i));
        Assert.DoesNotContain(p.Items, i => i.Key!.StartsWith("model:") && IsFinal(i)); // held until the token is known

        release.SetResult();
        var r = await check.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([Token], c.Iam.Tokens);
        Assert.Equal(CheckStatus.Ok, r[3].Status);
        var items = p.Items;
        Assert.True(items.FindIndex(i => i.Key == "signin" && IsFinal(i)) < items.FindIndex(i => i.Key == "iam" && IsFinal(i)));
    }

    [Fact]
    public async Task SignInFails_IamAndModelsSkipped_ProbesCancelled_NothingLeftOpen()
    {
        // Sonnet answers before the sign-in fails; Opus and Haiku hang until cancelled.
        var claude = new ScriptedClaude(m => m == Sonnet ? (0, "OK\n", "") : null);
        using var c = Make(claude);
        c.Gcloud.Responder = () =>
        {
            Assert.True(claude.FirstFinished.Task.Wait(Wait));
            SpinWait.SpinUntil(() => claude.Started == 3, Wait);
            return (1, "", "ERROR: not logged in");
        };
        var p = new ListProgress();
        var r = await c.Pm.CheckSetupAsync("work", default, p).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(PlanKeys, r.Select(i => i.Key));
        Assert.Equal(CheckStatus.Fail, r[1].Status);
        Assert.Equal(CheckStatus.Skipped, r[3].Status);
        Assert.Equal(CheckStatus.Ok, r[5].Status);
        // A probe that already answered is skipped too: without a sign-in its answer means nothing.
        Assert.All(r.Skip(6), i => Assert.Equal(CheckStatus.Skipped, i.Status));
        Assert.Equal(3, claude.Started);
        Assert.Equal(2, claude.Cancelled);
        Assert.Empty(c.Iam.Tokens);

        var last = p.Items.GroupBy(i => i.Key).Select(g => g.Last()).ToList();
        Assert.Equal(9, last.Count);
        Assert.All(last, i => Assert.True(IsFinal(i), $"{i.Key} left {i.Status}"));
        Assert.All(p.Items.Where(IsFinal).GroupBy(i => i.Key), g => Assert.Single(g)); // one final item per row
    }

    [Fact]
    public async Task ModelAnswersBeforeToken_ReportedAfterSignIn_Scrubbed_ListInPlanOrder()
    {
        var claude = new ScriptedClaude(m => m == Opus ? (1, "", "error with " + Token) : (0, "OK " + Token, ""));
        using var c = Make(claude);
        var finishedFirst = false;
        c.Gcloud.Responder = () =>
        {
            finishedFirst = claude.FirstFinished.Task.Wait(Wait);
            SpinWait.SpinUntil(() => false, 100); // let every probe finish
            return (0, Token + "\n", "");
        };
        var p = new ListProgress();
        var r = await c.Pm.CheckSetupAsync("work", default, p).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(finishedFirst, "no model answered before the token was known");
        var items = p.Items;
        var signIn = items.FindIndex(i => i.Key == "signin" && IsFinal(i));
        Assert.All(items.Select((i, n) => (i, n)).Where(x => x.i.Key!.StartsWith("model:") && IsFinal(x.i)),
            x => Assert.True(x.n > signIn, $"{x.i.Key} reported before the sign-in"));
        Assert.DoesNotContain(items, i => (i.Label + i.Hint + i.Detail).Contains(Token));
        Assert.Equal("error with ***", r[7].Detail);
        Assert.Equal(CheckStatus.Fail, r[7].Status);
        Assert.Equal(PlanKeys, r.Select(i => i.Key));
        Assert.Equal([$"{Sonnet} answers", $"{Opus} didn't answer", $"{Haiku} answers"], r.Skip(6).Select(i => i.Label));
    }

    private sealed class FaultingClaude : IClaudeRunner
    {
        public string? Find() => @"C:\fake\claude.cmd";
        public async Task<(int exit, string stdout, string stderr)> RunAsync(string claudePath, IReadOnlyList<string> args,
            string configDir, TimeSpan timeout, CancellationToken ct)
        {
            await Task.Yield();
            throw new ClauderooException("Couldn't start claude.");
        }
    }

    [Fact]
    public async Task ProbeFaultsBeforeToken_ThrowsAtOnce_NoIamCall()
    {
        using var c = Make(new FaultingClaude());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Gcloud.Responder = () => { release.Task.Wait(TimeSpan.FromSeconds(10)); return (0, Token + "\n", ""); };
        try
        {
            // Throws while the token is still pending: the error isn't held back until sign-in.
            var e = await Assert.ThrowsAsync<ClauderooException>(() => c.Pm.CheckSetupAsync("work").WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Couldn't start claude.", e.Message);
            Assert.Empty(c.Iam.Tokens);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task UserCancelsWhileTokenPending_Throws_NothingMoreReported()
    {
        var claude = new ScriptedClaude(_ => null);
        using var c = Make(claude);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Gcloud.Responder = () => { release.Task.Wait(TimeSpan.FromSeconds(10)); return (0, Token + "\n", ""); };
        using var cts = new CancellationTokenSource();
        var p = new ListProgress();
        var check = c.Pm.CheckSetupAsync("work", cts.Token, p);
        await Until(() => claude.Started == 3);
        var before = p.Items.Count;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.FromSeconds(10)));
        release.SetResult();
        await Until(() => claude.Cancelled == 3);
        await Task.Delay(50);
        Assert.Equal(before, p.Items.Count);
    }
}
