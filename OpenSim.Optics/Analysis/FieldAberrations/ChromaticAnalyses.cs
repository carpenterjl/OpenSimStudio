using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// OP-37: Longitudinal Aberration, Lateral Color and Chromatic Focal Shift.

internal static class ChromaticCommon
{
    /// <summary>The paraxial model at a row, with a real-image-height field type read as
    /// paraxial image height (the paraxial engine refuses real image height; the ray launcher
    /// reads it the same way, RayLauncher.Paraxial).</summary>
    public static ParaxialSystem Paraxial(ParaxialLens lens, OpticalSystemData system, int row)
    {
        if (system.Fields.Type == FieldType.RealImageHeight)
            system = system with { Fields = system.Fields with { Type = FieldType.ParaxialImageHeight } };
        return ParaxialSystem.Create(lens, system, row);
    }
}

// ---------------------------------------------------------------------------------------------
// Longitudinal aberration.
// ---------------------------------------------------------------------------------------------

/// <summary>Settings of the <see cref="LongitudinalAberrationAnalysis"/>.</summary>
public sealed record LongitudinalAberrationSettings
{
    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Pupil zones")]
    [Description("Zones from the axis (paraxial) to the rim of the pupil, evenly spaced; 3 to 1001.")]
    public int Zones { get; init; } = 21;
}

/// <summary>
/// OpticStudio's Longitudinal Aberration (OP-37; Ansys OpticStudio 2025 R2 help, "Longitudinal
/// Aberration"): "the distance from the image surface to where a zonal marginal ray 'focuses', or
/// crosses the optical axis. The computation is performed only for the on axis field point, and
/// only for zonal marginal tangential rays as a function of pupil zone." Here zone ρ is the real
/// ray (Hx, Hy, Px, Py) = (0, 0, 0, ρ) at each wavelength, its crossing of the image surface's
/// local z axis measured from the image surface's vertex along z, positive in the direction the
/// light travels; zone 0 is the limit, the paraxial marginal ray of that wavelength. The afocal
/// form (diopters) is not built.
/// </summary>
public sealed class LongitudinalAberrationAnalysis : OpticsAnalysis<LongitudinalAberrationSettings>
{
    public const string SavedId = "LongitudinalAberration";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public LongitudinalAberrationAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Longitudinal aberration";
    public override string Category => "Aberrations";

