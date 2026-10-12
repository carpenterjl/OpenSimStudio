using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Surface;

namespace OpenSim.Rf.Layered;

/// <summary>
/// FU-7 — the near field of a PROBE-FED sheet with every current the solve has: the sheet's
/// own RWG currents, the junction (its fan current, its half-RWGs and the charge δ(v) its
/// divergence leaves at the probe vertex) and the probe tube. The sheet-only map folded the
/// junction onto the fan's outer edges and left the tube out, so within a few substrate
/// thicknesses of the probe it was the field of a current that stops at the metal.
///
/// <para><b>Above the stack</b> (z above its top) the tube's field comes from the CLASSICAL
/// vertical potential: Ã_z^cl(z) = Ã_top·e^{−jk_z0(z−z_top)} with Ã_top = ∫ I(z′)Ã_z^cl(z_top, z′)dz′
/// (<see cref="VerticalSpectralKernels.ClassicalAtTop"/> for one slab,
/// <see cref="TransmissionLineGreens.ClassicalVerticalAtTop"/> for a stackup). In air,
/// E_z = k_ρ²Ã/(jωµ₀ε₀), E_ρ = ∂_ρ of the transform of −k_z0Ã/(ωµ₀ε₀), H_φ = −∂_ρA_z/µ₀ — no
/// gauge pieces, and only the TM denominator (the formulation-C kernels G̃_A^zz, G̃_A^xz each
/// carry D̂_TE·D̂_TM and cancel the TE poles only in their sum). The Sommerfeld integral runs on
/// an arc lifted ABOVE the real axis past the branch point and every surface-wave pole (with
/// e^{+jωt} a lossy pole sits just below the axis, so the arc keeps the physical side), then
/// along the axis until e^{−k_ρ(z−z_top)} has died; no residues are needed, and the nodes and
/// spectral values serve every point of one height. The classical field includes the charge
/// I(top)/(jω) at the tube's top end; the junction's δ(v) charge, the opposite, is added with
/// its full formulation-C field (Φ and the A_z coupling W), so the two cancel.</para>
///
/// <para><b>Inside the stack</b> the classical integral converges only algebraically next to
/// the tube, so the tube takes the shipped spatial vertical kernels (images, poles, remainder:
/// <see cref="VerticalKernels"/>) in formulation C, Ẽ_x/(−jk_x) = −jωG̃_A^xz − (1/jω)∂_z′K̃_Φ and
/// Ẽ_z = −jωG̃_A^zz − (1/jω)∂_z∂_z′K̃_Φ, integrated over the tube by parts. The end term at the top
/// is exactly the K_Φ field of the top charge, and the junction's δ(v) charge cancels it in the
/// same kernel, so both are dropped and the δ(v) charge keeps only its W leg. E_t = ∇_tU with
/// U = ∫[−jωG_A^xz I + (1/jω)K_Φ I′]dz′; H = ∇×A/µ₀ with A = ẑA_z + ∇_tΨ, so only
/// F = ∫(G_A^zz − ∂_zG_A^xz)I dz′ enters: H_t = (∂_yF, −∂_xF)/µ₀. The ρ and z derivatives are
/// central differences of the kernels (one-sided at the top and bottom of the stack).</para>
///
/// <para>The tube's field is evaluated at lateral distance ≥ its radius (inside the bore it is
/// the field at the wall). Points within about an element of the metal remain surface-scale
/// approximations, as for the sheet-only map.</para>
/// </summary>
public static partial class LayeredFieldEvaluator
{
    /// <summary>The probe-fed near field over a single slab (see the class remarks).</summary>
    public static FieldMap EvaluateProbeFed(SurfaceStructure surface, LayeredKernelTable kernel,
        ProbeFedSolution solution, ProbeFeed probe, IReadOnlyList<Vector3D> points,
        int? maxDegreeOfParallelism = null) =>
        EvaluateProbeFed(surface, kernel, solution, probe, points, maxDegreeOfParallelism, ProbeParts.All);

