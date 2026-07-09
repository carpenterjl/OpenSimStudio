using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step.Schema;

namespace OpenSim.Geometry.Step.Tessellate;

/// <summary>
/// Orchestrates one solid: shared edge sampling → per-face triangulation → face-id-aware
/// weld → outward orientation. Face ids are the NATIVE STEP faces (running index in shell
/// order) — no crease detection, so tangent faces stay separately selectable for boundary
/// conditions exactly as the CAD model defines them.
/// </summary>
internal static class SolidTessellator
{
    public static (TriangleMesh Mesh, double Volume) Tessellate(
        StepSolid solid, StepImportOptions options, List<string> notes,
        double? fileUncertaintyMeters = null)
    {
        double diag = EstimateDiagonal(solid);
        if (diag <= 0)
            throw new StepGeometryException($"solid #{solid.Id}: geometry has zero extent");
        double chordTol = options.RelativeChordTolerance * diag;
        double weldTol = diag * 1e-6; // VertexWelder's own default, made explicit here
        double minSpacing = 20 * weldTol;
        // B-spline inversion acceptance is anchored to the exporter's own stated
        // coordinate uncertainty — its accuracy claim, not a magic epsilon of ours.
        double inversionAcceptTol = Math.Max(10 * (fileUncertaintyMeters ?? 0), 1e-9 * diag);

        var edges = new EdgeTessellator(options, chordTol, minSpacing);
        var soup = new List<Vector3D>();
        var faceIds = new List<int>();
        int faceIndex = 0;

        foreach (var (shell, isVoid) in EnumerateShells(solid))
        {
            // A closed shell's composed faces point away from the volume it encloses; for
            // a void cavity that is INTO the material, so void faces get one extra flip.
            bool flip = !shell.Orientation ^ isVoid;
            foreach (var face in shell.Faces)
            {
                FaceTessellator.Tessellate(face, flip, edges.Tessellate, options,
                    chordTol, minSpacing, inversionAcceptTol, soup, faceIds, faceIndex, notes);
                faceIndex++;
            }
        }

        if (edges.FloorMerges > 0)
            notes.Add($"solid #{solid.Id}: {edges.FloorMerges} edge samples merged by the " +
                      "minimum-spacing floor (sub-tolerance features)");

        var (vertices, triangles, triangleFaceIds) = VertexWelder.Weld(soup, faceIds, weldTol);
        if (triangles.Count == 0)
            throw new StepGeometryException($"solid #{solid.Id}: tessellation produced no triangles");

        var mesh = new TriangleMesh(vertices, triangles, triangleFaceIds);
        double volume = mesh.ComputeSignedVolume();
        if (volume < 0)
        {
            // Per-face orientation should make this unreachable; a globally inside-out
            // exporter is repaired here and reported, never silently.
            var flipped = triangles.Select(t => new Triangle(t.A, t.C, t.B)).ToList();
            mesh = new TriangleMesh(vertices, flipped, triangleFaceIds);
            volume = -volume;
            notes.Add($"solid #{solid.Id}: shell was globally inside-out — orientation flipped");
        }
        return (mesh, volume);
    }

    private static IEnumerable<(StepShell Shell, bool IsVoid)> EnumerateShells(StepSolid solid)
    {
        yield return (solid.Outer, false);
        foreach (var v in solid.Voids) yield return (v, true);
    }

    /// <summary>
    /// Conservative bounding diagonal from every point that bounds the solid's geometry:
    /// edge vertices, circle/ellipse extremes, B-spline control points (which bound their
    /// curves/surfaces), and closed-surface extents. Cheap, and safe as a tolerance basis.
    /// </summary>
    private static double EstimateDiagonal(StepSolid solid)
    {
        var points = new List<Vector3D>();

        foreach (var (shell, _) in EnumerateShells(solid))
            foreach (var face in shell.Faces)
            {
                switch (face.Surface)
                {
                    case StepSphere s:
                        AddFrameExtent(points, s.Frame.Origin, s.Frame, s.Radius, s.Radius, s.Radius);
                        break;
                    case StepTorus t:
                        AddFrameExtent(points, t.Frame.Origin, t.Frame,
                            t.MajorRadius + t.MinorRadius, t.MajorRadius + t.MinorRadius, t.MinorRadius);
                        break;
                    case StepBSplineSurface b:
                        points.AddRange(b.ControlPoints);
                        break;
                }
                foreach (var bound in face.Bounds)
                {
                    if (bound.Loop.VertexLoopVertex is { } vlv) points.Add(vlv.Point);
                    foreach (var use in bound.Loop.Edges)
                    {
                        points.Add(use.Edge.Start.Point);
                        points.Add(use.Edge.End.Point);
                        switch (use.Edge.Curve)
                        {
                            case StepCircle c:
                                AddFrameExtent(points, c.Frame.Origin, c.Frame, c.Radius, c.Radius, 0);
                                break;
                            case StepEllipse e:
                                AddFrameExtent(points, e.Frame.Origin, e.Frame, e.SemiAxis1, e.SemiAxis2, 0);
                                break;
                            case StepBSplineCurve bs:
                                points.AddRange(bs.ControlPoints);
                                break;
                        }
                    }
                }
            }

        return points.Count == 0 ? 0 : Aabb.FromPoints(points).Diagonal;
    }

    private static void AddFrameExtent(List<Vector3D> points, Vector3D origin,
        Schema.Axis2Placement3D frame, double ex, double ey, double ez)
    {
        points.Add(origin + frame.XAxis * ex);
        points.Add(origin - frame.XAxis * ex);
        points.Add(origin + frame.YAxis * ey);
        points.Add(origin - frame.YAxis * ey);
        if (ez > 0)
        {
            points.Add(origin + frame.ZAxis * ez);
            points.Add(origin - frame.ZAxis * ez);
        }
    }
}
