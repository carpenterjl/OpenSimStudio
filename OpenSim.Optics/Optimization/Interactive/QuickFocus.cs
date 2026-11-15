using OpenSim.Core.Model;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization.Interactive;

// OP-60: OpticStudio's Quick Focus (docs/Optics-Conventions.md §44).

/// <summary>The best-focus criterion of Quick Focus (OpticStudio 2025 R2 help, "Quick Focus",
/// Settings: "Spot Size Radial", "Spot Size X Only", "Spot Size Y Only", "Wavefront Error").</summary>
public enum QuickFocusCriterion
{
    /// <summary>"Focus to best RMS spot radius image surface."</summary>
    SpotRadius = 0,
    /// <summary>"Focus to best RMS x direction spot size image surface."</summary>
    SpotX = 1,
    /// <summary>"Focus to best RMS y direction spot size image surface."</summary>
    SpotY = 2,
    /// <summary>"Focus to best RMS wavefront error image surface."</summary>
    Wavefront = 3,
}

/// <summary>Settings of <see cref="QuickFocus"/>.</summary>
public sealed record QuickFocusOptions
{
    public QuickFocusCriterion Criterion { get; init; } = QuickFocusCriterion.SpotRadius;

    /// <summary>The help's "Use Centroid": reference every calculation to the image centroid
    /// rather than the chief ray (spot: the weighted centroid; wavefront: piston and tilt removed,
    /// as OP-40's "Refer to centroid").</summary>
    public bool UseCentroid { get; init; }

    /// <summary>Gaussian quadrature rings, 1 to 20, 4 arms per ring (this program's choice; the
    /// help gives no sampling). Default 6, the RMS analyses' default.</summary>
    public int Rings { get; init; } = 6;

    /// <summary>The thread limit (null: none); the result is the same bits at any value.</summary>
    public int? MaxDegreeOfParallelism { get; init; }
}

/// <summary>What <see cref="QuickFocus.Find"/> found. Lengths in meters; the criterion in
/// meters (spot) or waves of the primary wavelength (wavefront).</summary>
public sealed record QuickFocusResult
{
    /// <summary>The surface whose thickness was adjusted: the one before the image.</summary>
    public required int Surface { get; init; }
    public required double StartThickness { get; init; }
    public required double Thickness { get; init; }
    /// <summary>Thickness − StartThickness.</summary>
    public required double Shift { get; init; }
    public required double StartValue { get; init; }
    public required double Value { get; init; }
    public required QuickFocusCriterion Criterion { get; init; }
    /// <summary>How far the criterion's minimum can lie from <see cref="Thickness"/> by the
    /// search's stopping rule, meters.</summary>
    public required double PositionTolerance { get; init; }
    public required int Evaluations { get; init; }
    /// <summary>Trial thicknesses where the criterion had no value (a ray failed).</summary>
    public required int FailedEvaluations { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }

    /// <summary>The criterion as shown: µm for a spot, waves for the wavefront.</summary>
    public static double Shown(QuickFocusCriterion criterion, double value) => criterion == QuickFocusCriterion.Wavefront ? value : value * 1e6;

    public static string Unit(QuickFocusCriterion criterion) => criterion == QuickFocusCriterion.Wavefront ? "waves" : "µm";

    public static string Label(QuickFocusCriterion criterion) => criterion switch
    {
        QuickFocusCriterion.SpotRadius => "RMS spot radius",
        QuickFocusCriterion.SpotX => "RMS spot x",
        QuickFocusCriterion.SpotY => "RMS spot y",
        _ => "RMS wavefront error",
    };
}

/// <summary>A Quick Focus applied to a project record: the record with the new thickness, and the search.</summary>
public sealed record QuickFocusApplied(OpticsSettings Settings, QuickFocusResult Result);

/// <summary>
/// OpticStudio's Quick Focus (2025 R2 help, "Quick Focus"): "This feature adjusts the thickness
/// of the surface prior to the image surface. The thickness chosen is that which minimizes the
/// RMS aberration", with the RMS "always computed as a polychromatic average over the field, using
/// the defined fields, wavelengths, and weights".
/// <para>
/// The criterion of one field is OP-40's RMS (<see cref="RmsCore.Evaluate"/>, the engine of RMS vs
/// Focus): the field row's position, every wavelength (polychromatic when there are several, each
/// weighted by its weight), the focus shift added to the thickness before the image, Gaussian
/// quadrature over the pupil, about the chief ray or the centroid. The fields with a positive
/// weight are combined as √(Σ w_f v_f² / Σ w_f) (<see cref="InteractiveSearch.CombineFields"/>);
/// one field is its own value, so for a one-field lens the criterion is exactly what RMS vs Focus
/// plots. The shift is found by <see cref="InteractiveSearch"/> from a first step of 1% of the
/// lens's length scale.
/// </para>
/// </summary>
public static class QuickFocus
{
    private const string Who = "Quick Focus";

