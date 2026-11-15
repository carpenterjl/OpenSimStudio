using OpenSim.Core.Numerics;
using OpenSim.Geometry.Export;
using OpenSim.Geometry.Step;
using OpenSim.Geometry.Step.Part21;
using OpenSim.Geometry.Step.Schema;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.CadExport;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using static OpenSim.Tests.Optics.CadExportFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-96, the gate's half that runs here (docs/Optics-Gates.md): a lens exported as STEP and read
/// back by OpenSim's own STEP importer has the same volume and surface areas, to the importer's
/// tessellation and the fit tolerance. The other half, opening the file as a valid solid in a CAD
/// program, needs a CAD program on the owner's machine (none is installed where this was built).
/// <para>
/// The importer meshes every face with chords of sagitta at most c = RelativeChordTolerance × its
/// bounding diagonal (StepImportOptions, SolidTessellator); its interior lattice is spaced so a
/// lattice edge has sagitta c, and the constrained triangulation can join points up to √2 lattice
/// spacings apart, sagitta 2c. So every triangle lies within 2c (+ the fit deviation for a B-spline
/// face) of the exact surface: the mesh volume is within A·(2c + fit) of the exact one, and for the
/// all-convex singlet it is not larger (an inscribed polyhedron). A flat triangle of height h under
/// a sphere of radius R has an area smaller by the fraction h/(2R) to first order (cap 2πRh against
/// disc π(2Rh − h²), derived), so the triangles cover their part of a face with an area short by at
/// most A·(2c)/R (taken with a factor two: 4c/R). The face's boundary is meshed by chords too: along
/// a rim of perimeter P each chord of length L and sagitta s ≤ c leaves out the circular segment
/// between it and the rim, (2/3)·L·s to leading order (derived), so a band of area (2/3)·P·c measured
/// in the rim's plane, or (2/3)·P·c/cos α on a face meeting that plane at α (14.5° for the front
/// sphere at its rim, 0 for the planes and cylinder; 2/(3 cos α) below 1 for α under 48°), is missing
/// at every rim: P·c per rim bounds it. For the front sphere this term (about 3.9e-8 m² here) is
/// about five times the interior one.
/// </para>
/// </summary>
public sealed class CadExportStepTests
{
    private const double RelativeChord = 1e-6;

    /// <summary>
    /// An upper bound on the importer's bounding diagonal for the singlets: the box its estimate
    /// spans (SolidTessellator.EstimateDiagonal adds each sphere's centre ± R along its frame axes,
    /// and a B-spline face's control points), here x, y ∈ ±|R2| and z from the back sphere's far
    /// side, T + R2 − |R2|, to the front sphere's, 2·R1 (derived from the fixture).
    /// </summary>
    private static double DiagonalBound()
    {
        double xy = 2 * Math.Abs(R2);
        double z = 2 * R1 - (T + R2 - Math.Abs(R2));
        return Math.Sqrt(2 * xy * xy + z * z);
    }

    private static StepImportReport Import(string text) =>
        new StepImporter(StepImportOptions.Default with { RelativeChordTolerance = RelativeChord }).ImportText(text);

    private static string Step(SequentialLens lens, CadExportSettings settings, out CadWriteReport report)
    {
        var scene = Scene(lens, settings);
        var options = CadExporter.Options(OpticalSystemData.Default, settings, Stamp, "test lens");
        (string text, report) = StepWriter.WriteText(scene, options);
        return text;
    }