    /// <summary>The axis crossing of each zone at each row, meters; NaN for a ray that failed or
    /// runs parallel to the axis, with the reason in <paramref name="failures"/>.</summary>
    internal static double[][] Crossings(FieldScanSetup setup, IReadOnlyList<int> rows, double[] zones, List<string> failures,
        CancellationToken cancellation)
    {
        var paraxialLens = setup.Lens.ToParaxialLens();
        var result = new double[rows.Count][];
        for (int w = 0; w < rows.Count; w++)
        {
            int row = rows[w];
            var trace = setup.Trace(row);
            var p = ChromaticCommon.Paraxial(paraxialLens, setup.System, row);
            var values = new double[zones.Length];
            for (int i = 0; i < zones.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (zones[i] == 0)
                {
                    values[i] = FieldScanCommon.ParaxialCrossing(p, p.MarginalRay);
                    if (double.IsNaN(values[i])) failures.Add($"Wavelength {row + 1}: the paraxial marginal ray leaves parallel to the axis (no focus).");
                    continue;
                }
                var ray = trace.Trace(setup.Launcher.Launch(trace, 0, 0, 0, zones[i]));
                if (!ray.IsOk)
                {
                    values[i] = double.NaN;
                    failures.Add($"Wavelength {row + 1}, zone {zones[i]:g6}: {FieldScanCommon.Failure(ray)}; left out of the plot.");
                    continue;
                }
                values[i] = FieldScanCommon.AxisCrossing(ray.Position, ray.Direction);
                if (double.IsNaN(values[i])) failures.Add($"Wavelength {row + 1}, zone {zones[i]:g6}: the ray leaves parallel to the axis; left out of the plot.");
            }
            result[w] = values;
        }
        return result;
    }

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, LongitudinalAberrationSettings settings,
        CancellationToken cancellation)
    {
        const string who = "Longitudinal aberration";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return FieldScanCommon.NoSurfaces(Name);
        FieldScanCommon.CheckCount(settings.Zones, 3, 1001, who, "number of pupil zones");
        FieldScanCommon.RefuseAfocal(system, who);
        var rows = FieldScanCommon.Rows(settings.Wavelength, system, who, zeroIsAll: true);
        system = VignettingTools.Clear(system);
        var setup = FieldScanCommon.Prepare(lens, system, notes, cancellation);
        var zones = FieldScanCommon.Even(settings.Zones);
        var failures = new List<string>();
        var crossings = Crossings(setup, rows, zones, failures, cancellation);

        string u = setup.UnitName;
        var curves = new List<AnalysisCurve>();
        for (int w = 0; w < rows.Length; w++)
        {
            var keep = Enumerable.Range(0, zones.Length).Where(i => double.IsFinite(crossings[w][i])).ToArray();
            curves.Add(new AnalysisCurve(FieldScanCommon.WavelengthLabel(system, rows[w]),
                keep.Select(i => setup.Shown(crossings[w][i])).ToArray(), keep.Select(i => zones[i]).ToArray()));
        }
        var table = new List<IReadOnlyList<string>>();
        for (int i = 0; i < zones.Length; i++)
        {
            var cells = new List<string> { FieldScanCommon.N(zones[i], "g6") };
            for (int w = 0; w < rows.Length; w++) cells.Add(FieldScanCommon.N(setup.Shown(crossings[w][i])));
            table.Add(cells);
        }
        var sections = new List<ReportSection>
        {
            new("Axis crossing from the image surface")
            {
                TableHeaders = new[] { "Pupil zone" }.Concat(rows.Select(r => $"{FieldScanCommon.WavelengthLabel(system, r)} ({u})")).ToArray(),
                TableRows = table,
            },
        };
        if (failures.Count > 0) sections.Add(new ReportSection("Rays not plotted") { Lines = failures });
        var assumptions = new List<string>
        {
            "On-axis field; zonal marginal tangential rays (Px = 0, Py = zone) at each wavelength; zone 0 is the paraxial marginal ray of that wavelength.",
            "Distance from the image surface's vertex along its z axis to where the ray crosses that axis, positive in the direction the light travels.",
            "Vignetting factors are ignored (cleared).",
            $"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.",
        };
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[]
        {
            new LinePlot("Longitudinal aberration", $"Axis crossing from the image surface ({u})", "Pupil zone", curves),
        });
    }
}

// ---------------------------------------------------------------------------------------------
// Lateral colour.
// ---------------------------------------------------------------------------------------------

/// <summary>Settings of the <see cref="LateralColorAnalysis"/>.</summary>
public sealed record LateralColorSettings
{
    [DisplayName("Use real rays")]
    [Description("Real chief rays; off uses paraxial chief rays.")]
    public bool UseRealRays { get; init; } = true;

    [DisplayName("All wavelengths")]
    [Description("Every wavelength against the primary; off gives the shortest minus the longest.")]
    public bool AllWavelengths { get; init; }

    [DisplayName("Field points")]
    [Description("Fields from the axis to the largest +y field, evenly spaced; 3 to 1001.")]
    public int FieldPoints { get; init; } = 21;
}

/// <summary>
/// OpticStudio's Lateral Color (OP-37; Ansys OpticStudio 2025 R2 help, "Lateral Color"): "For
/// focal systems, the lateral color is the y-distance between the chief ray intercepts of the two
/// extreme wavelengths"; "Use Real Rays: If checked, real rays are used, otherwise paraxial rays";
/// "All Wavelengths: If checked, then data for all defined wavelengths will be displayed. Each
/// wavelength will be referenced to the primary wavelength." Here the y-distance is
/// y(shortest) − y(longest), or y(λ) − y(primary), on the image surface's local y, fields along
/// +y. The real chief ray of each wavelength is launched as the system says (with ray aiming on it
/// is aimed at the stop centre at that wavelength; off, it goes through the primary wavelength's
/// paraxial entrance pupil centre); the paraxial chief ray of each wavelength goes through that
/// wavelength's own paraxial pupil centre. The afocal form (angles) and the Airy disk option are
/// not built.
/// </summary>
public sealed class LateralColorAnalysis : OpticsAnalysis<LateralColorSettings>
{
    public const string SavedId = "LateralColor";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public LateralColorAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Lateral colour";
    public override string Category => "Aberrations";

