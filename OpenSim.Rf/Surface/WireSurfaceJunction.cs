using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// Where a thin wire ends ON a surface: the geometry and the admissibility rules, separated from
/// the matrix assembly so they can be gated on their own.
///
/// <para>The physical content is the classical attachment mode. The wire arrives carrying current
/// I; that current cannot stop, so it spreads radially into the sheet as a 1/ρ disc centred on
/// the attachment point, and the disc's ∇·D = δ²(v) cancels the wire's endpoint charge delta
/// EXACTLY — no point charge is left at the junction. Beyond the fan the disc is continued into
/// each neighbouring triangle by a half-RWG weighted γ = θ/(2πl), so the fraction of I leaving
/// through each wedge is its angular share. Those are the Stage E facts, unchanged; what is new
/// here is that the wire need not be perpendicular.</para>
///
/// <para><b>The fan stays in the SHEET plane at any wire angle.</b> The disc exists to absorb the
/// wire's endpoint charge, and that requirement is on the DIVERGENCE — it fixes the total current
/// the disc carries away, not the direction the wire came from. So an oblique wire uses the same
/// in-plane fan; only the magnitude of the arriving current enters. What obliqueness does change
/// is the wire's own near-field over the sheet, and that is carried by the ordinary mixed block
/// (<see cref="WireSurfaceCoupling"/>), not by the junction.</para>
///
/// <para>Grazing incidence is refused rather than approximated: as the wire flattens toward the
/// sheet its reduced-kernel tube starts to intersect the metal it is attaching to, and the
/// thin-wire model stops describing anything. That is a modelling limit, so it is named.</para>
/// </summary>
internal sealed class WireSurfaceJunction
{
    /// <summary>Below this angle between the wire and the sheet plane the attachment is refused.
    /// At 10° a wire of radius a runs within ~6a of the metal along its last 3a of length: the
    /// reduced kernel and the sheet's own current are no longer separable.</summary>
    public const double MinimumIncidenceDegrees = 10.0;

    private WireSurfaceJunction(AttachmentFan fan, int vertex, int wireBasis,
        int wireElement, bool wireEndsAtElementEnd, double incidenceDegrees, double discSign)
    {
        Fan = fan;
        Vertex = vertex;
        WireBasis = wireBasis;
        WireElement = wireElement;
        WireEndsAtElementEnd = wireEndsAtElementEnd;
        IncidenceDegrees = incidenceDegrees;
        DiscSign = discSign;
    }

    /// <summary>The in-plane 1/ρ attachment fan anchored at the contact vertex.</summary>
    public AttachmentFan Fan { get; }

    /// <summary>The sheet vertex the wire lands on.</summary>
    public int Vertex { get; }

    /// <summary>The wire basis whose half-hat at the attached end becomes the junction's wire
    /// leg — the analogue of the probe tube's top half hat.</summary>
    public int WireBasis { get; }

    /// <summary>The wire element that touches the sheet.</summary>
    public int WireElement { get; }

    /// <summary>True when the contact is at that element's END node rather than its start.</summary>
    public bool WireEndsAtElementEnd { get; }

    /// <summary>The angle between the wire's last element and the sheet plane, in degrees.
    /// Reported so a caller can state how oblique the attachment was.</summary>
    public double IncidenceDegrees { get; }

    /// <summary>The sign the attachment fan carries inside the junction basis,
    /// <c>J = (wire half hat) + DiscSign·(D + Σγᵢ Hᵢ)</c>.
    ///
    /// <para>It is fixed by the ONLY thing the disc exists for: cancelling the wire half hat's
    /// endpoint charge. Integrating ∇·J over a small ball at the contact gives the current
    /// LEAVING it, so a half hat that peaks at the contact (the wire ENDS there) delivers current
    /// inward and contributes −δ, which the disc's own +δ² cancels — sign +1, the coaxial probe's
    /// case. A half hat that peaks at the contact and falls AWAY along the wire (the wire STARTS
    /// there, which is how a monopole standing on a plate is built) carries current outward and
    /// contributes +δ, so the disc must run the other way: sign −1, and the sheet current then
    /// flows radially INWARD to feed the wire, which is the physically obvious answer.</para>
    ///
    /// <para>Getting this wrong leaves 2δ of point charge at the junction rather than none, and
    /// it does not announce itself as an exception — it was measured live as a NEGATIVE input
    /// resistance (−38 Ω) and negative radiated power on a structure that is passive by
    /// construction.</para></summary>
    public double DiscSign { get; }

