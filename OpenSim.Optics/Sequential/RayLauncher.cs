using System.Collections.Concurrent;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential;

/// <summary>A launched ray and how its aiming went: whether the aim converged, the distance at
/// the stop between where the ray lands and where it was aimed (meters; NaN with aiming off),
/// and, when it failed, why.</summary>
public readonly record struct AimedRay(Ray Ray, bool Converged, double StopMiss, string? Failure);

/// <summary>
/// Real rays from normalised field (Hx, Hy) and pupil (Px, Py) coordinates
/// (docs/Optics-Conventions.md §7, §13, §16).
/// <para>
/// <b>Field.</b> The field point f = H·F (<see cref="OpticsConventions.DenormalizeField"/>) is
/// turned into the object-space chief ray by the paraxial engine
/// (<see cref="ParaxialSystem.TraceChiefRay"/>), whose object-space slopes are the exact tangents
/// of an angle field and whose object heights are exact for a height field (a theodolite field
/// the direction (cos el·sin az, sin el, cos el·cos az)). A real image height is solved: the
/// paraxial-image-height field whose real chief ray (aimed as the system says, primary
/// wavelength) meets the image surface at the requested height, by damped Newton
/// (<see cref="DampedNewton2D"/>) with continuation in field when Newton from the paraxial guess
/// fails. <see cref="Paraxial"/> is then built with the field type read as paraxial image
/// height, the seed of that solve.
/// </para>
/// <para>
/// <b>Pupil.</b> The field's vignetting factors remap (Px, Py) first (<see cref="PupilVignetting"/>).
/// With ray aiming off the point (Px·r, Py·r) lies on the paraxial entrance pupil of the primary
/// wavelength (radius r, position z_E); an object at infinity launches the field's direction
/// through it from the plane through surface 1's vertex at right angles to it; a finite object
/// launches from the object point (x̄, ȳ, −t0) through it, or with the entrance pupil at infinity
/// along (Px·u, Py·u, 1), u the paraxial marginal slope. With ray aiming on, the same family of
/// rays is searched instead for the one that crosses the stop surface at (Px·R, Py·R) in the
/// stop's local frame, R the stop radius (<see cref="StopRadius"/>): the unknowns are the two
/// coordinates of the ray's crossing of the entrance-pupil plane (or its two slopes when that
/// plane is at infinity). The solve is <see cref="DampedNewton2D"/> from the chief ray's solution
/// shifted by the paraxial pupil offset; when that fails, continuation (the chief ray in field
/// from the axis, a pupil ray in pupil from the chief ray). Robust aiming always continues and
/// refuses a ray that reaches the stop along a virtual segment. A ray that cannot be aimed is
/// returned failed, <see cref="RayStatus.NoConvergence"/> at the stop.
/// </para>
/// <para>
/// <b>Aim reference</b> (TC-15). A lens with a <see cref="SequentialLens.AimReference"/> (a
/// toleranced trial made non-axial) takes <see cref="Paraxial"/> from the reference instead of
/// itself; everything else is traced on the lens.
/// </para>
/// <para>
/// Every solve is a pure function of its inputs, so the chief-ray cache is order independent and
/// a batch traced in parallel is bitwise the sequential one. Safe to share between threads.
/// </para>
/// </summary>
public sealed class RayLauncher
{
    /// <summary>Target stop miss, relative to the stop radius (or, for a real image height, to
    /// the field scale).</summary>
    public const double AimTolerance = 1e-13;

    /// <summary>A stop miss still accepted, relative, when Newton stalls at rounding.</summary>
    public const double AimAcceptTolerance = 1e-11;

    private readonly SequentialLens _lens;
    private readonly OpticalSystemData _system;
    private readonly RayAiming _aiming;
    private readonly PupilVignetting _vignetting;
    private readonly int _stop;
    private readonly bool _realImageHeight;
    private readonly SequentialTrace? _primary;
    private readonly int[] _expectedSign;
    private readonly double _parameterScale;
    private readonly List<string> _notes = new();
    private readonly ConcurrentDictionary<(long W, long X, long Y), ChiefAim> _chiefs = new();
    private readonly ConcurrentDictionary<(long X, long Y), (double X, double Y)> _imageFields = new();

    private static readonly TraceOptions Unapertured = new() { IgnoreApertures = true };

    /// <summary>The paraxial model at the primary wavelength that defines the pupil and fields
    /// (for a real-image-height system, with the fields read as paraxial image heights).</summary>
    public ParaxialSystem Paraxial { get; }

