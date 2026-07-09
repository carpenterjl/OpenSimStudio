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
}