    /// <summary>The currents a probe-fed map can include; the tests take them apart.</summary>
    [Flags]
    internal enum ProbeParts { Sheet = 1, Halves = 2, Fan = 4, VertexCharge = 8, Tube = 16, All = 31 }

    /// <summary>As above, with only <paramref name="parts"/> (the tests show what each closes).</summary>
    internal static FieldMap EvaluateProbeFed(SurfaceStructure surface, LayeredKernelTable kernel,
        ProbeFedSolution solution, ProbeFeed probe, IReadOnlyList<Vector3D> points,
        int? maxDegreeOfParallelism, ProbeParts parts)
    {
        var substrate = kernel.Substrate;
        double d = substrate.ThicknessMeters;
        double k0 = kernel.K0;
        return EvaluateProbeFedCore(surface, solution, probe, points, maxDegreeOfParallelism,
            k0, top: d, kMedium: k0 * Math.Sqrt(substrate.RelativePermittivity),
            (z, rhoMax) => new FieldKernelTable(substrate, kernel, z, rhoMax, maxDegreeOfParallelism),
            (kRho, kz0, zPrime) => VerticalSpectralKernels.ClassicalAtTop(substrate, k0, kRho, kz0, zPrime),
            new VerticalKernelSet(substrate, kernel.FrequencyHz), parts);
    }

    /// <summary>The probe-fed near field over a grounded stackup — the coplanar-top patch or a
    /// patch buried under a cover (see the class remarks).</summary>
    public static FieldMap EvaluateProbeFed(SurfaceStructure surface, MultiLayerKernelTable kernel,
        ProbeFedSolution solution, ProbeFeed probe, IReadOnlyList<Vector3D> points,
        int? maxDegreeOfParallelism = null)
    {
        if (kernel.IsShielded || kernel.IsUngrounded)
            throw new ArgumentException("A probe needs the vertical kernels, which are written for a grounded stack "
                + "with an open top.", nameof(kernel));
        var stackup = kernel.Stackup;
        int sourceInterface = kernel.SourceInterface ?? stackup.Layers.Count - 1;
        double k0 = kernel.K0;
        double epsMax = stackup.Layers.Max(l => l.RelativePermittivity);
        return EvaluateProbeFedCore(surface, solution, probe, points, maxDegreeOfParallelism,
            k0, top: stackup.TotalThicknessMeters, kMedium: k0 * Math.Sqrt(epsMax),
            (z, rhoMax) => new MultiLayerFieldKernelTable(stackup, k0, kernel.Poles, sourceInterface, z,
                rhoMax, maxDegreeOfParallelism),
            (kRho, kz0, zPrime) => TransmissionLineGreens.ClassicalVerticalAtTop(stackup, k0, kRho, kz0, zPrime),
            new MultiLayerVerticalKernelSet(stackup, kernel.FrequencyHz), ProbeParts.All);
    }

