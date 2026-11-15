using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>The initial beam types of the help ("Defining the Initial Beam").</summary>
public enum PopBeamType
{
    GaussianWaist,
    GaussianAngle,
    GaussianSizeAngle,
    AstigmaticGaussian,
    TopHat,
    File,
    /// <summary>A beam computed by a Windows DLL; refused (no plug-in DLLs here).</summary>
    Dll,
    Multimode,
}

/// <summary>Whether the initial beam is scaled to a total power or to a peak irradiance (help,
/// "Physical Optics Propagation", Beam Definition tab).</summary>
public enum PopNormalization { TotalPower, PeakIrradiance }

/// <summary>
/// The parameters of an initial beam, in meters and degrees (OP-49). Which ones a type reads
/// follows the help's page for that type; the others are ignored.
/// </summary>
public sealed record PopBeamDefinition
{
    public PopBeamType Type { get; init; } = PopBeamType.GaussianWaist;

    /// <summary>The 1/e² intensity radius of the waist (Gaussian waist, astigmatic Gaussian) or
    /// the semi-widths (top hat), meters.</summary>
    public double WaistX { get; init; } = 1e-3;
    public double WaistY { get; init; } = 1e-3;

    public double DecenterX { get; init; }
    public double DecenterY { get; init; }

    /// <summary>The truncating aperture semi-axes Ax, Ay of the Gaussian waist beam; 0: none.</summary>
    public double ApertureX { get; init; }
    public double ApertureY { get; init; }

    /// <summary>The Hermite–Gaussian orders l (x) and m (y) of the Gaussian waist beam.</summary>
    public int OrderX { get; init; }
    public int OrderY { get; init; }

    /// <summary>The far-field half-angle divergence in degrees, measured in air (Gaussian angle,
    /// Gaussian size+angle).</summary>
    public double AngleX { get; init; } = 1;
    public double AngleY { get; init; } = 1;

    /// <summary>The beam size (not waist) of the Gaussian size+angle beam, meters.</summary>
    public double SizeX { get; init; } = 1e-3;
    public double SizeY { get; init; } = 1e-3;

    /// <summary>The distance from the beam to its waist (astigmatic Gaussian), negative when the
    /// waist is to the left of the beam, meters.</summary>
    public double WaistPositionX { get; init; }
    public double WaistPositionY { get; init; }

    /// <summary>The ZBF (File) or ZMM (Multimode) file, a full path.</summary>
    public string File { get; init; } = "";

    /// <summary>Multimode parameter 1, the random seed; parameter 2, the scale (0: 1).</summary>
    public long Seed { get; init; }
    public double Scale { get; init; }

    public PopNormalization Normalization { get; init; } = PopNormalization.TotalPower;

    /// <summary>The total power (W), or the peak irradiance (W/m²).</summary>
    public double Power { get; init; } = 1;
}

/// <summary>
/// Builds the initial field of each beam type of the help (OP-49; docs/Optics-Conventions.md
/// §66): "Gaussian Waist", "Gaussian Angle", "Gaussian Size+Angle", "Astigmatic Gaussian", "Top
/// Hat", "File (defining the initial beam)", "Multimode"; "DLL" is refused by name.
/// </summary>
public static class PopBeams
{
    /// <summary>Above this Hermite order the help uses order zero ("If the order number is set
    /// higher than 30, the order zero will be used").</summary>
    public const int MaximumOrder = 30;

    /// <summary>The field of <paramref name="beam"/> on an nx × ny grid of the given widths,
    /// normalised, with its pilot, in a medium of <paramref name="index"/>. Notes about choices
    /// the help leaves open go to <paramref name="notes"/>. A File beam brings its own grid, so
    /// the sampling and widths are not used for it.</summary>
    public static PopField Create(PopBeamDefinition beam, int nx, int ny, double widthX, double widthY,
        double airWavelength, double index, LensUnit unit, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(beam);
        ArgumentNullException.ThrowIfNull(notes);
        PopField field = beam.Type switch
        {
            PopBeamType.Dll => throw new NotSupportedException(
                "Physical optics: the DLL beam type computes the beam in a Windows plug-in DLL, which this program does not load; use a ZBF file or another type."),
            PopBeamType.File => ZbfFile.ToField(ZbfFile.Read(beam.File), airWavelength, index, notes),
            _ => Build(beam, nx, ny, widthX, widthY, airWavelength, index, unit, notes),
        };
        return Normalise(field, beam, notes);
    }

