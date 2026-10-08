using System.Diagnostics;
using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// FU-34 — the horizontal-to-horizontal potential kernels between metal on two DIFFERENT
/// interfaces of one grounded stackup: a horizontal current on interface <see cref="SourceInterface"/>
/// seen on interface <see cref="ObservationInterface"/>. These are the off-diagonal blocks of a
/// two-level MoM; the diagonal ones are each level's own <see cref="MultiLayerKernelTable"/>.
///
///   G_A(ρ) = µ₀·[g(R₀) − g(R₁)] + Smooth_A(ρ),   K_Φ(ρ) = (c₀/ε₀)·[g(R₀) − g(R₁)] + Smooth_Φ(ρ)
///
/// with R₀ = √(ρ² + (z − z′)²) the direct distance between the two planes and R₁ = √(ρ² + (z + z′)²)
/// the PEC ground's image — the two images <see cref="MultiLayerFieldKernels.FieldImages"/> subtracts
/// (c₀ the Coulomb coefficient of the observation side). Neither is singular: the planes are a
/// height apart. The smooth part is the per-height field remainder
/// (<see cref="SommerfeldIntegrator.FieldRemainderMultiLayer"/>) plus the surface-wave pole terms
/// with the per-height residues, tabulated on the same near-log / far-hybrid grids as the
/// single-level table.
///
/// <para>Formulation C needs nothing more for horizontal currents tested on a horizontal plane:
/// the tangential field there is −jωG_A^xx·J − ∇Φ, and the scalar kernel read out at the
/// observation interface, (A_x + ∂_zã_z)/(µ₀ε₀ε), is single-valued across it (the TM contrast
/// source at an interface is exactly the jump of 1/ε). The kernels are reciprocal — the same
/// table serves both off-diagonal blocks.</para>
/// </summary>
public sealed class CrossLevelKernelTable
{
    private readonly SurfaceWavePole[] _poles;
    private readonly (Complex A, Complex Phi)[] _residues;
    private readonly double _rhoCross, _rhoMin, _rhoMax;
    private readonly LayeredKernelTable.ComplexSpline _nearA, _nearPhi;
    private readonly LayeredKernelTable.ComplexSpline? _farA, _farPhi;

    public LayeredStackup Stackup { get; }
    public double FrequencyHz { get; }
    public double K0 { get; }
    public int SourceInterface { get; }
    public int ObservationInterface { get; }
    public double SourceHeight { get; }
    public double ObservationHeight { get; }
    public double BuildMilliseconds { get; }

    /// <summary>The two quasi-static images, (depth, coefficient) with the depth the vertical
    /// distance from the observation plane: the direct one at |z − z′| and the ground's at z + z′.
    /// G_A's coefficients are (1, −1); K_Φ's (c₀, −c₀).</summary>
    internal IReadOnlyList<MultiLayerImages.Image> GaImages { get; }
    internal IReadOnlyList<MultiLayerImages.Image> PhiImages { get; }

    public CrossLevelKernelTable(LayeredStackup stackup, double frequencyHz, double rhoMax,
        int sourceInterface, int observationInterface, int? maxDegreeOfParallelism = null)
    {
        int n = stackup.Layers.Count;
        if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (rhoMax <= 0) throw new ArgumentOutOfRangeException(nameof(rhoMax));
        if (sourceInterface < 0 || sourceInterface >= n) throw new ArgumentOutOfRangeException(nameof(sourceInterface));
        if (observationInterface < 0 || observationInterface >= n) throw new ArgumentOutOfRangeException(nameof(observationInterface));
        if (sourceInterface == observationInterface)
            throw new ArgumentException("The two levels must differ; one level's own kernels are a MultiLayerKernelTable.");
        var stopwatch = Stopwatch.StartNew();
        Stackup = stackup;
        FrequencyHz = frequencyHz;
        K0 = 2 * Math.PI * frequencyHz / RfConstants.SpeedOfLight;
        SourceInterface = sourceInterface;
        ObservationInterface = observationInterface;
        var heights = stackup.InterfaceHeights();
        SourceHeight = heights[sourceInterface];
        ObservationHeight = heights[observationInterface];
        var (ga, phi, _) = MultiLayerFieldKernels.FieldImages(stackup, sourceInterface, ObservationHeight);
        GaImages = ga;
        PhiImages = phi;
        _poles = SurfaceWavePoles.Find(stackup, K0).ToArray();
        _residues = _poles.Select(p =>
        {
            var r = MultiLayerFieldKernels.PoleResidues(stackup, K0, p.KRho, p.IsTm, sourceInterface, ObservationHeight);
            return (r.A, r.Phi);
        }).ToArray();

        double epsMax = stackup.Layers.Max(l => l.RelativePermittivity);
        double k1 = K0 * Math.Sqrt(epsMax);
        double d = stackup.TotalThicknessMeters;
        _rhoMin = Math.Min(Math.Min(1e-4 * 2 * Math.PI / k1, 0.01 * d), 0.1 * rhoMax);
        _rhoMax = rhoMax;
        _rhoCross = rhoMax > 2 / k1 ? 1 / k1 : rhoMax;

        var nearGrid = LayeredKernelTable.LogGrid(_rhoMin, Math.Min(_rhoCross * 1.02, _rhoMax));
        var nearA = new Complex[nearGrid.Length];
        var nearPhi = new Complex[nearGrid.Length];
        LayeredKernelTable.ForKnots(nearGrid.Length, maxDegreeOfParallelism, i =>
        {
            var (a, p) = Remainder(nearGrid[i]);
            var (poleA, polePhi) = PoleTerms(nearGrid[i]);
            nearA[i] = a + poleA;
            nearPhi[i] = p + polePhi;
        });
        _nearA = new LayeredKernelTable.ComplexSpline(nearGrid, nearA);
        _nearPhi = new LayeredKernelTable.ComplexSpline(nearGrid, nearPhi);
        if (_rhoCross < _rhoMax)
        {
            var farGrid = LayeredKernelTable.HybridGrid(_rhoCross * 0.98, _rhoMax, 2 * Math.PI / K0 / 64);
            var farA = new Complex[farGrid.Length];
            var farPhi = new Complex[farGrid.Length];
            LayeredKernelTable.ForKnots(farGrid.Length, maxDegreeOfParallelism, i =>
                (farA[i], farPhi[i]) = Remainder(farGrid[i]));
            _farA = new LayeredKernelTable.ComplexSpline(farGrid, farA, logAbscissa: false);
            _farPhi = new LayeredKernelTable.ComplexSpline(farGrid, farPhi, logAbscissa: false);
        }
        BuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    }

