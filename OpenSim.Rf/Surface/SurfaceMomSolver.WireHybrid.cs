using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>One frequency point of a hybrid wire-attached surface solve: the impedance seen at
/// the delta-gap feed, the sheet's edge currents (with the junction's transported current folded
/// onto the fan outer edges, for generic consumers), the wire's basis currents (the attachment
/// basis carrying the junction coefficient), the RAW un-folded edge currents so the accurate far
/// field can add the junction's disc and half-RWG currents exactly, and the measured angle at
/// which the wire met the sheet.</summary>
public sealed record WireAttachedSolution(
    double FrequencyHz, Complex InputImpedance,
    Complex[] EdgeCurrents, Complex[] WireCurrents, Complex[] RawEdgeCurrents,
    double IncidenceDegrees)
{
    /// <summary>How unevenly the junction's disc current meets the triangles that continue
    /// it: the worst relative difference between the disc's flux density along a fan outer edge
    /// and the uniform density the continuation assumes. About 0.17 on a regular mesh; above
    /// <see cref="SkewedFanThreshold"/> the contact vertex sits close to one of its neighbours
    /// and the omitted edge line charge is no longer small.</summary>
    public double FanFluxMismatch { get; init; }

    public const double SkewedFanThreshold = 0.5;
}

/// <summary>
/// The wire↔sheet hybrid assembly: a free-space RWG sheet, a thin wire that ENDS on it, and ONE
/// junction unknown carrying the attachment mode across the contact,
///
/// <code>
///   J = wire-end half rooftop + D (the 1/ρ disc on the fan wedges)
///       + Σᵢ γᵢ·(half-RWG on each wedge's outward neighbour),   γᵢ = θᵢ/(2π·lᵢ)
/// </code>
///
/// which is the same construction the coaxial probe uses (<see cref="SolveProbeFed"/>), with two
/// differences and no third. First, the medium is free space, so the disc's only kernel fact is
/// <see cref="FreeSpaceRadialGaKernel"/> through the <see cref="IRadialGaKernel"/> seam and the
/// pair moments are the ordinary free-space ones. Second, the wire may arrive at ANY angle: the
/// fan still lies in the sheet plane, because the disc exists to absorb the wire's endpoint
/// CHARGE and that is a condition on the divergence, not on the arrival direction. What
/// obliqueness changes is the wire's own near field over the sheet, and that is carried by the
/// ordinary mixed block (<see cref="WireSurfaceCoupling"/>).
///
/// <para><b>Why the charge bookkeeping needs no special case.</b> ∇·D = δ²(v) cancels the wire
/// half hat's endpoint delta exactly. The thin-wire solver ALREADY builds a grounded end's basis
/// with a single supporting element and no endpoint term (that is what makes a monopole base
/// work), so asking it for the wire's self block with the attached end marked grounded and NO
/// ground plane produces precisely the charge-neutral half the junction wants. D is then
/// chargeless, the half-RWGs carry the ordinary −l/A, and nothing anywhere carries a point
/// charge.</para>
///
/// <para><b>The index map is what keeps the matrix symmetric.</b> The unknowns are
/// [RWG 0..nE) | wire bases, with the ATTACHED basis mapped onto the single junction column].
/// Every block is then written with <c>+=</c> through that one map, so the junction's diagonal
/// picks up the wire half hat's self term, twice its coupling to the disc and halves, and the
/// surface-surface term, without a single hand-written factor of two.</para>
/// </summary>
public sealed partial class SurfaceMomSolver
{
    /// <summary>Kernel facts every consumer must surface next to wire-attached results.</summary>
    public static IReadOnlyList<string> WireAttachedAssumptions { get; } = new[]
    {
        "Perfect electric conductor, zero-thickness sheet and thin wire (no ohmic loss).",
        "Free space — no dielectric substrate and no image ground plane; a wire over a substrate is named future work.",
        "The sheet is planar and the wire ends ON one of its interior mesh vertices.",
        "Classical 1/ρ attachment mode at the junction: the wire and disc endpoint deltas cancel exactly — no junction point charge.",
        "The disc's flux is handed to the surrounding triangles in total, not point by point along each edge; the zero-net line charge this leaves is neglected (small on a regular mesh, reported when the contact vertex is badly placed).",
        "Thin-wire reduced kernel R = √(d² + a²); the attachment is refused below 10° of incidence, where the wire's tube overlaps the sheet.",
        "Delta-gap voltage feed at one wire basis (feeding the attachment basis is the monopole-over-a-finite-plate case)."
    };

