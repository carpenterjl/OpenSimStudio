using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Tests.Rf.MultiLayerProbeFixtures;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-7 (audit RF-7): the probe-fed near field with the probe's own current — the tube, the
/// junction's fan current and half-RWGs, and the junction's vertex charge — against an image
/// oracle at εr = 1 and against Maxwell's curl equation on a probe-fed patch.
/// </summary>
public class ProbeNearFieldTests
{
    private readonly ITestOutputHelper _out;
    public ProbeNearFieldTests(ITestOutputHelper output) => _out = output;

    private const double Eps0 = 8.8541878128e-12;
    private const double Mu0 = 4e-7 * Math.PI;

    // ------------------------------------------------------------------
    // Bessel J₀, J₁ of complex argument
    // ------------------------------------------------------------------

    [Fact]
    public void J01_OnTheRealAxis_IsJ0AndMinusItsDerivative()
    {
        foreach (double x in new[] { 0.3, 2.0, 7.5, 13.9, 14.1, 30.0, 120.0 })
        {
            var (j0, j1) = Bessel.J01(new Complex(x, 0));
            Assert.InRange((j0 - Bessel.J0(x)).Magnitude, 0, 1e-12);
            double h = 1e-5 * Math.Max(x, 1);
            double derivative = (Bessel.J0(x + h) - Bessel.J0(x - h)) / (2 * h);
            Assert.InRange((j1 + derivative).Magnitude, 0, 1e-8);
        }
    }

    [Fact]
    public void J01_SeriesAndAsymptotics_AgreeOffTheAxis()
    {
        // The two branches share no algebra; where both are accurate they must agree. And J₀′ = −J₁
        // holds for complex z (central differences along the real direction of an analytic function).
        foreach (var z in new[] { new Complex(14, 0.5), new Complex(16, 2), new Complex(18, -1), new Complex(20, 3) })
        {
            var series = Bessel.J01Series(z);
            var asymptotic = Bessel.J01Asymptotic(z);
            double scale = Math.Max(series.J0.Magnitude, series.J1.Magnitude);
            Assert.InRange((series.J0 - asymptotic.J0).Magnitude / scale, 0, 1e-9);
            Assert.InRange((series.J1 - asymptotic.J1).Magnitude / scale, 0, 1e-9);
        }
        foreach (var z in new[] { new Complex(0.7, 0.2), new Complex(5, 0.8), new Complex(25, 1.5) })
        {
            double h = 1e-5 * z.Magnitude;
            var d0 = (Bessel.J01(z + h).J0 - Bessel.J01(z - h).J0) / (2 * h);
            Assert.InRange((d0 + Bessel.J01(z).J1).Magnitude / Bessel.J01(z).J1.Magnitude, 0, 1e-7);
        }
    }

    // ------------------------------------------------------------------
    // The classical vertical potential, one slab against an N = 1 stack
    // ------------------------------------------------------------------

    [Fact]
    public void TheClassicalTopPotential_IsTheFormulationCPairAboveTheSlab()
    {
        // Above the slab A^C = A^cl − ∇Δ/ω², so Ã_z^cl = G̃_A^zz + jk_z0·G̃_A^xz there — the pair the
        // far field already radiates through. Checked off the TE poles, where the pair's two
        // terms are each finite.
        var substrate = new SubstrateStackup(2.2, 0.001, 1.588e-3);
        double k0 = 2 * Math.PI * 9.4e9 / RfConstants.SpeedOfLight, d = substrate.ThicknessMeters;
        foreach (var kRho in new[] { new Complex(0.4 * k0, 0), new Complex(0.95 * k0, 0), new Complex(1.3 * k0, 0.1 * k0), new Complex(6 * k0, 0) })
        {
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            foreach (double zp in new[] { 0.2e-3, 0.9e-3, 1.5e-3 })
            {
                var (gzz, gxz, _) = VerticalSpectralKernels.Evaluate(substrate, k0, kRho, kz0, d, zp);
                var pair = gzz + Complex.ImaginaryOne * kz0 * gxz;
                var classical = VerticalSpectralKernels.ClassicalAtTop(substrate, k0, kRho, kz0, zp);
                Assert.InRange((pair - classical).Magnitude / classical.Magnitude, 0, 1e-10);
            }
        }
    }

