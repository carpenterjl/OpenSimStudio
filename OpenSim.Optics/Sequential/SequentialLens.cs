using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// A sequential lens for the real ray tracer, numbered as the lens data editor numbers it:
/// surface 0 is the object (its thickness the object distance, possibly +infinity), the last
/// is the image surface, and exactly one surface between them is the stop.
/// <see cref="ToParaxialLens"/> gives the same lens to the paraxial engine (OP-19), so the
/// first-order data and the real trace always describe one lens.
/// <para>
/// The project saves its surfaces as <c>OpticsSurfaceSettings</c> (OP-31); <see cref="SavedSurfaces"/>
/// converts them to and from this record.
/// </para>
/// </summary>
public sealed record SequentialLens
{
    public required IReadOnlyList<SequentialSurface> Surfaces { get; init; }

    /// <summary>The image surface's index.</summary>
    public int ImageIndex => Surfaces.Count - 1;

    /// <summary>The last surface before the image.</summary>
    public int LastSurfaceIndex => Surfaces.Count - 2;

    /// <summary>The stop's index, or −1 when no surface is the stop.</summary>
    public int StopIndex
    {
        get
        {
            for (int j = 1; j < Surfaces.Count - 1; j++)
                if (Surfaces[j].IsStop) return j;
            return -1;
        }
    }

    /// <summary>True when the object distance is infinite.</summary>
    public bool ObjectAtInfinity => Surfaces.Count > 0 && double.IsPositiveInfinity(Surfaces[0].Thickness);

    /// <summary>
    /// The same lens for the paraxial engine: vertex curvature, thickness, medium, stop, mirror,
    /// and the stop's semi-diameter. Conics and apertures do not enter first-order optics. An
    /// ignored surface and a coordinate break become a plane with the medium before it, which is
    /// what the real trace does with them. A surface with a <see cref="SequentialSurface.Shape"/>
    /// gives the shape's <see cref="SurfaceShape.ParaxialEquivalent"/> (OP-27): its curvature,
    /// thin-lens power or ABCD matrix. The object and image surfaces are taken as planes by both
    /// engines.
    /// <para>
    /// <b>Tilts and coordinate breaks</b> (OP-21, docs/Optics-Conventions.md §15). Without any,
    /// the thicknesses are copied. With some, the lens is unfolded along its axis: the ray from
    /// the global origin along +z is followed through the surfaces (<see cref="SurfacePlacement"/>),
    /// and each thickness becomes the signed length of that ray from one surface to the next
    /// (negative after an odd number of mirrors). This is exact when the tilts only fold the axis
    /// with plane mirrors and move dummy planes: every surface with power (or a shape) must be met
    /// at its vertex along its local z, with z pointing along the light after an even number of
    /// mirrors and against it after an odd number, and no refracting surface may meet the axis
    /// obliquely. Any other lens is not axial, and this method throws
    /// <see cref="InvalidOperationException"/> naming the first surface that breaks the rule,
    /// because first-order optics about a tilted or decentered surface is not defined by this
    /// model. The real ray trace has no such limit.
    /// </para>
    /// </summary>
    public ParaxialLens ToParaxialLens()
    {
        var lens = ToParaxialLensAsEntered();
        if (!SurfacePlacement.HasPlacementData(this)) return lens;
        var problems = Validate();
        if (problems.Count > 0)
            throw new ArgumentException("Sequential lens:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        var thickness = UnfoldedThicknesses(SurfacePlacement.ComputeValidated(this));
        var surfaces = lens.Surfaces.ToArray();
        for (int j = 1; j < ImageIndex; j++)
            surfaces[j] = surfaces[j] with { Thickness = thickness[j] };
        return new ParaxialLens { Surfaces = surfaces };
    }

    /// <summary>True when <see cref="ToParaxialLens"/> describes this lens: it has no tilts or
    /// coordinate breaks, or they only fold its axis (the rule there). Otherwise
    /// <paramref name="reason"/> names the surface that makes it non-axial.</summary>
    public bool IsAxial(out string? reason)
    {
        reason = null;
        if (!SurfacePlacement.HasPlacementData(this)) return true;
        try
        {
            ToParaxialLens();
            return true;
        }
        catch (InvalidOperationException e)
        {
            reason = e.Message;
            return false;
        }
    }

    /// <summary>Surface by surface with the thicknesses as entered: what the paraxial lens's
    /// own rules check. With <paramref name="forValidation"/>, a shape that has no first-order
    /// model enters with its vertex curvature only, so the thickness, media and stop rules can
    /// still be checked for a lens that traces but cannot be analysed paraxially (the refusal is
    /// raised where paraxial data are asked for, not when the lens is built).</summary>
    private ParaxialLens ToParaxialLensAsEntered(bool forValidation = false)
    {
        var media = EffectiveMedia();
        var surfaces = new ParaxialSurface[Surfaces.Count];
        int image = Surfaces.Count - 1;
        for (int j = 0; j < Surfaces.Count; j++)
        {
            var s = Surfaces[j];
            bool dummy = s.IsIgnored || s.IsCoordinateBreak;
            var equivalent = dummy || s.Shape is null || j == 0 || j == image
                ? new ParaxialShapeEquivalent { Curvature = dummy ? 0 : s.Curvature }
                : forValidation ? ShapeEquivalentOrVertex(s) : ShapeEquivalent(s, j);
            surfaces[j] = new ParaxialSurface
            {
                Curvature = equivalent.Curvature,
                ThinLensPower = equivalent.ThinLensPower,
                SlopeMatrix = equivalent.SlopeMatrix,
                Thickness = s.Thickness,
                Medium = dummy && j > 0 ? media[j - 1] : s.Medium,
                IsStop = s.IsStop,
                IsMirror = s.IsMirror && !dummy,
                SemiDiameter = s.SemiDiameter,
                Comment = s.Comment,
            };
        }
        return new ParaxialLens { Surfaces = surfaces };
    }

    private static ParaxialShapeEquivalent ShapeEquivalentOrVertex(SequentialSurface s)
    {
        try { return s.Shape!.ParaxialEquivalent(s); }
        catch (ArgumentException) { return new ParaxialShapeEquivalent { Curvature = s.Curvature }; }
    }

    /// <summary>The shape's first-order model; a shape that has none (it throws
    /// <see cref="ArgumentException"/>) is reported with the surface number.</summary>
    private static ParaxialShapeEquivalent ShapeEquivalent(SequentialSurface s, int j)
    {
        try
        {
            return s.Shape!.ParaxialEquivalent(s);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"Surface {j}: {e.Message}", e);
        }
    }

