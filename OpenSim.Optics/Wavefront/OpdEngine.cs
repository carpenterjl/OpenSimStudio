using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Wavefront;

/// <summary>Where the reference sphere (or, in afocal image space, the reference plane) is
/// centred (OP-35).</summary>
public enum OpdReferencePoint
{
    /// <summary>The chief ray's intercept with the image surface (afocal: the chief ray's
    /// direction). The chief ray's OPD is zero.</summary>
    ChiefRay = 0,

    /// <summary>The centroid of the image-surface intercepts of a square grid of rays over the
    /// pupil (afocal: their mean direction). This takes out the wavefront tilt the centroid's
    /// offset from the chief ray stands for; the OPD is still measured against the chief ray, so
    /// the chief ray's OPD stays zero.</summary>
    Centroid = 1,
}

/// <summary>
/// OpticStudio's "Reference OPD" (System Explorer, Advanced Options; Ansys OpticStudio 2025 R2
/// help, pages "Reference OPD", "Exit Pupil", "Infinity", "Absolute/Absolute 2", saved under
/// <c>plans/sources/optics/op-35-help/</c>).
/// </summary>
public enum OpdReference
{
    /// <summary>The default: the reference sphere centred on the image point and passing through
    /// the paraxial exit pupil. With the exit pupil at infinity (telecentric image space) its
    /// limit, a plane through the image point normal to each ray. Afocal image space: a plane
    /// normal to the reference direction at the exit pupil.</summary>
    ExitPupil = 0,

    /// <summary>The correction is the angular term only: the ray is carried back to the plane
    /// through the image point normal to the ray ("the exit pupil is very far away"). Afocal
    /// image space: the same as <see cref="ExitPupil"/>, as the help says.</summary>
    Infinity = 1,

    /// <summary>No correction: the paths to the image surface. Afocal image space: the paths to
    /// the plane normal to the reference direction through the chief ray's image-surface
    /// intercept.</summary>
    Absolute = 2,

    /// <summary>No correction: the paths to the image surface, in focal and afocal mode alike.</summary>
    Absolute2 = 3,
}

/// <summary>What a ray's path is carried to before it is compared with the chief ray's.</summary>
public enum OpdReferenceShape
{
    /// <summary>A sphere of finite radius centred on the reference point.</summary>
    Sphere,
    /// <summary>The plane through the reference point normal to each ray: the sphere's limit as
    /// its radius goes to infinity.</summary>
    PlaneNormalToEachRay,
    /// <summary>One plane, normal to the reference direction (afocal image space).</summary>
    Plane,
    /// <summary>Nothing: the paths end on the image surface.</summary>
    ImageSurface,
}

/// <summary>Options of <see cref="OpdEngine.Compute"/>.</summary>
public sealed record OpdOptions
{
    public OpdReferencePoint ReferencePoint { get; init; } = OpdReferencePoint.ChiefRay;

    public OpdReference Reference { get; init; } = OpdReference.ExitPupil;

    /// <summary>Centre the reference sphere (or orient the afocal plane) on the primary
    /// wavelength's chief ray or centroid instead of the traced wavelength's: lateral colour then
    /// shows as tilt. The OPD is still against the traced wavelength's own chief ray, so it has no
    /// piston between wavelengths.</summary>
    public bool ReferenceToPrimary { get; init; }

    /// <summary>n: the centroid is taken over the rays of an n × n grid of pupil nodes from −1 to 1
    /// inside the unit circle. At least 3.</summary>
    public int CentroidSampling { get; init; } = 33;

    /// <summary>Trace through every aperture as if it were not there.</summary>
    public bool IgnoreApertures { get; init; }

    /// <summary>Compute even when a surface's shape says the OPD through it is approximate or
    /// undefined (<see cref="SurfaceShape.OpdLimitation"/>); the limitations are then listed in
    /// the result. Off, such a lens is refused with the reasons.</summary>
    public bool AllowOpdLimitations { get; init; }

    /// <summary>
    /// Focal mode, <see cref="OpdReference.ExitPupil"/> only: the sphere's radius in meters in
    /// place of the distance from the reference point to the paraxial exit pupil, or
    /// <see cref="double.PositiveInfinity"/> for the plane limit. The sheet of the sphere is the
    /// exit pupil's side of the reference point (the side the rays come from when the exit pupil
    /// is at infinity). Null for the exit pupil's own distance. For checks of the reference
    /// geometry (the OP-35 gate moves the sphere to infinity with it).
    /// </summary>
    public double? ReferenceRadius { get; init; }
}

