using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage D1 — the hybrid wire↔sheet solve: an RWG plate, a thin wire ending on it, and one
/// junction unknown carrying the 1/ρ attachment mode across the contact.
///
/// <para>The anchor is the structure this codebase can already solve two ways: a quarter-wave
/// monopole. Image theory gives it over an INFINITE PEC ground; the hybrid gives it standing on
/// a finite plate. They must agree on the radiation resistance, and the finite plate's remaining
/// difference must shrink where a discretization difference should.</para>
///
/// <para><b>What the mesh does and does not move.</b> The resistance is an integral of the whole
/// current distribution and lands within a few ohms of the image-theory answer at every mesh.
/// The reactance is a near-field quantity concentrated at the contact, and until Fix 11 it moved
/// strongly with the sheet mesh (j49.9 at λ/8, j31.5 at λ/12). That was read as a physical
/// ln(mesh/wire radius) term and gated as "convergence"; it was the junction disc's self term
/// integrated with coincident nodes, a spurious series inductance growing as mesh²/radius. With
/// the self term integrated properly, measured (plate 1λ, wire radius λ/2000, infinite-ground
/// reference 41.2 + j22.0 Ω):</para>
/// <code>
///   mesh λ/6   43.80 + j16.97
///   mesh λ/8   43.41 + j17.09
///   mesh λ/10  43.27 + j17.14
///   mesh λ/12  43.19 + j17.20
/// </code>
/// <para>— flat to 0.25 Ω. The 5 Ω that remains against the infinite ground is the finite plate
/// (a 1.5λ plate reads 38.5 + j17.9), not the mesh.</para>
/// </summary>
public class WireAttachedHybridTests
{
    private const double Frequency = 300e6;
    private const double Lambda = 299792458.0 / Frequency;
    private const double Radius = Lambda / 2000;
    private const double Height = 0.25 * Lambda;

    /// <summary>A square plate with a vertex snapped at the origin, and a wire of the given
    /// length attached there at the given angle above the sheet.</summary>
    private static (SurfaceStructure Surface, WireStructure Wire) Build(
        double plateSide, double edge, int elements = 10, double degrees = 90, double radius = Radius)
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(plateSide, plateSide, edge,
            z: 0, portFraction: 0, snapVertex: (0, 0));
        Assert.True(grid.Structure is not null, grid.FailureReason);

