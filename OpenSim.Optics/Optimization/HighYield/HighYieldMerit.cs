using System.Globalization;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization.HighYield;

// OP-62: the High-Yield rows of the merit function (docs/Optics-Conventions.md §50).

/// <summary>What <see cref="HighYieldMerit.Generate"/> wrote.</summary>
public sealed record HighYieldRows
{
    /// <summary>The rows: a BLNK header (<see cref="HighYieldMerit.HeaderPrefix"/>), CONF rows
    /// when the lens has more than one configuration, and the HYLD rows.</summary>
    public required IReadOnlyList<MeritOperandRow> Rows { get; init; }

    /// <summary>The pupil integration of the rows: the default merit function's (Gaussian
    /// quadrature unless asked for the rectangular array or the pupil is vignetted).</summary>
    public required PupilIntegration Integration { get; init; }

    /// <summary>The number of HYLD rows.</summary>
    public required int OperandCount { get; init; }

    /// <summary>The surfaces given rows, by configuration (key 1 = configuration 1).</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<int>> Surfaces { get; init; }

    /// <summary>One line: what was written.</summary>
    public required string Summary { get; init; }

    /// <summary>What was left out and why (mirrors, failed rays), one line each.</summary>
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>The High-Yield term of one surface in an evaluated merit function.</summary>
/// <param name="Configuration">1-based.</param>
/// <param name="Surface">The surface.</param>
/// <param name="Rows">The HYLD rows at that surface that were evaluated.</param>
/// <param name="MeanTau">Σ wτ / Σ w over those rows: the weighted mean of τ over the field,
/// wavelengths and pupil (the weights sum the default merit function's ray weights).</param>
/// <param name="MaxTau">The largest τ of those rows.</param>
/// <param name="Contribution">The rows' share of the merit function, percent.</param>
public sealed record HighYieldSurfaceTerm(int Configuration, int Surface, int Rows, double MeanTau, double MaxTau, double Contribution);

/// <summary>The High-Yield rows of an evaluated merit function, surface by surface.</summary>
public sealed record HighYieldReport
{
    public required IReadOnlyList<HighYieldSurfaceTerm> Surfaces { get; init; }

    /// <summary>The HYLD rows' share of the merit function, percent.</summary>
    public required double Contribution { get; init; }

    /// <summary>HYLD rows that failed, one line each (the evaluator's messages).</summary>
    public required IReadOnlyList<string> Failures { get; init; }
}

/// <summary>
/// The High-Yield optimization of OpticStudio ("Improve Manufacturing Yield" in the Optimization
/// Wizard; OpticStudio 2025 R2 help, "Optimization Goal"): HYLD rows added to the merit function
/// so the optimizer trades nominal performance against the High-Yield penalty τ of
/// <see cref="HighYieldPenalty"/>, a proxy for tolerance sensitivity.
/// <para>
/// <b>Which rows.</b> KA-01837: "The HYLD operands are computed with the same rays as are used
/// for the image quality criterion ... utilize the same pupil integration techniques (Gaussian
/// Quadrature, or Rectangular Array) as the other criteria ... HYLD operands thus optimize the
/// penalty term at every surface, and over the entire field of view and pupil." So for every
/// configuration, field and wavelength of non-zero weight and every pupil point of the default
/// merit function (<see cref="DefaultMeritWizard"/>, the options saved with the lens: the same
/// rule, the same symmetry reduction, the same switch to the rectangular array when the pupil is
/// vignetted), one HYLD row per refracting surface (<see cref="HighYieldPenalty.RefractingSurfaces"/>),
/// target 0.
/// </para>
/// <para>
/// <b>Weights.</b> Each row's weight is W × overall weight × field weight × wavelength weight ×
/// the point's pupil weight, W the High-Yield weight ("Weight sets the weight of the overall
/// contribution from the HYLD operands with respect to the rest of the merit function", help,
/// "Optimization Goal"). This is this program's reading (docs/Optics-Conventions.md §10 item 46):
/// the help does not give the formula; KA-01837's Merit Function Editor screenshot shows HYLD
/// rows whose weights are the ray weights of the criterion rows above them (its first HYLD ring
/// and last TRCY ring carry the same weight, 0.0552, as a Gauss–Legendre rule's symmetric end
/// weights do), with the wizard's Weight at 1 in the screenshot beside it.
/// </para>
/// <para>
/// <b>Not reproduced.</b> Rays that fail are left out (their rows could only fail) and named in
/// the notes; mirrors get no rows (<see cref="HighYieldPenalty"/>).
/// </para>
/// <para>
/// <b>Where the rows go.</b> At the end of the merit function, after a BLNK row whose comment
/// starts with <see cref="HeaderPrefix"/>; <see cref="WithRows"/> replaces an earlier block, and
/// <see cref="Without"/> removes it. A block not at the end, or HYLD rows outside it, are refused
/// by name rather than renumbered: edit those in the Merit Function Editor.
/// </para>
/// </summary>
public static class HighYieldMerit
{
    /// <summary>The start of the comment of the block's header row.</summary>
    public const string HeaderPrefix = "High-Yield:";

