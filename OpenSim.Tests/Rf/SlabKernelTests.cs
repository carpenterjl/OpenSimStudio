using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-36: a dielectric stack with air on both sides — a board as an infinite slab, its copper all
/// meshed. Gated by what it must reduce to and by energy: evanescent spectral values equal those of
/// the shipped grounded stack with a ground pushed far down through air (where the ground's echo
/// has died out); all-air is free space, kernel and solve; and a printed dipole on a lossless slab
/// puts its input power into radiation (both half-spaces) and the slab's own modes.
/// </summary>
public class SlabKernelTests
{
    private const double C0 = 299792458.0;
    private readonly ITestOutputHelper _output;
    public SlabKernelTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvanescentValues_AreTheGroundedStackWithItsGroundFarAway(bool metalUnderneath)
    {
        // 1.6 mm of εr 4.4 at 3 GHz. A ground 40 free-space wavelengths below, through air, returns
        // e^{−2γ₀h} of the field at k_ρ ≥ 1.2k₀ — below 10⁻³⁰⁰. Metal on the top face (interface 0
        // of the slab), or on the bottom face (the top of a thin air layer under it).
        double f = 3e9, k0 = 2 * Math.PI * f / C0, lambda = C0 / f;
        var board = new LayeredStackup.Layer(4.4, 0, 1.6e-3);
        var spacer = new LayeredStackup.Layer(1, 0, 0.2e-3);
        var deep = new LayeredStackup.Layer(1, 0, 40 * lambda);
        var slab = metalUnderneath ? new LayeredStackup(new[] { spacer, board }) : new LayeredStackup(new[] { board });
        var grounded = metalUnderneath ? new LayeredStackup(new[] { deep, spacer, board }) : new LayeredStackup(new[] { deep, board });
        int mSlab = 0, mGrounded = metalUnderneath ? 1 : 1;
        foreach (Complex kRho in new Complex[] { 1.2 * k0, 2.5 * k0, 30 * k0, new(1.5 * k0, -0.3 * k0) })
        {
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            var (a1, p1) = TransmissionLineGreens.EvaluateUngrounded(slab, k0, kRho, kz0, mSlab);
            var (a2, p2) = TransmissionLineGreens.EvaluateInterior(grounded, k0, kRho, kz0, mGrounded);
            _output.WriteLine($"k_ρ = {kRho / k0} k₀: G̃_A {a1} / {a2}; K̃_Φ {p1} / {p2}");
            Assert.True((a1 - a2).Magnitude < 1e-9 * a2.Magnitude, $"G̃_A at {kRho}");
            Assert.True((p1 - p2).Magnitude < 1e-9 * p2.Magnitude, $"K̃_Φ at {kRho}");
        }
    }

    [Fact]
    public void AllAir_IsFreeSpace()
    {
        var air = new LayeredStackup(new[] { new LayeredStackup.Layer(1, 0, 1e-3) });
        var table = new MultiLayerKernelTable(air, 3e9, rhoMax: 0.1, ungrounded: true);
        Assert.Empty(table.Poles);
        double k0 = table.K0;
        foreach (double rho in new[] { 1e-4, 3e-3, 4e-2 })
        {
            var (ga, kPhi) = table.EvaluateKernels(rho);
            var g = Complex.Exp(new Complex(0, -k0 * rho)) / (4 * Math.PI * rho);
            Assert.True((ga - RfConstants.Mu0 * g).Magnitude < 1e-8 * (RfConstants.Mu0 * g).Magnitude, $"G_A at {rho}");
            Assert.True((kPhi - g / RfConstants.Eps0).Magnitude < 1e-8 * (g / RfConstants.Eps0).Magnitude, $"K_Φ at {rho}");
        }
    }

    [Fact]
    public void TheSlabModes_AreRootsAndIncludeTE0AndTM0()
    {
        // A thin slab carries TE0 and TM0 at any frequency, both just above k₀.
        double f = 2.4e9, k0 = 2 * Math.PI * f / C0;
        var slab = new LayeredStackup(new[] { new LayeredStackup.Layer(4.4, 0, 1.6e-3) });
        var poles = SurfaceWavePoles.FindUngrounded(slab, k0, 0);
        _output.WriteLine(string.Join(", ", poles.Select(p => $"{(p.IsTm ? "TM" : "TE")} k_ρ/k₀ − 1 = {p.KRho.Real / k0 - 1:e3}")));
        Assert.Contains(poles, p => p.IsTm);
        Assert.Contains(poles, p => !p.IsTm);
        foreach (var pole in poles)
        {
            Assert.InRange(pole.KRho.Real, k0, k0 * Math.Sqrt(4.4));
            double scale = Math.Abs(SurfaceWaveDispersion.UngroundedResidual(slab, k0, k0 * 1.5, pole.IsTm));
            Assert.True(Math.Abs(SurfaceWaveDispersion.UngroundedResidual(slab, k0, pole.KRho.Real, pole.IsTm)) < 1e-9 * scale);
        }
    }

