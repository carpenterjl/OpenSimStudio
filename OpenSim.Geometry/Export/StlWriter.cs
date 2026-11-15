using System.Globalization;
using System.Text;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Export;

/// <summary>
/// STL output (OP-96): every body triangulated by <see cref="RevolvedTessellator"/> with the
/// export tolerance as its chordal error, coordinates in the file's length unit. The layouts are
/// those of the Wikipedia article "STL (file format)" (read 2026-11-15, saved under
/// plans/sources/optics/op-96-help/wiki-STL.html), which quotes 3D Systems' StereoLithography
/// Interface Specification (1989, not available here):
/// <list type="bullet">
/// <item>ASCII: "solid name", then per triangle "facet normal ni nj nk / outer loop / vertex x y z
/// (three times) / endloop / endfacet", then "endsolid name".</item>
/// <item>Binary: an 80-byte header that must not begin with "solid", a little-endian UINT32
/// triangle count, then per triangle 12 REAL32 (normal, three vertices) and a UINT16 attribute
/// byte count of zero.</item>
/// </list>
/// Each normal is the unit outward normal and the vertices run counterclockwise seen from outside
/// (the right-hand rule the article asks for). STL has no line entity, so rays are not written
/// (OpticStudio's help page "CAD Files" says the same of its STL export); STL has no units, so the
/// header names the unit.
/// </summary>
public static class StlWriter
{
    public static CadWriteReport Write(CadScene scene, string path, CadWriteOptions options, bool binary = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        using var stream = File.Create(path);
        return Write(scene, stream, options, binary);
    }

    public static CadWriteReport Write(CadScene scene, Stream stream, CadWriteOptions options, bool binary = true)
    {
        options.Validate();
        if (scene.Bodies.Count == 0) throw new InvalidOperationException("STL export: there is no body to write.");
        var meshes = scene.Bodies.Select(b => RevolvedTessellator.Tessellate(b, options.Tolerance)).ToList();
        double scale = options.PerUnit;
        int count = meshes.Sum(m => m.Triangles.Count);
        string name = CadUnits.Ascii(options.Name).Replace(' ', '_');
        string unit = options.Unit.ToString().ToLowerInvariant();

        if (binary)
        {
            using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            var header = new byte[80];
            var text = Encoding.ASCII.GetBytes(CadUnits.Ascii($"OpenSim Studio STL, {name}, units {unit}"));
            Array.Copy(text, header, Math.Min(80, text.Length));
            w.Write(header);
            w.Write((uint)count);
            foreach (var mesh in meshes)
                for (int t = 0; t < mesh.Triangles.Count; t++)
                {
                    var tri = mesh.Triangles[t];
                    var normal = mesh.TriangleNormal(t);
                    WriteVector(w, normal, 1);
                    WriteVector(w, mesh.Vertices[tri.A], scale);
                    WriteVector(w, mesh.Vertices[tri.B], scale);
                    WriteVector(w, mesh.Vertices[tri.C], scale);
                    w.Write((ushort)0);
                }
        }
        else
        {
            using var w = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            w.WriteLine($"solid {name} units {unit}");
            foreach (var mesh in meshes)
                for (int t = 0; t < mesh.Triangles.Count; t++)
                {
                    var tri = mesh.Triangles[t];
                    w.WriteLine($"facet normal {E(mesh.TriangleNormal(t), 1)}");
                    w.WriteLine("outer loop");
                    w.WriteLine($"vertex {E(mesh.Vertices[tri.A], scale)}");
                    w.WriteLine($"vertex {E(mesh.Vertices[tri.B], scale)}");
                    w.WriteLine($"vertex {E(mesh.Vertices[tri.C], scale)}");
                    w.WriteLine("endloop");
                    w.WriteLine("endfacet");
                }
            w.WriteLine($"endsolid {name}");
        }

        var notes = new List<string>
        {
            FormattableString.Invariant($"STL: {count} triangles, chordal error at most {options.Tolerance * scale:g3} {unit}, coordinates in {unit}s."),
        };
        if (scene.Rays.Count > 0) notes.Add("STL: rays are not written; the format has no line entity.");
        int sheets = scene.Bodies.Count(b => !b.IsClosed);
        if (sheets > 0) notes.Add($"STL: {sheets} open surface(s) (bare mirrors) are written as open triangle sheets.");
        return new CadWriteReport
        {
            Solids = scene.Bodies.Count - sheets, Sheets = sheets, Faces = meshes.Sum(m => m.FaceCount), Triangles = count, Notes = notes,
        };
    }

    private static void WriteVector(BinaryWriter w, Vector3D v, double scale)
    {
        w.Write((float)(v.X * scale));
        w.Write((float)(v.Y * scale));
        w.Write((float)(v.Z * scale));
    }

    private static string E(Vector3D v, double scale) => string.Create(CultureInfo.InvariantCulture,
        $"{v.X * scale:e9} {v.Y * scale:e9} {v.Z * scale:e9}");
}
