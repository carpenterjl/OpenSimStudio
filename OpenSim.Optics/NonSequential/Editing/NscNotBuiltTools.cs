namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>
/// The tools of OpticStudio's NSC Editor toolbar (help, "NSC Editor Toolbar") that this build
/// does not have, each refused with the reason and the plan item that would make it possible
/// (plan item OP-71, docs/Optics-Conventions.md §71). The descriptions are from the tools' help
/// pages in <c>plans/sources/optics/op-71-help/</c>.
/// </summary>
public static class NscNotBuiltTools
{
    /// <summary>Combine Objects applies a Boolean operation to two objects and writes the result
    /// as a CAD file (IGES, STEP or STL) that is imported back as a CAD part (help, "Combine
    /// Objects"). None of OP-70's types can hold the result.</summary>
    public const string CombineObjects =
        "Combine Objects is not built. It writes the Boolean result of two objects as a CAD file and imports it as a CAD part (help, \"Combine Objects\"); Boolean objects are plan item OP-74 and imported CAD parts OP-75.";

    /// <summary>Create Polygon Object writes a POB file for the Polygon Object type (help,
    /// "Create Polygon Object").</summary>
    public const string CreatePolygonObject =
        "Create Polygon Object is not built. It writes a POB file for the Polygon Object type (help, \"Create Polygon Object\"), which is plan item OP-74.";

    /// <summary>Freeform Z Tools edit the Freeform Z object (help, "Freeform Z Tools").</summary>
    public const string FreeformZTools =
        "Freeform Z Tools are not built: they edit the Freeform Z object, which is part of the object library (plan item OP-72).";

    /// <summary>The Object Editor ("Edit an Object in a CAD-like environment").</summary>
    public const string ObjectEditor =
        "The Object Editor (editing an object in a CAD-like environment) is not built; objects are edited in the table and the properties.";

    /// <summary>The Object Properties sections this build does not have, by name, with what
    /// builds them (help, "Object Properties (non-sequential component editor)").</summary>
    public static IReadOnlyList<(string Section, string Reason)> PropertySections { get; } = new[]
    {
        ("Sources", "the Sources tab has no fields in the editor yet; it is saved with the object, read by the trace and set by the macro codes 101 to 166 of SETNSCPROPERTY"),
        // OP-80: Scatter and Scatter To are built (the editor's Coat/Scatter and Scatter To fields).
        ("Scatter profiles and User Defined scatter (on the Coat/Scatter section)", "scatter profiles (SCATTER_PROFILE.DAT, undocumented), scatter DLLs and the IS/ISX scatter catalogs are not built; the other scatter models are (OP-80)"),
        ("Volume Physics", "bulk scatter, wavelength shift and fluorescence are plan item OP-81"),
        ("Index (GRIN types)", "non-sequential gradient index is plan item OP-82"),
        ("Index (birefringent types)", "non-sequential birefringence is not built (no plan item names it)"),
        ("Diffraction", "non-sequential diffraction is plan item OP-82"),
        ("CAD", "CAD parts are plan item OP-75"),
        ("Detector settings", "the detector settings (pixel interpolation, coherent power normalization, spectral bins) have no fields in the editor yet; they are saved with the object and read by the trace"),
        ("Draw (on layout plots)", "there is no non-sequential layout plot yet; the single-object viewer takes its own resolution and opacity"),
    };

    /// <summary>One line per section of <see cref="PropertySections"/>.</summary>
    public static string PropertySectionsText =>
        "Not built: " + string.Join("; ", PropertySections.Select(s => $"{s.Section} ({s.Reason})")) + ".";
}
