using OpenSim.Core.Numerics;
using OpenSim.Optics.Photometry;

namespace OpenSim.Optics.NonSequential.Detectors;

/// <summary>
/// The parameters Detector Rectangle and Detector Color share (help, "Detector Rectangle
/// Object", "Detector Color Object"): x and y half widths (meters), pixel counts, the display
/// parameters 5–9 (kept for the shaded model, not used by the trace), Front Only, the angle
/// ranges (degrees; the help's default is −90 to +90 in both), the polarization flag and the
/// mirroring flag; plus the object properties Use Pixel Interpolation (help, "Detector Settings").
/// </summary>
public abstract class NscFlatDetector : NscGeometry, INscDetector
{
    private static readonly IReadOnlyDictionary<int, string> FaceNames = new Dictionary<int, string> { [0] = "All Faces" };

    public double HalfWidthX { get; }
    public double HalfWidthY { get; }
    public int PixelsX { get; }
    public int PixelsY { get; }
    public bool FrontOnly { get; }
    public double AngleXMin { get; }
    public double AngleXMax { get; }
    public double AngleYMin { get; }
    public double AngleYMax { get; }

    /// <summary>0 none; 1 left-right; 2 top-bottom; 3 both; 4–6 the same without scaling the copies down.</summary>
    public int Mirroring { get; }

    /// <summary>"Use Pixel Interpolation": each ray's incoherent flux shared bilinearly among the
    /// pixel centres around the hit (docs/Optics-Conventions.md §73).</summary>
    public bool PixelInterpolation { get; }

    /// <summary>Parameters 5–9 as saved (data type, colour, smoothing, scale, plot scale): display only.</summary>
    public IReadOnlyList<double> DisplayParameters { get; }

    public override bool IsVolume => false;
    public override IReadOnlyDictionary<int, string> Faces => FaceNames;
    public override Aabb LocalBounds { get; }

    /// <summary>The help's pixel limit for 64-bit builds ("6000 for 64-bit versions").</summary>
    public const int MaxPixels = 6000;

    protected NscFlatDetector(string type, double halfWidthX, double halfWidthY, int pixelsX, int pixelsY, bool frontOnly,
        double angleXMin, double angleXMax, double angleYMin, double angleYMax, int polarization, int mirroring,
        bool pixelInterpolation, IReadOnlyList<double>? display)
    {
        var p = new List<string>();
        if (!(halfWidthX > 0) || !double.IsFinite(halfWidthX) || !(halfWidthY > 0) || !double.IsFinite(halfWidthY))
            p.Add($"both half widths must be positive and finite (meters); they are {halfWidthX} and {halfWidthY}");
        if (pixelsX < 1 || pixelsY < 1 || pixelsX > MaxPixels || pixelsY > MaxPixels)
            p.Add($"the pixel counts must be 1 to {MaxPixels} (help); they are {pixelsX} × {pixelsY}");
        if (!(angleXMin < angleXMax) || !(angleYMin < angleYMax) || !double.IsFinite(angleXMin + angleXMax + angleYMin + angleYMax))
            p.Add($"the angle ranges must be finite with minimum below maximum; they are x {angleXMin} to {angleXMax}, y {angleYMin} to {angleYMax} degrees");
        if (mirroring < 0 || mirroring > 6) p.Add($"the mirroring flag must be 0 to 6; it is {mirroring}");
        if (polarization < 0 || polarization > 9) p.Add($"the polarization flag must be 0 to 9; it is {polarization}");
        else if (polarization != 0)
            p.Add($"polarization flag {polarization} (only one field component counted) is not built: the non-sequential trace carries each ray's coherency matrix, not its field and phase; use 0");
        if (p.Count > 0) throw new ArgumentException($"{type}: {string.Join("; ", p)}.");
        HalfWidthX = halfWidthX; HalfWidthY = halfWidthY; PixelsX = pixelsX; PixelsY = pixelsY; FrontOnly = frontOnly;
        AngleXMin = angleXMin; AngleXMax = angleXMax; AngleYMin = angleYMin; AngleYMax = angleYMax;
        Mirroring = mirroring; PixelInterpolation = pixelInterpolation;
        DisplayParameters = display?.ToArray() ?? Array.Empty<double>();
        LocalBounds = new Aabb(new Vector3D(-halfWidthX, -halfWidthY, 0), new Vector3D(halfWidthX, halfWidthY, 0));
    }

