namespace OpenSim.Core.Model;

/// <summary>
/// The Optics workspace's inputs, saved with the project so an optical study re-opens the way
/// it was set up, and the whole content of a standalone lens file (<c>OpticsFileSerializer</c>).
/// Null on a project — every file written before the workspace existed — leaves the workspace
/// as it is. Results are not saved; they are recomputed.
/// <para>
/// The optical model grows here one nullable property per plan item, each added by the item
/// that defines its content, so any older file loads (null = what that item's default is):
/// <see cref="SystemData"/> (OP-18, OP-10), <see cref="Analyses"/> (OP-9) and
/// <see cref="Materials"/> (OP-11), <see cref="Coatings"/> (OP-15) and
/// <see cref="Surfaces"/> (OP-31) and <see cref="MultiConfiguration"/> (OP-30) now; the non-sequential objects (OP-70 on), the merit function
/// (OP-54) and tolerance data (OP-63) later. No placeholder is declared for those: their shape
/// is their item's to decide, and an empty schema written now would be a promise a later file
/// could not keep.
/// </para>
/// </summary>
public sealed record OpticsSettings
{
    /// <summary>Sequential or non-sequential, by the saved name OpenSim.Optics defines
    /// (<c>OpticsModes.SavedName</c>). A string, as the project's analysis type is, so a file
    /// from a later build with a mode this one does not know still loads. Null means the
    /// workspace's default.</summary>
    public string? Mode { get; init; }

    // ---- The optical model (OP-10). Later items add their nullable properties after these. ----

    /// <summary>The system data: aperture, fields, wavelengths, environment, units, title and
    /// notes (OP-18). Null — a file from OP-1's version 4 — means the default system.</summary>
    public OpticsSystemSettings? SystemData { get; init; }

    /// <summary>The analysis windows: which are open, their docking and their settings (OP-9).
    /// Null — every file written before the analysis framework — opens none.</summary>
    public OpticsAnalysesSettings? Analyses { get; init; }

    /// <summary>The material choices: the active glass catalogs (OP-11). Null — every file
    /// written before OP-11 — makes every loaded catalog active.</summary>
    public OpticsMaterialsSettings? Materials { get; init; }

    /// <summary>The thin-film coating catalog (OP-15). Null — every file written before OP-15 —
    /// is an empty catalog.</summary>
    public OpticsCoatingsSettings? Coatings { get; init; }

    // OP-31
    /// <summary>The sequential surfaces, object first and image last (OP-31's lens data editor).
    /// Null — every file written before OP-31 — is a system with no surfaces.</summary>
    public List<OpticsSurfaceSettings>? Surfaces { get; init; }

    // OP-54
    /// <summary>The merit function's operand rows (OP-54's Merit Function Editor). Null — every
    /// file written before OP-54 — is an empty merit function.</summary>
    public OpticsMeritFunctionSettings? MeritFunction { get; init; }

    // OP-30
    /// <summary>The multi-configuration table (OP-30). Null — every file written before OP-30,
    /// and every single-configuration lens — is one configuration: the system as entered.</summary>
    public OpticsMultiConfigSettings? MultiConfiguration { get; init; }

    // OP-63
    /// <summary>The tolerance data (OP-63's Tolerance Data Editor). Null — every file written
    /// before OP-63 — is an empty table.</summary>
    public OpticsToleranceSettings? Tolerances { get; init; }

    // OP-91
    /// <summary>The thermal analysis's own data: which glasses name a mechanical material for
    /// their expansion (OP-91, owner decision OQ-12). Null — every file written before OP-91 —
    /// links none, so every glass expands with its catalog TCE.</summary>
    public OpticsThermalSettings? Thermal { get; init; }

    // OP-59
    /// <summary>The Glass Substitution Template (OP-59): the limits on the glasses that glass
    /// substitution and the model-to-real conversion may choose. Null — every file written before
    /// OP-59 — is no template: any glass not excluded in its catalog.</summary>
    public OpticsGlassSubstitutionSettings? GlassSubstitution { get; init; }
}
