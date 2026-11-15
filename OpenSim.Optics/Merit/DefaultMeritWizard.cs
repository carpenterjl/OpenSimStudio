using System.Globalization;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

/// <summary>What the default merit function measures (OpticStudio 2025 R2 help, Optimization
/// Wizard, "Optimization Function Criteria").</summary>
public enum MeritWizardCriterion
{
    /// <summary>The OPD in waves (OPDX, OPDM or OPDC by the reference).</summary>
    Wavefront = 0,
    /// <summary>The radial transverse aberration (TRAC, TRAR).</summary>
    SpotRadius = 1,
    /// <summary>The x transverse aberration (TRCX, TRAX).</summary>
    SpotX = 2,
    /// <summary>The y transverse aberration (TRCY, TRAY).</summary>
    SpotY = 3,
    /// <summary>x and y as separate rows, weighted by the relative X and Y weights; radial when both are 0.</summary>
    SpotXY = 4,
    /// <summary>The angular aberration in x and y (ANCX/ANCY, ANAX/ANAY), weighted by the relative X
    /// and Y weights; radial (ANAC, ANAR) when both are 0. For afocal systems.</summary>
    Angular = 5,
    /// <summary>"Optimizes the MTF of the system using the Moore-Elliott Contrast method" (help):
    /// the pupil phase differences across the shift of the Spatial Frequency (MECT, MECS;
    /// <see cref="MtfOperands"/>), weighted by the relative T and S weights.</summary>
    Contrast = 6,
}

/// <summary>What the aberrations are measured from (help, "Optimization Function Reference Points").</summary>
public enum MeritWizardReference
{
    /// <summary>The weighted centroid of the field's rays; for the wavefront, the mean and the tilt removed.</summary>
    Centroid = 0,
    /// <summary>The primary wavelength's chief ray; for the wavefront, the mean removed.</summary>
    ChiefRay = 1,
    /// <summary>The OPD against the chief ray as it is (wavefront only).</summary>
    Unreferenced = 2,
}

/// <summary>Which configurations the rows cover (help, "Other Settings", "Configuration").</summary>
public enum MeritWizardConfigurations
{
    Current = 0,
    All = 1,
}

/// <summary>The Optimization Wizard's settings (help pages under "Optimization Wizard"). Lengths
/// in lens units.</summary>
public sealed record DefaultMeritOptions
{
    public MeritWizardCriterion Criterion { get; init; } = MeritWizardCriterion.SpotRadius;
    public MeritWizardReference Reference { get; init; } = MeritWizardReference.Centroid;
    public PupilIntegration Integration { get; init; } = PupilIntegration.GaussianQuadrature;

    /// <summary>Gaussian quadrature rings, 1 to 20. The help gives no default; 3 is this
    /// program's, the help's "If an optical design will be limited by 5th order ray aberrations n
    /// should be at least 3".</summary>
    public int Rings { get; init; } = 3;

    /// <summary>Gaussian quadrature arms: "By default six ... This number may be changed to eight,
    /// ten, or twelve" (help, "Gaussian Quadrature").</summary>
    public int Arms { get; init; } = 6;

    /// <summary>Rectangular array rays across (help: "4x4 ... 6x6 etc."); 6 is this program's default.</summary>
    public int Grid { get; init; } = 6;

    /// <summary>Rectangular array: leave out the rows whose ray fails when the rows are generated
    /// (help, "Rectangular Array", "Delete Vignetted").</summary>
    public bool DeleteVignetted { get; init; } = true;

    /// <summary>Exploit left-right and rotational symmetry; null is automatic: on when the lens
    /// is rotationally symmetric (<see cref="MeritEvaluator.IsRotationallySymmetric"/>), as the
    /// help's default ("In systems with coordinate breaks or non-rotationally symmetric systems,
    /// the default is unselected").</summary>
    public bool? AssumeAxialSymmetry { get; init; }

    /// <summary>One reference per wavelength instead of one per field (help, "Optimization
    /// Function Distortion and Color").</summary>
    public bool IgnoreLateralColor { get; init; }

