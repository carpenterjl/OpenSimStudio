using System.Globalization;
using System.Text;
using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Export;

/// <summary>One entity of a 2-D drawing (drawing units: meters until written).</summary>
public abstract record DxfEntity(string Layer);

public sealed record DxfLine(string Layer, double X0, double Y0, double X1, double Y1) : DxfEntity(Layer);

/// <summary>An arc counterclockwise from <see cref="StartDegrees"/> to <see cref="EndDegrees"/>.</summary>
public sealed record DxfArc(string Layer, double CenterX, double CenterY, double Radius, double StartDegrees, double EndDegrees) : DxfEntity(Layer);

public sealed record DxfPolyline(string Layer, IReadOnlyList<(double X, double Y)> Points, bool Closed) : DxfEntity(Layer);

/// <summary>
/// The 2-D cross-section drawing (OP-96; OpticStudio's Export 2D DXF, help page "Export 2D DXF":
/// "Only available for spherical symmetric lenses"): every body cut by the global YZ plane and
/// seen as the layouts' cross-section shows it (docs/Optics-Conventions.md §3: global z
/// horizontal, global y vertical; the layout's CrossSection.Project). A body's axis must lie in
/// that plane (its x component and its origin's x zero), so the cut is its meridian on both sides
/// of the axis; any other body is refused by name (a 3-D DXF is not built). Spheres are exact
/// ARCs, planes, cylinders and cones LINEs, any other cap a POLYLINE through points of the exact
/// sag with the export tolerance as chordal error; rays are POLYLINEs of their points projected
/// onto the plane.
/// </summary>
public static class CrossSectionLinework
{
    public const string LensLayer = "LENS";
    public const string RayLayer = "RAYS";

    public static IReadOnlyList<DxfEntity> Build(CadScene scene, double chordalError)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!(chordalError > 0)) throw new ArgumentOutOfRangeException(nameof(chordalError), chordalError, "The chordal error must be positive.");
        var entities = new List<DxfEntity>();
        foreach (var body in scene.Bodies)
        {
            body.Validate();
            var a = body.Placement.Col2;
            var o = body.Placement.Translation;
            double size = Math.Max(body.MaxRadius, 1e-9);
            if (Math.Abs(a.X) > 1e-12 || Math.Abs(o.X) > 1e-12 * Math.Max(size, o.Length))
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"DXF export: {body.Name} has its axis outside the global YZ plane (axis x {a.X:g3}, vertex x {o.X:g3} m); the 2-D cross-section needs every axis in that plane, and a 3-D DXF is not built."));
            var v = new Vector3D(0, -a.Z, a.Y); // perpendicular to the axis, in the YZ plane
            (double X, double Y) P(double r, double z, double side)
            {
                var g = o + a * z + v * (side * r);
                return (g.Z, g.Y);
            }

            foreach (var piece in body.Pieces)
            {
                switch (piece)
                {
                    case LinePiece line:
                        foreach (double side in new[] { 1.0, -1.0 })
                        {
                            var p0 = P(line.R0, line.Z0, side);
                            var p1 = P(line.R1, line.Z1, side);
                            if (p0 != p1) entities.Add(new DxfLine(LensLayer, p0.X, p0.Y, p1.X, p1.Y));
                        }
                        break;
                    case CapPiece cap when cap.Shape is PlaneCap:
                    {
                        var p0 = P(cap.RimRadius, cap.VertexZ, -1);
                        var p1 = P(cap.RimRadius, cap.VertexZ, 1);
                        entities.Add(new DxfLine(LensLayer, p0.X, p0.Y, p1.X, p1.Y));
                        break;
                    }
                    case CapPiece { Shape: SphereCap sphere } cap:
                    {
                        var c = P(0, cap.VertexZ + sphere.Radius, 1);
                        var lo = P(cap.RimRadius, cap.RimZ, -1);
                        var hi = P(cap.RimRadius, cap.RimZ, 1);
                        var vertex = P(0, cap.VertexZ, 1);
                        double tLo = Degrees(lo, c), tHi = Degrees(hi, c), tV = Degrees(vertex, c);
                        bool loFirst = Mod(tV - tLo) < Mod(tHi - tLo);
                        entities.Add(new DxfArc(LensLayer, c.X, c.Y, Math.Abs(sphere.Radius), loFirst ? tLo : tHi, loFirst ? tHi : tLo));
                        break;
                    }
                    case CapPiece cap:
                    {
                        var half = RevolvedTessellator.Sample(cap with { TowardAxis = false }, chordalError, body.Name);
                        var pts = new List<(double, double)>();
                        for (int k = half.Count - 1; k >= 1; k--) pts.Add(P(half[k].R, half[k].Z, -1));
                        foreach (var h in half) pts.Add(P(h.R, h.Z, 1));
                        entities.Add(new DxfPolyline(LensLayer, pts, false));
                        break;
                    }
                }
            }
        }
        foreach (var ray in scene.Rays)
        {
            if (ray.Points.Count < 2) continue;
            entities.Add(new DxfPolyline(RayLayer, ray.Points.Select(p => (p.Z, p.Y)).ToList(), false));
        }
        return entities;
    }

    private static double Degrees((double X, double Y) p, (double X, double Y) c) =>
        Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI;

    private static double Mod(double degrees)
    {
        double m = degrees % 360;
        return m < 0 ? m + 360 : m;
    }
}

