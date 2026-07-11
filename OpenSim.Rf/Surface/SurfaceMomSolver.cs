using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>One frequency point of a surface moment-method solve: the input impedance
/// seen by the delta-gap edge port and the RWG coefficient per interior edge. A NEW
/// record rather than <see cref="MomSolution"/> on purpose: an edge coefficient is a
/// surface current density crossing an edge [A/m·(edge length) semantics], not a node
/// current, and consumers must never confuse the two.</summary>
public sealed record SurfaceMomSolution(double FrequencyHz, Complex InputImpedance, Complex[] EdgeCurrents);

/// <summary>
/// Frequency-domain surface method of moments for PEC sheets: the electric-field
/// integral equation with RWG bases and Galerkin testing — the thin-wire solver's
/// architecture one dimension up. Assembly enumerates TRIANGLE PAIRS (P ≤ Q); each pair
/// yields the 3×3 barycentric kernel moments M_ab = ∬ λ_a(r) λ_b(r′) g(R) dS dS′, and
/// every RWG pairing supported on (P, Q) is a fixed linear combination (the charge term
/// uses only M00 = Σ M_ab, exactly like the wire solver's charge term). Each pair is
/// evaluated ONCE and scattered to both matrix entries, so Z is bitwise
/// complex-symmetric; same-triangle pairs symmetrize the numerical moment matrix first
/// (the exact one IS symmetric; the quadrature's isn't).
///
/// Singular integration — the wire regime split, one dimension up (g = 1/R analytic
/// + (e^{−jkR}−1)/R smooth quadrature):
///  • SELF and TOUCHING (shared edge/vertex): the static inner integral over Q is the
///    ANALYTIC Wilton–Rao potential; the outer over P subdivides geometrically toward
///    the shared feature (the integrand is log-like there after inner integration).
///  • NEAR (centroid gap &lt; 2·max circumdiameter, not touching — the regime that
///    DOMINATES coplanar patch metal): analytic inner under one unsubdivided 12-point
///    rule; deliberately no adaptivity, this pair count is O(N²).
///  • FAR: the whole kernel is smooth — a 7×7 product rule (3×3 beyond 10 diameters).
/// A ground plane adds an image pass: the image of an RWG basis is MINUS the RWG on the
/// mirrored triangles (the mirror pushforward gives (Jx, Jy, −Jz); the PEC image wants
/// (−Jx, −Jy, +Jz)) — one sign, one place, nailed by the strip-monopole ≡ ½ strip-dipole
/// discrete identity in the tests.
/// </summary>
public sealed partial class SurfaceMomSolver
{
    /// <summary>Thread count for the parallel triangle-pair fill (null = unbounded).
    /// The assembled matrix is bitwise identical for ANY value — pair moments compute
    /// into pre-sized slots and scatter in canonical serial order (see
    /// <see cref="PairMomentSchedule"/>) — so this is purely a resource knob.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>Kernel facts every consumer must surface next to results.</summary>
    public static IReadOnlyList<string> Assumptions { get; } = new[]
    {
        "Perfect electric conductor, zero-thickness sheet (no ohmic loss).",
        "Free space — no dielectric substrate; an air-spaced patch only. Substrates arrive with the layered-media stage.",
        "Delta-gap voltage feed across an interior mesh edge (or a colinear edge group).",
        "Current normal to the sheet rim is zero by construction (no wire attachments)."
    };