    /// <summary>The row the generated rows start at (1 = the first); 0 for the end; null for the
    /// row after the first DMFS row, or the end when there is none (help, "Other Settings",
    /// "Start At"; DMFS).</summary>
    public int? StartAt { get; init; }

    /// <summary>Scales every weight of the generated rows (help, "Overall Weight").</summary>
    public double OverallWeight { get; init; } = 1;

    /// <summary>The help's "Relative X Weight" and "Relative Y Weight" (spot X+Y and angular).</summary>
    public double RelativeXWeight { get; init; } = 1;
    public double RelativeYWeight { get; init; } = 1;

    /// <summary>Contrast: "The relative S weight is the additional weighting to be placed on the
    /// sagittal component of the phase differences" and the T weight on the tangential (help,
    /// "Other Settings"); 1 is this program's default, as for X and Y.</summary>
    public double RelativeSWeight { get; init; } = 1;
    public double RelativeTWeight { get; init; } = 1;

    /// <summary>Contrast: "The spatial frequency at which to optimize" (help, "Other Settings"),
    /// cycles/mm. The help gives no default; 0 is unset, and Contrast refuses it.</summary>
    public double SpatialFrequency { get; init; }

    public MeritWizardConfigurations Configurations { get; init; } = MeritWizardConfigurations.Current;

    /// <summary>Glass boundaries: MNCG, MXCG and MNEG (help, "Boundary Values"). The help gives no
    /// default values; these are this program's, for a lens in millimetres.</summary>
    public bool GlassBoundaries { get; init; }
    public double GlassMinimum { get; init; } = 1;
    public double GlassMaximum { get; init; } = 10;
    public double GlassEdge { get; init; } = 1;

    /// <summary>Air boundaries: MNCA, MXCA and MNEA; defaults as for the glass.</summary>
    public bool AirBoundaries { get; init; }
    public double AirMinimum { get; init; } = 0.1;
    public double AirMaximum { get; init; } = 1000;
    public double AirEdge { get; init; } = 0.1;
}

/// <summary>What the wizard generated.</summary>
public sealed record DefaultMeritResult
{
    /// <summary>The rows, in order, to insert at the start row.</summary>
    public required IReadOnlyList<MeritOperandRow> Rows { get; init; }

    /// <summary>The integration the rows use: Gaussian quadrature unless it was asked for the
    /// rectangular array or the pupil is vignetted.</summary>
    public required PupilIntegration Integration { get; init; }

    /// <summary>What was generated, one line.</summary>
    public required string Summary { get; init; }

    /// <summary>Warnings and what the wizard decided (switching to the rectangular array, rows
    /// left out), one line each.</summary>
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// The Optimization Wizard (OP-56; docs/Optics-Conventions.md §40): writes the default merit
/// function's rows. For every configuration chosen, every field and every wavelength of non-zero
/// weight, and every pupil point of the integration rule, one row (two for x and y) of the
/// criterion's operand (<see cref="RayAberrationOperands"/>), weighted by
/// overall weight × field weight × wavelength weight × the point's pupil weight (× the relative X or
/// Y weight), with a target of zero. So, for one configuration,
/// <code>MF² = Σ_f w_f Σ_λ w_λ Σ_i w_i e²(f, λ, i) / Σ_f w_f Σ_λ w_λ</code>
/// (the pupil weights sum to 1), the field- and wavelength-weighted mean of the squared RMS: for
/// one field the merit function is the RMS of OP-40's RMS analyses (exactly for spot radius, x, y
/// and wavefront; for x + y with equal relative weights it is the RMS radius over √2). See the
/// conventions section for the polychromatic wavefront.
/// <para>
/// <b>Vignetting.</b> Gaussian quadrature "assumes the pupil is a circle ... if there are surface
/// apertures in the optical system that vignette enough rays to alter the effective shape of the
/// pupil significantly, GQ should not be used" (help, "Gaussian Quadrature"). Before writing
/// Gaussian rows the wizard traces every ray of the rule at every field and wavelength; if any
/// fails it writes the rectangular array instead and says so in <see cref="DefaultMeritResult.Notes"/>.
/// </para>
/// <para>
/// <b>Symmetry.</b> With axial symmetry assumed, a field on the y axis (Hx = 0) keeps only the
/// pupil points with Px ≥ 0, those with Px &gt; 0 at twice their weight (left-right symmetry), and
/// the on-axis field keeps one point per ring at the whole ring's weight for the radial criteria
/// (rotational symmetry); the help: "the number of rays traced per ring is equal to half the number
/// of arms". Arms on the y axis are their own mirror image and keep their weight. The reference
/// (centroid, mean, tilt) is still computed from the whole rule (<see cref="RayAberrationOperands"/>).
/// Fields off the y axis keep every point.
/// </para>
/// </summary>
public static class DefaultMeritWizard
{
    /// <summary>The arms the help offers.</summary>
    public static IReadOnlyList<int> AllowedArms { get; } = new[] { 6, 8, 10, 12 };

