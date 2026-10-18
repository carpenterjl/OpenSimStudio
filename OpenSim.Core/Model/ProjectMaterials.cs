using System.Globalization;

namespace OpenSim.Core.Model;

/// <summary>
/// A project file carries each body's whole material, and the library may hold a material of
/// the same name with other values — a file saved before a library correction, or a library
/// edited since. The project's values are the ones its results were computed with, so a load
/// or a solve keeps them; this names what differs so the choice is never silent (evaluation
/// D24 — the library's copy used to replace the project's without a word).
/// </summary>
public static class MaterialDivergence
{
    /// <summary>Whether two materials carry the same values. <see cref="Material.IsBuiltIn"/>
    /// is the library's bookkeeping, not a property, and is ignored.</summary>
    public static bool SameValues(Material a, Material b) => a with { IsBuiltIn = b.IsBuiltIn } == b;

    /// <summary>The properties in which the project's material differs from the library's,
    /// each as "property project vs library unit"; empty when the values agree.</summary>
    public static IReadOnlyList<string> Differences(Material project, Material library)
    {
        var differences = new List<string>();
        void Compare(string property, double? ours, double? theirs, string unit)
        {
            if (ours == theirs) return;
            var (a, b) = (Format(ours, "G6"), Format(theirs, "G6"));
            // Six figures can print two different numbers alike; then show them in full.
            if (a == b) (a, b) = (Format(ours, "R"), Format(theirs, "R"));
            differences.Add($"{property} {a} vs {b}{unit}");
        }
        Compare("Young's modulus", project.YoungsModulus, library.YoungsModulus, " Pa");
        Compare("Poisson's ratio", project.PoissonRatio, library.PoissonRatio, "");
        Compare("density", project.Density, library.Density, " kg/m³");
        Compare("thermal conductivity", project.ThermalConductivity, library.ThermalConductivity, " W/(m·K)");
        Compare("specific heat", project.SpecificHeat, library.SpecificHeat, " J/(kg·K)");
        Compare("electrical conductivity", project.ElectricalConductivity, library.ElectricalConductivity, " S/m");
        Compare("resistivity temperature coefficient", project.ResistivityTemperatureCoefficient,
            library.ResistivityTemperatureCoefficient, " 1/K");
        Compare("resistivity reference temperature", project.ResistivityReferenceTemperature,
            library.ResistivityReferenceTemperature, " K");
        Compare("relative permittivity", project.RelativePermittivity, library.RelativePermittivity, "");
        Compare("relative permeability", project.RelativePermeability, library.RelativePermeability, "");
        Compare("dielectric strength", project.DielectricStrength, library.DielectricStrength, " V/m");
        Compare("emissivity", project.Emissivity, library.Emissivity, "");
        Compare("yield strength", project.YieldStrength, library.YieldStrength, " Pa");
        Compare("ultimate tensile strength", project.UltimateTensileStrength, library.UltimateTensileStrength, " Pa");
        if (project.Color != library.Color)
            differences.Add($"colour {project.Color} vs {library.Color}");
        return differences;
    }

    /// <summary>The log line for a project material that differs from the library's material
    /// of its name, or null when the values agree.</summary>
    public static string? Describe(Material project, Material library)
    {
        var differences = Differences(project, library);
        return differences.Count == 0
            ? null
            : $"'{project.Name}': the project's values differ from the library's ({string.Join(", ", differences)}); " +
              "the project's are used.";
    }

    private static string Format(double? value, string format) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "unset";
}

/// <summary>
/// The materials a solve of a saved project uses: a body's own material as the project saved
/// it, a region's material name the project's own copy where a body carries one and the
/// library's otherwise; a sweep's override of a name before either. Every project material
/// that differs from the library's of its name is logged once.
/// </summary>
public sealed class ProjectMaterialLookup
{
    private readonly IReadOnlyList<Material> _project;
    private readonly IReadOnlyList<Material> _library;
    private readonly IReadOnlyDictionary<string, Material>? _overrides;
    private readonly Action<string> _log;
    private readonly HashSet<Material> _reported = new();

    /// <param name="projectMaterials">The bodies' materials, the one a name should resolve
    /// to first (the solved body's) first; nulls are skipped.</param>
    /// <param name="library">The material library's list.</param>
    /// <param name="overrides">Materials by name that take precedence over everything — how a
    /// sweep varies a material property.</param>
    /// <param name="log">Where the divergence lines go.</param>
    public ProjectMaterialLookup(IEnumerable<Material?> projectMaterials, IReadOnlyList<Material> library,
        IReadOnlyDictionary<string, Material>? overrides, Action<string> log)
    {
        _project = projectMaterials.OfType<Material>().ToList();
        _library = library;
        _overrides = overrides;
        _log = log;
    }

    /// <summary>A body's own material: an override of its name, else the material itself.</summary>
    public Material Of(Material own)
    {
        if (_overrides is not null && _overrides.TryGetValue(own.Name, out var over)) return over;
        if (_library.FirstOrDefault(m => m.Name == own.Name) is { } library
            && MaterialDivergence.Describe(own, library) is { } note && _reported.Add(own))
            _log("Material " + note);
        return own;
    }

    /// <summary>The material of a name (a region's): an override, else the project's own copy,
    /// else the library's; null when none has it.</summary>
    public Material? ByName(string name)
    {
        if (_overrides is not null && _overrides.TryGetValue(name, out var over)) return over;
        return _project.FirstOrDefault(m => m.Name == name) is { } own
            ? Of(own)
            : _library.FirstOrDefault(m => m.Name == name);
    }
}
