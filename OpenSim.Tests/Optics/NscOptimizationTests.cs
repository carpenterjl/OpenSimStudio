// OP-88
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis.ImageSimulation;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Merit.Catalogue;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.Optimization;
using Xunit;

namespace OpenSim.Tests.Optics;

/// <summary>
/// Illumination optimization (plan item OP-88; docs/Optics-Conventions.md §76): the
/// non-sequential merit operands, the fixed random streams, the variables on object cells, the
/// non-sequential Optimization and Bitmap Wizards, and the gate — a source's distance from a
/// detector optimised to the closed-form position for a target irradiance.
/// <para>
/// The geometry of the irradiance tests is derived here. A Source Point with a 90° semi-cone
/// emits P uniformly in solid angle over the hemisphere (OP-77's reading, Conventions §72), so
/// its intensity is P/2π. A square detector of half width a centred on the axis at distance d
/// subtends Ω(d) = ∫∫ d dx dy/(x² + y² + d²)^{3/2} = 4 arctan(a²/(d√(2a² + d²))) (derived: the
/// inner integral over y gives 2a d/((x² + d²)√(x² + a² + d²)), and the outer one an arctangent;
/// checked against a midpoint quadrature in <see cref="SolidAngleOfASquare_MatchesQuadrature"/>).
/// The detector's average irradiance is Ē = PΩ/(2π·4a²); inverted, with t = tan(Ω/4),
/// d = a√(√(1 + 1/t²) − 1). With random sampling each of N rays lands on the detector with
/// probability f = Ω/2π, so the fraction landing has the binomial standard deviation
/// √(f(1 − f)/N).
/// </para>
/// </summary>
public class NscOptimizationTests
{
    private static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    private static void Within(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    private static double SolidAngle(double a, double d) => 4 * Math.Atan(a * a / (d * Math.Sqrt(2 * a * a + d * d)));

    private static double DistanceFor(double a, double omega)
    {
        double t = Math.Tan(omega / 4);
        return a * Math.Sqrt(Math.Sqrt(1 + 1 / (t * t)) - 1);
    }

    /// <summary>A pure non-sequential system (no sequential surfaces, millimetres): a 1 W Source
    /// Point (90° semi-cone, <paramref name="rays"/> random analysis rays) at z = <paramref name="sourceZ"/>
    /// and a Detector Rectangle of half width <paramref name="halfWidth"/> mm, 10 × 10 pixels, at z = 0.</summary>
    private static OpticsSettings PointAndDetector(double sourceZ, int rays, double halfWidth = 20, bool variable = false) => new()
    {
        NonSequential = new OpticsNonSequentialSettings
        {
            Objects = new()
            {
                new OpticsNscObjectSettings
                {
                    Type = "Source Point", Z = sourceZ, Parameters = new() { 10, rays, 1, 0, 0, 90 },
                    Source = new OpticsNscSourceSettings { Sampling = "Random" },
                    Flags = variable ? new() { new OpticsCellFlagSettings { Column = "Z", Kind = "Variable" } } : null,
                },
                new OpticsNscObjectSettings { Type = "Detector Rectangle", Parameters = new() { halfWidth, halfWidth, 10, 10 } },
            },
        },
    };

    private static MeritOperandRow Row(string code, int int1, int int2, double target = 0, double weight = 0, params double[] data) =>
        MeritOperandRow.Of(code, int1, int2, target, weight, data);

    /// <summary>Clear, trace, read the detector's average irradiance (NSDD Pix# 0, Data 1).</summary>
    private static MeritFunction IrradianceMerit(double target) => new(new[]
    {
        Row("NSDD", 1, 0),
        Row("NSTR", 1, 0),
        Row("NSDD", 1, 2, target, 1, 0, 1),
    });

    private static MeritEvaluation Evaluate(OpticsSettings optics, MeritFunction function, int? threads = null) =>
        MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(optics, NoCatalogs), options: new MeritEvaluationOptions { MaxDegreeOfParallelism = threads });

