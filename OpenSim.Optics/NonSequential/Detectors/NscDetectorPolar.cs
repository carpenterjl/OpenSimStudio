using OpenSim.Core.Numerics;
using OpenSim.Optics.Photometry;

namespace OpenSim.Optics.NonSequential.Detectors;

/// <summary>
/// OpticStudio's Detector Polar (help, "Detector Polar Object"): a part of a sphere of radius
/// "Radial Size" about the object's origin, from the local +z axis out to "Maximum Angle"
/// (1–180°), that records the flux and tristimulus values of the rays striking it binned by the
/// ray's direction ("The detector only considers the angle the ray striking the detector makes,
/// and not the position"). Pixel counts: 10–721 polar, 12–720 azimuthal and a multiple of 4.
/// <para>
/// OpticStudio draws and traces it as "a faceted approximation to a portion of a sphere"; here
/// the face is the exact sphere (docs/Optics-Conventions.md §73), which changes only where a ray
/// strikes, not its bin. A surface object: blank is transparent, MIRROR reflects, ABSORB absorbs.
/// The normal is the outward radial direction.
/// </para>
/// </summary>
public sealed class NscDetectorPolar : NscGeometry, INscDetector
{
    private static readonly IReadOnlyDictionary<int, string> FaceNames = new Dictionary<int, string> { [0] = "All Faces" };

    public double MaxAngle { get; }
    public double RadialSize { get; }
    public int PolarPixels { get; }
    public int AzimuthPixels { get; }
    public int Mirroring { get; }

    public override string TypeName => "Detector Polar";
    public override bool IsVolume => false;
    public override IReadOnlyDictionary<int, string> Faces => FaceNames;
    public override Aabb LocalBounds { get; }

    private readonly double _cosMax;

    public NscDetectorPolar(double maxAngleDegrees, double radialSize, int polarPixels, int azimuthPixels, int mirroring = 0)
    {
        var p = new List<string>();
        if (!(maxAngleDegrees >= 1 && maxAngleDegrees <= 180)) p.Add($"the maximum angle must be 1 to 180 degrees (help); it is {maxAngleDegrees}");
        if (!(radialSize > 0) || !double.IsFinite(radialSize)) p.Add($"the radial size must be positive (meters); it is {radialSize}");
        if (polarPixels < 10 || polarPixels > 721) p.Add($"# Radial Pixels must be 10 to 721 (help); it is {polarPixels}");
        if (azimuthPixels < 12 || azimuthPixels > 720 || azimuthPixels % 4 != 0) p.Add($"# Angular Pixels must be 12 to 720 and a multiple of 4 (help); it is {azimuthPixels}");
        if (mirroring < 0 || mirroring > 6) p.Add($"the mirroring flag must be 0 to 6; it is {mirroring}");
        if (p.Count > 0) throw new ArgumentException($"Detector Polar: {string.Join("; ", p)}.");
        MaxAngle = maxAngleDegrees; RadialSize = radialSize; PolarPixels = polarPixels; AzimuthPixels = azimuthPixels; Mirroring = mirroring;
        _cosMax = Math.Cos(maxAngleDegrees * Math.PI / 180);
        double r = radialSize, zMin = r * _cosMax;
        double side = maxAngleDegrees >= 90 ? r : r * Math.Sin(maxAngleDegrees * Math.PI / 180);
        LocalBounds = new Aabb(new Vector3D(-side, -side, Math.Min(zMin, r)), new Vector3D(side, side, r));
    }

    public override void Intersect(Vector3D origin, Vector3D direction, double tMin, double tMax, List<NscLocalHit> hits)
    {
        // |o + t·d|² = R²: t² + 2(o·d)t + (o·o − R²) = 0 (unit d).
        double b = Vector3D.Dot(origin, direction), c = Vector3D.Dot(origin, origin) - RadialSize * RadialSize;
        int n = Roots(1, b, c, out double t1, out double t2);
        if (n == 0) return;
        Try(t1);
        if (n == 2 && t2 != t1) Try(t2);

        void Try(double t)
        {
            if (!(t > tMin && t <= tMax)) return;
            var p = origin + direction * t;
            if (p.Z < RadialSize * _cosMax) return;   // outside the cap
            Add(hits, t, tMin, tMax, p, p / RadialSize, 0);
        }
    }

