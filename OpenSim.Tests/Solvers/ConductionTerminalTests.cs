using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// FU-27 (audit F-05, F-11, (d)8): the equipotential current terminal, each material's own
/// current density at an interface node, and bodies joined by an electrical contact. Every
/// fixture is a structured box whose exact field lies in the TET4 space, so the oracles are
/// identities of the discrete problem, not bands.
/// </summary>
public class ConductionTerminalTests
{
    private const double Length = 10e-3, Width = 2e-3, Height = 2e-3;
    private const double Volts = 1.0;
    private const int PadFace = 6;

    private static readonly Material Copper = new()
    {
        Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
        ElectricalConductivity = 5.96e7, ThermalConductivity = 401, SpecificHeat = 385
    };

    private static readonly Material Alloy = new()
    {
        Name = "Resistive alloy", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8000,
        ElectricalConductivity = 1e6, ThermalConductivity = 20, SpecificHeat = 400
    };

    private static readonly Material Fr4 = new()
    {
        Name = "FR4", YoungsModulus = 22e9, PoissonRatio = 0.15, Density = 1850,
        ElectricalConductivity = 1e-14, RelativePermittivity = 4.4, ThermalConductivity = 0.29, SpecificHeat = 1100
    };

    /// <summary>A 10 × 2 × 2 mm bar, 10 × 2 × 4 cells; region 0 where <paramref name="isFirst"/>
    /// says so (by centroid), region 1 elsewhere. The far half of the top face (x ≥ 5 mm) is
    /// re-tagged as its own face, <see cref="PadFace"/>: a pad that feeds the bar from one side.</summary>
    private static FeMesh Bar(Func<Vector3D, bool>? isFirst = null)
    {
        var plain = StructuredBoxMesh.Build(0, Length, 0, Width, 0, Height, 10, 2, 4);
        var regions = new int[plain.ElementCount];
        for (int e = 0; e < regions.Length; e++)
        {
            var el = plain.Elements[e];
            var centroid = (plain.Nodes[el.N0] + plain.Nodes[el.N1] + plain.Nodes[el.N2] + plain.Nodes[el.N3]) * 0.25;
            regions[e] = isFirst is null || isFirst(centroid) ? 0 : 1;
        }
        var skin = plain.BoundaryTriangles.Select(t =>
            t.FaceId == StructuredBoxMesh.FaceZMax
            && plain.Nodes[t.A].X >= 5e-3 - 1e-9 && plain.Nodes[t.B].X >= 5e-3 - 1e-9 && plain.Nodes[t.C].X >= 5e-3 - 1e-9
                ? new BoundaryTriangle(t.A, t.B, t.C, PadFace)
                : t).ToList();
        return new FeMesh(plain.Nodes, plain.Elements, skin, isFirst is null ? null : regions);
    }

    private static SolveInput Input(FeMesh mesh, Material second, params BoundaryCondition[] conditions) => new()
    {
        Mesh = mesh,
        Material = Copper,
        RegionMaterials = mesh.ElementRegionIds is null ? null : new Dictionary<int, Material> { [0] = Copper, [1] = second },
        BoundaryConditions = conditions
    };

    private static VoltagePotential Ground() =>
        new() { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 };

    private static double Resistance(SolveOutput output) => output.Summary!["Resistance (Ω)"];

    // ---------------------------------------------------------------- the equipotential terminal

    [Fact]
    public void AnEquipotentialTerminal_IsTheVoltageDrive_WhereAUniformInjectionIsNot()
    {
        // A pad on the far half of the top face feeds the bar toward its grounded end. Driven by
        // a voltage the pad is one equipotential; driven by a current entering at uniform
        // density it is not, and the spreading term reads above. The equipotential terminal
        // IS the voltage drive's discrete problem (tie the pad nodes, load the total): the two
        // resistances agree to solver precision, not within a band.
        var mesh = Bar();
        var solver = new ElectricalConductionSolver();
        const double amps = 2.5;

        var byVoltage = solver.Solve(Input(mesh, Copper, Ground(),
            new VoltagePotential { Name = "Pad", FaceIds = new[] { PadFace }, Volts = Volts }));
        var byTerminal = solver.Solve(Input(mesh, Copper, Ground(),
            new CurrentFlow { Name = "Pad", FaceIds = new[] { PadFace }, TotalCurrent = amps, Equipotential = true }));
        var byUniform = solver.Solve(Input(mesh, Copper, Ground(),
            new CurrentFlow { Name = "Pad", FaceIds = new[] { PadFace }, TotalCurrent = amps }));

        double rv = Resistance(byVoltage), rt = Resistance(byTerminal), ru = Resistance(byUniform);
        Assert.Equal(rv, rt, rv * 1e-7);
        Assert.Equal(rt * amps, byTerminal.Summary!["Terminal 'Pad' potential (V)"], rt * amps * 1e-7);
        Assert.Equal(amps, byTerminal.Summary["Current (A)"], 12);
        Assert.True(ru > rt * (1 + 1e-3), $"uniform injection {ru:g6} Ω should read above the terminal's {rt:g6} Ω");
        Assert.Contains(byTerminal.Log, l => l.Contains("equipotential terminal"));
        Assert.Contains(byUniform.Log, l => l.Contains("uniform density"));

        // The terminal face is one potential in the field, and the pad's reactions carry the
        // whole current — K·φ summed over the pad nodes is the injected total.
        var phi = ((NodalScalarField)byTerminal.Fields.Single(f => f.Name == "Electric potential")).Values;
        var padNodes = mesh.GetFaceNodes(new[] { PadFace });
        double padVolts = phi[padNodes.First()];
        foreach (int n in padNodes) Assert.Equal(padVolts, phi[n], 12);
        var stiffness = new ScalarDiffusionAssembler(mesh, _ => Copper.ElectricalConductivity!.Value).AssembleStiffness();
        var reaction = new double[mesh.NodeCount];
        stiffness.Multiply(phi.ToArray(), reaction);
        Assert.Equal(amps, padNodes.Sum(n => reaction[n]), amps * 1e-8);
    }