/// <summary>The reference surface used, in the image surface's local frame.</summary>
public sealed record OpdReferenceGeometry
{
    public required OpdReferenceShape Shape { get; init; }

    /// <summary>The sphere's centre or the plane-per-ray's point: the chief ray's image-surface
    /// intercept, or the centroid (image surface's frame, meters). For <see cref="OpdReferenceShape.Plane"/>
    /// the point the plane passes through.</summary>
    public required Vector3D Point { get; init; }

    /// <summary>The sphere's radius (meters); infinity for the planes; NaN when none.</summary>
    public double Radius { get; init; } = double.NaN;

    /// <summary>+1 when the sphere's sheet lies ahead of its centre along the reference ray's
    /// direction, −1 when behind (the usual real exit pupil before the image).</summary>
    public int Sheet { get; init; } = -1;

    /// <summary>The centre of the paraxial exit pupil (image surface's frame), or null when it is
    /// at infinity or not used.</summary>
    public Vector3D? ExitPupil { get; init; }

    /// <summary>The plane's unit normal for <see cref="OpdReferenceShape.Plane"/>.</summary>
    public Vector3D? Normal { get; init; }

    /// <summary>One sentence naming the reference, for the result's assumptions.</summary>
    public required string Description { get; init; }
}

/// <summary>One pupil point's OPD.</summary>
/// <param name="Px">Normalised pupil x.</param>
/// <param name="Py">Normalised pupil y.</param>
/// <param name="Path">The OPD as an optical path, meters relative to the system's air (chief
/// path − ray path, docs/Optics-Conventions.md §6); NaN when the ray failed.</param>
/// <param name="Waves">The OPD in waves of the traced wavelength (in the system's air); NaN when
/// the ray failed.</param>
/// <param name="Ray">The ray at the image surface (its frame), or where it failed.</param>
/// <param name="Failure">Why there is no OPD (the ray failed, or missed the reference surface);
/// null when there is one.</param>
public readonly record struct OpdSample(double Px, double Py, double Path, double Waves, Ray Ray, string? Failure)
{
    public bool IsValid => Failure is null;
}

/// <summary>The OPD of one field and wavelength over a set of pupil points.</summary>
public sealed record OpdResult
{
    /// <summary>0-based field row.</summary>
    public required int Field { get; init; }
    /// <summary>0-based wavelength row.</summary>
    public required int Wavelength { get; init; }
    /// <summary>The table wavelength, µm in the system's air: one wave of OPD.</summary>
    public required double AirMicrometers { get; init; }
    /// <summary>The image-space index, relative to the system's air.</summary>
    public required double ImageIndex { get; init; }
    public required bool Afocal { get; init; }
    /// <summary>The traced wavelength's chief ray at the image surface (its frame).</summary>
    public required Ray ChiefRay { get; init; }
    public required OpdReferenceGeometry Geometry { get; init; }
    public required IReadOnlyList<OpdSample> Samples { get; init; }
    /// <summary>What the numbers assume, one sentence each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    /// <summary>The surfaces whose shape limits the OPD, one line each (empty normally).</summary>
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();

    public int FailedCount => Samples.Count(s => !s.IsValid);
}