    /// <summary>The largest rectangular array the wizard writes (this program's limit: the rows
    /// grow as the square).</summary>
    public const int MaximumGrid = 100;

    /// <summary>The problems of <paramref name="o"/>, one line each; empty when it can be used.</summary>
    public static IReadOnlyList<string> Validate(DefaultMeritOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var p = new List<string>();
        if (!Enum.IsDefined(o.Criterion)) p.Add($"Criterion {(int)o.Criterion} is not one this version knows.");
        if (!Enum.IsDefined(o.Reference)) p.Add($"Reference {(int)o.Reference} is not one this version knows.");
        if (!Enum.IsDefined(o.Integration)) p.Add($"Pupil integration {(int)o.Integration} is not one this version knows.");
        if (!Enum.IsDefined(o.Configurations)) p.Add($"Configurations {(int)o.Configurations} is not a choice this version knows.");
        if (o.Reference == MeritWizardReference.Unreferenced && o.Criterion != MeritWizardCriterion.Wavefront)
            p.Add("Unreferenced is for the wavefront criterion only (help, \"Optimization Function Reference Points\": \"This option is only available if the data is wavefront\").");
        if (o.Rings < 1 || o.Rings > PupilQuadrature.MaximumRings) p.Add($"Rings must be 1 to {PupilQuadrature.MaximumRings} (it is {o.Rings}).");
        if (!AllowedArms.Contains(o.Arms)) p.Add($"Arms must be 6, 8, 10 or 12, as the help offers (it is {o.Arms}).");
        if (o.Grid < 2 || o.Grid > MaximumGrid) p.Add($"The rectangular array must be 2 to {MaximumGrid} rays across (it is {o.Grid}).");
        if (o.StartAt is < 0) p.Add($"Start At must be 0 (the end) or a row number (it is {o.StartAt}).");
        if (!(o.OverallWeight > 0) || !double.IsFinite(o.OverallWeight)) p.Add($"The overall weight must be positive and finite (it is {F(o.OverallWeight)}).");
        if (!(o.RelativeXWeight >= 0) || !double.IsFinite(o.RelativeXWeight)) p.Add($"The relative X weight must be zero or positive (it is {F(o.RelativeXWeight)}).");
        if (!(o.RelativeYWeight >= 0) || !double.IsFinite(o.RelativeYWeight)) p.Add($"The relative Y weight must be zero or positive (it is {F(o.RelativeYWeight)}).");
        if (o.Criterion == MeritWizardCriterion.Contrast)
        {
            if (!(o.SpatialFrequency > 0) || !double.IsFinite(o.SpatialFrequency)) p.Add($"Contrast needs a spatial frequency above zero, cycles/mm (it is {F(o.SpatialFrequency)}).");
            if (!(o.RelativeSWeight >= 0) || !double.IsFinite(o.RelativeSWeight)) p.Add($"The relative S weight must be zero or positive (it is {F(o.RelativeSWeight)}).");
            if (!(o.RelativeTWeight >= 0) || !double.IsFinite(o.RelativeTWeight)) p.Add($"The relative T weight must be zero or positive (it is {F(o.RelativeTWeight)}).");
            if (o.RelativeSWeight == 0 && o.RelativeTWeight == 0) p.Add("Contrast needs a relative S or T weight above zero.");
        }
        void Bounds(bool on, string what, double min, double max, double edge)
        {
            if (!on) return;
            if (!double.IsFinite(min) || !double.IsFinite(max) || !double.IsFinite(edge)) p.Add($"The {what} boundaries must be finite.");
            else if (min > max) p.Add($"The {what} minimum thickness ({F(min)}) is above the maximum ({F(max)}).");
        }
        Bounds(o.GlassBoundaries, "glass", o.GlassMinimum, o.GlassMaximum, o.GlassEdge);
        Bounds(o.AirBoundaries, "air", o.AirMinimum, o.AirMaximum, o.AirEdge);
        return p;
    }

