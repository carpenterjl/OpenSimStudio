using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Fix 10 gates (RF-2, RF-3, RF-4): antenna ports that are physical terminal pairs and a
/// mesh the result does not depend on.
/// </summary>
public class AntennaPortTests
{
    private readonly ITestOutputHelper _output;
    public AntennaPortTests(ITestOutputHelper output) => _output = output;

    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;

    private static Polygon2 Rectangle(double width, double length) => new(new[]
    {
        new Point2(-width / 2, -length / 2), new Point2(width / 2, -length / 2),
        new Point2(width / 2, length / 2), new Point2(-width / 2, length / 2),
    });

    private static Complex IslandZin(double width, double length, double element, Point2 feed,
        bool singleEdge, out SurfaceGridResult grid)
    {
        grid = SurfaceMeshBuilder.BuildFromPolygon(Rectangle(width, length), element, 0, feed,
            singleEdgePort: singleEdge);
        Assert.NotNull(grid.Structure);
        return new SurfaceMomSolver().Solve(grid.Structure!, Frequency, grid.Port!).InputImpedance;
    }

    [Fact]
    public void IslandPort_IsACutAcrossTheWholeConductor()
    {
        // A strip three elements wide: the port must be every edge on the cross-section
        // through the feed point, not one of them.
        double width = Lambda / 40, length = 0.47 * Lambda, element = width / 3;
        var grid = SurfaceMeshBuilder.BuildFromPolygon(Rectangle(width, length), element, 0,
            new Point2(0, 0));
        Assert.NotNull(grid.Structure);
        var structure = grid.Structure!;
        Assert.True(grid.Port!.EdgeBases.Count >= 3, $"{grid.Port.EdgeBases.Count} port edge(s)");

        // The cut runs across the strip (current along y), and its edges span the width.
        Assert.True(Math.Abs(grid.Port.Direction.Y) > 0.99);
        double span = 0;
        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (int e in grid.Port.EdgeBases)
        {
            var edge = structure.Edges[e];
            foreach (int v in new[] { edge.V1, edge.V2 })
            {
                minX = Math.Min(minX, structure.Vertices[v].X);
                maxX = Math.Max(maxX, structure.Vertices[v].X);
                Assert.True(Math.Abs(structure.Vertices[v].Y) < 1.5 * element);
            }
        }
        span = maxX - minX;
        Assert.InRange(span, 0.98 * width, 1.02 * width);
        Assert.Contains(grid.Warnings, w => w.Contains("full width"));

        // Removing the port edges disconnects the two halves: no triangle-to-triangle path
        // across a non-port interior edge joins y > 0 to y < 0.
        var port = new HashSet<int>(grid.Port.EdgeBases);
        var neighbours = new List<int>[structure.Triangles.Count];
        for (int t = 0; t < neighbours.Length; t++) neighbours[t] = new List<int>();
        for (int e = 0; e < structure.Edges.Count; e++)
        {
            var edge = structure.Edges[e];
            if (port.Contains(e) || edge.PlusTriangle < 0 || edge.MinusTriangle < 0) continue;
            neighbours[edge.PlusTriangle].Add(edge.MinusTriangle);
            neighbours[edge.MinusTriangle].Add(edge.PlusTriangle);
        }
        int start = Enumerable.Range(0, structure.Triangles.Count)
            .First(t => structure.TriangleCentroids[t].Y > length / 4);
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>(new[] { start });
        while (queue.Count > 0)
            foreach (int next in neighbours[queue.Dequeue()])
                if (seen.Add(next)) queue.Enqueue(next);
        Assert.DoesNotContain(seen, t => structure.TriangleCentroids[t].Y < -length / 4);
    }

