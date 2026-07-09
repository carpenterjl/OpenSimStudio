using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Floating-point geometric predicates for Delaunay construction. These are not
/// exact-arithmetic predicates; the mesher defends against degeneracies by applying
/// a deterministic sub-tolerance jitter to the point set before triangulating.
/// </summary>
public static class GeometricPredicates
{
    /// <summary>
    /// Six times the signed volume of tetrahedron (a,b,c,d).
    /// Positive when d lies on the positive side of the plane through (a,b,c).
    /// </summary>
    public static double Orient3D(Vector3D a, Vector3D b, Vector3D c, Vector3D d)
    {
        return Vector3D.Dot(d - a, Vector3D.Cross(b - a, c - a));
    }

    /// <summary>
    /// Positive when p lies inside the circumsphere of the positively oriented
    /// tetrahedron (a,b,c,d); negative outside; near zero when cospherical.
    /// </summary>
    public static double InSphere(Vector3D a, Vector3D b, Vector3D c, Vector3D d, Vector3D p)
    {
        double aex = a.X - p.X, aey = a.Y - p.Y, aez = a.Z - p.Z;
        double bex = b.X - p.X, bey = b.Y - p.Y, bez = b.Z - p.Z;
        double cex = c.X - p.X, cey = c.Y - p.Y, cez = c.Z - p.Z;
        double dex = d.X - p.X, dey = d.Y - p.Y, dez = d.Z - p.Z;

        double ae2 = aex * aex + aey * aey + aez * aez;
        double be2 = bex * bex + bey * bey + bez * bez;
        double ce2 = cex * cex + cey * cey + cez * cez;
        double de2 = dex * dex + dey * dey + dez * dez;

        // 4x4 determinant expanded along the last column:
        // | aex aey aez ae2 |
        // | bex bey bez be2 |
        // | cex cey cez ce2 |
        // | dex dey dez de2 |
        double det = ae2 * Det3(bex, bey, bez, cex, cey, cez, dex, dey, dez)
                   - be2 * Det3(aex, aey, aez, cex, cey, cez, dex, dey, dez)
                   + ce2 * Det3(aex, aey, aez, bex, bey, bez, dex, dey, dez)
                   - de2 * Det3(aex, aey, aez, bex, bey, bez, cex, cey, cez);
        // With Orient3D(a,b,c,d) > 0 this expansion is positive for points inside
        // (verified by unit test against the unit tetrahedron).
        return det;
    }

    private static double Det3(
        double a1, double a2, double a3,
        double b1, double b2, double b3,
        double c1, double c2, double c3)
    {
        return a1 * (b2 * c3 - b3 * c2)
             - a2 * (b1 * c3 - b3 * c1)
             + a3 * (b1 * c2 - b2 * c1);
    }
}
