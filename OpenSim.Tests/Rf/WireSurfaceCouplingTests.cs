using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage D1a — the mixed wire↔sheet block of a hybrid MoM matrix. Wire–wire coupling belongs to
/// the thin-wire solver and sheet–sheet to the RWG solver; this cross block is the only genuinely
/// new integral a hybrid structure needs, so it is gated on its own before anything is assembled
/// on top of it.
///
/// <para>The oracle is a brute-force evaluation of the same mixed-potential integral at a much
/// finer sampling, written independently in the test. That is the right reference here because
/// there is no coincident-support case and hence no analytic inner integral to compare against —
/// the wire is a curve, the basis a surface, and the wire's own radius keeps the kernel
/// bounded.</para>
/// </summary>
public class WireSurfaceCouplingTests
{
    private const double Frequency = 1e9;
    private static double K0 => 2 * Math.PI * Frequency / 299_792_458.0;
    private static double Omega => 2 * Math.PI * Frequency;

    /// <summary>A flat plate in the z = 0 plane and a wire parallel to x at height h.</summary>
    private static (WireStructure Wire, SurfaceStructure Surface) Fixture(
        double height, double wireRadius = 1e-3, double plateEdge = 0.03)
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(0.10, 0.08, plateEdge, z: 0,
            portFraction: 0);
        Assert.NotNull(grid.Structure);

