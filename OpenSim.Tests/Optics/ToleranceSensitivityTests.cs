using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Tolerancing;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>OP-64: the tolerance run's definitions (OpticStudio 2025 R2 help, "Tolerancing", "The
/// RSS Estimated Change", "Inverse Sensitivity Analysis", "Quick Sensitivity", saved under
/// plans/sources/optics/op-63-help/ and op-64-help/), its compensators and its saved settings.</summary>
public sealed class ToleranceSensitivityTests
{
    [Fact]
    public void Change_IsTheHelpsLinearAndRssDifference()
    {
        // Linear: Δ = P − N. RSS: Δ = S(P − N)√|P² − N²| (help, "Tolerancing", "Change"); 5² − 3² = 4².
        Assert.Equal(2, ToleranceAnalysis.ChangeOf(5, 3, ToleranceChangeMode.LinearDifference));
        Assert.Equal(4, ToleranceAnalysis.ChangeOf(5, 3, ToleranceChangeMode.RssDifference));
        Assert.Equal(-4, ToleranceAnalysis.ChangeOf(3, 5, ToleranceChangeMode.RssDifference));
        Assert.Equal(0, ToleranceAnalysis.ChangeOf(3, 3, ToleranceChangeMode.RssDifference));
    }

    /// <summary>The estimate is C² = Σ (Δmin² + Δmax²)/2 and F = N + C (linear) or √(N² + C²) (RSS);
    /// the worst offenders are sorted by change and cut at Show Worst; one thread or many give the
    /// same bits.</summary>
    [Fact]
    public void RssEstimate_WorstOffenders_AndThreads_FollowTheHelp()
    {
        var lens = X.Singlet();
        var table = X.Table(X.Row("TRAD", 1, 0, -1, 1), X.Row("TRAD", 2, 0, -1, 1), X.Row("TTHI", 1, 1, -0.2, 0.2));
        foreach (var change in new[] { ToleranceChangeMode.LinearDifference, ToleranceChangeMode.RssDifference })
        {
            var o = X.Wavefront with { Change = change, ShowWorst = 3 };
            var r = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, o with { MaxDegreeOfParallelism = 1 });
            Assert.Equal(3, r.Rows.Count);
            Assert.All(r.Rows, row => { Assert.True(row.AtMin.Succeeded, row.AtMin.Failure); Assert.True(row.AtMax.Succeeded, row.AtMax.Failure); });
            double c2 = r.Rows.Sum(x => (x.AtMin.Change * x.AtMin.Change + x.AtMax.Change * x.AtMax.Change) / 2);
            Assert.Equal(Math.Sqrt(c2), r.EstimatedChange!.Value, 15);
            double n = r.Nominal, c = r.EstimatedChange.Value;
            Assert.Equal(change == ToleranceChangeMode.LinearDifference ? n + c : Math.Sqrt(n * n + c * c), r.EstimatedPerformance!.Value, 15);
            foreach (var row in r.Rows)
                Assert.Equal(ToleranceAnalysis.ChangeOf(row.AtMax.Criterion, n, change), row.AtMax.Change);

            Assert.Equal(3, r.WorstOffenders.Count);
            var changes = r.Rows.SelectMany(x => new[] { x.AtMin.Change, x.AtMax.Change }).OrderByDescending(v => v).Take(3).ToArray();
            Assert.Equal(changes, r.WorstOffenders.Select(t => t.Change).ToArray());

            var parallel = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, o with { MaxDegreeOfParallelism = null });
            Assert.Equal(r.Rows.Select(x => (x.AtMin.Criterion, x.AtMax.Criterion)), parallel.Rows.Select(x => (x.AtMin.Criterion, x.AtMax.Criterion)));
        }
    }

    /// <summary>A change of the back distance itself is undone exactly by the paraxial focus
    /// compensator ("the focus is adjusted to maintain the amount of defocus in the nominal lens
    /// design"), and not at all without compensation.</summary>
    [Fact]
    public void ParaxialFocus_UndoesABackFocusError_AndNoneDoesNot()
    {
        var lens = X.Singlet();
        var table = X.Table(X.Row("TTHI", 2, 2, -0.5, 0.5));
        var focused = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront);
        var row = focused.Rows[0];
        // The thickness goes t + v, then the compensator adds −v: equal to t up to one rounding.
        Assert.True(Math.Abs(row.AtMin.Change) <= 1e-9 * focused.Nominal, $"{row.AtMin.Change:R}");
        Assert.True(Math.Abs(row.AtMax.Change) <= 1e-9 * focused.Nominal, $"{row.AtMax.Change:R}");
        Assert.Equal(-0.5, row.AtMax.Compensators[0], 9);   // the shift, lens units

        var none = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront with { Compensation = ToleranceCompensation.None });
        Assert.Equal(focused.Nominal, none.Nominal);
        // Derived: 0.5 mm of defocus at y/f = 0.02 is W020 ≈ 0.5·0.02²/2 mm = 1e-4 mm, RMS ≈ 0.29·W020 ≈ 3e-8 m,
        // some twenty times the nominal third-order RMS (≈ 1e-9 m, see the gate fixture).
        Assert.True(none.Rows[0].AtMax.Change > 5 * none.Nominal, $"{none.Rows[0].AtMax.Change:R} against {none.Nominal:R}");
        Assert.Empty(none.Compensators);
    }

    /// <summary>Optimizing a COMP on the back distance does at least as well as holding the
    /// paraxial focus: on axis the best RMS spot lies away from the paraxial focus.</summary>
    [Fact]
    public void OptimizedBackFocus_DoesAtLeastAsWellAsParaxialFocus()
    {
        var lens = X.Singlet();
        var table = X.Table(X.Row("COMP", 2, 0, -5, 5), X.Row("TRAD", 1, 0, -1, 1));
        var o = new ToleranceRunOptions { Criterion = ToleranceCriterion.RmsSpotRadius, Fields = ToleranceFieldSet.UserDefined, Sampling = 4 };
        var paraxial = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, o);
        var optimized = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, o with { Compensation = ToleranceCompensation.OptimizeDls });
        Assert.Equal(new[] { "row 1 COMP: surface 2 thickness" }, optimized.Compensators);
        Assert.True(optimized.Nominal <= paraxial.Nominal * (1 + 1e-9), $"{optimized.Nominal:R} against {paraxial.Nominal:R}");
        var p = paraxial.Rows.Single();
        var q = optimized.Rows.Single();
        Assert.Equal(2, q.Row);
        Assert.True(q.AtMax.Succeeded, q.AtMax.Failure);
        Assert.True(q.AtMax.Criterion <= p.AtMax.Criterion * (1 + 1e-9));
        Assert.True(q.AtMin.Criterion <= p.AtMin.Criterion * (1 + 1e-9));
        Assert.Single(q.AtMax.Compensators);
    }

    /// <summary>The merit function criterion is the lens's own merit function: the nominal equals
    /// its evaluation on the saved lens.</summary>
    [Fact]
    public void MeritFunctionCriterion_IsTheLenssMeritFunction()
    {
        var function = new MeritFunction(new[] { MeritOperandRow.Of("REAY", 3, 1, target: 0, weight: 1, 0, 0, 0, 1) });
        var lens = X.Singlet() with { MeritFunction = MeritFunctionSettings.ToSettings(function) };
        var r = ToleranceAnalysis.Run(lens, X.Table(X.Row("TRAD", 1, 0, -1, 1)), X.NoCatalogs,
            new ToleranceRunOptions { Criterion = ToleranceCriterion.MeritFunction });
        var expected = MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(lens, X.NoCatalogs)).Value;
        Assert.Equal(expected, r.Nominal);
        Assert.True(r.Rows[0].AtMax.Succeeded, r.Rows[0].AtMax.Failure);
        Assert.Equal("", r.Unit);
    }

    [Fact]
    public void BoresightError_OfACentredLens_IsZero()
    {
        var r = ToleranceAnalysis.Run(X.Singlet(), X.Table(X.Row("TRAD", 1, 0, -1, 1)), X.NoCatalogs,
            new ToleranceRunOptions { Criterion = ToleranceCriterion.BoresightError });
        Assert.Equal(0, r.Nominal);
        Assert.Equal(0, r.Rows[0].AtMax.Criterion);   // a radius change keeps the lens centred
        Assert.Equal("rad", r.Unit);
    }

    [Fact]
    public void BadSettings_AreRefusedByName()
    {
        var lens = X.Singlet();
        var table = X.Table(X.Row("TRAD", 1, 0, -1, 1));
        var o = X.Wavefront;
        var below = Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(lens, table, X.NoCatalogs, o with { Mode = ToleranceMode.InverseLimit, Limit = 0 }));
        Assert.Contains("Limit", below.Message);
        var increment = Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(lens, table, X.NoCatalogs, o with { Mode = ToleranceMode.InverseIncrement, Increment = 0 }));
        Assert.Contains("Increment must be positive", increment.Message);
        Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(lens, table, X.NoCatalogs, o with { Sampling = 0 }));
        var noComp = Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(lens, table, X.NoCatalogs, o with { Compensation = ToleranceCompensation.OptimizeOd }));
        Assert.Contains("COMP", noComp.Message);
        var merit = Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(lens, table, X.NoCatalogs, o with { Criterion = ToleranceCriterion.MeritFunction }));
        Assert.Contains("merit function", merit.Message);
        Assert.Throws<ArgumentException>(() => ToleranceAnalysis.QuickSensitivity(lens, table, X.NoCatalogs, o with { Criterion = ToleranceCriterion.StrehlRatio }));
    }

    [Fact]
    public void QuickSensitivity_IsSensitivityWithParaxialFocus()
    {
        var r = ToleranceAnalysis.QuickSensitivity(X.Singlet(), X.Table(X.Row("TRAD", 1, 0, -1, 1)), X.NoCatalogs,
            X.Wavefront with { Mode = ToleranceMode.InverseIncrement, Increment = 1, Compensation = ToleranceCompensation.None });
        Assert.Equal(ToleranceMode.Sensitivity, r.Options.Mode);
        Assert.Equal(ToleranceCompensation.ParaxialFocus, r.Options.Compensation);
        Assert.Equal(new[] { ToleranceCriterion.RmsSpotRadius, ToleranceCriterion.RmsSpotX, ToleranceCriterion.RmsSpotY, ToleranceCriterion.RmsWavefront, ToleranceCriterion.BoresightError },
            ToleranceAnalysis.QuickCriteria);
    }

    [Fact]
    public void ARowThatCannotBeApplied_FailsAlone_AndTheEstimateIsWithheld()
    {
        // TRAD on a plane surface: the perturber refuses it ("a plane, which has no radius").
        var lens = X.Singlet();
        var table = X.Table(X.Row("TRAD", 1, 0, -1, 1), X.Row("TRAD", 3, 0, -1, 1));
        var r = ToleranceAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront);
        Assert.True(r.Rows[0].AtMax.Succeeded);
        Assert.False(r.Rows[1].AtMax.Succeeded);
        Assert.Contains("plane", r.Rows[1].AtMax.Failure);
        Assert.Null(r.EstimatedChange);
        Assert.Contains(r.Notes, n => n.Contains("Row 2 (TRAD)"));
    }

    [Fact]
    public void RunSettings_RoundTripEveryField_AndNameAnUnknownValue()
    {
        var o = new ToleranceRunOptions
        {
            Mode = ToleranceMode.InverseLimit, Criterion = ToleranceCriterion.StrehlRatio, Change = ToleranceChangeMode.RssDifference,
            Compensation = ToleranceCompensation.OptimizeOd, Fields = ToleranceFieldSet.XYSymmetric, Sampling = 5, PsfSampling = 128,
            OptimizerCycles = 3, CompensatorRows = new[] { 2, 4 }, Limit = 0.6, Increment = 0.1, ShowWorst = 7,
            MtfFrequency = 25, EncircledRadius = 7.5,
        };
        var saved = ToleranceRunSettings.ToSettings(o)!;
        var notes = new List<string>();
        var back = ToleranceRunSettings.FromSettings(saved, notes);
        Assert.Empty(notes);
        Assert.Equal(o with { CompensatorRows = null }, back with { CompensatorRows = null });
        Assert.Equal(o.CompensatorRows, back.CompensatorRows);
        Assert.Null(ToleranceRunSettings.ToSettings(new ToleranceRunOptions()));

        var odd = ToleranceRunSettings.FromSettings(new OpticsToleranceRunSettings { Criterion = "GeometricMtf" }, notes);
        Assert.Equal(ToleranceCriterion.RmsSpotRadius, odd.Criterion);
        Assert.Contains(notes, n => n.Contains("GeometricMtf"));

        // The run settings ride on the tolerance data record.
        var data = new OpticsToleranceSettings { Run = saved };
        Assert.Equal("InverseLimit", data.Run!.Mode);
    }
}
