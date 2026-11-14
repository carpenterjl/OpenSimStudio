using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Special;
using OpenSim.Optics.Wavefront;
using F = OpenSim.Tests.Optics.Gates.SeidelFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-36: the Seidel sums against real rays (coma, distortion), against the OPD's Zernike fit
/// (W040, W131), against first-order colour, closed forms (paraboloid, Petzval radius), the
/// aspheric term, refusals, and the three analyses through the registry. The gate clauses are
/// in <c>Gates/SeidelGateTests</c>. Expected values are derived in the comments from the tests'
/// own inputs.
/// </summary>
public sealed class SeidelTests
{
    private const double Theta = 5;   // degrees, the full field of the singlet tests

    // ---- Against real rays ----

    /// <summary>
    /// TTCO = 3S2/(2n′u′) is the tangential coma blur of the full field and pupil: for a field at
    /// fraction h and rays at ±p, [EY(+p) + EY(−p)]/2 − EY(0) = TTCO·p²·h' + O(p⁴h, p²h³), where h'
    /// is the field's fraction in the paraxial chief ray's measure tan θ: tan(hΘ)/tan Θ, whose
    /// limit as h → 0 is h·Θ/tan Θ (Θ in radians). The test takes h = 10⁻³ (the h³ terms are 10⁻⁶
    /// of the result) and Richardson-extrapolates p = 0.1 and 0.05 to p = 0 (error O(p⁴) ~ 10⁻⁶).
    /// </summary>
    [Fact]
    public void TangentialComa_IsTheRealRayLimit()
    {
        var system = F.Sys(0.01, new[] { 0.0, Theta });
        var lens = F.Singlet(system);
        var r = SeidelEngine.Compute(lens, system);
        double ttco = r.Transverse(r.Sum).TangentialComa;

        var trace = SequentialTrace.Create(lens, system);
        var launcher = new RayLauncher(lens, system);
        double Y(double h, double p)
        {
            var ray = trace.Trace(launcher.Launch(trace, 0, h, 0, p));
            Assert.True(ray.IsOk);
            return ray.Position.Y;
        }
        const double h = 1e-3;
        double t = Math.Tan(h * Theta * Math.PI / 180) / Math.Tan(Theta * Math.PI / 180);
        double Blur(double p) => ((Y(h, p) + Y(h, -p)) / 2 - Y(h, 0)) / (p * p * t);
        double extrapolated = (4 * Blur(0.05) - Blur(0.1)) / 3;
        F.Close(ttco, extrapolated, 1e-6 * Math.Abs(ttco), "tangential coma of real rays");
    }

    /// <summary>
    /// TDIS = S5/(2n′u′) is the chief ray's distance from the paraxial image point at the full
    /// field: at field fraction t (in tan θ) the real chief ray lands at ȳ′·t + TDIS·t³ + O(t⁵),
    /// ȳ′ the paraxial chief ray's image height at the full field. Richardson in t² from h = 0.1
    /// and 0.05 removes the t⁵ term (the remainder is O(t⁴) ~ 10⁻⁴ of a part that is itself ~10⁻²).
    /// </summary>
    [Fact]
    public void Distortion_IsTheRealChiefRayLimit()
    {
        var system = F.Sys(0.002, new[] { 0.0, Theta });
        var lens = F.Singlet(system);
        var r = SeidelEngine.Compute(lens, system);
        double tdis = r.Transverse(r.Sum).Distortion;
        var p = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        double full = p.ChiefRay.Heights[lens.ImageIndex];

        var trace = SequentialTrace.Create(lens, system);
        var launcher = new RayLauncher(lens, system);
        (double Value, double T) At(double h)
        {
            var ray = trace.Trace(launcher.Launch(trace, 0, h, 0, 0));
            Assert.True(ray.IsOk);
            double t = Math.Tan(h * Theta * Math.PI / 180) / Math.Tan(Theta * Math.PI / 180);
            return ((ray.Position.Y - full * t) / (t * t * t), t);
        }
        var (v1, t1) = At(0.1);
        var (v2, t2) = At(0.05);
        double extrapolated = (v2 * t1 * t1 - v1 * t2 * t2) / (t1 * t1 - t2 * t2);
        F.Close(tdis, extrapolated, 1e-6 * Math.Abs(tdis), "distortion of the real chief ray");
    }

