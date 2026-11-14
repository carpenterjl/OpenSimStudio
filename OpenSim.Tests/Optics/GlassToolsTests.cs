using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-14: glass tools and material plots. The plan's gates: fitting synthetic data generated
/// from a known Sellmeier set recovers the coefficients; the glass map places every glass of a
/// catalog at its own computed nd, Vd. Also the model glass, the offsets, the comparison, the
/// athermal map against SCHOTT TIE-19 Table 2 and the transmittance scaling.
/// </summary>
public sealed class GlassToolsTests
{
    private const double Eps = 2.220446049250313e-16;
    private readonly ITestOutputHelper _out;

    public GlassToolsTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Five SCHOTT glasses as the AGF catalog shipped with ZemaxGlass (MIT, N. Hagen) prints them:
    /// plans/sources/optics/zemaxglass/AGF_files/schott.agf, the NM (nd, Vd), ED (expansion
    /// −30/70 °C and ΔPgF), CD, TD and LD lines of F2 (line 3), N-BK7 (line 744), N-LAF2
    /// (line 1210), N-PK51 (line 2009) and SF57 (line 3481). The CD and TD numbers are the same as
    /// ThermalIndexTests copies.
    /// </summary>
    internal static OpticalMaterial Schott(string name) => name switch
    {
        "N-BK7" => Make(name, "1.5168", "64.17", 7.1, -0.0009, 0.3,
            new[] { 1.039612120E+00, 6.000698670E-03, 2.317923440E-01, 2.001791440E-02, 1.010469450E+00, 1.035606530E+02 },
            new ThermalData(1.860000E-06, 1.310000E-08, -1.370000E-11, 4.340000E-07, 6.270000E-10, 1.700000E-01, 2.000000E+01)),
        "SF57" => Make(name, "1.84666", "23.83", 8.3, 0.0123, 0.37,
            new[] { 1.816513710E+00, 1.437041980E-02, 4.288936410E-01, 5.928011720E-02, 1.071862780E+00, 1.214199420E+02 },
            new ThermalData(7.260000E-06, 1.880000E-08, -5.140000E-11, 1.960000E-06, 1.790000E-09, 2.760000E-01, 2.000000E+01)),
        "N-PK51" => Make(name, "1.52855", "76.98", 12.35, 0.0258, 0.3,
            new[] { 1.156107750E+00, 5.855974020E-03, 1.532293440E-01, 1.940724160E-02, 7.856189660E-01, 1.405370460E+02 },
            new ThermalData(-1.980000E-05, -6.060000E-09, 1.600000E-11, 4.160000E-07, 5.010000E-10, 1.340000E-01, 2.000000E+01)),
        "F2" => Make(name, "1.62004", "36.37", 8.2, 0.0002, 0.32,
            new[] { 1.345333590E+00, 9.977438710E-03, 2.090731760E-01, 4.704507670E-02, 9.373571620E-01, 1.118867640E+02 },
            new ThermalData(1.510000E-06, 1.560000E-08, -2.780000E-11, 9.340000E-07, 1.040000E-09, 2.500000E-01, 2.000000E+01)),
        "N-LAF2" => Make(name, "1.74397", "44.85", 8.06, -0.0027, 0.35,
            new[] { 1.809842270E+00, 1.017116220E-02, 1.572955500E-01, 4.424317650E-02, 1.093003700E+00, 1.006877480E+02 },
            new ThermalData(-3.640000E-06, 9.200000E-09, -6.000000E-12, 6.430000E-07, 6.110000E-10, 2.200000E-01, 2.000000E+01)),
        _ => throw new ArgumentException(name)
    };

    private static readonly string[] Five = { "N-BK7", "SF57", "N-PK51", "F2", "N-LAF2" };

    private static OpticalMaterial Make(string name, string nd, string vd, double tce, double dpgf, double ldMin, double[] cd, ThermalData td)
    {
        PrintedNumber.TryParse(nd, out var pnd);
        PrintedNumber.TryParse(vd, out var pvd);
        return new OpticalMaterial
        {
            Name = name, Catalog = "schott", FormulaNumber = 2, Coefficients = cd, Thermal = td, MinMicrometers = ldMin, MaxMicrometers = 2.5,
            Nd = pnd, Vd = pvd, ThermalExpansion = tce, DeltaPgF = dpgf
        };
    }

    private static OpticalCatalog FiveSchott() => new() { Name = "schott", Materials = Five.Select(Schott).ToArray() };

    private static OpticalCatalog Fixture(string file) => AgfReader.Read(AgfReaderTests.FixturePath(file));

    // ---------------- Gate 1: fitting recovers a known Sellmeier set ----------------

