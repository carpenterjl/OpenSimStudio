using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>
/// The layered-media (microstrip) assembly path: the SAME triangle-pair enumeration,
/// RWG scatter composition, and singular machinery as free space, with the kernel
/// swapped through the split contract
///
///   G_A/µ₀ = [e^{−jk₀ρ}/ρ] − e^{−jk₀R₂}/R₂ + smooth_A(ρ)
///   K_Φ·ε₀ = c₀[e^{−jk₀ρ}/ρ] + c₁e^{−jk₀R₂}/R₂ + smooth_Φ(ρ)
///
/// (kernels normalized to the free-space g = e^{−jkR}/R scale so the jωµ₀/4π and
/// −j/4πε₀ω assembly factors are IDENTICAL to Stage B). Three tracks per pair:
///  • the singular PRIMARY: 1/ρ static through Wilton–Rao with coefficient 1
///    (vector) / c₀ (charge — complex for lossy slabs), its (e^{−jkρ}−1)/ρ phase
///    remainder through the same panel rules as Stage B;
///  • the 2d IMAGE: on thin substrates e^{−jk₀R₂}/R₂ is NEARLY SINGULAR on triangle
///    scale (2d ≈ an edge length for the reference patch), so it is integrated as
///    what it geometrically is — a free-space pair moment against the source
///    triangle shifted down by 2d, with the full regime dispatch — and combined with
///    scalar weights (−1 vector, c₁ charge). The currents are NOT mirrored (unlike
///    Stage B's PEC pass): only the kernel argument is.
///  • the tabulated Sommerfeld remainder + surface-wave poles: regular everywhere,
///    evaluated pointwise inside the primary track's quadratures.
///
/// v1-C scope, enforced loudly: one slab on an infinite ground, ALL metal coplanar
/// (the kernel is radial — R ≡ lateral ρ only holds in-plane), no grounded-rim
/// half-bases (a patch shorting wall is a via structure), no wire hybrid.
/// </summary>
public sealed partial class SurfaceMomSolver
{
    /// <summary>Kernel facts every consumer must surface next to layered results.</summary>
    public static IReadOnlyList<string> LayeredAssumptions { get; } = new[]
    {
        "Perfect electric conductor, zero-thickness sheet (no ohmic loss).",
        "A grounded dielectric stackup (one or more layers, per-layer εr/tanδ) on an infinite PEC ground plane; all metal coplanar at a single interface — the slab top, or buried under a dielectric cover of the same εr (a covered patch).",
        "Rigorous layered-media Green's function (MPIE, direct Sommerfeld integration) — surface waves included; only TM0/TE modes above cutoff are extracted into the power ledger.",
        "No vias or probe feeds (vertical currents are out of scope in v1); delta-gap voltage feed across an interior mesh edge.",
        "Current normal to the sheet rim is zero by construction (no wire attachments)."
    };

    /// <summary>Solve over the single-slab layered medium described by <paramref name="kernel"/>
    /// (which fixes the frequency — a table is one (frequency, stackup) pair).</summary>
    public SurfaceMomSolution Solve(SurfaceStructure surface, LayeredKernelTable kernel,
        SurfacePort port, double gapVolts = 1.0)
    {
        ValidateLayeredSurface(surface, port);
        double omega = 2 * Math.PI * kernel.FrequencyHz;
        var z = AssembleLayeredImpedanceMatrix(surface, kernel, omega, MaxDegreeOfParallelism);
        return SolveAssembled(surface, port, gapVolts, kernel.FrequencyHz, z);
    }

    /// <summary>Solve over a multi-layer stackup (Stage F): identical assembly to the
    /// single-slab path, but the kernel split carries G_A's ε-independent ground image plus
    /// K_Φ's quasi-static image SERIES. Same coplanar-at-top scope — metal at an interior
    /// interface (a covered patch) is a separate future item.</summary>
    public SurfaceMomSolution Solve(SurfaceStructure surface, MultiLayerKernelTable kernel,
        SurfacePort port, double gapVolts = 1.0)
    {
        ValidateLayeredSurface(surface, port);
        double omega = 2 * Math.PI * kernel.FrequencyHz;
        var z = AssembleLayeredImpedanceMatrix(surface, kernel, omega, MaxDegreeOfParallelism);
        return SolveAssembled(surface, port, gapVolts, kernel.FrequencyHz, z);
    }

