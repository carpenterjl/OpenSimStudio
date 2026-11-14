using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// The closed-form steps of the real ray trace (docs/Optics-Conventions.md §13): the ray's
/// intersection with a conic from a point on its vertex plane, the surface normal, and vector
/// refraction. Every formula is derived in the comments; the arrangement (transfer to the
/// vertex plane, then a rationalised root) is the one Spencer and Murty (JOSA 52, 1962) and
/// Welford describe, neither of which was on hand, so nothing here is copied from them.
/// </summary>
public static class ConicIntersection
{
    /// <summary>
    /// The intersection of the ray through (<paramref name="x"/>, <paramref name="y"/>, 0) with
    /// unit direction <paramref name="direction"/> and the conic of vertex curvature
    /// <paramref name="curvature"/> and conic constant <paramref name="conic"/>, vertex at the
    /// origin. Returns false when the line misses the surface, or meets it only beyond the
    /// sheet that holds the vertex.
    /// <para>
    /// The conic is F = c·(x² + y² + (1 + k)·z²) − 2z = 0 (its sag c·r²/(1 + √(1 − (1 + k)c²r²))
    /// is the root through the vertex). Putting p + s·d into F with p = (x, y, 0) gives
    /// A·s² + 2B·s + C = 0 with A = c·(L² + M² + (1 + k)·N²), B = c·(xL + yM) − N,
    /// C = c·(x² + y²). The roots are s = −C / (B ± √(B² − AC)); the one that tends to the sag
    /// over N as c → 0 (the vertex sheet) takes the sign that makes the denominator tend to 2B,
    /// i.e. −sign(N), because B → −N there. This form never subtracts near-equal numbers for
    /// that root, stays finite for a plane (C = 0) and for a paraboloid along the axis (A = 0),
    /// where the textbook (−B ± √)/A divides by zero.
    /// </para>
    /// <para>
    /// The vertex sheet is where ∂F/∂z = 2(c(1 + k)z − 1) keeps its vertex sign, i.e.
    /// 1 − c(1 + k)z &gt; 0; a point with 1 − c(1 + k)z ≤ 0 is on the far half of a sphere or
    /// ellipsoid, or the other sheet of a hyperboloid, where the sag is not defined.
    /// </para>
    /// </summary>
    /// <param name="distance">Signed geometric distance from (x, y, 0) to the intersection.</param>
    public static bool Intersect(double x, double y, Vector3D direction, double curvature, double conic,
        out Vector3D point, out double distance)
    {
        double c = curvature;
        double L = direction.X, M = direction.Y, N = direction.Z;
        if (c == 0)
        {
            point = new Vector3D(x, y, 0);
            distance = 0;
            return true;
        }
        double kp = 1 + conic;
        double a = c * (L * L + M * M + kp * N * N);
        double b = c * (x * L + y * M) - N;
        double cc = c * (x * x + y * y);
        double disc = b * b - a * cc;
        if (!(disc >= 0))
        {
            point = default;
            distance = double.NaN;
            return false;
        }
        double denominator = b - Math.CopySign(Math.Sqrt(disc), N);
        if (cc == 0) distance = 0;
        else if (denominator == 0)
        {
            point = default;
            distance = double.NaN;
            return false;
        }
        else distance = -cc / denominator;
        point = new Vector3D(x + distance * L, y + distance * M, distance * N);
        if (!(1 - c * kp * point.Z > 0) || !double.IsFinite(distance))
        {
            point = default;
            distance = double.NaN;
            return false;
        }
        return true;
    }

    /// <summary>The unit normal of the conic at a point on it, the sense that is +z at the
    /// vertex: −∇F/|∇F| with ∇F/2 = (c·x, c·y, c(1 + k)z − 1).</summary>
    public static Vector3D Normal(Vector3D point, double curvature, double conic)
    {
        double c = curvature;
        if (c == 0) return Vector3D.UnitZ;
        var n = new Vector3D(-c * point.X, -c * point.Y, 1 - c * (1 + conic) * point.Z);
        return n / n.Length;
    }

    /// <summary>
    /// Vector Snell's law: the direction of a ray of unit direction <paramref name="direction"/>
    /// refracted at a surface of unit normal <paramref name="unitNormal"/> (either sense) from
    /// index <paramref name="indexBefore"/> to <paramref name="indexAfter"/>. Returns false for
    /// total internal reflection.
    /// <para>
    /// With μ = n/n′, the normal turned so cos I = d·n̂ ≥ 0, sin²I = |d × n̂|² (accurate at small
    /// angles, where 1 − cos²I is not) and cos T = √(1 − μ²·sin²I), the refracted direction is
    /// d′ = μ·d + Γ·n̂ with Γ = cos T − μ·cos I. Since cos²T − μ²cos²I = 1 − μ², Γ is computed
    /// as (1 − μ)(1 + μ)/(cos T + μ·cos I), which does not cancel when the indices are close.
    /// Then n′·(d′ × n̂) = n·(d × n̂): the tangential component is conserved, which is Snell's
    /// law with the incident ray, the refracted ray and the normal in one plane.
    /// </para>
    /// </summary>
    public static bool Refract(Vector3D direction, Vector3D unitNormal, double indexBefore, double indexAfter,
        out Vector3D refracted)
    {
        if (indexBefore == indexAfter)
        {
            refracted = direction;
            return true;
        }
        double cosI = Vector3D.Dot(direction, unitNormal);
        var n = unitNormal;
        if (cosI < 0) { n = -n; cosI = -cosI; }
        double mu = indexBefore / indexAfter;
        double sin2I = Vector3D.Cross(direction, n).LengthSquared;
        double sin2T = mu * mu * sin2I;
        if (sin2T > 1)
        {
            refracted = direction;
            return false;
        }
        double cosT = Math.Sqrt(1 - sin2T);
        double oneMinusMu = (indexAfter - indexBefore) / indexAfter;
        double gamma = oneMinusMu * (1 + mu) / (cosT + mu * cosI);
        var d = direction * mu + n * gamma;
        refracted = d / d.Length;
        return true;
    }
}