    /// <summary>
    /// The plan's first gate. Indices generated from N-BK7's Sellmeier 1 coefficients at 41
    /// evenly spaced wavelengths are fitted with no starting values; every coefficient comes back.
    /// <para>
    /// <b>Tolerance.</b> The data are exact up to the rounding of the formula's evaluation, a few
    /// ulps of n: ‖δn‖₂ ≤ 4ε·n_max·√m. To first order a coefficient then moves by at most
    /// s_j·‖δn‖₂, where s_j is the length of row j of the Jacobian's pseudo-inverse; s_j is
    /// computed here independently of the fitter, from a central-difference Jacobian of
    /// <see cref="DispersionFormulas.Evaluate"/> and the first-party SVD. The tolerance is ten
    /// times that bound, for the nonlinearity and the solver's own stopping tolerance (1e-14 of
    /// the step), plus 1e-13 of the coefficient. When written the largest miss was about 5 % of
    /// s_j·2ε·√m, i.e. under 1 % of the tolerance, over the full LD range (0.3–2.5 µm, scaled
    /// condition number about 1e5) and over the visible only (0.4–0.8 µm, about 7e6, where the
    /// infrared term is poorly determined and the tolerance correspondingly wider).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.3, 2.5)]
    [InlineData(0.4, 0.8)]
    public void FittingSyntheticSellmeierData_RecoversTheCoefficients(double lo, double hi)
    {
        var truth = Schott("N-BK7").Coefficients.ToArray();
        const int m = 41;
        var samples = Enumerable.Range(0, m).Select(i => lo + (hi - lo) * i / (m - 1))
            .Select(w => new IndexSample(w, DispersionFormulas.Evaluate(DispersionFormula.Sellmeier1, truth, w))).ToArray();

        var fit = DispersionFit.Fit(samples, DispersionFormula.Sellmeier1);

        var s = IndependentSensitivity(DispersionFormula.Sellmeier1, truth, samples.Select(p => p.Micrometers).ToArray());
        double nMax = samples.Max(p => p.Index);
        for (int j = 0; j < truth.Length; j++)
        {
            double tolerance = 10 * s[j] * 4 * Eps * nMax * Math.Sqrt(m) + 1e-13 * Math.Abs(truth[j]);
            double miss = Math.Abs(fit.Coefficients[j] - truth[j]);
            _out.WriteLine($"C{j + 1}: true {truth[j]:E12} fit {fit.Coefficients[j]:E12} miss {miss:E2} tolerance {tolerance:E2}");
            Assert.True(miss <= tolerance, $"C{j + 1}: missed by {miss:E3}, tolerance {tolerance:E3}");
        }
        // Residuals at the rounding floor: a few ulps of n ≈ 1.5 (3.3e-16 each).
        Assert.True(fit.RmsResidual <= 1e-14, $"RMS residual {fit.RmsResidual:E2}");
    }

    /// <summary>A formula linear in n² (Schott) is recovered the same way, its zero coefficients
    /// to an absolute tolerance from the same bound. The coefficients are OP11_FIXTURE.agf's
    /// SCHOTT-FORM (made-up values, line 12).</summary>
    [Fact]
    public void FittingSyntheticSchottData_RecoversTheCoefficients()
    {
        var truth = Fixture("OP11_FIXTURE.agf").Find("SCHOTT-FORM")!.Coefficients.Take(6).ToArray();
        const int m = 30;
        var samples = Enumerable.Range(0, m).Select(i => 0.3 + 2.2 * i / (m - 1))
            .Select(w => new IndexSample(w, DispersionFormulas.Evaluate(DispersionFormula.Schott, truth, w))).ToArray();
        var fit = DispersionFit.Fit(samples, DispersionFormula.Schott);
        var s = IndependentSensitivity(DispersionFormula.Schott, truth, samples.Select(p => p.Micrometers).ToArray());
        double nMax = samples.Max(p => p.Index);
        for (int j = 0; j < truth.Length; j++)
            Assert.True(Math.Abs(fit.Coefficients[j] - truth[j]) <= 10 * s[j] * 4 * Eps * nMax * Math.Sqrt(m) + 1e-13 * Math.Abs(truth[j]),
                $"C{j + 1}: {fit.Coefficients[j]:E12} vs {truth[j]:E12}");
    }

