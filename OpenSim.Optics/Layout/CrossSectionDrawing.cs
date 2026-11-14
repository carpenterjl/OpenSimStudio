namespace OpenSim.Optics.Layout;

/// <summary>The 2-D cross-section: its lines, and the bounds of everything but the arrows.</summary>
public sealed record CrossSectionDrawing(IReadOnlyList<LayoutPolyline> Lines, LayoutBounds2D Bounds);
