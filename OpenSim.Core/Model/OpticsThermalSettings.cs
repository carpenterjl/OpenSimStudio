namespace OpenSim.Core.Model;

// OP-91
/// <summary>
/// The thermal analysis's saved data (plan item OP-91): the links from glasses to mechanical
/// materials. Owner decision OQ-12 (2026-11-12): a glass may optionally name a mechanical
/// <see cref="Material"/>, so one lens body serves both the optical and the mechanical model;
/// the thermal pickups then expand that glass with the material's
/// <see cref="Material.ThermalExpansionCoefficient"/> instead of its catalog TCE. The material is
/// stored whole, as a project stores each body's material, so the lens opens with the values it
/// was computed with.
/// </summary>
public sealed record OpticsThermalSettings
{
    /// <summary>The links, at most one per glass; null is none.</summary>
    public List<OpticsGlassMaterialSettings>? GlassMaterials { get; init; }
}

/// <summary>One glass named to a mechanical material.</summary>
public sealed record OpticsGlassMaterialSettings
{
    /// <summary>The glass as the material column names it: "N-BK7" or "SCHOTT:N-BK7", any case.</summary>
    public string? Glass { get; init; }

    /// <summary>The mechanical material, copied from the material library.</summary>
    public Material? Material { get; init; }
}
