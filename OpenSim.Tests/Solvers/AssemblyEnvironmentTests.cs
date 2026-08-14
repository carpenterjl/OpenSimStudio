using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Solvers;
using OpenSim.Solvers.Environment;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Composition gates for the assembly heat-flow path: several bodies merged into one mesh,
/// joined by finite-conductance contacts, each dissipating its own power, cooling into an
/// environment. Every piece is gated on its own elsewhere (merge, contact, correlations,
/// the Picard fixed point) — what is proved here is that they compose into the physics the
/// user is promised, exactly as <c>SolveViewModel</c> assembles them.
/// </summary>
public class AssemblyEnvironmentTests
{
    private const double Ambient = 300.0;

    /// <summary>A high-conductivity material so each body is near-isothermal and the
    /// interesting temperature difference is the one ACROSS the joint.</summary>
    private static Material Metal(string name) => new()
    {
        Name = name,
        Density = 1000,
        YoungsModulus = 1e9,
        PoissonRatio = 0.3,
        ThermalConductivity = 400,
        SpecificHeat = 500,
        Emissivity = 0.8
    };

    /// <summary>Two 20 mm cubes meeting on the x = 0.02 plane, meshed independently — the
    /// interfaces do NOT match node for node (2×2×2 against 3×3×3), which is exactly the
    /// case the contact quadrature exists for.</summary>
    private static List<Body> TouchingPair(double gap = 0) => new()
    {
        StructuredBoxMesh.Box("Hot", 0, 0.02, 0, 0.02, 0, 0.02, 2, 2, 2, Metal("HotMetal")),
        StructuredBoxMesh.Box("Cold", 0.02 + gap, 0.04 + gap, 0, 0.02, 0, 0.02, 3, 3, 3, Metal("ColdMetal"))
    };

    private static EnvironmentSettings StillAir => new()
    {
        Medium = MediumKind.StillFluid,
        AmbientTemperature = Ambient,
        IncludeRadiation = true
    };

