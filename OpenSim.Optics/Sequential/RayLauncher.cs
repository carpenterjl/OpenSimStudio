using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// Real rays from normalised field (Hx, Hy) and pupil (Px, Py) coordinates
/// (docs/Optics-Conventions.md §7, §13), with ray aiming off: the pupil point is on the
/// paraxial entrance pupil of the primary wavelength, scaled by its radius. Ray aiming at the
/// real stop is OP-22.
/// <para>
/// The field point f = H·F (<see cref="OpticsConventions.DenormalizeField"/>) is turned into
/// the object-space chief ray by the paraxial engine (<see cref="ParaxialSystem.TraceChiefRay"/>),
/// whose object-space slopes are the exact tangents of an angle field and whose object heights
/// are exact for a height field; so every field type the paraxial engine defines launches the
/// same real ray it describes (a theodolite field the direction (cos el·sin az, sin el,
/// cos el·cos az)); real image height is refused there. Then:
/// <list type="bullet">
/// <item>Object at infinity: the direction is (ūx, ūy, 1) normalised; the ray passes through
/// the pupil point (Px·r, Py·r, z_E) and starts on the plane through surface 1's vertex at
/// right angles to it, the reference plane wavefront of the field.</item>
/// <item>Object at a finite distance: the ray starts at the object point (x̄, ȳ, −t0) and passes
/// through the pupil point; with the entrance pupil at infinity (telecentric) its direction is
/// (Px·u, Py·u, 1), u the paraxial marginal slope in object space.</item>
/// </list>
/// Vignetting factors of the field points are ray aiming's (OP-22) and are not applied.
/// </para>
/// </summary>
public sealed class RayLauncher
{
    private readonly SequentialLens _lens;

    /// <summary>The paraxial model at the primary wavelength that defines the pupil and fields.</summary>
    public ParaxialSystem Paraxial { get; }

    public RayLauncher(SequentialLens lens, OpticalSystemData system)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        _lens = lens;
        var problems = lens.Validate();
        if (problems.Count > 0)
            throw new ArgumentException("Real ray trace refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), system);
    }

    /// <summary>The ray of normalised field (hx, hy) and pupil (px, py), in surface 1's frame,
    /// carrying the vacuum wavelength of <paramref name="trace"/>.</summary>
    public Ray Launch(SequentialTrace trace, double hx, double hy, double px, double py)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var system = Paraxial.System;
        var (fx, fy) = OpticsConventions.DenormalizeField((hx, hy), system.Fields.Scale);
        var (chiefX, chiefY) = Paraxial.TraceChiefRay(new FieldPoint { X = fx, Y = fy });

        if (_lens.ObjectAtInfinity)
        {
            var direction = new Vector3D(chiefX.Slopes[0], chiefY.Slopes[0], 1);
            direction /= direction.Length;
            var pupil = PupilPoint(px, py);
            var start = pupil - direction * Vector3D.Dot(pupil, direction);
            return trace.NewRay(start, direction);
        }

        double t0 = _lens.Surfaces[0].Thickness;
        var objectPoint = new Vector3D(chiefX.Heights[0], chiefY.Heights[0], -t0);
        if (Paraxial.EntrancePupilAtInfinity)
        {
            double u = Math.Abs(Paraxial.MarginalRay.Slopes[0]);
            return trace.NewRay(objectPoint, new Vector3D(px * u, py * u, 1));
        }
        var toPupil = PupilPoint(px, py) - objectPoint;
        if (toPupil.Z == 0)
            throw new InvalidOperationException("Real ray trace: the object lies in the entrance-pupil plane, so the ray has no direction; move the object or the stop.");
        // Light travels +z in object space; a virtual object (negative distance) lies beyond the pupil.
        if (toPupil.Z < 0) toPupil = -toPupil;
        return trace.NewRay(objectPoint, toPupil);
    }

    private Vector3D PupilPoint(double px, double py)
    {
        double r = Paraxial.EntrancePupilDiameter!.Value / 2;
        return new Vector3D(px * r, py * r, Paraxial.EntrancePupilPosition!.Value);
    }
}
