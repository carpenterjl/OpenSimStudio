namespace OpenSim.Optics.Coatings;

/// <summary>
/// A coating catalog: materials, tapers and coatings, each found by name ignoring case. Names
/// must be unique within their kind (OpticStudio leaves a repeated name undefined, its help,
/// "Defining Coatings"; here it is refused). The catalog is immutable; the editor builds a new
/// one for every change.
/// </summary>
public sealed record CoatingCatalog
{
    public static CoatingCatalog Empty { get; } = new();

    public IReadOnlyList<CoatingMaterial> Materials { get; init; } = Array.Empty<CoatingMaterial>();
    public IReadOnlyList<CoatingTaper> Tapers { get; init; } = Array.Empty<CoatingTaper>();
    public IReadOnlyList<Coating> Coatings { get; init; } = Array.Empty<Coating>();

    public CoatingMaterial? FindMaterial(string? name) =>
        name is null ? null : Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public CoatingTaper? FindTaper(string? name) =>
        name is null ? null : Tapers.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public Coating? FindCoating(string? name) =>
        name is null ? null : Coatings.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every problem in the catalog, one line each; empty when all of it can be used.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        Duplicates(Materials.Select(m => m.Name), "material", problems);
        Duplicates(Tapers.Select(t => t.Name), "taper", problems);
        Duplicates(Coatings.Select(c => c.Name), "coating", problems);
        foreach (var m in Materials) problems.AddRange(m.Validate());
        foreach (var t in Tapers) problems.AddRange(t.Validate());
        foreach (var c in Coatings)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) problems.Add("A coating has no name.");
            problems.AddRange(c.Validate(this));
        }
        return problems;
    }

    private static void Duplicates(IEnumerable<string> names, string kind, List<string> problems)
    {
        foreach (var group in names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"The {kind} name '{group.Key}' is used {group.Count()} times; names must be unique.");
    }
}
