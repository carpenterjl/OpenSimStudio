using OpenSim.Core.Model;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Results;
using OpenSim.Tests.Solvers;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Gates for the min / max / average a result report carries. The two averages are gated
/// separately because they are different numbers and the difference is the point: the
/// unweighted mean is mesh-density weighted (what Ansys reports), the volume-weighted mean
/// is not.
/// </summary>
public class FieldStatisticsTests
{
    private static FeMesh UniformBox(int n = 2) => StructuredBoxMesh.Build(0, 1, 0, 1, 0, 1, n, n, n);

    [Fact]
    public void NodalField_MinMaxMeanAndTheirIndices_AreExact()
    {
        var mesh = UniformBox();
        var values = new double[mesh.NodeCount];
        for (int i = 0; i < values.Length; i++) values[i] = i;
        var field = new NodalScalarField("Ramp", "-", values);

        var stats = FieldStatistics.Compute(field, mesh);

        Assert.Equal(0, stats.Min);
        Assert.Equal(values.Length - 1, stats.Max);
        Assert.Equal(0, stats.MinIndex);
        Assert.Equal(values.Length - 1, stats.MaxIndex);
        Assert.Equal((values.Length - 1) / 2.0, stats.Mean, 12);
        Assert.Equal(values.Length, stats.Count);
    }

    [Fact]
    public void ConstantField_EveryStatisticIsThatConstant()
    {
        var mesh = UniformBox(3);
        var values = Enumerable.Repeat(7.5, mesh.ElementCount).ToArray();
        var stats = FieldStatistics.Compute(new ElementScalarField("Flat", "-", values), mesh);

        Assert.Equal(7.5, stats.Min, 12);
        Assert.Equal(7.5, stats.Max, 12);
        Assert.Equal(7.5, stats.Mean, 12);
        Assert.Equal(7.5, stats.VolumeWeightedMean, 12);
    }

    /// <summary>
    /// On a mesh whose elements all have the same volume the two averages must agree
    /// exactly — that is the collapse case that proves the weighting is applied correctly
    /// rather than merely applied.
    /// </summary>
    [Fact]
    public void UniformElementVolumes_BothAveragesAgree()
    {
        var mesh = UniformBox(3);
        var random = new Random(11);
        var values = Enumerable.Range(0, mesh.ElementCount).Select(_ => random.NextDouble()).ToArray();

        var stats = FieldStatistics.Compute(new ElementScalarField("Random", "-", values), mesh);

        // Freudenthal subdivision gives every tet of every cell the same volume, so the
        // weights are all equal.
        Assert.Equal(stats.Mean, stats.VolumeWeightedMean, 12);
    }

    /// <summary>
    /// And where the volumes differ the two averages must DISAGREE in the direction the
    /// weighting predicts — otherwise the volume weight is being computed but ignored.
    /// </summary>
    [Fact]
    public void GradedMesh_VolumeWeightedMeanFollowsTheLargerElements()
    {
        // Two stacked boxes of very different cell size, merged: the coarse half has far
        // fewer, far larger elements.
        var material = StructuredBoxMesh.Conductor("c", 1);
        var fine = StructuredBoxMesh.Box("fine", 0, 1, 0, 1, 0, 1, 4, 4, 4, material);
        var coarse = StructuredBoxMesh.Box("coarse", 2, 4, 0, 2, 0, 2, 1, 1, 1, material);
        var mesh = FeMeshAssembler.Assemble(new[] { fine, coarse }).Mesh;

        // 0 in the fine half, 1 in the coarse half.
        var values = new double[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++) values[e] = mesh.RegionOf(e) == 1 ? 1.0 : 0.0;

        var stats = FieldStatistics.Compute(new ElementScalarField("Half", "-", values), mesh);

        Assert.Equal(0, stats.Min);
        Assert.Equal(1, stats.Max);
        // The coarse half holds 8/9 of the volume but a small minority of the elements, so
        // the volume-weighted mean is much the larger.
        Assert.True(stats.VolumeWeightedMean > stats.Mean + 0.2,
            $"volume-weighted {stats.VolumeWeightedMean:g4} should far exceed unweighted {stats.Mean:g4}");
        Assert.Equal(8.0 / 9.0, stats.VolumeWeightedMean, 6);
    }

    [Fact]
    public void VectorFieldStatistics_UseTheMagnitude()
    {
        var mesh = UniformBox();
        var values = new OpenSim.Core.Numerics.Vector3D[mesh.NodeCount];
        values[0] = new OpenSim.Core.Numerics.Vector3D(3, 4, 0);   // magnitude 5
        var field = new NodalVectorField("Displacement", "m", values);

        var stats = FieldStatistics.Compute(field, mesh);

        Assert.Equal(0, stats.Min, 12);
        Assert.Equal(5, stats.Max, 12);
        Assert.Equal(0, stats.MaxIndex);
    }

    [Fact]
    public void EmptyField_IsATypedFailure()
    {
        Assert.Throws<ArgumentException>(() =>
            FieldStatistics.Compute(new NodalScalarField("Nothing", "-", Array.Empty<double>()), UniformBox()));
    }
}