    [Fact]
    public void AnEquipotentialTerminal_OnCopperOverLaminate_GoesThroughTheStagedSolve()
    {
        // Copper is the top quarter of the bar on FR4; the pad sits on the copper. Same identity
        // as above, now with the insulators solved in the second stage.
        var mesh = Bar(c => c.Z > 1.5e-3);
        var solver = new ElectricalConductionSolver();

        var byVoltage = solver.Solve(Input(mesh, Fr4, Ground(),
            new VoltagePotential { Name = "Pad", FaceIds = new[] { PadFace }, Volts = Volts }));
        var byTerminal = solver.Solve(Input(mesh, Fr4, Ground(),
            new CurrentFlow { Name = "Pad", FaceIds = new[] { PadFace }, TotalCurrent = 1, Equipotential = true }));

        Assert.Contains(byTerminal.Log, l => l.Contains("insulators"));
        Assert.Equal(Resistance(byVoltage), Resistance(byTerminal), Resistance(byVoltage) * 1e-7);
    }

    [Fact]
    public void ATerminalThatTouchesAVoltage_OrAnotherTerminal_IsRefusedByName()
    {
        var mesh = Bar();
        var solver = new ElectricalConductionSolver();

        // The whole top face shares its x = 0 edge with the grounded end.
        var touching = Input(mesh, Copper, Ground(),
            new CurrentFlow { Name = "Top", FaceIds = new[] { StructuredBoxMesh.FaceZMax, PadFace }, TotalCurrent = 1, Equipotential = true });
        var ex = Assert.Throws<InvalidOperationException>(() => solver.Validate(touching));
        Assert.Contains("Top", ex.Message);
        Assert.Contains("Ground", ex.Message);

        // Two terminals whose faces meet would be one equipotential carrying two currents.
        var twin = Input(mesh, Copper, Ground(),
            new CurrentFlow { Name = "Pad", FaceIds = new[] { PadFace }, TotalCurrent = 1, Equipotential = true },
            new CurrentFlow { Name = "End", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalCurrent = 1, Equipotential = true });
        ex = Assert.Throws<InvalidOperationException>(() => solver.Validate(twin));
        Assert.Contains("Pad", ex.Message);
        Assert.Contains("End", ex.Message);
        Assert.Contains("one equipotential", ex.Message);

        // Unchanged: a uniform injection may share an edge with a voltage.
        solver.Validate(Input(mesh, Copper, Ground(),
            new CurrentFlow { Name = "Top", FaceIds = new[] { StructuredBoxMesh.FaceZMax, PadFace }, TotalCurrent = 1 }));
    }

    // ---------------------------------------------------------------- per-side values at interface nodes

