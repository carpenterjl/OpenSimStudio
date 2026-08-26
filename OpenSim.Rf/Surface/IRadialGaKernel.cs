using System.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>
/// The one kernel fact the junction attachment machinery needs: the vector-potential Green's
/// function G_A at a lateral separation ρ, both source and observation being IN THE SAME METAL
/// PLANE.
///
/// <para>The 1/ρ attachment disc and its half-RWG continuation are pure GEOMETRY — the fan
/// angles, γ = θ/(2πl), the Σθ = 2π identity, the current transform. Only two integrals reach
/// for a kernel at all, and each reads exactly this scalar. Naming that dependency lets the same
/// fan serve a single-slab probe, a multi-layer one, both ends of an inter-level via, and a
/// free-space wire junction, without a copy per medium.</para>
/// </summary>
public interface IRadialGaKernel
{
    /// <summary>G_A(ρ) between two in-plane points separated laterally by <paramref name="rho"/>,
    /// including µ₀ — the same normalization <see cref="LayeredKernelTable.EvaluateKernels"/>
    /// returns.</summary>
    Complex EvaluateGa(double rho);
}

/// <summary>The single-slab layered table as a radial G_A source — the shipped path, reached
/// through the seam so it is provably the same arithmetic.</summary>
public sealed class LayeredRadialGaKernel : IRadialGaKernel
{
    private readonly LayeredKernelTable _table;
    public LayeredRadialGaKernel(LayeredKernelTable table) => _table = table;
    public Complex EvaluateGa(double rho) => _table.EvaluateKernels(rho).GA;
}

/// <summary>A multi-layer / covered stackup's table as a radial G_A source.</summary>
public sealed class MultiLayerRadialGaKernel : IRadialGaKernel
{
    private readonly MultiLayerKernelTable _table;
    public MultiLayerRadialGaKernel(MultiLayerKernelTable table) => _table = table;
    public Complex EvaluateGa(double rho) => _table.EvaluateKernels(rho).GA;
}

/// <summary>Free space: G_A = µ₀·e^{−jk₀ρ}/(4πρ). The junction machinery was built for a probe
/// through a substrate, but nothing in it is layered — this adapter is what lets a wire attach
/// to a sheet with no medium at all.</summary>
public sealed class FreeSpaceRadialGaKernel : IRadialGaKernel
{
    private readonly double _k0;
    public FreeSpaceRadialGaKernel(double k0) => _k0 = k0;

    public Complex EvaluateGa(double rho)
    {
        if (rho <= 0) throw new ArgumentOutOfRangeException(nameof(rho),
            "G_A is singular at zero separation; callers regularize with the disc's radius floor.");
        var (sin, cos) = Math.SinCos(_k0 * rho);
        return RfConstants.Mu0 * new Complex(cos, -sin) / (4 * Math.PI * rho);
    }
}
