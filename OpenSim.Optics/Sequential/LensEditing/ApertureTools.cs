using OpenSim.Core.Model;

namespace OpenSim.Optics.Sequential.LensEditing;

/// <summary>
/// The Apertures menu of OpticStudio's Lens Data Editor toolbar (OpticStudio 2025 R2 help,
/// Lens Data Editor Toolbar › Apertures; plans/sources/optics/ansyshelp-op31/Remove_All_Apertures.html,
/// Convert_Semi_Diameters_to_Circular_Apertures.html, …_Floating_Apertures.html,
/// …_Maximum_Apertures.html). Every tool works on surfaces 1 to the image; the object surface
/// cannot have an aperture (section 13).
/// </summary>
public static class ApertureTools
{
    /// <summary>
    /// Remove All Apertures: "All surface aperture types are set to 'none'". The help adds that
    /// OpticStudio puts floating apertures on powered surfaces with a user-defined
    /// semi-diameter and these cannot be deleted; this program adds no aperture of its own
    /// (a surface without one never vignettes, section 13), so after this tool nothing
    /// vignettes.
    /// </summary>
    public static LensEditResult RemoveAll(IReadOnlyList<OpticsSurfaceSettings> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        int count = surfaces.Count(s => s.Aperture is not null);
        return new LensEditResult(surfaces.Select(s => s with { Aperture = null }).ToList(),
            new[] { $"{count} aperture{(count == 1 ? "" : "s")} removed." });
    }

    /// <summary>
    /// Convert Semi-Diameters to Circular Apertures: every surface without an aperture gets a
    /// fixed clear semi-diameter and a circular aperture of that radius; so does a surface with
    /// a floating aperture whose semi-diameter is picked up (the help's two cases). Automatic
    /// semi-diameters take the value <see cref="SemiDiameterSizing"/> computes. A surface whose
    /// semi-diameter is zero cannot have a circular aperture and is left, with a note.
    /// </summary>
    public static LensEditResult ToCircular(IReadOnlyList<OpticsSurfaceSettings> surfaces, LensEditContext context)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        var clear = ClearSemiDiameters(surfaces, context);
        var result = surfaces.ToList();
        int converted = 0;
        var notes = new List<string>();
        for (int j = 1; j < surfaces.Count; j++)
        {
            var s = surfaces[j];
            bool pickedFloating = s.Aperture?.Type == SavedSurfaces.FloatingAperture
                && SurfaceFlags.Of(s, SavedSurfaces.SemiDiameterColumn) is { } f && SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag);
            if (s.Aperture is not null && !pickedFloating) continue;
            double sd = clear[j];
            if (!(sd > 0)) { notes.Add($"Surface {j}: its semi-diameter is zero, so it gets no circular aperture."); continue; }
            var t = SurfaceFlags.With(s, SavedSurfaces.SemiDiameterColumn, null) with
            {
                SemiDiameter = sd,
                Aperture = new OpticsSurfaceApertureSettings { Type = SavedSurfaces.CircularAperture, MinRadius = 0, MaxRadius = sd }
            };
            result[j] = t;
            converted++;
        }
        notes.Insert(0, $"{converted} surface{(converted == 1 ? "" : "s")} given a fixed semi-diameter and a circular aperture.");
        return new LensEditResult(result, notes);
    }

    /// <summary>
    /// Convert Semi-Diameters to Floating Apertures: every surface without an aperture gets a
    /// floating aperture, which vignettes at its clear semi-diameter; automatic semi-diameters
    /// stay automatic and fixed ones fixed, as the help says.
    /// </summary>
    public static LensEditResult ToFloating(IReadOnlyList<OpticsSurfaceSettings> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        var result = surfaces.ToList();
        int converted = 0;
        for (int j = 1; j < surfaces.Count; j++)
        {
            if (surfaces[j].Aperture is not null) continue;
            result[j] = surfaces[j] with { Aperture = new OpticsSurfaceApertureSettings { Type = SavedSurfaces.FloatingAperture } };
            converted++;
        }
        return new LensEditResult(result, new[] { $"{converted} surface{(converted == 1 ? "" : "s")} given a floating aperture." });
    }

    /// <summary>
    /// Convert Semi-Diameters to Maximum Apertures: every surface without an aperture gets the
    /// clear semi-diameter needed to pass all rays, with OpticStudio's "Maximum" semi-diameter
    /// solve stored on the cell. The help takes the maximum over all configurations;
    /// configurations are not built (OP-30), so it is the one configuration's automatic size
    /// (<see cref="SemiDiameterSizing"/>, computed with those surfaces' semi-diameters made
    /// automatic), stored as the fixed value the trace reads.
    /// </summary>
    public static LensEditResult ToMaximum(IReadOnlyList<OpticsSurfaceSettings> surfaces, LensEditContext context)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        var sizing = surfaces.Select((s, j) => j > 0 && s.Aperture is null ? s with { SemiDiameter = null } : s).ToList();
        var clear = ClearSemiDiameters(sizing, context);
        var result = surfaces.ToList();
        int converted = 0;
        for (int j = 1; j < surfaces.Count; j++)
        {
            if (surfaces[j].Aperture is not null) continue;
            result[j] = SurfaceFlags.With(surfaces[j] with { SemiDiameter = clear[j] }, SavedSurfaces.SemiDiameterColumn,
                new OpticsCellFlagSettings { Kind = SavedSurfaces.SolveFlag, Solve = MaximumSolve });
            converted++;
        }
        return new LensEditResult(result, new[] { $"{converted} surface{(converted == 1 ? "" : "s")} given the maximum semi-diameter (one configuration)." });
    }

    /// <summary>The key of the clear semi-diameter's "Maximum" solve.</summary>
    public const string MaximumSolve = "Maximum";

    /// <summary>The clear semi-diameter of each surface in lens units: fixed ones as given,
    /// automatic ones sized by <see cref="SemiDiameterSizing"/>.</summary>
    public static double[] ClearSemiDiameters(IReadOnlyList<OpticsSurfaceSettings> surfaces, LensEditContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lens = context.ToLens(surfaces);
        var report = SemiDiameterSizing.Compute(lens, context.System());
        double perUnit = OpticsConventions.MetersPer(context.Unit);
        return report.Clear.Select(c => c / perUnit).ToArray();
    }
}
