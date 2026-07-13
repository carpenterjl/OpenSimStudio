using System.Diagnostics;
using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The Stage F multi-layer counterpart of <see cref="LayeredKernelTable"/>: the spatial
/// G_A and K_Φ kernels of an N-layer grounded stackup for one (frequency, stackup) pair,
/// composed exactly as the single-slab table but with the generalized pieces —
///
///   G_A(ρ) = µ₀·Σ_m a_m·g(R_m)     + Smooth_A(ρ)      (a: primary + PEC ground image)
///   K_Φ(ρ) = (1/ε₀)·Σ_m c_m·g(R_m) + Smooth_Φ(ρ)      (c: the quasi-static image SERIES)
///
/// g(R) = e^{−jk₀R}/4πR, R_m = √(ρ² + D_m²), the images from <see cref="MultiLayerImages"/>
/// and the Smooth part from the multi-layer Sommerfeld remainder
/// (<see cref="SommerfeldIntegrator.RemainderMultiLayer"/>) tabulated on the SAME near-log /
/// far-hybrid grids (this reuses the single-slab table's grid + spline helpers). The primary
/// image (Depth 0, c₀) carries the entire 1/ρ singularity, so the MoM's Wilton–Rao machinery
/// applies to it verbatim; the deeper images are non-singular free-space pair moments against
/// the source triangle shifted down by each D_m (the multi-layer generalization of the
/// single-slab 2d image). The single-slab path (<see cref="LayeredKernelTable"/>) is untouched
/// — this is an additive sibling, pinned to it at N = 1 by the extraction gates.
///
/// Only the two potential kernels are carried (no K_V / vertical kernels — those are
/// single-slab features whose multi-layer versions are named follow-ups).
/// </summary>
public sealed class MultiLayerKernelTable
{
    private readonly SurfaceWavePole[] _poles;
    private readonly double _rhoCross;
    private readonly double _rhoMin;
    private readonly double _rhoMax;
    private readonly LayeredKernelTable.ComplexSpline _nearA, _nearPhi;
    private readonly LayeredKernelTable.ComplexSpline? _farA, _farPhi;

    public LayeredStackup Stackup { get; }
    public double FrequencyHz { get; }
    public double K0 { get; }

    /// <summary>The K_Φ quasi-static image series (primary first) — the MoM subtracts the
    /// primary singularity via Wilton–Rao and integrates the rest as shifted image moments.
    /// Internal: the image type is an OpenSim.Rf implementation detail (the app constructs
    /// the table and reads only <see cref="EvaluateKernels"/> / the smooth split).</summary>
    internal IReadOnlyList<MultiLayerImages.Image> PhiImages { get; }

    /// <summary>G_A's two images: primary + PEC ground image at 2·d_total (ε-independent).</summary>
    internal IReadOnlyList<MultiLayerImages.Image> GaImages { get; }

    public double BuildMilliseconds { get; }
    public int PoleCount => _poles.Length;

