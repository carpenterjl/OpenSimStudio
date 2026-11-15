using OpenSim.Core.Model;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization.Interactive;

// OP-60: OpticStudio's Quick Adjust (docs/Optics-Conventions.md §44).

/// <summary>Which cell Quick Adjust varies (OpticStudio 2025 R2 help, "Quick Adjust", Settings,
/// "Radius/Thickness").</summary>
public enum QuickAdjustParameter
{
    /// <summary>The radius, varied as the curvature 1/R so that it may pass through a plane.</summary>
    Radius = 0,
    Thickness = 1,
}

/// <summary>
/// Quick Adjust's criterion. The help names "best transverse or angular ray focus", says "All
/// criterion use the image centroid for a reference" and that angular data are "computed after
/// refraction into the specified evaluation surface", but does not list the choices; radial, x and
/// y of each, by analogy with Quick Focus's list, are this program's reading
/// (docs/Optics-Conventions.md §10, item 40).
/// </summary>
public enum QuickAdjustCriterion
{
    /// <summary>RMS radial distance of the rays from their centroid at the evaluation surface.</summary>
    SpotRadius = 0,
    SpotX = 1,
    SpotY = 2,
    /// <summary>RMS radial spread of the rays' direction cosines (L, M) about their centroid,
    /// after refraction into the evaluation surface (for small angles, radians).</summary>
    AngularRadius = 3,
    AngularX = 4,
    AngularY = 5,
}

/// <summary>Settings of <see cref="QuickAdjust"/>.</summary>
public sealed record QuickAdjustOptions
{
    /// <summary>The surface whose radius or thickness is adjusted (0-based; 0 is the object).</summary>
    public int Surface { get; init; } = 1;
    public QuickAdjustParameter Parameter { get; init; } = QuickAdjustParameter.Thickness;
    public QuickAdjustCriterion Criterion { get; init; } = QuickAdjustCriterion.SpotRadius;
    /// <summary>The surface the criterion is evaluated at, after the adjusted one; null for the image.</summary>
    public int? EvaluateSurface { get; init; }
    /// <summary>Gaussian quadrature rings, 1 to 20, 4 arms each (this program's choice, as
    /// <see cref="QuickFocusOptions.Rings"/>).</summary>
    public int Rings { get; init; } = 6;
    public int? MaxDegreeOfParallelism { get; init; }
}

/// <summary>What <see cref="QuickAdjust.Find"/> found. A radius is reported as its curvature
/// (1/m), a thickness in meters; the criterion in meters (spot) or direction cosine (angular).</summary>
public sealed record QuickAdjustResult
{
    public required int Surface { get; init; }
    public required QuickAdjustParameter Parameter { get; init; }
    public required int EvaluateSurface { get; init; }
    public required QuickAdjustCriterion Criterion { get; init; }
    /// <summary>Curvature (1/m) or thickness (m) before.</summary>
    public required double StartValue { get; init; }
    /// <summary>Curvature (1/m) or thickness (m) after.</summary>
    public required double Value { get; init; }
    public required double StartCriterion { get; init; }
    public required double CriterionValue { get; init; }
    /// <summary>How far the criterion's minimum can lie from <see cref="Value"/> by the search's
    /// stopping rule, in <see cref="Value"/>'s unit.</summary>
    public required double PositionTolerance { get; init; }
    public required int Evaluations { get; init; }
    public required int FailedEvaluations { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }

    public static bool IsAngular(QuickAdjustCriterion c) => c >= QuickAdjustCriterion.AngularRadius;

    /// <summary>µm for a spot, milliradians (direction cosine × 1000) for an angle.</summary>
    public static double Shown(QuickAdjustCriterion c, double value) => IsAngular(c) ? value * 1e3 : value * 1e6;

    public static string Unit(QuickAdjustCriterion c) => IsAngular(c) ? "mrad" : "µm";

