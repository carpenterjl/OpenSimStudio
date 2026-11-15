using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.UniversalPlot;
using OpenSim.Optics.Merit;
using G = OpenSim.Tests.Optics.Gates.UniversalPlotGateTests;
using F = OpenSim.Tests.Optics.LocalOptimizationFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-45: Universal Plot 1D and 2D beyond the gate (<see cref="Gates.UniversalPlotGateTests"/>):
/// solves evaluated again at every point, system and configuration data, the merit function and
/// its rows, refusals by name, threads, registration and the command line. The lenses are test
/// inputs (constant indices, no glass catalog); expected values are derived in each test.
/// </summary>
public sealed class UniversalPlotTests
{
    private static OpticsAnalysisContext Context(OpticsSettings system) => new("P", system);

    private static UniversalPlot1DSettings Radius1(double start = 20, double stop = 200, int steps = 5) => new()
    {
        Category = UniversalPlotCategory.Surface, Parameter = UniversalPlotParameter.Radius, Surface = 1,
        Start = start, Stop = stop, Steps = steps, Operand = "EFFL", Int2 = 1,
    };

    private static OpticsAnalysisResult Run1D(OpticsSettings system, UniversalPlot1DSettings settings, int? threads = null) =>
        new UniversalPlot1DAnalysis { MaxDegreeOfParallelism = threads }.Compute(Context(system), settings, default);

