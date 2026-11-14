using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential;

/// <summary>Options of one real ray trace.</summary>
public readonly record struct TraceOptions
{
    /// <summary>Trace through every aperture as if it were not there (automatic semi-diameter
    /// sizing does). Misses and total internal reflection are still flagged.</summary>
    public bool IgnoreApertures { get; init; }
}

/// <summary>
/// The real ray trace of a <see cref="SequentialLens"/> at one wavelength (OP-20): the
/// Spencer–Murty loop of transfer to the next vertex plane, closed-form conic intersection,
/// aperture test, and vector refraction or reflection, accumulating the optical path
/// (docs/Optics-Conventions.md §13). Built once per lens, system and wavelength by
/// <see cref="Create"/>; immutable and safe to share between threads.
/// <para>
/// <b>Frames.</b> A launched ray is written in surface 1's local frame (vertex at the origin,
/// +z the direction light starts in). Each surface j has its own local frame, placed in the
/// frame of surface j − 1 by <see cref="SurfaceStep"/>; today that is a translation by the
/// previous thickness along z (negative after an odd number of mirrors), and the coordinate
/// breaks and tilts of OP-21 replace it with their transform, with no change to the loop.
/// </para>
/// <para>
/// <b>Failures.</b> A ray that misses a surface, is totally internally reflected, or is
/// stopped by an aperture comes back with its <see cref="RayStatus"/> and the surface it failed
/// at; it is never dropped.
/// </para>
/// </summary>
public sealed class SequentialTrace
{
    private readonly double[] _index;
    private readonly RigidTransform[] _step;
    private readonly RigidTransform[] _global;
    private readonly double[] _semiDiameter;

    public SequentialLens Lens { get; }
    public OpticalSystemData System { get; }

    /// <summary>0-based row of the wavelength table.</summary>
    public int WavelengthIndex { get; }

    /// <summary>The table wavelength, µm in the system's air.</summary>
    public double AirMicrometers { get; }

    /// <summary>The vacuum wavelength every ray of this trace carries, meters.</summary>
    public double VacuumWavelength { get; }

    /// <summary>The index of the space after each surface, relative to the system's air
    /// (entry 0 is object space; the image surface repeats the last space).</summary>
    public IReadOnlyList<double> Indices => _index;

    /// <summary>The clear semi-diameter each floating aperture uses (NaN where none is known).</summary>
    public IReadOnlyList<double> SemiDiameters => _semiDiameter;

    private SequentialTrace(SequentialLens lens, OpticalSystemData system, int wavelengthIndex,
        IReadOnlyList<double>? semiDiameters)
    {
        Lens = lens;
        System = system;
        WavelengthIndex = wavelengthIndex;
        AirMicrometers = system.Wavelengths.Wavelengths[wavelengthIndex].Micrometers;
        VacuumWavelength = OpticsConventions.MicrometersToMeters(system.Environment.VacuumMicrometers(AirMicrometers));

        int count = lens.Surfaces.Count, image = lens.ImageIndex;
        var media = lens.EffectiveMedia();
        _index = new double[count];
        for (int j = 0; j < image; j++)
        {
            if (j > 0 && (lens.Surfaces[j].IsMirror || lens.Surfaces[j].IsIgnored))
            {
                _index[j] = _index[j - 1];
                continue;
            }
            double n = media[j].IndexAt(AirMicrometers, system.Environment);
            if (!double.IsFinite(n) || !(n > 0))
                throw new InvalidOperationException(
                    $"Real ray trace: the index of {(j == 0 ? "object space" : $"the space after surface {j}")} is {n} at {AirMicrometers} µm; it must be positive.");
            _index[j] = n;
        }
        _index[image] = _index[image - 1];

        _step = new RigidTransform[count];
        _global = new RigidTransform[count];
        _step[1] = RigidTransform.Identity;
        _global[1] = RigidTransform.Identity;
        for (int j = 2; j < count; j++)
        {
            _step[j] = SurfaceStep(j);
            _global[j] = RigidTransform.Compose(_global[j - 1], _step[j]);
        }
        _global[0] = lens.ObjectAtInfinity
            ? RigidTransform.Identity
            : RigidTransform.FromTranslation(new Vector3D(0, 0, -lens.Surfaces[0].Thickness));
        _step[0] = _global[0];

        _semiDiameter = new double[count];
        for (int j = 0; j < count; j++)
            _semiDiameter[j] = lens.Surfaces[j].SemiDiameter
                ?? (semiDiameters is not null && j < semiDiameters.Count ? semiDiameters[j] : double.NaN);
    }

