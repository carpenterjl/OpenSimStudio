using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Regression benchmarks for the electro-quasistatic AC solver against the analytic
/// impedance of lossy-dielectric slabs: Z(ω) = d / ((σ + jωε₀εᵣ)·A).
/// </summary>
public class HarmonicElectricBenchmarks
{
    private const double Epsilon0 = 8.8541878128e-12;

    // A lossy dielectric with the σ↔ωε corner at ω = σ/(ε₀εᵣ) ≈ 2.8e6 rad/s (≈ 450 kHz).
    private static readonly Material LossyDielectric = new()
    {
        Name = "Lossy dielectric",
        YoungsModulus = 1e9,
        PoissonRatio = 0.3,
        Density = 1200,
        ElectricalConductivity = 1e-4,
        RelativePermittivity = 4
    };

    private static FeMesh MeshBox(double x, double y, double z, double h) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(x, y, z), new MeshSettings { TargetEdgeLength = h });

    private static SolveInput PlateInput(FeMesh mesh, Material material,
        double fMin, double fMax, int points, double volts = 1.0) => new()
    {
        Mesh = mesh,
        Material = material,
        BoundaryConditions = new BoundaryCondition[]
        {
            new VoltagePotential { Name = "Drive", FaceIds = new[] { 0 }, Volts = volts },
            new VoltagePotential { Name = "Ground", FaceIds = new[] { 1 }, Volts = 0 }
        },
        HarmonicElectric = new HarmonicElectricSettings
        {
            MinFrequency = fMin, MaxFrequency = fMax, PointCount = points
        }
    };

    private static System.Numerics.Complex AnalyticPlateImpedance(FeMesh mesh, Material m,
        double length, double frequency)
    {
        // Effective area from the actual (jittered) mesh volume isolates solver error.
        double area = mesh.TotalVolume() / length;
        var admittivity = new System.Numerics.Complex(
            m.ElectricalConductivity ?? 0,
            2 * Math.PI * frequency * Epsilon0 * (m.RelativePermittivity ?? 1));
        return length / (admittivity * area);
    }

    // ------------------------------------------------------------------
    // Patch-sharp lossy parallel plate. With ONE material and voltage-only BCs the
    // system is A(ω) = (σ + jωε₀εᵣ)·K_geo with a material-independent real K_geo, so
    // Z(ω)·(σ + jωε₀εᵣ) must equal the REAL geometric constant R_dc·σ EXACTLY (to
    // solver precision) at every frequency — magnitude and phase — regardless of the
    // mesher's surface jitter. (The absolute d/(yA) value is only jitter-accurate to
    // ~2%, like the DC resistance benchmark; that physical bound is asserted too.)
    // ------------------------------------------------------------------
    [Fact]
    public void LossyParallelPlate_ImpedanceExactAcrossTheCorner()
    {
        const double length = 0.02, cross = 0.01;
        var mesh = MeshBox(length, cross, cross, 0.005);
        double cornerHz = LossyDielectric.ElectricalConductivity!.Value
                          / (2 * Math.PI * Epsilon0 * LossyDielectric.RelativePermittivity!.Value);

        var dc = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = LossyDielectric,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Drive", FaceIds = new[] { 0 }, Volts = 1 },
                new VoltagePotential { Name = "Ground", FaceIds = new[] { 1 }, Volts = 0 }
            }
        });
        double geometric = dc.Summary!["Resistance (Ω)"] * LossyDielectric.ElectricalConductivity!.Value;

        var output = new HarmonicElectricSolver().Solve(
            PlateInput(mesh, LossyDielectric, cornerHz / 100, cornerHz * 100, 3));

        Assert.Equal(3, output.Frames!.Count);
        foreach (var frame in output.Frames)
        {
            double f = frame.Value;
            var admittivity = new System.Numerics.Complex(
                LossyDielectric.ElectricalConductivity!.Value,
                2 * Math.PI * f * Epsilon0 * LossyDielectric.RelativePermittivity!.Value);
            var sharp = geometric / admittivity;

            double magnitude = frame.Summary!["|Z| (Ω)"];
            double phase = frame.Summary!["Phase (°)"];
            Assert.Equal(sharp.Magnitude, magnitude, sharp.Magnitude * 1e-9);
            Assert.Equal(sharp.Phase * 180 / Math.PI, phase, 1e-6);

            // Physical sanity: the ideal-plate value, jitter-accurate to ~2%.
            var analytic = AnalyticPlateImpedance(mesh, LossyDielectric, length, f);
            Assert.Equal(analytic.Magnitude, magnitude, analytic.Magnitude * 2e-2);
        }
    }

    // ------------------------------------------------------------------
    // ω → 0 limit: at a frequency where ωε₀εᵣ ≪ σ the impedance must equal the DC
    // solver's resistance on the same mesh.
    // ------------------------------------------------------------------
    [Fact]
    public void LowFrequencyLimit_MatchesDcResistance()
    {
        const double length = 0.02, cross = 0.01;
        var mesh = MeshBox(length, cross, cross, 0.005);

        var dc = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = LossyDielectric,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Drive", FaceIds = new[] { 0 }, Volts = 1 },
                new VoltagePotential { Name = "Ground", FaceIds = new[] { 1 }, Volts = 0 }
            }
        });
        double dcResistance = dc.Summary!["Resistance (Ω)"];

        var ac = new HarmonicElectricSolver().Solve(
            PlateInput(mesh, LossyDielectric, 1e-3, 1e-3, 1));

        double acMagnitude = ac.Frames![0].Summary!["|Z| (Ω)"];
        Assert.Equal(dcResistance, acMagnitude, dcResistance * 1e-6);
    }

    // ------------------------------------------------------------------
    // High-frequency limit: when ωε₀εᵣ ≫ σ the slab is a capacitor — phase → −90°.
    // ------------------------------------------------------------------
    [Fact]
    public void HighFrequencyLimit_PhaseApproachesMinus90()
    {
        const double length = 0.02, cross = 0.01;
        var mesh = MeshBox(length, cross, cross, 0.005);
        double cornerHz = LossyDielectric.ElectricalConductivity!.Value
                          / (2 * Math.PI * Epsilon0 * LossyDielectric.RelativePermittivity!.Value);

        var output = new HarmonicElectricSolver().Solve(
            PlateInput(mesh, LossyDielectric, cornerHz * 1000, cornerHz * 1000, 1));

        double phase = output.Frames![0].Summary!["Phase (°)"];
        Assert.InRange(phase, -90.0, -89.5);
    }

    // ------------------------------------------------------------------
    // Two-region series slab (the RegionMaterials path): total impedance must be the
    // series sum Z₁ + Z₂ of the two half-slabs.
    // ------------------------------------------------------------------
    [Fact]
    public void TwoRegionSeriesSlab_MatchesSeriesImpedance()
    {
        const double length = 0.02, cross = 0.01;
        var mesh = MeshBox(length, cross, cross, 0.004);

        var left = LossyDielectric;
        var right = LossyDielectric with
        {
            Name = "Lossier dielectric", ElectricalConductivity = 5e-4, RelativePermittivity = 2
        };
        // Region split at midspan (region 1 = left half, 2 = right half by centroid X).
        var baseMesh = mesh;
        var regionIds = new int[baseMesh.ElementCount];
        for (int e = 0; e < baseMesh.ElementCount; e++)
        {
            var el = baseMesh.Elements[e];
            double cx = (baseMesh.Nodes[el.N0].X + baseMesh.Nodes[el.N1].X
                         + baseMesh.Nodes[el.N2].X + baseMesh.Nodes[el.N3].X) / 4.0;
            regionIds[e] = cx < length / 2 ? 1 : 2;
        }
        mesh = new FeMesh(baseMesh.Nodes, baseMesh.Elements, baseMesh.BoundaryTriangles, regionIds);

        double corner1 = left.ElectricalConductivity!.Value
                         / (2 * Math.PI * Epsilon0 * left.RelativePermittivity!.Value);
        var input = PlateInput(mesh, left, corner1, corner1, 1) with
        {
            RegionMaterials = new Dictionary<int, Material> { [1] = left, [2] = right }
        };
        var output = new HarmonicElectricSolver().Solve(input);

        // Series impedance from the ACTUAL region volumes (jitter-tolerant): each half
        // is a plate of thickness V_region/A with A = V_total/length.
        double area = mesh.TotalVolume() / length;
        double v1 = 0, v2 = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            if (regionIds[e] == 1) v1 += mesh.ElementVolume(e);
            else v2 += mesh.ElementVolume(e);
        }
        System.Numerics.Complex Slab(Material m, double volume)
        {
            var admittivity = new System.Numerics.Complex(
                m.ElectricalConductivity!.Value,
                2 * Math.PI * corner1 * Epsilon0 * m.RelativePermittivity!.Value);
            return volume / area / (admittivity * area);
        }
        var analytic = Slab(left, v1) + Slab(right, v2);

        double magnitude = output.Frames![0].Summary!["|Z| (Ω)"];
        double phase = output.Frames[0].Summary!["Phase (°)"];
        // 3% / 2°: the region interface follows jagged element boundaries, not the exact
        // midplane, so the field is genuinely 3D near the interface — the same tolerance
        // class as the DC TwoRegionSeriesBar benchmark on this fixture.
        Assert.Equal(analytic.Magnitude, magnitude, analytic.Magnitude * 3e-2);
        Assert.Equal(analytic.Phase * 180 / Math.PI, phase, 2.0);
    }

    // ------------------------------------------------------------------
    // Current drive: one injected current against a 0 V ground (the pad-electrode
    // pattern). Z comes from the port power, Z = (Σφᵢfᵢ)/I², which uses the SAME load
    // vector as the DC solver's R = P/I² — so Z(ω)·(σ + jωε₀εᵣ) must equal the DC
    // current-drive R·σ to solver precision at every frequency. The voltage-drive
    // comparison is only ~2%-loose: an equipotential electrode face and a uniform-flux
    // electrode face are physically different fields on a jittered mesh.
    // ------------------------------------------------------------------
    [Fact]
    public void CurrentDrive_ImpedanceExactVsDcAndConsistentWithVoltageDrive()
    {
        const double length = 0.02, cross = 0.01;
        var mesh = MeshBox(length, cross, cross, 0.005);
        double cornerHz = LossyDielectric.ElectricalConductivity!.Value
                          / (2 * Math.PI * Epsilon0 * LossyDielectric.RelativePermittivity!.Value);
        const double injected = 2.5e-3;

        BoundaryCondition[] currentDrive =
        {
            new CurrentFlow { Name = "Drive", FaceIds = new[] { 0 }, TotalCurrent = injected },
            new VoltagePotential { Name = "Ground", FaceIds = new[] { 1 }, Volts = 0 }
        };

        var dc = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = LossyDielectric, BoundaryConditions = currentDrive
        });
        double geometric = dc.Summary!["Resistance (Ω)"] * LossyDielectric.ElectricalConductivity!.Value;

        var ac = new HarmonicElectricSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = LossyDielectric, BoundaryConditions = currentDrive,
            HarmonicElectric = new HarmonicElectricSettings
            {
                MinFrequency = cornerHz / 100, MaxFrequency = cornerHz * 100, PointCount = 3
            }
        });
        var acVoltage = new HarmonicElectricSolver().Solve(
            PlateInput(mesh, LossyDielectric, cornerHz / 100, cornerHz * 100, 3));

        Assert.Equal(3, ac.Frames!.Count);
        for (int i = 0; i < 3; i++)
        {
            var frame = ac.Frames[i];
            double f = frame.Value;
            var admittivity = new System.Numerics.Complex(
                LossyDielectric.ElectricalConductivity!.Value,
                2 * Math.PI * f * Epsilon0 * LossyDielectric.RelativePermittivity!.Value);
            var sharp = geometric / admittivity;

            double magnitude = frame.Summary!["|Z| (Ω)"];
            Assert.Equal(sharp.Magnitude, magnitude, sharp.Magnitude * 1e-9);
            Assert.Equal(sharp.Phase * 180 / Math.PI, frame.Summary!["Phase (°)"], 1e-6);

            double voltageDriven = acVoltage.Frames![i].Summary!["|Z| (Ω)"];
            Assert.Equal(voltageDriven, magnitude, voltageDriven * 2e-2);
        }
    }

    // ------------------------------------------------------------------
    // Frames contract: geometric spacing, Hz unit, first-frequency default fields.
    // ------------------------------------------------------------------
    [Fact]
    public void Frames_GeometricSweep_FirstFrequencyIsDefault()
    {
        var mesh = MeshBox(0.02, 0.01, 0.01, 0.006);
        var output = new HarmonicElectricSolver().Solve(
            PlateInput(mesh, LossyDielectric, 1e3, 1e7, 5));

        Assert.Equal("Frequency", output.FrameAxis);
        Assert.Equal(5, output.Frames!.Count);
        Assert.Same(output.Frames[0].Fields, output.Fields);
        Assert.Equal(1e3, output.Frames[0].Value, 1e-9);
        Assert.Equal(1e7, output.Frames[^1].Value, 1e-2);
        for (int i = 1; i < 5; i++)
        {
            double ratio = output.Frames[i].Value / output.Frames[i - 1].Value;
            Assert.Equal(10.0, ratio, 1e-6);   // (1e7/1e3)^(1/4) per step
            Assert.Equal("Hz", output.Frames[i].Unit);
        }
        Assert.Contains(output.Frames[0].Fields, f => f.Name == "Potential magnitude");
        Assert.Contains(output.Frames[0].Fields, f => f.Name == "Loss density");
    }

    // ------------------------------------------------------------------
    // Validation: the σ-spread ban must fire for conductor+dielectric composites and
    // point at the alternatives; other bad inputs get actionable messages.
    // ------------------------------------------------------------------
    [Fact]
    public void Validate_SigmaSpreadBanAndBadInputs_Throw()
    {
        const double length = 0.02, cross = 0.01;
        var mesh = MeshBox(length, cross, cross, 0.005);
        var solver = new HarmonicElectricSolver();

        var copper = new Material
        {
            Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
            ElectricalConductivity = 5.96e7
        };
        var regionIds = new int[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
            regionIds[e] = e % 2 == 0 ? 1 : 2;
        var regionMesh = new FeMesh(mesh.Nodes, mesh.Elements, mesh.BoundaryTriangles, regionIds);
        var composite = PlateInput(regionMesh, copper, 1e6, 1e6, 1) with
        {
            RegionMaterials = new Dictionary<int, Material> { [1] = copper, [2] = LossyDielectric }
        };
        var ex1 = Assert.Throws<InvalidOperationException>(() => solver.Validate(composite));
        Assert.Contains("copper-only", ex1.Message);
        Assert.Contains("R + jωL", ex1.Message);

        var noSettings = PlateInput(mesh, LossyDielectric, 1e3, 1e6, 5) with { HarmonicElectric = null };
        var ex2 = Assert.Throws<InvalidOperationException>(() => solver.Validate(noSettings));
        Assert.Contains("settings", ex2.Message, StringComparison.OrdinalIgnoreCase);

        var noElectrode = PlateInput(mesh, LossyDielectric, 1e3, 1e6, 5) with
        {
            BoundaryConditions = Array.Empty<BoundaryCondition>()
        };
        var ex3 = Assert.Throws<InvalidOperationException>(() => solver.Validate(noElectrode));
        Assert.Contains("voltage", ex3.Message, StringComparison.OrdinalIgnoreCase);

        var noAcProps = PlateInput(mesh, LossyDielectric with
        {
            ElectricalConductivity = null, RelativePermittivity = null
        }, 1e3, 1e6, 5);
        var ex4 = Assert.Throws<InvalidOperationException>(() => solver.Validate(noAcProps));
        Assert.Contains("RelativePermittivity", ex4.Message);
    }
}
