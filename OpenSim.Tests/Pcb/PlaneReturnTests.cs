using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class PlaneReturnTests
{
    private const double L = 100e-3, H = 1e-3, R = 0.1e-3;

    private static TraceSegment3D Wire(double z, bool reversed = false) => reversed
        ? new TraceSegment3D(new Vector3D(L, 0, z), new Vector3D(0, 0, z), 2 * R, 0, SegmentProfile.RoundWire)
        : new TraceSegment3D(new Vector3D(0, 0, z), new Vector3D(L, 0, z), 2 * R, 0, SegmentProfile.RoundWire);

    // ------------------------------------------------------------------
    // The load-bearing identity, three ways at 1e-12: for a single wire at height h,
    //   PlaneReturn = ½ · Compose([wire, anti-parallel image at 2h])   (½-pair flux)
    //               = L_self − M(parallel at 2h)                        (direct algebra)
    // This is what pins the composition as L_chain + M(chain, image), NOT the pair
    // formula L_A + L_img − 2M (which is exactly twice the plane loop).
    // ------------------------------------------------------------------
    [Fact]
    public void SingleWireOverPlane_MatchesTheHalfPairAndDirectIdentities()
    {
        var chain = new[] { Wire(H) };
        var report = new PlaneReturnComposer().Compose(chain, planeSurfaceZ: 0);
        Assert.Null(report.FailureReason);
        double plane = report.LoopInductanceHenries!.Value;

        double pair = new LoopComposer().Compose(new[] { Wire(H), Wire(-H, reversed: true) })
            .LoopInductance;
        Assert.Equal(0.5 * pair, plane, Math.Abs(plane) * 1e-12);

        double direct = PartialInductance.RoundWireSelfInductance(L, R)
                        - FilamentMutual.Between(
                            new Vector3D(0, 0, H), new Vector3D(L, 0, H),
                            new Vector3D(0, 0, -H), new Vector3D(L, 0, -H));
        Assert.Equal(direct, plane, Math.Abs(plane) * 1e-12);
    }

    [Fact]
    public void SingleWireOverPlane_MatchesTheClassicPerLengthValue()
    {
        // Infinite-length physics: L/len = (µ₀/2π)·ln(2h/r) ≈ 599 nH/m. The finite wire
        // sits ABOVE it (uncancelled end terms in the self-inductance shrink like
        // ln(l)/l); a one-sided band, benchmark style — do not widen.
        var report = new PlaneReturnComposer().Compose(new[] { Wire(H) }, planeSurfaceZ: 0);
        double perLength = report.LoopInductanceHenries!.Value / L;
        double classic = 2e-7 * Math.Log(2 * H / R);
        Assert.InRange(perLength / classic, 1.0, 1.15);
    }

    [Fact]
    public void ChainTouchingOrCrossingThePlane_FailsTyped()
    {
        var touching = new PlaneReturnComposer().Compose(new[] { Wire(0) }, planeSurfaceZ: 0);
        Assert.Null(touching.LoopInductanceHenries);
        Assert.Contains("touches or crosses", touching.FailureReason);

        // A two-layer chain with the plane between its layers crosses it too.
        var spanning = new[]
        {
            Wire(H),
            new TraceSegment3D(new Vector3D(L, 0, H), new Vector3D(L, 0, -H),
                2 * R, 25e-6, SegmentProfile.RoundTube)
        };
        var crossed = new PlaneReturnComposer().Compose(spanning, planeSurfaceZ: 0);
        Assert.Null(crossed.LoopInductanceHenries);
        Assert.Contains("touches or crosses", crossed.FailureReason);
    }

    [Fact]
    public void PlaneReturn_IsDeterministic_AndBelowThePartialInductance()
    {
        var chain = new[] { Wire(H) };
        var composer = new PlaneReturnComposer();
        double first = composer.Compose(chain, 0).LoopInductanceHenries!.Value;
        Assert.Equal(first, composer.Compose(chain, 0).LoopInductanceHenries!.Value);
        Assert.True(first < PartialInductance.RoundWireSelfInductance(L, R),
            "The image return must reduce the loop below the wire's partial inductance.");
        Assert.True(first > 0);
    }

    // ------------------------------------------------------------------
    // Rectangular traces over a near plane — the case the small-section GMD expansion
    // got wrong, then NEGATIVE, once the trace was wider than about twice its height.
    // ------------------------------------------------------------------

    private const double Copper = 35e-6;

    /// <summary>A straight trace along x, its lower face <paramref name="height"/> above
    /// the plane surface z = 0.</summary>
    private static TraceSegment3D Trace(double length, double width, double height) =>
        new(new Vector3D(0, 0, height + Copper / 2), new Vector3D(length, 0, height + Copper / 2),
            width, Copper);

    private static double PlaneLoop(double length, double width, double height)
    {
        var report = new PlaneReturnComposer().Compose(new[] { Trace(length, width, height) }, planeSurfaceZ: 0);
        Assert.Null(report.FailureReason);
        return report.LoopInductanceHenries!.Value;
    }

    /// <summary>
    /// The same finite geometry from the exact Neumann kernel, sharing nothing with the
    /// bar kernel: trace and image sections are cut into near-square cells two deep
    /// (t/2 on a side), each carrying 2 × 2 Gauss filaments, and every filament pair is
    /// <see cref="FilamentMutual"/>. Filaments cannot resolve a cell against ITSELF or
    /// a cell it touches (the logarithm sits on the shared edge), and thin cells cannot
    /// resolve their thin neighbours at all — a plain 20 × 4 grid is 2 % out for a 4 mm
    /// trace. So the cells are square, and the touching terms come from the exact-GMD
    /// slender series through the subdivision identity: two equal cells side by side
    /// ARE a bar of twice the width, L(2w) = ½·(L(w) + M), so M = 2·L(2w) − L(w);
    /// likewise stacked, and the diagonal neighbour from the 2 × 2 block. Every such
    /// block is at least 285 times longer than it is wide, where the series is exact
    /// to better than 1e-8. Loop = L(trace) − M(trace, image).
    /// </summary>
    private static double SubFilamentPlaneLoop(double length, double width, double height)
    {
        const int through = 2;
        int across = Math.Max(2, (int)Math.Round(width / (Copper / through)));
        int cells = across * through;
        var (nodes, weights) = RectangularBarKernelTests.GaussLegendre(2);
        double cellWidth = width / across, cellThickness = Copper / through;

        double cellSelf = RectangularBarKernelTests.SlenderSeries(length, cellWidth, cellThickness);
        double beside = 2 * RectangularBarKernelTests.SlenderSeries(length, 2 * cellWidth, cellThickness) - cellSelf;
        double stacked = 2 * RectangularBarKernelTests.SlenderSeries(length, cellWidth, 2 * cellThickness) - cellSelf;
        double diagonal = 4 * RectangularBarKernelTests.SlenderSeries(length, 2 * cellWidth, 2 * cellThickness)
                          - cellSelf - beside - stacked;

        var filaments = new List<(double Y, double Z, double Weight, int I, int J)>();
        for (int i = 0; i < across; i++)
            for (int j = 0; j < through; j++)
                for (int p = 0; p < nodes.Length; p++)
                    for (int q = 0; q < nodes.Length; q++)
                        filaments.Add((
                            -width / 2 + cellWidth * (i + 0.5 * (1 + nodes[p])),
                            height + cellThickness * (j + 0.5 * (1 + nodes[q])),
                            0.25 * weights[p] * weights[q] / cells, i, j));

        // Touching cell pairs (ordered), each weighted 1/cells².
        double self = 0;
        for (int i = 0; i < across; i++)
            for (int j = 0; j < through; j++)
                for (int di = -1; di <= 1; di++)
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        if (i + di < 0 || i + di >= across || j + dj < 0 || j + dj >= through) continue;
                        self += (di == 0 && dj == 0 ? cellSelf
                                : dj == 0 ? beside
                                : di == 0 ? stacked
                                : diagonal) / ((double)cells * cells);
                    }

        double image = 0;
        foreach (var a in filaments)
        {
            var a1 = new Vector3D(0, a.Y, a.Z);
            var a2 = new Vector3D(length, a.Y, a.Z);
            foreach (var b in filaments)
            {
                if (Math.Abs(a.I - b.I) > 1 || Math.Abs(a.J - b.J) > 1)
                    self += a.Weight * b.Weight * FilamentMutual.Between(a1, a2,
                        new Vector3D(0, b.Y, b.Z), new Vector3D(length, b.Y, b.Z));
                image += a.Weight * b.Weight * FilamentMutual.Between(a1, a2,
                    new Vector3D(0, b.Y, -b.Z), new Vector3D(length, b.Y, -b.Z));
            }
        }
        return self - image;
    }

    public static TheoryData<double, double, double> TracesOverAPlane()
    {
        var cases = new TheoryData<double, double, double>();
        foreach (double length in new[] { 10e-3, 100e-3 })
            foreach (double height in new[] { 0.1e-3, 0.2e-3 })
                foreach (double widthOverHeight in new[] { 1.0, 5.0, 10.0, 20.0 })
                    cases.Add(length, widthOverHeight * height, height);
        return cases;
    }

    [Theory]
    [MemberData(nameof(TracesOverAPlane))]
    public void TraceOverPlane_MatchesTheSubFilamentNeumannQuadrature(double length, double width, double height)
    {
        double composed = PlaneLoop(length, width, height);
        double reference = SubFilamentPlaneLoop(length, width, height);
        Assert.True(composed > 0);
        Assert.True(Math.Abs(composed / reference - 1) < 1e-4,
            $"l = {length * 1e3:g} mm, w = {width * 1e3:g} mm, h = {height * 1e3:g} mm: " +
            $"composed {composed * 1e9:g8} nH vs sub-filament {reference * 1e9:g8} nH " +
            $"({composed / reference - 1:e2})");
    }

    /// <summary>Hammerstad–Jensen zero-thickness microstrip impedance in air, as a
    /// per-length inductance L' = Z₀/c [H/m] (εr = 1: the TEM line's L is geometry only).</summary>
    private static double HammerstadJensenPerLength(double widthOverHeight)
    {
        double u = widthOverHeight;
        double f = 6 + (2 * Math.PI - 6) * Math.Exp(-Math.Pow(30.666 / u, 0.7528));
        double z0 = 376.730313668 / (2 * Math.PI) * Math.Log(f / u + Math.Sqrt(1 + 4 / (u * u)));
        return z0 / 299792458.0;
    }

    [Theory]
    [InlineData(0.1e-3, 0.1e-3)]
    [InlineData(0.5e-3, 0.1e-3)]
    [InlineData(1.0e-3, 0.1e-3)]
    [InlineData(2.0e-3, 0.1e-3)]
    [InlineData(0.2e-3, 0.2e-3)]
    [InlineData(1.0e-3, 0.2e-3)]
    [InlineData(2.0e-3, 0.2e-3)]
    [InlineData(4.0e-3, 0.2e-3)]
    public void LongTraceOverPlane_SitsInThePhysicsBandAroundTheMicrostripValue(double width, double height)
    {
        // l = 100 mm: end effects are a fraction of a percent. The composed value is
        // the UNIFORM-current loop of a 35 µm trace; the microstrip formula is the
        // high-frequency, zero-thickness one. Uniform current stores more flux than the
        // edge-crowded distribution (up), copper thickness spreads the current away
        // from a single sheet (down, most when the trace is as narrow as it is thick).
        // A physics band, not a pin: 0.90 … 1.15.
        double perLength = PlaneLoop(100e-3, width, height) / 100e-3;
        double microstrip = HammerstadJensenPerLength(width / height);
        Assert.InRange(perLength / microstrip, 0.90, 1.15);
    }

    [Fact]
    public void HammerstadJensen_ReferenceValues_AreTheHandValues()
    {
        // nH/mm: w/h = 1, 5, 10 → 0.422, 0.165, 0.097.
        Assert.Equal(0.422e-6, HammerstadJensenPerLength(1), 0.001e-6);
        Assert.Equal(0.165e-6, HammerstadJensenPerLength(5), 0.001e-6);
        Assert.Equal(0.097e-6, HammerstadJensenPerLength(10), 0.001e-6);
    }

    [Fact]
    public void AMillimetreTraceOverATenthMillimetreGap_IsPositive_NotTheNegativeItWas()
    {
        // The defect: w = 1.0 mm over h = 0.1 mm composed to −0.14 nH/mm, and the UI
        // printed it. The physical value is just above the microstrip 0.097 nH/mm.
        double perLength = PlaneLoop(10e-3, 1.0e-3, 0.1e-3) / 10e-3;
        Assert.InRange(perLength, 0.097e-6, 0.115e-6);

        // Wider still — the old expansion went to −0.72 nH/mm here.
        Assert.InRange(PlaneLoop(10e-3, 2.0e-3, 0.1e-3) / 10e-3, 0.050e-6, 0.065e-6);

        // Monotone in both directions that matter: wider ⇒ less, higher ⇒ more.
        Assert.True(PlaneLoop(10e-3, 2.0e-3, 0.1e-3) < PlaneLoop(10e-3, 1.0e-3, 0.1e-3));
        Assert.True(PlaneLoop(10e-3, 1.0e-3, 0.2e-3) > PlaneLoop(10e-3, 1.0e-3, 0.1e-3));
    }

    // ------------------------------------------------------------------
    // The guard: finite and positive — and nothing stronger.
    // ------------------------------------------------------------------

    [Fact]
    public void AVerticalWire_LoopsMoreThanItsPartialValue_AndIsNotRefusedForIt()
    {
        // A vertical conductor's image carries current the SAME way (mirror z, swap the
        // ends), so its image mutual is positive and loop > partial. A "loop ≤ partial"
        // guard would refuse a correct answer; the guard is positivity only.
        var vertical = new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, 1e-3), new Vector3D(0, 0, 2e-3), 0.1e-3, 0,
                SegmentProfile.RoundWire)
        };
        var report = new PlaneReturnComposer().Compose(vertical, planeSurfaceZ: 0);
        Assert.Null(report.FailureReason);
        double partial = new LoopComposer().Compose(vertical).LoopInductance;
        Assert.True(report.LoopInductanceHenries!.Value > partial);

        // A horizontal trace is the ordinary case: the image opposes.
        var horizontal = new[] { Trace(10e-3, 0.5e-3, 0.2e-3) };
        double horizontalPartial = new LoopComposer().Compose(horizontal).LoopInductance;
        Assert.True(PlaneLoop(10e-3, 0.5e-3, 0.2e-3) < horizontalPartial);
    }

    [Fact]
    public void ANonPositiveLoop_IsRefusedWithAReason_NotReported()
    {
        // A round wire whose copper reaches through the plane surface: axis 0.05 mm
        // above it, radius 1 mm. The image then sits inside the wire's own
        // geometric-mean radius and the composed "loop" is negative — not a small
        // error but a geometry image theory cannot represent.
        var sunk = new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, 0.05e-3), new Vector3D(L, 0, 0.05e-3), 2e-3, 0,
                SegmentProfile.RoundWire)
        };
        var report = new PlaneReturnComposer().Compose(sunk, planeSurfaceZ: 0);
        Assert.Null(report.LoopInductanceHenries);
        Assert.Contains("not a physical value", report.FailureReason);
    }
}
