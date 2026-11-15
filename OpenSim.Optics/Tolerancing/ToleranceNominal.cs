using OpenSim.Core.Model;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Tolerancing;

/// <summary>
/// The Nominal column of the Tolerance Data Editor: the saved value a row tolerances, in the
/// row's unit, where it is one number of the saved lens (radius, curvature, thickness, conic,
/// fixed semi-diameter, parameter, index or Abbe number of a constant index or model glass, a
/// coordinate break's value, a multi-configuration value, a compensator's value). Null where
/// the row has none (decenters, tilts, irregularity: zero change from a nominal of zero) or the
/// numbers do not name one.
/// </summary>
public static class ToleranceNominal
{
    public static double? ValueOf(OpticsSettings lens, ToleranceOperandRow row)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(row);
        var surfaces = lens.Surfaces;
        OpticsSurfaceSettings? S(int j) => surfaces is not null && j >= 0 && j < surfaces.Count ? surfaces[j] : null;
        double? Radius(OpticsSurfaceSettings s) => SavedSurfaces.IsPlane(s) ? double.PositiveInfinity : s.Radius;
        double Curvature(OpticsSurfaceSettings s) => SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value;
        double? Thickness(OpticsSurfaceSettings s) => s.ThicknessIsInfinite ? double.PositiveInfinity : s.Thickness;
        switch (row.Code.ToUpperInvariant())
        {
            case "TRAD": return S(row.Int1) is { } a ? Radius(a) : null;
            case "TCUR": return S(row.Int1) is { } b ? Curvature(b) : null;
            case "TTHI": return S(row.Int1) is { } c ? Thickness(c) : null;
            case "TCON": return S(row.Int1)?.Conic;
            case "TSDI": return S(row.Int1)?.SemiDiameter;
            case "TPAR" or "CPAR": return S(row.Int1) is { } p && !SavedSurfaces.IsStandard(p) ? SavedSurfaces.Value(p, row.Int2) : null;
            case "TPAI": return S(row.Int1) is { } q && !SavedSurfaces.IsStandard(q) && SavedSurfaces.Value(q, row.Int2) is var v && v != 0 ? 1 / v : null;
            case "TIND": return S(row.Int1) is { } i ? i.ModelGlass?.Nd ?? (string.IsNullOrWhiteSpace(i.Material) ? i.Index : null) : null;
            case "TABB": return S(row.Int1)?.ModelGlass?.Vd;
            case "TUDX" or "TUDY" or "TUTX" or "TUTY" or "TUTZ":
            {
                int n = row.Code.ToUpperInvariant() switch { "TUDX" => 1, "TUDY" => 2, "TUTX" => 3, "TUTY" => 4, _ => 5 };
                return S(row.Int1) is { } cb && string.Equals(cb.Type, SurfaceTypes.CoordinateBreak, StringComparison.OrdinalIgnoreCase)
                    ? SavedSurfaces.Value(cb, n) : null;
            }
            case "COMP":
                return S(row.Int1) is { } k ? row.Int2 switch { 0 => Thickness(k), 1 => Curvature(k), 2 => k.Conic, _ => null } : null;
            case "TMCO" or "CMCO":
            {
                var rows = lens.MultiConfiguration?.Operands;
                if (rows is null || row.Int1 < 1 || row.Int1 > rows.Count) return null;
                var values = rows[row.Int1 - 1].Values;
                if (values is null || row.Int2 < 1) return null;
                if (row.Int2 > values.Count) return 0;
                var cell = values[row.Int2 - 1];
                return cell.Text is not null ? null : cell.IsInfinite ? double.PositiveInfinity : cell.Value;
            }
            default: return null;
        }
    }
}