    /// <summary>Row lengths of the pseudo-inverse of the central-difference Jacobian ∂n/∂C.</summary>
    private static double[] IndependentSensitivity(DispersionFormula formula, double[] c, double[] wavelengths)
    {
        var jac = new double[wavelengths.Length, c.Length];
        for (int j = 0; j < c.Length; j++)
        {
            double h = 1e-5 * Math.Max(Math.Abs(c[j]), 1e-3);
            var plus = (double[])c.Clone();
            var minus = (double[])c.Clone();
            plus[j] += h;
            minus[j] -= h;
            for (int i = 0; i < wavelengths.Length; i++)
                jac[i, j] = (DispersionFormulas.Evaluate(formula, plus, wavelengths[i]) - DispersionFormulas.Evaluate(formula, minus, wavelengths[i])) / (2 * h);
        }
        var svd = RealSvd.Decompose(jac);
        var s = new double[c.Length];
        for (int j = 0; j < c.Length; j++)
        {
            double sum = 0;
            for (int k = 0; k < svd.SingularValues.Length; k++) sum += Math.Pow(svd.V[j, k] / svd.SingularValues[k], 2);
            s[j] = Math.Sqrt(sum);
        }
        return s;
    }

    /// <summary>The analytic Jacobian the fit uses agrees with central differences of
    /// <see cref="DispersionFormulas.Evaluate"/> for every fitted formula, at the coefficients
    /// OP11_FIXTURE.agf gives each formula (h = 1e-6 of the coefficient: truncation about h²,
    /// rounding about ε/h, both far below 1e-6 relative).</summary>
    [Fact]
    public void TheAnalyticGradient_MatchesCentralDifferences()
    {
        var fixture = Fixture("OP11_FIXTURE.agf");
        foreach (var formula in DispersionFit.Fittable)
        {
            var glass = fixture.Materials.First(g => g.FormulaNumber == (int)formula);
            var c = glass.Coefficients.Take(DispersionFormulas.CoefficientCount(formula)).ToArray();
            double w = 0.5 * (glass.ValidRange!.Value.Min + glass.ValidRange.Value.Max);
            var g = new double[c.Length];
            DispersionFit.Gradient(formula, c, w, g);
            for (int j = 0; j < c.Length; j++)
            {
                double h = 1e-6 * Math.Max(Math.Abs(c[j]), 1e-3);
                var plus = (double[])c.Clone();
                var minus = (double[])c.Clone();
                plus[j] += h;
                minus[j] -= h;
                double fd = (DispersionFormulas.Evaluate(formula, plus, w) - DispersionFormulas.Evaluate(formula, minus, w)) / (2 * h);
                Assert.True(Math.Abs(fd - g[j]) <= 1e-6 * Math.Max(1, Math.Abs(g[j])), $"{formula} C{j + 1}: analytic {g[j]:E10}, difference {fd:E10}");
            }
        }
    }

    [Fact]
    public void TheFit_RefusesWhatItCannotDetermine()
    {
        var three = new[] { new IndexSample(0.5, 1.52), new IndexSample(0.6, 1.515), new IndexSample(0.7, 1.512) };
        Assert.Contains("cannot determine", Assert.Throws<ArgumentException>(() => DispersionFit.Fit(three, DispersionFormula.Sellmeier1)).Message);
        Assert.Throws<NotSupportedException>(() => DispersionFit.Fit(three, DispersionFormula.Sellmeier2));
        var repeated = new[] { new IndexSample(0.5, 1.52), new IndexSample(0.5, 1.521), new IndexSample(0.7, 1.512) };
        Assert.Contains("share a wavelength", Assert.Throws<ArgumentException>(() => DispersionFit.Fit(repeated, DispersionFormula.Conrady)).Message);
    }

    [Fact]
    public void Samples_ReadFromText_AndABadLineIsNamed()
    {
        var samples = DispersionFit.ReadSamples("# wavelength index\n0.5 1.52\r\n\n0.6,1.515\n! note\n0.7\t1.512\n");
        Assert.Equal(new[] { 0.5, 0.6, 0.7 }, samples.Select(s => s.Micrometers));
        Assert.Equal(1.515, samples[1].Index);
        Assert.Contains("Line 2", Assert.Throws<FormatException>(() => DispersionFit.ReadSamples("0.5 1.52\nnot a number\n")).Message);
    }

    /// <summary>The AGF lines of a fit read back through the catalog reader: the coefficients to
    /// the ten decimals of their E-notation, and the NM line's nd and Vd pass the catalog's own
    /// self-check (printed to 6 and 4 decimals).</summary>
    [Fact]
    public void TheFitsAgfLines_ReadBackAndCheckThemselves()
    {
        var glass = Schott("SF57");
        var samples = Enumerable.Range(0, 25).Select(i => 0.4 + 0.05 * i).Select(w => new IndexSample(w, glass.RelativeIndex(w))).ToArray();
        var fit = DispersionFit.Fit(samples, DispersionFormula.Sellmeier1);
        var parsed = AgfReader.ParseText(fit.ToAgf("MY GLASS"), "fitted").Materials.Single();
        Assert.Equal("MY_GLASS", parsed.Name);
        Assert.Equal(2, parsed.FormulaNumber);
        for (int j = 0; j < 6; j++)
            Assert.True(Math.Abs(parsed.Coefficients[j] - fit.Coefficients[j]) <= 5e-11 * Math.Abs(fit.Coefficients[j]) + 1e-300, $"C{j + 1}");
        Assert.Equal(0.4, parsed.MinMicrometers!.Value, 12);
        Assert.Equal(1.6, parsed.MaxMicrometers!.Value, 12);
        Assert.Equal(GlassCheckOutcome.Passed, CatalogSelfCheck.Check(parsed).Outcome);
    }

