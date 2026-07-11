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
        foreach (var (z, indices) in groups.OrderBy(g => g.Key))
        {
            if (z <= 0)
                continue; // PEC interior/ground: E = 0 (slots stay zero)
            var table = new FieldKernelTable(substrate, kernel, z, rhoMax, maxDegreeOfParallelism);
            try
            {
                Parallel.ForEach(indices,
                    new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 },
                    i => fields[i] = FieldAt(surface, solution, table, omega, points[i]));
            }
            catch (AggregateException e)
            {
                throw e.InnerExceptions[0];
            }
        }

        var magnitudes = new double[points.Count];
        var snapshots = new Vector3D[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var (ex, ey, ez) = fields[i];
            magnitudes[i] = Math.Sqrt(ex.Magnitude * ex.Magnitude
                + ey.Magnitude * ey.Magnitude + ez.Magnitude * ez.Magnitude);
            snapshots[i] = new Vector3D(ex.Real, ey.Real, ez.Real);
        }
        return new FieldMap(points, fields, magnitudes, snapshots);
    }

    private static (Complex X, Complex Y, Complex Z) FieldAt(SurfaceStructure surface,
        SurfaceMomSolution solution, FieldKernelTable table, double omega, Vector3D point)
    {
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(5);
        var (gl, glw) = GaussLegendre.Rule(4);
        Complex ax = Complex.Zero, ay = Complex.Zero;
        Complex azW = Complex.Zero, phiDz = Complex.Zero;
        Complex gx = Complex.Zero, gy = Complex.Zero;

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
                }
            }

            // In-plane ∇Φ by the divergence theorem (q constant per triangle):
            // ∇Φ = −q ∮ K_Φ n̂′ dl′ over the ORIGINAL triangle boundary (interior
            // panel edges cancel pairwise, so panelling is unnecessary here).
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

                for (int i = 0; i < gl.Length; i++)
                {
                    var source = p1 + direction * (0.5 * (gl[i] + 1));
                    double dx = point.X - source.X, dy = point.Y - source.Y;
                    double rho = Math.Sqrt(dx * dx + dy * dy);
                    var kPhi = table.Evaluate(rho).Phi;
                    Complex lineWeight = -charge * glw[i] * (length / 2) * kPhi;
                    gx += lineWeight * normal.X;
                    gy += lineWeight * normal.Y;
                }
            }
        }

        var jOmega = Complex.ImaginaryOne * omega;
        var ex = -jOmega * ax - gx;
        var ey = -jOmega * ay - gy;
        var ez = -omega * omega * azW - phiDz;
        return (ex, ey, ez);
    }

    /// <summary>One observation height's radial kernels: closed-form images at eval
    /// (exact — they carry all singular content) + one spline over
    /// [remainder + pole terms], knots evaluated in parallel (bitwise-deterministic
    /// slot recipe). Grid: log steps capped at λ₀/64 so the splined H₀⁽²⁾ pole
    /// oscillation stays resolved (the production far-grid reasoning).</summary>
    private sealed class FieldKernelTable
    {
        private readonly SubstrateStackup _substrate;
        private readonly double _k0, _z, _d, _rhoMin, _rhoMax, _epsilon;
        private readonly LayeredFieldKernels.KernelImage[] _images;
        private readonly NaturalCubicSpline[] _smooth; // A/W/Phi/DzPhi × re/im

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
            var residues = new (Complex A, Complex W, Complex Phi, Complex DzPhi)[poles.Count];
            for (int p = 0; p < poles.Count; p++)
                residues[p] = LayeredFieldKernels.PoleResidues(substrate, _k0, poles[p].KRho, z);

            var knots = new (Complex A, Complex W, Complex Phi, Complex Dz)[x.Length];
            try
            {
                Parallel.For(0, x.Length,
                    new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 },
                    i =>
                    {
                        var (a, wv, phi, dz) = SommerfeldIntegrator.FieldRemainder(
                            substrate, _k0, poles, x[i], z);
                        Complex pa = Complex.Zero, pw = Complex.Zero, pp = Complex.Zero, pd = Complex.Zero;
                        for (int p = 0; p < poles.Count; p++)
                        {
                            var factor = new Complex(0, -0.25) * poles[p].KRho
                                         * Bessel.H02(poles[p].KRho * x[i]);
                            pa += residues[p].A * factor;
                            pw += residues[p].W * factor;
                            pp += residues[p].Phi * factor;
                            pd += residues[p].DzPhi * factor;
                        }
                        knots[i] = (a + pa, wv + pw, phi + pp, dz + pd);
                    });
            }
            catch (AggregateException e)
            {
                throw e.InnerExceptions[0];
            }

            _smooth = new NaturalCubicSpline[8];
            var logX = new double[x.Length];
            for (int i = 0; i < x.Length; i++) logX[i] = Math.Log(x[i]);
            for (int c = 0; c < 4; c++)
            {
                var re = new double[x.Length];
                var im = new double[x.Length];
                for (int i = 0; i < x.Length; i++)
                {
                    var v = c switch { 0 => knots[i].A, 1 => knots[i].W, 2 => knots[i].Phi, _ => knots[i].Dz };
                    re[i] = v.Real;
                    im[i] = v.Imaginary;
                }
                _smooth[2 * c] = new NaturalCubicSpline(logX, re);
                _smooth[2 * c + 1] = new NaturalCubicSpline(logX, im);
            }
        }

        public (Complex A, Complex W, Complex Phi, Complex DzPhi) Evaluate(double rho)
        {
            double clamped = Math.Clamp(rho, _rhoMin, _rhoMax);
            double x = Math.Log(clamped);
            var a = new Complex(_smooth[0].Evaluate(x), _smooth[1].Evaluate(x));
            var wv = new Complex(_smooth[2].Evaluate(x), _smooth[3].Evaluate(x));
            var phi = new Complex(_smooth[4].Evaluate(x), _smooth[5].Evaluate(x));
            var dz = new Complex(_smooth[6].Evaluate(x), _smooth[7].Evaluate(x));

            // Closed-form images on the true (unclamped) lateral distance, with the
            // free-space probe's diameter-scale regularization near the source point.
            Complex imgA = Complex.Zero, imgPhi = Complex.Zero, imgDz = Complex.Zero;
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
            }
            return (RfConstants.Mu0 * imgA + a, wv,
                    imgPhi / RfConstants.Eps0 + phi,
                    imgDz / RfConstants.Eps0 + dz);
        }
    }
}
