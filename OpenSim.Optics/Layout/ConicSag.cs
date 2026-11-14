namespace OpenSim.Optics.Layout;

/// <summary>
/// A rotationally symmetric conic (OpticStudio's Standard surface shape): curvature c = 1/R at
/// the vertex (1/m) and conic constant k.
/// <para>
/// Sag, from the surface-profile formula of the Wikipedia article "Aspheric lens" (section
/// "Surface profile", read 2026-11-13) with every polynomial coefficient zero:
/// z(r) = r² / (R·(1 + √(1 − (1 + k)·r²/R²))), which is the same as
/// z(r) = c·r² / (1 + √(1 − (1 + k)·c²·r²)). k = 0 is a sphere, k = −1 a paraboloid,
/// k &lt; −1 a hyperboloid, −1 &lt; k &lt; 0 a prolate and k &gt; 0 an oblate ellipsoid.
/// </para>
/// <para>
/// The form is the rationalised root of the quadric (1 + k)·z² − 2R·z + r² = 0: it has no
/// subtraction of near-equal numbers, so it keeps full precision near the vertex and is finite
/// for a plane (c = 0) and a paraboloid (k = −1), where the textbook root divides by zero.
/// </para>
/// </summary>
public sealed record ConicSag(double Curvature, double Conic = 0.0) : ISurfaceSag
{
    /// <summary>A plane.</summary>
    public static ConicSag Plane { get; } = new(0.0);

    /// <summary>A conic from its vertex radius (meters, the sign rule of
    /// docs/Optics-Conventions.md §3; zero or infinity is a plane).</summary>
    public static ConicSag FromRadius(double radius, double conic = 0.0) =>
        new(OpticsConventions.CurvatureFromRadius(radius), conic);

    /// <inheritdoc />
    public double Sag(double x, double y) => SagAtRadiusSquared(x * x + y * y);

    /// <summary>The sag at radial distance r (meters).</summary>
    public double SagAtRadius(double r) => SagAtRadiusSquared(r * r);

    private double SagAtRadiusSquared(double r2)
    {
        double c = Curvature;
        double arg = 1.0 - (1.0 + Conic) * c * c * r2;
        if (arg < 0) return double.NaN;
        return c * r2 / (1.0 + Math.Sqrt(arg));
    }

    /// <inheritdoc />
    public double MaxDefinedRadius
    {
        get
        {
            // The square root's argument 1 − (1 + k)c²r² is negative past r = 1/(|c|·√(1 + k));
            // it never is for a plane or for k ≤ −1 (paraboloid, hyperboloid).
            double q = (1.0 + Conic) * Curvature * Curvature;
            return q > 0 ? 1.0 / Math.Sqrt(q) : double.PositiveInfinity;
        }
    }
}
