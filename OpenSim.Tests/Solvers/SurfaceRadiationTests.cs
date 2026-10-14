using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using OpenSim.Solvers.Environment;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// FU-44: radiation between faces with view factors. The checks are what the geometry and
/// the energy balance say exactly: inside a closed surface every point's view factors add
/// to 1; a convex body sees none of itself; a plate hidden behind another sees nothing; a
/// body in a black cavity loses ε·A·σ(T⁴ − T_w⁴) whatever its own emissivity does to the
/// rest; the heat a body radiates is the heat put into it.
/// </summary>
public class SurfaceRadiationTests
{
    private readonly ITestOutputHelper _out;
    public SurfaceRadiationTests(ITestOutputHelper output) => _out = output;

    /// <summary>σ = 2π⁵k⁴/(15h³c²) from the SI's defining constants (exact by definition).</summary>
    private static readonly double Sigma =
        2 * Math.Pow(Math.PI, 5) * Math.Pow(1.380649e-23, 4) / (15 * Math.Pow(6.62607015e-34, 3) * 299792458.0 * 299792458.0);
    private const double Cell = 0.01;
    private const int Cavity = CellLattice.InnerFace;       // the shell's inner surface (region 0)
    private const int BodyFace = CellLattice.InnerFace + 1;  // the body's skin (region 1)

    private static Material Solid(string name, double k, double emissivity, double volumetricCapacity = 1e6) => new()
    {
        Name = name, Density = 1000, SpecificHeat = volumetricCapacity / 1000, ThermalConductivity = k,
        Emissivity = emissivity, YoungsModulus = 1e9, PoissonRatio = 0.3
    };

    /// <summary>An 8-cell cube with a one-cell wall around a 6-cell cavity and, optionally,
    /// a 2-cell cube floating in its middle.</summary>
    private static FeMesh BodyInCavity(bool body = true) => CellLattice.Build(8, 8, 8, Cell, (i, j, k) =>
    {
        if (i == 0 || j == 0 || k == 0 || i == 7 || j == 7 || k == 7) return 0;
        return body && i is 3 or 4 && j is 3 or 4 && k is 3 or 4 ? 1 : -1;
    });

    private static int[] TrianglesOf(FeMesh mesh, params int[] faces) =>
        Enumerable.Range(0, mesh.BoundaryTriangles.Count).Where(t => faces.Contains(mesh.BoundaryTriangles[t].FaceId)).ToArray();

    private static double ShellSurfaceTemperature(FeMesh mesh, IReadOnlyList<double> t, int face) =>
        mesh.GetFaceTriangles(new[] { face }).SelectMany(b => new[] { b.A, b.B, b.C }).Distinct().Average(n => t[n]);

    private static IReadOnlyList<double> Temperature(SolveOutput output) =>
        ((NodalScalarField)output.Fields.Single(f => f.Name == "Temperature")).Values;

    // ---------------- View factors ----------------

    [Fact]
    public void ViewFactors_AddToOneInsideAClosedCavity_AndAConvexBodySeesNoneOfItself()
    {
        var empty = BodyInCavity(body: false);
        var walls = TrianglesOf(empty, Cavity);
        var (f, _) = SurfaceRadiationExchange.ViewFactors(empty, BoundaryAdjacency.Build(empty), walls, false, default);
        double worstEmpty = RowSums(f, walls.Length).Max(s => Math.Abs(s - 1));

        var mesh = BodyInCavity();
        var adjacency = BoundaryAdjacency.Build(mesh);
        var triangles = TrianglesOf(mesh, Cavity, BodyFace);
        var (g, rays) = SurfaceRadiationExchange.ViewFactors(mesh, adjacency, triangles, true, default);
        int n = triangles.Length;
        var sums = RowSums(g, n);
        bool IsBody(int i) => mesh.BoundaryTriangles[triangles[i]].FaceId == BodyFace;
        double worstBody = 0, worstWall = 0, bodyOnBody = 0, mismatch = 0, largest = 0;
        for (int i = 0; i < n; i++)
        {
            if (IsBody(i)) worstBody = Math.Max(worstBody, Math.Abs(sums[i] - 1));
            else worstWall = Math.Max(worstWall, Math.Abs(sums[i] - 1));
            for (int j = 0; j < n; j++)
            {
                if (IsBody(i) && IsBody(j)) bodyOnBody = Math.Max(bodyOnBody, g[i * n + j]);
                double ij = adjacency.Areas[triangles[i]] * g[i * n + j], ji = adjacency.Areas[triangles[j]] * g[j * n + i];
                mismatch = Math.Max(mismatch, Math.Abs(ij - ji));
                largest = Math.Max(largest, ij);
            }
        }
        _out.WriteLine($"empty cavity ({walls.Length} triangles): row sums within {worstEmpty:g3} of 1");
        _out.WriteLine($"with the body ({n} triangles, {rays} rays): body rows within {worstBody:g3} of 1, wall rows within " +
                       $"{worstWall:g3} (the body's shadow sampled by rays), body-to-body {bodyOnBody:g3}; " +
                       $"reciprocity A·F matched to {mismatch / largest:g3} of the largest pair");
        Assert.True(worstEmpty < 1e-9, $"empty cavity rows off by {worstEmpty:g3}");
        Assert.True(worstBody < 1e-9, $"body rows off by {worstBody:g3}");
        Assert.Equal(0, bodyOnBody);
        Assert.True(worstWall < 0.05, $"wall rows off by {worstWall:g3}");
    }

