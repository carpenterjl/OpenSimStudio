using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Gates for the result fields a reproduction of a commercial FE report needs: equivalent
/// elastic strain beside von Mises, and statistics over both.
/// </summary>
public class DerivedResultFieldTests
{
    private const double E = 2.0e11;
    private const double Nu = 0.3;

    private static Material Steel() => new()
    {
        Name = "Steel", Density = 7850, YoungsModulus = E, PoissonRatio = Nu
    };

    /// <summary>Steel with no strength characterised — the brittle/unknown case.</summary>
    private static Material SteelWithoutStrength() => new()
    {
        Name = "Steel", Density = 7850, YoungsModulus = E, PoissonRatio = Nu
    };

    private static Material SteelWithStrength() => SteelWithoutStrength() with
    {
        YieldStrength = 250e6, UltimateTensileStrength = 460e6
    };

    private static SolveOutput SolveBlock(Material? material = null)
    {
        var mesh = StructuredBoxMesh.Build(0, 0.2, 0, 0.06, 0, 0.02, 6, 3, 2);
        int left = mesh.Edges.EdgesBetween(
            new[] { StructuredBoxMesh.FaceYMin, StructuredBoxMesh.FaceXMin })[0];
        int right = mesh.Edges.EdgesBetween(
            new[] { StructuredBoxMesh.FaceYMin, StructuredBoxMesh.FaceXMax })[0];

        return new LinearStaticSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = material ?? Steel(),
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport
                {
                    Name = "Supports", FaceIds = Array.Empty<int>(), EdgeIds = new[] { left, right }
                },
                new ForceLoad
                {
                    Name = "Load",
                    FaceIds = new[] { StructuredBoxMesh.FaceYMax },
                    TotalForce = new Vector3D(0, -5e5, 0)
                }
            }
        });
    }

    [Fact]
    public void StaticSolve_EmitsTheAnsysResultRoster()
    {
        var names = SolveBlock().Fields.Select(f => f.Name).ToList();

        Assert.Contains("Displacement", names);                 // Ansys "Total Deformation"
        Assert.Contains("Equivalent elastic strain", names);
        Assert.Contains("Stress (von Mises)", names);           // Ansys "Equivalent Stress"
        Assert.Contains("Stress tensor", names);                // shear components live here
        Assert.Contains("Strain tensor", names);
    }

    /// <summary>
    /// The identity that pins both fields, carried all the way through the solver nodal
    /// averaging: each is a volume-weighted average of per-element values that satisfy
    /// E*eq = vm exactly, and averaging is linear, so the averaged values satisfy it too.
    /// A mismatch here would mean one of them is averaged over a different weight set.
    /// </summary>
    [Fact]
    public void NodalEquivalentStrainTimesE_IsTheNodalVonMisesStress()
    {
        var fields = SolveBlock().Fields;
        var strain = (NodalScalarField)fields.First(f => f.Name == "Equivalent elastic strain");
        var vonMises = (NodalScalarField)fields.First(f => f.Name == "Stress (von Mises)");

        Assert.Equal(vonMises.Count, strain.Count);
        int compared = 0;
        for (int i = 0; i < strain.Count; i++)
        {
            if (vonMises.Values[i] < 1e-6) continue;   // an unstressed node carries no ratio
            Assert.Equal(1.0, strain.Values[i] * E / vonMises.Values[i], 10);
            compared++;
        }
        Assert.True(compared > 10, "The block should carry stress at more than a handful of nodes.");
    }

    [Fact]
    public void EquivalentStrainField_CarriesTheStrainUnit()
    {
        var field = SolveBlock().Fields.First(f => f.Name == "Equivalent elastic strain");
        Assert.Equal("m/m", field.Unit);
        Assert.Equal(FieldLocation.Node, field.Location);
    }

    /// <summary>
    /// The shear component an Ansys report shows as "Shear Stress (XY Component)" — present
    /// in the tensor field all along, reachable now.
    /// </summary>
    [Fact]
    public void ShearComponentView_IsSymmetricAboutZero_AndSmallerThanVonMises()
    {
        var tensor = (ElementTensorField)SolveBlock().Fields.First(f => f.Name == "Stress tensor");
        var shear = tensor.View(TensorComponent.XY);

        var mesh = StructuredBoxMesh.Build(0, 0.2, 0, 0.06, 0, 0.02, 6, 3, 2);
        var stats = FieldStatistics.Compute(shear, mesh);

        // A symmetrically supported beam under a symmetric load carries equal and opposite
        // shear in its two halves.
        Assert.True(stats.Min < 0 && stats.Max > 0, "Bending should produce shear of both signs.");
        Assert.Equal(1.0, Math.Abs(stats.Min) / stats.Max, 1);
        // A single component can never exceed the von Mises equivalent by much.
        Assert.True(stats.Max < FieldStatistics.Compute(tensor, mesh).Max);
    }

    /// <summary>
    /// A safety factor is a strength divided by a stress, so a material with no strength
    /// gets no field — not a field computed against an invented allowable.
    /// </summary>
    [Fact]
    public void MaterialWithoutStrength_GetsNoSafetyFactorField()
    {
        var names = SolveBlock(SteelWithoutStrength()).Fields.Select(f => f.Name).ToList();

        Assert.DoesNotContain(names, n => n.StartsWith("Safety factor"));
    }

    [Fact]
    public void SafetyFactor_IsStrengthOverVonMises_CappedWhereTheStressVanishes()
    {
        var fields = SolveBlock(SteelWithStrength()).Fields;
        var vonMises = (NodalScalarField)fields.First(f => f.Name == "Stress (von Mises)");
        var yieldFactor = (NodalScalarField)fields.First(f => f.Name == "Safety factor (yield)");
        var ultimateFactor = (NodalScalarField)fields.First(f => f.Name == "Safety factor (ultimate)");

        const double cap = 15.0;
        int exact = 0;
        for (int i = 0; i < vonMises.Count; i++)
        {
            double expectedYield = vonMises.Values[i] <= 0
                ? cap
                : Math.Min(250e6 / vonMises.Values[i], cap);
            Assert.Equal(expectedYield, yieldFactor.Values[i], 10);
            Assert.True(yieldFactor.Values[i] <= cap + 1e-12);
            // Ultimate is the larger allowable, so never the smaller factor.
            Assert.True(ultimateFactor.Values[i] >= yieldFactor.Values[i] - 1e-12);
            if (yieldFactor.Values[i] < cap) exact++;
        }
        Assert.True(exact > 0, "Somewhere in a loaded beam the safety factor must be below the cap.");
        Assert.Equal("-", yieldFactor.Unit);
    }

    [Fact]
    public void Statistics_OfEveryEmittedField_AreFinite()
    {
        var mesh = StructuredBoxMesh.Build(0, 0.2, 0, 0.06, 0, 0.02, 6, 3, 2);
        foreach (var field in SolveBlock().Fields)
        {
            var stats = FieldStatistics.Compute(field, mesh);
            Assert.True(double.IsFinite(stats.Min) && double.IsFinite(stats.Max)
                        && double.IsFinite(stats.Mean) && double.IsFinite(stats.VolumeWeightedMean),
                $"Field '{field.Name}' produced a non-finite statistic.");
            Assert.True(stats.Min <= stats.Mean && stats.Mean <= stats.Max);
            Assert.True(stats.Min <= stats.VolumeWeightedMean && stats.VolumeWeightedMean <= stats.Max);
        }
    }
}
