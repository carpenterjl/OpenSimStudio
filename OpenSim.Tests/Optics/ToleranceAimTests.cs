using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Tolerancing;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// TC-15: tolerance trials whose decenters and tilts make the lens non-axial are traced as they
/// are, with the pupil, fields and paraxial data of their twin with those drawn as zero
/// (docs/Optics-Conventions.md §42, "Trials made non-axial"). Expected values are derived in each test.
/// </summary>
public sealed class ToleranceAimTests
{
    private const double FocalLength = 100;   // mm, a test input
    private const double Pupil = 10;          // mm, a test input

    /// <summary>An ideal lens of focal length f behind a plane stop, object at infinity, image at its focus.</summary>
    private static OpticsSettings IdealLens(string? aiming = null) => new()
    {
        SystemData = new OpticsSystemSettings
        {
            ApertureValue = Pupil,
            RayAiming = aiming,
            Fields = new() { new OpticsFieldSettings() },
            Wavelengths = new() { new OpticsWavelengthSettings { Micrometers = 0.55 } },
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Thickness = 10, IsStop = true },
            new()
            {
                // Paraxial surface: parameter 1 the focal length, parameter 2 the OPD mode (2: object at infinity, §20).
                Type = "Paraxial", Thickness = FocalLength,
                Parameters = new() { new() { Number = 1, Value = FocalLength }, new() { Number = 2, Value = 2 } },
            },
            new(),
        },
    };

    /// <summary>
    /// A TEDX of d on the ideal lens moves the on-axis image by d, so the boresight error is d/f.
    /// Derived: the Paraxial surface sends a ray of slope u at local height x to slope u − x/f
    /// (§20), so every ray parallel to its axis meets its focal point, wherever it hits; decentered
    /// by d (a coordinate break, decenter only) its focal point is at x = d in the image plane f
    /// behind it. So the image of the on-axis field is at radial distance |d| for any aim, and the
    /// boresight error is |d|/f (help, "Tolerancing": "the radial chief ray coordinate traced for the
    /// on axis field divided by the effective focal length"). The nominal is 0, so the sensitivity
    /// (linear difference) is d/f at both signs. Tolerance 1e-12 relative: one refraction and two
    /// transfers of 100 mm in double precision.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Paraxial")]
    public void ATedxSensitivity_IsTheDerivedBoresightShift(string? aiming)
    {
        const double d = 0.1;   // mm, a test input
        var result = ToleranceAnalysis.Run(IdealLens(aiming), X.Table(X.Row("TEDX", 2, 2, -d, d)), X.NoCatalogs,
            new ToleranceRunOptions { Criterion = ToleranceCriterion.BoresightError, Compensation = ToleranceCompensation.None });
        Assert.Equal(0, result.Nominal);
        var row = Assert.Single(result.Rows);
        double expected = d / FocalLength;
        foreach (var t in new[] { row.AtMin, row.AtMax })
        {
            Assert.True(t.Succeeded, t.Failure);
            Assert.True(Math.Abs(t.Criterion - expected) <= 1e-12 * expected, $"{t.Side}: boresight {t.Criterion:R} rad against d/f = {expected:R}");
            Assert.Equal(t.Criterion, t.Change);
        }
        Assert.NotNull(result.EstimatedChange);
    }

    /// <summary>
    /// TSDX ±d on the singlet's front (made Irregular, decentered) gives the same RMS spot radius at
    /// both signs, and more than the nominal. Derived: the lens decentered by −d is the mirror image
    /// in x of the lens decentered by +d, the on-axis field and the pupil quadrature (rings × arms
    /// symmetric about the y axis) are their own mirror images, so the centroid-referenced RMS
    /// radius is the same; tolerance 1e-9 relative for the different rounding of the mirrored rays.
    /// </summary>
    [Fact]
    public void ASurfaceDecenter_IsTraced_AndIsEvenInTheDecenter()
    {
        const double d = 0.2;   // mm, a test input
        var result = ToleranceAnalysis.Run(X.Singlet(), X.Table(X.Row("TSDX", 1, 0, -d, d)), X.NoCatalogs,
            new ToleranceRunOptions { Criterion = ToleranceCriterion.RmsSpotRadius, Compensation = ToleranceCompensation.ParaxialFocus, Fields = ToleranceFieldSet.UserDefined, Sampling = 6 });
        var row = Assert.Single(result.Rows);
        Assert.True(row.AtMin.Succeeded, row.AtMin.Failure);
        Assert.True(row.AtMax.Succeeded, row.AtMax.Failure);
        Assert.True(Math.Abs(row.AtMax.Criterion - row.AtMin.Criterion) <= 1e-9 * row.AtMax.Criterion,
            $"{row.AtMin.Criterion:R} at −d against {row.AtMax.Criterion:R} at +d");
        Assert.True(row.AtMax.Criterion > result.Nominal, $"{row.AtMax.Criterion:R} against the nominal {result.Nominal:R}");
        // The paraxial focus of the twin (the decenter at zero) is the nominal's: no focus shift.
        Assert.Equal(0, row.AtMax.Compensators[0]);
    }

    /// <summary>
    /// The paraxial focus compensator of a trial with a radius change and a decenter is the radius
    /// change's alone: the twin carries the radius change, and the decenter enters the focus only
    /// at second order (§42). Monte Carlo trial k of the table TRAD, TSDX draws TRAD first from the
    /// same stream as the table TRAD alone, so the two runs' trial k have the same radius; the
    /// twin's Irregular surface without terms or placement has the Standard surface's curvature,
    /// so the shifts agree to rounding (1e-12 relative).
    /// </summary>
    [Fact]
    public void TheFocusCompensator_OfADecenteredTrial_IsItsTwinsFocus()
    {
        var options = X.Wavefront;
        var mc = new MonteCarloOptions { Trials = 4, Seed = 11 };
        var both = MonteCarloAnalysis.Run(X.Singlet(), X.Table(X.Row("TRAD", 1, 0, -1, 1), X.Row("TSDX", 1, 0, -0.1, 0.1)), X.NoCatalogs, options, mc);
        var radius = MonteCarloAnalysis.Run(X.Singlet(), X.Table(X.Row("TRAD", 1, 0, -1, 1)), X.NoCatalogs, options, mc);
        Assert.Equal(0, both.Failed);
        for (int k = 0; k < mc.Trials; k++)
        {
            Assert.Equal(radius.Trials[k].Draws[0].Value, both.Trials[k].Draws[0].Value);
            double a = both.Trials[k].Compensators[0], b = radius.Trials[k].Compensators[0];
            Assert.True(Math.Abs(a - b) <= 1e-12 * Math.Abs(b), $"trial {k + 1}: shift {a:R} against {b:R}");
            Assert.True(both.Trials[k].Criterion >= 0);
        }
    }

    /// <summary>A decenter so large that the rays miss the front sphere (radius 100 mm, decentered
    /// by 150 mm) still fails, by name: the twin aims the rays, the trace reports the miss.</summary>
    [Fact]
    public void ARayThatGenuinelyMisses_StillFails()
    {
        var result = ToleranceAnalysis.Run(X.Singlet(), X.Table(X.Row("TEDX", 1, 2, -150, 150)), X.NoCatalogs, X.Wavefront);
        var row = Assert.Single(result.Rows);
        Assert.False(row.AtMin.Succeeded);
        Assert.False(row.AtMax.Succeeded);
        Assert.Null(result.EstimatedChange);
        Assert.Contains(result.Notes, n => n.Contains("Row 1 (TEDX)", StringComparison.Ordinal));
    }

    /// <summary>The Tolerance Wizard's default table on the singlet (surface decenters and tilts, TIR,
    /// element decenters and tilts among its rows) runs in Monte Carlo and Quick Yield with no failed
    /// trial (TC-15; before, every trial carrying them failed).</summary>
    [Fact]
    public void TheWizardsDefaultTable_RunsInMonteCarloAndQuickYield()
    {
        var lens = X.Singlet();
        var table = ToleranceWizard.Generate(lens, new ToleranceTable(), X.NoCatalogs).Table;
        Assert.Contains(table.Rows, r => r.Code == "TEDX");
        Assert.Contains(table.Rows, r => r.Code == "TSDX");
        var mc = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront, new MonteCarloOptions { Trials = 3, Seed = 3, YieldLimit = 1 });
        Assert.True(mc.Failed == 0, mc.Trials.FirstOrDefault(t => !t.Succeeded)?.Failure);
        var quick = QuickYield.Run(lens, table, X.NoCatalogs, X.Wavefront, new MonteCarloOptions { Seed = 3, YieldLimit = 1 }, new QuickYieldOptions { Samples = 20 });
        Assert.True(quick.Succeeded, string.Join("; ", quick.Failures));
    }

    /// <summary>The launcher refuses an aim reference that is not the same lens surface for surface.</summary>
    [Fact]
    public void TheLauncher_RefusesAMismatchedAimReference()
    {
        var lens = OpenSim.Optics.Analysis.ProjectLens.From(IdealLens(), X.NoCatalogs)!;
        var system = SystemDataSettings.FromSettings(IdealLens().SystemData, new List<string>());
        var shorter = lens with { Surfaces = lens.Surfaces.Where((_, j) => j != 1).ToArray() };
        var e = Assert.Throws<ArgumentException>(() => new RayLauncher(lens with { AimReference = shorter }, system));
        Assert.Contains("aim reference", e.Message);
        // The same lens as its own reference launches the same rays.
        var withReference = lens with { AimReference = lens };
        var own = new RayLauncher(lens, system).Launch(SequentialTrace.Create(lens, system), 0, 0, 0.3, 0.7);
        var aimed = new RayLauncher(withReference, system).Launch(SequentialTrace.Create(withReference, system), 0, 0, 0.3, 0.7);
        Assert.Equal(own.Position, aimed.Position);
        Assert.Equal(own.Direction, aimed.Direction);
    }
}