    public static string Label(QuickAdjustCriterion c) => c switch
    {
        QuickAdjustCriterion.SpotRadius => "RMS spot radius",
        QuickAdjustCriterion.SpotX => "RMS spot x",
        QuickAdjustCriterion.SpotY => "RMS spot y",
        QuickAdjustCriterion.AngularRadius => "RMS angular radius",
        QuickAdjustCriterion.AngularX => "RMS angular x",
        _ => "RMS angular y",
    };
}

/// <summary>A Quick Adjust applied to a project record.</summary>
public sealed record QuickAdjustApplied(OpticsSettings Settings, QuickAdjustResult Result);

/// <summary>
/// OpticStudio's Quick Adjust (2025 R2 help, "Quick Adjust"): "adjusts any radius or thickness to
/// minimize the RMS aberration at any subsequent surface. The RMS is always computed as a
/// polychromatic average over the field, using the defined fields, wavelengths, and weights."
/// <para>
/// The rays are OP-34's (<see cref="RaySampler.Trace"/>: the field rows with a positive weight,
/// the wavelengths with a positive weight, Gaussian quadrature pupil points, read at the evaluation
/// surface in its local frame). One field's value is the RMS about the centroid of its rays pooled
/// over wavelengths with weight pupil weight × wavelength weight (<see cref="SpotStatistics"/>), of
/// the positions (spot) or of the direction cosines after the surface (angular). Fields combine as
/// √(Σ w_f v_f² / Σ w_f). Gaussian quadrature needs every ray: a trial where any ray fails has no
/// value. A radius is searched as its curvature, from a first step of 1% of max(|c|, 1/L); a
/// thickness from 1% of L, L the lens's length scale (<see cref="InteractiveSearch.LengthScale"/>).
/// </para>
/// </summary>
public static class QuickAdjust
{
    private const string Who = "Quick Adjust";

    public static QuickAdjustResult Find(SequentialLens lens, OpticalSystemData system, QuickAdjustOptions options,
        CancellationToken cancellation = default)
    {
        var problem = Problem.Create(lens, system, options);
        int j = options.Surface;
        bool radius = options.Parameter == QuickAdjustParameter.Radius;
        double x0 = radius ? lens.Surfaces[j].Curvature : lens.Surfaces[j].Thickness;
        double scale = InteractiveSearch.LengthScale(lens);
        double step = radius ? 0.01 * Math.Max(Math.Abs(x0), 1 / scale) : 0.01 * scale;
        var outcome = InteractiveSearch.Minimize(t => problem.At(x0 + t * step, cancellation), Who, cancellation);
        double x = x0 + outcome.T * step;
        var assumptions = problem.Assumptions();
        string what = radius ? $"curvature of surface {j}" : $"thickness of surface {j}";
        string unit = radius ? "1/m" : "m";
        assumptions.Add($"Search: the {what} from {StudyReport.Number(x0, "g8")} {unit} with first steps of {StudyReport.Number(step, "g6")} {unit}, the bracket widened by the golden ratio, then Brent's method; {outcome.Evaluations} evaluations; the minimum lies within {StudyReport.Number(InteractiveSearch.Tolerance(outcome.T, step), "g3")} {unit} of the answer if the criterion has one minimum in the bracket.");
        if (outcome.Failed > 0)
            assumptions.Add($"{outcome.Failed} trial values gave no value and were treated as worse than any other; the first: {outcome.FirstFailure}");
        if (outcome.T == 0)
            assumptions.Add($"The criterion did not fall from its value at the {what} as it was: the value is unchanged.");
        return new QuickAdjustResult
        {
            Surface = j, Parameter = options.Parameter, EvaluateSurface = problem.Evaluate, Criterion = options.Criterion,
            StartValue = x0, Value = x, StartCriterion = outcome.StartValue, CriterionValue = outcome.Value,
            PositionTolerance = InteractiveSearch.Tolerance(outcome.T, step),
            Evaluations = outcome.Evaluations, FailedEvaluations = outcome.Failed, Assumptions = assumptions,
        };
    }