    /// <summary>
    /// The signed axis lengths of <see cref="ToParaxialLens"/>: the ray from the global origin
    /// along +z, moved surface by surface in each local frame. At a surface's vertex the normal is
    /// the local z, so an axis ray along ±z passes a refracting surface unchanged whatever the
    /// indices, and any mirror (a plane one wherever it is met) reflects it about the local z.
    /// The position tolerance is 10⁻¹² of the largest finite thickness.
    /// </summary>
    private double[] UnfoldedThicknesses(SurfacePlacement placement)
    {
        int image = ImageIndex;
        double scale = 0;
        for (int j = 1; j < image; j++)
            if (double.IsFinite(placement.Thickness(j))) scale = Math.Max(scale, Math.Abs(placement.Thickness(j)));
        if (!(scale > 0)) scale = 1;
        double positionTolerance = 1e-12 * scale, directionTolerance = 1e-12;
        var media = EffectiveMedia();
        var unitZ = Vector3D.UnitZ;

        var p = Vector3D.Zero;
        var d = unitZ;
        var segment = new double[Surfaces.Count];
        int mirrors = 0;
        for (int j = 1; j <= image; j++)
        {
            var step = placement.Step(j);
            p = step.InverseApply(p);
            d = step.InverseApplyDirection(d);
            var s = Surfaces[j];
            bool dummy = s.IsIgnored || s.IsCoordinateBreak;
            double length = 0;
            if (s.IsCoordinateBreak ? Math.Abs(d.Z) < SequentialTrace.ParallelToBreak : d.Z == 0)
            {
                if (!s.IsCoordinateBreak)
                    throw new InvalidOperationException($"Paraxial model: the axis runs parallel to surface {j}'s vertex plane, so the lens is not axial.");
            }
            else
            {
                length = -p.Z / d.Z;
                p += d * length;
            }
            segment[j] = length;
            if (dummy || j == image) continue;

            bool plane = s.Curvature == 0 && s.Shape is null;
            bool alongZ = Math.Abs(d.X) <= directionTolerance && Math.Abs(d.Y) <= directionTolerance;
            if (!plane)
            {
                if (Math.Abs(p.X) > positionTolerance || Math.Abs(p.Y) > positionTolerance)
                    throw new InvalidOperationException(
                        $"Paraxial model: the axis meets surface {j} at ({p.X:g6}, {p.Y:g6}) m from its vertex; a decentered or tilted surface with power makes the lens not axial.");
                if (!alongZ)
                    throw new InvalidOperationException(
                        $"Paraxial model: the axis meets surface {j} at {SurfaceHit.AngleDegrees(d, unitZ):g6}° to its axis; a tilted surface with power makes the lens not axial.");
                int expected = mirrors % 2 == 0 ? 1 : -1;
                if (Math.Sign(d.Z) != expected)
                    throw new InvalidOperationException(
                        $"Paraxial model: at surface {j} the local z points {(expected > 0 ? "against" : "along")} the light after {mirrors} mirror(s); the paraxial sign rules (Conventions §3) need it {(expected > 0 ? "along" : "against")} the light.");
            }
            if (s.IsMirror)
            {
                d = OpticsConventions.Reflect(d, unitZ);
                mirrors++;
            }
            else if (!alongZ && !Equals(media[j], media[j - 1]))
            {
                throw new InvalidOperationException(
                    $"Paraxial model: the axis crosses the refracting surface {j} at {SurfaceHit.AngleDegrees(d, unitZ):g6}° to its normal, so the lens is not axial.");
            }
        }

        // Thickness j is the length from surface j to j + 1, signed by the mirrors up to and including j.
        var thickness = new double[Surfaces.Count];
        int count = 0;
        for (int j = 1; j < image; j++)
        {
            var s = Surfaces[j];
            if (s.IsMirror && !s.IsIgnored && !s.IsCoordinateBreak) count++;
            thickness[j] = (count % 2 == 0 ? 1 : -1) * segment[j + 1];
        }
        return thickness;
    }

