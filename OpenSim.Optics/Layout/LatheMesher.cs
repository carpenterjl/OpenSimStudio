using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Layout;

/// <summary>How finely the lathe divides a surface: rings from the vertex to the clear edge,
/// and segments around the axis.</summary>
public readonly record struct LatheResolution(int RadialSamples = 16, int Segments = 64)
{
    public static LatheResolution Default { get; } = new(16, 64);

    internal void Check()
    {
        if (RadialSamples < 1) throw new ArgumentOutOfRangeException(nameof(RadialSamples), RadialSamples, "At least one ring is needed.");
        if (Segments < 3) throw new ArgumentOutOfRangeException(nameof(Segments), Segments, "At least three segments are needed.");
    }
}

/// <summary>
/// Triangle meshes of lens bodies, mirrors and apertures, built by sweeping each surface's sag
/// around its local z on a polar grid (a lathe; for a surface without rotational symmetry the
/// same sweep follows its sag in every direction). Every vertex is placed by
/// <see cref="LayoutSurface.GlobalPoint"/>, so the mesh is in the global frame and its meridian
/// is exactly the cross-section's profile.
/// </summary>
public static class LatheMesher
{
    /// <summary>Face ids of a lens solid.</summary>
    public const int FrontFace = 0, FrontFlat = 1, BackFace = 2, BackFlat = 3, EdgeFace = 4;

    /// <summary>
    /// The closed solid between two surfaces: the front face, out to the edge radius (sag inside
    /// its clear aperture, a flat beyond it), the back face likewise, and the cylindrical edge
    /// joining their rims. Watertight (every edge shared by two triangles, in opposite
    /// directions) and wound so every triangle's normal points out of the body, whichever way
    /// the thickness runs.
    /// </summary>
    public static TriangleMesh Solid(LayoutSurface front, LayoutSurface back, double edgeRadius,
        LatheResolution resolution)
    {
        resolution.Check();
        var vertices = new List<Vector3D>();
        var triangles = new List<Triangle>();
        var faces = new List<int>();
        int segments = resolution.Segments;

        AddDisk(front, edgeRadius, resolution, vertices, triangles, faces, FrontFace, FrontFlat, reverse: false, out int frontRim);
        AddDisk(back, edgeRadius, resolution, vertices, triangles, faces, BackFace, BackFlat, reverse: true, out int backRim);

        // The edge band: front rim to back rim (outward when the front faces −z and the
        // back +z; the volume check below turns it all round otherwise).
        for (int j = 0; j < segments; j++)
        {
            int j1 = (j + 1) % segments;
            int f0 = frontRim + j, f1 = frontRim + j1, b0 = backRim + j, b1 = backRim + j1;
            triangles.Add(new Triangle(f0, f1, b1)); faces.Add(EdgeFace);
            triangles.Add(new Triangle(f0, b1, b0)); faces.Add(EdgeFace);
        }

        var mesh = new TriangleMesh(vertices, triangles, faces);
        if (mesh.ComputeSignedVolume() >= 0) return mesh;
        return new TriangleMesh(vertices, Reversed(triangles), faces);
    }

    /// <summary>An open disk on one surface out to <paramref name="outerRadius"/> (a mirror
    /// drawn as a surface only). Face ids <see cref="FrontFace"/> and <see cref="FrontFlat"/>.
    /// Wound with its normal toward the surface's local −z.</summary>
    public static TriangleMesh Cap(LayoutSurface surface, double outerRadius, LatheResolution resolution)
    {
        resolution.Check();
        var vertices = new List<Vector3D>();
        var triangles = new List<Triangle>();
        var faces = new List<int>();
        AddDisk(surface, outerRadius, resolution, vertices, triangles, faces, FrontFace, FrontFlat, reverse: false, out _);
        return new TriangleMesh(vertices, triangles, faces);
    }

    /// <summary>An annulus on a surface from <paramref name="innerRadius"/> to
    /// <paramref name="outerRadius"/> (the aperture at a stop), on the drawn surface (sag inside
    /// the clear aperture, flat beyond). One face id, 0.</summary>
    public static TriangleMesh Annulus(LayoutSurface surface, double innerRadius, double outerRadius,
        LatheResolution resolution)
    {
        resolution.Check();
        if (!(innerRadius > 0) || !(outerRadius > innerRadius))
            throw new ArgumentOutOfRangeException(nameof(outerRadius), "An annulus needs 0 < inner < outer radius.");
        int segments = resolution.Segments;
        var vertices = new List<Vector3D>(2 * segments);
        foreach (double r in new[] { innerRadius, outerRadius })
            for (int j = 0; j < segments; j++)
            {
                var (s, c) = Azimuth(j, segments);
                vertices.Add(surface.GlobalPoint(r * c, r * s));
            }
        var triangles = new List<Triangle>(2 * segments);
        for (int j = 0; j < segments; j++)
        {
            int j1 = (j + 1) % segments;
            triangles.Add(new Triangle(j, segments + j1, segments + j));
            triangles.Add(new Triangle(j, j1, segments + j1));
        }
        return new TriangleMesh(vertices, triangles, new int[triangles.Count]);
    }

    /// <summary>
    /// A disk of rings on one surface: the vertex, then one ring per radius of
    /// <see cref="SagSampler.Radii"/>. Wound toward local −z (reversed: toward +z).
    /// <paramref name="rimStart"/> is the index of the outermost ring's first vertex.
    /// </summary>
    private static void AddDisk(LayoutSurface surface, double outerRadius, LatheResolution resolution,
        List<Vector3D> vertices, List<Triangle> triangles, List<int> faces, int opticalFace, int flatFace,
        bool reverse, out int rimStart)
    {
        int segments = resolution.Segments;
        var radii = SagSampler.Radii(surface.ClearSemiDiameter, outerRadius, resolution.RadialSamples);
        int center = vertices.Count;
        vertices.Add(surface.GlobalPoint(0, 0));
        int first = vertices.Count;
        foreach (double r in radii)
            for (int j = 0; j < segments; j++)
            {
                var (s, c) = Azimuth(j, segments);
                vertices.Add(surface.GlobalPoint(r * c, r * s));
            }

        void Add(int a, int b, int c, int face)
        {
            triangles.Add(reverse ? new Triangle(a, c, b) : new Triangle(a, b, c));
            faces.Add(face);
        }

        // Fan to the first ring: (vertex, ring[j+1], ring[j]) has its normal toward −z.
        for (int j = 0; j < segments; j++)
            Add(center, first + (j + 1) % segments, first + j, opticalFace);

        for (int k = 0; k + 1 < radii.Length; k++)
        {
            int inner = first + k * segments, outer = inner + segments;
            int face = radii[k + 1] > surface.ClearSemiDiameter ? flatFace : opticalFace;
            for (int j = 0; j < segments; j++)
            {
                int j1 = (j + 1) % segments;
                Add(inner + j, outer + j1, outer + j, face);
                Add(inner + j, inner + j1, outer + j1, face);
            }
        }
        rimStart = first + (radii.Length - 1) * segments;
    }

    /// <summary>Sine and cosine of segment j of n; the quarter turns are exact when n is a
    /// multiple of four, so the four meridians the cross-section and the wireframe use lie on
    /// rings of the mesh.</summary>
    internal static (double Sin, double Cos) Azimuth(int j, int segments) =>
        OpticsConventions.SinCosDegrees(360.0 * j / segments);

    private static List<Triangle> Reversed(List<Triangle> triangles)
    {
        var r = new List<Triangle>(triangles.Count);
        foreach (var t in triangles) r.Add(new Triangle(t.A, t.C, t.B));
        return r;
    }
}
