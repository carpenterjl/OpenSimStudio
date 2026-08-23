namespace OpenSim.Core.Numerics;

/// <summary>
/// Distance from a point to a finite line segment.
/// <para>
/// It lives in Core because both sides of feature-edge handling need it and they cannot see
/// each other: the mesher asks "is this sample already on an edge?" while resolving a
/// geometry-scoped boundary condition asks "does this mesh edge run along that curve?".
/// Two copies of a clamped projection would be two things to get subtly differently wrong.
/// </para>
/// </summary>
public static class SegmentDistance
{
    /// <summary>Shortest distance from <paramref name="p"/> to the segment a–b. A degenerate
    /// segment reduces to the distance to its single point.</summary>
    public static double PointToSegment(Vector3D p, Vector3D a, Vector3D b)
    {
        var ab = b - a;
        double lengthSquared = Vector3D.Dot(ab, ab);
        if (lengthSquared <= 0) return Vector3D.Distance(p, a);
        double t = Vector3D.Dot(p - a, ab) / lengthSquared;
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        return Vector3D.Distance(p, a + ab * t);
    }
}
