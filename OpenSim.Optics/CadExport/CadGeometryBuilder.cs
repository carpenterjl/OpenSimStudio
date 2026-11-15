using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Export;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Composite;

namespace OpenSim.Optics.CadExport;

/// <summary>
/// The sequential lens as bodies of revolution for the CAD writers (OP-96, docs/Optics-Conventions.md
/// §70). It reads the same lens model the layouts draw (OP-21's <see cref="SurfacePlacement"/> for
/// the global frame, the clear and mechanical semi-diameters of <see cref="SemiDiameterSizing"/>,
/// the element rule of the layout's OpticalLayout):
/// <list type="bullet">
/// <item><b>Lens elements.</b> Surface j and j + 1 bound a body when j is not a mirror and the
/// medium after it is not air (a cemented doublet is two bodies sharing a face; a Mangin mirror is
/// the body that ends on the mirror). Both faces must be coaxial: surface j + 1's frame is j's moved
/// along j's z, possibly turned about that axis or reversed (an axis reversed by a coordinate
/// break), to 10⁻¹² of the element's size; any other element is refused by name, as are faces that
/// are coordinate breaks or ignored surfaces inside glass.</item>
/// <item><b>Faces</b> reach their outer semi-diameter (the larger of the clear and mechanical
/// semi-diameters); beyond the clear one a face continues as a flat at its clear-edge sag, as the
/// layouts draw it. The edge follows <see cref="CadEdgeStyle"/>. A body whose faces meet or cross
/// at its edge is refused: it is not a solid.</item>
/// <item><b>Mirrors</b> outside a lens body get a flat back <see cref="CadExportSettings.MirrorSubstrateThickness"/>
/// beyond the mirror's farthest point on the side away from the incoming light (local +z after an
/// even number of mirrors, −z after an odd number, docs/Optics-Conventions.md §3), or are exported
/// as bare surfaces when the thickness is zero (not for STEP).</item>
/// <item><b>Shapes.</b> A plane and a sphere are carried exactly; the Standard conic with k ≠ 0 and
/// every rotationally symmetric sag shape (the even, odd and extended aspheres, Q-type, superconic,
/// radial NURBS: <see cref="RadialSagShape"/>) are given to the writers with their exact sag to fit.
/// Every other surface type is refused by name (no fit of a non-rotational surface is built), as are
/// apertures other than circular ones centred on the axis.</item>
/// </list>
/// Dummy surfaces, the stop's aperture and the image surface are not exported (OpticStudio's
/// "Export Dummy Surfaces" is not built).
/// </summary>
public static class CadGeometryBuilder
{
    private const double CoaxialTolerance = 1e-12;

    public static CadScene Build(SequentialLens lens, OpticalSystemData system, CadExportSettings settings, List<string> notes,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notes);
        if (settings.Validate() is { Count: > 0 } bad) throw new ArgumentException(string.Join(Environment.NewLine, bad));
        var placement = SurfacePlacement.Compute(lens);
        var (first, last) = Range(lens, settings);
        double perUnit = OpticsConventions.MetersPer(system.Units.LensUnit);
        var (clear, mechanical) = SemiDiameters(lens, system, first, last, notes, cancellation);
        var media = lens.EffectiveMedia();

        var bodies = new List<RevolvedBody>();
        var inElement = new HashSet<int>();
        for (int j = first; j <= last; j++)
        {
            var s = lens.Surfaces[j];
            if (s.IsMirror || ReportText.IsAir(media[j])) continue;
            if (s.IsCoordinateBreak || s.IsIgnored)
                throw new InvalidOperationException($"Surface {j}: a {(s.IsCoordinateBreak ? "coordinate break" : "ignored surface")} inside glass; CAD export of such an element is not built.");
            if (j + 1 > last)
            {
                notes.Add($"Surfaces {j}–{j + 1}: the element reaches past the last surface exported ({last}); it is not exported.");
                continue;
            }
            bodies.Add(Element(lens, placement, j, clear, mechanical, settings.EdgeStyle));
            inElement.Add(j);
            inElement.Add(j + 1);
        }

