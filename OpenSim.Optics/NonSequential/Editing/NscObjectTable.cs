using OpenSim.Core.Model;

namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>An object's two links to other objects: its reference object and "Inside Of"
/// (0 none; positive absolute; negative relative, help "Object Placement" and "Placing Sources
/// Inside Objects").</summary>
public readonly record struct NscObjectLinks(int ReferenceObject, int InsideOf);

/// <summary>
/// Inserting, deleting and moving rows of the non-sequential object list (plan item OP-71,
/// docs/Optics-Conventions.md §71) with every reference object and "Inside Of" number kept
/// pointing at the same object.
/// <para>
/// OpticStudio's help defines the two forms ("Object Placement": a positive reference is
/// "absolute"; a negative one is "relative", found "by adding the current object number to the
/// negative reference object number"; "Placing Sources Inside Objects" the same for "Inside Of")
/// and says relative references "are particularly useful when copying and pasting groups of
/// objects". It does not say what inserting, deleting or moving a row does to the numbers. This
/// program's reading: each link keeps its form (absolute stays absolute, relative stays
/// relative) and is rewritten so that it names the same object afterwards; a pasted block keeps
/// its numbers as copied, which is what makes relative links useful there. An edit is refused,
/// naming the objects, when it would delete an object another one still refers to, or put a
/// referenced object after the object referring to it ("the reference object must precede in the
/// object list"). A link that was already broken before the edit is left as it was.
/// </para>
/// </summary>
public static class NscObjectTable
{
    /// <summary>
    /// The links after a structural edit: <paramref name="newIndexOfOld"/>[i] is where old row i
    /// goes (0-based), or −1 when it is deleted. Returns the new links per old row (null for a
    /// deleted row), or, in <paramref name="refusal"/>, why the edit cannot be made.
    /// </summary>
    public static NscObjectLinks?[] Remap(IReadOnlyList<NscObjectLinks> links, IReadOnlyList<int> newIndexOfOld, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(newIndexOfOld);
        if (links.Count != newIndexOfOld.Count) throw new ArgumentException("One new index per row is needed.", nameof(newIndexOfOld));
        var result = new NscObjectLinks?[links.Count];
        var problems = new List<string>();
        for (int i = 0; i < links.Count; i++)
        {
            int n = newIndexOfOld[i];
            if (n < 0) continue;
            int reference = One(links[i].ReferenceObject, i, n, "reference object");
            int inside = One(links[i].InsideOf, i, n, "\"Inside Of\"");
            result[i] = new NscObjectLinks(reference, inside);
        }
        refusal = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
        return result;

        int One(int value, int oldIndex, int newIndex, string what)
        {
            if (value == 0) return 0;
            int oldNumber = oldIndex + 1;
            int target = NscScene.Resolve(value, oldNumber);
            // Broken before the edit (no such object, or not earlier): left alone.
            if (target < 1 || target >= oldNumber || target > links.Count) return value;
            int m = newIndexOfOld[target - 1];
            if (m < 0)
            {
                problems.Add($"Object {oldNumber}: its {what} is object {target}, which would be deleted. Change object {oldNumber}'s {what} first (Modify Reference Object keeps its place).");
                return value;
            }
            if (m >= newIndex)
            {
                problems.Add($"Object {oldNumber}: its {what}, object {target}, would come after it (as object {m + 1}, it as object {newIndex + 1}); a referenced object must precede the object that refers to it.");
                return value;
            }
            return value > 0 ? m + 1 : m - newIndex;
        }
    }

