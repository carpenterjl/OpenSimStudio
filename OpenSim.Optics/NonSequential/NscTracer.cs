using System.Globalization;
using System.Numerics;
using OpenSim.Core.Geometry;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Polarization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.NonSequential;

/// <summary>
/// A ray handed to the non-sequential engine: start point and direction in the scene frame
/// (meters; the direction is normalised here), its power (source units, watts for the source
/// stand-in), the object it starts inside (OpticStudio's "Inside Of", 0 for the background) and,
/// optionally, its electric field (null: unpolarized).
/// </summary>
public sealed record NscLaunchRay
{
    public required Vector3D Position { get; init; }
    public required Vector3D Direction { get; init; }
    public double Intensity { get; init; } = 1;
    public int InsideOf { get; init; }
    public RayField? Field { get; init; }
}

/// <summary>
/// The non-sequential ray tracing core (plan item OP-70; docs/Optics-Conventions.md §69): rays
/// that find their own way through a <see cref="NscScene"/> at one wavelength.
/// <list type="bullet">
/// <item><b>First hit.</b> A <see cref="BoundingVolumeHierarchy"/> over the objects' scene-frame
/// boxes gives the candidates nearest first; each object intersects the ray exactly in its own
/// frame. Every hit within the glue distance of the nearest is part of one contact (touching
/// objects), and the highest-numbered object of the contact decides what happens there.</item>
/// <item><b>Media.</b> Each ray carries the set of refractive volumes it is inside; its medium is
/// the highest-numbered of them (the later object wins where objects overlap), or the
/// background. Crossing a face adds or removes the object by the sign of direction·normal; a ray
/// leaving an object it is not inside, or entering one it is already inside, is a trace error.</item>
/// <item><b>Faces.</b> ABSORB and absorbing faces absorb; MIRROR and reflective faces reflect back
/// into the medium the ray came from; a detecting stand-in absorbs and counts; a refractive
/// interface refracts, reflects on total internal reflection, and with energy accounted splits
/// by the Fresnel or coating coefficients of the polarization trace (OP-15, OP-51).</item>
/// <item><b>Polarization.</b> With energy accounted each ray carries its 3 × 3 coherency matrix Φ
/// (<see cref="CoherencyMatrix"/>), transformed by the interface matrices of
/// <see cref="PolarizationMatrix.Interface"/> for the reflected and the transmitted ray. An
/// unpolarized ray starts as the mean of two orthogonal states, which is exact for incoherent
/// light through any sequence of interfaces.</item>
/// <item><b>New origins</b> are offset off the surface by <see cref="RobustRayOrigin"/> (PBR
/// section 6.8.6), and intercepts closer than the glue distance to the previous one are ignored
/// (help, "Glue Distance In Lens Units").</item>
/// </list>
/// Immutable after construction and safe to share between threads.
/// </summary>
public sealed class NscTracer
{
    private readonly NscObject[] _objects;
    private readonly RigidTransform[] _global;
    private readonly BoundingVolumeHierarchy _bvh;
    private readonly double[] _index;     // refractive volumes: index; others NaN
    private readonly double[] _alpha;     // bulk absorption, 1/m (0 when energy is not accounted)
    private readonly Dictionary<(int Object, int Face), Coating> _coatings = new();
    private readonly CoatingCatalog _catalog;
    private readonly double _backgroundIndex;
    private readonly double _primaryMicrometers;

    public NscScene Scene { get; }
    public NscTraceSettings Settings { get; }

    /// <summary>The trace wavelength, µm in the system's air.</summary>
    public double AirMicrometers { get; }

    /// <summary>The vacuum wavelength, meters.</summary>
    public double VacuumWavelength { get; }

