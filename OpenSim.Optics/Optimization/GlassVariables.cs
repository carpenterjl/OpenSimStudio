using OpenSim.Core.Model;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization;

// OP-59: how glass variables and glass substitution are flagged in the saved surfaces.

/// <summary>
/// The saved flags of glass optimization (plan item OP-59; docs/Optics-Conventions.md §41). The
/// Lens Data Editor's Material cell takes no Variable or Solve flag, so these flags name columns
/// of their own, which the editor keeps unchanged with the row (as it keeps every flag on a
/// column it has no cell for):
/// <list type="bullet">
/// <item><see cref="ModelNdColumn"/>, <see cref="ModelVdColumn"/>, <see cref="ModelDeltaPgFColumn"/>
/// flagged Variable on a surface with a model glass: the optimizer varies that parameter of the
/// model glass (OpticStudio's "Model" material solve, whose index, Abbe number and partial
/// dispersion "can then be optimized using the standard optimization methods", help: Material
/// Solves).</item>
/// <item><see cref="SubstituteColumn"/> flagged Variable on a surface with a glass: the glass
/// may be replaced by glass substitution (<see cref="GlassSubstitution"/>; OpticStudio's
/// "Substitute" material solve). The catalogs it draws from are the template's
/// (<see cref="OpticsGlassSubstitutionSettings.Catalogs"/>); a per-surface catalog is not saved.</item>
/// </list>
/// </summary>
public static class GlassVariables
{
    public const string ModelNdColumn = "ModelNd";
    public const string ModelVdColumn = "ModelVd";
    public const string ModelDeltaPgFColumn = "ModelDeltaPgF";
    public const string SubstituteColumn = "Substitute";

    /// <summary>The three model-glass columns.</summary>
    public static IReadOnlyList<string> ModelColumns { get; } = new[] { ModelNdColumn, ModelVdColumn, ModelDeltaPgFColumn };

    /// <summary>True when the surface carries a Variable flag on <paramref name="column"/>.</summary>
    public static bool IsFlagged(OpticsSurfaceSettings? s, string column) =>
        s?.Flags?.Any(f => f is not null && f.Column == column && f.Kind == SavedSurfaces.VariableFlag) == true;

    /// <summary>The surface with a Variable flag on <paramref name="column"/> set or cleared;
    /// every other flag kept in its order.</summary>
    public static OpticsSurfaceSettings WithFlag(OpticsSurfaceSettings s, string column, bool on)
    {
        ArgumentNullException.ThrowIfNull(s);
        var flags = (s.Flags ?? new List<OpticsCellFlagSettings>()).Where(f => f is not null && f.Column != column).ToList();
        if (on) flags.Add(new OpticsCellFlagSettings { Column = column, Kind = SavedSurfaces.VariableFlag });
        return s with { Flags = flags.Count > 0 ? flags : null };
    }

    /// <summary>The surfaces (0-based) flagged for glass substitution, in order.</summary>
    public static IReadOnlyList<int> SubstituteSurfaces(OpticsSettings optics)
    {
        ArgumentNullException.ThrowIfNull(optics);
        var surfaces = optics.Surfaces ?? new List<OpticsSurfaceSettings>();
        return Enumerable.Range(0, surfaces.Count).Where(j => IsFlagged(surfaces[j], SubstituteColumn)).ToArray();
    }

    /// <summary>The wavelengths of the system data, µm (the lens's, configuration as entered).</summary>
    public static IReadOnlyList<double> Wavelengths(OpticsSettings optics)
    {
        ArgumentNullException.ThrowIfNull(optics);
        var system = SystemDataSettings.FromSettings(optics.SystemData, new List<string>());
        return system.Wavelengths.Wavelengths.Select(w => w.Micrometers).ToArray();
    }
}
