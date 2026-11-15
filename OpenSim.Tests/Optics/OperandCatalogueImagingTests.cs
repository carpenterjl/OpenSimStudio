using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.EnclosedEnergy;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;
using OpenSim.Tests.Optics.Gates;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-55A: the imaging and ray operands (<see cref="ImagingOperands"/>, docs/Optics-Conventions.md
/// §63). Every registered operand is checked against the engine it wraps, called with the same
/// inputs, and several against closed forms derived in the comments (a thin lens, a plane-parallel
/// plate). Definitions: OpticStudio 2025 R2 help, "Optimization Operands (Alphabetically)" and the
/// category pages (plans/sources/optics/op-54-help/). The lenses are arbitrary inputs: the OP-56
/// gate singlet (<see cref="DefaultMeritGateTests.Singlet"/>), a thin lens and a plate.
/// </summary>
public sealed class OperandCatalogueImagingTests
{
    // ------------------------------------------------------------------ fixtures

    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop,
    };

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }.Concat(surfaces).Append(new SequentialSurface()).ToArray(),
    };

    /// <summary>Thin lens: R = ±100 mm, n = 1.5, zero thickness, stop on it, image 100 mm behind
    /// (f = 1/((n − 1)(c1 − c2)) = 1/(0.5 · 20 m⁻¹) = 100 mm, derived).</summary>
    private static SequentialLens ThinLens() => Lens(S(0.1, 0, 1.5, stop: true), S(-0.1, 0.1));

    /// <summary>Plate: two planes, 10 mm of n = 1.5, stop on the first, 20 mm of air after.</summary>
    private static SequentialLens Plate() => Lens(S(double.PositiveInfinity, 0.01, 1.5, stop: true), S(double.PositiveInfinity, 0.02));

    /// <summary>EPD 10 mm, fields 0° and 10°, one wavelength 0.55 µm.</summary>
    private static OpticalSystemData Sys10() => DefaultMeritGateTests.Sys(0.01, new[] { 0.0, 10 }, micrometers: new[] { 0.55 });

    /// <summary>The singlet's system (as MtfOperandsTests): EPD 20 mm, fields 0° and 10°, 0.5 and 0.6 µm.</summary>
    private static OpticalSystemData Sys() => DefaultMeritGateTests.Sys(0.02, new[] { 0.0, 10 }, micrometers: new[] { 0.5, 0.6 });

    private static MeritEvaluation Eval(SequentialLens lens, OpticalSystemData system, params MeritOperandRow[] rows)
    {
        var e = MeritEvaluator.Evaluate(new MeritFunction(rows), new SingleMeritConfiguration(lens, system));
        return e;
    }

    private static double[] Values(SequentialLens lens, OpticalSystemData system, params MeritOperandRow[] rows)
    {
        var e = Eval(lens, system, rows);
        Assert.False(e.HasFailures, string.Join(Environment.NewLine, e.Errors));
        return e.Rows.Select(r => r.Value).ToArray();
    }

    private static MeritOperandRow R(string code, int int1 = 0, int int2 = 0, params double[] data) => MeritOperandRow.Of(code, int1, int2, 0, 0, data);

    /// <summary>Meters in the default lens unit (mm).</summary>
    private static double L(double meters) => OpticsConventions.FromMeters(meters, LensUnit.Millimeter);

    private static readonly double Deg10 = 10 * Math.PI / 180;

    // ------------------------------------------------------------------ the catalogue

    /// <summary>The codes OP-55A was given (OpticStudio's categories, op-54-help's summary table).</summary>
    public static readonly string[] BriefCodes =
    (
        "AMAG CARD ENPP EFLA EFLX EFLY EPDI EXPD EXPP ISFN ISNA LINV OBSN PIMH PMAG POWF POWP POWR SFNO TFNO WFNO " +
        "ABCD ASTI AXCL BIOC BIOD BSER COMA DIMX DISA DISC DISG DIST FCGS FCGT FCUR LACL LONA OSCD PETC PETZ RSCE RSCH RSRE RSRH RWCE RWCH RWRE RWRH SMIA SPCH SPHA TRAD TRAE TRAI ZERN " +
        "GMTN GMTX MSWA MSWN MSWS MSWT MSWX MTFN MTFX MTHA MTHN MTHS MTHT MTHX " +
        "STRH DENC DENF ERFP GENC GENF XENC XENF " +
        "PANA PANB PANC PARA PARB PARC PARR PARX PARY PARZ PATX PATY " +
        "CEHX CEHY CENX CENY CNAX CNAY CNPX CNPY DXDX DXDY DYDX DYDY HHCN IMAE MNRE MNRI MXRE MXRI OPTH PLEN RAED RAEN RAGA RAGB RAGC RAGX RAGY RAGZ RAID RAIN RANG REAA REAB REAC RENA RENB RENC RETX RETY " +
        "RELI EFNO FOUC BFSD TOLR"
    ).Split(' ');

    [Fact]
    public void EveryBriefCode_IsRegistered_OrNamedNotBuilt_NeverBoth()
    {
        var registry = MeritOperandRegistry.CreateDefault();
        var mine = ImagingOperands.Definitions().Select(d => d.Code).ToHashSet();
        Assert.Equal(134, BriefCodes.Length);   // 133 briefed + TRAD, found by the merged coverage test
        Assert.Equal(BriefCodes.Length, BriefCodes.Distinct().Count());
        foreach (var code in BriefCodes)
        {
            bool registered = registry.Find(code) is not null;
            bool notBuilt = ImagingOperands.NotBuilt.ContainsKey(code);
            Assert.True(registered ^ notBuilt, $"{code}: registered {registered}, not built {notBuilt}");
            if (registered) Assert.Contains(code, mine);
        }
        Assert.All(mine, c => Assert.Contains(c, BriefCodes));
        Assert.All(ImagingOperands.NotBuilt.Keys, c => Assert.Contains(c, BriefCodes));
        Assert.Equal(registry.All.Count, registry.All.Select(d => d.Code).Distinct().Count());
    }

    [Fact]
    public void RegistrySearch_FindsEachOperand_ByCodeAndByName()
    {
        var registry = MeritOperandRegistry.CreateDefault();
        foreach (var d in ImagingOperands.Definitions())
        {
            Assert.Equal(d.Code, registry.Search(d.Code)[0].Code);
            Assert.Equal(d.Code, registry.Search(d.Code.ToLowerInvariant())[0].Code);
            Assert.Contains(registry.Search(d.Name), x => x.Code == d.Code);
            Assert.EndsWith($"({d.Code})", d.DisplayName);
        }
    }

    // ------------------------------------------------------------------ first order

    [Fact]
    public void FirstOrder_OnAThinLens_MatchesTheClosedForms()
    {
        var lens = ThinLens();
        var sys = Sys10();
        var v = Values(lens, sys,
            R("EFLY", 1, 2), R("EFLX", 1, 2), R("POWR", 1, 1), R("ENPP"), R("EPDI"), R("ISFN"),
            R("AMAG", 0, 1), R("EXPP"), R("EXPD"), R("PIMH", 0, 1), R("PMAG", 0, 1), R("OBSN"),
            R("CARD", 1, 2, 1, 0, 1), R("CARD", 1, 2, 1, 0, 3), R("CARD", 1, 2, 1, 0, 5), R("ISNA"), R("LINV", 0, 1));
        Assert.Equal(100, v[0], 9);                         // f = 100 mm (derived in ThinLens)
        Assert.Equal(100, v[1], 9);                         // rotationally symmetric: x = y
        Assert.Equal(0.005, v[2], 12);                      // c(n′ − n) = 10 m⁻¹ · 0.5 = 5 m⁻¹ = 0.005 mm⁻¹
        Assert.Equal(0, v[3], 12);                          // stop on surface 1: the entrance pupil is there
        Assert.Equal(10, v[4], 12);                         // the aperture is the EPD
        Assert.Equal(10, v[5], 9);                          // f / EPD
        Assert.Equal(1, v[6], 12);                          // the chief ray crosses a thin lens at its centre undeviated
        Assert.Equal(-100, v[7], 9);                        // exit pupil at the lens, 100 mm before the image
        Assert.Equal(10, v[8], 9);                          // exit pupil = stop = EPD
        Assert.Equal(100 * Math.Tan(Deg10), v[9], 9);       // f·tan θ
        Assert.Equal(0, v[10], 12);                         // object at infinity
        Assert.Equal(0, v[11], 12);
        Assert.Equal(100, v[12], 9);                        // image focal length n′/Φ
        Assert.Equal(100, v[13], 9);                        // image focal plane from the last vertex
        Assert.Equal(0, v[14], 9);                          // image principal plane at the lens
        Assert.Equal(Math.Sin(Math.Atan(5.0 / 100)), v[15], 12);   // n′ sin(atan |u′|), u′ = −5/100
        Assert.Equal(5 * Math.Tan(Deg10), Math.Abs(v[16]), 9);     // |H| = y·ū with u = 0 (object at infinity)
    }

    [Fact]
    public void FirstOrder_IsTheParaxialModelsValue()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        var v = Values(lens, sys, R("ENPP"), R("EPDI"), R("EXPD"), R("EXPP"), R("ISFN"), R("ISNA"), R("LINV", 0, 2),
            R("PIMH", 0, 2), R("AMAG", 0, 2), R("POWR", 2, 2), R("WFNO"), R("CARD", 1, 2, 2, 1, 4));
        var p = ParaxialSystem.Create(lens.ToParaxialLens(), sys, 0);
        var p2 = ParaxialSystem.Create(lens.ToParaxialLens(), sys, 1);
        Assert.Equal(L(p.EntrancePupilPosition!.Value), v[0], 12);
        Assert.Equal(L(p.EntrancePupilDiameter!.Value), v[1], 12);
        Assert.Equal(L(p.ExitPupilDiameter!.Value), v[2], 12);
        Assert.Equal(L(p.ExitPupilPosition!.Value - lens.Surfaces[2].Thickness), v[3], 12);
        Assert.Equal(p.ImageSpaceFNumber!.Value, v[4], 12);
        Assert.Equal(p.ImageNumericalAperture, v[5], 12);
        Assert.Equal(L(p2.LagrangeInvariant), v[6], 12);
        Assert.Equal(L(p2.ParaxialImageHeight!.Value), v[7], 12);
        Assert.Equal(p2.AngularMagnification!.Value, v[8], 12);
        Assert.Equal(p2.Powers[2] / 1000, v[9], 15);
        var w = RealWorkingFNumber.Compute(SequentialTrace.Create(lens, sys, 0), new RayLauncher(lens, sys), 0, 0);
        Assert.Equal(w.Value, v[10], 12);
        Assert.Equal(L(CardinalPlanes.Compute(p2, 1, 2).Principal!.Value.Object), v[11], 12);
    }

    [Fact]
    public void FirstOrder_Refusals_NameTheReason()
    {
        var e = Eval(ThinLens(), Sys10(), R("CARD", 1, 2, 1, 0, 12), R("EFLY", 2, 1), R("POWR", 0, 1));
        Assert.Equal(3, e.Errors.Count);
        Assert.Contains(e.Errors, x => x.Contains("Data 12"));
        Assert.Contains(e.Errors, x => x.Contains("in order"));
        Assert.Contains(e.Errors, x => x.Contains("no power"));
    }

    // ------------------------------------------------------------------ Seidel and chromatic

    [Fact]
    public void Seidel_IsTheSeidelSumInWaves()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        var v = Values(lens, sys, R("SPHA", 0, 1), R("SPHA", 1, 1), R("COMA", 0, 2), R("ASTI", 2, 1), R("FCUR", 0, 1), R("DIST", 1, 1),
            R("PETZ", 0, 1), R("PETC", 0, 1));
        var r1 = SeidelEngine.Compute(lens, sys, 0);
        var r2 = SeidelEngine.Compute(lens, sys, 1);
        double w1 = r1.WavelengthMicrometers * 1e-6, w2 = r2.WavelengthMicrometers * 1e-6;
        Assert.Equal(r1.Sum.S1 / w1, v[0], 9);
        Assert.Equal(r1.Surfaces[0].Terms.S1 / w1, v[1], 9);
        Assert.Equal(r2.Sum.S2 / w2, v[2], 9);
        Assert.Equal(r1.Surfaces[1].Terms.S3 / w1, v[3], 9);
        Assert.Equal(r1.Sum.S4 / w1, v[4], 9);
        Assert.Equal(r1.Surfaces[0].Terms.S5 / w1, v[5], 9);
        Assert.Equal(L(r1.PetzvalRadius), v[6], 9);
        Assert.Equal(1 / L(r1.PetzvalRadius), v[7], 12);
        // SPHA's sum is W040 times 8 (S1 = 8 W040, help "Seidel Coefficients"): positive for this
        // undercorrected singlet, as the transverse coefficient's sign shows.
        Assert.Equal(8 * r1.Wave(r1.Sum).W040, v[0], 9);
    }

    [Fact]
    public void Chromatic_AndLongitudinal_AreTheEnginesValues()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        var v = Values(lens, sys, R("LACL", 1, 2), R("LONA", 0, 1, 0), R("LONA", 0, 2, 1), R("AXCL", 1, 2, 0), R("AXCL", 1, 2, 0.7));
        var pl = lens.ToParaxialLens();
        var p1 = ParaxialSystem.Create(pl, sys, 0);
        var p2 = ParaxialSystem.Create(pl, sys, 1);
        Assert.Equal(L(p1.ImageSurfaceChiefHeight - p2.ImageSurfaceChiefHeight), v[0], 12);
        double c1 = FieldScanCommon.ParaxialCrossing(p1, p1.MarginalRay), c2 = FieldScanCommon.ParaxialCrossing(p2, p2.MarginalRay);
        Assert.Equal(L(c1), v[1], 12);
        var setup = FieldScanCommon.Prepare(lens, VignettingTools.Clear(sys), new List<string>(), default);
        var failures = new List<string>();
        var real = LongitudinalAberrationAnalysis.Crossings(setup, new[] { 0, 1 }, new[] { 1.0, 0.7 }, failures, default);
        Assert.Empty(failures);
        Assert.Equal(L(real[1][0]), v[2], 12);
        Assert.Equal(L(c1 - c2), v[3], 12);
        Assert.Equal(L(real[0][1] - real[1][1]), v[4], 12);
    }

    [Fact]
    public void FieldCurvature_AndRealDistortion_AreTheFieldScansValues()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        var v = Values(lens, sys, R("FCGT", 0, 1, 0, 1), R("FCGS", 0, 1, 0, 0.5), R("DIST", 0, 1, 0), R("DIST", 0, 1, 1));
        var system = FieldScanCommon.ScanSystem(VignettingTools.Clear(sys), true, new List<string>());
        var setup = FieldScanCommon.Prepare(lens, system, new List<string>(), default);
        var full = FieldCurvatureDistortion.Compute(setup, new[] { 0 }, FieldScan.PlusY, new[] { 1.0 }, DistortionReference.FTanTheta, default)[0].Points[0];
        var half = FieldCurvatureDistortion.Compute(setup, new[] { 0 }, FieldScan.PlusY, new[] { 0.5 }, DistortionReference.FTanTheta, default)[0].Points[0];
        Assert.Equal(L(full.Tangential), v[0], 12);
        Assert.Equal(L(half.Sagittal), v[1], 12);
        Assert.Equal(full.DistortionPercent, v[2], 12);
        Assert.Equal(L(full.RealHeight - full.ReferenceHeight), v[3], 12);

        var off = Eval(lens, sys, R("FCGT", 0, 1, 0.5, 0.5));
        Assert.Contains(off.Errors, x => x.Contains("off both axes"));
    }

    [Fact]
    public void Rms_IsTheRmsCoresValue()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        var v = Values(lens, sys, R("RSCH", 3, 1, 0, 1), R("RSCE", 3, 0, 0, 1), R("RWRE", 4, 2, 0, 0.5), R("RWCH", 2, 0, 0, 1), R("RSRH", 4, 1, 0, 1));
        var setup = RmsCore.Prepare(lens, sys, false, "test", new List<string>(), default);
        RmsValue At(double hy, int[] rows, bool poly, RmsQuantity q, RmsReference r, PupilQuadratureRule rule) =>
            RmsCore.Evaluate(setup, new RmsPoint { FieldX = 0, FieldY = 10 * hy, Rows = rows, Polychromatic = poly, Label = "t" }, q, r, rule, default);
        Assert.Equal(L(At(1, new[] { 0 }, false, RmsQuantity.SpotRadius, RmsReference.ChiefRay, PupilQuadrature.Gaussian(3)).Value), v[0], 12);
        Assert.Equal(L(At(1, new[] { 0, 1 }, true, RmsQuantity.SpotRadius, RmsReference.Centroid, PupilQuadrature.Gaussian(3)).Value), v[1], 12);
        Assert.Equal(At(0.5, new[] { 1 }, false, RmsQuantity.WavefrontRms, RmsReference.Centroid, PupilQuadrature.Rectangular(8)).Value, v[2], 12);
        Assert.Equal(At(1, new[] { 0, 1 }, true, RmsQuantity.WavefrontRms, RmsReference.ChiefRay, PupilQuadrature.Gaussian(2)).Value, v[3], 12);
        Assert.Equal(L(At(1, new[] { 0 }, false, RmsQuantity.SpotRadius, RmsReference.ChiefRay, PupilQuadrature.Rectangular(8)).Value), v[4], 12);
    }

    [Fact]
    public void Zernike_IsTheFieldFitsCoefficient_AndBoresight_IsZeroOnACentredLens()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        var v = Values(lens, sys, R("ZERN", 4, 1, 1, 2, 1), R("ZERN", 9, 1, 1, 2, 0), R("BSER", 0, 1));
        var options = new OpdOptions { Reference = OpdReference.ExitPupil, ReferencePoint = OpdReferencePoint.ChiefRay };
        var pupil = PsfEngine.Nodes(32).Pupil;
        var standard = FieldZernike.Fit(lens, sys, 1, 0, new ZernikeBasis(ZernikeOrdering.Standard, 11), pupil, options);
        var fringe = FieldZernike.Fit(lens, sys, 1, 0, new ZernikeBasis(ZernikeOrdering.Fringe, 11), pupil, options);
        Assert.Equal(standard.Z(4), v[0], 12);
        Assert.Equal(fringe.Z(9), v[1], 12);
        Assert.True(Math.Abs(v[2]) < 1e-15, $"BSER {v[2]}");      // the on-axis chief ray meets the image vertex

        var e = Eval(lens, sys, R("ZERN", -2, 1, 1, 2, 0), R("ZERN", 4, 1, 1, 2, 2), R("ZERN", 38, 1, 1, 2, 0));
        Assert.Equal(3, e.Errors.Count);
        Assert.Contains(e.Errors, x => x.Contains("not built"));
        Assert.Contains(e.Errors, x => x.Contains("annular"));
        Assert.Contains(e.Errors, x => x.Contains("37"));
    }

    // ------------------------------------------------------------------ MTF, PSF, enclosed energy

    [Fact]
    public void MtfMinimaMaxima_AndSquareWave_AreTheEnginesValues()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        const double freq = 20;
        var v = Values(lens, sys,
            R("MTFT", 1, 1, 2, freq), R("MTFS", 1, 1, 2, freq), R("MTFN", 1, 1, 2, freq), R("MTFX", 1, 1, 2, freq),
            R("GMTT", 1, 0, 2, freq), R("GMTS", 1, 0, 2, freq), R("GMTN", 1, 0, 2, freq), R("GMTX", 1, 0, 2, freq),
            R("MSWT", 1, 1, 2, freq), R("MSWS", 1, 1, 2, freq), R("MSWA", 1, 1, 2, freq), R("MSWN", 1, 1, 2, freq), R("MSWX", 1, 1, 2, freq));
        Assert.Equal(Math.Min(v[0], v[1]), v[2]);
        Assert.Equal(Math.Max(v[0], v[1]), v[3]);
        Assert.Equal(Math.Min(v[4], v[5]), v[6]);
        Assert.Equal(Math.Max(v[4], v[5]), v[7]);
        var sw = MtfEngine.Evaluate(lens, sys, 1, new[] { 0 }, freq, new MtfOptions { Sampling = 32, SquareWave = true });
        Assert.Equal(sw.Tangential, v[8], 14);
        Assert.Equal(sw.Sagittal, v[9], 14);
        Assert.Equal(sw.Average, v[10], 14);
        Assert.Equal(Math.Min(sw.Tangential, sw.Sagittal), v[11], 14);
        Assert.Equal(Math.Max(sw.Tangential, sw.Sagittal), v[12], 14);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void HuygensMtf_AndStrehl_AreTheEnginesValues()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        const double freq = 20;
        var v = Values(lens, sys, R("MTHT", 1, 1, 1, freq), R("MTHS", 1, 1, 1, freq), R("MTHA", 1, 1, 1, freq),
            R("MTHN", 1, 1, 1, freq), R("MTHX", 1, 1, 1, freq), R("STRH", 1, 1, 1));
        var h = MtfEngine.Evaluate(lens, sys, 0, new[] { 0 }, freq, new MtfOptions { Method = MtfMethod.Huygens, Sampling = 32, ImageSampling = 32 });
        Assert.Equal(h.Tangential, v[0], 14);
        Assert.Equal(h.Sagittal, v[1], 14);
        Assert.Equal(h.Average, v[2], 14);
        Assert.Equal(Math.Min(h.Tangential, h.Sagittal), v[3], 14);
        Assert.Equal(Math.Max(h.Tangential, h.Sagittal), v[4], 14);
        // STRH: the Huygens run at half the help's default image delta λF/√n_p ("2x smaller").
        var pupil = PsfEngine.BuildHuygens(lens, sys, 0, 0, 32, PsfRuns.Options(false, true, false));
        double delta = PsfEngine.DefaultHuygensDelta(pupil.Wavelength, pupil.FNumber, 32) / 2;
        var run = PsfRuns.Huygens(lens, sys, 0, new[] { 0 }, 32, 32, delta, false, true, false, null, default);
        Assert.Equal(run.Strehl, v[5], 14);
        Assert.InRange(v[5], 0, 1.0 + 1e-9);

        var e = Eval(lens, sys, R("STRH", 1, 1, 1, 1), R("MTHT", 1, 1, 1, freq, 0, 1));
        Assert.Contains(e.Errors, x => x.Contains("Pol?"));
        Assert.Contains(e.Errors, x => x.Contains("All Conf?"));
    }

    [Fact]
    public void EnclosedEnergy_IsTheEnginesValue_AndTheDistanceInvertsIt()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        // The diffraction rows use the F/10 thin lens, whose PSF sits well inside the FFT window.
        var d = Values(ThinLens(), Sys10(), R("DENF", 1, 1, 1, 10, 1, 0), R("DENC", 1, 1, 1, 0.5, 1, 0));
        var setup = DiffractionEnclosedEnergy.Prepare(ThinLens(), Sys10(), 0, new[] { 0 }, 32, EnclosedEnergyReference.ChiefRay);
        Assert.Equal(setup.Fraction(EnclosedEnergyType.Encircled, 10e-6), d[0], 14);
        Assert.Equal(0.5, setup.Fraction(EnclosedEnergyType.Encircled, d[1] * 1e-6), 9);

        var v = Values(lens, sys,
            R("GENF", 1, 1, 2, 20, 1, 1, 1), R("GENC", 1, 1, 2, 0.5, 1, 1, 1), R("GENF", 1, 1, 2, 20, 1, 0, 0), R("GENF", 1, 1, 2, 20, 4, 0, 1));
        var centroid = GeometricEnclosedEnergy.Trace(lens, sys, 1, new[] { 0 }, 32, EnclosedEnergyReference.Centroid);
        Assert.Equal(centroid.Fraction(EnclosedEnergyType.Encircled, 20e-6), v[0], 14);
        double r = v[1] * 1e-6;
        Assert.True(centroid.Fraction(EnclosedEnergyType.Encircled, r) >= 0.5);
        Assert.True(centroid.Fraction(EnclosedEnergyType.Encircled, r * (1 - 1e-9)) < 0.5);
        var chief = GeometricEnclosedEnergy.Trace(lens, sys, 1, new[] { 0 }, 32, EnclosedEnergyReference.ChiefRay);
        var airy = EnclosedEnergyRuns.AiryParameters(lens, sys, new[] { 0 }, "test");
        Assert.Equal(chief.Fraction(EnclosedEnergyType.Encircled, 20e-6) * AiryEnclosedEnergy.AtRadius(20e-6, airy.Lambda, airy.FNumber), v[2], 14);
        Assert.Equal(chief.Fraction(EnclosedEnergyType.Ensquared, 20e-6), v[3], 14);

        var e = Eval(lens, sys, R("DENC", 1, 1, 1, 0.5, 1, 3), R("GENF", 1, 1, 2, 20, 2, 0, 0), R("DENC", 1, 1, 1, 1.5, 1, 0));
        Assert.Contains(e.Errors, x => x.Contains("Huygens"));
        Assert.Contains(e.Errors, x => x.Contains("encircled type only"));
        Assert.Contains(e.Errors, x => x.Contains("strictly between"));
    }

    [Fact]
    public void RelativeIllumination_AndEffectiveF_AreTheEnginesValues()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        var v = Values(lens, sys, R("RELI", 7, 1, 2), R("EFNO", 7, 1, 2), R("RELI", 7, 1, 1));
        var system = FieldScanCommon.ScanSystem(sys, false, new List<string>());
        var setup = FieldScanCommon.Prepare(lens, system, new List<string>(), default);
        var result = RelativeIllumination.Compute(setup, 0, FieldScan.PlusY, 7, new[] { 0, 1.0 }, null, default);
        Assert.Equal(result.ProjectedSolidAngle[1] / result.ProjectedSolidAngle[0], v[0], 14);
        Assert.Equal(result.EffectiveFNumber[1], v[1], 14);
        Assert.Equal(1, v[2], 14);                                // the on-axis field against itself
        Assert.Contains(Eval(lens, sys, R("RELI", 4, 1, 2)).Errors, x => x.Contains("5 to 400"));
    }

    [Fact]
    public void BestFitSphere_OfASphere_IsTheSphere_AndIsTheFitsValue()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        var v = Values(lens, sys, R("BFSD", 1, 0), R("BFSD", 1, 1), R("BFSD", 2, 3, 0, 6), R("BFSD", 2, 7, 0, 6));
        // R1 = 50 mm: the best-fit sphere of a sphere is itself, to the fit's line-search tolerance.
        Assert.InRange(v[0], 1 / 50.0 * (1 - 1e-6), 1 / 50.0 * (1 + 1e-6));
        Assert.InRange(v[1], 50 * (1 - 1e-6), 50 * (1 + 1e-6));
        var s = lens.Surfaces[2];
        double sd = SemiDiameterSizing.Compute(lens, sys).Clear[2];   // BFSD's probe step is a fraction of the semi-diameter, as the Sag Table's
        var probe = new SurfaceProbe(s, SurfaceShapeAnalysisBase<SagTableSettings>.DifferenceStepFraction * sd);
        int air = BestFitSphere.AirSign(lens, sys, 2, false, out _);
        var fit = BestFitSphere.Fit(probe, 0, 0.006, BestFitSphereCriterion.MinimumVolume, air, "test");
        Assert.Equal(L(fit.MaxDepthToRemove), v[2], 12);
        Assert.Equal(fit.RmsSlopeDeviation, v[3], 12);
    }

    // ------------------------------------------------------------------ paraxial rays

    [Fact]
    public void ParaxialRays_OnAPlate_MatchTheClosedForms()
    {
        // Plate (stop on surface 1, t = 10 mm, n = 1.5), field 10°: the paraxial chief ray has
        // slope tan 10° in air and tan 10°/n in the glass (ν = n·u is unchanged at a plane), height
        // 0 on surface 1; the marginal ray keeps the pupil radius r = 5 mm. Derived.
        double t = 10, n = 1.5, r = 5, u = Math.Tan(Deg10) / n;
        var v = Values(Plate(), Sys10(),
            R("PARY", 2, 1, 0, 1, 0, 0.5), R("PATY", 1, 1, 0, 1, 0, 0), R("PARX", 2, 1, 1, 0, 0, 0),
            R("PARR", 2, 1, 1, 0, 0, 1), R("PATX", 2, 1, 1, 0, 0, 0), R("PARY", 1, 1, 0, 0, 0, 1));
        Assert.Equal(0.5 * r + t * u, v[0], 12);
        Assert.Equal(u, v[1], 14);
        Assert.Equal(t * u, v[2], 12);
        Assert.Equal(Math.Sqrt(t * u * t * u + r * r), v[3], 12);
        Assert.Equal(Math.Tan(Deg10), v[4], 14);                  // back in air after surface 2
        Assert.Equal(r, v[5], 12);
    }

    // ------------------------------------------------------------------ real rays

    [Fact]
    public void RealRays_OnAPlate_MatchTheClosedForms()
    {
        // Snell at a plane: sin θ′ = sin θ / n; the leaving direction's y cosine is sin θ′, its z
        // cosine cos θ′; the normal is +z; the path in the glass is n·t/cos θ′. Derived.
        double n = 1.5, t = 10, sin = Math.Sin(Deg10), sinIn = sin / n, cosIn = Math.Sqrt(1 - sinIn * sinIn);
        double thetaIn = Math.Asin(sinIn);
        var lens = Plate();
        var sys = Sys10();
        double[] chief = { 0, 1, 0, 0 };
        MeritOperandRow H(string code, int surf, params double[] ray) => R(code, surf, 1, ray.Length == 0 ? chief : ray);
        var v = Values(lens, sys,
            H("RAID", 1), H("RAED", 1), H("RAIN", 1), H("RAEN", 1), H("REAB", 1), H("REAC", 1), H("REAA", 1),
            H("RENA", 1), H("RENB", 1), H("RENC", 1), H("RETY", 1), H("RANG", 1), H("RAED", 2), H("REAY", 3),
            H("RAGZ", 2), H("RAGY", 2), H("REAY", 2), H("RAGB", 2), H("HHCN", 2), H("TRAI", 2, 0, 1, 0, 1),
            R("PLEN", 1, 2, 0, 1, 0, 0), H("OPTH", 1), H("OPTH", 1, 0, 1, 0, 1), H("RETX", 1));
        Assert.Equal(10, v[0], 10);
        Assert.Equal(thetaIn * 180 / Math.PI, v[1], 10);
        Assert.Equal(Math.Cos(Deg10), v[2], 12);
        Assert.Equal(cosIn, v[3], 12);
        Assert.Equal(sinIn, Math.Abs(v[4]), 12);
        Assert.Equal(Math.Sign(v[13]), Math.Sign(v[4]));          // the ray leans the way it lands
        Assert.Equal(cosIn, v[5], 12);
        Assert.Equal(0, v[6], 14);
        Assert.Equal((0.0, 0.0, 1.0), (v[7], v[8], v[9]));
        Assert.Equal(sinIn / cosIn, Math.Abs(v[10]), 12);
        Assert.Equal(thetaIn, v[11], 12);
        Assert.Equal(10, v[12], 10);                               // out of the plate at the angle it came in
        Assert.Equal(t, v[14], 12);                                // surface 2's vertex is 10 mm along z
        Assert.Equal(v[16], v[15], 14);                            // no tilt: global y is local y
        Assert.Equal(sin, Math.Abs(v[17]), 12);
        Assert.Equal(0, v[18]);                                    // a plane is never a hyperhemisphere
        Assert.Equal(5, v[19], 12);                                // parallel rays from pupil y = 5 mm keep Δy = 5 mm on any z plane
        Assert.Equal(n * t / cosIn, v[20], 10);
        Assert.Equal(0, v[21], 12);                                // the reference plane passes through the chief ray's surface-1 point
        Assert.Equal(5 * sin, v[22], 10);                          // n0·(r ŷ)·d, d = (0, sin θ, cos θ)
        Assert.Equal(0, v[23], 14);
    }

    [Fact]
    public void RealRays_AreTheTracesHits_OnTheSinglet()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var sys = Sys();
        double[] ray = { 0.3, 0.7, -0.2, 0.6 };
        var v = Values(lens, sys, R("RAID", 2, 2, ray), R("RENB", 2, 2, ray), R("REAA", 2, 2, ray), R("RAGX", 3, 2, ray), R("OPTH", 2, 2, ray), R("HHCN", 1, 2, ray));
        var trace = SequentialTrace.Create(lens, sys, 1);
        var path = trace.TracePath(new RayLauncher(lens, sys).Launch(trace, 0.3, 0.7, -0.2, 0.6));
        var h2 = path.Hits.First(h => h.Surface == 2);
        Assert.Equal(h2.AngleOfIncidenceDegrees, v[0], 12);
        Assert.Equal(h2.Normal.Y, v[1], 14);
        Assert.Equal(h2.Direction.X, v[2], 14);
        Assert.Equal(L(path.Hits.First(h => h.Surface == 3).GlobalPosition.X), v[3], 12);
        Assert.Equal(0, v[5]);
    }

    [Fact]
    public void AngleBounds_AreTheTargetWhileTheyHold_ElseTheExtreme()
    {
        // Plate, chief ray at 10°: incidence 10° on surface 1 and θ′ on surface 2 (derived).
        double thetaIn = Math.Asin(Math.Sin(Deg10) / 1.5) * 180 / Math.PI;
        var v = Values(Plate(), Sys10(),
            MeritOperandRow.Of("MXRI", 1, 2, 5, 1, 0, 1, 0, 0, 0),     // max 10 > 5: violated, the value is 10
            MeritOperandRow.Of("MXRI", 1, 2, 30, 1, 0, 1, 0, 0, 0),    // holds: the target
            MeritOperandRow.Of("MNRI", 1, 2, 20, 1, 0, 1, 0, 0, 0),    // min θ′ < 20: violated, the value is θ′
            MeritOperandRow.Of("MNRE", 1, 2, 1, 1, 0, 1, 0, 0, 1));    // min exit angle θ′ > 1: holds
        Assert.Equal(10, v[0], 10);
        Assert.Equal(30, v[1]);
        Assert.Equal(thetaIn, v[2], 10);
        Assert.Equal(1, v[3]);
    }
}