    /// <summary>
    /// The default merit function's rows for <paramref name="configurations"/>
    /// (<paramref name="current"/> is the current configuration, 1-based). Throws
    /// <see cref="ArgumentException"/> listing the problems of the options, or naming a
    /// configuration that cannot be built.
    /// </summary>
    public static DefaultMeritResult Generate(IMeritConfigurations configurations, int current, DefaultMeritOptions options,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        ArgumentNullException.ThrowIfNull(options);
        var problems = Validate(options).ToList();
        if (current < 1 || current > configurations.Count)
            problems.Add($"The current configuration {current} is not one of the {configurations.Count}.");
        if (problems.Count > 0)
            throw new ArgumentException("Optimization Wizard:" + Environment.NewLine + string.Join(Environment.NewLine, problems));

        var chosen = options.Configurations == MeritWizardConfigurations.All && configurations.Count > 1
            ? Enumerable.Range(1, configurations.Count).ToArray() : new[] { current };
        var built = new List<(int Number, SequentialLens Lens, OpticalSystemData System, bool Symmetric)>();
        foreach (int k in chosen)
        {
            try
            {
                var lens = configurations.LensFor(k);
                var system = configurations.SystemFor(k);
                built.Add((k, lens, system, options.AssumeAxialSymmetry ?? MeritEvaluator.IsRotationallySymmetric(lens)));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                throw new ArgumentException($"Optimization Wizard: configuration {k} cannot be built: {e.Message}", e);
            }
        }

        var notes = new List<string>();
        var integration = options.Integration;
        if (integration == PupilIntegration.GaussianQuadrature)
        {
            var rule = PupilQuadrature.Gaussian(options.Rings, options.Arms);
            var vignetted = new List<string>();
            foreach (var c in built)
                foreach (var (field, hx, hy, _) in Fields(c.System))
                    foreach (var (wave, _) in Waves(c.System))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var tracer = RayAberrationOperands.For(c.Lens, c.System);
                        int failed = rule.Points.Count(pt => Fails(tracer, c.Number, wave, hx, hy, pt.Px, pt.Py));
                        if (failed > 0)
                            vignetted.Add($"{(built.Count > 1 ? $"configuration {c.Number}, " : "")}field {field + 1}, wavelength {wave}: {failed} of {rule.Points.Count}");
                    }
            if (vignetted.Count > 0)
            {
                integration = PupilIntegration.RectangularArray;
                notes.Add($"The pupil is vignetted (rays of the Gaussian quadrature fail: {string.Join("; ", vignetted)}). Gaussian quadrature assumes an unvignetted circular pupil (help, \"Gaussian Quadrature\"), so the rows use the rectangular array, {options.Grid} × {options.Grid}, instead.");
            }
        }

        if (options.IgnoreLateralColor && options.Reference != MeritWizardReference.Centroid)
            notes.Add("Ignore Lateral Color has no effect here: the chief-ray operands measure from the primary wavelength's chief ray by their definition (help, ANAR), and OPDC from its own wavelength's chief ray.");
        foreach (var c in built)
        {
            if (c.System.Aperture.AfocalImageSpace && options.Criterion is not (MeritWizardCriterion.Angular or MeritWizardCriterion.Wavefront))
                notes.Add($"{(built.Count > 1 ? $"Configuration {c.Number}: i" : "I")}mage space is afocal; the help intends the angular criterion for afocal systems (\"Afocal Image Space\").");
            if (options.AssumeAxialSymmetry == true && !MeritEvaluator.IsRotationallySymmetric(c.Lens))
                notes.Add($"{(built.Count > 1 ? $"Configuration {c.Number}: a" : "A")}xial symmetry is assumed on a lens this program does not find rotationally symmetric; the halved rows are exact only if the aberrations are symmetric.");
        }