    public SurfaceMomSolution Solve(SurfaceStructure surface, double frequencyHz,
        SurfacePort port, double gapVolts = 1.0)
    {
        if (frequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Frequency must be positive.");
        if (port.EdgeBases.Count == 0)
            throw new ArgumentException("The port needs at least one interior edge.", nameof(port));
        foreach (int e in port.EdgeBases)
            if (e < 0 || e >= surface.BasisCount)
                throw new ArgumentOutOfRangeException(nameof(port),
                    $"Port edge {e} is outside 0..{surface.BasisCount - 1}.");

        double omega = 2 * Math.PI * frequencyHz;
        double k = omega / RfConstants.SpeedOfLight;

        // The RHS/port/LU stage is shared with the layered path (SolveAssembled):
        // the port drives edges whose T⁺→T⁻ crossing agrees with the port direction
        // at +V·l, the rest at −V·l; a grounded rim edge's minus side is the image,
        // crossing INTO the plane.
        var z = AssembleImpedanceMatrix(surface, k, omega, MaxDegreeOfParallelism);
        return SolveAssembled(surface, port, gapVolts, frequencyHz, z);
    }

    // ------------------------------------------------------------------
    // Assembly
    // ------------------------------------------------------------------

    internal static ComplexDenseMatrix AssembleImpedanceMatrix(SurfaceStructure surface,
        double k, double omega, int? maxDegreeOfParallelism = null)
    {
        int n = surface.BasisCount;
        var z = new ComplexDenseMatrix(n, n);
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);
        var pairs = PairMomentSchedule.Build(surface);

        // Parallel moment computation into slots, sequential scatter in the exact
        // order of the historical serial double loop — bitwise-identical Z at any DOP.
        var direct = PairMomentSchedule.Compute(pairs, maxDegreeOfParallelism, (p, q) =>
        {
            var moments = PairMoments(surface, p, q, k);
            return p == q ? moments.Symmetrized() : moments;
        });
        for (int i = 0; i < pairs.Length; i++)
        {
            var (p, q) = pairs[i];
            ScatterPair(z, surface, p, q, direct[i], QVertices(surface, q),
                vectorFactor, chargeFactor, imageSign: 1.0);
        }

        if (surface.Ground is { } ground)
        {
            double z0 = ground.SurfaceZ;
            // Image geometry: mirrored Q vertices, SAME barycentric indexing.
            // The image basis is −(RWG on the mirrored triangle): one −1 here.
            var image = PairMomentSchedule.Compute(pairs, maxDegreeOfParallelism, (p, q) =>
            {
                var moments = GeometricPairMoments(PVertices(surface, p), MirroredQ(surface, q, z0), k);
                return p == q ? moments.Symmetrized() : moments;
            });
            for (int i = 0; i < pairs.Length; i++)
            {
                var (p, q) = pairs[i];
                ScatterPair(z, surface, p, q, image[i], MirroredQ(surface, q, z0),
                    vectorFactor, chargeFactor, imageSign: -1.0);
            }
        }
        return z;
    }

    private static (Vector3D, Vector3D, Vector3D) MirroredQ(SurfaceStructure s, int q, double z0)
    {
        var (qa, qb, qc) = QVertices(s, q);
        return (ThinWireMomSolver.Mirror(qa, z0),
                ThinWireMomSolver.Mirror(qb, z0),
                ThinWireMomSolver.Mirror(qc, z0));
    }

    internal static (Vector3D, Vector3D, Vector3D) PVertices(SurfaceStructure s, int t)
    {
        var (a, b, c) = s.Triangles[t];
        return (s.Vertices[a], s.Vertices[b], s.Vertices[c]);
    }

    private static (Vector3D, Vector3D, Vector3D) QVertices(SurfaceStructure s, int t) => PVertices(s, t);