/// <summary>
/// Optical path difference against the chief ray on a reference surface (OP-35,
/// docs/Optics-Conventions.md §6 and §26).
/// <para>
/// <b>Definition.</b> Each ray and the chief ray of the same field and wavelength are traced to
/// the image surface (<see cref="BatchTracer"/>, whose path difference is summed segment by
/// segment), then carried along their image-space lines, forward or back, to the reference
/// surface. OPD = chief path − ray path. This is OpticStudio's "OPD = OPD Absolute + OPD
/// Correction" (help page "Exit Pupil", 2025 R2), with OPD Absolute the difference to the image
/// surface and the correction the difference of the image-to-reference-sphere legs.
/// </para>
/// <para>
/// <b>The sphere.</b> Centre C (the reference point), radius R = |E − C| with E the centre of the
/// primary wavelength's paraxial exit pupil, the sheet on E's side of C. Writing a ray's image
/// intercept q and unit direction d, w = q − C, b = w·d and p = −|w − b·d|² (minus the squared
/// distance of C from the ray's line), the ray meets the sheet at signed distance
/// s = −b + σ·√(R² + p) (σ = ±1, the sheet), so with n the image-space index
/// <code>OPD = −ΔP + n·[(b_r − b_c) + σ·(p_c − p_r) / (√(R² + p_c) + √(R² + p_r))]</code>
/// where ΔP is the ray's path to the image surface minus the chief ray's. The second form of the
/// square-root difference keeps its digits when R is large. Welford's "Aberrations of Optical
/// Systems" was not on hand; this is derived from the definition above, not copied.
/// </para>
/// <para>
/// <b>Infinity.</b> As R → ∞ the bracket tends to b_r − b_c (the error is O(|w⊥|²/R)): each ray's
/// path to the plane through C normal to that ray. This is OpticStudio's "Infinity" reference
/// (help page "Infinity": the correction "is given strictly by the angular error in the ray"),
/// and it is what the exit-pupil reference becomes when the exit pupil is at infinity, so the
/// telecentric case is the continuous limit of the finite one.
/// </para>
/// <para>
/// <b>Afocal image space</b> (help page "Afocal Image Space": "the OPD is measured relative to a
/// reference plane that is normal to the chief ray of the reference wavelength", at the exit
/// pupil position for the exit-pupil reference, at the image surface for "Absolute"). A ray
/// meets the plane through Q with normal u at t = (Q − q)·u / (d·u), and
/// OPD = −ΔP + n·(t_c − t_r). Q is the point of the chief ray's line nearest the exit pupil
/// centre (exit pupil), or the chief ray's image intercept (Absolute).
/// </para>
/// </summary>
public static class OpdEngine
{
    /// <summary>
    /// The OPD at each of <paramref name="pupil"/>'s normalised pupil points for 0-based field row
    /// <paramref name="field"/> and wavelength row <paramref name="wavelength"/>. A failed ray is
    /// kept with its reason (<see cref="OpdSample.Failure"/>). Throws, naming the field and
    /// wavelength, when the chief ray fails, the reference cannot be built, or (unless allowed) a
    /// surface's shape limits the OPD.
    /// </summary>
    public static OpdResult Compute(SequentialLens lens, OpticalSystemData system, int field, int wavelength,
        IReadOnlyList<(double Px, double Py)> pupil, OpdOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(pupil);
        options ??= new OpdOptions();
        if (field < 0 || field >= system.Fields.Points.Count)
            throw new ArgumentOutOfRangeException(nameof(field), field, $"OPD: field {field + 1} is not one of the {system.Fields.Points.Count} fields.");
        if (wavelength < 0 || wavelength >= system.Wavelengths.Wavelengths.Count)
            throw new ArgumentOutOfRangeException(nameof(wavelength), wavelength, $"OPD: wavelength {wavelength + 1} is not one of the {system.Wavelengths.Wavelengths.Count} wavelengths.");
        if (options.CentroidSampling < 3)
            throw new ArgumentException($"OPD: the centroid sampling must be at least 3 (it is {options.CentroidSampling}).", nameof(options));
        if (options.ReferenceRadius is double rr && !(rr > 0))
            throw new ArgumentException($"OPD: a reference radius must be positive or infinite (it is {rr}).", nameof(options));

        string where = $"field {field + 1}, wavelength {wavelength + 1}";
        var limitations = Limitations(lens);
        if (limitations.Count > 0 && !options.AllowOpdLimitations)
            throw new InvalidOperationException(
                $"OPD ({where}) refused: the OPD through this lens is approximate or undefined:" + Environment.NewLine
                + string.Join(Environment.NewLine, limitations) + Environment.NewLine
                + "Turn on \"Allow OPD limitations\" to compute it anyway.");

        int image = lens.ImageIndex;
        int primary = system.Wavelengths.PrimaryIndex;
        int referenceWave = options.ReferenceToPrimary ? primary : wavelength;
        bool afocal = system.Aperture.AfocalImageSpace;
        var traceOptions = new TraceOptions { IgnoreApertures = options.IgnoreApertures };
        var notes = new List<string>();

        var trace = SequentialTrace.Create(lens, system, wavelength);
        double n = trace.Indices[image];
        var batch = BatchTracer.Trace(lens, system, new BatchTraceRequest
        {
            Fields = new[] { field }, Wavelengths = new[] { wavelength }, Pupil = pupil, Options = traceOptions,
        }, maxDegreeOfParallelism: 1, cancellation: cancellation);
        var chief = batch.ChiefRays[0];
        if (!chief.IsOk)
            throw new InvalidOperationException(
                $"OPD ({where}): the chief ray fails ({RayStatuses.Label(chief.Status)} at surface {chief.FailedAtSurface}), so there is nothing to measure against.");

        // ---- The reference point (and, afocal, the reference direction) ----
        bool needsPoint = afocal ? options.Reference != OpdReference.Absolute2
            : options.Reference is OpdReference.ExitPupil or OpdReference.Infinity;
        Ray referenceChief = chief;
        if (referenceWave != wavelength && needsPoint)
        {
            referenceChief = BatchTracer.Trace(lens, system, new BatchTraceRequest
            {
                Fields = new[] { field }, Wavelengths = new[] { referenceWave }, Pupil = new[] { (0.0, 0.0) }, Options = traceOptions,
            }, maxDegreeOfParallelism: 1, cancellation: cancellation).ChiefRays[0];
            if (!referenceChief.IsOk)
                throw new InvalidOperationException(
                    $"OPD ({where}): the primary wavelength's chief ray fails ({RayStatuses.Label(referenceChief.Status)} at surface {referenceChief.FailedAtSurface}), so the reference cannot be centred on it.");
        }
        Vector3D point = referenceChief.Position;
        Vector3D direction = referenceChief.Direction;
        string pointName = referenceWave == wavelength ? "the chief ray" : "the primary wavelength's chief ray";
        if (options.ReferencePoint == OpdReferencePoint.Centroid && needsPoint)
        {
            var grid = PupilNodes(options.CentroidSampling);
            var sample = BatchTracer.Trace(lens, system, new BatchTraceRequest
            {
                Fields = new[] { field }, Wavelengths = new[] { referenceWave }, Pupil = grid, Options = traceOptions,
            }, maxDegreeOfParallelism: 1, cancellation: cancellation);
            Vector3D sumP = Vector3D.Zero, sumD = Vector3D.Zero;
            int used = 0;
            foreach (var t in sample.Rays)
            {
                if (!t.Ray.IsOk) continue;
                sumP += t.Ray.Position;
                sumD += t.Ray.Direction;
                used++;
            }
            if (used == 0)
                throw new InvalidOperationException($"OPD ({where}): no ray of the {grid.Count}-ray centroid grid reaches the image surface.");
            point = sumP / used;
            direction = (sumD / used).Normalized();
            pointName = $"the centroid of {used} of {grid.Count} rays on a {options.CentroidSampling} × {options.CentroidSampling} pupil grid"
                + (referenceWave == wavelength ? "" : " at the primary wavelength");
        }
        else if (options.ReferencePoint == OpdReferencePoint.Centroid)
            notes.Add("The reference point plays no part with this OPD reference, so the centroid was not computed.");

        // ---- The exit pupil, in the image surface's frame ----
        Vector3D? exitPupil = null;
        var launcher = new RayLauncher(lens, system);
        var paraxial = launcher.Paraxial;
        if (!paraxial.ExitPupilAtInfinity && paraxial.ExitPupilPosition is double zx)
        {
            var global = trace.GlobalPlacement(lens.LastSurfaceIndex).Apply(new Vector3D(0, 0, zx));
            exitPupil = trace.GlobalPlacement(image).InverseApply(global);
        }

        // ---- The geometry ----
        OpdReferenceGeometry geometry;
        if (afocal)
        {
            if (options.ReferenceRadius is not null)
                throw new ArgumentException("OPD: a reference radius applies to focal image space only; afocal image space uses a reference plane.", nameof(options));
            var reference = options.Reference == OpdReference.Infinity ? OpdReference.ExitPupil : options.Reference;
            if (options.Reference == OpdReference.Infinity)
                notes.Add("Afocal image space: the Infinity reference is the same as the exit pupil reference (OpticStudio help, \"Infinity\").");
            if (reference == OpdReference.ExitPupil)
            {
                if (exitPupil is not Vector3D e)
                    throw new InvalidOperationException(
                        $"OPD ({where}): afocal image space with the exit pupil at infinity has no plane at the exit pupil; use the Absolute reference.");
                var q0 = referenceChief.Position;
                var d0 = referenceChief.Direction;
                var foot = q0 + d0 * Vector3D.Dot(e - q0, d0);
                geometry = new OpdReferenceGeometry
                {
                    Shape = OpdReferenceShape.Plane, Point = foot, Normal = direction, Radius = double.PositiveInfinity, ExitPupil = e,
                    Description = $"Afocal image space: the plane normal to the direction of {pointName}, through the point of that wavelength's chief ray nearest the paraxial exit pupil centre.",
                };
            }
            else if (reference == OpdReference.Absolute)
            {
                geometry = new OpdReferenceGeometry
                {
                    Shape = OpdReferenceShape.Plane, Point = referenceChief.Position, Normal = direction, Radius = double.PositiveInfinity, ExitPupil = exitPupil,
                    Description = $"Afocal image space, Absolute: the plane normal to the direction of {pointName}, through the chief ray's image-surface intercept.",
                };
            }
            else
            {
                geometry = new OpdReferenceGeometry
                {
                    Shape = OpdReferenceShape.ImageSurface, Point = chief.Position, ExitPupil = exitPupil,
                    Description = "Absolute 2: the paths to the image surface, with no correction.",
                };
            }
        }
        else if (options.Reference is OpdReference.Absolute or OpdReference.Absolute2)
        {
            if (options.ReferenceRadius is not null)
                throw new ArgumentException("OPD: a reference radius applies to the exit pupil reference only.", nameof(options));
            geometry = new OpdReferenceGeometry
            {
                Shape = OpdReferenceShape.ImageSurface, Point = chief.Position, ExitPupil = exitPupil,
                Description = "Absolute: the paths to the image surface, with no correction (OpticStudio help, \"Absolute/Absolute 2\").",
            };
        }
        else
        {
            double radius;
            int sheet;
            if (options.Reference == OpdReference.Infinity)
            {
                if (options.ReferenceRadius is not null)
                    throw new ArgumentException("OPD: a reference radius applies to the exit pupil reference only.", nameof(options));
                radius = double.PositiveInfinity;
                sheet = -1;
            }
            else
            {
                sheet = -1;
                if (exitPupil is Vector3D e)
                {
                    double along = Vector3D.Dot(e - point, direction);
                    if (along == 0)
                        throw new InvalidOperationException($"OPD ({where}): the paraxial exit pupil lies in the plane through the reference point normal to the chief ray, so the reference sphere's sheet is undefined.");
                    sheet = along > 0 ? 1 : -1;
                    radius = (e - point).Length;
                    if (!(radius > 0))
                        throw new InvalidOperationException($"OPD ({where}): the paraxial exit pupil lies on the reference point, so the reference sphere has no radius.");
                }
                else
                {
                    radius = double.PositiveInfinity;
                    notes.Add("The paraxial exit pupil is at infinity (telecentric image space): the reference sphere becomes its limit, the plane through the reference point normal to each ray.");
                }
                if (options.ReferenceRadius is double given) radius = given;
            }
            geometry = double.IsPositiveInfinity(radius)
                ? new OpdReferenceGeometry
                {
                    Shape = OpdReferenceShape.PlaneNormalToEachRay, Point = point, Radius = radius, Sheet = sheet, ExitPupil = exitPupil,
                    Description = $"Each ray is carried to the plane through {pointName}'s image point normal to the ray (the reference sphere's limit at infinite radius{(options.Reference == OpdReference.Infinity ? "; OpticStudio's Infinity reference" : "")}).",
                }
                : new OpdReferenceGeometry
                {
                    Shape = OpdReferenceShape.Sphere, Point = point, Radius = radius, Sheet = sheet, ExitPupil = exitPupil,
                    Description = options.ReferenceRadius is null
                        ? $"Reference sphere centred on {pointName}'s image-surface point, radius {radius:g6} m to the primary wavelength's paraxial exit pupil, on the {(sheet < 0 ? "incoming" : "far")} side."
                        : $"Reference sphere centred on {pointName}'s image-surface point, radius {radius:g6} m (given), on the {(sheet < 0 ? "incoming" : "far")} side.",
                };
        }
        // ---- Each ray ----
        var chiefTerm = Leg(geometry, chief.Position, chief.Direction, out string? chiefMiss);
        if (chiefMiss is not null)
            throw new InvalidOperationException($"OPD ({where}): the chief ray {chiefMiss}.");
        double wave = OpticsConventions.MicrometersToMeters(trace.AirMicrometers);
        var samples = new OpdSample[pupil.Count];
        for (int i = 0; i < pupil.Count; i++)
        {
            if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
            var (px, py) = pupil[i];
            var traced = batch.At(0, 0, i);
            var ray = traced.Ray;
            if (!ray.IsOk || !double.IsFinite(traced.PathFromChief))
            {
                samples[i] = new OpdSample(px, py, double.NaN, double.NaN, ray,
                    $"{RayStatuses.Label(ray.Status)} at surface {ray.FailedAtSurface}");
                continue;
            }
            var term = Leg(geometry, ray.Position, ray.Direction, out string? miss);
            if (miss is not null)
            {
                samples[i] = new OpdSample(px, py, double.NaN, double.NaN, ray, $"The ray {miss}");
                continue;
            }
            double path = -traced.PathFromChief + n * Difference(geometry, chiefTerm, term);
            samples[i] = new OpdSample(px, py, path, OpticsConventions.InWaves(path, wave), ray, null);
        }

        notes.Insert(0, geometry.Description);
        notes.Add("OPD = chief path − ray path (positive: the ray's wavefront is ahead), in waves of the traced wavelength in the system's air (docs/Optics-Conventions.md §6).");
        if (limitations.Count > 0) notes.Add("Computed although a surface limits the OPD; see the limitations.");
        return new OpdResult
        {
            Field = field, Wavelength = wavelength, AirMicrometers = trace.AirMicrometers, ImageIndex = n, Afocal = afocal,
            ChiefRay = chief, Geometry = geometry, Samples = samples, Notes = notes, Limitations = limitations,
        };
    }

