using System.Diagnostics;
using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The spatial-domain layered-media MPIE kernels for one (frequency, stackup) pair,
/// composed as
///
///   G_A(ρ)  = µ₀·[g(ρ) − g(R_2d)]              + Smooth_A(ρ)
///   K_Φ(ρ)  = [c₀·g(ρ) + c₁·g(R_2d)]/ε₀        + Smooth_Φ(ρ)
///
/// with g(R) = e^{−jk₀R}/4πR and R_2d = √(ρ² + 4d²). The closed-form image part
/// carries the ENTIRE 1/ρ singularity (with the exact static coefficients, so the
/// MoM's Wilton–Rao machinery applies to it verbatim) and reduces the whole kernel
/// EXACTLY at εr = 1; Smooth is regular at ρ = 0 and comes from the Sommerfeld
/// remainder integral tabulated on a log-ρ grid:
///  • NEAR region (ρ ≤ 1/k₁): Smooth = pole terms + remainder combined in one spline —
///    non-oscillatory here, and the pole term's ln ρ cancels against the remainder's.
///  • FAR region: the surface-wave pole terms −(j/4)Res·k_p·H₀⁽²⁾(k_pρ) are added in
///    CLOSED form (they decay 1/√ρ and oscillate — exactly what a spline resolves
///    worst) and only the small 1/ρ²-decaying residual wave is splined.
/// Below the first knot (1e-4·λ_d) Smooth is clamped to its first value: it is flat on
/// the scale of d there while the image part diverges as 1/ρ, so the clamp error is
/// buried ~6 orders under the kernel value. Everything is deterministic — fixed grids,
/// fixed panel counts — and a build is immutable once constructed.
/// </summary>
public sealed class LayeredKernelTable
{
    private const int PointsPerDecade = 96;
    private const int MinPointsPerRegion = 48;
    private const int FarKnotsPerWavelength = 64;

    private readonly SurfaceWavePole[] _poles;
    private readonly double _rhoCross;
    private readonly double _rhoMin;
    private readonly double _rhoMax;
    private readonly ComplexSpline _nearA, _nearPhi;
    private readonly ComplexSpline? _farA, _farPhi;

    public SubstrateStackup Substrate { get; }
    public double FrequencyHz { get; }
    public double K0 { get; }

    /// <summary>The K_Φ primary/first-image coefficients (c₀, c₁); G_A's are (1, −1).</summary>
    public (Complex C0, Complex C1) PhiImages { get; }

    /// <summary>Wall-clock cost of the table build — a slow sweep names its own bottleneck.</summary>
    public double BuildMilliseconds { get; }

    /// <summary>Number of surface-wave poles extracted (TM0 is always present).</summary>
    public int PoleCount => _poles.Length;

