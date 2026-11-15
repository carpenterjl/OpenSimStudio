namespace OpenSim.Optics.Paraxial;

/// <summary>
/// The surfaces of a sequential system as the paraxial engine takes them, numbered as the lens
/// data editor numbers them: surface 0 is the object (its thickness is the object distance and
/// its medium is object space), the last is the image surface, and the ones between refract or
/// reflect. Exactly one surface between the object and the image is the stop.
/// <para>
/// A configuration (OP-30) is a different lens and system data; the engine takes one pair at a
/// time, so configurations need nothing from it.
/// </para>
/// </summary>
public sealed record ParaxialLens
{
    public required IReadOnlyList<ParaxialSurface> Surfaces { get; init; }

    /// <summary>The image surface's index.</summary>
    public int ImageIndex => Surfaces.Count - 1;

    /// <summary>The last surface before the image: the one image-space data are measured from.</summary>
    public int LastSurfaceIndex => Surfaces.Count - 2;

    /// <summary>The stop's index, or −1 when no surface is the stop (<see cref="Validate"/> reports it).</summary>
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

    /// <summary>Every problem, one actionable line each; empty when the lens can be traced.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Surfaces is null || Surfaces.Count < 3)
        {
            problems.Add("Paraxial lens: it needs the object surface, at least one surface and the image surface.");
            return problems;
        }
        int image = Surfaces.Count - 1;
        int stops = 0;
        for (int j = 0; j < Surfaces.Count; j++)
        {
            var s = Surfaces[j];
            string where = j == 0 ? "Object surface" : j == image ? "Image surface" : $"Surface {j}";
            if (s is null)
            {
                problems.Add($"{where}: missing.");
                continue;
            }
            if (!double.IsFinite(s.Curvature))
                problems.Add($"{where}: the curvature must be a number (it is {s.Curvature}).");
            if (j == 0)
            {
                if (double.IsNaN(s.Thickness) || double.IsNegativeInfinity(s.Thickness))
                    problems.Add($"{where}: the object distance must be a number or +infinity (it is {s.Thickness}).");
            }
            else if (j < image && !double.IsFinite(s.Thickness))
                problems.Add($"{where}: the thickness must be a number (it is {s.Thickness}).");
            if ((j == 0 || j == image) && (s.IsMirror || s.IsStop))
                problems.Add($"{where}: the object and image surfaces cannot be a mirror or the stop.");
            if (j > 0 && j < image && s.IsStop) stops++;
            if (j < image && !(j > 0 && s.IsMirror)) s.Medium.Validate(where, problems);
            if (s.SemiDiameter is double sd && (!double.IsFinite(sd) || sd < 0))
                problems.Add($"{where}: the semi-diameter must be zero or positive (it is {sd}).");
            // OP-27: idealised surfaces.
            if (!double.IsFinite(s.ThinLensPower))
                problems.Add($"{where}: the thin-lens power must be a number (it is {s.ThinLensPower}).");
            // OP-26: diffractive surfaces.
            if (!double.IsFinite(s.DiffractivePowerPerWavelength))
                problems.Add($"{where}: the diffractive power per wavelength must be a number (it is {s.DiffractivePowerPerWavelength}).");
            if (s.SlopeMatrix is { } m)
            {
                if (!double.IsFinite(m.A) || !double.IsFinite(m.B) || !double.IsFinite(m.C) || !double.IsFinite(m.D))
                    problems.Add($"{where}: every entry of the ABCD matrix must be a number (it is {m}).");
                if (j == 0 || j == image)
                    problems.Add($"{where}: the object and image surfaces cannot carry an ABCD matrix.");
                else if (m.B != 0 && (j == 1 || s.IsStop || j == image - 1))
                    problems.Add($"{where}: an ABCD matrix with B ≠ 0 moves the ray across the surface, so it cannot be surface 1, the stop or the last surface; put a plane dummy surface before or after it.");
            }
        }
        if (stops != 1)
            problems.Add(stops == 0
                ? "Paraxial lens: no surface is the stop; mark one surface between the object and the image as the stop."
                : $"Paraxial lens: {stops} surfaces are marked as the stop; exactly one may be.");
        return problems;
    }
}