    /// <summary>The surfaces whose shape says the OPD through it is approximate or undefined
    /// (<see cref="SurfaceShape.OpdLimitation"/>), one line each naming the surface.</summary>
    public static IReadOnlyList<string> Limitations(SequentialLens lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        var lines = new List<string>();
        for (int j = 1; j < lens.ImageIndex; j++)
        {
            var s = lens.Surfaces[j];
            if (s.IsIgnored || s.Shape is null) continue;
            if (s.Shape.OpdLimitation(s) is { } text) lines.Add($"Surface {j}: {text}");
        }
        return lines;
    }

    /// <summary>The per-ray quantities the correction needs: for a sphere (b, p, √(R² + p)); for
    /// the plane per ray b; for one plane t.</summary>
    private readonly record struct LegTerm(double B, double P, double Root, double T);

    private static LegTerm Leg(OpdReferenceGeometry g, Vector3D q, Vector3D d, out string? miss)
    {
        miss = null;
        switch (g.Shape)
        {
            case OpdReferenceShape.Sphere:
            {
                var w = q - g.Point;
                double b = Vector3D.Dot(w, d);
                double p = -(w - d * b).LengthSquared;
                double r2 = g.Radius * g.Radius + p;
                if (r2 < 0)
                {
                    miss = $"passes {Math.Sqrt(-p):g6} m from the reference point, outside the reference sphere of radius {g.Radius:g6} m";
                    return default;
                }
                return new LegTerm(b, p, Math.Sqrt(r2), 0);
            }
            case OpdReferenceShape.PlaneNormalToEachRay:
                return new LegTerm(Vector3D.Dot(q - g.Point, d), 0, 0, 0);
            case OpdReferenceShape.Plane:
            {
                var u = g.Normal!.Value;
                double du = Vector3D.Dot(d, u);
                if (!(du > 0))
                {
                    miss = "runs parallel to, or away from, the afocal reference plane's normal";
                    return default;
                }
                return new LegTerm(0, 0, 0, Vector3D.Dot(g.Point - q, u) / du);
            }
            default:
                return default;
        }
    }

