using OpenSim.Core.Geometry2D;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Pdn;
using OpenSim.Solvers;
using OpenSim.Tests.Solvers;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pdn;

/// <summary>
/// The open-edge fringe (FU-32): the cross-section solve that measures it, against the parallel
/// plate it must reduce to inside and against itself on a finer grid; a finite plate pair in the
/// 3D electrostatic solver against the plane model with the fringe on its edges; and a long
/// strip pair, whose resonances must move to the line velocity of its own cross-section.
/// </summary>
public class EdgeFringeTests
{
    private const double Epsilon0 = 8.8541878128e-12;
    private const double C0 = 299792458.0;

    private readonly ITestOutputHelper _output;
    public EdgeFringeTests(ITestOutputHelper output) => _output = output;

    private static readonly SectionLayer[] Stripline =
    {
        new(35e-6, 0.2e-3, 4.0), new(35e-6, 0.1e-3, 4.4), new(35e-6, 0.2e-3, 4.0), new(35e-6, 0, 1)
    };

    [Fact]
    public void TheSection_IsAParallelPlateInside_AndItsFringeConverges()
    {
        // A pair (L2, L3) between two ground planes that run on past the edge: the fringe is a
        // property of the edge alone, so it must not move when the interior widens.
        EdgeFringe Fringe(double halfWidth, double finest = 0.25, double growth = 1.3) => EdgeCrossSection.FringeOf(
            Stripline,
            new[] { new EdgeCrossSection.EdgePlate(1, 0, 0), new EdgeCrossSection.EdgePlate(2, 1, 0) },
            new[] { new EdgeCrossSection.EdgeGround(0, -halfWidth, 3e-3), new EdgeCrossSection.EdgeGround(3, -halfWidth, 3e-3) },
            boardEdgeX: 3e-3, halfWidth, finestFraction: finest, growth: growth);
        var narrow = Fringe(2e-3).Pairs.Single();
        var wide = Fringe(4e-3).Pairs.Single();
        var fine = Fringe(2e-3, 0.125, 1.15).Pairs.Single();
        double interior = Epsilon0 * 4.4 / 0.1e-3;
        _output.WriteLine($"fringe: {narrow.FaradsPerMeter * 1e12:f4} pF/m (air {narrow.AirFaradsPerMeter * 1e12:f4}) at 2 mm, " +
                          $"{wide.FaradsPerMeter * 1e12:f4} at 4 mm, {fine.FaradsPerMeter * 1e12:f4} on the finer grid; " +
                          $"as plate width {narrow.FaradsPerMeter / interior * 1e6:f2} µm");
        Assert.True(narrow.FaradsPerMeter > 0 && narrow.AirFaradsPerMeter > 0);
        Assert.Equal(narrow.FaradsPerMeter, wide.FaradsPerMeter, 1e-4 * narrow.FaradsPerMeter);
        Assert.Equal(narrow.AirFaradsPerMeter, wide.AirFaradsPerMeter, 1e-4 * narrow.AirFaradsPerMeter);
        Assert.Equal(fine.FaradsPerMeter, narrow.FaradsPerMeter, 0.005 * fine.FaradsPerMeter);
        Assert.Equal(fine.AirFaradsPerMeter, narrow.AirFaradsPerMeter, 0.005 * fine.AirFaradsPerMeter);

        // Inside a closed box every field line ends on a conductor: the rows sum to zero.
        var result = EdgeCrossSection.Solve(new SectionGeometry
        {
            Layers = Stripline,
            Copper = new[]
            {
                new SectionCopper(1, -2e-3, 0, 0), new SectionCopper(2, -2e-3, 0, 1),
                new SectionCopper(0, -2e-3, 3e-3, 2), new SectionCopper(3, -2e-3, 3e-3, 2)
            },
            ConductorCount = 3, BoardEdgeX = 3e-3, InteriorX = -2e-3
        });
        for (int i = 0; i < 3; i++)
        {
            double sum = 0;
            for (int j = 0; j < 3; j++) sum += result.Capacitance[i, j];
            Assert.True(Math.Abs(sum) < 1e-9 * result.Capacitance[i, i], $"row {i} sums to {sum:e3}");
        }
    }