    // ---------------- Gate 2: the map places every glass at its own nd, Vd ----------------

    /// <summary>
    /// The plan's second gate on the committed fixtures: every glass is either placed or left out
    /// with a reason, and each placed glass sits exactly at nd = n(d) and
    /// Vd = (n(d) − 1)/(n(F) − n(C)) computed here from its coefficients at the TIE-29 lines
    /// (the same arithmetic, so equal to the bit).
    /// </summary>
    [Theory]
    [InlineData("OP11_FIXTURE.agf")]
    [InlineData("TEST_CAT.agf")]
    public void TheGlassMap_PlacesEveryGlassAtItsOwnComputedNdVd(string file)
    {
        var catalog = Fixture(file);
        AssertMapPlacesEveryGlass(catalog);
    }

    private static void AssertMapPlacesEveryGlass(OpticalCatalog catalog)
    {
        var (points, left) = GlassMaps.GlassMap(new[] { catalog });
        Assert.Equal(catalog.Materials.Count, points.Count + left.Count);
        Assert.All(left, l => Assert.False(string.IsNullOrWhiteSpace(l.Reason)));
        double d = SpectralLines.LowerD.Micrometers, f = SpectralLines.F.Micrometers, c = SpectralLines.C.Micrometers;
        foreach (var p in points)
        {
            double nd = p.Material.RelativeIndex(d);
            Assert.Equal(nd, p.Properties.Nd);
            Assert.Equal((nd - 1) / (p.Material.RelativeIndex(f) - p.Material.RelativeIndex(c)), p.Properties.Vd);
        }
        // The analysis draws the same points.
        var context = new OpticsAnalysisContext("P", new OpticsSettings()) { Catalogs = new[] { catalog } };
        var plot = (AnalysisScatterPlot)new GlassMapAnalysis().Compute(context, new GlassMapSettings(), default).Plots.Single();
        var drawn = plot.Series.SelectMany(s => s.Points).ToList();
        Assert.Equal(points.Count, drawn.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Assert.Equal(points[i].Properties.Vd, drawn[i].X);
            Assert.Equal(points[i].Properties.Nd, drawn[i].Y);
            Assert.Equal(points[i].Material.Name, drawn[i].Label);
        }
    }