    /// <summary>Composes every RWG pairing on triangle pair (p, q) from the barycentric
    /// moments and scatters it symmetrically. <paramref name="qGeometry"/> carries the
    /// SOURCE triangle's vertices (mirrored ones during the image pass, whose overall
    /// sign is <paramref name="imageSign"/> = −1). The opposite-vertex positions for
    /// source bases are mapped through the same geometry, so the RWG vector
    /// (r′ − p_opp) is evaluated on the actual source triangle — mirrored included.</summary>
    private static void ScatterPair(ComplexDenseMatrix z, SurfaceStructure surface,
        int p, int q, SurfaceMoments moments, (Vector3D A, Vector3D B, Vector3D C) qGeometry,
        Complex vectorFactor, Complex chargeFactor, double imageSign)
    {
        var (pa, pb, pc) = PVertices(surface, p);
        Span<Vector3D> pVerts = stackalloc[] { pa, pb, pc };
        Span<Vector3D> qVerts = stackalloc[] { qGeometry.A, qGeometry.B, qGeometry.C };
        var (qa0, qb0, qc0) = surface.Triangles[q];
        Span<int> qIndex = stackalloc[] { qa0, qb0, qc0 };
        var (pa0, pb0, pc0) = surface.Triangles[p];
        Span<int> pIndex = stackalloc[] { pa0, pb0, pc0 };

        double areaP = surface.TriangleAreas[p];
        double areaQ = surface.TriangleAreas[q];

        foreach (var (basisM, signM, oppositeM) in surface.TriangleSupports[p])
        {
            double lM = surface.Edges[basisM].Length;
            int oppLocalM = LocalIndex(pIndex, oppositeM);
            foreach (var (basisN, signN, oppositeN) in surface.TriangleSupports[q])
            {
                double lN = surface.Edges[basisN].Length;
                int oppLocalN = LocalIndex(qIndex, oppositeN);

                Complex dotSum = Complex.Zero;
                for (int a = 0; a < 3; a++)
                {
                    var fA = pVerts[a] - pVerts[oppLocalM];
                    for (int b = 0; b < 3; b++)
                        dotSum += Vector3D.Dot(fA, qVerts[b] - qVerts[oppLocalN]) * moments[a, b];
                }

                Complex vector = vectorFactor * (signM * signN * lM * lN / (4 * areaP * areaQ)) * dotSum;
                Complex charge = chargeFactor * (signM * signN * lM * lN / (areaP * areaQ)) * moments.M00;
                Complex contribution = imageSign * (vector + charge);

                z[basisM, basisN] += contribution;
                // The bilinear form is symmetric in (m, n) — mirror-both-variables for
                // the image pass — so the transposed entry gets the numerically
                // identical addend and Z stays bitwise symmetric. Same-triangle pairs
                // are covered by the symmetrized moments plus this ordered double loop.
                if (p != q)
                    z[basisN, basisM] += contribution;
            }
        }
    }

    internal static int LocalIndex(Span<int> triangle, int vertex)
    {
        for (int i = 0; i < 3; i++)
            if (triangle[i] == vertex) return i;
        throw new InvalidOperationException("Opposite vertex is not part of its triangle.");
    }

    // ------------------------------------------------------------------
    // Kernel moments per triangle pair
    // ------------------------------------------------------------------

    /// <summary>The 3×3 barycentric kernel moments over one triangle pair, plus the
    /// derived M00 (Σλ = 1). Exact moments are exchange-symmetric for identical
    /// triangles; <see cref="Symmetrized"/> imposes that on the quadrature values.</summary>
    internal struct SurfaceMoments
    {
        private Complex _m11, _m12, _m13, _m21, _m22, _m23, _m31, _m32, _m33;

        public Complex this[int a, int b]
        {
            readonly get => (a * 3 + b) switch
            {
                0 => _m11, 1 => _m12, 2 => _m13,
                3 => _m21, 4 => _m22, 5 => _m23,
                6 => _m31, 7 => _m32, _ => _m33
            };
            set
            {
                switch (a * 3 + b)
                {
                    case 0: _m11 = value; break;
                    case 1: _m12 = value; break;
                    case 2: _m13 = value; break;
                    case 3: _m21 = value; break;
                    case 4: _m22 = value; break;
                    case 5: _m23 = value; break;
                    case 6: _m31 = value; break;
                    case 7: _m32 = value; break;
                    default: _m33 = value; break;
                }
            }
        }

        public readonly Complex M00 =>
            _m11 + _m12 + _m13 + _m21 + _m22 + _m23 + _m31 + _m32 + _m33;

        public readonly SurfaceMoments Symmetrized()
        {
            var s = new SurfaceMoments();
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                    s[a, b] = (this[a, b] + this[b, a]) / 2;
            return s;
        }
    }

    internal static SurfaceMoments PairMoments(SurfaceStructure surface, int p, int q, double k)
    {
        var pVerts = PVertices(surface, p);
        var qVerts = QVertices(surface, q);
        if (p == q)
            return SingularMoments(pVerts, qVerts, k, shared: SelfShared(pVerts));

        // Shared vertices by INDEX (real pairs; the image pass detects geometrically).
        var (pa, pb, pc) = surface.Triangles[p];
        var (qa, qb, qc) = surface.Triangles[q];
        Span<int> pIdx = stackalloc[] { pa, pb, pc };
        Span<int> qIdx = stackalloc[] { qa, qb, qc };
        Span<Vector3D> pArr = stackalloc[] { pVerts.Item1, pVerts.Item2, pVerts.Item3 };
        var shared = new List<Vector3D>(2);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (pIdx[i] == qIdx[j]) shared.Add(pArr[i]);

        return Dispatch(pVerts, qVerts, k, shared);
    }

