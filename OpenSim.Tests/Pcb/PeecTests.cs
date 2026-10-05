using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The filament solve against closed forms: a bar at DC (the uniform-current kernel it is
/// built from), a round wire against the Bessel solution of the skin effect, two wide plates
/// close together against the one-sided plate, and a trace over a meshed plane against the
/// 2D line it tends to.
/// </summary>
public class PeecTests
{
    private const double Mu0 = 4e-7 * Math.PI;
    private const double Sigma = 5.8e7;
    private readonly ITestOutputHelper _output;
    public PeecTests(ITestOutputHelper output) => _output = output;

    private static Complex BesselJ0(Complex z)
    {
        Complex term = 1, sum = 1, q = -z * z / 4;
        for (int k = 1; k < 200; k++) { term *= q / (k * (double)k); sum += term; if (term.Magnitude < 1e-18 * sum.Magnitude) break; }
        return sum;
    }

    private static Complex BesselJ1(Complex z)
    {
        Complex term = 1, sum = 1, q = -z * z / 4;
        for (int k = 1; k < 200; k++) { term *= q / (k * (double)(k + 1)); sum += term; if (term.Magnitude < 1e-18 * sum.Magnitude) break; }
        return z / 2 * sum;
    }

    [Fact]
    public void ASubdividedBarAtLowFrequency_IsTheWholeBar()
    {
        // At DC the filaments carry the same current density, and the kernel between them
        // is the uniform-current average: the bundle is the undivided bar, to rounding.
        double l = 10e-3, w = 1e-3, t = 35e-6;
        var model = new PeecModel();
        int a = model.AddNode(new Vector3D(0, 0, 0)), b = model.AddNode(new Vector3D(l, 0, 0));
        model.AddBar(a, b, w, t, Sigma, acrossWidth: 5, acrossThickness: 3);
        model.AddPort("bar", a, b);
        Assert.Equal(15, model.FilamentCount);
        var point = model.Solve(new[] { 1.0 })[0];
        Assert.Equal(l / (Sigma * w * t), point.Resistance(0, 0), 1e-9 * l / (Sigma * w * t));
        double whole = PartialInductance.SelfInductance(l, w, t);
        _output.WriteLine($"L at 1 Hz {point.Inductance(0, 0) * 1e9:f5} nH, undivided bar {whole * 1e9:f5} nH");
        Assert.Equal(whole, point.Inductance(0, 0), 1e-6 * whole);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void ARoundWire_FollowsTheBesselSolution(double radiusOverSkinDepth)
    {
        // Z_int/R_dc = (ka/2)·J0(ka)/J1(ka) with k = (1 − j)/δ.
        double radius = 0.5e-3, length = 50e-3;
        double delta = radius / radiusOverSkinDepth;
        double f = 1 / (Math.PI * Mu0 * Sigma * delta * delta);
        var model = new PeecModel();
        int a = model.AddNode(new Vector3D(0, 0, 0)), b = model.AddNode(new Vector3D(length, 0, 0));
        model.AddRoundWire(a, b, radius, Sigma, cellsAcross: 21);
        model.AddPort("wire", a, b);
        var points = model.Solve(new[] { 1.0, f });
        double rdc = length / (Sigma * Math.PI * radius * radius);
        Assert.Equal(rdc, points[0].Resistance(0, 0), 1e-9 * rdc);

        Complex ka = new Complex(1, -1) / delta * radius;
        Complex exact = ka / 2 * BesselJ0(ka) / BesselJ1(ka);
        double resistanceRatio = points[1].Resistance(0, 0) / rdc;
        // Internal inductance: what the inductance has lost from DC is internal flux, and
        // the DC internal inductance of a round wire is µ₀/8π per length.
        double internalDc = Mu0 / (8 * Math.PI) * length;
        double internalNow = internalDc - (points[0].Inductance(0, 0) - points[1].Inductance(0, 0));
        double exactInternal = exact.Imaginary * rdc / (2 * Math.PI * f);
        _output.WriteLine($"a/δ = {radiusOverSkinDepth}: R/Rdc {resistanceRatio:f4} (Bessel {exact.Real:f4}, {resistanceRatio / exact.Real - 1:p2}); " +
                          $"L_int {internalNow * 1e9:f4} nH (Bessel {exactInternal * 1e9:f4} nH); {model.FilamentCount} filaments");
        Assert.Equal(exact.Real, resistanceRatio, 0.015 * exact.Real);
        Assert.Equal(exactInternal, internalNow, 0.03 * internalDc);

        // The DC inductance is the uniform-current round wire's.
        double gmd = PartialInductance.RoundWireSelfInductance(length, radius);
        Assert.Equal(gmd, points[0].Inductance(0, 0), 0.002 * gmd);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public void TwoWidePlatesCloseTogether_CarryTheirCurrentOnTheFacingSides(double thicknessOverSkinDepth)
    {
        // Out on one plate and back on the other, the plates much wider than their spacing:
        // the field is confined between them and each plate is a one-sided sheet,
        // Z = (k/σw)·coth(kt) per length with k = (1 + j)/δ.
        // Thin against their width as well: the current that wraps round a thick edge onto
        // the outer face is not in the plate formula (with 0.2 mm plates 6 mm wide the solve
        // came out 7 % under it at t/δ = 3).
        double w = 8e-3, t = 0.05e-3, gap = 0.02e-3, l = 20e-3;
        double delta = t / thicknessOverSkinDepth;
        double f = 1 / (Math.PI * Mu0 * Sigma * delta * delta);
        var model = new PeecModel();
        double z1 = (gap + t) / 2;
        int a0 = model.AddNode(new Vector3D(0, 0, z1)), a1 = model.AddNode(new Vector3D(l, 0, z1));
        int b0 = model.AddNode(new Vector3D(0, 0, -z1)), b1 = model.AddNode(new Vector3D(l, 0, -z1));
        model.AddBar(a0, a1, w, t, Sigma, acrossWidth: 9, acrossThickness: 9);
        model.AddBar(b0, b1, w, t, Sigma, acrossWidth: 9, acrossThickness: 9);
        model.AddShort(a1, b1);
        model.AddPort("loop", a0, b0);
        var points = model.Solve(new[] { 1.0, f });
        double rdc = 2 * l / (Sigma * w * t);
        Assert.Equal(rdc, points[0].Resistance(0, 0), 1e-9 * rdc);

        Complex kt = new Complex(1, 1) / delta * t;
        Complex sheet = kt / Complex.Tanh(kt);                    // per plate, over its DC resistance
        double ratio = points[1].Resistance(0, 0) / rdc;
        // Loop inductance: the gap's flux plus each plate's internal part.
        double external = Mu0 * gap * l / w;
        double expectedL = external + 2 * sheet.Imaginary * (l / (Sigma * w * t)) / (2 * Math.PI * f);
        _output.WriteLine($"t/δ = {thicknessOverSkinDepth}: R/Rdc {ratio:f4} (one-sided plate {sheet.Real:f4}, {ratio / sheet.Real - 1:p2}); " +
                          $"L {points[1].Inductance(0, 0) * 1e12:f1} pH (plate formula without fringing {expectedL * 1e12:f1} pH)");
        Assert.Equal(sheet.Real, ratio, 0.03 * sheet.Real);
        Assert.Equal(expectedL, points[1].Inductance(0, 0), 0.05 * expectedL);
    }

    [Fact]
    public void ATraceOverAMeshedPlane_TendsToTheLineInductanceAsTheCurrentGathersUnderIt()
    {
        // A trace 0.3 mm over a 12 × 5 mm plane, joined to it at the far end; the loop is
        // seen at the near end. At high frequency the plane's return gathers under the trace
        // and the loop tends to the microstrip's L′·length; at low frequency it spreads over
        // the whole plane and the loop is larger.
        double w = 0.5e-3, t = 35e-6, h = 0.3e-3, l = 10e-3, planeT = 35e-6;
        var model = new PeecModel();
        var plane = model.AddPlane(new[] { new Polygon2(new[]
            { new Point2(-1e-3, -2.5e-3), new Point2(11e-3, -2.5e-3), new Point2(11e-3, 2.5e-3), new Point2(-1e-3, 2.5e-3) }) },
            z: -planeT / 2, planeT, pitch: 0.25e-3, Sigma);
        double zt = h + t / 2;
        int start = model.AddNode(new Vector3D(0.125e-3, 0.125e-3, zt)), end = model.AddNode(new Vector3D(l + 0.125e-3, 0.125e-3, zt));
        model.AddBar(start, end, w, t, Sigma, acrossWidth: 5);
        model.AddShort(end, plane.NodeNear(new Point2(l + 0.125e-3, 0.125e-3)));
        model.AddPort("loop", start, plane.NodeNear(new Point2(0.125e-3, 0.125e-3)));
        _output.WriteLine($"{model.FilamentCount} filaments, {model.NodeCount} nodes");

        var points = model.Solve(new[] { 100.0, 300e6 });
        double low = points[0].Inductance(0, 0), high = points[1].Inductance(0, 0);

        // The line: a strip w wide, h above an infinite plane, in air. L′ = µ₀ε₀/C′_air.
        var section = new CoupledLineCrossSection(new LayeredStackup(new[] { new LayeredStackup.Layer(1.0, 0, h) }), 0,
            new[] { new TraceCrossSection(0, w, t, Sigma) });
        double perLength = RlgcExtractor.Extract(section).InductanceHenriesPerMeter[0, 0];
        _output.WriteLine($"loop inductance: {low * 1e9:f3} nH at 100 Hz, {high * 1e9:f3} nH at 300 MHz; " +
                          $"line L′·length {perLength * l * 1e9:f3} nH ({high / (perLength * l) - 1:p1})");
        Assert.True(low > 1.1 * high, "the return should spread at low frequency and raise the loop inductance");
        Assert.Equal(perLength * l, high, 0.05 * perLength * l);
    }

    [Fact]
    public void TwoPorts_AreReciprocal_AndTheirResistanceAndInductanceArePositive()
    {
        // Two parallel traces, each its own port.
        var model = new PeecModel();
        for (int k = 0; k < 2; k++)
        {
            int a = model.AddNode(new Vector3D(0, k * 0.6e-3, 0)), b = model.AddNode(new Vector3D(15e-3, k * 0.6e-3, 0));
            model.AddBar(a, b, 0.3e-3, 35e-6, Sigma, acrossWidth: 5, acrossThickness: 3);
            model.AddPort($"trace {k + 1}", a, b);
        }
        var point = model.Solve(new[] { 50e6 }, keepCurrents: true)[0];
        var z = point.Impedance;
        Assert.True((z[0, 1] - z[1, 0]).Magnitude <= 1e-12 * z[0, 1].Magnitude);
        Assert.True(point.Resistance(0, 0) > 0 && point.Inductance(0, 0) > 0);
        // Passive: the resistance and the inductance matrices are positive definite.
        Assert.True(point.Resistance(0, 0) * point.Resistance(1, 1) > point.Resistance(0, 1) * point.Resistance(0, 1));
        Assert.True(point.Inductance(0, 0) * point.Inductance(1, 1) > point.Inductance(0, 1) * point.Inductance(0, 1));
        // The neighbour's eddy currents make a shared resistance: the proximity effect.
        Assert.True(Math.Abs(point.Resistance(0, 1)) > 0);
        // One ampere through port 1 is one ampere in trace 1's filaments and none net in trace 2's.
        Complex first = Complex.Zero, second = Complex.Zero;
        for (int i = 0; i < 15; i++) { first += point.FilamentCurrents![0][i]; second += point.FilamentCurrents[0][15 + i]; }
        Assert.True((first - 1).Magnitude < 1e-9 && second.Magnitude < 1e-9);
    }

    [Fact]
    public void APortBetweenUnconnectedConductors_IsRefused()
    {
        var model = new PeecModel();
        int a = model.AddNode(new Vector3D(0, 0, 0)), b = model.AddNode(new Vector3D(1e-3, 0, 0));
        int c = model.AddNode(new Vector3D(0, 1e-3, 0)), d = model.AddNode(new Vector3D(1e-3, 1e-3, 0));
        model.AddBar(a, b, 0.2e-3, 35e-6, Sigma);
        model.AddBar(c, d, 0.2e-3, 35e-6, Sigma);
        model.AddPort("open", a, c);
        var ex = Assert.Throws<InvalidOperationException>(() => model.Solve(new[] { 1e6 }));
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public void Grading_IsSymmetricThinAtTheFaces_AndSumsToOne()
    {
        var g = PeecModel.Graded(5);
        Assert.Equal(1.0, g.Sum(), 1e-12);
        Assert.Equal(g[0], g[4], 1e-15);
        Assert.Equal(2 * g[0], g[1], 1e-15);
        Assert.Equal(4 * g[0], g[2], 1e-15);
        // 35 µm copper at 1 GHz (δ = 2.1 µm) needs the outer strip under 0.7 δ.
        double delta = PeecModel.SkinDepth(1e9, Sigma);
        int n = PeecModel.StripsFor(35e-6, delta);
        Assert.True(PeecModel.Graded(n)[0] * 35e-6 <= 0.7 * delta);
        Assert.Equal(1, PeecModel.StripsFor(35e-6, PeecModel.SkinDepth(1e3, Sigma)));
    }
}