    /// <summary>The flat face z = 0 within the half widths. With Front Only, rays travelling
    /// toward −z ("coming from the back", the side facing +z) miss: "The Front Only flag is
    /// applied before any materials are considered."</summary>
    public override void Intersect(Vector3D origin, Vector3D direction, double tMin, double tMax, List<NscLocalHit> hits)
    {
        if (direction.Z == 0 || (FrontOnly && direction.Z < 0)) return;
        double t = -origin.Z / direction.Z;
        if (!(t > tMin && t <= tMax)) return;
        double x = origin.X + t * direction.X, y = origin.Y + t * direction.Y;
        if (Math.Abs(x) > HalfWidthX || Math.Abs(y) > HalfWidthY) return;
        Add(hits, t, tMin, tMax, new Vector3D(x, y, 0), Vector3D.UnitZ, 0);
    }

    public override bool IsInside(Vector3D point) => false;

    public override string Describe(Func<double, string> length) =>
        $"half widths {length(HalfWidthX)} × {length(HalfWidthY)}, {PixelsX} × {PixelsY} pixels{(FrontOnly ? ", front only" : "")}";

    public abstract NscDetectorAccumulator CreateAccumulator(int objectNumber, double airMicrometers);

    /// <summary>The mirror copies of a hit (sign of x, sign of y, weight factor) for the mirroring flag.</summary>
    internal static (int Sx, int Sy, double W)[] MirrorCopies(int mirroring) => mirroring switch
    {
        1 => new[] { (1, 1, 0.5), (-1, 1, 0.5) },
        2 => new[] { (1, 1, 0.5), (1, -1, 0.5) },
        3 => new[] { (1, 1, 0.25), (-1, 1, 0.25), (1, -1, 0.25), (-1, -1, 0.25) },
        4 => new[] { (1, 1, 1.0), (-1, 1, 1.0) },
        5 => new[] { (1, 1, 1.0), (1, -1, 1.0) },
        6 => new[] { (1, 1, 1.0), (-1, 1, 1.0), (1, -1, 1.0), (-1, -1, 1.0) },
        _ => new[] { (1, 1, 1.0) },
    };
}

/// <summary>
/// OpticStudio's Detector Rectangle (help, "Detector Rectangle Object"): incoherent flux per
/// pixel in position and angle space, and the coherent sums Σ A cos φ, Σ A sin φ, Σ A per pixel,
/// A = √(flux), φ the phase of the ray's optical path referenced to the centre of the pixel
/// struck (docs/Optics-Conventions.md §73). PSF mode (parameter 11 ≠ 0) is not built.
/// </summary>
public sealed class NscDetectorRectangle : NscFlatDetector
{
    public override string TypeName => "Detector Rectangle";

    /// <summary>The object property "Normalize Coherent Power" (help: on by default).</summary>
    public bool NormalizeCoherentPower { get; }

    public NscDetectorRectangle(double halfWidthX, double halfWidthY, int pixelsX, int pixelsY,
        bool frontOnly = false, double angleXMin = -90, double angleXMax = 90, double angleYMin = -90, double angleYMax = 90,
        int polarization = 0, int mirroring = 0, int psfWavenumber = 0, bool pixelInterpolation = false,
        bool normalizeCoherentPower = true, IReadOnlyList<double>? display = null)
        : base("Detector Rectangle", halfWidthX, halfWidthY, pixelsX, pixelsY, frontOnly, angleXMin, angleXMax, angleYMin, angleYMax,
            polarization, mirroring, pixelInterpolation, display)
    {
        if (psfWavenumber != 0)
            throw new ArgumentException($"Detector Rectangle: PSF mode (parameter 11, PSF Mode Wavenumber {psfWavenumber}) is not built; the Huygens integral of the rays onto the detector waits for the NSC physical-optics items. Use 0.");
        NormalizeCoherentPower = normalizeCoherentPower;
    }

    public override NscDetectorAccumulator CreateAccumulator(int objectNumber, double airMicrometers) =>
        new FlatAccumulator(this, objectNumber, airMicrometers, coherent: true, color: false, null);
}

