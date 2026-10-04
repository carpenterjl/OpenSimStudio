using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using OpenSim.Solvers.Environment;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Fix 18 (TH-08, TH-09/D9, TH-14/D8): the three things the thermal solvers said about
/// themselves that did not hold — radiation exact on a face that is not isothermal, a
/// monotone transient at any step, and a radiation iteration that settles.
/// </summary>
public class ThermalClaimsTests
{
    private const double Sigma = 5.670374419e-8;
    private const double Ambient = 300.0;

    private static Material Laminate(double conductivity) => new()
    {
        Name = "laminate",
        YoungsModulus = 22e9,
        PoissonRatio = 0.15,
        Density = 1850,
        ThermalConductivity = conductivity,
        SpecificHeat = 1100,
        Emissivity = 0.9
    };

    private static EnvironmentSettings Vacuum() => new()
    {
        Medium = MediumKind.Vacuum,
        AmbientTemperature = Ambient,
        IncludeRadiation = true
    };

    /// <summary>∫εσ(T⁴ − T_a⁴)dA over the skin with T linear on each triangle, by the
    /// edge-midpoint rule (exact to cubics) on four sub-triangles — an evaluation of the
    /// radiation LAW on the solved field, independent of how the solver put it in.</summary>
    private static double RadiatedPower(FeMesh mesh, IReadOnlyList<double> t, double emissivity)
    {
        double total = 0;
        foreach (var tri in mesh.BoundaryTriangles)
        {
            double area = 0.5 * Vector3D.Cross(mesh.Nodes[tri.B] - mesh.Nodes[tri.A],
                mesh.Nodes[tri.C] - mesh.Nodes[tri.A]).Length;
            double a = t[tri.A], b = t[tri.B], c = t[tri.C];
            double ab = 0.5 * (a + b), bc = 0.5 * (b + c), ca = 0.5 * (c + a);
            double sum = 0;
            foreach (var (p, q, r) in new[] { (a, ab, ca), (ab, b, bc), (ca, bc, c), (ab, bc, ca) })
                foreach (double m in new[] { 0.5 * (p + q), 0.5 * (q + r), 0.5 * (r + p) })
                    sum += Math.Pow(m, 4) - Math.Pow(Ambient, 4);
            total += emissivity * Sigma * sum / 12.0 * area;
        }
        return total;
    }

    // ---------------------------------------------------------------- TH-08

