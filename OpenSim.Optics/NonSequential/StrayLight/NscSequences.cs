using System.Globalization;
using System.Text.RegularExpressions;
using OpenSim.Core.Model;

namespace OpenSim.Optics.NonSequential.StrayLight;

/// <summary>
/// One sequence of the Sequence Selector (plan item OP-84; help, "Sequence Selector";
/// docs/Optics-Conventions.md §78): a filter name (SEQ# or a name of the user's) for a path.
/// "The SEQ5, or a given filter string will equate to X_SEQ(1.0, 3.1, ...)": the name used in a
/// filter string selects the branches whose path is <see cref="Path"/>. <see cref="Enabled"/> is
/// the list's check box ("Selecting the check box enables the sequence in supported analyses"),
/// kept with the project; filters by name work whether or not it is set.
/// </summary>
public sealed record NscSequence(string Name, NscPathSequence Path, bool Enabled = true);

/// <summary>The rules of sequence names and of the project's saved path filters and sequences.</summary>
public static class NscSequenceNames
{
    /// <summary>Why <paramref name="name"/> cannot name a sequence, or null. From the help
    /// ("Sequence Selector", Discussion): it "must start with a letter ... After the initial
    /// letter, the filter name can include letters, numbers, or underscores"; it cannot start with
    /// X, "Nor can they consist of a single uppercase letter followed by numbers (e.g. A1 or
    /// H123)", and "The name Z is also restricted". The default form SEQ&lt;n&gt; is accepted (the
    /// help allows it when reverting a renamed sequence; the original number is not tracked here,
    /// so any SEQ&lt;n&gt; is taken, duplicates refused). Filter flags are read without regard to
    /// case, so the single-letter rule is applied to lower case too, and a bare "SEQ" (the
    /// SEQ[a:b] flag) is refused.</summary>
    public static string? Problem(string? name)
    {
        string n = name ?? "";
        if (!Regex.IsMatch(n, "^[A-Za-z][A-Za-z0-9_]*$"))
            return $"the sequence name \"{n}\" must start with a letter and hold only letters, digits and underscores (help, \"Sequence Selector\").";
        if (n[0] is 'X' or 'x') return $"the sequence name \"{n}\" starts with X, which the help reserves for the extended filter flags.";
        if (Regex.IsMatch(n, "^[A-Za-z][0-9]+$")) return $"the sequence name \"{n}\" is a single letter followed by numbers, which reads as a filter flag.";
        if (n is "Z" or "z") return "the sequence name \"Z\" is reserved (the error flag).";
        if (string.Equals(n, "SEQ", StringComparison.OrdinalIgnoreCase)) return "the sequence name \"SEQ\" is the SEQ[a:b] flag.";
        return null;
    }