        var pupilRule = integration == PupilIntegration.GaussianQuadrature
            ? PupilQuadrature.Gaussian(options.Rings, options.Arms) : PupilQuadrature.Rectangular(options.Grid);
        var rows = new List<MeritOperandRow>();
        string summary = Summary(options, pupilRule, built.Count);
        rows.Add(MeritOperandRow.Of("BLNK") with { Comment = "Default merit function: " + summary });

        if (options.GlassBoundaries || options.AirBoundaries)
        {
            int last = built[0].Lens.ImageIndex - 1;
            rows.Add(MeritOperandRow.Of("BLNK") with { Comment = "Default individual air and glass thickness boundary constraints." });
            double w = options.OverallWeight;
            if (options.GlassBoundaries)
            {
                rows.Add(MeritOperandRow.Of("MNCG", 1, last, options.GlassMinimum, w));
                rows.Add(MeritOperandRow.Of("MXCG", 1, last, options.GlassMaximum, w));
                rows.Add(MeritOperandRow.Of("MNEG", 1, last, options.GlassEdge, w, 0, 0));
            }
            if (options.AirBoundaries)
            {
                rows.Add(MeritOperandRow.Of("MNCA", 1, last, options.AirMinimum, w));
                rows.Add(MeritOperandRow.Of("MXCA", 1, last, options.AirMaximum, w));
                rows.Add(MeritOperandRow.Of("MNEA", 1, last, options.AirEdge, w, 0, 0));
            }
        }

        int omitted = 0, unpaired = 0;
        bool contrast = options.Criterion == MeritWizardCriterion.Contrast;
        foreach (var c in built)
        {
            if (configurations.Count > 1)
                rows.Add(new MeritOperandRow { Code = "CONF", Int1 = c.Number, Comment = $"Configuration {c.Number}" });
            var tracer = RayAberrationOperands.For(c.Lens, c.System);
            foreach (var (field, hx, hy, fieldWeight) in Fields(c.System))
            {
                cancellation.ThrowIfCancellationRequested();
                rows.Add(MeritOperandRow.Of("BLNK") with { Comment = $"Operands for field {field + 1} (Hx {F(hx)}, Hy {F(hy)})." });
                var points = Points(pupilRule, hx, hy, c.Symmetric && !contrast, Radial(options));
                foreach (var (wave, waveWeight) in Waves(c.System))
                {
                    double shift = contrast ? Shift(c.Lens, c.System, c.Number, wave, options.SpatialFrequency) : 0;
                    int written = 0;
                    foreach (var (px, py, pupilWeight) in points)
                    {
                        if (integration == PupilIntegration.RectangularArray && options.DeleteVignetted
                            && Fails(tracer, c.Number, wave, hx, hy, px, py))
                        {
                            omitted++;
                            continue;
                        }
                        double weight = options.OverallWeight * fieldWeight * waveWeight * pupilWeight;
                        foreach (var (code, factor) in Codes(options))
                        {
                            if (contrast)
                            {
                                // The partner point of the phase difference (MtfOperands): inside the pupil, and its ray passes.
                                double qx = code == "MECS" ? px - shift : px, qy = code == "MECT" ? py - shift : py;
                                if (qx * qx + qy * qy > 1 || Fails(tracer, c.Number, wave, hx, hy, qx, qy)) { unpaired++; continue; }
                                rows.Add(new MeritOperandRow { Code = code, Int2 = wave, Data = new[] { hx, hy, px, py, options.SpatialFrequency }, Weight = weight * factor });
                                written++;
                                continue;
                            }
                            rows.Add(Row(code, wave, hx, hy, px, py, weight * factor, pupilRule, options.IgnoreLateralColor));
                        }
                    }
                    if (contrast && written == 0)
                        throw new ArgumentException($"Optimization Wizard: at {F(options.SpatialFrequency)} cycles/mm the pupil shift is {F(shift)} of the pupil radius ({(built.Count > 1 ? $"configuration {c.Number}, " : "")}field {field + 1}, wavelength {wave}), so no pupil point has its partner inside the pupil: the frequency is at or beyond the cutoff.");
                }
            }
        }
        if (unpaired > 0)
            notes.Add($"Contrast: {unpaired} phase difference{(unpaired == 1 ? "" : "s")} left out because the shifted pupil point lies outside the pupil or its ray fails; the remaining rows keep their quadrature weights.");
        if (omitted > 0)
            notes.Add($"Delete Vignetted: {omitted} pupil point{(omitted == 1 ? "" : "s")} whose ray fails {(omitted == 1 ? "was" : "were")} left out (help, \"Rectangular Array\"); regenerate the rows if the vignetting changes.");
        return new DefaultMeritResult { Rows = rows, Integration = integration, Summary = summary, Notes = notes };
    }