    /// <summary>The help's "Auto" width, X = ω0x·√(π·nx) ("Comments about Point Spacing and
    /// Sampling"), for a pilot of waist <paramref name="pilotWaist"/>.</summary>
    public static double AutoWidth(double pilotWaist, int samples) => pilotWaist * Math.Sqrt(Math.PI * samples);

    /// <summary>The pilot waist the type's field will have, for the Auto width; null for the
    /// types whose pilot is fitted to the field (top hat, multimode, file).</summary>
    public static double? PilotWaist(PopBeamDefinition beam, double airWavelength)
    {
        var (wx, _) = Waists(beam, airWavelength, 'x');
        var (wy, _) = Waists(beam, airWavelength, 'y');
        return beam.Type switch
        {
            PopBeamType.GaussianWaist or PopBeamType.GaussianAngle or PopBeamType.GaussianSizeAngle or PopBeamType.AstigmaticGaussian
                => Math.Sqrt(wx * wy),
            PopBeamType.TopHat => Math.Sqrt(beam.WaistX * beam.WaistY),
            _ => null,
        };
    }

    /// <summary>The waist and the beam's distance past it, in one direction, for the Gaussian
    /// types (help: ω0 = λ/(π·tan θ), "the wavelength is not scaled by the index of the starting
    /// medium"; size+angle: z = z_R·√((ω/ω0)² − 1)).</summary>
    private static (double Waist, double Z) Waists(PopBeamDefinition b, double airWavelength, char axis)
    {
        bool x = axis == 'x';
        double waist = x ? b.WaistX : b.WaistY;
        double angle = x ? b.AngleX : b.AngleY;
        double size = x ? b.SizeX : b.SizeY;
        double position = x ? b.WaistPositionX : b.WaistPositionY;
        switch (b.Type)
        {
            case PopBeamType.GaussianWaist:
            case PopBeamType.TopHat:
                return (waist, 0);
            case PopBeamType.AstigmaticGaussian:
                return (waist, -position);
            case PopBeamType.GaussianAngle:
            case PopBeamType.GaussianSizeAngle:
                if (!(angle > 0) || !(angle < 90))
                    throw new ArgumentException($"Physical optics, {b.Type} beam: the {axis} divergence angle must be between 0 and 90 degrees (it is {angle}).");
                double w0 = airWavelength / (Math.PI * Math.Tan(angle * Math.PI / 180));
                if (b.Type == PopBeamType.GaussianAngle) return (w0, 0);
                return (w0, double.NaN);   // z needs the medium; see Build
            default:
                return (double.NaN, 0);
        }
    }