    /// <summary>The criterion with the adjusted cell at <paramref name="value"/> (curvature 1/m or
    /// thickness m); NaN and the reason when it has no value.</summary>
    public static (double Value, string? Failure) Criterion(SequentialLens lens, OpticalSystemData system, QuickAdjustOptions options,
        double value, CancellationToken cancellation = default) =>
        Problem.Create(lens, system, options).At(value, cancellation);

    /// <summary>Quick Adjust on a project record: the current configuration's solved lens, the
    /// cell refused if a solve, pickup or the multi-configuration table sets it, and the record
    /// returned with the cell changed (lens units; a curvature of 0 writes a plane).</summary>
    public static QuickAdjustApplied Run(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, QuickAdjustOptions options,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var built = InteractiveProject.Build(optics, catalogs, Who);
        int j = options.Surface;
        if (j < 0 || j >= built.Lens.LastSurfaceIndex + 1)
            throw new ArgumentException($"{Who}: surface {j} is not one of the surfaces 0 to {built.Lens.LastSurfaceIndex} before the image.");
        bool radius = options.Parameter == QuickAdjustParameter.Radius;
        InteractiveProject.CheckOwnCell(optics, catalogs, j, radius ? SavedSurfaces.RadiusColumn : SavedSurfaces.ThicknessColumn, Who);
        var result = Find(built.Lens, built.System, options, cancellation);
        result = result with { Assumptions = result.Assumptions.Concat(built.Notes).ToArray() };
        var s = optics.Surfaces![j];
        var updated = radius
            ? s with { Radius = result.Value == 0 ? null : OpticsConventions.FromMeters(1 / result.Value, built.Unit) }
            : s with { Thickness = s.Thickness + OpticsConventions.FromMeters(result.Value - result.StartValue, built.Unit) };
        return new QuickAdjustApplied(InteractiveProject.WithSurface(optics, j, updated), result);
    }

    private sealed class Problem
    {
        public required SequentialLens Lens { get; init; }
        public required OpticalSystemData System { get; init; }
        public required QuickAdjustOptions Options { get; init; }
        public required PupilQuadratureRule Rule { get; init; }
        public required List<(int Row, double Weight)> Fields { get; init; }
        public required int[] Waves { get; init; }
        public required int Evaluate { get; init; }

        public static Problem Create(SequentialLens lens, OpticalSystemData system, QuickAdjustOptions options)
        {
            ArgumentNullException.ThrowIfNull(lens);
            ArgumentNullException.ThrowIfNull(system);
            ArgumentNullException.ThrowIfNull(options);
            if (!Enum.IsDefined(options.Criterion)) throw new ArgumentException($"{Who}: unknown criterion {(int)options.Criterion}.");
            if (!Enum.IsDefined(options.Parameter)) throw new ArgumentException($"{Who}: unknown parameter {(int)options.Parameter}.");
            int image = lens.ImageIndex, j = options.Surface;
            int evaluate = options.EvaluateSurface ?? image;
            bool radius = options.Parameter == QuickAdjustParameter.Radius;
            if (j < (radius ? 1 : 0) || j >= image)
                throw new ArgumentException($"{Who}: the {(radius ? "radius" : "thickness")} of surface {j} cannot be adjusted; choose a surface from {(radius ? 1 : 0)} to {image - 1}.");
            if (evaluate <= j || evaluate > image)
                throw new ArgumentException($"{Who}: the evaluation surface must come after the adjusted surface {j} and be at most the image ({image}); it is {evaluate}.");
            if (!radius && !double.IsFinite(lens.Surfaces[j].Thickness))
                throw new ArgumentException($"{Who}: the thickness of surface {j} is infinite; give it a finite value first.");
            var rule = RmsCore.Rule(PupilIntegration.GaussianQuadrature, options.Rings, Who);
            var waves = Enumerable.Range(0, system.Wavelengths.Wavelengths.Count).Where(w => system.Wavelengths.Wavelengths[w].Weight > 0).ToArray();
            if (waves.Length == 0) throw new ArgumentException($"{Who}: every wavelength has weight zero, so there is nothing to average.");
            return new Problem
            {
                Lens = lens, System = system, Options = options, Rule = rule,
                Fields = InteractiveSearch.WeightedFields(system, Who), Waves = waves, Evaluate = evaluate,
            };
        }

