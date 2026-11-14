using OpenSim.Core.Model;

namespace OpenSim.Optics.Sequential.LensEditing;

/// <summary>
/// The bulk variable tools of OpticStudio's Optimize tab (OpticStudio 2025 R2 help, Optimize
/// tab › Automatic Optimization: Remove All Variables, Set All Radii Variable, Set All Thickness
/// Variable; plans/sources/optics/ansyshelp-op31/Remove_All_Variables.html,
/// Set_All_Radii_Variable.html, Set_All_Thickness_Variable.html). They change flags only; the
/// optimizer that reads them is OP-54.
/// </summary>
public static class VariableTools
{
    /// <summary>Remove All Variables: "Variables are removed by setting each of the values to
    /// 'fixed' at the current value." Solves and pickups are not variables and stay.</summary>
    public static LensEditResult RemoveAllVariables(IReadOnlyList<OpticsSurfaceSettings> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        int count = surfaces.Sum(s => s.Flags?.Count(f => SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag)) ?? 0);
        var result = surfaces.Select(s => SurfaceFlags.Without(s, f => SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag))).ToList();
        return new LensEditResult(result, new[] { $"{count} variable{(count == 1 ? "" : "s")} made fixed." });
    }

    /// <summary>
    /// Set All Radii Variable: "Variables are only set on curved surfaces that define the
    /// boundary between two different media." Read here as: surfaces 1 to the last before the
    /// image, Standard, not a plane, whose material differs from the one before it (MIRROR is a
    /// material, so a curved mirror in air counts). A radius that carries a solve or a pickup is
    /// left as it is, with a note.
    /// </summary>
    public static LensEditResult SetAllRadiiVariable(IReadOnlyList<OpticsSurfaceSettings> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        return SetVariable(surfaces, SavedSurfaces.RadiusColumn, "radius", "radii", j =>
            SavedSurfaces.IsStandard(surfaces[j]) && !SavedSurfaces.IsPlane(surfaces[j])
            && !SameMedium(surfaces[j - 1], surfaces[j]));
    }

    /// <summary>
    /// Set All Thickness Variable: "Variables are only set on finite non-zero thicknesses."
    /// Read here as surfaces 1 to the last before the image: the object distance is the
    /// conjugate, not a lens dimension, and the image surface's thickness is not used. A
    /// thickness that carries a solve or a pickup is left as it is, with a note.
    /// </summary>
    public static LensEditResult SetAllThicknessesVariable(IReadOnlyList<OpticsSurfaceSettings> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        return SetVariable(surfaces, SavedSurfaces.ThicknessColumn, "thickness", "thicknesses", j =>
            !surfaces[j].ThicknessIsInfinite && surfaces[j].Thickness != 0 && double.IsFinite(surfaces[j].Thickness));
    }

    private static LensEditResult SetVariable(IReadOnlyList<OpticsSurfaceSettings> surfaces, string column, string one, string many, Func<int, bool> eligible)
    {
        var result = surfaces.ToList();
        int set = 0, kept = 0;
        for (int j = 1; j < surfaces.Count - 1; j++)
        {
            if (!eligible(j)) continue;
            var flag = SurfaceFlags.Of(surfaces[j], column);
            if (flag is not null && !SurfaceFlags.IsKind(flag, SavedSurfaces.VariableFlag)) { kept++; continue; }
            if (flag is null) set++;
            result[j] = SurfaceFlags.With(surfaces[j], column, SurfaceFlags.Variable(column));
        }
        var notes = new List<string> { $"{set} {(set == 1 ? one : many)} made variable." };
        if (kept > 0) notes.Add($"{kept} {(kept == 1 ? one : many)} with a solve or pickup left as {(kept == 1 ? "it is" : "they are")}.");
        return new LensEditResult(result, notes);
    }

    /// <summary>True when two surfaces' materials are the same entry (name ignoring case, index or model glass).</summary>
    public static bool SameMedium(OpticsSurfaceSettings a, OpticsSurfaceSettings b) =>
        string.Equals((a.Material ?? "").Trim(), (b.Material ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
        && a.Index == b.Index && Equals(a.ModelGlass, b.ModelGlass);
}
