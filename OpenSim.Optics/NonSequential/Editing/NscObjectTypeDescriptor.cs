namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>What a non-sequential object parameter holds, which decides its unit in the
/// editor and whether it is converted from the lens unit.</summary>
public enum NscParameterKind
{
    /// <summary>A length in lens units (converted to meters for the trace).</summary>
    Length = 0,

    /// <summary>A pure number (a conic constant, a ratio).</summary>
    Number = 1,

    /// <summary>A flag: zero is off, any other value on (OpticStudio's "Is Volume?").</summary>
    Flag = 2,

    /// <summary>An angle in degrees.</summary>
    Angle = 3,

    /// <summary>A whole number (a count of rays, of pixels).</summary>
    Integer = 4,
}

/// <summary>Which of OpticStudio's three object families a type belongs to (help, "Non-sequential
/// Component Editor": geometry objects, sources and detectors share one object list).</summary>
public enum NscObjectCategory
{
    Geometry = 0,
    Source = 1,
    Detector = 2,
}

/// <summary>One parameter column of an object type: its column name, the value a new object
/// starts with, and its kind.</summary>
/// <param name="Name">The column header without its unit ("Radius 1", "X Half Width").</param>
/// <param name="Default">The value of a new object's cell.</param>
public sealed record NscParameterDescriptor(string Name, double Default, NscParameterKind Kind = NscParameterKind.Length)
{
    /// <summary>What the parameter is, for the cell's tooltip.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// One object type of the Non-Sequential Component Editor (plan item OP-71,
/// docs/Optics-Conventions.md §71): the type's saved name, its parameters in OpticStudio's order,
/// its faces, whether it is a volume, and how to build its shape. The editor's catalogue
/// (<see cref="NscObjectCatalogue"/>) is a list of these; OP-70's three types are in
/// <see cref="NscCoreObjectTypes"/>, and OP-77 (sources) and OP-79 (detectors) publish their own
/// lists in their own files, which the wiring step joins to the catalogue with
/// <see cref="NscObjectCatalogue.Join"/>.
/// </summary>
public sealed record NscObjectTypeDescriptor
{
    /// <summary>OpticStudio's type name as saved ("Standard Lens"), matched without case.</summary>
    public required string TypeName { get; init; }

    public NscObjectCategory Category { get; init; }

    /// <summary>The parameters in OpticStudio's order (parameter 1 first).</summary>
    public required IReadOnlyList<NscParameterDescriptor> Parameters { get; init; }

    /// <summary>Face numbers and names from the type's help page (for "Face Is" and coatings).</summary>
    public required IReadOnlyDictionary<int, string> Faces { get; init; }

    /// <summary>True for a volume, false for a surface; null when <see cref="VolumeFlagParameter"/>
    /// decides it.</summary>
    public bool? IsVolume { get; init; }

    /// <summary>The 0-based index of the parameter whose non-zero value makes the object a volume
    /// (the Sphere's "Is Volume?"), or null.</summary>
    public int? VolumeFlagParameter { get; init; }

    /// <summary>The help page the parameters and faces come from.</summary>
    public string? Source { get; init; }

    /// <summary>The plan item that built the type ("OP-70").</summary>
    public string? PlanItem { get; init; }

    /// <summary>Builds the shape from the parameters with every <see cref="NscParameterKind.Length"/>
    /// already in meters; throws <see cref="ArgumentException"/> or <see cref="NotSupportedException"/>
    /// naming the parameter. Null for a type with no shape of its own to show.</summary>
    public Func<IReadOnlyList<double>, NscGeometry>? Geometry { get; init; }

    /// <summary>The parameters of a new object.</summary>
    public IReadOnlyList<double> Defaults => Parameters.Select(p => p.Default).ToArray();

    /// <summary>True when an object of this type with these parameters encloses a volume.</summary>
    public bool IsVolumeFor(IReadOnlyList<double>? parameters)
    {
        if (IsVolume is bool fixedAnswer) return fixedAnswer;
        int i = VolumeFlagParameter ?? throw new InvalidOperationException($"{TypeName}: the descriptor says neither whether it is a volume nor which parameter decides it.");
        double flag = parameters is not null && i < parameters.Count ? parameters[i] : Parameters[i].Default;
        return flag != 0;
    }

    /// <summary>The shape of an object of this type, its saved parameters (lens units) padded with
    /// the defaults and its lengths converted to meters.</summary>
    public NscGeometry Build(IReadOnlyList<double>? parameters, LensUnit unit)
    {
        if (Geometry is null) throw new NotSupportedException($"{TypeName}: this type has no shape to build.");
        var p = new double[Parameters.Count];
        for (int i = 0; i < p.Length; i++)
        {
            double v = parameters is not null && i < parameters.Count ? parameters[i] : Parameters[i].Default;
            p[i] = Parameters[i].Kind == NscParameterKind.Length ? OpticsConventions.ToMeters(v, unit) : v;
        }
        return Geometry(p);
    }

    /// <summary>Every problem with the descriptor itself, naming the type.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        string where = $"Object type '{TypeName}'";
        if (string.IsNullOrWhiteSpace(TypeName)) problems.Add("An object type has no name.");
        if (Parameters is null) problems.Add($"{where}: no parameter list.");
        else
            for (int i = 0; i < Parameters.Count; i++)
                if (string.IsNullOrWhiteSpace(Parameters[i]?.Name)) problems.Add($"{where}: parameter {i + 1} has no name.");
        if (Faces is null || Faces.Count == 0) problems.Add($"{where}: no faces (every object has at least face 0).");
        if (IsVolume is null && (VolumeFlagParameter is not int f || Parameters is null || f < 0 || f >= Parameters.Count))
            problems.Add($"{where}: it must say whether it is a volume, or name the parameter that does.");
        return problems;
    }
}
