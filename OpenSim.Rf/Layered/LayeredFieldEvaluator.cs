using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Surface;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Stage D near-field maps over a substrate: E(r) = −jωA − ∇Φ at arbitrary points
/// above the PEC ground (in-slab AND above-slab; at or below the ground E ≡ 0),
/// from a layered <see cref="SurfaceMomSolution"/>.
///
/// Kernels: the per-observation-height field kernels — closed-form quasi-static
/// images (exact, carrying the whole singular content) + a per-z 1-D radial spline
/// of [Sommerfeld remainder + surface-wave pole terms]. Probe grids repeat few
/// distinct z values, so kernels tabulate PER DISTINCT z (each build is a Stage-G
/// parallel knot pass, ~10 ms) — the per-(source, probe) pairing that a naive
/// "direct per point" plan implies would be millions of Sommerfeld integrals.
///
/// The in-plane ∇Φ uses the divergence-theorem boundary trick: the RWG charge is
/// CONSTANT per triangle, so ∇Φ(r) = −Σ_T q_T ∮_{∂T} K_Φ(|ρ−ρ′|, z)·n̂′ dl′ — line
/// integrals of the KERNEL VALUE over triangle edges, no ∂ρ kernels (which would
/// need a first-party J₁/H₁⁽²⁾ family) anywhere. E_z takes the analytic ∂zK_Φ
/// kernel plus the A_z leg through the TM coupling profile W — the same two gauge
/// legs that closed the edge-resistance benchmark.
///
/// Accuracy: physics is carried by the kernel gates (εr → 1 exactness, spectral
/// D_z jump, self-convergence — see LayeredFieldTests); the map assembly is
/// quadrature-limited like the free-space probe (near triangles subdivide 2×1:4,
/// image distances regularize by diameter/50), so points within ~an element of the
/// metal are surface-scale approximations, stated here rather than silently wrong.
/// </summary>
public static class LayeredFieldEvaluator
{
    public static FieldMap Evaluate(SurfaceStructure surface, LayeredKernelTable kernel,
        SurfaceMomSolution solution, IReadOnlyList<Vector3D> points,
        int? maxDegreeOfParallelism = null)
    {
        double omega = 2 * Math.PI * solution.FrequencyHz;
        var substrate = kernel.Substrate;

        // One shared build radius: the largest lateral probe-to-source distance.
        double rhoMax = 1e-9;
        foreach (var p in points)
            foreach (var v in surface.Vertices)
            {
                double dx = p.X - v.X, dy = p.Y - v.Y;
                rhoMax = Math.Max(rhoMax, Math.Sqrt(dx * dx + dy * dy));
            }
        rhoMax *= 1.05;

        // Group probe points by height; one kernel table per distinct z ≥ 0.
        var groups = new Dictionary<double, List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            if (!groups.TryGetValue(points[i].Z, out var list))
                groups[points[i].Z] = list = new List<int>();
            list.Add(i);
        }

