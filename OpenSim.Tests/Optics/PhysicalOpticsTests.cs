using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Merit.Catalogue;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.PhysicalOptics;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-49: physical optics propagation against closed forms derived in each test's comments
/// (Gaussian beams from the paraxial wave equation, the Airy pattern of a disc from the
/// Fraunhofer integral, Talbot self-imaging from the angular-spectrum transfer function, the
/// Poisson spot from the Fresnel integral on the axis). Nothing is copied from a book: Siegman,
/// Goodman, Lawrence and Schmidt are not on disk. Wavelengths, waists, distances and focal
/// lengths are arbitrary test inputs, not reference data.
/// <para>
/// <b>Why the tolerances are tight.</b> A Gaussian sampled with the help's Auto width
/// X = ω0·√(π·n) has ω0/Δx = √(n/π) samples per 1/e² radius (6.4 for n = 128). Its spectrum at
/// the Nyquist frequency is exp(−π²ω0²/(4Δx²)) = exp(−π·n/4) ≈ e^{−100} of its peak, and the
/// field at the array edge exp(−π·n/4) as well, so the discrete transforms reproduce the
/// continuous integrals to rounding (Poisson summation), and every Gaussian quantity below is
/// held to 1e-9 relative. Hard-edged fields (a disc) are not band-limited; their tolerances are
/// derived where they are used.
/// </para>
/// </summary>
public sealed class PhysicalOpticsTests : IDisposable
{
    private const double Lambda = 1e-6;   // test wavelength, meters (1 µm), in air
    private const double W0 = 1e-3;       // test waist, meters
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-pop-" + Guid.NewGuid().ToString("N"));

    public PhysicalOpticsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- Builders ----

    private static PopField Gaussian(int n = 128, double w0 = W0, double index = 1)
    {
        var beam = new PopBeamDefinition { Type = PopBeamType.GaussianWaist, WaistX = w0, WaistY = w0 };
        double width = PopBeams.AutoWidth(w0, n);
        return PopBeams.Create(beam, n, n, width, width, Lambda, index, LensUnit.Millimeter, new List<string>());
    }

    private static double ZR(double w0 = W0, double index = 1) => Math.PI * w0 * w0 * index / Lambda;

