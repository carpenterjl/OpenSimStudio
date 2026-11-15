namespace OpenSim.Core.Model;

/// <summary>
/// The non-sequential system as saved (plan item OP-70): the objects in editor order (object 1
/// first) and the non-sequential controls of OpticStudio's System Explorer. Lengths are in the
/// lens unit of the system data, as the editors show them. OpenSim.Optics'
/// <c>ProjectNonSequential</c> turns it into a scene and trace settings. Every property is
/// nullable: null is the engine's default.
/// </summary>
public sealed record OpticsNonSequentialSettings
{
    public List<OpticsNscObjectSettings>? Objects { get; init; }

    public int? MaxIntersectionsPerRay { get; init; }
    public int? MaxSegmentsPerRay { get; init; }
    public int? MaxNestedObjects { get; init; }
    public double? MinimumRelativeIntensity { get; init; }
    public double? MinimumAbsoluteIntensity { get; init; }

    /// <summary>Lens units (OpticStudio limits it to 1e-10–1e-3).</summary>
    public double? GlueDistance { get; init; }

    /// <summary>OpticStudio's "Simple Ray Splitting" system setting.</summary>
    public bool? SimpleRaySplitting { get; init; }

    /// <summary>The material outside every object: blank (air), a glass name or an index.</summary>
    public string? BackgroundMaterial { get; init; }
}

/// <summary>One saved object row.</summary>
public sealed record OpticsNscObjectSettings
{
    /// <summary>OpticStudio's object type name ("Sphere", "Rectangle", "Standard Lens").</summary>
    public string? Type { get; init; }

    /// <summary>The type's parameters in OpticStudio's order, lengths in lens units.</summary>
    public List<double>? Parameters { get; init; }

    public int? ReferenceObject { get; init; }
    public int? InsideOf { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public double? TiltX { get; init; }
    public double? TiltY { get; init; }
    public double? TiltZ { get; init; }
    public bool? GlobalXyzRotationOrder { get; init; }

    /// <summary>Blank, MIRROR, ABSORB, a glass name or an index.</summary>
    public string? Material { get; init; }

    public List<OpticsNscFaceSettings>? Faces { get; init; }

    /// <summary>OP-70's detecting stand-in (absorbs and counts; OP-79 builds real detectors).</summary>
    public bool? DetectingStandIn { get; init; }

    public string? Comment { get; init; }
}

/// <summary>One face's saved properties.</summary>
public sealed record OpticsNscFaceSettings
{
    public int? Face { get; init; }

    /// <summary>"Object Default", "Reflective" or "Absorbing".</summary>
    public string? FaceIs { get; init; }

    public string? Coating { get; init; }
}
