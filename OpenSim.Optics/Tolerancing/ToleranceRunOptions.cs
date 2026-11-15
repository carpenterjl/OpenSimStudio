using System.Globalization;
using OpenSim.Core.Model;

namespace OpenSim.Optics.Tolerancing;

// OP-64: the settings of a tolerance run (sensitivity, inverse limit, inverse increment). The
// definitions are OpticStudio 2025 R2 help pages, saved under plans/sources/optics/op-63-help/
// ("Tolerancing", the dialog) and plans/sources/optics/op-64-help/ ("Sensitivity Analysis",
// "The RSS Estimated Change", "Inverse Sensitivity Analysis", "Evaluating Compensators", "How
// OpticStudio Computes the Tolerance Analysis", "Quick Sensitivity"); docs/Optics-Conventions.md §42.

/// <summary>The analysis a run makes (help, "Tolerancing", Set-Up tab, "Mode"). Skip Sensitivity
/// (straight to Monte Carlo) is the Monte Carlo item, OP-65, not built.</summary>
public enum ToleranceMode
{
    /// <summary>"Computes the change in the criterion for each of the extreme values of the tolerances."</summary>
    Sensitivity = 0,
    /// <summary>"Computes the value of each tolerance that will yield a criterion being equal to the value specified by the Limit parameter."</summary>
    InverseLimit = 1,
    /// <summary>"Computes the value of each tolerance that will yield a change in the criterion equal to the value specified by the Increment parameter."</summary>
    InverseIncrement = 2,
}

/// <summary>
/// The tolerance criterion (help, "Tolerancing", Criterion tab). Built: the RMS spot radius, x and
/// y and the RMS wavefront (OP-40's RMS core), the merit function (OP-54), the boresight error,
/// and the Strehl ratio (OP-38's PSF engine, an addition of the plan; OpticStudio's list does not
/// have it). Not built: geometric and diffraction MTF (OP-39), encircled energy (OP-41), RMS angular
/// radial, x and y, and the user script (<see cref="ToleranceCriteria.NotBuilt"/>). A criterion is
/// added with one value here, one arm in <see cref="ToleranceCriteria.Info"/> and one arm in the
/// criterion evaluator.
/// </summary>
public enum ToleranceCriterion
{
    RmsSpotRadius = 0,
    RmsSpotX = 1,
    RmsSpotY = 2,
    RmsWavefront = 3,
    MeritFunction = 4,
    BoresightError = 5,
    StrehlRatio = 6,
}

/// <summary>How the change due to a tolerance is computed (help, "Tolerancing", "Change").</summary>
public enum ToleranceChangeMode
{
    /// <summary>Δ = P − N.</summary>
    LinearDifference = 0,
    /// <summary>Δ = S(P − N)·√|P² − N²|, S(x) = +1 for x ≥ 0 and −1 otherwise.</summary>
    RssDifference = 1,
}

/// <summary>How compensators are evaluated (help, "Tolerancing", "Comp"; "Evaluating Compensators").</summary>
public enum ToleranceCompensation
{
    /// <summary>"No compensation will be performed, and any defined compensators will be ignored."</summary>
    None = 0,
    /// <summary>"The focus is adjusted to maintain the amount of defocus in the nominal lens
    /// design"; every COMP and CPAR row is ignored.</summary>
    ParaxialFocus = 1,
    /// <summary>"Optimize All (DLS)": "one cycle of the Orthogonal Decent algorithm then ... the
    /// Damped Least Squares algorithm" (OP-57's <see cref="Optimization.LocalOptimizer"/>).</summary>
    OptimizeDls = 2,
    /// <summary>"Optimize All (OD)": Orthogonal Descent only.</summary>
    OptimizeOd = 3,
}

/// <summary>The field points of the criterion (help, "Tolerancing", "Fields").</summary>
public enum ToleranceFieldSet
{
    /// <summary>+1, +0.7, 0, −0.7, −1 times the largest field coordinate in y; the centre weighs 2, the others 1.</summary>
    YSymmetric = 0,
    /// <summary>The five y points and ±0.7, ±1 in x; the centre weighs 4, the others 1.</summary>
    XYSymmetric = 1,
    /// <summary>The system's own field points and weights.</summary>
    UserDefined = 2,
}

