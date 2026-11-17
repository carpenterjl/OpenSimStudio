using System.Globalization;
using System.Text;
using OpenSim.Optics.NonSequential.RayDatabase;

namespace OpenSim.Optics.NonSequential.StrayLight;

/// <summary>One entry of a path: an object and the face struck (null in a hand-written sequence
/// means any face, as X_SEQ's "n" without ".f").</summary>
public readonly record struct NscPathStep(int Object, int? Face)
{
    public override string ToString() =>
        Face is int f ? $"{Object.ToString(CultureInfo.InvariantCulture)}.{f.ToString(CultureInfo.InvariantCulture)}" : Object.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A path through a non-sequential system (plan item OP-84; docs/Optics-Conventions.md §78): the
/// ordered sequence of object faces a ray branch interacts with, written as OpticStudio's Path
/// Analysis reports it, "n.f format where n is the object number and f is the face number", the
/// source first with face 0 (help, "Path Analysis": "1.0, 2.1/(0,*), 2.2, 5.0, 4.0"). A branch's
/// path is its source object then the hit object and face of every segment that hit something,
/// in order: exactly the steps OP-83's X_SEQ compares (help, "The Filter String", X_SEQ), so a
/// path filter _n, a sequence SEQn and X_SEQ of the same entries select the same branches.
/// <para>
/// <see cref="Prefix"/> marks a path that is only the start of the branches it selects (the
/// ~ filter's trailing #): Path Analysis by detector groups each detector hit by the path from the
/// source to the segment that made it, and a transparent detector lets the branch go on.
/// <see cref="Overflow"/> is the catch-all of branches with more than <see cref="MaxEntries"/>
/// entries: "All paths with more than this number of object interactions are considered to have
/// the same path".
/// </para>
/// </summary>
public sealed record NscPathSequence
{
    /// <summary>"There is a maximum number of objects allowed in any one path sequence, and this
    /// limit is currently 100" (help, "Path Analysis"); the source entry counts.</summary>
    public const int MaxEntries = 100;

    public required IReadOnlyList<NscPathStep> Steps { get; init; }

    /// <summary>True when the path selects every branch that starts with it.</summary>
    public bool Prefix { get; init; }

    /// <summary>True for the one path of every branch with more than <see cref="MaxEntries"/> entries.</summary>
    public bool Overflow { get; init; }

    /// <summary>The overflow path.</summary>
    public static NscPathSequence OverflowPath { get; } = new() { Steps = Array.Empty<NscPathStep>(), Overflow = true };

    /// <summary>The steps of one branch (segment numbers from 0 to a terminating segment) of
    /// <paramref name="ray"/>: the source object with face 0, then each segment that hit an object.</summary>
    public static List<NscPathStep> StepsOf(NscRecordedRay ray, IReadOnlyList<int> branch)
    {
        ArgumentNullException.ThrowIfNull(ray);
        ArgumentNullException.ThrowIfNull(branch);
        var steps = new List<NscPathStep>(branch.Count) { new(ray.SourceObject, 0) };
        for (int i = 1; i < branch.Count; i++)
        {
            var s = ray.Segments[branch[i]];
            if (s.Hit > 0) steps.Add(new NscPathStep(s.Hit, s.Face));
        }
        return steps;
    }

    /// <summary>The path of a branch (the overflow path past <see cref="MaxEntries"/> entries).</summary>
    public static NscPathSequence Of(NscRecordedRay ray, IReadOnlyList<int> branch, bool prefix = false)
    {
        var steps = StepsOf(ray, branch);
        return steps.Count > MaxEntries ? OverflowPath : new NscPathSequence { Steps = steps, Prefix = prefix };
    }

    /// <summary>The segment numbers from 0 to segment <paramref name="segment"/> along the parents.</summary>
    public static int[] ChainTo(IReadOnlyList<NscSegment> segments, int segment)
    {
        var chain = new List<int>();
        for (int k = segment; k >= 0; k = segments[k].Parent)
        {
            chain.Add(k);
            if (chain.Count > segments.Count)
                throw new InvalidDataException($"Segment {segment}'s parent chain does not reach segment 0.");
        }
        chain.Reverse();
        return chain.ToArray();
    }

    /// <summary>Whether the branch's path is this one: the same entries in order (a step with no
    /// face matches any face), or for <see cref="Prefix"/> the branch's first entries; the
    /// overflow path matches every branch with more than <see cref="MaxEntries"/> entries.</summary>
    public bool Matches(NscRecordedRay ray, IReadOnlyList<int> branch)
    {
        var steps = StepsOf(ray, branch);
        if (Overflow) return steps.Count > MaxEntries;
        if (Prefix ? steps.Count < Steps.Count : steps.Count != Steps.Count) return false;
        for (int k = 0; k < Steps.Count; k++)
        {
            var want = Steps[k];
            if (steps[k].Object != want.Object) return false;
            if (want.Face is int f && steps[k].Face != f) return false;
        }
        return true;
    }

    /// <summary>The number of entries after the source (the objects struck).</summary>
    public int ObjectsStruck => Math.Max(0, Steps.Count - 1);

    /// <summary>The path as text: "1.0, 2.1, 2.2, 5.0, 4.0", with " #" after a prefix path;
    /// "> 100" for the overflow path. <see cref="Parse"/> reads it back.</summary>
    public string Text
    {
        get
        {
            if (Overflow) return $"> {MaxEntries}";
            var b = new StringBuilder();
            for (int i = 0; i < Steps.Count; i++)
            {
                if (i > 0) b.Append(", ");
                b.Append(Steps[i].ToString());
            }
            if (Prefix) b.Append(" #");
            return b.ToString();
        }
    }

    /// <summary>The equivalent X_SEQ filter (help, "The Filter String"), or null when there is
    /// none (a prefix or the overflow path).</summary>
    public string? XSeq => Overflow || Prefix ? null : "X_SEQ(" + string.Join(",", Steps.Select(s => s.ToString())) + ")";

    public override string ToString() => Text;

    /// <summary>
    /// Reads a path written as <see cref="Text"/>: entries "n.f" or "n" (any face) separated by
    /// commas, optionally wrapped in X_SEQ( ), optionally ending in "#" (a prefix); "> 100" is the
    /// overflow path. The first entry is the source. Diffraction orders ("/(2,*)") are refused:
    /// they are recorded only once diffraction is built (plan item OP-82). Throws
    /// <see cref="FormatException"/> naming <paramref name="where"/> and the entry.
    /// </summary>
    public static NscPathSequence Parse(string? text, string where = "Path")
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) throw new FormatException($"{where}: the sequence is empty.");
        if (t.Replace(" ", "", StringComparison.Ordinal) == ">" + MaxEntries.ToString(CultureInfo.InvariantCulture)) return OverflowPath;
        if (t.StartsWith("X_SEQ", StringComparison.OrdinalIgnoreCase))
        {
            int open = t.IndexOf('('), close = t.LastIndexOf(')');
            if (open < 0 || close < open) throw new FormatException($"{where}: \"{t}\" needs its entries in parentheses.");
            if (t[(close + 1)..].Trim().Length > 0) throw new FormatException($"{where}: unexpected text after X_SEQ(...): \"{t[(close + 1)..].Trim()}\".");
            t = t[(open + 1)..close];
        }
        bool prefix = false;
        if (t.EndsWith('#'))
        {
            prefix = true;
            t = t[..^1].TrimEnd().TrimEnd(',');
        }
        if (t.Contains('/'))
            throw new FormatException($"{where}: diffraction orders in a sequence (\"/(x,y)\") are recorded only once non-sequential diffraction is built (plan item OP-82).");
        var steps = new List<NscPathStep>();
        foreach (var raw in t.Split(','))
        {
            string e = raw.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(e, @"^(\d+)(?:\.(\d+))?$");
            if (!m.Success) throw new FormatException($"{where}: \"{e}\" is not an object number or object.face (entry {steps.Count + 1}).");
            int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int? f = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            if (steps.Count > 0 && n < 1) throw new FormatException($"{where}: entry {steps.Count + 1} names object 0; only the source entry may be 0 (unknown source).");
            steps.Add(new NscPathStep(n, f));
        }
        if (steps.Count > MaxEntries)
            throw new FormatException($"{where}: {steps.Count} entries; a path holds at most {MaxEntries} (help, \"Path Analysis\").");
        return new NscPathSequence { Steps = steps, Prefix = prefix };
    }

    /// <summary>True when the two paths select the same branches (same entries, faces, prefix and overflow flags).</summary>
    public bool SameAs(NscPathSequence other) =>
        other is not null && Overflow == other.Overflow && Prefix == other.Prefix && Steps.SequenceEqual(other.Steps);
}
