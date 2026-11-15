using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization.TestPlates;
using OpenSim.Optics.Sequential;
using Xunit;
using static OpenSim.Tests.Optics.LocalOptimizationFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-17: test plate lists (OpticStudio's .TPD layout) and test plate fitting
/// (docs/Optics-Conventions.md §48). Every list here is synthetic: lists come from fabricators and
/// none ships. The layout and the fitting rules are from OpticStudio 2025 R2 help, "Test Plate
/// Fitting" (plans/sources/optics/op-17-help/); the fringe relation from its "TFRN: Tolerance on
/// Fringes" page. Lenses are in millimeters with constant indices; their radii are arbitrary inputs.
/// </summary>
public sealed class TestPlateFittingTests
{
    // ------------------------------------------------------------------ the list file

    [Fact]
    public void AList_WrittenAndReadBack_IsTheSame_IncludingTabsExponentsAndHeader()
    {
        const string text = "! Synthetic Optics Ltd\r\n!  tel 000\r\n\r\nP1 60 30 -1\nP2\t4.5E1\t30\t0\r\nP-3   200.25 0 1\n";
        var list = TestPlateList.Parse(text, "synthetic.tpd");
        Assert.Equal(new[] { " Synthetic Optics Ltd", "  tel 000" }, list.Header);
        Assert.Equal(3, list.Plates.Count);
        Assert.Equal(new TestPlate("P2", 45, 30, TestPlateShape.Both, 5), list.Plates[1]);
        Assert.Equal(TestPlateShape.ConvexOnly, list.Plates[2].Shape);
        Assert.Equal(0, list.Plates[2].Diameter);

        var again = TestPlateList.Parse(list.Format());
        Assert.Equal(list.Header, again.Header);
        Assert.Equal(list.Plates.Select(p => (p.Name, p.Radius, p.Diameter, p.Shape)), again.Plates.Select(p => (p.Name, p.Radius, p.Diameter, p.Shape)));
    }

    [Fact]
    public void ABadList_FailsNamingEveryLine()
    {
        string text = "!h\nA 10 5\nB -3 5 0\nC 10 5 2\nD 10 x 0\nE 10 5 0\n!late\n";
        var e = Assert.Throws<FormatException>(() => TestPlateList.Parse(text, "bad.tpd"));
        Assert.Contains("bad.tpd", e.Message);
        foreach (string line in new[] { "line 2", "line 3", "line 4", "line 5", "line 7" }) Assert.Contains(line, e.Message);
        Assert.DoesNotContain("line 6", e.Message);

        string sixteen = string.Concat(Enumerable.Range(1, 16).Select(i => $"!h{i}\n")) + "A 10 5 0\n";
        Assert.Contains("line 16", Assert.Throws<FormatException>(() => TestPlateList.Parse(sixteen)).Message);
        Assert.Throws<FormatException>(() => TestPlateList.Parse("!only a header\n"));
        Assert.Throws<ArgumentException>(() => new TestPlateList { Plates = new[] { new TestPlate("has space", 10, 5, TestPlateShape.Both) } }.Format());
    }

    [Fact]
    public void TheListReport_ShowsCcAndCxColumns_AndTheHeaderWithASpaceForTheBang()
    {
        var list = TestPlateList.Parse("!Vendor\nA 10 5 -1\nB 20 5 1\nC 30 0 0\n");
        var lines = list.ReportLines();
        Assert.Contains(" Vendor", lines);
        Assert.Contains(lines, l => l.Contains("CC") && l.Contains("CX"));
        Assert.Contains(lines, l => l.StartsWith("A ") && l.TrimEnd().EndsWith("X   -"));
        Assert.Contains(lines, l => l.StartsWith("B ") && l.TrimEnd().EndsWith("-   X"));
    }

    // ------------------------------------------------------------------ the plate search

    /// <summary>The TFRN help page's worked example: 1 fringe at 0.6328 µm over a clear
    /// semi-diameter of 6 mm on a 50 mm radius is ΔZ = 0.3164 µm and ΔC = ΔZ·2/r² ≈ 1.76E-5 /mm
    /// (OpticStudio 2025 R2 help, "TFRN: Tolerance on Fringes"). A plate one fringe away by that
    /// relation (ΔC = λ/r², derived from ΔZ = (λ/2)·N and ΔC = 2ΔZ/r²) is reported as 1 fringe.</summary>
    [Fact]
    public void APlateOneFringeAway_ByTheTfrnRelation_IsReportedAsOneFringe()
    {
        const double lambdaMm = 0.6328e-3, r = 6, c = 1.0 / 50;
        double plateRadius = 1 / (c + lambdaMm / (r * r));
        var list = new TestPlateList { Plates = new[] { new TestPlate("ONE", plateRadius, 0, TestPlateShape.ConcaveOnly) } };
        var m = TestPlateFitting.FindPlate(list, c, 0, r, new TestPlateFitting.SurfaceSide(1, false), lambdaMm);
        Assert.Equal("ONE", m.Plate!.Name);
        Assert.Equal(1.0, m.Fringes, 9);
    }

    [Fact]
    public void ThePlateSearch_SkipsTheWrongShape_TooSmallAndTooFarInSag_AndKeepsTheClosest()
    {
        // A convex front surface (glass after it), R 60 mm, clear semi-diameter 10 mm: it needs a
        // concave plate of diameter ≥ 15 mm whose edge sag is within 0.2 mm of the surface's.
        var list = new TestPlateList
        {
            Plates = new[]
            {
                new TestPlate("CONVEXONLY", 60.0, 30, TestPlateShape.ConvexOnly),
                new TestPlate("SMALL", 60.01, 14.9, TestPlateShape.Both),
                new TestPlate("FAR", 30.0, 30, TestPlateShape.Both),   // sag at 10 mm ≈ 1.72 vs 0.84 mm
                new TestPlate("FAR2", 63.0, 30, TestPlateShape.ConcaveOnly),
                new TestPlate("NEAR", 60.5, 0, TestPlateShape.ConcaveOnly),
                new TestPlate("NEARTOO", 60.5, 30, TestPlateShape.Both),   // ties NEAR: the earlier line wins
            },
        };
        var m = TestPlateFitting.FindPlate(list, 1.0 / 60, 0, 10, new TestPlateFitting.SurfaceSide(1, false), 0.6328e-3);
        Assert.Equal("NEAR", m.Plate!.Name);
        Assert.Equal(1, m.Sign);

        // The same surface as the back of an element (glass before it) is concave: the convex-only plate fits exactly.
        var back = TestPlateFitting.FindPlate(list, 1.0 / 60, 0, 10, new TestPlateFitting.SurfaceSide(-1, false), 0.6328e-3);
        Assert.Equal("CONVEXONLY", back.Plate!.Name);
        Assert.Equal(0, back.Fringes);

        // Cemented: only code 0 plates; SMALL is too small, FAR too far in sag, so NEARTOO.
        var cemented = TestPlateFitting.FindPlate(list, 1.0 / 60, 0, 10, new TestPlateFitting.SurfaceSide(1, true), 0.6328e-3);
        Assert.Equal("NEARTOO", cemented.Plate!.Name);

        var none = TestPlateFitting.FindPlate(new TestPlateList { Plates = new[] { list.Plates[1], list.Plates[2] } },
            1.0 / 60, 0, 10, new TestPlateFitting.SurfaceSide(1, false), 0.6328e-3);
        Assert.Null(none.Plate);
        Assert.Contains("1 are smaller than 15 mm", none.Reason);
        Assert.Contains("1 differ from the surface's edge sag", none.Reason);
    }

    [Fact]
    public void TheMaterialSide_FollowsGlassMirrorsAndCementing()
    {
        var s = new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Index = 1.5 },                 // 1: glass after → +1
            new() { Radius = -50, Index = 1.6 },                // 2: glass both sides → cemented
            new() { Radius = -80 },                             // 3: glass before → −1
            new() { Radius = -200, Material = "MIRROR" },       // 4: mirror in air → substrate behind, +1
            new() { Radius = 100 },                             // 5: air both sides
            new() { Radius = 100, Index = 1.5 },                // 6: after the mirror light runs −z; glass after → −1
            new(),
        };
        Assert.Equal(new TestPlateFitting.SurfaceSide(1, false), TestPlateFitting.Side(s, 1));
        Assert.True(TestPlateFitting.Side(s, 2)!.Cemented);
        Assert.Equal(new TestPlateFitting.SurfaceSide(-1, false), TestPlateFitting.Side(s, 3));
        Assert.Equal(new TestPlateFitting.SurfaceSide(1, false), TestPlateFitting.Side(s, 4));
        Assert.Null(TestPlateFitting.Side(s, 5));
        Assert.Equal(new TestPlateFitting.SurfaceSide(-1, false), TestPlateFitting.Side(s, 6));
    }

    // ------------------------------------------------------------------ fitting

    private const double R1 = 60, R2 = -45, R3 = -200;

    /// <summary>A cemented doublet (indices 1.5, 1.65), stop on surface 1, entrance pupil 10 mm,
    /// fixed clear semi-diameters 10 mm, the three radii variable; merit EFFL = the focal length of
    /// the doublet with radii (R1, R2, R3), evaluated by the merit function itself. One row against
    /// three variables, so the merit is zero along a two-parameter family of radii and each
    /// re-optimization after a fitted radius brings the others back onto it.</summary>
    private static OpticsSettings Doublet(double r1, double r2, double r3)
    {
        OpticsSettings Build(double a, double b, double c, double target) => new()
        {
            SystemData = new OpticsSystemSettings { ApertureValue = 10 },
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = a, Thickness = 4, Index = 1.5, IsStop = true, SemiDiameter = 10, Flags = new() { Variable("Radius") } },
                new() { Radius = b, Thickness = 2, Index = 1.65, SemiDiameter = 10, Flags = new() { Variable("Radius") } },
                new() { Radius = c, Thickness = 95, SemiDiameter = 10, Flags = new() { Variable("Radius") } },
                new(),
            },
            MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new[] { MeritOperandRow.Of("EFFL", 0, 1, target: target, weight: 1) })),
        };
        var nominal = Build(R1, R2, R3, 0);
        double f0 = MeritEvaluator.Evaluate(new MeritFunction(new[] { MeritOperandRow.Of("EFFL", 0, 1, target: 0, weight: 1) }),
            new ProjectMeritConfigurations(nominal, NoCatalogs)).Rows[0].Value;
        return Build(r1, r2, r3, f0);
    }

    /// <summary>The plates of R1, R2, R3 with the shapes the surfaces need (surface 1 convex, a
    /// concave plate; surface 2 cemented, both; surface 3 convex as the back of the flint, a
    /// concave plate), neighbours 5 % away, and decoys nearer to the perturbed radii that the
    /// shape and diameter tests must reject.</summary>
    private static TestPlateList SyntheticList(bool withSurface3 = true)
    {
        var plates = new List<TestPlate>
        {
            new("N57", 57, 30, TestPlateShape.Both), new("N63", 63, 30, TestPlateShape.Both),
            new("P60", 60, 30, TestPlateShape.ConcaveOnly),
            new("DECOY60", 60.3, 30, TestPlateShape.ConvexOnly),     // closest to 60.3 but the wrong shape
            new("N43", 43, 30, TestPlateShape.Both), new("N47", 47, 30, TestPlateShape.Both),
            new("P45", 45, 30, TestPlateShape.Both),
            new("DECOY45", 45.2, 12, TestPlateShape.Both),            // closest to 45.2 but smaller than 15 mm
            new("CC45", 45.1, 30, TestPlateShape.ConcaveOnly),         // close, but a cemented surface needs both
        };
        if (withSurface3)
            plates.AddRange(new[] { new TestPlate("N190", 190, 30, TestPlateShape.Both), new TestPlate("P200", 200, 30, TestPlateShape.ConcaveOnly), new TestPlate("N210", 210, 30, TestPlateShape.Both) });
        else
            plates.Add(new TestPlate("TINY200", 200, 5, TestPlateShape.Both));
        return new TestPlateList { Header = new[] { " Synthetic plates" }, Plates = plates };
    }

    /// <summary>
    /// OP-17 gate: on a design whose radii are perturbed copies of a synthetic plate list, fitting
    /// returns the unperturbed design, by every method of fit. The radii are written from the
    /// plates, so they come back exactly; the merit function (EFFL against the unperturbed focal
    /// length) is then evaluated on the very design it was built from, so it is zero up to
    /// rounding. Each radius loses its Variable flag.
    /// </summary>
    [Fact]
    public void Gate_PerturbedRadii_FitBackToTheUnperturbedDesign_ByEveryMethod()
    {
        var perturbed = Doublet(R1 * 1.005, R2 * 1.0045, R3 * 1.005);
        var result = TestPlateFitting.Run(perturbed, SyntheticList(), NoCatalogs, new TestPlateFitOptions { Method = TestPlateFitMethod.TryAll });

        Assert.Equal(4, result.Runs.Count);
        foreach (var run in result.Runs)
        {
            Assert.Null(run.Error);
            var s = run.Design!.Surfaces!;
            Assert.Equal(new double?[] { R1, R2, R3 }, new[] { s[1].Radius, s[2].Radius, s[3].Radius });
            Assert.Equal(new[] { "P60", "P45", "P200" }, run.Steps.OrderBy(t => t.Surface).Select(t => t.Plate!.Name));
            Assert.Equal(0, run.Merit, 9);
            Assert.All(new[] { 1, 2, 3 }, j => Assert.DoesNotContain(s[j].Flags ?? new(), f => f.Column == "Radius" && f.Kind == "Variable"));
        }
        Assert.Empty(result.Unmatched);
        Assert.Equal(0, result.Merit, 9);
        Assert.Equal(TestPlateFitMethod.BestToWorst, result.Method);   // all tie at zero: the first in the help's order is kept
        // The orders by radius length do not depend on the optimizer: 200 is the longest, 45 the shortest.
        Assert.Equal(3, result.Runs.Single(r => r.Method == TestPlateFitMethod.LongToShort).Steps[0].Surface);
        Assert.Equal(2, result.Runs.Single(r => r.Method == TestPlateFitMethod.ShortToLong).Steps[0].Surface);
        Assert.Contains(result.ReportLines(), l => l.Contains("plate P60"));
    }

    [Fact]
    public void ARadiusWithNoPlate_IsReportedByName_AndStaysVariable()
    {
        var perturbed = Doublet(R1 * 1.005, R2 * 1.0045, R3 * 1.005);
        var result = TestPlateFitting.Run(perturbed, SyntheticList(withSurface3: false), NoCatalogs, new TestPlateFitOptions { Method = TestPlateFitMethod.BestToWorst });
        var miss = Assert.Single(result.Steps, s => s.Plate is null);
        Assert.Equal(3, miss.Surface);
        Assert.Contains("smaller than 15 mm", miss.NoMatch);
        Assert.Equal(new[] { 3 }, result.Unmatched);
        Assert.Contains(result.Fitted.Surfaces![3].Flags!, f => f.Column == "Radius" && f.Kind == "Variable");
        Assert.Equal(R1, result.Fitted.Surfaces[1].Radius);
        Assert.Equal(R2, result.Fitted.Surfaces[2].Radius);
        Assert.Contains(result.ReportLines(), l => l.Contains("Surface 3") && l.Contains("NO MATCH FOUND!"));
    }

    [Fact]
    public void NoVariableRadius_OrAVariableRadiusInAir_IsRefusedByName()
    {
        var fixedRadii = Doublet(R1, R2, R3) with { };
        fixedRadii = fixedRadii with { Surfaces = fixedRadii.Surfaces!.Select(s => s with { Flags = null }).ToList() };
        fixedRadii.Surfaces[3] = fixedRadii.Surfaces[3] with { Flags = new() { Variable("Thickness") } };
        Assert.Contains("No radius is variable", Assert.Throws<ArgumentException>(() => TestPlateFitting.Run(fixedRadii, SyntheticList(), NoCatalogs)).Message);

        var air = Doublet(R1, R2, R3);
        var list = air.Surfaces!.ToList();
        list.Insert(4, new OpticsSurfaceSettings { Radius = 300, SemiDiameter = 10, Flags = new() { Variable("Radius") } });
        list[3] = list[3] with { Thickness = 5 };
        air = air with { Surfaces = list };
        Assert.Contains("Surface 4 has air on both sides", Assert.Throws<ArgumentException>(() => TestPlateFitting.Run(air, SyntheticList(), NoCatalogs)).Message);
    }
}
