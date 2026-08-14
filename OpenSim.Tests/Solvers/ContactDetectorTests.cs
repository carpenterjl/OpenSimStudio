using OpenSim.Core.Model;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Gates for finding the interfaces between touching bodies. The measured quantity is the
/// COUPLED AREA — the honest Σ of quadrature weights — because that is what the physics
/// downstream is proportional to, and it is exactly known for these fixtures.
/// </summary>
public class ContactDetectorTests
{
    private static readonly Material Steel = StructuredBoxMesh.Conductor("Steel", 45);

    private const double Width = 0.01;      // y and z extent of every fixture box
    private const double InterfaceArea = Width * Width;

    /// <summary>Two boxes meeting at x = 0.02, meshed at independent resolutions.</summary>
    private static FeMeshAssembler.AssembledMesh Stack(int nLeft, int nRight,
        int transverseLeft, int transverseRight, double gap = 0)
    {
        var bodies = new List<Body>
        {
            StructuredBoxMesh.Box("Left", 0, 0.02, 0, Width, 0, Width,
                nLeft, transverseLeft, transverseLeft, Steel),
            StructuredBoxMesh.Box("Right", 0.02 + gap, 0.04 + gap, 0, Width, 0, Width,
                nRight, transverseRight, transverseRight, Steel)
        };
        return FeMeshAssembler.Assemble(bodies);
    }

    [Fact]
    public void MatchingInterface_CouplesTheWholeFaceExactly()
    {
        var assembled = Stack(2, 2, 2, 2);
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases);

