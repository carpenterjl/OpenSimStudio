using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Tolerancing;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-67's gate (plan): "A two-stage alignment (centre a group, then focus) reproduces the same
/// result as the equivalent hand-built compensator sequence." The script follows the help's
/// "Tolerance Script Example" (OpticStudio 2025 R2, saved under plans/sources/optics/op-67-help/):
/// CLEARCOMP, LOADMERIT, CPAR, OPTIMIZE, then CLEARCOMP, LOADMERIT, COMP, OPTIMIZE, then the
/// evaluation merit function, whose value is the criterion.
/// <para>
/// The hand-built sequence is written here from the primitives the tolerancing already has: OP-63's
/// perturber and TC-15's aim twin (the same trial lens and twin the engines build), OP-57's local
/// optimizer with the decenter cells flagged Variable, then the back distance flagged Variable,
/// and OP-54's merit evaluator. The comparison is bitwise: both perform the same floating-point
/// operations in the same order on one thread (the script interpreter adds only control flow), so
/// any difference is a difference in what was done, not rounding. Were the variable order or the
/// optimizer's options different, the two would agree only to the optimizer's convergence.
/// </para>
/// </summary>
public sealed class ToleranceScriptGateTests
{
    /// <summary>The image surface of <see cref="Lens"/>.</summary>
    private const int Image = 8;

