using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Core.PostProcessing;

/// <summary>
/// Contour (iso-value) line extraction on the mesh's boundary skin: marching triangles
/// over the nodal scalar field. Lives in Core (UI-free) so the interpolation geometry
/// is testable; the rendering layer only converts the segments to line visuals.
/// </summary>
public static class IsoLineExtractor
{
    /// <summary>
    /// Extracts <paramref name="levelCount"/> interior iso-levels (never the degenerate
    /// min/max themselves) as line segments on the deformed skin. Positions use the
    /// same node + displacement·deformScale formula as the rendered surface, so the
    /// contours lie exactly on it. Triangles entirely beyond <paramref name="clip"/>'s
    /// kept side are skipped, matching the clipped surface.
    /// </summary>
    public static List<(Vector3D A, Vector3D B)> Extract(
        FeMesh mesh, IReadOnlyList<double> nodalScalars,
        IReadOnlyList<Vector3D>? displacement, double deformScale,
        int levelCount, double min, double max, SectionPlane? clip = null)
    {
        var segments = new List<(Vector3D, Vector3D)>();
        double range = max - min;
        if (range <= 0 || levelCount <= 0) return segments;

        Vector3D Deformed(int n) => displacement is null
            ? mesh.Nodes[n]
            : mesh.Nodes[n] + displacement[n] * deformScale;

        // A vertex exactly on a level is nudged off it so the strict-inequality case
        // analysis stays total (a triangle then always yields 0 or 2 crossings).
        double epsilon = 1e-12 * range;

        var s = new double[3];
        var p = new Vector3D[3];
        var hits = new Vector3D[3];
        foreach (var t in mesh.BoundaryTriangles)
        {
            p[0] = Deformed(t.A);
            p[1] = Deformed(t.B);
            p[2] = Deformed(t.C);
            if (clip is { } plane &&
                plane.SignedDistance(p[0]) > 0 && plane.SignedDistance(p[1]) > 0 && plane.SignedDistance(p[2]) > 0)
                continue;

            s[0] = nodalScalars[t.A];
            s[1] = nodalScalars[t.B];
            s[2] = nodalScalars[t.C];

            for (int k = 1; k <= levelCount; k++)
            {
                double level = min + k * range / (levelCount + 1);
                int hitCount = 0;
                for (int i = 0; i < 3; i++)
                {
                    int j = (i + 1) % 3;
                    double si = s[i] == level ? level + epsilon : s[i];
                    double sj = s[j] == level ? level + epsilon : s[j];
                    if ((si - level) * (sj - level) >= 0) continue;
                    double f = (level - si) / (sj - si);
                    hits[hitCount++] = p[i] + (p[j] - p[i]) * f;
                }
                if (hitCount == 2)
                    segments.Add((hits[0], hits[1]));
            }
        }
        return segments;
    }
}