    private static AnalysisCurve Curve(OpticsAnalysisResult result) => Assert.IsType<LinePlot>(result.Plots[0]).Curves.Single();

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected)), $"{what}: {actual:R} against {expected:R}");

    /// <summary>
    /// The singlet's back distance is a marginal-ray-height solve (height 0: the paraxial focus).
    /// Sweeping R₁ and plotting that thickness (CTVA, Surf 2) shows the solve evaluated again at each
    /// point: it must be the thick-lens back focal distance. Derived with the ray of the gate's
    /// comment: after surface 2 the height is y₂ = 1 − tφ₁/n and the slope u₂′ = −φ, so the ray
    /// meets the axis at BFD = −y₂/u₂′ = (1 − tφ₁/n)/φ. The solve is the same paraxial algebra
    /// (one step, no iteration), so it agrees to rounding; 1e-10 relative is allowed.
    /// </summary>
    [Fact]
    public void ASolveDrivenValue_IsEvaluatedAgainAtEveryPoint()
    {
        var lens = G.Singlet();
        lens = lens with
        {
            Surfaces = lens.Surfaces!.Select((s, j) => j == 2 ? s with { Flags = new() { F.Solve("Thickness", "Marginal ray height", 0, 0) } } : s).ToList(),
        };
        var result = Run1D(lens, Radius1() with { Operand = "CTVA", Int1 = 2, Int2 = 0 });
        var curve = Curve(result);
        for (int i = 0; i < curve.X.Count; i++)
        {
            double r1 = curve.X[i], phi1 = 0.5 / r1, phi2 = -0.5 / -100.0;
            double phi = phi1 + phi2 - 5 / 1.5 * phi1 * phi2;
            Close((1 - 5 / 1.5 * phi1) / phi, curve.Y[i], 1e-10, $"back focal distance at R1 = {r1}");
        }
        // The caller's record is not changed.
        Assert.Equal(50, lens.Surfaces![1].Radius);

        // Sweeping the solved cell itself is refused: the solve would overwrite every value.
        var ex = Assert.Throws<ArgumentException>(() => Run1D(lens, Radius1() with { Parameter = UniversalPlotParameter.Thickness, Surface = 2, Start = 10, Stop = 20 }));
        Assert.Contains("Surface 2 thickness is driven by a Marginal ray height solve", ex.Message);
    }

    /// <summary>Curvature is 1/R: the same focal lengths as the radius sweep at R = 1/c.</summary>
    [Fact]
    public void ACurvatureSweep_IsTheRadiusSweepAtOneOverC()
    {
        var curve = Curve(Run1D(G.Singlet(), Radius1() with { Parameter = UniversalPlotParameter.Curvature, Start = 0.01, Stop = 0.05 }));
        for (int i = 0; i < curve.X.Count; i++)
            Close(G.ThickLensFocalLength(1 / curve.X[i], -100), curve.Y[i], 1e-12, $"EFFL at c = {curve.X[i]}");
    }

    /// <summary>System data reaches the evaluation: the paraxial entrance pupil diameter (EPDI) of a
    /// lens whose stop is its first surface, object at infinity, is the aperture value itself.</summary>
    [Fact]
    public void ASystemDataSweep_ReachesTheEvaluation()
    {
        var settings = new UniversalPlot1DSettings
        {
            Category = UniversalPlotCategory.System, Parameter = UniversalPlotParameter.ApertureValue, Start = 1, Stop = 9, Steps = 5, Operand = "EPDI", Int2 = 0,
        };
        var result = Run1D(G.Singlet(), settings);
        var curve = Curve(result);
        for (int i = 0; i < curve.X.Count; i++) Close(curve.X[i], curve.Y[i], 1e-12, $"EPDI at aperture {curve.X[i]}");
        Assert.Contains("Aperture value (EntrancePupilDiameter) (mm)", result.Text);

        // A wavelength made invalid by the sweep fails that point by name; the others are values.
        var waves = Run1D(G.Singlet(), new UniversalPlot1DSettings
        {
            Category = UniversalPlotCategory.System, Parameter = UniversalPlotParameter.Wavelength, Number = 1, Start = -0.5, Stop = 0.6, Steps = 2, Operand = "EFFL", Int2 = 1,
        });
        var c = Curve(waves);
        Assert.True(double.IsNaN(c.Y[0]));
        Assert.True(double.IsFinite(c.Y[1]));
        Assert.Contains("Point 1, Wavelength 1 = -0.5 µm: the system data is not valid", waves.Text);
    }

    private static OpticsSettings WithThicknessTable(OpticsSettings lens) => lens with
    {
        MultiConfiguration = new OpticsMultiConfigSettings
        {
            ConfigurationCount = 2,
            Operands = new() { new OpticsConfigOperandSettings { Type = "THIC", Number1 = 2, Values = new() { new() { Value = 40 }, new() { Value = 60 } } } },
        },
    };

    /// <summary>A cell of the multi-configuration table: row 1 (THIC of surface 2) in configuration
    /// 1, the current one, read back by CTVA. A sweep of surface 2's thickness itself, which that row
    /// overwrites, is refused naming the row.</summary>
    [Fact]
    public void AConfigurationCell_IsSwept_AndASurfaceCellTheTableSets_IsRefusedNamingTheRow()
    {
        var lens = WithThicknessTable(G.Singlet());
        var settings = new UniversalPlot1DSettings
        {
            Category = UniversalPlotCategory.Configuration, Parameter = UniversalPlotParameter.Operand, Number = 1, Surface = 1,
            Start = 10, Stop = 30, Steps = 3, Operand = "CTVA", Int1 = 2, Int2 = 0,
        };
        var curve = Curve(Run1D(lens, settings));
        for (int i = 0; i < 3; i++) Close(10.0 * (i + 1), curve.Y[i], 1e-12, $"CTVA at point {i + 1}");

        var ex = Assert.Throws<ArgumentException>(() => Run1D(lens, settings with
        {
            Category = UniversalPlotCategory.Surface, Parameter = UniversalPlotParameter.Thickness, Surface = 2,
        }));
        Assert.Contains("multi-configuration row 1 (THIC)", ex.Message);
        Assert.Contains("Number 1, Surface 1", ex.Message);

        // Surface 1's radius is not in the table, so it sweeps as without one.
        Assert.All(Curve(Run1D(lens, Radius1())).Y, y => Assert.True(double.IsFinite(y)));
    }

    /// <summary>Merit with Line 0 is the merit function's value, √(W(V − T)²/W) = |EFFL − 100| for
    /// the one EFFL row of target 100 and weight 1; Line 1 is that row's value, the focal length.</summary>
    [Fact]
    public void TheMeritFunction_AndOneOfItsRows()
    {
        var lens = G.Singlet() with
        {
            MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new[] { MeritOperandRow.Of("EFFL", 0, 1, target: 100, weight: 1) })),
        };
        var value = Curve(Run1D(lens, Radius1() with { Operand = "Merit", Line = 0 }));
        var row = Curve(Run1D(lens, Radius1() with { Operand = "merit", Line = 1 }));
        for (int i = 0; i < value.X.Count; i++)
        {
            double f = G.ThickLensFocalLength(value.X[i], -100);
            Close(f, row.Y[i], 1e-12, $"row 1 at R1 = {value.X[i]}");
            Close(Math.Abs(f - 100), value.Y[i], 1e-12, $"merit at R1 = {value.X[i]}");
        }
        Assert.Contains("Line 2 is not a row", Assert.Throws<ArgumentException>(() => Run1D(lens, Radius1() with { Operand = "Merit", Line = 2 })).Message);
        Assert.Contains("merit function is empty", Assert.Throws<ArgumentException>(() => Run1D(G.Singlet(), Radius1() with { Operand = "Merit" })).Message);
    }

    [Fact]
    public void WhatCannotBeSwept_OrEvaluated_IsRefusedByName()
    {
        var lens = G.Singlet();
        string Refusal(UniversalPlot1DSettings s) => Assert.Throws<ArgumentException>(() => Run1D(lens, s)).Message;
        Assert.Contains("non-sequential data", Refusal(Radius1() with { Category = UniversalPlotCategory.NonSequential }));
        Assert.Contains("Radius is not system data", Refusal(Radius1() with { Category = UniversalPlotCategory.System }));
        Assert.Contains("Wavelength is not surface data", Refusal(Radius1() with { Parameter = UniversalPlotParameter.Wavelength }));
        Assert.Contains("no multi-configuration table", Refusal(Radius1() with { Category = UniversalPlotCategory.Configuration, Parameter = UniversalPlotParameter.Operand }));
        Assert.Contains("surface 7 does not exist", Refusal(Radius1() with { Surface = 7 }));
        Assert.Contains("field 4 does not exist", Refusal(Radius1() with { Category = UniversalPlotCategory.System, Parameter = UniversalPlotParameter.FieldY, Number = 4 }));
        Assert.Contains("has no parameter 1", Refusal(Radius1() with { Parameter = UniversalPlotParameter.Parameter, Number = 1 }));
        Assert.Contains("at least 2", Refusal(Radius1() with { Steps = 1 }));
        Assert.Contains("give a range", Refusal(Radius1() with { Start = 5, Stop = 5 }));
        Assert.Contains("ZZZZ", Refusal(Radius1() with { Operand = "ZZZZ" }));
        Assert.Contains("reads other rows", Refusal(Radius1() with { Operand = "DIFF" }));
        Assert.Contains("control operand", Refusal(Radius1() with { Operand = "BLNK" }));
        Assert.Contains("choose an operand", Refusal(Radius1() with { Operand = " " }));

        var two = new UniversalPlot2DSettings { XParameter = UniversalPlotParameter.Radius, YParameter = UniversalPlotParameter.Curvature, Operand = "EFFL" };
        string Refusal2(UniversalPlot2DSettings s) =>
            Assert.Throws<ArgumentException>(() => new UniversalPlot2DAnalysis().Compute(Context(lens), s, default)).Message;
        Assert.Contains("write the same cell", Refusal2(two));
        Assert.Contains("GreyScale is not built", Refusal2(two with { YParameter = UniversalPlotParameter.Conic, ShowAs = UniversalPlotDisplay.GreyScale }));
        Assert.Contains("Surface is not built", Refusal2(two with { YParameter = UniversalPlotParameter.Conic, ShowAs = UniversalPlotDisplay.Surface }));
    }

    /// <summary>Every point fails (the pupil-edge ray misses surface 1 for every R₁ below 1 mm): the
    /// analysis fails, naming each point by its radius.</summary>
    [Fact]
    public void WhenNoPointCanBeComputed_TheAnalysisFails_NamingEachPoint()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Run1D(G.Singlet(),
            Radius1(0.5, 0.9, 3) with { Operand = "REAY", Int1 = 3, Int2 = 1, Data4 = 1 }));
        Assert.Contains("any of the 3 points", ex.Message);
        foreach (string r in new[] { "0.5", "0.7", "0.9" }) Assert.Contains($"Surface 1 radius = {r} mm", ex.Message);
    }

    [Fact]
    public void TheSweep_IsBitwiseTheSameAtAnyThreadCount()
    {
        var settings = Radius1(0.8, 8, 9) with { Operand = "REAY", Int1 = 3, Int2 = 1, Data4 = 1 };
        var one = Curve(Run1D(G.Singlet(), settings, threads: 1)).Y;
        foreach (int? threads in new int?[] { 3, null })
        {
            var other = Curve(Run1D(G.Singlet(), settings, threads)).Y;
            Assert.Equal(one.Select(BitConverter.DoubleToInt64Bits), other.Select(BitConverter.DoubleToInt64Bits));
        }
    }

    [Fact]
    public void BothPlots_AreRegistered_AndReachedByTheCommandLine()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(UniversalPlot1DAnalysis.SavedId, out var one));
        Assert.True(registry.TryGet(UniversalPlot2DAnalysis.SavedId, out var two));
        Assert.Equal("Universal plot", one.Category);
        Assert.Equal("Universal plot", two.Category);

        // The settings parse from --set strings (an enum by name, a string, numbers).
        var parsed = (UniversalPlot1DSettings)OpticsBatch.WithOverrides(one, one.CreateDefaultSettings(), new KeyValuePair<string, string>[]
        {
            new("Parameter", "Radius"), new("Start", "20"), new("Stop", "200"), new("Steps", "4"), new("Operand", "EFFL"), new("Int2", "1"),
        });
        Assert.Equal((UniversalPlotParameter.Radius, 20.0, 200.0, 4, "EFFL"), (parsed.Parameter, parsed.Start, parsed.Stop, parsed.Steps, parsed.Operand));

        string dir = Path.Combine(Path.GetTempPath(), "opensim-universal-plot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "singlet.ossproj");
            new ProjectSerializer().Save(new SimProject { Name = "Singlet", Optics = G.Singlet() }, path);
            var stdout = new StringWriter();
            int code = CliRunner.Run(new[]
            {
                "optics", "analyse", path, "--analysis", UniversalPlot1DAnalysis.SavedId, "--builtin-catalogs",
                "--set", "Parameter=Radius;Surface=1;Start=20;Stop=200;Steps=4;Operand=EFFL;Int2=1",
            }, stdout, new StringWriter(), CancellationToken.None);
            Assert.True(code == 0, stdout.ToString());
            string csv = File.ReadAllText(Path.Combine(dir, "singlet-optics-analyse", UniversalPlot1DAnalysis.SavedId + "-plot1.csv"));
            Assert.Contains("200", csv);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