    /// <summary>True for the default form SEQ&lt;n&gt;; <paramref name="number"/> is n.</summary>
    public static bool IsDefault(string name, out int number)
    {
        number = 0;
        var m = Regex.Match(name ?? "", "^SEQ([1-9][0-9]*)$", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>"When a new sequence is generated, the next available # will be used to generate
    /// the SEQ# in numerical order. Once a sequence is deleted, the SEQ# filter becomes available
    /// for the next generated sequence": the smallest n ≥ 1 no sequence is named SEQn.</summary>
    public static string NextDefault(IEnumerable<NscSequence> existing)
    {
        var used = new HashSet<int>();
        foreach (var s in existing) if (IsDefault(s.Name, out int k)) used.Add(k);
        int n = 1;
        while (used.Contains(n)) n++;
        return "SEQ" + n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <paramref name="existing"/> with each path of <paramref name="paths"/> appended as a new
    /// SEQ# sequence, skipping a path already in the list ("duplicate sequences cannot be added
    /// to the list of sequences"). <paramref name="skipped"/> counts the duplicates.
    /// </summary>
    public static List<NscSequence> Append(IReadOnlyList<NscSequence> existing, IEnumerable<NscPathSequence> paths, out int skipped)
    {
        var list = existing.ToList();
        skipped = 0;
        foreach (var p in paths)
        {
            if (p.Overflow) { skipped++; continue; }
            if (list.Any(s => s.Path.SameAs(p))) { skipped++; continue; }
            list.Add(new NscSequence(NextDefault(list), p));
        }
        return list;
    }

    /// <summary>Checks a list of sequences: each name valid and unique (without regard to case),
    /// no path twice, no overflow path; throws <see cref="ArgumentException"/> naming the entry.</summary>
    public static void Validate(IReadOnlyList<NscSequence> sequences, IEnumerable<string>? namedFilters = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filters = new HashSet<string>(namedFilters ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sequences.Count; i++)
        {
            var s = sequences[i];
            if (Problem(s.Name) is { } p) throw new ArgumentException($"Sequence {i + 1}: {p}");
            if (!names.Add(s.Name)) throw new ArgumentException($"Sequence {i + 1}: the name \"{s.Name}\" is used twice.");
            if (filters.Contains(s.Name)) throw new ArgumentException($"Sequence {i + 1}: \"{s.Name}\" is also a named filter; rename one of them.");
            if (s.Path.Overflow) throw new ArgumentException($"Sequence {i + 1} (\"{s.Name}\"): the overflow path (more than {NscPathSequence.MaxEntries} entries) cannot be a sequence.");
            for (int j = 0; j < i; j++)
                if (sequences[j].Path.SameAs(s.Path))
                    throw new ArgumentException($"Sequence {i + 1} (\"{s.Name}\") repeats the path of sequence {j + 1} (\"{sequences[j].Name}\"); duplicate sequences cannot be added (help, \"Sequence Selector\").");
        }
    }

    // ------------------------------------------------------------------ the saved form

    /// <summary>The project's path filters (<see cref="OpticsNonSequentialSettings.PathFilters"/>,
    /// line k is _k); throws <see cref="FormatException"/> naming the line.</summary>
    public static IReadOnlyList<NscPathSequence> PathFilters(OpticsNonSequentialSettings? saved) =>
        (saved?.PathFilters ?? new List<string>()).Select((t, i) => NscPathSequence.Parse(t, $"Path filter _{i + 1}")).ToArray();

    /// <summary>The project's sequences (<see cref="OpticsNonSequentialSettings.Sequences"/>),
    /// validated; throws naming the entry.</summary>
    public static IReadOnlyList<NscSequence> Sequences(OpticsNonSequentialSettings? saved)
    {
        var list = new List<NscSequence>();
        int k = 0;
        foreach (var s in saved?.Sequences ?? new List<OpticsNscSequenceSettings>())
        {
            k++;
            string name = (s.Name ?? "").Trim();
            list.Add(new NscSequence(name, NscPathSequence.Parse(s.Sequence, $"Sequence {k} (\"{name}\")"), s.Enabled ?? true));
        }
        Validate(list);
        return list;
    }

    /// <summary>The saved form of path filters (null for none).</summary>
    public static List<string>? ToSettings(IReadOnlyList<NscPathSequence> paths) =>
        paths.Count == 0 ? null : paths.Select(p => p.Text).ToList();

    /// <summary>The saved form of sequences (null for none).</summary>
    public static List<OpticsNscSequenceSettings>? ToSettings(IReadOnlyList<NscSequence> sequences) =>
        sequences.Count == 0 ? null : sequences.Select(s => new OpticsNscSequenceSettings
        {
            Name = s.Name, Sequence = s.Path.Text, Enabled = s.Enabled ? null : false,
        }).ToList();

    /// <summary>Sequences as editor text, one per line: "name; sequence", with a leading "-" for
    /// a sequence whose check box is off.</summary>
    public static string Format(IReadOnlyList<NscSequence> sequences) =>
        string.Join(Environment.NewLine, sequences.Select(s => $"{(s.Enabled ? "" : "-")}{s.Name}; {s.Path.Text}"));

    /// <summary>Reads <see cref="Format"/>'s text (blank lines skipped); throws
    /// <see cref="FormatException"/> or <see cref="ArgumentException"/> naming the line.</summary>
    public static List<NscSequence> ParseText(string? text)
    {
        var list = new List<NscSequence>();
        int line = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            line++;
            string t = raw.Trim();
            if (t.Length == 0) continue;
            bool enabled = true;
            if (t.StartsWith('-')) { enabled = false; t = t[1..].TrimStart(); }
            int semi = t.IndexOf(';');
            if (semi < 0) throw new FormatException($"Sequence line {line} (\"{raw.Trim()}\"): the form is \"name; sequence\", for example \"SEQ1; 1.0, 3.1, 3.2, 4.0\".");
            string name = t[..semi].Trim();
            list.Add(new NscSequence(name, NscPathSequence.Parse(t[(semi + 1)..], $"Sequence line {line} (\"{name}\")"), enabled));
        }
        Validate(list);
        return list;
    }

    /// <summary>Path filters as editor text, line k for _k.</summary>
    public static string FormatPathFilters(IReadOnlyList<NscPathSequence> paths) =>
        string.Join(Environment.NewLine, paths.Select(p => p.Text));

    /// <summary>Reads <see cref="FormatPathFilters"/>'s text: every non-blank line is the next
    /// path filter (_1, _2, ...).</summary>
    public static List<NscPathSequence> ParsePathFilters(string? text)
    {
        var list = new List<NscPathSequence>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            list.Add(NscPathSequence.Parse(raw, $"Path filter _{list.Count + 1}"));
        }
        return list;
    }
}