    /// <summary>
    /// The engine for <paramref name="scene"/> at <paramref name="airMicrometers"/> (µm in the
    /// system's air). Throws <see cref="ArgumentException"/> listing every problem of the scene
    /// and the settings, and <see cref="InvalidOperationException"/> naming the object when a
    /// material has no index at this wavelength or a face names a coating the catalog lacks.
    /// </summary>
    public NscTracer(NscScene scene, double airMicrometers, NscTraceSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        settings ??= new NscTraceSettings();
        var problems = scene.Validate().Concat(settings.Validate()).ToList();
        if (!(airMicrometers > 0) || !double.IsFinite(airMicrometers))
            problems.Add($"The wavelength must be positive (µm); it is {airMicrometers}.");
        if (problems.Count > 0)
            throw new ArgumentException("Non-sequential trace refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Scene = scene;
        Settings = settings;
        AirMicrometers = airMicrometers;
        VacuumWavelength = OpticsConventions.MicrometersToMeters(settings.Environment.VacuumMicrometers(airMicrometers));
        _catalog = settings.Coatings ?? CoatingCatalog.Empty;
        _primaryMicrometers = settings.PrimaryMicrometers > 0 ? settings.PrimaryMicrometers : airMicrometers;

        _objects = scene.Objects.ToArray();
        _global = scene.GlobalPlacements();
        int n = _objects.Length;
        _index = new double[n];
        _alpha = new double[n];
        var boxes = new Aabb[n];
        for (int i = 0; i < n; i++)
        {
            var o = _objects[i];
            string where = $"Object {i + 1} ({o.Geometry.TypeName})";
            boxes[i] = WorldBox(o.Geometry.LocalBounds, _global[i]);
            _index[i] = double.NaN;
            if (o.Geometry.IsVolume && o.Material.Kind == NscMaterialKind.Refractive)
            {
                var medium = o.Material.Medium ?? Paraxial.ParaxialMedium.Air;
                _index[i] = IndexOf(medium, where);
                if (settings.AccountsEnergy) _alpha[i] = AlphaOf(medium, where);
            }
            if (o.FaceProperties is { } faces)
                foreach (var (face, props) in faces)
                {
                    if (props.Coating is not { Length: > 0 } name) continue;
                    var coating = _catalog.FindCoating(name)
                        ?? throw new InvalidOperationException($"{where}, face {face}: the coating '{name}' is not in the coating catalog.");
                    if (coating.Validate(_catalog) is { Count: > 0 } bad)
                        throw new InvalidOperationException($"{where}, face {face}, coating '{name}': {string.Join(" ", bad)}");
                    _coatings[(i, face)] = coating;
                }
        }
        _backgroundIndex = IndexOf(scene.Background, "The background medium");
        if (settings.AccountsEnergy && AlphaOf(scene.Background, "The background medium") != 0)
            throw new NotSupportedException("The background medium absorbs; an absorbing background (rays that escape through it) is not built. Use a non-absorbing background and put the absorbing medium in an object.");
        _bvh = new BoundingVolumeHierarchy(boxes);
    }

    private double IndexOf(Paraxial.ParaxialMedium medium, string where)
    {
        double n;
        try { n = medium.IndexAt(AirMicrometers, Settings.Environment); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{where}: no index at {AirMicrometers.ToString("g9", CultureInfo.InvariantCulture)} µm: {e.Message}", e);
        }
        if (!(n > 0) || !double.IsFinite(n))
            throw new InvalidOperationException($"{where}: the index is {n} at {AirMicrometers} µm; it must be positive.");
        return n;
    }

    private double AlphaOf(Paraxial.ParaxialMedium medium, string where)
    {
        try { return BulkAbsorption.Coefficient(medium, AirMicrometers); }
        catch (InvalidOperationException e) { throw new InvalidOperationException($"{where}: {e.Message}", e); }
    }

    private static Aabb WorldBox(Aabb local, RigidTransform t)
    {
        var corners = new Vector3D[8];
        for (int c = 0; c < 8; c++)
            corners[c] = t.Apply(new Vector3D((c & 1) == 0 ? local.Min.X : local.Max.X,
                (c & 2) == 0 ? local.Min.Y : local.Max.Y, (c & 4) == 0 ? local.Min.Z : local.Max.Z));
        var box = Aabb.FromPoints(corners);
        // Widen by a few rounding units of the box's size so the rotation's rounding never
        // shrinks it below the true one (a missed box would be a missed object).
        double pad = BoundingVolumeHierarchy.Gamma(8) * (box.Max - box.Min).Length
            + BoundingVolumeHierarchy.Gamma(8) * (Math.Abs(t.Translation.X) + Math.Abs(t.Translation.Y) + Math.Abs(t.Translation.Z));
        return box.Expanded(pad);
    }

    /// <summary>The placement of object <paramref name="objectNumber"/> (1-based) in the scene frame.</summary>
    public RigidTransform GlobalPlacement(int objectNumber) => _global[objectNumber - 1];

    // ================================================================== public traces

    /// <summary>
    /// Traces one launched ray and returns every segment of its tree (OpticStudio's NSC Single
    /// Ray Trace). Trace errors never throw here: the segment is marked Z and the error is
    /// listed. A ray whose start contradicts its "Inside Of" throws
    /// <see cref="ArgumentException"/>, as that is a set-up error, not a trace error.
    /// </summary>
    public NscRayTree TraceSingle(NscLaunchRay ray, long rayNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(ray);
        var ledger = new NscLedgerBuilder();
        var segments = new List<NscSegment>();
        var errors = new List<string>();
        var work = new Workspace();
        TraceTree(ray, rayNumber, ledger, segments, errors, work);
        return new NscRayTree { Segments = segments, Ledger = ledger.Build(), Errors = errors };
    }

    /// <summary>Rays per work item of a Monte Carlo run: fixed, so the partition (and so every
    /// sum) does not depend on the thread count.</summary>
    public const int RaysPerChunk = 64;

    /// <summary>
    /// The Monte Carlo loop over caller-supplied rays: every ray's tree is traced and its energy
    /// entered in the ledger. Work is split into fixed chunks of <see cref="RaysPerChunk"/> rays,
    /// traced in parallel (<see cref="ParallelSlots"/>) and combined in chunk order, and ray k
    /// draws its random numbers from SplitMix64.ForStream(seed, k), so the result is the same to
    /// the bit at any thread count. With <see cref="NscTraceSettings.IgnoreErrors"/> off, the
    /// first trace error (by ray number) throws <see cref="InvalidOperationException"/> naming the
    /// ray, segment, object and cause.
    /// </summary>
    public NscRunResult Run(IReadOnlyList<NscLaunchRay> rays, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(rays);
        for (int i = 0; i < rays.Count; i++) CheckLaunch(rays[i], i);
        int chunks = (rays.Count + RaysPerChunk - 1) / RaysPerChunk;
        var parts = ParallelSlots.Compute(chunks, Settings.MaxDegreeOfParallelism, c =>
        {
            cancellation.ThrowIfCancellationRequested();
            var ledger = new NscLedgerBuilder();
            var errors = new List<string>();
            var work = new Workspace();
            int end = Math.Min(rays.Count, (c + 1) * RaysPerChunk);
            for (int i = c * RaysPerChunk; i < end; i++)
                TraceTree(rays[i], i, ledger, null, errors, work);
            return (ledger, errors);
        }, cancellation);
        var total = new NscLedgerBuilder();
        var allErrors = new List<string>();
        foreach (var (ledger, errors) in parts)
        {
            total.Add(ledger);
            foreach (var e in errors)
                if (allErrors.Count < 20) allErrors.Add(e);
        }
        if (!Settings.IgnoreErrors && allErrors.Count > 0)
            throw new InvalidOperationException(
                $"Non-sequential ray trace error (turn Ignore Errors on to count such rays as lost energy and go on): {allErrors[0]}");
        return new NscRunResult { Ledger = total.Build(), Errors = allErrors, Notes = Notes() };
    }

    /// <summary>What the trace modelled, for reports.</summary>
    public IReadOnlyList<string> Notes()
    {
        var s = Settings;
        var notes = new List<string>
        {
            $"Wavelength {AirMicrometers.ToString("g9", CultureInfo.InvariantCulture)} µm in the system's air ({(VacuumWavelength * 1e6).ToString("g9", CultureInfo.InvariantCulture)} µm in vacuum); indices relative to that air. {s.Environment.AirModelDescription}",
            s.AccountsEnergy
                ? "Energy accounted by the polarization ray trace: Fresnel or coating reflection and transmission at every refractive face, uncoated mirrors as aluminium 0.7 − 7.0i, bulk absorption from the glass data."
                    + (s.UsePolarization ? "" : " (Turned on because ray splitting needs it.)")
                : "Energy not accounted (Use Polarization off): interfaces neither gain nor lose energy, mirrors reflect everything, no bulk absorption.",
            s.Splitting switch
            {
                NscSplitting.Split => "Ray splitting: reflected and transmitted rays both traced.",
                NscSplitting.Simple => $"Simple ray splitting: reflected or transmitted ray chosen at random (seed {s.Seed}), carrying both parts' energy.",
                _ => "No ray splitting: the transmitted ray is traced (the reflected one on total internal reflection or at a mirror).",
            },
            $"Glue distance {s.GlueDistance.ToString("g6", CultureInfo.InvariantCulture)} m; maximum {s.MaxIntersectionsPerRay} intersections and {s.MaxSegmentsPerRay} segments per ray, {s.MaxNestedObjects} nested objects; minimum relative intensity {s.MinimumRelativeIntensity.ToString("g6", CultureInfo.InvariantCulture)}, absolute {s.MinimumAbsoluteIntensity.ToString("g6", CultureInfo.InvariantCulture)}.",
            "Not built: scattering (OP-80), volume physics (OP-81), diffraction and gradient index in objects (OP-82), sources (OP-77) and detectors (OP-79) beyond the OP-70 stand-ins, Consider/Ignore Objects lists.",
        };
        return notes;
    }

    // ================================================================== the tree

    private sealed class Pending
    {
        public Vector3D Origin, Direction;
        public double Intensity;
        public CoherencyMatrix? Phi;
        public int[] Inside = Array.Empty<int>();   // 0-based object indices, ascending
        public int Level, Parent;
        public double Optical;
        public bool Ghost, Launch;
    }

    private readonly record struct GlobalHit(int Object, double T, Vector3D LocalPoint, Vector3D Point, Vector3D Normal, int Face);

    private sealed class Workspace
    {
        public readonly List<NscLocalHit> Local = new();
        public readonly List<GlobalHit> Hits = new();
        public readonly List<GlobalHit> Group = new();
        public readonly Stack<Pending> Stack = new();
    }

    private struct HitVisitor : IBvhRayVisitor
    {
        public NscTracer Tracer;
        public Workspace Work;
        public Vector3D Origin, Direction;
        public double TMin, Best;

        public void Visit(int primitive, ref double tMax)
        {
            var t = Tracer._global[primitive];
            var lo = t.InverseApply(Origin);
            var ld = t.InverseApplyDirection(Direction);
            Work.Local.Clear();
            Tracer._objects[primitive].Geometry.Intersect(lo, ld, TMin, tMax, Work.Local);
            foreach (var h in Work.Local)
            {
                if (!double.IsFinite(h.T)) continue;
                Work.Hits.Add(new GlobalHit(primitive, h.T, h.Point, t.Apply(h.Point), t.ApplyDirection(h.Normal), h.Face));
                if (h.T < Best)
                {
                    Best = h.T;
                    tMax = Math.Min(tMax, Best + Tracer.Settings.GlueDistance);
                }
            }
        }
    }

    /// <summary>The 0-based objects the start point is inside, from its "Inside Of" chain, checked
    /// against the objects' inside tests.</summary>
    private int[] CheckLaunch(NscLaunchRay ray, long rayNumber)
    {
        if (!(ray.Intensity > 0) || !double.IsFinite(ray.Intensity))
            throw new ArgumentException($"Ray {rayNumber}: its intensity must be positive and finite (it is {ray.Intensity}).");
        double length = ray.Direction.Length;
        if (!(length > 0) || !double.IsFinite(length) || !double.IsFinite(ray.Position.X + ray.Position.Y + ray.Position.Z))
            throw new ArgumentException($"Ray {rayNumber}: its position and direction must be finite and the direction non-zero.");
        var chain = new List<int>();
        int k = ray.InsideOf;
        if (k < 0 || k > _objects.Length)
            throw new ArgumentException($"Ray {rayNumber}: \"Inside Of\" {k} is not an object number (1 to {_objects.Length}, or 0).");
        while (k > 0)
        {
            var o = _objects[k - 1];
            if (!o.Geometry.IsVolume || o.Material.Kind != NscMaterialKind.Refractive)
                throw new ArgumentException($"Ray {rayNumber}: it is said to start inside object {k} ({o.Geometry.TypeName}, {o.Material.Describe()}), which is not a refractive volume.");
            chain.Add(k - 1);
            k = NscScene.Resolve(o.InsideOf, k);
        }
        for (int i = 0; i < _objects.Length; i++)
        {
            var o = _objects[i];
            if (!o.Geometry.IsVolume || o.Material.Kind != NscMaterialKind.Refractive) continue;
            bool inside = o.Geometry.IsInside(_global[i].InverseApply(ray.Position));
            bool declared = chain.Contains(i);
            if (inside && !declared)
                throw new ArgumentException($"Ray {rayNumber} starts inside object {i + 1} ({o.Geometry.TypeName}), but its \"Inside Of\" chain does not name it; set Inside Of (help, \"Placing Sources Inside Objects\").");
            if (!inside && declared)
                throw new ArgumentException($"Ray {rayNumber} is said to start inside object {i + 1} ({o.Geometry.TypeName}), but its start point is not inside it.");
        }
        chain.Sort();
        return chain.ToArray();
    }

    private (double Index, double Alpha, int Object) MediumOf(int[] inside) =>
        inside.Length == 0 ? (_backgroundIndex, 0, 0) : (_index[inside[^1]], _alpha[inside[^1]], inside[^1] + 1);

    private void TraceTree(NscLaunchRay launch, long rayNumber, NscLedgerBuilder ledger, List<NscSegment>? segments,
        List<string> errors, Workspace work)
    {
        var s = Settings;
        bool energy = s.AccountsEnergy;
        var inside0 = CheckLaunch(launch, rayNumber);
        var d0 = launch.Direction / launch.Direction.Length;
        double i0 = launch.Intensity;
        double threshold = Math.Max(s.MinimumRelativeIntensity * i0, s.MinimumAbsoluteIntensity);
        ledger.Rays++;
        ledger.Emitted.Add(i0);
        SplitMix64? rng = null;

        int number = 0;
        segments?.Add(new NscSegment
        {
            Number = 0, Parent = -1, Level = 0, Inside = MediumOf(inside0).Object, Hit = 0, Face = 0,
            Position = launch.Position, Direction = d0, Intensity = i0, Index = MediumOf(inside0).Index,
            Fate = "source point",
        });
        var stack = work.Stack;
        stack.Clear();
        stack.Push(new Pending
        {
            Origin = launch.Position, Direction = d0, Intensity = i0, Inside = inside0, Level = 0, Parent = 0, Launch = true,
            Phi = energy ? LaunchCoherency(launch, d0) : null,
        });
        int traced = 0;

        while (stack.Count > 0)
        {
            var r = stack.Pop();
            double intensity = r.Intensity;
            if (!r.Launch && intensity < threshold)
            {
                ledger.Thresholded.Add(intensity);
                ledger.ThresholdedRays++;
                if (segments is not null) segments[r.Parent].Fate += " (a child ray below the intensity threshold was not traced)";
                continue;
            }
            if (traced >= s.MaxSegmentsPerRay)
            {
                ledger.LimitTerminated.Add(intensity);
                ledger.LimitTerminatedRays++;
                if (segments is not null) segments[r.Parent].Fate += " (a child ray was stopped at the segment limit)";
                continue;
            }
            traced++;
            ledger.Segments++;
            number++;
            var medium = MediumOf(r.Inside);

            // ---- first hit and the contact around it
            work.Hits.Clear();
            var visitor = new HitVisitor
            {
                Tracer = this, Work = work, Origin = r.Origin, Direction = r.Direction,
                TMin = r.Launch ? 0 : s.GlueDistance, Best = double.PositiveInfinity,
            };
            _bvh.Traverse(r.Origin, r.Direction, visitor.TMin, double.PositiveInfinity, ref visitor);
            if (work.Hits.Count == 0)
            {
                ledger.Escaped.Add(intensity);
                segments?.Add(new NscSegment
                {
                    Number = number, Parent = r.Parent, Level = r.Level + 1, Inside = medium.Object, Hit = 0, Face = 0,
                    Position = r.Origin, Direction = r.Direction, Intensity = intensity, Index = medium.Index,
                    OpticalPath = r.Optical, Events = NscEvents.Terminated | (r.Ghost ? NscEvents.Ghost : 0),
                    Fate = "escaped (no object hit); the position is the segment's start",
                });
                continue;
            }
            double best = visitor.Best;
            var group = work.Group;
            group.Clear();
            foreach (var h in work.Hits)
            {
                if (h.T > best + s.GlueDistance) continue;
                int existing = group.FindIndex(g => g.Object == h.Object);
                if (existing < 0) group.Add(h);
                else if (h.T < group[existing].T) group[existing] = h;
            }
            group.Sort((a, b) => a.Object.CompareTo(b.Object));

            // The deciding object: the highest-numbered one that is not an ignored surface.
            int decider = -1;
            for (int g = group.Count - 1; g >= 0; g--)
            {
                var o = _objects[group[g].Object];
                bool ignored = !o.Geometry.IsVolume && o.Material.Kind == NscMaterialKind.Refractive && !o.IsDetectingStandIn
                    && o.Face(group[g].Face).FaceIs == NscFaceBehaviour.ObjectDefault;
                if (!ignored) { decider = g; break; }
            }
            var hit = decider >= 0 ? group[decider] : group[^1];
            int obj = hit.Object;
            var objRecord = _objects[obj];
            double length = hit.T;

            // ---- bulk absorption over the segment
            CoherencyMatrix? phi = r.Phi;
            if (medium.Alpha > 0)
            {
                double f = Math.Exp(-medium.Alpha * length);
                ledger.Absorbed.Add(intensity * (1 - f));
                intensity *= f;
                phi = phi?.Scaled(f);
            }
            double optical = r.Optical + medium.Index * length;
            int level = r.Level + 1;
            NscSegment? seg = null;
            if (segments is not null)
            {
                seg = new NscSegment
                {
                    Number = number, Parent = r.Parent, Level = level, Inside = medium.Object, Hit = obj + 1, Face = hit.Face,
                    Position = hit.Point, Direction = r.Direction, Normal = hit.Normal, Intensity = intensity,
                    PathLength = length, OpticalPath = optical, Index = medium.Index,
                    Events = r.Ghost ? NscEvents.Ghost : NscEvents.None,
                };
                segments.Add(seg);
            }
            int segIndex = segments is null ? 0 : segments.Count - 1;
            string Where() => $"ray {rayNumber}, segment {number}, object {obj + 1} ({objRecord.Geometry.TypeName}) face {hit.Face} at {hit.Point}";

            void Error(string message)
            {
                ledger.Errors.Add(intensity);
                ledger.ErrorRays++;
                errors.Add($"{Where()}: {message}");
                if (seg is not null) { seg.Events |= NscEvents.Error | NscEvents.Terminated; seg.Fate = "error: " + message; }
            }

            if (!double.IsFinite(hit.Point.X + hit.Point.Y + hit.Point.Z) || !double.IsFinite(intensity))
            {
                Error("the intersection is not a finite number.");
                continue;
            }
            if (level >= s.MaxIntersectionsPerRay)
            {
                ledger.LimitTerminated.Add(intensity);
                ledger.LimitTerminatedRays++;
                if (seg is not null) { seg.Events |= NscEvents.Terminated; seg.Fate = $"stopped: {s.MaxIntersectionsPerRay} intersections reached"; }
                continue;
            }

            // ---- the media on the far side of the contact
            int[] after = r.Inside;
            string? nestingError = null;
            foreach (var h in group)
            {
                var o = _objects[h.Object];
                if (!o.Geometry.IsVolume || o.Material.Kind != NscMaterialKind.Refractive) continue;
                bool entering = Vector3D.Dot(r.Direction, h.Normal) < 0;
                bool contains = Array.IndexOf(after, h.Object) >= 0;
                if (entering && contains) { nestingError = $"the ray enters object {h.Object + 1}, which it is already inside (overlapping or badly nested objects?)."; break; }
                if (!entering && !contains) { nestingError = $"the ray leaves object {h.Object + 1}, which it is not inside (overlapping or badly nested objects, or a missing Inside Of?)."; break; }
                after = entering ? Insert(after, h.Object) : Remove(after, h.Object);
            }
            if (nestingError is null && after.Length > s.MaxNestedObjects)
                nestingError = $"the ray would be inside {after.Length} objects at once, more than the maximum nested/touching objects ({s.MaxNestedObjects}).";
            if (nestingError is not null) { Error(nestingError); continue; }

            var spawnScale = Math.Abs(hit.Point.X) + Math.Abs(hit.Point.Y) + Math.Abs(hit.Point.Z) + length;

            void Spawn(Vector3D direction, double power, CoherencyMatrix? state, int[] inside, bool ghost)
            {
                var origin = RobustRayOrigin.Offset(hit.Point, RobustRayOrigin.ErrorBox(spawnScale), hit.Normal, direction);
                stack.Push(new Pending
                {
                    Origin = origin, Direction = direction, Intensity = power, Phi = state, Inside = inside,
                    Level = level, Parent = segIndex, Optical = optical, Ghost = ghost,
                });
            }

            if (decider < 0)
            {
                // Only ignored surfaces here: pass straight on.
                if (seg is not null) { seg.Events |= NscEvents.Transmitted; seg.Fate = "passed an ignored surface object"; }
                Spawn(r.Direction, intensity, phi, after, false);
                continue;
            }
            var face = objRecord.Face(hit.Face);
            if (objRecord.IsDetectingStandIn)
            {
                ledger.Detect(obj + 1, intensity);
                if (seg is not null) { seg.Events |= NscEvents.Terminated; seg.Fate = "detected (absorbed by the detecting stand-in)"; }
                continue;
            }
            if (objRecord.Material.Kind == NscMaterialKind.Absorb || face.FaceIs == NscFaceBehaviour.Absorbing)
            {
                ledger.Absorbed.Add(intensity);
                if (seg is not null) { seg.Events |= NscEvents.Terminated; seg.Fate = "absorbed"; }
                continue;
            }
            bool mirror = objRecord.Material.Kind == NscMaterialKind.Mirror || face.FaceIs == NscFaceBehaviour.Reflective;
            var eta = hit.Normal;
            double cosI = Vector3D.Dot(r.Direction, eta);
            var reflected = Unit(r.Direction - eta * (2 * cosI));
            int[] before = r.Inside;
            if (mirror) after = before;   // "Rays will always reflect from mirror surfaces back into the medium the rays had been traveling through."
            double n1 = medium.Index, n2 = MediumOf(after).Index;
            _coatings.TryGetValue((obj, hit.Face), out var coating);

            if (!mirror && n1 == n2 && coating is null)
            {
                if (seg is not null) { seg.Events |= NscEvents.Transmitted; seg.Fate = Crossing(before, after) + " (same index: no interface)"; }
                Spawn(r.Direction, intensity, phi, after, false);
                continue;
            }
            Vector3D refracted = default;
            bool tir = !mirror && !ConicIntersection.Refract(r.Direction, eta, n1, n2, out refracted);

            if (!energy)
            {
                if (mirror || tir)
                {
                    if (seg is not null) { seg.Events |= NscEvents.Reflected | (tir ? NscEvents.TotalInternalReflection : 0); seg.Fate = tir ? "total internal reflection" : "reflected by a mirror"; }
                    Spawn(reflected, intensity, null, before, false);
                }
                else
                {
                    if (seg is not null) { seg.Events |= NscEvents.Transmitted; seg.Fate = "refracted: " + Crossing(before, after); }
                    Spawn(refracted, intensity, null, after, false);
                }
                continue;
            }

            // ---- energy accounted: coating or Fresnel response and the polarization matrices
            double angle = Math.Acos(Math.Min(1, Math.Abs(cosI))) * 180 / Math.PI;
            if (!(angle < 90))
            {
                Error("grazing incidence (90°): the reflection and transmission are not defined.");
                continue;
            }
            bool exitingDecider = Array.IndexOf(before, obj) >= 0 && Array.IndexOf(after, obj) < 0;
            var unit = s.LensUnit;
            var conditions = new CoatingConditions
            {
                Incident = n1,
                Substrate = mirror ? Complex.One : n2,
                IsMirror = mirror,
                ReverseLayers = !mirror && exitingDecider,
                Micrometers = AirMicrometers,
                PrimaryMicrometers = _primaryMicrometers,
                AngleDegrees = angle,
                X = OpticsConventions.FromMeters(hit.LocalPoint.X, unit),
                Y = OpticsConventions.FromMeters(hit.LocalPoint.Y, unit),
                SurfaceRadius = objRecord.Geometry.FaceRadius(hit.Face) is var fr && double.IsInfinity(fr) ? double.PositiveInfinity : OpticsConventions.FromMeters(fr, unit),
            };
            CoatingResponse response;
            try { response = CoatingEvaluator.Evaluate(_catalog, coating, conditions); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Error($"the {(coating is null ? "uncoated face" : $"coating '{coating.Name}'")} could not be evaluated: {e.Message}");
                continue;
            }
            var state = phi!;
            var (sHat, pIn, sOutR, pOutR) = Frames(r.Direction, reflected, eta);
            var matrixR = PolarizationMatrix.Interface(sHat, pIn, r.Direction, sOutR, pOutR, reflected,
                Amplitude(response.S.R, response.S.ReflectionPhase), Amplitude(response.P.R, response.P.ReflectionPhase));
            var phiR = state.Transformed(matrixR);
            double powerR = phiR.Power;
            CoherencyMatrix? phiT = null;
            double powerT = 0;
            if (!mirror && !tir)
            {
                var (sT, pInT, sOutT, pOutT) = Frames(r.Direction, refracted, eta);
                var matrixT = PolarizationMatrix.Interface(sT, pInT, r.Direction, sOutT, pOutT, refracted,
                    Amplitude(response.S.T, response.S.TransmissionPhase), Amplitude(response.P.T, response.P.TransmissionPhase));
                phiT = state.Transformed(matrixT);
                powerT = phiT.Power;
            }
            // Whatever is neither reflected nor transmitted is absorbed (coating, mirror, or the
            // rounding of a lossless interface).
            ledger.Absorbed.Add(intensity - powerR - powerT);
            string crossing = Crossing(before, after);
            if (mirror || tir)
            {
                if (seg is not null) { seg.Events |= NscEvents.Reflected | (tir ? NscEvents.TotalInternalReflection : 0); seg.Fate = tir ? "total internal reflection" : "reflected by a mirror"; }
                Spawn(reflected, powerR, phiR, before, false);
                continue;
            }
            switch (s.Splitting)
            {
                case NscSplitting.Split:
                    if (seg is not null) { seg.Events |= NscEvents.Reflected | NscEvents.Transmitted; seg.Fate = "split: refracted (" + crossing + ") and reflected"; }
                    Spawn(reflected, powerR, phiR, before, true);   // popped second
                    Spawn(refracted, powerT, phiT, after, false);   // popped first
                    break;
                case NscSplitting.Simple:
                {
                    double both = powerR + powerT;
                    if (!(both > 0))
                    {
                        if (seg is not null) { seg.Events |= NscEvents.Terminated; seg.Fate = "absorbed at the face"; }
                        break;
                    }
                    rng ??= SplitMix64.ForStream(s.Seed, rayNumber);
                    bool reflect = rng.NextDouble() * both < powerR;
                    if (reflect)
                    {
                        if (seg is not null) { seg.Events |= NscEvents.Reflected; seg.Fate = "reflected (simple ray splitting)"; }
                        Spawn(reflected, both, phiR.Scaled(both / powerR), before, true);
                    }
                    else
                    {
                        if (seg is not null) { seg.Events |= NscEvents.Transmitted; seg.Fate = "refracted (simple ray splitting): " + crossing; }
                        Spawn(refracted, both, phiT!.Scaled(both / powerT), after, false);
                    }
                    break;
                }
                default:
                    ledger.NotTraced.Add(powerR);
                    if (seg is not null) { seg.Events |= NscEvents.Transmitted; seg.Fate = "refracted: " + crossing; }
                    Spawn(refracted, powerT, phiT, after, false);
                    break;
            }
        }
    }

    private static string Crossing(int[] before, int[] after)
    {
        int b = before.Length == 0 ? 0 : before[^1] + 1, a = after.Length == 0 ? 0 : after[^1] + 1;
        string Name(int k) => k == 0 ? "the background" : $"object {k}";
        return $"from {Name(b)} into {Name(a)}";
    }

    private static int[] Insert(int[] set, int value)
    {
        var r = new int[set.Length + 1];
        int j = 0;
        bool placed = false;
        foreach (var v in set)
        {
            if (!placed && value < v) { r[j++] = value; placed = true; }
            r[j++] = v;
        }
        if (!placed) r[j] = value;
        return r;
    }

    private static int[] Remove(int[] set, int value)
    {
        var r = new int[set.Length - 1];
        int j = 0;
        foreach (var v in set) if (v != value) r[j++] = v;
        return r;
    }

    /// <summary>The launched ray's coherency matrix with power equal to its intensity: from its
    /// field (made transverse) when polarized, else the mean of two orthogonal transverse states.</summary>
    private static CoherencyMatrix LaunchCoherency(NscLaunchRay ray, Vector3D d)
    {
        if (ray.Field is { } e)
        {
            var along = e.Along(d);
            var transverse = new RayField(e.X - along * d.X, e.Y - along * d.Y, e.Z - along * d.Z);
            double power = transverse.NormSquared;
            if (!(power > 0)) throw new ArgumentException("The launch field has no part at right angles to the ray's direction.");
            return CoherencyMatrix.FromField(transverse).Scaled(ray.Intensity / power);
        }
        var a = Math.Abs(d.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitY;
        var u = Unit(a - d * Vector3D.Dot(a, d));
        var v = Vector3D.Cross(d, u);
        double amp = Math.Sqrt(ray.Intensity);
        return CoherencyMatrix.Average(
            CoherencyMatrix.FromField(new RayField(amp * u.X, amp * u.Y, amp * u.Z)),
            CoherencyMatrix.FromField(new RayField(amp * v.X, amp * v.Y, amp * v.Z)));
    }

    /// <summary>√(power)·e^(−i·phase) for an OpticStudio-convention phase (as the sequential
    /// polarization trace, <see cref="PolarizationTracer"/>).</summary>
    private static Complex Amplitude(double power, double opticStudioPhase) =>
        Complex.FromPolarCoordinates(Math.Sqrt(Math.Max(0, power)), -opticStudioPhase);

    /// <summary>ŝ, p̂_in, ŝ_out, p̂_out as the sequential polarization trace builds them
    /// (<c>PolarizationTracer.Frames</c>, OP-51): ŝ = k̂_in × η̂ normalised, any transverse unit
    /// vector at normal incidence, p̂ = k̂ × ŝ on each side.</summary>
    private static (Vector3D S, Vector3D PIn, Vector3D SOut, Vector3D POut) Frames(Vector3D kIn, Vector3D kOut, Vector3D eta)
    {
        var s = Vector3D.Cross(kIn, eta);
        if (s.Length < 1e-12)
        {
            var a = Math.Abs(kIn.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitY;
            s = a - kIn * Vector3D.Dot(a, kIn);
        }
        s = Unit(s);
        var pIn = Unit(Vector3D.Cross(kIn, s));
        var sOut = s;
        double along = Vector3D.Dot(s, kOut);
        if (Math.Abs(along) > 1e-12) sOut = Unit(s - kOut * along);
        var pOut = Unit(Vector3D.Cross(kOut, sOut));
        return (s, pIn, sOut, pOut);
    }

    private static Vector3D Unit(Vector3D v) => v / v.Length;
}
