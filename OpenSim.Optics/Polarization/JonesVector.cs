using System.Globalization;
using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Polarization;

/// <summary>
/// The launch polarization of a ray as OpticStudio states it (its help, "Defining the Initial
/// Polarization" and "Jx, Jy, X-Phase, Y-Phase"): the field along a vector S is
/// <see cref="X"/> and along a vector P is <see cref="Y"/>, S, P and the ray direction K a
/// right-handed orthonormal set chosen by <see cref="PolarizationReference"/>
/// (<see cref="LaunchBasis"/>). The two components are held in this program's complex
/// convention (a wave e^(i(k·r − ωt)), docs/Optics-Conventions.md §12 and §21); a phase entered
/// or reported in degrees is OpticStudio's, the opposite sign (<see cref="FromAmplitudes"/>).
/// A Jones vector is normalised to unit intensity, as OpticStudio normalises it.
/// </summary>
public readonly record struct JonesVector(Complex X, Complex Y)
{
    /// <summary>|Jx|² + |Jy|².</summary>
    public double Intensity => X.Real * X.Real + X.Imaginary * X.Imaginary + Y.Real * Y.Real + Y.Imaginary * Y.Imaginary;

    /// <summary>Linear polarization along S, (1, 0).</summary>
    public static JonesVector AlongS { get; } = new(Complex.One, Complex.Zero);

    /// <summary>Linear polarization along P, (0, 1).</summary>
    public static JonesVector AlongP { get; } = new(Complex.Zero, Complex.One);

    /// <summary>
    /// The Jones vector of magnitudes <paramref name="jx"/>, <paramref name="jy"/> and phases in
    /// degrees in OpticStudio's convention (n − ik, propagation factor e^(−2πiτ/λ); its help,
    /// "Definition of Polarization Terms"), normalised to unit intensity. A phase φ there is
    /// e^(−iφ) here. Throws when both magnitudes are zero or any input is not finite.
    /// </summary>
    public static JonesVector FromAmplitudes(double jx, double jy, double xPhaseDegrees, double yPhaseDegrees)
    {
        if (!double.IsFinite(jx) || !double.IsFinite(jy) || !double.IsFinite(xPhaseDegrees) || !double.IsFinite(yPhaseDegrees))
            throw new ArgumentException("The Jones vector's magnitudes and phases must be finite numbers.");
        double norm = Math.Sqrt(jx * jx + jy * jy);
        if (!(norm > 0))
            throw new ArgumentException("The Jones vector is zero: Jx and Jy cannot both be zero.");
        const double deg = Math.PI / 180;
        return new JonesVector(
            Complex.FromPolarCoordinates(jx / norm, -xPhaseDegrees * deg),
            Complex.FromPolarCoordinates(jy / norm, -yPhaseDegrees * deg));
    }

    /// <summary>
    /// The unit vectors S and P for a ray of unit direction <paramref name="k"/>, by OpticStudio's
    /// three rules (help, "Defining the Initial Polarization"): X axis reference P = K × X̂ and
    /// S = P × K; Y axis reference S = Ŷ × K and P = K × S; Z axis reference S = K × Ẑ and
    /// P = K × S. Each rule is undefined when K lies along its axis; there this program takes S
    /// as the part of x̂ (ŷ when K lies along x) at right angles to K, and P = K × S, which
    /// for K = +z gives Ex = Jx, Ey = Jy, the case the help calls trivial.
    /// </summary>
    public static (Vector3D S, Vector3D P) LaunchBasis(Vector3D k, PolarizationReference reference)
    {
        Vector3D s;
        switch (reference)
        {
            case PolarizationReference.XAxis:
            {
                var p = Vector3D.Cross(k, Vector3D.UnitX);
                if (p.Length < 1e-12) return Fallback(k);
                p /= p.Length;
                s = Vector3D.Cross(p, k);
                return (s / s.Length, p);
            }
            case PolarizationReference.YAxis:
                s = Vector3D.Cross(Vector3D.UnitY, k);
                break;
            case PolarizationReference.ZAxis:
                s = Vector3D.Cross(k, Vector3D.UnitZ);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reference), reference, "Unknown polarization reference.");
        }
        if (s.Length < 1e-12) return Fallback(k);
        s /= s.Length;
        var pp = Vector3D.Cross(k, s);
        return (s, pp / pp.Length);
    }

    private static (Vector3D S, Vector3D P) Fallback(Vector3D k)
    {
        var a = Math.Abs(k.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitY;
        var s = a - k * Vector3D.Dot(a, k);
        s /= s.Length;
        var p = Vector3D.Cross(k, s);
        return (s, p / p.Length);
    }

    /// <summary>The three-dimensional field Jx·S + Jy·P of a ray of direction
    /// <paramref name="k"/>, transverse to it by construction.</summary>
    public RayField ToField(Vector3D k, PolarizationReference reference)
    {
        var (s, p) = LaunchBasis(k, reference);
        return new RayField(X * s.X + Y * p.X, X * s.Y + Y * p.Y, X * s.Z + Y * p.Z);
    }

    public override string ToString()
    {
        static string Part(Complex z) => string.Create(CultureInfo.InvariantCulture,
            $"{z.Magnitude:g6} ∠ {-z.Phase * 180 / Math.PI:g6}°");
        return $"Jx {Part(X)}, Jy {Part(Y)}";
    }
}
