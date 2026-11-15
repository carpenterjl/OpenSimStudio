using System.Numerics;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>How the stored samples are referenced (help, "Sign Conventions for Phase Data").</summary>
public enum PopReference
{
    /// <summary>Inside the pilot's Rayleigh range: the phase is relative to a plane.</summary>
    Plane,

    /// <summary>Outside it: the phase is relative to a sphere centred on the pilot's waist,
    /// of radius the distance from that waist (<see cref="PopField.ReferenceRadius"/>).</summary>
    Sphere,
}

/// <summary>
/// The sampled beam of physical optics propagation (OP-49; docs/Optics-Conventions.md §66): one
/// scalar complex field on an Nx × Ny grid normal to the beam, with the pilot beam carried
/// beside it.
/// <para>
/// <b>Grid.</b> Sample [r, c] (row r is y, column c is x) sits at x = (c − Nx/2)·Dx,
/// y = (r − Ny/2)·Dy, so the centre sample is [Ny/2, Nx/2] (help, "Physical Optics
/// Propagation": "The center point in the beam file is at the coordinate (nx/2+1, ny/2+1)",
/// counted from 1), and row 0 is the −y edge ("The first data point is at the −x, −y corner,
/// and the data proceeds across the x rows first").
/// </para>
/// <para>
/// <b>Field.</b> <see cref="Samples"/> hold U in √(W/m²), so |U|² is the irradiance. The field
/// itself is E = U·exp(i·k·r²/(2·R_ref)) with k = 2π·n/λ the wavenumber in the medium and R_ref
/// the <see cref="ReferenceRadius"/> (E = U against a plane). Internally the field follows the
/// physics convention of Goodman and of the help's own propagation formulas (a wave e^{+ikz},
/// angular-spectrum transfer e^{−iπλΔzρ²}); the phase the help prints, writes to beam files and
/// returns from POPI is the opposite sign ("positive if the wavefront is to the right of the
/// reference surface"), so every output conjugates (docs §66).
/// </para>
/// </summary>
public sealed class PopField
{
    public PopField(Complex[,] samples, double dx, double dy, PilotBeam pilot, PopReference reference)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int ny = samples.GetLength(0), nx = samples.GetLength(1);
        if (!IsSampling(nx) || !IsSampling(ny))
            throw new ArgumentException($"Physical optics: the sampling must be a power of 2 from {MinimumSampling} to {MaximumSampling} in x and y (it is {nx} x {ny}).");
        if (!(dx > 0) || !(dy > 0) || !double.IsFinite(dx) || !double.IsFinite(dy))
            throw new ArgumentException($"Physical optics: the sample spacing must be positive (it is {dx} x {dy} m).");
        Samples = samples;
        Dx = dx;
        Dy = dy;
        Pilot = pilot;
        Reference = reference;
    }

    /// <summary>The help's limits for a beam file: "OpticStudio requires the values of nx and ny
    /// to be an integral power of 2 ... The minimum sampling is 32 and the current maximum
    /// sampling is ... 16,384 for 64-bit versions" (help, "File (defining the initial beam)").</summary>
    public const int MinimumSampling = 32, MaximumSampling = 16384;

    public static bool IsSampling(int n) => n >= MinimumSampling && n <= MaximumSampling && (n & (n - 1)) == 0;

    /// <summary>U, √(W/m²); [row (y), column (x)]. Held, not copied.</summary>
    public Complex[,] Samples { get; }

    public int Nx => Samples.GetLength(1);
    public int Ny => Samples.GetLength(0);

    /// <summary>The sample spacing, meters.</summary>
    public double Dx { get; }
    public double Dy { get; }

    /// <summary>The full width of the array, Nx·Dx, meters.</summary>
    public double WidthX => Nx * Dx;
    public double WidthY => Ny * Dy;

    public PilotBeam Pilot { get; }

    public PopReference Reference { get; }

    /// <summary>The radius of the reference sphere, the pilot's distance past its waist; infinite
    /// for a plane reference.</summary>
    public double ReferenceRadius => Reference == PopReference.Sphere ? Pilot.DistanceFromWaist : double.PositiveInfinity;

    /// <summary>The wavenumber in the medium, 2π·n/λ.</summary>
    public double Wavenumber => 2 * Math.PI / Pilot.MediumWavelength;

    public double X(int column) => (column - Nx / 2) * Dx;
    public double Y(int row) => (row - Ny / 2) * Dy;

    /// <summary>The total power, Σ|U|²·Dx·Dy, watts.</summary>
    public double Power
    {
        get
        {
            double sum = 0;
            foreach (var u in Samples) sum += u.Real * u.Real + u.Imaginary * u.Imaginary;
            return sum * Dx * Dy;
        }
    }

    /// <summary>The irradiance |U|², W/m², [row, column].</summary>
    public double[,] Irradiance()
    {
        var result = new double[Ny, Nx];
        for (int r = 0; r < Ny; r++)
            for (int c = 0; c < Nx; c++)
            {
                var u = Samples[r, c];
                result[r, c] = u.Real * u.Real + u.Imaginary * u.Imaginary;
            }
        return result;
    }

    /// <summary>The field E itself (reference phase added back), physics convention.</summary>
    public Complex TrueField(int row, int column)
    {
        var u = Samples[row, column];
        if (Reference == PopReference.Plane) return u;
        double x = X(column), y = Y(row);
        return u * Complex.FromPolarCoordinates(1, Wavenumber * (x * x + y * y) / (2 * ReferenceRadius));
    }
}