    /// <summary>The layered-scope guards shared by both table types: no explicit ground
    /// (the Green's function owns it), a valid non-rim port, and all metal coplanar (the
    /// kernel is radial — R ≡ lateral ρ only holds in-plane).</summary>
    private static void ValidateLayeredSurface(SurfaceStructure surface, SurfacePort port)
    {
        if (surface.Ground is not null)
            throw new ArgumentException(
                "A layered solve must not carry a SurfaceStructure ground plane — the substrate model "
                + "already contains the ground inside its Green's function. Build the surface without one.",
                nameof(surface));
        if (port.EdgeBases.Count == 0)
            throw new ArgumentException("The port needs at least one interior edge.", nameof(port));
        foreach (int e in port.EdgeBases)
        {
            if (e < 0 || e >= surface.BasisCount)
                throw new ArgumentOutOfRangeException(nameof(port),
                    $"Port edge {e} is outside 0..{surface.BasisCount - 1}.");
            if (surface.Edges[e].MinusTriangle < 0)
                throw new ArgumentException(
                    "A layered solve cannot drive a grounded rim edge — shorting metal to the ground "
                    + "through the slab is a via structure, out of the v1 layered scope.", nameof(port));
        }

        double minZ = double.MaxValue, maxZ = double.MinValue, diameter = 0;
        foreach (var v in surface.Vertices)
        {
            minZ = Math.Min(minZ, v.Z);
            maxZ = Math.Max(maxZ, v.Z);
        }
        var first = surface.Vertices[0];
        foreach (var v in surface.Vertices)
            diameter = Math.Max(diameter, (v - first).Length);
        if (maxZ - minZ > 1e-9 * Math.Max(diameter, 1e-12))
            throw new ArgumentException(
                $"The v1 layered scope needs ALL metal in one plane (z spread {maxZ - minZ:g3} m found) — "
                + "multi-level metal and vertical currents are named future work, not approximated.",
                nameof(surface));
    }

    /// <summary>RHS build + LU + port current — shared verbatim by both kernel paths.</summary>
    private SurfaceMomSolution SolveAssembled(SurfaceStructure surface, SurfacePort port,
        double gapVolts, double frequencyHz, ComplexDenseMatrix z)
    {
        var rhs = new Complex[surface.BasisCount];
        var portSigns = new double[port.EdgeBases.Count];
        for (int i = 0; i < port.EdgeBases.Count; i++)
        {
            int e = port.EdgeBases[i];
            var plusCentroid = surface.TriangleCentroids[surface.Edges[e].PlusTriangle];
            var crossing = surface.Edges[e].MinusTriangle >= 0
                ? surface.TriangleCentroids[surface.Edges[e].MinusTriangle] - plusCentroid
                : ThinWireMomSolver.Mirror(plusCentroid, surface.Ground!.SurfaceZ) - plusCentroid;
            portSigns[i] = Vector3D.Dot(crossing, port.Direction) >= 0 ? 1.0 : -1.0;
            rhs[e] = portSigns[i] * gapVolts * surface.Edges[e].Length;
        }

        var currents = ComplexLu.Factor(z).Solve(rhs);
        Complex portCurrent = Complex.Zero;
        for (int i = 0; i < port.EdgeBases.Count; i++)
            portCurrent += portSigns[i] * surface.Edges[port.EdgeBases[i]].Length
                           * currents[port.EdgeBases[i]];
        if (portCurrent == Complex.Zero)
            throw new InvalidOperationException(
                "The port carries zero current — the feed sits at a current null of a degenerate structure.");
        return new SurfaceMomSolution(frequencyHz, gapVolts / portCurrent, currents);
    }

    internal static ComplexDenseMatrix AssembleLayeredImpedanceMatrix(
        SurfaceStructure surface, LayeredKernelTable kernel, double omega,
        int? maxDegreeOfParallelism = null) =>
        AssembleLayeredCore(surface, new LayeredKernelSplit(kernel), omega, maxDegreeOfParallelism);

