using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;
using OpenSim.Optics.Wavefront;
using OpenSim.Tests.Optics.Gates;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-56: the Optimization Wizard's rows, its ray-aberration and boundary operands, Start At and
/// the renumbering of the rows after it, the vignetting fallback, configurations and the saved
/// settings. OpticStudio's definitions are quoted from its 2025 R2 help (copies under
/// plans/sources/optics/op-54-help and op-56-help); expected values are derived in the comments.
/// The gate is <c>Gates/DefaultMeritGateTests</c>. Lenses are arbitrary inputs.
/// </summary>
public sealed class DefaultMeritWizardTests
{
    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false, SurfaceAperture? aperture = null, double? semi = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, Aperture = aperture, SemiDiameter = semi,
    };

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }.Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(params double[] angles) => DefaultMeritGateTests.Sys(0.02, angles, micrometers: new[] { 0.5, 0.6 });

    private static MeritEvaluation Eval(SequentialLens lens, OpticalSystemData system, params MeritOperandRow[] rows) =>
        MeritEvaluator.Evaluate(new MeritFunction(rows), new SingleMeritConfiguration(lens, system));

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{what}: expected {expected:R}, got {actual:R}.");

    // ---- The operands ----

    [Fact]
    public void TheOperands_AreRegistered_WithTheirColumns()
    {
        var registry = MeritOperandRegistry.CreateDefault();
        foreach (var code in new[] { "TRAR", "TRAX", "TRAY", "ANAR", "ANAX", "ANAY" })
        {
            var d = registry.Get(code);
            Assert.NotNull(d.Ray);                                  // the row's own ray through the evaluator
            Assert.Equal("Wave", d.Int2);
            Assert.Equal(new[] { "Hx", "Hy", "Px", "Py" }, d.Data);
        }
        foreach (var code in new[] { "TRAC", "TRCX", "TRCY", "ANAC", "ANCX", "ANCY", "OPDM", "OPDX" })
        {
            var d = registry.Get(code);
            Assert.Null(d.Ray);                                     // the group is traced by the operand
            Assert.Equal(RayAberrationOperands.RingsLabel, d.Int1);
            Assert.Equal(RayAberrationOperands.ArmsLabel, d.DataLabel(5));
            Assert.Equal(RayAberrationOperands.PerWaveLabel, d.DataLabel(6));
        }
        foreach (var code in new[] { "MNCG", "MXCG", "MNEG", "MNCA", "MXCA", "MNEA" })
            Assert.True(registry.Get(code).IsBound);
        Assert.NotNull(registry.Get("OPDC").Compute);
    }

    [Fact]
    public void TheChiefRayOperands_OnAnIdealLens_MatchTheClosedForm()
    {
        // The ideal lens of the gate (f, the image δ past focus, stop on the lens): a ray at pupil
        // height y leaves with slope u′ = tanθ − y/f, so against the chief ray (y = 0) TRAY =
        // −δ·y/f and TRAX = −δ·x/f with (x, y) = (D/2)(Px, Py), and ANAY = m − m_c with
        // m = u′/√(1 + u′²) (no x component). TRAR is |TRAY| at Px = 0.
        double f = 0.1, delta = 0.002, epd = 0.02, theta = 10;
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Thickness = f + delta, IsStop = true, Shape = new ParaxialShape(f) },
                new SequentialSurface(),
            },
        };
        var system = DefaultMeritGateTests.Sys(epd, new[] { theta }, micrometers: new[] { 0.55 });
        var e = Eval(lens, system,
            MeritOperandRow.Of("TRAY", 0, 1, 0, 1, 0, 1, 0, 1),
            MeritOperandRow.Of("TRAX", 0, 1, 0, 1, 0, 1, 1, 0),
            MeritOperandRow.Of("TRAR", 0, 1, 0, 1, 0, 1, 0, 1),
            MeritOperandRow.Of("ANAY", 0, 1, 0, 1, 0, 1, 0, 1),
            MeritOperandRow.Of("ANAX", 0, 1, 0, 1, 0, 1, 0, 1));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        double mm = OpticsConventions.FromMeters(1, system.Units.LensUnit);
        double y = epd / 2, tan = Math.Tan(theta * Math.PI / 180);
        Close(-delta * y / f * mm, e.Rows[0].Value, 1e-10 * delta * y / f * mm, "TRAY");
        Close(-delta * y / f * mm, e.Rows[1].Value, 1e-10 * delta * y / f * mm, "TRAX");
        Close(delta * y / f * mm, e.Rows[2].Value, 1e-10 * delta * y / f * mm, "TRAR");
        static double M(double u) => u / Math.Sqrt(1 + u * u);
        Close(M(tan - y / f) - M(tan), e.Rows[3].Value, 1e-13, "ANAY");
        Close(0, e.Rows[4].Value, 1e-15, "ANAX");
    }

    [Fact]
    public void Opdc_IsTheOpdEnginesOpd_InWavesOfTheRowsWavelength()
    {
        // OPDC: "Optical path difference with respect to chief ray in waves at the wavelength
        // defined by Wave" — OpdEngine's OPD against the exit-pupil sphere on that wavelength's
        // chief ray, at the table's own field row.
        var lens = DefaultMeritGateTests.Singlet();
        var system = Sys(0, 10);
        var e = Eval(lens, system, MeritOperandRow.Of("OPDC", 0, 2, 0, 1, 0, 1, 0.3, 0.8));
        var expected = OpdEngine.Compute(lens, system, 1, 1, new[] { (0.3, 0.8) }).Samples[0].Waves;
        Assert.True(Math.Abs(e.Rows[0].Value / expected - 1) <= 1e-12, $"OPDC {e.Rows[0].Value:R}, OpdEngine {expected:R}.");
    }

    [Fact]
    public void ACentroidRow_IsItsRayLessTheGroupsWeightedCentroid()
    {
        // TRCY with Gaussian quadrature 2 rings × 6 arms, one wavelength (Per-wave ref 1): the
        // centroid is Σ wᵢ yᵢ / Σ wᵢ over the rule, read here from REAY rows at the image surface
        // (image-surface y of each ray) with the rule's own weights; TRCY = y − ȳ.
        var lens = DefaultMeritGateTests.Singlet();
        var system = Sys(0, 10);
        var rule = PupilQuadrature.Gaussian(2, 6);
        int image = lens.ImageIndex;
        var rows = rule.Points.Select(p => MeritOperandRow.Of("REAY", image, 1, 0, 0, 0, 1, p.Px, p.Py)).ToList();
        rows.Add(MeritOperandRow.Of("TRCY", 2, 1, 0, 1, 0, 1, rule.Points[7].Px, rule.Points[7].Py, 6, 1));
        var e = Eval(lens, system, rows.ToArray());
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        double centroid = 0;
        for (int i = 0; i < rule.Points.Count; i++) centroid += rule.Weights[i] * e.Rows[i].Value;
        centroid /= rule.Weights.Sum();
        Close(e.Rows[7].Value - centroid, e.Rows[^1].Value, 1e-12, "TRCY");
    }

    [Fact]
    public void AGroupColumn_ThatIsNotARule_FailsTheRowNamingTheColumn()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var system = Sys(0);
        var e = Eval(lens, system,
            MeritOperandRow.Of("TRAC", 0, 1, 0, 1, 0, 0, 0, 1, 6, 0),       // 0 rings
            MeritOperandRow.Of("TRAC", 3, 1, 0, 1, 0, 0, 0, 1, 6.5, 0),     // half an arm
            MeritOperandRow.Of("TRAC", 3, 1, 0, 1, 0, 0, 0, 1, 6, 2));      // per-wave flag 2
        Assert.Contains("Rings/Grid (Int1)", e.Rows[0].Error);
        Assert.Contains("Arms (Data5)", e.Rows[1].Error);
        Assert.Contains("Per-wave ref (Data6)", e.Rows[2].Error);
    }

    [Fact]
    public void AVignettedGaussianGroup_FailsItsRows_SayingToUseTheRectangularArray()
    {
        // A circular aperture of 7 mm on the back surface clips the outer rings of a 20 mm beam.
        var lens = Lens(S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064, aperture: new CircularAperture(0, 0.007)));
        var e = Eval(lens, Sys(0), MeritOperandRow.Of("TRAC", 3, 1, 0, 1, 0, 0, 0, 0.1, 6, 0));
        Assert.Equal(MeritRowStatus.Failed, e.Rows[0].Status);
        Assert.Contains("Gaussian quadrature needs every ray", e.Rows[0].Error);
    }

    [Fact]
    public void TheBoundaries_AddTheViolationsOfTheirGlassOrAirSurfaces()
    {
        // The singlet: surface 1 is followed by glass (6 mm), surface 2 by air (64 mm). The MNCT
        // pattern: value = target − Σ violations (minimum), target + Σ violations (maximum).
        // MNEG at Mode 1 (clear semi-diameter a = 10 mm): edge = 6 + sag₂(a) − sag₁(a), sag of a
        // sphere c r²/(1 + √(1 − c² r²)).
        var lens = Lens(S(0.05, 0.006, 1.6, stop: true, semi: 0.01), S(-0.2, 0.064, semi: 0.01));
        var system = Sys(0);
        var e = Eval(lens, system,
            MeritOperandRow.Of("MNCG", 1, 2, 8, 1),
            MeritOperandRow.Of("MXCG", 1, 2, 5, 1),
            MeritOperandRow.Of("MNCA", 1, 2, 1, 1),
            MeritOperandRow.Of("MXCA", 1, 2, 50, 1),
            MeritOperandRow.Of("MNEG", 1, 2, 5, 1, 0, 1),
            MeritOperandRow.Of("MNEA", 1, 2, 0.5, 1, 0, 1));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        Close(8 - (8 - 6), e.Rows[0].Value, 1e-12, "MNCG");
        Close(5 + (6 - 5), e.Rows[1].Value, 1e-12, "MXCG");
        Close(1, e.Rows[2].Value, 1e-12, "MNCA holds");
        Close(50 + (64 - 50), e.Rows[3].Value, 1e-12, "MXCA");
        static double Sag(double radius, double r) { double c = 1 / radius; return c * r * r / (1 + Math.Sqrt(1 - c * c * r * r)); }
        double edge = 6 + Sag(-200, 10) - Sag(50, 10);
        Assert.True(edge < 5);
        Close(5 - (5 - edge), e.Rows[4].Value, 1e-9, "MNEG");
        Close(0.5, e.Rows[5].Value, 1e-12, "MNEA holds");                 // the air edge is about 64 mm
    }

    // ---- The rows the wizard writes ----

    [Fact]
    public void TheRows_CoverEveryWeightedFieldAndWavelength_WithTheirWeights()
    {
        // Spot x + y, centroid, 3 rings × 6 arms, symmetry off: per field and wavelength 18 points,
        // a TRCX and a TRCY row each. Weight = overall × field × wavelength × pupil weight × relative
        // weight; the pupil weights sum to 1, so the TRCX weights sum to 2 × Σ_f w_f Σ_λ w_λ.
        var lens = DefaultMeritGateTests.Singlet();
        var system = DefaultMeritGateTests.Sys(0.02, new[] { 0.0, 10, 14 }, new[] { 1.0, 2, 0 }, new[] { 0.5, 0.6 }, new[] { 1.0, 3 });
        var result = DefaultMeritWizard.Generate(new SingleMeritConfiguration(lens, system), 1, new DefaultMeritOptions
        {
            Criterion = MeritWizardCriterion.SpotXY, Arms = 6, AssumeAxialSymmetry = false, OverallWeight = 2, RelativeXWeight = 1, RelativeYWeight = 0.5,
        });
        var x = result.Rows.Where(r => r.Code == "TRCX").ToArray();
        var y = result.Rows.Where(r => r.Code == "TRCY").ToArray();
        Assert.Equal(2 * 2 * 18, x.Length);                     // the field of weight 0 has no rows
        Assert.Equal(x.Length, y.Length);
        Close(2 * (1 + 2) * (1 + 3), x.Sum(r => r.Weight), 1e-12, "TRCX weights");
        Close(0.5 * 2 * (1 + 2) * (1 + 3), y.Sum(r => r.Weight), 1e-12, "TRCY weights");
        Assert.All(x, r => Assert.Equal((3, 6.0, 0.0), (r.Int1, r.DataAt(5), r.DataAt(6))));
        Assert.Equal(PupilIntegration.GaussianQuadrature, result.Integration);
        Assert.StartsWith("Default merit function: RMS spot x + y, centroid, Gaussian quadrature 3 rings × 6 arms", result.Rows[0].Comment);
    }

    [Theory]
    [InlineData(MeritWizardCriterion.Wavefront, MeritWizardReference.Centroid, "OPDX")]
    [InlineData(MeritWizardCriterion.Wavefront, MeritWizardReference.ChiefRay, "OPDM")]
    [InlineData(MeritWizardCriterion.Wavefront, MeritWizardReference.Unreferenced, "OPDC")]
    [InlineData(MeritWizardCriterion.SpotRadius, MeritWizardReference.ChiefRay, "TRAR")]
    [InlineData(MeritWizardCriterion.SpotY, MeritWizardReference.Centroid, "TRCY")]
    [InlineData(MeritWizardCriterion.Angular, MeritWizardReference.ChiefRay, "ANAX,ANAY")]
    public void EachCriterionAndReference_WritesTheHelpsOperand(MeritWizardCriterion criterion, MeritWizardReference reference, string codes)
    {
        // Help, "Optimization Function Reference Points": centroid wavefront "the operand used is
        // OPDX", chief "OPDM", unreferenced "OPDC".
        var lens = DefaultMeritGateTests.Singlet();
        var result = DefaultMeritWizard.Generate(new SingleMeritConfiguration(lens, Sys(0, 10)), 1,
            new DefaultMeritOptions { Criterion = criterion, Reference = reference });
        Assert.Equal(codes.Split(','), result.Rows.Select(r => r.Code).Where(c => c is not ("BLNK")).Distinct());
        var e = MeritEvaluator.Evaluate(new MeritFunction(result.Rows), new SingleMeritConfiguration(lens, Sys(0, 10)));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        Assert.True(e.Value > 0);
    }

    [Fact]
    public void TheOptions_AreChecked()
    {
        Assert.Contains(DefaultMeritWizard.Validate(new DefaultMeritOptions { Reference = MeritWizardReference.Unreferenced }), p => p.Contains("wavefront criterion only"));
        Assert.Contains(DefaultMeritWizard.Validate(new DefaultMeritOptions { Arms = 7 }), p => p.Contains("Arms"));
        Assert.Contains(DefaultMeritWizard.Validate(new DefaultMeritOptions { Rings = 21 }), p => p.Contains("Rings"));
        Assert.Contains(DefaultMeritWizard.Validate(new DefaultMeritOptions { GlassBoundaries = true, GlassMinimum = 5, GlassMaximum = 2 }), p => p.Contains("glass"));
        Assert.Empty(DefaultMeritWizard.Validate(new DefaultMeritOptions()));
        var lens = DefaultMeritGateTests.Singlet();
        Assert.Throws<ArgumentException>(() => DefaultMeritWizard.Generate(new SingleMeritConfiguration(lens, Sys(0)), 1, new DefaultMeritOptions { Rings = 0 }));
    }

    [Fact]
    public void AVignettedPupil_SwitchesToTheRectangularArray_AndSaysSo()
    {
        // The help: GQ "assumes the pupil is a circle ... if there are surface apertures in the
        // optical system that vignette enough rays ... GQ should not be used". The rows that
        // remain (Delete Vignetted) all trace, so the merit function evaluates without failures.
        var lens = Lens(S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064, aperture: new CircularAperture(0, 0.007)));
        var system = Sys(0, 10);
        var configurations = new SingleMeritConfiguration(lens, system);
        var result = DefaultMeritWizard.Generate(configurations, 1, new DefaultMeritOptions { Criterion = MeritWizardCriterion.SpotRadius, Grid = 8 });
        Assert.Equal(PupilIntegration.RectangularArray, result.Integration);
        Assert.Contains(result.Notes, n => n.Contains("vignetted") && n.Contains("rectangular array"));
        Assert.Contains(result.Notes, n => n.Contains("Delete Vignetted"));
        Assert.All(result.Rows.Where(r => r.Code == "TRAC"), r => Assert.Equal((8, 0.0), (r.Int1, r.DataAt(5))));
        var e = MeritEvaluator.Evaluate(new MeritFunction(result.Rows), configurations);
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));

        // The same lens without the aperture keeps Gaussian quadrature.
        var clear = DefaultMeritWizard.Generate(new SingleMeritConfiguration(DefaultMeritGateTests.Singlet(), system), 1, new DefaultMeritOptions());
        Assert.Equal(PupilIntegration.GaussianQuadrature, clear.Integration);
        Assert.Empty(clear.Notes);
    }

    [Fact]
    public void TheBoundaryRows_CoverSurfaceOneToTheLastBeforeTheImage()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var result = DefaultMeritWizard.Generate(new SingleMeritConfiguration(lens, Sys(0)), 1,
            new DefaultMeritOptions { GlassBoundaries = true, AirBoundaries = true, GlassMinimum = 2, AirMaximum = 500 });
        var codes = result.Rows.Select(r => r.Code).ToArray();
        foreach (var code in new[] { "MNCG", "MXCG", "MNEG", "MNCA", "MXCA", "MNEA" }) Assert.Contains(code, codes);
        Assert.All(result.Rows.Where(r => r.Code.StartsWith("MN") || r.Code.StartsWith("MX")), r => Assert.Equal((1, 2), (r.Int1, r.Int2)));
        Assert.Equal(2, result.Rows.Single(r => r.Code == "MNCG").Target);
        Assert.Equal(500, result.Rows.Single(r => r.Code == "MXCA").Target);
    }

    // ---- Start At and the rows after it ----

    [Fact]
    public void StartAt_DefaultsToTheRowAfterDmfs_AndLaterRowNumbersAreRenumbered()
    {
        // Rows: 1 EFFL, 2 DMFS, 3 CTVA, 4 OPGT Op# 3, 5 ATAN Op# 3 Flag 4, 6 GOTO 7, 7 BLNK.
        var existing = new MeritFunction(new[]
        {
            MeritOperandRow.Of("EFFL", 0, 1), MeritOperandRow.Of("DMFS"), MeritOperandRow.Of("CTVA", 1),
            MeritOperandRow.Of("OPGT", 3), MeritOperandRow.Of("ATAN", 3, 4), MeritOperandRow.Of("GOTO", 7), MeritOperandRow.Of("BLNK"),
        });
        Assert.Equal(3, DefaultMeritWizard.DefaultStartAt(existing));
        Assert.Equal(0, DefaultMeritWizard.DefaultStartAt(new MeritFunction()));
        var generated = new[] { MeritOperandRow.Of("BLNK"), MeritOperandRow.Of("TOTR") };
        var registry = MeritOperandRegistry.CreateDefault();
        var f = DefaultMeritWizard.Insert(existing, generated, 3, registry);
        Assert.Equal(new[] { "EFFL", "DMFS", "BLNK", "TOTR", "CTVA", "OPGT", "ATAN", "GOTO", "BLNK" }, f.Rows.Select(r => r.Code));
        Assert.Equal(5, f.Row(6).Int1);                         // OPGT still reads CTVA
        Assert.Equal((5, 4), (f.Row(7).Int1, f.Row(7).Int2));   // ATAN's Flag is not a row number
        Assert.Equal(9, f.Row(8).Int1);                         // GOTO still goes to the last BLNK
        Assert.Equal(1, f.Row(1).Int2);                         // EFFL's Wave untouched
        Assert.Empty(MeritEvaluator.Validate(f, registry, 1));

        // 0 appends; nothing to renumber.
        var appended = DefaultMeritWizard.Insert(existing, generated, 0, registry);
        Assert.Equal(existing.Rows.Concat(generated), appended.Rows);
    }

    [Fact]
    public void Configurations_AllWritesAConfRowPerConfiguration_AndTheConfigurationIsRestoredAfter()
    {
        var one = DefaultMeritGateTests.Singlet();
        var two = one with { Surfaces = one.Surfaces.Select((s, j) => j == 2 ? s with { Thickness = 0.066 } : s).ToArray() };
        var configurations = new Two(one, two, Sys(0, 10));
        var all = DefaultMeritWizard.Generate(configurations, 2, new DefaultMeritOptions { Configurations = MeritWizardConfigurations.All });
        Assert.Equal(new[] { 1, 2 }, all.Rows.Where(r => r.Code == "CONF").Select(r => r.Int1));
        var current = DefaultMeritWizard.Generate(configurations, 2, new DefaultMeritOptions());
        Assert.Equal(new[] { 2 }, current.Rows.Where(r => r.Code == "CONF").Select(r => r.Int1));

        // Inserted before existing rows, a CONF back to the configuration in effect follows.
        var existing = new MeritFunction(new[] { MeritOperandRow.Of("CONF", 2), MeritOperandRow.Of("DMFS"), MeritOperandRow.Of("CTVA", 2, 0, 0, 1) });
        var f = DefaultMeritWizard.Insert(existing, current.Rows, 3, MeritOperandRegistry.CreateDefault());
        Assert.Equal("CTVA", f.Rows[^1].Code);
        Assert.Equal(("CONF", 2), (f.Rows[^2].Code, f.Rows[^2].Int1));
        var e = MeritEvaluator.Evaluate(f, configurations);
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        Assert.Equal(2, e.Rows[^1].Configuration);
        Close(66, e.Rows[^1].Value, 1e-9, "CTVA 2 in configuration 2");
    }

    private sealed class Two(SequentialLens one, SequentialLens two, OpticalSystemData system) : IMeritConfigurations
    {
        public int Count => 2;
        public SequentialLens LensFor(int configuration) => configuration == 1 ? one : two;
        public OpticalSystemData SystemFor(int configuration) => system;
    }

    // ---- Determinism and the saved settings ----

    [Fact]
    public void TheEvaluation_IsBitwiseTheSame_AtAnyThreadCount_AndOnAFreshLens()
    {
        var system = Sys(0, 10);
        var options = new DefaultMeritOptions { Criterion = MeritWizardCriterion.Wavefront };
        var rows = DefaultMeritWizard.Generate(new SingleMeritConfiguration(DefaultMeritGateTests.Singlet(), system), 1, options).Rows;
        double Run(int? threads) => MeritEvaluator.Evaluate(new MeritFunction(rows), new SingleMeritConfiguration(DefaultMeritGateTests.Singlet(), system),
            options: new MeritEvaluationOptions { MaxDegreeOfParallelism = threads }).Value;
        double a = Run(1), b = Run(null), c = Run(3);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(c));
    }

    [Fact]
    public void TheSettings_RoundTrip_AndAnUnknownChoiceIsNamed()
    {
        var options = new DefaultMeritOptions
        {
            Criterion = MeritWizardCriterion.Angular, Reference = MeritWizardReference.ChiefRay, Integration = PupilIntegration.RectangularArray,
            Rings = 5, Arms = 10, Grid = 12, DeleteVignetted = false, AssumeAxialSymmetry = false, IgnoreLateralColor = true, StartAt = 4,
            OverallWeight = 3, RelativeXWeight = 0.5, RelativeYWeight = 2, RelativeSWeight = 0.25, RelativeTWeight = 4, SpatialFrequency = 30, Configurations = MeritWizardConfigurations.All,
            GlassBoundaries = true, GlassMinimum = 2, GlassMaximum = 9, GlassEdge = 1.5, AirBoundaries = true, AirMinimum = 0.2, AirMaximum = 400, AirEdge = 0.3,
        };
        var notes = new List<string>();
        Assert.Equal(options, DefaultMeritWizardSettings.FromSettings(DefaultMeritWizardSettings.ToSettings(options), notes));
        Assert.Empty(notes);
        Assert.Equal(new DefaultMeritOptions(), DefaultMeritWizardSettings.FromSettings(null, notes));
        var unknown = DefaultMeritWizardSettings.FromSettings(new OpticsMeritWizardSettings { Criterion = "Brightness" }, notes);   // a name no version uses
        Assert.Equal(new DefaultMeritOptions().Criterion, unknown.Criterion);
        Assert.Contains(notes, n => n.Contains("'Brightness'"));
    }
}