    [Fact]
    public void SphericalSinglet_RoundTrips_SameVolumeAndFaceAreas_AsTheClosedForm()
    {
        string text = Step(Singlet(), GeometryOnly(), out var written);
        Assert.Equal(1, written.Solids);
        Assert.Equal(12, written.Faces);   // sphere 4 + cylinder 4 + sphere 4 quarter faces
        Assert.Equal(0, written.MaxFitDeviation);   // spheres and a cylinder: no spline

        var report = Import(text);
        var mesh = report.Mesh;
        Assert.DoesNotContain(report.Notes, n => n.Contains("inside-out"));
        Assert.True(mesh.IsWatertight());
        Assert.Equal(12, mesh.FaceCount);

        // Every vertex of a face lies on that face's exact surface (to rounding): the geometry
        // written is the lens itself.
        var front = new Vector3D(0, 0, R1);
        var back = new Vector3D(0, 0, T + R2);
        for (int t = 0; t < mesh.Triangles.Count; t++)
        {
            int f = mesh.TriangleFaceIds[t];
            var tri = mesh.Triangles[t];
            foreach (int i in new[] { tri.A, tri.B, tri.C })
            {
                var p = mesh.Vertices[i];
                if (f < 4) Assert.Equal(Math.Abs(R1), (p - front).Length, 12);
                else if (f < 8) Assert.Equal(Semi, Math.Sqrt(p.X * p.X + p.Y * p.Y), 12);
                else Assert.Equal(Math.Abs(R2), (p - back).Length, 12);
            }
        }

        double c = RelativeChord * DiagonalBound();
        double frontArea = 2 * Math.PI * Math.Abs(R1) * CapHeight(R1, Semi);
        double backArea = 2 * Math.PI * Math.Abs(R2) * CapHeight(R2, Semi);
        double edgeArea = 2 * Math.PI * Semi * SingletEdge();
        double total = frontArea + backArea + edgeArea;

        double exact = SingletVolume();
        // An independent oracle for the closed form: OP-44's element volume.
        var lens = Singlet();
        Assert.Equal(exact, ElementVolume.Compute(lens.Surfaces[1], Semi, lens.Surfaces[2], Semi, T, "singlet").CubicMeters, 15);

        double volume = mesh.ComputeSignedVolume();
        Assert.True(volume <= exact * (1 + 1e-12), $"inscribed mesh volume {volume} exceeds the exact {exact}");
        Assert.True(exact - volume <= total * 2 * c, $"volume {volume} vs exact {exact}: deficit {exact - volume} > A·2c = {total * 2 * c}");

        double rim = 2 * Math.PI * Semi * c;   // the boundary band of one rim, P·c
        Assert.InRange(Area(mesh, f => f < 4), frontArea * (1 - 4 * c / Math.Abs(R1)) - rim, frontArea * (1 + 1e-12));
        Assert.InRange(Area(mesh, f => f >= 4 && f < 8), edgeArea * (1 - 4 * c / Semi) - 2 * rim, edgeArea * (1 + 1e-12));
        Assert.InRange(Area(mesh, f => f >= 8), backArea * (1 - 4 * c / Math.Abs(R2)) - rim, backArea * (1 + 1e-12));
    }

