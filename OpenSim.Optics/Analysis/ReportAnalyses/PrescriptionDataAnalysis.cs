// OP-44
using System.ComponentModel;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="PrescriptionDataAnalysis"/>: the sections to list, as
/// OpticStudio's Prescription Data settings choose them, and the global coordinate reference.</summary>
public sealed record PrescriptionDataSettings
{
    /// <summary>The surface whose frame global vertices and centres of curvature are given in; 0
    /// for the launch frame.</summary>
    [DisplayName("Global reference surface")]
    [Description("Surface whose local frame the global vertices and centres of curvature are listed in; 0 for the launch frame.")]
    public int ReferenceSurface { get; init; } = 1;

    [DisplayName("General data")] public bool GeneralData { get; init; } = true;
    [DisplayName("Edge thickness")] public bool EdgeThickness { get; init; } = true;
    [DisplayName("Solves and variables")] public bool SolvesAndVariables { get; init; } = true;
    [DisplayName("Index data")] public bool IndexData { get; init; } = true;
    [DisplayName("Global vertex")] public bool GlobalVertex { get; init; } = true;
    [DisplayName("Center of curvature")] public bool CenterOfCurvature { get; init; } = true;
    [DisplayName("Element volume")] public bool ElementVolume { get; init; } = true;
    [DisplayName("Cardinal points")] public bool CardinalPoints { get; init; } = true;
    [DisplayName("Files used")] public bool FilesUsed { get; init; } = true;
}

