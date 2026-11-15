using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Export;
using OpenSim.Geometry.Step.Tessellate;
using OpenSim.Optics;
using OpenSim.Optics.CadExport;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Freeform;
using static OpenSim.Tests.Optics.CadExportFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-96: STL against the closed form (divergence-theorem volume within the stated chordal error),
/// IGES and DXF re-read by the small parsers below (entity counts and key coordinates), the spline
/// fits, the rays and the refusals.
/// </summary>
public sealed class CadExportFormatTests
{
    // ---------------- STL ----------------

    [Fact]
    public void Stl_VolumeByTheDivergenceTheorem_IsWithinTheChordalErrorOfTheClosedForm()
    {
        const double delta = 1e-6;   // 1 µm chordal error
        var body = Scene(Singlet(), GeometryOnly(CadExportFormat.Stl)).Bodies[0];
        var mesh = RevolvedTessellator.Tessellate(body, delta);
        Assert.True(mesh.IsWatertight());

        double exact = SingletVolume();
        double area = 2 * Math.PI * (Math.Abs(R1) * CapHeight(R1, Semi) + Math.Abs(R2) * CapHeight(R2, Semi) + Semi * SingletEdge());
        double volume = mesh.ComputeSignedVolume();
        // Every vertex is on the surface and the singlet is convex, so the mesh is inscribed: no larger,
        // and short by at most the area times the chordal error.
        Assert.True(volume <= exact * (1 + 1e-12));
        Assert.True(exact - volume <= area * delta, $"deficit {exact - volume} > A·δ = {area * delta}");

        // The chordal error itself: every triangle's centroid on the front sphere within δ of it.
        var centre = new Vector3D(0, 0, R1);
        for (int t = 0; t < mesh.Triangles.Count; t++)
        {
            if (mesh.TriangleFaceIds[t] != 0) continue;
            var tri = mesh.Triangles[t];
            var g = (mesh.Vertices[tri.A] + mesh.Vertices[tri.B] + mesh.Vertices[tri.C]) / 3;
            Assert.InRange(Math.Abs(R1) - (g - centre).Length, -1e-15, delta);
            foreach (int i in new[] { tri.A, tri.B, tri.C }) Assert.Equal(Math.Abs(R1), (mesh.Vertices[i] - centre).Length, 13);
        }

        // The ASCII file read back: the same volume (to its nine-decimal mantissas), in millimeters.
        string path = TempFile(".stl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var settings = GeometryOnly(CadExportFormat.Stl) with { Tolerance = delta * 1e3, StlBinary = false };
            var report = CadExporter.Write(new CadScene(new[] { body }, Array.Empty<CadPolyline>()), path, settings,
                CadExporter.Options(OpticalSystemData.Default, settings, Stamp, "singlet"));
            Assert.Equal(mesh.Triangles.Count, report.Triangles);
            var lines = File.ReadAllLines(path);
            Assert.StartsWith("solid ", lines[0]);
            var vertices = lines.Where(l => l.StartsWith("vertex ")).Select(l =>
            {
                var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return new Vector3D(double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture), double.Parse(p[3], CultureInfo.InvariantCulture));
            }).ToList();
            Assert.Equal(3 * mesh.Triangles.Count, vertices.Count);
            double fileVolume = 0;
            for (int i = 0; i < vertices.Count; i += 3)
                fileVolume += Vector3D.Dot(vertices[i], Vector3D.Cross(vertices[i + 1], vertices[i + 2])) / 6;
            Assert.Equal(volume * 1e9, fileVolume, 1e-8 * volume * 1e9);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Stl_Binary_HasTheLayoutOfTheFormat()
    {
        var body = Scene(Singlet(), GeometryOnly(CadExportFormat.Stl)).Bodies[0];
        using var stream = new MemoryStream();
        var options = new CadWriteOptions { Tolerance = 1e-5, Timestamp = Stamp };
        var report = StlWriter.Write(new CadScene(new[] { body }, Array.Empty<CadPolyline>()), stream, options, binary: true);
        var bytes = stream.ToArray();
        Assert.Equal(84 + 50L * report.Triangles, bytes.Length);
        Assert.False(System.Text.Encoding.ASCII.GetString(bytes, 0, 5) == "solid");
        Assert.Equal((uint)report.Triangles, BitConverter.ToUInt32(bytes, 80));
    }

    // ---------------- IGES ----------------

    private sealed record IgesEntity(int Type, int Form, int Level, int Transform, double[] Values);

    /// <summary>A small IGES 5.3 reader for the test: the fixed-format sections, the DE pairs, the
    /// PD records split at the delimiters, D exponents read as E.</summary>
    private static (List<IgesEntity> Entities, Dictionary<char, int> Lines, string Terminate) ReadIges(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.Equal(80, l.Length));
        var count = lines.GroupBy(l => l[72]).ToDictionary(g => g.Key, g => g.Count());
        var de = lines.Where(l => l[72] == 'D').ToList();
        var pd = lines.Where(l => l[72] == 'P').ToList();
        int Field(string line, int n) => int.Parse(line.Substring(8 * (n - 1), 8).Trim() is { Length: > 0 } s ? s : "0", CultureInfo.InvariantCulture);
        var entities = new List<IgesEntity>();
        for (int i = 0; i < de.Count; i += 2)
        {
            int type = Field(de[i], 1), pointer = Field(de[i], 2), level = Field(de[i], 5), transform = Field(de[i], 7);
            int lineCount = Field(de[i + 1], 4), form = Field(de[i + 1], 5);
            string data = string.Concat(pd.Skip(pointer - 1).Take(lineCount).Select(l => l[..64]));
            var tokens = data.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            Assert.Equal(type, int.Parse(tokens[0], CultureInfo.InvariantCulture));
            var values = tokens.Skip(1).Select(t => double.Parse(t.Replace('D', 'E'), CultureInfo.InvariantCulture)).ToArray();
            entities.Add(new IgesEntity(type, form, level, transform, values));
        }
        return (entities, count, lines.Single(l => l[72] == 'T'));
    }

