using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Regression benchmarks for the modal solver against analytical rod/beam frequencies.
/// </summary>
public class ModalSolverBenchmarks
{
    private static FeMesh MeshBox(double x, double y, double z, double h) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(x, y, z), new MeshSettings { TargetEdgeLength = h });

    private static readonly Material Steel = new()
    {
        Name = "Steel", YoungsModulus = 200e9, PoissonRatio = 0.3, Density = 7850
    };

    private static SolveInput CantileverInput(FeMesh mesh, Material material, int modes) => new()
    {
        Mesh = mesh,
        Material = material,
        BoundaryConditions = new BoundaryCondition[]
        {
            new FixedSupport { Name = "Wall", FaceIds = new[] { 0 } }
        },
        Modal = new ModalSettings { ModeCount = modes }
    };

    private static double ModeFrequency(SolveOutput output, int index) =>
        output.Frames![index].Summary!["Frequency (Hz)"];

    // ------------------------------------------------------------------
    // Fixed-free axial rod with ν = 0: u_x = sin(πx/2L) uniform over the cross-section
    // is an EXACT 3D eigenmode (no Poisson coupling), so the 1D result
    // f = (1/4L)·√(E/ρ) applies without slenderness correction. The axial mode is not
    // the lowest (bending pairs come first) — it is selected by axial dominance.
    // ------------------------------------------------------------------
    [Fact]
    public void AxialRod_FundamentalAxialFrequency_Within3Percent()
    {
        const double length = 0.5, side = 0.1;
        var material = Steel with { PoissonRatio = 0.0 };
        var mesh = MeshBox(length, side, side, 0.04);

        var output = new ModalAnalysisSolver().Solve(CantileverInput(mesh, material, 10));

        int axialMode = -1;
        double bestDominance = 0;
        for (int i = 0; i < output.Frames!.Count; i++)
        {
            var shape = (NodalVectorField)output.Frames[i].Fields.Single(f => f.Name == "Mode shape");
            double axial = 0, total = 0;
            foreach (var v in shape.Values)
            {
                axial += v.X * v.X;
                total += v.LengthSquared;
            }
            double dominance = total > 0 ? axial / total : 0;
            if (dominance > bestDominance)
            {
                bestDominance = dominance;
                axialMode = i;
            }
        }
        Assert.True(bestDominance > 0.8,
            $"No predominantly axial mode among the first 10 (best dominance {bestDominance:g3}).");

        double analytic = Math.Sqrt(material.YoungsModulus / material.Density) / (4 * length);
        double numeric = ModeFrequency(output, axialMode);
        Assert.Equal(analytic, numeric, analytic * 3e-2);
    }

    // ------------------------------------------------------------------
    // TET4 cantilever bending vs Euler–Bernoulli f₁ = (1.875²/2π)·√(EI/(ρAL⁴)).
    // TET4 locks in bending (too stiff), so the numeric frequency comes out HIGH —
    // the band is one-sided by design, mirroring the static TET4 compliance band
    // [0.40, 1.05] (frequency ~ 1/√compliance ⇒ up to ~1.6).
    // ------------------------------------------------------------------
    [Fact]
    public void CantileverBending_Tet4_WithinDocumentedOneSidedBand()
    {
        const double length = 0.5, side = 0.1;
        var mesh = MeshBox(length, side, side, 0.03);

        var output = new ModalAnalysisSolver().Solve(CantileverInput(mesh, Steel, 2));

        double inertia = Math.Pow(side, 4) / 12;
        double area = side * side;
        double analytic = Math.Pow(1.8751040687, 2) / (2 * Math.PI)
                          * Math.Sqrt(Steel.YoungsModulus * inertia
                                      / (Steel.Density * area * Math.Pow(length, 4)));
        double ratio = ModeFrequency(output, 0) / analytic;
        Assert.InRange(ratio, 0.95, 1.65);
    }

    // ------------------------------------------------------------------
    // TET10 cantilever: quadratic elements resolve bending, so the band tightens to
    // [0.95, 1.06] (from the static TET10 compliance band [0.92, 1.03]). This also
    // exercises the degree-5 TET10 mass end-to-end: an indefinite or misweighted mass
    // would break the eigensolve or throw the frequency far outside the band.
    // ------------------------------------------------------------------
    [Fact]
    public void CantileverBending_Tet10_WithinTightBand()
    {
        const double length = 0.5, side = 0.1;
        var mesh = QuadraticMeshBuilder.Upgrade(MeshBox(length, side, side, 0.035));

        var output = new ModalAnalysisSolver().Solve(CantileverInput(mesh, Steel, 2));

        double inertia = Math.Pow(side, 4) / 12;
        double area = side * side;
        double analytic = Math.Pow(1.8751040687, 2) / (2 * Math.PI)
                          * Math.Sqrt(Steel.YoungsModulus * inertia
                                      / (Steel.Density * area * Math.Pow(length, 4)));
        double ratio = ModeFrequency(output, 0) / analytic;
        Assert.InRange(ratio, 0.95, 1.06);
    }

    // ------------------------------------------------------------------
    // TET10 consistent mass: the degree-5 rule must reproduce the total mass exactly
    // (partition of unity: Σᵢⱼ ∫NᵢNⱼ dV = V), independently on each axis.
    // ------------------------------------------------------------------
    [Fact]
    public void Tet10Mass_TotalMassExact()
    {
        var mesh = QuadraticMeshBuilder.Upgrade(MeshBox(0.06, 0.04, 0.02, 0.015));
        var mass = new Tet10Assembler(mesh, Steel).AssembleMass();

        // Row sums per axis: (M·1)_i summed over the axis's DOFs = ρ·V.
        double expected = Steel.Density * mesh.TotalVolume();
        for (int axis = 0; axis < 3; axis++)
        {
            double total = 0;
            for (int row = axis; row < mass.RowCount; row += 3)
                for (int k = mass.RowPointers[row]; k < mass.RowPointers[row + 1]; k++)
                    total += mass.Values[k];
            Assert.Equal(expected, total, expected * 1e-12);
        }
    }

    // ------------------------------------------------------------------
    // Frames contract + normalization: one frame per mode, mode-1 default, unit-max
    // shapes, ascending frequencies.
    // ------------------------------------------------------------------
    [Fact]
    public void Frames_OneFramePerMode_UnitMaxShapes_AscendingFrequencies()
    {
        var mesh = MeshBox(0.2, 0.05, 0.05, 0.025);
        var output = new ModalAnalysisSolver().Solve(CantileverInput(mesh, Steel, 4));

        Assert.Equal("Mode", output.FrameAxis);
        Assert.Equal(4, output.Frames!.Count);
        Assert.Same(output.Frames[0].Fields, output.Fields);

        double previous = 0;
        for (int i = 0; i < 4; i++)
        {
            var frame = output.Frames[i];
            Assert.Equal(i + 1.0, frame.Value);
            double f = frame.Summary!["Frequency (Hz)"];
            Assert.True(f >= previous, "Frequencies must ascend.");
            previous = f;

            var shape = (NodalVectorField)frame.Fields.Single(fld => fld.Name == "Mode shape");
            double max = shape.Values.Max(v => v.Length);
            Assert.Equal(1.0, max, 1e-9);
        }
        Assert.Equal(output.Summary!["f1 (Hz)"], output.Frames[0].Summary!["Frequency (Hz)"]);
    }

    // ------------------------------------------------------------------
    // Validation errors must be actionable.
    // ------------------------------------------------------------------
    [Fact]
    public void Validate_MissingSupportOrBadModeCount_Throws()
    {
        var mesh = MeshBox(0.1, 0.05, 0.05, 0.03);
        var solver = new ModalAnalysisSolver();

        var unsupported = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = Array.Empty<BoundaryCondition>()
        };
        var ex1 = Assert.Throws<InvalidOperationException>(() => solver.Validate(unsupported));
        Assert.Contains("rigid-body", ex1.Message, StringComparison.OrdinalIgnoreCase);

        var tooMany = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Wall", FaceIds = new[] { 0 } }
            },
            Modal = new ModalSettings { ModeCount = 31 }
        };
        var ex2 = Assert.Throws<InvalidOperationException>(() => solver.Validate(tooMany));
        Assert.Contains("mode count", ex2.Message, StringComparison.OrdinalIgnoreCase);

        var thermalBc = new SolveInput
        {
            Mesh = mesh,
            Material = Steel,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Wall", FaceIds = new[] { 0 } },
                new FixedTemperature { Name = "Hot", FaceIds = new[] { 1 }, Kelvin = 400 }
            }
        };
        var ex3 = Assert.Throws<InvalidOperationException>(() => solver.Validate(thermalBc));
        Assert.Contains("does not apply", ex3.Message);
    }
}