    private static double[] RowSums(double[] f, int n)
    {
        var sums = new double[n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) sums[i] += f[i * n + j];
        return sums;
    }

    /// <summary>Two 4 × 4-cell plates 5 cells apart, and a plate between them: whole, it hides
    /// one from the other completely; half, it hides part.</summary>
    [Fact]
    public void AnotherSurface_BlocksTheView()
    {
        double Plates(Func<int, int, bool>? blocker, bool obstruction)
        {
            var mesh = CellLattice.Build(4, 4, 7, Cell, (i, j, k) =>
                k == 0 ? 0 : k == 6 ? 1 : k == 3 && blocker is not null && blocker(i, j) ? 2 : -1);
            var adjacency = BoundaryAdjacency.Build(mesh);
            var triangles = TrianglesOf(mesh, CellLattice.InnerFace, CellLattice.InnerFace + 1);
            var (f, _) = SurfaceRadiationExchange.ViewFactors(mesh, adjacency, triangles, obstruction, default);
            int n = triangles.Length;
            double area = 0, sum = 0;
            for (int i = 0; i < n; i++)
            {
                if (mesh.BoundaryTriangles[triangles[i]].FaceId != CellLattice.InnerFace) continue;
                double a = adjacency.Areas[triangles[i]];
                area += a;
                for (int j = 0; j < n; j++)
                    if (mesh.BoundaryTriangles[triangles[j]].FaceId == CellLattice.InnerFace + 1) sum += a * f[i * n + j];
            }
            return sum / area;
        }
        double open = Plates(null, true);
        double hidden = Plates((_, _) => true, true);
        double half = Plates((i, _) => i < 2, true);
        double ignored = Plates((_, _) => true, false);
        _out.WriteLine($"plate to plate: open {open:F5}, behind a whole plate {hidden:g3}, behind half a plate {half:F5}, " +
                       $"obstruction off {ignored:F5}");
        Assert.InRange(open, 0.05, 0.5);
        Assert.Equal(0, hidden);
        Assert.InRange(half, 0.25 * open, 0.75 * open);
        Assert.Equal(open, ignored, 1e-12);
    }

    // ---------------- In the solve ----------------