    /// <summary>Grid lines through the marks, each step at most <paramref name="growth"/> times
    /// its neighbour, starting at <paramref name="finest"/> at the marks in <paramref name="fine"/>.</summary>
    private static double[] Lines(double[] marks, double[] fine, double finest, double coarsest, double growth)
    {
        var lines = new List<double> { marks[0] };
        for (int s = 0; s + 1 < marks.Length; s++)
        {
            double a = marks[s], b = marks[s + 1];
            double sa = fine.Contains(a) ? finest : coarsest, sb = fine.Contains(b) ? finest : coarsest;
            var steps = new List<double>();
            for (double t = a; t < b;)
            {
                double h = Math.Min(coarsest, Math.Min(sa + (growth - 1) * (t - a), sb + (growth - 1) * (b - t)));
                steps.Add(h);
                t += h;
            }
            double total = steps.Sum();
            if (steps.Count > 1 && total - (b - a) > 0.5 * steps[^1]) { total -= steps[^1]; steps.RemoveAt(steps.Count - 1); }
            double at = a;
            for (int i = 0; i + 1 < steps.Count; i++) { at += steps[i] * (b - a) / total; lines.Add(at); }
            lines.Add(b);
        }
        return lines.ToArray();
    }

    /// <summary>Adds the element faces between a region and every other one to the skin as one
    /// face id, so a voltage condition can name an interior conductor.</summary>
    private static FeMesh TagConductor(FeMesh mesh, int region, int faceId)
    {
        var owners = new Dictionary<(int, int, int), List<int>>();
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            foreach (var (a, b, c) in new[] { (el.N1, el.N2, el.N3), (el.N0, el.N2, el.N3), (el.N0, el.N1, el.N3), (el.N0, el.N1, el.N2) })
            {
                var k = new[] { a, b, c }.OrderBy(x => x).ToArray();
                var key = (k[0], k[1], k[2]);
                if (!owners.TryGetValue(key, out var list)) owners[key] = list = new List<int>();
                list.Add(e);
            }
        }
        var skin = mesh.BoundaryTriangles.ToList();
        foreach (var (key, list) in owners)
            if (list.Count == 2 && (mesh.RegionOf(list[0]) == region) != (mesh.RegionOf(list[1]) == region))
                skin.Add(new BoundaryTriangle(key.Item1, key.Item2, key.Item3, faceId));
        return new FeMesh(mesh.Nodes, mesh.Elements, skin, mesh.ElementRegionIds);
    }

    [Fact]
    public void AFinitePlatePair_InTheElectrostaticSolver_HasThePlaneModelsCapacitanceWithItsFringe()
    {
        // Two 10 × 10 mm plates 0.1 mm thick on a 0.5 mm FR4 core that runs 2 mm past them, in
        // air: a quarter of it meshed in 3D (the two symmetry planes are the solver's natural
        // condition), and the same pair as a plane model whose edges carry the fringe of the
        // section through them. The plane model has no corners; they are the difference.
        const double half = 5e-3, t = 0.1e-3, d = 0.5e-3, er = 4.4, pullback = 2e-3, box = 15e-3;
        var layers = new[] { new SectionLayer(t, d, er), new SectionLayer(t, 0, 1) };
        var fringe = EdgeCrossSection.FringeOf(layers,
            new[] { new EdgeCrossSection.EdgePlate(0, 0, 0), new EdgeCrossSection.EdgePlate(1, 1, 0) },
            Array.Empty<EdgeCrossSection.EdgeGround>(), boardEdgeX: pullback, halfWidth: half, airMargin: box);
        var pair = fringe.Pairs.Single();
        double plate = Epsilon0 * er * (2 * half) * (2 * half) / d;
        double edges = pair.FaradsPerMeter * 8 * half;

        var model = new PlanePair(new PlanePairSpec
        {
            Shape = new[] { PlanePairTests.Rect(-half, -half, half, half) },
            SeparationMeters = d, RelativePermittivity = er, LossTangent = 0, WidebandDielectric = false,
            UpperCopperThicknessMeters = t, LowerCopperThicknessMeters = t,
            Ports = new[] { new PlanePort("P", new Point2(0, 0), 0.5e-3) }, MeshEdgeMeters = 1e-3, Fringe = fringe
        });
        // At 1 MHz: lower, the plane solve's round-off grows as 1/f² (its stiffness dwarfs the
        // capacitance); higher, the plates' spreading inductance shows.
        double f = 1e6;
        double planeModel = (1 / model.Impedance(f)[0, 0]).Imaginary / (2 * Math.PI * f);
        // On its own mesh (whose area is short of the square's by the slivers the mesher drops)
        // the plane model holds its plates' capacitance plus the fringe along its edges.
        double meshed = Epsilon0 * er * model.AreaSquareMeters / d;
        _output.WriteLine($"plates {plate * 1e12:f4} pF ({meshed * 1e12:f4} pF on the mesh) + edges {edges * 1e12:f4} pF " +
                          $"({pair.FaradsPerMeter * 1e12:f3} pF/m) = {(plate + edges) * 1e12:f4} pF; plane model {planeModel * 1e12:f4} pF");
        Assert.Equal(meshed + edges, planeModel, 1e-6 * planeModel);

        double ThreeD(double finest, double growth)
        {
            double top = d / 2 + t;
            var xs = Lines(new[] { 0, half, half + pullback, half + pullback + box }, new[] { half, half + pullback }, finest, 1.5e-3, growth);
            var zs = Lines(new[] { -top - box, -top, -d / 2, d / 2, top, top + box }, new[] { -top, -d / 2, d / 2, top }, finest, 1.5e-3, growth);
            var mesh = StructuredBoxMesh.Build(xs, xs, zs);
            var regions = new int[mesh.ElementCount];
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                var el = mesh.Elements[e];
                var c = (mesh.Nodes[el.N0] + mesh.Nodes[el.N1] + mesh.Nodes[el.N2] + mesh.Nodes[el.N3]) * 0.25;
                bool onPlate = c.X < half && c.Y < half;
                regions[e] = onPlate && c.Z > d / 2 && c.Z < top ? 2
                    : onPlate && c.Z < -d / 2 && c.Z > -top ? 3
                    : c.X < half + pullback && c.Y < half + pullback && Math.Abs(c.Z) < d / 2 ? 1 : 0;
            }
            mesh = new FeMesh(mesh.Nodes, mesh.Elements, mesh.BoundaryTriangles, regions);
            mesh = TagConductor(TagConductor(mesh, 2, 10), 3, 11);
            var air = new Material { Name = "air", YoungsModulus = 1e9, PoissonRatio = 0.3, Density = 1, ElectricalConductivity = 1e-14, RelativePermittivity = 1 };
            var fr4 = air with { Name = "FR4", RelativePermittivity = er };
            var copper = new Material { Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960, ElectricalConductivity = 5.8e7 };
            var matrix = ElectrostaticSolver.CapacitanceMatrixOf(new SolveInput
            {
                Mesh = mesh, Material = air,
                RegionMaterials = new Dictionary<int, Material> { [0] = air, [1] = fr4, [2] = copper, [3] = copper },
                BoundaryConditions = new BoundaryCondition[]
                {
                    new VoltagePotential { Name = "upper", FaceIds = new[] { 10 }, Volts = 1 },
                    new VoltagePotential { Name = "lower", FaceIds = new[] { 11 }, Volts = 0 }
                }
            });
            double c3 = 4 * matrix.Mutual(0, 1);
            _output.WriteLine($"3D, finest {finest * 1e6:f0} µm, growth {growth}: {mesh.ElementCount} tetrahedra, {c3 * 1e12:f4} pF, " +
                              $"edges and corners {(c3 - plate) * 1e12:f4} pF");
            return c3;
        }
        double coarse = ThreeD(50e-6, 1.35), fine = ThreeD(25e-6, 1.25);
        _output.WriteLine($"plane model's edges {edges * 1e12:f4} pF against the 3D solve's {(fine - plate) * 1e12:f4} pF " +
                          $"({(plate + edges) / fine - 1:p2} of the total)");
        Assert.True(fine < coarse);                                  // the energy bound falls with refinement
        // The plane model's edges (no corners) against the 3D solve's edges and corners: within what
        // the 3D solve itself still moved between its two meshes.
        Assert.True(Math.Abs(plate + edges - fine) < Math.Abs(coarse - fine),
            $"plane model {(plate + edges) * 1e12:f4} pF, 3D {fine * 1e12:f4} pF (coarse {coarse * 1e12:f4} pF)");
        Assert.Equal(fine, plate + edges, 0.01 * fine);
    }

    [Fact]
    public void ALongStripPair_ResonatesAtItsSectionsLineVelocity()
    {
        // 100 × 4 mm plates, 0.2 mm FR4, the board's edge 1 mm past the long sides. Along its
        // length the pair is a line with the section's C′ and L′ = µ₀ε₀/C′_air; with the fringe
        // on the long edges (the short ends left open) the plane model must resonate where that
        // line does, f_n = n·c₀·√(C′_air/C′)/2L.
        const double length = 0.100, width = 4e-3, d = 0.2e-3, er = 4.4, t = 35e-6;
        var layers = new[] { new SectionLayer(t, d, er), new SectionLayer(t, 0, 1) };
        var plates = new[] { new EdgeCrossSection.EdgePlate(0, 0, 0), new EdgeCrossSection.EdgePlate(1, 1, 0) };
        var section = EdgeCrossSection.Solve(new SectionGeometry
        {
            Layers = layers, Copper = new[] { new SectionCopper(0, -width / 2, 0, 0), new SectionCopper(1, -width / 2, 0, 1) },
            ConductorCount = 2, BoardEdgeX = 1e-3, InteriorX = -width / 2
        });
        double perMeter = -2 * section.Capacitance[0, 1], airPerMeter = -2 * section.AirCapacitance[0, 1];
        double velocity = C0 * Math.Sqrt(airPerMeter / perMeter);
        var fringe = EdgeCrossSection.FringeOf(layers, plates, Array.Empty<EdgeCrossSection.EdgeGround>(), 1e-3, width / 2);
        _output.WriteLine($"C′ {perMeter * 1e12:f3} pF/m, C′_air {airPerMeter * 1e12:f3} pF/m: εeff {perMeter / airPerMeter:f4} (εr {er}); " +
                          $"fringe {fringe.Pairs[0].FaradsPerMeter * 1e12:f3} pF/m, air {fringe.Pairs[0].AirFaradsPerMeter * 1e12:f3} pF/m");

        PlaneStack Strip(bool withFringe) => new(new PlaneStackSpec
        {
            Layers = new[]
            {
                new StackLayer { Name = "upper", Plates = new[] { new StackPlate("upper", Shape) }, GapBelowMeters = d, GapPermittivity = er, GapLossTangent = 0, CopperThicknessMeters = t },
                new StackLayer { Name = "lower", Plates = new[] { new StackPlate("lower", Shape) }, CopperThicknessMeters = t }
            },
            Ports = new[] { new StackPort(new PlanePort("P", new Point2(0.010, width / 2), 0.3e-3), 0, 1) },
            WidebandDielectric = false, MeshEdgeMeters = 1e-3,
            Fringe = withFringe ? edge => Math.Abs(edge.Outward.Y) > 0.9 ? fringe : null : null
        });
        var fringed = Strip(true).Modes(3);
        var bare = Strip(false).Modes(3);
        for (int n = 1; n <= 3; n++)
        {
            double line = n * velocity / (2 * length);
            _output.WriteLine($"mode {n}: line {line / 1e6:f4} MHz, plane model {fringed[n - 1].FrequencyHz / 1e6:f4} MHz " +
                              $"({fringed[n - 1].FrequencyHz / line - 1:e2}), bare edges {bare[n - 1].FrequencyHz / 1e6:f4} MHz ({bare[n - 1].FrequencyHz / line - 1:p2})");
            Assert.Equal(line, fringed[n - 1].FrequencyHz, 1e-4 * line);
            Assert.True(Math.Abs(bare[n - 1].FrequencyHz / line - 1) > 0.01);
        }
    }

    private static readonly IReadOnlyList<Polygon2> Shape = new[] { PlanePairTests.Rect(0, 0, 0.100, 4e-3) };
}