    [Fact]
    public void AHotSpotOnAPoorConductor_RadiatesByItsOwnTemperature()
    {
        // 100 × 100 × 2 mm plate, k = 0.3 W/(m·K), in vacuum, 3 W in the central 10 × 10 mm.
        // The face is far from isothermal. With one radiative coefficient per face (at the
        // face's mean temperature) the solved field satisfied the face-averaged balance,
        // and the Stefan–Boltzmann law integrated over it did not return the power.
        // Per triangle the law holds at each triangle's mean temperature, and what is left
        // is the mesh's own: 0.87 % on 2.5 mm cells, under 0.5 % on these 1.25 mm ones.
        const double power = 3.0;
        var material = Laminate(0.3);
        var mesh = StructuredBoxMesh.Build(0, 0.1, 0, 0.1, 0, 0.002, 80, 80, 1);
        var source = new double[mesh.ElementCount];
        double heated = 0;
        for (int e = 0; e < source.Length; e++)
        {
            var el = mesh.Elements[e];
            var c = (mesh.Nodes[el.N0] + mesh.Nodes[el.N1] + mesh.Nodes[el.N2] + mesh.Nodes[el.N3]) * 0.25;
            if (Math.Abs(c.X - 0.05) < 0.005 && Math.Abs(c.Y - 0.05) < 0.005)
            {
                source[e] = 1;
                heated += mesh.ElementVolume(e);
            }
        }
        for (int e = 0; e < source.Length; e++) source[e] *= power / heated;

        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ElementHeatSource = source,
            Environment = Vacuum()
        });
        var t = ((NodalScalarField)output.Fields.Single(f => f.Name == "Temperature")).Values;
        Assert.True(t.Max() - t.Min() > 60, $"premise: a real hot spot (spread {t.Max() - t.Min():F1} K)");

        double radiated = RadiatedPower(mesh, t, 0.9);
        Assert.True(Math.Abs(radiated - power) <= 0.005 * power,
            $"the solved field radiates {radiated:F4} W of the {power} W put in");
    }

    // ---------------------------------------------------------------- TH-14 / D8

    [Fact]
    public void ARadiationDominatedBody_SettlesAboveTheOldDivergenceLimit()
    {
        // A lumped cube in vacuum sized to sit at 800 K in a 300 K room: T_s/T_a = 2.67,
        // past the 1.84 where the lagged factored coefficient oscillates with growing
        // amplitude. The answer is (P/(εσA) + T_a⁴)^¼.
        const double side = 0.02, target = 800.0, emissivity = 0.9;
        double area = 6 * side * side;
        double power = emissivity * Sigma * area * (Math.Pow(target, 4) - Math.Pow(Ambient, 4));
        var material = Laminate(1e7) with { Emissivity = emissivity };
        var mesh = StructuredBoxMesh.Build(0, side, 0, side, 0, side, 2, 2, 2);

        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ElementHeatSource = Enumerable.Repeat(power / mesh.TotalVolume(), mesh.ElementCount).ToArray(),
            Environment = Vacuum()
        });
        var t = ((NodalScalarField)output.Fields.Single(f => f.Name == "Temperature")).Values;
        Assert.All(t, value => Assert.Equal(target, value, target * 1e-6));
    }

    // ---------------------------------------------------------------- TH-09 / D9

    private const double Hot = 400.0, Start = 300.0;

    private static SolveOutput SlabStep(double stepOverH2Alpha, ThermalCapacity capacity)
    {
        // 20 mm bar of 1 mm cells, one end raised from 300 K to 400 K at t = 0.
        var material = Laminate(0.3);
        double alpha = 0.3 / (1850 * 1100.0);
        double dt = stepOverH2Alpha * 1e-3 * 1e-3 / alpha;
        var mesh = StructuredBoxMesh.Build(0, 0.02, 0, 0.001, 0, 0.001, 20, 1, 1);
        return new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "hot end", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = Hot }
            },
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = Start,
                TimeStep = dt,
                Duration = 20 * dt,
                OutputStride = 1,
                Capacity = capacity
            }
        });
    }

    private static (double Below, double Above) Excursion(SolveOutput output)
    {
        double below = 0, above = 0;
        foreach (var frame in output.Frames!)
        {
            var t = ((NodalScalarField)frame.Fields.Single(f => f.Name == "Temperature")).Values;
            below = Math.Max(below, Start - t.Min());
            above = Math.Max(above, t.Max() - Hot);
        }
        return (below, above);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.1)]
    [InlineData(1.0)]
    [InlineData(10.0)]
    public void AStepResponse_StaysBetweenItsBounds_AtAnyTimeStep(double stepOverH2Alpha)
    {
        // The plan's gate: every node inside [T₀, T_s] at every frame, for Δt from 0.01 to
        // 10 × h²/α. The default (automatic) capacity lumps below the bound.
        var (below, above) = Excursion(SlabStep(stepOverH2Alpha, ThermalCapacity.Automatic));
        Assert.True(below <= 1e-9 && above <= 1e-9,
            $"Δt = {stepOverH2Alpha}·h²/α: {below:E2} K below the start, {above:E2} K above the hot end");
    }

    [Fact]
    public void TheConsistentCapacity_UndershootsAtASmallStep_AndSaysSo()
    {
        // What the automatic choice avoids, and the reason "monotone" was not true.
        var output = SlabStep(0.01, ThermalCapacity.Consistent);
        var (below, _) = Excursion(output);
        Assert.True(below > 0.1, $"premise: the consistent matrix undershoots ({below:F3} K of a 100 K step)");
        Assert.Contains(output.Log, line => line.StartsWith("WARNING") && line.Contains("consistent"));

        Assert.Contains(SlabStep(0.01, ThermalCapacity.Automatic).Log, line => line.Contains("LUMPED"));
        Assert.DoesNotContain(SlabStep(10, ThermalCapacity.Automatic).Log, line => line.Contains("LUMPED"));
    }

    [Fact]
    public void TheLumpedCapacity_HoldsTheSameHeat()
    {
        var mesh = StructuredBoxMesh.Build(0, 0.02, 0, 0.01, 0, 0.005, 4, 3, 2);
        var assembler = new ScalarDiffusionAssembler(mesh, _ => 1.0);
        var ones = Enumerable.Repeat(1.0, mesh.NodeCount).ToArray();
        var lumped = new double[mesh.NodeCount];
        var consistent = new double[mesh.NodeCount];
        assembler.AssembleLumpedMass(_ => 2.5).Multiply(ones, lumped);
        assembler.AssembleMass(_ => 2.5).Multiply(ones, consistent);
        Assert.Equal(2.5 * mesh.TotalVolume(), lumped.Sum(), 12);
        for (int n = 0; n < lumped.Length; n++) Assert.Equal(consistent[n], lumped[n], 15);

        // FR4 on 1 mm cells: the bound is h²/(6α) with h the edge of the regular
        // tetrahedron of the element's volume — 1.4 s; the app's default step is 0.1 s.
        var input = new SolveInput
        {
            Mesh = StructuredBoxMesh.Build(0, 0.004, 0, 0.001, 0, 0.001, 4, 1, 1),
            Material = Laminate(0.3),
            BoundaryConditions = Array.Empty<BoundaryCondition>()
        };
        Assert.InRange(TransientThermalSolver.MonotoneTimeStep(input).Step, 1.2, 1.6);
    }
}