    /// <summary>The best focus of a lens; the lens itself is not changed.</summary>
    public static QuickFocusResult Find(SequentialLens lens, OpticalSystemData system, QuickFocusOptions options,
        CancellationToken cancellation = default)
    {
        var problem = Problem.Create(lens, system, options, cancellation);
        double step = 0.01 * InteractiveSearch.LengthScale(problem.Setup.Lens);
        var outcome = InteractiveSearch.Minimize(t => problem.At(t * step, cancellation), Who, cancellation);
        int last = problem.Setup.Lens.LastSurfaceIndex;
        double start = problem.Setup.Lens.Surfaces[last].Thickness;
        double shift = outcome.T * step;
        var unit = problem.Setup.Unit;
        var assumptions = problem.Assumptions();
        assumptions.Add($"Search: the thickness of surface {last} from {Len(start, unit)} with first steps of {Len(step, unit)}, the bracket widened by the golden ratio, then Brent's method; {outcome.Evaluations} evaluations; the minimum lies within {Len(InteractiveSearch.Tolerance(outcome.T, step), unit)} of the answer if the criterion has one minimum in the bracket.");
        if (outcome.Failed > 0)
            assumptions.Add($"{outcome.Failed} trial thicknesses gave no value and were treated as worse than any other; the first: {outcome.FirstFailure}");
        if (outcome.T == 0)
            assumptions.Add("The criterion did not fall from its value at the thickness as it was: the thickness is unchanged.");
        return new QuickFocusResult
        {
            Surface = last, StartThickness = start, Thickness = start + shift, Shift = shift,
            StartValue = outcome.StartValue, Value = outcome.Value, Criterion = options.Criterion,
            PositionTolerance = InteractiveSearch.Tolerance(outcome.T, step),
            Evaluations = outcome.Evaluations, FailedEvaluations = outcome.Failed, Assumptions = assumptions,
        };
    }

    /// <summary>The criterion with <paramref name="shift"/> meters added to the thickness before
    /// the image (NaN and the reason when it has no value).</summary>
    public static (double Value, string? Failure) Criterion(SequentialLens lens, OpticalSystemData system, QuickFocusOptions options,
        double shift, CancellationToken cancellation = default) =>
        Problem.Create(lens, system, options, cancellation).At(shift, cancellation);

    /// <summary>Quick Focus on a project record: the current configuration's solved lens, the
    /// thickness before the image refused if a solve, pickup or the multi-configuration table
    /// sets it, and the record returned with that thickness changed (lens units).</summary>
    public static QuickFocusApplied Run(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, QuickFocusOptions options,
        CancellationToken cancellation = default)
    {
        var built = InteractiveProject.Build(optics, catalogs, Who);
        int last = built.Lens.LastSurfaceIndex;
        InteractiveProject.CheckOwnCell(optics, catalogs, last, SavedSurfaces.ThicknessColumn, Who);
        var result = Find(built.Lens, built.System, options, cancellation);
        result = result with { Assumptions = result.Assumptions.Concat(built.Notes).ToArray() };
        var s = optics.Surfaces![last];
        var updated = InteractiveProject.WithSurface(optics, last, s with { Thickness = s.Thickness + OpticsConventions.FromMeters(result.Shift, built.Unit) });
        return new QuickFocusApplied(updated, result);
    }

    private static string Len(double meters, LensUnit unit) =>
        $"{StudyReport.Number(OpticsConventions.FromMeters(meters, unit), "g8")} {ParaxialReport.UnitName(unit)}";

    private sealed class Problem
    {
        public required RmsSetup Setup { get; init; }
        public required RmsQuantity Quantity { get; init; }
        public required RmsReference Reference { get; init; }
        public required PupilQuadratureRule Rule { get; init; }
        public required List<(int Row, double Weight)> Fields { get; init; }
        public required int[] Rows { get; init; }
        public required bool Polychromatic { get; init; }
        public int? MaxDegreeOfParallelism { get; init; }

