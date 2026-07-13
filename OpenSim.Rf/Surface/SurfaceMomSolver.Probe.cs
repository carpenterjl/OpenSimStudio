using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>One frequency point of a probe-fed layered solve: the surface solution
/// (input impedance seen by the REAL probe port, edge currents with the junction's
/// transported current folded onto the fan outer edges for generic consumers) plus the
/// tube current per probe node (index 0 = ground contact = the port current; last = the
/// junction current entering the patch), and the RAW (un-folded) edge currents so the
/// accurate far field can add the junction's disc + half-RWG current exactly instead of
/// through the mesh-scale fold.</summary>
public sealed record ProbeFedSolution(
    SurfaceMomSolution Surface, Complex[] TubeCurrents, Complex[] RawEdgeCurrents);

/// <summary>
/// The probe-fed (coaxial feed) assembly path: the layered RWG system extended by the
/// vertical tube bases and ONE junction unknown,
///
///   J = tube-top half rooftop + D (the 1/ρ disc on the fan wedges)
///       + Σᵢ βᵢ·(half-RWG on each wedge's outward neighbor),
///
/// with βᵢ = ±θᵢ/(2πlᵢ) the closed-form flux continuation of D across the wedge's
/// outer edge. ∇·D = δ²(v) cancels the tube's endpoint delta EXACTLY, so the junction
/// carries no point charge (see <see cref="AttachmentFan"/> for the measured failure
/// of the affine ρ_v fan this replaces); D itself is chargeless, and the junction's
/// distributed charge lives on the tube (line) and the neighbor halves (−l/A) —
/// standard bookkeeping throughout.
///
/// Tube↔surface coupling tests the scalar radial kernels only: the tube's horizontal
/// A is ∇_ρ of the G_A^xz potential, so jω⟨f, ∇V⟩ integrates by parts onto
/// −jω⟨∇·f, V⟩ — valid for RWG bases (no rim flux) and for the junction's surface
/// part AS A WHOLE (D's outer-edge flux is absorbed by the halves, interior boundary
/// terms cancel pairwise). Transposed entries are assigned symmetrically — exact,
/// because G_A^xz(d, z′) = −W̃(z′, d) makes the two directions the same integral.
/// The probe-absent path is untouched: Z_cc is the existing bitwise-pinned fill.
/// </summary>
public sealed partial class SurfaceMomSolver
{
    /// <summary>Kernel facts every consumer must surface next to probe-fed results.</summary>
    public static IReadOnlyList<string> ProbeFedAssumptions { get; } = new[]
    {
        "Perfect electric conductor, zero-thickness sheet and probe tube (no ohmic loss).",
        "One dielectric slab (εr, tanδ) on an infinite PEC ground; all sheet metal coplanar at the slab top.",
        "Coaxial probe: a vertical tube from ground to patch, delta-gap driven at its BASE (a real port voltage against ground).",
        "Classical 1/ρ attachment mode at the junction (the probe position is a mesh vertex); the tube and disc deltas cancel exactly — no junction point charge.",
        "Far field and the power ledger use the sheet currents (the junction's transported current mapped onto the fan outer edges); the electrically short probe's own radiation is neglected (k₀d ≪ 1)."
    };

    public ProbeFedSolution SolveProbeFed(SurfaceStructure surface, LayeredKernelTable kernel,
        ProbeFeed probe, double gapVolts = 1.0)
    {
        if (surface.Ground is not null)
            throw new ArgumentException(
                "A layered solve must not carry a SurfaceStructure ground plane — the substrate model "
                + "already contains the ground inside its Green's function.", nameof(surface));
        double diameter = 0;
        var first = surface.Vertices[0];
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var v in surface.Vertices)
        {
            diameter = Math.Max(diameter, (v - first).Length);
            minZ = Math.Min(minZ, v.Z);
            maxZ = Math.Max(maxZ, v.Z);
        }
        if (maxZ - minZ > 1e-9 * Math.Max(diameter, 1e-12))
            throw new ArgumentException(
                $"The v1 layered scope needs ALL sheet metal in one plane (z spread {maxZ - minZ:g3} m found) — "
                + "the probe is the only vertical current; multi-level metal is named future work.",
                nameof(surface));

