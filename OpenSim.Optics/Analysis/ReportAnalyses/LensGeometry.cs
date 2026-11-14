// OP-44
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>One element of <see cref="LensGeometry.Elements"/>: the space after
/// <see cref="Front"/> up to <see cref="Back"/> filled with <see cref="Medium"/>.</summary>
internal sealed record ElementRow(int Front, int Back, ParaxialMedium Medium)
{
    public ElementVolumeResult? Volume { get; init; }

    /// <summary>Why there is no volume, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>The catalog density, g/cm³, or null.</summary>
    public double? Density => Medium.Material?.Density;

    /// <summary>The mass in grams, or null without a volume or a density.</summary>
    public double? Grams => Volume is { } v && Density is double d ? v.CubicCentimeters * d : null;
}

/// <summary>
/// The geometry the report analyses share (docs/Optics-Conventions.md §29): the surface
/// placement (OP-21), the clear semi-diameter of every surface (as entered, or sized from rays by
/// <see cref="SemiDiameterSizing"/> when automatic), edge thicknesses and element volumes.
/// </summary>
internal sealed class LensGeometry
{
    private readonly double[] _semi;
    private readonly bool[] _automatic;

    public SequentialLens Lens { get; }
    public SurfacePlacement Placement { get; }

    /// <summary>Why the automatic semi-diameters could not be sized, or null.</summary>
    public string? SizingError { get; }

    /// <summary>Rays that failed while sizing, one line each.</summary>
    public IReadOnlyList<string> SizingNotes { get; } = Array.Empty<string>();

    private LensGeometry(SequentialLens lens, SurfacePlacement placement, double[] semi, bool[] automatic, string? sizingError, IReadOnlyList<string> notes)
    {
        Lens = lens;
        Placement = placement;
        _semi = semi;
        _automatic = automatic;
        SizingError = sizingError;
        SizingNotes = notes;
    }

