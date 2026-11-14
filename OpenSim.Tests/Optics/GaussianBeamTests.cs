using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-48: the paraxial Gaussian beam against closed forms derived in the comments of each test
/// from the complex beam parameter q = z + i·z_R, 1/q = 1/R − i·λ/(π·w²) (the textbook
/// definitions; Siegman, Lasers, is not on disk, so nothing is copied from it and every expected
/// value is computed in the test). The lens values, waists and wavelengths are arbitrary test
/// inputs, not reference data.
/// </summary>
public sealed class GaussianBeamTests
{
    // ---- Builders (as ParaxialTests) ----

    private static ParaxialSurface S(double radius, double thickness, double n = 1, bool stop = false, bool mirror = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror,
    };

    private static ParaxialSurface Plane(double thickness, double n = 1, bool stop = false) => new()
    {
        Curvature = 0, Thickness = thickness, Medium = ParaxialMedium.Constant(n), IsStop = stop,
    };

    private static ParaxialLens Lens(params ParaxialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new ParaxialSurface { Thickness = double.PositiveInfinity } }
            .Concat(surfaces).Append(new ParaxialSurface()).ToArray()
    };

    /// <summary>System data at one wavelength (µm, in the system's air).</summary>
    private static OpticalSystemData Sys(double micrometers = 0.6328) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
        Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = micrometers } } },
    };

    /// <summary>An equiconvex thin lens of focal length f in glass of index 1.5 (c = ±1/(2f(n − 1)), lensmaker).</summary>
    private static ParaxialSurface[] ThinLens(double f, double distanceAfter, bool stop = false)
    {
        const double n = 1.5;
        double r = 2 * f * (n - 1);
        return new[] { S(r, 0, n, stop), S(-r, distanceAfter) };
    }

    private static GaussianBeamTrace Trace(ParaxialLens lens, GaussianBeamInput input, double micrometers = 0.6328) =>
        GaussianBeamTrace.Create(ParaxialSystem.Create(lens, Sys(micrometers)), input);

    private static void Close(double expected, double actual, double relative)
    {
        double scale = Math.Max(Math.Abs(expected), double.Epsilon);
        Assert.True(Math.Abs(actual - expected) <= relative * scale,
            $"Expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {relative * scale:R}).");
    }

    // ---- Free space ----

    [Theory]
    [InlineData(1e-3, -0.2, 0.3)]      // waist in front of surface 1
    [InlineData(0.2e-3, 0.1, 0.25)]    // waist between surface 1 and the image: the beam passes through it
    [InlineData(0.5e-3, 0, 1.0)]       // waist on surface 1
    public void FreeSpace_FollowsTheClosedFormsOfWAndR(double w0, double waistPosition, double length)
    {
        const double um = 0.6328;
        double lambda = um * 1e-6;
        var trace = Trace(Lens(Plane(length, stop: true)), GaussianBeamInput.Round(w0, waistPosition), um);
        // In a medium of index 1: z_R = π·w0²/λ; at a distance z past the waist
        // w(z) = w0·√(1 + (z/z_R)²), R(z) = z·(1 + (z_R/z)²), divergence θ = λ/(π·w0), Gouy phase
        // ψ(z) = atan(z/z_R). These follow from q = z + i·z_R and 1/q = 1/R − i·λ/(π·w²):
        // 1/q = (z − i·z_R)/(z² + z_R²), so R = (z² + z_R²)/z and w² = λ(z² + z_R²)/(π·z_R).
        double zR = Math.PI * w0 * w0 / lambda;
        void Check(GaussianBeamState s, double z, double gouy)
        {
            Close(w0 * Math.Sqrt(1 + z / zR * (z / zR)), s.BeamRadius, 1e-14);
            Close(w0, s.WaistRadius, 1e-14);
            Close(zR, s.RayleighRange, 1e-14);
            Close(lambda / (Math.PI * w0), s.Divergence, 1e-14);
            Assert.Equal(-z, s.WaistOffset, 1e-15);    // the waist lies z behind the point, along z
            if (z == 0) Assert.True(double.IsPositiveInfinity(s.WavefrontRadius));
            else Close(z * (1 + zR / z * (zR / z)), s.WavefrontRadius, 1e-13);
            Assert.Equal(gouy, s.GouyPhase, 1e-14);
        }
        double z1 = -waistPosition, z2 = length - waistPosition;
        Check(trace.X[0], z1, 0);
        Check(trace.X[1], z1, 0);                      // a plane surface in air changes nothing
        Check(trace.X[^1], z2, Math.Atan(z2 / zR) - Math.Atan(z1 / zR));
        Assert.Equal(trace.X, trace.Y);
    }

    [Fact]
    public void AFlatInterface_KeepsTheWaist_AndStretchesTheRayleighRangeByTheIndex()
    {
        double w0 = 0.3e-3, n = 1.5, d = 0.05, um = 1.064, lambda = um * 1e-6;
        var trace = Trace(Lens(Plane(0.1, n, stop: true), Plane(0.2)), GaussianBeamInput.Round(w0, -d), um);
        // In a medium of relative index n the wavelength is λ/n, so z_R = π·w0²·n/λ. At a flat
        // interface y and the reduced slope n·u are continuous, so q/n is: arriving with
        // q = d + i·z_R (index 1) the beam leaves with q = n·d + i·n·z_R. The waist radius
        // (√(λ_n·z_R,n/π) = √(λ·z_R/π)) is unchanged, its apparent distance is n·d, and the beam
        // radius at the interface is continuous.
        double zR = Math.PI * w0 * w0 / lambda;
        var arriving = trace.X[0];
        var inside = trace.X[1];
        Close(n * zR, inside.RayleighRange, 1e-14);
        Close(w0, inside.WaistRadius, 1e-14);
        Close(n * d, inside.DistanceFromWaist, 1e-14);
        Close(-n * d, inside.WaistOffset, 1e-14);
        Close(arriving.BeamRadius, inside.BeamRadius, 1e-14);
        Close(lambda / n / (Math.PI * w0), inside.Divergence, 1e-14);
        // Out again into air after 0.1 m of glass: the beam is d + 0.1/n past a waist of w0.
        var after = trace.X[2];
        Close(zR, after.RayleighRange, 1e-14);
        Close(d + 0.1 / n, after.DistanceFromWaist, 1e-14);
    }

    // ---- Thin lens ----

    [Theory]
    [InlineData(0.1, 0.25, 0.4e-3)]     // waist well outside the focal length
    [InlineData(0.1, 0.1, 0.4e-3)]      // waist at the front focal point: the output waist is at the back focal point
    [InlineData(0.05, 0.02, 0.1e-3)]    // inside the focal length
    [InlineData(-0.08, 0.15, 0.6e-3)]   // a negative lens: a virtual output waist
    public void ThinLens_FollowsTheWaistTransformationFormula(double f, double s, double w0)
    {
        double um = 0.6328, lambda = um * 1e-6;
        double zR = Math.PI * w0 * w0 / lambda;
        // At the lens q = s + i·z_R; the lens makes 1/q′ = 1/q − 1/f, q′ = f·q/(f − q). With
        // a = s − f: q′ = −f(s + i·z_R)(a − i·z_R)/(a² + z_R²), so the output waist lies
        // s′ = −Re q′ = f·(s·a + z_R²)/(a² + z_R²) = f + a·f²/(a² + z_R²) after the lens, its
        // Rayleigh range is Im q′ = f²·z_R/(a² + z_R²), and w0′ = w0·|f|/√(a² + z_R²).
        double a = s - f, den = a * a + zR * zR;
        double sOut = f * (s * a + zR * zR) / den;
        double zROut = f * f * zR / den;
        double w0Out = w0 * Math.Abs(f) / Math.Sqrt(den);
        var trace = Trace(Lens(ThinLens(f, Math.Abs(sOut) + 0.01, stop: true)), GaussianBeamInput.Round(w0, -s), um);
        var after = trace.X[2];
        Close(sOut, after.WaistOffset, 1e-12);
        Close(zROut, after.RayleighRange, 1e-12);
        Close(w0Out, after.WaistRadius, 1e-12);
        Close(trace.X[0].BeamRadius, after.BeamRadius, 1e-13);   // the beam radius is continuous at a thin lens
    }

    [Fact]
    public void ThinLens_ImagesTheWaistIntoTheImageSurface_WhenThatIsTheOutputWaist()
    {
        double f = 0.1, s = 0.3, w0 = 0.2e-3, um = 0.6328, lambda = um * 1e-6;
        double zR = Math.PI * w0 * w0 / lambda, a = s - f, den = a * a + zR * zR;
        double sOut = f * (s * a + zR * zR) / den;   // derived in ThinLens_FollowsTheWaistTransformationFormula
        var trace = Trace(Lens(ThinLens(f, sOut, stop: true)), GaussianBeamInput.Round(w0, -s), um);
        var image = trace.X[^1];
        Assert.True(Math.Abs(image.DistanceFromWaist) <= 1e-12 * image.RayleighRange);
        Close(w0 * f / Math.Sqrt(den), image.BeamRadius, 1e-12);
        Assert.True(Math.Abs(image.WavefrontRadius) > 1e6 * image.RayleighRange);   // flat at the waist
    }

    // ---- Telescopes ----

    [Theory]
    [InlineData(0.2, 0.05)]     // Keplerian
    [InlineData(0.2, -0.05)]    // Galilean
    [InlineData(0.05, 0.15)]    // Keplerian expander
    public void ModeMatchedTelescope_ReturnsTheInputWaistScaledByItsMagnification(double f1, double f2)
    {
        double um = 1.064, lambda = um * 1e-6, w0 = 0.5e-3;
        double zR = Math.PI * w0 * w0 / lambda;
        // Lenses f1 and f2 at d = f1 + f2 make an afocal system with A = −f2/f1, C = 0,
        // D = −f1/f2 (ParaxialTests, Telescope_…). Then q′ = (A·q + B)/D = (f2/f1)²·q + B/D: the
        // Rayleigh range is multiplied by (f2/f1)² and the waist by |f2/f1|, wherever the input
        // waist is. With the input waist at the front focal point of lens 1 (s = f1) the thin-lens
        // formula puts the intermediate waist at the back focal point of lens 1, which is the
        // front focal point of lens 2, and so the output waist at the back focal point of lens 2:
        // the mode-matched case.
        double m = Math.Abs(f2 / f1);
        foreach (double s in new[] { f1, 0.0, 0.3, -0.05 })
        {
            var lens = Lens(ThinLens(f1, f1 + f2, stop: true).Concat(ThinLens(f2, Math.Abs(f2))).ToArray());
            var trace = Trace(lens, GaussianBeamInput.Round(w0, -s), um);
            var output = trace.X[4];
            Close(m * w0, output.WaistRadius, 1e-12);
            Close(m * m * zR, output.RayleighRange, 1e-12);
            if (s == f1)
            {
                Close(f2, output.WaistOffset, 1e-11);
                if (f2 > 0) Close(m * w0, trace.X[^1].BeamRadius, 1e-11);   // the image surface is at that waist (a Galilean's is virtual)
            }
        }
    }

    // ---- Mirrors ----

    [Fact]
    public void ConcaveMirror_ActsAsALensOfFocalLengthHalfItsRadius_WithTheWaistTowardMinusZ()
    {
        double r = -0.4, f = -r / 2, s = 0.5, w0 = 0.3e-3, um = 0.6328, lambda = um * 1e-6;
        // A concave mirror of radius R (R < 0 facing +z light, §3) has power −2/R = 1/f. The
        // beam returns along −z: the thin-lens formula gives the output waist s′ in front of the
        // mirror along the beam, which is −s′ along z (the sign of a thickness after a mirror).
        double zR = Math.PI * w0 * w0 / lambda, a = s - f, den = a * a + zR * zR;
        double sOut = f * (s * a + zR * zR) / den;
        var trace = Trace(Lens(S(r, -0.3, mirror: true, stop: true)), GaussianBeamInput.Round(w0, -s), um);
        var after = trace.X[1];
        Assert.Equal(-1, after.SignedIndex);
        Close(-sOut, after.WaistOffset, 1e-12);
        Close(w0 * f / Math.Sqrt(den), after.WaistRadius, 1e-12);
        Close(f * f * zR / den, after.RayleighRange, 1e-12);
        // At the image surface 0.3 m back along −z, the beam is 0.3 − s′ past its waist.
        Close(0.3 - sOut, trace.X[^1].DistanceFromWaist, 1e-12);
    }

    // ---- M² ----

    [Fact]
    public void MSquared_ScalesTheSizesByM_AndKeepsTheEmbeddedBeamsPropagation()
    {
        double w0 = 1e-3, m2 = 4, um = 1.064, lambda = um * 1e-6;
        var lens = Lens(ThinLens(0.1, 0.05, stop: true).Concat(ThinLens(-0.05, 0.2)).ToArray());
        var real = Trace(lens, GaussianBeamInput.Round(w0, -0.2, m2), um);
        // The embedded Gaussian of an M² beam of waist W0 has waist W0/M, M = √M²: a perfect
        // Gaussian of that waist must propagate identically, and the real beam's radius, waist
        // and divergence are M times its own. The real divergence is then M²·λ/(π·W0).
        var embedded = Trace(lens, GaussianBeamInput.Round(w0 / Math.Sqrt(m2), -0.2), um);
        double m = Math.Sqrt(m2);
        for (int j = 0; j < lens.Surfaces.Count; j++)
        {
            var r = real.X[j];
            var e = embedded.X[j];
            Close(e.BeamRadius, r.EmbeddedBeamRadius, 1e-14);
            Close(m * e.BeamRadius, r.BeamRadius, 1e-14);
            Close(m * e.WaistRadius, r.WaistRadius, 1e-14);
            Close(m * e.Divergence, r.Divergence, 1e-14);
            Close(e.RayleighRange, r.RayleighRange, 1e-14);
            Close(e.WaistOffset, r.WaistOffset, 1e-14);
            Assert.Equal(e.GouyPhase, r.GouyPhase, 1e-15);
        }
        Close(m2 * lambda / (Math.PI * w0), real.X[0].Divergence, 1e-14);
    }

    // ---- Splitting a gap, and the system matrix ----

    [Fact]
    public void SplittingAGap_WithADummySurface_ChangesNothing()
    {
        double w0 = 0.4e-3;
        var whole = Lens(S(0.05, 0.006, 1.6, stop: true), S(-0.08, 0.12));
        var split = Lens(S(0.05, 0.002, 1.6, stop: true), Plane(0.004, 1.6), S(-0.08, 0.05), Plane(0.07));
        var a = Trace(whole, GaussianBeamInput.Round(w0, -0.1));
        var b = Trace(split, GaussianBeamInput.Round(w0, -0.1));
        // Two transfers t1/n and t2/n add to one of (t1 + t2)/n, and a plane surface in a medium
        // of the same index has no power: q̂ at the image is the same up to rounding.
        var qa = a.X[^1].ReducedQ;
        var qb = b.X[^1].ReducedQ;
        Assert.True(Complex.Abs(qa - qb) <= 1e-14 * Complex.Abs(qa), $"{qa} vs {qb}");
        Close(a.X[^1].BeamRadius, b.X[^1].BeamRadius, 1e-14);
        Assert.Equal(a.X[^1].GouyPhase, b.X[^1].GouyPhase, 1e-14);
        Close(a.X[2].BeamRadius, b.X[3].BeamRadius, 1e-14);
    }

    [Fact]
    public void SurfaceBySurface_EqualsTheSystemMatrix_AndTheGouyPhaseIsMinusArgOfAPlusBOverQ()
    {
        var lens = Lens(S(0.05, 0.006, 1.6, stop: true), S(-0.08, 0.03), S(-0.04, 0.004, 1.7), S(0.2, 0.09));
        var model = ParaxialSystem.Create(lens, Sys());
        var trace = GaussianBeamTrace.Create(model, GaussianBeamInput.Round(0.3e-3, -0.05));
        int last = lens.LastSurfaceIndex;
        // The whole system from surface 1 to the image surface: the system matrix followed by
        // the last transfer. q̂ obeys the bilinear law with it.
        var m = ParaxialMatrix.Transfer(lens.Surfaces[last].Thickness / model.SignedIndices[last]) * model.SystemMatrix;
        var expected = GaussianBeamTrace.Transform(m, trace.X[0].ReducedQ);
        var actual = trace.X[^1].ReducedQ;
        Assert.True(Complex.Abs(expected - actual) <= 1e-13 * Complex.Abs(expected), $"{expected} vs {actual}");
        // Gouy phase: for a transfer d, A + B/q = (q + d)/q, and arg q = π/2 − atan(z/z_R), so
        // −arg(A + B/q) = atan((z + d)/z_R) − atan(z/z_R); a thin surface (A = 1, B = 0) adds
        // nothing, and a product of such factors multiplies, so for a total below π the
        // accumulated Gouy phase is −arg(A + B/q̂) of the whole matrix.
        double gouy = -Complex.Add(m.A, m.B / trace.X[0].ReducedQ).Phase;
        Assert.Equal(gouy, trace.X[^1].GouyPhase, 1e-13);
    }

    // ---- Simple astigmatism ----

    [Fact]
    public void AnAstigmaticBeam_PropagatesEachPlaneAsARoundBeamWouldAlone()
    {
        var lens = Lens(ThinLens(0.1, 0.15, stop: true));
        var px = new GaussianBeamPlane(0.3e-3, -0.2, 1.2);
        var py = new GaussianBeamPlane(0.8e-3, 0.05, 2.5);
        var both = Trace(lens, new GaussianBeamInput(px, py));
        var xOnly = Trace(lens, new GaussianBeamInput(px, px));
        var yOnly = Trace(lens, new GaussianBeamInput(py, py));
        Assert.False(both.Input.IsRound);
        Assert.Equal(xOnly.X, both.X);
        Assert.Equal(yOnly.Y, both.Y);
        var profile = both.Profile(10);
        Assert.Equal(xOnly.Profile(10).RadiusX, profile.RadiusX);
        Assert.Equal(yOnly.Profile(10).RadiusY, profile.RadiusY);
    }

    // ---- Profile, refusals ----

    [Fact]
    public void TheProfile_MatchesTheSurfaceStates_AtEachSurface()
    {
        var lens = Lens(S(0.05, 0.006, 1.6, stop: true), S(-0.08, 0.1));
        var trace = Trace(lens, GaussianBeamInput.Round(0.5e-3, -0.1, 2));
        var p = trace.Profile(8);
        Assert.Equal(2 * 9, p.Path.Count);
        Assert.Equal(new[] { 0, 0.006, 0.106 }, p.SurfacePath.Select(v => Math.Round(v, 12)));
        Close(trace.X[1].BeamRadius, p.RadiusX[0], 1e-14);
        Close(trace.X[2].BeamRadius, p.RadiusX[9], 1e-14);
        Close(trace.X[^1].BeamRadius, p.RadiusX[^1], 1e-13);
        Close(trace.X[^1].EmbeddedBeamRadius, p.EmbeddedRadiusX[^1], 1e-13);
        Assert.Throws<ArgumentOutOfRangeException>(() => trace.Profile(0));
    }

    [Fact]
    public void ABadInput_IsRefusedWithEveryReason()
    {
        var model = ParaxialSystem.Create(Lens(Plane(0.1, stop: true)), Sys());
        var bad = new GaussianBeamInput(new GaussianBeamPlane(0, 0, 1), new GaussianBeamPlane(1e-3, double.NaN, 0.5));
        var message = Assert.Throws<ArgumentException>(() => GaussianBeamTrace.Create(model, bad)).Message;
        Assert.Contains("x: the waist radius", message);
        Assert.Contains("y: the waist position", message);
        Assert.Contains("y: M²", message);
    }

    // ---- The analysis ----

    [Fact]
    public void TheAnalysis_IsRegistered_AndSaysSoWhenThereAreNoSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(GaussianBeamAnalysis.SavedId, out var analysis));
        var result = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), analysis.CreateDefaultSettings(), default);
        Assert.Equal(new[] { "No surfaces" }, result.Sections.Select(s => s.Heading));
    }

    [Fact]
    public void TheAnalysis_ListsEverySurface_AndPlotsTheBeamRadius()
    {
        var lens = Lens(S(0.05, 0.006, 1.6, stop: true), S(-0.08, 0.05));
        var analysis = new GaussianBeamAnalysis(_ => lens);
        var context = new OpticsAnalysisContext("P", new OpticsSettings());
        var result = analysis.Compute(context, new GaussianBeamSettings(), default);
        Assert.Equal(new[] { "Input beam", "Paraxial Gaussian Beam" }, result.Sections.Select(s => s.Heading));
        Assert.Equal(lens.Surfaces.Count, result.Sections[1].TableRows.Count);
        var plot = Assert.IsType<LinePlot>(Assert.Single(result.Plots));
        Assert.Single(plot.Curves);
        Assert.Equal(2 * 41, plot.Curves[0].X.Count);

        var astigmatic = analysis.Compute(context, new GaussianBeamSettings { SameInY = false, WaistY = 0.2, MSquaredY = 1.5 }, default);
        Assert.Equal(new[] { "Input beam", "Paraxial Gaussian Beam, x", "Paraxial Gaussian Beam, y" }, astigmatic.Sections.Select(s => s.Heading));
        Assert.Equal(4, Assert.IsType<LinePlot>(astigmatic.Plots[0]).Curves.Count);   // x, y, and the embedded pair

        Assert.Throws<ArgumentException>(() => analysis.Compute(context, new GaussianBeamSettings { Wavelength = 2 }, default));
        Assert.Throws<ArgumentException>(() => analysis.Compute(context, new GaussianBeamSettings { WaistX = 0 }, default));
        Assert.Throws<ArgumentException>(() => analysis.Compute(context, new GaussianBeamSettings { PointsPerGap = 0 }, default));
    }

    [Fact]
    public void TheSettings_RoundTripEveryField_AndConvertMillimetersToMeters()
    {
        var analysis = new GaussianBeamAnalysis();
        var settings = new GaussianBeamSettings
        {
            Wavelength = 2, WaistX = 0.25, WaistPositionX = -12.5, MSquaredX = 1.3, SameInY = false,
            WaistY = 0.75, WaistPositionY = 3.5, MSquaredY = 2.2, PointsPerGap = 17,
        };
        // Every property differs from its default, so a field the codec dropped would fail here.
        var defaults = new GaussianBeamSettings();
        foreach (var property in typeof(GaussianBeamSettings).GetProperties())
            Assert.NotEqual(property.GetValue(defaults), property.GetValue(settings));
        Assert.Equal(settings, OpticsAnalysisSettingsCodec.Clone(analysis, settings));

        var input = settings.ToInput();
        Assert.Equal(new GaussianBeamPlane(0.25e-3, -12.5e-3, 1.3), input.X);
        Assert.Equal(new GaussianBeamPlane(0.75e-3, 3.5e-3, 2.2), input.Y);
        Assert.True((settings with { SameInY = true }).ToInput().IsRound);
    }
}