    /// <summary>The default weight: the Optimization Wizard's Weight box in KA-01837's screenshot
    /// (KA-01837_8_Wizard.png) shows 1.</summary>
    public const double DefaultWeight = 1;

    /// <summary>The problems of a weight; empty when it can be used.</summary>
    public static IReadOnlyList<string> Validate(double weight) =>
        weight > 0 && double.IsFinite(weight)
            ? Array.Empty<string>()
            : new[] { $"The High-Yield weight must be positive and finite (it is {F(weight)})." };

    /// <summary>
    /// The High-Yield rows for <paramref name="configurations"/> (<paramref name="current"/> the
    /// current configuration, 1-based) with the default merit function's options
    /// <paramref name="wizard"/> and High-Yield weight <paramref name="weight"/>. Throws
    /// <see cref="ArgumentException"/> naming why (the wizard's own problems included).
    /// </summary>
    public static HighYieldRows Generate(IMeritConfigurations configurations, int current, DefaultMeritOptions wizard, double weight,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        ArgumentNullException.ThrowIfNull(wizard);
        var problems = Validate(weight);
        if (problems.Count > 0) throw new ArgumentException("High-Yield: " + string.Join(" ", problems));

        // The default merit function's own decisions: its validation, its integration (switched to
        // the rectangular array when the pupil is vignetted) and its configurations.
        DefaultMeritResult merit;
        try { merit = DefaultMeritWizard.Generate(configurations, current, wizard, cancellation); }
        catch (ArgumentException e) { throw new ArgumentException("High-Yield: the default merit function's options cannot be used: " + e.Message, e); }
        var rule = merit.Integration == PupilIntegration.GaussianQuadrature
            ? PupilQuadrature.Gaussian(wizard.Rings, wizard.Arms) : PupilQuadrature.Rectangular(wizard.Grid);
        var chosen = wizard.Configurations == MeritWizardConfigurations.All && configurations.Count > 1
            ? Enumerable.Range(1, configurations.Count).ToArray() : new[] { current };

        var rows = new List<MeritOperandRow>();
        var notes = new List<string>();
        var bySurface = new SortedDictionary<int, IReadOnlyList<int>>();
        int operands = 0, failedRays = 0;
        foreach (int k in chosen)
        {
            var lens = configurations.LensFor(k);
            var system = configurations.SystemFor(k);
            bool symmetric = wizard.AssumeAxialSymmetry ?? MeritEvaluator.IsRotationallySymmetric(lens);
            var tracer = RayAberrationOperands.For(lens, system);
            string where = chosen.Length > 1 || configurations.Count > 1 ? $"configuration {k}, " : "";
            if (configurations.Count > 1)
                rows.Add(new MeritOperandRow { Code = "CONF", Int1 = k, Comment = $"{HeaderPrefix} configuration {k}" });
            var mirrors = new List<int>();
            var used = new SortedSet<int>();
            foreach (var (field, hx, hy, fieldWeight) in Fields(system))
            {
                cancellation.ThrowIfCancellationRequested();
                var points = DefaultMeritWizard.Points(rule, hx, hy, symmetric, radial: true);
                foreach (var (wave, waveWeight) in Waves(system))
                {
                    var surfaces = HighYieldPenalty.RefractingSurfaces(lens, system, wave, mirrors);
                    foreach (var (px, py, pupilWeight) in points)
                    {
                        string? failure;
                        try { failure = tracer.RayFailure(wave, hx, hy, px, py); }
                        catch (MeritOperandException e) { throw new ArgumentException($"High-Yield: {where}{e.Message}", e); }
                        if (failure is not null) { failedRays++; continue; }
                        double w = weight * wizard.OverallWeight * fieldWeight * waveWeight * pupilWeight;
                        foreach (int s in surfaces)
                        {
                            rows.Add(new MeritOperandRow { Code = HighYieldOperands.Code, Int1 = s, Int2 = wave, Data = new[] { hx, hy, px, py }, Weight = w });
                            used.Add(s);
                            operands++;
                        }
                    }
                }
            }
            bySurface[k] = used.ToArray();
            if (mirrors.Count > 0)
                notes.Add(Capital($"{where}mirror{(mirrors.Count == 1 ? "" : "s")} {string.Join(", ", mirrors)}: no HYLD rows; the High-Yield term is defined for refracting surfaces (KA-01837)."));
            if (used.Count == 0)
                notes.Add(Capital($"{where}no surface changes the index, so there is nothing for the High-Yield term to act on."));
        }
        if (failedRays > 0)
            notes.Add($"{failedRays} pupil point{(failedRays == 1 ? "" : "s")} whose ray fails {(failedRays == 1 ? "was" : "were")} left out; regenerate the rows if the vignetting changes.");
        if (merit.Integration != wizard.Integration)
            notes.AddRange(merit.Notes.Where(n => n.StartsWith("The pupil is vignetted", StringComparison.Ordinal)));
        if (operands == 0)
            throw new ArgumentException("High-Yield: no HYLD row could be written. " + string.Join(" ", notes));

        string sampling = rule.Method == PupilIntegration.GaussianQuadrature
            ? $"Gaussian quadrature {rule.Density} rings × {rule.Arms} arms" : $"rectangular array {rule.Density} × {rule.Density}";
        string summary = $"{HeaderPrefix} {operands} HYLD rows, weight {F(weight)}, {sampling}, surfaces "
            + string.Join("; ", bySurface.Select(p => (bySurface.Count > 1 ? $"configuration {p.Key}: " : "") + string.Join(", ", p.Value))) + ".";
        rows.Insert(0, MeritOperandRow.Of("BLNK") with { Comment = summary });
        return new HighYieldRows
        {
            Rows = rows, Integration = merit.Integration, OperandCount = operands,
            Surfaces = bySurface.ToDictionary(p => p.Key, p => p.Value), Summary = summary, Notes = notes,
        };
    }

