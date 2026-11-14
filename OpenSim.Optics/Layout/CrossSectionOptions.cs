namespace OpenSim.Optics.Layout;

/// <summary>What decides a ray's colour in the layout views.</summary>
public enum RayColoring
{
    /// <summary>One colour per field point.</summary>
    ByField = 0,

    /// <summary>One colour per wavelength.</summary>
    ByWavelength = 1,
}

/// <summary>Where direction arrows ("fletches") are drawn on the rays.</summary>
public enum FletchMode
{
    None = 0,

    /// <summary>One arrow, at the middle of each ray's first segment.</summary>
    FirstSegment = 1,

    /// <summary>An arrow at the middle of every segment.</summary>
    EverySegment = 2,
}

/// <summary>
/// Options of the 2-D cross-section (OpticStudio's Cross-Section settings, those that do not
/// need the trace itself).
/// </summary>
public sealed record CrossSectionOptions
{
    public RayColoring Coloring { get; init; } = RayColoring.ByField;
    public FletchMode Fletch { get; init; } = FletchMode.None;

    /// <summary>Arrow length as a fraction of the drawing's larger extent.</summary>
    public double FletchSize { get; init; } = 0.012;

    /// <summary>Leave out rays that failed (vignetted, missed, TIR) instead of drawing them up
    /// to the surface they failed at.</summary>
    public bool DeleteVignetted { get; init; }

    /// <summary>Sag samples from the vertex to the clear edge of each surface.</summary>
    public int SamplesPerSemiDiameter { get; init; } = 32;

    public static CrossSectionOptions Default { get; } = new();
}
