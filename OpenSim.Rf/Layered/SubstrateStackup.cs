namespace OpenSim.Rf.Layered;

/// <summary>
/// The v1 layered medium: ONE dielectric slab on an infinite PEC ground — PEC at
/// z = ground, dielectric (εr, tanδ) filling a thickness of <see cref="ThicknessMeters"/>
/// above it, free space beyond, and ALL metal in the single plane at the top of the
/// slab. That scope is what keeps the Green's function one-dimensional in lateral ρ;
/// vias/probes, multi-level metal, and multiple slabs are named future items, not
/// silent approximations.
/// </summary>
public sealed record SubstrateStackup
{
    /// <summary>Relative permittivity εr of the slab (≥ 1; εr = 1 is an air spacer).</summary>
    public double RelativePermittivity { get; }

    /// <summary>Dielectric loss tangent tanδ (≥ 0); εc = εr(1 − j·tanδ).</summary>
    public double LossTangent { get; }

    /// <summary>Slab thickness in meters (metal sits at this height above the ground).</summary>
    public double ThicknessMeters { get; }

    public SubstrateStackup(double relativePermittivity, double lossTangent, double thicknessMeters)
    {
        if (relativePermittivity < 1)
            throw new ArgumentOutOfRangeException(nameof(relativePermittivity),
                "The substrate εr must be ≥ 1 — a value below vacuum is not a physical dielectric.");
        if (lossTangent < 0)
            throw new ArgumentOutOfRangeException(nameof(lossTangent),
                "The loss tangent must be ≥ 0.");
        if (thicknessMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(thicknessMeters),
                "The substrate thickness must be positive — for no substrate use the free-space or ground-image path.");
        RelativePermittivity = relativePermittivity;
        LossTangent = lossTangent;
        ThicknessMeters = thicknessMeters;
    }
}