    private static PopField Build(PopBeamDefinition b, int nx, int ny, double widthX, double widthY,
        double airWavelength, double index, LensUnit unit, ICollection<string> notes)
    {
        if (!PopField.IsSampling(nx) || !PopField.IsSampling(ny))
            throw new ArgumentException($"Physical optics: the sampling must be a power of 2 from {PopField.MinimumSampling} to {PopField.MaximumSampling} (it is {nx} x {ny}).");
        if (!(widthX > 0) || !(widthY > 0) || !double.IsFinite(widthX) || !double.IsFinite(widthY))
            throw new ArgumentException($"Physical optics: the array widths must be positive (they are {widthX} x {widthY} m).");
        double dx = widthX / nx, dy = widthY / ny;
        double lambdaN = airWavelength / index;
        var samples = new Complex[ny, nx];

        if (b.Type == PopBeamType.Multimode)
        {
            var mm = ZmmFile.Read(b.File, unit);
            var sum = ZmmFile.Sum(mm, nx, ny, dx, dy, airWavelength, b.Seed, b.Scale, notes);
            var fitted = FitPilot(sum, dx, dy, airWavelength, index, notes, "multimode beam");
            return new PopField(sum, dx, dy, fitted, PopReference.Plane);
        }
        if (b.Type == PopBeamType.TopHat)
        {
            Positive(b.WaistX, "top hat x semi-width");
            Positive(b.WaistY, "top hat y semi-width");
            TopHat(samples, dx, dy, b.WaistX, b.WaistY, b.DecenterX, b.DecenterY, 1.0);
            var pilot = FitPilot(samples, dx, dy, airWavelength, index, notes, "top hat beam");
            return new PopField(samples, dx, dy, pilot, PopReference.Plane);
        }

        var (wx, zx) = Waists(b, airWavelength, 'x');
        var (wy, zy) = Waists(b, airWavelength, 'y');
        Positive(wx, "x waist");
        Positive(wy, "y waist");
        if (b.Type == PopBeamType.GaussianSizeAngle)
        {
            zx = SizeAngleZ(b.SizeX, wx, lambdaN, 'x', notes, out double sx);
            zy = SizeAngleZ(b.SizeY, wy, lambdaN, 'y', notes, out double sy);
        }
        if (b.Type == PopBeamType.GaussianWaist)
        {
            int l = Order(b.OrderX, 'x', notes), m = Order(b.OrderY, 'y', notes);
            HermiteGaussian(samples, dx, dy, wx, wy, b.DecenterX, b.DecenterY, l, m);
            if (b.ApertureX != 0 || b.ApertureY != 0)
            {
                Positive(b.ApertureX, "x truncating aperture");
                Positive(b.ApertureY, "y truncating aperture");
                Truncate(samples, dx, dy, b.ApertureX, b.ApertureY, b.DecenterX, b.DecenterY);
            }
        }
        else
            AstigmaticGaussian(samples, dx, dy, wx, zx, wy, zy, b.DecenterX, b.DecenterY, lambdaN);

        double w = Math.Sqrt(wx * wy), z = 0.5 * (zx + zy);
        if (wx != wy || zx != zy)
            notes.Add($"Physical optics: the beam differs in x and y; the one pilot beam has the waist √(ωx·ωy) = {w:g6} m and lies {z:g6} m past it (the mean of x and y). \"Separate X, Y\" is not built.");
        var pilotBeam = PilotBeam.FromWaist(w, z, index, airWavelength);
        var reference = pilotBeam.IsInsideRayleighRange ? PopReference.Plane : PopReference.Sphere;
        if (reference == PopReference.Sphere)
            RemoveReference(samples, dx, dy, pilotBeam);
        return new PopField(samples, dx, dy, pilotBeam, reference);
    }

    private static double SizeAngleZ(double size, double w0, double lambdaN, char axis, ICollection<string> notes, out double used)
    {
        Positive(size, $"{axis} beam size");
        used = size;
        if (w0 > size)
        {
            // Help, "Gaussian Size+Angle": "If the resulting beam waist is larger than the
            // specified beam size (a physical impossibility), the waist value will be used for the
            // beam size."
            notes.Add($"Physical optics, Gaussian size+angle: the {axis} waist {w0:g6} m from the angle is larger than the size {size:g6} m; the waist is used as the size (as the help says).");
            used = w0;
        }
        double zR = Math.PI * w0 * w0 / lambdaN;
        double ratio = used / w0;
        return zR * Math.Sqrt(Math.Max(0, ratio * ratio - 1));
    }

    private static int Order(int order, char axis, ICollection<string> notes)
    {
        if (order < 0) throw new ArgumentException($"Physical optics, Gaussian waist: the {axis} order must be 0 or more (it is {order}).");
        if (order > MaximumOrder)
        {
            notes.Add($"Physical optics, Gaussian waist: the {axis} order {order} is above {MaximumOrder}; order 0 is used, as the help says.");
            return 0;
        }
        return order;
    }

    private static void Positive(double v, string what)
    {
        if (!(v > 0) || !double.IsFinite(v))
            throw new ArgumentException($"Physical optics: the {what} must be positive (it is {v}).");
    }

