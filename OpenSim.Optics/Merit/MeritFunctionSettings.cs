using OpenSim.Core.Model;

namespace OpenSim.Optics.Merit;

/// <summary>
/// The merit function to and from the project's saved form (<see cref="OpticsMeritFunctionSettings"/>).
/// Rows are saved by OpticStudio's four-letter code. A code this build does not know becomes an
/// OOFF row, as OpticStudio's help says of OOFF ("only used to indicate that the merit function
/// operand type was not recognized", Merit Function Control Operands), and is named in the notes.
/// </summary>
public static class MeritFunctionSettings
{
    public static OpticsMeritFunctionSettings ToSettings(MeritFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new OpticsMeritFunctionSettings
        {
            Operands = function.Rows.Select(r => new OpticsMeritOperandSettings
            {
                Type = r.Code,
                Comment = r.Comment.Length == 0 ? null : r.Comment,
                Int1 = r.Int1,
                Int2 = r.Int2,
                Data = r.Data.All(x => x == 0) ? null : r.Data.ToList(),
                Target = r.Target,
                Weight = r.Weight,
            }).ToList(),
        };
    }

    /// <summary>The saved merit function; null is an empty one. Unknown codes become OOFF and
    /// real parameters past the sixth are dropped, each named in <paramref name="notes"/>.</summary>
    public static MeritFunction FromSettings(OpticsMeritFunctionSettings? saved, MeritOperandRegistry registry, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(notes);
        if (saved?.Operands is not { } operands) return new MeritFunction();
        var rows = new List<MeritOperandRow>(operands.Count);
        for (int i = 0; i < operands.Count; i++)
        {
            var s = operands[i] ?? new OpticsMeritOperandSettings();
            string code = (s.Type ?? "").Trim();
            var definition = registry.Find(code);
            if (definition is null)
            {
                // OP-55B: a not-built OpticStudio operand is named with what it waits for.
                notes.Add(Catalogue.MeritOperandCatalogue.IsNotBuilt(code)
                    ? $"Merit function row {i + 1}: {Catalogue.MeritOperandCatalogue.Unknown(code)} The row became OOFF (unused)."
                    : $"Merit function row {i + 1}: operand '{code}' is not one this version knows; the row became OOFF (unused).");
                code = "OOFF";
            }
            else code = definition.Code;
            var data = s.Data ?? new List<double>();
            if (data.Count > MeritOperandRow.DataCount)
            {
                if (data.Skip(MeritOperandRow.DataCount).Any(x => x != 0))
                    notes.Add($"Merit function row {i + 1}: only {MeritOperandRow.DataCount} real parameters are kept; the rest were dropped.");
                data = data.Take(MeritOperandRow.DataCount).ToList();
            }
            rows.Add(new MeritOperandRow
            {
                Code = code, Comment = s.Comment ?? "", Int1 = s.Int1, Int2 = s.Int2, Data = data, Target = s.Target, Weight = s.Weight,
            });
        }
        return new MeritFunction(rows);
    }
}
