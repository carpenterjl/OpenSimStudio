using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-54: the merit function engine. The merit value is checked against
/// MF² = Σ W(V − T)² / Σ W with W = |weight| and the percent contribution against
/// 100·W(V − T)² / Σ W(V − T)² (OpticStudio 2025 R2 help, Modifying the merit function,
/// "Merit function definition", and Notes on Operand Weights; their equation images are saved
/// rendered under plans/sources/optics/op-54-help/images/). Every expected number is derived in
/// the test from the lens's own inputs; radii, thicknesses and indices are arbitrary inputs.
/// </summary>
public sealed class MeritFunctionTests
{
    // ---- Builders ----

    private static SequentialSurface S(double radiusMm, double thicknessMm, double n = 1, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radiusMm * 1e-3), Thickness = thicknessMm * 1e-3,
        Medium = ParaxialMedium.Constant(n), IsStop = stop,
    };

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    /// <summary>A biconvex singlet (mm): R 50 and −50, 5 thick, n = 1.5, then 90 to the image.</summary>
    private static SequentialLens Singlet() => Lens(S(50, 5, 1.5, stop: true), S(-50, 90));

    /// <summary>EPD 10 mm, an on-axis field and one at 5°, millimeters.</summary>
    private static OpticalSystemData Sys() => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
        Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint(), new FieldPoint { Y = 5 } } },
    };

    private static IMeritConfigurations One(SequentialLens? lens = null) => new SingleMeritConfiguration(lens ?? Singlet(), Sys());

    private static MeritOperandRow R(string code, int int1 = 0, int int2 = 0, double target = 0, double weight = 0, params double[] data) =>
        MeritOperandRow.Of(code, int1, int2, target, weight, data);

    private static MeritEvaluation Eval(IMeritConfigurations configurations, params MeritOperandRow[] rows) =>
        MeritEvaluator.Evaluate(new MeritFunction(rows), configurations);

    private static void Close(double expected, double actual, double relative, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= relative * Math.Max(1, Math.Abs(expected)),
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}).");
    }

    // ---- The gate ----

    /// <summary>Gate: the merit value equals the hand-computed weighted RMS of a small table.
    /// Values: CTVA 1 = 5 mm, CVVA 1 = 1/50 mm⁻¹, COVA 1 = 0, TTHI 1..2 = 95 mm. A negative weight
    /// counts by its absolute value; a zero weight, BLNK and CONS add nothing.</summary>
    [Fact]
    public void TheMeritValue_IsTheHandComputedWeightedRms()
    {
        var e = Eval(One(),
            R("CTVA", 1, target: 4, weight: 2),
            R("CVVA", 1, target: 0, weight: 1),
            R("BLNK"),
            R("COVA", 1, target: -1, weight: -3),
            R("CONS", target: 7, weight: 0),
            R("TTHI", 1, 2, target: 90, weight: 0.5));

        // Differences V − T: 1, 0.02, 1, (CONS: 0, weight 0), 5. Weights |W|: 2, 1, 3, 0.5.
        double[] w = { 2, 1, 3, 0.5 }, d = { 1, 0.02, 1, 5 };
        double num = 0, den = 0;
        for (int i = 0; i < w.Length; i++) { num += w[i] * d[i] * d[i]; den += w[i]; }
        Close(Math.Sqrt(num / den), e.Value, 1e-13, "MF");
        Close(den, e.SumOfWeights, 0, "ΣW");
        Close(5, e.Rows[0].Value, 1e-14, "CTVA");
        Close(0.02, e.Rows[1].Value, 1e-14, "CVVA");
        Close(7, e.Rows[4].Value, 0, "CONS");
        Close(95, e.Rows[5].Value, 1e-14, "TTHI");
        Assert.Equal(MeritRowStatus.Control, e.Rows[2].Status);
        Assert.False(e.Rows[4].Counts);
        // Percent contributions, and the residuals' squares sum to MF².
        Close(100 * 3 * 1 / num, e.Rows[3].Contribution, 1e-12, "contribution of COVA");
        Close(100 * 0.5 * 25 / num, e.Rows[5].Contribution, 1e-12, "contribution of TTHI");
        Close(100, e.Rows.Sum(r => r.Contribution), 1e-12, "Σ contributions");
        Close(e.Value * e.Value, e.Residuals.Sum(r => r * r), 1e-13, "Σ r²");
    }

    /// <summary>Gate: reordering rows that do not read each other leaves the merit value the same
    /// bits (the sums add their terms smallest first).</summary>
    [Fact]
    public void ReorderingIndependentRows_DoesNotChangeTheMeritValue()
    {
        var rows = new[]
        {
            R("CTVA", 1, target: 4.3, weight: 2),
            R("REAY", 2, 1, target: 0.1, weight: 1, 0, 1, 0, 1),
            R("CVVA", 2, target: 0.01, weight: 0.7),
            R("REAX", 3, 1, target: -0.2, weight: 3, 0, 1, 1, 0),
            R("EFFL", 0, 1, target: 48, weight: 1.3),
            R("TOTR", target: 96, weight: 0.25),
            R("REAR", 3, 1, target: 0.05, weight: 1, 0, 1, 0.5, -0.5),
        };
        var forward = Eval(One(), rows);
        var reversed = Eval(One(), rows.Reverse().ToArray());
        var shuffled = Eval(One(), rows[3], rows[0], rows[6], rows[2], rows[5], rows[1], rows[4]);
        Assert.False(forward.HasFailures, string.Join("; ", forward.Errors));
        Assert.Equal(BitConverter.DoubleToInt64Bits(forward.Value), BitConverter.DoubleToInt64Bits(reversed.Value));
        Assert.Equal(BitConverter.DoubleToInt64Bits(forward.Value), BitConverter.DoubleToInt64Bits(shuffled.Value));
        Assert.True(forward.Value > 0);
    }

    /// <summary>Gate: a ray that fails in one row penalises that row alone: the other rows' values
    /// are the same bits as without it, and the merit is the hand formula with the penalty in
    /// place of the failed row's V − T. The failing ray starts 250 mm off axis (Py = 50 on a
    /// 10 mm pupil) and misses the 50 mm radius sphere.</summary>
    [Fact]
    public void AFailedRayInOneRow_PenalisesOnlyThatRow()
    {
        var good = new[]
        {
            R("REAY", 2, 1, target: 0, weight: 1, 0, 1, 0, 1),
            R("CTVA", 1, target: 4, weight: 2),
            R("REAX", 3, 1, target: 0, weight: 1, 0, 0, 1, 0),
        };
        var failing = R("REAY", 2, 1, target: 0, weight: 4, 0, 0, 0, 50);
        var without = Eval(One(), good);
        var with = Eval(One(), good[0], failing, good[1], good[2]);

        Assert.False(without.HasFailures);
        var failed = with.Rows[1];
        Assert.Equal(MeritRowStatus.Failed, failed.Status);
        Assert.StartsWith("Row 2 (REAY):", failed.Error);
        Assert.Contains("failed", failed.Error);
        Assert.True(double.IsNaN(failed.Value));
        Assert.Single(with.Errors);
        int[] others = { 0, 2, 3 };
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(MeritRowStatus.Evaluated, with.Rows[others[k]].Status);
            Assert.Equal(BitConverter.DoubleToInt64Bits(without.Rows[k].Value), BitConverter.DoubleToInt64Bits(with.Rows[others[k]].Value));
        }
        double p = new MeritEvaluationOptions().FailedRowPenalty;
        double num = 0, den = 0;
        foreach (var r in without.Rows) { double dd = r.Value - r.Row.Target; num += Math.Abs(r.Row.Weight) * dd * dd; den += Math.Abs(r.Row.Weight); }
        Close(Math.Sqrt((num + 4 * p * p) / (den + 4)), with.Value, 1e-13, "MF with the penalised row");
    }

    // ---- Operands ----

    /// <summary>EFFL against the thick-lens formula 1/f = (n − 1)[1/R1 − 1/R2 + (n − 1)d/(n R1 R2)]
    /// (derived from the two refractions and the transfer between them); TOTR is 5 + 90.</summary>
    [Fact]
    public void FirstOrderOperands_MatchTheClosedForms()
    {
        double n = 1.5, r1 = 50, r2 = -50, dd = 5;
        double f = 1 / ((n - 1) * (1 / r1 - 1 / r2 + (n - 1) * dd / (n * r1 * r2)));
        var e = Eval(One(), R("EFFL", 0, 1), R("TOTR"));
        Close(f, e.Rows[0].Value, 1e-12, "EFFL");
        Close(95, e.Rows[1].Value, 1e-13, "TOTR");
    }

    /// <summary>The real-ray operands read the traced path's local coordinates, in mm; rows on the
    /// same ray share one trace.</summary>
    [Fact]
    public void RealRayOperands_ReadTheTracedRay_AndShareIt()
    {
        var lens = Singlet();
        var system = Sys();
        var trace = SequentialTrace.Create(lens, system, 0);
        var path = trace.TracePath(new RayLauncher(lens, system).Launch(trace, 0, 1, 0.3, 0.8));
        var hit = path.Hits.Single(h => h.Surface == 2);
        var e = Eval(One(),
            R("REAX", 2, 1, data: new[] { 0, 1, 0.3, 0.8 }), R("REAY", 2, 1, data: new[] { 0, 1, 0.3, 0.8 }),
            R("REAZ", 2, 1, data: new[] { 0, 1, 0.3, 0.8 }), R("REAR", 2, 1, data: new[] { 0, 1, 0.3, 0.8 }));
        Assert.Equal(1, e.RaysTraced);
        Assert.Equal(hit.Position.X * 1e3, e.Rows[0].Value, 12);
        Assert.Equal(hit.Position.Y * 1e3, e.Rows[1].Value, 12);
        Assert.Equal(hit.Position.Z * 1e3, e.Rows[2].Value, 12);
        Assert.Equal(Math.Sqrt(hit.Position.X * hit.Position.X + hit.Position.Y * hit.Position.Y) * 1e3, e.Rows[3].Value, 12);
    }

    /// <summary>Bounds (help: Understanding Boundary Operands): the target while the bound holds,
    /// the actual value when violated; MNCT's value with thicknesses 2.5 and 2.2 under a 3.0 bound
    /// is 1.7, the help's own example.</summary>
    [Fact]
    public void BoundOperands_AreTheTargetWhileTheyHold()
    {
        var e = Eval(One(),
            R("CTGT", 1, target: 3, weight: 1), R("CTGT", 1, target: 8, weight: 1),
            R("CTLT", 1, target: 8, weight: 1), R("CTLT", 1, target: 3, weight: 1),
            R("CVGT", 2, target: 0, weight: 1));
        Assert.Equal(3, e.Rows[0].Value);
        Close(5, e.Rows[1].Value, 1e-14);
        Assert.Equal(8, e.Rows[2].Value);
        Close(5, e.Rows[3].Value, 1e-14);
        Close(-0.02, e.Rows[4].Value, 1e-14);   // curvature −1/50 violates "greater than 0"

        var thin = Lens(S(50, 2.5, 1.5, stop: true), S(-50, 2.2), S(double.PositiveInfinity, 80));
        var m = Eval(One(thin), R("MNCT", 1, 2, target: 3, weight: 1), R("MXCT", 1, 2, target: 2.4, weight: 1), R("MNCT", 1, 3, target: 2, weight: 1));
        Close(1.7, m.Rows[0].Value, 1e-14, "MNCT");
        Close(2.4 + 0.1, m.Rows[1].Value, 1e-14, "MXCT");       // 2.5 exceeds 2.4 by 0.1; 2.2 holds
        Assert.Equal(2, m.Rows[2].Value);                       // every thickness ≥ 2
    }

    /// <summary>Math operands read earlier rows (the help's Defining complex operands examples).</summary>
    [Fact]
    public void MathOperands_ReadEarlierRows()
    {
        var e = Eval(One(),
            R("CTVA", 1), R("CTVA", 2),                                   // 1, 2: 5 and 90
            R("SUMM", 1, 2), R("DIFF", 2, 1), R("PROD", 1, 2), R("DIVI", 2, 1),  // 3–6
            R("CONS", target: 0.5), R("ASIN", 7, 1), R("COSI", 8, 1),    // 7–9: asin(0.5) in degrees, cos of it
            R("OSUM", 1, 2), R("QSUM", 1, 2), R("MAXX", 1, 2), R("MINN", 1, 2), // 10–13
            R("OPGT", 3, target: 100, weight: 1), R("OPLT", 3, target: 100, weight: 1), // 14, 15
            R("DIVB", 2, data: new[] { 3.0 }), R("PROB", 2, data: new[] { 3.0 }), R("RECI", 1), R("SQRT", 2),
            R("LOGE", 7), R("LOGT", 2), R("ABSO", 4), R("OPVA", 1));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        double a = e.Rows[0].Value, b = e.Rows[1].Value;
        Close(5, a, 1e-14); Close(90, b, 1e-14);
        Assert.Equal(a + b, e.Rows[2].Value);
        Assert.Equal(b - a, e.Rows[3].Value);
        Assert.Equal(a * b, e.Rows[4].Value);
        Assert.Equal(b / a, e.Rows[5].Value);
        Close(30, e.Rows[7].Value, 1e-13, "ASIN in degrees");
        Close(Math.Sqrt(3) / 2, e.Rows[8].Value, 1e-14, "COSI in degrees");
        Assert.Equal(a + b, e.Rows[9].Value);
        Assert.Equal(Math.Sqrt(a * a + b * b), e.Rows[10].Value);
        Assert.Equal(b, e.Rows[11].Value);
        Assert.Equal(a, e.Rows[12].Value);
        Assert.Equal(e.Rows[2].Value, e.Rows[13].Value);              // 95 < 100 violates "greater than"
        Assert.Equal(100, e.Rows[14].Value);                          // 95 < 100 holds
        Assert.Equal(b / 3, e.Rows[15].Value);
        Assert.Equal(b * 3, e.Rows[16].Value);
        Assert.Equal(1 / a, e.Rows[17].Value);
        Assert.Equal(Math.Sqrt(b), e.Rows[18].Value);
        Assert.Equal(Math.Log(0.5), e.Rows[19].Value);
        Assert.Equal(Math.Log10(b), e.Rows[20].Value);
        Assert.Equal(Math.Abs(b - a), e.Rows[21].Value);
        Assert.Equal(a, e.Rows[22].Value);
    }

    /// <summary>A math row reading a failed row fails, naming it; a row it does not read is untouched.</summary>
    [Fact]
    public void AMathRowReadingAFailedRow_FailsNamingIt()
    {
        var e = Eval(One(), R("REAY", 2, 1, data: new[] { 0, 0, 0, 50.0 }), R("CTVA", 1), R("ABSO", 1, weight: 1), R("ABSO", 2, weight: 1));
        Assert.Equal(MeritRowStatus.Failed, e.Rows[2].Status);
        Assert.Contains("reads row 1, which failed", e.Rows[2].Error);
        Assert.Equal(MeritRowStatus.Evaluated, e.Rows[3].Status);
        var sqrt = Eval(One(), R("CONS", target: -4), R("SQRT", 1, weight: 1));
        Assert.Contains("negative", sqrt.Rows[1].Error);
    }

    // ---- Control operands ----

    [Fact]
    public void GotoAndEndx_SkipRows()
    {
        var e = Eval(One(),
            R("CTVA", 1, weight: 1), R("GOTO", 4), R("CTVA", 2, weight: 1), R("CTVA", 1, weight: 1),
            R("ENDX"), R("CTVA", 2, weight: 1));
        Assert.Equal(new[] { MeritRowStatus.Evaluated, MeritRowStatus.Control, MeritRowStatus.Skipped, MeritRowStatus.Evaluated, MeritRowStatus.Control, MeritRowStatus.Skipped },
            e.Rows.Select(r => r.Status));
        Close(2, e.SumOfWeights, 0);
        Assert.Equal(0, e.Rows[2].Residual);
        // A math row reading a skipped row fails.
        var m = Eval(One(), R("GOTO", 3), R("CTVA", 1), R("ABSO", 2, weight: 1));
        Assert.Contains("not evaluated", m.Rows[2].Error);
    }

    /// <summary>SKIS skips on a symmetric lens and SKIN does not; a decentered surface makes the
    /// lens not symmetric, and USYM anywhere makes it count as symmetric.</summary>
    [Fact]
    public void SkisSkinAndUsym_FollowTheLensSymmetry()
    {
        MeritOperandRow[] Rows(bool usym) => new[]
        {
            R("SKIS", 3), R("CTVA", 1, weight: 1), R("SKIN", 5), R("CTVA", 2, weight: 1), usym ? R("USYM") : R("BLNK"),
        };
        var symmetric = Eval(One(), Rows(false));
        Assert.True(MeritEvaluator.IsRotationallySymmetric(Singlet()));
        Assert.Equal(MeritRowStatus.Skipped, symmetric.Rows[1].Status);
        Assert.Equal(MeritRowStatus.Evaluated, symmetric.Rows[3].Status);

        var decentered = Singlet();
        decentered = decentered with
        {
            Surfaces = decentered.Surfaces.Select((s, j) => j == 1 ? s with { TiltBefore = new TiltDecenter(0, 1e-3, 0, 0, 0) } : s).ToArray()
        };
        Assert.False(MeritEvaluator.IsRotationallySymmetric(decentered));
        var tilted = Eval(One(decentered), Rows(false));
        Assert.Equal(MeritRowStatus.Evaluated, tilted.Rows[1].Status);
        Assert.Equal(MeritRowStatus.Skipped, tilted.Rows[3].Status);
        var assumed = Eval(One(decentered), Rows(true));
        Assert.Equal(MeritRowStatus.Skipped, assumed.Rows[1].Status);
        Assert.Equal(MeritRowStatus.Evaluated, assumed.Rows[3].Status);
        Assert.True(assumed.Symmetric[0]);
    }

    /// <summary>OOFF and DMFS do nothing; CONF switches the configuration for the rows after it.</summary>
    [Fact]
    public void Conf_SwitchesTheConfiguration()
    {
        var configs = new TwoConfigurations(Singlet(), Lens(S(50, 7, 1.5, stop: true), S(-50, 80)), Sys());
        var e = Eval(configs, R("CTVA", 1, weight: 1), R("OOFF"), R("CONF", 2), R("CTVA", 1, weight: 1), R("DMFS"), R("CONF", 1), R("CTVA", 1, weight: 1));
        Close(5, e.Rows[0].Value, 1e-14);
        Close(7, e.Rows[3].Value, 1e-14);
        Close(5, e.Rows[6].Value, 1e-14);
        Assert.Equal(new[] { 1, 1, 1, 2, 2, 2, 1 }, e.Rows.Select(r => r.Configuration));
        Assert.Equal(MeritRowStatus.Control, e.Rows[1].Status);
    }

    private sealed class TwoConfigurations(SequentialLens one, SequentialLens two, OpticalSystemData system) : IMeritConfigurations
    {
        public int Count => 2;
        public SequentialLens LensFor(int configuration) => configuration == 1 ? one : two;
        public OpticalSystemData SystemFor(int configuration) => system;
    }

    /// <summary>Problems of the merit function itself are refused before anything is computed,
    /// every one named.</summary>
    [Fact]
    public void AMalformedMeritFunction_IsRefused_NamingEveryProblem()
    {
        var ex = Assert.Throws<ArgumentException>(() => Eval(One(),
            R("CTVA", 1), R("GOTO", 1), R("SUMM", 1, 5), R("CONF", 2), R("XXXX"), R("ABSO", 2)));
        Assert.Contains("Row 2 (GOTO): Op# 1 must be a later row", ex.Message);
        Assert.Contains("Row 3 (SUMM): it reads row 5", ex.Message);
        Assert.Contains("Row 4 (CONF): Cfg# 2 is not a configuration", ex.Message);
        Assert.Contains("Row 5: there is no operand 'XXXX'", ex.Message);
        Assert.Contains("Row 6 (ABSO): it reads row 2, a GOTO row, which has no value", ex.Message);
    }

    /// <summary>A surface or wavelength that does not exist fails that row, naming it.</summary>
    [Fact]
    public void ARowWithABadParameter_FailsAlone()
    {
        var e = Eval(One(), R("CTVA", 9, weight: 1), R("REAY", 2, 4, weight: 1), R("CTVA", 1, weight: 1));
        Assert.Contains("Surf 9 is not a surface", e.Rows[0].Error);
        Assert.Contains("Wave 4 is not a wavelength", e.Rows[1].Error);
        Assert.Equal(MeritRowStatus.Evaluated, e.Rows[2].Status);
    }

    // ---- Determinism, residuals, registry, saved form ----

    /// <summary>The traces run in parallel; every degree of parallelism gives the same bits.</summary>
    [Fact]
    public void TheEvaluation_IsBitwiseTheSame_AtAnyDegreeOfParallelism()
    {
        var rows = new List<MeritOperandRow>();
        for (int i = 0; i < 12; i++)
        {
            double py = -1 + i / 6.0;
            rows.Add(R("REAY", 3, 1, target: 0, weight: 1, 0, i % 2, 0, py));
            rows.Add(R("REAX", 2, 1, target: 0, weight: 1, 0, 1, py, 0));
        }
        var f = new MeritFunction(rows);
        var reference = MeritEvaluator.Evaluate(f, One(), options: new MeritEvaluationOptions { MaxDegreeOfParallelism = 1 });
        foreach (int? dop in new int?[] { 2, 3, 7, null })
        {
            var e = MeritEvaluator.Evaluate(f, One(), options: new MeritEvaluationOptions { MaxDegreeOfParallelism = dop });
            Assert.Equal(reference.Residuals.Select(BitConverter.DoubleToInt64Bits), e.Residuals.Select(BitConverter.DoubleToInt64Bits));
            Assert.Equal(BitConverter.DoubleToInt64Bits(reference.Value), BitConverter.DoubleToInt64Bits(e.Value));
        }
    }

    /// <summary>The residual adapter's squares sum to MF², one residual per row.</summary>
    [Fact]
    public void TheResidualAdapter_GivesTheMeritAsASumOfSquares()
    {
        var f = new MeritFunction(new[] { R("CTVA", 1, target: 4, weight: 2), R("BLNK"), R("EFFL", 0, 1, target: 50, weight: 1) });
        var adapter = new MeritResiduals(f, x => One(Lens(S(50, x[0], 1.5, stop: true), S(-50, 90))));
        Assert.Equal(3, adapter.Count);
        var r = new double[3];
        adapter.AsResidualFunction()(new[] { 6.0 }, r);
        var e = Eval(One(Lens(S(50, 6, 1.5, stop: true), S(-50, 90))), f.Rows.ToArray());
        Assert.Equal(e.Residuals, r);
        Assert.Equal(0, r[1]);
        Close(e.Value * e.Value, r.Sum(v => v * v), 1e-13);
    }

    [Fact]
    public void TheRegistry_FindsByCodeAndName()
    {
        var registry = MeritOperandRegistry.CreateDefault();
        Assert.Equal("EFFL", registry.Search("focal").Single().Code);
        Assert.Equal("CTGT", registry.Search("ctgt")[0].Code);
        Assert.Equal("REAY", registry.Get("reay").Code);
        Assert.Null(registry.Find("EQUA"));                     // not built (see the registry)
        foreach (var code in new[] { "BLNK", "DMFS", "ENDX", "GOTO", "OOFF", "SKIN", "SKIS", "USYM", "CONF" })
            Assert.False(registry.Get(code).UsesTargetAndWeight);
        Assert.Equal(registry.All.Count, registry.All.Select(d => d.Code).Distinct().Count());
    }

    /// <summary>The saved form round-trips; an unknown code becomes OOFF and is named.</summary>
    [Fact]
    public void TheSavedForm_RoundTrips_AndAnUnknownCodeBecomesOoff()
    {
        var registry = MeritOperandRegistry.CreateDefault();
        var f = new MeritFunction(new[]
        {
            R("REAY", 2, 1, target: 0.1 + 0.2, weight: -3, 0, 1, 0.25, 1) with { Comment = "marginal" },
            R("BLNK") with { Comment = "lens data" },
            R("CTGT", 1, target: 2, weight: 1),
        });
        var notes = new List<string>();
        var back = MeritFunctionSettings.FromSettings(MeritFunctionSettings.ToSettings(f), registry, notes);
        Assert.Empty(notes);
        Assert.Equal(f.Rows, back.Rows);

        var saved = new OpticsMeritFunctionSettings
        {
            Operands = new() { new() { Type = "ZZZZ", Comment = "from a later build", Weight = 1 }, new() { Type = "effl", Int2 = 1 } },
        };
        var loaded = MeritFunctionSettings.FromSettings(saved, registry, notes);
        Assert.Equal("OOFF", loaded.Rows[0].Code);
        Assert.Equal("from a later build", loaded.Rows[0].Comment);
        Assert.Equal("EFFL", loaded.Rows[1].Code);
        Assert.Contains(notes, n => n.Contains("'ZZZZ'"));
        Assert.Empty(MeritFunctionSettings.FromSettings(null, registry, notes).Rows);
    }
}