    /// <summary>Pair moments from raw vertex geometry (the image pass): shared corners
    /// are detected by coincidence — a grounded structure's plane-touching features
    /// would coincide bitwise, though Stage-B surfaces sit strictly above the plane.</summary>
    internal static SurfaceMoments GeometricPairMoments(
        (Vector3D A, Vector3D B, Vector3D C) pVerts,
        (Vector3D A, Vector3D B, Vector3D C) qVerts, double k)
    {
        Span<Vector3D> pArr = stackalloc[] { pVerts.A, pVerts.B, pVerts.C };
        Span<Vector3D> qArr = stackalloc[] { qVerts.A, qVerts.B, qVerts.C };
        double scale = Math.Max(Diameter(pVerts), Diameter(qVerts));
        var shared = new List<Vector3D>(2);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if ((pArr[i] - qArr[j]).Length <= 1e-9 * scale) shared.Add(pArr[i]);
        return Dispatch(pVerts, qVerts, k, shared);
    }

    private static List<Vector3D> SelfShared((Vector3D A, Vector3D B, Vector3D C) v) =>
        new() { v.A, v.B, v.C };

    private static SurfaceMoments Dispatch(
        (Vector3D A, Vector3D B, Vector3D C) pVerts,
        (Vector3D A, Vector3D B, Vector3D C) qVerts, double k, List<Vector3D> shared)
    {
        if (shared.Count > 0)
            return SingularMoments(pVerts, qVerts, k, shared);

        double diameter = Math.Max(Diameter(pVerts), Diameter(qVerts));
        double gap = (Centroid(pVerts) - Centroid(qVerts)).Length;
        if (gap < 2 * diameter)
            return SingularMoments(pVerts, qVerts, k, shared: null);
        // Degree 6 is the ceiling of the all-positive symmetric rules; at λ/10 edges
        // (k·size ≈ 0.6) its phase-curvature error is ~1e-8 relative and does NOT
        // improve with distance, so there is no cheaper far-far downgrade — the wire
        // solver's extra digit came from a degree-15 1D Gauss rule that has no 2D twin.
        return SmoothMoments(pVerts, qVerts, k, degree: 6);
    }

    internal static double Diameter((Vector3D A, Vector3D B, Vector3D C) t) =>
        Math.Max((t.B - t.A).Length, Math.Max((t.C - t.B).Length, (t.A - t.C).Length));

    internal static Vector3D Centroid((Vector3D A, Vector3D B, Vector3D C) t) =>
        (t.A + t.B + t.C) / 3;