    /// <summary>A Rational B-Spline Surface (type 128, IGES 5.3 §4.24) at (u, v), from its parameters.</summary>
    private static Vector3D Evaluate128(double[] p, double u, double v)
    {
        int k1 = (int)p[0], k2 = (int)p[1], m1 = (int)p[2], m2 = (int)p[3];
        int nk1 = k1 + m1 + 2, nk2 = k2 + m2 + 2;
        int at = 9;
        var s = p.Skip(at).Take(nk1).ToArray(); at += nk1;
        var t = p.Skip(at).Take(nk2).ToArray(); at += nk2;
        int n = (k1 + 1) * (k2 + 1);
        var w = p.Skip(at).Take(n).ToArray(); at += n;
        var c = p.Skip(at).Take(3 * n).ToArray();
        int su = Nurbs.FindSpan(s, m1, k1 + 1, u), sv = Nurbs.FindSpan(t, m2, k2 + 1, v);
        var nu = new double[m1 + 1];
        var nv = new double[m2 + 1];
        Nurbs.Basis(s, m1, su, u, nu);
        Nurbs.Basis(t, m2, sv, v, nv);
        var sum = Vector3D.Zero;
        double den = 0;
        for (int a = 0; a <= m1; a++)
            for (int b = 0; b <= m2; b++)
            {
                int i = su - m1 + a, j = sv - m2 + b, idx = j * (k1 + 1) + i;
                double basis = nu[a] * nv[b] * w[idx];
                sum += new Vector3D(c[3 * idx], c[3 * idx + 1], c[3 * idx + 2]) * basis;
                den += basis;
            }
        return sum / den;
    }

    private static CadExportResult ExportSinglet(CadExportFormat format, string path) =>
        CadExporter.Export(SingletSettings(), Array.Empty<OpenSim.Optics.Materials.OpticalCatalog>(),
            new CadExportSettings { Format = format, RayPattern = CadRayPattern.YFan, NumberOfRays = 5, Field = 1, Wavelength = 1 },
            path, Stamp);

