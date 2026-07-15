using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;

namespace OpenSim.Core.PostProcessing;

/// <summary>
/// The sampling grid for a board field overlay (SI Stage S10): a regular lattice over a
/// rectangle at a fixed height, plus even-odd masking against a board outline so the
/// heatmap paints ONLY over copper-bearing board area (the SIwave "field over the board"
/// view, not a rectangular plane hovering past the board edge). Pure geometry — the WPF
/// mesh builder consumes the mask; kept here so the masking and quad topology are testable
/// without the App layer.
/// </summary>
public static class OverlayGrid
{
    /// <summary>Row-major (y outer, x inner) lattice of <paramref name="nx"/>×<paramref name="ny"/>
    /// points spanning [minX,maxX]×[minY,maxY] at height <paramref name="z"/>.</summary>
    public static Vector3D[] RectPoints(double minX, double minY, double maxX, double maxY,
        double z, int nx, int ny)
    {
        if (nx < 2 || ny < 2) throw new ArgumentOutOfRangeException(nameof(nx), "Need ≥2×2 points.");
        var points = new Vector3D[nx * ny];
        double dx = (maxX - minX) / (nx - 1), dy = (maxY - minY) / (ny - 1);
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
                points[y * nx + x] = new Vector3D(minX + x * dx, minY + y * dy, z);
        return points;
    }

    /// <summary>Per-point containment in the outline (even-odd, holes respected). Points'
    /// (X, Y) are tested; Z is ignored (the outline is 2-D).</summary>
    public static bool[] InteriorMask(IReadOnlyList<Vector3D> points, PolygonSetIndex outline)
    {
        var inside = new bool[points.Count];
        for (int i = 0; i < points.Count; i++)
            inside[i] = outline.Contains(new Point2(points[i].X, points[i].Y));
        return inside;
    }

    /// <summary>The lattice quads (as corner index quadruples v00,v10,v11,v01) whose ALL
    /// FOUR corners are inside the mask — the cells the overlay actually paints. Emitting
    /// only fully-interior cells keeps the painted region inside the board with no partial
    /// quads straddling the edge.</summary>
    public static IEnumerable<(int V00, int V10, int V11, int V01)> InteriorQuads(
        bool[] inside, int nx, int ny)
    {
        for (int y = 0; y < ny - 1; y++)
            for (int x = 0; x < nx - 1; x++)
            {
                int v00 = y * nx + x, v10 = v00 + 1, v01 = v00 + nx, v11 = v01 + 1;
                if (inside[v00] && inside[v10] && inside[v11] && inside[v01])
                    yield return (v00, v10, v11, v01);
            }
    }

    /// <summary>Count of fully-interior quads (topology gate helper).</summary>
    public static int InteriorQuadCount(bool[] inside, int nx, int ny)
    {
        int count = 0;
        foreach (var _ in InteriorQuads(inside, nx, ny)) count++;
        return count;
    }
}
