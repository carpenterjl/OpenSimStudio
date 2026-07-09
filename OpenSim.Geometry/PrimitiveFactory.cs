using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Geometry;

/// <summary>
/// Generates parametric primitive geometry with analytically assigned face ids.
/// Primitives give exact dimensions, which makes them ideal for solver validation.
/// </summary>
public static class PrimitiveFactory
{
    /// <summary>
    /// Axis-aligned box with one corner at the origin.
    /// Face ids: 0 = x-min, 1 = x-max, 2 = y-min, 3 = y-max, 4 = z-min, 5 = z-max.
    /// </summary>
    public static TriangleMesh CreateBox(double sizeX, double sizeY, double sizeZ)
    {
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
            throw new ArgumentException("Box dimensions must be positive.");

        var v = new List<Vector3D>
        {
            new(0, 0, 0),           new(sizeX, 0, 0),
            new(sizeX, sizeY, 0),   new(0, sizeY, 0),
            new(0, 0, sizeZ),       new(sizeX, 0, sizeZ),
            new(sizeX, sizeY, sizeZ), new(0, sizeY, sizeZ)
        };

        var triangles = new List<Triangle>();
        var faceIds = new List<int>();

        void Quad(int a, int b, int c, int d, int face)
        {
            // Wound counter-clockwise as seen from outside.
            triangles.Add(new Triangle(a, b, c)); faceIds.Add(face);
            triangles.Add(new Triangle(a, c, d)); faceIds.Add(face);
        }

        Quad(0, 4, 7, 3, 0); // x-min
        Quad(1, 2, 6, 5, 1); // x-max
        Quad(0, 1, 5, 4, 2); // y-min
        Quad(3, 7, 6, 2, 3); // y-max
        Quad(0, 3, 2, 1, 4); // z-min
        Quad(4, 5, 6, 7, 5); // z-max

        return new TriangleMesh(v, triangles, faceIds);
    }

    /// <summary>A thin rectangular plate — a box with thickness along Z.</summary>
    public static TriangleMesh CreatePlate(double sizeX, double sizeY, double thickness) =>
        CreateBox(sizeX, sizeY, thickness);

    /// <summary>
    /// Cylinder with its base circle at z = 0, axis along +Z.
    /// Face ids: 0 = bottom cap, 1 = top cap, 2 = lateral surface.
    /// </summary>
    public static TriangleMesh CreateCylinder(double radius, double height, int segments = 48)
    {
        if (radius <= 0 || height <= 0)
            throw new ArgumentException("Cylinder dimensions must be positive.");
        if (segments < 3)
            throw new ArgumentOutOfRangeException(nameof(segments), "At least 3 segments required.");

        var v = new List<Vector3D>();
        for (int ring = 0; ring <= 1; ring++)
        {
            double z = ring * height;
            for (int s = 0; s < segments; s++)
            {
                double angle = 2 * Math.PI * s / segments;
                v.Add(new Vector3D(radius * Math.Cos(angle), radius * Math.Sin(angle), z));
            }
        }
        int bottomCenter = v.Count; v.Add(new Vector3D(0, 0, 0));
        int topCenter = v.Count; v.Add(new Vector3D(0, 0, height));

        var triangles = new List<Triangle>();
        var faceIds = new List<int>();
        for (int s = 0; s < segments; s++)
        {
            int next = (s + 1) % segments;
            int b0 = s, b1 = next;                       // bottom ring
            int t0 = segments + s, t1 = segments + next; // top ring

            // Bottom cap (normal -Z ⇒ clockwise when viewed from +Z)
            triangles.Add(new Triangle(bottomCenter, b1, b0)); faceIds.Add(0);
            // Top cap (normal +Z)
            triangles.Add(new Triangle(topCenter, t0, t1)); faceIds.Add(1);
            // Side quad
            triangles.Add(new Triangle(b0, b1, t1)); faceIds.Add(2);
            triangles.Add(new Triangle(b0, t1, t0)); faceIds.Add(2);
        }

        return new TriangleMesh(v, triangles, faceIds);
    }
}
