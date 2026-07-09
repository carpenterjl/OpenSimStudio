using System.Globalization;
using System.Text;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Geometry;

/// <summary>
/// Imports binary and ASCII STL files into a welded, face-segmented
/// <see cref="TriangleMesh"/> with outward-oriented winding.
/// </summary>
public sealed class StlImporter : IGeometryImporter
{
    public string FormatName => "STL";
    public IReadOnlyList<string> FileExtensions { get; } = new[] { ".stl" };

    public TriangleMesh Import(string filePath)
    {
        byte[] data = File.ReadAllBytes(filePath);
        var soup = IsAscii(data) ? ParseAscii(data) : ParseBinary(data);
        if (soup.Count == 0)
            throw new InvalidDataException($"STL file '{Path.GetFileName(filePath)}' contains no triangles.");

        var (vertices, triangles) = VertexWelder.Weld(soup);
        if (triangles.Count == 0)
            throw new InvalidDataException($"STL file '{Path.GetFileName(filePath)}' contains only degenerate triangles.");

        // Orient outward: a consistently wound closed mesh has positive signed volume.
        var provisional = new TriangleMesh(vertices, triangles, new int[triangles.Count]);
        if (provisional.ComputeSignedVolume() < 0)
        {
            for (int i = 0; i < triangles.Count; i++)
                triangles[i] = new Triangle(triangles[i].A, triangles[i].C, triangles[i].B);
        }

        var faceIds = FaceDetector.DetectFaces(vertices, triangles);
        return new TriangleMesh(vertices, triangles, faceIds);
    }

    /// <summary>
    /// ASCII files start with "solid" and contain "facet" as text; binary files are
    /// 84 + 50·n bytes. The size check wins because some binary exporters also write
    /// "solid" into the 80-byte header.
    /// </summary>
    private static bool IsAscii(byte[] data)
    {
        if (data.Length >= 84)
        {
            uint count = BitConverter.ToUInt32(data, 80);
            if (data.Length == 84 + 50L * count)
                return false;
        }
        string head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 512));
        return head.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase)
               && head.Contains("facet", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Vector3D> ParseBinary(byte[] data)
    {
        if (data.Length < 84)
            throw new InvalidDataException("Binary STL is too short to contain a header.");
        uint count = BitConverter.ToUInt32(data, 80);
        if (data.Length < 84 + 50L * count)
            throw new InvalidDataException("Binary STL is truncated: triangle count exceeds file size.");

        var soup = new List<Vector3D>((int)count * 3);
        int offset = 84;
        for (uint i = 0; i < count; i++)
        {
            // 12 bytes normal (ignored — recomputed from winding), then 3 vertices
            for (int v = 0; v < 3; v++)
            {
                int p = offset + 12 + v * 12;
                soup.Add(new Vector3D(
                    BitConverter.ToSingle(data, p),
                    BitConverter.ToSingle(data, p + 4),
                    BitConverter.ToSingle(data, p + 8)));
            }
            offset += 50;
        }
        return soup;
    }

    private static List<Vector3D> ParseAscii(byte[] data)
    {
        var soup = new List<Vector3D>();
        using var reader = new StreamReader(new MemoryStream(data), Encoding.ASCII);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                throw new InvalidDataException($"Malformed STL vertex line: '{line.Trim()}'");
            soup.Add(new Vector3D(
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture)));
        }
        if (soup.Count % 3 != 0)
            throw new InvalidDataException("ASCII STL vertex count is not a multiple of 3.");
        return soup;
    }
}
