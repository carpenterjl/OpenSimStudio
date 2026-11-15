using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis.StrayLight;

/// <summary>What a surface of a ghost system does on the ghost path.</summary>
public enum GhostPass
{
    /// <summary>Met on the way in, before the first bounce (same direction as the lens).</summary>
    In,
    /// <summary>The first bounce: the original surface made a mirror.</summary>
    FirstBounce,
    /// <summary>Met on the way back, between the two bounces (or back to the object after a single bounce).</summary>
    Back,
    /// <summary>The second bounce of a double-bounce ghost.</summary>
    SecondBounce,
    /// <summary>Met on the way out to the image, after the second bounce.</summary>
    Out,
    /// <summary>The object surface or the ghost's image surface.</summary>
    End,
}

/// <summary>Where surface g of a ghost system comes from: the original surface and its pass.</summary>
public readonly record struct GhostSurfaceOrigin(int Original, GhostPass Pass);

/// <summary>
/// One ghost path as a sequential lens of its own (OP-46, docs/Optics-Conventions.md §49): the
/// lens that reflects at <see cref="FirstBounce"/> instead of refracting, goes back through the
/// surfaces before it, and, for a double-bounce ghost, reflects again at
/// <see cref="SecondBounce"/> and goes forward to the image.
/// </summary>
public sealed record GhostSystem
{
    /// <summary>The original surface of the first bounce.</summary>
    public required int FirstBounce { get; init; }

    /// <summary>The original surface of the second bounce, or null for a single-bounce ghost.</summary>
    public int? SecondBounce { get; init; }

    /// <summary>The ghost lens (meters, as every <see cref="SequentialLens"/>).</summary>
    public required SequentialLens Lens { get; init; }

    /// <summary>One entry per surface of <see cref="Lens"/>.</summary>
    public required IReadOnlyList<GhostSurfaceOrigin> Origins { get; init; }

    /// <summary>The ghost lens index of the first bounce.</summary>
    public int FirstBounceIndex => FirstBounce;

    /// <summary>The ghost lens index of the second bounce, or −1 for a single bounce.</summary>
    public int SecondBounceIndex => SecondBounce is int i ? FirstBounce + (FirstBounce - i) : -1;

    /// <summary>"7 → 2", or "7" for a single bounce: the bounce surfaces in the order the light meets them.</summary>
    public string Label => SecondBounce is int i ? $"{FirstBounce} → {i}" : $"{FirstBounce}";

    /// <summary>OpticStudio's file name for this ghost, GHfffsss (help, "Ghost Focus Generator");
    /// no file is written in this build.</summary>
    public string FileStem => $"GH{FirstBounce:000}{SecondBounce ?? 0:000}";
}

