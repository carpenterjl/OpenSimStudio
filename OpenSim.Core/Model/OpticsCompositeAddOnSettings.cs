namespace OpenSim.Core.Model;

/// <summary>
/// One composite add-on of a surface as project and lens files store it (OP-32): a surface
/// whose sag is added to the surface that holds it (OpticStudio 2025 R2 help, "Composite
/// Surface"). The shape is given like a surface's own (type, radius, conic, parameters and extra
/// data, a Grid Sag's grid) in lens units; the placement relative to the base surface is the
/// help's Composite page: decenter x and y in lens units, tilts about x, y and z in degrees.
/// OpenSim.Optics' <c>CompositeAddOn</c> is the traced form.
/// </summary>
public sealed record OpticsCompositeAddOnSettings
{
    /// <summary>The add-on's surface type; null is "Standard".</summary>
    public string? Type { get; init; }

    /// <summary>The radius in lens units; null is a plane.</summary>
    public double? Radius { get; init; }

    /// <summary>The conic constant.</summary>
    public double Conic { get; init; }

    /// <summary>Parameters 0–12 of the type, lens units per column.</summary>
    public List<OpticsSurfaceParameterSettings>? Parameters { get; init; }

    /// <summary>Extra data (parameters 13 on) of the type.</summary>
    public List<OpticsSurfaceParameterSettings>? ExtraData { get; init; }

    /// <summary>A Grid Sag add-on's grid, or null.</summary>
    public OpticsGridSagSettings? GridSag { get; init; }

    /// <summary>The add-on's own clear semi-diameter in lens units (read by the types that
    /// normalise to it, such as Irregular), or null to use the base surface's.</summary>
    public double? SemiDiameter { get; init; }

    /// <summary>Decenter x relative to the base surface, lens units.</summary>
    public double DecenterX { get; init; }

    /// <summary>Decenter y relative to the base surface, lens units.</summary>
    public double DecenterY { get; init; }

    /// <summary>Tilt about x relative to the base surface, degrees.</summary>
    public double TiltX { get; init; }

    /// <summary>Tilt about the new y, degrees.</summary>
    public double TiltY { get; init; }

    /// <summary>Tilt about the new z, degrees.</summary>
    public double TiltZ { get; init; }

    /// <summary>OpticStudio's "Ignore This Surface" on the add-on: its sag is not added.</summary>
    public bool IsIgnored { get; init; }

    /// <summary>The add-on's comment.</summary>
    public string? Comment { get; init; }
}