    [Fact]
    public void IslandPort_GivesTheSameDipoleAsTheStructuredPlate_AndIsMeshStable()
    {
        // A centre-fed strip dipole drawn as a copper island against the wizard plate with
        // its built-in full-row port, and against itself on a finer mesh.
        double width = Lambda / 40, length = 0.47 * Lambda;
        var plate = SurfaceMeshBuilder.BuildRectangularPlate(width, length, width / 3, portFraction: 0.5);
        Complex reference = new SurfaceMomSolver().Solve(plate.Structure!, Frequency, plate.Port!).InputImpedance;

        Complex coarse = IslandZin(width, length, width / 3, new Point2(0, 0), false, out _);
        Complex fine = IslandZin(width, length, width / 4.5, new Point2(0, 0), false, out _);
        Complex oneEdge = IslandZin(width, length, width / 3, new Point2(0, 0), true, out _);
        Complex oneEdgeFine = IslandZin(width, length, width / 4.5, new Point2(0, 0), true, out _);
        _output.WriteLine($"plate {reference}; island {coarse}; finer {fine}; single edge {oneEdge} / {oneEdgeFine}");

        Assert.True(MeshConvergence.RelativeChange(coarse, reference) < 0.05,
            $"island {coarse} vs plate {reference}");
        Assert.True(MeshConvergence.IsConverged(coarse, fine), $"island {coarse} → {fine}");

        // The single-edge port is a different quantity that moves with the mesh.
        Assert.True(MeshConvergence.RelativeChange(oneEdge, reference) > 0.3,
            $"single edge {oneEdge} vs plate {reference}");
    }

    [Fact]
    public void IslandPort_OffCentreCut_StaysAcrossTheStrip()
    {
        // A feed point away from the centre, near one edge of the strip: the cut still
        // spans the whole width at that station.
        double width = Lambda / 40, length = 0.47 * Lambda, element = width / 3;
        var grid = SurfaceMeshBuilder.BuildFromPolygon(Rectangle(width, length), element, 0,
            new Point2(0.4 * width, 0.2 * length));
        var structure = grid.Structure!;
        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (int e in grid.Port!.EdgeBases)
            foreach (int v in new[] { structure.Edges[e].V1, structure.Edges[e].V2 })
            {
                minX = Math.Min(minX, structure.Vertices[v].X);
                maxX = Math.Max(maxX, structure.Vertices[v].X);
                Assert.True(Math.Abs(structure.Vertices[v].Y - 0.2 * length) < 1.5 * element);
            }
        Assert.InRange(maxX - minX, 0.98 * width, 1.02 * width);
    }

    [Fact]
    public void PatchGap_SitsAtItsPhysicalOffset_WhateverTheMesh()
    {
        double w = 1.186e-2, l = 0.906e-2, offset = l / 8;
        foreach (double element in new[] { 2.0e-3, 1.4e-3, 0.9e-3 })
        {
            var grid = SurfaceMeshBuilder.BuildRectangularPlate(w, l, element, portOffset: offset);
            var s = grid.Structure!;
            foreach (int e in grid.Port!.EdgeBases)
            {
                Assert.Equal(-l / 2 + offset, s.Vertices[s.Edges[e].V1].Y, 12);
                Assert.Equal(-l / 2 + offset, s.Vertices[s.Edges[e].V2].Y, 12);
            }
            Assert.Equal((int)Math.Ceiling(w / element), grid.Port.EdgeBases.Count);
        }
        Assert.Null(SurfaceMeshBuilder.BuildRectangularPlate(w, l, 1e-3, portOffset: l).Structure);
    }

    [Fact]
    public void FiniteGap_ReadsAUniformCurrentAsTheCurrentThatCrossesIt()
    {
        // The port current of a finite gap is Σ c_m·l_m·I_m with c_m·l_m = ⟨f_m, E_gap⟩/V, the
        // average over the gap of the current crossing it. A uniform J = J₀ŷ is exactly
        // representable by RWGs (I_m = J·n̂_m on every interior edge), so it must read back as
        // exactly J₀·W, on any mesh and with any number of rows across the gap.
        double w = 1.186e-2, l = 0.906e-2, j0 = 0.37;
        foreach (double element in new[] { 1.4e-3, 0.9e-3, 0.4e-3 })
        {
            var grid = SurfaceMeshBuilder.BuildRectangularPlate(w, l, element,
                portOffset: l / 8, portGapWidth: 0.5e-3, maxUnknowns: 20000);
            var s = grid.Structure!;
            var port = grid.Port!;
            Assert.NotNull(port.Coefficients);
            Complex current = Complex.Zero;
            for (int i = 0; i < port.EdgeBases.Count; i++)
            {
                var edge = s.Edges[port.EdgeBases[i]];
                var along = s.Vertices[edge.V2] - s.Vertices[edge.V1];
                var normal = new Vector3D(along.Y, -along.X, 0) * (1 / along.Length);
                var crossing = s.TriangleCentroids[edge.MinusTriangle] - s.TriangleCentroids[edge.PlusTriangle];
                if (Vector3D.Dot(normal, crossing) < 0) normal = normal * -1;
                current += port.Coefficients![i] * edge.Length * j0 * normal.Y;
            }
            Assert.Equal(j0 * w, current.Real, 10);
        }
    }