    /// <summary>E = Gl(√2(x−dx)/ω0x)·Gm(√2(y−dy)/ω0y), G_i(u) = H_i(u)·exp(−u²/2), H_i the
    /// Hermite polynomial (help, "Gaussian Waist").</summary>
    internal static void HermiteGaussian(Complex[,] samples, double dx, double dy, double wx, double wy,
        double decenterX, double decenterY, int l, int m)
    {
        int ny = samples.GetLength(0), nx = samples.GetLength(1);
        var gx = new double[nx];
        var gy = new double[ny];
        for (int c = 0; c < nx; c++)
        {
            double u = Math.Sqrt(2) * ((c - nx / 2) * dx - decenterX) / wx;
            gx[c] = Hermite(l, u) * Math.Exp(-u * u / 2);
        }
        for (int r = 0; r < ny; r++)
        {
            double u = Math.Sqrt(2) * ((r - ny / 2) * dy - decenterY) / wy;
            gy[r] = Hermite(m, u) * Math.Exp(-u * u / 2);
        }
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++) samples[r, c] = gy[r] * gx[c];
    }

    /// <summary>The physicists' Hermite polynomial by H₀ = 1, H₁ = 2u, H_{n+1} = 2u·H_n − 2n·H_{n−1}.</summary>
    internal static double Hermite(int n, double u)
    {
        if (n == 0) return 1;
        double previous = 1, current = 2 * u;
        for (int k = 1; k < n; k++)
        {
            double next = 2 * u * current - 2 * k * previous;
            previous = current;
            current = next;
        }
        return current;
    }

    /// <summary>
    /// The truncating aperture T(x, y) of the Gaussian waist beam, with the help's smoothing:
    /// "The smoothing function weights the pixel amplitude by the area of the pixel inside the
    /// truncating aperture." The area is counted on a 16 × 16 subgrid of each pixel the ellipse's
    /// edge crosses (exact to 1/256 of a pixel).
    /// </summary>
    internal static void Truncate(Complex[,] samples, double dx, double dy, double ax, double ay, double cx0, double cy0)
    {
        int ny = samples.GetLength(0), nx = samples.GetLength(1);
        const int sub = 16;
        for (int r = 0; r < ny; r++)
        {
            double y = (r - ny / 2) * dy - cy0;
            for (int c = 0; c < nx; c++)
            {
                double x = (c - nx / 2) * dx - cx0;
                double fraction = AreaInside(x, y, dx, dy, ax, ay, sub);
                if (fraction < 1) samples[r, c] *= fraction;
            }
        }
    }

    private static double AreaInside(double x, double y, double dx, double dy, double ax, double ay, int sub)
    {
        // Corners decide the clear cases: an ellipse is convex, so all four corners inside means
        // the pixel is inside; the pixel is outside when its nearest point is outside.
        bool In(double px, double py) => (px / ax) * (px / ax) + (py / ay) * (py / ay) <= 1;
        double hx = dx / 2, hy = dy / 2;
        if (In(x - hx, y - hy) && In(x + hx, y - hy) && In(x - hx, y + hy) && In(x + hx, y + hy)) return 1;
        double nxp = Math.Clamp(0, x - hx, x + hx), nyp = Math.Clamp(0, y - hy, y + hy);
        if (!In(nxp, nyp)) return 0;
        int count = 0;
        for (int i = 0; i < sub; i++)
            for (int k = 0; k < sub; k++)
                if (In(x - hx + (k + 0.5) * dx / sub, y - hy + (i + 0.5) * dy / sub)) count++;
        return count / (double)(sub * sub);
    }

    /// <summary>E = E0 inside the ellipse ((x−dx)/wx)² + ((y−dy)/wy)² ≤ 1, 0 outside (help, "Top
    /// Hat"), tested at each sample, added to <paramref name="samples"/> times
    /// <paramref name="amplitude"/>.</summary>
    internal static void TopHat(Complex[,] samples, double dx, double dy, double wx, double wy, double cx0, double cy0, Complex amplitude)
    {
        int ny = samples.GetLength(0), nx = samples.GetLength(1);
        for (int r = 0; r < ny; r++)
        {
            double y = ((r - ny / 2) * dy - cy0) / wy;
            for (int c = 0; c < nx; c++)
            {
                double x = ((c - nx / 2) * dx - cx0) / wx;
                if (x * x + y * y <= 1) samples[r, c] += amplitude;
            }
        }
    }

    /// <summary>
    /// A TEM00 beam with separate x and y waists, the beam lying z past each, in the physics
    /// convention: per axis √(q0/q)·exp(ik·x²/(2q)) with q = z − i·z_R and q0 = −i·z_R (derived
    /// in docs §66 from the paraxial wave equation; |·|² is a Gaussian of 1/e² radius ω(z), the
    /// phase k·x²/(2R) minus half the Gouy phase atan(z/z_R)).
    /// </summary>
    internal static void AstigmaticGaussian(Complex[,] samples, double dx, double dy, double wx, double zx, double wy, double zy,
        double decenterX, double decenterY, double lambdaN)
    {
        int ny = samples.GetLength(0), nx = samples.GetLength(1);
        double k = 2 * Math.PI / lambdaN;
        Complex[] Axis(int n, double d, double w, double z, double decenter)
        {
            double zR = Math.PI * w * w / lambdaN;
            var q = new Complex(z, -zR);
            var amplitude = Complex.Sqrt(new Complex(0, -zR) / q);
            var result = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                double x = (i - n / 2) * d - decenter;
                result[i] = amplitude * Complex.Exp(new Complex(0, k * x * x / 2) / q);
            }
            return result;
        }
        var ax = Axis(nx, dx, wx, zx, decenterX);
        var ay = Axis(ny, dy, wy, zy, decenterY);
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++) samples[r, c] = ay[r] * ax[c];
    }

    /// <summary>Divides out the reference sphere of <paramref name="pilot"/>: U = E·exp(−ik·r²/(2R)).</summary>
    internal static void RemoveReference(Complex[,] samples, double dx, double dy, PilotBeam pilot)
    {
        int ny = samples.GetLength(0), nx = samples.GetLength(1);
        double k = 2 * Math.PI / pilot.MediumWavelength, radius = pilot.DistanceFromWaist;
        for (int r = 0; r < ny; r++)
        {
            double y = (r - ny / 2) * dy;
            for (int c = 0; c < nx; c++)
            {
                double x = (c - nx / 2) * dx;
                samples[r, c] *= Complex.FromPolarCoordinates(1, -k * (x * x + y * y) / (2 * radius));
            }
        }
    }

    /// <summary>
    /// A pilot fitted to a field taken to lie at its waist: the waist √(Wx·Wy) from the second
    /// moments, W = 2σ (help, "Beam Width and M-Squared"). The help says only that "the initial
    /// parameters may be generated by fitting the Gaussian beam equations to the initial
    /// distribution" (help, "The Pilot Beam"); this fit is this program's (docs §66).
    /// </summary>
    internal static PilotBeam FitPilot(Complex[,] samples, double dx, double dy, double airWavelength, double index,
        ICollection<string> notes, string what)
    {
        var (sx, sy) = PopBeamMetrics.SpatialSigmas(samples, dx, dy);
        double w = Math.Sqrt(2 * sx * 2 * sy);
        if (!(w > 0) || !double.IsFinite(w))
            throw new ArgumentException($"Physical optics, {what}: the beam has no power on the grid, so no pilot beam can be fitted.");
        notes.Add($"Physical optics, {what}: the pilot beam is fitted to the field, a waist at the beam of radius √(Wx·Wy) = {w:g6} m from its second moments.");
        return PilotBeam.FromWaist(w, 0, index, airWavelength);
    }

    /// <summary>Scales the field to the total power or the peak irradiance asked for.</summary>
    private static PopField Normalise(PopField field, PopBeamDefinition beam, ICollection<string> notes)
    {
        if (!(beam.Power > 0) || !double.IsFinite(beam.Power))
            throw new ArgumentException($"Physical optics: the {(beam.Normalization == PopNormalization.TotalPower ? "total power" : "peak irradiance")} must be positive (it is {beam.Power}).");
        double current = beam.Normalization == PopNormalization.TotalPower ? field.Power : PopBeamMetrics.Peak(field.Samples);
        if (!(current > 0))
            throw new ArgumentException("Physical optics: the initial beam is zero everywhere on the grid (is it decentered off the array, or truncated to nothing?).");
        double scale = Math.Sqrt(beam.Power / current);
        var s = field.Samples;
        for (int r = 0; r < field.Ny; r++)
            for (int c = 0; c < field.Nx; c++) s[r, c] *= scale;
        return field;
    }
}