    /// <summary>The geometry of <paramref name="lens"/>; the automatic semi-diameters are sized
    /// with <paramref name="system"/> only when some surface has one.</summary>
    public static LensGeometry Create(SequentialLens lens, OpticalSystemData system, CancellationToken cancellation)
    {
        var placement = SurfacePlacement.Compute(lens);
        int count = lens.Surfaces.Count;
        var semi = new double[count];
        var automatic = new bool[count];
        string? error = null;
        IReadOnlyList<string> notes = Array.Empty<string>();
        bool needsSizing = false;
        for (int j = 1; j < count; j++)
        {
            if (lens.Surfaces[j].SemiDiameter is double sd) semi[j] = sd;
            else if (!lens.Surfaces[j].IsCoordinateBreak)
            {
                // The image surface's semi-diameter enters no report value, so it alone does not
                // start a sizing trace.
                automatic[j] = true;
                semi[j] = double.NaN;
                needsSizing |= j < lens.ImageIndex;
            }
        }
        if (needsSizing)
        {
            try
            {
                var report = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation);
                for (int j = 1; j < count; j++)
                    if (automatic[j]) semi[j] = report.Clear[j];
                notes = report.Notes;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                error = ex.Message.Replace("\r", "").Replace('\n', ' ');
            }
        }
        return new LensGeometry(lens, placement, semi, automatic, error, notes);
    }

    /// <summary>True when surface j's clear semi-diameter is automatic.</summary>
    public bool IsAutomatic(int j) => _automatic[j];

    /// <summary>The clear semi-diameter of surface j, meters; NaN when automatic and not sized.</summary>
    public double SemiDiameter(int j) => _semi[j];

    /// <summary>The axial distance from surface <paramref name="j"/>'s vertex to surface
    /// <paramref name="k"/>'s when k's frame is j's moved along j's z only (no tilt, no lateral
    /// decenter, to rounding); null otherwise.</summary>
    public double? CoaxialDistance(int j, int k)
    {
        var t = Placement.RelativeTo(k, j);
        const double tol = 1e-12;
        bool rotationFree = (t.Col0 - Vector3D.UnitX).Length <= tol && (t.Col1 - Vector3D.UnitY).Length <= tol && (t.Col2 - Vector3D.UnitZ).Length <= tol;
        double lateral = Math.Sqrt(t.Translation.X * t.Translation.X + t.Translation.Y * t.Translation.Y);
        double size = Math.Max(Math.Abs(t.Translation.Z), Math.Max(Math.Abs(_semi[j]), 1e-9));
        return rotationFree && lateral <= tol * size ? t.Translation.Z : null;
    }

    /// <summary>
    /// The edge thickness of surface j along x and y: the axial distance to the next surface plus
    /// that surface's sag minus this one's, both at (a, 0) and (0, a) with a this surface's clear
    /// semi-diameter. Null with the reason when the next surface is not coaxial with this one, a
    /// sag is undefined there, or the semi-diameter is unknown.
    /// </summary>
    public (double X, double Y)? EdgeThickness(int j, out string? why)
    {
        why = null;
        var s = Lens.Surfaces[j];
        var next = Lens.Surfaces[j + 1];
        if (s.IsCoordinateBreak || next.IsCoordinateBreak) { why = "coordinate break"; return null; }
        double a = _semi[j];
        if (!double.IsFinite(a)) { why = "semi-diameter not sized"; return null; }
        if (CoaxialDistance(j, j + 1) is not double t) { why = "next surface tilted or decentered"; return null; }
        double ex = t + ElementVolume.Sag(next, a, 0) - ElementVolume.Sag(s, a, 0);
        double ey = t + ElementVolume.Sag(next, 0, a) - ElementVolume.Sag(s, 0, a);
        if (!double.IsFinite(ex) || !double.IsFinite(ey)) { why = "sag undefined at the semi-diameter"; return null; }
        return (ex, ey);
    }

    /// <summary>
    /// Every glass element: each space between surfaces j and j + 1 whose medium is not the
    /// system's air (a cemented doublet is two elements). The volume is computed
    /// (<see cref="ElementVolume"/>) when both faces are refracting surfaces (not mirrors,
    /// coordinate breaks or ignored surfaces), coaxial, with known semi-diameters; otherwise the
    /// row says why not.
    /// </summary>
    public IReadOnlyList<ElementRow> Elements(CancellationToken cancellation)
    {
        var media = ReportText.SpaceMedia(Lens);
        var rows = new List<ElementRow>();
        for (int j = 1; j < Lens.LastSurfaceIndex; j++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (ReportText.IsAir(media[j])) continue;
            var row = new ElementRow(j, j + 1, media[j]);
            var front = Lens.Surfaces[j];
            var back = Lens.Surfaces[j + 1];
            string? problem = null;
            if (front.IsMirror || back.IsMirror) problem = "a face is a mirror";
            else if (front.IsCoordinateBreak || back.IsCoordinateBreak) problem = "a face is a coordinate break";
            else if (front.IsIgnored || back.IsIgnored) problem = "a face is ignored";
            else if (!double.IsFinite(_semi[j]) || !double.IsFinite(_semi[j + 1])) problem = "automatic semi-diameters not sized" + (SizingError is null ? "" : ": " + SizingError);
            else if (CoaxialDistance(j, j + 1) is not double t) problem = "the faces are tilted or decentered relative to each other";
            else
            {
                try
                {
                    row = row with { Volume = ElementVolume.Compute(front, _semi[j], back, _semi[j + 1], t, $"Element {j}–{j + 1}") };
                }
                catch (InvalidOperationException ex) { problem = ex.Message; }
            }
            rows.Add(problem is null ? row : row with { Problem = problem });
        }
        return rows;
    }

    /// <summary>The element table's section.</summary>
    public static ReportSection ElementSection(IReadOnlyList<ElementRow> rows, string heading = "Element volume")
    {
        string N(double? v) => v is double d ? StudyReport.Number(d, "g8") : "—";
        var table = rows.Select(r => (IReadOnlyList<string>)new[]
        {
            $"{r.Front}–{r.Back}", ReportText.Describe(r.Medium),
            N(r.Volume?.CubicCentimeters),
            r.Density is double d ? StudyReport.Number(d, "g6") : r.Medium.Material is null ? "— (constant index)" : "— (not in catalog)",
            N(r.Grams),
            r.Problem is { } p ? "not computed: " + p : r.Volume!.Method,
        }).ToArray();
        var lines = new List<string>();
        double total = rows.Where(r => r.Grams is not null).Sum(r => r.Grams!.Value);
        int withMass = rows.Count(r => r.Grams is not null);
        if (rows.Count == 0) lines.Add("The lens has no glass elements (every space is the system's air).");
        else lines.Add($"Total mass of the {withMass} element(s) with a volume and a catalog density: {StudyReport.Number(total, "g8")} g.");
        return new ReportSection(heading)
        {
            TableHeaders = new[] { "Surfaces", "Material", "Volume (cc)", "Density (g/cc)", "Mass (g)", "Method" },
            TableRows = table,
            Lines = lines,
        };
    }

    /// <summary>What every element volume assumes.</summary>
    public static readonly string VolumeAssumption =
        "Element volumes: each face has a circular edge at its clear semi-diameter, and the smaller face is squared up to the larger semi-diameter (OpticStudio 2025 R2 help, \"Prescription Data\", Comments on Computing Element Volumes); Standard faces are integrated exactly, other shapes numerically (docs/Optics-Conventions.md §29). Surface apertures (rectangular, elliptical, decentered) do not enter; mass is volume × the catalog density.";
}
