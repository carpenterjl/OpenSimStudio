namespace OpenSim.Geometry.Step;

/// <summary>
/// Tessellation controls with deterministic defaults. All tolerances are relative to the
/// solid's bounding-box diagonal so the same file imports identically at any unit scale.
/// </summary>
public sealed record StepImportOptions
{
    /// <summary>Chord (sagitta) tolerance as a fraction of the solid's bbox diagonal.</summary>
    public double RelativeChordTolerance { get; init; } = 1e-3;

    /// <summary>Angular ceiling per curve segment [rad] — keeps small circles round even
    /// when the sagitta rule alone would allow coarse chords.</summary>
    public double MaxAnglePerSegment { get; init; } = Math.PI / 8;

    /// <summary>Minimum segments for a full closed curve (welding sanity on tiny features).</summary>
    public int MinSegmentsPerCircle { get; init; } = 8;

    /// <summary>Safety cap per edge; exceeding it is a loud failure, never a hang.</summary>
    public int MaxSegmentsPerEdge { get; init; } = 2048;

    /// <summary>
    /// Interior Steiner sampling of curved faces. Off ⇒ boundary-only triangulation,
    /// which the sharp discretization-identity benchmarks rely on.
    /// </summary>
    public bool InteriorRefinement { get; init; } = true;

    public static StepImportOptions Default { get; } = new();
}