        for (int j = first; j <= last; j++)
        {
            var s = lens.Surfaces[j];
            if (!s.IsMirror || inElement.Contains(j)) continue;
            bodies.Add(Mirror(lens, placement, j, clear[j], Math.Max(clear[j], mechanical[j]), settings.MirrorSubstrateThickness * perUnit, settings.Format));
        }

        int dummies = Enumerable.Range(first, last - first + 1)
            .Count(j => !inElement.Contains(j) && !lens.Surfaces[j].IsMirror && !lens.Surfaces[j].IsCoordinateBreak);
        if (dummies > 0) notes.Add($"{dummies} surface(s) in the range bound no lens body and are not mirrors; they are not exported (dummy surfaces are not built).");
        if (bodies.Count == 0)
            throw new InvalidOperationException($"CAD export: surfaces {first} to {last} hold no lens element and no mirror, so there is no body to export.");

        var rays = CadRays.Trace(lens, system, settings, first, last, notes, cancellation);
        return new CadScene(bodies, rays);
    }

    /// <summary>The surfaces exported: first to last, the last before the image when 0.</summary>
    public static (int First, int Last) Range(SequentialLens lens, CadExportSettings settings)
    {
        int last = settings.LastSurface == 0 ? lens.LastSurfaceIndex : settings.LastSurface;
        if (settings.FirstSurface < 1 || last > lens.LastSurfaceIndex || settings.FirstSurface > last)
            throw new ArgumentException($"CAD export: surfaces {settings.FirstSurface} to {last} are not a range of this lens (surfaces 1 to {lens.LastSurfaceIndex}, the image excluded).");
        return (settings.FirstSurface, last);
    }

    /// <summary>The clear and mechanical semi-diameters, meters (sized from rays when a surface in
    /// the range has an automatic one).</summary>
    private static (double[] Clear, double[] Mechanical) SemiDiameters(SequentialLens lens, OpticalSystemData system,
        int first, int last, List<string> notes, CancellationToken cancellation)
    {
        int count = lens.Surfaces.Count;
        bool automatic = Enumerable.Range(first, last - first + 1).Any(j => lens.Surfaces[j].SemiDiameter is null);
        if (automatic)
        {
            var report = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation);
            notes.AddRange(report.Notes);
            return (report.Clear.ToArray(), report.Mechanical.ToArray());
        }
        var clear = new double[count];
        var mechanical = new double[count];
        for (int j = 0; j < count; j++)
        {
            var s = lens.Surfaces[j];
            clear[j] = s.SemiDiameter ?? 0;
            mechanical[j] = Math.Max(s.MechanicalSemiDiameter ?? clear[j], clear[j] + s.ChipZone);
        }
        return (clear, mechanical);
    }

    private static RevolvedBody Element(SequentialLens lens, SurfacePlacement placement, int j,
        double[] clear, double[] mechanical, CadEdgeStyle style)
    {
        string name = $"Lens {j}-{j + 1}";
        var front = lens.Surfaces[j];
        var back = lens.Surfaces[j + 1];
        if (back.IsCoordinateBreak || back.IsIgnored)
            throw new InvalidOperationException($"Surfaces {j}–{j + 1}: surface {j + 1} is {(back.IsCoordinateBreak ? "a coordinate break" : "ignored")} inside glass; CAD export of such an element is not built.");
        CheckAperture(front, j);
        CheckAperture(back, j + 1);
        double cf = Positive(clear[j], j), cb = Positive(clear[j + 1], j + 1);
        double of = Math.Max(cf, mechanical[j]), ob = Math.Max(cb, mechanical[j + 1]);

        // The back face in the front face's frame: a move along z, possibly turned about z or reversed.
        var rel = placement.RelativeTo(j + 1, j);
        double size = Math.Max(Math.Max(of, ob), Math.Abs(rel.Translation.Z));
        double lateral = Math.Sqrt(rel.Translation.X * rel.Translation.X + rel.Translation.Y * rel.Translation.Y);
        double sigma = rel.Col2.Z >= 0 ? 1 : -1;
        if ((rel.Col2 - Vector3D.UnitZ * sigma).Length > CoaxialTolerance || lateral > CoaxialTolerance * size)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Surfaces {j}–{j + 1}: the faces are not coaxial (surface {j + 1} is tilted or decentered against surface {j}: axis {rel.Col2}, lateral offset {lateral:g3} m); CAD export of a non-coaxial element is not built."));
        double d = rel.Translation.Z;

        var frontShape = Shape(front, j, 1);
        var backShape = Shape(back, j + 1, sigma);
        var (rhoF, rhoB) = style switch
        {
            CadEdgeStyle.Squared => (Math.Max(of, ob), Math.Max(of, ob)),
            CadEdgeStyle.Tapered => (of, ob),
            CadEdgeStyle.FlatToNext => (of, of),
            _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown edge style."),
        };
        var pieces = new List<MeridianPiece>();
        var frontPieces = FacePieces(frontShape, 0, cf, rhoF, j);
        var backPieces = FacePieces(backShape, d, cb, rhoB, j + 1);
        var frontEnd = frontPieces[^1].End;
        var backEnd = backPieces[^1].End;
        double edge = (backEnd.Z - frontEnd.Z) * Math.Sign(d == 0 ? 1 : d);
        if (!(edge > 0))
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Surfaces {j}–{j + 1}: the faces meet or cross at the edge (edge thickness {edge:g4} m at r = {frontEnd.R:g6} and {backEnd.R:g6} m), so the element is not a solid; increase the thickness or reduce the semi-diameters."));
        pieces.AddRange(frontPieces);
        pieces.Add(new LinePiece(frontEnd.R, frontEnd.Z, backEnd.R, backEnd.Z));
        for (int i = backPieces.Count - 1; i >= 0; i--) pieces.Add(Reverse(backPieces[i]));
        var body = new RevolvedBody(name, CadBodyKind.Lens, placement.Global(j), pieces, true);
        body.Validate();
        return body;
    }

    private static RevolvedBody Mirror(SequentialLens lens, SurfacePlacement placement, int j, double clear, double outer,
        double substrate, CadExportFormat format)
    {
        var s = lens.Surfaces[j];
        CheckAperture(s, j);
        clear = Positive(clear, j);
        string name = $"Mirror {j}";
        var shape = Shape(s, j, 1);
        var face = FacePieces(shape, 0, clear, outer, j);
        if (substrate == 0)
        {
            if (format == CadExportFormat.Step)
                throw new InvalidOperationException($"Surface {j}: a mirror outside any lens body needs a mirror substrate thickness for STEP, which writes solids only (open sheets are not built for STEP).");
            return new RevolvedBody(name, CadBodyKind.Mirror, placement.Global(j), face, false);
        }
        int mirrorsBefore = Enumerable.Range(1, j - 1).Count(k => lens.Surfaces[k].IsMirror && !lens.Surfaces[k].IsIgnored);
        double toward = mirrorsBefore % 2 == 0 ? 1 : -1;
        // The mirror's farthest point toward its back: the cap sampled at 1024 radii (the flat
        // beyond the clear edge lies at the rim's sag).
        double extreme = 0;
        var cap = (CapPiece)face[0];
        for (int k = 0; k <= 1024; k++)
        {
            double z = cap.Shape.Sag(cap.RimRadius * k / 1024);
            extreme = toward > 0 ? Math.Max(extreme, z) : Math.Min(extreme, z);
        }
        double backZ = extreme + toward * substrate;
        var rim = face[^1].End;
        var pieces = new List<MeridianPiece>(face)
        {
            new LinePiece(rim.R, rim.Z, rim.R, backZ),
            new CapPiece(PlaneCap.Instance, backZ, rim.R, TowardAxis: true),
        };
        var body = new RevolvedBody(name, CadBodyKind.Mirror, placement.Global(j), pieces, true);
        body.Validate();
        return body;
    }

    /// <summary>A face from the axis out to <paramref name="rho"/>: the cap to the clear radius
    /// (or to rho when smaller), then a flat at the clear-edge sag.</summary>
    private static List<MeridianPiece> FacePieces(CapShape shape, double vertexZ, double clear, double rho, int j)
    {
        double capRim = Math.Min(clear, rho);
        var cap = new CapPiece(shape, vertexZ, capRim, false);
        if (!double.IsFinite(cap.RimZ))
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Surface {j}: the sag of the {shape.Describe()} is not defined at r = {capRim:g6} m."));
        var pieces = new List<MeridianPiece> { cap };
        if (rho > capRim) pieces.Add(new LinePiece(capRim, cap.RimZ, rho, cap.RimZ));
        return pieces;
    }

    private static MeridianPiece Reverse(MeridianPiece piece) => piece switch
    {
        CapPiece cap => cap with { TowardAxis = !cap.TowardAxis },
        LinePiece l => new LinePiece(l.R1, l.Z1, l.R0, l.Z0),
        _ => throw new ArgumentException("Unknown meridian piece."),
    };

    private static double Positive(double semi, int j) => semi > 0 && double.IsFinite(semi) ? semi
        : throw new InvalidOperationException($"Surface {j}: the clear semi-diameter is {semi.ToString("g6", CultureInfo.InvariantCulture)} m; a positive one is needed to draw the face.");

    private static void CheckAperture(SequentialSurface s, int j)
    {
        switch (s.Aperture)
        {
            case null:
            case FloatingAperture { DecenterX: 0, DecenterY: 0 }:
            case CircularAperture { DecenterX: 0, DecenterY: 0 }:
                return;
            default:
                throw new InvalidOperationException($"Surface {j}: the {s.Aperture.Describe()} is not exported; CAD export draws round faces at the semi-diameter, and other or decentered aperture shapes are not built.");
        }
    }

    /// <summary>
    /// The face's shape as a cap of the body's frame: z along the body's axis, multiplied by
    /// <paramref name="sign"/> (−1 for a face whose own axis is reversed against the body's).
    /// </summary>
    public static CapShape Shape(SequentialSurface s, int j, double sign = 1)
    {
        if (s.IsCoordinateBreak || s.IsIgnored)
            throw new InvalidOperationException($"Surface {j}: a {(s.IsCoordinateBreak ? "coordinate break" : "ignored surface")} has no shape to export.");
        SurfaceShape? shape;
        try { shape = CompositeSurfaces.EffectiveShape(s); }
        catch (ArgumentException ex) { throw new InvalidOperationException($"Surface {j}: {ex.Message}", ex); }
        double c = s.Curvature, k = s.Conic;
        if (shape is null)
        {
            if (c == 0) return PlaneCap.Instance;
            if (k == 0) return new SphereCap(sign / c);
            string name = string.Create(CultureInfo.InvariantCulture, $"surface {j} (Standard conic, R = {1 / c:g8} m, k = {k:g8})");
            return new GeneralCap(name, r => Sequential.Aspheres.ConicSag.TryEvaluate(r, c, k, out double z, out double dz)
                ? (sign * z, sign * dz) : (double.NaN, double.NaN));
        }
        if (shape is RadialSagShape radial)
        {
            string name = $"surface {j} ({shape.TypeName})";
            return new GeneralCap(name, r => radial.TryRadialSag(r, s, out double z, out double dz) && double.IsFinite(z) && double.IsFinite(dz)
                ? (sign * z, sign * dz) : (double.NaN, double.NaN));
        }
        throw new InvalidOperationException($"Surface {j}: CAD export of the {shape.TypeName} surface is not built; only rotationally symmetric surfaces (the Standard conic and the radial sag forms) are exported.");
    }
}