        // The probe position must BE a mesh vertex (the attachment fan is anchored
        // there; the mesh builders snap one on request).
        int vertex = -1;
        double bestDistance = double.MaxValue;
        for (int v = 0; v < surface.Vertices.Count; v++)
        {
            double dx = surface.Vertices[v].X - probe.X, dy = surface.Vertices[v].Y - probe.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < bestDistance) { bestDistance = dist; vertex = v; }
        }
        if (bestDistance > 1e-9 * Math.Max(diameter, 1e-12))
            throw new ArgumentException(
                $"The probe position ({probe.X:g6}, {probe.Y:g6}) is not a mesh vertex (nearest is "
                + $"{bestDistance:g3} m away) — build the mesh with the probe snap so the attachment "
                + "fan has its anchor.", nameof(probe));

        var fan = new AttachmentFan(surface, vertex, probe.RadiusMeters);

        double omega = 2 * Math.PI * kernel.FrequencyHz;
        var set = new VerticalKernelSet(kernel.Substrate, kernel.FrequencyHz);
        double[] tubeNodes = ProbeAssembly.TubeNodes(kernel.Substrate, probe);
        int segments = probe.Segments;

        // Tube-side quadrature: 2-point Gauss per element — these z′ nodes are the
        // coupling tables' fixed heights.
        var (gaussNodes, gaussWeights) = GaussLegendre.Rule(2, 0, 1);
        var zNodes = new double[2 * segments];
        for (int e = 0; e < segments; e++)
            for (int q = 0; q < 2; q++)
                zNodes[2 * e + q] = tubeNodes[e]
                    + (tubeNodes[e + 1] - tubeNodes[e]) * gaussNodes[q];
        double maxRho = 0;
        var axis = new Vector3D(probe.X, probe.Y, surface.Vertices[vertex].Z);
        foreach (var v in surface.Vertices)
        {
            double dx = v.X - probe.X, dy = v.Y - probe.Y;
            maxRho = Math.Max(maxRho, Math.Sqrt(dx * dx + dy * dy));
        }
        double tableRhoMax = Math.Sqrt(maxRho * maxRho + probe.RadiusMeters * probe.RadiusMeters) * 1.02;
        var tables = new ProbeCouplingTables(set, zNodes, tableRhoMax, MaxDegreeOfParallelism);

        // Per tube basis: the Gauss-node weights of its value and slope. Basis n peaks
        // at tube node n; basis `segments` is the top half hat (the junction's tube leg).
        int tubeBases = segments + 1;
        var valueWeights = new double[tubeBases][];
        var slopeWeights = new double[tubeBases][];
        for (int n = 0; n < tubeBases; n++)
        {
            valueWeights[n] = new double[zNodes.Length];
            slopeWeights[n] = new double[zNodes.Length];
            for (int e = 0; e < segments; e++)
            {
                double h = tubeNodes[e + 1] - tubeNodes[e];
                for (int q = 0; q < 2; q++)
                {
                    int idx = 2 * e + q;
                    double weight = gaussWeights[q] * h;
                    if (n == e + 1) // rising on element e
                    {
                        valueWeights[n][idx] += weight * gaussNodes[q];
                        slopeWeights[n][idx] += weight / h;
                    }
                    if (n == e)     // falling on element e
                    {
                        valueWeights[n][idx] += weight * (1 - gaussNodes[q]);
                        slopeWeights[n][idx] += -weight / h;
                    }
                }
            }
        }

        // I_T(n) = ∫_T Ψ_n(ρ_eff) dA per (triangle, tube basis) — the tube↔surface
        // scalar-radial coupling; panels refine toward the probe axis where the
        // kernels peak; ρ_eff = √(ρ² + a²) keeps everything regular.
        var (l1, l2, l3, wq) = TriangleQuadrature.Rule(6);
        var triangleIntegrals = new Complex[surface.Triangles.Count, tubeBases];
        var minusJOmega = new Complex(0, -omega);
        var overJOmega = 1 / new Complex(0, omega);
        var jOmega = new Complex(0, omega);
        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            var verts = PVertices(surface, t);
            foreach (var (pa, pb, pc) in OuterPanels(verts, verts, new List<Vector3D> { axis }))
            {
                double panelArea = Vector3D.Cross(pb - pa, pc - pa).Length / 2;
                for (int i = 0; i < wq.Length; i++)
                {
                    var r = pa * l1[i] + pb * l2[i] + pc * l3[i];
                    double dx = r.X - probe.X, dy = r.Y - probe.Y;
                    double rhoEff = Math.Sqrt(dx * dx + dy * dy
                        + probe.RadiusMeters * probe.RadiusMeters);
                    double weight = wq[i] * panelArea;
                    for (int q = 0; q < zNodes.Length; q++)
                    {
                        var (gxz, kPhi) = tables.Evaluate(q, rhoEff);
                        for (int n = 0; n < tubeBases; n++)
                        {
                            double vw = valueWeights[n][q];
                            double sw = slopeWeights[n][q];
                            if (vw == 0 && sw == 0) continue;
                            triangleIntegrals[t, n] += weight
                                * (minusJOmega * vw * gxz + overJOmega * sw * kPhi);
                        }
                    }
                }
            }
        }

        // C[m, n] = Σ_T (∇·f_m)|_T · I_T(n) — the RWG divergence is ±l/A per triangle.
        int nEdges = surface.BasisCount;
        var coupling = new Complex[nEdges, tubeBases];
        for (int t = 0; t < surface.Triangles.Count; t++)
            foreach (var (basis, sign, _) in surface.TriangleSupports[t])
            {
                double div = sign * surface.Edges[basis].Length / surface.TriangleAreas[t];
                for (int n = 0; n < tubeBases; n++)
                    coupling[basis, n] += div * triangleIntegrals[t, n];
            }

        // The junction's SURFACE part vs the tube bases, through the same by-parts
        // machinery: its distributed divergence is Σᵢβᵢ·(−lᵢ/A) on the neighbors
        // (D is chargeless; the tube/disc deltas cancel inside the basis).
        var junctionTube = new Complex[tubeBases];
        foreach (var wedge in fan.Wedges)
        {
            double div = -wedge.Gamma * surface.Edges[wedge.EdgeBasis].Length
                / surface.TriangleAreas[wedge.NeighborTriangle];
            for (int n = 0; n < tubeBases; n++)
                junctionTube[n] += div * triangleIntegrals[wedge.NeighborTriangle, n];
        }

        // Disc-current vector couplings: jω⟨f, A(D)⟩ with A(D) from the fan quadrature.
        var discV = new Complex[nEdges];
        Complex discVHalfTotal = Complex.Zero; // Σᵢβᵢ·jω⟨Hᵢ, A(D)⟩
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
                    var (ax, ay) = fan.DiscPotential(kernel, surface, r);
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
                    var (ax, ay) = fan.DiscPotential(kernel, surface, r);
                    var fDir = pOpp - r; // the half form (p⁻ − r)
                    sum += wq[i] * panelArea * (fDir.X * ax + fDir.Y * ay);
                }
            }
            discVHalfTotal += wedge.Gamma * jOmega * (lI / (2 * surface.TriangleAreas[t])) * sum;
        }
        Complex discDD = jOmega * fan.DiscSelf(kernel, surface);

        // Half-RWG pairings against every RWG basis and against each other, through
        // the standard layered pair moments (the half is σ = −1 with the neighbor's
        // opposite vertex — one triangle of an ordinary RWG).
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);
        var split = new LayeredKernelSplit(kernel);
        var halfRow = new Complex[nEdges];   // Σᵢβᵢ·⟨f_m, E(Hᵢ)⟩ per RWG m
        Complex halfHalf = Complex.Zero;     // ΣΣβᵢβⱼ⟨Hᵢ, E(Hⱼ)⟩
        // Heap buffers — stackalloc inside these loops would only release at method
        // exit (the live-testhost StackOverflow lesson).
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
                var (mA, mPhi) = LayeredPairMoments(surface, p, q, split);
                if (p == q)
                {
                    mA = mA.Symmetrized();
                    mPhi = mPhi.Symmetrized();
                }
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
                            dotSum += Vector3D.Dot(fA, qArr[b] - qArr[oppLocalQ]) * mA[a, b];
                    }
                    // σ_N = −1: the half current is (l/2A)(p⁻ − r) = −(l/2A)(r − p⁻).
                    Complex vector = vectorFactor * (signM * -1.0 * lM * lI / (4 * areaP * areaQ)) * dotSum;
                    Complex charge = chargeFactor * (signM * -1.0 * lM * lI / (areaP * areaQ)) * mPhi.M00;
                    halfRow[basisM] += wedge.Gamma * (vector + charge);
                }
            }

            // Half × half (including this wedge with itself and with the others).
            foreach (var wedge2 in fan.Wedges)
            {
                int p2 = wedge2.NeighborTriangle;
                double lJ = surface.Edges[wedge2.EdgeBasis].Length;
                double areaP2 = surface.TriangleAreas[p2];
                (pArr[0], pArr[1], pArr[2]) = PVertices(surface, p2);
                (pIdx[0], pIdx[1], pIdx[2]) = surface.Triangles[p2];
                int oppLocalP2 = LocalIndex(pIdx, wedge2.NeighborOpposite);
                var (mA2, mPhi2) = LayeredPairMoments(surface, p2, q, split);
                if (p2 == q)
                {
                    mA2 = mA2.Symmetrized();
                    mPhi2 = mPhi2.Symmetrized();
                }
                Complex dotSum2 = Complex.Zero;
                for (int a = 0; a < 3; a++)
                {
                    var fA = pArr[oppLocalP2] - pArr[a]; // (p⁻ − r) on the test side
                    for (int b = 0; b < 3; b++)
                        dotSum2 += Vector3D.Dot(fA, qArr[oppLocalQ] - qArr[b]) * mA2[a, b];
                }
                Complex vector2 = vectorFactor * (lJ * lI / (4 * areaP2 * areaQ)) * dotSum2;
                Complex charge2 = chargeFactor * (lJ * lI / (areaP2 * areaQ)) * mPhi2.M00;
                halfHalf += wedge2.Gamma * wedge.Gamma * (vector2 + charge2);
            }
        }

        // Extended system: [RWG 0..nE) | tube hats nE..nE+segments) | junction J].
        var zCc = AssembleLayeredImpedanceMatrix(surface, kernel, omega, MaxDegreeOfParallelism);
        var probeBlock = ProbeAssembly.ProbeSelfBlock(set, probe, omega, includeTopBasis: true);
        int nTube = segments;          // bases 0..segments−1 (ground + interior hats)
        int total = nEdges + nTube + 1;
        int jIndex = total - 1;
        var z = new ComplexDenseMatrix(total, total);
        for (int i = 0; i < nEdges; i++)
            for (int j = 0; j < nEdges; j++)
                z[i, j] = zCc[i, j];
        for (int m = 0; m < nEdges; m++)
        {
            for (int n = 0; n < nTube; n++)
            {
                z[m, nEdges + n] = coupling[m, n];
                z[nEdges + n, m] = coupling[m, n];
            }
            Complex zmJ = coupling[m, segments] + discV[m] + halfRow[m];
            z[m, jIndex] = zmJ;
            z[jIndex, m] = zmJ;
        }
        for (int n = 0; n < nTube; n++)
        {
            for (int n2 = 0; n2 < nTube; n2++)
                z[nEdges + n, nEdges + n2] = probeBlock[n, n2];
            Complex znJ = probeBlock[n, segments] + junctionTube[n];
            z[nEdges + n, jIndex] = znJ;
            z[jIndex, nEdges + n] = znJ;
        }
        z[jIndex, jIndex] = probeBlock[segments, segments] + 2 * junctionTube[segments]
            + discDD + 2 * discVHalfTotal + halfHalf;

        var rhs = new Complex[total];
        rhs[nEdges] = gapVolts; // the base half hat has f(0) = 1: the real probe port
        var x = ComplexLu.Factor(z).Solve(rhs);
        Complex baseCurrent = x[nEdges];
        if (baseCurrent == Complex.Zero)
            throw new InvalidOperationException("The probe base carries zero current.");

        // Physical edge currents for the far-field consumers: the junction's surface
        // current transports θᵢ/2π of J across each fan outer edge — mapped onto the
        // full RWG there (exact crossing, mesh-scale approximation of the local
        // distribution, consistent with k₀·mesh ≪ 1).
        var junction = x[jIndex];
        var rawEdgeCurrents = new Complex[nEdges];
        var edgeCurrents = new Complex[nEdges];
        for (int e = 0; e < nEdges; e++) rawEdgeCurrents[e] = edgeCurrents[e] = x[e];
        foreach (var wedge in fan.Wedges)
            edgeCurrents[wedge.EdgeBasis] += wedge.OrientationSign * wedge.Gamma * junction;
        var tubeCurrents = new Complex[segments + 1];
        for (int n = 0; n < nTube; n++) tubeCurrents[n] = x[nEdges + n];
        tubeCurrents[segments] = junction;

        return new ProbeFedSolution(
            new SurfaceMomSolution(kernel.FrequencyHz, gapVolts / baseCurrent, edgeCurrents),
            tubeCurrents, rawEdgeCurrents);
    }
}