        var fields = new (Complex X, Complex Y, Complex Z)[points.Count];
        var hFields = new (Complex X, Complex Y, Complex Z)[points.Count];
        foreach (var (z, indices) in groups.OrderBy(g => g.Key))
        {
            if (z <= 0)
                continue; // PEC interior/ground: E = H = 0 (slots stay zero)
            var table = new FieldKernelTable(substrate, kernel, z, rhoMax, maxDegreeOfParallelism);
            try
            {
                Parallel.ForEach(indices,
                    new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 },
                    i => (fields[i], hFields[i]) = FieldAt(surface, solution, table, omega, points[i]));
            }
            catch (AggregateException e)
            {
                throw e.InnerExceptions[0];
            }
        }

        return Package(points, fields, hFields);
    }

    /// <summary>Stage S9b — the MULTI-LAYER / covered near-field map: E = −jωA − ∇Φ and
    /// H = ∇×A/µ₀ over a general grounded stackup (the covered patch, or a coplanar multi-gap
    /// stack), through the TLGF per-z field kernels (<see cref="MultiLayerFieldKernelTable"/>).
    /// The MoM assembly (<see cref="FieldAt"/>) is IDENTICAL to the single-slab path — only the
    /// radial kernel table changes — so the E and H legs, the boundary-trick curl, and every
    /// quadrature rule are shared. Observation must stand at or above the source metal
    /// (z ≥ z_source); points at or below it stay zero (the map region is above the metal, like
    /// the ground-plane skip in the single-slab path).</summary>
    public static FieldMap Evaluate(SurfaceStructure surface, MultiLayerKernelTable kernel,
        SurfaceMomSolution solution, IReadOnlyList<Vector3D> points,
        int? maxDegreeOfParallelism = null)
    {
        double omega = 2 * Math.PI * solution.FrequencyHz;
        var stackup = kernel.Stackup;
        int sourceInterface = kernel.SourceInterface ?? stackup.Layers.Count - 1;
        double sourceHeight = stackup.InterfaceHeights()[sourceInterface];

        double rhoMax = 1e-9;
        foreach (var p in points)
            foreach (var v in surface.Vertices)
            {
                double dx = p.X - v.X, dy = p.Y - v.Y;
                rhoMax = Math.Max(rhoMax, Math.Sqrt(dx * dx + dy * dy));
            }
        rhoMax *= 1.05;

        var groups = new Dictionary<double, List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            if (!groups.TryGetValue(points[i].Z, out var list))
                groups[points[i].Z] = list = new List<int>();
            list.Add(i);
        }

        var fields = new (Complex X, Complex Y, Complex Z)[points.Count];
        var hFields = new (Complex X, Complex Y, Complex Z)[points.Count];
        foreach (var (z, indices) in groups.OrderBy(g => g.Key))
        {
            // The map lives above the source metal; at/below it the field is not tabulated
            // (the below-source image ladder is a named follow-up), so leave those slots zero.
            if (z <= sourceHeight)
                continue;
            var table = new MultiLayerFieldKernelTable(stackup, kernel.K0, kernel.Poles,
                sourceInterface, z, rhoMax, maxDegreeOfParallelism);
            try
            {
                Parallel.ForEach(indices,
                    new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 },
                    i => (fields[i], hFields[i]) = FieldAt(surface, solution, table, omega, points[i]));
            }
            catch (AggregateException e)
            {
                throw e.InnerExceptions[0];
            }
        }
        return Package(points, fields, hFields);
    }

    private static FieldMap Package(IReadOnlyList<Vector3D> points,
        (Complex X, Complex Y, Complex Z)[] fields, (Complex X, Complex Y, Complex Z)[] hFields)
    {
        var magnitudes = new double[points.Count];
        var snapshots = new Vector3D[points.Count];
        var hMagnitudes = new double[points.Count];
        var hSnapshots = new Vector3D[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var (ex, ey, ez) = fields[i];
            magnitudes[i] = Math.Sqrt(ex.Magnitude * ex.Magnitude
                + ey.Magnitude * ey.Magnitude + ez.Magnitude * ez.Magnitude);
            snapshots[i] = new Vector3D(ex.Real, ey.Real, ez.Real);
            var (hx, hy, hz) = hFields[i];
            hMagnitudes[i] = Math.Sqrt(hx.Magnitude * hx.Magnitude
                + hy.Magnitude * hy.Magnitude + hz.Magnitude * hz.Magnitude);
            hSnapshots[i] = new Vector3D(hx.Real, hy.Real, hz.Real);
        }
        return new FieldMap(points, fields, magnitudes, snapshots)
        {
            H = hFields, HMagnitude = hMagnitudes, HSnapshot = hSnapshots
        };
    }

    /// <summary>E = −jωA − ∇Φ AND H = ∇×A/µ₀ at one probe point. The curl uses the SAME
    /// divergence-theorem boundary trick that gives ∇Φ (kernel VALUES only, no ∂ρ family):
    /// H_z = −Σ_T ∮_{∂T} G̃_A(J·t̂) dl′ (each RWG basis is curl-free — J = c(r−v_opp) — so
    /// the area term vanishes and only the tangential line integral survives), the in-plane
    /// ∂A_z legs are ∮ W̃ n̂′ with the charge (like ∇Φ), and ∂zA_x/∂zA_y take the analytic
    /// ∂zG̃_A kernel over the same area quadrature.</summary>
    /// <summary>One observation height's radial field kernels A/W/Φ/∂zΦ/∂zA/∂zW — the seam
    /// that lets the SAME <see cref="FieldAt"/> assembly serve the single-slab
    /// (<see cref="FieldKernelTable"/>) and multi-layer (<see cref="MultiLayerFieldKernelTable"/>)
    /// paths.</summary>
    internal interface IRadialFieldKernel
    {
        (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW) Evaluate(double rho);
    }

    private static ((Complex X, Complex Y, Complex Z) E, (Complex X, Complex Y, Complex Z) H)
        FieldAt(SurfaceStructure surface,
        SurfaceMomSolution solution, IRadialFieldKernel table, double omega, Vector3D point)
    {
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(5);
        var (gl, glw) = GaussLegendre.Rule(4);
        Complex ax = Complex.Zero, ay = Complex.Zero;
        Complex azW = Complex.Zero, phiDz = Complex.Zero;
        Complex gx = Complex.Zero, gy = Complex.Zero;
        // H legs: ∂zA_x/∂zA_y (area), ∮ W̃ n̂′ for ∂A_z (boundary), ∮ G̃_A(J·t̂) for H_z.
        Complex dzAx = Complex.Zero, dzAy = Complex.Zero;
        Complex awx = Complex.Zero, awy = Complex.Zero, hzLine = Complex.Zero;

        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            var supports = surface.TriangleSupports[t];
            if (supports.Count == 0) continue;
            var (ia, ib, ic) = surface.Triangles[t];
            var va = surface.Vertices[ia];
            var vb = surface.Vertices[ib];
            var vc = surface.Vertices[ic];
            double area = surface.TriangleAreas[t];
            double diameter = Math.Max((vb - va).Length,
                Math.Max((vc - vb).Length, (va - vc).Length));

            Complex charge = Complex.Zero;
            foreach (var (basis, sign, _) in supports)
                charge += solution.EdgeCurrents[basis] * (sign * surface.Edges[basis].Length / area);
            charge /= Complex.ImaginaryOne * omega;

            bool near = (point - surface.TriangleCentroids[t]).Length < 2 * diameter;
            var panels = near
                ? SurfaceMomSolver.Split((va, vb, vc)).SelectMany(SurfaceMomSolver.Split)
                : new[] { (va, vb, vc) };

            foreach (var (ta, tb, tc) in panels)
            {
                double panelArea = Vector3D.Cross(tb - ta, tc - ta).Length / 2;
                for (int i = 0; i < w.Length; i++)
                {
                    var source = ta * l1[i] + tb * l2[i] + tc * l3[i];
                    double dx = point.X - source.X, dy = point.Y - source.Y;
                    double rho = Math.Sqrt(dx * dx + dy * dy);
                    var k = table.Evaluate(rho);
                    double weight = w[i] * panelArea;

                    Complex jx = Complex.Zero, jy = Complex.Zero;
                    foreach (var (basis, sign, opposite) in supports)
                    {
                        Complex c = solution.EdgeCurrents[basis]
                            * (sign * surface.Edges[basis].Length / (2 * area));
                        var rhoVec = source - surface.Vertices[opposite];
                        jx += c * rhoVec.X;
                        jy += c * rhoVec.Y;
                    }
                    ax += weight * k.A * jx;
                    ay += weight * k.A * jy;
                    azW += weight * charge * k.W;
                    phiDz += weight * charge * k.DzPhi;
                    dzAx += weight * k.DzA * jx;    // ∂z A_x
                    dzAy += weight * k.DzA * jy;    // ∂z A_y
                }
            }

            // In-plane boundary integrals (q / J constant-or-linear per triangle):
            //  ∇Φ  = −q ∮ K_Φ n̂′       (E's gradient leg)
            //  ∂A_z ∝ q ∮ W̃  n̂′        (A_z = −jω(W̃∗q); its in-plane gradient)
            //  H_z  = −∮ G̃_A (J·t̂)     (t̂ = CCW tangent = (−n_y, n_x))
            // over the ORIGINAL triangle boundary (interior panel edges cancel pairwise).
            for (int e = 0; e < 3; e++)
            {
                var (p1, p2, third) = e switch
                {
                    0 => (va, vb, vc),
                    1 => (vb, vc, va),
                    _ => (vc, va, vb)
                };
                var direction = p2 - p1;
                double length = direction.Length;
                // Outward in-plane normal: perpendicular to the edge, away from the
                // opposite vertex (metal is coplanar, so this is 2-D geometry).
                var normal = new Vector3D(direction.Y, -direction.X, 0);
                normal = normal / normal.Length;
                if (Vector3D.Dot(normal, third - p1) > 0) normal = -1.0 * normal;
                // CCW tangent from the outward normal (interior on the left).
                var tangent = new Vector3D(-normal.Y, normal.X, 0);

                for (int i = 0; i < gl.Length; i++)
                {
                    var source = p1 + direction * (0.5 * (gl[i] + 1));
                    double dx = point.X - source.X, dy = point.Y - source.Y;
                    double rho = Math.Sqrt(dx * dx + dy * dy);
                    var k = table.Evaluate(rho);
                    double dl = glw[i] * (length / 2);
                    Complex phiWeight = -charge * dl * k.Phi;
                    gx += phiWeight * normal.X;
                    gy += phiWeight * normal.Y;
                    Complex wWeight = charge * dl * k.W;   // Σ_T q ∮ W̃ n̂′
                    awx += wWeight * normal.X;
                    awy += wWeight * normal.Y;

                    // H_z tangential leg: J on the edge (linear), dotted with the tangent.
                    Complex jx = Complex.Zero, jy = Complex.Zero;
                    foreach (var (basis, sign, opposite) in supports)
                    {
                        Complex c = solution.EdgeCurrents[basis]
                            * (sign * surface.Edges[basis].Length / (2 * area));
                        var rhoVec = source - surface.Vertices[opposite];
                        jx += c * rhoVec.X;
                        jy += c * rhoVec.Y;
                    }
                    hzLine += dl * k.A * (jx * tangent.X + jy * tangent.Y);
                }
            }
        }

        var jOmega = Complex.ImaginaryOne * omega;
        var ex = -jOmega * ax - gx;
        var ey = -jOmega * ay - gy;
        var ez = -omega * omega * azW - phiDz;

        // H = ∇×A/µ₀. A_z = −jω(W̃∗q) ⇒ ∂x A_z = jω·awx, ∂y A_z = jω·awy.
        Complex dxAz = jOmega * awx, dyAz = jOmega * awy;
        double inv = 1 / RfConstants.Mu0;   // every kernel carries µ₀; strip it once
        var hx = inv * (dyAz - dzAy);
        var hy = inv * (dzAx - dxAz);
        var hz = inv * (-hzLine);
        return ((ex, ey, ez), (hx, hy, hz));
    }

    /// <summary>One observation height's radial kernels: closed-form images at eval
    /// (exact — they carry all singular content) + one spline over
    /// [remainder + pole terms], knots evaluated in parallel (bitwise-deterministic
    /// slot recipe). Grid: log steps capped at λ₀/64 so the splined H₀⁽²⁾ pole
    /// oscillation stays resolved (the production far-grid reasoning).</summary>
    private sealed class FieldKernelTable : IRadialFieldKernel
    {
        private readonly SubstrateStackup _substrate;
        private readonly double _k0, _z, _d, _rhoMin, _rhoMax, _epsilon;
        private readonly LayeredFieldKernels.KernelImage[] _images;
        private readonly NaturalCubicSpline[] _smooth; // A/W/Phi/DzPhi/DzA/DzW × re/im

        public FieldKernelTable(SubstrateStackup substrate, LayeredKernelTable boundary,
            double z, double rhoMax, int? maxDegreeOfParallelism)
        {
            _substrate = substrate;
            _k0 = boundary.K0;
            _z = z;
            _d = substrate.ThicknessMeters;
            _rhoMax = rhoMax;
            double lambdaD = 2 * Math.PI / (_k0 * Math.Sqrt(substrate.RelativePermittivity));
            _rhoMin = Math.Min(Math.Min(1e-4 * lambdaD, 0.01 * _d), 0.1 * rhoMax);
            _epsilon = _d / 50; // image-distance regularization at the metal plane
            _images = LayeredFieldKernels.Images(substrate, z);

            var grid = new List<double> { _rhoMin };
            double logFactor = Math.Log(10) / 96;
            double maxSpacing = 2 * Math.PI / _k0 / 64;
            double rho = _rhoMin;
            while (rho < rhoMax)
            {
                rho += Math.Min(rho * logFactor, maxSpacing);
                grid.Add(Math.Min(rho, rhoMax));
            }
            while (grid.Count < 4) grid.Add(grid[^1] + maxSpacing);
            var x = grid.ToArray();

            var poles = boundary.Poles;
            var residues = new (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)[poles.Count];
            for (int p = 0; p < poles.Count; p++)
                residues[p] = LayeredFieldKernels.PoleResidues(substrate, _k0, poles[p].KRho, z);

            var knots = new (Complex A, Complex W, Complex Phi, Complex Dz, Complex DzA, Complex DzW)[x.Length];
            try
            {
                Parallel.For(0, x.Length,
                    new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 },
                    i =>
                    {
                        var (a, wv, phi, dz, dza, dzw) = SommerfeldIntegrator.FieldRemainder(
                            substrate, _k0, poles, x[i], z);
                        Complex pa = Complex.Zero, pw = Complex.Zero, pp = Complex.Zero, pd = Complex.Zero;
                        Complex pda = Complex.Zero, pdw = Complex.Zero;
                        for (int p = 0; p < poles.Count; p++)
                        {
                            var factor = new Complex(0, -0.25) * poles[p].KRho
                                         * Bessel.H02(poles[p].KRho * x[i]);
                            pa += residues[p].A * factor;
                            pw += residues[p].W * factor;
                            pp += residues[p].Phi * factor;
                            pd += residues[p].DzPhi * factor;
                            pda += residues[p].DzA * factor;
                            pdw += residues[p].DzW * factor;
                        }
                        knots[i] = (a + pa, wv + pw, phi + pp, dz + pd, dza + pda, dzw + pdw);
                    });
            }
            catch (AggregateException e)
            {
                throw e.InnerExceptions[0];
            }

            _smooth = new NaturalCubicSpline[12];
            var logX = new double[x.Length];
            for (int i = 0; i < x.Length; i++) logX[i] = Math.Log(x[i]);
            for (int c = 0; c < 6; c++)
            {
                var re = new double[x.Length];
                var im = new double[x.Length];
                for (int i = 0; i < x.Length; i++)
                {
                    var v = c switch
                    {
                        0 => knots[i].A, 1 => knots[i].W, 2 => knots[i].Phi,
                        3 => knots[i].Dz, 4 => knots[i].DzA, _ => knots[i].DzW
                    };
                    re[i] = v.Real;
                    im[i] = v.Imaginary;
                }
                _smooth[2 * c] = new NaturalCubicSpline(logX, re);
                _smooth[2 * c + 1] = new NaturalCubicSpline(logX, im);
            }
        }

        /// <summary>The six field kernels at a lateral distance. A/Φ/∂zΦ/∂zA carry a
        /// closed-form image (∂zA's image is the ∂z of A's, exactly as ∂zΦ is of Φ's);
        /// W̃/∂zW̃ are pure spline (no image extraction).</summary>
        public (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW) Evaluate(double rho)
        {
            double clamped = Math.Clamp(rho, _rhoMin, _rhoMax);
            double x = Math.Log(clamped);
            var a = new Complex(_smooth[0].Evaluate(x), _smooth[1].Evaluate(x));
            var wv = new Complex(_smooth[2].Evaluate(x), _smooth[3].Evaluate(x));
            var phi = new Complex(_smooth[4].Evaluate(x), _smooth[5].Evaluate(x));
            var dz = new Complex(_smooth[6].Evaluate(x), _smooth[7].Evaluate(x));
            var dza = new Complex(_smooth[8].Evaluate(x), _smooth[9].Evaluate(x));
            var dzw = new Complex(_smooth[10].Evaluate(x), _smooth[11].Evaluate(x));

            // Closed-form images on the true (unclamped) lateral distance, with the
            // free-space probe's diameter-scale regularization near the source point.
            Complex imgA = Complex.Zero, imgPhi = Complex.Zero, imgDz = Complex.Zero, imgDzA = Complex.Zero;
            for (int m = 0; m < _images.Length; m++)
            {
                double h = _images[m].Height;
                double r = Math.Sqrt(rho * rho + h * h + _epsilon * _epsilon);
                var (sin, cos) = Math.SinCos(_k0 * r);
                var g = new Complex(cos, -sin) / (4 * Math.PI * r);
                var gPrime = -new Complex(cos, -sin)
                             * (1 + Complex.ImaginaryOne * _k0 * r) / (4 * Math.PI * r * r);
                imgA += _images[m].CoefficientA * g;
                imgPhi += _images[m].CoefficientPhi * g;
                double dhdz = _z >= _d ? 1 : (m % 2 == 0 ? -1 : 1);
                imgDz += _images[m].CoefficientPhi * dhdz * (h / r) * gPrime;
                imgDzA += _images[m].CoefficientA * dhdz * (h / r) * gPrime;
            }
            return (RfConstants.Mu0 * imgA + a, wv,
                    imgPhi / RfConstants.Eps0 + phi,
                    imgDz / RfConstants.Eps0 + dz,
                    RfConstants.Mu0 * imgDzA + dza, dzw);
        }
    }

    /// <summary>Stage S9b — one observation height's MULTI-LAYER / covered radial field kernels:
    /// closed-form images (G̃_A pair + K̃_Φ primary + ground image) at eval + one spline over
    /// [multi-layer Sommerfeld remainder + per-z pole terms], knots evaluated in parallel
    /// (bitwise-deterministic slot recipe). The multi-layer twin of <see cref="FieldKernelTable"/>;
    /// observation stands above the source metal so both image heights rise with z (dh/dz = +1).</summary>
    private sealed class MultiLayerFieldKernelTable : IRadialFieldKernel
    {
        private readonly double _k0, _rhoMin, _rhoMax, _epsilon;
        private readonly MultiLayerImages.Image[] _gaImages, _phiImages;
        private readonly NaturalCubicSpline[] _smooth; // A/W/Phi/DzPhi/DzA/DzW × re/im

        public MultiLayerFieldKernelTable(LayeredStackup stackup, double k0,
            IReadOnlyList<SurfaceWavePole> poles, int sourceInterface, double z,
            double rhoMax, int? maxDegreeOfParallelism)
        {
            _k0 = k0;
            double d = stackup.TotalThicknessMeters;
            _rhoMax = rhoMax;
            double epsMax = stackup.Layers.Max(l => l.RelativePermittivity);
            double lambdaD = 2 * Math.PI / (_k0 * Math.Sqrt(epsMax));
            _rhoMin = Math.Min(Math.Min(1e-4 * lambdaD, 0.01 * d), 0.1 * rhoMax);
            _epsilon = d / 50;
            (_gaImages, _phiImages) = MultiLayerFieldKernels.FieldImages(stackup, sourceInterface, z);

            var grid = new List<double> { _rhoMin };
            double logFactor = Math.Log(10) / 96;
            double maxSpacing = 2 * Math.PI / _k0 / 64;
            double rho = _rhoMin;
            while (rho < rhoMax)
            {
                rho += Math.Min(rho * logFactor, maxSpacing);
                grid.Add(Math.Min(rho, rhoMax));
            }
            while (grid.Count < 4) grid.Add(grid[^1] + maxSpacing);
            var x = grid.ToArray();

            var residues = new (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)[poles.Count];
            for (int p = 0; p < poles.Count; p++)
                residues[p] = MultiLayerFieldKernels.PoleResidues(stackup, _k0, poles[p].KRho, poles[p].IsTm, sourceInterface, z);

            var knots = new (Complex A, Complex W, Complex Phi, Complex Dz, Complex DzA, Complex DzW)[x.Length];
            try
            {
                Parallel.For(0, x.Length,
                    new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 },
                    i =>
                    {
                        var (a, wv, phi, dz, dza, dzw) = SommerfeldIntegrator.FieldRemainderMultiLayer(
                            stackup, _k0, poles, sourceInterface, x[i], z);
                        Complex pa = Complex.Zero, pw = Complex.Zero, pp = Complex.Zero, pd = Complex.Zero;
                        Complex pda = Complex.Zero, pdw = Complex.Zero;
                        for (int p = 0; p < poles.Count; p++)
                        {
                            var factor = new Complex(0, -0.25) * poles[p].KRho
                                         * Bessel.H02(poles[p].KRho * x[i]);
                            pa += residues[p].A * factor;
                            pw += residues[p].W * factor;
                            pp += residues[p].Phi * factor;
                            pd += residues[p].DzPhi * factor;
                            pda += residues[p].DzA * factor;
                            pdw += residues[p].DzW * factor;
                        }
                        knots[i] = (a + pa, wv + pw, phi + pp, dz + pd, dza + pda, dzw + pdw);
                    });
            }
            catch (AggregateException e)
            {
                throw e.InnerExceptions[0];
            }

            _smooth = new NaturalCubicSpline[12];
            var logX = new double[x.Length];
            for (int i = 0; i < x.Length; i++) logX[i] = Math.Log(x[i]);
            for (int c = 0; c < 6; c++)
            {
                var re = new double[x.Length];
                var im = new double[x.Length];
                for (int i = 0; i < x.Length; i++)
                {
                    var v = c switch
                    {
                        0 => knots[i].A, 1 => knots[i].W, 2 => knots[i].Phi,
                        3 => knots[i].Dz, 4 => knots[i].DzA, _ => knots[i].DzW
                    };
                    re[i] = v.Real;
                    im[i] = v.Imaginary;
                }
                _smooth[2 * c] = new NaturalCubicSpline(logX, re);
                _smooth[2 * c + 1] = new NaturalCubicSpline(logX, im);
            }
        }

        public (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW) Evaluate(double rho)
        {
            double clamped = Math.Clamp(rho, _rhoMin, _rhoMax);
            double x = Math.Log(clamped);
            var a = new Complex(_smooth[0].Evaluate(x), _smooth[1].Evaluate(x));
            var wv = new Complex(_smooth[2].Evaluate(x), _smooth[3].Evaluate(x));
            var phi = new Complex(_smooth[4].Evaluate(x), _smooth[5].Evaluate(x));
            var dz = new Complex(_smooth[6].Evaluate(x), _smooth[7].Evaluate(x));
            var dza = new Complex(_smooth[8].Evaluate(x), _smooth[9].Evaluate(x));
            var dzw = new Complex(_smooth[10].Evaluate(x), _smooth[11].Evaluate(x));

            // Closed-form images: dh/dz = +1 (observation above the source metal).
            Complex imgA = Complex.Zero, imgDzA = Complex.Zero;
            foreach (var img in _gaImages)
            {
                double r = Math.Sqrt(rho * rho + img.Depth * img.Depth + _epsilon * _epsilon);
                var (sin, cos) = Math.SinCos(_k0 * r);
                var g = new Complex(cos, -sin) / (4 * Math.PI * r);
                var gPrime = -new Complex(cos, -sin)
                             * (1 + Complex.ImaginaryOne * _k0 * r) / (4 * Math.PI * r * r);
                imgA += img.Coeff * g;
                imgDzA += img.Coeff * (img.Depth / r) * gPrime;
            }
            Complex imgPhi = Complex.Zero, imgDz = Complex.Zero;
            foreach (var img in _phiImages)
            {
                double r = Math.Sqrt(rho * rho + img.Depth * img.Depth + _epsilon * _epsilon);
                var (sin, cos) = Math.SinCos(_k0 * r);
                var g = new Complex(cos, -sin) / (4 * Math.PI * r);
                var gPrime = -new Complex(cos, -sin)
                             * (1 + Complex.ImaginaryOne * _k0 * r) / (4 * Math.PI * r * r);
                imgPhi += img.Coeff * g;
                imgDz += img.Coeff * (img.Depth / r) * gPrime;
            }
            return (RfConstants.Mu0 * imgA + a, wv,
                    imgPhi / RfConstants.Eps0 + phi,
                    imgDz / RfConstants.Eps0 + dz,
                    RfConstants.Mu0 * imgDzA + dza, dzw);
        }
    }
}
