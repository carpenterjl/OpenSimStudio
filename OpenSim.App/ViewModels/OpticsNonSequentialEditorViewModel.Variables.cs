// OP-88
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Core.Model;
using S = OpenSim.App.ViewModels.OpticsNonSequentialEditorSchema;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Variables on object cells (plan item OP-88; docs/Optics-Conventions.md §76): the X, Y, Z,
/// tilt and parameter cells of the Non-Sequential Component Editor take OpticStudio's Variable
/// flag (the grid's flag editor, or Ctrl+Z on a cell), saved as
/// <see cref="OpticsNscObjectSettings.Flags"/> and read by the optimizer
/// (<c>OpenSim.Optics.Optimization.NscVariables</c>). The optimizer's result comes back through
/// <see cref="ApplyObjectValues"/> as one undo step; the Bitmap Wizard's Resample Detector
/// through <see cref="SetDetectorPixels"/>.
/// </summary>
public sealed partial class OpticsNonSequentialEditorViewModel
{
    /// <summary>The row's flags as saved: the saved list when the Variable cells are the same
    /// ones; else the saved non-Variable entries (kept for the file) and the Variable cells in
    /// column order. Null when there are none and none were saved.</summary>
    private static List<OpticsCellFlagSettings>? ReadFlags(SheetRow row, List<OpticsCellFlagSettings>? saved)
    {
        var cells = new List<string>();
        for (int slot = S.XSlot; slot < row.Cells.Count; slot++)
            if (row.Cells[slot].Flag.Kind == CellFlagKind.Variable && S.FlagColumn(slot) is { } column) cells.Add(column);
        var savedVariables = (saved ?? new List<OpticsCellFlagSettings>())
            .Where(f => f is not null && f.Kind == "Variable" && S.FlagSlot(f.Column) is int slot && slot < row.Cells.Count)
            .Select(f => f.Column!).ToList();
        if (cells.ToHashSet().SetEquals(savedVariables) && cells.Count == savedVariables.Count) return saved;
        var kept = (saved ?? new List<OpticsCellFlagSettings>())
            .Where(f => f is not null && !(f.Kind == "Variable" && S.FlagSlot(f.Column) is int slot && slot < row.Cells.Count)).ToList();
        kept.AddRange(cells.Select(c => new OpticsCellFlagSettings { Column = c, Kind = "Variable" }));
        return kept.Count == 0 && saved is null ? null : kept;
    }

    /// <summary>Marks a saved row's Variable cells on sheet row <paramref name="index"/>; a flag
    /// on a cell that takes none is named in <paramref name="notes"/> and kept as saved.</summary>
    private void WriteFlags(int index, OpticsNscObjectSettings s, string where, ICollection<string> notes)
    {
        foreach (var f in s.Flags ?? new List<OpticsCellFlagSettings>())
        {
            if (f is null) continue;
            if (f.Kind != "Variable")
            {
                notes.Add($"{where}, {f.Column}: the flag '{f.Kind}' is not built on object cells; it is kept as saved and not shown.");
                continue;
            }
            if (S.FlagSlot(f.Column) is not int slot || slot >= Document.Rows[index].Cells.Count)
            {
                notes.Add($"{where}: the Variable flag on '{f.Column}' names no cell of this type; it is kept as saved.");
                continue;
            }
            var result = Document.SetFlag(index, slot + 1, CellFlag.Variable);
            if (!result.Succeeded) notes.Add($"{where}, {f.Column}: {result.Message}");
        }
    }

    /// <summary>Writes the X, Y, Z, tilt and parameter values of <paramref name="rows"/> that
    /// differ from the table's (the optimizer's result) as one undo step named
    /// <paramref name="description"/>; returns why it was refused, or null.</summary>
    public string? ApplyObjectValues(IReadOnlyList<OpticsNscObjectSettings> rows, string description)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var now = Rows();
        if (rows.Count != now.Count)
            return $"The non-sequential objects have changed since the optimization ran ({now.Count} objects now, {rows.Count} then); nothing was written.";
        for (int i = 0; i < rows.Count; i++)
            if (rows[i] is null || !string.Equals(rows[i].Type, now[i].Type, StringComparison.OrdinalIgnoreCase))
                return $"Object {i + 1} has changed type since the optimization ran; nothing was written.";
        var doc = Document;
        bool changed = false;
        _quiet = true;
        try
        {
            using var group = doc.History.BeginGroup(description);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var n = now[i];
                void One(int slot, double? value, double? current)
                {
                    if ((value ?? 0).Equals(current ?? 0)) return;
                    SetNumber(i, slot, value ?? 0);
                    changed = true;
                }
                One(S.XSlot, r.X, n.X); One(S.YSlot, r.Y, n.Y); One(S.ZSlot, r.Z, n.Z);
                One(S.TiltXSlot, r.TiltX, n.TiltX); One(S.TiltYSlot, r.TiltY, n.TiltY); One(S.TiltZSlot, r.TiltZ, n.TiltZ);
                var p = r.Parameters ?? new List<double>();
                var q = n.Parameters ?? new List<double>();
                int cells = doc.Rows[i].Cells.Count;
                for (int k = 0; k < p.Count && S.FirstParameterSlot + k < cells && k < S.ParameterSlots; k++)
                    One(S.FirstParameterSlot + k, p[k], k < q.Count ? q[k] : null);
            }
        }
        catch (InvalidOperationException e) { return e.Message; }
        finally { _quiet = false; }
        if (changed) AfterChange(publish: true);
        return null;
    }

    /// <summary>Sets detector object <paramref name="number"/>'s # X Pixels and # Y Pixels
    /// (parameters 3 and 4) as one undo step (the Bitmap Wizard's Resample Detector); returns
    /// why it was refused, or null.</summary>
    public string? SetDetectorPixels(int number, int x, int y)
    {
        if (number < 1 || number > Document.RowCount) return $"There is no object {number}.";
        var row = ReadRow(Document.Rows[number - 1]);
        if (!OpenSim.Optics.NonSequential.Detectors.NscDetectorTypes.IsDetectorType((row.Type ?? "").Trim()) || row.Type == OpenSim.Optics.NonSequential.Detectors.NscDetectorTypes.Polar)
            return $"Object {number} ({row.Type}) is not a Detector Rectangle or Color.";
        var doc = Document;
        _quiet = true;
        try
        {
            using var group = doc.History.BeginGroup("Resample detector");
            SetNumber(number - 1, S.FirstParameterSlot + 2, x);
            SetNumber(number - 1, S.FirstParameterSlot + 3, y);
        }
        catch (InvalidOperationException e) { return e.Message; }
        finally { _quiet = false; }
        AfterChange(publish: true);
        return null;
    }
}