    private (Complex A, Complex Phi) Remainder(double rho, int refinement = 1)
    {
        var r = SommerfeldIntegrator.FieldRemainderMultiLayer(Stackup, K0, _poles, SourceInterface, rho,
            ObservationHeight, refinement);
        return (r.A, r.Phi);
    }

    /// <summary>Σ −(j/4)·Res·k_p·H₀⁽²⁾(k_pρ) over the surface-wave poles, with the residues of
    /// the source-to-observation-height kernels.</summary>
    public (Complex A, Complex Phi) PoleTerms(double rho)
    {
        Complex a = Complex.Zero, phi = Complex.Zero;
        for (int i = 0; i < _poles.Length; i++)
        {
            var kp = _poles[i].KRho;
            var factor = new Complex(0, -0.25) * kp * Bessel.H02(kp * rho);
            a += _residues[i].A * factor;
            phi += _residues[i].Phi * factor;
        }
        return (a, phi);
    }

    /// <summary>The tabulated smooth parts (kernel minus the two images).</summary>
    public (Complex SmoothA, Complex SmoothPhi) EvaluateSmooth(double rho)
    {
        if (rho > _rhoMax)
            throw new ArgumentOutOfRangeException(nameof(rho), $"ρ = {rho:g6} exceeds the table's build radius {_rhoMax:g6}.");
        double clamped = Math.Max(rho, _rhoMin);
        if (clamped <= _rhoCross || _farA is null)
            return (_nearA.Evaluate(clamped), _nearPhi.Evaluate(clamped));
        var (poleA, polePhi) = PoleTerms(clamped);
        return (_farA.Evaluate(clamped) + poleA, _farPhi!.Evaluate(clamped) + polePhi);
    }

    /// <summary>The closed-form image parts µ₀·Σ c g(R) and (1/ε₀)·Σ c g(R).</summary>
    public (Complex ImageA, Complex ImagePhi) ImageTerms(double rho)
    {
        Complex a = Complex.Zero, phi = Complex.Zero;
        foreach (var img in GaImages) a += img.Coeff * FreeSpaceG(Math.Sqrt(rho * rho + img.Depth * img.Depth));
        foreach (var img in PhiImages) phi += img.Coeff * FreeSpaceG(Math.Sqrt(rho * rho + img.Depth * img.Depth));
        return (RfConstants.Mu0 * a, phi / RfConstants.Eps0);
    }

    /// <summary>The whole kernels at ρ.</summary>
    public (Complex GA, Complex KPhi) EvaluateKernels(double rho)
    {
        var (sa, sp) = EvaluateSmooth(rho);
        var (ia, ip) = ImageTerms(rho);
        return (ia + sa, ip + sp);
    }

    /// <summary>The same with the remainder integrated directly at ρ (no spline) — the reference
    /// for the table's own accuracy.</summary>
    public (Complex GA, Complex KPhi) EvaluateKernelsDirect(double rho, int refinement = 1)
    {
        var (a, p) = Remainder(rho, refinement);
        var (poleA, polePhi) = PoleTerms(rho);
        var (ia, ip) = ImageTerms(rho);
        return (ia + a + poleA, ip + p + polePhi);
    }

    private Complex FreeSpaceG(double r)
    {
        var (sin, cos) = Math.SinCos(K0 * r);
        double scale = 1 / (4 * Math.PI * r);
        return new Complex(scale * cos, -scale * sin);
    }
}