/// <summary>Progress of a run: trials (one perturbed lens evaluated and compensated) done so far.</summary>
public sealed record ToleranceRunProgress(int RowsDone, int RowCount, int Trials, string? Last);

/// <summary>Settings of <see cref="ToleranceAnalysis.Run"/>. Every member has a usable default.</summary>
public sealed record ToleranceRunOptions
{
    public ToleranceMode Mode { get; init; } = ToleranceMode.Sensitivity;

    public ToleranceCriterion Criterion { get; init; } = ToleranceCriterion.RmsSpotRadius;

    public ToleranceChangeMode Change { get; init; } = ToleranceChangeMode.LinearDifference;

    public ToleranceCompensation Compensation { get; init; } = ToleranceCompensation.ParaxialFocus;

    /// <summary>The default is Y-Symmetric, "the default for rotationally symmetric lenses" (help).</summary>
    public ToleranceFieldSet Fields { get; init; } = ToleranceFieldSet.YSymmetric;

    /// <summary>Spot and wavefront: "the number of rays traced along a radial arm of the pupil in
    /// the Gaussian quadrature technique ... The number of arms is always twice the number of rays
    /// along each arm" (help), 1 to 20. The default 3 is the help's starting point ("run the
    /// tolerance at a sampling of 3, then again for a sampling of 4").</summary>
    public int Sampling { get; init; } = 3;

    /// <summary>The Strehl ratio's pupil grid, 16 to 1024 (OP-38's Strehl ratio analysis).</summary>
    public int PsfSampling { get; init; } = 64;

    /// <summary>Inverse Limit: the criterion value each tolerance may reach.</summary>
    public double Limit { get; init; }

    /// <summary>Inverse Increment: the change each tolerance may cause; positive.</summary>
    public double Increment { get; init; }

    /// <summary>The compensator optimizer's cycles; null for automatic ("Cycles": "Auto ... the
    /// safest to use").</summary>
    public int? OptimizerCycles { get; init; }

    /// <summary>The 1-based rows of the COMP, CPAR and CMCO compensators the optimizer drives; null
    /// for all of them ("Optimize All").</summary>
    public IReadOnlyList<int>? CompensatorRows { get; init; }

    /// <summary>How many worst offenders to list (help, Classic tab, "Show worst"); the default 10
    /// is this program's.</summary>
    public int ShowWorst { get; init; } = 10;

    /// <summary>The thread limit over tolerance rows; null for no limit. The result is bitwise
    /// identical at every setting.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>Inverse modes: the width, as a fraction of the starting limit, to which the root
    /// finder brackets the adjusted limit.</summary>
    public double InverseTolerance { get; init; } = 1e-7;

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Called after every tolerance row, on a worker thread.</summary>
    public Action<ToleranceRunProgress>? Progress { get; init; }
}

/// <summary>What a criterion is: its name, whether larger is better, and whether Quick
/// Sensitivity offers it.</summary>
public sealed record ToleranceCriterionInfo(ToleranceCriterion Criterion, string Name, bool HigherIsBetter, bool InQuickSensitivity);

/// <summary>The criteria and what each is.</summary>
public static class ToleranceCriteria
{
    public static IReadOnlyList<ToleranceCriterionInfo> All { get; } = Enum.GetValues<ToleranceCriterion>().Select(Info).ToArray();

    /// <summary>The criteria of OpticStudio's list (and the plan's) that are not built, with the item that builds them.</summary>
    public static IReadOnlyList<string> NotBuilt { get; } = new[]
    {
        "Geometric MTF (average, tangential, sagittal): needs OP-39's MTF engine.",
        "Diffraction MTF (average, tangential, sagittal): needs OP-39's MTF engine.",
        "Encircled energy: needs OP-41's enclosed-energy engine.",
        "RMS angular radial, x and y (afocal systems): not built.",
        "User script: tolerance scripts are OP-67.",
    };

