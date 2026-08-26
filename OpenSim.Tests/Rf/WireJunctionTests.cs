using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage D2 — multi-wire junctions. Three or more wires meeting at a node used to be a typed
/// failure; they now carry (degree − 1) unknowns each pairing one incident element against a
/// common reference, so Kirchhoff's current law is an IDENTITY of the basis set rather than a
/// constraint bolted on afterwards.
///
/// <para>What is gated as an identity and what as physics:</para>
/// <list type="bullet">
///   <item><b>Identity.</b> A chain forced through the branched construction reproduces the
///     ordered construction's impedance matrix BITWISE — the pin on the refactor. Kirchhoff at
///     a junction is exact in IEEE for arbitrary coefficients.</item>
///   <item><b>Symmetry.</b> A mirror-symmetric T carries mirror-equal arm currents to ~1e-12:
///     the two arms are assembled from DIFFERENT element pairs, so this checks the junction
///     bookkeeping rather than a shared code path.</item>
///   <item><b>Physics.</b> The house power ledger P_rad(sphere) ≡ ½Re(V·I*) at 2%, and the
///     classical capacitive loading of a top hat (a sign-decisive statement — a junction sign
///     error would move the reactance the wrong way).</item>
/// </list>
/// </summary>
public class WireJunctionTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;
    private static readonly double WireRadius = Lambda / 2000;

    private static WireStructure Build(IReadOnlyList<WireSegment> segments, double maxElement,
        GroundPlane? ground = null)
    {
        var result = WireGridBuilder.Build(segments, maxElement, ground: ground);
        Assert.True(result.Structure is not null, result.FailureReason);
        return result.Structure!;
    }

    /// <summary>A symmetric T: a crossbar along y through the origin, and a stem along +x.
    /// The junction sits at the origin, where three elements meet.</summary>
    private static IReadOnlyList<WireSegment> Tee(double armLength, double stemLength) => new[]
    {
        new WireSegment(new Vector3D(0, -armLength, 0), new Vector3D(0, 0, 0), WireRadius),
        new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0, armLength, 0), WireRadius),
        new WireSegment(new Vector3D(0, 0, 0), new Vector3D(stemLength, 0, 0), WireRadius)
    };

    // ------------------------------------------------------------------
    // The refactor pin: the general path IS the ordered path on a chain.
    // ------------------------------------------------------------------

    [Fact]
    public void AChainThroughTheBranchedPath_IsBitwiseTheOrderedPath()
    {
        // Every pre-existing structure keeps the ordered constructor, so nothing shipped can
        // move. This gate proves the general machinery agrees where both apply — and BITWISE,
        // not nearly: the branched path emits a degree-2 node's two legs in the same order
        // (arriving element first) with the same +1 coefficients, so the assembly accumulates
        // the identical addends in the identical sequence. Anything less than bitwise here
        // would mean the junction bases are not the rooftop in disguise.
        var segments = new[]
        {
            new WireSegment(new Vector3D(-0.2, 0, 0), new Vector3D(0, 0, 0), WireRadius),
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0.1, 0.15, 0), WireRadius)
        };
        double k = 2 * Math.PI / Lambda, omega = k * 299_792_458.0;

        var ordered = Build(segments, Lambda / 20);
        var general = GeneralBuild(segments, Lambda / 20);
        Assert.False(ordered.IsBranched);
        Assert.True(general.IsBranched);
        Assert.Equal(ordered.BasisCount, general.BasisCount);
        Assert.Equal(ordered.ElementCount, general.ElementCount);

        var a = ThinWireMomSolver.AssembleImpedanceMatrix(ordered, k, omega);
        var b = ThinWireMomSolver.AssembleImpedanceMatrix(general, k, omega);
        for (int i = 0; i < ordered.BasisCount; i++)
            for (int j = 0; j < ordered.BasisCount; j++)
            {
                Assert.Equal(a[i, j].Real, b[i, j].Real);
                Assert.Equal(a[i, j].Imaginary, b[i, j].Imaginary);
            }
    }

    [Fact]
    public void AGroundedChainThroughTheBranchedPath_GivesTheSameImpedance()
    {
        // The grounded case cannot be bitwise: the anchored end's basis is numbered by node
        // index, and the branched path numbers the special (end) nodes FIRST, so the two
        // matrices are a permutation of one another. Zin is permutation-invariant, which is
        // exactly the right thing to compare.
        var segments = CanonicalAntennas.Monopole(0.25 * Lambda, WireRadius);
        var ground = new GroundPlane(0);
        var ordered = Build(segments, Lambda / 20, ground);
        var general = GeneralBuild(segments, Lambda / 20, ground);

        var solver = new ThinWireMomSolver();
        var a = solver.Solve(ordered, Frequency, ordered.NearestBasis(new Vector3D(0, 0, 0)));
        var b = solver.Solve(general, Frequency, general.NearestBasis(new Vector3D(0, 0, 0)));
        Assert.Equal(a.InputImpedance.Real, b.InputImpedance.Real, 12);
        Assert.Equal(a.InputImpedance.Imaginary, b.InputImpedance.Imaginary, 12);
    }

    // ------------------------------------------------------------------
    // Kirchhoff — an identity of the basis set, not a constraint
    // ------------------------------------------------------------------

    [Fact]
    public void EveryJunctionBasis_ConservesCurrentExactly()
    {
        // Per unit coefficient a leg delivers σ = +1 of current into the node when the node is
        // the element's B end and −1 when it is the A end. A junction basis carries (−σ_k, +σ_ref)
        // on its two legs, so the inflow sums to EXACTLY zero in IEEE — the ±1s cancel with no
        // rounding to hide behind.
        var wire = Build(Tee(0.25 * Lambda, 0.05 * Lambda), Lambda / 20);
        Assert.True(wire.IsBranched);

        for (int b = 0; b < wire.BasisCount; b++)
        {
            var halves = wire.BasisHalves(b);
            int node = wire.BasisNode(b);
            double inflow = 0;
            foreach (var half in halves)
            {
                Assert.Equal(node, half.Rising ? wire.Elements[half.Element].B
                                               : wire.Elements[half.Element].A);
                inflow += half.Sign * (half.Rising ? 1.0 : -1.0);
            }
            Assert.Equal(0.0, inflow);          // exact, not approximate
        }
    }

    [Fact]
    public void AJunctionNodeCarriesOneFewerUnknownThanItsDegree()
    {
        var wire = Build(Tee(0.25 * Lambda, 0.05 * Lambda), Lambda / 20);
        int junction = JunctionNode(wire);

        int degree = 0;
        foreach (var (a, b) in wire.Elements) if (a == junction || b == junction) degree++;
        Assert.Equal(3, degree);

        int bases = 0;
        for (int b = 0; b < wire.BasisCount; b++) if (wire.BasisNode(b) == junction) bases++;
        Assert.Equal(degree - 1, bases);
    }

    // ------------------------------------------------------------------
    // Symmetry
    // ------------------------------------------------------------------

    [Fact]
    public void AMirrorSymmetricTee_CarriesMirrorEqualArmCurrents()
    {
        // Feed the STEM, so structure AND excitation are symmetric about the y = 0 plane. The
        // two crossbar arms are then assembled from DIFFERENT element pairs and reached through
        // DIFFERENT junction bases, so their agreement is a genuine check on the junction
        // bookkeeping rather than a shared-code tautology.
        double arm = 0.2 * Lambda, stem = 0.15 * Lambda;
        var wire = Build(Tee(arm, stem), Lambda / 24);
        var solution = new ThinWireMomSolver().Solve(
            wire, Frequency, wire.NearestBasis(new Vector3D(stem / 2, 0, 0)));

        int junction = JunctionNode(wire);

        // (1) EXACT: Kirchhoff on the SOLVED currents. Not "small" — the legs deliver ±1 per unit
        // coefficient, so the outflows cancel bit for bit whatever the solution turned out to be.
        var arms = JunctionOutflows(wire, solution, junction);
        Assert.Equal(3, arms.Length);
        double armScale = arms.Max(c => c.Magnitude);
        Assert.Equal(0.0, arms.Aggregate(Complex.Zero, (s, c) => s + c).Magnitude);

        // (2) MACHINE: mirror-congruent element pairs at the junction produce equal moments, so
        // the geometry and the quadrature that feed the two arms really are symmetric.
        var incident = new List<int>();
        for (int e = 0; e < wire.ElementCount; e++)
        {
            var (a, b) = wire.Elements[e];
            if (a == junction || b == junction) incident.Add(e);
        }
        double k0 = 2 * Math.PI / Lambda;
        var armStem = ThinWireMomSolver.PairMoments(
            wire, Math.Min(incident[0], incident[2]), Math.Max(incident[0], incident[2]), k0);
        var mirrorStem = ThinWireMomSolver.PairMoments(
            wire, Math.Min(incident[1], incident[2]), Math.Max(incident[1], incident[2]), k0);
        Assert.True((armStem.M00 - mirrorStem.M00).Magnitude / armStem.M00.Magnitude < 1e-14,
            $"mirror-congruent pair moments differ: {armStem.M00} vs {mirrorStem.M00}");

        // (3) The two crossbar arms carry mirror-equal current, and the pattern is mirror-
        // symmetric. Both are gated at 1e-7 rather than at machine precision, and the reason is
        // MEASURED rather than assumed: elements separated by exactly TWO element lengths sit
        // exactly ON the solver's near/far quadrature threshold (separation and threshold agree
        // to three ulps here), so the last bits decide which rule runs — and they decide
        // differently for the two arms, whose elements are numbered in opposite senses. The two
        // rules agree to about 1e-9 there, which is exactly what shows up: arm currents 7.9e-10
        // apart and, since intensity is quadratic in current, 1.6e-9 in the pattern. This is a
        // pre-existing property of any regime split, not something a junction introduced — a
        // junction bookkeeping error would be O(1), nine orders above the gate.
        var crossbar = arms.OrderBy(c => c.Magnitude).Take(2).ToArray();
        Assert.True((crossbar[0] - crossbar[1]).Magnitude / armScale < 1e-7,
            $"crossbar arms should carry mirror-equal current: {crossbar[0]} vs {crossbar[1]}");

        const int PhiCount = 48;
        var pattern = FarFieldEvaluator.Compute(wire, solution, 24, PhiCount);
        double peak = 0;
        for (int i = 0; i < pattern.ThetaRadians.Count; i++)
            for (int j = 0; j < PhiCount; j++)
                peak = Math.Max(peak, pattern.IntensityWattsPerSteradian[i, j]);
        Assert.True(peak > 0, "the T should radiate");
        for (int i = 0; i < pattern.ThetaRadians.Count; i++)
            for (int j = 0; j < PhiCount; j++)
            {
                double here = pattern.IntensityWattsPerSteradian[i, j];
                double mirrored = pattern.IntensityWattsPerSteradian[i, (PhiCount - j) % PhiCount];
                Assert.True(Math.Abs(here - mirrored) / peak < 1e-7,
                    $"θ index {i}, φ index {j}: {here:g6} vs mirrored {mirrored:g6}");
            }
    }

    // ------------------------------------------------------------------
    // Physics
    // ------------------------------------------------------------------

    [Fact]
    public void PowerBalance_HoldsOnABranchedStructure()
    {
        // The house identity: the far-field sphere integral of a structure whose current now
        // splits three ways at a node equals the power the feed delivers. It exercises the
        // generalized radiation vector (per-element endpoint currents) as much as the assembly.
        double arm = 0.2 * Lambda, stem = 0.15 * Lambda;
        var wire = Build(Tee(arm, stem), Lambda / 24);
        var solution = new ThinWireMomSolver().Solve(
            wire, Frequency, wire.NearestBasis(new Vector3D(stem / 2, 0, 0)));
        var pattern = FarFieldEvaluator.Compute(wire, solution, 48, 96);

        double delivered = 0.5 * (1.0 / solution.InputImpedance).Real;
        Assert.True(delivered > 0,
            $"a passive structure cannot absorb negative power: {solution.InputImpedance}");
        Assert.InRange(pattern.TotalRadiatedPowerWatts / delivered, 0.98, 1.02);
    }

    [Fact]
    public void ATopHatMakesAShortMonopoleLessCapacitive()
    {
        // The classical reason top hats exist: the hat adds end capacitance, so a short
        // monopole's large negative reactance shrinks. It is sign-decisive — a flipped junction
        // leg would move X the wrong way — and needs no reference number, only the ordering.
        double height = 0.06 * Lambda, hat = 0.05 * Lambda;
        var ground = new GroundPlane(0);
        var plain = Build(CanonicalAntennas.Monopole(height, WireRadius), Lambda / 40, ground);
        var hatted = Build(new[]
        {
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0, 0, height), WireRadius),
            new WireSegment(new Vector3D(0, 0, height), new Vector3D(hat, 0, height), WireRadius),
            new WireSegment(new Vector3D(0, 0, height), new Vector3D(-hat, 0, height), WireRadius)
        }, Lambda / 40, ground);
        Assert.True(hatted.IsBranched);

        var solver = new ThinWireMomSolver();
        var a = solver.Solve(plain, Frequency, plain.NearestBasis(new Vector3D(0, 0, 0)));
        var b = solver.Solve(hatted, Frequency, hatted.NearestBasis(new Vector3D(0, 0, 0)));

        Assert.True(a.InputImpedance.Imaginary < 0,
            $"a short monopole is capacitive; got X = {a.InputImpedance.Imaginary:g4} Ω");
        Assert.True(b.InputImpedance.Imaginary > a.InputImpedance.Imaginary,
            $"the top hat must reduce the capacitive reactance: plain {a.InputImpedance.Imaginary:g4} Ω, "
            + $"hatted {b.InputImpedance.Imaginary:g4} Ω");
        Assert.True(b.InputImpedance.Real > a.InputImpedance.Real,
            $"the hat raises the current at the base, so R rises too: plain {a.InputImpedance.Real:g4} Ω, "
            + $"hatted {b.InputImpedance.Real:g4} Ω");
    }

    [Fact]
    public void AVanishingStub_ConvergesToThePlainDipole()
    {
        // A T whose stem shrinks becomes a straight dipole. There is no closed form for the
        // approach, so the gate is the TREND: monotone convergence of Zin toward the plain
        // dipole as the stub shortens, which is what a correct junction must produce and a
        // mis-signed one cannot.
        double arm = 0.25 * Lambda;
        var dipole = Build(new[]
        {
            new WireSegment(new Vector3D(0, -arm, 0), new Vector3D(0, 0, 0), WireRadius),
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0, arm, 0), WireRadius)
        }, Lambda / 40);
        var solver = new ThinWireMomSolver();
        var reference = solver.Solve(dipole, Frequency, dipole.NearestBasis(new Vector3D(0, 0, 0)));

        double previous = double.MaxValue;
        foreach (double stub in new[] { Lambda / 40, Lambda / 80, Lambda / 160 })
        {
            var wire = Build(Tee(arm, stub), Lambda / 40);
            var solution = solver.Solve(wire, Frequency, wire.NearestBasis(new Vector3D(0, 0, 0)));
            double error = (solution.InputImpedance - reference.InputImpedance).Magnitude
                / reference.InputImpedance.Magnitude;
            Assert.True(error < previous,
                $"stub {stub:g3} m: relative Zin error {error:g3} did not improve on {previous:g3}");
            previous = error;
        }
        Assert.True(previous < 0.15,
            $"the shortest stub should sit close to the plain dipole; relative error {previous:g3}");
    }

    // ------------------------------------------------------------------
    // Typed failures
    // ------------------------------------------------------------------

    [Fact]
    public void AGroundedJunctionIsATypedFailure()
    {
        var result = WireGridBuilder.Build(new[]
        {
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0, 0, 0.2 * Lambda), WireRadius),
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0.2 * Lambda, 0, 0.2 * Lambda), WireRadius),
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(-0.2 * Lambda, 0, 0.2 * Lambda), WireRadius)
        }, Lambda / 20, ground: new GroundPlane(0));
        Assert.Null(result.Structure);
        Assert.Contains("grounded multi-wire junction", result.FailureReason);
    }

    [Fact]
    public void DisconnectedBranchedPiecesAreATypedFailure()
    {
        var result = WireGridBuilder.Build(new[]
        {
            new WireSegment(new Vector3D(0, -0.1, 0), new Vector3D(0, 0, 0), WireRadius),
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0, 0.1, 0), WireRadius),
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0.1, 0, 0), WireRadius),
            new WireSegment(new Vector3D(5, 5, 5), new Vector3D(5, 5.1, 5), WireRadius)
        }, Lambda / 20);
        Assert.Null(result.Structure);
        Assert.Contains("disconnected", result.FailureReason);
    }

    [Fact]
    public void AttachingASheetAtAJunctionIsATypedFailure()
    {
        var result = WireGridBuilder.Build(Tee(0.2 * Lambda, 0.1 * Lambda), Lambda / 20,
            attachmentPoint: new Vector3D(0, 0, 0));
        Assert.Null(result.Structure);
        Assert.Contains("free END", result.FailureReason);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static WireStructure GeneralBuild(IReadOnlyList<WireSegment> segments,
        double maxElement, GroundPlane? ground = null)
    {
        var result = WireGridBuilder.BuildForcingGeneralTopology(segments, maxElement, ground: ground);
        Assert.True(result.Structure is not null, result.FailureReason);
        return result.Structure!;
    }

    /// <summary>The current LEAVING a node along each incident element, taken from the solved
    /// element-end currents: at the element's A node the end current already flows away along the
    /// element, at its B node it flows in, so the sign flips. Their sum is Kirchhoff's law read
    /// off the solution rather than off the basis definitions.</summary>
    private static Complex[] JunctionOutflows(WireStructure wire, MomSolution solution, int node)
    {
        var ends = FarFieldEvaluator.ElementEndCurrents(wire, solution);
        var outflows = new List<Complex>();
        for (int e = 0; e < wire.ElementCount; e++)
        {
            var (a, b) = wire.Elements[e];
            if (a == node) outflows.Add(ends[e].A);
            else if (b == node) outflows.Add(-ends[e].B);
        }
        return outflows.ToArray();
    }

    /// <summary>The node where the T's three elements meet — the origin, by construction.</summary>
    private static int JunctionNode(WireStructure wire)
    {
        for (int v = 0; v < wire.Nodes.Count; v++)
            if (wire.Nodes[v].Length < 1e-15) return v;
        Assert.Fail("the fixture should place its junction at the origin");
        return -1;
    }
}