    [Fact]
    public void Iges_ReadBack_CountsTheEntities_AndItsSurfacesLieOnTheSpheres()
    {
        string path = TempFile(".igs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            ExportSinglet(CadExportFormat.Iges, path);
            var (entities, lines, terminate) = ReadIges(File.ReadAllText(path));
            // The Terminate section counts the sections' lines (§2.2.4.6).
            Assert.Equal($"S{lines['S'],7}G{lines['G'],7}D{lines['D'],7}P{lines['P'],7}", terminate[..32]);

            Assert.Equal(3, entities.Count(e => e.Type == 128));   // front cap, edge band, back cap
            Assert.Equal(2, entities.Count(e => e.Type == 100));   // the two rims
            Assert.Equal(2, entities.Count(e => e.Type == 124));
            Assert.Equal(5, entities.Count(e => e.Type == 106 && e.Form == 12));   // the Y fan
            Assert.All(entities.Where(e => e.Type == 106), e => Assert.Equal(2, e.Level));
            Assert.All(entities.Where(e => e.Type == 128), e => Assert.Equal(1, e.Level));

            // Millimeters: the spheres' centres at z = 50 and 6 − 200 mm.
            var spheres = entities.Where(e => e.Type == 128 && e.Form == 4).ToList();
            Assert.Equal(2, spheres.Count);
            foreach (var sphere in spheres)
            {
                var p = sphere.Values;
                double u1 = p[^3], v1 = p[^1];
                double worst = double.MaxValue;
                foreach (var (centre, radius) in new[] { (new Vector3D(0, 0, R1 * 1e3), Math.Abs(R1) * 1e3), (new Vector3D(0, 0, (T + R2) * 1e3), Math.Abs(R2) * 1e3) })
                {
                    double m = 0;
                    for (int a = 0; a <= 20; a++)
                        for (int b = 0; b <= 10; b++)
                            m = Math.Max(m, Math.Abs((Evaluate128(p, u1 * a / 20, v1 * b / 10) - centre).Length - radius));
                    worst = Math.Min(worst, m);
                }
                Assert.True(worst < 1e-10, $"a sphere surface deviates {worst} mm from both spheres");
                var rim = Evaluate128(p, 0.3, v1);
                Assert.Equal(Semi * 1e3, Math.Sqrt(rim.X * rim.X + rim.Y * rim.Y), 10);
            }

            // The rim circles: radius 12.5 mm, placed by an identity matrix (surface 1 is the global origin).
            foreach (var arc in entities.Where(e => e.Type == 100))
            {
                Assert.Equal(Semi * 1e3, arc.Values[3], 12);
                Assert.Equal(arc.Values[3], arc.Values[5]);
                Assert.True(arc.Transform > 0);
            }

            // The marginal ray Py = −1 of the 20 mm entrance pupil meets surface 1 at y = −10 mm,
            // z = sag(10 mm) (derived: the stop is surface 1 and the object at infinity).
            var first = entities.First(e => e.Type == 106).Values;
            Assert.Equal(2, first[0]);
            Assert.Equal(2, first[1]);
            Assert.Equal(-10, first[3], 9);
            Assert.Equal(SphereSag(R1, 0.010) * 1e3, first[4], 9);
        }
        finally { File.Delete(path); }
    }

    // ---------------- DXF ----------------

