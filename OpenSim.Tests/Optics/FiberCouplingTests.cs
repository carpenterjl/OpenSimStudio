using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.FiberCoupling;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.PhysicalOptics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-50: fiber coupling against Gaussian overlap closed forms derived below. Wagner and
/// Tomlinson, "Coupling efficiency of optics in single-mode fiber components", Appl. Opt. 21
/// (1982), was not on hand (no free copy), so every expected value is derived here; the inputs
/// (wavelength, NAs, waists, offsets, angles) are arbitrary test choices, not reference data.
/// <para>
/// <b>The closed forms (derived).</b> Two Gaussian modes in one plane, amplitudes
/// exp(−r²/w1²) and exp(−r²/w2²), have the normalised overlap
/// T = |∫f·g*|²/(∫|f|²·∫|g|²). Per axis ∫exp(−x²(1/w1² + 1/w2²))dx = √π·w1·w2/√(w1² + w2²) and
/// ∫exp(−2x²/w²)dx = √π·w/√2, so per axis T = 2·w1·w2/(w1² + w2²) and over both axes
/// T = (2·w1·w2/(w1² + w2²))² (waist mismatch; 1 when matched). With equal waists w and one
/// shifted by d along x, the exponent is −(x² + (x − d)²)/w² = −2(x − d/2)²/w² − d²/(2w²),
/// so the amplitude overlap carries exp(−d²/(2w²)) and T = exp(−d²/w²) (lateral offset). With
/// equal waists and one tilted by θ about y, the help models the tilt as the linear phase
/// e^{ik·sin θ·x}, k = 2π/λ in a medium of index 1: ∫exp(−2x²/w²)·e^{ik·sin θ·x}dx =
/// √π·w/√2·exp(−k²·sin²θ·w²/8), so T = exp(−k²·sin²θ·w²/4) = exp(−(π·w·sin θ/λ)²) (tilt; the
/// plan's exp(−(πwθ/λ)²) is its small-angle form, and sin θ is what the linear-phase model
/// gives exactly). In the ray-based engine the same forms appear in direction space, the
/// mode being exp(−n²·α²/NA²) with w = λ/(π·NA) (GaussianFiberMode's remarks): an image-space
/// shift d is the phase e^{ik·α·d} there, and a tilt shifts the Gaussian by sin θ.
/// </para>
/// <para>
/// <b>The ray fixture.</b> A concave sphere of radius R with the object point at its centre of
/// curvature: every ray meets the sphere along its normal and returns along itself, so the image
/// is the object point with no aberration at all, the image-space direction cosines are the
/// object-space ones reversed, and the image Jacobian equals the object one. The source fiber's
/// Gaussian therefore arrives unchanged, and the four closed forms hold exactly for the
/// continuous integrals.
/// </para>
/// <para>
/// <b>Expected discretisation error.</b> Ray-based: the overlap is a lattice sum over pupil
/// nodes h = 2/(n − 1) apart of a smooth integrand that is a Gaussian of 1/e width about 0.12
/// in pupil units (NA 0.1 in a pupil of sin θ ≈ 0.4); by Poisson summation the error of such a
/// sum is of order exp(−π²/(a·h²)) for e^{−a·p²}, a ≈ 32, h = 2/63: e^{−300}. The integrand at
/// the pupil rim is down to e^{−2·0.4²/0.1²} = e^{−32} (and less for the shifted cases). The
/// Jacobian's central differences add about 1e-10 relative (FiberPupil). So the method is good
/// to about 1e-9; the tolerance is 1e-7 to leave room for ray-trace rounding. Huygens: the same
/// plane waves summed on a grid in the fiber's plane spaced so that the integrand's spectrum
/// (fiber spectrum truncated at e^{−25}) fits under the grid's Nyquist band, over ±5 waists
/// (e^{−25} edge): the grid sum then adds errors near e^{−25} ≈ 1e-11 relative; tolerance 1e-7
/// as well. POP: OP-49's sampled fields with the Auto width, the Gaussians to e^{−100} at the
/// array edge and in the spectrum (PhysicalOpticsTests' remarks): tolerance 1e-9.
/// </para>
/// </summary>
public sealed class FiberCouplingTests
{
    private const double Lambda = 1e-6;          // 1 µm, a test wavelength
    private const double R = 0.1;                // mirror radius, meters
    private const double EpdMm = 87;             // entrance pupil diameter, mm (sin θ at the rim ≈ 0.399)

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    /// <summary>The object point at the centre of curvature of a concave mirror, image back at it.</summary>
    private static SequentialLens Mirror() => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = R },
            new SequentialSurface
            {
                Curvature = OpticsConventions.CurvatureFromRadius(-R), Thickness = -R, Medium = ParaxialMedium.Constant(1),
                IsStop = true, IsMirror = true,
            },
            new SequentialSurface(),
        },
    };

    private static OpticalSystemData Sys(ApodizationType apodization = ApodizationType.Uniform) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = EpdMm, Apodization = apodization },
        Fields = new FieldSet { Type = FieldType.ObjectHeight, Points = new[] { new FieldPoint() } },
        Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 1.0 } } },
    };

    /// <summary>sin θ of the rim ray: the object point R before the pupil plane, the rim
    /// EPD/2 off the axis in it.</summary>
    private static double RimSine()
    {
        double a = EpdMm / 2 * 1e-3;
        return a / Math.Sqrt(a * a + R * R);
    }

    private static SingleModeCouplingRequest Request(double sourceNa = 0.1, double receiverNa = 0.1, bool huygens = false) => new()
    {
        Sampling = 64,
        Source = new SourceFiber { NaX = sourceNa, NaY = sourceNa },
        ReceiverNaX = receiverNa, ReceiverNaY = receiverNa, UseHuygens = huygens,
    };

    private static SingleModeCouplingResult Run(SingleModeCouplingRequest request) =>
        SingleModeFiberCoupling.Compute(Mirror(), Sys(), request);

    // ---- Gate: the ray-based and Huygens engines ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchedGaussians_CoupleFully(bool huygens)
    {
        var r = Run(Request(huygens: huygens));
        Close(1, r.ReceiverEfficiency, 1e-7, "T, matched");
        // S = 1 − exp(−2·s²/NA²) (the source Gaussian's power inside the rim sine s, derived in
        // TruncatedSource_SystemEfficiency): e^{−32}, far below the tolerance.
        Close(1, r.SystemEfficiency, 1e-7, "S, matched");
        Close(1, r.Coupling, 1e-7, "S·T, matched");
        Close(1, r.MaximumCoupling, 1e-7, "maximum");
        // No aberration and the fiber at the focus: every plane wave in phase, so the amplitude
        // coupling is real.
        Close(1, r.AmplitudeCoupling.Real, 1e-7, "amplitude coupling, real part");
        Close(0, r.AmplitudeCoupling.Imaginary, 1e-7, "amplitude coupling, imaginary part");
    }

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(false, 0.5)]
    [InlineData(true, 1.0)]
    public void LateralOffset_GivesExpMinusDSquaredOverWSquared(bool huygens, double offsetInWaists)
    {
        double w = Lambda / (Math.PI * 0.1);    // the receiver's waist λ/(π·NA)
        double d = offsetInWaists * w;
        var r = Run(Request(huygens: huygens) with { DecenterX = d });
        Close(Math.Exp(-d * d / (w * w)), r.ReceiverEfficiency, 1e-7, $"T at d = {offsetInWaists} w");
        // A decenter is a phase only: the maximum (every phase dropped) stays 1.
        Close(1, r.MaximumCoupling, 1e-7, "maximum with a decenter");
    }

    [Theory]
    [InlineData(false, 0, 5)]
    [InlineData(false, 3, 0)]
    [InlineData(true, 0, 5)]
    public void Tilt_GivesExpMinusPiWSinThetaOverLambdaSquared(bool huygens, double tiltX, double tiltY)
    {
        double w = Lambda / (Math.PI * 0.1);
        double theta = Math.Max(tiltX, tiltY) * Math.PI / 180;
        var r = Run(Request(huygens: huygens) with { TiltX = tiltX, TiltY = tiltY });
        double x = Math.PI * w * Math.Sin(theta) / Lambda;
        Close(Math.Exp(-x * x), r.ReceiverEfficiency, 1e-7, $"T at a tilt of {tiltX}° about x, {tiltY}° about y");
    }

    [Theory]
    [InlineData(false, 0.15)]
    [InlineData(false, 0.07)]
    [InlineData(true, 0.15)]
    public void WaistMismatch_GivesTwoW1W2OverSumOfSquaresSquared(bool huygens, double receiverNa)
    {
        // Waists λ/(π·NA): w1/w2 = NA2/NA1, and 2w1w2/(w1² + w2²) = 2·NA1·NA2/(NA1² + NA2²).
        double w1 = Lambda / (Math.PI * 0.1), w2 = Lambda / (Math.PI * receiverNa);
        double expected = Math.Pow(2 * w1 * w2 / (w1 * w1 + w2 * w2), 2);
        var r = Run(Request(receiverNa: receiverNa, huygens: huygens));
        Close(expected, r.ReceiverEfficiency, 1e-7, $"T, receiver NA {receiverNa}");
        // Only amplitudes differ, so the maximum equals the coupling.
        Close(r.Coupling, r.MaximumCoupling, 1e-7, "maximum with a waist mismatch");
    }

    /// <summary>
    /// The system efficiency of a source fiber that overfills the pupil. The pupil disc maps to
    /// the disc of direction cosines of radius s (the rim sine), so
    /// S = ∫_{ρ<s} exp(−2ρ²/NA²) dα dβ / (π·NA²/2) = 1 − exp(−2s²/NA²) (derived). The rim is
    /// where the integrand is largest relative to the tolerance: with NA = s/1.5 it is e^{−4.5}
    /// there, and the lattice of nodes can misplace at most a band h/√2 (h = 2/(n − 1)) either
    /// side of the rim, about δs = 0.0037 in s at n = 128 (ds/dP ≈ s·cos²θ ≈ 0.335); that band
    /// holds 4·s·2δs·e^{−4.5}/NA² ≈ 1.9e-3 of the source power, the tolerance's basis.
    /// </summary>
    [Fact]
    public void TruncatedSource_SystemEfficiency_IsOneMinusExpMinusTwoSSquaredOverNaSquared()
    {
        double s = RimSine(), na = s / 1.5;
        var r = Run(Request(sourceNa: na) with { Sampling = 128 });
        Close(1 - Math.Exp(-2 * s * s / (na * na)), r.SystemEfficiency, 2e-3, "S of an overfilled pupil");
        Assert.True(r.SystemEfficiency < 1 - 5e-3, "the truncation is seen");
    }

    /// <summary>A defocused fiber, against the overlap computed by an independent 1D radial
    /// quadrature of the same model: the spectra exp(−2ρ²/NA²) (source and receiver) and the
    /// phase e^{−ik·dz·√(1 − ρ²)} of the plane waves at the shifted centre, both round, so
    /// η = ∫₀^s exp(−2ρ²/NA²)·e^{−ik·dz·√(1−ρ²)}·2πρ dρ / (π·NA²/2) and T = |η|² (derived). In the
    /// paraxial limit √(1 − ρ²) ≈ 1 − ρ²/2 this is 1/(1 + (dz/(2z_R))²), z_R = π·w²/λ.</summary>
    [Fact]
    public void Defocus_MatchesTheRadialIntegral()
    {
        double na = 0.1, w = Lambda / (Math.PI * na), zR = Math.PI * w * w / Lambda, dz = 2 * zR;
        var r = Run(Request() with { DecenterZ = dz, Sampling = 96 });
        double k = 2 * Math.PI / Lambda, s = RimSine();
        // Composite Simpson's rule on 20 000 intervals: the integrand is smooth (its phase turns
        // k·dz·ρ²/2 ≈ 2 rad across the Gaussian), error far below 1e-10.
        int m = 20000;
        Complex sum = Complex.Zero;
        for (int i = 0; i <= m; i++)
        {
            double rho = s * i / m;
            double weight = i == 0 || i == m ? 1 : i % 2 == 1 ? 4 : 2;
            sum += weight * Math.Exp(-2 * rho * rho / (na * na)) * Complex.FromPolarCoordinates(1, -k * dz * Math.Sqrt(1 - rho * rho)) * 2 * Math.PI * rho;
        }
        var eta = sum * (s / m) / 3 / (Math.PI * na * na / 2);
        Close(eta.Magnitude * eta.Magnitude, r.ReceiverEfficiency, 1e-7, "T against the radial integral");
        // The paraxial closed form, 1/(1 + 1) = 0.5: the dropped phase k·dz·ρ⁴/8 (k·dz = 4/NA² =
        // 400) is 5e-3 rad at ρ = NA and about 2.5e-3 rad on the Gaussian's average (⟨ρ⁴⟩ = NA⁴/2),
        // which moves T by at most about 2·|η|·2.5e-3 ≈ 3.5e-3.
        Close(0.5, r.ReceiverEfficiency, 5e-3, "T against the paraxial closed form");
    }

    [Fact]
    public void IgnoringTheSource_LightsThePupilByTheApodization_AndLosesNothing()
    {
        var r = Run(Request() with { Source = new SourceFiber { Ignore = true } });
        Close(1, r.SystemEfficiency, 1e-12, "S with the source ignored and nothing vignetted");
        Assert.InRange(r.ReceiverEfficiency, 0.01, 0.99);   // a flat pupil against a Gaussian mode
        Assert.True(r.MaximumCoupling >= r.Coupling - 1e-12);
    }

    [Fact]
    public void FastAndHuygens_Agree_OnADecenteredTiltedMismatchedFiber()
    {
        var request = Request(receiverNa: 0.12) with { DecenterX = 1e-6, DecenterY = -0.5e-6, TiltX = 1, TiltY = -2 };
        var fast = Run(request);
        var huygens = Run(request with { UseHuygens = true });
        Close(fast.ReceiverEfficiency, huygens.ReceiverEfficiency, 1e-7, "T");
        Close(fast.AmplitudeCoupling.Real, huygens.AmplitudeCoupling.Real, 1e-7, "amplitude, real");
        Close(fast.AmplitudeCoupling.Imaginary, huygens.AmplitudeCoupling.Imaginary, 1e-7, "amplitude, imaginary");
        Assert.NotNull(huygens.Image);
    }

    // ---- Gate: POP fiber coupling ----

    /// <summary>A beam that ends where it starts (the end surface is surface 1, nothing between),
    /// a 1 mm waist at λ = 1 µm, and the fiber mode set on the Fiber Data settings.</summary>
    private static PopFiberCouplingResult Pop(PhysicalOpticsSettings settings)
    {
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Thickness = 0.01, IsStop = true, Medium = ParaxialMedium.Constant(1) },
                new SequentialSurface(),
            },
        };
        var system = new OpticalSystemData
        {
            Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 10 },
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 1.0 } } },
        };
        var s = settings with { EndSurface = 1, WaistX = 1, WaistY = 1, SamplingX = 128, SamplingY = 128, ComputeFiberCoupling = true };
        var run = PopRunner.Run(lens, system, s);
        return PopFiberCoupling.Compute(run, s.ToFiber(LensUnit.Millimeter), LensUnit.Millimeter);
    }

    [Fact]
    public void Pop_MatchedOffsetMismatchedAndTilted_FollowTheClosedForms()
    {
        var matched = Pop(new PhysicalOpticsSettings { FiberWaistX = 1, FiberWaistY = 1 });
        Close(1, matched.ReceiverEfficiency, 1e-9, "POP T, matched");
        Close(1, matched.SystemEfficiency, 1e-12, "POP S, nothing lost");
        Close(1, matched.Coupling, 1e-9, "POP total, matched");

        var offset = Pop(new PhysicalOpticsSettings { FiberWaistX = 1, FiberWaistY = 1, FiberDecenterX = 0.5 });
        Close(Math.Exp(-0.25), offset.ReceiverEfficiency, 1e-9, "POP T, offset half a waist");

        var mismatch = Pop(new PhysicalOpticsSettings { FiberWaistX = 1.5, FiberWaistY = 1.5 });
        Close(Math.Pow(2 * 1.5 / (1 + 1.5 * 1.5), 2), mismatch.ReceiverEfficiency, 1e-9, "POP T, waists 1 and 1.5 mm");

        // Tilt 0.015° about y: π·w·sin θ/λ ≈ 0.82 with w = 1 mm, λ = 1 µm; the linear phase steps
        // k·sin θ·Δx ≈ 0.26 rad between samples, well resolved.
        double theta = 0.015 * Math.PI / 180;
        var tilt = Pop(new PhysicalOpticsSettings { FiberWaistX = 1, FiberWaistY = 1, FiberTiltY = 0.015 });
        double x = Math.PI * 1e-3 * Math.Sin(theta) / Lambda;
        Close(Math.Exp(-x * x), tilt.ReceiverEfficiency, 1e-9, "POP T, tilted 0.015°");
        var tiltX = Pop(new PhysicalOpticsSettings { FiberWaistX = 1, FiberWaistY = 1, FiberTiltX = 0.015 });
        Close(Math.Exp(-x * x), tiltX.ReceiverEfficiency, 1e-9, "POP T, tilted 0.015° about x");
    }

    /// <summary>
    /// A waist against the same waist propagated a distance z (the beam's curvature put back by
    /// TrueField). Derivation, physics convention u = (q0/q)·exp(ik·r²/(2q)), q = z − i·z_R,
    /// q0 = −i·z_R (PhysicalOpticsTests.AssertGaussian): ∫u0*·u = (q0/q)·π/a with
    /// a = −(ik/2)(1/q + i/z_R), and a·q = z/w² − 2πi/λ (k/2 = π/λ = z_R/w²), so
    /// ∫u0*·u = (π·w²/2)/(1 + i·z/(2z_R)) and, both of power π·w²/2,
    /// T = 1/(1 + (z/(2z_R))²): 0.8 at z = z_R.
    /// </summary>
    [Fact]
    public void Pop_WaistAgainstTheSameWaistPropagated()
    {
        double zR = Math.PI * 1e-3 * 1e-3 / Lambda;
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Thickness = zR, IsStop = true, Medium = ParaxialMedium.Constant(1) },
                new SequentialSurface(),
            },
        };
        var system = new OpticalSystemData
        {
            Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 10 },
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 1.0 } } },
        };
        var settings = new PhysicalOpticsSettings { WaistX = 1, WaistY = 1, SamplingX = 64, SamplingY = 64, ComputeFiberCoupling = true };
        var run = PopRunner.Run(lens, system, settings);
        var r = PopFiberCoupling.Compute(run, settings.ToFiber(LensUnit.Millimeter), LensUnit.Millimeter);
        Close(0.8, r.ReceiverEfficiency, 1e-9, "T one Rayleigh range from the waist");
    }

    // ---- Multimode ----

    /// <summary>
    /// The geometric estimate on the mirror: a uniform pupil, every ray landing on the image point
    /// (in any core), accepted when sin θ ≤ NA. The ray of pupil radius ρ leaves at
    /// sin θ = ρ·a/√((ρ·a)² + R²) (a the pupil radius), so it is accepted for ρ ≤ ρc = R·tan θ_NA/a
    /// and the efficiency is the fraction of pupil nodes inside ρc. Bounds (derived): squares of
    /// side h about the nodes within radius r cover the disc of radius r − h/√2 and lie inside
    /// the one of radius r + h/√2, so π(r − h/√2)² ≤ N(r)·h² ≤ π(r + h/√2)²; the fraction
    /// N(ρc)/N(1) lies between (ρc − h/√2)²/(1 + h/√2)² and (ρc + h/√2)²/(1 − h/√2)².
    /// </summary>
    [Fact]
    public void Multimode_UniformPupil_AcceptsTheRaysInsideTheNa()
    {
        double na = 0.2;
        var r = MultimodeFiberCoupling.Compute(Mirror(), Sys(), new MultimodeCouplingRequest { FiberNa = na, CoreRadius = 25e-6, Sampling = 128 });
        double a = EpdMm / 2 * 1e-3, rc = R * (na / Math.Sqrt(1 - na * na)) / a, h = 2.0 / 127, e = h / Math.Sqrt(2);
        Assert.InRange(r.Efficiency, (rc - e) * (rc - e) / ((1 + e) * (1 + e)), (rc + e) * (rc + e) / ((1 - e) * (1 - e)));
        Close(1, r.SystemEfficiency, 1e-12, "everything reaches the image");

        // The NA from the indices: √(1.5² − 1.4²).
        var byIndex = MultimodeFiberCoupling.Compute(Mirror(), Sys(), new MultimodeCouplingRequest { FiberNa = 0, CoreIndex = 1.5, CladdingIndex = 1.4, CoreRadius = 25e-6, Sampling = 32 });
        Close(Math.Sqrt(1.5 * 1.5 - 1.4 * 1.4), byIndex.NumericalAperture, 1e-15, "NA from the indices");

        // A core moved off the image point by twice its radius accepts nothing.
        var off = MultimodeFiberCoupling.Compute(Mirror(), Sys(), new MultimodeCouplingRequest { FiberNa = na, CoreRadius = 25e-6, DecenterX = 50e-6, Sampling = 32 });
        Assert.Equal(0, off.Efficiency);
        // An NA above every ray's accepts everything.
        var all = MultimodeFiberCoupling.Compute(Mirror(), Sys(), new MultimodeCouplingRequest { FiberNa = 0.5, CoreRadius = 25e-6, Sampling = 32 });
        Close(1, all.Efficiency, 1e-12, "a wide NA");
    }

    // ---- Operands ----

    private sealed class Saved : IMeritConfigurations
    {
        private readonly SequentialLens _lens;
        private readonly OpticalSystemData _system;
        private readonly SingleModeFiberCouplingSettings? _single;
        private readonly PhysicalOpticsSettings? _pop;
        public Saved(SequentialLens lens, OpticalSystemData system, SingleModeFiberCouplingSettings? single = null, PhysicalOpticsSettings? pop = null)
        { _lens = lens; _system = system; _single = single; _pop = pop; }
        public int Count => 1;
        public SequentialLens LensFor(int configuration) => _lens;
        public OpticalSystemData SystemFor(int configuration) => _system;
        public System.Text.Json.JsonElement? AnalysisSettings(string analysisId) =>
            analysisId == SingleModeFiberCouplingAnalysis.SavedId && _single is not null ? OpticsAnalysisSettingsCodec.Save(_single)
            : analysisId == PhysicalOpticsPropagationAnalysis.SavedId && _pop is not null ? OpticsAnalysisSettingsCodec.Save(_pop)
            : null;
    }

    [Fact]
    public void Ficl_ReturnsTheSingleModeCoupling_FastHuygensAndSaved()
    {
        // Columns: Int1 Samp, Int2 Wave, Data1 Field, Data2 IgSrc, Data3 Sna, Data4 Rna, Data5 Data, Data6 Pol?.
        double w = Lambda / (Math.PI * 0.1);
        var saved = new SingleModeFiberCouplingSettings { DecenterX = w * 1e3, Sampling = 64 };   // lens units mm
        var rows = new[]
        {
            MeritOperandRow.Of("FICL", int1: 2, int2: 1, weight: 1, data: new double[] { 1, 0, 0.1, 0.1, 0 }),
            MeritOperandRow.Of("FICL", int1: 2, int2: 1, weight: 1, data: new double[] { 1, 0, 0.1, 0.1, 1 }),
            MeritOperandRow.Of("FICL", int1: 2, int2: 1, weight: 1, data: new double[] { 1, 0, 0.1, 0.1, 2 }),
            MeritOperandRow.Of("FICL", int1: 2, int2: 1, weight: 1, data: new double[] { 1, 0, 0.1, 0.1, 4 }),
            MeritOperandRow.Of("FICL", int1: 0, int2: 0, weight: 1, data: new double[] { 0, 0, 0, 0, 6 }),
            MeritOperandRow.Of("FICL", int1: 2, int2: 1, weight: 1, data: new double[] { 1, 0, 0.1, 0.1, 0, 1 }),
        };
        var e = MeritEvaluator.Evaluate(new MeritFunction(rows), new Saved(Mirror(), Sys(), single: saved));
        Close(1, e.Rows[0].Value, 1e-7, "FICL 0, fast");
        Close(1, e.Rows[1].Value, 1e-7, "FICL 1, Huygens");
        Close(1, e.Rows[2].Value, 1e-7, "FICL 2, real amplitude");
        Close(0, e.Rows[3].Value, 1e-7, "FICL 4, imaginary amplitude");
        Close(Math.Exp(-1), e.Rows[4].Value, 1e-7, "FICL 6, the saved decenter of one waist");
        Assert.Contains("Pol?", e.Rows[5].Error);
    }

    [Fact]
    public void Ficp_And_Popd_ReturnThePopFiberCoupling()
    {
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Thickness = 0.01, IsStop = true, Medium = ParaxialMedium.Constant(1) },
                new SequentialSurface(),
            },
        };
        var system = new OpticalSystemData
        {
            Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 10 },
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 1.0 } } },
        };
        var pop = new PhysicalOpticsSettings { EndSurface = 1, WaistX = 1, WaistY = 1, SamplingX = 64, SamplingY = 64, FiberWaistX = 1, FiberWaistY = 1, FiberDecenterX = 1 };
        var rows = new[]
        {
            MeritOperandRow.Of("FICP", weight: 1, data: new double[] { 0, 0 }),
            MeritOperandRow.Of("FICP", weight: 1, data: new double[] { 0, 1 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 0 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 1 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 2 }),
            MeritOperandRow.Of("POPD", weight: 1, data: new double[] { 0, 60 }),
        };
        var e = MeritEvaluator.Evaluate(new MeritFunction(rows), new Saved(lens, system, pop: pop));
        Close(Math.Exp(-1), e.Rows[0].Value, 1e-9, "FICP 0, offset one waist");
        Close(0, e.Rows[1].Value, 0, "FICP 1 is 0 for an unpolarized beam");
        Close(Math.Exp(-1), e.Rows[2].Value, 1e-9, "POPD 0");
        Close(1, e.Rows[3].Value, 1e-12, "POPD 1, system efficiency");
        Close(Math.Exp(-1), e.Rows[4].Value, 1e-9, "POPD 2, receiver efficiency");
        Close(0, e.Rows[5].Value, 0, "POPD 60 is 0 for an unpolarized beam");
    }

    // ---- Analyses and refusals ----

    [Fact]
    public void Analyses_AreRegistered_AndRun()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(SingleModeFiberCouplingAnalysis.SavedId, out _));
        Assert.True(registry.TryGet(MultimodeFiberCouplingAnalysis.SavedId, out _));
        var context = new OpticsAnalysisContext("Fiber", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(Sys()) });
        var single = new SingleModeFiberCouplingAnalysis(_ => Mirror()).Compute(context, new SingleModeFiberCouplingSettings { Sampling = 32, UseHuygens = true }, CancellationToken.None);
        Assert.Contains("Receiver efficiency", single.Text);
        Assert.Equal(3, single.Plots.Count);   // beam and fiber in the pupil, the beam at the fiber
        var multi = new MultimodeFiberCouplingAnalysis(_ => Mirror()).Compute(context, new MultimodeFiberCouplingSettings { Sampling = 32 }, CancellationToken.None);
        Assert.Contains("Coupling efficiency", multi.Text);
        Assert.Single(multi.Plots);

        // POP shows the fiber coupling when asked.
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Thickness = 0.01, IsStop = true, Medium = ParaxialMedium.Constant(1) },
                new SequentialSurface(),
            },
        };
        var popContext = new OpticsAnalysisContext("POP", new OpticsSettings
        {
            SystemData = SystemDataSettings.ToSettings(new OpticalSystemData
            {
                Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 10 },
                Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 1.0 } } },
            }),
        });
        var popResult = new PhysicalOpticsPropagationAnalysis(_ => lens).Compute(popContext,
            new PhysicalOpticsSettings { WaistX = 1, WaistY = 1, SamplingX = 64, SamplingY = 64, ComputeFiberCoupling = true }, CancellationToken.None);
        Assert.Contains("Receiver efficiency", popResult.Text);
    }

    [Fact]
    public void WhatIsNotBuilt_IsRefusedByName()
    {
        Assert.Contains("Use polarization", Assert.Throws<NotSupportedException>(() => Run(Request() with { UsePolarization = true })).Message);
        var atInfinity = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Curvature = OpticsConventions.CurvatureFromRadius(-0.2), Thickness = -0.1, IsStop = true, IsMirror = true, Conic = -1 },
                new SequentialSurface(),
            },
        };
        var system = Sys() with { Fields = new FieldSet { Points = new[] { new FieldPoint() } } };
        Assert.Contains("Ignore source fiber", Assert.Throws<InvalidOperationException>(() => SingleModeFiberCoupling.Compute(atInfinity, system, Request())).Message);
        // With the source ignored the same paraboloid couples (it focuses a plane wave perfectly).
        var ok = SingleModeFiberCoupling.Compute(atInfinity, system, Request() with { Source = new SourceFiber { Ignore = true } });
        Assert.InRange(ok.ReceiverEfficiency, 0.01, 1);
        Assert.Contains("multimode", Assert.Throws<NotSupportedException>(() => Pop(new PhysicalOpticsSettings { FiberType = PopBeamType.Multimode })).Message);
    }
}