    // ---- Against the OPD ----

    /// <summary>
    /// The wave coefficients against the OPD (OpdEngine, chief-ray reference sphere) fitted with
    /// Zernike Standard terms: on axis at the paraxial focus the OPD is W040·ρ⁴ + O(ρ⁶), so its
    /// Z11 is W040 times the Z11 of ρ⁴; at the full field the coma term W131·Py·ρ² gives Z7 = W131
    /// times the Z7 of Py·ρ² (field along +y, so the meridional pupil coordinate is Py). Those
    /// two factors are fitted here on the same nodes, so no Zernike normalisation is typed. The
    /// higher orders are ~(a/f)² ≈ 4·10⁻⁵ and Θ² ≈ 3·10⁻⁴ (1°) of the coefficients; the tolerance
    /// is 2·10⁻³. This also pins the sign: W040 > 0 means the rim ray's wavefront is ahead
    /// (Conventions §6), as for this undercorrected singlet.
    /// </summary>
    [Fact]
    public void WaveCoefficients_AreTheZernikeFitOfTheOpd_InTheThirdOrderLimit()
    {
        var system = F.Sys(0.001, new[] { 0.0, 1.0 });
        var lens = F.Singlet(system);
        var r = SeidelEngine.Compute(lens, system);
        var w = r.Wave(r.Sum);
        Assert.True(w.W040 > 0, "an undercorrected singlet has W040 > 0");

        var basis = new ZernikeBasis(ZernikeOrdering.Standard, 15);
        var nodes = OpdEngine.PupilNodes(33);
        var px = nodes.Select(q => q.Px).ToArray();
        var py = nodes.Select(q => q.Py).ToArray();
        double c11 = ZernikeFit.Fit(basis, px, py, nodes.Select(q => Math.Pow(q.Px * q.Px + q.Py * q.Py, 2)).ToArray()).Coefficients[10];
        double c7 = ZernikeFit.Fit(basis, px, py, nodes.Select(q => q.Py * (q.Px * q.Px + q.Py * q.Py)).ToArray()).Coefficients[6];

        var onAxis = FieldZernike.Fit(lens, system, 0, 0, basis, nodes, new OpdOptions());
        var edge = FieldZernike.Fit(lens, system, 1, 0, basis, nodes, new OpdOptions());
        Assert.Null(onAxis.Failure);
        Assert.Null(edge.Failure);
        F.Close(w.W040 * c11, onAxis.Z(11), 2e-3 * Math.Abs(w.W040 * c11), "Z11 on axis against W040");
        F.Close(w.W131 * c7, edge.Z(7), 2e-3 * Math.Abs(w.W131 * c7), "Z7 at the full field against W131");
    }

    // ---- Colour ----

    /// <summary>
    /// CL and CT are first order in the dispersion: TAXC is the difference of the shortest and
    /// longest wavelengths' paraxial marginal ray heights on the primary's paraxial image plane,
    /// and TLAC that of their chief rays, up to O(δn²) — here δn = 4·10⁻⁴ about the primary's
    /// index, so the remainder is ~10⁻³ of the result at most (and less, the wavelengths being
    /// symmetric about the primary). The glass is a two-point table (test data, not a real
    /// glass).
    /// </summary>
    [Fact]
    public void ColourSums_AreTheFirstOrderChromaticDifferences()
    {
        var glass = ParaxialMedium.Of(new OpticalMaterial
        {
            Name = "TESTDISP",
            Tabulated = new TabulatedIndex { Points = new[] { new TabulatedIndexPoint(0.4, 1.5005, 0), new TabulatedIndexPoint(0.7, 1.4995, 0) } },
        });
        var system = F.Sys(0.01, new[] { 0.0, Theta }, new[] { 0.45, 0.55, 0.65 }, primary: 1);
        var lens = F.AtParaxialFocus(F.Lens(double.PositiveInfinity,
            F.S(0.05, 0.005, stop: true, medium: glass), F.S(-0.2, 0.1)), system);
        var r = SeidelEngine.Compute(lens, system);
        Assert.Equal((0, 2), r.ColourRows);
        var t = r.Transverse(r.Sum);

        int image = lens.ImageIndex;
        var shortest = ParaxialSystem.Create(lens.ToParaxialLens(), system, 0);
        var longest = ParaxialSystem.Create(lens.ToParaxialLens(), system, 2);
        double axial = shortest.MarginalRay.Heights[image] - longest.MarginalRay.Heights[image];
        double lateral = shortest.ChiefRay.Heights[image] - longest.ChiefRay.Heights[image];
        F.Close(axial, t.AxialColour, 2e-3 * Math.Abs(axial), "TAXC");
        F.Close(lateral, t.LateralColour, 2e-3 * Math.Abs(lateral), "TLAC");
    }

