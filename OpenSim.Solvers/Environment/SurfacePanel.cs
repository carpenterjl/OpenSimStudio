using OpenSim.Core.Numerics;

namespace OpenSim.Solvers.Environment;

/// <summary>Which correlation family a panel's geometry selects.</summary>
public enum PanelShape
{
    /// <summary>A flat panel standing along gravity — the vertical-plate correlation.</summary>
    VerticalPlate,

    /// <summary>A flat panel whose outward normal points up.</summary>
    HorizontalPlateUpward,

    /// <summary>A flat panel whose outward normal points down.</summary>
    HorizontalPlateDownward,

    /// <summary>A panel whose normals spread too far to call it flat (a fillet, a bore, a
    /// blend). Treated as a vertical plate of the panel's own vertical extent — a stated
    /// approximation; automatic cylinder recognition is a named refinement, and the
    /// cylinder correlations ship tested against the day it lands.</summary>
    CurvedSurface
}

/// <summary>
/// One geometric face of the model, exposed to the environment.
/// <para>
/// A panel — not a triangle — is the unit the correlations apply to, because a Nusselt
/// number is an INTEGRAL result over a surface: it already contains the whole boundary
/// layer that developed over that surface, and its length scale is a property of the
/// surface, not of a mesh triangle. Evaluating a correlation per triangle would invent a
/// length scale per triangle and report a film coefficient that changed with mesh density.
/// </para>
/// </summary>
public sealed record SurfacePanel
{
    /// <summary>The geometric face id this panel covers.</summary>
    public required int FaceId { get; init; }

    /// <summary>Indices into <c>FeMesh.BoundaryTriangles</c> that make up the panel.</summary>
    public required IReadOnlyList<int> TriangleIndices { get; init; }

    /// <summary>Total panel area [m²].</summary>
    public required double Area { get; init; }

    /// <summary>Area-weighted mean outward normal, normalized.</summary>
    public required Vector3D MeanNormal { get; init; }

    /// <summary>The widest angle between a triangle normal and <see cref="MeanNormal"/> [deg];
    /// what decides flat versus curved.</summary>
    public required double NormalSpreadDegrees { get; init; }

    /// <summary>The correlation family the panel's geometry selects.</summary>
    public required PanelShape Shape { get; init; }

    /// <summary>Natural-convection length scale [m]: the vertical extent for a plate along
    /// gravity, or the area-over-perimeter of a horizontal one.</summary>
    public required double CharacteristicLength { get; init; }

    /// <summary>Forced-convection length scale [m]: the streamwise extent of the BODY this
    /// panel belongs to. The body's run is what the boundary layer develops over; a panel's
    /// own projected extent would be zero for the face that squarely faces the flow, which
    /// would report no forced convection exactly where it is strongest.</summary>
    public required double FlowLength { get; init; }

    /// <summary>Surface emissivity taken from the material of the body this panel belongs
    /// to; zero when radiation is switched off.</summary>
    public required double Emissivity { get; init; }

    /// <summary>Mesh region (body index in an assembly) the panel belongs to.</summary>
    public required int Region { get; init; }

    /// <summary>Material name, for the assumption log.</summary>
    public required string MaterialName { get; init; }

    /// <summary>True when the outward normal points against gravity.</summary>
    public bool FacesUpward => Shape == PanelShape.HorizontalPlateUpward;
}
