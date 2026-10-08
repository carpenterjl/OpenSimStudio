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
/// FU-36: the air model with each part meshed on its own (<see cref="SheetPartsAssembly"/>) instead
/// of on one shared tensor grid. Gated against the same oracle the grid model is — the layered pins
/// in air — and against the grid model itself on shapes both can describe.
/// </summary>
public class SheetPartsTests
{
    private readonly ITestOutputHelper _out;
    public SheetPartsTests(ITestOutputHelper output) => _out = output;

    private static Polygon2 Rect(double u0, double v0, double u1, double v1) =>
        new(new[] { new Point2(u0, v0), new Point2(u1, v0), new Point2(u1, v1), new Point2(u0, v1) });

    private (double Henries, int Unknowns) PartsInductance(double h, double x, double w, double f, double minEdge)
    {
        var parts = new[]
        {
            new SheetPart("top", SheetNormal.Z, h, Rect(-0.01, -0.01, 0.01, 0.01)),
            new SheetPart("feed", SheetNormal.Y, 0, Rect(-x - w / 2, 0, -x + w / 2, h)),
            new SheetPart("short", SheetNormal.Y, 0, Rect(x - w / 2, 0, x + w / 2, h)),
        };
        var ports = new[] { new SheetPortLine("feed", new Vector3D(-x - w / 2, 0, h / 2), new Vector3D(-x + w / 2, 0, h / 2), new Vector3D(0, 0, 1)) };
        var model = SheetPartsAssembly.Build(parts, ports, 2e-3, maxUnknowns: 4000, minEdgeLength: minEdge, imageGround: new GroundPlane(0));
        _out.WriteLine(string.Join("; ", model.Notes));
        var z = 1 / new SurfaceMomSolver().SolveMultiPort(model.Structure, f, model.Ports.Select(p => p.Port).ToList()).Admittance[0, 0];
        return (z.Imaginary / (2 * Math.PI * f), model.Structure.BasisCount);
    }

    [Fact]
    public void ThePinLoop_ConvergesOnTheLayeredPins_WithFewerUnknownsThanTheGrid()
    {
        // The two-strip loop under a 20 mm plate over an image ground (BoardStructuresTests'
        // air-model gate): the grid model reads +3.4 % at 0.25 mm grading with 1975 unknowns,
        // converging from above. Meshed per part: +4.3 % at 0.25 mm (663 unknowns), +3.8 % at
        // 0.125 mm (990) — about the grid's accuracy for half its unknowns, the fine elements kept
        // where the strips meet the plate instead of along every grid line across it.
        const double h = 4e-3, a = 0.25e-3, f = 30e6, x = 4e-3;
        var plate = SurfaceMeshBuilder.BuildRectangularPlate(0.02, 0.02, 2e-3, z: h).Structure!;
        var table = new LayeredKernelTable(new SubstrateStackup(1.0, 0, h), f, 0.04);
        double layered = new SurfaceMomSolver().SolvePins(plate, table, new[]
        {
            new VerticalPin("feed", new ProbeFeed(-x, 0, a, 3)),
            new VerticalPin("short", new ProbeFeed(x, 0, a, 3), IsPort: false)
        }).Impedance()[0, 0].Imaginary / (2 * Math.PI * f);
        var (coarse, coarseN) = PartsInductance(h, x, 4 * a, f, 0.25e-3);
        var (fine, fineN) = PartsInductance(h, x, 4 * a, f, 0.125e-3);
        var grid = SheetMetalAssembly.Build(new[]
        {
            new SheetRectangle("top", new Vector3D(-0.01, -0.01, h), new Vector3D(0.01, 0.01, h)),
            new SheetRectangle("feed", new Vector3D(-x - 2 * a, 0, 0), new Vector3D(-x + 2 * a, 0, h)),
            new SheetRectangle("short", new Vector3D(x - 2 * a, 0, 0), new Vector3D(x + 2 * a, 0, h)),
        }, new[] { new SheetPortLine("feed", new Vector3D(-x - 2 * a, 0, h / 2), new Vector3D(-x + 2 * a, 0, h / 2), new Vector3D(0, 0, 1)) },
            2e-3, maxUnknowns: 4000, minEdgeLength: 0.25e-3, imageGround: new GroundPlane(0));
        _out.WriteLine($"layered {layered * 1e9:F4} nH; parts 0.25 mm {coarse * 1e9:F4} nH ({coarseN} unknowns), " +
                       $"0.125 mm {fine * 1e9:F4} nH ({fineN}); the grid at 0.25 mm has {grid.Structure.BasisCount} unknowns");
        Assert.True(Math.Abs(fine - layered) < Math.Abs(coarse - layered), "refining must approach the layered value");
        Assert.InRange((fine - layered) / layered, -0.04, 0.04);
        Assert.True(fineN < grid.Structure.BasisCount);
    }

