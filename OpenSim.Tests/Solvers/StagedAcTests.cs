using System.Numerics;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// FU-27: the AC solver no longer refuses conductor + dielectric. Copper plates in a lossy
/// dielectric are the capacitor they form (with the laminate's leakage), solved in two
/// stages wherever the admittivity spread exceeds 10⁸ and as one system where ωε has
/// closed it. Structured boxes: the exact field is one-dimensional and lies in the TET4
/// space, so the gates are solver precision.
/// </summary>
public class StagedAcTests
{
    private const double Epsilon0 = 8.8541878128e-12;
    private const double Length = 10e-3, Width = 2e-3, Height = 2e-3, Area = Width * Height;
    private const double Volts = 1.0;

    private static readonly Material Copper = new()
    {
        Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960, ElectricalConductivity = 5.96e7
    };

    // σ↔ωε corner at ω = σ/(ε₀εᵣ) ≈ 2.8e6 rad/s (≈ 450 kHz).
    private static readonly Material Dielectric = new()
    {
        Name = "Lossy dielectric", YoungsModulus = 1e9, PoissonRatio = 0.3, Density = 1200,
        ElectricalConductivity = 1e-4, RelativePermittivity = 4
    };

    private static Complex Admittivity(Material m, double f) =>
        new(m.ElectricalConductivity ?? 0, 2 * Math.PI * f * Epsilon0 * (m.RelativePermittivity ?? 1));

    /// <summary>A 10 × 2 × 2 mm bar, 10 × 2 × 4 cells; region 0 = copper where <paramref name="isCopper"/>
    /// says so (by centroid), region 1 = the dielectric.</summary>
    private static FeMesh Bar(Func<double, bool> isCopper)
    {
        var plain = StructuredBoxMesh.Build(0, Length, 0, Width, 0, Height, 10, 2, 4);
        var regions = new int[plain.ElementCount];
        for (int e = 0; e < regions.Length; e++)
        {
            var el = plain.Elements[e];
            double x = (plain.Nodes[el.N0].X + plain.Nodes[el.N1].X + plain.Nodes[el.N2].X + plain.Nodes[el.N3].X) * 0.25;
            regions[e] = isCopper(x) ? 0 : 1;
        }
        return new FeMesh(plain.Nodes, plain.Elements, plain.BoundaryTriangles, regions);
    }

    private static SolveInput Input(FeMesh mesh, double frequency, params BoundaryCondition[] conditions) => new()
    {
        Mesh = mesh,
        Material = Copper,
        RegionMaterials = new Dictionary<int, Material> { [0] = Copper, [1] = Dielectric },
        BoundaryConditions = conditions,
        HarmonicElectric = new HarmonicElectricSettings { MinFrequency = frequency, MaxFrequency = frequency, PointCount = 1 }
    };

    private static BoundaryCondition[] EndToEnd() => new BoundaryCondition[]
    {
        new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
        new VoltagePotential { Name = "Drive", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = Volts }
    };

    private static (double Magnitude, double PhaseDegrees) Impedance(SolveOutput output) =>
        (output.Frames![0].Summary!["|Z| (Ω)"], output.Frames[0].Summary!["Phase (°)"]);

