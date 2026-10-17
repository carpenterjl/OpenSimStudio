using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Magnetics;
using OpenSim.Solvers.Magnetics;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>FU-40: polygonal regions on a constrained Delaunay mesh, the spiral cut averaged
/// around the revolution, lead-outs as PEEC series terms, and the ER core's square legs on a
/// planar companion cut.</summary>
public class MagneticsGeometryTests
{
    private readonly ITestOutputHelper _out;
    public MagneticsGeometryTests(ITestOutputHelper output) => _out = output;

    private const double Mu0 = MagneticConstants.Mu0;

    private static Polygon2 Rectangle(double x0, double x1, double y0, double y1) => new(new[]
    {
        new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1)
    });

    private static Polygon2 Circle(double cx, double cy, double r, int n) => new(
        Enumerable.Range(0, n).Select(k => new Point2(
            cx + r * Math.Cos(2 * Math.PI * k / n), cy + r * Math.Sin(2 * Math.PI * k / n))).ToList());

    [Fact]
    public void APolygonRectangle_MatchesTheStructuredMesh()
    {
        // The same ring coil built twice: as a rectangle (graded grid) and as the identical
        // four-point polygon (constrained Delaunay). The inductances must agree to mesh accuracy,
        // and on the unstructured mesh the discrete identity λ·I = 2·W must hold to roundoff
        // (it gates the assembly, the sparse factorization and the solve in one).
        MagneticModel2D Ring(bool polygon, double minElement = 0.1e-3)
        {
            var model = new MagneticModel2D
            {
                Geometry = MagneticGeometry.Axisymmetric,
                DomainX0 = 0, DomainX1 = 40e-3, DomainY0 = -20e-3, DomainY1 = 20e-3,
                MaxElement = 2e-3, MinElement = minElement
            };
            model.Regions.Add(polygon
                ? MagneticRegion.FromPolygon("coil", Rectangle(9.8e-3, 10.2e-3, -0.2e-3, 0.2e-3), MagneticMaterial.Air)
                : new MagneticRegion("coil", 9.8e-3, 10.2e-3, -0.2e-3, 0.2e-3, MagneticMaterial.Air));
            model.Coils.Add(new StrandedCoil("ring", new[] { ("coil", 1) }, 1, 1));
            return model;
        }
        var solver = new MagneticSolver2D();
        var structured = solver.SolveStatic(Ring(false));
        var unstructured = solver.SolveStatic(Ring(true));
        foreach (var line in unstructured.Log) _out.WriteLine(line);
        Assert.Contains(unstructured.Log, l => l.Contains("Unstructured"));

        double l0 = structured.Inductance("ring").Real;
        double l1 = unstructured.Inductance("ring").Real;
        double identity = Math.Abs(l1 - 2 * unstructured.Energy()) / (2 * unstructured.Energy());
        _out.WriteLine($"L structured {l0 * 1e9:g8} nH vs unstructured {l1 * 1e9:g8} ({l1 / l0 - 1:e2}); "
            + $"|λI − 2W|/2W {identity:e2}");
        // Measured −4.2e-4 (−4.4e-4 with MinElement halved — the gap is stable, not growing).
        Assert.True(Math.Abs(l1 - l0) / l0 <= 2e-3, $"{l1} vs {l0}");
        Assert.True(identity <= 1e-9, $"identity {identity}");

        // A polygon outside the domain is refused like a rectangle.
        var bad = Ring(true);
        bad.Regions.Add(MagneticRegion.FromPolygon("outside",
            Rectangle(39e-3, 41e-3, 0, 1e-3), MagneticMaterial.Air));
        Assert.Throws<ArgumentException>(() => solver.SolveStatic(bad));
    }

    [Fact]
    public void AMagnetizableCylinder_ConcentratesTheGapField()
    {
        // A µr cylinder in the uniform transverse field of the parallel-plate line. The 2D closed
        // form (solve Laplace in and out of the cylinder with matching Hθ and Br):
        // B_in = 2µr/(µr + 1)·B0, uniform inside. The cylinder is a 64-gon — a genuinely
        // non-rectangular region on the Delaunay mesh.
        const double current = 10, w = 10e-3, s = 6e-3, t = 0.5e-3, mur = 50, radius = 0.5e-3;
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Planar,
            DomainX0 = -5e-3, DomainX1 = 5e-3, DomainY0 = 0, DomainY1 = w,
            NaturalSides = DomainSides.Bottom | DomainSides.Top | DomainSides.Right,
            MaxElement = 0.5e-3, MinElement = 0.05e-3
        };
        model.Regions.Add(new MagneticRegion("go", -s / 2 - t, -s / 2, 0, w, MagneticMaterial.Air));
        model.Regions.Add(new MagneticRegion("return", s / 2, s / 2 + t, 0, w, MagneticMaterial.Air));
        model.Regions.Add(MagneticRegion.FromPolygon("cylinder", Circle(0, w / 2, radius, 64),
            new MagneticMaterial("iron", mur)));
        model.Coils.Add(new StrandedCoil("line", new[] { ("go", 1), ("return", -1) }, 1, current));

        var solution = new MagneticSolver2D().SolveStatic(model);
        _out.WriteLine(solution.Log[0]);
        double b0 = Mu0 * current / w;
        double expected = 2 * mur / (mur + 1.0) * b0;
        var samples = new[] { (0.0, 0.0), (0.2e-3, 0.0), (-0.2e-3, 0.0), (0.0, 0.2e-3), (0.0, -0.2e-3) }
            .Select(p => solution.FluxDensityAt(p.Item1, w / 2 + p.Item2).By.Real).ToArray();
        double worst = samples.Max(b => Math.Abs(b - expected) / expected);
        _out.WriteLine($"B0 {b0 * 1e3:g6} mT; B_in centre {samples[0] * 1e3:g6} mT vs 2µr/(µr+1)·B0 "
            + $"{expected * 1e3:g6} mT; worst of 5 samples {worst:e2}");
        Assert.True(worst <= 0.01, $"worst {worst}");   // measured 2.6e-3 (64-gon + mesh + plate images)
    }

    [Fact]
    public void AnAveragedCut_RecoversTheSpiralsMeanRadius_AndDropsTheLeadOut()
    {
        // A two-revolution Archimedean spiral r(θ) = r0 + p·θ/2π, θ ∈ [θs, θs + 4π], traced as a
        // polygon, plus a radial lead-out strip at 90° in the same net. A single cut sees each turn
        // at the radius of its angle; the averaged cut must see each turn at the mean of the used
        // angles' radii, and must drop the one angle whose ray crosses the lead-out.
        const double r0 = 5e-3, pitch = 2e-3, width = 1e-3, thetaS = Math.PI / 7;
        const int samples = 960, angleCount = 16;
        var outerEdge = Enumerable.Range(0, samples + 1).Select(i =>
        {
            double th = thetaS + 4 * Math.PI * i / samples;
            double r = r0 + pitch * (th - thetaS) / (2 * Math.PI) + width / 2;
            return new Point2(r * Math.Cos(th), r * Math.Sin(th));
        });
        var innerEdge = Enumerable.Range(0, samples + 1).Reverse().Select(i =>
        {
            double th = thetaS + 4 * Math.PI * i / samples;
            double r = r0 + pitch * (th - thetaS) / (2 * Math.PI) - width / 2;
            return new Point2(r * Math.Cos(th), r * Math.Sin(th));
        });
        var spiral = new Polygon2(outerEdge.Concat(innerEdge).ToList());
        var leadOut = Rectangle(-0.25e-3, 0.25e-3, 4e-3, 13e-3);   // along 90°
        var islands = new[] { new CopperIsland(0, 1, "top", spiral), new CopperIsland(1, 1, "top", leadOut) };
        var board = new PcbBoard
        {
            Outline = new[] { Rectangle(-15e-3, 15e-3, -15e-3, 15e-3) },
            Islands = islands,
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = new[] { new CopperNet(1, islands) { Name = "SP" } },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>()
        };
        var cut = PlanarWindingSection.CutAveraged(board, new[] { "SP" }, new Point2(0, 0), 15e-3,
            new BoardStackup(), angleCount);
        foreach (var n in cut.Notes) _out.WriteLine(n);
        Assert.Equal(2, cut.Turns.Count);
        Assert.Contains(cut.Notes, n => n.Contains("dropped"));
        Assert.Contains(cut.Notes, n => n.Contains("pitch"));

        // The expected means, over exactly the used angles (every j except j = 4, the 90° ray).
        var used = Enumerable.Range(0, angleCount).Where(j => j != 4).ToList();
        for (int turn = 0; turn < 2; turn++)
        {
            double expected = used.Average(j =>
            {
                double phi = 2 * Math.PI * j / angleCount;
                double th = phi + 2 * Math.PI * Math.Ceiling((thetaS - phi) / (2 * Math.PI)) + 2 * Math.PI * turn;
                return r0 + pitch * (th - thetaS) / (2 * Math.PI);
            });
            double got = 0.5 * (cut.Turns[turn].R0 + cut.Turns[turn].R1);
            _out.WriteLine($"turn {turn}: mean radius {got * 1e3:g8} mm vs analytic {expected * 1e3:g8} ({got / expected - 1:e2})");
            Assert.True(Math.Abs(got - expected) / expected <= 1e-3, $"{got} vs {expected}");
            Assert.True(Math.Abs(cut.Turns[turn].R1 - cut.Turns[turn].R0 - width) / width <= 1e-2,
                "the averaged turn keeps the trace width");
        }
    }

    [Fact]
    public void ALeadOut_AddsItsSeriesTermToTheDiagonal()
    {
        // The impedance matrix with and without a lead-out comes from the same deterministic
        // solves, so the difference must be exactly the PEEC series term, on Z11 alone.
        const double r = 10e-3, width = 0.2e-3, c = 0.2e-3, sep = 5e-3, sigma = 5.8e7, f = 1e5;
        var turns = new[]
        {
            new WindingTurn("P", 1, r - width / 2, r + width / 2, -c / 2, c / 2),
            new WindingTurn("S", 2, r - width / 2, r + width / 2, sep - c / 2, sep + c / 2)
        };
        var lead = new LeadOut("P", Length: 5e-3, Width: 1e-3, Thickness: 35e-6);
        var bare = PlanarWindingSection.ImpedanceMatrix(turns, core: null, f, sigma, minElement: 0.1e-3);
        var with = PlanarWindingSection.ImpedanceMatrix(turns, core: null, f, sigma, minElement: 0.1e-3,
            leadOuts: new[] { lead });
        var z = lead.SeriesImpedance(f);
        var delta = with.Z("P", "P") - bare.Z("P", "P");
        _out.WriteLine($"series term {z.Real * 1e3:g6} mΩ + {z.Imaginary / (2 * Math.PI * f) * 1e9:g6} nH; "
            + $"ΔZ11 {delta.Real * 1e3:g6} mΩ + {delta.Imaginary / (2 * Math.PI * f) * 1e9:g6} nH");
        Assert.True((delta - z).Magnitude / z.Magnitude <= 1e-9, $"{delta} vs {z}");
        Assert.True((with.Z("P", "S") - bare.Z("P", "S")).Magnitude <= 1e-15 * bare.Z("P", "S").Magnitude,
            "the mutual is untouched");
        Assert.True((with.Z("S", "S") - bare.Z("S", "S")).Magnitude <= 1e-15 * bare.Z("S", "S").Magnitude,
            "the other winding is untouched");
        Assert.Contains(with.Notes, n => n.Contains("Lead-out"));
        Assert.Throws<ArgumentException>(() => PlanarWindingSection.ImpedanceMatrix(turns, null, f, sigma,
            minElement: 0.1e-3, leadOuts: new[] { lead with { Net = "X" } }));
    }

    [Fact]
    public void AnErCore_PreservesLegArea_AndSplitsTheFluxBetweenLegs()
    {
        var er = new ErCore(PostRadius: 4e-3, WindowWidth: 6e-3, LegWidth: 3e-3, Depth: 12e-3,
            PlateThickness: 3e-3, WindowHeight: 8e-3, CentreGap: 0.2e-3,
            new MagneticMaterial("ferrite", 2000));

        // The axisymmetric wall preserves the legs' cross-section exactly.
        var axi = er.ToAxisymmetric();
        double wall = Math.PI * (axi.OuterRadius * axi.OuterRadius - axi.WindowRadius * axi.WindowRadius);
        double legs = 2 * er.LegWidth * er.Depth;
        Assert.True(Math.Abs(wall - legs) / legs <= 1e-12, $"{wall} vs {legs}");

        // The planar cut through the legs: the post's flux must split equally between the two legs
        // and (up to window leakage) be conserved into them.
        var section = er.PlanarLegSection(ampereTurns: 100);
        section.ExtraLinesY.Add(2e-3);
        var solution = new MagneticSolver2D().SolveStatic(section);
        double A(double x) => solution.PotentialAt(x, 2e-3).Real;
        double post = A(-er.PostRadius) - A(er.PostRadius);
        double legL = A(-er.WindowRadius - er.LegWidth) - A(-er.WindowRadius);
        double legR = A(er.WindowRadius) - A(er.WindowRadius + er.LegWidth);
        // The legs return the post's flux, so their total is its opposite; the shortfall is the
        // window's leakage flux.
        double symmetry = Math.Abs(legL - legR) / Math.Abs(post);
        double conservation = Math.Abs(Math.Abs(legL + legR) - Math.Abs(post)) / Math.Abs(post);
        _out.WriteLine($"post flux {post * 1e6:g6} µWb/m, legs {legL * 1e6:g6} + {legR * 1e6:g6}; "
            + $"symmetry {symmetry:e2}, conservation {conservation:e2}");
        Assert.True(symmetry <= 1e-3, $"symmetry {symmetry}");   // measured 4.2e-5
        Assert.True(conservation <= 5e-2, $"conservation {conservation}");   // measured 1.1e-2 (leakage)

        // The gapped axisymmetric equivalent: the magnetic circuit µ0·π·a²/gap is a sanity band
        // only (fringing at the gap raises L above it).
        var turn = new[] { new WindingTurn("W", 1, 5e-3, 9e-3, -1e-3, 1e-3) };
        var built = PlanarWindingSection.Build(turn, new Dictionary<string, Complex> { ["W"] = 1 },
            axi, solidTurns: false);
        var l = new MagneticSolver2D().SolveStatic(built.Model).FluxLinkage(turn[0].Name).Real;
        double circuit = Mu0 * Math.PI * er.PostRadius * er.PostRadius / er.CentreGap;
        _out.WriteLine($"L {l * 1e9:g6} nH vs gap magnetic circuit {circuit * 1e9:g6} nH ({l / circuit - 1:e2})");
        Assert.True(l > circuit, "fringing must raise L above the ungapped-circuit value");
        Assert.True(Math.Abs(l - circuit) / circuit <= 0.1, $"{l} vs {circuit}");   // measured +2.7 % (fringing)

        Assert.Throws<ArgumentException>(() => (er with { CentreGap = 9e-3 }).PlanarLegSection(1));
        Assert.Throws<ArgumentException>(() => (er with { LegWidth = 0 }).ToAxisymmetric());
    }
}