    /// <summary>SELF / TOUCHING / NEAR: static part with the ANALYTIC inner integral
    /// (λ′ is affine ⇒ ∫λ′/R = λ′(ρ)·I0 + ∇λ′·Iρ) under an outer rule panelled toward
    /// the shared feature; smooth remainder (e^{−jkR}−1)/R by a plain product rule.</summary>
    private static SurfaceMoments SingularMoments(
        (Vector3D A, Vector3D B, Vector3D C) pVerts,
        (Vector3D A, Vector3D B, Vector3D C) qVerts, double k, List<Vector3D>? shared)
    {
        var moments = new SurfaceMoments();
        var (outerL1, outerL2, outerL3, outerW) = TriangleQuadrature.Rule(6);

        // Q's affine barycentric data for the analytic inner integral.
        var (qa, qb, qc) = qVerts;
        var nQ = Vector3D.Cross(qb - qa, qc - qa);
        double twoAreaQ = nQ.Length;
        var nHatQ = nQ / twoAreaQ;
        Span<Vector3D> qArr = stackalloc[] { qa, qb, qc };
        Span<Vector3D> gradQ = stackalloc Vector3D[3];
        gradQ[0] = Vector3D.Cross(nHatQ, qc - qb) / twoAreaQ;
        gradQ[1] = Vector3D.Cross(nHatQ, qa - qc) / twoAreaQ;
        gradQ[2] = Vector3D.Cross(nHatQ, qb - qa) / twoAreaQ;

        // P's affine barycentric data for evaluating λ_a at panel quadrature points.
        var (pa, pb, pc) = pVerts;
        var nP = Vector3D.Cross(pb - pa, pc - pa);
        double twoAreaP = nP.Length;
        var nHatP = nP / twoAreaP;
        Span<Vector3D> pArr = stackalloc[] { pa, pb, pc };
        Span<Vector3D> gradP = stackalloc Vector3D[3];
        gradP[0] = Vector3D.Cross(nHatP, pc - pb) / twoAreaP;
        gradP[1] = Vector3D.Cross(nHatP, pa - pc) / twoAreaP;
        gradP[2] = Vector3D.Cross(nHatP, pb - pa) / twoAreaP;

        // Hoisted work buffers: stackalloc inside the panel loop would only release at
        // method exit and the deep SELF panelling would overflow the stack.
        Span<double> lambdaP = stackalloc double[3];
        Span<double> innerB = stackalloc double[3];
        foreach (var (ta, tb, tc) in OuterPanels(pVerts, qVerts, shared))
        {
            double panelArea = Vector3D.Cross(tb - ta, tc - ta).Length / 2;
            for (int i = 0; i < outerW.Length; i++)
            {
                var r = ta * outerL1[i] + tb * outerL2[i] + tc * outerL3[i];
                var (i0, iRho, projection) = TrianglePotentials.Integrals(qa, qb, qc, r);
                double weight = outerW[i] * panelArea;

                for (int a = 0; a < 3; a++)
                    lambdaP[a] = 1 + Vector3D.Dot(gradP[a], r - pArr[a]);
                for (int b = 0; b < 3; b++)
                {
                    double lambdaAtProjection = 1 + Vector3D.Dot(gradQ[b], projection - qArr[b]);
                    innerB[b] = lambdaAtProjection * i0 + Vector3D.Dot(gradQ[b], iRho);
                }
                for (int a = 0; a < 3; a++)
                    for (int b = 0; b < 3; b++)
                        moments[a, b] += weight * lambdaP[a] * innerB[b];
            }
        }

        // Smooth remainder: (e^{−jkR}−1)/R is bounded but R itself has a KINK on the
        // coincidence set r = r′ — for SELF/TOUCHING pairs that set intersects the
        // integration domain, and a single product rule stalls at ~1e-4 (measured
        // against the oracle). Two 1:4 subdivision levels per triangle (256 panel
        // pairs) push the kink error below the static machinery's — these pair classes
        // are O(N), so the cost is bounded. Plain NEAR pairs never touch the kink and
        // keep the single 7×7 rule.
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(5);
        var pPanels = shared is null
            ? new[] { (pa, pb, pc) }
            : Split((pa, pb, pc)).SelectMany(Split).ToArray();
        var qPanels = shared is null
            ? new[] { (qa, qb, qc) }
            : Split((qa, qb, qc)).SelectMany(Split).ToArray();
        Complex m11 = default, m12 = default, m13 = default;
        Complex m21 = default, m22 = default, m23 = default;
        Complex m31 = default, m32 = default, m33 = default;
        Span<double> lambdaQ = stackalloc double[3];
        foreach (var (tpa, tpb, tpc) in pPanels)
        {
            double areaTp = Vector3D.Cross(tpb - tpa, tpc - tpa).Length / 2;
            foreach (var (tqa, tqb, tqc) in qPanels)
            {
                double areaTq = Vector3D.Cross(tqb - tqa, tqc - tqa).Length / 2;
                double areaFactor = areaTp * areaTq;
                for (int i = 0; i < w.Length; i++)
                {
                    var r = tpa * l1[i] + tpb * l2[i] + tpc * l3[i];
                    for (int a = 0; a < 3; a++)
                        lambdaP[a] = 1 + Vector3D.Dot(gradP[a], r - pArr[a]);
                    for (int j = 0; j < w.Length; j++)
                    {
                        var rPrime = tqa * l1[j] + tqb * l2[j] + tqc * l3[j];
                        double distance = (r - rPrime).Length;
                        // (e^{−jkR}−1)/R via SinCos; the R → 0 limit is −jk.
                        Complex gs;
                        if (distance == 0) gs = new Complex(0, -k);
                        else
                        {
                            var (sin, cos) = Math.SinCos(k * distance);
                            gs = new Complex((cos - 1) / distance, -sin / distance);
                        }
                        Complex weight = w[i] * w[j] * areaFactor * gs;
                        for (int b = 0; b < 3; b++)
                            lambdaQ[b] = 1 + Vector3D.Dot(gradQ[b], rPrime - qArr[b]);
                        Complex w1 = weight * lambdaQ[0], w2 = weight * lambdaQ[1], w3 = weight * lambdaQ[2];
                        m11 += lambdaP[0] * w1; m12 += lambdaP[0] * w2; m13 += lambdaP[0] * w3;
                        m21 += lambdaP[1] * w1; m22 += lambdaP[1] * w2; m23 += lambdaP[1] * w3;
                        m31 += lambdaP[2] * w1; m32 += lambdaP[2] * w2; m33 += lambdaP[2] * w3;
                    }
                }
            }
        }
        moments[0, 0] += m11; moments[0, 1] += m12; moments[0, 2] += m13;
        moments[1, 0] += m21; moments[1, 1] += m22; moments[1, 2] += m23;
        moments[2, 0] += m31; moments[2, 1] += m32; moments[2, 2] += m33;
        return moments;
    }

