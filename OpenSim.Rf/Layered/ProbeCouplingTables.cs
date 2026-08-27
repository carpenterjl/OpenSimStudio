using System.Diagnostics;
using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The vertical↔surface coupling kernels for one probe: G_A^xz(ρ; z_m, z′) and K_Φ(ρ; z_m, z′)
/// tabulated in ρ at a FIXED set of tube source heights z′ (the tube bases' Gauss nodes) and one
/// fixed observation height z_m — the metal plane the tube ends on — the plan's coupling-class
/// split: a handful of 1-D tables of the existing kind amortize over every patch quadrature
/// point, while tube↔tube pairs (which only occur at tube-scale ρ) integrate directly.
///
/// Per z′ node, the same near/far structure as <see cref="LayeredKernelTable"/>: near
/// (ρ ≤ 1/k₁) splines pole terms + remainder together on a log grid; far splines the remainder
/// only on the hybrid grid and adds the H₀⁽²⁾ pole terms in closed form. K_Φ's quasi-static
/// images are added in closed form at evaluation (the caller passes the reduced
/// ρ_eff = √(ρ² + a²), so nothing here is singular); G_A^xz has no image extraction at all
/// (1/k_ρ² spectral decay — the W̃ precedent).
///
/// <para>Everything medium-specific arrives through <see cref="VerticalKernels"/>: the single
/// slab extracts five images, an N-layer stackup two, and this table neither knows nor cares —
/// which is the point, since an image set only changes how fast the remainder converges, never
/// what it converges to.</para>
///
/// <para>The observation height is a CONSTRUCTOR argument rather than "the top of the stack",
/// because a covered patch's metal is buried: the tube runs from the ground to the metal
/// interface, wherever that sits.</para>
/// </summary>
internal sealed class ProbeCouplingTables
{
    private readonly VerticalKernels _set;
    private readonly double _zObservation;
    private readonly double _rhoMin, _rhoMax, _rhoCross;
    private readonly double[] _zPrimeNodes;
    private readonly (Complex GAxz, Complex KPhi)[][] _poleResidues; // [node][pole]
    private readonly LayeredKernelTable.ComplexSpline[] _nearXz, _nearPhi;
    private readonly LayeredKernelTable.ComplexSpline?[] _farXz, _farPhi;

    /// <summary>Wall-clock build cost — a slow probe solve names its own bottleneck.</summary>
    public double BuildMilliseconds { get; }

    public IReadOnlyList<double> ZPrimeNodes => _zPrimeNodes;