        public static Problem Create(SequentialLens lens, OpticalSystemData system, QuickFocusOptions options, CancellationToken cancellation)
        {
            ArgumentNullException.ThrowIfNull(lens);
            ArgumentNullException.ThrowIfNull(system);
            ArgumentNullException.ThrowIfNull(options);
            if (!Enum.IsDefined(options.Criterion))
                throw new ArgumentException($"{Who}: unknown criterion {(int)options.Criterion}.");
            if (system.Aperture.AfocalImageSpace)
                throw new InvalidOperationException($"{Who}: the system is in afocal image space. The help's criteria (spot sizes and wavefront at the image) need an image at a finite distance, and the thickness before the image does not change ray angles. Use Quick Adjust's angular criteria on an earlier thickness instead.");
            if (lens.LastSurfaceIndex < 1) throw new InvalidOperationException($"{Who}: the lens has no surface before the image.");
            if (!double.IsFinite(lens.Surfaces[lens.LastSurfaceIndex].Thickness))
                throw new InvalidOperationException($"{Who}: the thickness of surface {lens.LastSurfaceIndex} (before the image) is infinite.");
            var rule = RmsCore.Rule(PupilIntegration.GaussianQuadrature, options.Rings, Who);
            var notes = new List<string>();
            var setup = RmsCore.Prepare(lens, system, removeVignettingFactors: false, Who, notes, cancellation);
            var rows = FieldScanCommon.Rows(0, setup.System, Who, zeroIsAll: true);
            return new Problem
            {
                Setup = setup,
                Quantity = options.Criterion switch
                {
                    QuickFocusCriterion.SpotRadius => RmsQuantity.SpotRadius,
                    QuickFocusCriterion.SpotX => RmsQuantity.SpotX,
                    QuickFocusCriterion.SpotY => RmsQuantity.SpotY,
                    _ => RmsQuantity.WavefrontRms,
                },
                Reference = options.UseCentroid ? RmsReference.Centroid : RmsReference.ChiefRay,
                Rule = rule,
                Fields = InteractiveSearch.WeightedFields(setup.System, Who),
                Rows = rows,
                Polychromatic = rows.Length > 1,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            };
        }

        public (double Value, string? Failure) At(double shift, CancellationToken cancellation)
        {
            var unit = Setup.Unit;
            var points = Fields.Select(f =>
            {
                var p = Setup.System.Fields.Points[f.Row];
                return new RmsPoint
                {
                    FieldX = p.X, FieldY = p.Y, Rows = Rows, Polychromatic = Polychromatic, Focus = shift,
                    Label = $"{RmsPresentation.FieldRowLabel(Setup, f.Row)}, focus shift {StudyReport.Number(OpticsConventions.FromMeters(shift, unit), "g8")} {ParaxialReport.UnitName(unit)}",
                };
            }).ToArray();
            var values = RmsCore.EvaluateAll(Setup, points, Quantity, Reference, Rule, cancellation, MaxDegreeOfParallelism);
            if (values.FirstOrDefault(v => !v.Succeeded) is { } failed) return (double.NaN, failed.Failure);
            return (InteractiveSearch.CombineFields(values.Select(v => v.Value).ToArray(), Fields.Select(f => f.Weight).ToArray()), null);
        }

        public List<string> Assumptions()
        {
            var lines = RmsCore.Assumptions(Setup, Quantity, Reference, Rule);
            lines.Insert(0, $"Criterion: {QuickFocusResult.Label(CriterionOf(Quantity))}, {(Polychromatic ? "polychromatic over every wavelength with its weight" : "one wavelength")}, about the {(Reference == RmsReference.Centroid ? "centroid" : "chief ray")}; the fields with a positive weight combined as √(Σ w·RMS² / Σ w) (OpticStudio 2025 R2 help, \"Quick Focus\": \"a polychromatic average over the field, using the defined fields, wavelengths, and weights\").");
            lines.AddRange(Setup.Launcher.Notes);
            lines.AddRange(Setup.Notes);
            return lines;
        }

        private static QuickFocusCriterion CriterionOf(RmsQuantity q) => q switch
        {
            RmsQuantity.SpotRadius => QuickFocusCriterion.SpotRadius,
            RmsQuantity.SpotX => QuickFocusCriterion.SpotX,
            RmsQuantity.SpotY => QuickFocusCriterion.SpotY,
            _ => QuickFocusCriterion.Wavefront,
        };
    }
}