    [Fact]
    public void TheClassicalTopPotential_OfOneSlab_IsTheStackupsAtNEqualsOne()
    {
        var substrate = new SubstrateStackup(4.4, 0.02, 1.2e-3);
        var stackup = LayeredStackup.FromSubstrate(substrate);
        double k0 = 2 * Math.PI * 6e9 / RfConstants.SpeedOfLight;
        foreach (var kRho in new[] { new Complex(0.3 * k0, 0), new Complex(1.1 * k0, 0.2 * k0), new Complex(1.9 * k0, 0.05 * k0), new Complex(40 * k0, 0) })
        {
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            foreach (double zp in new[] { 0.1e-3, 0.6e-3, 1.15e-3 })
            {
                var slab = VerticalSpectralKernels.ClassicalAtTop(substrate, k0, kRho, kz0, zp);
                var stack = TransmissionLineGreens.ClassicalVerticalAtTop(stackup, k0, kRho, kz0, zp);
                Assert.InRange((slab - stack).Magnitude / slab.Magnitude, 0, 1e-10);
            }
        }
    }

    // ------------------------------------------------------------------
    // The tube against image theory in air
    // ------------------------------------------------------------------

    /// <summary>A vertical line current over a PEC ground in free space, by images: the current
    /// images upward, its charge −I′/(jω) images with the opposite sign, and — when asked — the
    /// top end's charge I(top)/(jω) with its image. Direct 1-D quadrature of the free-space
    /// potentials; nothing shared with the layered kernels.</summary>
    private static ((Complex X, Complex Y, Complex Z) E, (Complex X, Complex Y, Complex Z) H) LineOverGround(
        double[] nodes, Complex[] currents, double omega, Vector3D point, bool withTopCharge)
    {
        double k = omega / RfConstants.SpeedOfLight;
        var jOmega = Complex.ImaginaryOne * omega;
        Complex ax = 0, ay = 0, az = 0, dxAz = 0, dyAz = 0;
        Complex gx = 0, gy = 0, gz = 0;
        (Complex G, Complex Dx, Complex Dy, Complex Dz) Green(double zs)
        {
            double rx = point.X, ry = point.Y, rz = point.Z - zs;
            double r = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            var g = Complex.Exp(new Complex(0, -k * r)) / r;
            var dg = -(1 + new Complex(0, k * r)) * g / (r * r);
            return (g, dg * rx, dg * ry, dg * rz);
        }
        var (gn, gw) = GaussLegendre.Rule(24, 0, 1);
        for (int e = 0; e + 1 < nodes.Length; e++)
        {
            double h = nodes[e + 1] - nodes[e];
            Complex charge = -(currents[e + 1] - currents[e]) / h / jOmega;
            for (int q = 0; q < gn.Length; q++)
            {
                double zp = nodes[e] + h * gn[q];
                Complex current = currents[e] * (1 - gn[q]) + currents[e + 1] * gn[q];
                double w = gw[q] * h;
                foreach (var (zs, sign) in new[] { (zp, 1.0), (-zp, -1.0) })
                {
                    var (g, dx, dy, dz) = Green(zs);
                    az += w * current * g;
                    dxAz += w * current * dx;
                    dyAz += w * current * dy;
                    gx += w * sign * charge * dx;
                    gy += w * sign * charge * dy;
                    gz += w * sign * charge * dz;
                }
            }
        }
        if (withTopCharge)
        {
            Complex top = currents[^1] / jOmega;
            foreach (var (zs, sign) in new[] { (nodes[^1], 1.0), (-nodes[^1], -1.0) })
            {
                var (_, dx, dy, dz) = Green(zs);
                gx += sign * top * dx;
                gy += sign * top * dy;
                gz += sign * top * dz;
            }
        }
        double a = Mu0 / (4 * Math.PI), f = 1 / (4 * Math.PI * Eps0);
        var e3 = (-f * gx, -f * gy, -jOmega * a * az - f * gz);
        var h3 = (a * dyAz / Mu0, -a * dxAz / Mu0, (Complex)0);
        return (e3, h3);
    }

