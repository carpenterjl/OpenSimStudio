using System.Text.RegularExpressions;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// The import pipeline's diagnostic sink, split by severity. A <b>Warning</b> means
/// something the file DECLARES was skipped or approximated — a conforming file should
/// import with zero warnings. A <b>Note</b> is informational: data genuinely absent from
/// the file with defaults applied (a missing Stackup section), physically-empty
/// declarations (zero-width strokes deposit no copper), and summaries/timings.
///
/// <see cref="SealedWarnings"/>/<see cref="SealedNotes"/> collapse repeats: real boards
/// repeat one problem tens of thousands of times (e.g. an unsupported feature on every
/// trace of a layer), and 30k identical log lines help nobody. Messages differing only
/// in their "(line N)" position collapse together — the first occurrence's line number
/// is kept as the representative and the count is appended as " (×N)". Determinism:
/// the parser is single-threaded and the builder appends only in its sequential
/// assembly loop, so insertion order — and therefore the sealed output — is stable.
/// </summary>
public sealed class Ipc2581Diagnostics
{
    private readonly List<string> _warnings = new();
    private readonly List<string> _notes = new();

    private static readonly Regex LinePosition = new(@" \(line \d+\)", RegexOptions.Compiled);

    /// <summary>Something the file declares was skipped or approximated.</summary>
    public void Warn(string message) => _warnings.Add(message);

    /// <summary>Informational: absent data defaulted, empty geometry, summaries.</summary>
    public void Note(string message) => _notes.Add(message);

    /// <summary>Raw warning count before collapsing (for tests and thresholds).</summary>
    public int WarningCount => _warnings.Count;

    public IReadOnlyList<string> SealedWarnings() => Seal(_warnings);

    public IReadOnlyList<string> SealedNotes() => Seal(_notes);

    /// <summary>Collapses messages identical up to their "(line N)" position into the
    /// first occurrence + " (×N)", preserving first-occurrence order.</summary>
    private static IReadOnlyList<string> Seal(List<string> messages)
    {
        var counts = new Dictionary<string, (string First, int Count)>();
        var order = new List<string>();
        foreach (var message in messages)
        {
            string key = LinePosition.Replace(message, "");
            if (counts.TryGetValue(key, out var entry))
                counts[key] = (entry.First, entry.Count + 1);
            else
            {
                counts[key] = (message, 1);
                order.Add(key);
            }
        }
        return order
            .Select(key => counts[key] is { Count: > 1 } e ? $"{e.First} (×{e.Count})" : counts[key].First)
            .ToList();
    }
}
