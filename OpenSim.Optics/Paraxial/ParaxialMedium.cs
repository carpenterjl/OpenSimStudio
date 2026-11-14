using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Paraxial;

/// <summary>
/// The medium after a paraxial surface: a constant index, or an optical material whose index
/// follows the wavelength and the system environment. Exactly one of <see cref="Index"/> and
/// <see cref="Material"/> is set (<see cref="Validate"/>).
/// <para>
/// Indices are relative to the system's air, as OpticStudio's are (docs/Optics-Conventions.md
/// §2): a constant index of 1 is that air, and a material's index comes from
/// <see cref="OpticsEnvironment.GlassIndex"/>, so it follows the "adjust indices to the
/// environment" setting. A constant index is the same at every wavelength and temperature.
/// </para>
/// </summary>
public sealed record ParaxialMedium
{
    /// <summary>A constant index relative to the system's air, or null for a material.</summary>
    public double? Index { get; init; }

    /// <summary>The material, or null for a constant index.</summary>
    public OpticalMaterial? Material { get; init; }

    /// <summary>The system's air: index 1 relative to itself.</summary>
    public static ParaxialMedium Air { get; } = new() { Index = 1.0 };

    /// <summary>A constant index, the same at every wavelength.</summary>
    public static ParaxialMedium Constant(double index) => new() { Index = index };

    /// <summary>An optical material from a catalog or a table.</summary>
    public static ParaxialMedium Of(OpticalMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return new ParaxialMedium { Material = material };
    }

    /// <summary>The index relative to the system's air at a wavelength measured in that air
    /// (µm). A material outside its valid range throws with the material's own message.</summary>
    public double IndexAt(double airMicrometers, OpticsEnvironment environment) =>
        Index ?? environment.GlassIndex(Material!, airMicrometers);

    /// <summary>"n = 1.5" or the material's name, for reports.</summary>
    public string Describe() =>
        Index is double n ? $"n = {n.ToString("g6", System.Globalization.CultureInfo.InvariantCulture)}"
        : Material is { } m ? (m.Catalog.Length > 0 ? $"{m.Name} ({m.Catalog})" : m.Name)
        : "(none)";

    /// <summary>Adds one line per problem, naming the surface.</summary>
    internal void Validate(string where, ICollection<string> problems)
    {
        if (Index is null == Material is null)
        {
            problems.Add($"{where}: the medium needs either a constant index or a material, not {(Index is null ? "neither" : "both")}.");
            return;
        }
        if (Index is double n && (!double.IsFinite(n) || !(n > 0)))
            problems.Add($"{where}: the index must be a positive number (it is {n}).");
    }
}