    /// <summary>The chief ray's image-surface y at each row and field, meters.</summary>
    internal static double[][] ChiefHeights(FieldScanSetup setup, IReadOnlyList<int> rows, double[] h, bool real, CancellationToken cancellation)
    {
        var paraxialLens = setup.Lens.ToParaxialLens();
        int image = setup.Lens.ImageIndex;
        var result = new double[rows.Count][];
        for (int w = 0; w < rows.Count; w++)
        {
            int row = rows[w];
            var values = new double[h.Length];
            if (real)
            {
                var trace = setup.Trace(row);
                for (int i = 0; i < h.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var ray = trace.Trace(setup.Launcher.Launch(trace, 0, h[i], 0, 0));
                    if (!ray.IsOk)
                        throw new InvalidOperationException($"Lateral colour: the chief ray at Hy = {h[i]:g6}, wavelength {row + 1}, does not reach the image surface ({FieldScanCommon.Failure(ray)}).");
                    values[i] = ray.Position.Y;
                }
            }
            else
            {
                var p = ChromaticCommon.Paraxial(paraxialLens, setup.System, row);
                for (int i = 0; i < h.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var (gx, gy) = setup.Launcher.ParaxialField(0, h[i]);
                    values[i] = p.TraceChiefRay(new FieldPoint { X = gx, Y = gy }).Y.Heights[image];
                }
            }
            result[w] = values;
        }
        return result;
    }

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, LateralColorSettings settings, CancellationToken cancellation)
    {
        const string who = "Lateral colour";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return FieldScanCommon.NoSurfaces(Name);
        FieldScanCommon.CheckCount(settings.FieldPoints, 3, 1001, who, "number of field points");
        FieldScanCommon.RefuseAfocal(system, who);
        var table = system.Wavelengths.Wavelengths;
        if (table.Count < 2)
            return OpticsAnalysisResult.FromSections(Name, new[]
            {
                new ReportSection("One wavelength") { Lines = new[] { "The system has one wavelength, so there is no lateral colour to show; add wavelengths." } }
            }, new[] { "Nothing is traced." });
        system = FieldScanCommon.ScanSystem(VignettingTools.Clear(system), realImageHeightAsParaxial: false, notes);
        var setup = FieldScanCommon.Prepare(lens, system, notes, cancellation);
        if (!(setup.ScanScale(FieldScan.PlusY) > 0))
            throw new ArgumentException($"{who}: every field is on the axis, so there is no field to scan; add an off-axis field.");
        var h = FieldScanCommon.Even(settings.FieldPoints);

        int primary = system.Wavelengths.PrimaryIndex;
        int shortest = Enumerable.Range(0, table.Count).MinBy(i => table[i].Micrometers);
        int longest = Enumerable.Range(0, table.Count).MaxBy(i => table[i].Micrometers);
        var rows = settings.AllWavelengths ? Enumerable.Range(0, table.Count).ToArray() : new[] { shortest, longest };
        var y = ChiefHeights(setup, rows, h, settings.UseRealRays, cancellation);

        var series = new List<(string Name, double[] Values)>();
        if (settings.AllWavelengths)
        {
            for (int w = 0; w < rows.Length; w++)
                if (rows[w] != primary)
                    series.Add(($"{FieldScanCommon.WavelengthLabel(system, rows[w])} − primary", y[w].Zip(y[primary], (a, b) => a - b).ToArray()));
        }
        else series.Add(($"{FieldScanCommon.WavelengthLabel(system, shortest)} − {FieldScanCommon.WavelengthLabel(system, longest)}", y[0].Zip(y[1], (a, b) => a - b).ToArray()));

        string u = setup.UnitName;
        string fieldLabel = setup.FieldLabel(FieldScan.PlusY);
        var fieldShown = h.Select(v => setup.FieldShown(v, FieldScan.PlusY)).ToArray();
        var curves = series.Select(s => new AnalysisCurve(s.Name, s.Values.Select(setup.Shown).ToArray(), fieldShown)).ToArray();
        var rowsText = new List<IReadOnlyList<string>>();
        for (int i = 0; i < h.Length; i++)
        {
            var cells = new List<string> { FieldScanCommon.N(fieldShown[i], "g6") };
            cells.AddRange(series.Select(s => FieldScanCommon.N(setup.Shown(s.Values[i]))));
            rowsText.Add(cells);
        }
        var sections = new[]
        {
            new ReportSection("Lateral colour")
            {
                TableHeaders = new[] { fieldLabel }.Concat(series.Select(s => $"{s.Name} ({u})")).ToArray(),
                TableRows = rowsText,
            },
        };
        var assumptions = new List<string>
        {
            settings.UseRealRays ? "Real chief rays; the y coordinate on the image surface's local frame." : "Paraxial chief rays of each wavelength through that wavelength's paraxial pupil centre.",
            settings.AllWavelengths ? "Each wavelength minus the primary." : "The shortest wavelength minus the longest.",
            "Fields along +y from the axis to the largest radial field; vignetting factors ignored (cleared).",
            $"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.",
        };
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[]
        {
            new LinePlot("Lateral colour", $"Lateral colour ({u})", fieldLabel, curves),
        });
    }
}

