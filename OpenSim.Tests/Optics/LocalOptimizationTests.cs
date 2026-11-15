using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Paraxial;
using F = OpenSim.Tests.Optics.LocalOptimizationFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-57: the lens variables, the optimizer's refusals, multi-configuration variables, exact
/// (negative-weight) constraints, the cycle modes and undo. The gate is in
/// <see cref="Gates.LocalOptimizationGateTests"/>. Targets are made by construction in each test
/// (a focal length computed from the same lens at a known value), never typed from a source.
/// </summary>
public sealed class LocalOptimizationTests
{
    private static double Relative(double expected, double actual) => Math.Abs(actual - expected) / Math.Abs(expected);

    private static OpticsSettings WithMerit(OpticsSettings optics, params MeritOperandRow[] rows) =>
        optics with { MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(rows)) };

    // ------------------------------------------------------------------ variables

    [Fact]
    public void Variables_AreFoundInOrder_AndARadiusIsVariedAsItsCurvature()
    {
        var optics = F.Singlet(r1: 80) with { };
        var surfaces = optics.Surfaces!.ToList();
        surfaces[2] = surfaces[2] with
        {
            Conic = -0.5,
            Flags = new() { F.Variable("Conic"), F.Solve("Thickness", "Marginal ray height", 0, 0), F.Variable("Radius") },
        };
        optics = optics with { Surfaces = surfaces };
        var variables = LensVariables.Find(optics);
        Assert.Equal(new[] { LensVariableKind.Curvature, LensVariableKind.Conic, LensVariableKind.Curvature }, variables.Select(v => v.Kind));
        Assert.Equal(new[] { 1, 2, 2 }, variables.Select(v => v.Surface));
        var values = LensVariables.Values(optics, variables);
        Assert.Equal(1 / 80.0, values[0]);
        Assert.Equal(-0.5, values[1]);
        Assert.Equal(1 / -100.0, values[2]);

        // A curvature of zero is a plane (no radius); the flags stay.
        var flat = LensVariables.Apply(optics, variables, new[] { 0.0, -1, 0.02 });
        Assert.Null(flat.Surfaces![1].Radius);
        Assert.Equal(-1, flat.Surfaces[2].Conic);
        Assert.Equal(50, flat.Surfaces[2].Radius!.Value, 12);
        Assert.Equal(3, flat.Surfaces[2].Flags!.Count);
        Assert.Equal(0, LensVariables.Values(flat, variables)[0]);
    }

    [Fact]
    public void ParameterAndConfigurationVariables_ReadAndWrite()
    {
        var optics = new OpticsSettings
        {
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new()
                {
                    Type = "Even Asphere", Radius = 50, Thickness = 4, Index = 1.5, IsStop = true,
                    Parameters = new() { new() { Number = 2, Value = 1e-6 } },
                    Flags = new() { F.Variable("Par2") },
                },
                new() { Radius = -50, Thickness = 90 },
                new(),
            },
            MultiConfiguration = new()
            {
                ConfigurationCount = 2,
                Operands = new() { new() { Type = "THIC", Number1 = 2, Values = new() { new() { Value = 90 }, new() { Value = 80, Solve = "Variable" } } } },
            },
        };
        var variables = LensVariables.Find(optics);
        Assert.Equal(new[] { LensVariableKind.Parameter, LensVariableKind.Configuration }, variables.Select(v => v.Kind));
        Assert.Equal(new MultiConfigCell(1, 2), variables[1].Cell);
        Assert.Equal(new[] { 1e-6, 80 }, LensVariables.Values(optics, variables));
        var changed = LensVariables.Apply(optics, variables, new[] { 2e-6, 70 });
        Assert.Equal(new[] { 2e-6, 70 }, LensVariables.Values(changed, variables));
        Assert.Equal("Variable", changed.MultiConfiguration!.Operands![0].Values![1].Solve);
        Assert.Equal(90, changed.MultiConfiguration.Operands[0].Values![0].Value);
    }

    [Fact]
    public void CellsThatCannotBeVaried_AreRefusedByName()
    {
        var optics = F.Singlet();
        var surfaces = optics.Surfaces!.ToList();
        surfaces[1] = surfaces[1] with { Flags = new() { F.Variable("Material"), F.Variable("SemiDiameter") } };
        surfaces[0] = surfaces[0] with { Flags = new() { F.Variable("Thickness") } };
        var e = Assert.Throws<ArgumentException>(() => LensVariables.Find(optics with { Surfaces = surfaces }));
        Assert.Contains("the Material cell is not varied itself", e.Message);   // OP-59: glasses vary through their own flags
        Assert.Contains("automatic", e.Message);
        Assert.Contains("infinite thickness", e.Message);
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void TheOptimizer_RefusesWithoutVariablesOrMeritRows_OrWhenTheStartFails()
    {
        Assert.Contains("no variables", Assert.Throws<ArgumentException>(() => LocalOptimizer.Run(F.Singlet(r1Variable: false), F.NoCatalogs)).Message);
        Assert.Contains("no rows", Assert.Throws<ArgumentException>(() =>
            LocalOptimizer.Run(F.Singlet() with { MeritFunction = null }, F.NoCatalogs)).Message);
        // REAY on surface 9 of a four-surface lens cannot be computed: the run cannot begin.
        var failing = WithMerit(F.Singlet(), MeritOperandRow.Of("REAY", 9, 1, 0, 1, 0, 0, 0, 1));
        Assert.Contains("cannot begin", Assert.Throws<InvalidOperationException>(() => LocalOptimizer.Run(failing, F.NoCatalogs)).Message);
    }

    // ------------------------------------------------------------------ multi-configuration

    /// <summary>A two-configuration zoom: configuration 2's gap is variable and the merit function
    /// asks, after CONF 2, for the focal length the same lens has with a 25 mm gap (computed
    /// here from the single-configuration lens). One variable, one row with a root: the
    /// optimizer finds the gap to rounding; 1e-9 relative leaves room for the focal length's
    /// slope.</summary>
    [Fact]
    public void AConfigurationCell_IsOptimized()
    {
        static List<OpticsSurfaceSettings> Zoom(double gap) => new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true },
            new() { Radius = -50, Thickness = gap },
            new() { Radius = -30, Thickness = 3, Index = 1.6 },
            new() { Radius = 80, Thickness = 60 },
            new(),
        };
        var separate = new OpticsSettings { Surfaces = Zoom(25) };
        var lens = OpenSim.Optics.Analysis.ProjectLens.From(separate, F.NoCatalogs)!;
        double target = ParaxialSystem.Create(lens.ToParaxialLens(), SystemDataSettings.FromSettings(null, new List<string>()))
            .EffectiveFocalLength!.Value * 1e3;   // mm

        var optics = WithMerit(new OpticsSettings
        {
            Surfaces = Zoom(99),
            MultiConfiguration = new()
            {
                ConfigurationCount = 2,
                Operands = new() { new() { Type = "THIC", Number1 = 2, Values = new() { new() { Value = 10 }, new() { Value = 18, Solve = "Variable" } } } },
            },
        }, MeritOperandRow.Of("CONF", 2), MeritOperandRow.Of("EFFL", 0, 1, target: target, weight: 1));
        var result = LocalOptimizer.Run(optics, F.NoCatalogs);
        Assert.Equal(LocalOptimizationStop.Converged, result.Stop);
        Assert.True(Relative(25, result.Values[0]) < 1e-9, $"gap {result.Values[0]:R}");
        Assert.Equal(10, result.Optimized.MultiConfiguration!.Operands![0].Values![0].Value);   // configuration 1 untouched
    }

    // ------------------------------------------------------------------ exact constraints

    /// <summary>Both radii of the singlet vary; the focal length row has weight −1, an exact
    /// constraint in OpticStudio's sense. The run must meet it to the constraint tolerance
    /// (1e-9 × 100 mm) while lowering the spherical aberration.</summary>
    [Fact]
    public void ANegativeWeightRow_IsMetExactly()
    {
        var singlet = F.Singlet(r1: 80);
        var surfaces = singlet.Surfaces!.ToList();
        surfaces[2] = surfaces[2] with { Flags = new() { F.Variable("Radius"), F.Solve("Thickness", "Marginal ray height", 0, 0) } };
        var optics = WithMerit(singlet with { Surfaces = surfaces },
            MeritOperandRow.Of("REAY", 3, 1, 0, 1, 0, 0, 0, 1),
            MeritOperandRow.Of("EFFL", 0, 1, target: 100, weight: -1));
        var result = LocalOptimizer.Run(optics, F.NoCatalogs);
        Assert.Equal(LocalOptimizationStop.Converged, result.Stop);
        var effl = result.Evaluation.Rows[1];
        Assert.Equal(MeritRowStatus.Evaluated, effl.Status);
        Assert.True(Math.Abs(effl.Value - 100) <= 1e-9 * 100, $"EFFL {effl.Value:R}");
        Assert.True(double.IsFinite(result.Merit));
        Assert.Contains("negative weight", result.Log[0]);
    }

    // ------------------------------------------------------------------ cycles

    [Fact]
    public void FixedCycles_StopAtTheCount()
    {
        var result = LocalOptimizer.Run(F.Doublet(), F.NoCatalogs, new LocalOptimizationOptions { Cycles = LocalOptimizationCycles.Fixed, CycleCount = 2 });
        Assert.True(result.Cycles <= 2);
        Assert.Contains(result.Stop, new[] { LocalOptimizationStop.CycleLimit, LocalOptimizationStop.Converged });
        Assert.True(result.Merit < result.StartMerit);
    }

    /// <summary>Infinite cycles run until Stop (here a timer) and keep the best design.</summary>
    [Fact]
    public void InfiniteCycles_RunUntilCancelled()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var result = LocalOptimizer.Run(F.Singlet(), F.NoCatalogs, new LocalOptimizationOptions
        {
            Cycles = LocalOptimizationCycles.Infinite, CancellationToken = cancel.Token,
        });
        Assert.Equal(LocalOptimizationStop.Cancelled, result.Stop);
        Assert.True(result.Merit < result.StartMerit);
    }

    /// <summary>A derivative increment per type changes only the differencing: the singlet ends
    /// at the same bending to the resolution of its flat minimum (see the gate's comment).</summary>
    [Fact]
    public void ADerivativeIncrementPerType_IsUsed()
    {
        var plain = LocalOptimizer.Run(F.Singlet(), F.NoCatalogs);
        var stepped = LocalOptimizer.Run(F.Singlet(), F.NoCatalogs, new LocalOptimizationOptions
        {
            Kinds = new Dictionary<LensVariableKind, LensVariableKindSettings>
            {
                [LensVariableKind.Curvature] = new() { DerivativeIncrement = 1e-8 },
            },
        });
        Assert.True(Relative(plain.Values[0], stepped.Values[0]) < 1e-3);
        Assert.Throws<ArgumentException>(() => LocalOptimizer.Run(F.Singlet(), F.NoCatalogs, new LocalOptimizationOptions
        {
            Kinds = new Dictionary<LensVariableKind, LensVariableKindSettings> { [LensVariableKind.Curvature] = new() { Scale = 0 } },
        }));
    }

    // ------------------------------------------------------------------ undo

    [Fact]
    public void Undo_RestoresTheSavedCells_AndRefusesWhenTheVariablesChanged()
    {
        var start = F.Doublet();
        var result = LocalOptimizer.Run(start, F.NoCatalogs, new LocalOptimizationOptions { Cycles = LocalOptimizationCycles.Fixed, CycleCount = 2 });
        Assert.NotEqual(start.Surfaces![2].Radius, result.Optimized.Surfaces![2].Radius);
        var undone = result.UndoOn(result.Optimized);
        Assert.Equal(start.Surfaces[2].Radius, undone.Surfaces![2].Radius);   // the saved digits, not 1/(1/R)
        Assert.Equal(start.Surfaces[3].Radius, undone.Surfaces[3].Radius);

        var surfaces = result.Optimized.Surfaces.ToList();
        surfaces[1] = surfaces[1] with { Flags = new() { F.Variable("Thickness") } };
        Assert.Throws<ArgumentException>(() => result.UndoOn(result.Optimized with { Surfaces = surfaces }));
    }
}
