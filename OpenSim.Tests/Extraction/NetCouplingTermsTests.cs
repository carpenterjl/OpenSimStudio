using OpenSim.Core.Geometry2D;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Extraction;
using OpenSim.Rf.Si.Layout;
using OpenSim.Solvers;
using OpenSim.Tests.Pdn;
using OpenSim.Tests.Solvers;
using Xunit.Abstractions;

namespace OpenSim.Tests.Extraction;

/// <summary>
/// The capacitance-matrix terms FU-33 adds (crossings, pads, via barrels): the crossing term
/// against the 3D electrostatic solver, and the board-level scan picking each kind up — or
/// dropping it where a third net's copper shields the pair.
/// </summary>
public class NetCouplingTermsTests
{
    private const double Epsilon0 = 8.8541878128e-12;
    private readonly ITestOutputHelper _output;
    public NetCouplingTermsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ACrossing_InTheElectrostaticSolver_MatchesTheSectionTerm()
    {
        // Two 0.5 mm traces crossing at right angles, 0.3 mm of εr 4.2 between them, a ground
        // plane 0.3 mm under the lower one (as a real board has — it takes most of each
        // trace's field, so a section without it would badly over-state the mutual), board
        // dielectric around them and air above — a quarter of it in 3D (both symmetry planes
        // are the solver's natural condition), against the term the matrix uses: the broadside
        // section's mutual per length times the mean width.
        const double w = 0.5e-3, h = 0.3e-3, hg = 0.3e-3, t = 35e-6, er = 4.2, arm = 2.5e-3, box = 2.5e-3;
        var stackup = new BoardStackup
        {
            GapThickness = new Dictionary<int, double> { [1] = h, [2] = hg },
            GapPermittivity = new Dictionary<int, double> { [1] = er, [2] = er },
            LayerThickness = new Dictionary<int, double> { [1] = t, [2] = t, [3] = t }
        };
        double term = NetCouplingTerms.CrossingFarads(stackup, 1, 2, w, w, 1, planeBelowLayer: 3);

        double ThreeD(double finest, double growth)
        {
            double top = h / 2 + t, extent = arm + box;
            double groundTop = -top - hg, groundBottom = groundTop - t;
            var xy = EdgeFringeTests.Lines(new[] { 0, w / 2, arm, extent }, new[] { w / 2 }, finest, 0.8e-3, growth);
            var zs = EdgeFringeTests.Lines(new[] { groundBottom - 0.3e-3, groundBottom, groundTop, -top, -h / 2, h / 2, top, top + box },
                new[] { -top, -h / 2, h / 2, top }, finest, 0.8e-3, growth);
            var mesh = StructuredBoxMesh.Build(xy, xy, zs);
            var regions = new int[mesh.ElementCount];
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                var el = mesh.Elements[e];
                var c = (mesh.Nodes[el.N0] + mesh.Nodes[el.N1] + mesh.Nodes[el.N2] + mesh.Nodes[el.N3]) * 0.25;
                regions[e] = c.Y < w / 2 && c.X < arm && c.Z > h / 2 && c.Z < top ? 2       // upper, along x
                    : c.X < w / 2 && c.Y < arm && c.Z < -h / 2 && c.Z > -top ? 3           // lower, along y
                    : c.Z < groundTop && c.Z > groundBottom ? 4                             // the plane below
                    : c.Z < h / 2 && c.Z > groundTop ? 1 : 0;                               // board, air above and below
            }
            mesh = new FeMesh(mesh.Nodes, mesh.Elements, mesh.BoundaryTriangles, regions);
            mesh = EdgeFringeTests.TagConductor(EdgeFringeTests.TagConductor(EdgeFringeTests.TagConductor(mesh, 2, 10), 3, 11), 4, 12);
            var air = new Material { Name = "air", YoungsModulus = 1e9, PoissonRatio = 0.3, Density = 1, ElectricalConductivity = 1e-14, RelativePermittivity = 1 };
            var fr4 = air with { Name = "FR4", RelativePermittivity = er };
            var copper = new Material { Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960, ElectricalConductivity = 5.8e7 };
            var matrix = ElectrostaticSolver.CapacitanceMatrixOf(new SolveInput
            {
                Mesh = mesh, Material = air,
                RegionMaterials = new Dictionary<int, Material> { [0] = air, [1] = fr4, [2] = copper, [3] = copper, [4] = copper },
                BoundaryConditions = new BoundaryCondition[]
                {
                    new VoltagePotential { Name = "upper", FaceIds = new[] { 10 }, Volts = 1 },
                    new VoltagePotential { Name = "lower", FaceIds = new[] { 11 }, Volts = 0 },
                    new VoltagePotential { Name = "plane", FaceIds = new[] { 12 }, Volts = 0 }
                }
            });
            double mutual = 4 * matrix.Mutual(0, 1);
            _output.WriteLine($"3D, finest {finest * 1e6:f0} µm: {mesh.ElementCount} tetrahedra, {mutual * 1e15:f2} fF");
            return mutual;
        }
        double coarse = ThreeD(60e-6, 1.4), fine = ThreeD(35e-6, 1.3);
        double plate = Epsilon0 * er * w * w / h;
        _output.WriteLine($"term {term * 1e15:f2} fF (plate alone {plate * 1e15:f2} fF), 3D {fine * 1e15:f2} fF "
            + $"({term / fine - 1:p1}; coarse {coarse * 1e15:f2} fF)");
        // The term integrates the section's mutual over the lateral offset (the plate part
        // integrates exactly; the fringe reach along the arms comes with it). Measured
        // 2026-10-15: −13.0 % of the 3D value, and the 3D moved −4.8 % toward the term between
        // its meshes (the bare plate would be −71 %); the band allows ±18 %.
        Assert.True(fine < coarse, "the energy bound falls with refinement");
        Assert.True(Math.Abs(term - fine) < 0.18 * fine,
            $"term {term * 1e15:f2} fF against 3D {fine * 1e15:f2} fF");
    }

    // ------------------------------------------------------------------ board-level scan

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    private sealed record BoardParts(PcbBoard Board, BoardStackup Stackup, List<CopperNet> Nets);

    /// <summary>A three-layer board: net A along x on L1, net B along y on L2 (they cross at
    /// the origin), ground plane on L3; optionally a patch of a third net on L2 under the
    /// crossing is replaced by... (the shield case puts B on L3's side instead).</summary>
    private static BoardParts CrossingBoard(bool shielded = false)
    {
        const double w = 0.5e-3;
        var islands = new List<CopperIsland>();
        var traces = new List<TraceCenterline>();
        var nets = new List<CopperNet>();
        void Net(string name, int layer, Point2 from, Point2 to)
        {
            double minX = Math.Min(from.X, to.X) - w / 2, maxX = Math.Max(from.X, to.X) + w / 2;
            double minY = Math.Min(from.Y, to.Y) - w / 2, maxY = Math.Max(from.Y, to.Y) + w / 2;
            var island = new CopperIsland(islands.Count, layer, $"L{layer}", Rect(minX, minY, maxX, maxY));
            islands.Add(island);
            traces.Add(new TraceCenterline(layer, from, to, w));
            nets.Add(new CopperNet(nets.Count + 1, new[] { island }) { Name = name });
        }
        Net("A", 1, new Point2(-10e-3, 0), new Point2(10e-3, 0));
        Net("B", 2, new Point2(0, -10e-3), new Point2(0, 10e-3));
        if (shielded)
        {
            // A patch of a third net between the two layers over the crossing. Three gaps, so
            // B moves down to L3 and the patch sits on L2; the plane goes to L4.
            islands.Clear(); traces.Clear(); nets.Clear();
            Net("A", 1, new Point2(-10e-3, 0), new Point2(10e-3, 0));
            Net("B", 3, new Point2(0, -10e-3), new Point2(0, 10e-3));
            var patch = new CopperIsland(islands.Count, 2, "L2", Rect(-2e-3, -2e-3, 2e-3, 2e-3));
            islands.Add(patch);
            nets.Add(new CopperNet(nets.Count + 1, new[] { patch }) { Name = "SHIELD" });
        }
        int planeLayer = shielded ? 4 : 3;
        var plane = new CopperIsland(islands.Count, planeLayer, $"L{planeLayer}", Rect(-12e-3, -12e-3, 12e-3, 12e-3));
        islands.Add(plane);
        nets.Add(new CopperNet(nets.Count + 1, new[] { plane }) { Name = "GND" });
        var board = new PcbBoard
        {
            Outline = new[] { Rect(-12e-3, -12e-3, 12e-3, 12e-3) }, Islands = islands,
            Pads = Array.Empty<CopperPad>(), Vias = Array.Empty<Via>(), Nets = nets,
            Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>(), TraceCenterlines = traces
        };
        var gaps = Enumerable.Range(1, planeLayer - 1).ToDictionary(g => g, _ => 0.25e-3);
        var stackup = new BoardStackup
        {
            GapThickness = gaps,
            GapPermittivity = gaps.ToDictionary(kv => kv.Key, _ => 4.2),
            GapLossTangent = gaps.ToDictionary(kv => kv.Key, _ => 0.0)
        };
        return new BoardParts(board, stackup, nets);
    }

    [Fact]
    public void TheMatrix_CarriesACrossing_AndDropsItWhenShielded()
    {
        var open = CrossingBoard();
        var matrix = NetCapacitance.Extract(open.Board, open.Nets.Take(2).ToList(), open.Stackup);
        var term = Assert.Single(matrix.Terms);
        Assert.Equal("crossing", term.Kind);
        const double w = 0.5e-3;
        double expected = NetCouplingTerms.CrossingFarads(open.Stackup, 1, 2, w, w, 1, planeBelowLayer: 3);
        _output.WriteLine($"crossing term {term.Farads * 1e15:f2} fF (expected {expected * 1e15:f2} fF)");
        Assert.Equal(expected, term.Farads, 1e-6 * expected);
        Assert.Equal(expected, matrix.Between(0, 1), 1e-6 * expected);
        // The term joins the Maxwell bookkeeping: it is part of each net's total.
        Assert.True(matrix.Farads[0, 0] > matrix.ToPlanes(0));

        var blocked = CrossingBoard(shielded: true);
        var shieldedMatrix = NetCapacitance.Extract(blocked.Board,
            blocked.Nets.Where(n => n.Name is "A" or "B").ToList(), blocked.Stackup);
        Assert.Empty(shieldedMatrix.Terms);
        Assert.Equal(0, shieldedMatrix.Between(0, 1), 1e-20);
    }

    [Fact]
    public void TheMatrix_CarriesViaBarrels_ByTheTwoCylinderForm()
    {
        // Two nets, each a pair of stacked islands joined by one via, the barrels 1 mm apart.
        const double w = 1e-3, drill = 0.3e-3;
        var islands = new List<CopperIsland>();
        var nets = new List<CopperNet>();
        void Net(string name, double x)
        {
            var top = new CopperIsland(islands.Count, 1, "L1", Rect(x - w, -w, x + w, w));
            var bottom = new CopperIsland(islands.Count + 1, 2, "L2", Rect(x - w, -w, x + w, w));
            islands.Add(top);
            islands.Add(bottom);
            var via = new Via(new Point2(x, 0), drill, Plated: true, FromLayer: 1, ToLayer: 2);
            nets.Add(new CopperNet(nets.Count + 1, new[] { top, bottom })
            {
                Name = name, StitchingVias = new[] { new ViaBridge(via, new[] { 1, 2 }) }
            });
        }
        Net("A", 0);
        Net("B", 1e-3);
        var plane = new CopperIsland(islands.Count, 3, "L3", Rect(-4e-3, -4e-3, 8e-3, 4e-3));
        islands.Add(plane);
        nets.Add(new CopperNet(nets.Count + 1, new[] { plane }) { Name = "GND" });
        var board = new PcbBoard
        {
            Outline = new[] { Rect(-4e-3, -4e-3, 8e-3, 4e-3) }, Islands = islands,
            Pads = Array.Empty<CopperPad>(), Vias = Array.Empty<Via>(), Nets = nets,
            Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>(),
            TraceCenterlines = Array.Empty<TraceCenterline>()
        };
        var stackup = new BoardStackup
        {
            GapThickness = new Dictionary<int, double> { [1] = 0.5e-3, [2] = 0.25e-3 },
            GapPermittivity = new Dictionary<int, double> { [1] = 4.2, [2] = 4.2 },
            GapLossTangent = new Dictionary<int, double> { [1] = 0.0, [2] = 0.0 }
        };
        var index = new BoardLayoutIndex(board);
        var terms = NetCouplingTerms.Find(index, stackup, nets.Take(2).ToList(), 2e-3);
        var term = Assert.Single(terms);
        Assert.Equal("via barrels", term.Kind);
        // The two-cylinder form over the barrels' shared depth (one gap plus both coppers).
        double r = drill / 2, distance = 1e-3;
        double length = 0.5e-3 + 2 * stackup.CopperThicknessOf(1);
        double expected = 2 * Math.PI * Epsilon0 * 4.2 * length
            / Math.Acosh((distance * distance - 2 * r * r) / (2 * r * r));
        _output.WriteLine($"via term {term.Farads * 1e15:f3} fF (form {expected * 1e15:f3} fF)");
        Assert.Equal(expected, term.Farads, 1e-9 * expected);
    }
}
