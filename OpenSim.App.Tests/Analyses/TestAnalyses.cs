using OpenSim.Core.PostProcessing;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;

namespace OpenSim.App.Tests.Analyses;

/// <summary>Settings of the test analyses.</summary>
public sealed record CountSettings
{
    public int Factor { get; init; } = 1;
    public double Spacing { get; init; } = 0.5;
    public double? Limit { get; init; }
    public bool Flag { get; init; }
    public OpticsMode Mode { get; init; } = OpticsMode.Sequential;
    public string Note { get; init; } = "";
}

/// <summary>An analysis that counts its computes and reports the mode it saw.</summary>
public sealed class CountingAnalysis : OpticsAnalysis<CountSettings>
{
    private int _calls;

    public CountingAnalysis(string id = "Counting", string name = "Counting") { Id = id; Name = name; }

    public override string Id { get; }
    public override string Name { get; }
    public override string Category => "Test";

    public int Calls => Volatile.Read(ref _calls);

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, CountSettings settings,
        CancellationToken cancellation)
    {
        Interlocked.Increment(ref _calls);
        return OpticsAnalysisResult.FromSections(Name,
            new[] { new ReportSection("Seen") { Pairs = new[] { ("Mode", OpticsModes.Label(context.Mode)), ("Factor", settings.Factor.ToString()) } } },
            new[] { "Counted, not traced." });
    }
}

/// <summary>An analysis whose first compute runs until it is cancelled: the "long analysis".</summary>
public sealed class SlowAnalysis : OpticsAnalysis<CountSettings>
{
    private int _started, _cancelled, _finished;

    public override string Id => "Slow";
    public override string Name => "Slow";
    public override string Category => "Test";

    public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Started => Volatile.Read(ref _started);
    public int Cancelled => Volatile.Read(ref _cancelled);
    public int Finished => Volatile.Read(ref _finished);

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, CountSettings settings,
        CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref _started) == 1)
        {
            FirstStarted.SetResult();
            cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            if (cancellation.IsCancellationRequested) Interlocked.Increment(ref _cancelled);
            cancellation.ThrowIfCancellationRequested();
        }
        Interlocked.Increment(ref _finished);
        return new OpticsAnalysisResult { Text = "Mode " + OpticsModes.Label(context.Mode) };
    }
}
