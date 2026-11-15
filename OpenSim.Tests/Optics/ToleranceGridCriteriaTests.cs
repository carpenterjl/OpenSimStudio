using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.EnclosedEnergy;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Tolerancing;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-64's MTF and encircled-energy criteria (wave 7 wiring of OP-39's <see cref="MtfEngine"/> and
/// OP-41's enclosed-energy engines): the nominal criterion is the engine's value at the field, the
/// average MTF is (T + S)/2 (OpticStudio 2025 R2 help, "Tolerancing", Criterion tab: "If average
/// is selected, the average of the tangential and sagittal responses is used"), and a defocus
/// lowers each of them, so the change is negative and counts as a degradation. The singlet is the
/// shared fixture (F/25, 0.55 µm, near the diffraction limit); the frequency and radii are
/// arbitrary test inputs.
/// </summary>
public sealed class ToleranceGridCriteriaTests
{
    private const int Grid = 32;
    private const double Frequency = 20;   // cycles/mm

    /// <summary>The singlet's lens and system as the criterion evaluator prepares them, and its one field.</summary>
    private static (SequentialLens Lens, OpticalSystemData System) Prepared()
    {
        var (lens, system) = ToleranceCriterionEvaluator.Build(X.Singlet(), 1, X.NoCatalogs);
        var setup = RmsCore.Prepare(lens, system, false, "test", new List<string>(), default);
        return (setup.Lens, setup.System);
    }

    /// <summary>The back distance moved by ±0.5 mm, no compensation: about 0.18 waves of defocus
    /// (derived: W020 = δ/(8F²) = 0.5/(8·25²) mm = 1e-4 mm), far more than the singlet's own
    /// third-order aberration (the gate fixture's comments), so the image gets worse both ways.</summary>
    private static ToleranceRunResult Defocused(ToleranceCriterion criterion, double radius = 0) =>
        ToleranceAnalysis.Run(X.Singlet(), X.Table(X.Row("TTHI", 2, 2, -0.5, 0.5)), X.NoCatalogs, new ToleranceRunOptions
        {
            Criterion = criterion, Compensation = ToleranceCompensation.None, Fields = ToleranceFieldSet.UserDefined,
            PsfSampling = Grid, MtfFrequency = Frequency, EncircledRadius = radius,
        });

    [Theory]
    [InlineData(ToleranceCriterion.DiffractionMtfTangential, MtfMethod.Fft)]
    [InlineData(ToleranceCriterion.DiffractionMtfSagittal, MtfMethod.Fft)]
    [InlineData(ToleranceCriterion.GeometricMtfTangential, MtfMethod.Geometric)]
    public void MtfCriterion_IsTheEnginesValue_AndDefocusLowersIt(ToleranceCriterion criterion, MtfMethod method)
    {
        var (lens, system) = Prepared();
        var v = MtfEngine.Evaluate(lens, system, 0, new[] { 0 }, Frequency, new MtfOptions { Method = method, Sampling = Grid });
        double expected = criterion == ToleranceCriterion.DiffractionMtfSagittal ? v.Sagittal : v.Tangential;

        var r = Defocused(criterion);
        // The evaluator appends the field (0, 0) as a field of its own; the same rays, so the same value.
        Assert.Equal(expected, r.Nominal, 12);
        Assert.True(r.Nominal > 0.1 && r.Nominal <= 1, $"{r.Nominal:R}");
        var row = r.Rows[0];
        Assert.True(row.AtMin.Succeeded, row.AtMin.Failure);
        Assert.True(row.AtMax.Succeeded, row.AtMax.Failure);
        Assert.True(row.AtMin.Change < 0, $"{row.AtMin.Change:R}");
        Assert.True(row.AtMax.Change < 0, $"{row.AtMax.Change:R}");
        // Larger is better: the estimate is N − C with the linear difference.
        Assert.Equal(r.Nominal - r.EstimatedChange!.Value, r.EstimatedPerformance!.Value, 15);
    }

    [Fact]
    public void AverageMtf_IsTheMeanOfTangentialAndSagittal()
    {
        double t = Defocused(ToleranceCriterion.DiffractionMtfTangential).Nominal;
        double s = Defocused(ToleranceCriterion.DiffractionMtfSagittal).Nominal;
        double a = Defocused(ToleranceCriterion.DiffractionMtfAverage).Nominal;
        Assert.Equal((t + s) / 2, a, 15);
    }

    [Fact]
    public void DiffractionEncircledEnergy_IsTheEnginesValue_AndDefocusLowersIt()
    {
        // 15 µm lies inside the Airy disk's first dark ring, 1.22·λ·F = 1.22 × 0.55 × 25 ≈ 16.8 µm
        // (derived from the fixture's wavelength and F/#), where a defocus takes energy out.
        const double radius = 15;
        var (lens, system) = Prepared();
        double expected = DiffractionEnclosedEnergy.Fraction(lens, system, 0, new[] { 0 }, Grid, EnclosedEnergyType.Encircled,
            EnclosedEnergyReference.Centroid, radius * 1e-6);
        var r = Defocused(ToleranceCriterion.DiffractionEncircledEnergy, radius);
        Assert.Equal(expected, r.Nominal, 12);
        Assert.True(r.Rows[0].AtMax.Change < 0, $"{r.Rows[0].AtMax.Change:R}");
        Assert.True(r.Rows[0].AtMin.Change < 0, $"{r.Rows[0].AtMin.Change:R}");
    }

    [Fact]
    public void GeometricEncircledEnergy_IsTheEnginesValue_AndDefocusLowersIt()
    {
        // The nominal ray spot is far smaller than 5 µm; 0.5 mm of defocus at F/25 spreads it over a
        // disk of radius 0.5/(2·25) mm = 10 µm (derived), so the fraction within 5 µm drops.
        const double radius = 5;
        var (lens, system) = Prepared();
        double expected = GeometricEnclosedEnergy.Fraction(lens, system, 0, new[] { 0 }, Grid, EnclosedEnergyType.Encircled,
            EnclosedEnergyReference.Centroid, radius * 1e-6);
        var r = Defocused(ToleranceCriterion.GeometricEncircledEnergy, radius);
        Assert.Equal(expected, r.Nominal, 12);
        Assert.Equal(1, r.Nominal, 12);
        Assert.True(r.Rows[0].AtMax.Change < -0.3, $"{r.Rows[0].AtMax.Change:R}");
    }

    [Fact]
    public void MissingFrequencyOrRadius_IsRefused_NamingTheSetting()
    {
        var table = X.Table(X.Row("TTHI", 2, 2, -0.5, 0.5));
        var mtf = Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(X.Singlet(), table, X.NoCatalogs,
            new ToleranceRunOptions { Criterion = ToleranceCriterion.GeometricMtfAverage }));
        Assert.Contains("MTF Frequency", mtf.Message);
        var ee = Assert.Throws<ArgumentException>(() => ToleranceAnalysis.Run(X.Singlet(), table, X.NoCatalogs,
            new ToleranceRunOptions { Criterion = ToleranceCriterion.DiffractionEncircledEnergy }));
        Assert.Contains("radius", ee.Message);
        Assert.All(ToleranceCriteria.All.Where(i => ToleranceCriteria.IsMtf(i.Criterion) || ToleranceCriteria.IsEncircledEnergy(i.Criterion)),
            i => Assert.True(i.HigherIsBetter, i.Name));
        Assert.DoesNotContain(ToleranceCriteria.NotBuilt, n => n.Contains("MTF") || n.Contains("Encircled"));
    }
}
