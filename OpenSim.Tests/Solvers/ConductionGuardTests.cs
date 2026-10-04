using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Pcb.Inductance;
using OpenSim.Solvers;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Fix 17 (TH-05, TH-06/D13, TH-10, TH-11, TH-12, TH-13, D12, D18): what the conduction
/// solvers do with more than one material, with conductors that reach nothing, and with
/// terminal arrangements that have no single resistance. Every fixture is a structured
/// box whose exact field is linear, so the oracles are closed forms.
/// </summary>
public class ConductionGuardTests
{
    private const double Length = 10e-3, Width = 2e-3, Height = 2e-3;
    private const double Volts = 1.0;

    private static readonly Material Copper = new()
    {
        Name = "Copper",
        YoungsModulus = 110e9,
        PoissonRatio = 0.34,
        Density = 8960,
        ElectricalConductivity = 5.96e7,
        ThermalConductivity = 401,
        SpecificHeat = 385
    };

    private static readonly Material Fr4 = new()
    {
        Name = "FR4",
        YoungsModulus = 22e9,
        PoissonRatio = 0.15,
        Density = 1850,
        ElectricalConductivity = 1e-14,
        RelativePermittivity = 4.4,
        ThermalConductivity = 0.29,
        SpecificHeat = 1100
    };

    /// <summary>A 10 × 2 × 2 mm bar, 10 × 2 × 4 cells, with region 0 = copper wherever
    /// <paramref name="isCopper"/> says so (by element centroid) and region 1 = FR4.</summary>
    private static FeMesh Bar(Func<Vector3D, bool> isCopper)
    {
        var plain = StructuredBoxMesh.Build(0, Length, 0, Width, 0, Height, 10, 2, 4);
        var regions = new int[plain.ElementCount];
        for (int e = 0; e < regions.Length; e++)
        {
            var el = plain.Elements[e];
            var centroid = (plain.Nodes[el.N0] + plain.Nodes[el.N1] + plain.Nodes[el.N2]
                            + plain.Nodes[el.N3]) * 0.25;
            regions[e] = isCopper(centroid) ? 0 : 1;
        }
        return new FeMesh(plain.Nodes, plain.Elements, plain.BoundaryTriangles, regions);
    }

    private static SolveInput Input(FeMesh mesh, params BoundaryCondition[] conditions) => new()
    {
        Mesh = mesh,
        Material = Copper,
        RegionMaterials = new Dictionary<int, Material> { [0] = Copper, [1] = Fr4 },
        BoundaryConditions = conditions
    };

    private static BoundaryCondition[] EndToEnd() => new BoundaryCondition[]
    {
        new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
        new VoltagePotential { Name = "Supply", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = Volts }
    };

    // ---------------------------------------------------------------- copper on laminate