        var nodes = new List<Vector3D>();
        const int segments = 6;
        for (int i = 0; i <= segments; i++)
            nodes.Add(new Vector3D(-0.03 + 0.06 * i / segments, 0.004, height));
        var radii = Enumerable.Repeat(wireRadius, segments).ToList();
        var wire = new WireStructure(nodes, radii, isLoop: false, ground: null);
        return (wire, grid.Structure!);
    }

    /// <summary>An independent, deliberately naive evaluation of the same mixed-potential
    /// integral: uniform midpoint sampling on the wire segment and on each triangle, refined far
    /// past what the production dispatch uses. Slow, obvious, and structurally unlike the
    /// production code — which is what makes it a reference rather than a mirror.</summary>
    private static Complex BruteForce(WireStructure wire, int wireBasis,
        SurfaceStructure surface, int rwgBasis, int wireSamples, int triangleSamples)
    {
        Complex vectorFactor = Complex.ImaginaryOne * Omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * Omega);
        var edge = surface.Edges[rwgBasis];
        Complex vector = Complex.Zero, charge = Complex.Zero;

        foreach (bool rising in new[] { true, false })
        {
            int element = rising ? wire.RisingElement(wireBasis) : wire.FallingElement(wireBasis);
            if (element < 0) continue;
            var a = wire.ElementStart(element);
            var b = wire.ElementEnd(element);
            double length = wire.ElementLength(element);
            var tHat = wire.ElementDirection(element);
            double radius = wire.ElementRadii[element];
            double slope = (rising ? 1.0 : -1.0) / length;

            foreach (var (tri, sign, opp) in new[]
                     {
                         (edge.PlusTriangle, +1.0, edge.PlusOpposite),
                         (edge.MinusTriangle, -1.0, edge.MinusOpposite),
                     })
            {
                var (ia, ib, ic) = surface.Triangles[tri];
                var va = surface.Vertices[ia];
                var vb = surface.Vertices[ib];
                var vc = surface.Vertices[ic];
                double area = surface.TriangleAreas[tri];
                var pOpp = surface.Vertices[opp];
                double jScale = sign * edge.Length / (2 * area);
                double divergence = sign * edge.Length / area;

                for (int i = 0; i < wireSamples; i++)
                {
                    double s = (i + 0.5) / wireSamples;
                    var r = a + (b - a) * s;
                    double f = rising ? s : 1 - s;
                    double ws = length / wireSamples;

                    // Uniform barycentric lattice over the triangle, midpoint rule.
                    for (int m = 0; m < triangleSamples; m++)
                        for (int n = 0; n < triangleSamples - m; n++)
                        {
                            // Two sub-triangles per lattice cell except on the diagonal.
                            foreach (var (u, v) in Cell(m, n, triangleSamples))
                            {
                                double w = 1 - u - v;
                                var rp = va * w + vb * u + vc * v;
                                double cellArea = area / (triangleSamples * triangleSamples);
                                var d = r - rp;
                                double rEff = Math.Sqrt(d.LengthSquared + radius * radius);
                                var (sin, cos) = Math.SinCos(K0 * rEff);
                                var g = new Complex(cos, -sin) / rEff;
                                var jVec = (rp - pOpp) * jScale;
                                vector += ws * cellArea * f * Vector3D.Dot(tHat, jVec) * g;
                                charge += ws * cellArea * slope * divergence * g;
                            }
                        }
                }
            }
        }
        return vectorFactor * vector + chargeFactor * charge;
    }

    /// <summary>The barycentric centroid(s) of one lattice cell: the upward sub-triangle always,
    /// plus the downward one where it exists. Together they tile the triangle exactly.</summary>
    private static IEnumerable<(double U, double V)> Cell(int m, int n, int count)
    {
        double h = 1.0 / count;
        yield return ((m + 1.0 / 3) * h, (n + 1.0 / 3) * h);
        if (m + n < count - 1)
            yield return ((m + 2.0 / 3) * h, (n + 2.0 / 3) * h);
    }

    [Theory]
    [InlineData(0.05)]     // far: over a plate-edge away
    [InlineData(0.02)]     // near
    [InlineData(0.008)]    // very near — under one element size
    public void TheMixedBlock_MatchesAnIndependentBruteForceIntegral(double height)
    {
        var (wire, surface) = Fixture(height);
        // A basis near the plate centre, and one out at the rim: different geometry, same claim.
        foreach (int rwgBasis in new[] { surface.BasisCount / 2, surface.BasisCount / 5 })
        {
            const int wireBasis = 2;
            var got = InvokeMutual(wire, wireBasis, surface, rwgBasis);
            var want = BruteForce(wire, wireBasis, surface, rwgBasis, 40, 40);
            double tol = 3e-3 * want.Magnitude;
            Assert.True((got - want).Magnitude < tol,
                $"h = {height}, basis {rwgBasis}: {got} vs brute force {want} "
                + $"(rel {(got - want).Magnitude / want.Magnitude:e2})");
        }
    }

    [Fact]
    public void TheMixedBlock_IsSelfConvergent_AsTheOracleRefines()
    {
        // The oracle's own convergence, so the tolerance above is known to be measuring the
        // production dispatch rather than the reference's coarseness.
        var (wire, surface) = Fixture(0.01);
        int rwgBasis = surface.BasisCount / 2;
        var coarse = BruteForce(wire, 2, surface, rwgBasis, 20, 20);
        var fine = BruteForce(wire, 2, surface, rwgBasis, 60, 60);
        Assert.True((fine - coarse).Magnitude < 5e-3 * fine.Magnitude,
            $"the oracle has not settled: {coarse} → {fine}");
    }

    [Fact]
    public void TheMixedBlock_VanishesAsTheWireRecedes()
    {
        // The kernel falls as 1/R, so moving the wire an order of magnitude away must drop the
        // coupling by roughly the same order. A block that ignored the separation — a constant,
        // or one keyed off the wrong geometry — would not.
        int basisAt(double h)
        {
            var (_, s) = Fixture(h);
            return s.BasisCount / 2;
        }
        var (wireNear, surfNear) = Fixture(0.01);
        var (wireFar, surfFar) = Fixture(0.10);
        var near = InvokeMutual(wireNear, 2, surfNear, basisAt(0.01));
        var far = InvokeMutual(wireFar, 2, surfFar, basisAt(0.10));
        Assert.True(far.Magnitude < 0.5 * near.Magnitude,
            $"coupling barely changed with distance: near {near.Magnitude:e3}, far {far.Magnitude:e3}");
    }

    private static Complex InvokeMutual(WireStructure wire, int wireBasis,
        SurfaceStructure surface, int rwgBasis) =>
        WireSurfaceCoupling.Mutual(wire, wireBasis, surface, rwgBasis, K0, Omega);
}
