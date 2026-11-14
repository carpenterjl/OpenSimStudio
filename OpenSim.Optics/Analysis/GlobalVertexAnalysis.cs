using System.ComponentModel;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="GlobalVertexAnalysis"/>.</summary>
public sealed record GlobalVertexSettings
{
    /// <summary>The surface whose frame the positions and rotations are given in (OpticStudio's
    /// global coordinate reference surface); 0 for the global frame rays are launched in.</summary>
    [DisplayName("Reference surface")]
    [Description("Surface whose local frame the vertices are listed in; 0 for the launch frame (object-space axes at surface 1's untilted vertex).")]
    public int ReferenceSurface { get; init; } = 1;
}

/// <summary>
/// Every surface's vertex and orientation in the frame of a reference surface (OpticStudio's
/// Global Vertex listing) from <see cref="SurfacePlacement"/> (OP-21): position, the tilts that
/// reproduce the rotation in the order decenter then tilt, the rotation matrix whose columns are
/// the surface's local axes, and the values of every coordinate break with returns solved.
/// <para>
/// The lens comes from a source given to the constructor, as for the single ray trace; the
/// default has none until the lens data editor (OP-31).
/// </para>
/// </summary>
public sealed class GlobalVertexAnalysis : OpticsAnalysis<GlobalVertexSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "GlobalVertex";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for none (until OP-31).</param>
    public GlobalVertexAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? (_ => null);
    }

    public override string Id => SavedId;
    public override string Name => "Global vertex";
    public override string Category => "Reports";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, GlobalVertexSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null)
        {
            return OpticsAnalysisResult.FromSections(Name, new[]
            {
                new ReportSection("No surfaces")
                {
                    Lines = new[] { "The system has no sequential surfaces yet: the lens data editor is a later plan item (OP-31), so there are no vertices to list." }
                }
            }, new[] { "Nothing is listed." });
        }
        var placement = SurfacePlacement.Compute(lens);
        int reference = settings.ReferenceSurface;
        if (reference < 0 || reference > lens.ImageIndex)
            throw new ArgumentException($"Global vertex: the reference surface must be 0 (launch frame) or 1 to {lens.ImageIndex} (it is {reference}).");

        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        string L(double meters) => StudyReport.Number(OpticsConventions.FromMeters(meters, unit), "g10");
        string N(double v) => StudyReport.Number(v, "g10");
        string Label(int j) => j == 0 ? "OBJ" : j == lens.ImageIndex ? "IMA" : lens.Surfaces[j].IsCoordinateBreak ? $"{j} (break)" : j == lens.StopIndex ? $"{j} (stop)" : j.ToString();

        var vertices = new List<IReadOnlyList<string>>();
        var matrices = new List<IReadOnlyList<string>>();
        int firstRow = lens.ObjectAtInfinity ? 1 : 0;
        for (int j = firstRow; j < lens.Surfaces.Count; j++)
        {
            cancellation.ThrowIfCancellationRequested();
            var t = reference == 0 ? placement.Global(j) : placement.RelativeTo(j, reference);
            var tilts = TiltDecenter.FromTransform(t with { Translation = Vector3D.Zero }, TiltDecenterOrder.DecenterThenTilt, out _);
            vertices.Add(new[]
            {
                Label(j), L(t.Translation.X), L(t.Translation.Y), L(t.Translation.Z),
                StudyReport.Number(tilts.TiltXDegrees, "g10"), StudyReport.Number(tilts.TiltYDegrees, "g10"), StudyReport.Number(tilts.TiltZDegrees, "g10"),
            });
            matrices.Add(new[]
            {
                Label(j), N(t.Col0.X), N(t.Col1.X), N(t.Col2.X), N(t.Col0.Y), N(t.Col1.Y), N(t.Col2.Y), N(t.Col0.Z), N(t.Col1.Z), N(t.Col2.Z),
            });
        }

        var breaks = new List<IReadOnlyList<string>>();
        for (int j = 1; j < lens.ImageIndex; j++)
        {
            var s = lens.Surfaces[j];
            if (s.CoordinateBreak is not { } cb) continue;
            var v = placement.ResolvedBefore(j);
            breaks.Add(new[]
            {
                j.ToString(), L(v.DecenterX), L(v.DecenterY),
                StudyReport.Number(v.TiltXDegrees, "g10"), StudyReport.Number(v.TiltYDegrees, "g10"), StudyReport.Number(v.TiltZDegrees, "g10"),
                v.Order == TiltDecenterOrder.DecenterThenTilt ? "0 (decenter, tilt)" : "1 (tilt, decenter)",
                L(placement.Thickness(j)),
                cb.Return == CoordinateReturnMode.None ? "—" : $"{ReturnLabel(cb.Return)} to surface {cb.ReturnToSurface}",
            });
        }

        string frame = reference == 0
            ? "the launch frame (object-space axes, origin at surface 1's vertex before its own tilt)"
            : $"surface {reference}'s local frame";
        var sections = new List<ReportSection>
        {
            new("Vertices")
            {
                TableHeaders = new[] { "Surface", $"X ({u})", $"Y ({u})", $"Z ({u})", "Tilt X (°)", "Tilt Y (°)", "Tilt Z (°)" },
                TableRows = vertices,
                Lines = new[]
                {
                    $"Vertex positions in {frame}.",
                    "Tilts reproduce each surface's orientation as a coordinate break in the order decenter then tilt (about x, the new y, the new z, right-handed). At a y tilt of ±90° the z tilt is reported as 0.",
                    lens.ObjectAtInfinity ? "The object is at infinity, so it has no vertex row." : "The OBJ row is the object plane.",
                }
            },
            new("Rotation matrices")
            {
                TableHeaders = new[] { "Surface", "R11", "R12", "R13", "R21", "R22", "R23", "R31", "R32", "R33" },
                TableRows = matrices,
                Lines = new[] { "Global = vertex + R·local: the columns of R are the surface's local x, y and z axes." }
            },
        };
        if (breaks.Count > 0)
        {
            sections.Add(new ReportSection("Coordinate breaks")
            {
                TableHeaders = new[] { "Surface", $"Decenter X ({u})", $"Decenter Y ({u})", "Tilt X (°)", "Tilt Y (°)", "Tilt Z (°)", "Order", $"Thickness ({u})", "Return" },
                TableRows = breaks,
                Lines = new[] { "Values with any coordinate return solved; a solved break is traced with its exact transform, and its degrees are read back from it." }
            });
        }
        bool axial = lens.IsAxial(out string? reason);
        var assumptions = new List<string>
        {
            "Coordinate breaks and surface tilts follow docs/Optics-Conventions.md §4 and §15.",
            axial ? "The lens is axial (or folded by plane mirrors only), so the paraxial analyses describe it."
                  : "The lens is not axial, so the paraxial analyses refuse it: " + reason,
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions);
    }

    private static string ReturnLabel(CoordinateReturnMode mode) => mode switch
    {
        CoordinateReturnMode.OrientationOnly => "Orientation only",
        CoordinateReturnMode.OrientationXY => "Orientation XY",
        CoordinateReturnMode.OrientationXYZ => "Orientation XYZ",
        _ => "None",
    };
}
