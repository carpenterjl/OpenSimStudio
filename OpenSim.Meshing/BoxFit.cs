using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Recognises geometry as an axis-aligned box and reports which of its OWN face ids lies on
/// each of the six bounding planes.
/// <para>
/// The mapping matters as much as the recognition: a box created by
/// <c>PrimitiveFactory.CreateBox</c> numbers its faces 0..5 in a known order, but a box
/// imported from STEP carries whatever ids the file's faces had, and those are the ids the
/// user's boundary conditions already name. Tagging the lattice skin from this map means an
/// imported block meshes structurally without silently renaming its faces.
/// </para>
/// </summary>
internal sealed class BoxFit
{
    public const int PlaneXMin = 0;
    public const int PlaneXMax = 1;
    public const int PlaneYMin = 2;
    public const int PlaneYMax = 3;
    public const int PlaneZMin = 4;
    public const int PlaneZMax = 5;

    private BoxFit(Aabb bounds, int[] faceIdOfPlane)
    {
        Bounds = bounds;
        FaceIdOfPlane = faceIdOfPlane;
    }

    public Aabb Bounds { get; }

    /// <summary>The geometry's own face id lying on each bounding plane, indexed by the
    /// <c>Plane*</c> constants.</summary>
    public int[] FaceIdOfPlane { get; }

    /// <summary>
    /// Recognises <paramref name="geometry"/> as an axis-aligned box, or throws naming the
    /// check that failed. Never falls back to another mesher: a body the user asked to mesh
    /// structurally and cannot be would otherwise be silently meshed a different way and
    /// silently lose the exactness that was the point.
    /// </summary>
    public static BoxFit Detect(TriangleMesh geometry)
    {
        if (!geometry.IsWatertight())
            throw new InvalidOperationException(
                "Geometry is not watertight; repair the surface before meshing.");

        var bounds = geometry.Bounds;
        var size = bounds.Size;
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
            throw Refuse($"it is flat along one axis (extent {size.X:g3} × {size.Y:g3} × {size.Z:g3} m)");

        double tolerance = 1e-9 * bounds.Diagonal;
        var planeValue = new[]
        {
            bounds.Min.X, bounds.Max.X, bounds.Min.Y, bounds.Max.Y, bounds.Min.Z, bounds.Max.Z
        };

        // Every triangle must lie wholly in exactly one bounding plane, and every triangle
        // of one face id must lie in the SAME plane.
        var planeOfFace = new Dictionary<int, int>();
        for (int t = 0; t < geometry.Triangles.Count; t++)
        {
            var tri = geometry.Triangles[t];
            int plane = -1;
            for (int p = 0; p < 6; p++)
            {
                if (!OnPlane(geometry.Vertices[tri.A], p, planeValue[p], tolerance) ||
                    !OnPlane(geometry.Vertices[tri.B], p, planeValue[p], tolerance) ||
                    !OnPlane(geometry.Vertices[tri.C], p, planeValue[p], tolerance)) continue;
                if (plane >= 0)
                    throw Refuse($"triangle {t} lies in two bounding planes at once, so it has no area");
                plane = p;
            }
            if (plane < 0)
                throw Refuse($"triangle {t} does not lie in any of the six bounding planes " +
                             "(the body is not a box, or it is not aligned with the axes)");

            int faceId = geometry.TriangleFaceIds[t];
            if (!planeOfFace.TryGetValue(faceId, out int known)) planeOfFace[faceId] = plane;
            else if (known != plane)
                throw Refuse($"face {faceId} spans more than one bounding plane");
        }

        if (planeOfFace.Count != 6)
            throw Refuse($"it has {planeOfFace.Count} face(s) rather than the six a box has");

        var faceIdOfPlane = new int[6];
        Array.Fill(faceIdOfPlane, -1);
        foreach (var (faceId, plane) in planeOfFace)
        {
            if (faceIdOfPlane[plane] >= 0)
                throw Refuse($"faces {faceIdOfPlane[plane]} and {faceId} share a bounding plane");
            faceIdOfPlane[plane] = faceId;
        }

        // The decisive check: six planar faces can also describe a notched or hollow solid.
        // Only a box FILLS its bounding volume.
        double boxVolume = size.X * size.Y * size.Z;
        double volume = Math.Abs(geometry.ComputeSignedVolume());
        if (Math.Abs(volume - boxVolume) > 1e-9 * boxVolume)
            throw Refuse($"its volume is {volume:g6} m³ against the {boxVolume:g6} m³ of its " +
                         "bounding box, so it does not fill it");

        return new BoxFit(bounds, faceIdOfPlane);
    }

    private static bool OnPlane(Vector3D v, int plane, double value, double tolerance) =>
        Math.Abs((plane switch { 0 or 1 => v.X, 2 or 3 => v.Y, _ => v.Z }) - value) <= tolerance;

    private static InvalidOperationException Refuse(string because) =>
        new($"Structured lattice meshing supports axis-aligned box bodies only; {because}. " +
            "Use the Delaunay mesher for this body.");
}