    /// <summary>The start row the help's Start At defaults to: the row after the first DMFS row
    /// ("The row number after this operand will appear as the default "Start At" row", help, DMFS),
    /// or 0 (the end) when there is none.</summary>
    public static int DefaultStartAt(MeritFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        for (int n = 1; n <= function.Rows.Count; n++)
            if (string.Equals(function.Row(n).Code, "DMFS", StringComparison.OrdinalIgnoreCase)) return n + 1;
        return 0;
    }

    /// <summary>
    /// <paramref name="existing"/> with <paramref name="generated"/> inserted so its first row is
    /// row <paramref name="startAt"/> (0 or past the end: appended), "adds the generated merit
    /// function to the existing merit function at the specified starting line" (help, "Button
    /// Functions"). Rows that refer to later rows by number (GOTO, SKIS, SKIN and the math
    /// operands' Op# columns) are renumbered, so they still name the same rows. When the generated
    /// rows change the configuration (CONF) and existing rows follow them, a CONF back to the
    /// configuration in effect at the start row is added after them.
    /// </summary>
    public static MeritFunction Insert(MeritFunction existing, IReadOnlyList<MeritOperandRow> generated, int startAt, MeritOperandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(registry);
        int count = existing.Rows.Count;
        int at = startAt < 1 || startAt > count ? count + 1 : startAt;     // the first generated row's number
        var block = generated.ToList();
        if (at <= count && block.Any(r => string.Equals(r.Code, "CONF", StringComparison.OrdinalIgnoreCase)))
        {
            int before = 1;
            for (int n = 1; n < at; n++)
                if (string.Equals(existing.Row(n).Code, "CONF", StringComparison.OrdinalIgnoreCase)) before = existing.Row(n).Int1;
            block.Add(new MeritOperandRow { Code = "CONF", Int1 = before, Comment = "Back to the configuration before the default merit function." });
        }
        int shift = block.Count;
        int Moved(int number) => number >= at ? number + shift : number;
        var rows = new List<MeritOperandRow>(count + shift);
        for (int n = 1; n <= count; n++)
        {
            var row = existing.Row(n);
            var d = registry.Find(row.Code);
            if (d is not null && (d.Control is MeritControl.GoTo or MeritControl.SkipIfSymmetric or MeritControl.SkipIfNotSymmetric || d.References is not null))
            {
                bool int1 = d.Control != MeritControl.None || IsRowColumn(d.Int1);
                bool int2 = d.Control == MeritControl.None && IsRowColumn(d.Int2);
                row = row with { Int1 = int1 ? Moved(row.Int1) : row.Int1, Int2 = int2 ? Moved(row.Int2) : row.Int2 };
            }
            if (n == at) rows.AddRange(block);
            rows.Add(row);
        }
        if (at == count + 1) rows.AddRange(block);
        return new MeritFunction(rows);
    }

    /// <summary>The Moore–Elliott pupil shift (<see cref="MtfOperands.ContrastShift"/>); an error names the configuration.</summary>
    private static double Shift(SequentialLens lens, OpticalSystemData system, int configuration, int wave, double frequency)
    {
        try { return MtfOperands.ContrastShift(lens, system, wave, frequency); }
        catch (MeritOperandException e)
        {
            throw new ArgumentException($"Optimization Wizard: configuration {configuration}, wavelength {wave}: {e.Message}", e);
        }
    }