    private static SolveInput Build(List<Body> bodies, out FeMeshAssembler.AssembledMesh assembled,
        out IReadOnlyList<ContactInterface> contacts)
    {
        assembled = FeMeshAssembler.Assemble(bodies);
        contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases);
        return new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = bodies[0].Material!,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ElementHeatSource = FeMeshAssembler.BuildElementHeatSource(assembled, bodies),
            ThermalContacts = contacts,
            Environment = StillAir
        };
    }

    private static double[] Temperatures(SolveOutput output) =>
        ((OpenSim.Core.Results.NodalScalarField)output.Fields.First(f => f.Name == "Temperature"))
        .Values.ToArray();

    [Fact]
    public void DissipatedPower_LeavesTheWholeAssemblyThroughTheEnvironment()
    {
        // The conservation statement for an assembly: whatever the parts dissipate must
        // cross their skin into the surroundings. The consistent Robin term integrates
        // h(T−T_a) exactly over each linear triangle, so this is an identity at the CG
        // tolerance — not a band. Contact moves heat between bodies but creates none, so
        // it must not appear in the ledger at all.
        const double hot = 5.0, cold = 1.5;
        var bodies = TouchingPair();
        bodies[0].HeatSourcePower = hot;
        bodies[1].HeatSourcePower = cold;
        var input = Build(bodies, out var assembled, out var contacts);
        Assert.NotEmpty(contacts);

        var output = new HeatConductionSolver().Solve(input);
        var temperature = Temperatures(output);

        var film = EnvironmentBoundaryModel.Build(input, new List<string>())!.Evaluate(temperature);
        double lost = 0;
        var mesh = assembled.Mesh;
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            if (!film.IsWetted(t)) continue;
            var triangle = mesh.BoundaryTriangles[t];
            double mean = (temperature[triangle.A] + temperature[triangle.B] + temperature[triangle.C]) / 3;
            lost += film.TriangleFilmCoefficient[t] * (mean - Ambient)
                    * ScalarDiffusionAssembler.SurfaceArea(mesh, triangle);
        }
        Assert.InRange(Math.Abs(lost - (hot + cold)) / (hot + cold), 0, 1e-6);
        Assert.True(temperature.Min() > Ambient, "an internally heated assembly sits above ambient");
    }

    [Fact]
    public void HeatCrossesTheJointOnlyWhenTheBodiesTouch()
    {
        // The decisive composition gate. One body dissipates, the other does not. In
        // contact, the silent body must run hot because heat crossed the interface; pulled
        // apart, it must sit at ambient — separate bodies are NEVER welded by the merge, so
        // with no contact there is no path between them at all.
        var joined = TouchingPair();
        joined[0].HeatSourcePower = 5.0;
        var joinedOutput = new HeatConductionSolver().Solve(Build(joined, out var jointed, out var contacts));
        Assert.NotEmpty(contacts);
        var joinedField = Temperatures(joinedOutput);
        double coldSideJoined = ColdBodyMean(joinedField, jointed);
        Assert.True(coldSideJoined > Ambient + 1.0,
            $"the touching body must be heated through the joint (measured {coldSideJoined - Ambient:F2} K rise)");

        // 5 mm apart: far beyond any mesh-scale tolerance, so no interface is detected.
        var apart = TouchingPair(gap: 5e-3);
        apart[0].HeatSourcePower = 5.0;
        var apartOutput = new HeatConductionSolver().Solve(Build(apart, out var separated, out var none));
        Assert.Empty(none);
        double coldSideApart = ColdBodyMean(Temperatures(apartOutput), separated);
        Assert.InRange(coldSideApart - Ambient, -1e-9, 1e-9);
    }

    [Fact]
    public void TighteningTheJoint_ShrinksTheTemperatureStepAcrossIt()
    {
        // The contact conductance must actually govern the interface: the step across the
        // joint is the flux times 1/h_c, so a ten-fold better joint drops it ten-fold.
        // Stated as a one-sided trend rather than a ratio because the flux itself shifts
        // slightly when the cold body's own surface temperature changes.
        double Step(double conductance)
        {
            var bodies = TouchingPair();
            bodies[0].HeatSourcePower = 5.0;
            var assembled = FeMeshAssembler.Assemble(bodies);
            var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases,
                new ContactDetectionSettings { DefaultConductance = conductance });
            var input = new SolveInput
            {
                Mesh = assembled.Mesh,
                Material = bodies[0].Material!,
                RegionMaterials = assembled.RegionMaterials,
                BoundaryConditions = assembled.BoundaryConditions,
                ElementHeatSource = FeMeshAssembler.BuildElementHeatSource(assembled, bodies),
                ThermalContacts = contacts,
                Environment = StillAir
            };
            var temperature = Temperatures(new HeatConductionSolver().Solve(input));
            return HotBodyMean(temperature, assembled) - ColdBodyMean(temperature, assembled);
        }

        double loose = Step(5e2);
        double tight = Step(5e3);
        Assert.True(loose > 0 && tight > 0, "heat flows from the dissipating body to the other one");
        // Measured 0.126 — not the naive 0.1, because a colder second body loses less to
        // the air and so carries slightly less flux; the gate is the one-sided trend.
        Assert.True(tight < 0.2 * loose,
            $"a ten-fold better joint must roughly ten-fold the step down (measured {loose:F3} → {tight:F3} K)");
    }

    [Fact]
    public void TheTransientEndsWhereTheSteadySolveSays()
    {
        // Two independent solvers, one answer: run the assembly long past its thermal time
        // constant and it must land on the steady equilibrium. This is the gate that the
        // transient path assembles the SAME contact + environment terms as the steady one.
        var bodies = TouchingPair();
        bodies[0].HeatSourcePower = 5.0;
        var steady = Temperatures(new HeatConductionSolver().Solve(Build(bodies, out _, out _)));

        var transientInput = Build(bodies, out var assembled, out _) with
        {
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = Ambient, Duration = 4000, TimeStep = 100
            }
        };
        var transient = Temperatures(new TransientThermalSolver().Solve(transientInput));

        Assert.Equal(steady.Length, transient.Length);
        double worst = 0;
        for (int i = 0; i < steady.Length; i++)
            worst = Math.Max(worst, Math.Abs(steady[i] - transient[i]));
        Assert.InRange(worst, 0, 1e-5);   // K; measured 6.9e-7 — the tail left after 4000 s
        Assert.True(assembled.BodyCount == 2);
    }

    private static double HotBodyMean(double[] temperature, FeMeshAssembler.AssembledMesh assembled) =>
        BodyMean(temperature, assembled, 0);

    private static double ColdBodyMean(double[] temperature, FeMeshAssembler.AssembledMesh assembled) =>
        BodyMean(temperature, assembled, 1);

    private static double BodyMean(double[] temperature, FeMeshAssembler.AssembledMesh assembled, int body)
    {
        int first = assembled.NodeBases[body];
        int last = body + 1 < assembled.BodyCount
            ? assembled.NodeBases[body + 1]
            : assembled.Mesh.NodeCount;
        double sum = 0;
        for (int n = first; n < last; n++) sum += temperature[n];
        return sum / (last - first);
    }
}