    /// <summary>The system data as given.</summary>
    public OpticalSystemData System => _system;

    public RayAimingMode Aiming => _aiming.Mode;

    /// <summary>The stop radius rays are aimed at, meters; NaN with ray aiming off.</summary>
    public double StopRadius { get; } = double.NaN;

    /// <summary>What the launcher had to decide that the user should know (a real stop radius
    /// that could not be traced, say).</summary>
    public IReadOnlyList<string> Notes => _notes;

    public RayLauncher(SequentialLens lens, OpticalSystemData system)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        _lens = lens;
        _system = system;
        var problems = lens.Validate();
        if (problems.Count > 0)
            throw new ArgumentException("Real ray trace refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        _aiming = system.RayAiming ?? new RayAiming();
        _vignetting = new PupilVignetting(system.Fields);
        _stop = lens.StopIndex;
        _realImageHeight = system.Fields.Type == FieldType.RealImageHeight;
        var paraxialSystem = _realImageHeight
            ? system with { Fields = system.Fields with { Type = FieldType.ParaxialImageHeight } }
            : system;
        // TC-15: a lens with an aim reference (a toleranced trial made non-axial by its decenters
        // and tilts) takes its pupil, fields and paraxial aim from the reference's paraxial model.
        Paraxial = ParaxialSystem.Create(AimSource(lens).ToParaxialLens(), paraxialSystem);
        _parameterScale = Paraxial.EntrancePupilAtInfinity
            ? Math.Abs(Paraxial.MarginalRay.Slopes[0])
            : Paraxial.EntrancePupilDiameter!.Value / 2;

        // The sign a real segment's length has from surface j − 1 to j (thickness times the
        // sense of travel along the local z), for robust aiming's virtual-path check.
        _expectedSign = new int[lens.Surfaces.Count];
        int travel = 1;
        for (int j = 1; j < lens.Surfaces.Count; j++)
        {
            if (j >= 2) _expectedSign[j] = Math.Sign(lens.Surfaces[j - 1].Thickness) * travel;
            if (lens.Surfaces[j].IsMirror && !lens.Surfaces[j].IsIgnored) travel = -travel;
        }

        if (_aiming.IsOn || _realImageHeight) _primary = SequentialTrace.Create(lens, system);
        if (_aiming.IsOn) StopRadius = FindStopRadius();
    }

    /// <summary>TC-15: the lens whose paraxial model defines the pupil and fields of
    /// <paramref name="lens"/>: its <see cref="SequentialLens.AimReference"/> when it has one (checked
    /// to have the same surfaces, stop and object distance), else the lens itself.</summary>
    internal static SequentialLens AimSource(SequentialLens lens)
    {
        if (lens.AimReference is not { } r) return lens;
        if (r.Surfaces.Count != lens.Surfaces.Count || r.StopIndex != lens.StopIndex
            || BitConverter.DoubleToInt64Bits(r.Surfaces[0].Thickness) != BitConverter.DoubleToInt64Bits(lens.Surfaces[0].Thickness))
            throw new ArgumentException(
                $"Real ray trace: the lens's aim reference has {r.Surfaces.Count} surfaces, stop {r.StopIndex} and object distance {r.Surfaces[0].Thickness:R} m, "
                + $"but the lens has {lens.Surfaces.Count}, stop {lens.StopIndex} and {lens.Surfaces[0].Thickness:R} m; the reference must be the same lens surface for surface.");
        return r;
    }

    /// <summary>The ray of normalised field (hx, hy) and pupil (px, py), in surface 1's frame,
    /// carrying the vacuum wavelength of <paramref name="trace"/>. A ray that cannot be aimed
    /// comes back failed (<see cref="RayStatus.NoConvergence"/> at the stop).</summary>
    public Ray Launch(SequentialTrace trace, double hx, double hy, double px, double py) =>
        LaunchAimed(trace, hx, hy, px, py).Ray;