    /// <summary>The wire basis that carries the attachment half hat: feeding there puts the delta
    /// gap AT the contact, which is what a monopole standing on a finite ground plane is. Exposed
    /// because the attachment itself is an internal construction — a caller needs the feed index
    /// without needing the junction's machinery, and computing it by hand would re-derive an
    /// internal numbering rule that has already changed once. Throws the same typed refusals
    /// <see cref="SolveWireAttached"/> would, so an unusable geometry fails before any fill.</summary>
    public static int AttachmentFeedBasis(SurfaceStructure surface, WireStructure wire) =>
        WireSurfaceJunction.Attach(wire, surface).WireBasis;

    /// <summary>Solve a sheet with a wire attached to it, fed by a delta gap at
    /// <paramref name="feedBasis"/> (a WIRE basis index; the attachment basis is legal and means
    /// the gap sits at the contact, the finite-plate monopole).</summary>
    public WireAttachedSolution SolveWireAttached(SurfaceStructure surface, WireStructure wire,
        double frequencyHz, int feedBasis, double gapVolts = 1.0)
    {
        if (frequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Frequency must be positive.");
        if (surface.Ground is not null)
            throw new ArgumentException(
                "A wire-attached solve is FREE SPACE: the sheet must not carry a PEC ground plane. "
                + "The junction's disc reads a free-space radial kernel, and an image pass would need "
                + "its own — named future work.", nameof(surface));
        if (wire.Ground is not null)
            throw new ArgumentException(
                "A wire-attached solve is FREE SPACE: the wire must not carry a PEC ground plane. "
                + "Build it with a null ground and its attached end marked, so the end carries the "
                + "half hat the junction needs.", nameof(wire));
        if (feedBasis < 0 || feedBasis >= wire.BasisCount)
            throw new ArgumentOutOfRangeException(nameof(feedBasis),
                $"Feed basis {feedBasis} is outside 0..{wire.BasisCount - 1}.");

        double omega = 2 * Math.PI * frequencyHz;
        double k = omega / RfConstants.SpeedOfLight;
        var (z, junction, wireIndex) = AssembleWireAttached(surface, wire, k, omega);
        int nEdges = surface.BasisCount;
        int nWire = wire.BasisCount;
        int jIndex = z.Rows - 1;

        // ---- Feed and solve. ----
        var rhs = new Complex[z.Rows];
        int feedIndex = wireIndex[feedBasis];
        rhs[feedIndex] = gapVolts;
        var x = ComplexLu.Factor(z).Solve(rhs);
        Complex feedCurrent = x[feedIndex];
        if (feedCurrent == Complex.Zero)
            throw new InvalidOperationException(
                "The feed carries zero current — the gap sits at a current null of a degenerate structure.");

        var rawEdgeCurrents = new Complex[nEdges];
        var edgeCurrents = new Complex[nEdges];
        for (int e = 0; e < nEdges; e++) rawEdgeCurrents[e] = edgeCurrents[e] = x[e];
        Complex junctionCurrent = x[jIndex];
        foreach (var wedge in junction.Fan.Wedges)
            edgeCurrents[wedge.EdgeBasis] +=
                junction.DiscSign * wedge.OrientationSign * wedge.Gamma * junctionCurrent;

        var wireCurrents = new Complex[nWire];
        for (int b = 0; b < nWire; b++) wireCurrents[b] = x[wireIndex[b]];

        return new WireAttachedSolution(frequencyHz, gapVolts / feedCurrent,
            edgeCurrents, wireCurrents, rawEdgeCurrents, junction.IncidenceDegrees)
        {
            FanFluxMismatch = junction.Fan.OuterFluxMismatch
        };
    }

    /// <summary>The hybrid system itself, exposed so its complex symmetry can be gated directly:
    /// an asymmetric entry means a block was scattered once instead of twice, and no physics
    /// result would name the culprit.</summary>
    internal (ComplexDenseMatrix Z, WireSurfaceJunction Junction, int[] WireIndex)
        AssembleWireAttached(SurfaceStructure surface, WireStructure wire, double k, double omega)
    {
        var jOmega = new Complex(0, omega);

        // The disc's radius floor is the wire's own radius at the contact — the junction current
        // physically rides that tube. Attach resolves which end lands on the sheet, so it is also
        // the only place that knows which element's radius to read.
        var junction = WireSurfaceJunction.Attach(wire, surface);
        var fan = junction.Fan;
        var radial = new FreeSpaceRadialGaKernel(k);

        int nEdges = surface.BasisCount;
        int nWire = wire.BasisCount;
        int attach = junction.WireBasis;

        // Index map: every wire basis gets a column, and the attached one IS the junction.
        var wireIndex = new int[nWire];
        int next = nEdges;
        for (int b = 0; b < nWire; b++) wireIndex[b] = b == attach ? -1 : next++;
        int jIndex = next;
        int total = jIndex + 1;
        wireIndex[attach] = jIndex;

        var z = new ComplexDenseMatrix(total, total);

        // ---- Sheet block: the existing bitwise-pinned free-space fill. ----
        var zCc = AssembleImpedanceMatrix(surface, k, omega, MaxDegreeOfParallelism);
        for (int i = 0; i < nEdges; i++)
            for (int j = 0; j < nEdges; j++)
                z[i, j] = zCc[i, j];

        // ---- Wire block: the thin-wire solver's own matrix, mapped through the index map. ----
        var zWw = ThinWireMomSolver.AssembleImpedanceMatrix(wire, k, omega);
        for (int b1 = 0; b1 < nWire; b1++)
            for (int b2 = 0; b2 < nWire; b2++)
                z[wireIndex[b1], wireIndex[b2]] += zWw[b1, b2];

        // ---- Mixed sheet↔wire blocks (the attached basis lands on the junction column). ----
        for (int m = 0; m < nEdges; m++)
            for (int b = 0; b < nWire; b++)
            {
                var value = WireSurfaceCoupling.Mutual(wire, b, surface, m, k, omega);
                z[m, wireIndex[b]] += value;
                z[wireIndex[b], m] += value;   // the identical double: Z stays bitwise symmetric
            }

        // ---- The junction's SURFACE part against every wire basis. ----
        // For the attached basis this lands twice on the diagonal, which is exactly the
        // 2·⟨half hat, E(disc + halves)⟩ cross term — no hand-written factor of two.
        var halves = new WireSurfaceCoupling.SurfaceHalf[fan.Wedges.Count];
        for (int i = 0; i < fan.Wedges.Count; i++)
        {
            var wedge = fan.Wedges[i];
            halves[i] = new WireSurfaceCoupling.SurfaceHalf(
                wedge.NeighborTriangle, -1.0, wedge.NeighborOpposite,
                surface.Edges[wedge.EdgeBasis].Length);
        }
        for (int b = 0; b < nWire; b++)
        {
            var value = WireSurfaceCoupling.MutualDisc(wire, b, fan, surface, k, omega);
            for (int i = 0; i < fan.Wedges.Count; i++)
                value += fan.Wedges[i].Gamma
                    * WireSurfaceCoupling.MutualHalves(wire, b, surface, new[] { halves[i] }, k, omega);
            value *= junction.DiscSign;
            z[wireIndex[b], jIndex] += value;
            z[jIndex, wireIndex[b]] += value;
        }

        // ---- The junction's surface part against the sheet, and against itself. ----
        var (discV, discVHalfTotal) = DiscCouplings(surface, fan, radial, jOmega);
        var (halfRow, halfHalf) = HalfCouplings(surface, fan, k, omega);
        for (int m = 0; m < nEdges; m++)
        {
            Complex value = junction.DiscSign * (discV[m] + halfRow[m]);
            z[m, jIndex] += value;
            z[jIndex, m] += value;
        }
        // The fan-against-fan term is quadratic in DiscSign, so it needs no sign at all.
        z[jIndex, jIndex] += jOmega * fan.DiscSelf(radial, surface) + 2 * discVHalfTotal + halfHalf;

        return (z, junction, wireIndex);
    }

    /// <summary>jω⟨f_m, A(D)⟩ for every RWG basis, and Σᵢγᵢ·jω⟨Hᵢ, A(D)⟩ — the disc's VECTOR
    /// couplings (it is chargeless once its δ² has cancelled the wire's endpoint delta). Both
    /// test points are in the sheet plane, so the disc's lateral-separation potential is the
    /// true one. Transcribed from the probe path with the layered table replaced by the radial
    /// seam — the geometry and quadrature are identical.</summary>
    private static (Complex[] DiscV, Complex HalfTotal) DiscCouplings(SurfaceStructure surface,
        AttachmentFan fan, IRadialGaKernel radial, Complex jOmega)
    {
        var (l1, l2, l3, wq) = TriangleQuadrature.Rule(6);
        var discV = new Complex[surface.BasisCount];
        Complex halfTotal = Complex.Zero;

        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            if (surface.TriangleSupports[t].Count == 0) continue;
            var verts = PVertices(surface, t);
            foreach (var (pa, pb, pc) in OuterPanels(verts, verts,
                new List<Vector3D> { fan.VertexPosition }))
            {
                double panelArea = Vector3D.Cross(pb - pa, pc - pa).Length / 2;
                for (int i = 0; i < wq.Length; i++)
                {
                    var r = pa * l1[i] + pb * l2[i] + pc * l3[i];
                    var (ax, ay) = fan.DiscPotential(radial, surface, r);
                    foreach (var (basis, sign, opposite) in surface.TriangleSupports[t])
                    {
                        var fDir = r - surface.Vertices[opposite];
                        double scale = sign * surface.Edges[basis].Length
                            / (2 * surface.TriangleAreas[t]);
                        discV[basis] += jOmega * wq[i] * panelArea * scale
                            * (fDir.X * ax + fDir.Y * ay);
                    }
                }
            }
        }

        foreach (var wedge in fan.Wedges)
        {
            int t = wedge.NeighborTriangle;
            var verts = PVertices(surface, t);
            var pOpp = surface.Vertices[wedge.NeighborOpposite];
            double lI = surface.Edges[wedge.EdgeBasis].Length;
            Complex sum = Complex.Zero;
            foreach (var (pa, pb, pc) in OuterPanels(verts, verts,
                new List<Vector3D> { fan.VertexPosition }))
            {
                double panelArea = Vector3D.Cross(pb - pa, pc - pa).Length / 2;
                for (int i = 0; i < wq.Length; i++)
                {
                    var r = pa * l1[i] + pb * l2[i] + pc * l3[i];
                    var (ax, ay) = fan.DiscPotential(radial, surface, r);
                    var fDir = pOpp - r;   // the half form (p⁻ − r)
                    sum += wq[i] * panelArea * (fDir.X * ax + fDir.Y * ay);
                }
            }
            halfTotal += wedge.Gamma * jOmega * (lI / (2 * surface.TriangleAreas[t])) * sum;
        }
        return (discV, halfTotal);
    }

