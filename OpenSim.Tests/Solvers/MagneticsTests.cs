using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Magnetics;
using OpenSim.Solvers.Magnetics;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>Feature 13: the 2D magnetic solver against closed forms.</summary>
public class MagneticsTests
{
    private readonly ITestOutputHelper _out;
    public MagneticsTests(ITestOutputHelper output) => _out = output;

    private const double Mu0 = MagneticConstants.Mu0;

    /// <summary>Two flat conductors s apart, t thick, W wide, carrying ±I, between symmetry
    /// planes (a parallel-plate line): L' = µ0·(s + 2t/3)/W per metre, exactly. The left wall holds
    /// A = 0 and the right one is a symmetry plane too: with A = 0 on both, the two walls would
    /// enclose zero total flux and the gap's flux would have to return outside the line.</summary>
    private static MagneticModel2D ParallelPlates(double current, MagneticMaterial gap, double s = 2e-3, double t = 0.5e-3, double w = 10e-3)
    {
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Planar,
            DomainX0 = -5e-3, DomainX1 = 5e-3, DomainY0 = 0, DomainY1 = w,
            NaturalSides = DomainSides.Bottom | DomainSides.Top | DomainSides.Right,
            MaxElement = 0.5e-3, MinElement = 0.05e-3
        };
        model.Regions.Add(new MagneticRegion("go", -s / 2 - t, -s / 2, 0, w, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("return", s / 2, s / 2 + t, 0, w, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("gap", -s / 2, s / 2, 0, w, gap));
        model.Coils.Add(new StrandedCoil("line", new[] { ("go", 1), ("return", -1) }, 1, current));
        return model;
    }

    [Fact]
    public void ParallelPlateLine_HasTheExactInductance()
    {
        var solution = new MagneticSolver2D().SolveStatic(ParallelPlates(1, MagneticMaterial.Air));
        double l = solution.Inductance("line").Real;
        double expected = Mu0 * (2e-3 + 2 * 0.5e-3 / 3) / 10e-3;
        double energyL = 2 * solution.Energy();
        _out.WriteLine($"L' {l * 1e9:F6} nH/m, expected {expected * 1e9:F6}, from energy {energyL * 1e9:F6}");
        Assert.True(Math.Abs(l - expected) / expected <= 1e-3, $"L' {l} vs {expected}");
        Assert.True(Math.Abs(energyL - l) / l <= 1e-6, $"energy {energyL} vs flux {l}");
    }

    private static BhCurve SteelCurve() => new(new[]
    {
        (100.0, 0.5), (300.0, 1.0), (1000.0, 1.4), (5000.0, 1.7), (20000.0, 1.9)
    });

    [Fact]
    public void SaturatingCore_CarriesTheCurveFluxDensity()
    {
        // Between the plates H = I/W everywhere (Ampère), so the core's B is the curve's B at that
        // H, whatever the curve; at 30 A over 10 mm, H = 3000 A/m, on the knee.
        var steel = new MagneticMaterial("steel", 1) { Curve = SteelCurve(), Steinmetz = new SteinmetzCoefficients(2.0, 1.4, 2.3) };
        var solution = new MagneticSolver2D().SolveStatic(ParallelPlates(30, steel));
        foreach (var line in solution.Log) _out.WriteLine(line);
        double h = 30 / 10e-3;
        double lo = 0, hi = 3;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (SteelCurve().Evaluate(mid).H < h) lo = mid; else hi = mid;
        }
        double expected = 0.5 * (lo + hi);
        double b = solution.FluxDensityAt(0, 5e-3).By.Magnitude;
        _out.WriteLine($"B in the core {b:F8} T, curve {expected:F8} T");
        Assert.True(Math.Abs(b - expected) / expected <= 1e-6, $"B {b} vs {expected}");
        Assert.DoesNotContain(solution.Log, l => l.StartsWith("WARNING"));

        // Steinmetz on a uniform field is k·f^α·B^β times the core's volume per metre.
        double loss = solution.CoreLoss(100e3);
        double expectedLoss = 2.0 * Math.Pow(100e3, 1.4) * Math.Pow(expected, 2.3) * 2e-3 * 10e-3;
        _out.WriteLine($"core loss {loss:g8} W/m, expected {expectedLoss:g8}");
        Assert.True(Math.Abs(loss - expectedLoss) / expectedLoss <= 1e-5, $"{loss} vs {expectedLoss}");
    }

