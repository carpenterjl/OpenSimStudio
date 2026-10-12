using OpenSim.Core.Geometry2D;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using OpenSim.Pcb.Extrude;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Thermal;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Feature 5: parts as two-resistor models on a whole-board thermal mesh. The references
/// are a rod (the network in closed form, exact for linear elements) and a convecting
/// disc plate (the thin-plate fin equation, Bessel functions by quadrature of their
/// integral definitions).
/// </summary>
public class ComponentThermalTests
{
    private readonly ITestOutputHelper _out;
    public ComponentThermalTests(ITestOutputHelper output) => _out = output;

    private static Material Solid(string name, double k) => new()
    {
        Name = name, YoungsModulus = 1e9, PoissonRatio = 0.3, Density = 2000,
        ThermalConductivity = k, SpecificHeat = 900
    };

    private static readonly Material Copper = Solid("Copper", 390) with { Density = 8960, SpecificHeat = 385 };
    private static readonly Material Fr4 = Solid("FR4", 0.3) with { Density = 1850, SpecificHeat = 1100 };

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) },
            Array.Empty<IReadOnlyList<Point2>>());

    /// <summary>A regular polygon with the area of the circle it stands for.</summary>
    private static Polygon2 Disc(double cx, double cy, double radius, int sides)
    {
        double r = radius * Math.Sqrt(2 * Math.PI / (sides * Math.Sin(2 * Math.PI / sides)));
        return new Polygon2(Enumerable.Range(0, sides).Select(i =>
                new Point2(cx + r * Math.Cos(2 * Math.PI * i / sides), cy + r * Math.Sin(2 * Math.PI * i / sides))).ToArray(),
            Array.Empty<IReadOnlyList<Point2>>());
    }

    private static PcbBoard Board(Polygon2 outline, IReadOnlyList<CopperIsland>? islands = null,
        IReadOnlyList<Via>? vias = null, IReadOnlyList<CopperPad>? pads = null) => new()
    {
        Outline = new[] { outline },
        Islands = islands ?? Array.Empty<CopperIsland>(),
        Pads = pads ?? Array.Empty<CopperPad>(),
        Vias = vias ?? Array.Empty<Via>(),
        Nets = Array.Empty<CopperNet>(),
        Layers = Array.Empty<BoardLayer>(),
        Warnings = Array.Empty<string>()
    };

    private static BoardThermalOptions Options(Material laminate, double edge = 0, double board = 1.6e-3,
        double copper = 35e-6) => new()
    {
        Stackup = new PcbStackupSettings { BoardThickness = board, CopperThickness = copper },
        Copper = Copper, Laminate = laminate, TargetEdgeLength = edge, SurfaceEmissivity = null
    };

    private static Convection Film(double h, double ambient, params int[] faces) =>
        new() { Name = "film", FaceIds = faces, Coefficient = h, AmbientTemperature = ambient };

    /// <summary>h·∫(T − T_a) dA over the given faces.</summary>
    private static double FilmLoss(FeMesh mesh, IReadOnlyList<double> temperature, double h, double ambient, params int[] faces)
    {
        double loss = 0;
        foreach (var t in mesh.GetFaceTriangles(faces))
        {
            double area = 0.5 * Vector3D.Cross(mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length;
            loss += h * area * ((temperature[t.A] + temperature[t.B] + temperature[t.C]) / 3 - ambient);
        }
        return loss;
    }

    private static IReadOnlyList<double> Temperature(ComponentThermalReport report) =>
        ((NodalScalarField)report.Fields.First(f => f.Name == "Temperature")).Values;

    // ---------------- The network on a rod ----------------

    private static BoardThermalMesh Rod(double side, double length, Material material)
    {
        var footprint = Rect(0, 0, side, side);
        var planar = new PlanarMesher().Mesh(new[] { new PlanarRegion(PcbStackup.CopperRegion, new[] { footprint }) }, side / 4);
        var mesh = new PcbMeshGenerator().GenerateCopperOnly(planar, length, new[] { footprint });
        return new BoardThermalMesh
        {
            Mesh = mesh,
            RegionMaterials = new Dictionary<int, Material> { [0] = material },
            Laminate = material,
            ComponentFaceIds = new[] { PcbMeshGenerator.PadFaceBase },
            ComponentContactArea = new[] { side * side }
        };
    }

    [Fact]
    public void Rod_TwoResistorNetwork_MatchesClosedForm()
    {
        const double side = 4e-3, length = 20e-3, k = 10, ambient = 300, sink = 320;
        const double power = 2, thetaJc = 5, thetaJb = 10, caseToAmbient = 40;
        var rod = Rod(side, length, Solid("bar", k));
        var part = new ThermalComponent
        {
            RefDes = "U1", Footprint = Rect(0, 0, side, side), PowerWatts = power,
            ThetaJc = thetaJc, ThetaJb = thetaJb, CaseToAmbient = caseToAmbient
        };
        var report = ComponentThermalAnalysis.Solve(rod, new ComponentThermalSetup
        {
            Components = new[] { part }, AmbientKelvin = ambient,
            BoardConditions = new BoundaryCondition[] { new FixedTemperature { Name = "sink", FaceIds = new[] { 1 }, Kelvin = sink } }
        });

        double bar = length / (k * side * side), top = thetaJc + caseToAmbient;
        double toBoard = (ambient + power * top - sink) / (thetaJb + top + bar);
        double board = sink + toBoard * bar, junction = board + thetaJb * toBoard;
        double @case = junction - thetaJc * (power - toBoard);
        var c = report.Components.Single();
        _out.WriteLine($"to board {c.ToBoardWatts:g9} (exact {toBoard:g9}), Tj {c.JunctionKelvin:g9} ({junction:g9}), case {c.CaseKelvin:g9} ({@case:g9})");
        Assert.Equal(toBoard, c.ToBoardWatts, 6);
        Assert.Equal(board, c.BoardKelvin, 5);
        Assert.Equal(junction, c.JunctionKelvin, 5);
        Assert.Equal(@case, c.CaseKelvin, 5);
        Assert.Equal(power, c.ToBoardWatts + c.FromTopWatts, 12);
        // The case top is where the heatsink resistance says it is.
        Assert.Equal(ambient + c.FromTopWatts * caseToAmbient, c.CaseKelvin, 5);
    }

    [Fact]
    public void Rod_NoTopPath_AllPowerGoesDown()
    {
        const double side = 4e-3, length = 20e-3, k = 10;
        var rod = Rod(side, length, Solid("bar", k));
        var part = new ThermalComponent
        {
            RefDes = "Q1", Footprint = Rect(0, 0, side, side), PowerWatts = 0.5,
            ThetaJc = 3, ThetaJb = 12, CaseToAmbient = double.PositiveInfinity
        };
        var report = ComponentThermalAnalysis.Solve(rod, new ComponentThermalSetup
        {
            Components = new[] { part }, AmbientKelvin = 300,
            BoardConditions = new BoundaryCondition[] { new FixedTemperature { Name = "sink", FaceIds = new[] { 1 }, Kelvin = 310 } }
        });
        var c = report.Components.Single();
        double bar = length / (k * side * side);
        Assert.Equal(0.5, c.ToBoardWatts, 12);
        Assert.Equal(310 + 0.5 * (bar + 12), c.JunctionKelvin, 5);
        Assert.Equal(c.JunctionKelvin, c.CaseKelvin, 9);   // no flow through θJC
    }

    // ---------------- A part on a convecting disc ----------------

    private static double Integrate(Func<double, double> f, double a, double b, int n)
    {
        double h = (b - a) / n, sum = f(a) + f(b);
        for (int i = 1; i < n; i++) sum += (i % 2 == 0 ? 2 : 4) * f(a + i * h);
        return sum * h / 3;
    }

    private static double I0(double x) => Integrate(t => Math.Exp(x * Math.Cos(t)), 0, Math.PI, 2000) / Math.PI;
    private static double I1(double x) => Integrate(t => Math.Exp(x * Math.Cos(t)) * Math.Cos(t), 0, Math.PI, 2000) / Math.PI;
    private static double K0(double x) => Integrate(t => Math.Exp(-x * Math.Cosh(t)), 0, 16, 40000);
    private static double K1(double x) => Integrate(t => Math.Exp(-x * Math.Cosh(t)) * Math.Cosh(t), 0, 16, 40000);

    /// <summary>
    /// Thin disc plate of radius R, film h on both faces except under a central disc of
    /// radius a on top, which takes a uniform flux instead; adiabatic rim. Returns the
    /// mean rise over the heated disc.
    /// </summary>
    private static double DiscMeanRise(double power, double a, double radius, double kt, double h)
    {
        double q = power / (Math.PI * a * a);
        double m1 = Math.Sqrt(h / kt), m2 = Math.Sqrt(2 * h / kt);
        // Inner: q/h + A·I0(m1 r). Outer: B·K0(m2 r) + C·I0(m2 r), zero slope at the rim.
        // Unknowns A, B, C.
        var matrix = new double[3, 3]
        {
            { I0(m1 * a), -K0(m2 * a), -I0(m2 * a) },
            { m1 * I1(m1 * a), m2 * K1(m2 * a), -m2 * I1(m2 * a) },
            { 0, -K1(m2 * radius), I1(m2 * radius) }
        };
        var rhs = new[] { -q / h, 0.0, 0.0 };
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 3; r++) if (Math.Abs(matrix[r, col]) > Math.Abs(matrix[pivot, col])) pivot = r;
            for (int c = 0; c < 3; c++) (matrix[col, c], matrix[pivot, c]) = (matrix[pivot, c], matrix[col, c]);
            (rhs[col], rhs[pivot]) = (rhs[pivot], rhs[col]);
            for (int r = col + 1; r < 3; r++)
            {
                double f = matrix[r, col] / matrix[col, col];
                for (int c = col; c < 3; c++) matrix[r, c] -= f * matrix[col, c];
                rhs[r] -= f * rhs[col];
            }
        }
        var x = new double[3];
        for (int r = 2; r >= 0; r--)
        {
            double s = rhs[r];
            for (int c = r + 1; c < 3; c++) s -= matrix[r, c] * x[c];
            x[r] = s / matrix[r, r];
        }
        return q / h + x[0] * 2 * I1(m1 * a) / (m1 * a);
    }

    [Theory]
    [InlineData(1.5e-3)]
    [InlineData(0.9e-3)]
    public void DiscPlate_HeatedPatch_MatchesThinPlateSolution(double edge)
    {
        const double radius = 30e-3, a = 4e-3, h = 12, ambient = 300, power = 1, k = 200, thetaJb = 7;
        var footprint = Disc(0, 0, a, 48);
        var part = new ThermalComponent
        {
            RefDes = "U1", Footprint = footprint, PowerWatts = power, ThetaJc = 2, ThetaJb = thetaJb,
            CaseToAmbient = double.PositiveInfinity
        };
        var options = Options(Solid("plate", k), edge);
        var mesh = BoardThermalMesher.Mesh(Board(Disc(0, 0, radius, 96)), new[] { part }, options);
        var report = ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
        {
            Components = new[] { part }, AmbientKelvin = ambient,
            BoardConditions = new BoundaryCondition[] { Film(h, ambient, 0, 1) }
        });
        var c = report.Components.Single();

        double thickness = options.Stackup.BoardThickness + options.Stackup.CopperThickness;
        double rise = DiscMeanRise(power, a, radius, k * thickness, h);
        double measured = c.BoardKelvin - ambient;
        _out.WriteLine($"edge {edge * 1e3} mm: board rise {measured:f4} K, thin plate {rise:f4} K ({(measured / rise - 1) * 100:+0.00;-0.00} %), " +
                       $"{mesh.Mesh.ElementCount} elements, contact {c.ContactArea * 1e6:f3} mm²");
        Assert.InRange(measured / rise, 0.99, 1.01);
        Assert.Equal(c.BoardKelvin + thetaJb * power, c.JunctionKelvin, 9);
        Assert.InRange(c.ContactArea / (Math.PI * a * a), 1 - 1e-4, 1 + 1e-4);

        // What went in comes out through the film.
        double loss = FilmLoss(mesh.Mesh, Temperature(report), h, ambient, 0, 1);
        Assert.Equal(power, loss, 5);
    }

    [Fact]
    public void DiscPlate_PowerSplitsBetweenBoardAndTop_AndBalances()
    {
        const double radius = 30e-3, a = 4e-3, h = 12, ambient = 300, power = 1.5;
        var footprint = Disc(0, 0, a, 48);
        ThermalComponent Part(double thetaJb) => new()
        {
            RefDes = "U1", Footprint = footprint, PowerWatts = power, ThetaJc = 3, ThetaJb = thetaJb,
            CaseFilmCoefficient = 15
        };
        var options = Options(Solid("plate", 200), 1.5e-3);
        var mesh = BoardThermalMesher.Mesh(Board(Disc(0, 0, radius, 96)), new[] { Part(8) }, options);
        ComponentThermalReport Run(double thetaJb) => ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
        {
            Components = new[] { Part(thetaJb) }, AmbientKelvin = ambient,
            BoardConditions = new BoundaryCondition[] { Film(h, ambient, 0, 1) }
        });

        var report = Run(8);
        var c = report.Components.Single();
        double caseToAir = 1 / (15 * footprint.Area());
        _out.WriteLine(string.Join("\n", report.Describe()));
        Assert.Equal(power, c.ToBoardWatts + c.FromTopWatts, 12);
        Assert.Equal(ambient + c.FromTopWatts * caseToAir, c.CaseKelvin, 6);
        Assert.Equal(c.CaseKelvin + 3 * c.FromTopWatts, c.JunctionKelvin, 9);
        Assert.Equal(c.ToBoardWatts, FilmLoss(mesh.Mesh, Temperature(report), h, ambient, 0, 1), 5);
        Assert.Equal(c.ToBoardWatts, report.IntoBoardWatts, 12);
        Assert.True(c.ToBoardWatts > c.FromTopWatts, "a part on a metal plate sends most of its heat down");

        // The board resistance seen by this part, from one run, predicts another run
        // (the board is linear): Q_b = (T_a + P·R_top − T_a) / (θJB + R_top + R_board). Since
        // FU-26 the junction is one isothermal node over a uniform film 1/(θJB·A), so the
        // footprint's flux pattern — and with it the resistance the mean board temperature
        // defines — moves a little with θJB: measured 3.6e-7 of Q_b between θJB = 8 and 25
        // on this near-isothermal patch (it was exact with the distributed source).
        double top = 3 + caseToAir;
        double boardResistance = (c.BoardKelvin - ambient) / c.ToBoardWatts;
        var other = Run(25).Components.Single();
        double predicted = power * top / (25 + top + boardResistance);
        Assert.Equal(predicted, other.ToBoardWatts, predicted * 1e-6);

        // Cut off from the board, the junction sits on the top path alone.
        var isolated = Run(1e9).Components.Single();
        Assert.InRange((isolated.JunctionKelvin - ambient) / (power * top), 1 - 1e-5, 1 + 1e-5);
    }

    // ---------------- Copper and vias in the board ----------------

    [Fact]
    public void Copper_IsHeldLayerByLayer()
    {
        var outline = Rect(0, 0, 40e-3, 30e-3);
        var islands = new[]
        {
            new CopperIsland(0, 1, "L1", Rect(0, 0, 20e-3, 30e-3)),        // half the top
            new CopperIsland(1, 2, "L2", Rect(-1e-3, -1e-3, 41e-3, 31e-3)) // a full plane
        };
        var mesh = BoardThermalMesher.Mesh(Board(outline, islands), Array.Empty<ThermalComponent>(), Options(Fr4, 1.0e-3));
        _out.WriteLine(string.Join("\n", mesh.Notes));
        Assert.Equal(2, mesh.LayerCoverage.Count);
        Assert.InRange(mesh.LayerCoverage[0], 0.495, 0.505);
        Assert.Equal(1.0, mesh.LayerCoverage[1], 9);
        double exact = (0.5 + 1.0) * 40e-3 * 30e-3 * 35e-6;
        Assert.InRange(mesh.CopperVolume / exact, 0.995, 1.005);

        // A fully covered element of a copper layer conducts as copper; the gap as laminate.
        var full = mesh.RegionMaterials[BoardThermalMesher.CopperLayerRegionBase + BoardThermalMesher.FractionSteps];
        Assert.Equal(390, full.ThermalConductivity!.Value, 9);
        Assert.Equal(0.3, mesh.RegionMaterials[0].ThermalConductivity!.Value, 12);

        // Copper actually in the elements, by region: the same volume.
        double inElements = 0;
        for (int e = 0; e < mesh.Mesh.ElementCount; e++)
        {
            int region = mesh.Mesh.RegionOf(e);
            if (region < BoardThermalMesher.CopperLayerRegionBase) continue;
            inElements += mesh.Mesh.ElementVolume(e) * (region - BoardThermalMesher.CopperLayerRegionBase)
                          / (double)BoardThermalMesher.FractionSteps;
        }
        Assert.InRange(inElements / mesh.CopperVolume, 0.999, 1.001);
    }

    [Fact]
    public void Vias_CarryHeatThroughTheBoard()
    {
        const double side = 10e-3, gap = 1.0e-3, copper = 70e-6, plating = 25e-6, drill = 0.3e-3;
        var outline = Rect(0, 0, side, side);
        var plane = Rect(-1e-3, -1e-3, side + 1e-3, side + 1e-3);
        var islands = new[] { new CopperIsland(0, 1, "L1", plane), new CopperIsland(1, 2, "L2", plane) };
        var vias = new List<Via>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                vias.Add(new Via(new Point2(1e-3 + 2e-3 * i, 1e-3 + 2e-3 * j), drill, true));
        var part = new ThermalComponent
        {
            RefDes = "U1", Footprint = outline, PowerWatts = 1, ThetaJb = 0, ThetaJc = 0,
            CaseToAmbient = double.PositiveInfinity
        };

        double Rise(IReadOnlyList<Via> withVias)
        {
            var options = Options(Fr4, 0.5e-3, board: gap, copper: copper);
            var mesh = BoardThermalMesher.Mesh(Board(outline, islands, withVias), new[] { part }, options);
            var report = ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
            {
                Components = new[] { part }, AmbientKelvin = 300,
                BoardConditions = new BoundaryCondition[] { new FixedTemperature { Name = "plate", FaceIds = new[] { 1 }, Kelvin = 300 } }
            });
            return report.Components.Single().BoardKelvin - 300;
        }

        double area = side * side;
        double sheets = 2 * copper / (390 * area);
        double bare = sheets + gap / (0.3 * area);
        double barrels = vias.Count * Math.PI * drill * plating;
        double stitched = sheets + gap / (0.3 * area + (390 - 0.3) * barrels);
        double measuredBare = Rise(Array.Empty<Via>()), measured = Rise(vias);
        _out.WriteLine($"through the board: bare {measuredBare:f4} K/W (exact {bare:f4}); 25 vias {measured:f4} K/W " +
                       $"(barrels in parallel with isothermal faces {stitched:f4}, {(measured / stitched - 1) * 100:+0.0;-0.0} %)");
        Assert.Equal(bare, measuredBare, 6);
        // Isothermal faces are the lower bound: the 70 µm sheets have to gather the heat
        // into the barrels' elements, which adds a little.
        Assert.InRange(measured / stitched, 1.0, 1.08);
        Assert.True(measured < 0.2 * measuredBare, "25 vias should cut the through-board resistance several times");
    }

    // ---------------- Small parts, placement ----------------

    [Fact]
    public void SmallFootprint_IsResolved_AndTheAnswerHoldsUnderRefinement()
    {
        var outline = Rect(0, 0, 60e-3, 40e-3);
        var islands = new[] { new CopperIsland(0, 2, "L2", Rect(-1e-3, -1e-3, 61e-3, 41e-3)) };
        var part = new ThermalComponent
        {
            RefDes = "U3", Footprint = Rect(29e-3, 19e-3, 31e-3, 21e-3), PowerWatts = 0.4,
            ThetaJc = 20, ThetaJb = 15, CaseFilmCoefficient = 12
        };
        (double Junction, int Triangles, double Contact) Run(double edge)
        {
            var mesh = BoardThermalMesher.Mesh(Board(outline, islands), new[] { part }, Options(Fr4, edge));
            int onPatch = mesh.Mesh.BoundaryTriangles.Count(t => t.FaceId == mesh.ComponentFaceIds[0]);
            var report = ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
            {
                Components = new[] { part }, AmbientKelvin = 300,
                BoardConditions = new BoundaryCondition[] { Film(10, 300, 0, 1) }
            });
            return (report.Components.Single().JunctionKelvin, onPatch, mesh.ComponentContactArea[0]);
        }
        var coarse = Run(2.0e-3);
        var fine = Run(1.0e-3);
        _out.WriteLine($"2 mm: Tj rise {coarse.Junction - 300:f3} K on {coarse.Triangles} triangles; " +
                       $"1 mm: {fine.Junction - 300:f3} K on {fine.Triangles} triangles");
        Assert.InRange(coarse.Contact / 4e-6, 1 - 1e-4, 1 + 1e-4);   // outline cleaning moves it by micrometres
        Assert.True(coarse.Triangles >= 16, $"a 2 mm part on 2 mm elements must be refined locally (got {coarse.Triangles})");
        Assert.InRange((coarse.Junction - 300) / (fine.Junction - 300), 0.97, 1.03);
    }

    [Fact]
    public void OffBoardPart_IsNamed_AndLeftOut()
    {
        var parts = new[]
        {
            new ThermalComponent { RefDes = "U1", Footprint = Rect(5e-3, 5e-3, 10e-3, 10e-3), PowerWatts = 0.2, ThetaJb = 10, ThetaJc = 5 },
            new ThermalComponent { RefDes = "U9", Footprint = Rect(50e-3, 50e-3, 55e-3, 55e-3), PowerWatts = 0.2, ThetaJb = 10, ThetaJc = 5 }
        };
        var mesh = BoardThermalMesher.Mesh(Board(Rect(0, 0, 20e-3, 20e-3)), parts, Options(Fr4, 1.5e-3));
        Assert.Equal(-1, mesh.ComponentFaceIds[1]);
        Assert.Contains(mesh.Notes, n => n.Contains("U9"));
        var report = ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
        {
            Components = parts, AmbientKelvin = 300,
            BoardConditions = new BoundaryCondition[] { Film(10, 300, 0, 1) }
        });
        Assert.Equal("U1", report.Components.Single().RefDes);
    }

    [Fact]
    public void BottomSidePart_SitsOnTheBottomFace()
    {
        var part = new ThermalComponent
        {
            RefDes = "Q2", OnTop = false, Footprint = Rect(8e-3, 8e-3, 12e-3, 12e-3), PowerWatts = 0.3,
            ThetaJb = 5, ThetaJc = 5, CaseToAmbient = double.PositiveInfinity
        };
        var mesh = BoardThermalMesher.Mesh(Board(Rect(0, 0, 20e-3, 20e-3)), new[] { part }, Options(Fr4, 1.0e-3));
        var patch = mesh.Mesh.GetFaceTriangles(new[] { mesh.ComponentFaceIds[0] });
        Assert.NotEmpty(patch);
        Assert.All(patch, t => Assert.Equal(0.0, mesh.Mesh.Nodes[t.A].Z, 12));
        var report = ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
        {
            Components = new[] { part }, AmbientKelvin = 300,
            BoardConditions = new BoundaryCondition[] { Film(10, 300, 0, 1) }
        });
        // Hottest board is on the side the part is on.
        var temperature = Temperature(report);
        int hottest = Enumerable.Range(0, temperature.Count).MaxBy(i => temperature[i]);
        Assert.Equal(0.0, mesh.Mesh.Nodes[hottest].Z, 12);
    }

    [Fact]
    public void Placement_FromPadNames()
    {
        CopperPad Pad(int layer, double x, double y, string refDes, string pin) =>
            new(layer, new Point2(x, y), Rect(x - 0.3e-3, y - 0.2e-3, x + 0.3e-3, y + 0.2e-3), 0.6e-3)
            { ComponentRef = refDes, Pin = pin, PartName = refDes == "U1" ? "LM1117" : null };
        var islands = new[]
        {
            new CopperIsland(0, 1, "L1", Rect(0, 0, 1e-3, 1e-3)), new CopperIsland(1, 2, "L2", Rect(0, 0, 1e-3, 1e-3))
        };
        var pads = new[]
        {
            Pad(1, 10e-3, 10e-3, "U1", "1"), Pad(1, 14e-3, 10e-3, "U1", "2"), Pad(1, 10e-3, 13e-3, "U1", "3"),
            Pad(1, 14e-3, 13e-3, "U1", "4"),
            Pad(2, 20e-3, 5e-3, "R5", "1"), Pad(2, 22e-3, 5e-3, "R5", "2"),
            new CopperPad(1, new Point2(1e-3, 1e-3), Rect(0.8e-3, 0.8e-3, 1.2e-3, 1.2e-3), 0.4e-3)   // no name
        };
        var parts = ComponentPlacement.FromPads(Board(Rect(0, 0, 30e-3, 20e-3), islands, pads: pads));
        Assert.Equal(new[] { "R5", "U1" }, parts.Select(p => p.RefDes));
        var u1 = parts[1];
        Assert.True(u1.OnTop);
        Assert.Equal("LM1117", u1.Part);
        Assert.Equal(12e-3, u1.Center.X, 12);
        Assert.Equal(11.5e-3, u1.Center.Y, 12);
        Assert.Equal(4.6e-3 * 3.4e-3, u1.PadExtent!.Area(), 12);
        Assert.False(parts[0].OnTop);
        var component = u1.ToComponent(1.0, 5, 10);
        Assert.Equal(u1.PadExtent, component.Footprint);
    }

    [Fact]
    public void Placement_FromFiles()
    {
        const string kicad = """
            ### Footprint positions - created on ...
            ## Unit = mm, Angle = deg.
            # Ref     Val       Package         PosX       PosY       Rot  Side
            C1        100n      C_0603       10.5000   -20.2500   90.0000  top
            U2        MCU       QFN-32       30.0000   -15.0000    0.0000  bottom
            ## End
            """;
        var a = ComponentPlacement.ReadPlacementFile(kicad, out var notes);
        Assert.Equal(2, a.Count);
        Assert.Equal("C1", a[0].RefDes);
        Assert.Equal(10.5e-3, a[0].Center.X, 12);
        Assert.Equal(-20.25e-3, a[0].Center.Y, 12);
        Assert.Equal(90, a[0].RotationDegrees);
        Assert.True(a[0].OnTop);
        Assert.False(a[1].OnTop);
        Assert.Equal("100n", a[0].Part);
        Assert.NotEmpty(notes);

        const string csv = "Designator,Comment,Layer,Mid X(mil),Mid Y(mil),Rotation\n" +
                           "\"U1\",\"Buck, 3A\",TopLayer,1000,500,180\n" +
                           "\"Q3\",\"FET\",BottomLayer,2000mil,12.7mm,0\n";
        var b = ComponentPlacement.ReadPlacementFile(csv, out _);
        Assert.Equal(2, b.Count);
        Assert.Equal(25.4e-3, b[0].Center.X, 12);
        Assert.Equal(12.7e-3, b[0].Center.Y, 12);
        Assert.Equal("Buck, 3A", b[0].Part);
        Assert.False(b[1].OnTop);
        Assert.Equal(50.8e-3, b[1].Center.X, 12);
        Assert.Equal(12.7e-3, b[1].Center.Y, 12);

        // A centre alone is not a footprint.
        Assert.Throws<InvalidOperationException>(() => b[0].ToComponent(1, 2, 3));
        var sized = b[0].ToComponent(1, 2, 3, 5e-3, 6e-3);
        Assert.Equal(30e-6, sized.Footprint.Area(), 12);

        Assert.Throws<InvalidOperationException>(() => ComponentPlacement.ReadPlacementFile("nothing here\n1 2 3\n", out _));
    }

    // ---------------- FU-26: networks — the enclosure's air, compact models, time ----------------

    private static EnvironmentSettings StillAir(double ambient) => new() { AmbientTemperature = ambient, IncludeRadiation = false };

    /// <summary>A 50 mm board with a copper plane on L2 and two powered parts, under still air.
    /// (A steady solve of a thin-copper board mesh costs a second or two of CG per Picard
    /// iterate — the copper/laminate contrast — so these tests keep the mesh coarse.)</summary>
    private static (BoardThermalMesh Mesh, ThermalComponent[] Parts) TwoPartBoard(double edge = 2.5e-3,
        double junctionCapacity = 0, double caseCapacity = 0, PowerProfile? profile = null)
    {
        var outline = Rect(0, 0, 50e-3, 50e-3);
        var islands = new[] { new CopperIsland(0, 2, "L2", Rect(-1e-3, -1e-3, 51e-3, 51e-3)) };
        var parts = new[]
        {
            new ThermalComponent
            {
                RefDes = "U1", Footprint = Rect(12e-3, 20e-3, 20e-3, 28e-3), PowerWatts = 1.0, ThetaJc = 6, ThetaJb = 9,
                CaseFilmCoefficient = 12, JunctionCapacity = junctionCapacity, CaseCapacity = caseCapacity, PowerProfile = profile
            },
            new ThermalComponent
            {
                RefDes = "Q2", Footprint = Rect(30e-3, 22e-3, 36e-3, 28e-3), PowerWatts = 0.4, ThetaJc = 4, ThetaJb = 20,
                CaseFilmCoefficient = 12, JunctionCapacity = junctionCapacity, CaseCapacity = caseCapacity
            }
        };
        return (BoardThermalMesher.Mesh(Board(outline, islands), parts, Options(Fr4, edge)), parts);
    }

    [Fact]
    public void EnclosureAir_WarmsByWhatThePartsAndTheBoardGiveIt()
    {
        const double ambient = 300, vent = 0.25;
        var (mesh, parts) = TwoPartBoard();
        ComponentThermalReport Run(EnclosureAir? air) => ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
        {
            Components = parts, Environment = StillAir(ambient), Air = air
        });
        var open = Run(null);
        var enclosed = Run(new EnclosureAir { ConductanceToAmbient = vent });
        var topsOnly = Run(new EnclosureAir { ConductanceToAmbient = vent, CoolsTheBoard = false });
        _out.WriteLine(string.Join("\n", enclosed.Describe()));
        _out.WriteLine(string.Join("\n", topsOnly.Describe()));
        _out.WriteLine(string.Join("\n", enclosed.Log));

        // Everything the parts dissipate ends in the air and leaves through the vent.
        double power = parts.Sum(p => p.PowerWatts);
        Assert.Null(open.AirKelvin);
        Assert.Equal(power / vent, enclosed.AirKelvin!.Value - ambient, power / vent * 1e-5);
        // With the board on the outside air, only the case tops warm the enclosure.
        double fromTops = topsOnly.Components.Sum(c => c.FromTopWatts);
        Assert.Equal(fromTops / vent, topsOnly.AirKelvin!.Value - ambient, fromTops / vent * 1e-5);
        Assert.True(fromTops > 0 && fromTops < power);
        // Every junction is hotter in the enclosure, by about the air's rise when the board breathes it.
        foreach (var part in open.Components)
        {
            double inside = enclosed.Components.First(c => c.RefDes == part.RefDes).JunctionKelvin;
            double tops = topsOnly.Components.First(c => c.RefDes == part.RefDes).JunctionKelvin;
            Assert.InRange(inside - part.JunctionKelvin, 0.7 * (power / vent), 1.3 * (power / vent));
            Assert.True(tops > part.JunctionKelvin && tops < inside);
        }
        Assert.Contains(enclosed.Assumptions, a => a.Contains("ENCLOSURE AIR"));
        Assert.Contains(enclosed.Log, l => l.Contains("drives toward 'air'"));

        // The air needs an environment to cool the board through, and a conductance.
        Assert.Throws<InvalidOperationException>(() => ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
        {
            Components = parts, AmbientKelvin = ambient, BoardConditions = new BoundaryCondition[] { Film(10, ambient, 0, 1) },
            Air = new EnclosureAir { ConductanceToAmbient = vent }
        }));
        Assert.Throws<InvalidOperationException>(() => Run(new EnclosureAir { ConductanceToAmbient = 0 }));
    }

    [Fact]
    public void CompactModel_IsTheVendorsNetwork()
    {
        // A compact model wired like the two resistors gives the two-resistor answer; two board
        // nodes in parallel are one; two exposed nodes at one temperature are one with the summed area.
        var (mesh, parts) = TwoPartBoard();
        var u1 = parts[0];
        double caseArea = u1.CaseArea;
        // A given film on the board (no environment iteration): the identities below are the
        // network's, not the cooling's.
        ComponentTemperature Run(ThermalComponent part) => ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
        {
            Components = new[] { part, parts[1] }, AmbientKelvin = 300,
            BoardConditions = new BoundaryCondition[] { Film(10, 300, 0, 1) }
        }).Components.First(c => c.RefDes == "U1");

        var twoResistor = Run(u1);
        var sameAsTwoResistor = Run(u1 with
        {
            ThetaJc = 0, ThetaJb = 0,
            CompactModel = new CompactThermalModel
            {
                Source = "the two resistors as a model",
                Nodes = new[]
                {
                    new CompactThermalNode("junction", CompactNodeRole.Junction),
                    new CompactThermalNode("top", CompactNodeRole.Exposed),
                    new CompactThermalNode("bottom", CompactNodeRole.Board)
                },
                Resistors = new[] { new CompactThermalResistor("junction", "top", 6), new CompactThermalResistor("junction", "bottom", 9) }
            }
        });
        _out.WriteLine($"two resistors: Tj {twoResistor.JunctionKelvin:f4}; as a compact model: {sameAsTwoResistor.JunctionKelvin:f4}");
        Assert.Equal(twoResistor.JunctionKelvin, sameAsTwoResistor.JunctionKelvin, 1e-6);
        Assert.Equal(twoResistor.CaseKelvin, sameAsTwoResistor.CaseKelvin, 1e-6);
        Assert.Equal(twoResistor.ToBoardWatts, sameAsTwoResistor.ToBoardWatts, 1e-7);
        Assert.Equal(twoResistor.FromTopWatts, sameAsTwoResistor.FromTopWatts, 1e-7);
        Assert.Equal(new[] { "junction", "top", "bottom" }, sameAsTwoResistor.Nodes.Select(n => n.Node));

        var split = Run(u1 with
        {
            ThetaJc = 0, ThetaJb = 0,
            CompactModel = new CompactThermalModel
            {
                Nodes = new[]
                {
                    new CompactThermalNode("junction", CompactNodeRole.Junction),
                    new CompactThermalNode("top", CompactNodeRole.Exposed) { ExposedAreaSquareMeters = 0.5 * caseArea },
                    new CompactThermalNode("side", CompactNodeRole.Exposed) { ExposedAreaSquareMeters = 0.5 * caseArea },
                    new CompactThermalNode("bottom", CompactNodeRole.Board),
                    new CompactThermalNode("leads", CompactNodeRole.Board),
                    new CompactThermalNode("inner", CompactNodeRole.Internal)
                },
                Resistors = new[]
                {
                    new CompactThermalResistor("junction", "inner", 2),      // θJC = 2 + 8 ∥ 8 = 6
                    new CompactThermalResistor("inner", "top", 8),
                    new CompactThermalResistor("inner", "side", 8),
                    new CompactThermalResistor("junction", "bottom", 18),    // 18 ∥ 18 = 9
                    new CompactThermalResistor("junction", "leads", 18)
                }
            }
        });
        // top and side are alike (equal resistances from the same node, equal areas), so they
        // sit at one temperature and the film on A/2 + A/2 is the film on A: the same part as before.
        Assert.Equal(twoResistor.JunctionKelvin, split.JunctionKelvin, 1e-6);
        Assert.Equal(twoResistor.FromTopWatts, split.FromTopWatts, 1e-7);
        Assert.Equal(twoResistor.ToBoardWatts, split.ToBoardWatts, 1e-7);
        Assert.Equal(split.Nodes.First(n => n.Node == "top").Kelvin, split.Nodes.First(n => n.Node == "side").Kelvin, 1e-9);
        Assert.Equal(split.Nodes.First(n => n.Node == "bottom").Kelvin, split.BoardKelvin, 1e-9);

        // Refusals name the part.
        var noBoard = new CompactThermalModel
        {
            Nodes = new[] { new CompactThermalNode("junction", CompactNodeRole.Junction), new CompactThermalNode("top", CompactNodeRole.Exposed) },
            Resistors = new[] { new CompactThermalResistor("junction", "top", 1) }
        };
        Assert.Contains("U1", Assert.Throws<InvalidOperationException>(() => Run(u1 with { CompactModel = noBoard })).Message);
        Assert.Throws<InvalidOperationException>(() => Run(u1 with { CompactModel = noBoard, JunctionCapacity = 1 }));
    }

    [Fact]
    public void Transient_TheJunctionStoresHeatFirst_AndSettlesOnTheSteadyState()
    {
        const double ambient = 300, cj = 0.02, cCase = 0.5;
        var (mesh, parts) = TwoPartBoard(junctionCapacity: cj, caseCapacity: cCase);
        var setup = new ComponentThermalSetup { Components = parts, Environment = StillAir(ambient) };
        var steady = ComponentThermalAnalysis.Solve(mesh, setup);
        // The board's own time constant is minutes; 3000 s in 150 s steps ends on the steady state.
        var transient = ComponentThermalAnalysis.SolveTransient(mesh, setup, new TransientThermalSettings
        {
            InitialTemperature = ambient, TimeStep = 150, Duration = 3000
        });
        _out.WriteLine(string.Join("\n", transient.Describe()));
        var junction = transient.JunctionHistory("U1");
        for (int i = 1; i < junction.Count; i++) Assert.True(junction[i].Kelvin >= junction[i - 1].Kelvin - 1e-9);
        double steadyRise = steady.Components.First(c => c.RefDes == "U1").JunctionKelvin - ambient;
        Assert.Equal(steadyRise, junction[^1].Kelvin - ambient, steadyRise * 2e-3);
        Assert.Equal(transient.Components.First(c => c.RefDes == "U1").JunctionKelvin, junction[^1].Kelvin);

        // Fine steps at the start: the junction's first step is its own capacity filling,
        // T₁ − T₀ ≤ P·Δt/C_j, and most of it when Δt ≪ C_j·θ.
        var early = ComponentThermalAnalysis.SolveTransient(mesh, setup, new TransientThermalSettings
        {
            InitialTemperature = ambient, TimeStep = 0.01, Duration = 0.1, OutputStride = 1
        }).JunctionHistory("U1");
        double firstStep = early[1].Kelvin - ambient, bound = parts[0].PowerWatts * 0.01 / cj;
        _out.WriteLine($"first 10 ms: junction +{firstStep:f4} K (adiabatic bound {bound:f4} K)");
        Assert.InRange(firstStep / bound, 0.8, 1.0);
        Assert.Contains(transient.Assumptions, a => a.Contains("backward Euler"));
    }

    [Fact]
    public void ThermalImpedance_OfAPart_IsItsThetaJa_AndPredictsStepAndPulse()
    {
        const double ambient = 300, cj = 0.05, cCase = 0.3;
        var (mesh, parts) = TwoPartBoard(edge: 2.5e-3, junctionCapacity: cj, caseCapacity: cCase);
        // The curve is U1 alone; the steady θJA it ends at is U1 alone on this board too.
        var alone = parts.Select(p => p.RefDes == "U1" ? p : p with { PowerWatts = 0 }).ToArray();
        var setup = new ComponentThermalSetup { Components = alone, Environment = StillAir(ambient) };
        var impedance = ComponentThermalAnalysis.ThermalImpedance(mesh, setup, "U1", new ThermalImpedanceSettings
        {
            StartTime = 1e-3, EndTime = 1e4, PointsPerDecade = 10
        });
        var steady = ComponentThermalAnalysis.Solve(mesh, setup).Components.First(c => c.RefDes == "U1");
        _out.WriteLine($"U1: θJA {impedance.ThetaJa:f4} K/W from the curve, {steady.ThetaJa:f4} steady; " +
                       $"Z_th {impedance.Junction.At(0.1):f3} at 0.1 s, {impedance.Junction.At(10):f3} at 10 s, {impedance.Junction.KelvinPerWatt[^1]:f3} at the end");
        Assert.Equal(steady.ThetaJa, impedance.ThetaJa, steady.ThetaJa * 1e-4);
        Assert.NotNull(impedance.Case);
        Assert.True(impedance.Case!.SteadyKelvinPerWatt < impedance.ThetaJa);
        Assert.Equal(1.0, impedance.PowerWatts);

        // The same step by the transient (backward Euler, 20 ms steps): the two agree on the
        // junction's rise at every stored time once a few steps are in.
        var step = ComponentThermalAnalysis.SolveTransient(mesh, setup, new TransientThermalSettings
        {
            InitialTemperature = ambient, TimeStep = 0.02, Duration = 20, OutputStride = 25
        }).JunctionHistory("U1");
        double worst = 0;
        foreach (var (time, kelvin) in step.Skip(1))
        {
            double rise = kelvin - ambient, predicted = impedance.Junction.At(time) * parts[0].PowerWatts;
            worst = Math.Max(worst, Math.Abs(rise / predicted - 1));
        }
        _out.WriteLine($"step: transient vs Z_th worst {worst * 100:f2} % over {step.Count - 1} stored times");
        Assert.True(worst < 0.02, $"worst {worst * 100:f2} %");

        // A pulsed load, 1 s period at 30 %: the transient with the part's own profile against
        // superposition of the curve, at the end of an on-time and of an off-time.
        var pulse = PowerProfile.Pulse(period: 1.0, dutyCycle: 0.3);
        var pulsedParts = alone.Select(p => p.RefDes == "U1" ? p with { PowerProfile = pulse } : p).ToArray();
        var pulsed = ComponentThermalAnalysis.SolveTransient(mesh, setup with { Components = pulsedParts }, new TransientThermalSettings
        {
            InitialTemperature = ambient, TimeStep = 0.01, Duration = 10, OutputStride = 10
        });
        var history = pulsed.JunctionHistory("U1");
        var onEnd = history.First(h => Math.Abs(h.Time - 9.3) < 1e-9);
        var offEnd = history.First(h => Math.Abs(h.Time - 10.0) < 1e-9);
        double onPredicted = impedance.Junction.RiseAt(9.3, pulse, parts[0].PowerWatts);
        double offPredicted = impedance.Junction.RiseAt(10.0, pulse, parts[0].PowerWatts);
        _out.WriteLine($"pulse: end of on-time {onEnd.Kelvin - ambient:f3} K (superposition {onPredicted:f3}), " +
                       $"end of off-time {offEnd.Kelvin - ambient:f3} K ({offPredicted:f3})");
        Assert.InRange((onEnd.Kelvin - ambient) / onPredicted, 0.97, 1.03);
        Assert.InRange((offEnd.Kelvin - ambient) / offPredicted, 0.97, 1.03);
        Assert.True(onEnd.Kelvin > offEnd.Kelvin);

        Assert.Throws<InvalidOperationException>(() => ComponentThermalAnalysis.ThermalImpedance(mesh, setup, "U7"));
    }

    [Fact]
    public void CopperPlane_CoolsThePart()
    {
        var outline = Rect(0, 0, 50e-3, 50e-3);
        var part = new ThermalComponent
        {
            RefDes = "U1", Footprint = Rect(22e-3, 22e-3, 28e-3, 28e-3), PowerWatts = 1,
            ThetaJc = 10, ThetaJb = 8, CaseFilmCoefficient = 12
        };
        double Junction(IReadOnlyList<CopperIsland> islands)
        {
            var mesh = BoardThermalMesher.Mesh(Board(outline, islands), new[] { part }, Options(Fr4, 1.5e-3));
            return ComponentThermalAnalysis.Solve(mesh, new ComponentThermalSetup
            {
                Components = new[] { part }, AmbientKelvin = 300,
                BoardConditions = new BoundaryCondition[] { Film(10, 300, 0, 1) }
            }).Components.Single().JunctionKelvin - 300;
        }
        var plane = Rect(-1e-3, -1e-3, 51e-3, 51e-3);
        double bare = Junction(Array.Empty<CopperIsland>());
        double oneLayer = Junction(new[] { new CopperIsland(0, 1, "L1", plane) });
        double twoLayers = Junction(new[] { new CopperIsland(0, 1, "L1", plane), new CopperIsland(1, 2, "L2", plane) });
        _out.WriteLine($"θJA on a 50 mm board: bare laminate {bare:f1} K/W, one copper plane {oneLayer:f1}, two {twoLayers:f1}");
        Assert.True(oneLayer < 0.6 * bare);
        Assert.True(twoLayers < oneLayer);
    }
}
