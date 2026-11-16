using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Detectors;

/// <summary>
/// What a detector object collected in a run (plan item OP-79; docs/Optics-Conventions.md §73).
/// Every flux is in the launched rays' units (source units: watts for the source stand-in);
/// lengths are meters, angles degrees. The arrays are held, not copied; callers must not change
/// them.
/// </summary>
public abstract record NscDetectorData
{
    /// <summary>The detector's object number (1-based); 0 for data read from a file.</summary>
    public required int ObjectNumber { get; init; }

    /// <summary>OpticStudio's type name ("Detector Rectangle", "Detector Color", "Detector Polar").</summary>
    public abstract string TypeName { get; }

    /// <summary>Rays launched in the run(s) the data comes from (the N of the noise estimate).</summary>
    public required long LaunchedRays { get; init; }

    /// <summary>
    /// The standard error of a pixel's summed flux from the per-launched-ray contributions
    /// (docs/Optics-Conventions.md §73): with w_k the flux launched ray k left in the pixel
    /// (zero for most rays) and F = Σw_k, SE = √(N/(N − 1)·(Σw_k² − F²/N)). OpticStudio's help
    /// defines no noise estimate, so this is the textbook standard error of a sum of N independent
    /// samples; for Sobol-sampled rays it overstates the error. NaN when there are no squares
    /// (data read from a file) or fewer than two rays.
    /// </summary>
    protected double StandardError(double flux, double squares)
    {
        long n = LaunchedRays;
        if (n < 2 || double.IsNaN(squares)) return double.NaN;
        double v = (double)n / (n - 1) * (squares - flux * flux / n);
        return Math.Sqrt(Math.Max(0, v));
    }

    protected static double[] Sum(double[] a, double[] b, string what)
    {
        if (a.Length != b.Length) throw new ArgumentException($"Detector data cannot be combined: {what} has {a.Length} and {b.Length} pixels.");
        var r = new double[a.Length];
        for (int i = 0; i < r.Length; i++) r[i] = a[i] + b[i];
        return r;
    }

    protected static double[]? SumOptional(double[]? a, double[]? b, string what) =>
        a is null || b is null ? null : Sum(a, b, what);

    /// <summary>The sum of two runs' data of the same detector (OpticStudio sums every wavelength
    /// and source onto a detector, coherently for the coherent data: help, "Detector Rectangle
    /// Object", "Comments on coherent data computations").</summary>
    public abstract NscDetectorData Combine(NscDetectorData other);
}

/// <summary>
/// The data of a Detector Rectangle or Detector Color (help, "Detector Rectangle Object",
/// "Detector Color Object"): pixels in x and y on the flat face, numbered from the (−x, −y)
/// corner with x fastest; the same number of pixels in angle space over the x and y angle ranges.
/// </summary>
public sealed record NscFlatDetectorData : NscDetectorData
{
    public required bool IsColor { get; init; }
    public override string TypeName => IsColor ? "Detector Color" : "Detector Rectangle";

    public required double HalfWidthX { get; init; }
    public required double HalfWidthY { get; init; }
    public required int PixelsX { get; init; }
    public required int PixelsY { get; init; }
    public required double AngleXMin { get; init; }
    public required double AngleXMax { get; init; }
    public required double AngleYMin { get; init; }
    public required double AngleYMax { get; init; }

    /// <summary>Incoherent flux per position pixel.</summary>
    public required double[] Flux { get; init; }
    /// <summary>Σ over launched rays of the squared flux each left in the pixel (NaN-filled when unknown).</summary>
    public required double[] FluxSquares { get; init; }
    /// <summary>Incoherent flux per angle-space pixel.</summary>
    public required double[] AngleFlux { get; init; }
    public required double[] AngleFluxSquares { get; init; }

    /// <summary>683·V(λ)·flux per pixel (lumens when the rays carry watts); null when unknown.</summary>
    public double[]? Luminous { get; init; }
    public double[]? AngleLuminous { get; init; }

    /// <summary>Σ A cos φ, Σ A sin φ and Σ A per pixel, A = √(ray flux) (Detector Rectangle only).</summary>
    public double[]? CoherentReal { get; init; }
    public double[]? CoherentImaginary { get; init; }
    public double[]? CoherentAmplitude { get; init; }

