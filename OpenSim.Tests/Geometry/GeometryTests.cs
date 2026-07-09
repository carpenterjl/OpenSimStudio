using System.Globalization;
using System.Text;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using Xunit;

namespace OpenSim.Tests.Geometry;

public class PrimitiveFactoryTests
{
    [Fact]
    public void Box_IsWatertightWithCorrectVolumeAndFaces()
    {
        var box = PrimitiveFactory.CreateBox(2, 3, 4);
        Assert.True(box.IsWatertight());
        Assert.Equal(24.0, box.ComputeSignedVolume(), 10);
        Assert.Equal(6, box.FaceCount);
    }

    [Fact]
    public void Cylinder_IsWatertightWithNearAnalyticVolume()
    {
        var cyl = PrimitiveFactory.CreateCylinder(1, 2, 128);
        Assert.True(cyl.IsWatertight());
        Assert.Equal(3, cyl.FaceCount);
        // Faceted volume approaches πr²h from below as segments increase.
        double analytic = Math.PI * 1 * 1 * 2;
        Assert.InRange(cyl.ComputeSignedVolume(), analytic * 0.995, analytic);
    }
}

public class StlImporterTests
{
    private static string WriteTempFile(byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".stl");
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] ToBinaryStl(OpenSim.Core.Model.TriangleMesh mesh)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(new byte[80]);
        w.Write((uint)mesh.Triangles.Count);
        for (int i = 0; i < mesh.Triangles.Count; i++)
        {
            var n = mesh.TriangleNormal(i);
            var t = mesh.Triangles[i];
            w.Write((float)n.X); w.Write((float)n.Y); w.Write((float)n.Z);
            foreach (var vi in new[] { t.A, t.B, t.C })
            {
                var v = mesh.Vertices[vi];
                w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z);
            }
            w.Write((ushort)0);
        }
        return ms.ToArray();
    }

    private static byte[] ToAsciiStl(OpenSim.Core.Model.TriangleMesh mesh)
    {
        var sb = new StringBuilder("solid test\n");
        for (int i = 0; i < mesh.Triangles.Count; i++)
        {
            var n = mesh.TriangleNormal(i);
            var t = mesh.Triangles[i];
            sb.AppendLine(FormattableString.Invariant($"facet normal {n.X} {n.Y} {n.Z}"));
            sb.AppendLine("outer loop");
            foreach (var vi in new[] { t.A, t.B, t.C })
            {
                var v = mesh.Vertices[vi];
                sb.AppendLine(FormattableString.Invariant($"vertex {v.X} {v.Y} {v.Z}"));
            }
            sb.AppendLine("endloop");
            sb.AppendLine("endfacet");
        }
        sb.AppendLine("endsolid test");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void BinaryRoundTrip_PreservesTopologyAndVolume()
    {
        var box = PrimitiveFactory.CreateBox(1, 1, 1);
        string path = WriteTempFile(ToBinaryStl(box));
        try
        {
            var imported = new StlImporter().Import(path);
            Assert.True(imported.IsWatertight());
            Assert.Equal(8, imported.Vertices.Count);   // soup re-welded to 8 corners
            Assert.Equal(12, imported.Triangles.Count);
            Assert.Equal(1.0, imported.ComputeSignedVolume(), 5);
            Assert.Equal(6, imported.FaceCount);        // faces recovered by crease detection
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AsciiRoundTrip_PreservesTopologyAndVolume()
    {
        var cyl = PrimitiveFactory.CreateCylinder(0.5, 1.0, 32);
        string path = WriteTempFile(ToAsciiStl(cyl));
        try
        {
            var imported = new StlImporter().Import(path);
            Assert.True(imported.IsWatertight());
            Assert.Equal(cyl.ComputeSignedVolume(), imported.ComputeSignedVolume(), 5);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Import_InwardWoundMesh_IsFlippedOutward()
    {
        var box = PrimitiveFactory.CreateBox(1, 1, 1);
        // Flip all windings so the signed volume is negative.
        var flipped = new OpenSim.Core.Model.TriangleMesh(
            box.Vertices,
            box.Triangles.Select(t => new OpenSim.Core.Model.Triangle(t.A, t.C, t.B)).ToList(),
            box.TriangleFaceIds);
        Assert.True(flipped.ComputeSignedVolume() < 0);

        string path = WriteTempFile(ToBinaryStl(flipped));
        try
        {
            var imported = new StlImporter().Import(path);
            Assert.True(imported.ComputeSignedVolume() > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Import_DegenerateOnlyFile_ThrowsInvalidData()
    {
        // One zero-area triangle (all vertices coincident).
        const string stl = """
            solid degenerate
            facet normal 0 0 1
            outer loop
            vertex 0 0 0
            vertex 0 0 0
            vertex 0 0 0
            endloop
            endfacet
            endsolid degenerate
            """;
        string path = WriteTempFile(Encoding.ASCII.GetBytes(stl));
        try
        {
            Assert.Throws<InvalidDataException>(() => new StlImporter().Import(path));
        }
        finally { File.Delete(path); }
    }
}
