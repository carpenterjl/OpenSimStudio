using System.Diagnostics;
using OpenSim.Core.Model;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Does the congruence cache actually pay on a real mapped mesh?
///
/// It is keyed on BITWISE equality of an element's node offsets, which is what makes stamping
/// sound — but the lattice computes its coordinates as min + (max-min)*i/n, and consecutive
/// differences of that are not all the same double. So the hit rate is a measurement, not an
/// assumption, and a cache that fragmented into one entry per element would be pure overhead
/// worth deleting rather than shipping.
/// </summary>
public class Hex20CacheEffectivenessTests
{
    private readonly ITestOutputHelper _output;

    public Hex20CacheEffectivenessTests(ITestOutputHelper output) => _output = output;

    private static readonly Material Steel = new()
    {
        Name = "Structural steel",
        YoungsModulus = 200e9,
        PoissonRatio = 0.30,
        Density = 7850
    };

    private static FeMesh Beam(int nx, int ny, int nz) =>
        new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.200, 0.060, 0.020),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = ElementShape.Hexahedral,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(nx, ny, nz)
            });

    /// <summary>
    /// The measurement that decides whether the cache stays. Reported either way, so the
    /// number is in the record rather than in someone's memory.
    /// </summary>
    [Fact]
    public void TheCongruenceCache_CollapsesAMappedMeshToAHandfulOfDistinctShapes()
    {
        var mesh = Beam(40, 12, 4);
        var assembler = new Hex20Assembler(mesh, Steel);
        assembler.AssembleStiffness();

        int shapes = assembler.DistinctShapeCount;
        _output.WriteLine($"{mesh.ElementCount} elements -> {shapes} distinct element shapes " +
                          $"({(double)shapes / mesh.ElementCount:p2} of them computed)");

        // A mapped lattice on a box is congruent bricks; last-bit differences in the
        // coordinate arithmetic may split them into a few families, but nothing like one
        // family per element. If this ever approaches the element count the cache is dead
        // weight and should go, not be tuned.
        Assert.True(shapes < mesh.ElementCount / 10,
            $"the cache fragmented into {shapes} shapes across {mesh.ElementCount} elements; " +
            "it is not earning its place");
    }

    /// <summary>
    /// And the cache must actually make assembly faster, not merely hit. Relative and
    /// Release-only, the house perf-guard shape: an absolute limit loose enough for Debug
    /// could never catch a regression.
    /// </summary>
    [Fact]
    public void StampedAssembly_IsFasterThanComputingEveryElement()
    {
        if (ShouldSkip()) return;

        var mesh = Beam(40, 12, 4);

        // Warm the JIT on both paths before timing either.
        new Hex20Assembler(mesh, Steel) { ForceGeneralPath = true }.AssembleStiffness();
        new Hex20Assembler(mesh, Steel).AssembleStiffness();

        var general = Stopwatch.StartNew();
        new Hex20Assembler(mesh, Steel) { ForceGeneralPath = true }.AssembleStiffness();
        general.Stop();

        var stamped = Stopwatch.StartNew();
        new Hex20Assembler(mesh, Steel).AssembleStiffness();
        stamped.Stop();

        _output.WriteLine($"general {general.Elapsed.TotalMilliseconds:F0} ms, " +
                          $"stamped {stamped.Elapsed.TotalMilliseconds:F0} ms");

        Assert.True(stamped.Elapsed < general.Elapsed,
            $"stamping ({stamped.Elapsed.TotalMilliseconds:F0} ms) did not beat computing every " +
            $"element ({general.Elapsed.TotalMilliseconds:F0} ms) — Hex20Assembler.MatricesFor " +
            "or its shape key has regressed");
    }

    private static bool ShouldSkip()
    {
#if DEBUG
        return true;     // Debug timings measure the JIT, not the code
#else
        return Environment.ProcessorCount < 4;
#endif
    }
}