    /// <summary>The object property "Normalize Coherent Power" (help, "Detector Settings").</summary>
    public bool NormalizeCoherentPower { get; init; } = true;

    /// <summary>Tristimulus X, Y, Z per position and angle pixel (Detector Color; Y in lumens when the rays carry watts).</summary>
    public double[]? X { get; init; }
    public double[]? Y { get; init; }
    public double[]? Z { get; init; }
    public double[]? AngleX { get; init; }
    public double[]? AngleY { get; init; }
    public double[]? AngleZ { get; init; }

    public long SpatialHits { get; init; }
    public long AngularHits { get; init; }

    /// <summary>"Record Spectral Data": flux per wavelength bin between the limits (µm), or null.</summary>
    public double[]? Spectrum { get; init; }
    public double SpectrumMinMicrometers { get; init; }
    public double SpectrumMaxMicrometers { get; init; }

    public int PixelCount => PixelsX * PixelsY;
    public double PixelWidthX => 2 * HalfWidthX / PixelsX;
    public double PixelWidthY => 2 * HalfWidthY / PixelsY;
    public double PixelArea => PixelWidthX * PixelWidthY;
    public double AngleStepX => (AngleXMax - AngleXMin) / PixelsX;
    public double AngleStepY => (AngleYMax - AngleYMin) / PixelsY;

    /// <summary>Pixel number − 1 of column <paramref name="ix"/> and row <paramref name="iy"/> (x fastest).</summary>
    public int Index(int ix, int iy) => iy * PixelsX + ix;

    /// <summary>The centre of a position pixel, local meters.</summary>
    public (double X, double Y) PixelCenter(int ix, int iy) =>
        (-HalfWidthX + (ix + 0.5) * PixelWidthX, -HalfWidthY + (iy + 0.5) * PixelWidthY);

    /// <summary>The centre of an angle pixel, degrees (x angle, y angle).</summary>
    public (double X, double Y) AngleCenter(int ix, int iy) =>
        (AngleXMin + (ix + 0.5) * AngleStepX, AngleYMin + (iy + 0.5) * AngleStepY);

    /// <summary>Incoherent irradiance: flux / pixel area ("The sum of the ray intensity divided by
    /// the pixel area yields the irradiance").</summary>
    public double Irradiance(int i) => Flux[i] / PixelArea;

    /// <summary>The standard error of pixel <paramref name="i"/>'s flux (see the base type).</summary>
    public double FluxStandardError(int i) => StandardError(Flux[i], FluxSquares[i]);

    public double AngleFluxStandardError(int i) => StandardError(AngleFlux[i], AngleFluxSquares[i]);

    /// <summary>
    /// The solid angle of an angle-space pixel, steradians. The x and y angles are θ·cos φ and
    /// θ·sin φ (help, "Comments on Detector Rectangle angular binning"), an azimuthal-equidistant
    /// map of the directions, whose area element is θ dθ dφ while the solid angle element is
    /// sin θ dθ dφ; so dΩ = (sin θ / θ) dX dY (derived), integrated over the pixel by 8 × 8-point
    /// Gauss–Legendre. Directions beyond θ = 180° do not exist and contribute nothing.
    /// </summary>
    public double AngleSolidAngle(int ix, int iy)
    {
        double toRad = Math.PI / 180;
        double x0 = (AngleXMin + ix * AngleStepX) * toRad, x1 = (AngleXMin + (ix + 1) * AngleStepX) * toRad;
        double y0 = (AngleYMin + iy * AngleStepY) * toRad, y1 = (AngleYMin + (iy + 1) * AngleStepY) * toRad;
        var (nx, wx) = GaussLegendre.Rule(8, x0, x1);
        var (ny, wy) = GaussLegendre.Rule(8, y0, y1);
        double sum = 0;
        for (int a = 0; a < nx.Length; a++)
            for (int b = 0; b < ny.Length; b++)
            {
                double t = Math.Sqrt(nx[a] * nx[a] + ny[b] * ny[b]);
                double f = t == 0 ? 1 : t > Math.PI ? 0 : Math.Sin(t) / t;
                sum += wx[a] * wy[b] * f;
            }
        return sum;
    }