/// <summary>
/// DXF output (OP-96), the minimal ASCII form Autodesk's AutoCAD 2012 DXF Reference describes in
/// "Writing a DXF File" (saved under plans/sources/optics/op-96-help/): the HEADER section may hold
/// only the variables set (here $INSUNITS, group 70, <see cref="CadUnits.DxfInsUnits"/>), the
/// TABLES section may be omitted (layers referenced in ENTITIES are created), and the file ends
/// with EOF. Group codes from the reference's entity tables: LINE 8 layer, 10/20/30 start, 11/21/31
/// end; ARC 10/20/30 centre, 40 radius, 50 start and 51 end angle (degrees, counterclockwise);
/// POLYLINE 66 (vertices follow), 10/20/30 zero, 70 flags (1 closed), then VERTEX 10/20/30 per point
/// and SEQEND. Coordinates are in the file's length unit.
/// </summary>
public static class DxfWriter
{
    public static CadWriteReport Write(IReadOnlyList<DxfEntity> entities, string path, CadWriteOptions options)
    {
        string text = WriteText(entities, options);
        File.WriteAllText(path, text, Encoding.ASCII);
        return new CadWriteReport
        {
            Polylines = entities.Count(e => e is DxfPolyline { Layer: CrossSectionLinework.RayLayer }),
            Notes = new[]
            {
                FormattableString.Invariant($"DXF: {entities.Count(e => e is DxfArc)} arcs, {entities.Count(e => e is DxfLine)} lines, {entities.Count(e => e is DxfPolyline)} polylines on layers {CrossSectionLinework.LensLayer} and {CrossSectionLinework.RayLayer}; chordal error of sampled profiles at most {options.Tolerance * options.PerUnit:g3} {options.Unit.ToString().ToLowerInvariant()}."),
            },
        };
    }

    public static string WriteText(IReadOnlyList<DxfEntity> entities, CadWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        double s = options.PerUnit;
        var sb = new StringBuilder();
        void G(int code, string value) => sb.Append(code.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(value).Append('\n');
        void R(int code, double value) => G(code, value.ToString("R", CultureInfo.InvariantCulture));

        G(0, "SECTION");
        G(2, "HEADER");
        G(9, "$INSUNITS");
        G(70, CadUnits.DxfInsUnits(options.Unit).ToString(CultureInfo.InvariantCulture));
        G(0, "ENDSEC");
        G(0, "SECTION");
        G(2, "ENTITIES");
        foreach (var e in entities)
        {
            switch (e)
            {
                case DxfLine l:
                    G(0, "LINE"); G(8, l.Layer);
                    R(10, l.X0 * s); R(20, l.Y0 * s); R(30, 0);
                    R(11, l.X1 * s); R(21, l.Y1 * s); R(31, 0);
                    break;
                case DxfArc a:
                    G(0, "ARC"); G(8, a.Layer);
                    R(10, a.CenterX * s); R(20, a.CenterY * s); R(30, 0);
                    R(40, a.Radius * s); R(50, a.StartDegrees); R(51, a.EndDegrees);
                    break;
                case DxfPolyline p:
                    G(0, "POLYLINE"); G(8, p.Layer); G(66, "1");
                    R(10, 0); R(20, 0); R(30, 0);
                    G(70, p.Closed ? "1" : "0");
                    foreach (var (x, y) in p.Points)
                    {
                        G(0, "VERTEX"); G(8, p.Layer);
                        R(10, x * s); R(20, y * s); R(30, 0);
                    }
                    G(0, "SEQEND"); G(8, p.Layer);
                    break;
            }
        }
        G(0, "ENDSEC");
        G(0, "EOF");
        return sb.ToString();
    }
}
