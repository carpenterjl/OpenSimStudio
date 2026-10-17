using OpenSim.Solvers.Magnetics;

namespace OpenSim.Pcb.Magnetics;

/// <summary>
/// An ER (or E) core around a round centre post: post radius <see cref="PostRadius"/>, a winding
/// window <see cref="WindowWidth"/> wide from the post's edge to the legs' inner faces, two
/// rectangular outer legs <see cref="LegWidth"/> (radial) × <see cref="Depth"/> (the core's depth),
/// plates <see cref="PlateThickness"/> thick above and below a window <see cref="WindowHeight"/>
/// tall, and an air gap <see cref="CentreGap"/> across the post at mid-height.
///
/// <para>The round post is axisymmetric; the square legs are not. <see cref="ToAxisymmetric"/>
/// maps them to the area-preserving cylindrical wall for the winding solve, and
/// <see cref="PlanarLegSection"/> builds the planar cut through the legs' plane — the legs as
/// they are, for their flux density and saturation, which the wall cannot show.</para>
/// </summary>
public sealed record ErCore(double PostRadius, double WindowWidth, double LegWidth, double Depth,
    double PlateThickness, double WindowHeight, double CentreGap, MagneticMaterial Material)
{
    /// <summary>Inner radius of the legs (the window's outer edge).</summary>
    public double WindowRadius => PostRadius + WindowWidth;

    /// <summary>The axisymmetric equivalent: the round post maps exactly; the two legs become a
    /// cylindrical wall whose cross-section preserves their area, π(R_o² − R_w²) = 2·LegWidth·Depth,
    /// so the wall carries the legs' flux at the legs' flux density.</summary>
    public AxisymmetricCore ToAxisymmetric()
    {
        Validate();
        double rw = WindowRadius;
        double ro = Math.Sqrt(rw * rw + 2 * LegWidth * Depth / Math.PI);
        return new AxisymmetricCore(PostRadius, rw, ro, PlateThickness, WindowHeight, CentreGap, Material);
    }

    /// <summary>
    /// The planar (per metre of depth) model of the cut through the legs' plane, symmetric about
    /// x = 0: the post as a 2·PostRadius slab (regions "post low"/"post high", or "post" without a
    /// gap), the legs as they are ("leg left", "leg right"), the plates ("plate top",
    /// "plate bottom"), and the window's ampere-turns as uniform blocks (stranded coil "window",
    /// out of the plane on the right of the post). Read the legs' flux density from it; absolute
    /// per-metre quantities scale by <see cref="Depth"/> only approximately (the post is round).
    /// </summary>
    public MagneticModel2D PlanarLegSection(double ampereTurns, double? minElement = null)
    {
        Validate();
        double rw = WindowRadius, outer = rw + LegWidth, halfWindow = WindowHeight / 2;
        double top = halfWindow + PlateThickness;
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Planar,
            DomainX0 = -3 * outer, DomainX1 = 3 * outer,
            DomainY0 = -top - 2 * outer, DomainY1 = top + 2 * outer,
            MaxElement = outer / 6,
            MinElement = minElement ?? Math.Max(Math.Min(Math.Min(PlateThickness, LegWidth) / 4,
                CentreGap > 0 ? CentreGap / 2 : double.MaxValue), 1e-6)
        };
        var m = Material;
        model.Regions.Add(new MagneticRegion("plate bottom", -outer, outer, -top, -halfWindow, m));
        model.Regions.Add(new MagneticRegion("plate top", -outer, outer, halfWindow, top, m));
        model.Regions.Add(new MagneticRegion("leg left", -outer, -rw, -halfWindow, halfWindow, m));
        model.Regions.Add(new MagneticRegion("leg right", rw, outer, -halfWindow, halfWindow, m));
        if (CentreGap > 0)
        {
            model.Regions.Add(new MagneticRegion("post low", -PostRadius, PostRadius, -halfWindow, -CentreGap / 2, m));
            model.Regions.Add(new MagneticRegion("post high", -PostRadius, PostRadius, CentreGap / 2, halfWindow, m));
        }
        else model.Regions.Add(new MagneticRegion("post", -PostRadius, PostRadius, -halfWindow, halfWindow, m));
        model.Regions.Add(new MagneticRegion("window right", PostRadius, rw, -halfWindow, halfWindow, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("window left", -rw, -PostRadius, -halfWindow, halfWindow, MagneticMaterial.Air));
        model.Coils.Add(new StrandedCoil("window",
            new[] { ("window right", 1), ("window left", -1) }, Turns: 1, Current: ampereTurns));
        return model;
    }

    private void Validate()
    {
        if (!(PostRadius > 0 && WindowWidth > 0 && LegWidth > 0 && Depth > 0
            && PlateThickness > 0 && WindowHeight > 0) || CentreGap < 0)
            throw new ArgumentException("ER core dimensions must be positive (the centre gap may be zero).");
        if (CentreGap >= WindowHeight)
            throw new ArgumentException("The centre gap does not fit in the window.");
    }
}