    private static SequentialSurface Surface(double thickness, double n = 1, bool stop = false, SurfaceShape? shape = null,
        SurfaceAperture? aperture = null) => new()
    {
        Thickness = thickness, Medium = ParaxialMedium.Constant(n), IsStop = stop, Shape = shape, Aperture = aperture,
    };

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double micrometers = 1.0, params double[] fieldY) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 10 },
        Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = micrometers } } },
        Fields = new FieldSet { Points = fieldY.Length == 0 ? new[] { new FieldPoint() } : fieldY.Select(y => new FieldPoint { Y = y }).ToArray() },
    };

    private static void Close(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    /// <summary>The phase of the field E (reference added back) at a sample, relative to the centre.</summary>
    private static double PhaseAt(PopField f, int row, int column) =>
        (f.TrueField(row, column) / f.TrueField(f.Ny / 2, f.Nx / 2)).Phase;

    /// <summary>
    /// Checks a field against the free-space Gaussian a distance z past its waist. Derivation:
    /// the paraxial wave equation 2ik∂u/∂z + ∇²u = 0 (physics convention, a wave e^{+ikz}) has
    /// the solution u = (q0/q)·exp(ik·r²/(2q)) with q = z − i·z_R, q0 = −i·z_R, z_R = πω0²/λ.
    /// Then |u|² is a Gaussian of 1/e² radius ω(z) = ω0√(1 + (z/z_R)²), so the width 2σ is ω(z)
    /// and the peak irradiance 2P/(πω²); the phase is k·r²/(2R) with R = z + z_R²/z, and on the
    /// axis arg(q0/q) = −atan(z/z_R) (the Gouy phase, entering with a minus sign in this
    /// convention).
    /// </summary>
    private static void AssertGaussian(PopField f, double z, double power, double w0 = W0, double index = 1, double tolerance = 1e-9)
    {
        double zR = ZR(w0, index), w = w0 * Math.Sqrt(1 + z / zR * (z / zR));
        var data = PopBeamMetrics.Compute(f);
        Close(power, data.Power, 1e-12 * power, "power");
        Close(w, data.WidthX, tolerance * w, "width x");
        Close(w, data.WidthY, tolerance * w, "width y");
        Close(2 * power / (Math.PI * w * w), data.PeakIrradiance, tolerance * data.PeakIrradiance, "peak irradiance");
        Close(1, data.MSquaredX, tolerance, "M² x");
        var centre = f.TrueField(f.Ny / 2, f.Nx / 2);
        Close(-Math.Atan(z / zR), centre.Phase, tolerance, "Gouy phase on the axis");
        // Curvature: two samples off the axis, the phase k·x²/(2R) (zero at the waist).
        double x = f.X(f.Nx / 2 + 2), k = 2 * Math.PI * index / Lambda;
        double expected = z == 0 ? 0 : k * x * x / (2 * (z + zR * zR / z));
        Close(expected, PhaseAt(f, f.Ny / 2, f.Nx / 2 + 2), tolerance, "wavefront phase two samples off the axis");
    }

    // ---- Gate: a Gaussian stays Gaussian through free space, every case ----

    [Fact]
    public void FreeSpace_InsideToInside_IsAngularSpectrum_AndStaysGaussian()
    {
        var f0 = Gaussian();
        Close(1, f0.Power, 1e-12, "initial power");
        Assert.Equal(PopReference.Plane, f0.Reference);
        var log = new List<PopStep>();
        double z = 0.5 * ZR();
        var f = PopPropagator.Propagate(f0, z, "test", log);
        Assert.Equal(new[] { "PTP" }, log.Select(s => s.Operator));
        Assert.Equal(PopReference.Plane, f.Reference);
        Assert.Equal(f0.Dx, f.Dx);   // the angular spectrum keeps the spacing
        AssertGaussian(f, z, 1);
    }

    [Fact]
    public void FreeSpace_InsideToOutside_IsPtpThenWts_WithTheFresnelSpacing()
    {
        var f0 = Gaussian();
        var log = new List<PopStep>();
        double z = 3 * ZR();
        var f = PopPropagator.Propagate(f0, z, "test", log);
        // From the waist the PTP leg is zero and skipped.
        Assert.Equal(new[] { "WTS" }, log.Select(s => s.Operator));
        Assert.Equal(PopReference.Sphere, f.Reference);
        Close(z, f.ReferenceRadius, 1e-15 * z, "reference radius");
        // Help, "Propagating In and Out of the Rayleigh Range": Δx2 = λ|z2|/(nx·Δx1).
        Close(Lambda * z / (f0.Nx * f0.Dx), f.Dx, 1e-15 * f.Dx, "spacing after WTS");
        AssertGaussian(f, z, 1);
    }

    [Fact]
    public void FreeSpace_OutsideToOutside_And_OutsideToInside_StayGaussian()
    {
        var f0 = Gaussian();
        double zR = ZR();
        var out3 = PopPropagator.Propagate(f0, 3 * zR, "a");
        var log = new List<PopStep>();
        var out5 = PopPropagator.Propagate(out3, 2 * zR, "b", log);
        Assert.Equal(new[] { "STW", "WTS" }, log.Select(s => s.Operator));
        AssertGaussian(out5, 5 * zR, 1);

        log.Clear();
        var in05 = PopPropagator.Propagate(out3, -2.5 * zR, "c", log);
        Assert.Equal(new[] { "STW", "PTP" }, log.Select(s => s.Operator));
        Assert.Equal(PopReference.Plane, in05.Reference);
        Close(f0.Dx, in05.Dx, 1e-15 * f0.Dx, "spacing back at the waist's grid");
        AssertGaussian(in05, 0.5 * zR, 1);

        // Before the waist (a virtual step back): z = −4 z_R, a sphere of negative radius.
        var before = PopPropagator.Propagate(f0, -4 * zR, "d");
        Assert.Equal(PopReference.Sphere, before.Reference);
        AssertGaussian(before, -4 * zR, 1);
    }

    [Fact]
    public void FreeSpace_GoingOutAndBack_ReturnsTheInitialSamples()
    {
        // Every operator is unitary up to rounding, and STW undoes WTS exactly in exact
        // arithmetic, so out and back reproduces the samples to rounding.
        var f0 = Gaussian();
        var back = PopPropagator.Propagate(PopPropagator.Propagate(f0, 7 * ZR(), "out"), -7 * ZR(), "back");
        Assert.Equal(f0.Dx, back.Dx, 15);
        double peak = Math.Sqrt(PopBeamMetrics.Peak(f0.Samples)), worst = 0;
        for (int r = 0; r < f0.Ny; r++)
            for (int c = 0; c < f0.Nx; c++) worst = Math.Max(worst, (back.Samples[r, c] - f0.Samples[r, c]).Magnitude);
        Assert.True(worst < 1e-12 * peak, $"largest change {worst:R} of a peak amplitude {peak:R}");
    }

    // ---- Gate: a Gaussian through a lens ----

    [Fact]
    public void ParaxialThinLens_GaussianFocus_MatchesTheQLaw_WidthCurvatureAndGouy()
    {
        // A waist ω0 on a thin lens of focal length f, observed d after it. Derivation (Siegman's
        // q = z + i·z_R, 1/q = 1/R − iλ/(πω²)): at the lens q1 = i·z_R; the lens's phase −k·r²/(2f)
        // makes 1/q2 = 1/q1 − 1/f; free space makes q3 = q2 + d. The width is
        // ω² = −λ/(π·Im(1/q3)), the curvature R = 1/Re(1/q3), and the axial phase changes by
        // arg(q3) − arg(q2) (the physics-convention amplitude q0/q of AssertGaussian, with
        // q_physics = conj(q)); the lens adds no phase on the axis and the waist starts at 0.
        double f = 0.5, d = 0.45;
        var lens = Lens(Surface(d, stop: true, shape: new ParaxialShape(f)));
        var run = PopEngine.Run(lens, Sys(), new PopRequest
        {
            Beam = new PopBeamDefinition { Type = PopBeamType.GaussianWaist, WaistX = W0, WaistY = W0 },
            SamplingX = 128, SamplingY = 128, Transfer = PopTransfer.ParaxialThinLens,
        });
        var q1 = new Complex(0, ZR());
        var q2 = 1 / (1 / q1 - 1 / f);
        var q3 = q2 + d;
        double w = Math.Sqrt(-Lambda / (Math.PI * (1 / q3).Imaginary));
        double radius = 1 / (1 / q3).Real;
        var field = run.Field;
        var data = PopBeamMetrics.Compute(field);
        Close(1, data.Power, 1e-12, "power after the lens");
        Close(w, data.WidthX, 1e-8 * w, "width at d");
        Close(2 / (Math.PI * w * w), data.PeakIrradiance, 1e-8 * data.PeakIrradiance, "peak irradiance at d");
        Close(q3.Phase - q2.Phase, field.TrueField(field.Ny / 2, field.Nx / 2).Phase, 1e-8, "axial (Gouy) phase");
        double x = field.X(field.Nx / 2 + 2), k = 2 * Math.PI / Lambda;
        Close(k * x * x / (2 * radius), PhaseAt(field, field.Ny / 2, field.Nx / 2 + 2), 1e-8, "wavefront curvature phase");
        // The pilot carries the same q.
        Close(q3.Real, field.Pilot.DistanceFromWaist, 1e-12, "pilot position");
        Close(q3.Imaginary, field.Pilot.RayleighRange, 1e-12 * q3.Imaginary, "pilot Rayleigh range");
        Assert.Empty(run.Warnings);
    }

    [Fact]
    public void RayBased_PlaneParallelPlate_IsFreeSpaceOfReducedThickness()
    {
        // Flat surfaces add no path to the probing rays (normal incidence, no sag), so the plate
        // only changes the medium: inside it the beam has λ/n and z_R·n. The beam after a plate
        // of thickness t and a gap g is the free-space Gaussian at L = t/n + g past its waist
        // (the reduced distance of the q-law): ω(L) and Gouy atan(L/z_R) in air.
        double n = 1.5, t = 2.0, g = 1.5;
        var lens = Lens(Surface(t, n: n, stop: true), Surface(g));
        var run = PopEngine.Run(lens, Sys(), new PopRequest
        {
            Beam = new PopBeamDefinition { Type = PopBeamType.GaussianWaist, WaistX = W0, WaistY = W0 },
            SamplingX = 128, SamplingY = 128, Transfer = PopTransfer.RayBased,
        });
        AssertGaussian(run.Field, t / n + g, 1, tolerance: 1e-8);
        Assert.Empty(run.Warnings);
    }

    [Fact]
    public void RayBased_ParaxialSurface_ProbePathIsThePerfectLensSphere()
    {
        // The Paraxial surface (OP-27) at infinite conjugates adds n′·(|O→F| − |X→F|) for a ray
        // parallel to the axis at X: f − √(f² + r²), the sphere converging on the focus F. The
        // probe launches exactly such rays, on a flat surface (no step back to the plane).
        double f = 0.25;
        var lens = Lens(Surface(f, stop: true, shape: new ParaxialShape(f)));
        var trace = SequentialTrace.Create(lens, Sys());
        var s = lens.Surfaces[1];
        foreach (var (x, y) in new[] { (1e-3, 0.0), (0.0, -2e-3), (3e-3, 4e-3) })
        {
            var probe = PopSurfaceTransfer.Probe(trace, s, s.Shape, 1, x, y, 1, 1, 1);
            Assert.Equal(RayStatus.Ok, probe.Status);
            double r2 = x * x + y * y;
            // f − √(f² + r²) cancels: both the closed form and the traced path carry an absolute
            // rounding error of order ε·f (ε = 2.2e-16), not ε times the small path itself.
            Close(f - Math.Sqrt(f * f + r2), probe.Path, 1e-15 * f, $"path at ({x}, {y})");
        }
    }

    [Fact]
    public void RayBased_ConvexSurface_ProbePathIsTheSagStepToFirstOrder()
    {
        // A sphere of curvature c between air and glass n: the axis-parallel probe meets the
        // surface at the sag s = c·r²/(1 + √(1 − c²r²)), refracts, and returns to the vertex
        // plane. To second order in r the path is (n_before − n_after)·s, the thin-element
        // phase; the remaining terms are of order s·θ′² (θ′ the refracted angle, ~ c·r·(1 − 1/n)),
        // so with c·r = 1e-3 they are below 1e-6 of the path itself.
        double c = 1 / 0.1, nGlass = 1.5;
        var lens = Lens(new SequentialSurface { Curvature = c, Thickness = 0.01, Medium = ParaxialMedium.Constant(nGlass), IsStop = true }, Surface(0.1));
        var trace = SequentialTrace.Create(lens, Sys());
        double r = 1e-4;
        var probe = PopSurfaceTransfer.Probe(trace, lens.Surfaces[1], null, 1, r, 0, 1, 1, nGlass);
        double sag = c * r * r / (1 + Math.Sqrt(1 - c * c * r * r));
        Close((1 - nGlass) * sag, probe.Path, 1e-6 * Math.Abs((1 - nGlass) * sag), "probe path");
        Close((nGlass - 1) * c, PopSurfaceTransfer.Power(lens.Surfaces[1], 1, 1, 1, nGlass), 1e-15 * c, "paraxial power (n′ − n)·c");
    }

    // ---- Gate: a top hat's far field is the Airy pattern ----

    [Fact]
    public void TopHat_FarField_IsTheAiryPattern()
    {
        // Fraunhofer: a uniform disc of radius a gives, at distance z ≫ a²/λ,
        // I(r)/I(0) = [2·J1(v)/v]², v = k·a·r/z (the Fourier transform of a disc, a textbook
        // integral). The propagation is WTS, the full Fresnel transform; its quadratic phase
        // across the disc is π·a²/(λz), here 1/300 rad (z = 300·πa²/λ), which changes the
        // normalised pattern by about its square, ~1e-5. The disc is sampled at sample centres with a/Δx = 16; its far field
        // differs from the continuous disc's by the staircase of its edge: to first order its
        // area, N_in·Δx² instead of πa², which the test removes by using the radius
        // a_eff = √(N_in·Δx²/π) of the disc of the same area; then the edge's scatter of ±Δx/2
        // about a_eff, and the hard edge's spectrum folded at the Nyquist frequency (the Airy
        // amplitude there, of order 2·√(2/(πv))/v with v = 2π·16, ~1.4e-3). Together these are a
        // few 1e-3 of the peak; the test allows 1e-2.
        int n = 256;
        double a = 1e-3, width = 16 * a;
        var notes = new List<string>();
        var f0 = PopBeams.Create(new PopBeamDefinition { Type = PopBeamType.TopHat, WaistX = a, WaistY = a }, n, n, width, width,
            Lambda, 1, LensUnit.Millimeter, notes);
        double z = 300 * f0.Pilot.RayleighRange;
        var f = PopPropagator.Propagate(f0, z, "far field");
        Assert.Equal(PopReference.Sphere, f.Reference);
        Close(f0.Power, f.Power, 1e-12 * f0.Power, "power");
        double i0 = f.Samples[n / 2, n / 2].Magnitude * f.Samples[n / 2, n / 2].Magnitude;
        double k = 2 * Math.PI / Lambda;
        int inside = 0;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                double x = f0.X(c) / a, y = f0.Y(r) / a;   // the same test as PopBeams.TopHat
                if (x * x + y * y <= 1) inside++;
            }
        double aEff = Math.Sqrt(inside * f0.Dx * f0.Dy / Math.PI);
        // Out to beyond the second dark ring (v up to 12).
        for (int c = n / 2 + 1; c < n; c++)
        {
            double x = f.X(c), v = k * aEff * x / z;
            if (v > 12) break;
            double airy = 2 * Bessel.J1(v) / v;
            double i = f.Samples[n / 2, c].Magnitude * f.Samples[n / 2, c].Magnitude / i0;
            Close(airy * airy, i, 1e-2, $"normalised irradiance at v = {v:F3}");
        }
        // The first dark ring: the first zero of J1 (found here by bisection of Bessel.J1 on
        // [3, 4.5], where J1 changes sign once) is where the irradiance is least.
        double lo = 3, hi = 4.5;
        for (int it = 0; it < 80; it++) { double mid = 0.5 * (lo + hi); if (Bessel.J1(lo) * Bessel.J1(mid) <= 0) hi = mid; else lo = mid; }
        double rZero = lo * z / (k * aEff);
        int cMin = Enumerable.Range(n / 2 + 1, n / 2 - 1).Where(c => f.X(c) < 1.5 * rZero)
            .OrderBy(c => f.Samples[n / 2, c].Magnitude).First();
        Assert.True(Math.Abs(f.X(cMin) - rZero) <= f.Dx, $"dark ring at {f.X(cMin):R}, expected {rZero:R} within one sample {f.Dx:R}");
    }

    // ---- Gate: Talbot self-imaging ----

    [Fact]
    public void Talbot_PeriodicField_ReimagesAtTheTalbotDistance_AndShiftsAtHalf()
    {
        // U = 1 + cos(2πx/p) holds the spatial frequencies 0 and ±1/p. The angular-spectrum
        // transfer exp(−iπλzρ²) gives them the phases 0 and −πλz/p²; at z_T = 2p²/λ that is −2π,
        // so the field returns exactly; at z_T/2 it is −π, the cosine changes sign, and
        // 1 − cos(2πx/p) = 1 + cos(2π(x − p/2)/p) is the field shifted by half a period.
        int n = 64;
        double dx = 1e-4, p = n * dx / 8;
        var samples = new Complex[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) samples[r, c] = 1 + Math.Cos(2 * Math.PI * (c - n / 2) * dx / p);
        // A pilot far larger than the field, so the propagation stays inside its Rayleigh range.
        var pilot = PilotBeam.FromWaist(1.0, 0, 1, Lambda);
        var f0 = new PopField(samples, dx, dx, pilot, PopReference.Plane);
        double zT = 2 * p * p / Lambda;
        var log = new List<PopStep>();
        var full = PopPropagator.Propagate(f0, zT, "Talbot", log);
        var half = PopPropagator.Propagate(f0, zT / 2, "half");
        Assert.Equal(new[] { "PTP" }, log.Select(s => s.Operator));
        Close(f0.Power, full.Power, 1e-12 * f0.Power, "power");
        int shift = (int)Math.Round(p / 2 / dx);
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                Assert.True((full.Samples[r, c] - samples[r, c]).Magnitude < 1e-12, $"Talbot image at ({r}, {c})");
                Assert.True((half.Samples[r, c] - samples[r, (c + shift) % n]).Magnitude < 1e-12, $"half-Talbot image at ({r}, {c})");
            }
    }

    // ---- Gate: the Poisson spot ----

    [Fact]
    [Trait("Category", "Slow")]
    public void PoissonSpot_BehindAnOpaqueDisc_IsBrightAsTheFresnelIntegralSays()
    {
        // A Gaussian waist ω on an opaque disc of radius a, observed on the axis a distance z
        // behind it. Fresnel on the axis: E(0, z) = (k/(iz))·∫ E0·e^{−r²/ω²}·e^{ikr²/(2z)} r dr
        // over the open area. With α = 1/ω² − ik/(2z), ∫_a^∞ e^{−αr²} r dr = e^{−αa²}/(2α), so
        // the ratio to the unobstructed beam is e^{−αa²} and the irradiance ratio is
        // |e^{−αa²}|² = exp(−2a²/ω²), whatever z: the bright Poisson spot. The disc is the set of
        // samples within a (the aperture tests sample centres), whose area is N_in·Δx², not πa²;
        // to first order the integral over that set is the closed form with a² replaced by
        // a_eff² = N_in·Δx²/π (the missing or extra samples sit on the edge, where the
        // integrand is e^{−αa²}). The second-order remainder comes from the edge samples' spread
        // of ±Δx/2 about a, over which the integrand's phase turns by k·a·Δx/(2z) = 0.2 rad: a
        // few 1e-3 after averaging round the edge. The test allows 1 %.
        double a = 0.5e-3, w = 2.5e-3, z = 0.25;   // Fresnel number a²/(λz) = 1
        int n = 512;
        double width = n * 3.2e-5;   // N·Δx² = 5.2e-7 m² ≥ 2λz: the transfer function is sampled
        PopRun Run(SurfaceAperture? aperture) => PopEngine.Run(Lens(Surface(z, stop: true, aperture: aperture)), Sys(), new PopRequest
        {
            Beam = new PopBeamDefinition { Type = PopBeamType.GaussianWaist, WaistX = w, WaistY = w },
            SamplingX = n, SamplingY = n, WidthX = width, WidthY = width, KeepSurfaceFields = true,
        });
        var disc = Run(new CircularAperture(0, a) { IsObscuration = true });
        var free = Run(null);
        var fd = disc.Field;
        double ratio = fd.Samples[n / 2, n / 2].Magnitude * fd.Samples[n / 2, n / 2].Magnitude
            / (free.Field.Samples[n / 2, n / 2].Magnitude * free.Field.Samples[n / 2, n / 2].Magnitude);
        int inside = 0;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                double x = fd.X(c), y = fd.Y(r);
                if (x * x + y * y <= a * a) inside++;
            }
        double aEff2 = inside * fd.Dx * fd.Dy / Math.PI;
        Close(Math.Exp(-2 * aEff2 / (w * w)), ratio, 1e-2, "on-axis irradiance ratio behind the disc");
        // Power: the disc removes what fell on it, and the propagation conserves the rest.
        var afterDisc = disc.SurfaceFields.Single(s => s.Surface == 1).Field;
        Close(afterDisc.Power, fd.Power, 1e-12, "power through the propagation");
        Assert.True(afterDisc.Power < free.Field.Power);
    }

    // ---- Beam quality ----

    [Fact]
    public void HermiteGaussian_MSquaredIsTwoLPlusOne_AndWidthGrowsBySqrtOfIt()
    {
        // ψ_l(u) ∝ H_l(u)·e^{−u²/2}, u = √2·x/ω0, is the harmonic oscillator's eigenfunction l,
        // with ⟨u²⟩ = ⟨p_u²⟩ = l + ½ (ladder operators). With x = ω0·u/√2 and
        // θ = (λ/2π)·(−i∂/∂x) = (λ/2π)(√2/ω0)(−i∂/∂u): ⟨x²⟩ = ω0²(2l+1)/4,
        // ⟨θ²⟩ = λ²(2l+1)/(4π²ω0²), so W = 2σ = ω0√(2l+1) and
        // M² = (4π/λ)·√(⟨x²⟩⟨θ²⟩) = 2l + 1.
        int n = 128;
        double width = PopBeams.AutoWidth(W0, n) * 1.5;   // room for the wider mode
        var field = PopBeams.Create(new PopBeamDefinition { Type = PopBeamType.GaussianWaist, WaistX = W0, WaistY = W0, OrderX = 1, OrderY = 2 },
            n, n, width, width, Lambda, 1, LensUnit.Millimeter, new List<string>());
        var d = PopBeamMetrics.Compute(field);
        Close(3, d.MSquaredX, 1e-9, "M² x of l = 1");
        Close(5, d.MSquaredY, 1e-9, "M² y of m = 2");
        Close(W0 * Math.Sqrt(3), d.WidthX, 1e-9 * W0, "width x");
        Close(W0 * Math.Sqrt(5), d.WidthY, 1e-9 * W0, "width y");
        // M² is invariant under free propagation (the moments' determinant is conserved).
        var far = PopPropagator.Propagate(field, 4 * ZR(), "far");
        Close(3, PopBeamMetrics.Compute(far).MSquaredX, 1e-8, "M² x after propagation");
    }

    [Fact]
    public void GaussianSizeAngle_PlacesTheBeamWhereItHasTheGivenSize()
    {
        // Help, "Gaussian Size+Angle": ω0 = λ/(π·tanθ), z = z_R·√((ω/ω0)² − 1); a Gaussian at that
        // z has the size ω0·√(1 + (z/z_R)²) = ω, the given size, and the Gouy phase −atan(z/z_R).
        double angle = 0.02;   // degrees
        double w0 = Lambda / (Math.PI * Math.Tan(angle * Math.PI / 180));
        double size = 3 * w0;
        var f = PopBeams.Create(new PopBeamDefinition { Type = PopBeamType.GaussianSizeAngle, AngleX = angle, AngleY = angle, SizeX = size, SizeY = size },
            128, 128, PopBeams.AutoWidth(w0, 128), PopBeams.AutoWidth(w0, 128), Lambda, 1, LensUnit.Millimeter, new List<string>());
        double z = ZR(w0) * Math.Sqrt(8);
        Assert.Equal(PopReference.Sphere, f.Reference);
        Close(z, f.Pilot.DistanceFromWaist, 1e-12 * z, "pilot position");
        AssertGaussian(f, z, 1, w0);
    }

    // ---- ZBF files ----

    [Fact]
    public void Zbf_BinaryAndText_RoundTripTheField()
    {
        var f = PopPropagator.Propagate(Gaussian(64), 2 * ZR(), "out");   // a sphere-referenced beam
        var zbf = ZbfFile.FromField(f, LensUnit.Millimeter);
        // Layout (help, "Zemax Beam File (ZBF) Binary Format"): 9 ints, 20 doubles, then Ex.
        var bytes = ZbfFile.ToBinary(zbf);
        Assert.Equal(9 * 4 + 20 * 8 + 16 * 64 * 64, bytes.Length);
        Assert.Equal(1, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(64, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(64, BitConverter.ToInt32(bytes, 8));
        Assert.Equal(0, BitConverter.ToInt32(bytes, 16));   // millimeters
        Close(f.Dx * 1e3, BitConverter.ToDouble(bytes, 36), 1e-15 * f.Dx * 1e3, "dx in the header");
        // "Ex*Ex + Ey*Ey is in units of watts": the samples' squares sum to the power.
        double sum = 0;
        foreach (var e in zbf.Ex) sum += e.Real * e.Real + e.Imaginary * e.Imaginary;
        Close(f.Power, sum, 1e-12, "sum of squares");
        foreach (var parsed in new[] { ZbfFile.Parse(bytes, "b.zbf"), ZbfFile.Parse(System.Text.Encoding.ASCII.GetBytes(ZbfFile.ToText(zbf)), "t.zbf") })
        {
            var back = ZbfFile.ToField(parsed, Lambda, 1, new List<string>());
            Assert.Equal(f.Reference, back.Reference);
            Close(f.Pilot.DistanceFromWaist, back.Pilot.DistanceFromWaist, 1e-14 * f.Pilot.DistanceFromWaist, "pilot position");
            Close(f.Pilot.RayleighRange, back.Pilot.RayleighRange, 1e-12 * f.Pilot.RayleighRange, "pilot Rayleigh range");
            Close(f.Dx, back.Dx, 1e-15 * f.Dx, "spacing");
            for (int r = 0; r < 64; r++)
                for (int c = 0; c < 64; c++)
                    Assert.True((back.Samples[r, c] - f.Samples[r, c]).Magnitude <= 1e-14 * (1 + f.Samples[r, c].Magnitude), $"sample ({r}, {c})");
        }
        // Through a file on disk, the extension added as the help says.
        string path = Path.Combine(_dir, "beam");
        ZbfFile.Write(path, zbf);
        var read = ZbfFile.Read(path + ".ZBF");
        Assert.Equal(zbf.Ex[10, 20], read.Ex[10, 20]);
    }

    [Fact]
    public void Zbf_PhaseIsTheHelpsSign_GouyPlusAQuarterPiAtOneRayleighRange()
    {
        // Help, "Sign Conventions for Phase Data": "the phase at the center of the beam will
        // change to π/4 radians" one Rayleigh range past the waist. The field's own (physics)
        // axial phase there is −atan(1) (AssertGaussian), so the file holds its conjugate.
        var f = PopPropagator.Propagate(Gaussian(64), ZR(), "one Rayleigh range");
        var zbf = ZbfFile.FromField(f, LensUnit.Millimeter);
        Close(Math.Atan(1), zbf.Ex[32, 32].Phase, 1e-9, "file phase at the centre");
    }

    [Fact]
    public void Zbf_Refusals_AreNamed()
    {
        var f = Gaussian(64);
        var polarized = ZbfFile.FromField(f, LensUnit.Millimeter) with { IsPolarized = true, Ey = ZbfFile.FromField(f, LensUnit.Millimeter).Ex };
        Assert.Contains("polarized", Assert.Throws<NotSupportedException>(() => ZbfFile.ToField(polarized, Lambda, 1, new List<string>())).Message);
        var bad = ZbfFile.ToBinary(ZbfFile.FromField(f, LensUnit.Millimeter));
        BitConverter.GetBytes(48).CopyTo(bad, 4);   // nx = 48, not a power of 2
        Assert.Contains("powers of 2", Assert.Throws<FormatException>(() => ZbfFile.Parse(bad, "bad.zbf")).Message);
        Assert.Contains("does not end in .ZBF", Assert.Throws<ArgumentException>(() => ZbfFile.Read(Path.Combine(_dir, "x.txt"))).Message);
    }

    // ---- Multimode ----

    [Fact]
    public void Multimode_CoherentAndIncoherentSums_FollowTheHelp()
    {
        // Two equal-weight Gaussians on one centre: coherent with a PHASE of 180° between them
        // cancels everywhere; incoherent gives the irradiance sum with zero imaginary part.
        string text = "! two beams\nGW 1 1 1 0 0 0 0 0 0\nPHASE 180\nGW 1 1 1 0 0 0 0 0 0\n";
        var beam = ZmmFile.Parse(text, "t.zmm");
        double dx = PopBeams.AutoWidth(W0, 64) / 64;
        var coherent = ZmmFile.Sum(beam, 64, 64, dx, dx, Lambda, 0, 0, new List<string>());
        Assert.True(coherent.Cast<Complex>().Max(u => u.Magnitude) < 1e-12 * 1e3, "coherent opposite-phase sum cancels");
        var incoherent = ZmmFile.Sum(ZmmFile.Parse("INCOHERENT\n" + text, "t.zmm"), 64, 64, dx, dx, Lambda, 0, 0, new List<string>());
        double power = incoherent.Cast<Complex>().Sum(u => u.Real * u.Real) * dx * dx;
        Close(2, power, 1e-12, "incoherent power is the sum of the weights");
        Assert.All(incoherent.Cast<Complex>(), u => Assert.Equal(0, u.Imaginary));
        Assert.Contains("DLL", Assert.Throws<NotSupportedException>(() => ZmmFile.Parse("DLL x.dll\n", "d.zmm")).Message);
    }

    // ---- Refusals ----

    [Fact]
    public void Engine_RefusesWhatIsNotBuilt_ByName()
    {
        var lens = Lens(Surface(0.1, stop: true));
        var request = new PopRequest
        {
            Beam = new PopBeamDefinition { Type = PopBeamType.GaussianWaist, WaistX = W0, WaistY = W0 },
            SamplingX = 64, SamplingY = 64,
        };
        Assert.Contains("off-axis", Assert.Throws<NotSupportedException>(() => PopEngine.Run(lens, Sys(1.0, 0, 5), request with { Field = 2 })).Message);
        Assert.Contains("DLL", Assert.Throws<NotSupportedException>(() => PopEngine.Run(lens, Sys(), request with { Beam = request.Beam with { Type = PopBeamType.Dll } })).Message);
        var settings = new PhysicalOpticsSettings();
        Assert.Contains("Use polarization", Assert.Throws<NotSupportedException>(() => (settings with { UsePolarization = true }).ToRequest(LensUnit.Millimeter, 1)).Message);
        Assert.Contains("Separate X, Y", Assert.Throws<NotSupportedException>(() => (settings with { SeparateXY = true }).ToRequest(LensUnit.Millimeter, 1)).Message);
        // The tilt sits on surface 2: the real trace itself refuses a tilt before surface 1 with the
        // object at infinity, before the engine's own check is reached.
        var tilted = Lens(Surface(0.1, stop: true), new SequentialSurface { Thickness = 0.1, TiltBefore = new TiltDecenter(0, 0, 5, 0, 0) });
        Assert.Contains("tilted or decentered", Assert.Throws<NotSupportedException>(() => PopEngine.Run(tilted, Sys(), request)).Message);
    }

    [Fact]
    public void Diagnostics_WarnWhenTheBeamFillsTheArray()
    {
        // A waist of a quarter of the array width: the outer eighth on each side begins 1.5·ω0
        // from the centre, and a Gaussian of 1/e² radius ω0 keeps erfc(1.5·√2) of its power
        // beyond that on each axis (the integral of exp(−2x²/ω0²)), well above the 1e-3 the
        // check allows.
        var notes = new List<string>();
        var f = PopBeams.Create(new PopBeamDefinition { Type = PopBeamType.GaussianWaist, WaistX = W0, WaistY = W0 }, 64, 64, 4 * W0, 4 * W0,
            Lambda, 1, LensUnit.Millimeter, notes);
        var warnings = new List<string>();
        PopDiagnostics.Check(f, "the initial beam", warnings);
        Assert.Contains(warnings, w => w.Contains("guard band"));
    }

    // ---- Operands ----

    private sealed class SavedPop : IMeritConfigurations
    {
        private readonly SequentialLens _lens;
        private readonly OpticalSystemData _system;
        private readonly PhysicalOpticsSettings _settings;
        public SavedPop(SequentialLens lens, OpticalSystemData system, PhysicalOpticsSettings settings) { _lens = lens; _system = system; _settings = settings; }
        public int Count => 1;
        public SequentialLens LensFor(int configuration) => _lens;
        public OpticalSystemData SystemFor(int configuration) => _system;
        public System.Text.Json.JsonElement? AnalysisSettings(string analysisId) =>
            analysisId == PhysicalOpticsPropagationAnalysis.SavedId ? OpticsAnalysisSettingsCodec.Save(_settings) : null;
    }

    [Fact]
    public void Popd_And_Popi_ReadTheSavedPropagation()
    {
        // Free space from a 1 mm waist on surface 1 to the image one Rayleigh range away
        // (z_R = π·1²/0.001 mm = π m with λ = 1 µm): ω = √2 mm, peak 2P/(πω²) per mm², pilot
        // position z_R, waist 1 mm, M² 1, the help's axial phase +π/4 (POPI 7 at the centre
        // pixel p = nx/2 + (ny/2)·nx).
        double zR = ZR();
        var lens = Lens(Surface(zR, stop: true));
        var system = Sys();
        var settings = new PhysicalOpticsSettings { WaistX = 1, WaistY = 1, SamplingX = 64, SamplingY = 64 };
        var rows = new[]
        {
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 3 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 4 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 5 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 7 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 23 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 25 }),
            MeritOperandRow.Of("POPI", weight: 1, data: new double[] { 0, 7, 32 + 32 * 64 }),
            MeritOperandRow.Of("POPI", weight: 1, data: new double[] { 0, 0, 32 + 32 * 64 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 0 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 99 }),
        };
        var e = MeritEvaluator.Evaluate(new MeritFunction(rows), new SavedPop(lens, system, settings));
        double V(int k) => e.Rows[k].Value;
        double w = Math.Sqrt(2);   // mm
        Close(1, V(0), 1e-12, "POPD 3 total power");
        Close(2 / (Math.PI * w * w), V(1), 1e-9, "POPD 4 peak irradiance (W/mm²)");
        Close(zR * 1e3, V(2), 1e-9 * zR * 1e3, "POPD 5 pilot position (mm)");
        Close(1, V(3), 1e-12, "POPD 7 pilot waist (mm)");
        Close(w, V(4), 1e-9 * w, "POPD 23 width x (mm)");
        Close(1, V(5), 1e-9, "POPD 25 M² x");
        Close(Math.Atan(1), V(6), 1e-9, "POPI 7 axial phase, the help's sign");
        Close(V(1), V(7), 1e-9 * V(1), "POPI 0 centre irradiance is the peak");
        // OP-50: POPD 0 is the fiber coupling with the saved fiber data (a 1 mm waist mode at the
        // end surface). The beam there is that waist one Rayleigh range on; the overlap of a waist
        // with the same waist propagated z is 1/(1 + i·z/(2z_R)) (derived in
        // FiberCouplingTests.Pop_WaistAgainstTheSameWaistPropagated), so T = 1/(1 + 1/4) = 0.8 and
        // nothing is lost on the way (S = 1).
        Close(0.8, V(8), 1e-9, "POPD 0 fiber coupling");
        Close(0, V(9), 0, "undefined data returns 0");
    }

    // ---- Analyses ----

    [Fact]
    public void Analysis_RunsSavesTheBeam_AndTheViewerReadsItBack()
    {
        var lens = Lens(Surface(0.5, stop: true, shape: new ParaxialShape(1.0)));
        var optics = new OpticsSettings { SystemData = SystemDataSettings.ToSettings(Sys()) };
        var context = new OpticsAnalysisContext("POP", optics);
        string path = Path.Combine(_dir, "out.ZBF");
        var analysis = new PhysicalOpticsPropagationAnalysis(_ => lens);
        var result = analysis.Compute(context, new PhysicalOpticsSettings
        {
            WaistX = 1, WaistY = 1, SamplingX = 64, SamplingY = 64, SaveOutputBeamTo = path, SaveBeamAtAllSurfaces = true,
        }, CancellationToken.None);
        Assert.Contains("Propagation report", result.Text);
        Assert.Equal(5, result.Plots.Count);   // irradiance and phase maps, x and y sections, encircled power
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_dir, "out_0001.ZBF")));
        Assert.True(File.Exists(Path.Combine(_dir, "out_0002.ZBF")));

        var viewer = new BeamFileViewerAnalysis().Compute(context, new BeamFileViewerSettings { File = path }, CancellationToken.None);
        Assert.Contains("Total power", viewer.Text);
        Assert.Contains("1 W", viewer.Text);
        Assert.True(OpticsAnalysisRegistry.CreateDefault().TryGet(PhysicalOpticsPropagationAnalysis.SavedId, out _));
        Assert.True(OpticsAnalysisRegistry.CreateDefault().TryGet(BeamFileViewerAnalysis.SavedId, out _));
    }
}
