using OpenSim.Core.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf;
using Xunit;

namespace OpenSim.Tests.Rf;

public class AntennaGeometryTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;

    [Fact]
    public void SmallLoop_RadiationResistance_MatchesTheAreaFormula_AndConservesPower()
    {
        // A 16-gon loop, circumference 0.05 λ: R = 320π⁴(A/λ²)² with the exact POLYGON
        // area is the UNIFORM-current (C → 0) limit; a fed loop's current develops a
        // (kC)²-family non-uniformity whose dipole mode radiates extra — measured ratios
        // to the formula: 1.03 at C = 0.05λ, 1.13 at 0.1λ, 1.66 at 0.2λ. So the gate
        // sits at C = 0.05λ with a one-sided-high band. This also exercises the
        // wraparound basis indexing — a loop has as many unknowns as elements.
        int sides = 16;
        double loopRadius = 0.05 * Lambda / (2 * sides * Math.Sin(Math.PI / sides));
        var grid = WireGridBuilder.Build(
            CanonicalAntennas.Loop(loopRadius, Lambda / 2000, sides), Lambda / 40);
        Assert.NotNull(grid.Structure);
        var wire = grid.Structure!;
        Assert.True(wire.IsLoop);

        var solution = new ThinWireMomSolver().Solve(wire, Frequency,
            wire.NearestBasis(new Vector3D(loopRadius, 0, 0)));

        double area = CanonicalAntennas.LoopArea(loopRadius, sides);
        double expected = 320 * Math.Pow(Math.PI, 4) * Math.Pow(area / (Lambda * Lambda), 2);
        Assert.InRange(solution.InputImpedance.Real, 0.98 * expected, 1.10 * expected);
        Assert.True(solution.InputImpedance.Imaginary > 10,
            $"X = {solution.InputImpedance.Imaginary:g4} — a small loop is inductive");

        // Energy conservation on the loop topology.
        var pattern = FarFieldEvaluator.Compute(wire, solution);
        double pIn = 0.5 * System.Numerics.Complex.Conjugate(
            solution.BasisCurrents[wire.NearestBasis(new Vector3D(loopRadius, 0, 0))]).Real;
        Assert.True(Math.Abs(pattern.TotalRadiatedPowerWatts / pIn - 1) < 0.02,
            $"P_rad = {pattern.TotalRadiatedPowerWatts:g6} vs P_in = {pIn:g6}");
    }

    [Fact]
    public void CanonicalDipole_IsTheSameProblem_AsTheHandBuiltOne()
    {
        double length = 0.5 * Lambda, radius = Lambda / 2000;
        var canonical = WireGridBuilder.Build(CanonicalAntennas.Dipole(length, radius), length / 40);
        var manual = WireGridBuilder.Build(
            new[] { new WireSegment(new Vector3D(0, 0, -length / 2), new Vector3D(0, 0, length / 2), radius) },
            length / 40);
        Assert.NotNull(canonical.Structure);
        Assert.NotNull(manual.Structure);

        var solver = new ThinWireMomSolver();
        var a = solver.Solve(canonical.Structure!, Frequency, canonical.Structure!.NearestBasis(Vector3D.Zero));
        var b = solver.Solve(manual.Structure!, Frequency, manual.Structure!.NearestBasis(Vector3D.Zero));
        Assert.Equal(b.InputImpedance, a.InputImpedance);        // bitwise: identical geometry
    }

    [Fact]
    public void TraceChain_MapsToWeldedWires_WithTheRightRadii()
    {
        // The standard two-layer chain: trace → via barrel → trace.
        const double copper = 35e-6, gap = 1.6e-3, plating = 25e-6, drill = 0.3e-3, width = 4e-4;
        var chain = TraceChainBuilder.Build(
            new[]
            {
                new TraceCenterline(1, new Point2(0, 0), new Point2(8e-3, 0), width),
                new TraceCenterline(2, new Point2(8e-3, 0), new Point2(16e-3, 0), width)
            },
            new[] { new ViaBridge(new Via(new Point2(8e-3, 0), drill, Plated: true), new[] { 1, 2 }) },
            new NetMeshOptions
            {
                CopperThickness = copper,
                DefaultDielectricThickness = gap,
                ViaPlatingThickness = plating
            });
        Assert.NotNull(chain.Chain);

        var wires = TraceChainAntenna.FromChain(chain.Chain!);
        Assert.Equal(3, wires.Count);
        Assert.Equal(width / 4, wires[0].Radius, 1e-15);                     // strip → w/4
        Assert.Equal((drill + plating) / 2, wires[1].Radius, 1e-15);         // barrel surface radius
        for (int i = 1; i < wires.Count; i++)
            Assert.Equal(wires[i - 1].B, wires[i].A);                        // welded, bitwise

        // And the welded chain is a valid single path for the grid builder.
        var grid = WireGridBuilder.Build(wires, 5e-3);
        Assert.NotNull(grid.Structure);
        Assert.False(grid.Structure!.IsLoop);
    }

    [Fact]
    public void TraceChain_WithEndpointMismatch_IsWeldedIntoOneRun()
    {
        // Real chains join within the junction tolerance, not bitwise: a 0.1 mm gap at
        // the joint must weld to the shared midpoint instead of failing as disconnected.
        var chain = new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(10e-3, 0, 0), 4e-4, 35e-6),
            new TraceSegment3D(new Vector3D(10.1e-3, 0, 0), new Vector3D(20e-3, 0, 0), 4e-4, 35e-6)
        };
        var wires = TraceChainAntenna.FromChain(chain);
        Assert.Equal(2, wires.Count);
        Assert.Equal(wires[0].B, wires[1].A);
        Assert.Equal(10.05e-3, wires[0].B.X, 1e-12);

        var grid = WireGridBuilder.Build(wires, 5e-3);
        Assert.NotNull(grid.Structure);
    }
}
