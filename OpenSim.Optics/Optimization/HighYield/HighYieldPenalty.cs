using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization.HighYield;

// OP-62: the High-Yield penalty term (docs/Optics-Conventions.md §50).

/// <summary>
/// The penalty term of OpticStudio's High-Yield optimization, the value of its HYLD operand.
/// <para>
/// <b>Definition.</b> Zemax Knowledgebase article KA-01837, "Designing for as-built performance
/// with High-Yield Optimization" (C. Normanshire, 2021; saved, with its equation images, under
/// plans/sources/optics/op-62-help/: wb-zemax.html, KA-01837_6_Penalty.png):
/// <code>τ = |n − n′| (1 − R·N)</code>
/// "n and n′ are the indices of refraction before and after surface; N is the surface normal
/// vector; R is the ray vector. If n′ &gt; n, this is the incident ray vector, else the refracted
/// ray vector. R·N is the cosine of the ray angle." The OpticStudio 2025 R2 help says the same in
/// outline (Constraints on Real Ray Data, HYLD: "calculated from the incident or exit ray cosine,
/// the surface normal and the indices of refraction before and after the surface"), and the
/// Zemax eGuide "High-Yield Optimization" (2019, p. 4, saved there as eGuide.txt) says which
/// angle in words: "the individual ray angles of incidence (or the exit angles, whichever is
/// larger)". The rule picks that angle: by Snell's law the ray in the lower index makes the
/// larger angle with the normal, and the incident ray is in the lower index exactly when n′ &gt; n.
/// </para>
/// <para>
/// <b>Arithmetic.</b> R·N is read as the cosine of the angle (0 to 90°), so its absolute value is
/// taken (the normal's sense, +z at the vertex, is the trace's, not the ray's). 1 − cos θ is
/// computed as sin²θ/(1 + cos θ) with sin²θ = |R × N|² (derived: 1 − cos² = sin² for unit
/// vectors), which keeps its digits at small angles where 1 − cos θ would cancel; τ goes as
/// |n − n′| θ²/2 there (the article's own expansion, KA-01837_7_PenaltyExpansion.png).
/// </para>
/// <para>
/// <b>Mirrors are refused.</b> The article and the help define τ for a surface with indices on
/// both sides; they do not say what n′ is at a mirror (this program's trace keeps n′ = n there,
/// which would make τ zero by construction). A HYLD on a mirror fails naming the surface rather
/// than return that zero; <see cref="HighYieldMerit"/> writes no rows for mirrors and says so.
/// </para>
/// </summary>
public static class HighYieldPenalty
{
    /// <summary>τ = |n − n′|(1 − |R·N|), R the incident direction when n′ &gt; n, else the
    /// refracted one (see the class remarks). Directions and normal are unit vectors.</summary>
    public static double Tau(double indexBefore, double indexAfter, Vector3D incident, Vector3D refracted, Vector3D normal)
    {
        var r = indexAfter > indexBefore ? incident : refracted;
        double cos = Math.Min(1, Math.Abs(Vector3D.Dot(r, normal)));
        double sin2 = Vector3D.Cross(r, normal).LengthSquared;
        return Math.Abs(indexBefore - indexAfter) * (sin2 / (1 + cos));
    }

    /// <summary>τ of a traced ray at a surface hit. Throws <see cref="ArgumentException"/> for a
    /// mirror or a hit where the ray failed (vignetted, total internal reflection).</summary>
    public static double Tau(SurfaceHit hit, bool mirror)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (mirror)
            throw new ArgumentException($"surface {hit.Surface} is a mirror; the High-Yield term is defined from the indices before and after a refracting surface (KA-01837), and its value at a mirror is not given.");
        if (hit.Status != RayStatus.Ok)
            throw new ArgumentException($"the ray failed at surface {hit.Surface}: {RayStatuses.Label(hit.Status)}.");
        return Tau(hit.IndexBefore, hit.IndexAfter, hit.IncomingDirection, hit.Direction, hit.Normal);
    }

    /// <summary>
    /// The surfaces at which the High-Yield term can be non-zero at wavelength
    /// <paramref name="wave"/> (1-based): surfaces 1 to the last before the image that are not
    /// mirrors, coordinate breaks or ignored, with a different index on either side (the indices
    /// the real-ray trace uses: a dummy keeps the index before it). Mirrors are added to
    /// <paramref name="mirrors"/> when given.
    /// </summary>
    public static IReadOnlyList<int> RefractingSurfaces(SequentialLens lens, OpticalSystemData system, int wave, ICollection<int>? mirrors = null)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        int count = system.Wavelengths.Wavelengths.Count;
        if (wave < 1 || wave > count)
            throw new ArgumentOutOfRangeException(nameof(wave), wave, $"The system has wavelengths 1 to {count}.");
        double micrometers = system.Wavelengths.Wavelengths[wave - 1].Micrometers;
        var media = lens.EffectiveMedia();
        int image = lens.ImageIndex;
        var index = new double[image];
        var surfaces = new List<int>();
        for (int j = 0; j < image; j++)
        {
            var s = lens.Surfaces[j];
            bool dummy = j > 0 && (s.IsMirror || s.IsIgnored || s.IsCoordinateBreak);
            index[j] = dummy ? index[j - 1] : media[j].IndexAt(micrometers, system.Environment);
            if (!double.IsFinite(index[j]) || !(index[j] > 0))
                throw new ArgumentException(
                    $"High-Yield: the index of {(j == 0 ? "object space" : $"the space after surface {j}")} is {index[j].ToString(CultureInfo.InvariantCulture)} at {micrometers.ToString(CultureInfo.InvariantCulture)} µm; it must be positive.");
            if (j == 0) continue;
            if (s.IsMirror) { if (mirrors is not null && !mirrors.Contains(j)) mirrors.Add(j); continue; }
            if (dummy) continue;
            if (index[j] != index[j - 1]) surfaces.Add(j);
        }
        return surfaces;
    }
}