    /// <summary>
    /// The fixture singlet (R1 100, R2 −100, 5 mm, n 1.5, EPD 4 mm, 0.55 µm; arbitrary inputs) with
    /// its stop in front, mounted between a zero "return target" coordinate break (surface 2), the
    /// compensator break (surface 3, decenters the element) and a break returning to surface 2
    /// (Orientation XYZ: the next surface lands on surface 2's vertex, Conventions §13), so the
    /// image plane (after the dummy surface 7, thickness = centre thickness + the closed-form back
    /// focus) does not move with the compensator.
    /// </summary>
    internal static OpticsSettings Lens()
    {
        static OpticsSurfaceSettings Break(double thickness = 0) => new()
        {
            Type = SurfaceTypes.CoordinateBreak, Thickness = thickness,
            Parameters = Enumerable.Range(1, 6).Select(k => new OpticsSurfaceParameterSettings { Number = k, Value = 0 }).ToList(),
        };
        return new OpticsSettings
        {
            SystemData = new OpticsSystemSettings
            {
                ApertureValue = X.EntrancePupil,
                Fields = new() { new OpticsFieldSettings() },
                Wavelengths = new() { new OpticsWavelengthSettings { Micrometers = X.Micrometers } },
            },
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Thickness = 0, IsStop = true },
                Break(),
                Break(),
                new() { Radius = X.R1, Thickness = X.CentreThickness, Index = X.Index },
                new() { Radius = X.R2, Thickness = 0 },
                Break() with { CoordinateReturn = new OpticsCoordinateReturnSettings { Mode = nameof(CoordinateReturnMode.OrientationXYZ), Surface = 2 } },
                new() { Thickness = X.CentreThickness + X.Seidel(X.R1).BackFocus },
                new(),
            },
        };
    }

    private static OpticsMeritOperandSettings Ray(string code, double py) =>
        new() { Type = code, Int1 = Image, Int2 = 1, Data = new List<double> { 0, 0, 0, py }, Target = 0, Weight = 1 };

    /// <summary>The chief ray at the image, x and y: centred when zero.</summary>
    internal static readonly OpticsMeritFunctionSettings Center = new() { Operands = new() { Ray("REAX", 0), Ray("REAY", 0) } };

    /// <summary>The ±0.7 pupil-zone rays at the image: in focus for that zone when both are zero.</summary>
    internal static readonly OpticsMeritFunctionSettings Focus = new() { Operands = new() { Ray("REAY", 0.7), Ray("REAY", -0.7) } };

    /// <summary>The evaluation: the chief ray (row 1) and the 0.7 and full-aperture rays.</summary>
    internal static readonly OpticsMeritFunctionSettings Evaluation = new() { Operands = new() { Ray("REAY", 0), Ray("REAY", 0.7), Ray("REAY", 1) } };

    internal const string Script = """
        ! the help's example, two stages: centre element 2 with the decenters of break 3, then focus
        NOMINAL OPT_OFF
        CLEARCOMP
        LOADMERIT CENTER.json
        CPAR 3 1
        CPAR 3 2
        OPTIMIZE
        CLEARCOMP
        LOADMERIT FOCUS.json
        COMP 7 0
        OPTIMIZE 0
        CLEARCOMP
        LOADMERIT EVALUATION.json
        REPORT "chief ray y = " 1
        """;

    internal static ToleranceScriptOptions ScriptOptions() => new()
    {
        Name = "ALIGN.TSC", Text = Script,
        MeritFunctions = new Dictionary<string, OpticsMeritFunctionSettings> { ["CENTER"] = Center, ["FOCUS"] = Focus, ["EVALUATION"] = Evaluation },
    };

    /// <summary>A ±0.05 mm element decenter of the lens and a ±1 mm front-radius tolerance.</summary>
    internal static ToleranceTable Table() => X.Table(X.Row("TEDY", 4, 5, -0.05, 0.05), X.Row("TRAD", 4, 0, -1, 1));

    internal static ToleranceRunOptions Run(int? threads) => new()
    {
        Criterion = ToleranceCriterion.UserScript, Fields = ToleranceFieldSet.UserDefined, Script = ScriptOptions(), MaxDegreeOfParallelism = threads,
    };

    /// <summary>The hand-built sequence on one trial: the steps applied to the nominal by the
    /// perturber, stage 1 (decenter compensators, centring merit function), stage 2 (back distance,
    /// focus merit function), then the evaluation merit function.</summary>
    private static double HandBuilt(OpticsSettings nominal, ToleranceTable table, IReadOnlyList<ToleranceStep> steps)
    {
        var catalogs = X.NoCatalogs;
        var trial = ToleranceAim.Apply(new TolerancePerturber(nominal, catalogs), nominal, table, steps);
        var registry = MeritOperandRegistry.CreateDefault();
        int count = nominal.Surfaces!.Count;
        MeritFunction Function(OpticsMeritFunctionSettings mf) =>
            MeritFunctionSettings.FromSettings(SavedLensSurgery.RenumberMerit(mf, trial.Map, count), registry, new List<string>());
        var twinBase = trial.Twin ?? trial.Perturbed;
        int back = trial.Map(7);
        bool focused = false;
        // The aim twin: the trial's, with the back distance copied once it is a compensator (the
        // decenters of break 3 stay at their start, so the twin is axial).
        OpticsSettings Twin(OpticsSettings s)
        {
            if (!focused) return twinBase;
            var list = twinBase.Surfaces!.ToList();
            list[back] = list[back] with { Thickness = s.Surfaces![back].Thickness, ThicknessIsInfinite = s.Surfaces[back].ThicknessIsInfinite };
            return twinBase with { Surfaces = list };
        }
        IMeritConfigurations Configurations(OpticsSettings s) =>
            new AimedConfigurations(new ProjectMeritConfigurations(s, catalogs), new ProjectMeritConfigurations(Twin(s), catalogs));
        OpticsSettings Flagged(OpticsSettings s, params (int Surface, string Column)[] cells)
        {
            var list = s.Surfaces!.Select(x => x.Flags is { Count: > 0 } f
                ? x with { Flags = f.Where(g => g.Kind != SavedSurfaces.VariableFlag).ToList() } : x).ToList();
            foreach (var (j, column) in cells)
                list[j] = list[j] with { Flags = (list[j].Flags ?? new List<OpticsCellFlagSettings>()).Append(new OpticsCellFlagSettings { Column = column, Kind = SavedSurfaces.VariableFlag }).ToList() };
            return s with { Surfaces = list };
        }
        LocalOptimizationOptions Options() => new()
        {
            Algorithm = LocalOptimizationAlgorithm.DampedLeastSquares, Cycles = LocalOptimizationCycles.Automatic, CycleCount = 1,
            MaxDegreeOfParallelism = 1, Configurations = Configurations,
        };

        int cb = trial.Map(3);
        var stage1 = LocalOptimizer.Run(Flagged(trial.Perturbed, (cb, SavedSurfaces.ParameterColumn(1)), (cb, SavedSurfaces.ParameterColumn(2))),
            catalogs, Options(), Function(Center), registry).Optimized;
        focused = true;
        var stage2 = LocalOptimizer.Run(Flagged(stage1, (back, SavedSurfaces.ThicknessColumn)), catalogs, Options(), Function(Focus), registry).Optimized;
        var e = MeritEvaluator.Evaluate(Function(Evaluation), Configurations(stage2), registry, new MeritEvaluationOptions { MaxDegreeOfParallelism = 1 });
        Assert.False(e.HasFailures, string.Join(" ", e.Errors));
        return e.Value;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void ATwoStageAlignmentScript_ReproducesTheHandBuiltCompensatorSequence_InSensitivityAndMonteCarlo()
    {
        var lens = Lens();
        var table = Table();

        // Sensitivity: four trials, each against the hand-built sequence, bit for bit.
        var sensitivity = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, Run(1));
        Assert.Equal(2, sensitivity.Rows.Count);
        foreach (var row in sensitivity.Rows)
            foreach (var (t, value) in new[] { (row.AtMin, row.Min), (row.AtMax, row.Max) })
            {
                Assert.True(t.Succeeded, t.Failure);
                double expected = HandBuilt(lens, table, new[] { new ToleranceStep(row.Row - 1, value) });
                Assert.Equal(expected, t.Criterion);
                // The centring worked: the chief ray (REPORT of evaluation row 1) is on the axis.
                var chief = Assert.Single(t.Script!.Reports);
                Assert.True(Math.Abs(chief.Value!.Value) < 1e-6, $"row {row.Row} {t.Side}: chief ray y {chief.Value:R} mm");
            }
        Assert.Contains("chief ray y = ", sensitivity.NominalScript!.Text);

        // Monte Carlo: four trials with seed 7, the same draws applied by hand.
        var mcOptions = new MonteCarloOptions { Trials = 4, Seed = 7, Statistics = ToleranceStatistics.Uniform };
        var mc = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, Run(1), mcOptions);
        foreach (var t in mc.Trials)
        {
            Assert.True(t.Succeeded, t.Failure);
            var steps = t.Draws.Select(d => new ToleranceStep(d.Row - 1, d.Value, d.DirectionDegrees ?? 90, d.AngleDegrees ?? 0)).ToArray();
            Assert.Equal(HandBuilt(lens, table, steps), t.Criterion);
        }

        // Trials in parallel give the same bits as one thread.
        var parallel = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, Run(null), mcOptions);
        Assert.Equal(mc.Trials.Select(t => t.Criterion), parallel.Trials.Select(t => t.Criterion));
        var parallelSensitivity = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, Run(null));
        Assert.Equal(sensitivity.Rows.Select(r => (r.AtMin.Criterion, r.AtMax.Criterion)), parallelSensitivity.Rows.Select(r => (r.AtMin.Criterion, r.AtMax.Criterion)));
    }
}