    private static (SurfaceStructure Structure, SurfacePort Port) Dipole(double length, double width, int along, int across)
    {
        var vertices = new List<Vector3D>();
        for (int i = 0; i <= along; i++)
            for (int j = 0; j <= across; j++)
                vertices.Add(new Vector3D(-length / 2 + length * i / along, -width / 2 + width * j / across, 0));
        int At(int i, int j) => i * (across + 1) + j;
        var triangles = new List<(int, int, int)>();
        for (int i = 0; i < along; i++)
            for (int j = 0; j < across; j++)
            {
                triangles.Add((At(i, j), At(i + 1, j), At(i, j + 1)));
                triangles.Add((At(i + 1, j), At(i + 1, j + 1), At(i, j + 1)));
            }
        var s = new SurfaceStructure(vertices, triangles, null);
        var port = new SurfacePort(Enumerable.Range(0, s.BasisCount).Where(e => s.Edges[e].MinusTriangle >= 0
            && Math.Abs(vertices[s.Edges[e].V1].X) < 1e-12 && Math.Abs(vertices[s.Edges[e].V2].X) < 1e-12).ToList(),
            new Vector3D(1, 0, 0));
        return (s, port);
    }

    [Fact]
    public void InAir_TheSlabSolveIsTheFreeSpaceSolve()
    {
        // The same printed dipole solved over an all-air "slab" and in free space.
        var (s, port) = Dipole(50e-3, 2e-3, 26, 2);
        double f = 2.8e9;
        var air = new LayeredStackup(new[] { new LayeredStackup.Layer(1, 0, 1.6e-3) });
        var table = new MultiLayerKernelTable(air, f, rhoMax: 0.06, ungrounded: true);
        var slab = new SurfaceMomSolver().Solve(s, table, port);
        var free = new SurfaceMomSolver().Solve(s, f, port);
        var pattern = SlabFarField.Compute(s, table, slab);
        var freePattern = SurfaceFarFieldEvaluator.Compute(s, free);
        _output.WriteLine($"Zin {slab.InputImpedance:f3} over the air slab, {free.InputImpedance:f3} in free space; " +
                          $"P_rad {pattern.TotalRadiatedPowerWatts:e5} / {freePattern.TotalRadiatedPowerWatts:e5}");
        Assert.True((slab.InputImpedance - free.InputImpedance).Magnitude < 1e-4 * free.InputImpedance.Magnitude);
        Assert.Equal(freePattern.TotalRadiatedPowerWatts, pattern.TotalRadiatedPowerWatts, 1e-3 * freePattern.TotalRadiatedPowerWatts);
    }