    /// <summary>Radiant intensity: angle-space flux / the pixel's solid angle.</summary>
    public double RadiantIntensity(int ix, int iy) => AngleFlux[Index(ix, iy)] / AngleSolidAngle(ix, iy);

    /// <summary>Radiance in position space: irradiance / 2π, as the help defines it ("the data
    /// values divided by two pi … assumes the detector is collecting data over a hemisphere").</summary>
    public double RadiancePosition(int i) => Irradiance(i) / (2 * Math.PI);

    /// <summary>Radiance in angle space: radiant intensity / (detector area × cos θ at the pixel
    /// centre) (help, "Detector Viewer"); θ = √(x angle² + y angle²). NaN where cos θ ≤ 0.</summary>
    public double RadianceAngle(int ix, int iy)
    {
        var (ax, ay) = AngleCenter(ix, iy);
        double c = Math.Cos(Math.Sqrt(ax * ax + ay * ay) * Math.PI / 180);
        return c > 0 ? RadiantIntensity(ix, iy) / (4 * HalfWidthX * HalfWidthY * c) : double.NaN;
    }

    /// <summary>
    /// Coherent irradiance per pixel (help, "Detector Rectangle Object", "Comments on coherent
    /// data computations"): with more than one pixel and Normalize Coherent Power on, |ΣE|² per
    /// pixel rescaled so the detector's total equals its incoherent flux; otherwise
    /// I_coh = I_inc·(Re² + Im²)/S_amp², S_amp = ΣA_n. Throws when the data has no coherent sums.
    /// </summary>
    public double[] CoherentIrradiance()
    {
        if (CoherentReal is null || CoherentImaginary is null || CoherentAmplitude is null)
            throw new InvalidOperationException($"{TypeName}: coherent data is recorded by Detector Rectangle objects only.");
        int n = PixelCount;
        var r = new double[n];
        if (NormalizeCoherentPower && n > 1)
        {
            double total = 0, incoherent = 0;
            for (int i = 0; i < n; i++)
            {
                r[i] = CoherentReal[i] * CoherentReal[i] + CoherentImaginary[i] * CoherentImaginary[i];
                total += r[i];
                incoherent += Flux[i];
            }
            double scale = total > 0 ? incoherent / total / PixelArea : 0;
            for (int i = 0; i < n; i++) r[i] *= scale;
            return r;
        }
        for (int i = 0; i < n; i++)
        {
            double s = CoherentAmplitude[i];
            r[i] = s > 0 ? Irradiance(i) * (CoherentReal[i] * CoherentReal[i] + CoherentImaginary[i] * CoherentImaginary[i]) / (s * s) : 0;
        }
        return r;
    }

    /// <summary>The phase of the coherent sum, radians in (−π, π]; NaN where nothing landed.</summary>
    public double CoherentPhase(int i)
    {
        if (CoherentReal is null || CoherentImaginary is null)
            throw new InvalidOperationException($"{TypeName}: coherent data is recorded by Detector Rectangle objects only.");
        return CoherentReal[i] == 0 && CoherentImaginary[i] == 0 ? double.NaN : Math.Atan2(CoherentImaginary[i], CoherentReal[i]);
    }

