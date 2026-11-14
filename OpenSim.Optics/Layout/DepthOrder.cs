using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Layout;

/// <summary>
/// Back-to-front order for transparent bodies. WPF 3-D blends translucent triangles in the
/// order the models are drawn and does not sort them, so a lens drawn before one behind it
/// hides it. Sorting whole bodies by their centre's depth along the view direction is enough
/// for a lens train: its bodies are convex-ish and spread along one axis, so they do not
/// interpenetrate in depth (plan OP-8, Research). Re-sort on every camera move.
/// </summary>
public static class DepthOrder
{
    /// <summary>
    /// The indices of <paramref name="centers"/> from farthest to nearest, depth being the
    /// distance in front of the camera along <paramref name="lookDirection"/> (need not be
    /// unit). Ties keep their input order, so the result is deterministic.
    /// </summary>
    public static int[] BackToFront(IReadOnlyList<Vector3D> centers, Vector3D cameraPosition, Vector3D lookDirection)
    {
        ArgumentNullException.ThrowIfNull(centers);
        if (!(lookDirection.Length > 0))
            throw new ArgumentException("The look direction must be a non-zero vector.", nameof(lookDirection));
        var depth = new double[centers.Count];
        var order = new int[centers.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
            depth[i] = Vector3D.Dot(centers[i] - cameraPosition, lookDirection);
        }
        // Array.Sort is not stable; break ties on the index explicitly.
        Array.Sort(order, (a, b) =>
        {
            int c = depth[b].CompareTo(depth[a]);
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }
}