    public ProbeCouplingTables(VerticalKernels set, double zObservation, double[] zPrimeNodes,
        double rhoMax, int? maxDegreeOfParallelism = null)
    {
        if (rhoMax <= 0) throw new ArgumentOutOfRangeException(nameof(rhoMax));
        var stopwatch = Stopwatch.StartNew();
        _set = set;
        _zObservation = zObservation;
        _zPrimeNodes = zPrimeNodes;
        double d = set.TotalThicknessMeters;
        double k1 = set.K0 * Math.Sqrt(set.MaxRelativePermittivity);
        double lambdaD = 2 * Math.PI / k1;
        _rhoMin = Math.Min(Math.Min(1e-4 * lambdaD, 0.01 * d), 0.1 * rhoMax);
        _rhoMax = rhoMax;
        _rhoCross = rhoMax > 2 / k1 ? 1 / k1 : rhoMax;

        var poles = set.Poles;
        _poleResidues = new (Complex, Complex)[zPrimeNodes.Length][];
        for (int n = 0; n < zPrimeNodes.Length; n++)
        {
            _poleResidues[n] = new (Complex, Complex)[poles.Count];
            for (int p = 0; p < poles.Count; p++)
            {
                var (_, resXz, resPhi) = set.PoleResidues(
                    poles[p].KRho, poles[p].IsTm, zObservation, zPrimeNodes[n]);
                _poleResidues[n][p] = (resXz, resPhi);
            }
        }

        int nodeCount = zPrimeNodes.Length;
        _nearXz = new LayeredKernelTable.ComplexSpline[nodeCount];
        _nearPhi = new LayeredKernelTable.ComplexSpline[nodeCount];
        _farXz = new LayeredKernelTable.ComplexSpline?[nodeCount];
        _farPhi = new LayeredKernelTable.ComplexSpline?[nodeCount];

        var nearGrid = LayeredKernelTable.LogGrid(_rhoMin, Math.Min(_rhoCross * 1.02, _rhoMax));
        var farGrid = _rhoCross < _rhoMax
            ? LayeredKernelTable.HybridGrid(_rhoCross * 0.98, _rhoMax,
                2 * Math.PI / set.K0 / 64)
            : null;

        // One parallel pass per (node, region), each writing its own ordered slots — the
        // slot-array recipe, so the table is bitwise identical at any thread count.
        //
        // MEASURED, then left alone: a multi-layer probe spends most of its solve right here
        // (a stackup integrates a Sommerfeld contour per knot where the single slab evaluates
        // a closed form — 364 ms became 9.0 s on the same grid), so flattening the whole
        // (node, region, knot) space into ONE Parallel.For was tried to remove the per-region
        // barriers. It measured 11.8–12.7 s against this loop's 9.0 s, in both node-major and
        // knot-major orderings, on a machine whose repeat spread is wide enough that the only
        // defensible reading is "no better". So the shipped shape stays. The real cost is the
        // contour reach itself: a probe's source and observation heights straddle at most one
        // layer, so the nearest un-extracted image is close and the head has to run out to
        // ~12/(that height) — which is the price of the 1e-12 kernel, not of this loop.
        for (int n = 0; n < nodeCount; n++)
        {
            int node = n;
            var nearXz = new Complex[nearGrid.Length];
            var nearPhi = new Complex[nearGrid.Length];
            LayeredKernelTable.ForKnots(nearGrid.Length, maxDegreeOfParallelism, i =>
            {
                var (_, rXz, rPhi) = set.Remainder(
                    nearGrid[i], zObservation, zPrimeNodes[node], 1);
                var (poleXz, polePhi) = PoleTerms(node, nearGrid[i]);
                nearXz[i] = rXz + poleXz;
                nearPhi[i] = rPhi + polePhi;
            });
            _nearXz[n] = new LayeredKernelTable.ComplexSpline(nearGrid, nearXz);
            _nearPhi[n] = new LayeredKernelTable.ComplexSpline(nearGrid, nearPhi);

            if (farGrid is null) continue;
            // The far region splines the remainder ALONE and adds the H₀⁽²⁾ pole terms in closed
            // form at evaluation; the near region splines them together, because there the
            // pole's ln ρ cancels against the remainder's.
            var farXz = new Complex[farGrid.Length];
            var farPhi = new Complex[farGrid.Length];
            LayeredKernelTable.ForKnots(farGrid.Length, maxDegreeOfParallelism, i =>
            {
                var (_, rXz, rPhi) = set.Remainder(
                    farGrid[i], zObservation, zPrimeNodes[node], 1);
                farXz[i] = rXz;
                farPhi[i] = rPhi;
            });
            _farXz[n] = new LayeredKernelTable.ComplexSpline(farGrid, farXz, logAbscissa: false);
            _farPhi[n] = new LayeredKernelTable.ComplexSpline(farGrid, farPhi, logAbscissa: false);
        }
        BuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>The FULL spatial coupling kernels at reduced lateral distance
    /// <paramref name="rhoEff"/> for tube node <paramref name="nodeIndex"/>:
    /// G_A^xz (the scalar whose in-plane gradient is the horizontal A of the tube
    /// current) and K_Φ(z_m, z′) with its closed-form images.</summary>
    public (Complex GAxz, Complex KPhi) Evaluate(int nodeIndex, double rhoEff)
    {
        if (rhoEff > _rhoMax)
            throw new ArgumentOutOfRangeException(nameof(rhoEff),
                $"ρ = {rhoEff:g6} exceeds the coupling table's build radius {_rhoMax:g6} — "
                + "build it for the structure's true diameter.");
        double clamped = Math.Max(rhoEff, _rhoMin);
        Complex gxz, phiSmooth;
        if (clamped <= _rhoCross || _farXz[nodeIndex] is null)
        {
            gxz = _nearXz[nodeIndex].Evaluate(clamped);
            phiSmooth = _nearPhi[nodeIndex].Evaluate(clamped);
        }
        else
        {
            var (poleXz, polePhi) = PoleTerms(nodeIndex, clamped);
            gxz = _farXz[nodeIndex]!.Evaluate(clamped) + poleXz;
            phiSmooth = _farPhi[nodeIndex]!.Evaluate(clamped) + polePhi;
        }

        Complex kPhi = phiSmooth;
        foreach (var image in _set.Images(_zObservation, _zPrimeNodes[nodeIndex]))
        {
            double r = Math.Sqrt(rhoEff * rhoEff + image.Height * image.Height);
            var (sin, cos) = Math.SinCos(_set.K0 * r);
            var g = new Complex(cos, -sin) / (4 * Math.PI * r);
            kPhi += image.CoefficientKPhi * g / RfConstants.Eps0;
        }
        return (gxz, kPhi);
    }

    private (Complex GAxz, Complex KPhi) PoleTerms(int nodeIndex, double rho)
    {
        Complex xz = Complex.Zero, phi = Complex.Zero;
        var poles = _set.Poles;
        for (int p = 0; p < poles.Count; p++)
        {
            var factor = new Complex(0, -0.25) * poles[p].KRho * Bessel.H02(poles[p].KRho * rho);
            xz += _poleResidues[nodeIndex][p].GAxz * factor;
            phi += _poleResidues[nodeIndex][p].KPhi * factor;
        }
        return (xz, phi);
    }
}