        // The perpendicular case is built from the canonical monopole rather than from
        // cos/sin(90°), which is 6.1e-17 — not zero, and the disc-coupling identity below is
        // EXACT, so a fixture that fakes the axis would defeat it.
        var tip = degrees == 90
            ? new Vector3D(0, 0, Height)
            : new Vector3D(Height * Math.Cos(degrees * Math.PI / 180), 0,
                           Height * Math.Sin(degrees * Math.PI / 180));
        var wireGrid = WireGridBuilder.Build(
            new[] { new WireSegment(Vector3D.Zero, tip, radius) },
            maxElementLength: Height / elements, attachmentPoint: Vector3D.Zero);
        Assert.True(wireGrid.Structure is not null, wireGrid.FailureReason);
        return (grid.Structure!, wireGrid.Structure!);
    }

    /// <summary>The same monopole over an INFINITE PEC ground, by image theory — the reference
    /// the hybrid has to reproduce.</summary>
    private static Complex InfiniteGroundMonopole(int elements = 10, double radius = Radius)
    {
        var grid = WireGridBuilder.Build(CanonicalAntennas.Monopole(Height, radius),
            maxElementLength: Height / elements, ground: new GroundPlane(0));
        Assert.True(grid.Structure is not null, grid.FailureReason);
        int feed = grid.Structure!.NearestBasis(Vector3D.Zero);
        Assert.Equal(0, grid.Structure.BasisNode(feed));
        return new ThinWireMomSolver().Solve(grid.Structure, Frequency, feed).InputImpedance;
    }

    // ------------------------------------------------------------------
    // Structure: the blocks this stage adds are scattered symmetrically
    // ------------------------------------------------------------------

    [Fact]
    public void EveryBlockTheHybridAdds_IsBitwiseComplexSymmetric()
    {
        // The two source blocks are subtracted out through the same index map that placed them,
        // so what remains is exactly the mixed and junction couplings this stage introduces.
        // Those are the ones that could be scattered once instead of twice, and an asymmetric
        // MoM matrix is not a physical result at all — it is worth catching structurally rather
        // than waiting for a number to look wrong. (The full matrix is symmetric only to ~1e-19
        // relative, because the thin-wire self block's own moments are not symmetrized; that is
        // pre-existing and is exactly what subtracting it removes from this claim.)
        var (surface, wire) = Build(0.75 * Lambda, Lambda / 8);
        double omega = 2 * Math.PI * Frequency;
        double k = omega / RfConstants.SpeedOfLight;

        var solver = new SurfaceMomSolver();
        var (z, _, wireIndex) = solver.AssembleWireAttached(surface, wire, k, omega);
        var zCc = SurfaceMomSolver.AssembleImpedanceMatrix(surface, k, omega);
        var zWw = ThinWireMomSolver.AssembleImpedanceMatrix(wire, k, omega);

        int nEdges = surface.BasisCount;
        for (int i = 0; i < nEdges; i++)
            for (int j = 0; j < nEdges; j++)
                z[i, j] -= zCc[i, j];
        for (int b1 = 0; b1 < wire.BasisCount; b1++)
            for (int b2 = 0; b2 < wire.BasisCount; b2++)
                z[wireIndex[b1], wireIndex[b2]] -= zWw[b1, b2];

        for (int i = 0; i < z.Rows; i++)
            for (int j = 0; j < i; j++)
                Assert.Equal(z[i, j], z[j, i]);
    }

    [Fact]
    public void ARwgMutual_IsTheSumOfItsTwoHalves()
    {
        // The junction's continuation is a LONE half, so the mixed block had to be generalized
        // from "an RWG basis" to "a list of halves". This is the decomposition that generalization
        // rests on. It is asserted to 1e-12 relative rather than bitwise ON PURPOSE: the two
        // halves' charge legs are summed before the 1/(ωε₀) prefactor multiplies them, and IEEE
        // addition does not distribute — which is precisely why the RWG case still goes through
        // one call, keeping the arithmetic its oracle gates were measured against.
        var (surface, wire) = Build(0.5 * Lambda, Lambda / 6, degrees: 45);
        double omega = 2 * Math.PI * Frequency;
        double k = omega / RfConstants.SpeedOfLight;

        for (int rwg = 0; rwg < Math.Min(12, surface.BasisCount); rwg++)
        {
            var edge = surface.Edges[rwg];
            var whole = WireSurfaceCoupling.Mutual(wire, 1, surface, rwg, k, omega);
            var plus = WireSurfaceCoupling.MutualHalves(wire, 1, surface, new[]
            {
                new WireSurfaceCoupling.SurfaceHalf(edge.PlusTriangle, +1.0, edge.PlusOpposite, edge.Length)
            }, k, omega);
            var minus = WireSurfaceCoupling.MutualHalves(wire, 1, surface, new[]
            {
                new WireSurfaceCoupling.SurfaceHalf(edge.MinusTriangle, -1.0, edge.MinusOpposite, edge.Length)
            }, k, omega);
            Assert.True((whole - (plus + minus)).Magnitude <= 1e-12 * whole.Magnitude,
                $"edge {rwg}: {whole} vs {plus + minus}");
        }
    }

    // ------------------------------------------------------------------
    // The disc coupling: the term that only an OBLIQUE wire has
    // ------------------------------------------------------------------

    [Fact]
    public void APerpendicularWire_HasIdenticallyZeroDiscCoupling()
    {
        // The disc is purely in-plane, so t̂·D vanishes for a wire along the normal — not
        // approximately, exactly. This is the cheapest possible check that the term is wired to
        // the geometry rather than to a fitted constant, and it is a genuine BITWISE identity.
        var (surface, wire) = Build(0.75 * Lambda, Lambda / 8);
        double omega = 2 * Math.PI * Frequency;
        double k = omega / RfConstants.SpeedOfLight;
        var junction = WireSurfaceJunction.Attach(wire, surface);

        for (int b = 0; b < wire.BasisCount; b++)
            Assert.Equal(Complex.Zero,
                WireSurfaceCoupling.MutualDisc(wire, b, junction.Fan, surface, k, omega));
    }

    [Fact]
    public void TheDiscCoupling_SelfConvergesUnderPanelRefinement()
    {
        // The disc's source is bounded (the 1/ρ is cancelled by the polar measure) but the wire's
        // kernel is not: a wire ending ON the sheet sits a distance of order its own radius from
        // the disc's inner region. Both the ray parameter and the touching wire element are
        // panelled geometrically toward the contact, and the honest gate for a new quadrature is
        // that it stops moving — measured to settle by ~10 panels and hold to 1e-13 thereafter.
        var (surface, wire) = Build(0.75 * Lambda, Lambda / 8, degrees: 45);
        double omega = 2 * Math.PI * Frequency;
        double k = omega / RfConstants.SpeedOfLight;
        var fan = WireSurfaceJunction.Attach(wire, surface).Fan;

        foreach (int b in new[] { 0, 1, 4 })
        {
            var coarse = WireSurfaceCoupling.MutualDisc(wire, b, fan, surface, k, omega, 10);
            var fine = WireSurfaceCoupling.MutualDisc(wire, b, fan, surface, k, omega, 14);
            Assert.True(fine.Magnitude > 0, $"basis {b} produced no disc coupling at all");
            Assert.True((fine - coarse).Magnitude <= 1e-9 * fine.Magnitude,
                $"basis {b}: {coarse} vs {fine}");
        }
    }

    // ------------------------------------------------------------------
    // Physics
    // ------------------------------------------------------------------

    [Fact]
    public void MonopoleOnAFinitePlate_ReproducesTheImageTheoryRadiationResistance()
    {
        // THE anchor gate. A broken junction does not land near this band — it lands outside
        // physics entirely: the first assembly of this stage put the attachment disc's current
        // the same way as the wire's own half hat instead of opposite to it, leaving 2δ of point
        // charge at the contact, and it read R = −38 Ω on a structure that is passive by
        // construction. The band is wide because the plate is finite (1λ square): its edges
        // diffract and its own resonance shifts the answer, both real effects that no mesh
        // refinement removes.
        var (surface, wire) = Build(1.0 * Lambda, Lambda / 10);
        var solution = new SurfaceMomSolver().SolveWireAttached(surface, wire, Frequency, feedBasis: 0);
        var reference = InfiniteGroundMonopole();

        Assert.Equal(90.0, solution.IncidenceDegrees, 6);
        Assert.InRange(solution.InputImpedance.Real / reference.Real, 0.85, 1.15);
    }

    [Fact]
    public void RefiningTheSheetMesh_DoesNotMoveTheImpedance()
    {
        // Three meshes at a fixed plate. Neither part may move: the resistance is an integral of
        // the whole current, and the junction's reactance depends on the wire radius and the
        // plate, not on the triangles around the contact. Measured j17.09, j17.14, j17.20.
        // (This test used to require the reactance to FALL monotonically with refinement, from
        // j49.9 toward the reference. That fall was the defect — see the class remarks.)
        var solver = new SurfaceMomSolver();
        var impedances = new List<Complex>();
        foreach (int divisions in new[] { 8, 10, 12 })
        {
            var (surface, wire) = Build(1.0 * Lambda, Lambda / divisions);
            impedances.Add(solver.SolveWireAttached(surface, wire, Frequency, feedBasis: 0)
                .InputImpedance);
        }

        string all = string.Join(", ", impedances.Select(z => z.ToString()));
        double xSpread = impedances.Max(z => z.Imaginary) - impedances.Min(z => z.Imaginary);
        Assert.True(xSpread < 0.5, $"the reactance moved with the mesh by {xSpread:F2} Ω: {all}");
        // The resistance is the stable half: it moves by under 2% across the same refinement.
        double first = impedances[0].Real, last = impedances[^1].Real;
        Assert.True(Math.Abs(last - first) / first < 0.02,
            $"the resistance moved with the mesh: {first:F3} → {last:F3}");
        // And the reactance sits within the finite plate's distance of the image-theory value.
        double referenceX = InfiniteGroundMonopole().Imaginary;
        Assert.InRange(impedances[^1].Imaginary - referenceX, -7.0, 0.0);
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(60.0)]
    [InlineData(45.0)]
    [InlineData(30.0)]
    public void PowerBalance_HoldsAtEveryIncidence(double degrees)
    {
        // The house identity, and the strongest single statement available here: the power
        // radiated through a far-field sphere quadrature — assembled from three currents the
        // solve keeps apart (wire, sheet, junction disc + halves) — equals the power the feed
        // delivers. It is independent of every reference antenna, and it is what the wrong disc
        // sign failed at −5.9. Measured 1.002 to 1.007 and TIGHTENING under mesh refinement
        // (1.00054 at λ/16), which is the signature of a discretization residue rather than a
        // missing term.
        var (surface, wire) = Build(1.0 * Lambda, Lambda / 8, degrees: degrees);
        var solution = new SurfaceMomSolver().SolveWireAttached(surface, wire, Frequency, feedBasis: 0);
        var pattern = WireAttachedFarField.Compute(surface, wire, solution, 24, 48);

        Assert.Equal(degrees, solution.IncidenceDegrees, 6);
        double delivered = 0.5 * (1.0 / solution.InputImpedance).Real;   // ½Re(V·I*) at V = 1
        Assert.True(delivered > 0, $"a passive structure cannot absorb negative power: {solution.InputImpedance}");
        Assert.InRange(pattern.TotalRadiatedPowerWatts / delivered, 0.98, 1.02);
    }

    [Fact]
    public void ATiltedWire_RadiatesByItsProjectedHeight()
    {
        // The oblique attachment's physics check, and the reason the fan is allowed to stay in
        // the sheet plane at any angle. Over a ground plane the image of a tilted wire cancels
        // its horizontal component, so only the vertical projection radiates and the radiation
        // resistance follows sin²θ. Measured 43.49 / 33.83 / 23.09 / 11.55 Ω against
        // sin²θ · 43.49 = 43.49 / 32.62 / 21.75 / 10.87 — within 6.2%, over a plate that is only
        // 1λ across and at a height (λ/4) where the short-monopole projection rule is already
        // stretched, so it is banded at 15% rather than claimed as an identity.
        var solver = new SurfaceMomSolver();
        double reference = 0;
        foreach (double degrees in new[] { 90.0, 60.0, 45.0, 30.0 })
        {
            var (surface, wire) = Build(1.0 * Lambda, Lambda / 8, degrees: degrees);
            double r = solver.SolveWireAttached(surface, wire, Frequency, feedBasis: 0)
                .InputImpedance.Real;
            if (degrees == 90.0) { reference = r; continue; }

            double sin = Math.Sin(degrees * Math.PI / 180);
            Assert.InRange(r / (reference * sin * sin), 0.85, 1.15);
        }
    }

    [Fact]
    public void DrawingTheWireTheOtherWayRound_ChangesNothing()
    {
        // Both branches of DiscSign in one statement. A wire drawn plate→tip attaches at its
        // START and its half hat falls AWAY from the contact (sign −1); drawn tip→plate it
        // attaches at its END and the half hat rises INTO the contact (sign +1, the coaxial
        // probe's case). The attachment basis also moves from index 0 to index BasisCount−1, so
        // the index map that folds it onto the junction column is exercised from both ends.
        // Physically it is one antenna, and the direction a segment was typed in is not physics —
        // which is exactly why the wrong sign was invisible until a passivity check caught it.
        var plate = SurfaceMeshBuilder.BuildRectangularPlate(0.75 * Lambda, 0.75 * Lambda,
            Lambda / 8, z: 0, portFraction: 0, snapVertex: (0, 0));
        Assert.True(plate.Structure is not null, plate.FailureReason);

        // Both node lists by hand rather than through WireGridBuilder: the builder CANONICALIZES
        // an open run's direction, so handing it the reversed segment gives back the identical
        // ordering and the end-attached case would never be reached. (Measured — the first
        // version of this test asserted EndGrounded and failed there, before any solve.)
        const int segments = 10;
        var up = new List<Vector3D>();
        for (int i = 0; i <= segments; i++)
            up.Add(new Vector3D(0, 0, Height * i / segments));
        var down = new List<Vector3D>(up);
        down.Reverse();
        var radii = Enumerable.Repeat(Radius, segments).ToList();

        var forward = new WireStructure(up, radii, isLoop: false, ground: null,
            startGrounded: true, endGrounded: false);
        var reversed = new WireStructure(down, radii, isLoop: false, ground: null,
            startGrounded: false, endGrounded: true);
        Assert.Equal(forward.BasisCount, reversed.BasisCount);
        Assert.Equal(0, forward.BasisNode(0));
        Assert.Equal(down.Count - 1, reversed.BasisNode(reversed.BasisCount - 1));

        var solver = new SurfaceMomSolver();
        var a = solver.SolveWireAttached(plate.Structure!, forward, Frequency, feedBasis: 0);
        var b = solver.SolveWireAttached(plate.Structure!, reversed, Frequency,
            feedBasis: reversed.BasisCount - 1);

        // Not bitwise, and not close to it: the wire's bases and elements are numbered the other
        // way, so every mixed block accumulates in a different order and the dense LU pivots
        // differently. Measured 3.9e-10 relative (38.821996326908 + j66.025350362917 against
        // 38.821996326203 + j66.025350333039), banded at 1e-8 for 25× headroom. A sign error
        // here is O(1) — the wrong DiscSign read −38 − j57 against +43 + j50 — so the margin
        // costs nothing.
        Assert.True((a.InputImpedance - b.InputImpedance).Magnitude
            <= 1e-8 * a.InputImpedance.Magnitude,
            $"{a.InputImpedance} vs {b.InputImpedance}");
    }

    // ------------------------------------------------------------------
    // Typed refusals
    // ------------------------------------------------------------------

    [Fact]
    public void AGroundedSheetOrWire_IsATypedFailure()
    {
        // The hybrid is free space. An image pass would need the junction disc's own imaged
        // kernel, which is a derivation, not a flag — so it is named rather than approximated.
        var (surface, wire) = Build(0.5 * Lambda, Lambda / 6);
        var solver = new SurfaceMomSolver();

        var grounded = SurfaceMeshBuilder.BuildRectangularPlate(0.5 * Lambda, 0.5 * Lambda,
            Lambda / 6, z: 0.05, portFraction: 0, ground: new GroundPlane(0), snapVertex: (0, 0));
        Assert.NotNull(grounded.Structure);
        var e1 = Assert.Throws<ArgumentException>(
            () => solver.SolveWireAttached(grounded.Structure!, wire, Frequency, 0));
        Assert.Contains("FREE SPACE", e1.Message);

        var groundedWire = WireGridBuilder.Build(CanonicalAntennas.Monopole(Height, Radius),
            maxElementLength: Height / 10, ground: new GroundPlane(0));
        Assert.NotNull(groundedWire.Structure);
        var e2 = Assert.Throws<ArgumentException>(
            () => solver.SolveWireAttached(surface, groundedWire.Structure!, Frequency, 0));
        Assert.Contains("FREE SPACE", e2.Message);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => solver.SolveWireAttached(surface, wire, Frequency, wire.BasisCount));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => solver.SolveWireAttached(surface, wire, 0, 0));
    }

    [Fact]
    public void TheBuilder_RefusesAnAttachmentThatIsNotAWireEnd()
    {
        // A guessed contact is a wrong placement, not an approximate one — the same rule the
        // STEP assembly resolver applies to a malformed NAUO chain.
        var midway = WireGridBuilder.Build(
            new[] { new WireSegment(Vector3D.Zero, new Vector3D(0, 0, Height), Radius) },
            maxElementLength: Height / 10,
            attachmentPoint: new Vector3D(0, 0, Height / 2));
        Assert.Null(midway.Structure);
        Assert.Contains("not a wire END", midway.FailureReason);

        var both = WireGridBuilder.Build(
            new[] { new WireSegment(Vector3D.Zero, new Vector3D(0, 0, Height), Radius) },
            maxElementLength: Height / 10, ground: new GroundPlane(0),
            attachmentPoint: Vector3D.Zero);
        Assert.Null(both.Structure);
        Assert.Contains("free space", both.FailureReason);
    }

    [Fact]
    public void AttachmentFeedBasis_NamesTheHalfHatBasis()
    {
        // The App needs the "fed at the contact" index without reaching into the junction, which
        // is internal. This is the entry it uses, so it is gated: it must name the basis with a
        // SINGLE leg (the attachment half hat), and solving through it must reproduce the solve
        // the D1 gates pin with an explicit index.
        var (surface, wire) = Build(1.0 * Lambda, Lambda / 8);
        int feed = SurfaceMomSolver.AttachmentFeedBasis(surface, wire);
        Assert.Single(wire.BasisHalves(feed));
        for (int b = 0; b < wire.BasisCount; b++)
            if (b != feed) Assert.Equal(2, wire.BasisHalves(b).Count);

        var solver = new SurfaceMomSolver();
        var byIndex = solver.SolveWireAttached(surface, wire, Frequency, feedBasis: feed);
        var pinned = solver.SolveWireAttached(surface, wire, Frequency, feedBasis: 0);
        Assert.Equal(pinned.InputImpedance.Real, byIndex.InputImpedance.Real);
        Assert.Equal(pinned.InputImpedance.Imaginary, byIndex.InputImpedance.Imaginary);
    }

    [Fact]
    public void ABranchedWire_StandsOnAPlate_AndItsTopHatLoadsIt()
    {
        // D1 x D2 composed: a top-hat monopole on a finite plate. The wire is BRANCHED (a T at
        // its far end) and ATTACHED at its other end, so the junction machinery and the
        // multi-wire bases meet in one structure for the first time.
        //
        // Two statements, both decisive. The power ledger is the house identity that caught the
        // disc sign at -5.9, and it must still hold with three wire currents meeting at a node.
        // The top hat must make the short monopole LESS capacitive - a sign-decisive classical
        // fact that a mis-signed junction leg would reverse.
        double stem = 0.12 * Lambda, hat = 0.10 * Lambda;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(1.0 * Lambda, 1.0 * Lambda, Lambda / 8,
            z: 0, portFraction: 0, snapVertex: (0, 0));
        Assert.True(grid.Structure is not null, grid.FailureReason);
        var surface = grid.Structure!;

        var plainGrid = WireGridBuilder.Build(
            new[] { new WireSegment(Vector3D.Zero, new Vector3D(0, 0, stem), Radius) },
            maxElementLength: Lambda / 40, attachmentPoint: Vector3D.Zero);
        Assert.True(plainGrid.Structure is not null, plainGrid.FailureReason);

        var hattedGrid = WireGridBuilder.Build(new[]
        {
            new WireSegment(Vector3D.Zero, new Vector3D(0, 0, stem), Radius),
            new WireSegment(new Vector3D(0, 0, stem), new Vector3D(hat, 0, stem), Radius),
            new WireSegment(new Vector3D(0, 0, stem), new Vector3D(-hat, 0, stem), Radius)
        }, maxElementLength: Lambda / 40, attachmentPoint: Vector3D.Zero);
        Assert.True(hattedGrid.Structure is not null, hattedGrid.FailureReason);
        Assert.True(hattedGrid.Structure!.IsBranched);

        var solver = new SurfaceMomSolver();
        var plain = solver.SolveWireAttached(surface, plainGrid.Structure!, Frequency,
            SurfaceMomSolver.AttachmentFeedBasis(surface, plainGrid.Structure!));
        var hatted = solver.SolveWireAttached(surface, hattedGrid.Structure, Frequency,
            SurfaceMomSolver.AttachmentFeedBasis(surface, hattedGrid.Structure));

        var pattern = WireAttachedFarField.Compute(surface, hattedGrid.Structure, hatted, 24, 48);
        double delivered = 0.5 * (1.0 / hatted.InputImpedance).Real;
        Assert.True(delivered > 0,
            $"a passive structure cannot absorb negative power: {hatted.InputImpedance}");
        Assert.InRange(pattern.TotalRadiatedPowerWatts / delivered, 0.98, 1.02);

        Assert.True(plain.InputImpedance.Imaginary < 0,
            $"a {stem / Lambda:g3}-wavelength monopole is capacitive; got "
            + $"X = {plain.InputImpedance.Imaginary:g4} Ohm");
        Assert.True(hatted.InputImpedance.Imaginary > plain.InputImpedance.Imaginary,
            $"the top hat must reduce the capacitive reactance: plain "
            + $"{plain.InputImpedance.Imaginary:g4} Ohm, hatted {hatted.InputImpedance.Imaginary:g4} Ohm");
        Assert.True(hatted.InputImpedance.Real > plain.InputImpedance.Real,
            $"the hat raises the base current, so R rises: plain {plain.InputImpedance.Real:g4} Ohm, "
            + $"hatted {hatted.InputImpedance.Real:g4} Ohm");
    }
}