    /// <summary>
    /// A 2 cm cube (ε = 0.5) heated by P inside a black cavity whose walls are held at
    /// 300 K: every point of the cube sees only the walls, and the walls return only their
    /// own emission, so the cube loses ε·A·σ(T⁴ − T_w⁴) — its temperature follows. The heat
    /// it radiates is P and the walls take all of it.
    /// </summary>
    [Fact]
    public void AHeatedBodyInABlackCavity_RadiatesItsPower()
    {
        const double wall = 300, power = 1.2, emissivity = 0.5;
        var mesh = BodyInCavity();
        double area = 6 * 4 * Cell * Cell;
        double expected = Math.Pow(power / (emissivity * area * Sigma) + Math.Pow(wall, 4), 0.25);
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Solid("wall", 400, 1.0),
            RegionMaterials = new Dictionary<int, Material> { [0] = Solid("wall", 400, 1.0), [1] = Solid("body", 5000, emissivity) },
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "walls", FaceIds = new[] { Cavity }, Kelvin = wall },
                new HeatFlux { Name = "heat", FaceIds = new[] { BodyFace }, TotalPower = power }
            },
            SurfaceRadiation = new[]
            {
                new SurfaceRadiation { Name = "cavity", FaceIds = new[] { Cavity, BodyFace }, AmbientTemperature = wall }
            }
        };
        var output = new HeatConductionSolver().Solve(input);
        var t = Temperature(output);
        double body = ShellSurfaceTemperature(mesh, t, BodyFace);
        var exchange = SurfaceRadiationExchange.Build(input, new List<string>())!;
        var balance = exchange.Balance(t).Single();
        foreach (var line in output.Log.Where(l => l.Contains("radiation"))) _out.WriteLine(line);
        _out.WriteLine($"body {body:F4} K, expected {expected:F4} K ({body - expected:+0.0000;-0.0000} K); body radiates " +
                       $"{balance.ByFace[BodyFace]:F9} W of {power} W, walls {balance.ByFace[Cavity]:F9} W, surroundings {balance.ToSurroundings:g3} W");
        // The balance closes to the conjugate gradient's residual (relative 1e-10 of a right-hand
        // side carrying the walls' fixed temperatures, about 1e4 here).
        Assert.Equal(power, balance.ByFace[BodyFace], power * 1e-4);
        Assert.Equal(-power, balance.ByFace[Cavity], power * 1e-4);
        Assert.True(Math.Abs(balance.ToSurroundings) < 1e-3 * power);
        Assert.True(Math.Abs(body - expected) < 0.05, $"body at {body:F4} K, expected {expected:F4} K");
    }

    /// <summary>
    /// A convex cube sees none of itself, so a set holding all its faces sends every view
    /// to the surroundings — the environment's own radiation to its ambient, exactly. Both
    /// together must not count the exchange twice.
    /// </summary>
    [Fact]
    public void ASetOnAConvexBody_IsTheEnvironmentsRadiation_AndDoesNotAddToIt()
    {
        var mesh = StructuredBoxMesh.Build(0, 0.02, 0, 0.02, 0, 0.02, 3, 3, 3);
        var faces = new[] { 0, 1, 2, 3, 4, 5 };
        // Heated in its volume: a heat-flow condition would claim its face from the environment.
        var baseInput = new SolveInput
        {
            Mesh = mesh, Material = Solid("cube", 5000, 0.8),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ElementHeatSource = Enumerable.Repeat(2.0 / 8e-6, mesh.ElementCount).ToArray()
        };
        var environment = new EnvironmentSettings { Medium = MediumKind.Vacuum, AmbientTemperature = 300, IncludeRadiation = true };
        var set = new[] { new SurfaceRadiation { Name = "cube", FaceIds = faces, AmbientTemperature = 300 } };
        double Mean(SolveInput input) => Temperature(new HeatConductionSolver().Solve(input)).Average();
        double byEnvironment = Mean(baseInput with { Environment = environment });
        double bySet = Mean(baseInput with { SurfaceRadiation = set });
        double both = Mean(baseInput with { Environment = environment, SurfaceRadiation = set });
        double area = 6 * 0.02 * 0.02;
        double lumped = Math.Pow(2.0 / (0.8 * area * Sigma) + Math.Pow(300, 4), 0.25);
        _out.WriteLine($"environment {byEnvironment:F6} K, set {bySet:F6} K, both {both:F6} K; lumped body {lumped:F6} K");
        Assert.Equal(byEnvironment, bySet, 1e-3);
        Assert.Equal(bySet, both, 1e-9);
        Assert.Equal(lumped, bySet, 0.05);
    }

    /// <summary>
    /// The cube in the black cavity starting at 500 K with no heat: one temperature, it
    /// follows C·dT/dt = −ε·A·σ(T⁴ − T_w⁴), integrated here by classical Runge–Kutta in
    /// steps of 0.05 s as the reference. Backward Euler in 1.5 s steps follows it to its
    /// first-order error.
    /// </summary>
    [Fact]
    public void ABodyCoolsByRadiation_AsItsLumpedEquationSays()
    {
        const double wall = 300, start = 500, emissivity = 0.5, capacity = 1e6, duration = 300;
        var mesh = BodyInCavity();
        double area = 6 * 4 * Cell * Cell, heat = capacity * 8 * Cell * Cell * Cell;
        var initial = new double[mesh.NodeCount];
        var bodyNodes = new HashSet<int>();
        for (int e = 0; e < mesh.ElementCount; e++)
            if (mesh.RegionOf(e) == 1) foreach (int node in mesh.GetElementNodes(e)) bodyNodes.Add(node);
        for (int i = 0; i < initial.Length; i++) initial[i] = bodyNodes.Contains(i) ? start : wall;
        var output = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Solid("wall", 400, 1.0),
            RegionMaterials = new Dictionary<int, Material> { [0] = Solid("wall", 400, 1.0), [1] = Solid("body", 5000, emissivity, capacity) },
            BoundaryConditions = new BoundaryCondition[] { new FixedTemperature { Name = "walls", FaceIds = new[] { Cavity }, Kelvin = wall } },
            SurfaceRadiation = new[] { new SurfaceRadiation { Name = "cavity", FaceIds = new[] { Cavity, BodyFace }, AmbientTemperature = wall } },
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = wall, Duration = duration, TimeStep = 1.5,
                InitialState = new ThermalInitialState { NodalTemperature = initial }
            }
        });
        double body = bodyNodes.Average(n => Temperature(output)[n]);

        double Rate(double temperature) => -emissivity * area * Sigma * (Math.Pow(temperature, 4) - Math.Pow(wall, 4)) / heat;
        double reference = start;
        const double h = 0.05;
        for (int s = 0; s < (int)Math.Round(duration / h); s++)
        {
            double k1 = Rate(reference), k2 = Rate(reference + 0.5 * h * k1), k3 = Rate(reference + 0.5 * h * k2), k4 = Rate(reference + h * k3);
            reference += h / 6 * (k1 + 2 * k2 + 2 * k3 + k4);
        }
        _out.WriteLine($"after {duration} s: body {body:F3} K, lumped equation {reference:F3} K ({body - reference:+0.000;-0.000} K of a " +
                       $"{start - reference:F1} K fall)");
        Assert.True(Math.Abs(body - reference) < 0.01 * (start - reference), $"body {body:F3} K against {reference:F3} K");
    }
}