    /// <summary>Whether the ray fails; a lens that cannot be traced at all is an error naming the configuration.</summary>
    private static bool Fails(RayAberrationOperands.Prepared tracer, int configuration, int wave, double hx, double hy, double px, double py)
    {
        try { return tracer.RayFailure(wave, hx, hy, px, py) is not null; }
        catch (MeritOperandException e)
        {
            throw new ArgumentException($"Optimization Wizard: configuration {configuration}: {e.Message}", e);
        }
    }

    private static bool IsRowColumn(string? label) => label is not null && label.StartsWith("Op#", StringComparison.Ordinal);

    // ------------------------------------------------------------------ rows

    /// <summary>The fields of non-zero weight: 0-based row, (Hx, Hy), weight.</summary>
    private static IEnumerable<(int Field, double Hx, double Hy, double Weight)> Fields(OpticalSystemData system)
    {
        var scale = system.Fields.Scale;
        for (int i = 0; i < system.Fields.Points.Count; i++)
        {
            var p = system.Fields.Points[i];
            if (!(p.Weight > 0)) continue;
            var (hx, hy) = OpticsConventions.NormalizeField((p.X, p.Y), scale);
            yield return (i, hx, hy, p.Weight);
        }
    }

    /// <summary>The wavelengths of non-zero weight: number (1-based) and weight.</summary>
    private static IEnumerable<(int Wave, double Weight)> Waves(OpticalSystemData system) =>
        system.Wavelengths.Wavelengths.Select((w, i) => (Wave: i + 1, w.Weight)).Where(w => w.Weight > 0);

    /// <summary>True when every row is a radial value, which rotational symmetry makes the same
    /// round each ring.</summary>
    private static bool Radial(DefaultMeritOptions o) => o.Criterion switch
    {
        MeritWizardCriterion.Wavefront or MeritWizardCriterion.SpotRadius => true,
        MeritWizardCriterion.SpotXY or MeritWizardCriterion.Angular => o.RelativeXWeight == 0 && o.RelativeYWeight == 0,
        _ => false,
    };

    /// <summary>The pupil points a field's rows use, with their weights (see the class remarks).</summary>
    internal static IReadOnlyList<(double Px, double Py, double Weight)> Points(PupilQuadratureRule rule, double hx, double hy, bool symmetric, bool radial)
    {
        const double onAxis = 1e-12;
        var points = new List<(double, double, double)>();
        if (!symmetric || hx != 0)
        {
            for (int i = 0; i < rule.Points.Count; i++) points.Add((rule.Points[i].Px, rule.Points[i].Py, rule.Weights[i]));
            return points;
        }
        if (hy == 0 && radial && rule.Method == PupilIntegration.GaussianQuadrature)
        {
            for (int ring = 0; ring < rule.Density; ring++)
            {
                double w = 0;
                for (int j = 0; j < rule.Arms; j++) w += rule.Weights[ring * rule.Arms + j];
                var p = rule.Points[ring * rule.Arms];   // the first arm, on +x
                points.Add((p.Px, p.Py, w));
            }
            return points;
        }
        for (int i = 0; i < rule.Points.Count; i++)
        {
            var (px, py) = rule.Points[i];
            if (px > onAxis) points.Add((px, py, 2 * rule.Weights[i]));
            else if (px >= -onAxis) points.Add((px, py, rule.Weights[i]));
        }
        return points;
    }