    /// <summary>A slab conductor 2b thick and W wide carrying I between two return walls a
    /// distance D from its centre: Z' = (k/σ)·coth(kb)/(2W) + jωµ0·(D − b)/(2W), k = (1 + j)/δ.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public void SlabConductor_HasTheSkinEffectImpedance(double bOverDelta)
    {
        const double b = 1e-3, w = 5e-3, d = 3e-3, sigma = 5.8e7;
        double delta = b / bOverDelta;
        double f = 1 / (Math.PI * Mu0 * sigma * delta * delta);
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Planar,
            DomainX0 = -d, DomainX1 = d, DomainY0 = 0, DomainY1 = w,
            NaturalSides = DomainSides.Bottom | DomainSides.Top,
            MaxElement = 0.25e-3, MinElement = delta / 25, Growth = 1.2
        };
        model.Regions.Add(new MagneticRegion("slab", -b, b, 0, w, new MagneticMaterial("copper", 1, sigma)));
        model.Conductors.Add(new SolidConductor("slab", new[] { "slab" }, 1));
        var solution = new MagneticSolver2D().SolveTimeHarmonic(model, f);
        var z = solution.ConductorImpedance("slab");
        var k = new Complex(1, 1) / delta;
        var expected = k / sigma / Complex.Tanh(k * b) / (2 * w) + new Complex(0, 2 * Math.PI * f * Mu0 * (d - b) / (2 * w));
        double rDc = 1 / (sigma * 2 * b * w);
        double loss = solution.OhmicLoss();
        _out.WriteLine($"b/δ {bOverDelta}: Z' {z}, expected {expected}, R/Rdc {z.Real / rDc:F5} vs {expected.Real / rDc:F5}, ½I²R {0.5 * z.Real:g6} vs loss {loss:g6}");
        Assert.True(Math.Abs(z.Real - expected.Real) / expected.Real <= 0.01, $"R {z.Real} vs {expected.Real}");
        Assert.True(Math.Abs(z.Imaginary - expected.Imaginary) / expected.Imaginary <= 0.01, $"X {z.Imaginary} vs {expected.Imaginary}");
        Assert.True(Math.Abs(loss - 0.5 * z.Real) / loss <= 0.01, $"loss {loss} vs ½R {0.5 * z.Real}");
    }

    /// <summary>Maxwell's mutual inductance of two coaxial filament loops.</summary>
    internal static double LoopMutual(double r1, double r2, double d)
    {
        double k2 = 4 * r1 * r2 / ((r1 + r2) * (r1 + r2) + d * d);
        double k = Math.Sqrt(k2);
        // K and E by the arithmetic-geometric mean.
        double a = 1, bb = Math.Sqrt(1 - k2), sum = 0.5 * k2, pow = 0.5;
        for (int n = 0; n < 40 && Math.Abs(a - bb) > 1e-16 * a; n++)
        {
            double c = 0.5 * (a - bb);
            double an = 0.5 * (a + bb);
            bb = Math.Sqrt(a * bb);
            a = an;
            pow *= 2;
            sum += pow * c * c;
        }
        double kk = Math.PI / (2 * a);
        double ee = kk * (1 - sum);
        return Mu0 * Math.Sqrt(r1 * r2) * ((2 / k - k) * kk - 2 / k * ee);
    }

    [Fact]
    public void LoopMutualFormula_HasItsDipoleLimit()
    {
        double r = 0.01, d = 0.4;
        double dipole = Mu0 * Math.PI * Math.Pow(r, 4) / (2 * Math.Pow(r * r + d * d, 1.5));
        Assert.True(Math.Abs(LoopMutual(r, r, d) - dipole) / dipole <= 2e-3);
    }

    [Fact]
    public void CoaxialRings_HaveMaxwellsMutualAndTheThinRingSelfInductance()
    {
        const double r = 10e-3, c = 0.2e-3, sep = 5e-3;
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Axisymmetric,
            DomainX0 = 0, DomainX1 = 0.15, DomainY0 = -0.15, DomainY1 = 0.15,
            MaxElement = 6e-3, MinElement = 0.025e-3, Growth = 1.3
        };
        model.Regions.Add(new MagneticRegion("ring1", r - c / 2, r + c / 2, -c / 2, c / 2, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("ring2", r - c / 2, r + c / 2, sep - c / 2, sep + c / 2, MagneticMaterial.Air));
        model.Coils.Add(new StrandedCoil("ring1", new[] { ("ring1", 1) }, 1, 1));
        model.Coils.Add(new StrandedCoil("ring2", new[] { ("ring2", 1) }, 1, 0));
        var solution = new MagneticSolver2D().SolveStatic(model);
        foreach (var line in solution.Log) _out.WriteLine(line);
        double m = solution.FluxLinkage("ring2").Real;
        double l = solution.FluxLinkage("ring1").Real;
        double mExpected = LoopMutual(r, r, sep);
        double gmd = 0.44705 * c;
        double lExpected = Mu0 * r * (Math.Log(8 * r / gmd) - 2);
        _out.WriteLine($"M {m * 1e9:F5} nH vs {mExpected * 1e9:F5}; L {l * 1e9:F5} nH vs {lExpected * 1e9:F5}");
        Assert.True(Math.Abs(m - mExpected) / mExpected <= 0.01, $"M {m} vs {mExpected}");
        Assert.True(Math.Abs(l - lExpected) / lExpected <= 0.015, $"L {l} vs {lExpected}");
    }

    [Fact]
    public void SolidRing_AtLowFrequency_HasItsDcResistance()
    {
        // A copper ring of square section: R_dc = 2π/(σ·∫dA/r) = 2π/(σ·c·ln(r1/r0)).
        const double r0 = 5e-3, r1 = 6e-3, c = 1e-3, sigma = 5.8e7;
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Axisymmetric,
            DomainX0 = 0, DomainX1 = 0.05, DomainY0 = -0.05, DomainY1 = 0.05,
            MaxElement = 3e-3, MinElement = 0.05e-3
        };
        model.Regions.Add(new MagneticRegion("ring", r0, r1, -c / 2, c / 2, new MagneticMaterial("copper", 1, sigma)));
        model.Conductors.Add(new SolidConductor("ring", new[] { "ring" }, 1));
        var z = new MagneticSolver2D().SolveTimeHarmonic(model, 1.0).ConductorImpedance("ring");
        double expected = 2 * Math.PI / (sigma * c * Math.Log(r1 / r0));
        _out.WriteLine($"R {z.Real:g8} Ω vs {expected:g8}");
        Assert.True(Math.Abs(z.Real - expected) / expected <= 1e-3, $"R {z.Real} vs {expected}");
    }

    private static Polygon2 Annulus(double r0, double r1, int segments = 512)
    {
        IReadOnlyList<Point2> Ring(double r, bool ccw) => Enumerable.Range(0, segments)
            .Select(i => (ccw ? 1 : -1) * 2 * Math.PI * i / segments)
            .Select(a => new Point2(r * Math.Cos(a), r * Math.Sin(a))).ToList();
        return new Polygon2(Ring(r1, true), new[] { Ring(r0, false) });
    }

    [Fact]
    public void PlanarWinding_CutFromTheLayout_IsItsRings()
    {
        // One ring of copper on each of two layers: the cut finds both at their radii and layer
        // heights, and the single-turn inductance of the top ring is the thin-ring value
        // µ0·R·(ln(8R/GMD) − 2) with GMD = 0.2235·(w + t).
        var islands = new[]
        {
            new CopperIsland(0, 1, "L1", Annulus(5e-3, 6e-3)),
            new CopperIsland(1, 2, "L2", Annulus(5e-3, 6e-3)),
        };
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(), Islands = islands, Pads = Array.Empty<CopperPad>(), Vias = Array.Empty<Via>(),
            Nets = new[] { new CopperNet(0, new[] { islands[0] }) { Name = "P" }, new CopperNet(1, new[] { islands[1] }) { Name = "S" } },
            Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>()
        };
        var stackup = new BoardStackup { DefaultGapThickness = 0.2e-3 };
        var turns = PlanarWindingSection.Cut(board, new[] { "P", "S" }, new Point2(0, 0), 0, 0.02, stackup);
        Assert.Equal(2, turns.Count);
        Assert.Equal(5e-3, turns[0].R0, 9);
        Assert.Equal(6e-3, turns[0].R1, 9);
        Assert.Equal(0.0, turns[0].Z1, 12);
        Assert.Equal(-35e-6, turns[0].Z0, 12);
        Assert.Equal(-35e-6 - 0.2e-3, turns[1].Z1, 12);

        var built = PlanarWindingSection.Build(turns, new Dictionary<string, Complex> { ["P"] = 1 }, core: null, solidTurns: false);
        var solution = new MagneticSolver2D().SolveStatic(built.Model);
        foreach (var line in solution.Log) _out.WriteLine(line);
        double l = built.Inductance(solution, "P").Real;
        double m = solution.FluxLinkage(turns[1].Name).Real;
        double gmd = 0.2235 * (1e-3 + 35e-6);
        double expected = Mu0 * 5.5e-3 * (Math.Log(8 * 5.5e-3 / gmd) - 2);
        _out.WriteLine($"L {l * 1e9:F4} nH vs thin ring {expected * 1e9:F4} nH; M to the second layer {m * 1e9:F4} nH");
        Assert.True(Math.Abs(l - expected) / expected <= 0.03, $"L {l} vs {expected}");
        Assert.InRange(m / l, 0.7, 1.0);
    }

    [Fact]
    public void CommandLine_CutsAGerberWinding()
    {
        // A square single-turn loop (four bars, net P) on the top copper: the cut along +x from the
        // centre finds the right-hand bar, 5 to 6 mm out.
        string dir = Path.Combine(Path.GetTempPath(), "opensim-magnetics-" + Guid.NewGuid().ToString("N"));
        string board = Path.Combine(dir, "board");
        Directory.CreateDirectory(board);
        try
        {
            File.WriteAllText(Path.Combine(board, "top.gbr"), """
                %TF.FileFunction,Copper,L1,Top,Signal*%
                %TF.FilePolarity,Positive*%
                %FSLAX46Y46*%
                %MOMM*%
                %ADD10R,12.000X1.000*%
                %ADD11R,1.000X12.000*%
                %TO.N,P*%
                D10*
                X0Y5500000D03*
                X0Y-5500000D03*
                D11*
                X5500000Y0D03*
                X-5500000Y0D03*
                %TD*%
                M02*
                """);
            File.WriteAllText(Path.Combine(board, "bottom.gbr"), """
                %TF.FileFunction,Copper,L2,Bot,Signal*%
                %TF.FilePolarity,Positive*%
                %FSLAX46Y46*%
                %MOMM*%
                %ADD10R,1.000X1.000*%
                %TO.N,X*%
                D10*
                X20000000Y20000000D03*
                %TD*%
                M02*
                """);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int code = OpenSim.Cli.CliRunner.Run(new[] { "magnetics", board, "--nets", "P", "--center", "0,0", "--out", Path.Combine(dir, "out") }, stdout, stderr);
            _out.WriteLine(stdout.ToString());
            Assert.Equal("", stderr.ToString());
            Assert.Equal(0, code);
            Assert.Contains("L(P):", stdout.ToString());
            string report = File.ReadAllText(Path.Combine(dir, "out", "magnetics-report.md"));
            Assert.Contains("| P | 1 | 5 | 6 |", report);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void GappedPotCore_InductanceFollowsTheGap()
    {
        // A 4-turn winding in a ferrite pot core: L ≈ µ0·N²·A_post/g for a gap that dominates the
        // reluctance; fringing round the gap adds to it, so the solve sits above that line.
        var turns = Enumerable.Range(0, 4).Select(i => new WindingTurn("P", 1, 4e-3 + i * 1.2e-3, 4.8e-3 + i * 1.2e-3, -35e-6, 0)).ToList();
        var ferrite = new MagneticMaterial("ferrite", 2000) { Steinmetz = new SteinmetzCoefficients(10, 1.3, 2.6) };
        double Inductance(double gap)
        {
            var core = new AxisymmetricCore(3e-3, 10e-3, 12e-3, 2e-3, 2e-3, gap, ferrite);
            var built = PlanarWindingSection.Build(turns, new Dictionary<string, Complex> { ["P"] = 1 }, core, solidTurns: false);
            var solution = new MagneticSolver2D().SolveStatic(built.Model);
            return built.Inductance(solution, "P").Real;
        }
        double l1 = Inductance(0.2e-3), l2 = Inductance(0.4e-3);
        double simple1 = Mu0 * 16 * Math.PI * 9e-6 / 0.2e-3;
        _out.WriteLine($"gap 0.2 mm: {l1 * 1e6:F4} µH (µ0N²A/g {simple1 * 1e6:F4}); gap 0.4 mm: {l2 * 1e6:F4} µH; ratio {l1 / l2:F4}");
        Assert.True(l1 > simple1);
        Assert.InRange(l1 / l2, 1.3, 2.0);
    }
}