    /// <summary>The medium a ray is in after each surface: its own, or the one before for a
    /// mirror, an ignored surface or a coordinate break.</summary>
    internal ParaxialMedium[] EffectiveMedia()
    {
        var media = new ParaxialMedium[Surfaces.Count];
        for (int j = 0; j < Surfaces.Count; j++)
        {
            var s = Surfaces[j];
            media[j] = j > 0 && (s.IsMirror || s.IsIgnored || s.IsCoordinateBreak) ? media[j - 1] : s.Medium;
        }
        return media;
    }

    /// <summary>Every problem, one actionable line each; empty when the lens can be traced.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Surfaces is null || Surfaces.Count < 3)
        {
            problems.Add("Sequential lens: it needs the object surface, at least one surface and the image surface.");
            return problems;
        }
        if (Surfaces.Any(s => s is null))
        {
            for (int j = 0; j < Surfaces.Count; j++)
                if (Surfaces[j] is null) problems.Add($"{Where(j)}: missing.");
            return problems;
        }
        int image = Surfaces.Count - 1;
        for (int j = 0; j < Surfaces.Count; j++)
        {
            var s = Surfaces[j];
            string where = Where(j);
            if (!double.IsFinite(s.Conic))
                problems.Add($"{where}: the conic constant must be a number (it is {s.Conic}).");
            if ((j == 0 || j == image) && s.IsIgnored)
                problems.Add($"{where}: the object and image surfaces cannot be ignored.");
            if (s.IsIgnored && s.IsStop)
                problems.Add($"{where}: the stop cannot be ignored.");
            if (s.IsIgnored && s.IsMirror)
                problems.Add($"{where}: a mirror cannot be ignored, because the thicknesses after it are signed for the reflection.");
            if (j == 0 && s.Aperture is not null)
                problems.Add($"{where}: the object surface cannot have an aperture.");
            s.Aperture?.Validate(where, problems);
            if (s.MechanicalSemiDiameter is double m && (!double.IsFinite(m) || m < 0))
                problems.Add($"{where}: the mechanical semi-diameter must be zero or positive (it is {m}).");
            if (!double.IsFinite(s.ChipZone) || s.ChipZone < 0)
                problems.Add($"{where}: the chip zone must be zero or positive (it is {s.ChipZone}).");
        }
        // Coordinate breaks and tilts (OP-21).
        SurfacePlacement.Validate(Surfaces, ObjectAtInfinity, Where, problems);
        // Curvature, thickness, media, stop count and semi-diameters: the paraxial lens's own rules.
        foreach (var p in ToParaxialLensAsEntered(forValidation: true).Validate())
            problems.Add(p.Replace("Paraxial lens:", "Sequential lens:"));
        return problems;
    }

    private string Where(int j) => j == 0 ? "Object surface" : j == Surfaces.Count - 1 ? "Image surface" : $"Surface {j}";
}