    [Fact]
    public void OneWavelength_AndMirrors_HaveNoColour()
    {
        var mono = F.Sys(0.01, new[] { 0.0, Theta });
        var r = SeidelEngine.Compute(F.Singlet(mono), mono);
        Assert.Null(r.ColourRows);
        Assert.Equal(0, r.Sum.CL);
        Assert.Equal(0, r.Sum.CT);

        // A mirror in a dispersive medium still has no colour: δn′/n′ = (−δn)/(−n).
        var glass = ParaxialMedium.Of(new OpticalMaterial
        {
            Name = "TESTDISP",
            Tabulated = new TabulatedIndex { Points = new[] { new TabulatedIndexPoint(0.4, 1.53, 0), new TabulatedIndexPoint(0.7, 1.51, 0) } },
        });
        var poly = F.Sys(0.01, new[] { 0.0, 2.0 }, new[] { 0.45, 0.55, 0.65 }, primary: 1);
        var mirror = F.Lens(double.PositiveInfinity, F.S(double.PositiveInfinity, 0.05, stop: true, medium: glass),
            F.S(-0.2, -0.1, mirror: true));
        var m = SeidelEngine.Compute(mirror, poly);
        Assert.True(Math.Abs(m.Surfaces[1].Terms.CL) <= 1e-15 * Math.Abs(m.Surfaces[1].Terms.S1) + 1e-30, "mirror CL");
        Assert.True(Math.Abs(m.Surfaces[1].Terms.CT) <= 1e-15 * Math.Abs(m.Surfaces[1].Terms.S2) + 1e-30, "mirror CT");
    }

    // ---- Closed forms ----

    /// <summary>A paraboloid focuses a collimated beam perfectly (Conventions §13's gate), so its
    /// S1 is zero: its conic term (k = −1) cancels the sphere's.</summary>
    [Fact]
    public void AParaboloid_HasNoSphericalAberration()
    {
        var system = F.Sys(0.02, new[] { 0.0, 1.0 });
        var sphere = SeidelEngine.Compute(F.Lens(double.PositiveInfinity, F.S(-0.2, -0.1, stop: true, mirror: true)), system);
        var parabola = SeidelEngine.Compute(F.Lens(double.PositiveInfinity, F.S(-0.2, -0.1, k: -1, stop: true, mirror: true)), system);
        F.Close(0, parabola.Sum.S1, 1e-14 * Math.Abs(sphere.Sum.S1), "S1 of a paraboloid");
        F.Close(-sphere.Sum.S1, parabola.Surfaces[0].Aspheric.S1, 1e-14 * Math.Abs(sphere.Sum.S1), "aspheric part");
    }