    [Fact]
    public void AsphericSinglet_TheBSplineFace_IsWithinTheStatedTolerance_AndTheVolumeRoundTrips()
    {
        const double tolerance = 1e-7; // 1E-4 mm, OpticStudio's coarsest choice
        var lens = AsphereSinglet();
        var settings = GeometryOnly() with { Tolerance = 1e-4 };
        string text = Step(lens, settings, out var written);
        Assert.Equal(1 + 4 + 4, written.Faces);   // one trimmed B-spline face, the cylinder, the back sphere
        Assert.InRange(written.MaxFitDeviation, 1e-15, tolerance / 2);

        // The B-spline surface as written, read back through the importer's schema: its deviation
        // from the exact Even Asphere sag inside the rim, on a grid the fit never saw.
        var file = Part21Parser.Parse(text);
        var units = StepUnits.Resolve(file);
        Assert.Equal(1e-3, units.MetersPerUnit, 15);
        Assert.Equal(tolerance, units.UncertaintyMeters!.Value, 1e-20);
        var resolver = new StepEntityResolver(file, units.MetersPerUnit);
        int id = file.Instances.First(kv => kv.Value.Has("B_SPLINE_SURFACE_WITH_KNOTS")).Key;
        var surface = (StepBSplineSurface)resolver.Surface(id, 0);
        var front = lens.Surfaces[1];
        var shape = (EvenAsphere)front.Shape!;
        double worst = 0;
        const int n = 157;
        for (int i = 0; i <= n; i++)
            for (int k = 0; k <= n; k++)
            {
                double x = -Semi + 2 * Semi * i / n, y = -Semi + 2 * Semi * k / n;
                double r = Math.Sqrt(x * x + y * y);
                if (r > Semi) continue;
                var p = surface.Point(x / units.MetersPerUnit, y / units.MetersPerUnit);
                // x(u) = u: the Greville abscissae reproduce the identity, exactly up to the rounding
                // of the sum and of the mm-to-m scaling (a few ulps of 12.5 mm, ulp about 1.7e-18 m).
                Assert.Equal(x, p.X, 1e-16);
                Assert.Equal(y, p.Y, 1e-16);
                Assert.True(shape.TryRadialSag(r, front, out double z, out _));
                worst = Math.Max(worst, Math.Abs(p.Z - z));
            }
        Assert.True(worst <= tolerance, $"B-spline face deviates {worst} m from the asphere; the stated tolerance is {tolerance} m");

        var report = Import(text);
        Assert.DoesNotContain(report.Notes, n => n.Contains("inside-out"));
        Assert.True(report.Mesh.IsWatertight());
        double exact = ElementVolume.Compute(lens.Surfaces[1], Semi, lens.Surfaces[2], Semi, T, "asphere singlet").CubicMeters;
        var body = Scene(lens, settings).Bodies[0];
        double area = MeridianArea(body);
        double c = RelativeChord * DiagonalBound();
        Assert.True(Math.Abs(report.Mesh.ComputeSignedVolume() - exact) <= area * (2 * c + tolerance),
            $"volume {report.Mesh.ComputeSignedVolume()} vs {exact}, bound {area * (2 * c + tolerance)}");
        Assert.Equal(exact, MeridianVolume(body), 13);   // the meridian quadrature agrees with ElementVolume
    }

    [Fact]
    public void TheShell_IsClosedAndManifold_AndEveryOuterLoopRunsCounterclockwiseAboutItsOutwardNormal()
    {
        // A plano-convex lens with a flat beyond the front's clear aperture and a squared edge:
        // a plane cap, a plane annulus with an inner bound, a cylinder and a sphere cap.
        var lens = Lens(Sphere(double.PositiveInfinity, T, 1.6, 0.010, stop: true) with { MechanicalSemiDiameter = Semi },
            Sphere(-0.08, 0.09, 1, Semi));
        string text = Step(lens, GeometryOnly(), out var written);
        var file = Part21Parser.Parse(text);
        var units = StepUnits.Resolve(file);
        var solid = new StepEntityResolver(file, units.MetersPerUnit).ResolveSolids().Single();

        // Every edge is used exactly twice, once in each direction.
        var uses = new Dictionary<int, (int Forward, int Backward)>();
        foreach (var face in solid.Outer.Faces)
            foreach (var bound in face.Bounds)
                foreach (var use in bound.Loop.Edges)
                {
                    uses.TryGetValue(use.Edge.Id, out var u);
                    uses[use.Edge.Id] = use.Forward ? (u.Forward + 1, u.Backward) : (u.Forward, u.Backward + 1);
                }
        Assert.All(uses.Values, u => Assert.Equal((1, 1), u));

        // The planar faces: the plane's normal (its frame z, same_sense .T.) points out of the lens
        // (−z at the front, which sits at z ≤ 0 of the body), the outer loop runs counterclockwise
        // about it and the annulus's inner loop clockwise.
        var planes = solid.Outer.Faces.Where(f => f.Surface is StepPlane).ToList();
        Assert.Equal(2, planes.Count);   // the plane cap and the annulus (no flat on the back: it is the larger)
        foreach (var face in planes)
        {
            var normal = ((StepPlane)face.Surface).Frame.ZAxis * (face.SameSense ? 1 : -1);
            Assert.True(normal.Z < 0);
            foreach (var bound in face.Bounds)
            {
                double d = Vector3D.Dot(VectorArea(bound.Loop), normal);
                Assert.True(bound.IsOuter ? d > 0 : d < 0, $"face #{face.Id}: loop #{bound.Loop.Id} runs the wrong way ({d})");
            }
        }

        // The sphere and cylinder faces: the composed normal at a boundary point, against the loop.
        foreach (var face in solid.Outer.Faces.Where(f => f.Surface is StepSphere or StepCylinder))
        {
            var loop = face.Bounds.Single().Loop;
            var centroid = Sample(loop.Edges[0])[8];
            var normal = face.Surface switch
            {
                StepSphere s => (centroid - s.Frame.Origin).Normalized(),
                StepCylinder cyl => new Vector3D(centroid.X, centroid.Y, 0).Normalized(),
                _ => throw new InvalidOperationException(),
            } * (face.SameSense ? 1 : -1);
            Assert.True(Vector3D.Dot(VectorArea(loop), normal) > 0, $"face #{face.Id}");
        }

        var report = Import(text);
        Assert.DoesNotContain(report.Notes, n => n.Contains("inside-out"));
        Assert.True(report.Mesh.IsWatertight());
        Assert.Equal(written.Faces, report.Mesh.FaceCount);
        Assert.Equal(1 + 1 + 4 + 4, written.Faces);
    }

