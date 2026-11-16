using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>
/// Triangle meshes of single non-sequential objects for OpticStudio's View Current Object
/// (help, "View Current Object": "a simplified version of the NSC Shaded Model ... for viewing one
/// object at a time"; plan item OP-71). The mesh is in the object's local frame, in the units its
/// geometry was built in, and each triangle carries the object's face number as its face id, so
/// a viewer can colour or pick faces. For display only: the trace uses the exact geometry.
/// </summary>
public static class NscObjectMesher
{
    /// <summary>The mesh of <paramref name="geometry"/>: <paramref name="rings"/> divisions from
    /// the axis or pole outwards, <paramref name="segments"/> around. Throws
    /// <see cref="NotSupportedException"/> for a shape this mesher does not know.</summary>
    public static TriangleMesh Mesh(NscGeometry geometry, int rings = 16, int segments = 48)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (rings < 1) throw new ArgumentOutOfRangeException(nameof(rings), rings, "At least one ring is needed.");
        if (segments < 3) throw new ArgumentOutOfRangeException(nameof(segments), segments, "At least three segments are needed.");
        return geometry switch
        {
            NscSphere s => Sphere(s.Radius, rings, segments),
            NscRectangle r => Rectangle(r.HalfWidthX, r.HalfWidthY),
            NscStandardLens l => Lens(l, rings, segments),
            _ => throw new NotSupportedException($"View Current Object: the {geometry.TypeName} object has no mesh in this build."),
        };
    }

    private static TriangleMesh Rectangle(double hx, double hy)
    {
        var v = new[] { new Vector3D(-hx, -hy, 0), new Vector3D(hx, -hy, 0), new Vector3D(hx, hy, 0), new Vector3D(-hx, hy, 0) };
        return new TriangleMesh(v, new[] { new Triangle(0, 1, 2), new Triangle(0, 2, 3) }, new[] { 0, 0 });
    }

    private static TriangleMesh Sphere(double radius, int rings, int segments)
    {
        int lat = Math.Max(2, 2 * rings);
        var vertices = new List<Vector3D> { new(0, 0, radius) };
        for (int i = 1; i < lat; i++)
        {
            double theta = Math.PI * i / lat;
            for (int j = 0; j < segments; j++)
            {
                double phi = 2 * Math.PI * j / segments;
                vertices.Add(new Vector3D(radius * Math.Sin(theta) * Math.Cos(phi), radius * Math.Sin(theta) * Math.Sin(phi), radius * Math.Cos(theta)));
            }
        }
        vertices.Add(new Vector3D(0, 0, -radius));
        int south = vertices.Count - 1;
        var triangles = new List<Triangle>();
        int Ring(int i, int j) => 1 + (i - 1) * segments + (j % segments);
        for (int j = 0; j < segments; j++) triangles.Add(new Triangle(0, Ring(1, j), Ring(1, j + 1)));
        for (int i = 1; i < lat - 1; i++)
            for (int j = 0; j < segments; j++)
            {
                triangles.Add(new Triangle(Ring(i, j), Ring(i + 1, j), Ring(i + 1, j + 1)));
                triangles.Add(new Triangle(Ring(i, j), Ring(i + 1, j + 1), Ring(i, j + 1)));
            }
        for (int j = 0; j < segments; j++) triangles.Add(new Triangle(south, Ring(lat - 1, j + 1), Ring(lat - 1, j)));
        return new TriangleMesh(vertices, triangles, new int[triangles.Count]);
    }

    /// <summary>The Standard Lens: the front face (face 1) and its flat annulus (face 0), the
    /// rear face (face 2) and its annulus (face 0), and the side (face 0) joining the edges.</summary>
    private static TriangleMesh Lens(NscStandardLens l, int rings, int segments)
    {
        var vertices = new List<Vector3D>();
        var triangles = new List<Triangle>();
        var faces = new List<int>();

        // Profile of each side: (r, z, face) from the axis outwards; the ring between two
        // profile points takes the outer point's face.
        var front = Profile(l.FrontCurvature, l.FrontConic, l.FrontClear, l.FrontEdge, 0, l.FrontEdgeZ, rings);
        var back = Profile(l.BackCurvature, l.BackConic, l.BackClear, l.BackEdge, l.Thickness, l.BackEdgeZ, rings);
        int frontRim = Disk(front, reverse: true);
        int backRim = Disk(back, reverse: false);
        for (int j = 0; j < segments; j++)
        {
            int a = frontRim + j, b = frontRim + (j + 1) % segments, c = backRim + (j + 1) % segments, d = backRim + j;
            Add(a, b, c, 0);
            Add(a, c, d, 0);
        }
        return new TriangleMesh(vertices, triangles, faces);

        int Disk(List<(double R, double Z, int Face)> profile, bool reverse)
        {
            int centre = vertices.Count;
            vertices.Add(new Vector3D(0, 0, profile[0].Z));
            int previous = -1;
            for (int k = 1; k < profile.Count; k++)
            {
                int start = vertices.Count;
                for (int j = 0; j < segments; j++)
                {
                    double phi = 2 * Math.PI * j / segments;
                    vertices.Add(new Vector3D(profile[k].R * Math.Cos(phi), profile[k].R * Math.Sin(phi), profile[k].Z));
                }
                int face = profile[k].Face;
                for (int j = 0; j < segments; j++)
                {
                    int j1 = (j + 1) % segments;
                    if (previous < 0) Tri(centre, start + j, start + j1, face, reverse);
                    else
                    {
                        Tri(previous + j, start + j, start + j1, face, reverse);
                        Tri(previous + j, start + j1, previous + j1, face, reverse);
                    }
                }
                previous = start;
            }
            return previous;
        }

        void Tri(int a, int b, int c, int face, bool reverse)
        {
            if (reverse) Add(a, c, b, face);
            else Add(a, b, c, face);
        }

        void Add(int a, int b, int c, int face)
        {
            triangles.Add(new Triangle(a, b, c));
            faces.Add(face);
        }
    }

    private static List<(double R, double Z, int Face)> Profile(double c, double k, double clear, double edge, double vertexZ, double edgeZ, int rings)
    {
        int faceNumber = vertexZ == 0 ? 1 : 2;
        var profile = new List<(double, double, int)> { (0, vertexZ, faceNumber) };
        for (int i = 1; i <= rings; i++)
        {
            double r = clear * i / rings;
            double under = 1 - (1 + k) * c * c * r * r;
            double sag = c * r * r / (1 + Math.Sqrt(Math.Max(under, 0)));
            profile.Add((r, vertexZ + sag, faceNumber));
        }
        if (edge > clear) profile.Add((edge, edgeZ, 0));
        return profile;
    }
}