    /// <summary>Beam centroid, RMS widths and RMS radius of the incoherent flux over the pixel
    /// centres, meters (the Detector Viewer's Beam Info: help, "Detector Viewer"; the moments are
    /// derived: x̄ = ΣF·x/ΣF, σx² = ΣF·(x − x̄)²/ΣF, r_rms = √(σx² + σy²)).</summary>
    public (double CentroidX, double CentroidY, double RmsX, double RmsY, double RmsRadius) BeamInfo()
    {
        double f = 0, fx = 0, fy = 0;
        for (int iy = 0; iy < PixelsY; iy++)
            for (int ix = 0; ix < PixelsX; ix++)
            {
                double w = Flux[Index(ix, iy)];
                var (x, y) = PixelCenter(ix, iy);
                f += w; fx += w * x; fy += w * y;
            }
        if (!(f > 0)) return (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        double cx = fx / f, cy = fy / f, sx = 0, sy = 0;
        for (int iy = 0; iy < PixelsY; iy++)
            for (int ix = 0; ix < PixelsX; ix++)
            {
                double w = Flux[Index(ix, iy)];
                var (x, y) = PixelCenter(ix, iy);
                sx += w * (x - cx) * (x - cx); sy += w * (y - cy) * (y - cy);
            }
        sx = Math.Sqrt(sx / f); sy = Math.Sqrt(sy / f);
        return (cx, cy, sx, sy, Math.Sqrt(sx * sx + sy * sy));
    }

    public override NscDetectorData Combine(NscDetectorData other)
    {
        if (other is not NscFlatDetectorData o || o.IsColor != IsColor || o.PixelsX != PixelsX || o.PixelsY != PixelsY
            || o.HalfWidthX != HalfWidthX || o.HalfWidthY != HalfWidthY || o.AngleXMin != AngleXMin || o.AngleXMax != AngleXMax
            || o.AngleYMin != AngleYMin || o.AngleYMax != AngleYMax)
            throw new ArgumentException($"{TypeName} data of object {ObjectNumber} cannot be combined with data of another size, pixel count or type.");
        return this with
        {
            LaunchedRays = LaunchedRays + o.LaunchedRays,
            Flux = Sum(Flux, o.Flux, "flux"), FluxSquares = Sum(FluxSquares, o.FluxSquares, "flux"),
            AngleFlux = Sum(AngleFlux, o.AngleFlux, "angle flux"), AngleFluxSquares = Sum(AngleFluxSquares, o.AngleFluxSquares, "angle flux"),
            Luminous = SumOptional(Luminous, o.Luminous, "luminous flux"), AngleLuminous = SumOptional(AngleLuminous, o.AngleLuminous, "luminous flux"),
            CoherentReal = SumOptional(CoherentReal, o.CoherentReal, "coherent"), CoherentImaginary = SumOptional(CoherentImaginary, o.CoherentImaginary, "coherent"),
            CoherentAmplitude = SumOptional(CoherentAmplitude, o.CoherentAmplitude, "coherent"),
            X = SumOptional(X, o.X, "X"), Y = SumOptional(Y, o.Y, "Y"), Z = SumOptional(Z, o.Z, "Z"),
            AngleX = SumOptional(AngleX, o.AngleX, "X"), AngleY = SumOptional(AngleY, o.AngleY, "Y"), AngleZ = SumOptional(AngleZ, o.AngleZ, "Z"),
            SpatialHits = SpatialHits + o.SpatialHits, AngularHits = AngularHits + o.AngularHits,
            Spectrum = SumOptional(Spectrum, o.Spectrum, "spectrum"),
        };
    }
}

/// <summary>
/// The data of a Detector Polar (help, "Detector Polar Object"): flux binned by the direction of
/// the ray in polar angle θ from the local z axis and azimuth φ = tan⁻¹(y/x) from +x
/// counter-clockwise; pixels numbered with the polar index fastest. The polar bins follow the
/// help's figure "Detector Polar sampling": inc = maximum angle/(# radial pixels − 1); pixel 1 is
/// [0, inc/2], pixel j is [inc(j − 2 + ½), inc(j − 1 + ½)], the last ends at the maximum angle.
/// </summary>
public sealed record NscPolarDetectorData : NscDetectorData
{
    public override string TypeName => "Detector Polar";

    /// <summary>Degrees, 1–180.</summary>
    public required double MaxAngle { get; init; }
    /// <summary>Meters.</summary>
    public required double RadialSize { get; init; }
    public required int PolarPixels { get; init; }
    public required int AzimuthPixels { get; init; }

    public required double[] Flux { get; init; }
    public required double[] FluxSquares { get; init; }
    public double[]? Luminous { get; init; }
    public double[]? X { get; init; }
    public double[]? Y { get; init; }
    public double[]? Z { get; init; }
    public long Hits { get; init; }