    /// <summary>Outer integration panels over P: after the analytic inner integral the
    /// integrand is bounded but log-like toward the singular set (the shared feature
    /// for touching pairs; P's own boundary for SELF, where the inner potential's
    /// in-plane gradient diverges; triangle Q itself for close NEAR pairs). Panels
    /// refine while the singular set is closer than their own size — widths shrink
    /// geometrically toward it, the wire solver's corner-panelling reasoning. The
    /// log-edge error halves per level, so SELF/TOUCHING go deep (depth 7 — these pair
    /// classes are O(N)); NEAR pairs are O(N²) and get a shallow depth 3, enough for
    /// their milder proximity.</summary>
    internal static IEnumerable<(Vector3D A, Vector3D B, Vector3D C)> OuterPanels(
        (Vector3D A, Vector3D B, Vector3D C) triangle,
        (Vector3D A, Vector3D B, Vector3D C) source, List<Vector3D>? shared)
    {
        // Distance from a point to the singular set.
        Func<Vector3D, double> distance;
        int maxDepth;
        if (shared is null)
        {
            // NEAR: the set is triangle Q (edges + interior). Depth 2 is the measured
            // sweet spot: 5e-7 against the oracle at half-a-diameter separation, and
            // this regime's panel count multiplies the O(N²) assembly cost directly.
            distance = r => DistanceToTriangle(r, source);
            maxDepth = 2;
        }
        else if (shared.Count >= 3)
        {
            // SELF: the set is P's own boundary.
            var edges = new[] { (triangle.A, triangle.B), (triangle.B, triangle.C), (triangle.C, triangle.A) };
            distance = r =>
            {
                double best = double.MaxValue;
                foreach (var (a, b) in edges) best = Math.Min(best, DistanceToSegment(r, a, b));
                return best;
            };
            maxDepth = 5;
        }
        else if (shared.Count == 1)
        {
            var point = shared[0];
            distance = r => (r - point).Length;
            maxDepth = 5;
        }
        else
        {
            // A shared EDGE refines along a whole band (panel count doubles per level,
            // unlike the vertex case) and measures 2e-6 at depth 5 — one more level
            // buys the 1e-6 gate for an O(N) pair class.
            var (s0, s1) = (shared[0], shared[1]);
            distance = r => DistanceToSegment(r, s0, s1);
            maxDepth = 6;
        }

        var stack = new Stack<((Vector3D, Vector3D, Vector3D) T, int Depth)>();
        stack.Push((triangle, 0));
        while (stack.Count > 0)
        {
            var (t, depth) = stack.Pop();
            double size = Diameter(t);
            if (depth < maxDepth && distance((t.Item1 + t.Item2 + t.Item3) / 3) < 1.2 * size)
            {
                foreach (var sub in Split(t)) stack.Push((sub, depth + 1));
                continue;
            }
            yield return t;
        }
    }