    [Fact]
    public void AnLShapedPart_IsTheTwoRectanglesOfTheGrid()
    {
        // A strip standing on an L-shaped plate, fed at its foot over an image ground: as ONE
        // polygon here, as two rectangles on the grid. Different meshes of one structure.
        const double h = 3e-3, f = 1.5e9;
        var l = new Polygon2(new[]
        {
            new Point2(-0.015, -0.01), new Point2(0.015, -0.01), new Point2(0.015, 0.0),
            new Point2(0.0, 0.0), new Point2(0.0, 0.02), new Point2(-0.015, 0.02)
        });
        var feedLine = new SheetPortLine("feed", new Vector3D(-0.008, -0.0005, 0.5e-3), new Vector3D(-0.008, 0.0005, 0.5e-3), new Vector3D(0, 0, 1));
        var parts = SheetPartsAssembly.Build(new[]
        {
            new SheetPart("plate", SheetNormal.Z, h, l),
            new SheetPart("strip", SheetNormal.X, -0.008, Rect(-0.0005, 0, 0.0005, h))
        }, new[] { feedLine }, 1.5e-3, imageGround: new GroundPlane(0));
        var gridModel = SheetMetalAssembly.Build(new[]
        {
            new SheetRectangle("a", new Vector3D(-0.015, -0.01, h), new Vector3D(0.015, 0.0, h)),
            new SheetRectangle("b", new Vector3D(-0.015, 0.0, h), new Vector3D(0.0, 0.02, h)),
            new SheetRectangle("strip", new Vector3D(-0.008, -0.0005, 0), new Vector3D(-0.008, 0.0005, h))
        }, new[] { feedLine }, 1.5e-3, imageGround: new GroundPlane(0));
        Complex Zin(SheetAssemblyResult m) => 1 / new SurfaceMomSolver().SolveMultiPort(m.Structure, f, m.Ports.Select(p => p.Port).ToList()).Admittance[0, 0];
        var zParts = Zin(parts);
        var zGrid = Zin(gridModel);
        double rel = (zParts - zGrid).Magnitude / zGrid.Magnitude;
        _out.WriteLine($"parts {zParts:f3} ({parts.Structure.BasisCount} unknowns), grid {zGrid:f3} ({gridModel.Structure.BasisCount}): {rel:p2} apart");
        Assert.InRange(rel, 0, 0.03);
    }

    [Fact]
    public void AStripStandingInAPlate_MeetsItThroughJunctionEdges()
    {
        var model = SheetPartsAssembly.Build(new[]
        {
            new SheetPart("plate", SheetNormal.Z, 0, Rect(-0.01, -0.01, 0.01, 0.01)),
            new SheetPart("strip", SheetNormal.Y, 0, Rect(-0.001, 0, 0.001, 0.005))
        }, Array.Empty<SheetPortLine>(), 2e-3, minEdgeLength: 0.5e-3);
        _out.WriteLine(string.Join("; ", model.Notes));
        Assert.True(model.Structure.JunctionEdgeCount >= 2);
        // Every vertex of the strip's foot is a vertex of the plate: the foot is shared, not overlapped.
        var foot = model.Structure.Vertices.Where(v => v.Z == 0 && v.Y == 0 && Math.Abs(v.X) <= 0.001).ToList();
        Assert.True(foot.Count >= 3);
    }
}