    [Fact]
    public void APrintedMonopoleOnFR4_ResonatesBelowItsAirResonance()
    {
        // FU-36's symptom: a 30 mm printed monopole beside a 40 × 30 mm pour, solved in air,
        // resonates too high. On the board (1.6 mm of εr 4.4, lossless) its series resonance — the
        // zero of Im Zin — must come down, and by less than the full √εr (the field is partly in air).
        // At εr = 1 the board solve is the air solve; on the lossless board the ledger closes.
        var trace = new Polygon2(new[] { new Point2(-0.0005, 0.001), new Point2(0.0005, 0.001), new Point2(0.0005, 0.031), new Point2(-0.0005, 0.031) });
        var pour = new Polygon2(new[] { new Point2(-0.02, -0.03), new Point2(0.02, -0.03), new Point2(0.02, 0), new Point2(-0.02, 0) });
        var model = PrintedAntennaBuilder.Build(trace, new[] { pour }, new Point2(0, 0.0005), 1e-3, 3e-3);
        var fr4 = new BoardSlab(4.4, 0, 1.6e-3);
        Complex Air(double f) => new SurfaceMomSolver().Solve(model.Structure, f, model.Port).InputImpedance;
        Complex Board(double f) => PrintedAntennaBuilder.SolveOnBoard(model, fr4, f).Solution.InputImpedance;
        double Resonance(Func<double, Complex> z, double lo, double hi)
        {
            double xLo = z(lo).Imaginary;
            for (int i = 0; i < 30; i++)
            {
                double mid = 0.5 * (lo + hi), x = z(mid).Imaginary;
                if (Math.Sign(x) == Math.Sign(xLo)) { lo = mid; xLo = x; } else hi = mid;
            }
            return 0.5 * (lo + hi);
        }
        double inAir = Resonance(Air, 1.2e9, 3.2e9), onBoard = Resonance(Board, 0.8e9, 3.2e9);
        _output.WriteLine($"series resonance {inAir / 1e9:f4} GHz in air, {onBoard / 1e9:f4} GHz on FR-4 (ratio {inAir / onBoard:f3}; √εr = {Math.Sqrt(4.4):f3})");
        Assert.InRange(inAir / onBoard, 1.05, Math.Sqrt(4.4));

        var air = PrintedAntennaBuilder.SolveOnBoard(model, new BoardSlab(1, 0, 1.6e-3), 2e9).Solution.InputImpedance;
        Assert.True((air - Air(2e9)).Magnitude < 1e-4 * air.Magnitude);
        var (solution, table) = PrintedAntennaBuilder.SolveOnBoard(model, fr4, onBoard);
        double input = 0.5 * (Complex.One / solution.InputImpedance).Real;
        double ledger = (SlabFarField.Compute(model.Structure, table, solution).TotalRadiatedPowerWatts
            + SlabFarField.SurfaceWavePowerWatts(model.Structure, table, solution)) / input;
        _output.WriteLine($"ledger at the board resonance {ledger:f5}");
        Assert.Equal(1, ledger, 0.002);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APrintedDipoleOnALosslessSlab_ClosesItsPowerLedger(bool metalUnderneath)
    {
        // A 50 × 2 mm dipole on 1.6 mm of lossless εr 4.4, on the top face or the bottom one, near
        // its first resonance: ½Re(V·I*) = P_rad (both half-spaces) + P_sw.
        double f = 2.0e9;
        var board = new LayeredStackup.Layer(4.4, 0, 1.6e-3);
        var stackup = metalUnderneath
            ? new LayeredStackup(new[] { new LayeredStackup.Layer(1, 0, 0.2e-3), board })
            : new LayeredStackup(new[] { board });
        var table = new MultiLayerKernelTable(stackup, f, rhoMax: 0.06, sourceInterface: 0, ungrounded: true);
        var (s, port) = Dipole(50e-3, 2e-3, 26, 2);
        var solution = new SurfaceMomSolver().Solve(s, table, port);
        double input = 0.5 * (Complex.One / solution.InputImpedance).Real;
        var pattern = SlabFarField.Compute(s, table, solution);
        double surfaceWave = SlabFarField.SurfaceWavePowerWatts(s, table, solution);
        _output.WriteLine($"{(metalUnderneath ? "under" : "on top")}: Zin {solution.InputImpedance:f2}; {table.PoleCount} modes; " +
                          $"radiated {pattern.TotalRadiatedPowerWatts / input:f5}, surface wave {surfaceWave / input:f5}, " +
                          $"ledger {(pattern.TotalRadiatedPowerWatts + surfaceWave) / input:f5}");
        Assert.Equal(1, (pattern.TotalRadiatedPowerWatts + surfaceWave) / input, 0.001);
        // The slab is its own mirror image: copper on the bottom face is copper on the top face
        // seen from below, so the two must give one input impedance — to the tables' own accuracy, as the`r`n            // two stacks differ in layering (an air layer under the bottom face) and so in images and contour.
        if (metalUnderneath)
        {
            var onTop = new SurfaceMomSolver().Solve(s,
                new MultiLayerKernelTable(new LayeredStackup(new[] { board }), f, rhoMax: 0.06, sourceInterface: 0, ungrounded: true), port);
            double mirror = (onTop.InputImpedance - solution.InputImpedance).Magnitude / onTop.InputImpedance.Magnitude;
            _output.WriteLine($"top face {onTop.InputImpedance:f5}, bottom face {solution.InputImpedance:f5}: {mirror:e2} apart");
            Assert.True(mirror < 1e-5, $"{mirror:e2}");   // measured 1.0e-6
        }
    }
}