    /// <summary>The operand codes of one pupil point and their weight factors.</summary>
    private static IEnumerable<(string Code, double Factor)> Codes(DefaultMeritOptions o)
    {
        bool centroid = o.Reference == MeritWizardReference.Centroid;
        switch (o.Criterion)
        {
            case MeritWizardCriterion.Wavefront:
                yield return (o.Reference switch { MeritWizardReference.Centroid => "OPDX", MeritWizardReference.ChiefRay => "OPDM", _ => "OPDC" }, 1);
                break;
            case MeritWizardCriterion.SpotRadius:
                yield return (centroid ? "TRAC" : "TRAR", 1);
                break;
            case MeritWizardCriterion.SpotX:
                yield return (centroid ? "TRCX" : "TRAX", 1);
                break;
            case MeritWizardCriterion.SpotY:
                yield return (centroid ? "TRCY" : "TRAY", 1);
                break;
            case MeritWizardCriterion.SpotXY:
            case MeritWizardCriterion.Angular:
                bool spot = o.Criterion == MeritWizardCriterion.SpotXY;
                if (o.RelativeXWeight == 0 && o.RelativeYWeight == 0)
                {
                    yield return (spot ? (centroid ? "TRAC" : "TRAR") : (centroid ? "ANAC" : "ANAR"), 1);
                    break;
                }
                if (o.RelativeXWeight > 0) yield return (spot ? (centroid ? "TRCX" : "TRAX") : (centroid ? "ANCX" : "ANAX"), o.RelativeXWeight);
                if (o.RelativeYWeight > 0) yield return (spot ? (centroid ? "TRCY" : "TRAY") : (centroid ? "ANCY" : "ANAY"), o.RelativeYWeight);
                break;
            case MeritWizardCriterion.Contrast:
                if (o.RelativeTWeight > 0) yield return ("MECT", o.RelativeTWeight);
                if (o.RelativeSWeight > 0) yield return ("MECS", o.RelativeSWeight);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(o), o.Criterion, "Unknown criterion.");
        }
    }

    /// <summary>The grouped (centroid) codes, which carry their pupil rule in Int1, Data5 and Data6.</summary>
    private static readonly HashSet<string> Grouped = new(StringComparer.Ordinal) { "TRAC", "TRCX", "TRCY", "ANAC", "ANCX", "ANCY", "OPDM", "OPDX" };

    private static MeritOperandRow Row(string code, int wave, double hx, double hy, double px, double py, double weight,
        PupilQuadratureRule rule, bool perWave)
    {
        if (!Grouped.Contains(code))
            return new MeritOperandRow { Code = code, Int2 = wave, Data = new[] { hx, hy, px, py }, Weight = weight };
        bool gaussian = rule.Method == PupilIntegration.GaussianQuadrature;
        return new MeritOperandRow
        {
            Code = code, Int1 = rule.Density, Int2 = wave, Weight = weight,
            Data = new[] { hx, hy, px, py, gaussian ? rule.Arms : 0, perWave ? 1 : 0 },
        };
    }

    private static string Summary(DefaultMeritOptions o, PupilQuadratureRule rule, int configurations)
    {
        string criterion = o.Criterion switch
        {
            MeritWizardCriterion.Wavefront => "RMS wavefront",
            MeritWizardCriterion.SpotRadius => "RMS spot radius",
            MeritWizardCriterion.SpotX => "RMS spot x",
            MeritWizardCriterion.SpotY => "RMS spot y",
            MeritWizardCriterion.SpotXY => "RMS spot x + y",
            MeritWizardCriterion.Contrast => $"Moore–Elliott contrast at {F(o.SpatialFrequency)} cycles/mm",
            _ => "RMS angular",
        };
        string reference = o.Criterion == MeritWizardCriterion.Contrast ? "phase differences of the chief-ray-referenced OPD" : o.Reference switch
        {
            MeritWizardReference.Centroid => "centroid",
            MeritWizardReference.ChiefRay => "chief ray",
            _ => "unreferenced",
        };
        string sampling = rule.Method == PupilIntegration.GaussianQuadrature
            ? $"Gaussian quadrature {rule.Density} rings × {rule.Arms} arms (exact for degree ≤ {PupilQuadrature.ExactDegree(rule.Density, rule.Arms)})"
            : $"rectangular array {rule.Density} × {rule.Density}";
        return $"{criterion}, {reference}, {sampling}{(o.IgnoreLateralColor ? ", lateral colour ignored" : "")}{(configurations > 1 ? $", {configurations} configurations" : "")}.";
    }

    private static string F(double x) => x.ToString("G6", CultureInfo.InvariantCulture);
}