    /// <summary>Inserts <paramref name="inserted"/> before row <paramref name="index"/> (0-based;
    /// the count appends). The inserted rows are taken as they are: their links are already in
    /// terms of their own new numbers.</summary>
    public static List<OpticsNscObjectSettings> Insert(IReadOnlyList<OpticsNscObjectSettings> rows, int index, IReadOnlyList<OpticsNscObjectSettings> inserted)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(inserted);
        if (index < 0 || index > rows.Count) throw new ArgumentOutOfRangeException(nameof(index), index, $"The list has {rows.Count} objects.");
        var map = Enumerable.Range(0, rows.Count).Select(i => i < index ? i : i + inserted.Count).ToArray();
        var result = Apply(rows, map, rows.Count + inserted.Count);
        for (int k = 0; k < inserted.Count; k++) result[index + k] = inserted[k] ?? new OpticsNscObjectSettings();
        return result!;
    }

    /// <summary>Deletes the rows at <paramref name="indices"/> (0-based). Throws
    /// <see cref="InvalidOperationException"/>, naming the objects, when another object refers to
    /// a deleted one.</summary>
    public static List<OpticsNscObjectSettings> Delete(IReadOnlyList<OpticsNscObjectSettings> rows, IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var gone = indices.ToHashSet();
        foreach (int i in gone)
            if (i < 0 || i >= rows.Count) throw new ArgumentOutOfRangeException(nameof(indices), i, $"The list has {rows.Count} objects.");
        var map = new int[rows.Count];
        int next = 0;
        for (int i = 0; i < rows.Count; i++) map[i] = gone.Contains(i) ? -1 : next++;
        return Apply(rows, map, next)!;
    }

    /// <summary>Moves the rows at <paramref name="indices"/> by <paramref name="offset"/> places
    /// (negative is up), keeping their order; the rows between close up. Throws
    /// <see cref="InvalidOperationException"/> when a referenced object would follow its user.</summary>
    public static List<OpticsNscObjectSettings> Move(IReadOnlyList<OpticsNscObjectSettings> rows, IEnumerable<int> indices, int offset)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var selected = indices.Distinct().OrderBy(i => i).ToArray();
        if (selected.Length == 0) return rows.ToList();
        if (selected[0] + offset < 0 || selected[^1] + offset >= rows.Count)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The rows would leave the list.");
        var order = Enumerable.Range(0, rows.Count).ToArray();
        var positions = (int[])selected.Clone();
        for (int step = 0; step < Math.Abs(offset); step++)
        {
            if (offset < 0)
                for (int k = 0; k < positions.Length; k++) { (order[positions[k] - 1], order[positions[k]]) = (order[positions[k]], order[positions[k] - 1]); positions[k]--; }
            else
                for (int k = positions.Length - 1; k >= 0; k--) { (order[positions[k]], order[positions[k] + 1]) = (order[positions[k] + 1], order[positions[k]]); positions[k]++; }
        }
        var map = new int[rows.Count];
        for (int newIndex = 0; newIndex < order.Length; newIndex++) map[order[newIndex]] = newIndex;
        return Apply(rows, map, rows.Count)!;
    }

    /// <summary>The links of every row as saved (null is 0).</summary>
    public static NscObjectLinks[] LinksOf(IReadOnlyList<OpticsNscObjectSettings> rows) =>
        rows.Select(r => new NscObjectLinks(r?.ReferenceObject ?? 0, r?.InsideOf ?? 0)).ToArray();

    private static List<OpticsNscObjectSettings?> Apply(IReadOnlyList<OpticsNscObjectSettings> rows, int[] map, int newCount)
    {
        var links = Remap(LinksOf(rows), map, out string? refusal);
        if (refusal is not null) throw new InvalidOperationException(refusal);
        var result = new List<OpticsNscObjectSettings?>(new OpticsNscObjectSettings?[newCount]);
        for (int i = 0; i < rows.Count; i++)
        {
            if (map[i] < 0) continue;
            var r = rows[i] ?? new OpticsNscObjectSettings();
            var l = links[i]!.Value;
            result[map[i]] = r with
            {
                ReferenceObject = l.ReferenceObject == (r.ReferenceObject ?? 0) ? r.ReferenceObject : l.ReferenceObject,
                InsideOf = l.InsideOf == (r.InsideOf ?? 0) ? r.InsideOf : l.InsideOf,
            };
        }
        return result;
    }
}
