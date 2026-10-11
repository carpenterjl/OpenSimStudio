using System.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-37: the power ledger of a patch over a lossy ground. On a lossless dielectric every watt the
/// feed accepts either radiates or heats the ground — the surface wave too, which the ground
/// absorbs as it goes — so P_rad + P_ground = P_in, with P_ground integrated over the whole plane
/// (<see cref="GroundPlaneLoss"/>) and P_in from the solve's port.
/// </summary>
public class GroundLossLedgerTests
{
    private const double PatchW = 1.186e-2, PatchL = 0.906e-2, Edge = 1.4e-3, SlabH = 1.588e-3;
    private const double RhoMax = 0.03, Frequency = 10e9;

    private readonly ITestOutputHelper _out;
    public GroundLossLedgerTests(ITestOutputHelper output) => _out = output;

    private static (SurfaceStructure Structure, SurfacePort Port) Plate()
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(PatchW, PatchL, Edge, z: SlabH, portFraction: 0);
        return (grid.Structure!, grid.Port!);
    }

    [Fact]
    public void DividedDifferencesOfTheExponential_AreExact()
    {
        Span<Complex> d = stackalloc Complex[4];
        // Confluent at zero: e[0,0,0] = 1/2!, e[0,0,0,0] = 1/3!.
        GroundPlaneLoss.ExpDividedDifferences(0, 0, 0, d);
        Assert.Equal(0.5, d[0].Real, 15);
        for (int i = 1; i < 4; i++) Assert.Equal(1.0 / 6, d[i].Real, 15);
        // Well separated: the textbook formula Σ e^{x_i}/Π(x_i − x_j).
        Complex x1 = new(0, 3.1), x2 = new(0, -7.4), x3 = new(0, 12.2);
        GroundPlaneLoss.ExpDividedDifferences(x1, x2, x3, d);
        Complex Dd3(Complex a, Complex b, Complex c) =>
            Complex.Exp(a) / ((a - b) * (a - c)) + Complex.Exp(b) / ((b - a) * (b - c)) + Complex.Exp(c) / ((c - a) * (c - b));
        Assert.True((d[0] - Dd3(x1, x2, x3)).Magnitude < 1e-13);
        // e[x1,x2,x3,x1] = (e[x1,x2,x3] − e[x2,x3,x1])/… is confluent; check against a difference quotient.
        double h = 1e-5;
        Complex numeric = (Dd3(x1 + new Complex(0, h), x2, x3) - Dd3(x1 - new Complex(0, h), x2, x3)) / new Complex(0, 2 * h);
        Assert.True((d[1] - numeric).Magnitude < 1e-8 * numeric.Magnitude);

        // The explicit form (separated points) and the series (small points) agree with the matrix
        // exponential, which holds everywhere, on both sides of their thresholds.
        Span<Complex> m = stackalloc Complex[4];
        var cases = new (double, double, double)[]
        {
            (0.1, -0.2, 0.05), (1.4, -1.45, 0.0), (0.3, 0.0, -0.3), (0.26, 0.0, -0.26),
            (5.0, 5.1, -4.0), (12.0, -3.0, 0.4), (-8.0, 7.9, 0.0), (0.0, 0.0, 9.0),
        };
        foreach (var (p, q, r) in cases)
        {
            Complex a = new(0, p), b = new(0, q), c = new(0, r);
            GroundPlaneLoss.ExpDividedDifferences(a, b, c, d);
            GroundPlaneLoss.ExpDividedDifferencesByMatrix(a, b, c, m);
            for (int i = 0; i < 4; i++)
                Assert.True((d[i] - m[i]).Magnitude <= 1e-12 * Math.Max(m[i].Magnitude, 1e-3),
                    $"({p}, {q}, {r}) entry {i}: {d[i]} vs {m[i]}");
        }
    }

    [Fact]
    public void TheLedger_NamesTheGroundsShare()
    {
        // Integrated: the surface wave is the ground's, not a term of its own.
        string integrated = PowerLedger.Describe(1.0, 0.8, 0.15, 0, 6.0, 0, lossyGround: true, groundWatts: 0.2);
        Assert.Contains("ground-plane loss 20.0", integrated);
        Assert.Contains("the ledger closes", integrated);
        Assert.DoesNotContain("P_sw =", integrated);
        // Not integrated (a probe's tube): told by difference, and said to be.
        string byDifference = PowerLedger.Describe(1.0, 0.8, 0.15, 0, 6.0, 0, lossyGround: true);
        Assert.Contains("the remaining 20.0", byDifference);
        Assert.Contains("ground-plane loss", byDifference);
        Assert.Contains("by difference", byDifference);
        // With a lossy dielectric as well, the difference is both.
        Assert.Contains("ground-plane and dielectric loss", PowerLedger.Describe(1.0, 0.7, 0, 0.02, 6.0, 0, lossyGround: true));
    }

    [Fact]
    public void APatchOverALossyGround_RadiatesOrHeatsTheGround()
    {
        var (structure, port) = Plate();
        var layers = new[] { new LayeredStackup.Layer(2.2, 0, SlabH) };
        double Ledger(LayeredStackup stack, out double groundShare)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var table = new MultiLayerKernelTable(stack, Frequency, RhoMax);
            double tTable = clock.Elapsed.TotalSeconds;
            var solution = new SurfaceMomSolver().Solve(structure, table, port);
            double tSolve = clock.Elapsed.TotalSeconds;
            double pin = 0.5 * (Complex.One / solution.InputImpedance).Real;
            double rad = LayeredFarField.Compute(structure, table, solution, 48, 96).TotalRadiatedPowerWatts;
            double tFar = clock.Elapsed.TotalSeconds;
            double ground = GroundPlaneLoss.Watts(structure, table, solution)!.Value;
            _out.WriteLine($"table {tTable:f1} s, solve {tSolve - tTable:f1} s, far field {tFar - tSolve:f1} s, ground {clock.Elapsed.TotalSeconds - tFar:f1} s");
            double sw = stack.HasLossyGround ? 0 : LayeredFarField.SurfaceWavePowerWatts(structure, table, solution);
            groundShare = ground / pin;
            _out.WriteLine($"{(stack.HasLossyGround ? $"σ = {stack.GroundConductivitySiemensPerMeter:g3} S/m" : "PEC")}: " +
                           $"Zin {solution.InputImpedance:f3}, P_rad {rad / pin:f5}, P_sw {sw / pin:f5}, P_ground {ground / pin:f5} of P_in; " +
                           $"ledger {(rad + sw + ground) / pin:f7}");
            return (rad + sw + ground) / pin;
        }
        double pec = Ledger(new LayeredStackup(layers), out _);
        double lossy = Ledger(new LayeredStackup(layers) { GroundConductivitySiemensPerMeter = 1e5 }, out double share);
        // Measured: both ledgers close to 1e-7 on this mesh (the solve's port power and the
        // spectral powers come from one Green's function); the ground takes 19.4 % — the PEC
        // ground's 15.7 % surface wave, which it now absorbs, and its own near-field heat.
        Assert.True(share > 0.05, "the ground must take a share worth checking");
        Assert.InRange(pec, 1 - 1e-5, 1 + 1e-5);
        Assert.InRange(lossy, 1 - 1e-5, 1 + 1e-5);
    }
}
