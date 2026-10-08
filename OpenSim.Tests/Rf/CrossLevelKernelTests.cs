using System.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-34: the kernels between metal on two interfaces of one grounded stackup. Reciprocity is the
/// independent gate: the table from level 1 to level 2 and the one from 2 to 1 solve different
/// boundary-value problems (the source jump sits on different interfaces, the read-out in
/// different layers), and must agree. An all-air stack, where the two images are the whole
/// answer, pins the normalization; a direct integration pins the spline.
/// </summary>
public class CrossLevelKernelTests
{
    private const double C0 = 299792458.0;
    private readonly ITestOutputHelper _output;
    public CrossLevelKernelTests(ITestOutputHelper output) => _output = output;

    private static LayeredStackup Board() => new(new[]
    {
        new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
        new LayeredStackup.Layer(3.5, 0.0, 0.2e-3),
        new LayeredStackup.Layer(4.4, 0.02, 0.5e-3)
    });

    [Fact]
    public void TheKernels_AreReciprocal()
    {
        var stackup = Board();
        double f = 5e9;
        var up = new CrossLevelKernelTable(stackup, f, rhoMax: 0.05, sourceInterface: 0, observationInterface: 2);
        var down = new CrossLevelKernelTable(stackup, f, rhoMax: 0.05, sourceInterface: 2, observationInterface: 0);
        foreach (double rho in new[] { 1e-4, 1e-3, 5e-3, 2e-2, 4.5e-2 })
        {
            var (a1, p1) = up.EvaluateKernels(rho);
            var (a2, p2) = down.EvaluateKernels(rho);
            _output.WriteLine($"ρ = {rho * 1e3:g3} mm: G_A {a1.Magnitude:e4} / {a2.Magnitude:e4}, K_Φ {p1.Magnitude:e4} / {p2.Magnitude:e4}");
            Assert.True((a1 - a2).Magnitude < 1e-5 * a1.Magnitude, $"G_A at {rho}");
            Assert.True((p1 - p2).Magnitude < 1e-5 * p1.Magnitude, $"K_Φ at {rho}");
        }
    }

    [Fact]
    public void InAir_TheTwoImagesAreTheWholeKernel()
    {
        // Air over a ground: G_A = µ₀[g(R₀) − g(R₁)] and K_Φ = [g(R₀) − g(R₁)]/ε₀ exactly, so the
        // tabulated smooth part must vanish.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(1, 0, 1e-3), new LayeredStackup.Layer(1, 0, 0.6e-3)
        });
        var table = new CrossLevelKernelTable(stackup, 3e9, rhoMax: 0.05, sourceInterface: 0, observationInterface: 1);
        foreach (double rho in new[] { 1e-4, 2e-3, 3e-2 })
        {
            var (ga, kPhi) = table.EvaluateKernels(rho);
            var (sa, sp) = table.EvaluateSmooth(rho);
            Assert.True(sa.Magnitude < 1e-8 * ga.Magnitude, $"G_A smooth {sa.Magnitude:e2} of {ga.Magnitude:e2}");
            Assert.True(sp.Magnitude < 1e-8 * kPhi.Magnitude, $"K_Φ smooth {sp.Magnitude:e2} of {kPhi.Magnitude:e2}");
        }
    }

    [Fact]
    public void TheTable_IsTheDirectIntegral()
    {
        var table = new CrossLevelKernelTable(Board(), 5e9, rhoMax: 0.05, sourceInterface: 0, observationInterface: 2);
        foreach (double rho in new[] { 3e-4, 1.7e-3, 7e-3, 3.3e-2 })
        {
            var (a, p) = table.EvaluateKernels(rho);
            var (da, dp) = table.EvaluateKernelsDirect(rho, refinement: 2);
            Assert.True((a - da).Magnitude < 1e-5 * da.Magnitude, $"G_A at {rho}: {a} against {da}");
            Assert.True((p - dp).Magnitude < 1e-5 * dp.Magnitude, $"K_Φ at {rho}: {p} against {dp}");
        }
    }
}