    /// <summary>
    /// The same over the 23 free AGF catalogs downloaded with ZemaxGlass into
    /// plans/sources/optics/zemaxglass/AGF_files, when they are on this machine (they are vendor
    /// files that may not be redistributed and are not in the repository; without them the test
    /// does nothing and says so).
    /// </summary>
    [Fact]
    public void TheGlassMap_OfEveryRealCatalog_WhenPresent()
    {
        string? folder = RealCatalogFolder();
        if (folder is null)
        {
            _out.WriteLine("DID NOTHING: the real-catalog folder is not on this machine; only the committed fixtures were checked.");
            return;
        }
        foreach (string file in Directory.GetFiles(folder, "*.agf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            AssertMapPlacesEveryGlass(AgfReader.Read(file));
    }

    private static string? RealCatalogFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OpenSimStudio.sln")))
            {
                string folder = Path.Combine(dir.FullName, "plans", "sources", "optics", "zemaxglass", "AGF_files");
                return Directory.Exists(folder) ? folder : null;
            }
        return null;
    }

    // ---------------- Partial dispersion ----------------

    /// <summary>
    /// ΔP_g,F computed from the coefficients against the ED line's printed ΔPgF for the five
    /// glasses: within half a unit of the fourth printed decimal (5e−5). The printed value is the
    /// vendor's rounding of the same quantity, P_g,F − (0.6438 − 0.001682·Vd)
    /// (zemaxglass_manual.txt, "dPgF"). When written the misses were N-BK7 4.03e−5, F2 3.99e−5,
    /// N-LAF2 3.55e−5, SF57 2.61e−5, N-PK51 8.8e−7.
    /// </summary>
    [Fact]
    public void DeltaPgF_MatchesTheCatalogsPrintedValue()
    {
        foreach (var name in Five)
        {
            var glass = Schott(name);
            var p = GlassProperties.Compute(glass);
            Assert.True(Math.Abs(p.DeltaPgF!.Value - glass.DeltaPgF!.Value) <= 5e-5, $"{name}: {p.DeltaPgF:F7} vs printed {glass.DeltaPgF}");
            // The definitions, computed independently here.
            double ng = glass.RelativeIndex(SpectralLines.G.Micrometers), nF = glass.RelativeIndex(SpectralLines.F.Micrometers), nC = glass.RelativeIndex(SpectralLines.C.Micrometers);
            Assert.Equal((ng - nF) / (nF - nC), p.PgF!.Value, 15);
            Assert.Equal(p.PgF.Value - (0.6438 - 0.001682 * p.Vd), p.DeltaPgF.Value, 15);
        }
    }

    // ---------------- Model glass ----------------

    /// <summary>A model glass has the requested nd, Vd and ΔP_g,F, by construction (the fit's
    /// constraints), to rounding: nd to 1e−14; Vd = (nd − 1)/(nF − nC) divides by a difference of
    /// order 1e−2 of two indices each rounded to about 2e−16, so to 1e−10 (largest miss when
    /// written 4.7e−12, at Vd = 90); ΔP_g,F, a ratio of such differences, to 1e−11 (written 7.6e−14).</summary>
    [Fact]
    public void AModelGlass_HasTheRequestedNdVdAndDeltaPgF()
    {
        var fit = ModelGlassFit.Train(Five.Select(Schott));
        Assert.Equal(5, fit.TrainingCount);
        foreach (var (nd, vd, dp) in Triples)
        {
            var glass = fit.Create(nd, vd, dp, "M");
            Assert.Equal((int)DispersionFormula.Herzberger, glass.FormulaNumber);
            var p = GlassProperties.Compute(glass);
            Assert.True(Math.Abs(p.Nd - nd) <= 1e-14, $"nd {p.Nd:R} vs {nd}");
            Assert.True(Math.Abs(p.Vd - vd) <= 1e-10, $"Vd {p.Vd:R} vs {vd}");
            Assert.True(Math.Abs(p.DeltaPgF!.Value - dp) <= 1e-11, $"ΔPgF {p.DeltaPgF:R} vs {dp}");
            Assert.Equal(GlassCheckOutcome.Passed, CatalogSelfCheck.Check(glass).Outcome);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => fit.Create(0.9, 50, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => fit.Create(1.5, 0, 0));
    }

    private static readonly (double Nd, double Vd, double DeltaPgF)[] Triples =
        { (1.50, 65, 0), (1.62, 36, 0.002), (1.75, 45, -0.003), (1.85, 24, 0.012), (1.53, 77, 0.026), (1.46, 90, 0.04) };

    /// <summary>
    /// The fit recovers its own model: glasses made by one model (so their partial dispersions
    /// follow it exactly) train a second model that gives the same indices. Measured when written:
    /// training residual 3.4e−16 RMS, largest index difference 4.4e−16; the tolerance 1e−13 is
    /// a few hundred ulps of n, room for the conditioning of the six Herzberger terms over
    /// 0.40–1.01 µm.
    /// </summary>
    [Fact]
    public void TheModelFit_RecoversAModelItGenerated()
    {
        var first = ModelGlassFit.Train(Five.Select(Schott));
        var synthetic = Triples.Select((t, i) => first.Create(t.Nd, t.Vd, t.DeltaPgF, "S" + i)).ToArray();
        var second = ModelGlassFit.Train(synthetic);
        Assert.True(second.RmsIndexResidual <= 1e-13, $"RMS {second.RmsIndexResidual:E2}");
        Assert.True(second.MaxIndexResidual <= 1e-13, $"max {second.MaxIndexResidual:E2}");
        foreach (var (nd, vd, dp) in new[] { (1.7, 50.0, 0.001), (1.5, 60.0, 0.0) })
        {
            var a = first.Create(nd, vd, dp);
            var b = second.Create(nd, vd, dp);
            foreach (double w in first.Wavelengths)
                Assert.True(Math.Abs(a.RelativeIndex(w) - b.RelativeIndex(w)) <= 1e-13, $"{w} µm");
        }
    }

    [Fact]
    public void TheModelFit_NamesWhatItLeftOut_AndNeedsTwoGlasses()
    {
        var fixture = Fixture("OP11_FIXTURE.agf");
        var e = Assert.Throws<InvalidOperationException>(() => ModelGlassFit.Train(new[] { Schott("F2") }));
        Assert.Contains("at least two training glasses", e.Message);
        var fit = ModelGlassFit.Train(Five.Select(Schott).Concat(fixture.Materials));
        Assert.Equal(Five.Length + fixture.Materials.Count, fit.TrainingCount + fit.LeftOut.Count);
        Assert.All(fit.LeftOut, l => Assert.False(string.IsNullOrWhiteSpace(l.Reason)));
    }

    /// <summary>Trained on the whole SCHOTT catalog, when present. A regression record, not a
    /// claim about OpticStudio: when written 158 glasses trained it with an RMS index residual
    /// of 8.33e−5 (largest 1.25e−3, N-SF66); 2e−4 leaves room for catalog updates.</summary>
    [Fact]
    public void TheModelFit_OnTheSchottCatalog_WhenPresent()
    {
        string? folder = RealCatalogFolder();
        if (folder is null) { _out.WriteLine("DID NOTHING: the real-catalog folder is not on this machine."); return; }
        var fit = ModelGlassFit.Train(AgfReader.Read(Path.Combine(folder, "schott.agf")).Materials);
        _out.WriteLine($"{fit.TrainingCount} glasses, RMS {fit.RmsIndexResidual:E3}, max {fit.MaxIndexResidual:E3} ({fit.WorstGlass})");
        Assert.True(fit.RmsIndexResidual <= 2e-4);
    }

    // ---------------- Offsets ----------------

    /// <summary>An offset moves nd and Vd by exactly the amounts asked (to rounding, as for the
    /// model glass) and keeps P_g,F; a second offset replaces the first; a zero offset changes
    /// no index by more than an ulp or two.</summary>
    [Fact]
    public void AnOffset_MovesNdAndVd_AndKeepsThePartialDispersion()
    {
        var glass = Schott("N-BK7");
        var before = GlassProperties.Compute(glass);
        var offset = GlassOffsets.Apply(glass, 0.001, -0.5);
        var after = GlassProperties.Compute(offset);
        Assert.True(Math.Abs(after.Nd - (before.Nd + 0.001)) <= 1e-14);
        Assert.True(Math.Abs(after.Vd - (before.Vd - 0.5)) <= 1e-10);
        Assert.True(Math.Abs(after.PgF!.Value - before.PgF!.Value) <= 1e-11);
        Assert.Equal(GlassCheckOutcome.Passed, CatalogSelfCheck.Check(offset).Outcome);

        var again = GlassProperties.Compute(GlassOffsets.Apply(offset, 0.002, 0));
        Assert.True(Math.Abs(again.Nd - (before.Nd + 0.002)) <= 1e-14);
        Assert.True(Math.Abs(again.Vd - before.Vd) <= 1e-10);

        var zero = GlassOffsets.Apply(glass, 0, 0);
        foreach (double w in new[] { 0.4, 0.6, 1.2, 2.0 })
            Assert.True(Math.Abs(zero.RelativeIndex(w) - glass.RelativeIndex(w)) <= 4 * Eps * 2, $"{w} µm");

        Assert.Throws<ArgumentOutOfRangeException>(() => GlassOffsets.Apply(glass, -0.6, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GlassOffsets.Apply(glass, 0, -70));
    }

    // ---------------- Athermal map ----------------

    /// <summary>
    /// SCHOTT TIE-19, Table 2 (page 9), "Optical glasses and their thermo optical constant G":
    /// ne, relative dn/dT [+20/+40 °C] at e, α −30/70 and G, all ×10⁻⁶/K except ne. Equation
    /// (10) applied to the table's own columns gives its G column, to the table's rounding: half
    /// the last printed digit of G, plus the rounding of dn/dT, plus (ne − 1) times the rounding
    /// of α (N-BK7's α is printed to one decimal).
    /// </summary>
    [Theory]
    [InlineData("N-PK51", "1.53019", "-6.70", "12.35", "-0.15")]
    [InlineData("N-FK58", "1.45600", "-6.20", "13.67", "0.03")]
    [InlineData("N-PK52A", "1.49845", "-6.40", "13.01", "0.08")]
    [InlineData("N-FK51A", "1.48794", "-5.70", "12.74", "0.52")]
    [InlineData("N-FK5", "1.48914", "-1.00", "9.2", "3.50")]
    [InlineData("N-PSK53A", "1.62033", "-2.40", "9.56", "3.53")]
    [InlineData("N-LAK12", "1.68083", "-0.40", "7.6", "4.77")]
    [InlineData("N-BK7", "1.51872", "3.00", "7.1", "6.68")]
    [InlineData("N-LAF2", "1.74791", "1.00", "8.06", "7.03")]
    [InlineData("F2", "1.62408", "4.40", "8.2", "9.52")]
    [InlineData("SF57", "1.85504", "12.50", "8.3", "19.60")]
    public void Equation10_ReproducesTie19Table2(string glass, string ne, string dndt, string alpha, string g)
    {
        Assert.True(PrintedNumber.TryParse(ne, out var n), glass);
        Assert.True(PrintedNumber.TryParse(dndt, out var d), glass);
        Assert.True(PrintedNumber.TryParse(alpha, out var a), glass);
        Assert.True(PrintedNumber.TryParse(g, out var expected), glass);
        double computed = GlassMaps.ThermoOpticConstant(a.Value * 1e-6, n.Value, d.Value * 1e-6) * 1e6;
        double tolerance = expected.HalfLastDigit + d.HalfLastDigit + (n.Value - 1) * a.HalfLastDigit + a.Value * n.HalfLastDigit + 1e-12;
        Assert.True(Math.Abs(computed - expected.Value) <= tolerance, $"{glass}: {computed:F4} vs {g} (tolerance {tolerance:E2})");
    }

    /// <summary>
    /// The whole chain for the five glasses whose TD constants are copied above: the athermal map
    /// at the e line, 20 → 40 °C, TIE-19 air at 1 atm, against Table 2's G. The tolerance is that
    /// of ThermalIndexTests' Table 1 check, 0.2·10⁻⁶/K, for the same reason: the computed dn/dT
    /// differs from the printed by up to 0.15 (the AGF TD constants may be from an older data
    /// sheet than the 2023 table), and G inherits that difference. When written the misses were
    /// N-BK7 0.023, SF57 0.146, N-PK51 0.005, F2 0.022, N-LAF2 0.085.
    /// </summary>
    [Theory]
    [InlineData("N-BK7", 6.68)]
    [InlineData("SF57", 19.60)]
    [InlineData("N-PK51", -0.15)]
    [InlineData("F2", 9.52)]
    [InlineData("N-LAF2", 7.03)]
    public void TheAthermalMap_MatchesTie19Table2(string name, double gE6)
    {
        var (points, left) = GlassMaps.AthermalMap(new[] { FiveSchott() }, SpectralLines.E.Micrometers, 20, 40,
            AirIndexModel.Schott, AirIndex.StandardPressurePascals);
        Assert.Empty(left);
        var p = points.Single(q => q.Material.Name == name);
        Assert.Equal(p.Material.ThermalExpansion!.Value * 1e-6, p.ExpansionPerKelvin);
        Assert.Equal(gE6, p.ThermoOpticConstant * 1e6, 0.2);
    }

    [Fact]
    public void TheAthermalMap_LeavesOutGlassesWithoutThermalData()
    {
        var catalog = new OpticalCatalog
        {
            Name = "c",
            Materials = new[] { Schott("F2") with { Thermal = null }, Schott("SF57") with { ThermalExpansion = null }, Schott("N-BK7") }
        };
        var (points, left) = GlassMaps.AthermalMap(new[] { catalog }, SpectralLines.E.Micrometers, 20, 40, AirIndexModel.Schott, AirIndex.StandardPressurePascals);
        Assert.Equal("N-BK7", points.Single().Material.Name);
        Assert.Contains("no TD line", left[0].Reason);
        Assert.Contains("expansion", left[1].Reason);
    }

    // ---------------- Comparison and transmittance ----------------

    [Fact]
    public void TheComparison_ListsComputedAndPrintedValues_SideBySide()
    {
        var fixture = Fixture("OP11_FIXTURE.agf");
        var glasses = new[] { fixture.Find("SCHOTT-FORM")!, fixture.Find("HERZBERGER-FORM")! };
        var table = GlassComparison.Build(glasses, new[] { 0.5875618, 2.0 });
        Assert.Equal(new[] { "Property", "SCHOTT-FORM", "HERZBERGER-FORM" }, table.Headers);
        IReadOnlyList<string> Row(string label) => table.Rows.Single(r => r[0] == label);
        var p = GlassProperties.Compute(glasses[0]);
        Assert.Equal(p.Nd.ToString("F6", System.Globalization.CultureInfo.InvariantCulture), Row("nd (computed)")[1]);
        Assert.Equal("1.516372", Row("nd (printed)")[1]);                     // OP11_FIXTURE.agf line 9
        Assert.Equal("out of range", Row("n at 2 µm")[2]);                     // HERZBERGER-FORM's LD is 0.4–1.0
        Assert.Equal(GlassComparison.Absent, Row("Density, g/cm³")[2]);       // its ED line prints "-"
        Assert.Equal("2.51", Row("Density, g/cm³")[1]);
        Assert.Equal("3", Row("Transmittance points (IT)")[1]);
        Assert.All(table.Rows, r => Assert.Equal(3, r.Count));
    }

    /// <summary>OP11_FIXTURE.agf's SCHOTT-FORM IT lines (lines 16–18): 0.35 µm 0.92 over 25 mm,
    /// 0.40 µm 0.992 over 25 mm, 0.50 µm 0.994 with no thickness. At 10 mm the first two are
    /// t^(10/25); the third cannot be scaled and is left out.</summary>
    [Fact]
    public void Transmittance_ScalesEachPointToTheThickness()
    {
        var glass = Fixture("OP11_FIXTURE.agf").Find("SCHOTT-FORM")!;
        var (points, skipped) = InternalTransmittance.Curve(glass, 10);
        Assert.Equal(1, skipped);
        Assert.Equal(new[] { 0.35, 0.40 }, points.Select(p => p.Micrometers));
        Assert.Equal(Math.Pow(0.92, 0.4), points[0].Transmittance, 15);
        Assert.Equal(Math.Pow(0.992, 0.4), points[1].Transmittance, 15);
        Assert.Equal(0.92, InternalTransmittance.Curve(glass, 25).Points[0].Transmittance, 15);
    }

    // ---------------- Analyses ----------------

    [Fact]
    public void TheMaterialAnalyses_AreRegistered_AndRefuseToRunWithoutCatalogs()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        foreach (var id in new[] { GlassMapAnalysis.SavedId, AthermalGlassMapAnalysis.SavedId, DispersionCurveAnalysis.SavedId,
                     TransmittanceCurveAnalysis.SavedId, GlassCompareAnalysis.SavedId, ModelGlassAnalysis.SavedId, GlassFitAnalysis.SavedId })
        {
            Assert.True(registry.TryGet(id, out var analysis), id);
            Assert.Equal("Materials", analysis.Category);
        }
        var empty = new OpticsAnalysisContext("P", new OpticsSettings());
        var e = Assert.Throws<InvalidOperationException>(() => new GlassMapAnalysis().Compute(empty, new GlassMapSettings(), default));
        Assert.Contains("No glass catalog is active", e.Message);
    }

    [Fact]
    public void TheCurveAndCompareAnalyses_FindGlassesByName_AndNameTheMissing()
    {
        var context = new OpticsAnalysisContext("P", new OpticsSettings()) { Catalogs = new[] { FiveSchott(), Fixture("OP11_FIXTURE.agf") } };
        var curves = new DispersionCurveAnalysis().Compute(context,
            new DispersionCurveSettings { Glasses = "N-BK7, OP11_FIXTURE:HERZBERGER-FORM, NOPE", MinMicrometers = 0.35, MaxMicrometers = 1.0, Points = 11 }, default);
        var plot = (LinePlot)curves.Plots.Single();
        Assert.Equal(new[] { "N-BK7", "HERZBERGER-FORM" }, plot.Curves.Select(c => c.Name));
        Assert.Equal(0.4, plot.Curves[1].X[0]);                                 // drawn over its own range only
        Assert.Equal(Schott("N-BK7").RelativeIndex(0.35), plot.Curves[0].Y[0]);
        Assert.Contains("NOPE", curves.Text);

        var compare = new GlassCompareAnalysis().Compute(context, new GlassCompareSettings { Glasses = "F2, SF57", Wavelengths = "0.5876" }, default);
        Assert.Contains("n at 0.5876 µm", compare.Text);

        var model = new ModelGlassAnalysis().Compute(context, new ModelGlassSettings { Nd = 1.6, Vd = 40 }, default);
        Assert.Contains("not OpticStudio's model", string.Join(" ", model.Assumptions));
    }

    [Fact]
    public void TheFitAnalysis_ReadsItsFile_AndWritesAgfLines()
    {
        string path = Path.Combine(Path.GetTempPath(), $"op14-fit-{Guid.NewGuid():N}.txt");
        try
        {
            var glass = Schott("F2");
            File.WriteAllLines(path, Enumerable.Range(0, 20).Select(i => 0.4 + 0.05 * i)
                .Select(w => $"{w.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} {glass.RelativeIndex(w).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"));
            var result = new GlassFitAnalysis().Compute(new OpticsAnalysisContext("P", new OpticsSettings()),
                new GlassFitSettings { DataFile = path, Formula = DispersionFormula.Sellmeier1, GlassName = "F2FIT" }, default);
            Assert.Contains("NM F2FIT 2 ", result.Text);
            Assert.Single(result.Plots);
        }
        finally { File.Delete(path); }
        Assert.Throws<InvalidOperationException>(() => new GlassFitAnalysis().Compute(new OpticsAnalysisContext("P", new OpticsSettings()), new GlassFitSettings(), default));
    }

    [Fact]
    public void ActiveCatalogs_FollowTheSavedOrder_OrAreEveryCatalog()
    {
        var a = new OpticalCatalog { Name = "A" };
        var b = new OpticalCatalog { Name = "B" };
        Assert.Equal(new[] { a, b }, GlassLookup.ActiveCatalogs(new[] { a, b }, null));
        Assert.Equal(new[] { b }, GlassLookup.ActiveCatalogs(new[] { a, b }, new[] { "b", "missing" }));
    }
}