    public int PixelCount => PolarPixels * AzimuthPixels;
    public double Increment => MaxAngle / (PolarPixels - 1);
    public double AzimuthStep => 360.0 / AzimuthPixels;

    /// <summary>Pixel number − 1 of polar index <paramref name="j"/> on azimuth arm <paramref name="a"/>.</summary>
    public int Index(int j, int a) => a * PolarPixels + j;

    /// <summary>The polar-angle limits of polar index <paramref name="j"/>, degrees.</summary>
    public (double From, double To) PolarBounds(int j)
    {
        double inc = Increment;
        double from = j == 0 ? 0 : inc * (j - 0.5);
        double to = j == PolarPixels - 1 ? MaxAngle : inc * (j + 0.5);
        return (from, to);
    }

    /// <summary>The polar index of a polar angle (degrees), or −1 beyond the maximum angle.</summary>
    public int PolarIndex(double theta)
    {
        if (!(theta >= 0) || theta > MaxAngle) return -1;
        double inc = Increment;
        if (theta <= inc / 2) return 0;
        int j = (int)Math.Ceiling(theta / inc - 0.5);   // ](j − ½)inc, (j + ½)inc]
        return Math.Min(j, PolarPixels - 1);
    }

    /// <summary>The azimuth arm of an azimuth (degrees); arm a is centred on a·Δφ (the help's
    /// figure shows arm 0 centred on 0°).</summary>
    public int AzimuthIndex(double phi)
    {
        double step = AzimuthStep;
        double u = (phi + step / 2) / step;
        int a = (int)Math.Floor(u) % AzimuthPixels;
        return a < 0 ? a + AzimuthPixels : a;
    }

    /// <summary>The pixel's solid angle: Δφ·(cos θ₁ − cos θ₂) (derived from dΩ = sin θ dθ dφ).</summary>
    public double SolidAngle(int j)
    {
        var (from, to) = PolarBounds(j);
        return AzimuthStep * Math.PI / 180 * (Math.Cos(from * Math.PI / 180) - Math.Cos(to * Math.PI / 180));
    }

    /// <summary>Radiant intensity: flux / solid angle.</summary>
    public double RadiantIntensity(int j, int a) => Flux[Index(j, a)] / SolidAngle(j);

    public double FluxStandardError(int i) => StandardError(Flux[i], FluxSquares[i]);

    public override NscDetectorData Combine(NscDetectorData other)
    {
        if (other is not NscPolarDetectorData o || o.MaxAngle != MaxAngle || o.PolarPixels != PolarPixels || o.AzimuthPixels != AzimuthPixels)
            throw new ArgumentException($"Detector Polar data of object {ObjectNumber} cannot be combined with data of another angle or pixel count.");
        return this with
        {
            LaunchedRays = LaunchedRays + o.LaunchedRays,
            Flux = Sum(Flux, o.Flux, "flux"), FluxSquares = Sum(FluxSquares, o.FluxSquares, "flux"),
            Luminous = SumOptional(Luminous, o.Luminous, "luminous flux"),
            X = SumOptional(X, o.X, "X"), Y = SumOptional(Y, o.Y, "Y"), Z = SumOptional(Z, o.Z, "Z"),
            Hits = Hits + o.Hits,
        };
    }
}

/// <summary>Combining the per-wavelength runs of one scene.</summary>
public static class NscDetectorResults
{
    /// <summary>Each detector's data summed over <paramref name="runs"/> in the order given
    /// (detectors matched by object number). Null when no run has detector data.</summary>
    public static IReadOnlyList<NscDetectorData>? Combine(IEnumerable<NscRunResult> runs)
    {
        Dictionary<int, NscDetectorData>? total = null;
        var order = new List<int>();
        foreach (var run in runs)
        {
            if (run.Detectors is null) continue;
            total ??= new Dictionary<int, NscDetectorData>();
            foreach (var d in run.Detectors)
            {
                if (total.TryGetValue(d.ObjectNumber, out var t)) total[d.ObjectNumber] = t.Combine(d);
                else { total[d.ObjectNumber] = d; order.Add(d.ObjectNumber); }
            }
        }
        return total is null ? null : order.Select(k => total[k]).ToArray();
    }
}
