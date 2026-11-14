namespace OpenSim.Optics.Materials;

/// <summary>
/// A material catalog as read from one file: its name (the file name without extension, as
/// OpticStudio names catalogs), the catalog's own comments (CC lines and leading text), its
/// materials in file order, and the reader's notes on anything it kept out or could not
/// confirm. Duplicate names are kept (some vendor files repeat a glass); <see cref="Find"/>
/// returns the first.
/// </summary>
public sealed record OpticalCatalog
{
    public required string Name { get; init; }

    /// <summary>The file it was read from, or null for a catalog built in memory.</summary>
    public string? SourcePath { get; init; }

    public IReadOnlyList<string> Comments { get; init; } = Array.Empty<string>();

    public IReadOnlyList<OpticalMaterial> Materials { get; init; } = Array.Empty<OpticalMaterial>();

    /// <summary>What the reader noted: unconfirmed formulas, ignored lines, duplicates.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>The first material of that name, ignoring case; null when there is none.</summary>
    public OpticalMaterial? Find(string name) =>
        Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