    /// <param name="maxDegreeOfParallelism">Thread count for the independent knot integrals
    /// (null = unbounded); the table is bitwise identical for any value (slot-array compute,
    /// sequential spline construction), like the single-slab build.</param>
    public MultiLayerKernelTable(LayeredStackup stackup, double frequencyHz, double rhoMax,
        int? maxDegreeOfParallelism = null)
    {
        if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (rhoMax <= 0) throw new ArgumentOutOfRangeException(nameof(rhoMax));
        var stopwatch = Stopwatch.StartNew();
        Stackup = stackup;
        FrequencyHz = frequencyHz;
        K0 = 2 * Math.PI * frequencyHz / RfConstants.SpeedOfLight;
        PhiImages = MultiLayerImages.PhiImages(stackup);
        GaImages = MultiLayerImages.GaImages(stackup);
        _poles = SurfaceWavePoles.Find(stackup, K0).ToArray();

        double epsMax = stackup.Layers.Max(l => l.RelativePermittivity);
        double k1 = K0 * Math.Sqrt(epsMax);
        double lambdaD = 2 * Math.PI / k1;
        double d = stackup.TotalThicknessMeters;
        _rhoMin = Math.Min(Math.Min(1e-4 * lambdaD, 0.01 * d), 0.1 * rhoMax);
        _rhoMax = rhoMax;
        _rhoCross = rhoMax > 2 / k1 ? 1 / k1 : rhoMax;

        var nearGrid = LayeredKernelTable.LogGrid(_rhoMin, Math.Min(_rhoCross * 1.02, _rhoMax));
        var nearA = new Complex[nearGrid.Length];
        var nearPhi = new Complex[nearGrid.Length];
        LayeredKernelTable.ForKnots(nearGrid.Length, maxDegreeOfParallelism, i =>
        {
            var (a, phi) = SommerfeldIntegrator.RemainderMultiLayer(
                stackup, K0, _poles, GaImages, PhiImages, nearGrid[i]);
            var (poleA, polePhi) = PoleTerms(nearGrid[i]);
            nearA[i] = a + poleA;
            nearPhi[i] = phi + polePhi;
        });
        _nearA = new LayeredKernelTable.ComplexSpline(nearGrid, nearA);
        _nearPhi = new LayeredKernelTable.ComplexSpline(nearGrid, nearPhi);

        if (_rhoCross < _rhoMax)
        {
            var farGrid = LayeredKernelTable.HybridGrid(_rhoCross * 0.98, _rhoMax,
                2 * Math.PI / K0 / 64);
            var farA = new Complex[farGrid.Length];
            var farPhi = new Complex[farGrid.Length];
            LayeredKernelTable.ForKnots(farGrid.Length, maxDegreeOfParallelism, i =>
                (farA[i], farPhi[i]) = SommerfeldIntegrator.RemainderMultiLayer(
                    stackup, K0, _poles, GaImages, PhiImages, farGrid[i]));
            _farA = new LayeredKernelTable.ComplexSpline(farGrid, farA, logAbscissa: false);
            _farPhi = new LayeredKernelTable.ComplexSpline(farGrid, farPhi, logAbscissa: false);
        }
        BuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>The tabulated smooth parts (kernel minus the closed-form images).</summary>
    public (Complex SmoothA, Complex SmoothPhi) EvaluateSmooth(double rho)
    {
        if (rho > _rhoMax)
            throw new ArgumentOutOfRangeException(nameof(rho),
                $"ρ = {rho:g6} exceeds the table's build radius {_rhoMax:g6}.");
        double clamped = Math.Max(rho, _rhoMin);
        if (clamped <= _rhoCross || _farA is null)
            return (_nearA.Evaluate(clamped), _nearPhi.Evaluate(clamped));
        var (poleA, polePhi) = PoleTerms(clamped);
        return (_farA.Evaluate(clamped) + poleA, _farPhi!.Evaluate(clamped) + polePhi);
    }

    /// <summary>The full physical kernels at ρ (diagnostics + gates; the MoM consumes the
    /// images and smooth parts separately).</summary>
    public (Complex GA, Complex KPhi) EvaluateKernels(double rho)
    {
        var (smoothA, smoothPhi) = EvaluateSmooth(rho);
        var (imageA, imagePhi) = ImageTerms(rho);
        return (imageA + smoothA, imagePhi + smoothPhi);
    }

    /// <summary>Same composition but with the remainder integrated DIRECTLY at ρ (no spline)
    /// — the reference the table-accuracy gate compares against.</summary>
    public (Complex GA, Complex KPhi) EvaluateKernelsDirect(double rho, int refinement = 1)
    {
        var (a, phi) = SommerfeldIntegrator.RemainderMultiLayer(
            Stackup, K0, _poles, GaImages, PhiImages, rho, refinement);
        var (poleA, polePhi) = PoleTerms(rho);
        var (imageA, imagePhi) = ImageTerms(rho);
        return (imageA + a + poleA, imagePhi + phi + polePhi);
    }

    /// <summary>The closed-form image parts: µ₀·Σ a_m g(R_m) and (1/ε₀)·Σ c_m g(R_m).</summary>
    public (Complex ImageA, Complex ImagePhi) ImageTerms(double rho)
    {
        Complex imageA = Complex.Zero, imagePhi = Complex.Zero;
        foreach (var img in GaImages)
            imageA += img.Coeff * FreeSpaceG(Math.Sqrt(rho * rho + img.Depth * img.Depth));
        foreach (var img in PhiImages)
            imagePhi += img.Coeff * FreeSpaceG(Math.Sqrt(rho * rho + img.Depth * img.Depth));
        return (RfConstants.Mu0 * imageA, imagePhi / RfConstants.Eps0);
    }

    /// <summary>Σ −(j/4)·Res·k_p·H₀⁽²⁾(k_pρ) over the extracted poles, per kernel.</summary>
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
}
