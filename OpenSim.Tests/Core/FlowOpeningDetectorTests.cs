using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Core;

/// <summary>
/// Finding the bore mouths of a fluid volume. The claim is that the numbers a user would
/// otherwise type — which face, which rectangle, how big — come from the geometry itself,
/// so they cannot disagree with the model on screen.
/// </summary>
public class FlowOpeningDetectorTests
{
    /// <summary>A closed box surface [x0,x1]x[y0,y1]x[z0,z1], two triangles per face,
    /// each face carrying its own id.</summary>
    private static TriangleMesh Box(double x0, double x1, double y0, double y1,
        double z0, double z1)
    {
        var v = new List<Vector3D>();
        for (int k = 0; k < 2; k++)
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                    v.Add(new Vector3D(i == 0 ? x0 : x1, j == 0 ? y0 : y1, k == 0 ? z0 : z1));
        int N(int i, int j, int k) => i + 2 * j + 4 * k;
        var tris = new List<Triangle>();
        var ids = new List<int>();
        void Quad(int a, int b, int c, int d, int face)
        {
            tris.Add(new Triangle(a, b, c)); ids.Add(face);
            tris.Add(new Triangle(a, c, d)); ids.Add(face);
        }
        Quad(N(0, 0, 0), N(0, 0, 1), N(0, 1, 1), N(0, 1, 0), 0);   // x-min
        Quad(N(1, 0, 0), N(1, 1, 0), N(1, 1, 1), N(1, 0, 1), 1);   // x-max
        Quad(N(0, 0, 0), N(1, 0, 0), N(1, 0, 1), N(0, 0, 1), 2);   // y-min
        Quad(N(0, 1, 0), N(0, 1, 1), N(1, 1, 1), N(1, 1, 0), 3);   // y-max
        Quad(N(0, 0, 0), N(0, 1, 0), N(1, 1, 0), N(1, 0, 0), 4);   // z-min
        Quad(N(0, 0, 1), N(1, 0, 1), N(1, 1, 1), N(0, 1, 1), 5);   // z-max
        return new TriangleMesh(v, tris, ids);
    }

    private static TriangleMesh Merge(params TriangleMesh[] parts)
    {
        var v = new List<Vector3D>();
        var t = new List<Triangle>();
        var ids = new List<int>();
        foreach (var part in parts)
        {
            int b = v.Count;
            v.AddRange(part.Vertices);
            for (int i = 0; i < part.Triangles.Count; i++)
            {
                var tri = part.Triangles[i];
                t.Add(new Triangle(tri.A + b, tri.B + b, tri.C + b));
                ids.Add(part.TriangleFaceIds[i] + 6 * (t.Count / 12));
            }
        }
        return new TriangleMesh(v, t, ids);
    }

    [Fact]
    public void ATubeThroughABlock_ReportsBothMouths_WithTheirRealOpenArea()
    {
        // A 20x20 mm square passage running the full length of a 300 mm domain.
        var fluid = Box(0, 0.300, 0.090, 0.110, 0.015, 0.035);
        var domain = new Aabb(new Vector3D(0, 0.085, 0.010), new Vector3D(0.300, 0.115, 0.040));

        var found = FlowOpeningDetector.Detect(fluid, domain);

        Assert.Equal(2, found.Count);
        var xmin = Assert.Single(found, c => c.Face == BoxFace.XMin);
        var xmax = Assert.Single(found, c => c.Face == BoxFace.XMax);

        // The rectangle is the passage's own cross-section, in the (y, z) convention.
        Assert.Equal(0.090, xmin.UMin, 12);
        Assert.Equal(0.110, xmin.UMax, 12);
        Assert.Equal(0.015, xmin.VMin, 12);
        Assert.Equal(0.035, xmin.VMax, 12);
        // Open area is the TRIANGLES' area, not the rectangle's — identical for a square
        // mouth, and π/4 of it for a round one, which is why it is measured not assumed.
        Assert.Equal(0.020 * 0.020, xmin.Area, 12);
        Assert.Equal(0.020 * 0.020, xmax.Area, 12);
        Assert.Equal(0.300, xmax.Center.X, 12);
        Assert.Equal(0.100, xmax.Center.Y, 12);

        // The mouths are the only faces in a domain plane: the passage's sides lie inside
        // the box, so a triangle merely touching a plane along an edge is not a mouth.
        Assert.DoesNotContain(found, c => c.Face is BoxFace.YMin or BoxFace.YMax
                                              or BoxFace.ZMin or BoxFace.ZMax);
    }

    [Fact]
    public void TwoPassagesOnTheSameFace_AreTwoOpenings_NotOneRectangleSpanningBoth()
    {
        // The failure this exists to stop: a bbox over all coplanar triangles would call
        // two 20 mm bores 100 mm apart a single 120 mm inlet.
        var fluid = Merge(
            Box(0, 0.2, 0.010, 0.030, 0.010, 0.030),
            Box(0, 0.2, 0.110, 0.130, 0.010, 0.030));
        var domain = new Aabb(new Vector3D(0, 0, 0), new Vector3D(0.2, 0.14, 0.04));

        var found = FlowOpeningDetector.Detect(fluid, domain).Where(c => c.Face == BoxFace.XMin)
            .ToList();

        Assert.Equal(2, found.Count);
        foreach (var c in found)
        {
            Assert.Equal(0.020, c.UMax - c.UMin, 12);
            Assert.Equal(0.020 * 0.020, c.Area, 12);
        }
        Assert.Equal(new[] { 0.020, 0.120 }, found.Select(c => Math.Round(c.Center.Y, 6)).OrderBy(y => y));
    }

    [Fact]
    public void ASealedVolume_ReportsNoOpenings()
    {
        var fluid = Box(0.05, 0.15, 0.05, 0.15, 0.05, 0.15);
        var domain = new Aabb(new Vector3D(0, 0, 0), new Vector3D(0.2, 0.2, 0.2));
        Assert.Empty(FlowOpeningDetector.Detect(fluid, domain));
    }

    [Fact]
    public void ACandidate_BecomesTheOpeningTheUserAsksFor()
    {
        var fluid = Box(0, 0.2, 0.05, 0.15, 0.05, 0.15);
        var domain = new Aabb(new Vector3D(0, 0, 0), new Vector3D(0.2, 0.2, 0.2));
        var candidate = Assert.Single(FlowOpeningDetector.Detect(fluid, domain),
            c => c.Face == BoxFace.XMin);

        var inlet = candidate.ToOpening(FlowFaceKind.InletVelocity, new Vector3D(0.1, 0, 0), 363.15);
        Assert.Equal(BoxFace.XMin, inlet.Face);
        Assert.Equal(FlowFaceKind.InletVelocity, inlet.Kind);
        Assert.Equal(363.15, inlet.Temperature);
        Assert.Equal(candidate.UMin, inlet.UMin, 12);

        var outlet = candidate.ToOpening(FlowFaceKind.OutletPressure, new Vector3D(0, 0, 0), null);
        Assert.Null(outlet.Temperature);
    }
}