/// <summary>
/// Builds ghost systems by the double-pass construction (OP-46): OpticStudio's Ghost Focus
/// Generator "generates lens files which are derived from the current lens prescription data.
/// The generated files are set up to reflect light at a given surface, rather than refract it. The
/// portion of the optical system prior to the new reflective surface is then duplicated so the
/// rays can be traced back through" (2025 R2 help, "Ghost Focus Generator", saved under
/// plans/sources/optics/op-46-help/). OP-51's <c>MakeDoublePass</c> works on the saved surfaces
/// and removes the data after the mirror, so the construction is written here on the
/// <see cref="SequentialLens"/>, with the same rule.
/// <para>
/// <b>The rule</b> (derived from the sign conventions of §3, not copied). For a first bounce at
/// original surface j and a second at i &lt; j: surfaces 0 … j − 1 are kept; surface j follows as
/// a mirror of the same shape with thickness −t(j−1) (thickness changes sign when the direction of
/// travel reverses); for k = j − 1 down to i + 1 a copy of surface k follows, with the medium the
/// returning light enters (the effective medium after k − 1) and thickness −t(k−1); surface i
/// follows as a mirror with thickness t(i) (two reversals); then surfaces i + 1 … image − 1 as
/// they are, and the image. Every copy sits at its original's vertex in space and keeps its
/// curvature, conic and shape, because no local frame turns round at a mirror (§3). A single
/// bounce at j stops after the copy of surface 1, whose thickness is −t(0) so that the ghost's
/// image surface lies on the object surface; with the object at infinity it is 0 (surface 1's
/// vertex plane), as OP-51's Make Double Pass does.
/// </para>
/// <para>
/// <b>Stop and apertures.</b> The ghost is launched with the original lens's ray launcher, so its
/// stop flag only satisfies the lens rules: it stays on the original stop when that comes before
/// the first bounce, else it is put on the first bounce (single bounce, or a stop at the first
/// bounce) or on the stop's copy on the way out. Fixed apertures are copied; a floating aperture
/// with an automatic semi-diameter is dropped, since the ghost file's automatic semi-diameters
/// would be sized for the ghost's own beam (this program's reading; Conventions §10 item 45).
/// </para>
/// <para>
/// <b>Refused.</b> Coordinate breaks and surface tilts or decenters (the help: the feature "may
/// not work correctly for systems which contain coordinate breaks"), ignored surfaces between the
/// bounces are kept as copies (they do nothing either way), and a bounce on a mirror, an ignored
/// surface or a coordinate break.
/// </para>
/// </summary>
public static class GhostSystems
{
    /// <summary>
    /// The candidate bounce surfaces from <paramref name="first"/> to <paramref name="last"/>
    /// (both original surface numbers, 1 ≤ first ≤ last ≤ image): every surface whose indices
    /// before and after differ at <paramref name="indices"/> (the original trace's indices at the
    /// analysis wavelength), mirrors, ignored surfaces and coordinate breaks excepted; and the
    /// image surface when <paramref name="includeImage"/> and it is in range. The help: "Dummy
    /// surfaces (those that do not define an index boundary) and mirror surfaces are ignored as
    /// candidate bounce surfaces. The only exception is the image surface, which is considered a
    /// potential first bounce surface even if there is no index change on that surface."
    /// </summary>
    public static IReadOnlyList<int> Candidates(SequentialLens lens, IReadOnlyList<double> indices, int first, int last, bool includeImage = true)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(indices);
        int image = lens.ImageIndex;
        if (first < 1 || last > image || first > last)
            throw new ArgumentException($"Ghost focus: the surfaces must run from 1 to the image {image} in order (they are {first} to {last}).");
        var list = new List<int>();
        for (int j = first; j <= Math.Min(last, image - 1); j++)
        {
            var s = lens.Surfaces[j];
            if (s.IsMirror || s.IsIgnored || s.IsCoordinateBreak) continue;
            if (indices[j - 1] != indices[j]) list.Add(j);
        }
        if (includeImage && last == image) list.Add(image);
        return list;
    }

    /// <summary>Every double-bounce pair (first, second) with first &gt; second among
    /// <paramref name="candidates"/>: n(n − 1)/2 pairs for n candidates, ordered by first bounce
    /// then by second bounce descending (the order the help's GPIM example walks: "12 and then 11,
    /// 10, 9").</summary>
    public static IReadOnlyList<(int First, int Second)> Pairs(IReadOnlyList<int> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var sorted = candidates.Distinct().OrderBy(j => j).ToArray();
        var pairs = new List<(int, int)>(sorted.Length * (sorted.Length - 1) / 2);
        for (int a = 0; a < sorted.Length; a++)
            for (int b = a - 1; b >= 0; b--)
                pairs.Add((sorted[a], sorted[b]));
        return pairs;
    }

    /// <summary>Throws, naming the surface, when the lens has a coordinate break or a tilted or
    /// decentered surface (see the class summary).</summary>
    public static void CheckSupported(SequentialLens lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        for (int j = 1; j < lens.Surfaces.Count; j++)
        {
            var s = lens.Surfaces[j];
            if (s.IsCoordinateBreak)
                throw new ArgumentException($"Ghost focus: surface {j} is a coordinate break; ghost systems are built for untilted, centred lenses only in this build (the help warns the feature may not work with coordinate breaks).");
            if (s.TiltBefore is not null || s.TiltAfter is not null)
                throw new ArgumentException($"Ghost focus: surface {j} has a surface tilt or decenter; ghost systems are built for untilted, centred lenses only in this build.");
        }
    }

    /// <summary>
    /// The ghost system of a first bounce at original surface <paramref name="firstBounce"/>
    /// and, when given, a second bounce at <paramref name="secondBounce"/> &lt; firstBounce (see
    /// the class summary). Throws <see cref="ArgumentException"/> naming the surface for a bounce
    /// that cannot be built.
    /// </summary>
    public static GhostSystem Build(SequentialLens lens, int firstBounce, int? secondBounce)
    {
        ArgumentNullException.ThrowIfNull(lens);
        CheckSupported(lens);
        int image = lens.ImageIndex, j = firstBounce;
        if (j < 1 || j > image)
            throw new ArgumentException($"Ghost focus: the first bounce surface must be 1 to the image {image} (it is {j}).");
        CheckBounce(lens, j, "first");
        if (secondBounce is int i0)
        {
            if (i0 < 1 || i0 >= j)
                throw new ArgumentException($"Ghost focus: the second bounce surface must lie between 1 and the first bounce surface {j} − 1 (it is {i0}); the first bounce surface number must be larger than the second.");
            CheckBounce(lens, i0, "second");
        }

        var s = lens.Surfaces;
        var media = lens.EffectiveMedia();
        int stop = lens.StopIndex;
        var surfaces = new List<SequentialSurface>();
        var origins = new List<GhostSurfaceOrigin>();
        void Add(SequentialSurface surface, int original, GhostPass pass)
        {
            surfaces.Add(Apertured(surface) with { IsStop = false });
            origins.Add(new GhostSurfaceOrigin(original, pass));
        }

        // In: object to j − 1, unchanged.
        for (int k = 0; k < j; k++) Add(s[k], k, k == 0 ? GhostPass.End : GhostPass.In);
        origins[0] = new GhostSurfaceOrigin(0, GhostPass.End);

        // First bounce at j.
        double back = s[j - 1].Thickness;
        bool infiniteObject = j == 1 && double.IsPositiveInfinity(back);
        Add(s[j] with { IsMirror = true, Thickness = infiniteObject ? 0 : -back, Medium = media[j - 1] }, j, GhostPass.FirstBounce);

        int lowest = secondBounce is int i1 ? i1 + 1 : 1;
        // Back: j − 1 down to the second bounce + 1 (or to surface 1 for a single bounce).
        for (int k = j - 1; k >= lowest; k--)
        {
            double t = s[k - 1].Thickness;
            double thickness = double.IsPositiveInfinity(t) ? 0 : -t;
            Add(s[k] with { Thickness = thickness, Medium = media[k - 1] }, k, GhostPass.Back);
        }

        if (secondBounce is int i)
        {
            Add(s[i] with { IsMirror = true, Thickness = s[i].Thickness, Medium = media[i] }, i, GhostPass.SecondBounce);
            for (int k = i + 1; k < image; k++) Add(s[k], k, GhostPass.Out);
            Add(s[image], image, GhostPass.End);
        }
        else
        {
            // The ghost's image surface lies on the object surface (or surface 1's vertex plane).
            Add(new SequentialSurface { Comment = "Ghost image (object plane)" }, 0, GhostPass.End);
        }

        // The stop flag (see the class summary).
        int stopAt;
        if (stop > 0 && stop < j) stopAt = stop;
        else if (stop == j || secondBounce is null) stopAt = j;
        else
        {
            stopAt = -1;
            for (int g = 0; g < origins.Count; g++)
                if (origins[g].Original == stop && origins[g].Pass == GhostPass.Out) { stopAt = g; break; }
            if (stopAt < 0) stopAt = j;
        }
        surfaces[stopAt] = surfaces[stopAt] with { IsStop = true };
        surfaces[0] = s[0];

        var ghost = new SequentialLens { Surfaces = surfaces };
        var problems = ghost.Validate();
        if (problems.Count > 0)
            throw new ArgumentException($"Ghost focus: the ghost system {Describe(j, secondBounce)} is not a valid lens:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        return new GhostSystem { FirstBounce = j, SecondBounce = secondBounce, Lens = ghost, Origins = origins };
    }

    private static string Describe(int j, int? i) => i is int k ? $"{j} → {k}" : $"{j}";

    private static void CheckBounce(SequentialLens lens, int j, string which)
    {
        var s = lens.Surfaces[j];
        if (j == lens.ImageIndex) return;
        if (s.IsMirror)
            throw new ArgumentException($"Ghost focus: the {which} bounce surface {j} is a mirror; mirrors are not ghost bounce surfaces (help).");
        if (s.IsIgnored || s.IsCoordinateBreak)
            throw new ArgumentException($"Ghost focus: the {which} bounce surface {j} is {(s.IsIgnored ? "ignored" : "a coordinate break")}, so it cannot reflect.");
    }

    /// <summary>A copy without the automatic floating aperture (see the class summary).</summary>
    private static SequentialSurface Apertured(SequentialSurface s) =>
        s.Aperture is FloatingAperture && s.SemiDiameter is null ? s with { Aperture = null } : s;

    /// <summary>Glass in the sense of OpticStudio's coating layer-order rule (as OP-51's
    /// polarization trace reads it): anything but air of index 1.</summary>
    internal static bool IsGlass(ParaxialMedium m) => m.Material is not null || m.Index != 1.0;
}
