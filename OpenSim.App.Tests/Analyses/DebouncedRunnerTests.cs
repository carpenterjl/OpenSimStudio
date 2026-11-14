using OpenSim.App.Analyses;

namespace OpenSim.App.Tests.Analyses;

/// <summary>OP-9: the update scheduler behind every analysis window — coalescing within the
/// debounce, cancelling a stale compute, never reporting a superseded result.</summary>
public sealed class DebouncedRunnerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>A debounce that waits for the test to open it instead of for the clock.</summary>
    private sealed class Gate
    {
        private TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waits;

        public Task Delay(TimeSpan _, CancellationToken token)
        {
            Interlocked.Increment(ref Waits);
            return _open.Task.WaitAsync(token);
        }

        public void Open() => _open.TrySetResult();
    }

    [Fact]
    public async Task RequestsWithinTheDebounce_ComputeOnce_WithTheLastRequest()
    {
        var gate = new Gate();
        var runner = new DebouncedRunner<int>(TimeSpan.FromSeconds(1), gate.Delay);
        int computes = 0;
        var results = new List<int>();
        runner.Completed += (_, r) => results.Add(r);

        for (int i = 1; i <= 5; i++)
        {
            int value = i;
            runner.Request(_ => { Interlocked.Increment(ref computes); return value; });
        }
        Assert.Equal(5, gate.Waits);
        Assert.True(runner.IsPending);
        gate.Open();
        await runner.Current.WaitAsync(Wait);

        Assert.Equal(1, computes);
        Assert.Equal(new[] { 5 }, results);
        Assert.False(runner.IsPending);
    }

    [Fact]
    public async Task ARequestDuringACompute_CancelsIt_AndOnlyTheNewResultIsReported()
    {
        var runner = new DebouncedRunner<string>(TimeSpan.Zero);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool firstSawCancellation = false;
        var results = new List<string>();
        int cancelledEvents = 0;
        runner.Completed += (_, r) => { lock (results) results.Add(r); };
        runner.Cancelled += (_, _) => Interlocked.Increment(ref cancelledEvents);

        runner.Request(token =>
        {
            started.SetResult();
            firstSawCancellation = token.WaitHandle.WaitOne(Wait);
            token.ThrowIfCancellationRequested();
            return "stale";
        });
        var first = runner.Current;
        await started.Task.WaitAsync(Wait);
        runner.Request(_ => "fresh");
        await Task.WhenAll(first, runner.Current).WaitAsync(Wait);

        Assert.True(firstSawCancellation);
        Assert.Equal(new[] { "fresh" }, results);
        Assert.Equal(0, cancelledEvents);     // superseded, not cancelled: the newer run reports
    }

    [Fact]
    public async Task ASupersededCompute_ThatIgnoresItsToken_IsStillDropped()
    {
        var runner = new DebouncedRunner<string>(TimeSpan.Zero);
        var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new List<string>();
        runner.Completed += (_, r) => { lock (results) results.Add(r); };

        runner.Request(_ => { started.SetResult(); release.Wait(Wait); return "stale"; });
        var first = runner.Current;
        await started.Task.WaitAsync(Wait);
        runner.Request(_ => "fresh");
        await runner.Current.WaitAsync(Wait);
        release.Set();
        await first.WaitAsync(Wait);

        Assert.Equal(new[] { "fresh" }, results);
    }

    [Fact]
    public async Task Immediate_SkipsTheDebounce()
    {
        var gate = new Gate();
        var runner = new DebouncedRunner<int>(TimeSpan.FromSeconds(1), gate.Delay);
        int result = 0;
        runner.Completed += (_, r) => result = r;

        runner.Request(_ => 7, immediate: true);
        await runner.Current.WaitAsync(Wait);

        Assert.Equal(0, gate.Waits);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task Cancel_StopsTheRun_AndSaysSo()
    {
        var gate = new Gate();
        var runner = new DebouncedRunner<int>(TimeSpan.FromSeconds(1), gate.Delay);
        int computes = 0, cancelled = 0;
        runner.Cancelled += (_, _) => cancelled++;

        runner.Request(_ => ++computes);
        runner.Cancel();
        await runner.Current.WaitAsync(Wait);

        Assert.Equal(0, computes);
        Assert.Equal(1, cancelled);
        Assert.False(runner.IsPending);
    }

    [Fact]
    public async Task AComputeThatThrows_IsReported_NotRaised()
    {
        var runner = new DebouncedRunner<int>(TimeSpan.Zero);
        Exception? failure = null;
        runner.Failed += (_, ex) => failure = ex;

        runner.Request(_ => throw new InvalidOperationException("no system"));
        await runner.Current.WaitAsync(Wait);

        Assert.Equal("no system", Assert.IsType<InvalidOperationException>(failure).Message);
    }
}