/// <summary>
/// OpticStudio's Detector Color (help, "Detector Color Object"): flux and tristimulus X, Y, Z per
/// pixel in position and angle space (Y in lumens when rays carry watts, docs/Optics-Conventions.md
/// §73), and optionally the spectrum of all rays landing on it ("Record Spectral Data").
/// </summary>
public sealed class NscDetectorColor : NscFlatDetector
{
    public override string TypeName => "Detector Color";

    /// <summary>"Record Spectral Data": number of bins and limits (µm), or null.</summary>
    public (int Bins, double MinMicrometers, double MaxMicrometers)? Spectral { get; }

    public NscDetectorColor(double halfWidthX, double halfWidthY, int pixelsX, int pixelsY,
        bool frontOnly = false, double angleXMin = -90, double angleXMax = 90, double angleYMin = -90, double angleYMax = 90,
        int polarization = 0, int mirroring = 0, bool pixelInterpolation = false,
        (int Bins, double MinMicrometers, double MaxMicrometers)? spectral = null, IReadOnlyList<double>? display = null)
        : base("Detector Color", halfWidthX, halfWidthY, pixelsX, pixelsY, frontOnly, angleXMin, angleXMax, angleYMin, angleYMax,
            polarization, mirroring, pixelInterpolation, display)
    {
        if (spectral is { } s && (s.Bins < 1 || !(s.MinMicrometers > 0) || !(s.MaxMicrometers > s.MinMicrometers)))
            throw new ArgumentException($"Detector Color: Record Spectral Data needs at least one bin and 0 < min wave < max wave (µm); it has {s.Bins} bins from {s.MinMicrometers} to {s.MaxMicrometers}.");
        Spectral = spectral;
    }

    public override NscDetectorAccumulator CreateAccumulator(int objectNumber, double airMicrometers) =>
        new FlatAccumulator(this, objectNumber, airMicrometers, coherent: false, color: true, Spectral);
}

/// <summary>The accumulator of a Detector Rectangle or Color.</summary>
internal sealed class FlatAccumulator : NscDetectorAccumulator
{
    private readonly NscFlatDetector _g;
    private readonly int _object;
    private readonly bool _coherent, _color;
    private readonly double _lumens, _tx, _ty, _tz, _waveNumber, _micrometers;
    private readonly (int Sx, int Sy, double W)[] _copies;
    private readonly (int Bins, double Min, double Max)? _spectral;

    internal readonly double[] Flux, Squares, AngleFlux, AngleSquares, Luminous, AngleLuminous;
    internal readonly double[]? Re, Im, Amp, X, Y, Z, AX, AY, AZ, Spectrum;
    internal long SpatialHits, AngularHits;
    private readonly Dictionary<int, double> _rayPos = new(), _rayAng = new();

    public FlatAccumulator(NscFlatDetector g, int objectNumber, double airMicrometers, bool coherent, bool color,
        (int, double, double)? spectral)
    {
        _g = g; _object = objectNumber; _coherent = coherent; _color = color; _spectral = spectral; _micrometers = airMicrometers;
        int n = g.PixelsX * g.PixelsY;
        Flux = new double[n]; Squares = new double[n]; AngleFlux = new double[n]; AngleSquares = new double[n];
        Luminous = new double[n]; AngleLuminous = new double[n];
        _lumens = Photometric.LumensPerWatt(airMicrometers);
        // Phase k·OPL with the optical path relative to the system's air: k = 2π/λ in that air.
        _waveNumber = 2 * Math.PI / (airMicrometers * 1e-6);
        if (coherent) { Re = new double[n]; Im = new double[n]; Amp = new double[n]; }
        if (color)
        {
            (_tx, _ty, _tz) = Photometric.TristimulusPerWatt(airMicrometers);
            X = new double[n]; Y = new double[n]; Z = new double[n]; AX = new double[n]; AY = new double[n]; AZ = new double[n];
        }
        if (spectral is { } s) Spectrum = new double[s.Item1];
        _copies = NscFlatDetector.MirrorCopies(g.Mirroring);
    }

