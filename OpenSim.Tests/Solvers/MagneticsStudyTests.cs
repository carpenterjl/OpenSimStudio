using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Magnetics;
using OpenSim.Solvers.Magnetics;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>FU-47: the planar-magnetics study the panel and the command line share is exactly the
/// direct solve; a cut works from the nets themselves (unnamed ones too); and the bucketed field
/// sampler behind the panel's flux-density picture picks the same element as the point lookup.</summary>
public class MagneticsStudyTests
{
    private readonly ITestOutputHelper _out;
    public MagneticsStudyTests(ITestOutputHelper output) => _out = output;

    private static Polygon2 Annulus(double r0, double r1, int segments = 512)
    {
        IReadOnlyList<Point2> Ring(double r, bool ccw) => Enumerable.Range(0, segments)
            .Select(i => (ccw ? 1 : -1) * 2 * Math.PI * i / segments)
            .Select(a => new Point2(r * Math.Cos(a), r * Math.Sin(a))).ToList();
        return new Polygon2(Ring(r1, true), new[] { Ring(r0, false) });
    }

    /// <summary>MagneticsTests' two rings, one per layer; <paramref name="nameSecond"/> false leaves
    /// the second net unnamed, as a Gerber set without X2 attributes would.</summary>
    private static (PcbBoard Board, CopperNet P, CopperNet S) Rings(bool nameSecond = true)
    {
        var islands = new[]
        {
            new CopperIsland(0, 1, "L1", Annulus(5e-3, 6e-3)),
            new CopperIsland(1, 2, "L2", Annulus(5e-3, 6e-3)),
        };
        var p = new CopperNet(0, new[] { islands[0] }) { Name = "P" };
        var s = new CopperNet(1, new[] { islands[1] }) { Name = nameSecond ? "S" : null };
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(), Islands = islands, Pads = Array.Empty<CopperPad>(), Vias = Array.Empty<Via>(),
            Nets = new[] { p, s }, Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>()
        };
        return (board, p, s);
    }

    private static readonly BoardStackup Stackup = new() { DefaultGapThickness = 0.2e-3 };

    [Fact]
    public void Study_IsTheDirectSolve_StaticAndAc()
    {
        var (board, _, _) = Rings();
        var turns = PlanarWindingSection.Cut(board, new[] { "P", "S" }, new Point2(0, 0), 0, 0.02, Stackup);

        var stat = PlanarMagneticsStudy.Run(new PlanarMagneticsInput { Turns = turns, Drive = "P", Windings = new[] { "P", "S" } });
        var built = PlanarWindingSection.Build(turns, new Dictionary<string, Complex> { ["P"] = 1 }, core: null, solidTurns: false);
        var solution = new MagneticSolver2D().SolveStatic(built.Model);
        Assert.Equal(built.Inductance(solution, "P"), stat.Inductance);
        var (winding, mutual) = Assert.Single(stat.Mutuals);
        Assert.Equal("S", winding);
        Assert.Equal(solution.FluxLinkage(turns[1].Name), mutual);
        Assert.Null(stat.AcResistance);

        const double f = 100e3;
        var ac = PlanarMagneticsStudy.Run(new PlanarMagneticsInput { Turns = turns, Drive = "P", FrequencyHz = f });
        var builtAc = PlanarWindingSection.Build(turns, new Dictionary<string, Complex> { ["P"] = 1 }, core: null, solidTurns: true);
        var solutionAc = new MagneticSolver2D().SolveTimeHarmonic(builtAc.Model, f);
        Assert.True(ac.SolidTurns);
        Assert.Equal(builtAc.Resistance(solutionAc, "P"), ac.AcResistance);
        Assert.Equal(builtAc.Inductance(solutionAc, "P"), ac.Inductance);
        foreach (var (k, v) in ac.Lines()) _out.WriteLine($"{k}: {v}");
        Assert.Contains(ac.Lines(), l => l.Key == "M(P,S)");

        string report = ac.Report("rings", "(0, 0) mm, cut at 0°").ToMarkdown();
        Assert.Contains("| P | 1 | 5 | 6 |", report);
        Assert.Contains("(0, 0) mm, cut at 0°", report);
    }

    [Fact]
    public void Cut_TakesTheNetsThemselves_AndNamesAnUnnamedOne()
    {
        var (named, p, s) = Rings();
        var byName = PlanarWindingSection.Cut(named, new[] { "P", "S" }, new Point2(0, 0), 0, 0.02, Stackup);
        var byNet = PlanarWindingSection.Cut(named, new[] { p, s }, new Point2(0, 0), 0, 0.02, Stackup);
        Assert.Equal(byName, byNet);

        var (unnamed, p2, s2) = Rings(nameSecond: false);
        var turns = PlanarWindingSection.Cut(unnamed, new[] { p2, s2 }, new Point2(0, 0), 0, 0.02, Stackup);
        Assert.Equal(new[] { "Net 1", "P" }, turns.Select(t => t.Net).Distinct().OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("Net 1", PlanarWindingSection.WindingName(s2));
        var averaged = PlanarWindingSection.CutAveraged(unnamed, new[] { p2, s2 }, new Point2(0, 0), 0.02, Stackup, 4);
        Assert.Equal(2, averaged.Turns.Count);
    }

    [Fact]
    public void SampledFluxDensity_IsThePointLookup_OnGridAndUnstructuredMeshes()
    {
        var (board, _, _) = Rings();
        var turns = PlanarWindingSection.Cut(board, new[] { "P", "S" }, new Point2(0, 0), 0, 0.02, Stackup);
        var built = PlanarWindingSection.Build(turns, new Dictionary<string, Complex> { ["P"] = 1 }, core: null, solidTurns: false);
        Check(new MagneticSolver2D().SolveStatic(built.Model), built.Model);

        // A polygon region switches the model to the constrained Delaunay mesh.
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Planar, DomainX0 = -10e-3, DomainX1 = 10e-3, DomainY0 = -10e-3, DomainY1 = 10e-3,
            MaxElement = 1e-3, MinElement = 0.25e-3
        };
        model.Regions.Add(MagneticRegion.FromPolygon("bar", new Polygon2(new[]
        {
            new Point2(-2e-3, -3e-3), new Point2(3e-3, -1e-3), new Point2(1e-3, 3e-3)
        }), MagneticMaterial.Air));
        model.Coils.Add(new StrandedCoil("coil", new[] { ("bar", 1) }, 1, 1));
        Check(new MagneticSolver2D().SolveTimeHarmonic(model, 1e3), model);

        void Check(MagneticSolution2D solution, MagneticModel2D m)
        {
            var random = new Random(47);
            double w = m.DomainX1 - m.DomainX0, h = m.DomainY1 - m.DomainY0;
            // Inside the domain, and a margin past it on every side (the lookup clamps nothing).
            var points = Enumerable.Range(0, 2000)
                .Select(_ => (m.DomainX0 - 0.1 * w + 1.2 * w * random.NextDouble(), m.DomainY0 - 0.1 * h + 1.2 * h * random.NextDouble()))
                .ToList();
            var sampled = solution.SampleFluxDensity(points);
            int same = 0;
            for (int k = 0; k < points.Count; k++)
            {
                var single = solution.FluxDensityAt(points[k].Item1, points[k].Item2);
                Assert.Equal(single, sampled[k]);
                same++;
            }
            _out.WriteLine($"{solution.ElementCount} elements: {same} of {points.Count} samples equal the point lookup");
            var (bx, by) = sampled[0];
            Assert.True(MagneticSolution2D.PeakMagnitude(bx, by) >= 0);
        }
    }
}
