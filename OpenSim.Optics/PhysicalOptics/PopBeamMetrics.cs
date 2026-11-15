using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>
/// The beam quantities of the help's "Quantitative Beam Analysis" pages for one field, in
/// meters, watts and radians (OP-49; docs/Optics-Conventions.md §66). The second moments are
/// those of the Wigner distribution (help, "Second Order Moments of the Wigner Distribution",
/// after Nemes and Siegman, ISO 11146-3 and Siegman 1991): spatial moments from |E|², angular and
/// mixed moments from E and its gradient, angles in the medium.
/// </summary>
public sealed record PopBeamData
{
    public required double Power { get; init; }
    public required double PeakIrradiance { get; init; }

    /// <summary>The centroid relative to the array centre (the chief ray), meters.</summary>
    public required double CentroidX { get; init; }
    public required double CentroidY { get; init; }

    /// <summary>⟨x²⟩, ⟨y²⟩, ⟨xy⟩ about the centroid, m².</summary>
    public required double XX { get; init; }
    public required double YY { get; init; }
    public required double XY { get; init; }

    /// <summary>⟨θx⟩, ⟨θy⟩, radians.</summary>
    public required double ThetaX { get; init; }
    public required double ThetaY { get; init; }

    /// <summary>⟨θx²⟩, ⟨θy²⟩, ⟨θxθy⟩ about the mean angles, rad².</summary>
    public required double ThetaXX { get; init; }
    public required double ThetaYY { get; init; }
    public required double ThetaXY { get; init; }

    /// <summary>⟨xθx⟩, ⟨yθy⟩, ⟨xθy⟩, ⟨yθx⟩, centred, m·rad.</summary>
    public required double XThetaX { get; init; }
    public required double YThetaY { get; init; }
    public required double XThetaY { get; init; }
    public required double YThetaX { get; init; }

    /// <summary>The beam widths (1/e² radius) W = 2σ along x and y (help, "Beam Width and
    /// M-Squared").</summary>
    public double WidthX => 2 * Math.Sqrt(XX);
    public double WidthY => 2 * Math.Sqrt(YY);

    /// <summary>The widths along the principal axes (help, "Beam Width and M-Squared":
    /// W = √2·{(σx² + σy²) ± γ[(σx² − σy²)² + 4(σxy²)²]^½}^½, γ = sgn(σx² − σy²); γ = 1 when they
    /// are equal).</summary>
    public (double X, double Y) PrincipalWidths
    {
        get
        {
            double gamma = XX >= YY ? 1 : -1;
            double root = Math.Sqrt((XX - YY) * (XX - YY) + 4 * XY * XY);
            return (Math.Sqrt(2) * Math.Sqrt(Math.Max(0, XX + YY + gamma * root)),
                    Math.Sqrt(2) * Math.Sqrt(Math.Max(0, XX + YY - gamma * root)));
        }
    }

    /// <summary>The medium wavelength the angular moments are in, meters.</summary>
    public required double MediumWavelength { get; init; }

    /// <summary>M² = (4π/λ)·√(⟨x²⟩⟨θx²⟩ − ⟨xθx⟩²), Siegman's beam-quality invariant, which is 1
    /// for a TEM00 beam and 2l + 1 for a Hermite–Gaussian of order l (docs §66).</summary>
    public double MSquaredX => 4 * Math.PI / MediumWavelength * Math.Sqrt(Math.Max(0, XX * ThetaXX - XThetaX * XThetaX));
    public double MSquaredY => 4 * Math.PI / MediumWavelength * Math.Sqrt(Math.Max(0, YY * ThetaYY - YThetaY * YThetaY));
}

/// <summary>Computes <see cref="PopBeamData"/>, enclosed power and the irradiance and phase
/// statistics of the POPD operand.</summary>
public static class PopBeamMetrics
{
    public static double Peak(Complex[,] samples)
    {
        double peak = 0;
        foreach (var u in samples) peak = Math.Max(peak, u.Real * u.Real + u.Imaginary * u.Imaginary);
        return peak;
    }