/// <summary>
/// OpticStudio's Prescription Data report (2025 R2 help, "Prescription Data (reports group, the
/// analyze tab, sequential ui mode)"), its sections in the help's order as far as OpenSim has
/// the data: General Data, Surface Data, Surface Detail (with tilts and decenters, apertures and
/// edge thickness), Solves/Variables, Index Data, Global Vertex, Center Of Curvature, Element
/// Volume and Cardinal Points, then Files Used. Multi-Config Data, F/ Numbers (real working F/#
/// per field and wavelength) and POP Settings are listed as not built
/// (docs/Optics-Conventions.md §29).
/// <para>
/// The surface sections (Surface data, Surface detail, Tilts and decenters, Apertures) give
/// every value the trace uses with round-trip precision, so <see cref="PrescriptionReader"/>
/// reads the report's text back into the saved surfaces that reproduce the lens.
/// </para>
/// </summary>
public sealed class PrescriptionDataAnalysis : OpticsAnalysis<PrescriptionDataSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "PrescriptionData";

    // Headings the reader looks for; changing one breaks reading back older reports.
    public const string SurfaceDataHeading = "Surface data";
    public const string SurfaceDetailHeading = "Surface detail";
    public const string TiltsHeading = "Tilts and decenters";
    public const string AperturesHeading = "Apertures";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for the project's saved surfaces (solves evaluated).</param>
    public PrescriptionDataAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? ProjectLens.Sequential;
    }

    public override string Id => SavedId;
    public override string Name => "Prescription data";
    public override string Category => "Reports";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, PrescriptionDataSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return ReportText.NoSurfaces(Name);
        if (settings.ReferenceSurface < 0 || settings.ReferenceSurface > lens.ImageIndex)
            throw new ArgumentException($"Prescription data: the global reference surface must be 0 (launch frame) or 1 to {lens.ImageIndex} (it is {settings.ReferenceSurface}).");
        var unit = system.Units.LensUnit;
        var geometry = LensGeometry.Create(lens, system, cancellation);
        var sections = new List<ReportSection>();
        var assumptions = new List<string>();
        string u = ConventionNames.Symbol(unit);

        // ---- General data ----
        ParaxialSystem? model = null;
        string? paraxialError = null;
        try { model = ParaxialSystem.Create(lens.ToParaxialLens(), system); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { paraxialError = ex.Message.Replace("\r", "").Replace('\n', ' '); }
        cancellation.ThrowIfCancellationRequested();
        if (settings.GeneralData)
        {
            sections.Add(SystemInputs("General data", system, lens));
            if (model is not null)
            {
                var first = ParaxialReport.Sections(model, null, cardinalPoints: false, rayData: false)[0];
                sections.Add(first with { Heading = "First-order data" });
                assumptions.AddRange(ParaxialReport.Assumptions(model));
            }
            else sections.Add(new ReportSection("First-order data") { Lines = new[] { "Not computed: " + paraxialError } });
        }

        // ---- Surface data, detail, tilts, apertures (read back by PrescriptionReader) ----
        var saved = SavedSurfaces.FromLens(lens, unit);
        sections.AddRange(SurfaceSections(lens, saved, geometry, unit));
        assumptions.Add($"Surface sections list the lens as the trace takes it, in {u}, with every number in round-trip precision so the text reads back to the same lens (PrescriptionReader); radii are vertex radii and the Material column is the medium after the surface. Variable, solve and pickup flags and drawing-only options (hide rays, do not draw, row colour) are not part of the read-back.");
        if (geometry.SizingError is not null)
            assumptions.Add("Automatic semi-diameters could not be sized: " + geometry.SizingError);
        assumptions.AddRange(geometry.SizingNotes);

        if (settings.EdgeThickness)
            sections.Add(EdgeSection(geometry, unit));

        // ---- Solves and variables ----
        if (settings.SolvesAndVariables)
            sections.Add(SolvesSection(context.System.Surfaces));

        // ---- Index data ----
        if (settings.IndexData)
            sections.Add(IndexSection(lens, system));

        // ---- Global vertex (the OP-21 analysis, reused) ----
        if (settings.GlobalVertex)
        {
            var vertex = new GlobalVertexAnalysis(_ => lens).Compute(context, new GlobalVertexSettings { ReferenceSurface = settings.ReferenceSurface }, cancellation);
            foreach (var s in vertex.Sections) sections.Add(s with { Heading = "Global vertex: " + s.Heading });
            foreach (var a in vertex.Assumptions) if (!assumptions.Contains(a)) assumptions.Add(a);
        }

        // ---- Center of curvature ----
        if (settings.CenterOfCurvature)
            sections.Add(CenterSection(geometry, settings.ReferenceSurface, unit));

        // ---- Element volume ----
        if (settings.ElementVolume)
        {
            sections.Add(LensGeometry.ElementSection(geometry.Elements(cancellation)));
            assumptions.Add(LensGeometry.VolumeAssumption);
        }

        // ---- Cardinal points ----
        if (settings.CardinalPoints)
        {
            if (model is not null)
                sections.Add(CardinalPlanes.Compute(model, 1, lens.LastSurfaceIndex).ToSection(unit));
            else
                sections.Add(new ReportSection("Cardinal points") { Lines = new[] { "Not computed: " + paraxialError } });
        }

        // ---- Files used ----
        if (settings.FilesUsed)
            sections.Add(FilesSection(lens, context));

        sections.Add(new ReportSection("Not in this report")
        {
            Lines = new[]
            {
                "Multi-Config Data: multi-configuration is a separate plan item (OP-30).",
                "F/ Numbers (real working F/# for each field and wavelength): not built; the paraxial working F/# is in First-order data.",
                "POP Settings: physical optics propagation is not built.",
                "Surface detail for non-sequential groups: not built.",
            }
        });
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions.Distinct().ToList());
    }

    // ------------------------------------------------------------------ sections

    /// <summary>Lens units, aperture, fields and wavelengths as entered.</summary>
    internal static ReportSection SystemInputs(string heading, OpticalSystemData system, SequentialLens? lens)
    {
        var unit = system.Units.LensUnit;
        string u = ConventionNames.Symbol(unit);
        var ap = system.Aperture;
        string apValue = ApertureTypes.HasNoValue(ap.Type) ? "—"
            : ApertureTypes.IsLength(ap.Type) ? $"{ReportText.Length(ap.Value, unit)} {u}"
            : StudyReport.Number(ap.Value, "g10");
        bool angle = FieldTypes.IsAngle(system.Fields.Type);
        var pairs = new List<(string, string)>
        {
            ("Lens units", ConventionNames.LensUnits.Label(unit)),
            ("Aperture", $"{ApertureTypes.Names.Label(ap.Type)} {apValue}"),
            ("Field type", FieldTypes.Names.Label(system.Fields.Type) + (angle ? " (degrees)" : $" ({u})")),
            ("Fields", string.Join("; ", system.Fields.Points.Select((p, i) =>
                $"{i + 1}: ({F(p.X, angle, unit)}, {F(p.Y, angle, unit)})"))),
            ("Wavelengths (µm)", string.Join("; ", system.Wavelengths.Wavelengths.Select((w, i) =>
                $"{i + 1}: {StudyReport.Number(w.Micrometers, "g8")}" + (i == system.Wavelengths.PrimaryIndex ? " (primary)" : "")))),
        };
        if (lens is not null)
            pairs.Add(("Surfaces", $"{lens.Surfaces.Count} (object, {lens.Surfaces.Count - 2} surfaces, image); stop on surface {lens.StopIndex}"));
        return new ReportSection(heading) { Pairs = pairs };

        static string F(double v, bool angle, LensUnit unit) => angle ? StudyReport.Number(v, "g8") : ReportText.Length(v, unit, "g8");
    }

    internal static IEnumerable<ReportSection> SurfaceSections(SequentialLens lens, IReadOnlyList<OpticsSurfaceSettings> saved,
        LensGeometry geometry, LensUnit unit)
    {
        string u = ConventionNames.Symbol(unit);
        string E(double v) => ReportText.Exact(v);
        var data = new List<IReadOnlyList<string>>();
        var detail = new List<IReadOnlyList<string>>();
        var tilts = new List<IReadOnlyList<string>>();
        var apertures = new List<IReadOnlyList<string>>();
        for (int j = 0; j < saved.Count; j++)
        {
            var s = saved[j];
            string label = ReportText.Label(lens, j);
            string semi = s.SemiDiameter is double sd ? E(sd)
                : geometry.IsAutomatic(j) && double.IsFinite(geometry.SemiDiameter(j))
                    ? $"auto ({StudyReport.Number(OpticsConventions.FromMeters(geometry.SemiDiameter(j), unit), "g8")})"
                    : "auto";
            string material = SavedSurfaces.FormatMaterial(s);
            data.Add(new[]
            {
                label, s.Type ?? SavedSurfaces.StandardType,
                s.Radius is double r ? E(r) : "Infinity",
                s.ThicknessIsInfinite ? "Infinity" : E(s.Thickness),
                material.Length == 0 ? "—" : material,
                semi, E(s.Conic), ReportText.Quote(s.Comment ?? ""),
            });

            var layout = SurfaceTypes.Find(s.Type);
            foreach (var p in (s.Parameters ?? new()).Concat(s.ExtraData ?? new()).OrderBy(p => p.Number))
                detail.Add(new[] { label, $"Parameter {p.Number} ({layout?.Parameter(p.Number)?.Name ?? "?"})", E(p.Value) });
            if (s.CoordinateReturn is { } cr)
                detail.Add(new[] { label, "Coordinate return", $"{cr.Mode} to surface {cr.Surface}" });
            if (s.MechanicalSemiDiameter is double mech) detail.Add(new[] { label, "Mechanical semi-diameter", E(mech) });
            if (s.ChipZone != 0) detail.Add(new[] { label, "Chip zone", E(s.ChipZone) });
            if (s.Tce != 0) detail.Add(new[] { label, "TCE", E(s.Tce) });
            if (s.IsIgnored) detail.Add(new[] { label, "Ignored", "yes" });
            if (s.SkipRaysTo) detail.Add(new[] { label, "Skip rays to", "yes" });
            if (s.Coating is { Length: > 0 } coating) detail.Add(new[] { label, "Coating", coating });

            if (s.TiltBefore is { } tb) tilts.Add(TiltRow(label, "before", tb));
            if (s.TiltAfter is { } ta) tilts.Add(TiltRow(label, "after", ta));

            if (s.Aperture is { } a)
                apertures.Add(new[]
                {
                    label, a.Type ?? "—", a.IsObscuration ? "yes" : "no", E(a.DecenterX), E(a.DecenterY),
                    E(a.MinRadius), E(a.MaxRadius), E(a.HalfWidthX), E(a.HalfWidthY), E(a.ArmWidth),
                    a.ArmCount.ToString(ReportText.Ci),
                    a.Vertices is { Count: > 0 } v ? string.Join("; ", v.Select(p => E(p.X) + "," + E(p.Y))) : "—",
                });
        }
        yield return new ReportSection(SurfaceDataHeading)
        {
            TableHeaders = new[] { "Surface", "Type", $"Radius ({u})", $"Thickness ({u})", "Material", $"Semi-diameter ({u})", "Conic", "Comment" },
            TableRows = data,
        };
        if (detail.Count > 0)
            yield return new ReportSection(SurfaceDetailHeading)
            {
                TableHeaders = new[] { "Surface", "Item", "Value" },
                TableRows = detail,
            };
        if (tilts.Count > 0)
            yield return new ReportSection(TiltsHeading)
            {
                TableHeaders = new[] { "Surface", "Where", "Mode", $"Decenter X ({u})", $"Decenter Y ({u})", "Tilt X (°)", "Tilt Y (°)", "Tilt Z (°)", "Order", "Pick-up surface" },
                TableRows = tilts,
            };
        if (apertures.Count > 0)
            yield return new ReportSection(AperturesHeading)
            {
                TableHeaders = new[] { "Surface", "Type", "Obscuration", $"Decenter X ({u})", $"Decenter Y ({u})", $"Min radius ({u})", $"Max radius ({u})",
                    $"Half-width X ({u})", $"Half-width Y ({u})", $"Arm width ({u})", "Arm count", $"Vertices ({u})" },
                TableRows = apertures,
            };

        static IReadOnlyList<string> TiltRow(string label, string where, OpticsTiltDecenterSettings t)
        {
            string mode = t.Mode ?? nameof(TiltAfterKind.Explicit);
            bool explicitValues = mode == nameof(TiltAfterKind.Explicit);
            string V(double v) => explicitValues ? ReportText.Exact(v) : "—";
            bool pickSurface = mode is nameof(TiltAfterKind.PickUpSurface) or nameof(TiltAfterKind.ReverseSurface);
            return new[]
            {
                label, where, mode, V(t.DecenterX), V(t.DecenterY), V(t.TiltX), V(t.TiltY), V(t.TiltZ),
                explicitValues ? t.Order ?? nameof(TiltDecenterOrder.DecenterThenTilt) : "—",
                pickSurface ? t.Surface.ToString(ReportText.Ci) : "—",
            };
        }
    }

    internal static ReportSection EdgeSection(LensGeometry geometry, LensUnit unit)
    {
        string u = ConventionNames.Symbol(unit);
        var lens = geometry.Lens;
        var rows = new List<IReadOnlyList<string>>();
        for (int j = 1; j < lens.ImageIndex; j++)
        {
            var e = geometry.EdgeThickness(j, out string? why);
            rows.Add(e is { } v
                ? new[] { ReportText.Label(lens, j), ReportText.Length(v.X, unit), ReportText.Length(v.Y, unit) }
                : new[] { ReportText.Label(lens, j), "— (" + why + ")", "— (" + why + ")" });
        }
        return new ReportSection("Edge thickness")
        {
            TableHeaders = new[] { "Surface", $"X edge ({u})", $"Y edge ({u})" },
            TableRows = rows,
            Lines = new[] { "Axial distance to the next surface plus its sag minus this surface's sag, at (a, 0) for X and (0, a) for Y, a this surface's clear semi-diameter." },
        };
    }

    internal static ReportSection SolvesSection(IReadOnlyList<OpticsSurfaceSettings>? surfaces)
    {
        var rows = new List<IReadOnlyList<string>>();
        if (surfaces is not null)
            for (int j = 0; j < surfaces.Count; j++)
                foreach (var f in surfaces[j]?.Flags ?? new())
                {
                    string detail = f.Kind switch
                    {
                        SavedSurfaces.SolveFlag => (f.Solve ?? "?") + (f.SolveParameters is { Count: > 0 } p ? " (" + string.Join(", ", p.Select(ReportText.Exact)) + ")" : ""),
                        SavedSurfaces.PickupFlag => $"from surface {f.PickupSurface}, {f.PickupColumn ?? f.Column}, scale {ReportText.Exact(f.PickupScale)}, offset {ReportText.Exact(f.PickupOffset)}",
                        _ => "",
                    };
                    rows.Add(new[] { j.ToString(ReportText.Ci), f.Column ?? "?", f.Kind ?? "?", detail.Length == 0 ? "—" : detail });
                }
        return rows.Count == 0
            ? new ReportSection("Solves and variables") { Lines = new[] { "No cell is variable, solved or picked up." } }
            : new ReportSection("Solves and variables")
            {
                TableHeaders = new[] { "Surface", "Cell", "Kind", "Detail" },
                TableRows = rows,
                Lines = new[] { "The values in the surface sections are the solved ones." },
            };
    }

    internal static ReportSection IndexSection(SequentialLens lens, OpticalSystemData system)
    {
        var media = ReportText.SpaceMedia(lens);
        var waves = system.Wavelengths.Wavelengths;
        var headers = new List<string> { "Surface", "Medium after" };
        headers.AddRange(waves.Select(w => $"n at {StudyReport.Number(w.Micrometers, "g8")} µm"));
        headers.Add("Glass TCE (1e-6/K)");
        headers.Add("Surface TCE (1e-6/K)");
        var rows = new List<IReadOnlyList<string>>();
        var errors = new List<string>();
        for (int j = 0; j < lens.ImageIndex; j++)
        {
            var row = new List<string> { ReportText.Label(lens, j), ReportText.Describe(media[j]) + (lens.Surfaces[j].IsMirror ? " (mirror)" : "") };
            foreach (var w in waves)
            {
                try { row.Add(StudyReport.Number(media[j].IndexAt(w.Micrometers, system.Environment), "F8")); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    row.Add("error");
                    errors.Add($"Surface {j} at {StudyReport.Number(w.Micrometers)} µm: {ex.Message}");
                }
            }
            row.Add(media[j].Material?.ThermalExpansion is double tce ? StudyReport.Number(tce, "g6") : "—");
            row.Add(StudyReport.Number(lens.Surfaces[j].ThermalExpansion, "g6"));
            rows.Add(row);
        }
        var lines = new List<string>
        {
            "Indices are relative to the system's air at each wavelength as the table gives it, " + (system.Environment.AdjustIndexToEnvironment
                ? "adjusted to the system temperature and pressure (SCHOTT TIE-19)." : "at each catalog's reference conditions."),
            "A mirror, a coordinate break and an ignored surface keep the medium before them.",
        };
        lines.AddRange(errors);
        return new ReportSection("Index data") { TableHeaders = headers, TableRows = rows, Lines = lines };
    }

    internal static ReportSection CenterSection(LensGeometry geometry, int reference, LensUnit unit)
    {
        string u = ConventionNames.Symbol(unit);
        var lens = geometry.Lens;
        var rows = new List<IReadOnlyList<string>>();
        for (int j = 1; j <= lens.ImageIndex; j++)
        {
            var s = lens.Surfaces[j];
            if (s.IsCoordinateBreak || s.Curvature == 0) continue;
            if (s.Shape is not null and not RadialSagShape) continue;
            var t = reference == 0 ? geometry.Placement.Global(j) : geometry.Placement.RelativeTo(j, reference);
            var c = t.Apply(new Vector3D(0, 0, 1 / s.Curvature));
            rows.Add(new[] { ReportText.Label(lens, j), ReportText.Length(c.X, unit), ReportText.Length(c.Y, unit), ReportText.Length(c.Z, unit) });
        }
        string frame = reference == 0 ? "the launch frame" : $"surface {reference}'s local frame";
        return rows.Count == 0
            ? new ReportSection("Center of curvature") { Lines = new[] { "No rotationally symmetric surface has curvature." } }
            : new ReportSection("Center of curvature")
            {
                TableHeaders = new[] { "Surface", $"X ({u})", $"Y ({u})", $"Z ({u})" },
                TableRows = rows,
                Lines = new[] { $"The vertex centre of curvature (the point at the vertex radius along the surface's local z) of each rotationally symmetric surface with curvature, in {frame}." },
            };
    }

    internal static ReportSection FilesSection(SequentialLens lens, OpticsAnalysisContext context)
    {
        var lines = new List<string>();
        var catalogs = new List<string>();
        foreach (var s in lens.Surfaces)
            if (s.Medium.Material is { } m && m.Catalog.Length > 0 && !catalogs.Contains(m.Catalog)) catalogs.Add(m.Catalog);
        foreach (var name in catalogs)
        {
            var catalog = context.Catalogs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            string file = catalog?.SourcePath is { Length: > 0 } path ? Path.GetFileName(path) : "built in or not among the active catalogs";
            lines.Add($"Glass catalog {name}: {file}");
        }
        var coatings = lens.Surfaces.Select(s => s.Coating).Where(c => c is { Length: > 0 }).Distinct().ToList();
        if (coatings.Count > 0) lines.Add("Coatings (the project's coating catalog): " + string.Join(", ", coatings));
        if (lines.Count == 0) lines.Add("No glass catalog or coating is used (constant indices only).");
        return new ReportSection("Files used") { Lines = lines };
    }
}