    private static double Worst(((Complex X, Complex Y, Complex Z) E, (Complex X, Complex Y, Complex Z) H) got,
        ((Complex X, Complex Y, Complex Z) E, (Complex X, Complex Y, Complex Z) H) want)
    {
        static double Norm((Complex X, Complex Y, Complex Z) v) =>
            Math.Sqrt(v.X.Magnitude * v.X.Magnitude + v.Y.Magnitude * v.Y.Magnitude + v.Z.Magnitude * v.Z.Magnitude);
        static (Complex, Complex, Complex) Minus((Complex X, Complex Y, Complex Z) a, (Complex X, Complex Y, Complex Z) b) =>
            (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        return Math.Max(Norm(Minus(got.E, want.E)) / Norm(want.E), Norm(Minus(got.H, want.H)) / Norm(want.H));
    }

    private static readonly double[] TubeNodes = { 0, 0.529e-3, 1.059e-3, 1.588e-3 };
    private static readonly Complex[] TubeCurrents =
        { new(1, 0), new(0.93, -0.12), new(0.81, -0.21), new(0.66, -0.27) };

    [Fact]
    public void InAir_TheTubeAboveTheSlab_IsTheImageLineWithItsTopCharge()
    {
        double d = TubeNodes[^1], f = 9.4e9, omega = 2 * Math.PI * f, k0 = omega / RfConstants.SpeedOfLight;
        var air = new SubstrateStackup(1.0, 0, d);
        var tube = new LayeredFieldEvaluator.Tube(TubeNodes, TubeCurrents, 0, 0, 0.2e-3);
        foreach (double z in new[] { d + 0.3e-3, d + 1.5e-3 })
        {
            var above = new LayeredFieldEvaluator.TubeAbove(
                (k, kz0, zp) => VerticalSpectralKernels.ClassicalAtTop(air, k0, k, kz0, zp),
                k0, k0, tube, d, z, 6e-3, omega);
            foreach (var (x, y) in new[] { (0.8e-3, 0.0), (1.4e-3, 2.1e-3), (-3.5e-3, 1.0e-3) })
            {
                var point = new Vector3D(x, y, z);
                double worst = Worst(above.At(point), LineOverGround(TubeNodes, TubeCurrents, omega, point, true));
                _out.WriteLine($"above z {z * 1e3:f2} mm at ({x * 1e3}, {y * 1e3}) mm: {worst:e2}");
                Assert.InRange(worst, 0, 1e-6);
            }
        }
    }

    [Fact]
    public void InAir_TheTubeInsideTheSlab_IsTheImageLineWithoutItsTopCharge()
    {
        double d = TubeNodes[^1], f = 9.4e9, omega = 2 * Math.PI * f;
        var air = new SubstrateStackup(1.0, 0, d);
        var kernels = new VerticalKernelSet(air, f);
        var tube = new LayeredFieldEvaluator.Tube(TubeNodes, TubeCurrents, 0, 0, 0.2e-3);
        foreach (double z in new[] { 0.37 * d, 0.81 * d, d })
        {
            var inside = new LayeredFieldEvaluator.TubeInside(kernels, tube, z, 6e-3);
            foreach (var (x, y) in new[] { (0.8e-3, 0.0), (1.4e-3, 2.1e-3), (-3.5e-3, 1.0e-3) })
            {
                var point = new Vector3D(x, y, z);
                double worst = Worst(inside.At(point, omega), LineOverGround(TubeNodes, TubeCurrents, omega, point, false));
                _out.WriteLine($"inside z {z / d:f2}d at ({x * 1e3}, {y * 1e3}) mm: {worst:e2}");
                Assert.InRange(worst, 0, 1e-4);
            }
        }
    }

    // ------------------------------------------------------------------
    // Maxwell on the probe-fed patch
    // ------------------------------------------------------------------

    /// <summary>|∇×H − jωεE| / |jωεE| at a point, ∇×H by central differences of the map.</summary>
    private static double CurlResidual(Func<IReadOnlyList<Vector3D>, FieldMap> evaluate, Vector3D p,
        double h, double epsR, double omega)
    {
        var offsets = new[]
        {
            new Vector3D(h, 0, 0), new Vector3D(-h, 0, 0), new Vector3D(0, h, 0),
            new Vector3D(0, -h, 0), new Vector3D(0, 0, h), new Vector3D(0, 0, -h)
        };
        var points = new List<Vector3D> { p };
        points.AddRange(offsets.Select(o => p + o));
        var map = evaluate(points);
        var hs = map.H!;
        Complex dHzdy = (hs[3].Z - hs[4].Z) / (2 * h), dHydz = (hs[5].Y - hs[6].Y) / (2 * h);
        Complex dHxdz = (hs[5].X - hs[6].X) / (2 * h), dHzdx = (hs[1].Z - hs[2].Z) / (2 * h);
        Complex dHydx = (hs[1].Y - hs[2].Y) / (2 * h), dHxdy = (hs[3].X - hs[4].X) / (2 * h);
        var curl = (dHzdy - dHydz, dHxdz - dHzdx, dHydx - dHxdy);
        var jwe = Complex.ImaginaryOne * omega * Eps0 * epsR;
        var e = map.E[0];
        var want = (jwe * e.X, jwe * e.Y, jwe * e.Z);
        double num = Math.Sqrt(Math.Pow((curl.Item1 - want.Item1).Magnitude, 2) + Math.Pow((curl.Item2 - want.Item2).Magnitude, 2)
                               + Math.Pow((curl.Item3 - want.Item3).Magnitude, 2));
        double den = Math.Sqrt(Math.Pow(want.Item1.Magnitude, 2) + Math.Pow(want.Item2.Magnitude, 2) + Math.Pow(want.Item3.Magnitude, 2));
        return num / den;
    }

    [Fact]
    public void NextToTheProbe_TheProbesCurrentsObeyAmpere_AndTheTubeIsWhatClosesThem()
    {
        // The Balanis probe-fed patch at 9.4 GHz, 1.4 mm mesh. Inside the substrate the tube's end
        // charge and the junction's vertex charge are dropped against each other (formulation C),
        // so only the probe's currents TOGETHER — the tube, the junction's fan current and
        // half-RWGs, and the vertex charge's W leg — conserve charge: their field must satisfy
        // ∇×H = jωεE 1–2 mm from the probe, and without the tube it fails by O(1).
        //
        // (Above the substrate each part closes on its own — the classical tube field carries its
        // top charge, and the fan, the halves and the vertex charge balance — so Ampère cannot see
        // the tube there; the εr = 1 image oracle and the kernel identity carry that path. The
        // sheet's RWG currents are checked away from the metal below: within about an element of
        // it the sheet near field is a surface-scale approximation, as the evaluator says.)
        var table = new LayeredKernelTable(Substrate, Frequency, rhoMax: 0.025);
        var surface = Plate(MeshEdge);
        var probe = Probe();
        var solution = new SurfaceMomSolver().SolveProbeFed(surface, table, probe);
        double omega = 2 * Math.PI * Frequency;
        const LayeredFieldEvaluator.ProbeParts Probe_ = LayeredFieldEvaluator.ProbeParts.Halves
            | LayeredFieldEvaluator.ProbeParts.Fan | LayeredFieldEvaluator.ProbeParts.VertexCharge;
        Func<IReadOnlyList<Vector3D>, FieldMap> Parts(LayeredFieldEvaluator.ProbeParts parts) => pts =>
            LayeredFieldEvaluator.EvaluateProbeFed(surface, table, solution, probe, pts, null, parts);
        foreach (var (dx, dy, z, epsR) in new[]
                 {
                     (1.3e-3, -0.6e-3, 0.5 * Thickness, Substrate.RelativePermittivity),
                     (-0.9e-3, 1.4e-3, 0.3 * Thickness, Substrate.RelativePermittivity),
                     (1.8e-3, 0.4e-3, 0.8 * Thickness, Substrate.RelativePermittivity),
                 })
        {
            var p = new Vector3D(probe.X + dx, probe.Y + dy, z);
            double with = CurlResidual(Parts(Probe_ | LayeredFieldEvaluator.ProbeParts.Tube), p, 0.02e-3, epsR, omega);
            double without = CurlResidual(Parts(Probe_), p, 0.02e-3, epsR, omega);
            _out.WriteLine($"({dx * 1e3}, {dy * 1e3}) mm at z {z * 1e3:f3} mm: probe currents {with:e2}, without the tube {without:e2}");
            Assert.InRange(with, 0, 0.02);
            Assert.True(without > 0.2, $"without the tube the residual should be large: {without:e2}");
        }
    }

    [Fact]
    public void AwayFromTheMetal_TheCompleteProbeFedFieldObeysAmpere()
    {
        // Every current together, two millimetres above the patch plane and inside the substrate.
        var table = new LayeredKernelTable(Substrate, Frequency, rhoMax: 0.025);
        var surface = Plate(MeshEdge);
        var probe = Probe();
        var solution = new SurfaceMomSolver().SolveProbeFed(surface, table, probe);
        double omega = 2 * Math.PI * Frequency;
        FieldMap Full(IReadOnlyList<Vector3D> pts) =>
            LayeredFieldEvaluator.EvaluateProbeFed(surface, table, solution, probe, pts);
        foreach (var (dx, dy, z, epsR) in new[]
                 {
                     (1.2e-3, 0.7e-3, Thickness + 2.0e-3, 1.0),
                     (-2.5e-3, 1.5e-3, Thickness + 2.0e-3, 1.0),
                     (1.3e-3, -0.6e-3, 0.5 * Thickness, Substrate.RelativePermittivity),
                 })
        {
            var p = new Vector3D(probe.X + dx, probe.Y + dy, z);
            double residual = CurlResidual(Full, p, 0.02e-3, epsR, omega);
            _out.WriteLine($"({dx * 1e3}, {dy * 1e3}) mm at z {z * 1e3:f3} mm: complete field {residual:e2}");
            Assert.InRange(residual, 0, 0.02);
        }
    }

    [Fact]
    public void OverAOneLayerStackup_TheProbeFedMapIsTheSingleSlabs()
    {
        // The stackup path (TLGF kernels, its own classical top potential, its own spatial vertical
        // kernels) against the single-slab path on the same patch: an identity at N = 1, as
        // precise as the two field-table implementations agree. The sheet-only maps of the same
        // two solves differ by 2.4e-4, 1.3e-3, 1.9e-4 at these points; with the probe's currents
        // 5.9e-4, 4.2e-3, 1.6e-4 — near the probe the vertex charge's potential cancels most of
        // the tube's top-charge field, which magnifies the tables' difference about threefold.
        var surface = Plate(MeshEdge);
        var probe = Probe();
        var slab = new LayeredKernelTable(Substrate, Frequency, rhoMax: 0.025);
        var stack = new MultiLayerKernelTable(OneLayer, Frequency, 0.025);
        var points = new[]
        {
            new Vector3D(probe.X + 1.2e-3, probe.Y + 0.7e-3, Thickness + 2.0e-3),
            new Vector3D(probe.X - 2.1e-3, probe.Y + 0.4e-3, Thickness + 0.6e-3),
            new Vector3D(probe.X + 1.3e-3, probe.Y - 0.6e-3, 0.5 * Thickness),
        };
        var slabSolution = new SurfaceMomSolver().SolveProbeFed(surface, slab, probe);
        var stackSolution = new SurfaceMomSolver().SolveProbeFed(surface, stack, probe);
        _out.WriteLine($"Zin {slabSolution.Surface.InputImpedance} vs {stackSolution.Surface.InputImpedance}");
        var one = LayeredFieldEvaluator.EvaluateProbeFed(surface, slab, slabSolution, probe, points);
        var many = LayeredFieldEvaluator.EvaluateProbeFed(surface, stack, stackSolution, probe, points);
        var oneSheet = LayeredFieldEvaluator.Evaluate(surface, slab, slabSolution.Surface, points);
        var manySheet = LayeredFieldEvaluator.Evaluate(surface, stack, stackSolution.Surface, points);
        for (int i = 0; i < points.Length; i++)
        {
            double worst = Worst((many.E[i], many.H![i]), (one.E[i], one.H![i]));
            double sheet = Worst((manySheet.E[i], manySheet.H![i]), (oneSheet.E[i], oneSheet.H![i]));
            _out.WriteLine($"point {i}: {worst:e2} (sheet-only maps of the same two solves: {sheet:e2})");
            Assert.InRange(worst, 0, Math.Max(1e-3, 5 * sheet));
        }
    }
}
