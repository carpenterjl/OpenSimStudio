using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization;

// OP-59: model glass to real glass.

/// <summary>The catalog glass nearest a model glass, how far it is, and which template step found it.</summary>
/// <param name="Note">Empty when the template (if any) was honoured in full; otherwise which part
/// of it was set aside, as the help's fallback order does.</param>
public sealed record NearestGlass(SubstitutionCandidate Glass, double Distance, string Note);

/// <summary>One model glass converted.</summary>
public sealed record ModelGlassConversionItem(int Surface, OpticsModelGlassSettings Model, NearestGlass Nearest);

/// <summary>The record with the model glasses replaced, and what was done.</summary>
public sealed record ModelGlassConversionResult(OpticsSettings Converted, IReadOnlyList<ModelGlassConversionItem> Items, IReadOnlyList<string> Log);

/// <summary>
/// Model glass to real glass (plan item OP-59; OpticStudio 2025 R2 help, "Optimizing Using
/// Model Glasses" and "Glass Substitution Template (global optimizers group)", saved under
/// <c>plans/sources/optics/op-59-help/</c>). The nearest catalog glass by the RGLA distance
/// with the partial-dispersion term omitted (<see cref="GlassDistance"/>, W_n = 1, W_a = 1E-04)
/// among the glasses substitution may choose (<see cref="GlassSubstitutionTemplate.Candidates"/>:
/// not excluded, not tabulated, covering the lens's wavelengths, and meeting the template). When
/// none qualifies the help's order is followed: "Exclude Glasses With No Data" is ignored, then
/// the template. The help's last step ("glass number 1 in the catalog, whatever properties it may
/// have, will be used") is not built: with no glass left the conversion fails, naming the
/// surface, rather than putting an arbitrary glass in the lens. Ties go to the first glass in
/// catalog order. The lens is not re-optimized here ("After converting from a model glass to a
/// real glass, another optimization run is generally required").
/// </summary>
public static class ModelGlassConversion
{
    /// <summary>The catalog glass nearest (<paramref name="nd"/>, <paramref name="vd"/>).
    /// Throws <see cref="InvalidOperationException"/> when no glass qualifies.</summary>
    public static NearestGlass Nearest(double nd, double vd, IReadOnlyList<OpticalCatalog> catalogs, IReadOnlyList<double> wavelengths,
        OpticsGlassSubstitutionSettings? template)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(wavelengths);
        var steps = new List<(bool? ExcludeIncomplete, bool UseTemplate, string Note)> { (null, true, "") };
        if (template is { UseTemplate: true })
        {
            if (template.ExcludeIncompleteData) steps.Add((false, true, "no glass met the template with incomplete data excluded, so glasses with incomplete data were allowed"));
            steps.Add((null, false, "no glass met the template, so it was ignored"));
        }
        var target = new GlassPoint(new OpticalMaterial { Name = "model" }, nd, vd, null, Computed: false);
        var skipped = new List<string>();
        foreach (var (exclude, use, note) in steps)
        {
            NearestGlass? best = null;
            foreach (var c in GlassSubstitutionTemplate.Candidates(catalogs, wavelengths, template, excludeIncomplete: exclude, useTemplate: use))
            {
                if (!GlassDistance.TryPoint(c.Glass, out var p, out string why)) { skipped.Add(why); continue; }
                double d = GlassDistance.Distance(target, p!, GlassDistance.DefaultIndexWeight, GlassDistance.DefaultAbbeWeight, 0)!.Value;
                if (best is null || d < best.Distance) best = new NearestGlass(c, d, note);
            }
            if (best is not null) return best;
        }
        throw new InvalidOperationException(
            $"No catalog glass can replace the model glass nd {F(nd)}, Vd {F(vd)}: no glass of the active catalogs ({string.Join(", ", catalogs.Select(c => c.Name))}) "
            + $"is free for substitution, untabulated and valid at every wavelength of the lens ({string.Join(", ", wavelengths.Select(F))} µm)"
            + (skipped.Count > 0 ? $"; {skipped.Distinct().Count()} had no nd and Vd to compare" : "") + ".");
    }

    /// <summary>The record with the model glasses of <paramref name="surfaces"/> (every model
    /// glass when null) replaced by their nearest catalog glasses, written "catalog:glass", and
    /// those surfaces' model-glass variable flags removed. Throws
    /// <see cref="InvalidOperationException"/> naming the surface when one cannot be converted,
    /// and <see cref="ArgumentException"/> when a listed surface has no model glass.</summary>
    public static ModelGlassConversionResult ConvertAll(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, IEnumerable<int>? surfaces = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        var list = (optics.Surfaces ?? new List<OpticsSurfaceSettings>()).ToList();
        var which = surfaces?.ToArray() ?? Enumerable.Range(0, list.Count).Where(j => list[j]?.ModelGlass is not null).ToArray();
        var wavelengths = GlassVariables.Wavelengths(optics);
        var items = new List<ModelGlassConversionItem>();
        var log = new List<string>();
        foreach (int j in which)
        {
            if (j < 0 || j >= list.Count || list[j]?.ModelGlass is not { } model)
                throw new ArgumentException($"{(j >= 0 && j < list.Count ? SavedSurfaces.Where(j, list.Count) : $"Surface {j}")} has no model glass to convert.");
            NearestGlass nearest;
            try { nearest = Nearest(model.Nd, model.Vd, catalogs, wavelengths, optics.GlassSubstitution); }
            catch (InvalidOperationException e) { throw new InvalidOperationException($"{SavedSurfaces.Where(j, list.Count)}: {e.Message}", e); }
            var s = list[j] with { Material = nearest.Glass.MaterialText, ModelGlass = null, Index = null };
            foreach (string column in GlassVariables.ModelColumns) s = GlassVariables.WithFlag(s, column, false);
            list[j] = s;
            items.Add(new ModelGlassConversionItem(j, model, nearest));
            log.Add($"{SavedSurfaces.Where(j, list.Count)}: model glass nd {F(model.Nd)}, Vd {F(model.Vd)} → {nearest.Glass.MaterialText} (distance {F(nearest.Distance)})"
                + (nearest.Note.Length > 0 ? $"; {nearest.Note}." : "."));
        }
        if (items.Count == 0) log.Add("There are no model glasses to convert.");
        return new ModelGlassConversionResult(optics with { Surfaces = list }, items, log);
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