    [Fact]
    public void CopperOnLaminate_SolvesBothMaterials_AndShowsTheCopperCurrentAtTheInterface()
    {
        // Copper is the top quarter of the bar (z > 1.5 mm), laminate the rest, electrodes
        // across both at the two ends. The exact field is φ = V·x/L in BOTH materials.
        const double copperFrom = 1.5e-3;
        var mesh = Bar(c => c.Z > copperFrom);
        var output = new ElectricalConductionSolver().Solve(Input(mesh, EndToEnd()));

        // R is the copper's alone, L/(σ·A_cu) — the plan's "within 0.5 % of the copper-only mesh".
        double copperArea = Width * (Height - copperFrom);
        double expectedR = Length / (Copper.ElectricalConductivity!.Value * copperArea);
        Assert.Equal(expectedR, output.Summary!["Resistance (Ω)"], expectedR * 5e-3);

        // The laminate's potential is solved, not left where an iteration happened to stop:
        // one system with a 6e21 conductivity ratio converged on the copper alone.
        var phi = ((NodalScalarField)output.Fields.Single(f => f.Name == "Electric potential")).Values;
        double worst = 0;
        for (int n = 0; n < mesh.NodeCount; n++)
            worst = Math.Max(worst, Math.Abs(phi[n] - Volts * mesh.Nodes[n].X / Length));
        Assert.True(worst <= 1e-6 * Volts, $"potential off the linear field by {worst:E2} V");

        // The plan's gate: uniform J in the copper to 1 % AT INTERFACE NODES. Averaged
        // across the interface they read half the copper value here (equal element volumes
        // either side; 8 % with 35 µm copper on 0.4 mm laminate elements).
        var current = (NodalVectorField)output.Fields.Single(f => f.Name == "Current density");
        var power = ((NodalScalarField)output.Fields.Single(f => f.Name == "Power density")).Values;
        double expectedJ = Copper.ElectricalConductivity.Value * Volts / Length;
        double expectedQ = expectedJ * Volts / Length;
        int interfaceNodes = 0;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            double z = mesh.Nodes[n].Z;
            if (z < copperFrom - 1e-9) continue;                  // laminate interior
            if (Math.Abs(z - copperFrom) < 1e-9) interfaceNodes++;
            Assert.Equal(expectedJ, current.GetScalar(n), expectedJ * 1e-2);
            Assert.Equal(expectedQ, power[n], expectedQ * 1e-2);
        }
        Assert.True(interfaceNodes > 0);
        Assert.Contains(output.Log, line => line.Contains("insulators"));
        Assert.Contains(output.Log, line => line.Contains("averaged within each material"));
    }

    [Fact]
    public void TwoConductorsJoinedThroughLaminate_ReportTheLeakageResistance()
    {
        // Copper | laminate | copper along the bar, a voltage on each copper end. All the
        // current is what the laminate carries: R = gap/(σ_FR4·A). The copper blocks are
        // equipotentials, so the electrode current is the current the laminate draws from
        // them — which a reaction sum over the electrode nodes alone would read as zero.
        var mesh = Bar(c => c.X < 3e-3 || c.X > 7e-3);
        var output = new ElectricalConductionSolver().Solve(Input(mesh, EndToEnd()));

        double expectedR = 4e-3 / (Fr4.ElectricalConductivity!.Value * Width * Height);
        Assert.Equal(expectedR, output.Summary!["Resistance (Ω)"], expectedR * 1e-3);
        Assert.Equal(Volts * Volts / expectedR, output.Summary["Total power (W)"],
            Volts * Volts / expectedR * 1e-3);

        var phi = ((NodalScalarField)output.Fields.Single(f => f.Name == "Electric potential")).Values;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            double x = mesh.Nodes[n].X;
            double exact = x <= 3e-3 ? 0 : x >= 7e-3 ? Volts : Volts * (x - 3e-3) / 4e-3;
            Assert.Equal(exact, phi[n], 6);
        }
    }

    // ---------------------------------------------------------------- floating conductors

    [Fact]
    public void AConductorThatReachesNoVoltage_FloatsWithTheLaminate()
    {
        // Copper | laminate | copper ISLAND | laminate | copper. The island reaches no
        // electrode; by symmetry it sits at V/2, and it is an equipotential.
        var mesh = Bar(c => c.X < 2e-3 || c.X > 8e-3 || (c.X > 4e-3 && c.X < 6e-3));
        var output = new ElectricalConductionSolver().Solve(Input(mesh, EndToEnd()));

        var phi = ((NodalScalarField)output.Fields.Single(f => f.Name == "Electric potential")).Values;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            double x = mesh.Nodes[n].X;
            if (x < 4e-3 - 1e-9 || x > 6e-3 + 1e-9) continue;
            Assert.Equal(Volts / 2, phi[n], 3);
        }
        Assert.Contains(output.Log, line => line.Contains("floats with the insulator"));

        // The two laminate gaps in series carry the current.
        double expectedR = 4e-3 / (Fr4.ElectricalConductivity!.Value * Width * Height);
        Assert.Equal(expectedR, output.Summary!["Resistance (Ω)"], expectedR * 2e-3);
    }

    [Fact]
    public void ACurrentWithNowhereToGo_IsRefusedByName()
    {
        // Injected into copper that reaches no voltage.
        var isolated = Bar(c => c.X < 3e-3 || c.X > 7e-3);
        var intoIsland = Input(isolated,
            new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
            new CurrentFlow { Name = "Feed", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalCurrent = 1 });
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ElectricalConductionSolver().Validate(intoIsland));
        Assert.Contains("Feed", ex.Message);
        Assert.Contains("reaches no voltage", ex.Message);

        // Injected into the laminate itself.
        var laminateEnd = Bar(c => c.X < 5e-3);
        var intoLaminate = Input(laminateEnd,
            new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
            new CurrentFlow { Name = "Feed", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalCurrent = 1 });
        ex = Assert.Throws<InvalidOperationException>(
            () => new ElectricalConductionSolver().Validate(intoLaminate));
        Assert.Contains("insulator", ex.Message);
    }

    // ---------------------------------------------------------------- what counts as a resistance

    [Fact]
    public void TwoVoltagesAndACurrent_ReportNoResistance()
    {
        // Three terminals: the electrode currents differ, and ΔV over their mean — which is
        // what used to be printed — is the resistance of nothing.
        var mesh = Bar(_ => true);
        var conditions = EndToEnd().Append(new CurrentFlow
        {
            Name = "Tap",
            FaceIds = new[] { StructuredBoxMesh.FaceZMax },
            TotalCurrent = 100
        }).ToArray();
        var output = new ElectricalConductionSolver().Solve(Input(mesh, conditions));

        Assert.False(output.Summary!.ContainsKey("Resistance (Ω)"));
        Assert.Contains(output.Log, line => line.Contains("three-terminal"));

        // Without the tap the same bar reports its resistance.
        var twoTerminal = new ElectricalConductionSolver().Solve(Input(mesh, EndToEnd()));
        double expectedR = Length / (Copper.ElectricalConductivity!.Value * Width * Height);
        Assert.Equal(expectedR, twoTerminal.Summary!["Resistance (Ω)"], expectedR * 1e-9);
    }

    [Fact]
    public void CurrentDrive_SaysItIsAUniformInjection()
    {
        var mesh = Bar(_ => true);
        var output = new ElectricalConductionSolver().Solve(Input(mesh,
            new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
            new CurrentFlow { Name = "Feed", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalCurrent = 2 }));

        // On a bar's end the uniform injection IS the equipotential one, so R is the bar's.
        double expectedR = Length / (Copper.ElectricalConductivity!.Value * Width * Height);
        Assert.Equal(expectedR, output.Summary!["Resistance (Ω)"], expectedR * 1e-9);
        Assert.Contains(output.Log, line => line.Contains("uniform density"));
    }

    // ---------------------------------------------------------------- guards

    [Fact]
    public void AnInvertedElement_IsRefused_NotSubtractedFromTheMatrix()
    {
        var good = StructuredBoxMesh.Build(0, 1, 0, 1, 0, 1, 1, 1, 1);
        var elements = good.Elements.ToList();
        var first = elements[0];
        elements[0] = new Tet4(first.N0, first.N1, first.N3, first.N2);     // two nodes swapped
        var inverted = new FeMesh(good.Nodes, elements, good.BoundaryTriangles);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScalarDiffusionAssembler(inverted, _ => 1.0));
        Assert.Contains("element 0", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARegionWhoseMaterialNameResolvesToNothing_IsAFailureNamingIt()
    {
        var library = new Dictionary<string, Material> { ["Copper"] = Copper };
        Material? Lookup(string name) => library.GetValueOrDefault(name);

        Assert.Null(RegionMaterialResolver.Resolve("board", null, Lookup));
        var resolved = RegionMaterialResolver.Resolve("board",
            new Dictionary<int, string> { [0] = "Copper" }, Lookup);
        Assert.Same(Copper, resolved![0]);

        var ex = Assert.Throws<InvalidOperationException>(() => RegionMaterialResolver.Resolve(
            "board", new Dictionary<int, string> { [0] = "Copper", [1] = "FR4 (renamed)" }, Lookup));
        Assert.Contains("board", ex.Message);
        Assert.Contains("region 1", ex.Message);
        Assert.Contains("FR4 (renamed)", ex.Message);
    }

    // ---------------------------------------------------------------- the two sweeps

    [Fact]
    public void AnAcSweepOfAMetal_SaysItIsTheDcResistance()
    {
        var mesh = Bar(_ => true);
        var settings = new HarmonicElectricSettings { MinFrequency = 1e3, MaxFrequency = 1e6, PointCount = 3 };
        var input = Input(mesh, EndToEnd()) with { RegionMaterials = null, HarmonicElectric = settings };

        var output = new HarmonicElectricSolver().Solve(input);
        string warning = Assert.Single(output.Log, line => line.StartsWith("WARNING"));
        Assert.Contains("DC resistance", warning);
        // δ(1 MHz) in 5.96e7 S/m copper = 65.2 µm, against a 2 mm bar; crossover where
        // δ = 1 mm: f = 1/(π·μ₀·σ·(1 mm)²) = 4.25 kHz.
        Assert.Contains("0.0652 mm", warning);
        Assert.Contains("4.25 kHz", warning);

        // A dielectric sweep is what the solver is for, and gets no such line.
        var dielectric = input with { Material = Fr4 };
        Assert.Null(HarmonicElectricSolver.NoteAllConductors(dielectric, settings));
    }

    [Fact]
    public void TheLumpedSweep_StatesWhereItsConstantResistanceStops()
    {
        // 35 µm copper: δ = 17.5 µm at 14.3 MHz (δ = 66.1 µm·√(1 MHz / f) in 5.8e7 S/m copper).
        double crossover = NetImpedanceEstimator.SkinCrossover(35e-6);
        double depth = 1 / Math.Sqrt(Math.PI * crossover * 4e-7 * Math.PI * 5.8e7);
        Assert.Equal(17.5e-6, depth, 12);
        Assert.InRange(crossover, 14.2e6, 14.4e6);

        var chain = new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(10e-3, 0, 0), 2e-4, 35e-6)
        };
        var wide = NetImpedanceEstimator.Estimate(0.01, chain, 1e6, 1e9, 4);
        Assert.Equal(crossover, wide.SkinCrossoverHz, crossover * 1e-12);
        string line = Assert.Single(wide.Assumptions, a => a.Contains("skin depth"));
        Assert.Contains("2 of 4 points lie ABOVE", line);        // 1 and 10 MHz below; 100 MHz and 1 GHz above

        var low = NetImpedanceEstimator.Estimate(0.01, chain, 1e3, 1e6, 4);
        Assert.Contains(low.Assumptions, a => a.Contains("whole sweep lies below"));
    }
}
