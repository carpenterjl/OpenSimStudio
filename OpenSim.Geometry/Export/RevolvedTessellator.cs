using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Export;

/// <summary>
/// Triangulates a <see cref="RevolvedBody"/> to a stated chordal error δ (the STL export, OP-96).
/// <list type="bullet">
/// <item>Around the axis: N equal segments, the same N on every ring of the body so neighbouring
/// pieces share their rim vertices (watertight by construction). N is the smallest multiple of
/// four, at least 16, whose chord sagitta r·(1 − cos(π/N)) at the body's largest radius is at
/// most δ/2 (derived: the sagitta of a chord spanning the angle 2π/N).</item>
/// <item>Along the meridian: a straight piece contributes its two ends; a cap is bisected in r
/// until the distance from each sub-arc's mid-point to its chord is at most δ/2.</item>
/// <item>Every vertex is a point of the exact surface; each quad between two rings is split into
/// two triangles. The deviation of a triangle from the surface is the sum of the two chordal
/// errors at most, δ (the stated chordal error), for the convex-in-each-direction faces of
/// revolution the export builds; the tests measure it.</item>
/// </list>
/// Face ids are the piece indices. A closed body is wound with every normal outward (positive
/// signed volume).
/// </summary>
public static class RevolvedTessellator
{
    /// <summary>A guard against a tolerance far below the model's size.</summary>
    public const int MaxSegments = 1 << 16;

    public static TriangleMesh Tessellate(RevolvedBody body, double chordalError)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!(chordalError > 0) || !double.IsFinite(chordalError))
            throw new ArgumentOutOfRangeException(nameof(chordalError), chordalError, "The chordal error must be positive.");
        body.Validate();
        int n = Segments(body.MaxRadius, chordalError, body.Name);

        // The meridian polyline and, per segment, the piece it belongs to.
        var meridian = new List<(double R, double Z)>();
        var pieceOf = new List<int>();
        for (int p = 0; p < body.Pieces.Count; p++)
        {
            var pts = Sample(body.Pieces[p], chordalError / 2, body.Name);
            if (meridian.Count == 0) meridian.Add(pts[0]);
            for (int k = 1; k < pts.Count; k++)
            {
                meridian.Add(pts[k]);
                pieceOf.Add(p);
            }
        }

        var cos = new double[n];
        var sin = new double[n];
        for (int j = 0; j < n; j++) (sin[j], cos[j]) = Math.SinCos(2 * Math.PI * j / n);

        var vertices = new List<Vector3D>();
        var first = new int[meridian.Count];
        for (int i = 0; i < meridian.Count; i++)
        {
            first[i] = vertices.Count;
            var (r, z) = meridian[i];
            if (r == 0) vertices.Add(body.Placement.Apply(new Vector3D(0, 0, z)));
            else
                for (int j = 0; j < n; j++) vertices.Add(body.Placement.Apply(new Vector3D(r * cos[j], r * sin[j], z)));
        }

        var triangles = new List<Triangle>();
        var faces = new List<int>();
        for (int i = 0; i + 1 < meridian.Count; i++)
        {
            bool axisA = meridian[i].R == 0, axisB = meridian[i + 1].R == 0;
            int face = pieceOf[i];
            for (int j = 0; j < n; j++)
            {
                int j1 = (j + 1) % n;
                if (axisA && axisB) break;
                if (axisA)
                {
                    // Wound like the quads' (a0, a1, b1) seen from the next ring, so the ring edge
                    // the fan shares with the next band runs the opposite way in each.
                    triangles.Add(new Triangle(first[i], first[i + 1] + j1, first[i + 1] + j));
                    faces.Add(face);
                }
                else if (axisB)
                {
                    triangles.Add(new Triangle(first[i] + j, first[i] + j1, first[i + 1]));
                    faces.Add(face);
                }
                else
                {
                    int a0 = first[i] + j, a1 = first[i] + j1, b0 = first[i + 1] + j, b1 = first[i + 1] + j1;
                    triangles.Add(new Triangle(a0, a1, b1));
                    triangles.Add(new Triangle(a0, b1, b0));
                    faces.Add(face);
                    faces.Add(face);
                }
            }
        }

        var mesh = new TriangleMesh(vertices, triangles, faces);
        if (!body.IsClosed || mesh.ComputeSignedVolume() >= 0) return mesh;
        var reversed = triangles.Select(t => new Triangle(t.A, t.C, t.B)).ToList();
        return new TriangleMesh(vertices, reversed, faces);
    }

    /// <summary>The number of segments around the axis for a largest radius and a chordal error.</summary>
    public static int Segments(double maxRadius, double chordalError, string what)
    {
        double half = chordalError / 2;
        if (half >= maxRadius) return 16;
        double angle = Math.Acos(1 - half / maxRadius); // half the angle one chord spans
        double needed = Math.Ceiling(Math.PI / angle);
        if (needed > MaxSegments)
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{what}: a chordal error of {chordalError:g3} m needs {needed:g6} segments around a radius of {maxRadius:g6} m, more than {MaxSegments}; loosen the tolerance."));
        int segments = Math.Max(16, (int)needed);
        return (segments + 3) / 4 * 4;
    }

    /// <summary>The meridian points of a piece, start to end.</summary>
    public static List<(double R, double Z)> Sample(MeridianPiece piece, double chordal, string what)
    {
        switch (piece)
        {
            case LinePiece line:
                return new List<(double, double)> { line.Start, line.End };
            case CapPiece cap when cap.Shape is PlaneCap:
                return new List<(double, double)> { cap.Start, cap.End };
            case CapPiece cap:
            {
                var pts = new List<(double R, double Z)> { (0, cap.VertexZ) };
                Refine(cap, 0, cap.RimRadius, pts, 0, chordal, what);
                pts.Add((cap.RimRadius, cap.RimZ));
                if (cap.TowardAxis) pts.Reverse();
                return pts;
            }
            default:
                throw new ArgumentException($"{what}: unknown meridian piece {piece.GetType().Name}.");
        }
    }

    private static void Refine(CapPiece cap, double r0, double r1, List<(double R, double Z)> pts, int depth, double chordal, string what)
    {
        double rm = 0.5 * (r0 + r1);
        double z0 = Z(cap, r0, what), z1 = Z(cap, r1, what), zm = Z(cap, rm, what);
        // Distance from the mid-point to the chord (r0, z0)–(r1, z1).
        double dr = r1 - r0, dz = z1 - z0;
        double distance = Math.Abs(dr * (zm - z0) - dz * (rm - r0)) / Math.Sqrt(dr * dr + dz * dz);
        if (depth > 0 && (distance <= chordal || depth >= 30)) return;
        Refine(cap, r0, rm, pts, depth + 1, chordal, what);
        pts.Add((rm, zm));
        Refine(cap, rm, r1, pts, depth + 1, chordal, what);
    }

    private static double Z(CapPiece cap, double r, string what)
    {
        double z = cap.VertexZ + cap.Shape.Sag(r);
        if (!double.IsFinite(z))
            throw new InvalidOperationException(FormattableString.Invariant($"{what}: the sag of the {cap.Shape.Describe()} is not defined at r = {r:g6} m."));
        return z;
    }
}