    /// <summary>σx and σy of |U|² about its centroid (sample coordinates centred on index n/2).</summary>
    public static (double SigmaX, double SigmaY) SpatialSigmas(Complex[,] samples, double dx, double dy)
    {
        int ny = samples.GetLength(0), nx = samples.GetLength(1);
        double p = 0, sx = 0, sy = 0;
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++)
            {
                double i = Norm(samples[r, c]);
                p += i; sx += i * (c - nx / 2) * dx; sy += i * (r - ny / 2) * dy;
            }
        if (!(p > 0)) return (0, 0);
        double cx = sx / p, cy = sy / p, xx = 0, yy = 0;
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++)
            {
                double i = Norm(samples[r, c]);
                double x = (c - nx / 2) * dx - cx, y = (r - ny / 2) * dy - cy;
                xx += i * x * x; yy += i * y * y;
            }
        return (Math.Sqrt(xx / p), Math.Sqrt(yy / p));
    }

    private static double Norm(Complex u) => u.Real * u.Real + u.Imaginary * u.Imaginary;

    /// <summary>Every quantity of <see cref="PopBeamData"/> for <paramref name="field"/>. The
    /// gradient is spectral (the field's DFT times i2πf, the Nyquist bin dropped), applied to the
    /// stored U, and the reference sphere's gradient ik·x/R·U is added analytically, so the
    /// angles are those of the field E itself.</summary>
    public static PopBeamData Compute(PopField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        int nx = field.Nx, ny = field.Ny;
        double dx = field.Dx, dy = field.Dy;
        var u = field.Samples;
        double power = field.Power;
        if (!(power > 0))
            throw new InvalidOperationException("Physical optics: the beam has no power left (every sample is zero), so its centroid, widths and M² are not defined.");
        var spectrum = Fft2D.Forward(u);
        var sx = new Complex[ny, nx];
        var sy = new Complex[ny, nx];
        for (int r = 0; r < ny; r++)
        {
            int pr = PopPropagator.Signed(r, ny);
            double fy = pr == -ny / 2 ? 0 : pr / (ny * dy);
            for (int c = 0; c < nx; c++)
            {
                int pc = PopPropagator.Signed(c, nx);
                double fx = pc == -nx / 2 ? 0 : pc / (nx * dx);
                sx[r, c] = spectrum[r, c] * new Complex(0, 2 * Math.PI * fx);
                sy[r, c] = spectrum[r, c] * new Complex(0, 2 * Math.PI * fy);
            }
        }
        var dux = Fft2D.Inverse(sx);
        var duy = Fft2D.Inverse(sy);
        double k = field.Wavenumber;
        double curvature = field.Reference == PopReference.Sphere ? 1 / field.ReferenceRadius : 0;

        double s0 = 0, s1x = 0, s1y = 0, peak = 0;
        double ax = 0, ay = 0;
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++)
            {
                double i = Norm(u[r, c]);
                double x = field.X(c), y = field.Y(r);
                s0 += i; s1x += i * x; s1y += i * y;
                peak = Math.Max(peak, i);
                var (gx, gy) = Gradient(u[r, c], dux[r, c], duy[r, c], x, y, k, curvature);
                ax += (Complex.Conjugate(u[r, c]) * gx).Imaginary;
                ay += (Complex.Conjugate(u[r, c]) * gy).Imaginary;
            }
        double cx = s1x / s0, cy = s1y / s0;
        double scale = 1 / k;   // λ/(2π)
        double tx = scale * ax / s0, ty = scale * ay / s0;

        double xx = 0, yy = 0, xy = 0, gxx = 0, gyy = 0, gxy = 0, xtx = 0, yty = 0, xty = 0, ytx = 0;
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++)
            {
                double i = Norm(u[r, c]);
                double x = field.X(c) - cx, y = field.Y(r) - cy;
                xx += i * x * x; yy += i * y * y; xy += i * x * y;
                var (gx, gy) = Gradient(u[r, c], dux[r, c], duy[r, c], field.X(c), field.Y(r), k, curvature);
                gxx += Norm(gx); gyy += Norm(gy);
                gxy += (Complex.Conjugate(gx) * gy).Real;
                double jx = (Complex.Conjugate(u[r, c]) * gx).Imaginary, jy = (Complex.Conjugate(u[r, c]) * gy).Imaginary;
                xtx += x * jx; yty += y * jy; xty += x * jy; ytx += y * jx;
            }
        return new PopBeamData
        {
            Power = power, PeakIrradiance = peak,
            CentroidX = cx, CentroidY = cy,
            XX = xx / s0, YY = yy / s0, XY = xy / s0,
            ThetaX = tx, ThetaY = ty,
            ThetaXX = scale * scale * gxx / s0 - tx * tx,
            ThetaYY = scale * scale * gyy / s0 - ty * ty,
            ThetaXY = scale * scale * gxy / s0 - tx * ty,
            XThetaX = scale * xtx / s0, YThetaY = scale * yty / s0,
            XThetaY = scale * xty / s0, YThetaX = scale * ytx / s0,
            MediumWavelength = field.Pilot.MediumWavelength,
        };
    }

    /// <summary>∇E·exp(−iφ_ref) = ∇U + i·k·(x, y)/R·U.</summary>
    private static (Complex X, Complex Y) Gradient(Complex u, Complex dux, Complex duy, double x, double y, double k, double curvature) =>
        (dux + new Complex(0, k * x * curvature) * u, duy + new Complex(0, k * y * curvature) * u);

    /// <summary>The fraction of the power within <paramref name="radius"/> of (x0, y0), samples
    /// counted whole by their centres (help, "Encircled Energy (quantitative beam analysis)").</summary>
    public static double EnclosedFraction(PopField field, double x0, double y0, double radius)
    {
        double inside = 0, total = 0;
        for (int r = 0; r < field.Ny; r++)
            for (int c = 0; c < field.Nx; c++)
            {
                double i = Norm(field.Samples[r, c]);
                total += i;
                double x = field.X(c) - x0, y = field.Y(r) - y0;
                if (x * x + y * y <= radius * radius) inside += i;
            }
        return total > 0 ? inside / total : 0;
    }

    /// <summary>The encircled power about (x0, y0): the sample radii in increasing order and the
    /// fraction within each.</summary>
    public static (double[] Radius, double[] Fraction) EnclosedCurve(PopField field, double x0, double y0)
    {
        int n = field.Nx * field.Ny;
        var radius = new double[n];
        var power = new double[n];
        int k = 0;
        double total = 0;
        for (int r = 0; r < field.Ny; r++)
            for (int c = 0; c < field.Nx; c++, k++)
            {
                double x = field.X(c) - x0, y = field.Y(r) - y0;
                radius[k] = Math.Sqrt(x * x + y * y);
                power[k] = Norm(field.Samples[r, c]);
                total += power[k];
            }
        Array.Sort(radius, power);
        double running = 0;
        for (int i = 0; i < n; i++) { running += power[i]; power[i] = total > 0 ? running / total : 0; }
        return (radius, power);
    }

    /// <summary>The radius within which <paramref name="fraction"/> of the power lies about
    /// (x0, y0), interpolated linearly between sample radii.</summary>
    public static double RadiusEnclosing(PopField field, double x0, double y0, double fraction)
    {
        if (!(fraction > 0) || !(fraction <= 1))
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "Physical optics: the enclosed fraction must be in (0, 1].");
        var (radius, f) = EnclosedCurve(field, x0, y0);
        for (int i = 0; i < radius.Length; i++)
        {
            if (f[i] < fraction) continue;
            if (i == 0) return radius[0];
            double t = (fraction - f[i - 1]) / (f[i] - f[i - 1]);
            return radius[i - 1] + t * (radius[i] - radius[i - 1]);
        }
        return radius[^1];
    }

    /// <summary>The mean, RMS (about the mean) and peak-to-valley of the irradiance and of the
    /// printed phase (radians, −arg U) over the samples whose irradiance is at least
    /// <paramref name="relativeThreshold"/> of the peak: the "non-zero amplitude portion of the
    /// beam" of POPD data 30 to 35.</summary>
    public static (double Mean, double Rms, double Ptv) Variation(PopField field, bool phase, double relativeThreshold)
    {
        double peak = Peak(field.Samples);
        double limit = relativeThreshold * peak;
        var values = new List<double>();
        foreach (var u in field.Samples)
        {
            double i = Norm(u);
            if (i <= 0 || i < limit) continue;
            values.Add(phase ? -Math.Atan2(u.Imaginary, u.Real) : i);
        }
        if (values.Count == 0) return (0, 0, 0);
        double mean = values.Average();
        double rms = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
        return (mean, rms, values.Max() - values.Min());
    }
}
