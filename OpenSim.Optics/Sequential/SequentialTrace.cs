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
/// <b>Frames.</b> A launched ray is written in the global frame: object space's axes, origin at
/// surface 1's vertex before surface 1's own tilt and decenter (surface 1's local frame when it
/// has none). Each surface j has its own local frame, placed in the frame of surface j − 1 by
/// <see cref="SurfaceStep"/> (<see cref="SurfacePlacement"/>, OP-21): the previous surface's
/// "after" tilt, its thickness along z (negative after an odd number of mirrors), and this
/// surface's "before" tilt or coordinate break.
/// </para>
/// <para>
/// <b>Coordinate breaks</b> are dummy planes: the ray is moved to the break's plane (a virtual
/// step back when the plane lies behind it) with no change of direction or medium. When the
/// ray runs parallel to the plane (|N| &lt; 10⁻¹²) it stays where it is, and the whole straight segment is
/// counted at the next surface; the optical path is the same either way.
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
    private readonly SurfaceShape?[] _shape;   // OP-32: each surface's effective (composite) shape
    private readonly Birefringent.BirefringentSpaces? _birefringence;   // OP-53: null for a lens without birefringent surfaces

    /// <summary>A ray whose local z direction cosine is below this in magnitude is taken as
    /// parallel to a coordinate break's plane.</summary>
    internal const double ParallelToBreak = 1e-12;

    /// <summary>Where every surface sits (OP-21).</summary>
    public SurfacePlacement Placement { get; }

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
            if (j > 0 && (lens.Surfaces[j].IsMirror || lens.Surfaces[j].IsIgnored || lens.Surfaces[j].IsCoordinateBreak))
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

        Placement = SurfacePlacement.ComputeValidated(lens);
        _step = new RigidTransform[count];
        _global = new RigidTransform[count];
        for (int j = 0; j < count; j++)
        {
            _step[j] = Placement.Step(j);
            _global[j] = Placement.Global(j);
        }

        // OP-32: the shape each surface is traced with, its own unless active composite add-ons
        // add to it (Composite.CompositeSurfaces.EffectiveShape returns the surface's own
        // instance otherwise, so such a surface is traced exactly as before).
        _shape = new SurfaceShape?[count];
        for (int j = 0; j < count; j++)
            _shape[j] = Composite.CompositeSurfaces.EffectiveShape(lens.Surfaces[j]);

        _semiDiameter = new double[count];
        for (int j = 0; j < count; j++)
            _semiDiameter[j] = lens.Surfaces[j].SemiDiameter
                ?? (semiDiameters is not null && j < semiDiameters.Count ? semiDiameters[j] : double.NaN);

        // OP-53: the crystals between Birefringent In and Out surfaces (docs §62); null, and the
        // trace below exactly as before, for a lens that has none.
        _birefringence = Birefringent.BirefringentSpaces.Create(lens, _index, AirMicrometers, system.Environment, _global);
    }

    // OP-53
    /// <summary>The birefringent crystals of the lens at this wavelength, or null when it has no
    /// Birefringent In or Out surface (docs/Optics-Conventions.md §62).</summary>
    public Birefringent.BirefringentSpaces? Birefringence => _birefringence;

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
    /// Where surface <paramref name="j"/>'s local frame sits in surface j − 1's (for surface 1,
    /// in the global frame): previous surface's "after" tilt, its thickness, this surface's
    /// "before" tilt or coordinate break (<see cref="SurfacePlacement.Step"/>).
    /// </summary>
    public RigidTransform SurfaceStep(int j) => _step[j];

    /// <summary>The placement of surface <paramref name="j"/>'s local frame in the global frame
    /// (for the object surface at a finite distance, its plane at z = −distance).</summary>
    public RigidTransform GlobalPlacement(int j) => _global[j];

    /// <summary>A new ray of this trace's wavelength in the global frame.</summary>
    public Ray NewRay(Vector3D position, Vector3D direction) => new(position, direction, VacuumWavelength);

    /// <summary>Traces a ray given in the global frame to the image surface. The result is in
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

    /// <summary>Traces a ray given in the global frame and records every surface it meets.</summary>
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
        Vector3D? wave = null;   // OP-53: the wave vector n·k̂ inside a crystal (local frame); null in isotropic spaces
        for (int j = 1; j <= image; j++)
        {
            ray = ray.ToLocal(_step[j]);
            if (wave is { } w0 && !_step[j].IsIdentity) wave = _step[j].InverseApplyDirection(w0);   // OP-53
            var s = surfaces[j];
            var p = ray.Position;
            var d = ray.Direction;
            double nBefore = _index[j - 1], nAfter = _index[j];
            bool dummy = s.IsIgnored || s.IsCoordinateBreak;

            // Transfer to the vertex plane z = 0 (a negative distance is a virtual step back).
            // A coordinate break the ray runs parallel to (|N| below 1e-12, where the step to its
            // plane would be a long virtual detour that only loses digits) leaves the ray where it is.
            bool parallel = s.IsCoordinateBreak ? Math.Abs(d.Z) < ParallelToBreak : d.Z == 0;
            if (parallel && !s.IsCoordinateBreak) return ray.Failed(RayStatus.MissedSurface, j);
            double s0 = parallel ? 0 : -p.Z / d.Z;
            double x = p.X + s0 * d.X, y = p.Y + s0 * d.Y;
            double c = dummy ? 0 : s.Curvature;
            var shape = dummy ? null : _shape[j];   // OP-32: s.Shape unless composite add-ons add to it
            Vector3D point;
            double delta;
            bool met;
            if (parallel)
            {
                point = p;
                delta = 0;
                met = true;
            }
            else
            {
                met = shape is null
                    ? ConicIntersection.Intersect(x, y, d, c, s.Conic, out point, out delta)
                    : shape.Intersect(x, y, d, s, out point, out delta);
            }
            if (!met || !double.IsFinite(s0) || !double.IsFinite(delta))
                return ray.Failed(RayStatus.MissedSurface, j);
            double length = s0 + delta;
            // OP-53: in a crystal the optical path is the phase, N·Δr along the wave normal
            // (n·cos α per unit length of the ray); elsewhere n·length as always.
            double segment = wave is { } wn ? Vector3D.Dot(wn, d) * length : nBefore * length;
            path += segment;
            if (segments is not null) segments[j] = segment;
            ray = ray with { Position = point, OpticalPath = path };
            var normal = shape is null ? ConicIntersection.Normal(point, c, s.Conic) : shape.Normal(point, s);

            // Aperture.
            if (!options.IgnoreApertures && !dummy && s.Aperture is { } aperture
                && !aperture.Passes(point.X, point.Y, _semiDiameter[j]))
            {
                Record(hits, j, ray, d, d, normal, nBefore, nBefore, length, segment, RayStatus.Vignetted);
                return ray.Failed(RayStatus.Vignetted, j);
            }

            // Refraction or reflection.
            Vector3D after = d;
            if (_birefringence is not null && j < image && _birefringence.IsInterface(j))   // OP-53
            {
                if (!_birefringence.TryRefract(j, d, wave, nBefore, nAfter, normal, out after, out wave))
                {
                    Record(hits, j, ray, d, d, normal, nBefore, nAfter, length, segment, RayStatus.TotalInternalReflection);
                    return ray.Failed(RayStatus.TotalInternalReflection, j);
                }
            }
            else if (j < image && shape is not null && shape.TryRedirect(new SurfaceRedirectInput
                {
                    Surface = s, SurfaceIndex = j, Point = point, Incoming = d, Normal = normal,
                    IndexBefore = nBefore, IndexAfter = s.IsMirror ? nBefore : nAfter,
                    VacuumWavelength = VacuumWavelength,
                    AirWavelength = OpticsConventions.MicrometersToMeters(AirMicrometers),   // OP-26
                }, out var redirected, out double extra, out var redirectFailure))
            {
                if (redirectFailure != RayStatus.Ok)
                {
                    Record(hits, j, ray, d, d, normal, nBefore, nAfter, length, segment, redirectFailure);
                    return ray.Failed(redirectFailure, j);
                }
                after = redirected / redirected.Length;
                path += extra;
                segment += extra;
                if (segments is not null) segments[j] = segment;
                ray = ray with { OpticalPath = path };
            }
            else if (j < image && !dummy)
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
            Record(hits, j, ray, d, after, normal, nBefore, j < image ? nAfter : nBefore, length, segment, RayStatus.Ok, wave);
        }
        return ray;
    }

    private void Record(List<SurfaceHit>? hits, int j, Ray ray, Vector3D incoming, Vector3D after, Vector3D normal,
        double nBefore, double nAfter, double length, double segment, RayStatus status, Vector3D? wave = null)
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
            WaveNormal = wave,   // OP-53
        });
    }
}
