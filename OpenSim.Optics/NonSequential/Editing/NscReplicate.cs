using System.Globalization;
using OpenSim.Core.Model;

namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>The settings of OpticStudio's Replicate Object tool (help, "Replicate Object"):
/// the object, "the total number of objects in the array in the X, Y, and Z directions", "the
/// spacing interval along each direction" (lens units), and the two check boxes.</summary>
public sealed record NscReplicateSettings
{
    /// <summary>The object to replicate (1-based).</summary>
    public int Object { get; init; } = 1;

    public int NumberX { get; init; } = 1;
    public int NumberY { get; init; } = 1;
    public int NumberZ { get; init; } = 1;
    public double DeltaX { get; init; }
    public double DeltaY { get; init; }
    public double DeltaZ { get; init; }

    /// <summary>"If checked, all replicated objects will pickup parameter data from the original
    /// object." Not built: this editor has no solves or pickups on object cells.</summary>
    public bool AddPickupSolves { get; init; }

    /// <summary>"If checked, all replicated objects will use relative references to the original
    /// object, otherwise, absolute references are used."</summary>
    public bool RelativeReferences { get; init; }
}

/// <summary>
/// OpticStudio's Replicate Object (help, "Replicate Object"; plan item OP-71,
/// docs/Optics-Conventions.md §71). "The original object is placed at the center of the array,
/// and all replicated objects are referenced to the original object's position and orientation."
/// <para>
/// This program's reading of what the help leaves open: the array has N = Nx·Ny·Nz places at
/// offsets ((i − (Nx − 1)/2)·Δx, (j − (Ny − 1)/2)·Δy, (k − (Nz − 1)/2)·Δz) in the original's
/// frame; the original fills the centre place, so every count must be odd (an even count has no
/// centre place, and the help does not say where the original goes then: refused by name). The
/// N − 1 copies are inserted directly after the original, x varying fastest, then y, then z. Each
/// copy is an independent row: the original's type, parameters, material, faces, comment and
/// every other field, with its reference object set to the original (absolute, or relative when
/// asked), its position the offset and its tilts zero. A relative "Inside Of" is rewritten to name
/// the same object from the copy's own number. Objects after the original are renumbered as for
/// any insertion (<see cref="NscObjectTable"/>). The help offers no tilt between copies.
/// </para>
/// </summary>
public static class NscReplicate
{
    /// <summary>Why the settings cannot be used on <paramref name="objectCount"/> objects, or null.</summary>
    public static string? Check(NscReplicateSettings s, int objectCount)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Object < 1 || s.Object > objectCount)
            return $"Replicate Object: object {s.Object} does not exist (the list has {objectCount} objects).";
        if (s.AddPickupSolves)
            return "Replicate Object: \"Add Pickup Solves\" is not built; object cells take no solves or pickups in this editor, so the copies are independent rows.";
        foreach (var (n, axis) in new[] { (s.NumberX, "X"), (s.NumberY, "Y"), (s.NumberZ, "Z") })
        {
            if (n < 1) return $"Replicate Object: Number {axis} must be at least 1 (it is {n}).";
            if (n % 2 == 0)
                return $"Replicate Object: Number {axis} is {n}, an even count. The help places the original object at the centre of the array, which an even count leaves empty, and does not say where the original goes then; use an odd count.";
        }
        long total = (long)s.NumberX * s.NumberY * s.NumberZ;
        if (total < 2) return "Replicate Object: the array is the original alone; ask for more than one object.";
        if (total > 100_000) return $"Replicate Object: {total} objects is more than this editor builds at once (100000).";
        foreach (var (d, axis) in new[] { (s.DeltaX, "X"), (s.DeltaY, "Y"), (s.DeltaZ, "Z") })
            if (!double.IsFinite(d)) return $"Replicate Object: Delta {axis} must be a finite number.";
        return null;
    }

    /// <summary>The offsets of the copies in the original's frame (lens units), in insertion order.</summary>
    public static IReadOnlyList<(double X, double Y, double Z)> Offsets(NscReplicateSettings s)
    {
        var offsets = new List<(double, double, double)>();
        int cx = (s.NumberX - 1) / 2, cy = (s.NumberY - 1) / 2, cz = (s.NumberZ - 1) / 2;
        for (int k = 0; k < s.NumberZ; k++)
            for (int j = 0; j < s.NumberY; j++)
                for (int i = 0; i < s.NumberX; i++)
                {
                    if (i == cx && j == cy && k == cz) continue;
                    offsets.Add(((i - cx) * s.DeltaX, (j - cy) * s.DeltaY, (k - cz) * s.DeltaZ));
                }
        return offsets;
    }

    /// <summary>The copies of <paramref name="original"/> (object <paramref name="originalNumber"/>)
    /// as rows numbered from originalNumber + 1, in insertion order.</summary>
    public static List<OpticsNscObjectSettings> Copies(OpticsNscObjectSettings original, int originalNumber, NscReplicateSettings s)
    {
        ArgumentNullException.ThrowIfNull(original);
        var offsets = Offsets(s);
        var copies = new List<OpticsNscObjectSettings>(offsets.Count);
        int inside = original.InsideOf ?? 0;
        int insideTarget = NscScene.Resolve(inside, originalNumber);
        for (int k = 0; k < offsets.Count; k++)
        {
            int number = originalNumber + 1 + k;
            var (x, y, z) = offsets[k];
            copies.Add(original with
            {
                ReferenceObject = s.RelativeReferences ? originalNumber - number : originalNumber,
                InsideOf = inside < 0 ? insideTarget - number : original.InsideOf,
                X = x + 0.0, Y = y + 0.0, Z = z + 0.0,
                TiltX = 0, TiltY = 0, TiltZ = 0,
                Parameters = original.Parameters?.ToList(),
                Faces = original.Faces?.Select(f => f with { }).ToList(),
            });
        }
        return copies;
    }

    /// <summary>The list with the copies inserted after the original. Throws
    /// <see cref="ArgumentException"/> with <see cref="Check"/>'s message.</summary>
    public static List<OpticsNscObjectSettings> Apply(IReadOnlyList<OpticsNscObjectSettings> rows, NscReplicateSettings s)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (Check(s, rows.Count) is { } problem) throw new ArgumentException(problem);
        var original = rows[s.Object - 1] ?? new OpticsNscObjectSettings();
        return NscObjectTable.Insert(rows, s.Object, Copies(original, s.Object, s));
    }

    /// <summary>"Replicated object 3: 8 copies (3 × 3 × 1)."</summary>
    public static string Describe(NscReplicateSettings s) => string.Create(CultureInfo.InvariantCulture,
        $"Replicated object {s.Object}: {(long)s.NumberX * s.NumberY * s.NumberZ - 1} copies ({s.NumberX} × {s.NumberY} × {s.NumberZ}).");
}