    [Theory]
    [InlineData(CadEdgeStyle.Squared, 4 + 1 + 4 + 4)]   // the smaller back face gets a flat
    [InlineData(CadEdgeStyle.Tapered, 4 + 4 + 4)]       // a cone joins the rims
    [InlineData(CadEdgeStyle.FlatToNext, 4 + 4 + 1 + 4)] // the back face is drawn out to the front's edge (with its flat)
    public void EdgeStyles_RoundTrip_ToTheMeridianVolume(CadEdgeStyle style, int faces)
    {
        var lens = Lens(Sphere(R1, T, 1.6, Semi, stop: true), Sphere(R2, 0.09, 1, 0.010));
        var settings = GeometryOnly() with { EdgeStyle = style };
        string text = Step(lens, settings, out var written);
        Assert.Equal(faces, written.Faces);
        var report = Import(text);
        Assert.DoesNotContain(report.Notes, n => n.Contains("inside-out"));
        Assert.True(report.Mesh.IsWatertight());
        var body = Scene(lens, settings).Bodies[0];
        double exact = MeridianVolume(body);
        double c = RelativeChord * DiagonalBound();
        Assert.True(Math.Abs(report.Mesh.ComputeSignedVolume() - exact) <= MeridianArea(body) * 2 * c,
            $"{style}: {report.Mesh.ComputeSignedVolume()} vs {exact}");
        if (style == CadEdgeStyle.Tapered) Assert.Contains("CONICAL_SURFACE", text);
    }

    [Fact]
    public void AMirrorWithASubstrate_IsASolid_AndABareMirror_IsRefusedForStep()
    {
        var mirror = Lens(Sphere(-0.1, -0.05, 1, 0.010, stop: true) with { IsMirror = true });
        var refused = Assert.Throws<InvalidOperationException>(() => Scene(mirror, GeometryOnly()));
        Assert.Contains("substrate", refused.Message);

        var settings = GeometryOnly() with { MirrorSubstrateThickness = 2 };   // 2 mm behind the mirror's farthest point
        string text = Step(mirror, settings, out var written);
        Assert.Equal(1, written.Solids);
        var report = Import(text);
        Assert.DoesNotContain(report.Notes, n => n.Contains("inside-out"));
        Assert.True(report.Mesh.IsWatertight());

        // Closed form: the concave mirror (R = −100 mm, sag ≤ 0) has its farthest point toward +z
        // at the vertex, so the back is the plane z = 2 mm: V = π a²·2 mm − ∫2πr·sag dr.
        const double a = 0.010, back = 0.002;
        double exact = Math.PI * a * a * back - SagIntegral(-0.1, a);
        var body = Scene(mirror, settings).Bodies[0];
        Assert.Equal(exact, MeridianVolume(body), 13);
        // The importer's box: the sphere's centre (z = −0.1 m) ± R = 0.1 m in x, y and z, and the back at z = 2 mm.
        double c = RelativeChord * Math.Sqrt(0.2 * 0.2 + 0.2 * 0.2 + 0.202 * 0.202);
        Assert.True(Math.Abs(report.Mesh.ComputeSignedVolume() - exact) <= MeridianArea(body) * 2 * c);
    }
}
