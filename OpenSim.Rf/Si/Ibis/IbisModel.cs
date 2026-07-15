namespace OpenSim.Rf.Si.Ibis;

/// <summary>An IBIS min/typ/max corner triple. Typ is the primary value; Min/Max are the
/// process corners (either may be null when the file lists "NA").</summary>
public readonly record struct IbisCorner(double? Typ, double? Min, double? Max)
{
    /// <summary>The value at the requested corner, falling back to Typ when the corner is NA.</summary>
    public double? At(IbisCornerSelection corner) => corner switch
    {
        IbisCornerSelection.Min => Min ?? Typ,
        IbisCornerSelection.Max => Max ?? Typ,
        _ => Typ,
    };
}

/// <summary>Which process corner to drive an IBIS model at.</summary>
public enum IbisCornerSelection { Typ, Min, Max }

/// <summary>One row of a V-I table: the terminal voltage and the current (min/typ/max) into
/// the device at that voltage.</summary>
public readonly record struct IbisIvRow(double VoltageVolts, IbisCorner CurrentAmps);

/// <summary>One row of a V-T waveform: time and the output voltage (min/typ/max).</summary>
public readonly record struct IbisVtRow(double TimeSeconds, IbisCorner VoltageVolts);

/// <summary>A [Rising Waveform] / [Falling Waveform]: the fixture it was measured into
/// (R_fixture to V_fixture) and the sampled V(t) rows. The two-waveform switching-coefficient
/// extraction uses the fixture to back out Ku(t)/Kd(t).</summary>
public sealed record IbisWaveform(
    double RFixtureOhms, double VFixtureVolts, IReadOnlyList<IbisVtRow> Rows);

/// <summary>One [Ramp] edge as Δv over Δt (min/typ/max); the slew rate is Δv/Δt.</summary>
public sealed record IbisRampEdge(IbisCorner DeltaVolts, IbisCorner DeltaSeconds);

/// <summary>The [Ramp] block: the rising and falling edge slews (the fallback switching
/// model when [Rising/Falling Waveform] tables are absent).</summary>
public sealed record IbisRamp(IbisRampEdge Rising, IbisRampEdge Falling);

/// <summary>
/// One IBIS [Model]: the behavioral I/O buffer. The V-I tables are the nonlinear device
/// currents (pull-up/-down output stages + protection clamps), C_comp the die capacitance,
/// and the ramp / waveforms the switching behavior. All numeric fields carry the
/// min/typ/max corners as parsed. Empty tables mean the keyword was absent.
/// </summary>
public sealed record IbisModel
{
    public required string Name { get; init; }
    public required string ModelType { get; init; }
    public IbisCorner CComp { get; init; }
    public IReadOnlyList<IbisIvRow> Pullup { get; init; } = Array.Empty<IbisIvRow>();
    public IReadOnlyList<IbisIvRow> Pulldown { get; init; } = Array.Empty<IbisIvRow>();
    public IReadOnlyList<IbisIvRow> GndClamp { get; init; } = Array.Empty<IbisIvRow>();
    public IReadOnlyList<IbisIvRow> PowerClamp { get; init; } = Array.Empty<IbisIvRow>();
    public IbisRamp? Ramp { get; init; }
    public IReadOnlyList<IbisWaveform> RisingWaveforms { get; init; } = Array.Empty<IbisWaveform>();
    public IReadOnlyList<IbisWaveform> FallingWaveforms { get; init; } = Array.Empty<IbisWaveform>();
    public IbisCorner? VoltageRange { get; init; }
    public double? PullupReferenceVolts { get; init; }
    public double? PulldownReferenceVolts { get; init; }
    public double? GndClampReferenceVolts { get; init; }
    public double? PowerClampReferenceVolts { get; init; }

    /// <summary>The supply rail the pull-up pulls toward: [Pullup Reference] if present, else
    /// the top of [Voltage Range].</summary>
    public double PullupRail => PullupReferenceVolts ?? VoltageRange?.Typ ?? 0;

    /// <summary>True when this model has the pull-up + pull-down output stage of a driver.</summary>
    public bool IsOutput => Pullup.Count > 0 && Pulldown.Count > 0;
}

/// <summary>A parsed IBIS file: its component name (if any), the models, and any warnings
/// (unsupported keywords skipped, not fatal).</summary>
public sealed record IbisFile(
    string? Component, IReadOnlyList<IbisModel> Models, IReadOnlyList<string> Warnings)
{
    /// <summary>The model by name (case-insensitive), or a typed failure naming the choices.</summary>
    public IbisModel Model(string name)
    {
        foreach (var m in Models)
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) return m;
        throw new ArgumentException(
            $"No IBIS model '{name}'. Available: {string.Join(", ", Models.Select(m => m.Name))}.");
    }
}