    [Theory]
    [InlineData(4.5e3, true)]
    [InlineData(450e3, true)]
    [InlineData(45e6, true)]
    [InlineData(3e9, false)]
    public void CopperPlatesInALossyDielectric_AreTheCapacitorTheyForm(double frequency, bool staged)
    {
        // Copper | dielectric | copper along the bar: Z = 2·L_cu/(σ_cu·A) + d/((σ_d + jωε_d)·A).
        // Below 2.7 GHz the admittivity spans more than 1e8 and the solve is staged — the
        // dielectric's displacement and conduction current is drawn from the plates' surfaces
        // and credited to the electrodes; at 3 GHz ωε_d has closed the spread to 9e7 and the
        // same mesh is one system. Either way the answer is the sandwich's.
        var mesh = Bar(x => x < 3e-3 || x > 7e-3);
        var output = new HarmonicElectricSolver().Solve(Input(mesh, frequency, EndToEnd()));

        var analytic = 2 * 3e-3 / (Admittivity(Copper, frequency) * Area) + 4e-3 / (Admittivity(Dielectric, frequency) * Area);
        var (magnitude, phase) = Impedance(output);
        Assert.Equal(analytic.Magnitude, magnitude, analytic.Magnitude * 1e-6);
        Assert.Equal(analytic.Phase * 180 / Math.PI, phase, 1e-4);
        Assert.Contains(output.Log, l => l.Contains(staged ? "iterations, staged" : "iterations, one system"));
        Assert.DoesNotContain(output.Log, l => l.StartsWith("WARNING"));

        // The plates are equipotentials and the dielectric's potential is linear across the gap.
        var potential = ((NodalScalarField)output.Frames![0].Fields.Single(f => f.Name == "Potential magnitude")).Values;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            double x = mesh.Nodes[n].X;
            double exact = x <= 3e-3 + 1e-9 ? 0 : x >= 7e-3 - 1e-9 ? Volts : Volts * (x - 3e-3) / 4e-3;
            Assert.Equal(exact, potential[n], 1e-6);
        }
    }

    [Fact]
    public void AFloatingCopperIsland_DividesTheDielectric()
    {
        // Copper | dielectric | copper ISLAND | dielectric | copper. The island reaches no
        // electrode: an equipotential at V/2 by symmetry, and the two 2 mm gaps in series
        // are one 4 mm gap.
        const double frequency = 450e3;
        var mesh = Bar(x => x < 2e-3 || x > 8e-3 || (x > 4e-3 && x < 6e-3));
        var output = new HarmonicElectricSolver().Solve(Input(mesh, frequency, EndToEnd()));

        var analytic = 4e-3 / (Admittivity(Dielectric, frequency) * Area);
        var (magnitude, phase) = Impedance(output);
        // The island stands 1e4 above the dielectric's admittivity, an equipotential to 1e-4.
        Assert.Equal(analytic.Magnitude, magnitude, analytic.Magnitude * 2e-4);
        Assert.Equal(analytic.Phase * 180 / Math.PI, phase, 2e-2);

        var potential = ((NodalScalarField)output.Frames![0].Fields.Single(f => f.Name == "Potential magnitude")).Values;
        var phaseField = ((NodalScalarField)output.Frames[0].Fields.Single(f => f.Name == "Potential phase")).Values;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            double x = mesh.Nodes[n].X;
            if (x < 4e-3 - 1e-9 || x > 6e-3 + 1e-9) continue;
            Assert.Equal(Volts / 2, potential[n], 1e-3);
            Assert.Equal(0, phaseField[n], 1e-1);
        }
        Assert.Contains(output.Log, l => l.Contains("floats with the dielectric"));
    }

    private const int PadFace = 6;

    /// <summary>The bar with the top-face triangles over 1 mm ≤ x ≤ 2 mm re-tagged as
    /// <see cref="PadFace"/>: a pad on the left plate that shares no node with its x = 0 end.</summary>
    private static FeMesh BarWithPad(Func<double, bool> isCopper)
    {
        var mesh = Bar(isCopper);
        var skin = mesh.BoundaryTriangles.Select(t =>
            t.FaceId == StructuredBoxMesh.FaceZMax
            && new[] { t.A, t.B, t.C }.All(n => mesh.Nodes[n].X >= 1e-3 - 1e-9 && mesh.Nodes[n].X <= 2e-3 + 1e-9)
                ? new BoundaryTriangle(t.A, t.B, t.C, PadFace)
                : t).ToList();
        return new FeMesh(mesh.Nodes, mesh.Elements, skin, mesh.ElementRegionIds);
    }

    [Fact]
    public void AnEquipotentialTerminal_OnAPlate_AtLowFrequency_IsTheDcTerminal()
    {
        // A pad on the grounded left plate fed by a current terminal, the right plate also at
        // 0 V: through the staged solve, at a frequency where ωε_d ≪ σ_d, the AC impedance is
        // the DC solver's equipotential-terminal resistance on the same mesh (copper path to
        // the x = 0 end, with the dielectric's leakage in parallel in both solves).
        var mesh = BarWithPad(x => x < 3e-3 || x > 7e-3);
        BoundaryCondition[] conditions =
        {
            new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
            new VoltagePotential { Name = "Far", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = 0 },
            new CurrentFlow { Name = "Pad", FaceIds = new[] { PadFace }, TotalCurrent = 1, Equipotential = true }
        };
        var dc = new ElectricalConductionSolver().Solve(Input(mesh, 1, conditions) with { HarmonicElectric = null });
        var ac = new HarmonicElectricSolver().Solve(Input(mesh, 1, conditions));

        double resistance = dc.Summary!["Resistance (Ω)"];
        var (magnitude, phase) = Impedance(ac);
        Assert.Contains(dc.Log, l => l.Contains("insulators"));
        Assert.Contains(ac.Log, l => l.Contains("iterations, staged"));
        Assert.Equal(resistance, magnitude, resistance * 1e-7);
        Assert.Equal(0, phase, 1e-4);
    }

    [Fact]
    public void ACurrentIntoAPlateThatReachesNoVoltage_IsRefused_TheDcRule()
    {
        // The capacitor "fed" by a current on the far plate: as in the DC solve, a current
        // must land on a conductor that reaches a voltage — stage one holds every other node.
        // Drive that plate with a voltage instead; the impedance is the same two-terminal value.
        var mesh = Bar(x => x < 3e-3 || x > 7e-3);
        var ex = Assert.Throws<InvalidOperationException>(() => new HarmonicElectricSolver().Validate(Input(mesh, 450e3,
            new VoltagePotential { Name = "Ground", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = 0 },
            new CurrentFlow { Name = "Drive", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalCurrent = 1e-3, Equipotential = true })));
        Assert.Contains("Drive", ex.Message);
        Assert.Contains("reaches no voltage", ex.Message);
    }

    [Fact]
    public void TwoDielectricBlocks_WithAContact_AddTheJointsResistance()
    {
        // Two lossy-dielectric bars end to end, meshed independently, joined by an electrical
        // contact G: Z = L₁/(yA) + L₂/(yA) + 1/(GA), exact on a matching interface.
        const double l1 = 0.02, l2 = 0.03, w = 0.01, area = w * w, joint = 1e-2, frequency = 450e3;
        var left = StructuredBoxMesh.Box("Left", 0, l1, 0, w, 0, w, 2, 2, 2, Dielectric);
        var right = StructuredBoxMesh.Box("Right", l1, l1 + l2, 0, w, 0, w, 3, 2, 2, Dielectric);
        left.BoundaryConditions.Add(new VoltagePotential { Name = "Drive", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Volts = Volts });
        right.BoundaryConditions.Add(new VoltagePotential { Name = "Return", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Volts = 0 });
        var assembled = FeMeshAssembler.Assemble(new List<Body> { left, right });
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases,
            new ContactDetectionSettings { DefaultConductance = joint });
        Assert.NotEmpty(contacts);

        var output = new HarmonicElectricSolver().Solve(new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = Dielectric,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ElectricalContacts = contacts,
            HarmonicElectric = new HarmonicElectricSettings { MinFrequency = frequency, MaxFrequency = frequency, PointCount = 1 }
        });

        var y = Admittivity(Dielectric, frequency);
        var analytic = (l1 + l2) / (y * area) + 1 / (joint * area);
        var (magnitude, phase) = Impedance(output);
        Assert.Equal(analytic.Magnitude, magnitude, analytic.Magnitude * 1e-9);
        Assert.Equal(analytic.Phase * 180 / Math.PI, phase, 1e-7);
        Assert.Contains(output.Log, l => l.Contains("Electrical contact bodies 0–1"));
    }
}