    /// <summary>
    /// The trace of <paramref name="lens"/> at one row of the wavelength table (the primary
    /// when null). <paramref name="semiDiameters"/> gives the clear semi-diameters of surfaces
    /// whose own is automatic (from <see cref="SemiDiameterSizing"/>); floating apertures read
    /// them. Throws <see cref="ArgumentException"/> listing every problem of the lens and the
    /// system data.
    /// </summary>
    public static SequentialTrace Create(SequentialLens lens, OpticalSystemData system, int? wavelengthIndex = null,
        IReadOnlyList<double>? semiDiameters = null)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        var problems = lens.Validate().Concat(system.Validate()).ToList();
        if (problems.Count > 0)
            throw new ArgumentException("Real ray trace refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        int index = wavelengthIndex ?? system.Wavelengths.PrimaryIndex;
        if (index < 0 || index >= system.Wavelengths.Wavelengths.Count)
            throw new ArgumentOutOfRangeException(nameof(wavelengthIndex), index,
                $"There are {system.Wavelengths.Wavelengths.Count} wavelengths; the row must be one of them (0-based).");
        return new SequentialTrace(lens, system, index, semiDiameters);
    }

    /// <summary>
    /// Where surface <paramref name="j"/>'s local frame sits in surface j − 1's (j ≥ 2): the
    /// previous thickness along z. This is the one place OP-21's coordinate breaks and surface
    /// tilts and decenters will enter.
    /// </summary>
    private RigidTransform SurfaceStep(int j) =>
        RigidTransform.FromTranslation(new Vector3D(0, 0, Lens.Surfaces[j - 1].Thickness));

    /// <summary>The placement of surface <paramref name="j"/>'s local frame in surface 1's
    /// (for the object surface at a finite distance, its plane at z = −distance).</summary>
    public RigidTransform GlobalPlacement(int j) => _global[j];

    /// <summary>A new ray of this trace's wavelength in surface 1's frame.</summary>
    public Ray NewRay(Vector3D position, Vector3D direction) => new(position, direction, VacuumWavelength);

    /// <summary>Traces a ray given in surface 1's frame to the image surface. The result is in
    /// the image surface's frame, or in the frame of the surface it failed at.</summary>
    public Ray Trace(Ray launch, TraceOptions options = default) => Run(launch, options, null, null);