    public override void Record(in NscDetectorHit hit)
    {
        var g = _g;
        double dx = 2 * g.HalfWidthX / g.PixelsX, dy = 2 * g.HalfWidthY / g.PixelsY;
        var d = hit.LocalDirection;
        double nz = Math.Clamp(d.Z, -1, 1);
        double theta = Math.Acos(nz) * 180 / Math.PI;     // |cos⁻¹ n|, degrees
        double sin = Math.Sqrt(Math.Max(0, 1 - nz * nz));
        // OP-81: a hit at a wavelength other than the run's (a volume event shifted it) is weighted
        // at its own wavelength; at the run's wavelength the run's factors are used as before.
        double um = hit.AirMicrometers, lumens = _lumens, tx = _tx, ty = _ty, tz = _tz, waveNumber = _waveNumber;
        if (um != _micrometers)
        {
            lumens = Photometric.LumensPerWatt(um);
            if (_color) (tx, ty, tz) = Photometric.TristimulusPerWatt(um);
            waveNumber = 2 * Math.PI / (um * 1e-6);
        }
        if (Spectrum is not null && _spectral is { } s && um >= s.Min && um <= s.Max)
        {
            int b = Math.Min(s.Bins - 1, (int)((um - s.Min) / (s.Max - s.Min) * s.Bins));
            Spectrum[b] += hit.Power;
        }
        foreach (var (sx, sy, wf) in _copies)
        {
            double w = hit.Power * wf;
            double x = sx * hit.LocalPoint.X, y = sy * hit.LocalPoint.Y;
            double l = sx * d.X, m = sy * d.Y;
            // ---- position space
            int ix = Math.Clamp((int)Math.Floor((x + g.HalfWidthX) / dx), 0, g.PixelsX - 1);
            int iy = Math.Clamp((int)Math.Floor((y + g.HalfWidthY) / dy), 0, g.PixelsY - 1);
            SpatialHits++;
            if (g.PixelInterpolation) Interpolated(x, y, w, dx, dy, lumens, tx, ty, tz);   // OP-81: the hit's factors
            else Deposit(iy * g.PixelsX + ix, w, lumens, tx, ty, tz);
            if (_coherent)
            {
                // φ = k·(OPL + n·d·(c − p)): the ray's phase carried as a local plane wave to the
                // centre c of the pixel struck (§73).
                double cx = -g.HalfWidthX + (ix + 0.5) * dx, cy = -g.HalfWidthY + (iy + 0.5) * dy;
                double phase = waveNumber * (hit.OpticalPath + hit.MediumIndex * (l * (cx - x) + m * (cy - y)));
                double a = Math.Sqrt(w);
                int k = iy * g.PixelsX + ix;
                Re![k] += a * Math.Cos(phase);
                Im![k] += a * Math.Sin(phase);
                Amp![k] += a;
            }
            // ---- angle space: X angle = θ·l/√(1 − n²), Y angle = θ·m/√(1 − n²) (help).
            double ax = sin > 0 ? theta * l / sin : 0, ay = sin > 0 ? theta * m / sin : 0;
            if (ax >= g.AngleXMin && ax <= g.AngleXMax && ay >= g.AngleYMin && ay <= g.AngleYMax)
            {
                int jx = Math.Min(g.PixelsX - 1, (int)Math.Floor((ax - g.AngleXMin) / (g.AngleXMax - g.AngleXMin) * g.PixelsX));
                int jy = Math.Min(g.PixelsY - 1, (int)Math.Floor((ay - g.AngleYMin) / (g.AngleYMax - g.AngleYMin) * g.PixelsY));
                int k = jy * g.PixelsX + jx;
                AngularHits++;
                AngleFlux[k] += w;
                AngleLuminous[k] += w * lumens;
                if (_color) { AX![k] += w * tx; AY![k] += w * ty; AZ![k] += w * tz; }
                _rayAng[k] = _rayAng.GetValueOrDefault(k) + w;
            }
        }
    }

    private void Deposit(int k, double w, double lumens, double tx, double ty, double tz)   // OP-81: the hit's factors
    {
        Flux[k] += w;
        Luminous[k] += w * lumens;
        if (_color) { X![k] += w * tx; Y![k] += w * ty; Z![k] += w * tz; }
        _rayPos[k] = _rayPos.GetValueOrDefault(k) + w;
    }

