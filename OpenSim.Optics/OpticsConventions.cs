using OpenSim.Core.Numerics;

namespace OpenSim.Optics;

/// <summary>The length unit a lens file or an editor shows. Inside the program every length
/// is meters.</summary>
public enum LensUnit
{
    Millimeter = 0,
    Centimeter = 1,
    Inch = 2,
    Meter = 3,
}

/// <summary>How field points are normalised to (Hx, Hy).</summary>
public enum FieldNormalization
{
    /// <summary>Both coordinates are divided by the largest radial field.</summary>
    Radial = 0,

    /// <summary>x is divided by the largest |x| and y by the largest |y|.</summary>
    Rectangular = 1,
}

/// <summary>
/// The conversions behind docs/Optics-Conventions.md. Each rule is written once, here, and
/// every later optics item calls it instead of restating it.
/// </summary>
public static class OpticsConventions
{
    /// <summary>Meters in one lens unit. The inch is 0.0254 m by definition.</summary>
    public static double MetersPer(LensUnit unit) => unit switch
    {
        LensUnit.Millimeter => 1e-3,
        LensUnit.Centimeter => 1e-2,
        LensUnit.Inch => 0.0254,
        LensUnit.Meter => 1.0,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown lens unit.")
    };

    /// <summary>A length in lens units, in meters.</summary>
    public static double ToMeters(double value, LensUnit unit) => value * MetersPer(unit);

    /// <summary>A length in meters, in lens units.</summary>
    public static double FromMeters(double meters, LensUnit unit) => meters / MetersPer(unit);

    /// <summary>A wavelength in micrometers (the unit of every editor and lens file), in meters.
    /// This changes the unit only: a file wavelength measured in air still needs the air
    /// index to become a vacuum wavelength.</summary>
    public static double MicrometersToMeters(double micrometers) => micrometers * 1e-6;

    /// <summary>A wavelength in meters, in micrometers.</summary>
    public static double MetersToMicrometers(double meters) => meters * 1e6;

    /// <summary>Curvature (1/m) from a radius (m). A radius of zero or infinity is a plane,
    /// as the editors show it. Positive when the centre of curvature is toward +z of the
    /// vertex, whatever the number of mirrors before the surface.</summary>
    public static double CurvatureFromRadius(double radius) =>
        radius == 0 || double.IsInfinity(radius) ? 0.0 : 1.0 / radius;

    /// <summary>Radius (m) from a curvature (1/m); a plane is +infinity.</summary>
    public static double RadiusFromCurvature(double curvature) =>
        curvature == 0 ? double.PositiveInfinity : 1.0 / curvature;

    /// <summary>The sign every thickness carries after this many mirrors: +1 after an even
    /// number, −1 after an odd number.</summary>
    public static int ThicknessSignAfter(int mirrorCount)
    {
        if (mirrorCount < 0) throw new ArgumentOutOfRangeException(nameof(mirrorCount));
        return mirrorCount % 2 == 0 ? 1 : -1;
    }

    /// <summary>A direction reflected in a surface with the given unit normal (either sense
    /// of the normal gives the same result).</summary>
    public static Vector3D Reflect(Vector3D direction, Vector3D unitNormal) =>
        direction - unitNormal * (2.0 * Vector3D.Dot(direction, unitNormal));

    /// <summary>
    /// Optical path difference of a ray against the chief ray, in meters, both paths measured
    /// to the reference sphere: chief minus ray. Positive means the ray's wavefront is ahead
    /// of the reference sphere (its path is shorter).
    /// </summary>
    public static double OpticalPathDifference(double chiefPath, double rayPath) => chiefPath - rayPath;

    /// <summary>An optical path difference in waves of a vacuum wavelength.</summary>
    public static double InWaves(double pathDifference, double wavelength) => pathDifference / wavelength;

    /// <summary>Sine and cosine of an angle in degrees, exact (0 and ±1) at every multiple of
    /// 90°, so a fold or a quarter turn does not leave 6e-17 where a zero belongs.</summary>
    public static (double Sin, double Cos) SinCosDegrees(double degrees)
    {
        double turns = degrees / 90.0;
        if (turns == Math.Floor(turns) && Math.Abs(turns) < 1e15)
        {
            return (((long)turns % 4 + 4) % 4) switch
            {
                0 => (0.0, 1.0),
                1 => (1.0, 0.0),
                2 => (0.0, -1.0),
                _ => (-1.0, 0.0),
            };
        }
        double radians = degrees * (Math.PI / 180.0);
        return (Math.Sin(radians), Math.Cos(radians));
    }

    /// <summary>
    /// The scale that turns field points into normalised coordinates: (Fr, Fr) with Fr the
    /// largest radial field for <see cref="FieldNormalization.Radial"/>, or (Fx, Fy) with the
    /// largest |x| and |y| for <see cref="FieldNormalization.Rectangular"/>. The fields are in
    /// whatever unit the field type uses (angle or height); H is dimensionless either way.
    /// </summary>
    public static (double X, double Y) FieldScale(IEnumerable<(double X, double Y)> fields,
        FieldNormalization normalization)
    {
        double fr = 0, fx = 0, fy = 0;
        foreach (var (x, y) in fields)
        {
            fr = Math.Max(fr, Math.Sqrt(x * x + y * y));
            fx = Math.Max(fx, Math.Abs(x));
            fy = Math.Max(fy, Math.Abs(y));
        }
        return normalization switch
        {
            FieldNormalization.Radial => (fr, fr),
            FieldNormalization.Rectangular => (fx, fy),
            _ => throw new ArgumentOutOfRangeException(nameof(normalization), normalization, "Unknown field normalisation.")
        };
    }

    /// <summary>Normalised field (Hx, Hy) of a field point. A zero scale (every field on that
    /// axis is zero) gives H = 0 on that axis.</summary>
    public static (double Hx, double Hy) NormalizeField((double X, double Y) field, (double X, double Y) scale) =>
        (scale.X == 0 ? 0 : field.X / scale.X, scale.Y == 0 ? 0 : field.Y / scale.Y);

    /// <summary>The field point of a normalised field: f = H·F on each axis.</summary>
    public static (double X, double Y) DenormalizeField((double Hx, double Hy) h, (double X, double Y) scale) =>
        (h.Hx * scale.X, h.Hy * scale.Y);
}
