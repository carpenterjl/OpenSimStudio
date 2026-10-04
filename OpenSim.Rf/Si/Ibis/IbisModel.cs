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
/// <param name="RFixtureOhms">R_fixture (scalar; the spec has no min/max form).</param>
/// <param name="VFixture">V_fixture as Typ, with V_fixture_min / V_fixture_max as Min / Max
/// when the file gives them ("when the fixture voltage is related to the power supply
/// voltages") — <see cref="IbisCorner.At"/> falls back to V_fixture when they are absent.</param>
/// <param name="Rows">The sampled V(t) rows.</param>
public sealed record IbisWaveform(
    double RFixtureOhms, IbisCorner VFixture, IReadOnlyList<IbisVtRow> Rows);

/// <summary>One [Ramp] edge as Δv over Δt (min/typ/max); the slew rate is Δv/Δt. By the
/// keyword's definition Δv is the 20 % to 80 % part of the swing into the ramp's test load,
/// so the straight line through it covers the whole swing in Δt / 0.6.</summary>
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

    /// <summary>[Pullup] rows. IBIS axis: <c>V_table = V_ref − V_out</c> ("Vcc relative"), with
    /// V_ref = <see cref="PullupRailAt"/> ([Pullup Reference], else [Voltage Range]). Current is
    /// INTO the pad, so a pull-up sourcing current into the line reads NEGATIVE, and a 100 Ω
    /// pull-up to 3.3 V has the row (3.3 V, −33 mA). Evaluate through
    /// <see cref="IbisTableAxis.SupplyReferenced"/>.</summary>
    public IReadOnlyList<IbisIvRow> Pullup { get; init; } = Array.Empty<IbisIvRow>();

    /// <summary>[Pulldown] rows. IBIS axis: <c>V_table = V_out − V_ref</c> with V_ref =
    /// <see cref="PulldownRailAt"/> ([Pulldown Reference], else 0 V) — EXCEPT for ECL model
    /// types (<see cref="IsEcl"/>), whose [Pulldown] is "Vcc relative" exactly like the
    /// pull-up: <c>V_table = V_ref − V_out</c>, V_ref defaulting to the pull-up rail. Current
    /// INTO the pad. (IBIS [Pulldown] keyword, "Other Notes": "When tabulating data for ECL
    /// models … in BOTH of these cases, the data is referenced to the Vcc supply voltage, using
    /// the equation: Vtable = Vcc - Voutput.")</summary>
    public IReadOnlyList<IbisIvRow> Pulldown { get; init; } = Array.Empty<IbisIvRow>();

    /// <summary>[GND Clamp] rows. Axis <c>V_table = V_out − V_ref</c>, V_ref =
    /// <see cref="GndClampRailAt"/> ([GND Clamp Reference], else 0 V) — ground-referenced for
    /// ECL models too. Current INTO the pad, so the clamp conducting below ground reads
    /// negative at negative V_table.</summary>
    public IReadOnlyList<IbisIvRow> GndClamp { get; init; } = Array.Empty<IbisIvRow>();

    /// <summary>[POWER Clamp] rows. Axis <c>V_table = V_ref − V_out</c> ("Vcc relative"),
    /// V_ref = <see cref="PowerClampRailAt"/> ([POWER Clamp Reference], else the pull-up
    /// rail). Current INTO the pad, so the clamp conducting above the rail reads POSITIVE at
    /// NEGATIVE V_table.</summary>
    public IReadOnlyList<IbisIvRow> PowerClamp { get; init; } = Array.Empty<IbisIvRow>();
    public IbisRamp? Ramp { get; init; }
    public IReadOnlyList<IbisWaveform> RisingWaveforms { get; init; } = Array.Empty<IbisWaveform>();
    public IReadOnlyList<IbisWaveform> FallingWaveforms { get; init; } = Array.Empty<IbisWaveform>();
    public IbisCorner? VoltageRange { get; init; }

    /// <summary>[Pullup Reference] / [Pulldown Reference] / [GND Clamp Reference] /
    /// [POWER Clamp Reference] as the typ/min/max triples the spec defines ("Provide actual
    /// voltages … in the typ, min, max format"). Null when the keyword is absent; the
    /// <c>…RailAt(corner)</c> members apply the spec's defaults. There is deliberately no
    /// scalar rail property: a Min run of a 3.3 / 3.0 / 3.6 V part must drive against 3.0 V,
    /// and a caller that could reach for "the" rail would silently get 3.3.</summary>
    public IbisCorner? PullupReference { get; init; }
    public IbisCorner? PulldownReference { get; init; }
    public IbisCorner? GndClampReference { get; init; }
    public IbisCorner? PowerClampReference { get; init; }

    /// <summary>The supply rail the pull-up pulls toward: [Pullup Reference] if present, else
    /// [Voltage Range], each at the requested corner. A Min/Max run of a part whose keywords
    /// list corners drives against THAT corner's rail — using Typ there would silently report
    /// a typical-supply swing for a worst-case run.</summary>
    public double PullupRailAt(IbisCornerSelection corner) =>
        PullupReference?.At(corner) ?? VoltageRange?.At(corner) ?? 0;

    /// <summary>The rail the PULL-DOWN table is referenced to — [Pulldown Reference] at the
    /// corner when the file gives one; else ground for a non-ECL model ("If this keyword is
    /// not present, the voltage data points in the [Pulldown] I-V table are referenced to
    /// 0 V"), and the PULL-UP rail for an ECL model, whose [Pulldown] the spec tabulates "Vcc
    /// relative" (see <see cref="Pulldown"/>). Non-zero on split-rail parts, where assuming
    /// ground shifts the whole pull-down characteristic.</summary>
    public double PulldownRailAt(IbisCornerSelection corner) =>
        PulldownReference?.At(corner) ?? (IsEcl ? PullupRailAt(corner) : 0);

    /// <summary>The rail the GND-clamp table is referenced to — [GND Clamp Reference] at the
    /// corner when given, else ground.</summary>
    public double GndClampRailAt(IbisCornerSelection corner) =>
        GndClampReference?.At(corner) ?? 0;

    /// <summary>True for the ECL model types (Input_ECL, Output_ECL, I/O_ECL, 3-state_ECL),
    /// which "follow different conventions for the [Pulldown] keyword": the pull-down table is
    /// tabulated against V_ref − V_out like the pull-up, not V_out − V_ref.</summary>
    public bool IsEcl => ModelType.Contains("ECL", StringComparison.OrdinalIgnoreCase);

    /// <summary>The rail the POWER-clamp table is referenced to — [POWER Clamp Reference] at
    /// the corner when given, else the pull-up rail. These differ on a part whose clamp diode
    /// returns to a different supply than the output stage pulls to, and the clamp then
    /// conducts at the wrong voltage if the pull-up rail is substituted.</summary>
    public double PowerClampRailAt(IbisCornerSelection corner) =>
        PowerClampReference?.At(corner) ?? PullupRailAt(corner);

    /// <summary>True when this model has the output stage of a driver: a pull-up and a
    /// pull-down, or — for an Open_* model type (open drain, open sink, open source), which
    /// has only one switching device by construction — the one table it carries.</summary>
    public bool IsOutput =>
        (Pullup.Count > 0 && Pulldown.Count > 0)
        || (IsOpenStage && (Pullup.Count > 0 || Pulldown.Count > 0));

    /// <summary>True for the open-drain / open-sink / open-source model types, whose output
    /// stage is a single switching device working against an external termination.</summary>
    public bool IsOpenStage => ModelType.Contains("open", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when [Model_type] names a buffer that can DRIVE (Output, 3-state,
    /// Open_drain/sink/source, I/O and their variants). An empty or unrecognized type is
    /// reported as drivable — a file that never claimed a type should not be refused on the
    /// strength of a type check.</summary>
    public bool TypeIsDriverCapable
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ModelType)) return true;
            string t = ModelType.Trim();
            if (t.Contains("output", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Contains("3-state", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Contains("open", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Contains("i/o", StringComparison.OrdinalIgnoreCase)) return true;
            // Input / Terminator / Series* cannot drive; anything unrecognized is allowed.
            return !(t.StartsWith("input", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("terminator", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("series", StringComparison.OrdinalIgnoreCase));
        }
    }
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