    public LayeredKernelTable(SubstrateStackup substrate, double frequencyHz, double rhoMax)
    {
        if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        var stopwatch = Stopwatch.StartNew();
        Substrate = substrate;
        FrequencyHz = frequencyHz;
        K0 = 2 * Math.PI * frequencyHz / RfConstants.SpeedOfLight;
        PhiImages = SommerfeldIntegrator.PhiImageCoefficients(
            SpectralKernels.ComplexPermittivity(substrate));
        _poles = SurfaceWavePoles.Find(substrate, K0).ToArray();

        if (rhoMax <= 0) throw new ArgumentOutOfRangeException(nameof(rhoMax));
        double k1 = K0 * Math.Sqrt(substrate.RelativePermittivity);
        double lambdaD = 2 * Math.PI / k1;
        // The clamp-below-ρmin argument needs ρmin ≪ every variation scale of the
        // smooth part — min(λ_d, d) — AND ρmin must sit inside the requested range
        // (in the static limit λ_d dwarfs any physical structure).
        _rhoMin = Math.Min(Math.Min(1e-4 * lambdaD, 0.01 * substrate.ThicknessMeters),
            0.1 * rhoMax);
        _rhoMax = rhoMax;
        // If the whole structure fits inside ~1/k₁ (sub-wavelength / static regimes),
        // nothing oscillates and the near representation covers everything — no far
        // region at all rather than inflating the build radius past the request.
        _rhoCross = rhoMax > 2 / k1 ? 1 / k1 : rhoMax;

        // NEAR: Smooth = poles + remainder, one spline. FAR: remainder only.
        var nearGrid = LogGrid(_rhoMin, Math.Min(_rhoCross * 1.02, _rhoMax));
        var nearA = new Complex[nearGrid.Length];
        var nearPhi = new Complex[nearGrid.Length];
        for (int i = 0; i < nearGrid.Length; i++)
        {
            var (a, phi) = SommerfeldIntegrator.Remainder(substrate, K0, _poles, nearGrid[i]);
            var (poleA, polePhi) = PoleTerms(nearGrid[i]);
            nearA[i] = a + poleA;
            nearPhi[i] = phi + polePhi;
        }
        _nearA = new ComplexSpline(nearGrid, nearA);
        _nearPhi = new ComplexSpline(nearGrid, nearPhi);

        if (_rhoCross < _rhoMax)
        {
            // The far residual has TWO variation scales: log-like curvature carried
            // over from the near field at its left end (measured 3.8e-5 interknot on
            // a plain linear grid there) and the ~k₀ branch-cut oscillation further
            // out (measured 3.6e-7 interknot on a plain log grid there). The hybrid
            // step Δρ = min(ρ·ln10/96, λ₀/64) satisfies both everywhere.
            var farGrid = HybridGrid(_rhoCross * 0.98, _rhoMax,
                2 * Math.PI / K0 / FarKnotsPerWavelength);
            var farA = new Complex[farGrid.Length];
            var farPhi = new Complex[farGrid.Length];
            for (int i = 0; i < farGrid.Length; i++)
                (farA[i], farPhi[i]) = SommerfeldIntegrator.Remainder(substrate, K0, _poles, farGrid[i]);
            _farA = new ComplexSpline(farGrid, farA, logAbscissa: false);
            _farPhi = new ComplexSpline(farGrid, farPhi, logAbscissa: false);
        }
        BuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>The tabulated smooth parts (kernel minus the closed-form images).</summary>
    public (Complex SmoothA, Complex SmoothPhi) EvaluateSmooth(double rho)
    {
        if (rho > _rhoMax)
            throw new ArgumentOutOfRangeException(nameof(rho),
                $"ρ = {rho:g6} exceeds the table's build radius {_rhoMax:g6} — build the table for the structure's true diameter.");
        double clamped = Math.Max(rho, _rhoMin);
        if (clamped <= _rhoCross || _farA is null)
            return (_nearA.Evaluate(clamped), _nearPhi.Evaluate(clamped));
        var (poleA, polePhi) = PoleTerms(clamped);
        return (_farA.Evaluate(clamped) + poleA, _farPhi!.Evaluate(clamped) + polePhi);
    }

    /// <summary>The full physical kernels at lateral distance ρ (diagnostics + gates;
    /// the MoM consumes the images and smooth parts separately).</summary>
    public (Complex GA, Complex KPhi) EvaluateKernels(double rho)
    {
        var (smoothA, smoothPhi) = EvaluateSmooth(rho);
        var (imageA, imagePhi) = ImageTerms(rho);
        return (imageA + smoothA, imagePhi + smoothPhi);
    }

    /// <summary>Same composition but with the remainder integrated DIRECTLY at ρ (no
    /// spline) — the reference the table-accuracy and C1/C2 gates compare against.</summary>
    public (Complex GA, Complex KPhi) EvaluateKernelsDirect(double rho, int refinement = 1)
    {
        var (a, phi) = SommerfeldIntegrator.Remainder(Substrate, K0, _poles, rho, refinement);
        var (poleA, polePhi) = PoleTerms(rho);
        var (imageA, imagePhi) = ImageTerms(rho);
        return (imageA + a + poleA, imagePhi + phi + polePhi);
    }

    /// <summary>The closed-form image parts: µ₀[g(ρ) − g(R_2d)] and [c₀g + c₁g(R_2d)]/ε₀.</summary>
    public (Complex ImageA, Complex ImagePhi) ImageTerms(double rho)
    {
        double d = Substrate.ThicknessMeters;
        var g0 = FreeSpaceG(rho);
        var g1 = FreeSpaceG(Math.Sqrt(rho * rho + 4 * d * d));
        return (RfConstants.Mu0 * (g0 - g1),
                (PhiImages.C0 * g0 + PhiImages.C1 * g1) / RfConstants.Eps0);
    }

    /// <summary>Σ −(j/4)·Res·k_p·H₀⁽²⁾(k_pρ) over the extracted poles, per kernel —
    /// the closed-form surface-wave content (public: the power ledger reports it).</summary>
    public (Complex A, Complex Phi) PoleTerms(double rho)
    {
        Complex a = Complex.Zero, phi = Complex.Zero;
        foreach (var pole in _poles)
        {
            var factor = new Complex(0, -0.25) * pole.KRho * Bessel.H02(pole.KRho * rho);
            if (pole.ResidueA != Complex.Zero) a += pole.ResidueA * factor;
            phi += pole.ResiduePhi * factor;
        }
        return (a, phi);
    }

    internal IReadOnlyList<SurfaceWavePole> Poles => _poles;

    private Complex FreeSpaceG(double r)
    {
        var (sin, cos) = Math.SinCos(K0 * r);
        double scale = 1 / (4 * Math.PI * r);
        return new Complex(scale * cos, -scale * sin);
    }

    private static double[] LogGrid(double from, double to)
    {
        double decades = Math.Log10(to / from);
        int count = Math.Max(MinPointsPerRegion, (int)Math.Ceiling(decades * PointsPerDecade) + 1);
        var grid = new double[count];
        for (int i = 0; i < count; i++)
            grid[i] = from * Math.Pow(to / from, i / (double)(count - 1));
        grid[^1] = to; // exact endpoint despite Pow rounding
        return grid;
    }

    private static double[] HybridGrid(double from, double to, double maxSpacing)
    {
        double logFactor = Math.Log(10) / PointsPerDecade;
        var knots = new List<double> { from };
        double rho = from;
        while (rho < to)
        {
            rho += Math.Min(rho * logFactor, maxSpacing);
            knots.Add(Math.Min(rho, to));
        }
        // Pad tiny grids so the cubic construction stays well-posed.
        while (knots.Count < 4) knots.Add(knots[^1] + maxSpacing);
        knots[^1] = Math.Max(knots[^1], to);
        return knots.ToArray();
    }

    /// <summary>Complex-valued spline: two real natural cubics, over log-ρ (near
    /// region: decades of smooth 1/scale variation) or plain ρ (far region:
    /// k₀-periodic residual, where uniform phase coverage is what matters).</summary>
    private sealed class ComplexSpline
    {
        private readonly NaturalCubicSpline _re, _im;
        private readonly bool _log;

        public ComplexSpline(double[] rho, Complex[] values, bool logAbscissa = true)
        {
            _log = logAbscissa;
            var x = new double[rho.Length];
            var re = new double[rho.Length];
            var im = new double[rho.Length];
            for (int i = 0; i < rho.Length; i++)
            {
                x[i] = logAbscissa ? Math.Log(rho[i]) : rho[i];
                re[i] = values[i].Real;
                im[i] = values[i].Imaginary;
            }
            _re = new NaturalCubicSpline(x, re);
            _im = new NaturalCubicSpline(x, im);
        }

        public Complex Evaluate(double rho)
        {
            double x = _log ? Math.Log(rho) : rho;
            return new Complex(_re.Evaluate(x), _im.Evaluate(x));
        }
    }
}