        var contact = Assert.Single(contacts);
        Assert.Equal(0, contact.BodyA);
        Assert.Equal(1, contact.BodyB);
        Assert.Equal(InterfaceArea, contact.CoupledArea, 15);
        Assert.Equal(InterfaceArea, contact.AreaA, 15);
        Assert.Equal(InterfaceArea, contact.AreaB, 15);
        Assert.Equal(0, contact.UnpairedPoints);
    }

    [Fact]
    public void NonMatchingInterface_StillCouplesTheWholeFaceExactly()
    {
        // 2×2 against 3×3 on the interface: no node has a partner node, yet both sides'
        // vertex quadratures still measure the full area — the point of projecting rather
        // than matching.
        var assembled = Stack(2, 3, 2, 3);
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases);

        var contact = Assert.Single(contacts);
        Assert.Equal(InterfaceArea, contact.CoupledArea, 15);
        Assert.Equal(InterfaceArea, contact.AreaA, 15);
        Assert.Equal(InterfaceArea, contact.AreaB, 15);
        Assert.Equal(0, contact.UnpairedPoints);
    }

    [Theory]
    [InlineData(2, 2.5e-5)]
    [InlineData(4, 1.25e-5)]
    public void PartialOverlap_MeasuresTheCoveredSideExactly_AndOverReportsTheRimBand(
        int transverse, double expectedExcess)
    {
        // The right box covers half the left box's face (y ∈ [0, W/2]).
        var bodies = new List<Body>
        {
            StructuredBoxMesh.Box("Left", 0, 0.02, 0, Width, 0, Width, 2, transverse, transverse, Steel),
            StructuredBoxMesh.Box("Right", 0.02, 0.04, 0, Width / 2, 0, Width, 2, 1, 2, Steel)
        };
        var assembled = FeMeshAssembler.Assemble(bodies);
        var contact = Assert.Single(ContactDetector.Find(assembled.Mesh, assembled.NodeBases));

        // The covered side is exact: every one of its vertices finds a partner.
        Assert.Equal(InterfaceArea / 2, contact.AreaB, 15);

        // The uncovered side over-reports, and by an exactly predictable amount. Triangles
        // straddling the rim y = W/2 have vertices ON the rim, which legitimately pair, and a
        // vertex rule credits each paired vertex its full A/3 share. Per straddling QUAD the
        // two triangles carry 2/3 and 1/3 of a triangle area — exactly half the quad — so
        //     AreaA = overlap + ½·(rim row area),
        // which is first order in the element size: halving it halves the excess (the two
        // rows of this theory). This is the price of the vertex rule over a mortar
        // projection, stated and measured rather than hidden.
        double rimRow = (Width / transverse) * Width;
        Assert.Equal(InterfaceArea / 2 + 0.5 * rimRow, contact.AreaA, 15);
        Assert.Equal(expectedExcess, contact.AreaA - InterfaceArea / 2, 15);
        Assert.Equal(0.5 * (contact.AreaA + contact.AreaB), contact.CoupledArea, 15);

        // Straddling triangles report their outside vertices instead of silently absorbing them.
        Assert.True(contact.UnpairedPoints > 0);
    }

    [Fact]
    public void SeparatedBodies_ProduceNoContact()
    {
        var assembled = Stack(2, 2, 2, 2, gap: 0.005);   // 5 mm apart, elements are 5–10 mm
        var log = new List<string>();
        var contacts = ContactDetector.Find(assembled.Mesh, assembled.NodeBases, null, log.Add);
        Assert.Empty(contacts);
        Assert.Contains(log, l => l.Contains("No body-to-body contact"));
    }

    [Fact]
    public void GapWithinTolerance_IsStillContact()
    {
        // A CAD-coincident face lands within the mesher's jitter, not exactly — so a gap
        // well under the tolerance must still couple.
        var assembled = Stack(2, 2, 2, 2, gap: 1e-6);
        var contact = Assert.Single(ContactDetector.Find(assembled.Mesh, assembled.NodeBases));
        Assert.Equal(InterfaceArea, contact.CoupledArea, 15);
    }

    [Fact]
    public void CoplanarFacesFacingTheSameWay_AreNotContact()
    {
        // Two bodies side by side in y: their +x faces are coplanar and CO-oriented. Only
        // anti-parallel normals mean "facing each other".
        var bodies = new List<Body>
        {
            StructuredBoxMesh.Box("A", 0, 0.02, 0, Width, 0, Width, 2, 2, 2, Steel),
            StructuredBoxMesh.Box("B", 0, 0.02, 2 * Width, 3 * Width, 0, Width, 2, 2, 2, Steel)
        };
        var assembled = FeMeshAssembler.Assemble(bodies);
        Assert.Empty(ContactDetector.Find(assembled.Mesh, assembled.NodeBases));
    }

    [Fact]
    public void Detection_IsIndependentOfBodyOrder()
    {
        var forward = Stack(2, 3, 2, 3);
        var reversed = FeMeshAssembler.Assemble(new List<Body>
        {
            StructuredBoxMesh.Box("Right", 0.02, 0.04, 0, Width, 0, Width, 3, 3, 3, Steel),
            StructuredBoxMesh.Box("Left", 0, 0.02, 0, Width, 0, Width, 2, 2, 2, Steel)
        });

        var a = Assert.Single(ContactDetector.Find(forward.Mesh, forward.NodeBases));
        var b = Assert.Single(ContactDetector.Find(reversed.Mesh, reversed.NodeBases));
        Assert.Equal(a.CoupledArea, b.CoupledArea, 12);
        Assert.Equal(a.Stamps.Count, b.Stamps.Count);
    }

    [Fact]
    public void Detection_IsDeterministic()
    {
        var first = Stack(2, 3, 2, 3);
        var second = Stack(2, 3, 2, 3);
        var a = Assert.Single(ContactDetector.Find(first.Mesh, first.NodeBases));
        var b = Assert.Single(ContactDetector.Find(second.Mesh, second.NodeBases));
        Assert.Equal(a.Stamps.Count, b.Stamps.Count);
        for (int i = 0; i < a.Stamps.Count; i++)
            Assert.Equal(a.Stamps[i], b.Stamps[i]);   // bitwise: same nodes, weights, coefficients
    }

    [Fact]
    public void EveryStamp_MeasuresATemperatureJumpAndCarriesPositiveWeight()
    {
        var assembled = Stack(2, 3, 2, 3);
        var contact = Assert.Single(ContactDetector.Find(assembled.Mesh, assembled.NodeBases));
        foreach (var s in contact.Stamps)
        {
            // Coefficients sum to zero: a uniform temperature field has zero jump, so a
            // body in equilibrium with its neighbour exchanges exactly no heat.
            Assert.Equal(0, s.S0 + s.S1 + s.S2 + s.S3, 12);
            Assert.Equal(1.0, s.S0);
            Assert.True(s.Weight > 0);
            // Source and target lie on opposite sides of the interface.
            Assert.NotEqual(assembled.BodyOfNode(s.Node0), assembled.BodyOfNode(s.Node1));
        }
    }

    [Fact]
    public void ConductanceCanBeChangedWithoutReDetecting()
    {
        var assembled = Stack(2, 2, 2, 2);
        var contact = Assert.Single(ContactDetector.Find(assembled.Mesh, assembled.NodeBases));
        var stronger = contact.WithConductance(2e4);
        Assert.Equal(2e4, stronger.Conductance);
        Assert.Same(contact.Stamps, stronger.Stamps);
    }

    [Fact]
    public void InvalidSettings_AreTypedFailures()
    {
        var assembled = Stack(2, 2, 2, 2);
        Assert.Throws<InvalidOperationException>(() => ContactDetector.Find(assembled.Mesh,
            assembled.NodeBases, new ContactDetectionSettings { DefaultConductance = 0 }));
        Assert.Throws<InvalidOperationException>(() => ContactDetector.Find(assembled.Mesh,
            assembled.NodeBases, new ContactDetectionSettings { GapTolerance = -1 }));
        Assert.Throws<InvalidOperationException>(() => ContactDetector.Find(assembled.Mesh,
            assembled.NodeBases, new ContactDetectionSettings { NormalOpposition = 1.5 }));
    }

    [Fact]
    public void ContactDetectedOnADifferentMesh_IsRejectedNotSilentlyMisapplied()
    {
        var assembled = Stack(2, 2, 2, 2);
        var contact = Assert.Single(ContactDetector.Find(assembled.Mesh, assembled.NodeBases));
        var ex = Assert.Throws<InvalidOperationException>(() => contact.Validate(10));
        Assert.Contains("different mesh", ex.Message);
    }
}