    private static double DistanceToTriangle(Vector3D r, (Vector3D A, Vector3D B, Vector3D C) t)
    {
        double best = Math.Min(DistanceToSegment(r, t.A, t.B),
            Math.Min(DistanceToSegment(r, t.B, t.C), DistanceToSegment(r, t.C, t.A)));
        var normal = Vector3D.Cross(t.B - t.A, t.C - t.A);
        var nHat = normal / normal.Length;
        double h = Vector3D.Dot(r - t.A, nHat);
        var projection = r - nHat * h;
        if (Vector3D.Dot(Vector3D.Cross(t.B - t.A, projection - t.A), normal) >= 0
            && Vector3D.Dot(Vector3D.Cross(t.C - t.B, projection - t.B), normal) >= 0
            && Vector3D.Dot(Vector3D.Cross(t.A - t.C, projection - t.C), normal) >= 0)
            best = Math.Min(best, Math.Abs(h));
        return best;
    }

    private static double DistanceToSegment(Vector3D r, Vector3D a, Vector3D b)
    {
        var d = b - a;
        double t = Math.Clamp(Vector3D.Dot(r - a, d) / d.LengthSquared, 0, 1);
        return (r - (a + d * t)).Length;
    }

    internal static IEnumerable<(Vector3D, Vector3D, Vector3D)> Split(
        (Vector3D A, Vector3D B, Vector3D C) t)
    {
        var mab = (t.A + t.B) / 2;
        var mbc = (t.B + t.C) / 2;
        var mca = (t.C + t.A) / 2;
        yield return (t.A, mab, mca);
        yield return (mab, t.B, mbc);
        yield return (mca, mbc, t.C);
        yield return (mab, mbc, mca);
    }

    /// <summary>FAR pairs: the whole kernel e^{−jkR}/R is smooth — a plain product rule
    /// of the given degree on each triangle. This loop runs O(N²)·points² times, so it
    /// accumulates into locals (the moment indexer is a switch) and evaluates the
    /// phase via SinCos rather than Complex.Exp.</summary>
    private static SurfaceMoments SmoothMoments(
        (Vector3D A, Vector3D B, Vector3D C) pVerts,
        (Vector3D A, Vector3D B, Vector3D C) qVerts, double k, int degree)
    {
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(degree);
        double areaP = Vector3D.Cross(pVerts.B - pVerts.A, pVerts.C - pVerts.A).Length / 2;
        double areaQ = Vector3D.Cross(qVerts.B - qVerts.A, qVerts.C - qVerts.A).Length / 2;
        double areaFactor = areaP * areaQ;

        Complex m11 = default, m12 = default, m13 = default;
        Complex m21 = default, m22 = default, m23 = default;
        Complex m31 = default, m32 = default, m33 = default;
        for (int i = 0; i < w.Length; i++)
        {
            var r = pVerts.A * l1[i] + pVerts.B * l2[i] + pVerts.C * l3[i];
            double la1 = l1[i], la2 = l2[i], la3 = l3[i];
            for (int j = 0; j < w.Length; j++)
            {
                var rPrime = qVerts.A * l1[j] + qVerts.B * l2[j] + qVerts.C * l3[j];
                double distance = (r - rPrime).Length;
                var (sin, cos) = Math.SinCos(k * distance);
                double scale = w[i] * w[j] * areaFactor / distance;
                var weight = new Complex(scale * cos, -scale * sin);
                Complex w1 = weight * l1[j], w2 = weight * l2[j], w3 = weight * l3[j];
                m11 += la1 * w1; m12 += la1 * w2; m13 += la1 * w3;
                m21 += la2 * w1; m22 += la2 * w2; m23 += la2 * w3;
                m31 += la3 * w1; m32 += la3 * w2; m33 += la3 * w3;
            }
        }
        var moments = new SurfaceMoments();
        moments[0, 0] = m11; moments[0, 1] = m12; moments[0, 2] = m13;
        moments[1, 0] = m21; moments[1, 1] = m22; moments[1, 2] = m23;
        moments[2, 0] = m31; moments[2, 1] = m32; moments[2, 2] = m33;
        return moments;
    }
}
