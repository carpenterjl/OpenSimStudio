using System.Diagnostics;
using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The vertical↔surface coupling kernels for one probe: G_A^xz(ρ; d, z′) and
/// K_Φ(ρ; d, z′) tabulated in ρ at a FIXED set of tube source heights z′ (the tube
/// bases' Gauss nodes) — the plan's coupling-class split: a handful of 1-D tables of
/// the existing kind amortize over every patch quadrature point, while tube↔tube
/// pairs (which only occur at tube-scale ρ) integrate directly.
///
/// Per z′ node, the same near/far structure as <see cref="LayeredKernelTable"/>:
/// near (ρ ≤ 1/k₁) splines pole terms + remainder together on a log grid; far splines
/// the remainder only on the hybrid grid and adds the H₀⁽²⁾ pole terms in closed form.
/// K_Φ's five quasi-static images are added in closed form at evaluation (the caller
/// passes the reduced ρ_eff = √(ρ² + a²), so nothing here is singular); G_A^xz has no
/// image extraction at all (1/k_ρ² spectral decay — the W̃ precedent).
/// </summary>
internal sealed class ProbeCouplingTables
{
    private readonly VerticalKernelSet _set;
    private readonly double _rhoMin, _rhoMax, _rhoCross;
    private readonly double[] _zPrimeNodes;
    private readonly (Complex GAxz, Complex KPhi)[][] _poleResidues; // [node][pole]
    private readonly LayeredKernelTable.ComplexSpline[] _nearXz, _nearPhi;
    private readonly LayeredKernelTable.ComplexSpline?[] _farXz, _farPhi;

    /// <summary>Wall-clock build cost — a slow probe solve names its own bottleneck.</summary>
    public double BuildMilliseconds { get; }

    public IReadOnlyList<double> ZPrimeNodes => _zPrimeNodes;

    public ProbeCouplingTables(VerticalKernelSet set, double[] zPrimeNodes, double rhoMax,
        int? maxDegreeOfParallelism = null)
    {
        if (rhoMax <= 0) throw new ArgumentOutOfRangeException(nameof(rhoMax));
        var stopwatch = Stopwatch.StartNew();
        _set = set;
        _zPrimeNodes = zPrimeNodes;
        var substrate = set.Substrate;
        double d = substrate.ThicknessMeters;
        double k1 = set.K0 * Math.Sqrt(substrate.RelativePermittivity);
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
                var (_, resXz, resPhi) = VerticalSpatialKernels.PoleResidues(
                    substrate, set.K0, poles[p].KRho, d, zPrimeNodes[n]);
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

        // One flat knot list over (node, region, knot) — the slot-array recipe keeps
        // the build bitwise deterministic at any thread count.
        for (int n = 0; n < nodeCount; n++)
        {
            int node = n;
            var nearXz = new Complex[nearGrid.Length];
            var nearPhi = new Complex[nearGrid.Length];
            LayeredKernelTable.ForKnots(nearGrid.Length, maxDegreeOfParallelism, i =>
            {
                var (_, rXz, rPhi) = SommerfeldIntegrator.VerticalRemainder(
                    substrate, set.K0, poles, nearGrid[i], d, zPrimeNodes[node]);
                var (poleXz, polePhi) = PoleTerms(node, nearGrid[i]);
                nearXz[i] = rXz + poleXz;
                nearPhi[i] = rPhi + polePhi;
            });
            _nearXz[n] = new LayeredKernelTable.ComplexSpline(nearGrid, nearXz);
            _nearPhi[n] = new LayeredKernelTable.ComplexSpline(nearGrid, nearPhi);

            if (farGrid is not null)
            {
                var farXz = new Complex[farGrid.Length];
                var farPhi = new Complex[farGrid.Length];
                LayeredKernelTable.ForKnots(farGrid.Length, maxDegreeOfParallelism, i =>
                {
                    var (_, rXz, rPhi) = SommerfeldIntegrator.VerticalRemainder(
                        substrate, set.K0, poles, farGrid[i], d, zPrimeNodes[node]);
                    farXz[i] = rXz;
                    farPhi[i] = rPhi;
                });
                _farXz[n] = new LayeredKernelTable.ComplexSpline(farGrid, farXz, logAbscissa: false);
                _farPhi[n] = new LayeredKernelTable.ComplexSpline(farGrid, farPhi, logAbscissa: false);
            }
        }
        BuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>The FULL spatial coupling kernels at reduced lateral distance
    /// <paramref name="rhoEff"/> for tube node <paramref name="nodeIndex"/>:
    /// G_A^xz (the scalar whose in-plane gradient is the horizontal A of the tube
    /// current) and K_Φ(d, z′) with its closed-form images.</summary>
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
        double d = _set.Substrate.ThicknessMeters;
        foreach (var image in VerticalSpatialKernels.Images(_set.Substrate, d, _zPrimeNodes[nodeIndex]))
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