    /// <summary>Σᵢγᵢ⟨f_m, E(Hᵢ)⟩ per RWG basis, and ΣΣγᵢγⱼ⟨Hᵢ, E(Hⱼ)⟩ — the half-RWG
    /// continuations against the sheet and against each other, through the ordinary free-space
    /// pair moments. A half is σ = −1 with the neighbour's opposite vertex, i.e. one triangle of
    /// an ordinary RWG, so no new singular family appears.</summary>
    private static (Complex[] HalfRow, Complex HalfHalf) HalfCouplings(SurfaceStructure surface,
        AttachmentFan fan, double k, double omega)
    {
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);
        var halfRow = new Complex[surface.BasisCount];
        Complex halfHalf = Complex.Zero;

        // Heap buffers — stackalloc inside these loops would only release at method exit
        // (the live-testhost StackOverflow lesson).
        var pArr = new Vector3D[3];
        var qArr = new Vector3D[3];
        var pIdx = new int[3];
        var qIdx = new int[3];

        foreach (var wedge in fan.Wedges)
        {
            int q = wedge.NeighborTriangle;
            double lI = surface.Edges[wedge.EdgeBasis].Length;
            double areaQ = surface.TriangleAreas[q];
            var qVerts = PVertices(surface, q);
            (qArr[0], qArr[1], qArr[2]) = qVerts;
            (qIdx[0], qIdx[1], qIdx[2]) = surface.Triangles[q];
            int oppLocalQ = LocalIndex(qIdx, wedge.NeighborOpposite);

            for (int p = 0; p < surface.Triangles.Count; p++)
            {
                if (surface.TriangleSupports[p].Count == 0) continue;
                var moments = PairMoments(surface, p, q, k);
                if (p == q) moments = moments.Symmetrized();
                (pArr[0], pArr[1], pArr[2]) = PVertices(surface, p);
                (pIdx[0], pIdx[1], pIdx[2]) = surface.Triangles[p];
                double areaP = surface.TriangleAreas[p];

                foreach (var (basisM, signM, oppositeM) in surface.TriangleSupports[p])
                {
                    double lM = surface.Edges[basisM].Length;
                    int oppLocalM = LocalIndex(pIdx, oppositeM);
                    Complex dotSum = Complex.Zero;
                    for (int a = 0; a < 3; a++)
                    {
                        var fA = pArr[a] - pArr[oppLocalM];
                        for (int b = 0; b < 3; b++)
                            dotSum += Vector3D.Dot(fA, qArr[b] - qArr[oppLocalQ]) * moments[a, b];
                    }
                    // σ_N = −1: the half current is (l/2A)(p⁻ − r) = −(l/2A)(r − p⁻).
                    Complex vector = vectorFactor
                        * (signM * -1.0 * lM * lI / (4 * areaP * areaQ)) * dotSum;
                    Complex charge = chargeFactor
                        * (signM * -1.0 * lM * lI / (areaP * areaQ)) * moments.M00;
                    halfRow[basisM] += wedge.Gamma * (vector + charge);
                }
            }

            foreach (var wedge2 in fan.Wedges)
            {
                int p2 = wedge2.NeighborTriangle;
                double lJ = surface.Edges[wedge2.EdgeBasis].Length;
                double areaP2 = surface.TriangleAreas[p2];
                (pArr[0], pArr[1], pArr[2]) = PVertices(surface, p2);
                (pIdx[0], pIdx[1], pIdx[2]) = surface.Triangles[p2];
                int oppLocalP2 = LocalIndex(pIdx, wedge2.NeighborOpposite);
                var moments2 = PairMoments(surface, p2, q, k);
                if (p2 == q) moments2 = moments2.Symmetrized();
                Complex dotSum2 = Complex.Zero;
                for (int a = 0; a < 3; a++)
                {
                    var fA = pArr[oppLocalP2] - pArr[a];   // (p⁻ − r) on the test side
                    for (int b = 0; b < 3; b++)
                        dotSum2 += Vector3D.Dot(fA, qArr[oppLocalQ] - qArr[b]) * moments2[a, b];
                }
                Complex vector2 = vectorFactor * (lJ * lI / (4 * areaP2 * areaQ)) * dotSum2;
                Complex charge2 = chargeFactor * (lJ * lI / (areaP2 * areaQ)) * moments2.M00;
                halfHalf += wedge2.Gamma * wedge.Gamma * (vector2 + charge2);
            }
        }
        return (halfRow, halfHalf);
    }
}