    /// <summary>
    /// The Petzval radius from the help's sum 1/R_p = −n_m Σ (nᵢ − nᵢ₋₁)/(nᵢ nᵢ₋₁ Rᵢ): a thin lens
    /// in air gives −(n − 1)(1/R₁ − 1/R₂)/n = −φ/n, so R_p = −n/φ; a mirror in air (n′ = −1)
    /// gives −(−2)/(−R) = −2/R, so R_p = −R/2: a concave mirror (R &lt; 0) has its Petzval
    /// surface's centre toward +z, on the mirror's side of the image (Conventions §3's sign).
    /// </summary>
    [Fact]
    public void ThePetzvalRadius_OfAThinLensAndOfAMirror()
    {
        var system = F.Sys(0.01, new[] { 0.0, 1.0 });
        const double n = 1.6, r1 = 0.08, r2 = -0.12;
        double phi = (n - 1) * (1 / r1 - 1 / r2);
        var lens = F.Lens(double.PositiveInfinity, F.S(r1, 0, n, stop: true), F.S(r2, 0.1));
        F.Close(-n / phi, SeidelEngine.Compute(lens, system).PetzvalRadius, 1e-14, "thin lens");
        var mirror = F.Lens(double.PositiveInfinity, F.S(-0.3, -0.15, stop: true, mirror: true));
        F.Close(0.15, SeidelEngine.Compute(mirror, system).PetzvalRadius, 1e-15, "mirror");
    }

    /// <summary>
    /// The distortion sum written without a division (RayOptics' form) equals Welford's
    /// S5 = (Ā/A)(S3 + S4) surface by surface wherever A ≠ 0 (derivable from the two forms; checked
    /// here numerically on a singlet with a conic, where the aspheric terms also follow it:
    /// their S5/S3 = ȳ/y = Ā/A only for the spherical part, so the identity is checked on
    /// spherical surfaces).
    /// </summary>
    [Fact]
    public void TheDistortionSum_EqualsWelfordsQuotientForm()
    {
        var system = F.Sys(0.01, new[] { 0.0, Theta });
        var lens = F.Singlet(system, frontConic: 0);
        var p = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        var r = SeidelEngine.Compute(lens, system);
        foreach (var s in r.Surfaces)
        {
            int j = s.Surface;
            double c = lens.Surfaces[j].Curvature, n = p.SignedIndices[j - 1];
            double a = n * (p.MarginalRay.Slopes[j - 1] + p.MarginalRay.Heights[j] * c);
            double ab = n * (p.ChiefRay.Slopes[j - 1] + p.ChiefRay.Heights[j] * c);
            double expected = ab / a * (s.Terms.S3 + s.Terms.S4);
            F.Close(expected, s.Terms.S5, 1e-13 * Math.Abs(expected), $"S5 of surface {j}");
        }
    }

    /// <summary>
    /// The aspheric term reads the r⁴ departure from the paraxial sphere whatever its source: an
    /// Even Asphere with α2 = k c³/8 on a sphere is the conic k to fourth order, and one with an
    /// r² term α1 too is the sphere of curvature c + 2α1 plus the departure
    /// (1 + k)c³/8 + α2 − (c + 2α1)³/8 (derived in SeidelEngine.FourthOrderDeparture).
    /// </summary>
    [Fact]
    public void TheAsphericTerm_IsTheFourthOrderDepartureFromTheParaxialSphere()
    {
        var system = F.Sys(0.01, new[] { 0.0, Theta });
        double c = 1 / 0.05, k = -0.7;
        var conic = SeidelEngine.Compute(F.Singlet(system, k), system);
        var even = SeidelEngine.Compute(F.AtParaxialFocus(F.Lens(double.PositiveInfinity,
            F.S(0.05, 0.005, 1.5, stop: true, shape: new EvenAsphere(new[] { 0, k * c * c * c / 8 })), F.S(-0.2, 0.1)), system), system);
        foreach (var (a, b, name) in conic.Sum.ToArray().Zip(even.Sum.ToArray(), SeidelTerms.Names))
            F.Close(a, b, 1e-13 * Math.Abs(a), name);

        const double a1 = 2.0;   // 1/m: moves the paraxial curvature to c + 4
        double cp = c + 2 * a1, a2 = 3e3;
        var surface = F.S(0.05, 0.005, 1.5, k, stop: true, shape: new EvenAsphere(new[] { a1, a2 }));
        F.Close((1 + k) * c * c * c / 8 + a2 - cp * cp * cp / 8, SeidelEngine.FourthOrderDeparture(surface, 1), 1e-12 * cp * cp * cp, "G with an r² term");
    }

