using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>One trace of a coupled-line cross-section: a copper strip of the given width
/// centered at <paramref name="CenterMeters"/> along the lateral axis. Thickness and
/// conductivity feed ONLY the per-unit-length resistance — the electrostatic C/L solve
/// models the strip as zero-thickness (the w ≫ t regime every PCB trace lives in; a
/// stated assumption on the result).</summary>
public sealed record TraceCrossSection(
    double CenterMeters, double WidthMeters, double ThicknessMeters,
    double ConductivitySiemensPerMeter)
{
    /// <summary>Standard 1 oz copper, 0.2 mm wide — a convenient test/wizard default.</summary>
    public static TraceCrossSection Copper(double centerMeters, double widthMeters,
        double thicknessMeters = 35e-6) =>
        new(centerMeters, widthMeters, thicknessMeters, 5.8e7);

    /// <summary>A coplanar ground strip: solved as a conductor of its own and then tied to the
    /// reference (<see cref="RlgcReduction"/>), so it is not a port of the line.</summary>
    public bool IsGround { get; init; }

    /// <summary>Width of the face away from the interface, for an etched (trapezoidal) trace;
    /// null is a rectangle. Only the side-wall model (<see cref="RlgcModel.SideWalls"/>) solves
    /// the slope; the effective-width model takes <see cref="WidthMeters"/>.</summary>
    public double? TopWidthMeters { get; init; }
}

/// <summary>
/// The 2D cross-section of an N-conductor coupled transmission line over a grounded
/// dielectric stackup: every trace lies at the SAME interface of the stackup (the
/// coplanar-metal contract the whole layered track shares — a microstrip when the metal
/// is on top, an embedded line when it is buried under cover layers, a stripline when
/// <see cref="TopGround"/> closes the stack with a second plane). Broadside-coupled
/// conductors on different layers are a typed failure by construction: they cannot be
/// expressed. The lateral axis is x; the stackup provides ε per layer and the PEC ground.
/// </summary>
public sealed record CoupledLineCrossSection
{
    public LayeredStackup Stackup { get; }

    /// <summary>The interface index carrying the conductors (see
    /// <see cref="LayeredStackup.InterfaceHeights"/>); Layers.Count − 1 is the stack top.</summary>
    public int MetalInterface { get; }

    /// <summary>The traces, sorted by center. Overlaps are a typed failure — an overlapped
    /// pair is one conductor drawn twice, and the BEM would return a garbage C matrix.</summary>
    public IReadOnlyList<TraceCrossSection> Traces { get; }

    /// <summary>
    /// True when a second infinite PEC plane closes the TOP of the stackup (a stripline:
    /// the conductors lie between two reference planes). False is the open stack — free
    /// space above the top layer — that <see cref="LayeredStackup"/> describes by itself.
    /// The layers above <see cref="MetalInterface"/> are then the dielectric between the
    /// conductors and the upper plane, so the metal cannot be at the stack top.
    /// </summary>
    public bool TopGround { get; }

    public CoupledLineCrossSection(LayeredStackup stackup, int metalInterface,
        IReadOnlyList<TraceCrossSection> traces, bool topGround = false)
    {
        if (metalInterface < 0 || metalInterface >= stackup.Layers.Count)
            throw new ArgumentOutOfRangeException(nameof(metalInterface),
                $"Metal interface {metalInterface} is out of range for a "
                + $"{stackup.Layers.Count}-layer stackup.");
        if (topGround && metalInterface == stackup.Layers.Count - 1)
            throw new ArgumentException(
                "With a top ground plane the conductors need at least one dielectric layer "
                + "above them — metal at the stack top would lie on the plane itself.",
                nameof(metalInterface));
        if (traces is null || traces.Count == 0)
            throw new ArgumentException("At least one trace is required.", nameof(traces));
        foreach (var trace in traces)
        {
            if (trace.WidthMeters <= 0)
                throw new ArgumentException("Every trace needs a positive width.", nameof(traces));
            if (trace.ThicknessMeters <= 0 || trace.ConductivitySiemensPerMeter <= 0)
                throw new ArgumentException(
                    "Every trace needs a positive thickness and conductivity (they set R).",
                    nameof(traces));
        }
        var sorted = traces.OrderBy(t => t.CenterMeters).ToArray();
        for (int i = 1; i < sorted.Length; i++)
        {
            double gap = (sorted[i].CenterMeters - sorted[i].WidthMeters / 2)
                       - (sorted[i - 1].CenterMeters + sorted[i - 1].WidthMeters / 2);
            if (gap <= 0)
                throw new ArgumentException(
                    $"Traces {i - 1} and {i} overlap or touch (gap {gap * 1e6:g3} µm) — "
                    + "merge them or separate them; an overlapped pair is not a coupled line.",
                    nameof(traces));
        }
        Stackup = stackup;
        MetalInterface = metalInterface;
        Traces = sorted;
        TopGround = topGround;
        GroundIndices = Enumerable.Range(0, sorted.Length).Where(i => sorted[i].IsGround).ToArray();
        if (GroundIndices.Count == sorted.Length)
            throw new ArgumentException("Every conductor is a ground strip; a line needs at least one signal trace.", nameof(traces));
    }

    /// <summary>Indices (into <see cref="Traces"/>) of the coplanar ground strips.</summary>
    public IReadOnlyList<int> GroundIndices { get; }

    /// <summary>The signal traces, in order: the line's ports.</summary>
    public IReadOnlyList<TraceCrossSection> SignalTraces => Traces.Where(t => !t.IsGround).ToArray();
}