// ---------------------------------------------------------------------------------------------
// Chromatic focal shift.
// ---------------------------------------------------------------------------------------------

/// <summary>Settings of the <see cref="ChromaticFocalShiftAnalysis"/>.</summary>
public sealed record ChromaticFocalShiftSettings
{
    [DisplayName("Pupil zone")]
    [Description("0 for paraxial marginal rays; 0 to 1 for real marginal rays at that zone of the entrance pupil.")]
    public double PupilZone { get; init; }

    [DisplayName("Wavelength samples")]
    [Description("Wavelengths from the shortest to the longest defined one, evenly spaced; 3 to 1001.")]
    public int Samples { get; init; } = 51;
}

/// <summary>
/// OpticStudio's Chromatic Focal Shift (OP-37; Ansys OpticStudio 2025 R2 help, "Chromatic Focal
/// Shift"): "the shift in back focal length with respect to the primary wavelength"; "Pupil Zone
/// ... The default value of zero means a paraxial ray will be used. Values between 0 and 1 mean
/// real marginal rays are used"; "The entire plot is always referenced to the primary wavelength
/// paraxial focus"; "The diffraction limited depth of focus listed is given by the formula 4λF²,
/// where F is the working F/# and λ is the primary wavelength". Here the shift at λ is the axis
/// crossing of the marginal ray (paraxial, or real at the zone) minus the paraxial marginal ray's
/// crossing at the primary wavelength, along the image space axis in the direction of travel, at
/// evenly spaced wavelengths from the shortest to the longest in the table (their indices from the
/// same glass models). The working F/# is the paraxial engine's at the primary wavelength.
/// </summary>
public sealed class ChromaticFocalShiftAnalysis : OpticsAnalysis<ChromaticFocalShiftSettings>
{
    public const string SavedId = "ChromaticFocalShift";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public ChromaticFocalShiftAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Chromatic focal shift";
    public override string Category => "Aberrations";