    // ---- Refusals ----

    [Fact]
    public void SurfacesOutsideThirdOrderTheory_AreRefused_NamingTheSurface()
    {
        var system = F.Sys(0.01, new[] { 0.0, 1.0 });
        void Refused(SurfaceShape shape, string expected)
        {
            var lens = F.Lens(double.PositiveInfinity, F.S(0.05, 0.005, 1.5, stop: true), F.S(-0.2, 0.1, shape: shape));
            var e = Assert.Throws<InvalidOperationException>(() => SeidelEngine.Compute(lens, system));
            Assert.Contains("surface 2", e.Message);
            Assert.Contains(expected, e.Message);
        }
        Refused(new OddAsphere(new[] { 1e-3 }), "β1");
        Refused(new OddAsphere(new[] { 0, 0, 1e-3 }), "β3");
        Refused(new AlternateEvenAsphere(new[] { 0.0 }), "Alternate Even");
        Refused(new ParaxialShape(0.1), "Standard, Even Asphere");
    }

    // ---- The analyses ----

    [Fact]
    public void TheThreeAnalyses_AreRegistered_AndRunOnALens()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(SeidelCoefficientsAnalysis.SavedId, out _));
        Assert.True(registry.TryGet(SeidelDiagramAnalysis.SavedId, out _));
        Assert.True(registry.TryGet(FullFieldAberrationAnalysis.SavedId, out _));

        var system = F.Sys(0.01, new[] { 0.0, Theta }, new[] { 0.48, 0.55, 0.65 }, primary: 1);
        var lens = F.Singlet(system);
        var context = F.Context(system);

        var coefficients = new SeidelCoefficientsAnalysis(_ => lens).Compute(context, new SeidelCoefficientsSettings(), default);
        Assert.Equal(new[] { "Seidel aberration coefficients", "Seidel aberration coefficients in waves", "Transverse aberration coefficients",
            "Longitudinal aberration coefficients", "Summary" }, coefficients.Sections.Select(s => s.Heading));
        Assert.Equal(3, coefficients.Sections[0].TableRows.Count);   // two surfaces and the sum
        Assert.Equal("STO (1)", coefficients.Sections[0].TableRows[0][0]);

        var diagram = new SeidelDiagramAnalysis(_ => lens).Compute(context, new SeidelDiagramSettings(), default);
        var bars = Assert.IsType<LinePlot>(Assert.Single(diagram.Plots));
        Assert.Equal(7, bars.Curves.Count);
        Assert.All(bars.Curves, c => Assert.Equal(3 * 5, c.X.Count));   // two surfaces and the sum, five points per bar
        var r = SeidelEngine.Compute(lens, system);
        double sumTop = OpticsConventions.FromMeters(r.Sum.S1, system.Units.LensUnit);
        F.Close(sumTop, bars.Curves[0].Y[11], 1e-12 * Math.Abs(sumTop), "the sum's bar top");   // the system data went through the saved settings
        var fewer = new SeidelDiagramAnalysis(_ => lens).Compute(context, new SeidelDiagramSettings { IgnoreChromatic = true, IgnoreDistortion = true, LastSurface = 1 }, default);
        var fewerBars = Assert.IsType<LinePlot>(Assert.Single(fewer.Plots));
        Assert.Equal(4, fewerBars.Curves.Count);
        Assert.All(fewerBars.Curves, c => Assert.Equal(2 * 5, c.X.Count));
        Assert.Throws<ArgumentException>(() => new SeidelDiagramAnalysis(_ => lens).Compute(context, new SeidelDiagramSettings { FirstSurface = 3 }, default));
    }

    /// <summary>
    /// Full-field aberration on a 5 × 5 grid over the full ±5° field: with radial normalisation
    /// the points with Hx² + Hy² &gt; 1 (the corners among them) are not computed; every computed point
    /// equals <see cref="FieldZernike.Fit"/> at that field; on the y axis of a rotationally
    /// symmetric lens the coma lies along y (Z8 = 0 within the fit's rounding), so its angle is
    /// ±90°; Relative subtracts the average and Average shows it.
    /// </summary>
    [Fact]
    public void FullFieldAberration_MapsTheFittedTermsOverTheField()
    {
        var system = F.Sys(0.01, new[] { 0.0, Theta });
        var lens = F.Singlet(system);
        var analysis = new FullFieldAberrationAnalysis(_ => lens);
        var settings = new FullFieldAberrationSettings
        {
            XFieldSampling = 5, YFieldSampling = 5, Aberration = FullFieldAberration.PrimaryComa, MaximumTerm = 15, PupilSampling = 17,
        };
        var result = analysis.Compute(F.Context(system), settings, default);
        var map = Assert.IsType<AnalysisMapPlot>(Assert.Single(result.Plots)).Field;
        Assert.Equal(5, map.Rows);
        Assert.Equal(5, map.Columns);
        Assert.Equal(-Theta, map.X0);
        Assert.Equal(Theta, map.Y1);
        foreach (var (i, k) in new[] { (0, 0), (0, 4), (4, 0), (4, 4) })
            Assert.True(double.IsNaN(map.Values[i, k]), $"corner ({i}, {k}) is outside the boundary");

        // The point at the top of the y axis (row 4, column 2): field (0, +5°).
        var basis = new ZernikeBasis(ZernikeOrdering.Standard, 15);
        var (pupil, _) = WavefrontAnalysisBase<FullFieldAberrationSettings>.MapNodes(17);
        var data = FieldZernike.WithPoints(system, new[] { (0.0, Theta) });
        var direct = FieldZernike.Fit(lens, data, 2, 0, basis, pupil, new OpdOptions());
        double z7 = direct.Z(7), z8 = direct.Z(8);
        double magnitude = Math.Sqrt(z7 * z7 + z8 * z8);
        F.Close(magnitude, map.Values[4, 2], 1e-12 * magnitude, "the map against a direct fit");
        Assert.True(Math.Abs(z8) <= 1e-9 * Math.Abs(z7), "no x coma on the y axis");

        var relative = analysis.Compute(F.Context(system), settings with { Display = FullFieldDisplay.Relative }, default);
        var average = analysis.Compute(F.Context(system), settings with { Display = FullFieldDisplay.Average }, default);
        var rel = Assert.IsType<AnalysisMapPlot>(Assert.Single(relative.Plots)).Field;
        var avg = Assert.IsType<AnalysisMapPlot>(Assert.Single(average.Plots)).Field;
        var computed = Enumerable.Range(0, 25).Select(q => map.Values[q / 5, q % 5]).Where(double.IsFinite).ToArray();
        Assert.Equal(13, computed.Length);   // H = (0, ±½, ±1) on the axes, (±½, ±½), (±½, 0), (±1, 0): Hx² + Hy² ≤ 1
        double mean = computed.Average();
        F.Close(mean, avg.Values[2, 2], 1e-15 * Math.Abs(mean), "average");
        F.Close(map.Values[4, 2] - mean, rel.Values[4, 2], 1e-15 * Math.Abs(mean), "relative");
    }

    [Fact]
    public void FullFieldAberration_RefusesATermAboveTheMaximum_AndAnOnAxisSystem()
    {
        var system = F.Sys(0.01, new[] { 0.0, Theta });
        var lens = F.Singlet(system);
        var analysis = new FullFieldAberrationAnalysis(_ => lens);
        Assert.Throws<ArgumentException>(() => analysis.Compute(F.Context(system),
            new FullFieldAberrationSettings { Aberration = FullFieldAberration.ZernikeTerm, ZernikeTerm = 40, MaximumTerm = 37 }, default));
        var axial = F.Sys(0.01, new[] { 0.0 });
        Assert.Throws<ArgumentException>(() => analysis.Compute(F.Context(axial), new FullFieldAberrationSettings(), default));
    }
}
