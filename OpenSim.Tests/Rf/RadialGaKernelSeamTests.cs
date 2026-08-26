using System.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage D0 — naming the ONE kernel fact the junction attachment machinery uses.
///
/// <para>The 1/ρ disc and its half-RWG continuation are geometry: fan angles, γ = θ/(2πl),
/// Σθ = 2π, the current transform. Only two integrals touch a kernel, and each reads a single
/// scalar G_A(ρ) in the metal plane. Routing that through <see cref="IRadialGaKernel"/> lets one
/// fan serve a single-slab probe, a multi-layer one, both ends of an inter-level via, and a
/// free-space wire junction — instead of a copy per medium.</para>
///
/// <para>This is a refactor stage: it lifts no guard and must change no number. The gates are
/// therefore equivalences, not physics.</para>
/// </summary>
public class RadialGaKernelSeamTests
{
    private const double Frequency = 10e9;
    private const double RhoMax = 0.03;

    private static LayeredKernelTable SlabTable() =>
        new(new SubstrateStackup(2.2, 0.0009, 1.588e-3), Frequency, RhoMax);

    [Fact]
    public void TheLayeredAdapter_IsBitwiseTheTableItWraps()
    {
        // The seam must not re-derive anything: the adapter is a projection of the table's own
        // pair, so every probe path that now goes through it produces identical values.
        var table = SlabTable();
        IRadialGaKernel adapter = new LayeredRadialGaKernel(table);
        foreach (double rho in new[] { 1e-5, 1e-4, 1e-3, 5e-3, 0.02, RhoMax })
            Assert.Equal(table.EvaluateKernels(rho).GA, adapter.EvaluateGa(rho));
    }

    [Fact]
    public void TheMultiLayerAdapter_IsBitwiseTheTableItWraps()
    {
        var table = new MultiLayerKernelTable(
            LayeredStackup.CoveredPatch(2.2, 0.0, 0.8e-3, 6.0, 0.0, 0.5e-3),
            Frequency, RhoMax, sourceInterface: LayeredStackup.CoveredPatchMetalInterface);
        IRadialGaKernel adapter = new MultiLayerRadialGaKernel(table);
        foreach (double rho in new[] { 1e-4, 1e-3, 5e-3, 0.02 })
            Assert.Equal(table.EvaluateKernels(rho).GA, adapter.EvaluateGa(rho));
    }

    [Fact]
    public void TheFreeSpaceKernel_IsTheClosedFormGreensFunction()
    {
        // µ₀·e^{−jk₀ρ}/(4πρ) — asserted against the formula rather than against another
        // implementation, since this adapter has no table behind it to agree with.
        const double k0 = 2 * Math.PI * Frequency / 299_792_458.0;
        var kernel = new FreeSpaceRadialGaKernel(k0);
        foreach (double rho in new[] { 1e-4, 1e-3, 0.01, 0.05 })
        {
            var (sin, cos) = Math.SinCos(k0 * rho);
            var expected = RfConstants.Mu0 * new Complex(cos, -sin) / (4 * Math.PI * rho);
            Assert.Equal(expected, kernel.EvaluateGa(rho));
        }
    }

    [Fact]
    public void TheFreeSpaceKernel_RefusesZeroSeparation()
    {
        var kernel = new FreeSpaceRadialGaKernel(1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => kernel.EvaluateGa(0));
    }

    [Fact]
    public void TheDiscIntegrals_AreBitwiseTheSameThroughEitherOverload()
    {
        // THE refactor pin. The probe path calls the LayeredKernelTable overloads; those now
        // delegate to the seam. Both must produce identical values, or every shipped probe
        // result has moved — which is what this stage promises not to do.
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            1.186e-2, 0.906e-2, 1.4e-3, z: 1.588e-3, portFraction: 0,
            snapVertex: (2e-3, -1e-3));
        Assert.NotNull(grid.Structure);
        var surface = grid.Structure!;
        var probe = new ProbeFeed(2e-3, -1e-3, 0.2e-3, 3);
        var fan = LayeredFarField.ProbeVertexFan(surface, probe);
        var table = SlabTable();
        IRadialGaKernel adapter = new LayeredRadialGaKernel(table);

        var probePoint = new OpenSim.Core.Numerics.Vector3D(3e-3, 1e-3, 1.588e-3);
        var viaTable = fan.DiscPotential(table, surface, probePoint);
        var viaSeam = fan.DiscPotential(adapter, surface, probePoint);
        Assert.Equal(viaTable.Ax, viaSeam.Ax);
        Assert.Equal(viaTable.Ay, viaSeam.Ay);

        Assert.Equal(fan.DiscSelf(table, surface), fan.DiscSelf(adapter, surface));
    }
}