    /// <summary>The 1-based number of the block's header row, or null when there is none.
    /// Throws <see cref="ArgumentException"/> when a block is not at the end, or HYLD rows lie
    /// outside it, naming the rows.</summary>
    public static int? FindBlock(MeritFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        int? header = null;
        for (int n = 1; n <= function.Rows.Count; n++)
        {
            var r = function.Row(n);
            if (IsCode(r, "BLNK") && r.Comment.StartsWith(HeaderPrefix, StringComparison.Ordinal))
            {
                if (header is not null)
                    throw new ArgumentException($"High-Yield: rows {header} and {n} both start a High-Yield block; delete one in the Merit Function Editor.");
                header = n;
            }
        }
        var stray = Enumerable.Range(1, function.Rows.Count)
            .Where(n => (header is null || n < header) && IsCode(function.Row(n), HighYieldOperands.Code)).ToArray();
        if (stray.Length > 0)
            throw new ArgumentException($"High-Yield: HYLD row{(stray.Length == 1 ? "" : "s")} {string.Join(", ", stray)} {(stray.Length == 1 ? "is" : "are")} outside the High-Yield block; delete or move {(stray.Length == 1 ? "it" : "them")} in the Merit Function Editor first.");
        if (header is int h)
        {
            var foreign = Enumerable.Range(h + 1, function.Rows.Count - h)
                .Where(n => !(IsCode(function.Row(n), HighYieldOperands.Code) || IsCode(function.Row(n), "CONF") || IsCode(function.Row(n), "BLNK"))).ToArray();
            if (foreign.Length > 0)
                throw new ArgumentException($"High-Yield: row{(foreign.Length == 1 ? "" : "s")} {string.Join(", ", foreign)} follow{(foreign.Length == 1 ? "s" : "")} the High-Yield block (row {h}) but {(foreign.Length == 1 ? "is" : "are")} not part of it; the block must be at the end of the merit function.");
        }
        return header;
    }

