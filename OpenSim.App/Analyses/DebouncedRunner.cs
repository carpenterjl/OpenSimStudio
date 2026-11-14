namespace OpenSim.App.Analyses;

/// <summary>
/// Runs one analysis window's compute on change: requests that arrive within the debounce
/// interval are coalesced into one compute (the last request's, which captured the newest
/// snapshot), a request that arrives while a compute runs cancels it, and a superseded compute's
/// result is dropped even when the compute ignores its token. The compute itself runs on the
/// thread pool, so the UI thread never waits on it; the events are raised on the context the
/// request was made on (the dispatcher, in the application).
/// <para>
/// Plain .NET with no UI types, so its timing is tested directly: the delay is a delegate a
/// test replaces with a gate it opens itself.
/// </para>
/// </summary>
public sealed class DebouncedRunner<T>
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _cts;
    private int _generation;

    public DebouncedRunner(TimeSpan debounce, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Debounce = debounce;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    /// <summary>How long a request waits for the next one before computing.</summary>
    public TimeSpan Debounce { get; set; }

    /// <summary>The latest request's run, from its debounce to its event. Completes, never
    /// faults: failures and cancellations are reported through the events.</summary>
    public Task Current { get; private set; } = Task.CompletedTask;

    /// <summary>True from a request until its result, failure or cancellation is reported.</summary>
    public bool IsPending { get; private set; }

    /// <summary>The debounce is over and the compute has been handed to the thread pool.</summary>
    public event EventHandler? Started;

    /// <summary>The current request's compute finished.</summary>
    public event EventHandler<T>? Completed;

    /// <summary>The current request's compute threw.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The current request was cancelled by <see cref="Cancel"/> (a request superseded
    /// by a newer one raises nothing: the newer one reports instead).</summary>
    public event EventHandler? Cancelled;

    /// <summary>Asks for a compute. <paramref name="compute"/> runs on the thread pool and must
    /// poll its token; with <paramref name="immediate"/> the debounce is skipped (the Update
    /// button).</summary>
    public void Request(Func<CancellationToken, T> compute, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(compute);
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        int generation = ++_generation;
        IsPending = true;
        Current = RunAsync(compute, generation, immediate ? TimeSpan.Zero : Debounce, cts);
    }

    /// <summary>Stops the pending or running compute, if any, and raises <see cref="Cancelled"/>.</summary>
    public void Cancel()
    {
        if (!IsPending) return;
        _cts?.Cancel();
    }

    private async Task RunAsync(Func<CancellationToken, T> compute, int generation, TimeSpan debounce,
        CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            if (debounce > TimeSpan.Zero) await _delay(debounce, token);
            token.ThrowIfCancellationRequested();
            Started?.Invoke(this, EventArgs.Empty);
            T result = await Task.Run(() => compute(token), token);
            if (generation != _generation) return;               // a newer request owns the window
            token.ThrowIfCancellationRequested();
            IsPending = false;
            Completed?.Invoke(this, result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation != _generation) return;
            IsPending = false;
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            IsPending = false;
            Failed?.Invoke(this, ex);
        }
        finally
        {
            if (generation == _generation) _cts = null;
            cts.Dispose();
        }
    }
}