    internal static ComplexDenseMatrix AssembleLayeredImpedanceMatrix(
        SurfaceStructure surface, MultiLayerKernelTable kernel, double omega,
        int? maxDegreeOfParallelism = null) =>
        AssembleLayeredCore(surface, new LayeredKernelSplit(kernel), omega, maxDegreeOfParallelism);

    private static ComplexDenseMatrix AssembleLayeredCore(
        SurfaceStructure surface, LayeredKernelSplit split, double omega,
        int? maxDegreeOfParallelism)
    {
        int n = surface.BasisCount;
        var z = new ComplexDenseMatrix(n, n);
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);
        var pairs = PairMomentSchedule.Build(surface);

        // Parallel dual-kernel moments into slots (all three tracks of one pair —
        // singular primary, shifted image, tabulated smooth — compute inside its
        // slot; the table's spline evaluation is immutable-read thread-safe), then
        // the sequential canonical-order scatter: bitwise-identical Z at any DOP.
        var slots = PairMomentSchedule.Compute(pairs, maxDegreeOfParallelism, (p, q) =>
        {
            var (momentsA, momentsPhi) = LayeredPairMoments(surface, p, q, split);
            if (p == q)
            {
                momentsA = momentsA.Symmetrized();
                momentsPhi = momentsPhi.Symmetrized();
            }
            return (momentsA, momentsPhi);
        });
        for (int i = 0; i < pairs.Length; i++)
        {
            var (p, q) = pairs[i];
            ScatterLayeredPair(z, surface, p, q, slots[i].momentsA, slots[i].momentsPhi,
                vectorFactor, chargeFactor);
        }
        return z;
    }

    /// <summary>One deeper (depth &gt; 0) quasi-static image, carrying its G_A weight and
    /// its K_Φ weight at a shared depth — the source triangle is shifted down by
    /// <see cref="Depth"/> and the resulting free-space pair moment scaled into each kernel.</summary>
    private readonly record struct ImageTerm(double Depth, Complex CoeffA, Complex CoeffPhi);

    /// <summary>The g-normalized kernel split (see the class doc): the singular primary
    /// (depth-0 image, coefficients (1, c₀)) handled by Wilton–Rao, plus the deeper images
    /// and the tabulated Sommerfeld part. The deeper image terms are deliberately NOT in the
    /// Wilton–Rao track: on thin substrates their depth is comparable to a triangle edge,
    /// making e^{−jk₀R}/R nearly singular on triangle scale — they are integrated by the
    /// free-space pair machinery against the shifted-down source triangle (their exact
    /// geometric meaning) and combined scalar-weighted.
    ///
    /// Single-slab (<see cref="LayeredKernelTable"/>): ONE image at 2d with weights (−1, c₁)
    /// — byte-identical to the Stage C/D/E path. Multi-layer (<see cref="MultiLayerKernelTable"/>):
    /// G_A's single ε-independent ground image at 2·d_total plus the K_Φ quasi-static image
    /// SERIES, merged by depth. At N = 1 the merged list collapses to the single-slab pair.</summary>
    private readonly struct LayeredKernelSplit
    {
        private readonly double _k0;
        public double WaveNumber => _k0;
        public readonly Complex ChargeStaticCoefficient; // c₀ — scales the Wilton–Rao primary
        public readonly ImageTerm[] Images;              // deeper images (depth > 0)
        private readonly Func<double, (Complex A, Complex Phi)> _smooth;
        private readonly double _scaleA;   // 4π/µ₀
        private readonly double _scalePhi; // 4πε₀

        public LayeredKernelSplit(LayeredKernelTable table)
        {
            _k0 = table.K0;
            ChargeStaticCoefficient = table.PhiImages.C0;
            Images = new[]
            {
                new ImageTerm(2 * table.Substrate.ThicknessMeters,
                    new Complex(-1, 0), table.PhiImages.C1)
            };
            _smooth = table.EvaluateSmooth;
            _scaleA = 4 * Math.PI / RfConstants.Mu0;
            _scalePhi = 4 * Math.PI * RfConstants.Eps0;
        }

        public LayeredKernelSplit(MultiLayerKernelTable table)
        {
            _k0 = table.K0;
            var phi = table.PhiImages;
            var ga = table.GaImages;
            if (phi.Count == 0 || phi[0].Depth != 0 || ga.Count == 0 || ga[0].Depth != 0)
                throw new ArgumentException(
                    "A multi-layer kernel table must lead each image list with the depth-0 "
                    + "primary (the 1/ρ singularity the Wilton–Rao track integrates).", nameof(table));
            ChargeStaticCoefficient = phi[0].Coeff;
            Images = MergeDeeperImages(ga, phi);
            _smooth = table.EvaluateSmooth;
            _scaleA = 4 * Math.PI / RfConstants.Mu0;
            _scalePhi = 4 * Math.PI * RfConstants.Eps0;
        }

        /// <summary>Merge G_A's deeper images (weight into CoeffA) and K_Φ's deeper images
        /// (weight into CoeffPhi) by shared depth, so each distinct depth costs exactly one
        /// free-space pair moment. First-seen order (G_A first) is deterministic.</summary>
        private static ImageTerm[] MergeDeeperImages(
            IReadOnlyList<MultiLayerImages.Image> ga, IReadOnlyList<MultiLayerImages.Image> phi)
        {
            var order = new List<double>();
            var aCoeff = new Dictionary<double, Complex>();
            var phiCoeff = new Dictionary<double, Complex>();
            void Ensure(double depth)
            {
                if (aCoeff.ContainsKey(depth)) return;
                aCoeff[depth] = Complex.Zero;
                phiCoeff[depth] = Complex.Zero;
                order.Add(depth);
            }
            for (int i = 1; i < ga.Count; i++) { Ensure(ga[i].Depth); aCoeff[ga[i].Depth] += ga[i].Coeff; }
            for (int i = 1; i < phi.Count; i++) { Ensure(phi[i].Depth); phiCoeff[phi[i].Depth] += phi[i].Coeff; }
            var result = new ImageTerm[order.Count];
            for (int i = 0; i < order.Count; i++)
                result[i] = new ImageTerm(order[i], aCoeff[order[i]], phiCoeff[order[i]]);
            return result;
        }

        /// <summary>Primary-track smooth parts: the primary's phase remainder
        /// (e^{−jk₀ρ}−1)/ρ plus the tabulated Sommerfeld content — everything in the
        /// primary track except the 1/ρ static handled by Wilton–Rao.</summary>
        public (Complex A, Complex Phi) SmoothParts(double rho)
        {
            Complex primaryRemainder;
            if (rho == 0) primaryRemainder = new Complex(0, -_k0);
            else
            {
                var (sin, cos) = Math.SinCos(_k0 * rho);
                primaryRemainder = new Complex((cos - 1) / rho, -sin / rho);
            }
            var (smoothA, smoothPhi) = _smooth(rho);
            return (primaryRemainder + _scaleA * smoothA,
                    ChargeStaticCoefficient * primaryRemainder + _scalePhi * smoothPhi);
        }

        /// <summary>Full primary-track kernels (FAR pairs, where nothing is singular).</summary>
        public (Complex A, Complex Phi) FullKernels(double rho)
        {
            var (sin, cos) = Math.SinCos(_k0 * rho);
            var primary = new Complex(cos / rho, -sin / rho);
            var (smoothA, smoothPhi) = _smooth(rho);
            return (primary + _scaleA * smoothA,
                    ChargeStaticCoefficient * primary + _scalePhi * smoothPhi);
        }
    }

    private static void ScatterLayeredPair(ComplexDenseMatrix z, SurfaceStructure surface,
        int p, int q, SurfaceMoments momentsA, SurfaceMoments momentsPhi,
        Complex vectorFactor, Complex chargeFactor)
    {
        var (pa, pb, pc) = PVertices(surface, p);
        Span<Vector3D> pVerts = stackalloc[] { pa, pb, pc };
        var (qa, qb, qc) = PVertices(surface, q);
        Span<Vector3D> qVerts = stackalloc[] { qa, qb, qc };
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
                        dotSum += Vector3D.Dot(fA, qVerts[b] - qVerts[oppLocalN]) * momentsA[a, b];
                }

                Complex vector = vectorFactor * (signM * signN * lM * lN / (4 * areaP * areaQ)) * dotSum;
                Complex charge = chargeFactor * (signM * signN * lM * lN / (areaP * areaQ)) * momentsPhi.M00;
                Complex contribution = vector + charge;
                z[basisM, basisN] += contribution;
                if (p != q)
                    z[basisN, basisM] += contribution;
            }
        }
    }

    // ------------------------------------------------------------------
    // Dual-kernel moments per triangle pair
    // ------------------------------------------------------------------

    private static (SurfaceMoments A, SurfaceMoments Phi) LayeredPairMoments(
        SurfaceStructure surface, int p, int q, in LayeredKernelSplit split)
    {
        var pVerts = PVertices(surface, p);
        var qVerts = PVertices(surface, q);

        // Primary track (regime-dispatched exactly like free space).
        SurfaceMoments primaryA, primaryPhi;
        List<Vector3D>? shared = null;
        if (p == q) shared = SelfSharedVertices(pVerts);
        else
        {
            var (pa, pb, pc) = surface.Triangles[p];
            var (qa, qb, qc) = surface.Triangles[q];
            Span<int> pIdx = stackalloc[] { pa, pb, pc };
            Span<int> qIdx = stackalloc[] { qa, qb, qc };
            Span<Vector3D> pArr = stackalloc[] { pVerts.Item1, pVerts.Item2, pVerts.Item3 };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (pIdx[i] == qIdx[j]) (shared ??= new List<Vector3D>(2)).Add(pArr[i]);
        }
        if (shared is not null)
            (primaryA, primaryPhi) = LayeredSingularMoments(pVerts, qVerts, split, shared);
        else
        {
            double diameter = Math.Max(Diameter(pVerts), Diameter(qVerts));
            double gap = (Centroid(pVerts) - Centroid(qVerts)).Length;
            (primaryA, primaryPhi) = gap < 2 * diameter
                ? LayeredSingularMoments(pVerts, qVerts, split, shared: null)
                : LayeredSmoothMoments(pVerts, qVerts, split, degree: 6);
        }

        var momentsA = new SurfaceMoments();
        var momentsPhi = new SurfaceMoments();
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
            {
                momentsA[a, b] = primaryA[a, b];
                momentsPhi[a, b] = primaryPhi[a, b];
            }

        // Deeper images: ∬λλ′·e^{−jk₀R}/R against the source triangle shifted DOWN by
        // each depth D — the exact geometric identity R = |r − (r′ − D ẑ)|. The free-space
        // pair machinery dispatches its own regimes, so a thin substrate's nearly singular
        // image integral gets the panelled analytic treatment for free. Barycentric values
        // are shift-invariant, so these moments combine with the REAL RWG vectors — the
        // currents are not mirrored here, only the kernel argument is (unlike Stage B's PEC
        // image pass). At N = 1 this is the single (−1, c₁) image at 2d.
        var images = split.Images;
        for (int k = 0; k < images.Length; k++)
        {
            var shift = new Vector3D(0, 0, -images[k].Depth);
            var imageQ = (qVerts.Item1 + shift, qVerts.Item2 + shift, qVerts.Item3 + shift);
            var image = GeometricPairMoments(pVerts, imageQ, split.WaveNumber);
            Complex ca = images[k].CoeffA, cf = images[k].CoeffPhi;
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                {
                    momentsA[a, b] += ca * image[a, b];
                    momentsPhi[a, b] += cf * image[a, b];
                }
        }
        return (momentsA, momentsPhi);
    }

    private static List<Vector3D> SelfSharedVertices((Vector3D A, Vector3D B, Vector3D C) v) =>
        new() { v.A, v.B, v.C };

    /// <summary>The Stage B singular machinery with dual kernels: the analytic static
    /// panel sweep is evaluated ONCE (it is pure geometry) and scaled into the A
    /// moments with coefficient 1 and the Φ moments with c₀; the smooth remainders
    /// (which differ between kernels) accumulate under the same panel subdivision
    /// rules as free space — the kink argument is about R's geometry, not the kernel.</summary>
    private static (SurfaceMoments A, SurfaceMoments Phi) LayeredSingularMoments(
        (Vector3D A, Vector3D B, Vector3D C) pVerts,
        (Vector3D A, Vector3D B, Vector3D C) qVerts,
        in LayeredKernelSplit split, List<Vector3D>? shared)
    {
        var momentsA = new SurfaceMoments();
        var momentsPhi = new SurfaceMoments();
        var (outerL1, outerL2, outerL3, outerW) = TriangleQuadrature.Rule(6);

        var (qa, qb, qc) = qVerts;
        var nQ = Vector3D.Cross(qb - qa, qc - qa);
        double twoAreaQ = nQ.Length;
        var nHatQ = nQ / twoAreaQ;
        Span<Vector3D> qArr = stackalloc[] { qa, qb, qc };
        Span<Vector3D> gradQ = stackalloc Vector3D[3];
        gradQ[0] = Vector3D.Cross(nHatQ, qc - qb) / twoAreaQ;
        gradQ[1] = Vector3D.Cross(nHatQ, qa - qc) / twoAreaQ;
        gradQ[2] = Vector3D.Cross(nHatQ, qb - qa) / twoAreaQ;

        var (pa, pb, pc) = pVerts;
        var nP = Vector3D.Cross(pb - pa, pc - pa);
        double twoAreaP = nP.Length;
        var nHatP = nP / twoAreaP;
        Span<Vector3D> pArr = stackalloc[] { pa, pb, pc };
        Span<Vector3D> gradP = stackalloc Vector3D[3];
        gradP[0] = Vector3D.Cross(nHatP, pc - pb) / twoAreaP;
        gradP[1] = Vector3D.Cross(nHatP, pa - pc) / twoAreaP;
        gradP[2] = Vector3D.Cross(nHatP, pb - pa) / twoAreaP;

        Span<double> lambdaP = stackalloc double[3];
        Span<double> innerB = stackalloc double[3];
        var c0 = split.ChargeStaticCoefficient;
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
                    {
                        double staticMoment = weight * lambdaP[a] * innerB[b];
                        momentsA[a, b] += staticMoment;
                        momentsPhi[a, b] += c0 * staticMoment;
                    }
            }
        }

        // Smooth remainders — same 16×16 subdivision for kink-crossing pairs, single
        // 7×7 for NEAR, as Stage B (the R-kink lives in the geometry either way).
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(5);
        var pPanels = shared is null
            ? new[] { (pa, pb, pc) }
            : Split((pa, pb, pc)).SelectMany(Split).ToArray();
        var qPanels = shared is null
            ? new[] { (qa, qb, qc) }
            : Split((qa, qb, qc)).SelectMany(Split).ToArray();
        Span<double> lambdaQ = stackalloc double[3];
        Complex a11 = default, a12 = default, a13 = default,
                a21 = default, a22 = default, a23 = default,
                a31 = default, a32 = default, a33 = default;
        Complex f11 = default, f12 = default, f13 = default,
                f21 = default, f22 = default, f23 = default,
                f31 = default, f32 = default, f33 = default;
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
                        var (gsA, gsPhi) = split.SmoothParts((r - rPrime).Length);
                        double baseWeight = w[i] * w[j] * areaFactor;
                        for (int b = 0; b < 3; b++)
                            lambdaQ[b] = 1 + Vector3D.Dot(gradQ[b], rPrime - qArr[b]);
                        Complex wa = baseWeight * gsA, wf = baseWeight * gsPhi;
                        Complex wa1 = wa * lambdaQ[0], wa2 = wa * lambdaQ[1], wa3 = wa * lambdaQ[2];
                        Complex wf1 = wf * lambdaQ[0], wf2 = wf * lambdaQ[1], wf3 = wf * lambdaQ[2];
                        a11 += lambdaP[0] * wa1; a12 += lambdaP[0] * wa2; a13 += lambdaP[0] * wa3;
                        a21 += lambdaP[1] * wa1; a22 += lambdaP[1] * wa2; a23 += lambdaP[1] * wa3;
                        a31 += lambdaP[2] * wa1; a32 += lambdaP[2] * wa2; a33 += lambdaP[2] * wa3;
                        f11 += lambdaP[0] * wf1; f12 += lambdaP[0] * wf2; f13 += lambdaP[0] * wf3;
                        f21 += lambdaP[1] * wf1; f22 += lambdaP[1] * wf2; f23 += lambdaP[1] * wf3;
                        f31 += lambdaP[2] * wf1; f32 += lambdaP[2] * wf2; f33 += lambdaP[2] * wf3;
                    }
                }
            }
        }
        momentsA[0, 0] += a11; momentsA[0, 1] += a12; momentsA[0, 2] += a13;
        momentsA[1, 0] += a21; momentsA[1, 1] += a22; momentsA[1, 2] += a23;
        momentsA[2, 0] += a31; momentsA[2, 1] += a32; momentsA[2, 2] += a33;
        momentsPhi[0, 0] += f11; momentsPhi[0, 1] += f12; momentsPhi[0, 2] += f13;
        momentsPhi[1, 0] += f21; momentsPhi[1, 1] += f22; momentsPhi[1, 2] += f23;
        momentsPhi[2, 0] += f31; momentsPhi[2, 1] += f32; momentsPhi[2, 2] += f33;
        return (momentsA, momentsPhi);
    }

    private static (SurfaceMoments A, SurfaceMoments Phi) LayeredSmoothMoments(
        (Vector3D A, Vector3D B, Vector3D C) pVerts,
        (Vector3D A, Vector3D B, Vector3D C) qVerts,
        in LayeredKernelSplit split, int degree)
    {
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(degree);
        double areaP = Vector3D.Cross(pVerts.B - pVerts.A, pVerts.C - pVerts.A).Length / 2;
        double areaQ = Vector3D.Cross(qVerts.B - qVerts.A, qVerts.C - qVerts.A).Length / 2;
        double areaFactor = areaP * areaQ;

        Complex a11 = default, a12 = default, a13 = default,
                a21 = default, a22 = default, a23 = default,
                a31 = default, a32 = default, a33 = default;
        Complex f11 = default, f12 = default, f13 = default,
                f21 = default, f22 = default, f23 = default,
                f31 = default, f32 = default, f33 = default;
        for (int i = 0; i < w.Length; i++)
        {
            var r = pVerts.A * l1[i] + pVerts.B * l2[i] + pVerts.C * l3[i];
            double la1 = l1[i], la2 = l2[i], la3 = l3[i];
            for (int j = 0; j < w.Length; j++)
            {
                var rPrime = qVerts.A * l1[j] + qVerts.B * l2[j] + qVerts.C * l3[j];
                var (gA, gPhi) = split.FullKernels((r - rPrime).Length);
                double baseWeight = w[i] * w[j] * areaFactor;
                Complex wa = baseWeight * gA, wf = baseWeight * gPhi;
                Complex wa1 = wa * l1[j], wa2 = wa * l2[j], wa3 = wa * l3[j];
                Complex wf1 = wf * l1[j], wf2 = wf * l2[j], wf3 = wf * l3[j];
                a11 += la1 * wa1; a12 += la1 * wa2; a13 += la1 * wa3;
                a21 += la2 * wa1; a22 += la2 * wa2; a23 += la2 * wa3;
                a31 += la3 * wa1; a32 += la3 * wa2; a33 += la3 * wa3;
                f11 += la1 * wf1; f12 += la1 * wf2; f13 += la1 * wf3;
                f21 += la2 * wf1; f22 += la2 * wf2; f23 += la2 * wf3;
                f31 += la3 * wf1; f32 += la3 * wf2; f33 += la3 * wf3;
            }
        }
        var momentsA = new SurfaceMoments();
        var momentsPhi = new SurfaceMoments();
        momentsA[0, 0] = a11; momentsA[0, 1] = a12; momentsA[0, 2] = a13;
        momentsA[1, 0] = a21; momentsA[1, 1] = a22; momentsA[1, 2] = a23;
        momentsA[2, 0] = a31; momentsA[2, 1] = a32; momentsA[2, 2] = a33;
        momentsPhi[0, 0] = f11; momentsPhi[0, 1] = f12; momentsPhi[0, 2] = f13;
        momentsPhi[1, 0] = f21; momentsPhi[1, 1] = f22; momentsPhi[1, 2] = f23;
        momentsPhi[2, 0] = f31; momentsPhi[2, 1] = f32; momentsPhi[2, 2] = f33;
        return (momentsA, momentsPhi);
    }
}