    /// <summary><paramref name="existing"/> without its High-Yield block (unchanged when it has
    /// none). Throws <see cref="ArgumentException"/> as <see cref="FindBlock"/>, or when a row
    /// before the block jumps into it (GOTO, SKIS, SKIN).</summary>
    public static MeritFunction Without(MeritFunction existing, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (FindBlock(existing) is not int header) return existing;
        registry ??= MeritOperandRegistry.CreateDefault();
        for (int n = 1; n < header; n++)
        {
            var r = existing.Row(n);
            if (registry.Find(r.Code) is { Control: MeritControl.GoTo or MeritControl.SkipIfSymmetric or MeritControl.SkipIfNotSymmetric } && r.Int1 >= header)
                throw new ArgumentException($"High-Yield: row {n} ({r.Code}) jumps to row {r.Int1}, inside the High-Yield block; change it in the Merit Function Editor first.");
        }
        return new MeritFunction(existing.Rows.Take(header - 1));
    }

    /// <summary><paramref name="existing"/> with its High-Yield block (if any) replaced by
    /// <paramref name="rows"/> at the end.</summary>
    public static MeritFunction WithRows(MeritFunction existing, IReadOnlyList<MeritOperandRow> rows, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new MeritFunction(Without(existing, registry).Rows.Concat(rows));
    }

    /// <summary>The HYLD rows of an evaluated merit function, surface by surface.</summary>
    public static HighYieldReport Report(MeritEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var groups = new SortedDictionary<(int, int), (int Rows, double Sw, double Swt, double Max, double Share)>();
        var failures = new List<string>();
        double total = 0;
        foreach (var r in evaluation.Rows)
        {
            if (!IsCode(r.Row, HighYieldOperands.Code)) continue;
            if (r.Status == MeritRowStatus.Failed) { failures.Add(r.Error ?? $"Row {r.Number} failed."); total += r.Contribution; continue; }
            if (r.Status != MeritRowStatus.Evaluated) continue;
            total += r.Contribution;
            var key = (r.Configuration, r.Row.Int1);
            groups.TryGetValue(key, out var g);
            double w = Math.Abs(r.Row.Weight);
            groups[key] = (g.Rows + 1, g.Sw + w, g.Swt + w * r.Value, Math.Max(g.Max, r.Value), g.Share + r.Contribution);
        }
        return new HighYieldReport
        {
            Surfaces = groups.Select(p => new HighYieldSurfaceTerm(p.Key.Item1, p.Key.Item2, p.Value.Rows,
                p.Value.Sw > 0 ? p.Value.Swt / p.Value.Sw : double.NaN, p.Value.Max, p.Value.Share)).ToArray(),
            Contribution = total,
            Failures = failures,
        };
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsCode(MeritOperandRow row, string code) => string.Equals(row.Code, code, StringComparison.OrdinalIgnoreCase);

    /// <summary>The fields of non-zero weight, as the wizard takes them: 0-based row, (Hx, Hy), weight.</summary>
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

    private static string Capital(string s) => s.Length == 0 ? "" : char.ToUpperInvariant(s[0]) + s[1..];

    private static string F(double x) => x.ToString("G6", CultureInfo.InvariantCulture);
}