    /// <summary>The shift at each wavelength of <paramref name="micrometers"/> (air µm), meters,
    /// against the primary paraxial focus of <paramref name="system"/>.</summary>
    internal static (double[] Shift, double PrimaryCrossing, ParaxialSystem PrimaryParaxial) Shifts(SequentialLens lens, OpticalSystemData system,
        double[] micrometers, double zone, List<string> notes, CancellationToken cancellation)
    {
        var primary = system.Wavelengths.Primary;
        var set = micrometers.Select(m => new Wavelength { Micrometers = m }).Append(primary with { Weight = Math.Max(primary.Weight, 1) }).ToArray();
        var sampled = system with { Wavelengths = new WavelengthSet { Wavelengths = set, PrimaryIndex = set.Length - 1 } };
        var paraxialLens = lens.ToParaxialLens();
        var reference = ChromaticCommon.Paraxial(paraxialLens, sampled, set.Length - 1);
        double referenceCrossing = FieldScanCommon.ParaxialCrossing(reference, reference.MarginalRay);
        if (!double.IsFinite(referenceCrossing))
            throw new InvalidOperationException("Chromatic focal shift: the paraxial marginal ray at the primary wavelength leaves parallel to the axis, so there is no focus to refer to.");
        FieldScanSetup? setup = zone > 0 ? FieldScanCommon.Prepare(lens, sampled, notes, cancellation) : null;
        var shift = new double[micrometers.Length];
        for (int i = 0; i < micrometers.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            double crossing;
            if (setup is null)
            {
                var p = ChromaticCommon.Paraxial(paraxialLens, sampled, i);
                crossing = FieldScanCommon.ParaxialCrossing(p, p.MarginalRay);
            }
            else
            {
                var trace = setup.Trace(i);
                var ray = trace.Trace(setup.Launcher.Launch(trace, 0, 0, 0, zone));
                if (!ray.IsOk)
                    throw new InvalidOperationException($"Chromatic focal shift: the marginal ray at zone {zone:g6}, {micrometers[i]:g6} µm, does not reach the image surface ({FieldScanCommon.Failure(ray)}).");
                crossing = FieldScanCommon.AxisCrossing(ray.Position, ray.Direction);
            }
            if (!double.IsFinite(crossing))
                throw new InvalidOperationException($"Chromatic focal shift: the marginal ray at {micrometers[i]:g6} µm leaves parallel to the axis, so it has no focus.");
            shift[i] = crossing - referenceCrossing;
        }
        return (shift, referenceCrossing, reference);
    }

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, ChromaticFocalShiftSettings settings, CancellationToken cancellation)
    {
        const string who = "Chromatic focal shift";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return FieldScanCommon.NoSurfaces(Name);
        FieldScanCommon.CheckCount(settings.Samples, 3, 1001, who, "number of wavelength samples");
        if (!(settings.PupilZone >= 0 && settings.PupilZone <= 1))
            throw new ArgumentException($"{who}: the pupil zone must lie in 0–1 (it is {settings.PupilZone}).");
        FieldScanCommon.RefuseAfocal(system, who);
        system = VignettingTools.Clear(system);
        var table = system.Wavelengths.Wavelengths;
        double min = table.Min(w => w.Micrometers), max = table.Max(w => w.Micrometers);
        if (!(max > min))
            return OpticsAnalysisResult.FromSections(Name, new[]
            {
                new ReportSection("One wavelength") { Lines = new[] { "Every wavelength in the table is the same, so there is no range to plot; add wavelengths." } }
            }, new[] { "Nothing is traced." });
        var micrometers = FieldScanCommon.Even(settings.Samples, max - min).Select(d => min + d).ToArray();
        micrometers[^1] = max;
        var (shift, _, primary) = Shifts(lens, system, micrometers, settings.PupilZone, notes, cancellation);
        var defined = Enumerable.Range(0, table.Count).Select(i => table[i].Micrometers).ToArray();
        var (definedShift, _, _) = Shifts(lens, system, defined, settings.PupilZone, notes, cancellation);

        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        double Shown(double m) => OpticsConventions.FromMeters(m, unit);
        double? f = primary.WorkingFNumber;
        double lambda = system.Wavelengths.Primary.Micrometers * 1e-6;
        string depth = f is { } fn ? $"{FieldScanCommon.N(Shown(4 * lambda * fn * fn))} {u}" : "— (no working F/#)";
        var sections = new[]
        {
            new ReportSection("Chromatic focal shift")
            {
                Pairs = new[]
                {
                    ("Pupil zone", settings.PupilZone == 0 ? "paraxial" : FieldScanCommon.N(settings.PupilZone, "g6")),
                    ("Maximum focal shift range", $"{FieldScanCommon.N(Shown(shift.Max() - shift.Min()))} {u}"),
                    ("Diffraction-limited depth of focus 4λF²", depth),
                },
                TableHeaders = new[] { "Wavelength", "µm", $"Shift ({u})" },
                TableRows = Enumerable.Range(0, table.Count).Select(i => (IReadOnlyList<string>)new[]
                {
                    (i + 1).ToString(), FieldScanCommon.N(table[i].Micrometers, "g6"), FieldScanCommon.N(Shown(definedShift[i])),
                }).ToList(),
            },
        };
        var assumptions = new List<string>
        {
            "Shift of the marginal ray's axis crossing against the primary wavelength's paraxial marginal ray, along the image space axis, positive in the direction the light travels.",
            settings.PupilZone == 0 ? "Paraxial marginal rays." : $"Real marginal rays at zone {settings.PupilZone:g6} of the entrance pupil, on-axis field.",
            $"Wavelengths sampled evenly from {min:g6} to {max:g6} µm; the depth of focus uses the paraxial working F/# and the primary wavelength in air.",
            "Vignetting factors are ignored (cleared).",
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[]
        {
            new LinePlot("Chromatic focal shift", $"Focal shift ({u})", "Wavelength (µm)",
                new[] { new AnalysisCurve("Shift", shift.Select(Shown).ToArray(), micrometers) }),
        });
    }
}
