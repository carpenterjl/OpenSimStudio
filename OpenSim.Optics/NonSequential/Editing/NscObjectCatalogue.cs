namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>
/// The object types the Non-Sequential Component Editor offers (plan item OP-71,
/// docs/Optics-Conventions.md §71): an open list of <see cref="NscObjectTypeDescriptor"/>s.
/// <see cref="Built"/> holds OP-70's three types; sources (OP-77) and detectors (OP-79) publish
/// their own descriptor lists and are joined with <see cref="Join"/> where <see cref="Default"/>
/// is defined. A saved type the catalogue does not know is not an error here: the editor shows
/// its raw parameters and writes them back unchanged, and refuses only to add new objects of it
/// (<see cref="NotBuiltMessage"/>).
/// </summary>
public sealed class NscObjectCatalogue
{
    private readonly NscObjectTypeDescriptor[] _types;

    /// <summary>A catalogue of <paramref name="types"/>, in the order the editor lists them.
    /// Throws, naming the type, for a descriptor that is incomplete or a name given twice.</summary>
    public NscObjectCatalogue(IEnumerable<NscObjectTypeDescriptor> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = types.ToArray();
        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _types)
        {
            if (t is null) { problems.Add("The catalogue holds a null object type."); continue; }
            problems.AddRange(t.Validate());
            if (!names.Add(t.TypeName ?? "")) problems.Add($"Object type '{t.TypeName}' is in the catalogue twice.");
        }
        if (problems.Count > 0)
            throw new ArgumentException("Non-sequential object catalogue refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>The types, in listing order.</summary>
    public IReadOnlyList<NscObjectTypeDescriptor> Types => _types;

    /// <summary>The type of this name (ignoring case and surrounding spaces), or null.</summary>
    public NscObjectTypeDescriptor? Find(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        string t = typeName.Trim();
        return _types.FirstOrDefault(d => string.Equals(d.TypeName, t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>This catalogue followed by <paramref name="more"/> (a name already present is refused).</summary>
    public NscObjectCatalogue Join(IEnumerable<NscObjectTypeDescriptor> more)
    {
        ArgumentNullException.ThrowIfNull(more);
        return new NscObjectCatalogue(_types.Concat(more));
    }

    /// <summary>OP-70's types: Sphere, Rectangle and Standard Lens.</summary>
    public static NscObjectCatalogue Built { get; } = new(NscCoreObjectTypes.All);

    /// <summary>
    /// The catalogue the editor uses. Today it is <see cref="Built"/>.
    /// OP-77 / OP-79: the wiring step joins the source and detector descriptor lists here,
    /// <c>Built.Join(sources).Join(detectors)</c>.
    /// </summary>
    public static NscObjectCatalogue Default { get; } = Built;

    /// <summary>Why an object of <paramref name="typeName"/> cannot be added: the plan item that
    /// will build it. OpticStudio's source types are named "Source ..." and its detector types
    /// "Detector ..." (help, "Non-sequential Sources" and "Non-sequential Detectors" lists); every
    /// other type belongs to the object library.</summary>
    public string NotBuiltMessage(string typeName)
    {
        string t = (typeName ?? "").Trim();
        string built = string.Join(", ", _types.Select(d => d.TypeName));
        string item = t.StartsWith("Source", StringComparison.OrdinalIgnoreCase) ? "sources are plan item OP-77"
            : t.StartsWith("Detector", StringComparison.OrdinalIgnoreCase) ? "detectors are plan item OP-79"
            : "the object library is plan items OP-72 to OP-75 (lenses, surfaces and volumes OP-72; illumination and array objects OP-73; Boolean, extruded and polygon objects OP-74; CAD parts OP-75)";
        return $"The object type '{t}' is not built; this build offers {built}, and {item}.";
    }
}