    [Fact]
    public void Dxf_ReadBack_HasTheExactArcs_TheEdgeLines_AndTheRays()
    {
        string path = TempFile(".dxf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            ExportSinglet(CadExportFormat.Dxf, path);
            var lines = File.ReadAllLines(path);
            Assert.Equal(0, lines.Length % 2);
            var pairs = Enumerable.Range(0, lines.Length / 2).Select(i => (Code: int.Parse(lines[2 * i].Trim(), CultureInfo.InvariantCulture), Value: lines[2 * i + 1])).ToList();
            Assert.Equal((0, "EOF"), pairs[^1]);
            Assert.Contains(pairs, p => p == (9, "$INSUNITS"));
            var entities = new List<(string Type, Dictionary<int, string> Codes)>();
            foreach (var (code, value) in pairs)
            {
                if (code == 0) entities.Add((value, new Dictionary<int, string>()));
                else if (entities.Count > 0) entities[^1].Codes.TryAdd(code, value);
            }
            double D(Dictionary<int, string> c, int code) => double.Parse(c[code], CultureInfo.InvariantCulture);

            var arcs = entities.Where(e => e.Type == "ARC").ToList();
            Assert.Equal(2, arcs.Count);
            Assert.Equal(2, entities.Count(e => e.Type == "LINE"));   // the edge on both sides of the axis
            Assert.Equal(5, entities.Count(e => e.Type == "POLYLINE" && e.Codes[8] == "RAYS"));
            foreach (var (_, c) in arcs)
            {
                double cx = D(c, 10), cy = D(c, 20), radius = D(c, 40);
                bool front = Math.Abs(radius - Math.Abs(R1) * 1e3) < 1e-9;
                double vertexX = front ? 0 : T * 1e3;
                Assert.Equal(front ? R1 * 1e3 : (T + R2) * 1e3, cx, 9);
                Assert.Equal(0, cy, 12);
                // Both end points are the rims at y = ±12.5 mm, and the counterclockwise sweep passes the vertex.
                double rimX = vertexX + (front ? SphereSag(R1, Semi) : SphereSag(R2, Semi)) * 1e3;
                foreach (int code in new[] { 50, 51 })
                {
                    double a = D(c, code) * Math.PI / 180;
                    Assert.Equal(rimX, cx + radius * Math.Cos(a), 9);
                    Assert.Equal(Semi * 1e3, Math.Abs(cy + radius * Math.Sin(a)), 9);
                }
                double start = D(c, 50), end = D(c, 51), vertex = Math.Atan2(0, vertexX - cx) * 180 / Math.PI;
                double Mod(double x) => ((x % 360) + 360) % 360;
                Assert.True(Mod(vertex - start) < Mod(end - start));
            }
        }
        finally { File.Delete(path); }
    }

    // ---------------- fits ----------------

    [Fact]
    public void BSplineFits_MeetHalfTheTolerance_OnTheirGrid_AndTheToleranceOnAFinerOne()
    {
        // A prolate conic, R = 30 mm, k = −0.6, out to 14 mm (steeper than the fixture lens).
        const double c = 1 / 0.030, k = -0.6, rim = 0.014, tol = 1e-8;
        var cap = new GeneralCap("test conic", r => ConicSag.TryEvaluate(r, c, k, out double z, out double dz) ? (z, dz) : (double.NaN, double.NaN));
        var profile = BSplineFit.FitProfile(cap, rim, tol, "test");
        var patch = BSplineFit.FitPatch(cap, rim, tol, "test");
        Assert.True(profile.MaxDeviation <= tol / 2);
        Assert.True(patch.MaxDeviation <= tol / 2);
        double worstProfile = 0, worstPatch = 0;
        for (int i = 0; i <= 5003; i++)
        {
            double r = rim * i / 5003;
            worstProfile = Math.Max(worstProfile, Math.Abs(profile.Basis.Value(profile.Z, r) - cap.Sag(r)));
            // Σ ξᵢNᵢ(t) = t, the property x(u) = u rests on.
            Assert.Equal(r, profile.Basis.Value(profile.Basis.Greville, r), 15);
        }
        for (int i = 0; i <= 301; i++)
            for (int j = 0; j <= 301; j++)
            {
                double x = -rim + 2 * rim * i / 301, y = -rim + 2 * rim * j / 301, r = Math.Sqrt(x * x + y * y);
                if (r <= rim) worstPatch = Math.Max(worstPatch, Math.Abs(patch.Value(x, y) - cap.Sag(r)));
            }
        Assert.True(worstProfile <= tol, $"profile {worstProfile}");
        Assert.True(worstPatch <= tol, $"patch {worstPatch}");
        // The profile leaves the axis flat: the first two control values are equal.
        Assert.Equal(profile.Z[0], profile.Z[1], 1e-15);
    }