    /// <summary>The patch resonance seen at the series gap — the peak of R(f) — by a scan and
    /// a parabola through the three highest points. (With the gap an eighth of the length in
    /// from the edge the reactance does not cross zero; the resistance peak marks the mode.)</summary>
    private (double Frequency, double Resistance) PatchResonance(double element, double offset,
        double? gapWidth = null)
    {
        var substrate = new SubstrateStackup(2.2, 0.0, 1.588e-3);
        double w = 1.186e-2, l = 0.906e-2;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(w, l, element,
            z: substrate.ThicknessMeters, portOffset: offset, portGapWidth: gapWidth);
        var solver = new SurfaceMomSolver();
        var f = new List<double>();
        var r = new List<double>();
        // A finite gap has less capacitance than a delta gap, so its peak sits higher.
        double start = gapWidth is null ? 10.125e9 : 10.45e9;
        for (double frequency = start; frequency <= start + 0.64e9; frequency += 0.125e9)
        {
            var table = new LayeredKernelTable(substrate, frequency, 0.025);
            Complex z = solver.Solve(grid.Structure!, table, grid.Port!).InputImpedance;
            _output.WriteLine($"  element {element * 1e3:g3} mm, f {frequency / 1e9:g5} GHz: Z = {z.Real:g4} + j{z.Imaginary:g4}");
            f.Add(frequency);
            r.Add(z.Real);
        }
        int k = r.IndexOf(r.Max());
        Assert.True(k > 0 && k < r.Count - 1, "the resistance peak must lie inside the scan");
        double step = f[1] - f[0];
        double curvature = r[k - 1] - 2 * r[k] + r[k + 1];
        double shift = 0.5 * (r[k - 1] - r[k + 1]) / curvature;
        return (f[k] + shift * step, r[k] - 0.25 * (r[k - 1] - r[k + 1]) * shift);
    }

    [Fact]
    public void PatchGap_Resonance_UnderRefinement()
    {
        // The Balanis patch (εr 2.2, 1.588 mm, 10 GHz design) with a 0.5 mm series gap
        // centred L/8 from the edge, at 10 and at 15 elements along the resonant length: the
        // plan's gate, under 5 % between two successive refinements (FU-8).
        //
        // A DELTA gap never meets it. Its peak resistance at 8/10/12/15 elements was 47.65 /
        // 43.16 / 41.60 / 39.75 Ω — past 10 elements falling linearly in ln h, about 8.5 Ω per
        // unit, which is the gap's own capacitance growing as ln(1/h); grading the mesh toward
        // the rim did not change that (7.7–7.8 % from 10 to 15). A gap of physical width has a
        // finite capacitance, and with its three rows resolving it the same four meshes read
        // 65.46 / 64.91 / 64.97 / 64.68 Ω (10.821 → 10.757 GHz).
        double offset = 0.906e-2 / 8;
        var coarse = PatchResonance(0.906e-2 / 10, offset, gapWidth: 0.5e-3);
        var fine = PatchResonance(0.906e-2 / 15, offset, gapWidth: 0.5e-3);
        _output.WriteLine($"coarse {coarse}; fine {fine}");
        Assert.InRange(fine.Frequency / coarse.Frequency, 0.99, 1.01);
        Assert.InRange(fine.Resistance / coarse.Resistance, 0.95, 1.05);
        Assert.True(MeshConvergence.IsConverged(
            new Complex(coarse.Resistance, 0), new Complex(fine.Resistance, 0)));
    }

    [Fact]
    public void MeshConvergence_ReportsTheChange()
    {
        var coarse = new Complex(50, 10);
        Assert.True(MeshConvergence.IsConverged(coarse, new Complex(51, 10.5)));
        Assert.False(MeshConvergence.IsConverged(coarse, new Complex(60, 2)));
        Assert.Contains("converged at this density",
            MeshConvergence.Describe(coarse, 100, new Complex(51, 10.5), 220));
        Assert.Contains("NOT converged", MeshConvergence.Describe(coarse, 100, new Complex(60, 2), 220));
    }
}