    [Fact]
    public void SolidAngleOfASquare_MatchesQuadrature()
    {
        // Midpoint rule on 2000 × 2000 cells of the derived integrand: its error is O(h²) of a
        // smooth integrand, about 1e-7 relative here; 1e-6 is the allowance.
        double a = 1, d = 1.3;
        int n = 2000;
        double h = 2 * a / n, sum = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double x = -a + (i + 0.5) * h, y = -a + (j + 0.5) * h;
                sum += d / Math.Pow(x * x + y * y + d * d, 1.5);
            }
        Within(SolidAngle(a, d), sum * h * h, 1e-6 * SolidAngle(a, d), "solid angle of a square");
        Within(d, DistanceFor(a, SolidAngle(a, d)), 1e-12, "the inverse of the closed form");
    }

    /// <summary>NSDD Data 1 after NSTR reads the average irradiance PΩ/(2π·4a²) within five
    /// binomial standard deviations (20 000 random rays, a fixed seed), in a system with no
    /// sequential surfaces; the clearing and trace rows are 0.</summary>
    [Fact]
    public void TraceAndDetectorRows_ReadTheClosedFormIrradiance_WithoutASequentialLens()
    {
        const int n = 20_000;
        double a = 0.020, d = 0.020;
        double omega = SolidAngle(a, d), f = omega / (2 * Math.PI);
        double expected = f / (4 * a * a);
        double sigma = Math.Sqrt(f * (1 - f) / n) / (4 * a * a);
        var e = Evaluate(PointAndDetector(-20, n), IrradianceMerit(0));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        Assert.Equal(0, e.Rows[0].Value);
        Assert.Equal(0, e.Rows[1].Value);
        Within(expected, e.Rows[2].Value, 5 * sigma, "average irradiance, W/m²");
    }

    /// <summary>The merit function is repeatable: the same design gives the same bits on every
    /// evaluation (NSTR's seed is fixed, NscMeritRandom) and at any thread count.</summary>
    [Fact]
    public void TheMeritFunction_IsRepeatable_AndThreadIndependent()
    {
        var optics = PointAndDetector(-25, 5000);
        var function = IrradianceMerit(100);
        var first = Evaluate(optics, function);
        var second = Evaluate(optics, function);
        var single = Evaluate(optics, function, threads: 1);
        Assert.Equal(BitConverter.DoubleToInt64Bits(first.Rows[2].Value), BitConverter.DoubleToInt64Bits(second.Rows[2].Value));
        Assert.Equal(BitConverter.DoubleToInt64Bits(first.Rows[2].Value), BitConverter.DoubleToInt64Bits(single.Rows[2].Value));
        Assert.Equal(BitConverter.DoubleToInt64Bits(first.Value), BitConverter.DoubleToInt64Bits(second.Value));
        Assert.Equal(NscMeritRandom.SeedOf(0), NscMeritRandom.SeedOf(0));
        Assert.NotEqual(NscMeritRandom.SeedOf(0), NscMeritRandom.SeedOf(1));
    }

    [Fact]
    public void Nstr_NeedsAClearingRowBeforeIt_AndClearingEmptiesADetector()
    {
        var optics = PointAndDetector(-20, 2000);
        var noClear = Evaluate(optics, new MeritFunction(new[] { Row("NSTR", 1, 0), Row("NSDD", 1, 2, 0, 1, 0, 0) }));
        Assert.Equal(MeritRowStatus.Failed, noClear.Rows[0].Status);
        Assert.Contains("Det# set equal to 0", noClear.Rows[0].Error);

        var cleared = Evaluate(optics, new MeritFunction(new[]
        {
            Row("NSDD", 1, 0), Row("NSTR", 1, 0), Row("NSDD", 1, 2, 0, 0, 0, 0), Row("NSDD", 1, -2), Row("NSDD", 1, 2, 0, 0, 0, 0),
            Row("NSTR", 1, 0), Row("NSTR", 1, 0), Row("NSDD", 1, 2, 0, 0, 0, 0),
        }));
        Assert.False(cleared.HasFailures, string.Join("; ", cleared.Errors));
        Assert.True(cleared.Rows[2].Value > 0);
        Assert.Equal(0, cleared.Rows[4].Value);   // detector 2 cleared
        // Two more traces after clearing, each with its own stream: about twice one trace, never the same rays twice over.
        Assert.True(cleared.Rows[7].Value > 1.5 * cleared.Rows[2].Value && cleared.Rows[7].Value < 2.5 * cleared.Rows[2].Value);

        var surf = Evaluate(optics, new MeritFunction(new[] { Row("NSDD", 2, 0) }));
        Assert.Contains("OP-86", surf.Rows[0].Error);
        // wire-16: Scat? (Data2) turns OP-80's surface scattering on. No face of this scene scatters,
        // so the tracer draws no scatter numbers and the trace is the same as with Scat? 0.
        var scatter = Evaluate(optics, new MeritFunction(new[] { Row("NSDD", 1, 0), Row("NSTR", 1, 0, 0, 0, 0, 1), Row("NSDD", 1, 2, 0, 0, 0, 0) }));
        Assert.False(scatter.HasFailures, string.Join("; ", scatter.Errors));
        Assert.Equal(cleared.Rows[2].Value, scatter.Rows[2].Value);
        var notDetector = Evaluate(optics, new MeritFunction(new[] { Row("NSDD", 1, 1, 0, 0, 0, 0) }));
        Assert.Contains("not a detector object", notDetector.Rows[0].Error);
    }

    /// <summary>NSDD Data 3 is a pixel's flux over the peak of the consecutive Data 3 rows of the
    /// same detector: the largest is 1 and each equals its pixel's flux (Data 0) over that peak.</summary>
    [Fact]
    public void NormalizedFlux_IsOverThePeakOfTheConsecutiveRows()
    {
        var optics = PointAndDetector(-20, 20_000);
        var rows = new List<MeritOperandRow> { Row("NSDD", 1, 0), Row("NSTR", 1, 0) };
        int[] pixels = { 1, 12, 45, 55, 100 };
        foreach (int p in pixels) rows.Add(Row("NSDD", 1, 2, 0, 1, p, 3));
        foreach (int p in pixels) rows.Add(Row("NSDD", 1, 2, 0, 0, p, 0));
        var e = Evaluate(optics, new MeritFunction(rows));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        double peak = Enumerable.Range(7, 5).Max(i => e.Rows[i].Value);
        for (int k = 0; k < pixels.Length; k++)
            Within(e.Rows[7 + k].Value / peak, e.Rows[2 + k].Value, 1e-15, $"pixel {pixels[k]} normalized");
        Within(1, Enumerable.Range(2, 5).Max(i => e.Rows[i].Value), 0, "the peak row");
    }

    /// <summary>NSRA: a Source Ray (one analysis ray) along +z from the origin onto an absorbing
    /// detector at z = 25 mm: the last segment ends at z = 25 on the detector, travelling +z, and
    /// its path from the source is 25 mm.</summary>
    [Fact]
    public void Nsra_ReadsTheSegmentsOfASingleRaySource()
    {
        var optics = new OpticsSettings
        {
            NonSequential = new OpticsNonSequentialSettings
            {
                Objects = new()
                {
                    new OpticsNscObjectSettings { Type = "Source Ray", Parameters = new() { 1, 1, 1, 0, 0, 0, 0, 1, 0 } },
                    new OpticsNscObjectSettings { Type = "Detector Rectangle", Z = 25, Material = "ABSORB", Parameters = new() { 5, 5, 4, 4 } },
                },
            },
        };
        double Data(int data, int seg = -1) => Evaluate(optics, new MeritFunction(new[] { Row("NSRA", 1, 1, 0, 0, 0, 0, seg, data) })).Rows[0].Value;
        Within(25, Data(3), 1e-12, "z of the last segment");
        Within(25, Data(33), 1e-12, "z of the last segment, global");
        Within(1, Data(6), 1e-15, "z cosine");
        Within(25, Data(16), 1e-12, "path from the source");
        Within(0, Data(3, 0), 0, "z of segment 0, the source point");
        var phase = Evaluate(optics, new MeritFunction(new[] { Row("NSRA", 1, 1, 0, 0, 0, 0, -1, 13) }));
        Assert.Contains("not built", phase.Rows[0].Error);
        var many = Evaluate(PointAndDetector(-20, 10), new MeritFunction(new[] { Row("NSRA", 1, 1, 0, 0, 0, 0, -1, 3) }));
        Assert.Contains("single analysis ray", many.Rows[0].Error);
    }

    /// <summary>Object operands. Object 2 is placed 5 mm along z in object 1's frame, object 1
    /// at (1, 2, 3) mm tilted 10° about x: in the origin's frame object 2 is at
    /// (1, 2 − 5 sin 10°, 3 + 5 cos 10°) (derived: Rx(10°) turns +z toward −y). Object 3's tilts
    /// (10, 20, 30)° in each rotation order come back from its rotation matrix with Ref? 1.</summary>
    [Fact]
    public void ObjectOperands_ReadPositionsTiltsParametersAndTheRotation()
    {
        var optics = new OpticsSettings
        {
            NonSequential = new OpticsNonSequentialSettings
            {
                Objects = new()
                {
                    new OpticsNscObjectSettings { Type = "Rectangle", X = 1, Y = 2, Z = 3, TiltX = 10, Parameters = new() { 4, 6 } },
                    new OpticsNscObjectSettings { Type = "Rectangle", ReferenceObject = 1, Z = 5, Parameters = new() { 1, 1 } },
                    new OpticsNscObjectSettings { Type = "Rectangle", TiltX = 10, TiltY = 20, TiltZ = 30, Parameters = new() { 1, 1 } },
                    new OpticsNscObjectSettings { Type = "Rectangle", TiltX = 10, TiltY = 20, TiltZ = 30, GlobalXyzRotationOrder = true, Parameters = new() { 1, 1 } },
                },
            },
        };
        double V(string code, int obj, params double[] data)
        {
            var e = Evaluate(optics, new MeritFunction(new[] { Row(code, 1, obj, 0, 0, data) }));
            Assert.False(e.HasFailures, string.Join("; ", e.Errors));
            return e.Rows[0].Value;
        }
        double s = Math.Sin(10 * Math.PI / 180), c = Math.Cos(10 * Math.PI / 180);
        Within(5, V("NPZV", 2, 0), 0, "z relative to the reference object");
        Within(3 + 5 * c, V("NPZV", 2, 1), 1e-12, "z in the origin's frame");
        Within(2 - 5 * s, V("NPYV", 2, 2), 1e-12, "y in the global frame");
        Within(1, V("NPXV", 2, 1), 1e-12, "x in the origin's frame");
        Within(10, V("NTXV", 2, 1), 1e-9, "object 2 inherits object 1's tilt");
        Within(0, V("NTXV", 2, 0), 0, "its own tilt cell");
        foreach (int obj in new[] { 3, 4 })
        {
            Within(10, V("NTXV", obj, 1), 1e-9, $"object {obj} tilt x");
            Within(20, V("NTYV", obj, 1), 1e-9, $"object {obj} tilt y");
            Within(30, V("NTZV", obj, 1), 1e-9, $"object {obj} tilt z");
        }
        // NSRM: R[2][3] of Rx(10°) is −sin 10° (row y, column z); Ref? 0 is the identity.
        Within(-s, V("NSRM", 1, 1, 6), 1e-15, "R[2][3]");
        Within(c, V("NSRM", 1, 1, 9), 1e-15, "R[3][3]");
        Within(0, V("NSRM", 1, 0, 6), 0, "Ref? 0 off-diagonal");
        Within(1, V("NSRM", 1, 0, 5), 0, "Ref? 0 diagonal");
        Within(6, V("NPVA", 1, 2), 0, "parameter 2");
        var bound = Evaluate(optics, new MeritFunction(new[] { Row("NPGT", 1, 1, 7, 1, 2), Row("NPLT", 1, 1, 7, 1, 2), Row("NPZG", 1, 2, 10, 1, 1) }));
        Assert.Equal(6, bound.Rows[0].Value);            // violated: the value
        Assert.Equal(7, bound.Rows[1].Value);            // holds: the target
        Within(3 + 5 * c, bound.Rows[2].Value, 1e-12, "violated lower bound on z");
        var missing = Evaluate(optics, new MeritFunction(new[] { Row("NPVA", 1, 1, 0, 0, 9) }));
        Assert.Contains("parameters 1 to 2", missing.Rows[0].Error);
    }

    [Fact]
    public void TheTiltDecomposition_RebuildsTheRotation_AtGimbalLockToo()
    {
        foreach (bool global in new[] { false, true })
            foreach (var (a, b, c) in new[] { (10.0, 20.0, 30.0), (-120.0, 45.0, 170.0), (35.0, 90.0, 0.0), (0.0, -90.0, 0.0) })
            {
                var t = new NscPlacement(0, 0, 0, a, b, c, global).ToTransform();
                var (x, y, z) = NscObjectFrames.Tilts(t, global);
                var back = new NscPlacement(0, 0, 0, x, y, z, global).ToTransform();
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        Within(NscObjectFrames.Element(t, i, j), NscObjectFrames.Element(back, i, j), 1e-12, $"R[{i}][{j}] of ({a}, {b}, {c}), global order {global}");
            }
    }

    // ================================================================ variables

    [Fact]
    public void ObjectCellVariables_AreFound_Read_Written_AndCopiedExactly()
    {
        var optics = new OpticsSettings
        {
            NonSequential = new OpticsNonSequentialSettings
            {
                Objects = new()
                {
                    new OpticsNscObjectSettings
                    {
                        Type = "Source Point", Z = -3, Parameters = new() { 10, 100, 1, 0, 0 },
                        Flags = new() { new() { Column = "Z", Kind = "Variable" }, new() { Column = "Par6", Kind = "Variable" }, new() { Column = "TiltY", Kind = "Variable" } },
                    },
                },
            },
        };
        var variables = LensVariables.Find(optics);
        Assert.Equal(new[] { LensVariableKind.NscPosition, LensVariableKind.NscParameter, LensVariableKind.NscTilt }, variables.Select(v => v.Kind));
        Assert.All(variables, v => Assert.Equal(1, v.NscObject));
        Assert.Equal("Object 1 Z position", variables[0].Label);
        // Parameter 6 (Cone Angle) is not saved: it reads as the type's default, 0.
        Assert.Equal(new[] { -3.0, 0, 0 }, LensVariables.Values(optics, variables));
        var applied = LensVariables.Apply(optics, variables, new[] { -7.5, 33, 4 });
        var row = applied.NonSequential!.Objects![0];
        Assert.Equal((-7.5, 4.0), (row.Z!.Value, row.TiltY!.Value));
        Assert.Equal(new[] { 10.0, 100, 1, 0, 0, 33 }, row.Parameters);
        Assert.Equal(new[] { -7.5, 33, 4 }, LensVariables.Values(applied, variables));
        var back = LensVariables.CopyCells(optics, applied, variables);
        Assert.Equal(-3, back.NonSequential!.Objects![0].Z);
        Assert.Null(back.NonSequential.Objects[0].TiltY);   // a missing value stays missing

        var refused = optics with
        {
            NonSequential = new OpticsNonSequentialSettings
            {
                Objects = new()
                {
                    new OpticsNscObjectSettings
                    {
                        Type = "Sphere", Parameters = new() { 1, 1 },
                        Flags = new() { new() { Column = "Par2", Kind = "Variable" }, new() { Column = "X", Kind = "Solve" }, new() { Column = "Material", Kind = "Variable" } },
                    },
                },
            },
        };
        var e = Assert.Throws<ArgumentException>(() => LensVariables.Find(refused));
        Assert.Contains("Is Volume? takes a flag", e.Message);
        Assert.Contains("'Solve' is not built on object cells", e.Message);
        Assert.Contains("'Material' is not one the optimizer varies", e.Message);
    }

    [Fact]
    public void ObjectFlags_RoundTripThroughTheProjectFile()
    {
        var row = new OpticsNscObjectSettings
        {
            Type = "Rectangle", Parameters = new() { 1, 2 },
            Flags = new() { new() { Column = "Z", Kind = "Variable" }, new() { Column = "Par2", Kind = "Variable" } },
        };
        var project = new SimProject { Name = "F", AnalysisType = "Optics", Optics = new OpticsSettings { NonSequential = new OpticsNonSequentialSettings { Objects = new() { row } } } };
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = Assert.Single(serializer.Load(path).Optics!.NonSequential!.Objects!);
            Assert.Equal(row.Flags, loaded.Flags!);
            Assert.Equal(row with { Parameters = loaded.Parameters, Flags = loaded.Flags }, loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ================================================================ wizards

    [Fact]
    public void OptimizationWizard_WritesClearTraceAndTheCriterionRows()
    {
        var rows = PointAndDetector(-20, 100).NonSequential!.Objects!;
        var existing = new MeritFunction(new[] { Row("BLNK", 0, 0) });
        var result = NscMeritWizards.Optimization(existing, rows, 1, new NscOptimizationWizardOptions
        {
            Detector = 2, Criterion = NscWizardCriterion.SpatialUniformity, Comparison = NscWizardComparison.LessThan, Target = 0.1,
            MinimumFlux = 0.2, OverallWeight = 2, SplitRays = true,
        });
        Assert.Equal(new[] { "BLNK", "NSDD", "NSTR", "NSDD", "NSDD", "DIVI", "OPLT", "NSDD", "OPGT" }, result.Function.Rows.Select(r => r.Code));
        var f = result.Function;
        Assert.Equal((1, 0), (f.Row(2).Int1, f.Row(2).Int2));                      // clear every detector
        Assert.Equal(1, f.Row(3).DataAt(1));                                         // Splt?
        Assert.Equal((-4.0, 1.0), (f.Row(4).DataAt(1), f.Row(4).DataAt(2)));         // σ of flux/area
        Assert.Equal((-5.0, 1.0), (f.Row(5).DataAt(1), f.Row(5).DataAt(2)));         // mean
        Assert.Equal((4, 5), (f.Row(6).Int1, f.Row(6).Int2));                        // DIVI rows 4 / 5
        Assert.Equal((6, 0.1, 2.0), (f.Row(7).Int1, f.Row(7).Target, f.Row(7).Weight));
        Assert.Equal((8, 0.2), (f.Row(9).Int1, f.Row(9).Target));
        Assert.Equal(10, result.NextStartAt);
        Assert.Empty(MeritEvaluator.Validate(f, MeritOperandRegistry.CreateDefault(), 1));
        var evaluated = Evaluate(PointAndDetector(-20, 5000), f);
        Assert.False(evaluated.HasFailures, string.Join("; ", evaluated.Errors));

        Assert.Contains("is not offered for a Detector Polar", Assert.Throws<ArgumentException>(() =>
            NscMeritWizards.CriterionRows(3, "Detector Polar", NscWizardCriterion.CentroidX, NscWizardComparison.Equal, 0, 1, 1)).Message);
        Assert.Contains("OP-85", Assert.Throws<ArgumentException>(() => NscMeritWizards.Optimization(existing, rows, 1,
            new NscOptimizationWizardOptions { Detector = 2, UseLightningTrace = true })).Message);
        Assert.Contains("not a Detector Rectangle", Assert.Throws<ArgumentException>(() => NscMeritWizards.Optimization(existing, rows, 1,
            new NscOptimizationWizardOptions { Detector = 1 })).Message);
        // Overwrite from row 1 replaces the BLNK.
        var over = NscMeritWizards.Optimization(existing, rows, 1, new NscOptimizationWizardOptions { Detector = 2, StartAt = 1, Overwrite = true, Target = 0.3 });
        Assert.Equal(new[] { "NSDD", "NSTR", "NSDD" }, over.Function.Rows.Select(r => r.Code));
        Assert.Equal((0.0, 0.0, 0.3), (over.Function.Row(3).DataAt(1), over.Function.Row(3).DataAt(2), over.Function.Row(3).Target));
    }

    /// <summary>Area scaling: a 1 × 3 image (0, 3, 6) onto 2 × 1 pixels gives (0·1 + 3·½)/1.5 = 1
    /// and (3·½ + 6·1)/1.5 = 5 (derived); a 4 × 4 image onto 2 × 2 the means of its quarters,
    /// with the picture's top row on the detector's +y edge.</summary>
    [Fact]
    public void BitmapScaling_AveragesByArea_TopRowAtPlusY()
    {
        var s = NscMeritWizards.Scale(new double[,] { { 0, 3, 6 } }, 2, 1);
        Within(1, s[0, 0], 1e-15, "left pixel");
        Within(5, s[0, 1], 1e-15, "right pixel");
        var map = new double[4, 4];
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) map[r, c] = r < 2 ? (c < 2 ? 1 : 2) : (c < 2 ? 3 : 4);
        var q = NscMeritWizards.Scale(map, 2, 2);
        Assert.Equal(new[] { 3.0, 4, 1, 2 }, new[] { q[0, 0], q[0, 1], q[1, 0], q[1, 1] });   // iy 0 is the image's bottom
    }

    /// <summary>
    /// The Bitmap Wizard on a 2 × 2 grey image onto a 2 × 2 Detector Rectangle: one total-flux row
    /// and one NSDD Data 3 row per pixel in the detector's numbering, targets the decoded grey
    /// levels over the brightest. Decoding (CSS Color 4 §19, lin_sRGB): 255 → 1, 0 → 0 and 128 →
    /// ((128/255 + 0.055)/1.055)^2.4. Resample Detector takes the image's pixel counts.
    /// </summary>
    [Fact]
    public void BitmapWizard_TargetsTheImageOnTheDetectorsPixels()
    {
        var optics = PointAndDetector(-20, 100) with { };
        var rows = optics.NonSequential!.Objects!.ToList();
        rows[1] = NscMeritWizards.WithPixels(rows[1], 2, 2);
        var image = SceneImage.Grey(new double[,] { { 255, 0 }, { 128, 255 } }, SceneFileKind.Memory, 255);
        var result = NscMeritWizards.Bitmap(new MeritFunction(), rows, 1, new NscBitmapWizardOptions { Detector = 2, TotalFlux = 0.25 }, image);
        var f = result.Function;
        Assert.Equal(new[] { "NSDD", "NSTR", "NSDD", "NSDD", "NSDD", "NSDD", "NSDD" }, f.Rows.Select(r => r.Code));
        Assert.Equal((0.0, 0.0, 0.25), (f.Row(3).DataAt(1), f.Row(3).DataAt(2), f.Row(3).Target));
        double grey = Math.Pow((128 / 255.0 + 0.055) / 1.055, 2.4);
        // Pixels 1, 2 are the detector's bottom row (the image's row 2: 128, 255); 3, 4 its top (255, 0).
        var expected = new[] { (1, grey), (2, 1.0), (3, 1.0), (4, 0.0) };
        for (int k = 0; k < 4; k++)
        {
            var r = f.Row(4 + k);
            Assert.Equal((double)expected[k].Item1, r.DataAt(1));
            Assert.Equal(3, r.DataAt(2));
            Within(expected[k].Item2, r.Target, 1e-15, $"pixel {expected[k].Item1} target");
        }
        Within(grey, NscMeritWizards.SrgbDecode(128 / 255.0), 0, "sRGB decode");

        var resampled = NscMeritWizards.Bitmap(new MeritFunction(), PointAndDetector(-20, 100).NonSequential!.Objects!, 1,
            new NscBitmapWizardOptions { Detector = 2, TotalFlux = 1, ResampleDetector = true }, image);
        Assert.Equal((2, 2), resampled.DetectorPixels);
        Assert.Contains("Color Targets need a Detector Color", Assert.Throws<ArgumentException>(() =>
            NscMeritWizards.Bitmap(new MeritFunction(), rows, 1, new NscBitmapWizardOptions { Detector = 2, TotalFlux = 1, ColorTargets = true }, image)).Message);
    }

    // ================================================================ the gate

    /// <summary>
    /// Gate (plan item OP-88): the source's distance from the detector optimises to the
    /// closed-form position for a target irradiance. The target is the average irradiance at
    /// d* = 20 mm on a detector of half width a = 20 mm, Ē = PΩ(d*)/(2π·4a²) (class summary). The
    /// source starts 30 mm away; its z cell is the only variable; Orthogonal Descent runs on the
    /// merit function clear, NSTR, NSDD Data 1 with N = 50 000 random rays, the same rays at every
    /// evaluation. The optimum of that Monte Carlo merit is where the landing fraction f̂(d) equals
    /// the target's f(d*), so its error is (f̂ − f)/f′: one binomial standard deviation
    /// σ_d = √(f(1 − f)/N)/|f′(d*)|, f′ = Ω′/2π (Ω′ by central difference of the closed form).
    /// Because f̂ steps by 1/N between designs, the optimizer can stop anywhere on the flat step
    /// containing the optimum, (1/N)/|f′| wide. Tolerance: 5σ_d plus one step.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void Gate_SourceDistance_OptimisesToTheClosedFormPositionForATargetIrradiance()
    {
        const int n = 50_000;
        double a = 0.020, dStar = 0.020;
        double f = SolidAngle(a, dStar) / (2 * Math.PI);
        double target = f / (4 * a * a);   // W/m², P = 1 W
        double h = 1e-7;
        double fPrime = (SolidAngle(a, dStar + h) - SolidAngle(a, dStar - h)) / (2 * h) / (2 * Math.PI);   // per meter
        double sigmaD = Math.Sqrt(f * (1 - f) / n) / Math.Abs(fPrime);
        double step = 1.0 / n / Math.Abs(fPrime);
        double tolerance = 5 * sigmaD + step;   // meters

        var optics = PointAndDetector(-30, n, variable: true) with
        {
            MeritFunction = MeritFunctionSettings.ToSettings(IrradianceMerit(target)),
        };
        var result = LocalOptimizer.Run(optics, NoCatalogs, new LocalOptimizationOptions { Algorithm = LocalOptimizationAlgorithm.OrthogonalDescent });
        double d = -result.Values[0] / 1000;   // the source's z in mm → distance in m
        Within(dStar, d, tolerance, "optimised source distance (m)");
        Within(DistanceFor(a, SolidAngle(a, dStar)), d, tolerance, "against the inverted closed form");
        Assert.True(result.Merit < result.StartMerit, "the merit fell");
        Assert.NotEqual(LocalOptimizationStop.Cancelled, result.Stop);
    }
}
