namespace Clauderoo.Tray;

/// <summary>
/// One writer at a time for the default profile and config.json inside the tray process: the auto-switch pass, manual
/// Set default, the Auto-switch settings, Fix, full syncs and the Profiles window's saves. The UI thread never waits
/// on it: <see cref="RunAsync{T}"/> waits asynchronously and runs the work on a worker thread, and
/// <see cref="TryEnter"/> only takes the gate when it is free.
/// </summary>
internal sealed class WriteGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True while a writer holds the gate.</summary>
    public bool Busy => _gate.CurrentCount == 0;

    /// <summary>Waits for the gate without blocking the caller, then runs <paramref name="work"/> on a worker thread.</summary>
    public async Task<T> RunAsync<T>(Func<T> work)
    {
        await _gate.WaitAsync();
        try { return await Task.Run(work); }
        finally { _gate.Release(); }
    }

    public Task RunAsync(Action work) => RunAsync(() => { work(); return true; });

    /// <summary>Takes the gate if it is free (never waits); dispose the result to let it go. Null when it is busy.</summary>
    public IDisposable? TryEnter() => _gate.Wait(0) ? new Held(_gate) : null;

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release();
        }
    }
}
