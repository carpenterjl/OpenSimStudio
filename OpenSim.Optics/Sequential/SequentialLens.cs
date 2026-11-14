using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// A sequential lens for the real ray tracer, numbered as the lens data editor numbers it:
/// surface 0 is the object (its thickness the object distance, possibly +infinity), the last
/// is the image surface, and exactly one surface between them is the stop.
/// <see cref="ToParaxialLens"/> gives the same lens to the paraxial engine (OP-19), so the
/// first-order data and the real trace always describe one lens.
/// <para>
/// Persistence and the editor are OP-31; until then a lens is built in code.
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
    /// ignored surface becomes a plane with the medium before it, which is what the real trace
    /// does with it. A surface with a <see cref="SequentialSurface.Shape"/> gives the shape's
    /// <see cref="SurfaceShape.ParaxialEquivalent"/> (OP-27): its curvature, thin-lens power or
    /// ABCD matrix. The object and image surfaces are taken as planes by both engines.
    /// </summary>
    public ParaxialLens ToParaxialLens()
    {
        var media = EffectiveMedia();
        var surfaces = new ParaxialSurface[Surfaces.Count];
        int image = Surfaces.Count - 1;
        for (int j = 0; j < Surfaces.Count; j++)
        {
            var s = Surfaces[j];
            var equivalent = s.IsIgnored || s.Shape is null || j == 0 || j == image
                ? new ParaxialShapeEquivalent { Curvature = s.IsIgnored ? 0 : s.Curvature }
                : s.Shape.ParaxialEquivalent(s);
            surfaces[j] = new ParaxialSurface
            {
                Curvature = equivalent.Curvature,
                ThinLensPower = equivalent.ThinLensPower,
                SlopeMatrix = equivalent.SlopeMatrix,
                Thickness = s.Thickness,
                Medium = s.IsIgnored && j > 0 ? media[j - 1] : s.Medium,
                IsStop = s.IsStop,
                IsMirror = s.IsMirror && !s.IsIgnored,
                SemiDiameter = s.SemiDiameter,
                Comment = s.Comment,
            };
        }
        return new ParaxialLens { Surfaces = surfaces };
    }

    /// <summary>The medium a ray is in after each surface: its own, or the one before for a
    /// mirror or an ignored surface.</summary>
    internal ParaxialMedium[] EffectiveMedia()
    {
        var media = new ParaxialMedium[Surfaces.Count];
        for (int j = 0; j < Surfaces.Count; j++)
        {
            var s = Surfaces[j];
            media[j] = j > 0 && (s.IsMirror || s.IsIgnored) ? media[j - 1] : s.Medium;
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
        // Curvature, thickness, media, stop count and semi-diameters: the paraxial lens's own rules.
        foreach (var p in ToParaxialLens().Validate())
            problems.Add(p.Replace("Paraxial lens:", "Sequential lens:"));
        return problems;
    }

    private string Where(int j) => j == 0 ? "Object surface" : j == Surfaces.Count - 1 ? "Image surface" : $"Surface {j}";
}
