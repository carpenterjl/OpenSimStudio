using OpenSim.Core.Model;
using OpenSim.Core.Results;

namespace OpenSim.Core.PostProcessing;

/// <summary>
/// Minimum, maximum and average of a result field — the three numbers every commercial FE
/// report carries per result, and the ones a reproduction is checked against.
///
/// TWO averages are reported on purpose, because "average" is ambiguous and the ambiguity
/// is worth several percent:
/// <list type="bullet">
/// <item><description><see cref="Mean"/> is the unweighted average over the field own
/// entities (nodes for a nodal field, elements for an element field). This is what Ansys
/// reports, and it is mesh-density weighted — a refined region counts more.</description></item>
/// <item><description><see cref="VolumeWeightedMean"/> weights each entity by the volume it
/// represents, which is the physically meaningful average of the field over the body and is
/// insensitive to where the mesh happens to be dense.</description></item>
/// </list>
/// Both are shown with their definitions rather than one being picked silently.
/// </summary>
public sealed record FieldStatistics(
    double Min,
    double Max,
    double Mean,
    double VolumeWeightedMean,
    int Count,
    int MinIndex,
    int MaxIndex)
{
    /// <summary>
    /// Computes the statistics of one field over the mesh it was solved on. The mesh is
    /// needed only for the volume weights; the unweighted mean and the extrema do not use it.
    /// </summary>
    public static FieldStatistics Compute(IResultField field, FeMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(mesh);
        if (field.Count == 0)
            throw new ArgumentException($"Result field '{field.Name}' has no values.", nameof(field));

        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        int minIndex = 0, maxIndex = 0;
        for (int i = 0; i < field.Count; i++)
        {
            double v = field.GetScalar(i);
            sum += v;
            if (v < min) { min = v; minIndex = i; }
            if (v > max) { max = v; maxIndex = i; }
        }

        var weights = Weights(field, mesh);
        double weighted = 0, totalWeight = 0;
        for (int i = 0; i < field.Count; i++)
        {
            weighted += field.GetScalar(i) * weights[i];
            totalWeight += weights[i];
        }
        // A degenerate mesh (zero total volume) would otherwise produce NaN; fall back to
        // the unweighted mean and say nothing false.
        double volumeMean = totalWeight > 0 ? weighted / totalWeight : sum / field.Count;

        return new FieldStatistics(min, max, sum / field.Count, volumeMean, field.Count, minIndex, maxIndex);
    }

    /// <summary>
    /// Volume represented by each entity: the element volume for an element field, and for a
    /// nodal field the sum of the adjacent element volumes divided among their nodes — the
    /// same weighting the solvers already use to average element results to nodes.
    /// </summary>
    private static double[] Weights(IResultField field, FeMesh mesh)
    {
        if (field.Location == FieldLocation.Element)
        {
            var byElement = new double[field.Count];
            for (int e = 0; e < Math.Min(field.Count, mesh.ElementCount); e++)
                byElement[e] = Math.Abs(mesh.ElementVolume(e));
            return byElement;
        }

        var byNode = new double[field.Count];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.GetElementNodes(e);
            double share = Math.Abs(mesh.ElementVolume(e)) / nodes.Length;
            foreach (int n in nodes)
                if (n < byNode.Length) byNode[n] += share;
        }
        return byNode;
    }
}