    /// <summary>Bilinear sharing among the pixel centres around (x, y); a share that would fall
    /// off the detector stays with the pixel inside ("with pixels, if any, adjacent to the edge").</summary>
    private void Interpolated(double x, double y, double w, double dx, double dy, double lumens, double tx, double ty, double tz)   // OP-81
    {
        var g = _g;
        double u = (x + g.HalfWidthX) / dx - 0.5, v = (y + g.HalfWidthY) / dy - 0.5;
        int i0 = (int)Math.Floor(u), j0 = (int)Math.Floor(v);
        double fu = u - i0, fv = v - j0;
        Span<(int, double)> xs = stackalloc (int, double)[2];
        Span<(int, double)> ys = stackalloc (int, double)[2];
        xs[0] = (Math.Clamp(i0, 0, g.PixelsX - 1), 1 - fu); xs[1] = (Math.Clamp(i0 + 1, 0, g.PixelsX - 1), fu);
        ys[0] = (Math.Clamp(j0, 0, g.PixelsY - 1), 1 - fv); ys[1] = (Math.Clamp(j0 + 1, 0, g.PixelsY - 1), fv);
        foreach (var (iy, wy) in ys)
            foreach (var (ix, wx) in xs)
                if (wx * wy > 0) Deposit(iy * g.PixelsX + ix, w * wx * wy, lumens, tx, ty, tz);
    }

    public override void EndRay()
    {
        if (_rayPos.Count > 0)
        {
            foreach (var (k, w) in _rayPos) Squares[k] += w * w;
            _rayPos.Clear();
        }
        if (_rayAng.Count > 0)
        {
            foreach (var (k, w) in _rayAng) AngleSquares[k] += w * w;
            _rayAng.Clear();
        }
    }

    public override void Add(NscDetectorAccumulator other)
    {
        var o = (FlatAccumulator)other;
        AddTo(Flux, o.Flux); AddTo(Squares, o.Squares); AddTo(AngleFlux, o.AngleFlux); AddTo(AngleSquares, o.AngleSquares);
        AddTo(Luminous, o.Luminous); AddTo(AngleLuminous, o.AngleLuminous);
        AddTo(Re, o.Re); AddTo(Im, o.Im); AddTo(Amp, o.Amp);
        AddTo(X, o.X); AddTo(Y, o.Y); AddTo(Z, o.Z); AddTo(AX, o.AX); AddTo(AY, o.AY); AddTo(AZ, o.AZ);
        AddTo(Spectrum, o.Spectrum);
        SpatialHits += o.SpatialHits; AngularHits += o.AngularHits;
    }

    internal static void AddTo(double[]? a, double[]? b)
    {
        if (a is null || b is null) return;
        for (int i = 0; i < a.Length; i++) a[i] += b[i];
    }

    public override NscDetectorData Build(long launchedRays) => new NscFlatDetectorData
    {
        ObjectNumber = _object, LaunchedRays = launchedRays, IsColor = _color,
        HalfWidthX = _g.HalfWidthX, HalfWidthY = _g.HalfWidthY, PixelsX = _g.PixelsX, PixelsY = _g.PixelsY,
        AngleXMin = _g.AngleXMin, AngleXMax = _g.AngleXMax, AngleYMin = _g.AngleYMin, AngleYMax = _g.AngleYMax,
        Flux = (double[])Flux.Clone(), FluxSquares = (double[])Squares.Clone(),
        AngleFlux = (double[])AngleFlux.Clone(), AngleFluxSquares = (double[])AngleSquares.Clone(),
        Luminous = (double[])Luminous.Clone(), AngleLuminous = (double[])AngleLuminous.Clone(),
        CoherentReal = (double[]?)Re?.Clone(), CoherentImaginary = (double[]?)Im?.Clone(), CoherentAmplitude = (double[]?)Amp?.Clone(),
        NormalizeCoherentPower = _g is NscDetectorRectangle r ? r.NormalizeCoherentPower : true,
        X = (double[]?)X?.Clone(), Y = (double[]?)Y?.Clone(), Z = (double[]?)Z?.Clone(),
        AngleX = (double[]?)AX?.Clone(), AngleY = (double[]?)AY?.Clone(), AngleZ = (double[]?)AZ?.Clone(),
        SpatialHits = SpatialHits, AngularHits = AngularHits,
        Spectrum = (double[]?)Spectrum?.Clone(),
        SpectrumMinMicrometers = _spectral?.Min ?? 0, SpectrumMaxMicrometers = _spectral?.Max ?? 0,
    };
}