    /// <summary>As <see cref="Launch"/>, with how the aiming went.</summary>
    public AimedRay LaunchAimed(SequentialTrace trace, double hx, double hy, double px, double py)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (!ReferenceEquals(trace.Lens, _lens) && !trace.Lens.Equals(_lens))
            throw new ArgumentException("The trace is of another lens than this launcher's.", nameof(trace));
        (px, py) = PupilVignetting.Apply(_vignetting.At(hx, hy), px, py);
        var (gx, gy) = ParaxialField(hx, hy);
        var field = FieldOf(gx, gy);
        if (!_aiming.IsOn)
        {
            var (a1, a2) = OffAim(field, px, py);
            if (!TryRay(trace, field, a1, a2, out var ray))
                throw new InvalidOperationException("Real ray trace: the object lies in the entrance-pupil plane, so the ray has no direction; move the object or the stop.");
            return new AimedRay(ray, true, double.NaN, null);
        }
        return Aim(trace, gx, gy, field, px, py);
    }

    /// <summary>The pupil point (P″x, P″y) the field's vignetting factors make of (px, py).</summary>
    public (double Px, double Py) VignettedPupil(double hx, double hy, double px, double py) =>
        PupilVignetting.Apply(_vignetting.At(hx, hy), px, py);

    /// <summary>The vignetting factors applied at normalised field (hx, hy).</summary>
    public VignettingFactors VignettingAt(double hx, double hy) => _vignetting.At(hx, hy);

    /// <summary>
    /// The field point in the paraxial engine's field type: f = H·F, or for a real image height
    /// the paraxial image height whose real chief ray (primary wavelength) meets the image
    /// surface at H·F. Throws, naming the field, when no chief ray can be found.
    /// </summary>
    public (double X, double Y) ParaxialField(double hx, double hy)
    {
        var target = OpticsConventions.DenormalizeField((hx, hy), _system.Fields.Scale);
        if (!_realImageHeight) return target;
        return _imageFields.GetOrAdd((Bits(hx), Bits(hy)), _ => SolveImageField(target));
    }

    // ---- Field geometry and the ray family ----

    /// <summary>The object side of a field: the direction (object at infinity) or the object point.</summary>
    private readonly record struct Field(Vector3D Direction, Vector3D ObjectPoint);

    private Field FieldOf(double gx, double gy)
    {
        var (chiefX, chiefY) = Paraxial.TraceChiefRay(new FieldPoint { X = gx, Y = gy });
        if (_lens.ObjectAtInfinity)
        {
            var direction = new Vector3D(chiefX.Slopes[0], chiefY.Slopes[0], 1);
            direction /= direction.Length;
            return new Field(direction, default);
        }
        double t0 = _lens.Surfaces[0].Thickness;
        return new Field(default, new Vector3D(chiefX.Heights[0], chiefY.Heights[0], -t0));
    }

    /// <summary>The ray-aiming-off parameters of pupil point (px, py): the crossing of the
    /// paraxial entrance-pupil plane (Px·r, Py·r), or the slopes (Px·u, Py·u) when it is at infinity.</summary>
    private (double A1, double A2) OffAim(Field field, double px, double py)
    {
        if (!_lens.ObjectAtInfinity && Paraxial.EntrancePupilAtInfinity)
        {
            double u = Math.Abs(Paraxial.MarginalRay.Slopes[0]);
            return (px * u, py * u);
        }
        double r = Paraxial.EntrancePupilDiameter!.Value / 2;
        return (px * r, py * r);
    }

    /// <summary>The first guess with the pupil shift and compression of the ray-aiming settings
    /// (docs/Optics-Conventions.md §16): the aim point (Px(1 − cx)·r + sx·k, Py(1 − cy)·r + sy·k,
    /// z_E + sz·|H|), k = |H| when the shifts scale with field and 1 otherwise, carried along the
    /// field's ray family to the entrance-pupil plane.</summary>
    private (double A1, double A2) AssistedAim(Field field, double hx, double hy, double px, double py)
    {
        var a = _aiming;
        double cpx = px * (1 - a.PupilCompressX), cpy = py * (1 - a.PupilCompressY);
        if (!_lens.ObjectAtInfinity && Paraxial.EntrancePupilAtInfinity) return OffAim(field, cpx, cpy);
        var (x, y) = OffAim(field, cpx, cpy);
        double h = Math.Sqrt(hx * hx + hy * hy);
        double k = a.ScalePupilShiftByField ? h : 1;
        x += a.PupilShiftX * k;
        y += a.PupilShiftY * k;
        double dz = a.PupilShiftZ * h;
        if (dz == 0) return (x, y);
        double zE = Paraxial.EntrancePupilPosition!.Value;
        if (_lens.ObjectAtInfinity)
        {
            var d = field.Direction;
            return (x - d.X / d.Z * dz, y - d.Y / d.Z * dz);
        }
        var o = field.ObjectPoint;
        double along = zE + dz - o.Z;
        if (along == 0) return (x, y);
        double s = (zE - o.Z) / along;
        return (o.X + (x - o.X) * s, o.Y + (y - o.Y) * s);
    }

    /// <summary>The ray of parameters (a1, a2) in the field's family; false when it has no direction.</summary>
    private bool TryRay(SequentialTrace trace, Field field, double a1, double a2, out Ray ray)
    {
        if (_lens.ObjectAtInfinity)
        {
            var direction = field.Direction;
            var pupil = new Vector3D(a1, a2, Paraxial.EntrancePupilPosition!.Value);
            var start = pupil - direction * Vector3D.Dot(pupil, direction);
            ray = trace.NewRay(start, direction);
            return true;
        }
        var objectPoint = field.ObjectPoint;
        if (Paraxial.EntrancePupilAtInfinity)
        {
            ray = trace.NewRay(objectPoint, new Vector3D(a1, a2, 1));
            return true;
        }
        var toPupil = new Vector3D(a1, a2, Paraxial.EntrancePupilPosition!.Value) - objectPoint;
        if (toPupil.Z == 0 || !double.IsFinite(toPupil.Length))
        {
            ray = default;
            return false;
        }
        // Light travels +z in object space; a virtual object (negative distance) lies beyond the pupil.
        if (toPupil.Z < 0) toPupil = -toPupil;
        ray = trace.NewRay(objectPoint, toPupil);
        return true;
    }

    // ---- The stop radius ----

    private double FindStopRadius()
    {
        var stopSurface = _lens.Surfaces[_stop];
        if (_system.Aperture.Type == ApertureType.FloatByStopSize && stopSurface.SemiDiameter is double given)
            return given;
        double paraxial = Math.Abs(Paraxial.MarginalRay.Heights[_stop]);
        double radius = paraxial;
        if (_aiming.Mode == RayAimingMode.Real)
        {
            // The real marginal ray: from the centre of the object (field 0) through the rim of
            // the paraxial entrance pupil, which for each aperture type is the ray the type
            // defines (an exact cone angle or NA, or the pupil diameter).
            var field = FieldOf(0, 0);
            var (a1, a2) = OffAim(field, 0, 1);
            if (TryRay(_primary!, field, a1, a2, out var marginal)
                && LandOnStop(_primary!, marginal, out var p, out _))
                radius = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            else
                _notes.Add($"Real ray aiming: the real marginal ray does not reach the stop (surface {_stop}), so the paraxial stop radius {paraxial:g8} m is used, as OpticStudio does; set ray aiming to paraxial to silence this note.");
        }
        if (!(radius > 0) || !double.IsFinite(radius))
            throw new InvalidOperationException($"Ray aiming: the stop (surface {_stop}) has radius {radius}; the object is imaged on the stop, so no ray can be aimed at its rim. Move the stop or turn ray aiming off.");
        return radius;
    }

    /// <summary>Traces to the stop with apertures ignored; false when the ray does not reach it.</summary>
    private bool LandOnStop(SequentialTrace trace, Ray ray, out Vector3D point, out RayPath path)
    {
        path = trace.TracePath(ray, Unapertured);
        var hits = path.Hits;
        if (hits.Count > _stop && hits[_stop].Surface == _stop)
        {
            point = hits[_stop].Position;
            return true;
        }
        point = default;
        return false;
    }

    // ---- Aiming ----

    private sealed record ChiefAim(bool Ok, double A1, double A2, string? Failure);

    private AimedRay Aim(SequentialTrace trace, double gx, double gy, Field field, double px, double py)
    {
        var chief = Chief(trace, gx, gy);
        var (s1, s2) = OffAim(field, 0, 0);
        TryRay(trace, field, s1, s2, out var unaimed);
        if (!chief.Ok)
            return new AimedRay(unaimed.Failed(RayStatus.NoConvergence, _stop), false, double.NaN, "the chief ray: " + chief.Failure);

        double tx = px * StopRadius, ty = py * StopRadius;
        Newton2DResult result;
        if (px == 0 && py == 0)
            result = new Newton2DResult(true, chief.A1, chief.A2, 0, 0, null);
        else
        {
            result = default;
            bool done = false;
            if (!_aiming.Robust)
            {
                var (o1, o2) = AssistedAim(field, 0, 0, px, py);
                var (z1, z2) = AssistedAim(field, 0, 0, 0, 0);
                result = SolveStop(trace, field, tx, ty, chief.A1 + (o1 - z1), chief.A2 + (o2 - z2));
                done = result.Converged;
            }
            if (!done)
            {
                Newton2DResult At(double t, double s1, double s2) => SolveStop(trace, field, t * tx, t * ty, s1, s2);
                if (!Continue((chief.A1, chief.A2), At, _aiming.Robust ? 8 : 4, !_aiming.Robust, out result))
                    return new AimedRay(unaimed.Failed(RayStatus.NoConvergence, _stop), false, result.ResidualNorm,
                        $"pupil ({px:g6}, {py:g6}): {result.Failure ?? "continuation from the chief ray did not reach the pupil point"}");
            }
        }
        TryRay(trace, field, result.X, result.Y, out var ray);
        if (_aiming.Robust && !RealPathToStop(trace, ray, out string? why))
            return new AimedRay(ray.Failed(RayStatus.NoConvergence, _stop), false, result.ResidualNorm, why);
        return new AimedRay(ray, true, result.ResidualNorm, null);
    }

    /// <summary>The chief ray's aim at paraxial field (gx, gy) for this trace's wavelength.</summary>
    private ChiefAim Chief(SequentialTrace trace, double gx, double gy)
    {
        var key = (Bits(trace.VacuumWavelength), Bits(gx), Bits(gy));
        return _chiefs.GetOrAdd(key, _ => SolveChief(trace, gx, gy));
    }

    private ChiefAim SolveChief(SequentialTrace trace, double gx, double gy)
    {
        var field = FieldOf(gx, gy);
        var (hx, hy) = OpticsConventions.NormalizeField((gx, gy), ParaxialScale());
        Newton2DResult result = default;
        if (!_aiming.Robust)
        {
            var (s1, s2) = AssistedAim(field, hx, hy, 0, 0);
            result = SolveStop(trace, field, 0, 0, s1, s2);
            if (result.Converged) return new ChiefAim(true, result.X, result.Y, null);
        }
        // Continuation in field from the axis.
        var axis = FieldOf(0, 0);
        var (z1, z2) = AssistedAim(axis, 0, 0, 0, 0);
        var start = SolveStop(trace, axis, 0, 0, z1, z2);
        if (!start.Converged)
            return new ChiefAim(false, double.NaN, double.NaN, $"the axial chief ray cannot be aimed at the stop centre ({start.Failure})");
        if (gx == 0 && gy == 0) return new ChiefAim(true, start.X, start.Y, null);
        Newton2DResult At(double t, double s1, double s2) => SolveStop(trace, FieldOf(t * gx, t * gy), 0, 0, s1, s2);
        if (!Continue((start.X, start.Y), At, _aiming.Robust ? 8 : 4, !_aiming.Robust, out result))
            return new ChiefAim(false, double.NaN, double.NaN,
                $"continuation in field from the axis stalled before field ({gx:g6}, {gy:g6}) ({result.Failure}); give the pupil shift and compression of the ray-aiming settings a rough guess of the real pupil");
        return new ChiefAim(true, result.X, result.Y, null);
    }

    /// <summary>The scale that normalises a paraxial field (for a real image height, the
    /// image heights read as paraxial ones: close enough to scale the pupil-shift guess).</summary>
    private (double X, double Y) ParaxialScale() => Paraxial.System.Fields.Scale;

    private Newton2DResult SolveStop(SequentialTrace trace, Field field, double tx, double ty, double s1, double s2)
    {
        double scale = StopRadius;
        return DampedNewton2D.Solve(
            (double a1, double a2, out double fx, out double fy) =>
            {
                fx = fy = double.NaN;
                if (!TryRay(trace, field, a1, a2, out var ray) || !LandOnStop(trace, ray, out var p, out _)) return false;
                fx = p.X - tx;
                fy = p.Y - ty;
                return true;
            },
            s1, s2, 1e-7 * _parameterScale, AimTolerance * scale, AimAcceptTolerance * scale);
    }

    /// <summary>
    /// Solves at t = 1 by continuation from a solution at t = 0: steps in t, each seeded by
    /// linear extrapolation of the last two solutions (or the last one), the step halved on a
    /// failure (to 1/512 of the way) and, when <paramref name="grow"/>, doubled on a success.
    /// </summary>
    private static bool Continue((double X, double Y) start, Func<double, double, double, Newton2DResult> solveAt,
        int steps, bool grow, out Newton2DResult result)
    {
        double t = 0, dt = 1.0 / steps, previousT = double.NaN;
        var current = start;
        (double X, double Y) previous = default;
        result = new Newton2DResult(true, start.X, start.Y, 0, 0, null);
        while (t < 1)
        {
            double next = Math.Min(1, t + dt);
            var seed = current;
            if (!double.IsNaN(previousT))
            {
                double k = (next - t) / (t - previousT);
                seed = (current.X + (current.X - previous.X) * k, current.Y + (current.Y - previous.Y) * k);
            }
            var r = solveAt(next, seed.X, seed.Y);
            if (!r.Converged && !double.IsNaN(previousT)) r = solveAt(next, current.X, current.Y);
            if (r.Converged)
            {
                previous = current;
                previousT = t;
                current = (r.X, r.Y);
                t = next;
                result = r;
                if (grow) dt = Math.Min(2 * dt, 1);
            }
            else
            {
                result = r;
                dt /= 2;
                if (dt < 1.0 / 512) return false;
            }
        }
        return true;
    }

    /// <summary>Robust aiming: every segment from surface 2 to the stop has the sign of a real
    /// path (thickness times the sense of travel); a segment the other way is a virtual path to
    /// the stop, a wrong root of the aiming equations.</summary>
    private bool RealPathToStop(SequentialTrace trace, Ray ray, out string? why)
    {
        why = null;
        if (!LandOnStop(trace, ray, out _, out var path))
        {
            why = "the aimed ray no longer reaches the stop";
            return false;
        }
        for (int j = 2; j <= _stop; j++)
        {
            int expected = _expectedSign[j];
            double length = path.Hits[j].SegmentLength;
            double scale = Math.Abs(_lens.Surfaces[j - 1].Thickness);
            if (expected != 0 && Math.Sign(length) == -expected && Math.Abs(length) > 1e-9 * scale)
            {
                why = $"robust ray aiming: the only ray found reaches the stop along a virtual segment before surface {j} (length {length:g6} m); the system has no real path to that stop point";
                return false;
            }
        }
        return true;
    }

    // ---- Real image height ----

    /// <summary>The paraxial image height whose real chief ray meets the image surface at
    /// <paramref name="target"/> (meters, image surface's local frame), primary wavelength.</summary>
    private (double X, double Y) SolveImageField((double X, double Y) target)
    {
        if (target.X == 0 && target.Y == 0) return (0, 0);
        var trace = _primary!;
        double scale = Math.Max(Math.Abs(_system.Fields.Scale.X), Math.Abs(_system.Fields.Scale.Y));
        // With aiming on, each trial field's chief ray is aimed by one Newton solve started from
        // the last chief aim that converged (the axial one first), not by the cached solve with
        // its continuation: an unreachable trial field then fails quickly. The sequence of trials
        // is fixed by the target, so the result is still a pure function of it.
        var warm = _aiming.IsOn ? Chief(trace, 0, 0) : null;
        if (warm is { Ok: false })
            throw new InvalidOperationException($"Real image height: the axial chief ray cannot be aimed at the stop centre ({warm.Failure}).");
        double w1 = warm?.A1 ?? 0, w2 = warm?.A2 ?? 0;
        Newton2DResult Solve(double tx, double ty, double s1, double s2) => DampedNewton2D.Solve(
            (double gx, double gy, out double fx, out double fy) =>
            {
                fx = fy = double.NaN;
                Ray chief;
                var field = FieldOf(gx, gy);
                if (_aiming.IsOn)
                {
                    var aim = SolveStop(trace, field, 0, 0, w1, w2);
                    if (!aim.Converged || !TryRay(trace, field, aim.X, aim.Y, out chief)) return false;
                    (w1, w2) = (aim.X, aim.Y);
                }
                else
                {
                    var (a1, a2) = OffAim(field, 0, 0);
                    if (!TryRay(trace, field, a1, a2, out chief)) return false;
                }
                var final = trace.Trace(chief, Unapertured);
                if (!final.IsOk) return false;
                fx = final.Position.X - tx;
                fy = final.Position.Y - ty;
                return true;
            },
            s1, s2, 1e-7 * scale, AimTolerance * scale, AimAcceptTolerance * scale);

        var result = Solve(target.X, target.Y, target.X, target.Y);
        if (result.Converged) return (result.X, result.Y);
        Newton2DResult At(double t, double s1, double s2) => Solve(t * target.X, t * target.Y, s1, s2);
        if (Continue((0, 0), At, 8, true, out result)) return (result.X, result.Y);
        throw new InvalidOperationException(
            $"Real image height: no real chief ray meets the image surface at ({target.X:g8}, {target.Y:g8}) m ({result.Failure}); the height may be beyond the largest the lens forms, or the image surface may be at a pupil. Use a smaller field or another field type.");
    }

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v == 0 ? 0 : v);
}
