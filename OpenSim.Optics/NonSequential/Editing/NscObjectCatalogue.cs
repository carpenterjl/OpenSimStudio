namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>
/// The object types the Non-Sequential Component Editor offers (plan item OP-71,
/// docs/Optics-Conventions.md §71): an open list of <see cref="NscObjectTypeDescriptor"/>s.
/// <see cref="Built"/> holds OP-70's three types; <see cref="Default"/> joins the source types
/// (OP-77, <see cref="Sources.NscSourceTypes.Descriptors"/>) and the detector types (OP-79,
/// <see cref="Detectors.NscDetectorTypes.Descriptors"/>). A descriptor with a
/// <see cref="NscObjectTypeDescriptor.NotBuiltReason"/> (Source File, Detector Surface, ...) is
/// kept apart in <see cref="Refused"/>: it is not offered, and asking for it gives its reason. A
/// saved type the catalogue does not build is not an error here: the editor shows its raw
/// parameters and writes them back unchanged, and refuses only to add new objects of it
/// (<see cref="NotBuiltMessage"/>).
/// </summary>
public sealed class NscObjectCatalogue
{
    private readonly NscObjectTypeDescriptor[] _types;
    private readonly NscObjectTypeDescriptor[] _all;

    /// <summary>A catalogue of <paramref name="types"/>, in the order the editor lists them.
    /// Throws, naming the type, for a descriptor that is incomplete or a name given twice.</summary>
    public NscObjectCatalogue(IEnumerable<NscObjectTypeDescriptor> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        _all = types.ToArray();
        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _all)
        {
            if (t is null) { problems.Add("The catalogue holds a null object type."); continue; }
            problems.AddRange(t.Validate());
            if (!names.Add(t.TypeName ?? "")) problems.Add($"Object type '{t.TypeName}' is in the catalogue twice.");
        }
        if (problems.Count > 0)
            throw new ArgumentException("Non-sequential object catalogue refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        _types = _all.Where(t => t.IsBuilt).ToArray();
        Refused = _all.Where(t => !t.IsBuilt).ToArray();
    }

    /// <summary>The types listed but not built, each with its reason.</summary>
    public IReadOnlyList<NscObjectTypeDescriptor> Refused { get; }

    /// <summary>The types, in listing order.</summary>
    public IReadOnlyList<NscObjectTypeDescriptor> Types => _types;

    /// <summary>The type of this name (ignoring case and surrounding spaces), or null.</summary>
    public NscObjectTypeDescriptor? Find(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        string t = typeName.Trim();
        return _types.FirstOrDefault(d => string.Equals(d.TypeName, t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>This catalogue (built and refused types) followed by <paramref name="more"/> (a name
    /// already present is refused).</summary>
    public NscObjectCatalogue Join(IEnumerable<NscObjectTypeDescriptor> more)
    {
        ArgumentNullException.ThrowIfNull(more);
        return new NscObjectCatalogue(_all.Concat(more));
    }

    /// <summary>OP-70's types: Sphere, Rectangle and Standard Lens.</summary>
    public static NscObjectCatalogue Built { get; } = new(NscCoreObjectTypes.All);

    /// <summary>
    /// The catalogue the editor uses: OP-70's types, then the source types (OP-77), then the
    /// detector types (OP-79), each list with its refused types kept apart.
    /// </summary>
    public static NscObjectCatalogue Default { get; } =
        Built.Join(Sources.NscSourceTypes.Descriptors).Join(Detectors.NscDetectorTypes.Descriptors);

    /// <summary>Why an object of <paramref name="typeName"/> cannot be added: a refused type's own
    /// reason, otherwise the plan item that will build it. OpticStudio's source types are named "Source ..." and its detector types
    /// "Detector ..." (help, "Non-sequential Sources" and "Non-sequential Detectors" lists); every
    /// other type belongs to the object library.</summary>
    public string NotBuiltMessage(string typeName)
    {
        string t = (typeName ?? "").Trim();
        if (Refused.FirstOrDefault(d => string.Equals(d.TypeName, t, StringComparison.OrdinalIgnoreCase)) is { } refused)
            return refused.NotBuiltReason!;
        string built = string.Join(", ", _types.Select(d => d.TypeName));
        bool sources = _types.Any(d => d.Category == NscObjectCategory.Source), detectors = _types.Any(d => d.Category == NscObjectCategory.Detector);
        string item = t.StartsWith("Source", StringComparison.OrdinalIgnoreCase)
                ? sources ? "it is not one of the source types this build lists (help, \"Summary of NSC Sources\")" : "sources are plan item OP-77"
            : t.StartsWith("Detector", StringComparison.OrdinalIgnoreCase)
                ? detectors ? "it is not one of the detector types this build lists" : "detectors are plan item OP-79"
            : "the object library is plan items OP-72 to OP-75 (lenses, surfaces and volumes OP-72; illumination and array objects OP-73; Boolean, extruded and polygon objects OP-74; CAD parts OP-75)";
        return $"The object type '{t}' is not built; this build offers {built}, and {item}.";
    }
}