    [Fact]
    public void AParallelBar_ShowsEachMaterialsOwnCurrentDensityAtTheInterface()
    {
        // Copper on laminate along the bar, electrodes across both ends: φ = V·x/L in both, so
        // J = σ·V/L on each side — 10²¹ apart at the same node. The nodal field shows the
        // copper's; the sides carry both (audit F-05's parallel bar).
        var mesh = Bar(c => c.Z > 1.5e-3);
        var output = new ElectricalConductionSolver().Solve(Input(mesh, Fr4, Ground(),
            new VoltagePotential { Name = "Supply", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = Volts }));

        var current = (NodalVectorField)output.Fields.Single(f => f.Name == "Current density");
        var power = (NodalScalarField)output.Fields.Single(f => f.Name == "Power density");
        Assert.NotNull(current.Sides);
        Assert.NotNull(power.Sides);
        Assert.Equal(new[] { "Copper", "FR4" }, current.Sides!.Materials.OrderBy(m => m));

        double field = Volts / Length;
        double copperJ = Copper.ElectricalConductivity!.Value * field;
        double laminateJ = Fr4.ElectricalConductivity!.Value * field;
        int interfaceNodes = 0;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            bool onInterface = Math.Abs(mesh.Nodes[n].Z - 1.5e-3) < 1e-9;
            Assert.Equal(onInterface, current.Sides.IsInterface(n));
            if (!onInterface)
            {
                Assert.Empty(current.Sides.At(n));
                continue;
            }
            interfaceNodes++;
            var sides = current.Sides.At(n);
            Assert.Equal(2, sides.Count);
            Assert.Equal(copperJ, current.Sides.Of(n, "Copper")!.Value.Length, copperJ * 1e-2);
            Assert.Equal(laminateJ, current.Sides.Of(n, "FR4")!.Value.Length, laminateJ * 1e-2);
            Assert.Equal(copperJ, current.GetScalar(n), copperJ * 1e-2);          // the larger side is shown
            Assert.Equal(copperJ * field, power.Sides!.Of(n, "Copper")!.Value, copperJ * field * 1e-2);
            Assert.Equal(laminateJ * field, power.Sides.Of(n, "FR4")!.Value, laminateJ * field * 1e-2);
            Assert.Null(current.Sides.Of(n, "Gold"));
        }
        Assert.Equal(33, interfaceNodes);                                        // 11 × 3 nodes on z = 1.5 mm
        Assert.Equal(interfaceNodes, current.Sides.Count);
        Assert.Contains(output.Log, l => l.Contains("each material's own value"));
    }

    [Fact]
    public void ASeriesBar_CarriesTheSameCurrentDensityOnBothSides()
    {
        // Copper then a resistive alloy along the bar (one system: the ratio is 60, not 10²¹).
        // J is continuous across the interface while E jumps by the ratio — both sides read J.
        var mesh = Bar(c => c.X < 5e-3);
        var output = new ElectricalConductionSolver().Solve(Input(mesh, Alloy, Ground(),
            new VoltagePotential { Name = "Supply", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = Volts }));

        double j = Volts / (5e-3 / Copper.ElectricalConductivity!.Value + 5e-3 / Alloy.ElectricalConductivity!.Value);
        var current = (NodalVectorField)output.Fields.Single(f => f.Name == "Current density");
        int interfaceNodes = 0;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            if (Math.Abs(mesh.Nodes[n].X - 5e-3) > 1e-9) continue;
            interfaceNodes++;
            Assert.Equal(j, current.Sides!.Of(n, "Copper")!.Value.Length, j * 1e-2);
            Assert.Equal(j, current.Sides.Of(n, "Resistive alloy")!.Value.Length, j * 1e-2);
        }
        Assert.Equal(15, interfaceNodes);                                        // 3 × 5 nodes on x = 5 mm
        Assert.Equal(j * Width * Height, output.Summary!["Current (A)"], j * Width * Height * 1e-6);
    }

    // ---------------------------------------------------------------- electrical contacts

    private const double L1 = 0.02, L2 = 0.03, W = 0.01, Area = W * W;
    private const double Joint = 1e7;   // S/m²: a 1 mΩ joint over the 1 cm² face

    private static Material Conductor(string name, double sigma) => new()
    {
        Name = name, YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8900,
        ElectricalConductivity = sigma, ThermalConductivity = 400, SpecificHeat = 385
    };

    private static readonly Material Left = Conductor("Left bar", 5.96e7);
    private static readonly Material Right = Conductor("Right bar", 1e6);

    /// <summary>Two bars end to end along x, meshed independently, a voltage on each far end.</summary>
    private static List<Body> Bars(int n1, int t1, int n2, int t2, bool rightVoltage = true)
    {
        var left = StructuredBoxMesh.Box("Left", 0, L1, 0, W, 0, W, n1, t1, t1, Left);
        var right = StructuredBoxMesh.Box("Right", L1, L1 + L2, 0, W, 0, W, n2, t2, t2, Right);
        left.BoundaryConditions.Add(new VoltagePotential
            { Name = "Drive", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = Volts });
        if (rightVoltage)
            right.BoundaryConditions.Add(new VoltagePotential
                { Name = "Return", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = 0 });
        return new List<Body> { left, right };
    }

    private static (SolveOutput Output, FeMeshAssembler.AssembledMesh Assembled) SolveBars(
        int n1, int t1, int n2, int t2, bool withContacts = true, bool rightVoltage = true)
    {
        var assembled = FeMeshAssembler.Assemble(Bars(n1, t1, n2, t2, rightVoltage));
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases,
            new ContactDetectionSettings { DefaultConductance = Joint });
        Assert.NotEmpty(contacts);
        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = Left,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ElectricalContacts = withContacts ? contacts : null
        });
        return (output, assembled);
    }

    private static double SeriesResistance() =>
        L1 / (Left.ElectricalConductivity!.Value * Area) + 1 / (Joint * Area) + L2 / (Right.ElectricalConductivity!.Value * Area);

    [Fact]
    public void TwoBarsPressedTogether_ConductThroughTheirJoint_ExactlyOnAMatchingInterface()
    {
        // R = L₁/σ₁A + 1/GA + L₂/σ₂A is an identity of the discrete problem on a matching
        // interface: the exact field is linear in each bar with a constant jump at the joint,
        // and the vertex quadrature integrates it exactly (the thermal contact's argument).
        var (output, assembled) = SolveBars(2, 2, 3, 2);

        double expected = SeriesResistance();
        Assert.Equal(expected, Resistance(output), expected * 1e-9);
        Assert.Equal(Volts * Volts / expected, output.Summary!["Total power (W)"], Volts * Volts / expected * 1e-9);
        Assert.Contains(output.Log, l => l.Contains("Electrical contact bodies 0–1"));
        Assert.Contains(output.Log, l => l.Contains("Dissipated in the contacts"));

        // The jump across the joint is the current times the joint's resistance.
        var phi = ((NodalScalarField)output.Fields.Single(f => f.Name == "Electric potential")).Values;
        double leftFace = Plane(assembled, phi, 0, L1), rightFace = Plane(assembled, phi, 1, L1);
        double amps = Volts / expected;
        Assert.Equal(amps / (Joint * Area), leftFace - rightFace, amps / (Joint * Area) * 1e-9);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(4, 6)]
    public void ANonMatchingJoint_IsWithinTheVertexRulesError(int left, int right)
    {
        // Node-to-surface coupling on meshes that share no node: first-order at the interface,
        // the same band the thermal contact is held to (0.05 % at 2×3, 0.013 % at 4×6).
        var (output, _) = SolveBars(2, left, 3, right);
        double error = Math.Abs(Resistance(output) - SeriesResistance()) / SeriesResistance();
        Assert.True(error < 1e-3, $"non-matching joint is off by {error:P3}");
    }

    [Fact]
    public void WithoutAContact_TheBarsExchangeNoCurrent()
    {
        // The merged mesh shares no nodes across bodies: with no contact each bar sits at its
        // own voltage and nothing flows, so no resistance is reported.
        var (output, _) = SolveBars(2, 2, 3, 2, withContacts: false);
        Assert.False(output.Summary!.ContainsKey("Resistance (Ω)"));
        Assert.True(output.Summary["Total power (W)"] < 1e-12 * Volts * Volts / SeriesResistance());
        Assert.Contains(output.Log, l => l.Contains("nothing joins"));
    }

    [Fact]
    public void AContactAnchorsABarThatCarriesNoVoltageOfItsOwn()
    {
        // The right bar reaches a voltage only THROUGH the joint: it is part of the left bar's
        // piece, not a floating conductor, and sits at the drive voltage with nothing flowing.
        var (output, assembled) = SolveBars(2, 2, 3, 2, rightVoltage: false);
        var phi = ((NodalScalarField)output.Fields.Single(f => f.Name == "Electric potential")).Values;
        for (int n = 0; n < assembled.Mesh.NodeCount; n++)
            Assert.Equal(Volts, phi[n], 9);
        Assert.DoesNotContain(output.Log, l => l.Contains("reaches no voltage"));

        // Without the contact the right bar floats, and the solve says so.
        var apart = FeMeshAssembler.Assemble(Bars(2, 2, 3, 2, rightVoltage: false));
        var floating = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = apart.Mesh, Material = Left, RegionMaterials = apart.RegionMaterials,
            BoundaryConditions = apart.BoundaryConditions
        });
        Assert.Contains(floating.Log, l => l.Contains("region(s) 1") && l.Contains("reaches no voltage"));
    }

    /// <summary>Mean potential over one body's nodes on the plane x = value, asserting they agree.</summary>
    private static double Plane(FeMeshAssembler.AssembledMesh assembled, IReadOnlyList<double> phi, int body, double x)
    {
        var values = new List<double>();
        for (int n = 0; n < assembled.Mesh.NodeCount; n++)
            if (assembled.BodyOfNode(n) == body && Math.Abs(assembled.Mesh.Nodes[n].X - x) < 1e-12)
                values.Add(phi[n]);
        Assert.NotEmpty(values);
        Assert.True(values.Max() - values.Min() < 1e-9 * Volts, $"potential on x = {x} of body {body} is not uniform");
        return values.Average();
    }
}
