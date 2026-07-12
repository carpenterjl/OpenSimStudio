using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage E checkpoint E2: the probe tube bases and their layered assembly, gated by
/// the CROSS-SOLVER identity — at εr = 1 with slab thickness = L, a standalone probe
/// (open top) IS a monopole of length L over a PEC ground, and must reproduce the
/// thin-wire solver's monopole (Stage A image theory) at the SAME node placement.
/// The two paths share the primary-track quadrature but differ in everything layered:
/// the ground arrives through the kernel's image coefficients here vs the explicit
/// image pass there, and the smooth track must integrate to ~zero.
/// </summary>
public class ProbeFeedTests
{
    [Fact]
    public void EpsilonOne_ProbeOnly_IsTheThinWireMonopole_CrossSolverIdentity()
    {
        // λ/4 monopole at 2.4 GHz: L = 31.2 mm; 8 elements of 3.9 mm ≥ 2·0.5 mm.
        double length = 0.0312, radius = 0.5e-3, f = 2.4e9;
        int segments = 8;

        var air = new SubstrateStackup(1.0, 0.0, length);
        var set = new VerticalKernelSet(air, f);
        var probe = new ProbeFeed(0, 0, radius, segments);
        var (zProbe, probeCurrents) = ProbeAssembly.SolveProbeOnly(set, probe);

        var nodes = new Vector3D[segments + 1];
        for (int i = 0; i <= segments; i++)
            nodes[i] = new Vector3D(0, 0, length * i / segments);
        var wire = new WireStructure(nodes, Enumerable.Repeat(radius, segments).ToArray(),
            isLoop: false, ground: new GroundPlane(0), startGrounded: true);
        var wireSolution = new ThinWireMomSolver().Solve(wire, f, feedBasis: 0);

        // Same bases, same primary quadrature; the difference is the smooth-track
        // integration noise (~1e-9 measured) — gate an order above it.
        double rel = (zProbe - wireSolution.InputImpedance).Magnitude
                     / wireSolution.InputImpedance.Magnitude;
        Assert.True(rel <= 1e-6,
            $"Probe Zin {zProbe} vs thin-wire monopole {wireSolution.InputImpedance} (rel {rel:e2})");

        // The current DISTRIBUTIONS must match basis-by-basis, not just the port.
        Assert.Equal(wireSolution.BasisCurrents.Length, probeCurrents.Length);
        for (int b = 0; b < probeCurrents.Length; b++)
        {
            double currentRel = (probeCurrents[b] - wireSolution.BasisCurrents[b]).Magnitude
                                / wireSolution.BasisCurrents[0].Magnitude;
            Assert.True(currentRel <= 1e-6,
                $"Basis {b}: probe {probeCurrents[b]} vs wire {wireSolution.BasisCurrents[b]} (rel {currentRel:e2})");
        }
    }

    [Fact]
    public void ProbeBlock_IsBitwiseComplexSymmetric()
    {
        var substrate = new SubstrateStackup(2.2, 0.001, 1.588e-3);
        var set = new VerticalKernelSet(substrate, 10e9);
        var probe = new ProbeFeed(0, 0, 0.25e-3, 3);
        var z = ProbeAssembly.ProbeSelfBlock(set, probe, 2 * Math.PI * 10e9, includeTopBasis: true);
        Assert.Equal(4, z.Rows);
        for (int i = 0; i < z.Rows; i++)
            for (int j = i + 1; j < z.Columns; j++)
                Assert.Equal(z[i, j], z[j, i]);
    }

    [Fact]
    public void SlabTooThinForTheBore_IsATypedFailure()
    {
        var substrate = new SubstrateStackup(2.2, 0.0, 1.588e-3);
        var probe = new ProbeFeed(0, 0, 0.4e-3, 3); // 0.53 mm elements < 2·0.4 mm
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ProbeAssembly.TubeNodes(substrate, probe));
        Assert.Contains("too thin for the probe bore", ex.Message);
    }
}