    /// <summary>Resolve the attachment of <paramref name="wire"/>'s free end onto
    /// <paramref name="surface"/>. Every failure is typed and names the geometric fact behind
    /// it — a guessed attachment point is a wrong answer, not an approximate one.</summary>
    /// <param name="wire">The wire; one of its two ends must land on the sheet.</param>
    /// <param name="surface">The sheet (all metal coplanar).</param>
    /// <param name="discRadius">The 1/ρ disc's inner radius. Omit it and the wire's own radius at
    /// the contact element is used, which is the physical answer — the junction current rides the
    /// wire's tube — and spares the caller from having to resolve which end attaches first.</param>
    public static WireSurfaceJunction Attach(WireStructure wire, SurfaceStructure surface,
        double? discRadius = null)
    {
        if (wire.IsLoop)
            throw new ArgumentException(
                "A closed loop has no free end to attach to the sheet.", nameof(wire));
        if (discRadius is <= 0)
            throw new ArgumentOutOfRangeException(nameof(discRadius));

        // The sheet must be planar: the fan and its 1/ρ disc are in-plane constructions.
        var (normal, planeZ, diameter) = PlaneOf(surface);

        double tolerance = 1e-9 * Math.Max(diameter, 1e-12);

        // Which basis carries the attachment? The one with a single LEG — the half hat whose
        // other half the junction transports across the contact. A two-leg basis is an ordinary
        // interior rooftop and would close the current inside the wire; no single-leg basis at
        // all means a free end, where the junction would transport nothing. Finding it by SHAPE
        // rather than by position in an ordered run is what lets a BRANCHED wire attach too, and
        // it retires an internal assumption about how bases are numbered.
        int wireBasis = -1;
        for (int b = 0; b < wire.BasisCount; b++)
        {
            if (wire.BasisHalves(b).Count != 1) continue;
            if (wireBasis >= 0)
                throw new ArgumentException(
                    "Two wire ends carry an attachment half hat, so which one meets this sheet is "
                    + "ambiguous — a wire bridging two sheets is a named follow-up. Build the wire "
                    + "with exactly one anchored end.", nameof(wire));
            wireBasis = b;
        }
        if (wireBasis < 0)
            throw new ArgumentException(
                "No wire end carries a half hat, so no basis carries current at the contact and "
                + "the junction would transport nothing. Build the wire with the attaching end "
                + "marked grounded (with a null ground plane) so it carries the attachment half "
                + "hat.", nameof(wire));

        var leg = wire.BasisHalves(wireBasis)[0];
        int element = leg.Element;
        // The leg RISES into the contact when the wire ENDS on the sheet and falls away from it
        // when the wire STARTS there. That is the same geometric fact the disc sign turns on, so
        // reading it off the basis keeps the two from ever disagreeing.
        bool atEnd = leg.Rising;
        var contact = wire.Nodes[wire.BasisNode(wireBasis)];
        double gap = Math.Abs(Vector3D.Dot(contact, normal) - planeZ);
        if (gap > tolerance)
            throw new ArgumentException(
                $"The attaching wire end lies {gap:g3} m off the sheet plane — an attached wire "
                + "must terminate ON the metal.", nameof(wire));

        // ...and on a mesh VERTEX, which is where the fan can be anchored.
        int vertex = -1;
        double best = double.MaxValue;
        for (int v = 0; v < surface.Vertices.Count; v++)
        {
            double dist = (surface.Vertices[v] - contact).Length;
            if (dist < best) { best = dist; vertex = v; }
        }
        if (best > tolerance)
            throw new ArgumentException(
                $"The wire lands at ({contact.X:g6}, {contact.Y:g6}) which is not a mesh vertex "
                + $"(nearest is {best:g3} m away) — build the mesh with that point snapped in, so "
                + "the attachment fan has its anchor.", nameof(wire));

        var direction = wire.ElementDirection(element);
        double sinIncidence = Math.Abs(Vector3D.Dot(direction, normal));
        double incidence = Math.Asin(Math.Clamp(sinIncidence, 0, 1)) * 180 / Math.PI;
        if (incidence < MinimumIncidenceDegrees)
            throw new ArgumentException(
                $"The wire meets the sheet at {incidence:g3}°, below the {MinimumIncidenceDegrees:g3}° "
                + "limit: at grazing incidence the wire's reduced-kernel tube overlaps the metal it "
                + "attaches to and the thin-wire model no longer describes it. Steepen the "
                + "approach, or model that run as sheet metal.", nameof(wire));

        AttachmentFan fan;
        try { fan = new AttachmentFan(surface, vertex, discRadius ?? wire.ElementRadii[element]); }
        catch (InvalidOperationException e)
        {
            throw new ArgumentException(
                $"The attachment point is not usable: {e.Message}", nameof(wire), e);
        }
        return new WireSurfaceJunction(fan, vertex, wireBasis, element, atEnd, incidence,
            discSign: atEnd ? 1.0 : -1.0);
    }

    /// <summary>The sheet's plane: its unit normal, the plane's offset along that normal, and the
    /// mesh diameter (the length scale every tolerance here is relative to). Refuses a
    /// non-planar sheet, since the disc and its wedges are in-plane by construction.</summary>
    private static (Vector3D Normal, double Offset, double Diameter) PlaneOf(SurfaceStructure surface)
    {
        var (ia, ib, ic) = surface.Triangles[0];
        var normal = Vector3D.Cross(surface.Vertices[ib] - surface.Vertices[ia],
                                    surface.Vertices[ic] - surface.Vertices[ia]);
        double length = normal.Length;
        if (length <= 0)
            throw new ArgumentException("The sheet's first triangle is degenerate.", nameof(surface));
        normal = normal * (1.0 / length);
        double offset = Vector3D.Dot(surface.Vertices[ia], normal);

        double diameter = 0;
        var first = surface.Vertices[0];
        double spread = 0;
        foreach (var v in surface.Vertices)
        {
            diameter = Math.Max(diameter, (v - first).Length);
            spread = Math.Max(spread, Math.Abs(Vector3D.Dot(v, normal) - offset));
        }
        if (spread > 1e-9 * Math.Max(diameter, 1e-12))
            throw new ArgumentException(
                $"A wire attachment needs a PLANAR sheet (out-of-plane spread {spread:g3} m found) "
                + "— the 1/ρ disc and its wedges are in-plane constructions.", nameof(surface));
        return (normal, offset, diameter);
    }
}