    /// <summary>As <see cref="Trace(Ray, TraceOptions)"/>, writing the optical path of each
    /// segment into <paramref name="segments"/> (entry j is the segment ending on surface j;
    /// entries after a failure are left as they were). It needs one entry per surface.</summary>
    public Ray Trace(Ray launch, double[] segments, TraceOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length < Lens.Surfaces.Count)
            throw new ArgumentException($"The segment buffer needs {Lens.Surfaces.Count} entries.", nameof(segments));
        return Run(launch, options, null, segments);
    }

    /// <summary>Traces a ray given in surface 1's frame and records every surface it meets.</summary>
    public RayPath TracePath(Ray launch, TraceOptions options = default)
    {
        var hits = new List<SurfaceHit>(Lens.Surfaces.Count);
        var final = Run(launch, options, hits, null);
        return new RayPath { Hits = hits, Final = final, ObjectAtInfinity = Lens.ObjectAtInfinity };
    }

    private Ray Run(Ray ray, TraceOptions options, List<SurfaceHit>? hits, double[]? segments)
    {
        if (!ray.IsOk) return ray;
        var surfaces = Lens.Surfaces;
        int image = Lens.ImageIndex;
        if (hits is not null)
        {
            var objectPoint = Lens.ObjectAtInfinity ? ray.Position : _global[0].InverseApply(ray.Position);
            hits.Add(new SurfaceHit
            {
                Surface = 0, Position = objectPoint, GlobalPosition = ray.Position,
                IncomingDirection = ray.Direction, Direction = ray.Direction, Normal = Vector3D.UnitZ,
                IndexBefore = _index[0], IndexAfter = _index[0], OpticalPath = ray.OpticalPath,
                AngleOfIncidenceDegrees = SurfaceHit.AngleDegrees(ray.Direction, Vector3D.UnitZ),
                AngleOfExitDegrees = SurfaceHit.AngleDegrees(ray.Direction, Vector3D.UnitZ),
            });
        }
        if (segments is not null) segments[0] = 0;
        double path = ray.OpticalPath;
        for (int j = 1; j <= image; j++)
        {
            if (j > 1) ray = ray.ToLocal(_step[j]);
            var s = surfaces[j];
            var p = ray.Position;
            var d = ray.Direction;
            double nBefore = _index[j - 1], nAfter = _index[j];

            // Transfer to the vertex plane z = 0 (a negative distance is a virtual step back).
            if (d.Z == 0) return ray.Failed(RayStatus.MissedSurface, j);
            double s0 = -p.Z / d.Z;
            double x = p.X + s0 * d.X, y = p.Y + s0 * d.Y;
            double c = s.IsIgnored ? 0 : s.Curvature;
            if (!ConicIntersection.Intersect(x, y, d, c, s.Conic, out var point, out double delta)
                || !double.IsFinite(s0))
                return ray.Failed(RayStatus.MissedSurface, j);
            double length = s0 + delta;
            double segment = nBefore * length;
            path += segment;
            if (segments is not null) segments[j] = segment;
            ray = ray with { Position = point, OpticalPath = path };
            var normal = ConicIntersection.Normal(point, c, s.Conic);

            // Aperture.
            if (!options.IgnoreApertures && !s.IsIgnored && s.Aperture is { } aperture
                && !aperture.Passes(point.X, point.Y, _semiDiameter[j]))
            {
                Record(hits, j, ray, d, d, normal, nBefore, nBefore, length, segment, RayStatus.Vignetted);
                return ray.Failed(RayStatus.Vignetted, j);
            }

            // Refraction or reflection.
            Vector3D after = d;
            if (j < image && !s.IsIgnored)
            {
                if (s.IsMirror)
                {
                    after = OpticsConventions.Reflect(d, normal);
                    after /= after.Length;
                }
                else if (!ConicIntersection.Refract(d, normal, nBefore, nAfter, out after))
                {
                    Record(hits, j, ray, d, d, normal, nBefore, nAfter, length, segment, RayStatus.TotalInternalReflection);
                    return ray.Failed(RayStatus.TotalInternalReflection, j);
                }
            }
            ray = ray with { Direction = after };
            Record(hits, j, ray, d, after, normal, nBefore, j < image ? nAfter : nBefore, length, segment, RayStatus.Ok);
        }
        return ray;
    }

    private void Record(List<SurfaceHit>? hits, int j, Ray ray, Vector3D incoming, Vector3D after, Vector3D normal,
        double nBefore, double nAfter, double length, double segment, RayStatus status)
    {
        if (hits is null) return;
        hits.Add(new SurfaceHit
        {
            Surface = j, Position = ray.Position, GlobalPosition = _global[j].Apply(ray.Position),
            IncomingDirection = incoming, Direction = after, Normal = normal,
            IndexBefore = nBefore, IndexAfter = nAfter,
            AngleOfIncidenceDegrees = SurfaceHit.AngleDegrees(incoming, normal),
            AngleOfExitDegrees = SurfaceHit.AngleDegrees(after, normal),
            SegmentLength = length, SegmentOpticalPath = segment, OpticalPath = ray.OpticalPath, Status = status,
        });
    }
}