    /// <summary>The correction (chief leg − ray leg) divided by the index.</summary>
    private static double Difference(OpdReferenceGeometry g, LegTerm c, LegTerm r) => g.Shape switch
    {
        OpdReferenceShape.Sphere => (r.B - c.B) + g.Sheet * (c.P - r.P) / (c.Root + r.Root),
        OpdReferenceShape.PlaneNormalToEachRay => r.B - c.B,
        OpdReferenceShape.Plane => c.T - r.T,
        _ => 0,
    };

    /// <summary>The nodes of an n × n grid over [−1, 1]² that lie inside the unit circle, row by
    /// row from Py = −1 (a private square grid until OP-34's shared pupil sampling lands).</summary>
    internal static IReadOnlyList<(double Px, double Py)> PupilNodes(int n)
    {
        var points = new List<(double, double)>(n * n);
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                var (px, py) = (Node(k, n), Node(i, n));
                if (px * px + py * py <= 1) points.Add((px, py));
            }
        return points;
    }

    /// <summary>Node k of n evenly spaced over [−1, 1], exact at both ends and at the centre of an
    /// odd count.</summary>
    internal static double Node(int k, int n) =>
        n == 1 ? 0 : 2 * k == n - 1 ? 0 : k == n - 1 ? 1 : -1 + 2.0 * k / (n - 1);
}