    // ---------------- rays ----------------

    [Fact]
    public void RayPatterns_HaveTheirCounts_AndTheRaysRunFromTheFirstToTheLastSurface()
    {
        Assert.Equal(7, CadRays.Pupil(CadRayPattern.YFan, 7).Count);
        Assert.Equal(14, CadRays.Pupil(CadRayPattern.XYFan, 7).Count);
        Assert.Equal(8, CadRays.Pupil(CadRayPattern.Ring, 8).Count);
        Assert.Empty(CadRays.Pupil(CadRayPattern.None, 8));
        Assert.All(CadRays.Pupil(CadRayPattern.Grid, 9), p => Assert.True(p.Px * p.Px + p.Py * p.Py <= 1 + 1e-12));
        Assert.Equal((0.0, 0.0), CadRays.Pupil(CadRayPattern.YFan, 1)[0]);   // one ray: the chief ray

        string path = TempFile(".step");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var result = ExportSinglet(CadExportFormat.Step, path);
            Assert.Equal(5, result.Report.Polylines);
            string text = File.ReadAllText(path);
            Assert.Equal(5, text.Split('\n').Count(l => l.Contains("=POLYLINE(")));
            Assert.Contains("GEOMETRICALLY_BOUNDED_WIREFRAME_SHAPE_REPRESENTATION", text);
        }
        finally { File.Delete(path); }
    }

    // ---------------- refusals ----------------

    [Fact]
    public void WhatIsNotBuilt_IsRefusedByName()
    {
        var polynomial = Lens(Sphere(R1, T, 1.6, Semi, stop: true) with { Shape = new PolynomialSurface(new[] { 1.0 }) }, Sphere(R2, 0.09, 1, Semi));
        var e1 = Assert.Throws<InvalidOperationException>(() => Scene(polynomial, GeometryOnly()));
        Assert.Contains("Polynomial", e1.Message);
        Assert.Contains("not built", e1.Message);

        var decentered = Lens(Sphere(R1, T, 1.6, Semi, stop: true), Sphere(R2, 0.09, 1, Semi) with { TiltBefore = new TiltDecenter(0.001, 0, 0, 0, 0) });
        var e2 = Assert.Throws<InvalidOperationException>(() => Scene(decentered, GeometryOnly()));
        Assert.Contains("not coaxial", e2.Message);

        var rectangular = Lens(Sphere(R1, T, 1.6, Semi, stop: true) with { Aperture = new RectangularAperture(0.01, 0.005) }, Sphere(R2, 0.09, 1, Semi));
        Assert.Contains("not built", Assert.Throws<InvalidOperationException>(() => Scene(rectangular, GeometryOnly())).Message);

        var thin = Lens(Sphere(R1, 0.001, 1.6, Semi, stop: true), Sphere(R2, 0.09, 1, Semi));   // the faces cross before the edge
        Assert.Contains("not a solid", Assert.Throws<InvalidOperationException>(() => Scene(thin, GeometryOnly())).Message);

        Assert.Contains("OP-76", Assert.Throws<NotSupportedException>(() => CadExporter.FormatFromExtension("lens.sat")).Message);
        Assert.Contains("point clouds", Assert.Throws<NotSupportedException>(() => CadExporter.FormatFromExtension("lens.pcd")).Message);

        // A 2-D DXF needs every axis in the global YZ plane: here the element is decentered 1 mm in x
        // (the decenter before surface 2 carries on to surface 3, so the element stays coaxial).
        var tilted = Lens(Sphere(double.PositiveInfinity, 0.01, 1, Semi, stop: true),
            Sphere(R1, T, 1.6, Semi) with { TiltBefore = new TiltDecenter(0.001, 0, 0, 0, 0) }, Sphere(R2, 0.09, 1, Semi));
        var scene = Scene(tilted, GeometryOnly(CadExportFormat.Dxf));
        Assert.Contains("3-D DXF is not built", Assert.Throws<InvalidOperationException>(() => CrossSectionLinework.Build(scene, 1e-7)).Message);
    }
}