    public override bool IsInside(Vector3D point) => false;

    public override double FaceRadius(int face) => RadialSize;

    public override string Describe(Func<double, string> length) =>
        $"maximum angle {MaxAngle}°, radial size {length(RadialSize)}, {PolarPixels} × {AzimuthPixels} pixels";

    public NscDetectorAccumulator CreateAccumulator(int objectNumber, double airMicrometers) =>
        new PolarAccumulator(this, objectNumber, airMicrometers);

    /// <summary>An empty data record of this detector's layout (for the bin arithmetic).</summary>
    internal NscPolarDetectorData Layout(int objectNumber) => new()
    {
        ObjectNumber = objectNumber, LaunchedRays = 0, MaxAngle = MaxAngle, RadialSize = RadialSize,
        PolarPixels = PolarPixels, AzimuthPixels = AzimuthPixels,
        Flux = Array.Empty<double>(), FluxSquares = Array.Empty<double>(),
    };
}

internal sealed class PolarAccumulator : NscDetectorAccumulator
{
    private readonly NscDetectorPolar _g;
    private readonly NscPolarDetectorData _layout;
    private readonly int _object;
    private readonly double _lumens, _tx, _ty, _tz;
    private readonly double[] _flux, _squares, _lum, _x, _y, _z;
    private long _hits;
    private readonly Dictionary<int, double> _ray = new();

    public PolarAccumulator(NscDetectorPolar g, int objectNumber, double airMicrometers)
    {
        _g = g; _object = objectNumber; _layout = g.Layout(objectNumber);
        int n = g.PolarPixels * g.AzimuthPixels;
        _flux = new double[n]; _squares = new double[n]; _lum = new double[n]; _x = new double[n]; _y = new double[n]; _z = new double[n];
        _lumens = Photometric.LumensPerWatt(airMicrometers);
        (_tx, _ty, _tz) = Photometric.TristimulusPerWatt(airMicrometers);
    }

    public override void Record(in NscDetectorHit hit)
    {
        var d = hit.LocalDirection;
        double theta = Math.Acos(Math.Clamp(d.Z, -1, 1)) * 180 / Math.PI;
        int j = _layout.PolarIndex(theta);
        if (j < 0) return;   // a direction beyond the maximum angle is not binned
        _hits++;
        double phi = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
        // Mirroring: left-right is x → −x (φ → 180° − φ), top-bottom y → −y (φ → −φ).
        foreach (var (sx, sy, wf) in NscFlatDetector.MirrorCopies(_g.Mirroring))
        {
            double p = sx < 0 ? 180 - phi : phi;
            if (sy < 0) p = -p;
            int k = _layout.Index(j, _layout.AzimuthIndex(p));
            double w = hit.Power * wf;
            _flux[k] += w; _lum[k] += w * _lumens;
            _x[k] += w * _tx; _y[k] += w * _ty; _z[k] += w * _tz;
            _ray[k] = _ray.GetValueOrDefault(k) + w;
        }
    }

    public override void EndRay()
    {
        if (_ray.Count == 0) return;
        foreach (var (k, w) in _ray) _squares[k] += w * w;
        _ray.Clear();
    }

    public override void Add(NscDetectorAccumulator other)
    {
        var o = (PolarAccumulator)other;
        FlatAccumulator.AddTo(_flux, o._flux); FlatAccumulator.AddTo(_squares, o._squares); FlatAccumulator.AddTo(_lum, o._lum);
        FlatAccumulator.AddTo(_x, o._x); FlatAccumulator.AddTo(_y, o._y); FlatAccumulator.AddTo(_z, o._z);
        _hits += o._hits;
    }

    public override NscDetectorData Build(long launchedRays) => _layout with
    {
        LaunchedRays = launchedRays, Flux = (double[])_flux.Clone(), FluxSquares = (double[])_squares.Clone(),
        Luminous = (double[])_lum.Clone(), X = (double[])_x.Clone(), Y = (double[])_y.Clone(), Z = (double[])_z.Clone(), Hits = _hits,
    };
}