        public (double Value, string? Failure) At(double value, CancellationToken cancellation)
        {
            int j = Options.Surface;
            bool radius = Options.Parameter == QuickAdjustParameter.Radius;
            var surfaces = Lens.Surfaces.ToArray();
            surfaces[j] = radius ? surfaces[j] with { Curvature = value } : surfaces[j] with { Thickness = value };
            var lens = Lens with { Surfaces = surfaces };
            string at = radius ? $"curvature {StudyReport.Number(value, "g8")} 1/m" : $"thickness {StudyReport.Number(value, "g8")} m";
            RaySample sample;
            try
            {
                sample = RaySampler.Trace(lens, System, new RaySampleRequest
                {
                    Fields = Fields.Select(f => f.Row).ToArray(), Wavelengths = Waves, Pupil = Rule.Points,
                    Surface = Evaluate, MaxDegreeOfParallelism = Options.MaxDegreeOfParallelism,
                }, cancellation);
            }
            catch (InvalidOperationException e) { return (double.NaN, $"surface {j} at {at}: {e.Message}"); }
            bool poly = Waves.Length > 1;
            bool angular = QuickAdjustResult.IsAngular(Options.Criterion);
            var values = new double[Fields.Count];
            for (int fi = 0; fi < Fields.Count; fi++)
            {
                var points = new List<SpotPoint>();
                for (int wi = 0; wi < Waves.Length; wi++)
                {
                    double waveWeight = poly ? System.Wavelengths.Wavelengths[Waves[wi]].Weight : 1;
                    var set = sample.Set(fi, wi).ToArray();
                    if (set.Any(r => !r.Passed))
                        return (double.NaN, $"surface {j} at {at}, field {Fields[fi].Row + 1}, wavelength {Waves[wi] + 1}: Gaussian quadrature needs every ray, but {string.Join("; ", RaySampler.DescribeFailures(set))}.");
                    for (int p = 0; p < set.Length; p++)
                    {
                        var r = set[p];
                        points.Add(angular
                            ? new SpotPoint(r.Direction.X, r.Direction.Y, Rule.Weights[p] * waveWeight)
                            : new SpotPoint(r.Position.X, r.Position.Y, Rule.Weights[p] * waveWeight));
                    }
                }
                var stats = SpotStatistics.Compute(points, SpotReference.Centroid, (0, 0));
                values[fi] = Options.Criterion switch
                {
                    QuickAdjustCriterion.SpotRadius or QuickAdjustCriterion.AngularRadius => stats.Rms,
                    QuickAdjustCriterion.SpotX or QuickAdjustCriterion.AngularX => stats.RmsX,
                    _ => stats.RmsY,
                };
            }
            return (InteractiveSearch.CombineFields(values, Fields.Select(f => f.Weight).ToArray()), null);
        }

        public List<string> Assumptions()
        {
            var c = Options.Criterion;
            var lines = new List<string>
            {
                $"Criterion: {QuickAdjustResult.Label(c)} at surface {Evaluate}{(Evaluate == Lens.ImageIndex ? " (the image)" : "")}, about the centroid (help: \"All criterion use the image centroid for a reference\"), {(Waves.Length > 1 ? "polychromatic over the wavelengths with a positive weight" : "one wavelength")}; the fields with a positive weight combined as √(Σ w·RMS² / Σ w).",
                QuickAdjustResult.IsAngular(c)
                    ? "Angular: the rays' direction cosines L and M in the evaluation surface's local frame, after refraction into it (help: \"computed after refraction into the specified evaluation surface\"), shown in milliradians (direction cosine × 1000; equal to the angle for small angles)."
                    : "Spot: the rays' positions on the evaluation surface in its local frame.",
                Rule.Describe(),
                "Gaussian quadrature assumes the whole pupil is traced: a trial value where any ray fails or is vignetted has no value.",
                "Floating semi-diameters are sized again for every trial value, as the Lens Data Editor would size them.",
            };
            return lines;
        }
    }
}