    private static FieldMap EvaluateProbeFedCore(SurfaceStructure surface, ProbeFedSolution solution,
        ProbeFeed probe, IReadOnlyList<Vector3D> points, int? maxDegreeOfParallelism, double k0,
        double top, double kMedium, Func<double, double, IRadialFieldKernel> tableAt,
        Func<Complex, Complex, double, Complex> classicalTop, VerticalKernels vertical, ProbeParts parts)
    {
        bool withTube = parts.HasFlag(ProbeParts.Tube);
        double omega = 2 * Math.PI * solution.Surface.FrequencyHz;
        var jOmega = Complex.ImaginaryOne * omega;
        int vertex = Enumerable.Range(0, surface.Vertices.Count).MinBy(i =>
        {
            double dx = surface.Vertices[i].X - probe.X, dy = surface.Vertices[i].Y - probe.Y;
            return dx * dx + dy * dy;
        });
        var fan = new AttachmentFan(surface, vertex, probe.RadiusMeters);
        var v = fan.VertexPosition;
        double metal = v.Z;
        Complex junction = solution.TubeCurrents[^1];

        // The half-RWGs: γJ·(l/2A)(p_opp − r) on each wedge's outward neighbour, i.e. a term
        // C = −γJ·l/(2A) on (r − p_opp).
        var extra = new List<(Complex C, int Opposite)>[surface.Triangles.Count];
        foreach (var wedge in fan.Wedges)
        {
            int t = wedge.NeighborTriangle;
            double l = surface.Edges[wedge.EdgeBasis].Length;
            (extra[t] ??= new List<(Complex, int)>()).Add(
                (-wedge.Gamma * junction * l / (2 * surface.TriangleAreas[t]), wedge.NeighborOpposite));
        }
        var sheet = new SurfaceMomSolution(solution.Surface.FrequencyHz, solution.Surface.InputImpedance,
            parts.HasFlag(ProbeParts.Sheet) ? solution.RawEdgeCurrents : new Complex[solution.RawEdgeCurrents.Length]);
        if (!parts.HasFlag(ProbeParts.Halves)) Array.Clear(extra);
        Complex vertexCharge = -junction / jOmega;
        var tube = new Tube(solution.TubeNodes, solution.TubeCurrents, v.X, v.Y,
            Math.Max(probe.RadiusMeters, 1e-12));

        double rhoMax = 1e-9;
        foreach (var p in points)
            foreach (var q in surface.Vertices)
            {
                double dx = p.X - q.X, dy = p.Y - q.Y;
                rhoMax = Math.Max(rhoMax, Math.Sqrt(dx * dx + dy * dy));
            }
        rhoMax *= 1.05;

        var groups = new Dictionary<double, List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            if (!groups.TryGetValue(points[i].Z, out var list)) groups[points[i].Z] = list = new List<int>();
            list.Add(i);
        }
        var fields = new (Complex X, Complex Y, Complex Z)[points.Count];
        var hFields = new (Complex X, Complex Y, Complex Z)[points.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 };
        foreach (var (z, indices) in groups.OrderBy(g => g.Key))
        {
            if (z <= 0) continue;   // at or below the ground plane E = H = 0
            var table = tableAt(z, rhoMax);
            // Just above the top (a board overlay at the copper's mid-height) the spectral tail
            // would need millions of nodes: there the tube is read at the top plane from inside.
            bool above = z > top + AboveMargin * top;
            var spectral = above && withTube
                ? new TubeAbove(classicalTop, k0, kMedium, tube, top, z, rhoMax, omega, maxDegreeOfParallelism)
                : null;
            var inside = above || !withTube ? null
                : new TubeInside(vertical, tube, Math.Min(z, top), rhoMax, maxDegreeOfParallelism);
            try
            {
                Parallel.ForEach(indices, options, i =>
                {
                    var point = points[i];
                    var (e, h) = FieldAt(surface, sheet, table, omega, point, extra);
                    if (parts.HasFlag(ProbeParts.Fan)) Add(ref e, ref h, FanField(fan, surface, table, omega, point, metal, junction));
                    if (parts.HasFlag(ProbeParts.VertexCharge))
                        Add(ref e, ref h, VertexChargeField(table, omega, point, v, vertexCharge, withPotential: above));
                    if (withTube) Add(ref e, ref h, above ? spectral!.At(point) : inside!.At(point, omega));
                    fields[i] = e;
                    hFields[i] = h;
                });
            }
            catch (AggregateException ex)
            {
                throw ex.InnerExceptions[0];
            }
        }
        return Package(points, fields, hFields);
    }

    /// <summary>Points less than this fraction of the stack height above its top read the tube from
    /// inside, at the top plane.</summary>
    internal const double AboveMargin = 0.05;

    private static void Add(ref (Complex X, Complex Y, Complex Z) e, ref (Complex X, Complex Y, Complex Z) h,
        ((Complex X, Complex Y, Complex Z) E, (Complex X, Complex Y, Complex Z) H) more)
    {
        e = (e.X + more.E.X, e.Y + more.E.Y, e.Z + more.E.Z);
        h = (h.X + more.H.X, h.Y + more.H.Y, h.Z + more.H.Z);
    }

    /// <summary>The probe tube: node heights (0 = ground … top = the metal), node currents
    /// (rooftop interpolation, last = the junction), lateral position and radius.</summary>
    internal sealed record Tube(double[] Nodes, Complex[] Currents, double X, double Y, double Radius);

    /// <summary>A step for central differences of a smooth function of a length near
    /// <paramref name="scale"/>.</summary>
    private static double Step(double scale) => 1e-4 * scale;

    /// <summary>The fan current's field: −jωA, and H = ∇×A/µ₀ with ∂_zA from the ∂zG̃_A kernel and
    /// the in-plane curl from the kernel's ρ-derivative. Chargeless, so no Φ or W leg.</summary>
    private static ((Complex X, Complex Y, Complex Z) E, (Complex X, Complex Y, Complex Z) H) FanField(
        AttachmentFan fan, SurfaceStructure surface, IRadialFieldKernel table, double omega, Vector3D point,
        double metal, Complex junction)
    {
        Complex ax = 0, ay = 0, dzAx = 0, dzAy = 0, dxAy = 0, dyAx = 0;
        fan.ForEachCurrentPoint(surface, point, point.Z - metal, (source, j) =>
        {
            double dx = point.X - source.X, dy = point.Y - source.Y;
            double rho = Math.Sqrt(dx * dx + dy * dy);
            var k = table.Evaluate(rho);
            ax += k.A * j.X;
            ay += k.A * j.Y;
            dzAx += k.DzA * j.X;
            dzAy += k.DzA * j.Y;
            if (rho > 0)
            {
                double step = Step(rho);
                var dA = (table.Evaluate(rho + step).A - table.Evaluate(rho - step).A) / (2 * step);
                dxAy += dA * (dx / rho) * j.Y;
                dyAx += dA * (dy / rho) * j.X;
            }
        });
        var jOmega = Complex.ImaginaryOne * omega;
        double inv = junction.Magnitude == 0 ? 0 : 1 / RfConstants.Mu0;
        return ((-jOmega * junction * ax, -jOmega * junction * ay, 0),
                (-inv * junction * dzAy, inv * junction * dzAx, inv * junction * (dxAy - dyAx)));
    }

    /// <summary>The field of the junction's δ(v) charge q: its A_z coupling A_z = −jω·q·W, and
    /// — only where the tube's top charge is not dropped against it — its potential q·K_Φ.</summary>
    private static ((Complex X, Complex Y, Complex Z) E, (Complex X, Complex Y, Complex Z) H) VertexChargeField(
        IRadialFieldKernel table, double omega, Vector3D point, Vector3D vertex, Complex charge, bool withPotential)
    {
        double dx = point.X - vertex.X, dy = point.Y - vertex.Y;
        double rho = Math.Sqrt(dx * dx + dy * dy);
        var k = table.Evaluate(rho);
        Complex ez = -omega * omega * charge * k.W - (withPotential ? charge * k.DzPhi : 0);
        Complex ex = 0, ey = 0, hx = 0, hy = 0;
        if (rho > 0)
        {
            double step = Step(rho);
            var plus = table.Evaluate(rho + step);
            var minus = table.Evaluate(rho - step);
            var dW = (plus.W - minus.W) / (2 * step);
            var jOmega = Complex.ImaginaryOne * omega;
            Complex dxAz = -jOmega * charge * dW * (dx / rho), dyAz = -jOmega * charge * dW * (dy / rho);
            hx = dyAz / RfConstants.Mu0;
            hy = -dxAz / RfConstants.Mu0;
            if (withPotential)
            {
                var dPhi = (plus.Phi - minus.Phi) / (2 * step);
                ex = -charge * dPhi * (dx / rho);
                ey = -charge * dPhi * (dy / rho);
            }
        }
        return ((ex, ey, ez), (hx, hy, 0));
    }

    /// <summary>The tube's field at one height inside the stack (formulation C, top end term
    /// dropped — see the class remarks). The three spatial kernels are tabulated once per height
    /// against log ρ — for each tube quadrature height z′ and each height of the z stencil — so a
    /// map costs a table, not ~150 Sommerfeld integrals per point.</summary>
    internal sealed class TubeInside
    {
        private readonly Tube _tube;
        private readonly double[] _zWeights;
        private readonly double _rhoMin, _rhoMax;
        private readonly (double Z, Complex Current, Complex Slope, double Weight)[] _sources;
        // [source, stencil height, kernel] → (re, im) splines over log ρ; stencil 0 is z itself.
        private readonly NaturalCubicSpline[,,] _splines;

        public TubeInside(VerticalKernels kernels, Tube tube, double z, double rhoMax,
            int? maxDegreeOfParallelism = null)
        {
            _tube = tube;
            double total = kernels.TotalThicknessMeters;
            z = Math.Clamp(z, 0, total);
            double dZ = Step(total);
            // z stencil: central where it fits, one-sided at the stack's bottom and top.
            double[] zs;
            if (z - dZ >= 0 && z + dZ <= total) { zs = new[] { z, z - dZ, z + dZ }; _zWeights = new[] { 0, -0.5 / dZ, 0.5 / dZ }; }
            else if (z + dZ > total) { zs = new[] { z, z - dZ, z - 2 * dZ }; _zWeights = new[] { 1.5 / dZ, -2 / dZ, 0.5 / dZ }; }
            else { zs = new[] { z, z + dZ, z + 2 * dZ }; _zWeights = new[] { -1.5 / dZ, 2 / dZ, -0.5 / dZ }; }

            var sources = new List<(double, Complex, Complex, double)>();
            var (gn, gw) = GaussLegendre.Rule(4, 0, 1);
            for (int e = 0; e + 1 < tube.Nodes.Length; e++)
            {
                double h = tube.Nodes[e + 1] - tube.Nodes[e];
                Complex slope = (tube.Currents[e + 1] - tube.Currents[e]) / h;
                for (int q = 0; q < gn.Length; q++)
                    sources.Add((tube.Nodes[e] + h * gn[q],
                        tube.Currents[e] * (1 - gn[q]) + tube.Currents[e + 1] * gn[q], slope, gw[q] * h));
            }
            _sources = sources.ToArray();

            // Log-spaced knots (48 a decade) from just inside the bore, capped at a 64th of the
            // dielectric wavelength so the surface-wave Hankel tail stays resolved.
            _rhoMin = 0.9 * tube.Radius;
            _rhoMax = Math.Max(1.05 * rhoMax, 2 * tube.Radius);
            double lambda = 2 * Math.PI / (kernels.K0 * Math.Sqrt(kernels.MaxRelativePermittivity));
            var grid = new List<double> { _rhoMin };
            while (grid[^1] < _rhoMax)
                grid.Add(grid[^1] + Math.Min(grid[^1] * (Math.Pow(10, 1.0 / 48) - 1), lambda / 64));
            var rho = grid.ToArray();
            var logRho = rho.Select(r => Math.Log(r)).ToArray();

            int nS = _sources.Length, nZ = zs.Length, nR = rho.Length;
            var values = new (Complex GAzz, Complex GAxz, Complex KPhi)[nS, nZ, nR];
            Parallel.For(0, nS * nZ * nR, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 }, i =>
            {
                int r = i % nR, zi = i / nR % nZ, si = i / (nR * nZ);
                values[si, zi, r] = kernels.Evaluate(rho[r], zs[zi], _sources[si].Z);
            });
            _splines = new NaturalCubicSpline[nS, nZ, 6];
            for (int si = 0; si < nS; si++)
                for (int zi = 0; zi < nZ; zi++)
                    for (int c = 0; c < 3; c++)
                    {
                        var re = new double[nR];
                        var im = new double[nR];
                        for (int r = 0; r < nR; r++)
                        {
                            var v = values[si, zi, r];
                            var x = c == 0 ? v.GAzz : c == 1 ? v.GAxz : v.KPhi;
                            re[r] = x.Real;
                            im[r] = x.Imaginary;
                        }
                        _splines[si, zi, 2 * c] = new NaturalCubicSpline(logRho, re);
                        _splines[si, zi, 2 * c + 1] = new NaturalCubicSpline(logRho, im);
                    }
        }

        private Complex Kernel(int source, int height, int which, double rho)
        {
            double x = Math.Log(Math.Clamp(rho, _rhoMin, _rhoMax));
            return new Complex(_splines[source, height, 2 * which].Evaluate(x),
                _splines[source, height, 2 * which + 1].Evaluate(x));
        }

        public ((Complex X, Complex Y, Complex Z) E, (Complex X, Complex Y, Complex Z) H) At(Vector3D point, double omega)
        {
            double dx = point.X - _tube.X, dy = point.Y - _tube.Y;
            double lateral = Math.Sqrt(dx * dx + dy * dy);
            double rho = Math.Max(lateral, _tube.Radius);
            double dRho = Step(rho);
            var jOmega = Complex.ImaginaryOne * omega;
            Complex uPlus = 0, uMinus = 0, ez = 0, fPlus = 0, fMinus = 0;
            for (int si = 0; si < _sources.Length; si++)
            {
                var (_, current, slope, w) = _sources[si];
                Complex dzK = 0, dzGxzPlus = 0, dzGxzMinus = 0;
                for (int zi = 0; zi < _zWeights.Length; zi++)
                {
                    if (_zWeights[zi] == 0) continue;
                    dzK += _zWeights[zi] * Kernel(si, zi, 2, rho);
                    dzGxzPlus += _zWeights[zi] * Kernel(si, zi, 1, rho + dRho);
                    dzGxzMinus += _zWeights[zi] * Kernel(si, zi, 1, rho - dRho);
                }
                uPlus += w * (-jOmega * Kernel(si, 0, 1, rho + dRho) * current + Kernel(si, 0, 2, rho + dRho) * slope / jOmega);
                uMinus += w * (-jOmega * Kernel(si, 0, 1, rho - dRho) * current + Kernel(si, 0, 2, rho - dRho) * slope / jOmega);
                ez += w * (-jOmega * Kernel(si, 0, 0, rho) * current + dzK * slope / jOmega);
                fPlus += w * (Kernel(si, 0, 0, rho + dRho) - dzGxzPlus) * current;
                fMinus += w * (Kernel(si, 0, 0, rho - dRho) - dzGxzMinus) * current;
            }
            Complex dU = (uPlus - uMinus) / (2 * dRho), dF = (fPlus - fMinus) / (2 * dRho);
            double ux = lateral > 0 ? dx / lateral : 0, uy = lateral > 0 ? dy / lateral : 0;
            double inv = 1 / RfConstants.Mu0;
            return ((dU * ux, dU * uy, ez), (inv * dF * uy, -inv * dF * ux, 0));
        }
    }

    /// <summary>The tube's field at one height above the stack (classical, see the class
    /// remarks): the spectral nodes and values are computed once and serve every point.</summary>
    internal sealed class TubeAbove
    {
        private static readonly (double[] Nodes, double[] Weights)[] UnitRules =
            Enumerable.Range(0, 41).Select(n => n < 1 ? (Array.Empty<double>(), Array.Empty<double>())
                : GaussLegendre.Rule(n, 0, 1)).ToArray();

        private readonly Complex[] _k, _weight, _value, _kz0;
        private readonly double _omega;
        private readonly Tube _tube;

        public TubeAbove(Func<Complex, Complex, double, Complex> classicalTop, double k0, double kMedium,
            Tube tube, double top, double z, double rhoMax, double omega, int? maxDegreeOfParallelism = null)
        {
            double height = z - top;
            if (!(height > 0)) throw new ArgumentOutOfRangeException(nameof(z), "The point must lie above the stack.");
            _omega = omega;
            _tube = tube;
            // The arc: k = x + j·lift·sin(πx/L) over [0, L], past the branch point and every pole
            // (they lie in (k₀, kMedium]); its height is capped so J's growth e^{lift·ρ} stays small.
            double reach = Math.Max(1.5 * kMedium, 1.2 * k0);
            double lift = Math.Min(0.25 * k0, 2 / rhoMax);
            double tailEnd = reach + 46 / height;
            var (gn, gw) = GaussLegendre.Rule(8);
            var nodes = new List<(Complex K, Complex Weight)>();
            int arcPanels = Math.Max(24, (int)Math.Ceiling(Math.Max(reach / (0.25 * lift), reach * rhoMax / Math.PI)));
            for (int p = 0; p < arcPanels; p++)
            {
                double x0 = reach * p / arcPanels, x1 = reach * (p + 1) / arcPanels;
                double mid = 0.5 * (x0 + x1), half = 0.5 * (x1 - x0);
                for (int i = 0; i < gn.Length; i++)
                {
                    double x = mid + half * gn[i];
                    var (sin, cos) = Math.SinCos(Math.PI * x / reach);
                    var k = new Complex(x, lift * sin);
                    var dk = new Complex(1, lift * Math.PI / reach * cos);
                    nodes.Add((k, gw[i] * half * dk));
                }
            }
            double width = Math.Min(Math.PI / rhoMax, 2 / height);
            int tailPanels = (int)Math.Ceiling((tailEnd - reach) / width);
            for (int p = 0; p < tailPanels; p++)
            {
                double x0 = reach + (tailEnd - reach) * p / tailPanels, x1 = reach + (tailEnd - reach) * (p + 1) / tailPanels;
                double mid = 0.5 * (x0 + x1), half = 0.5 * (x1 - x0);
                for (int i = 0; i < gn.Length; i++)
                    nodes.Add((new Complex(mid + half * gn[i], 0), gw[i] * half));
            }

            int n = nodes.Count;
            _k = new Complex[n];
            _weight = new Complex[n];
            _value = new Complex[n];
            _kz0 = new Complex[n];
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 }, i =>
            {
                var (k, weight) = nodes[i];
                var kz0 = SpectralKernels.Kz(k0 * k0, k);
                double decay = Math.Max(k.Real, 0);
                Complex sum = 0;
                for (int e = 0; e + 1 < tube.Nodes.Length; e++)
                {
                    double za = tube.Nodes[e], zb = tube.Nodes[e + 1];
                    // Ã_top(z′) ~ e^{−k(z_top − z′)}: what lies more than 50 decay lengths down is nothing.
                    if (decay * (top - zb) > 50) continue;
                    double lower = decay > 0 ? Math.Max(za, zb - 50 / decay) : za;
                    int count = Math.Clamp(4 + (int)(0.6 * decay * (zb - lower)), 4, 40);
                    var (qn, qw) = UnitRules[count];
                    for (int q = 0; q < qn.Length; q++)
                    {
                        double zp = lower + (zb - lower) * qn[q];
                        double t = (zp - za) / (zb - za);
                        Complex current = tube.Currents[e] * (1 - t) + tube.Currents[e + 1] * t;
                        sum += qw[q] * (zb - lower) * current * classicalTop(k, kz0, zp);
                    }
                }
                _k[i] = k;
                _weight[i] = weight / (4 * Math.PI);
                _value[i] = sum * Complex.Exp(-Complex.ImaginaryOne * kz0 * height);
                _kz0[i] = kz0;
            });
        }

        /// <summary>E and H of the tube at a point of this height.</summary>
        public ((Complex X, Complex Y, Complex Z) E, (Complex X, Complex Y, Complex Z) H) At(Vector3D point)
        {
            double dx = point.X - _tube.X, dy = point.Y - _tube.Y;
            double rho = Math.Sqrt(dx * dx + dy * dy);
            Complex az = 0, dAz = 0, ez = 0, er = 0;
            for (int i = 0; i < _k.Length; i++)
            {
                var k = _k[i];
                var (j0, j1) = Bessel.J01(k * rho);
                var wf = _weight[i] * _value[i];
                az += wf * j0 * k;
                dAz -= wf * j1 * k * k;
                ez += wf * k * k * j0 * k;
                er += wf * _kz0[i] * j1 * k * k;
            }
            double c = _omega * RfConstants.Mu0 * RfConstants.Eps0;
            ez /= Complex.ImaginaryOne * c;
            er /= c;
            double ux = rho > 0 ? dx / rho : 0, uy = rho > 0 ? dy / rho : 0;
            double inv = 1 / RfConstants.Mu0;
            return ((er * ux, er * uy, ez), (inv * dAz * uy, -inv * dAz * ux, 0));
        }
    }
}