    public static ToleranceCriterionInfo Info(ToleranceCriterion c) => c switch
    {
        ToleranceCriterion.RmsSpotRadius => new(c, "RMS spot radius", false, true),
        ToleranceCriterion.RmsSpotX => new(c, "RMS spot x", false, true),
        ToleranceCriterion.RmsSpotY => new(c, "RMS spot y", false, true),
        ToleranceCriterion.RmsWavefront => new(c, "RMS wavefront", false, true),
        ToleranceCriterion.MeritFunction => new(c, "Merit function", false, false),
        ToleranceCriterion.BoresightError => new(c, "Boresight error", false, true),
        ToleranceCriterion.StrehlRatio => new(c, "Strehl ratio", true, false),
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Unknown tolerance criterion."),
    };

    /// <summary>The criterion's unit: lens units for a spot, waves (primary wavelength) for the
    /// wavefront, radians for the boresight error, none for the merit function and the Strehl ratio.</summary>
    public static string Unit(ToleranceCriterion c, LensUnit unit) => c switch
    {
        ToleranceCriterion.RmsSpotRadius or ToleranceCriterion.RmsSpotX or ToleranceCriterion.RmsSpotY => Paraxial.ParaxialReport.UnitName(unit),
        ToleranceCriterion.RmsWavefront => "waves",
        ToleranceCriterion.BoresightError => "rad",
        _ => "",
    };
}

/// <summary>The run options to and from the files' <see cref="OpticsToleranceRunSettings"/>.</summary>
public static class ToleranceRunSettings
{
    /// <summary>The saved form; null when every option is the default.</summary>
    public static OpticsToleranceRunSettings? ToSettings(ToleranceRunOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var d = new ToleranceRunOptions();
        var s = new OpticsToleranceRunSettings
        {
            Mode = o.Mode == d.Mode ? null : o.Mode.ToString(),
            Criterion = o.Criterion == d.Criterion ? null : o.Criterion.ToString(),
            Change = o.Change == d.Change ? null : o.Change.ToString(),
            Compensation = o.Compensation == d.Compensation ? null : o.Compensation.ToString(),
            Fields = o.Fields == d.Fields ? null : o.Fields.ToString(),
            Sampling = o.Sampling == d.Sampling ? null : o.Sampling,
            PsfSampling = o.PsfSampling == d.PsfSampling ? null : o.PsfSampling,
            OptimizerCycles = o.OptimizerCycles is int c && c > 0 ? c : null,
            CompensatorRows = o.CompensatorRows?.ToList(),
            Limit = o.Limit == 0 ? null : o.Limit,
            Increment = o.Increment == 0 ? null : o.Increment,
            ShowWorst = o.ShowWorst == d.ShowWorst ? null : o.ShowWorst,
        };
        return s == new OpticsToleranceRunSettings() ? null : s;
    }

    /// <summary>The options of saved settings; null is the defaults. An unknown name keeps the
    /// default and is named in <paramref name="notes"/>.</summary>
    public static ToleranceRunOptions FromSettings(OpticsToleranceRunSettings? saved, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var o = new ToleranceRunOptions();
        if (saved is null) return o;
        T Parse<T>(string? name, T fallback, string what) where T : struct, Enum
        {
            if (name is null) return fallback;
            if (Enum.TryParse<T>(name, ignoreCase: true, out var v) && Enum.IsDefined(v) && !int.TryParse(name, out _)) return v;
            notes.Add($"Tolerance run: the {what} '{name}' is not one this version knows; '{fallback}' is used.");
            return fallback;
        }
        return o with
        {
            Mode = Parse(saved.Mode, o.Mode, "mode"),
            Criterion = Parse(saved.Criterion, o.Criterion, "criterion"),
            Change = Parse(saved.Change, o.Change, "change"),
            Compensation = Parse(saved.Compensation, o.Compensation, "compensation"),
            Fields = Parse(saved.Fields, o.Fields, "field set"),
            Sampling = saved.Sampling ?? o.Sampling,
            PsfSampling = saved.PsfSampling ?? o.PsfSampling,
            OptimizerCycles = saved.OptimizerCycles is int c && c > 0 ? c : null,
            CompensatorRows = saved.CompensatorRows?.ToArray(),
            Limit = saved.Limit ?? 0,
            Increment = saved.Increment ?? 0,
            ShowWorst = saved.ShowWorst ?? o.ShowWorst,
        };
    }

    internal static string F(double v) => v.ToString("G8", CultureInfo.InvariantCulture);
}
