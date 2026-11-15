using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Solves;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: running a macro on a built lens (the ZPLM operand and the macro solve).

/// <summary>A built lens handed to a macro as the settings it works on.</summary>
internal static class ZplLensCopy
{
    /// <summary>
    /// The settings of <paramref name="lens"/> and <paramref name="system"/> in lens units
    /// (<see cref="SavedSurfaces.FromLens"/>, <see cref="SystemDataSettings.ToSettings"/>), and the
    /// glass catalogs to rebuild it: <paramref name="catalogs"/> followed by catalogs made of the
    /// lens's own glasses (so a glass the lens holds is found even when no catalogs are given).
    /// </summary>
    public static (OpticsSettings Settings, IReadOnlyList<OpticalCatalog> Catalogs) Of(SequentialLens lens, OpticalSystemData system,
        IReadOnlyList<OpticalCatalog>? catalogs)
    {
        var unit = system.Units.LensUnit;
        var settings = new OpticsSettings
        {
            Surfaces = SavedSurfaces.FromLens(lens, unit),
            SystemData = SystemDataSettings.ToSettings(system),
        };
        var own = lens.Surfaces.Select(s => s.Medium.Material).OfType<OpticalMaterial>()
            .GroupBy(g => g.Catalog, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OpticalCatalog { Name = g.Key.Length > 0 ? g.Key : "Lens", Materials = g.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray() });
        return (settings, (catalogs ?? Array.Empty<OpticalCatalog>()).Concat(own).ToArray());
    }
}

/// <summary>Evaluates <see cref="ZplMacroSolve"/> for the solve engine.</summary>
public static class ZplMacroSolves
{
    /// <summary>The solve's value in meters (1/m for a curvature): the macro's SOLVERETURN in
    /// lens units, converted. Throws <see cref="InvalidOperationException"/> naming the macro when
    /// it fails or returns nothing.</summary>
    public static double Evaluate(SequentialLens lens, OpticalSystemData system, ZplMacroSolve solve)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(solve);
        if (!ZplMacroSolve.Takes(solve.Target))
            throw new InvalidOperationException($"the ZPL macro solve on the {SolveColumns.Label(solve.Target)} is not built (curvature, thickness, semi-diameter and conic are).");
        string folder = ZplMacroFolder.Current;
        string path = Path.Combine(folder, solve.Macro);
        if (!File.Exists(path)) throw new InvalidOperationException($"the solve macro '{solve.Macro}' is not in the macro folder '{folder}'.");
        var (settings, catalogs) = ZplLensCopy.Of(lens, system, null);
        ZplRunResult result;
        try
        {
            result = ZplMacro.RunFile(path, new ZplRunOptions { System = settings, Catalogs = catalogs, MacroFolder = folder, SolveSurface = solve.Surface });
        }
        catch (ZplException e) { throw new InvalidOperationException($"the solve macro failed: {e.Message}", e); }
        double v = result.SolveReturn
            ?? throw new InvalidOperationException($"the solve macro {solve.Macro} returned no value (it did not call SOLVERETURN, which marks a solve that cannot be computed).");
        double perUnit = OpticsConventions.MetersPer(system.Units.LensUnit);
        return solve.Target switch
        {
            SolveColumn.Curvature => v / perUnit,
            SolveColumn.Thickness or SolveColumn.SemiDiameter => v * perUnit,
            _ => v,
        };
    }
}
