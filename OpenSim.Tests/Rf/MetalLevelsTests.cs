using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Network;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-34: sheet metal on two interfaces of one stackup, solved together. The gate that cannot
/// be met by accident is the power ledger of a lossless structure: what the port puts in must
/// leave as radiation and surface waves, every term of which now carries cross-level parts —
/// the matrix's cross blocks decide the currents, the far field adds the two levels' fields
/// coherently, and the surface-wave power pairs them through the modes' cross-level residues.
/// </summary>
public class MetalLevelsTests
{
    private readonly ITestOutputHelper _output;
    public MetalLevelsTests(ITestOutputHelper output) => _output = output;

    /// <summary>Rectangles of metal, each on its level (z = that interface's height), meshed on
    /// a regular grid, merged into one structure; a gap port across the first rectangle at
    /// <paramref name="gapX"/>.</summary>
    private static (SurfaceStructure Structure, SurfacePort Port) Rectangles(
        IReadOnlyList<(double X0, double Y0, double X1, double Y1, double Z, int Nx, int Ny)> parts, double gapX)
    {
        var vertices = new List<Vector3D>();
        var triangles = new List<(int, int, int)>();
        foreach (var (x0, y0, x1, y1, z, nx, ny) in parts)
        {
            int start = vertices.Count;
            for (int i = 0; i <= nx; i++)
                for (int j = 0; j <= ny; j++)
                    vertices.Add(new Vector3D(x0 + (x1 - x0) * i / nx, y0 + (y1 - y0) * j / ny, z));
            int At(int i, int j) => start + i * (ny + 1) + j;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    triangles.Add((At(i, j), At(i + 1, j), At(i, j + 1)));
                    triangles.Add((At(i + 1, j), At(i + 1, j + 1), At(i, j + 1)));
                }
        }
        var structure = new SurfaceStructure(vertices, triangles, null);
        double zPort = parts[0].Z;
        var port = new SurfacePort(Enumerable.Range(0, structure.BasisCount).Where(e =>
            structure.Edges[e].MinusTriangle >= 0
            && Math.Abs(vertices[structure.Edges[e].V1].X - gapX) < 1e-9 && Math.Abs(vertices[structure.Edges[e].V2].X - gapX) < 1e-9
            && Math.Abs(vertices[structure.Edges[e].V1].Z - zPort) < 1e-12).ToList(), new Vector3D(1, 0, 0));
        return (structure, port);
    }

    [Theory]
    [InlineData(4.2e9)]
    [InlineData(5.0e9)]
    public void StackedPatches_CloseTheirPowerLedger(double f)
    {
        // A 20 mm patch on 1.5 mm of εr 2.2, fed by a gap 2.5 mm in from its edge, and a 24 mm
        // parasitic patch on a 2 mm εr 1.1 spacer above it. Lossless dielectrics, perfect metal:
        // ½Re(V·I*) = P_rad + P_sw.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(2.2, 0, 1.5e-3), new LayeredStackup.Layer(1.1, 0, 2e-3)
        });
        var heights = stackup.InterfaceHeights();
        var (structure, port) = Rectangles(new[]
        {
            (-10e-3, -10e-3, 10e-3, 10e-3, heights[0], 8, 8),
            (-12e-3, -12e-3, 12e-3, 12e-3, heights[1], 8, 8)
        }, gapX: -7.5e-3);
        var solution = new SurfaceMomSolver().SolveLevels(structure, stackup, f, new[] { port }, rhoMax: 0.05);
        var currents = solution.EdgeCurrents[0];
        double input = 0.5 * solution.Admittance[0, 0].Real;
        double radiated = LevelsFarField.Compute(structure, stackup, f, currents).TotalRadiatedPowerWatts;
        double surfaceWave = LevelsFarField.SurfaceWavePowerWatts(structure, stackup, f, currents);
        _output.WriteLine($"{f / 1e9:g3} GHz, {structure.BasisCount} unknowns: Zin {1 / solution.Admittance[0, 0]:f2}; " +
                          $"radiated {radiated / input:f4}, surface wave {surfaceWave / input:f4}, ledger {(radiated + surfaceWave) / input:f5}");
        Assert.Equal(1, (radiated + surfaceWave) / input, 0.002);
    }

    [Fact]
    public void TwoViasClosingALoop_HaveTheLoopInductance_InAnyDielectric()
    {
        // A 12 × 2 mm strip on each of two levels 1 mm apart, joined by vias 8 mm apart, the lower
        // strip cut by a gap port between them: a loop. At 100 MHz its input impedance is jωL,
        // and an inductance is magnetostatic — it cannot depend on εr. The power ledger cannot
        // see a reactive error (a missing coupling term that changes only the imaginary part of
        // the matrix leaves Re(V·I*) = P_rad + P_sw intact); this can, because every cross term
        // between the tubes, the fans and the sheets carries the dielectric differently.
        double Inductance(double epsR)
        {
            var stackup = new LayeredStackup(new[]
            {
                new LayeredStackup.Layer(epsR, 0, 0.5e-3), new LayeredStackup.Layer(epsR, 0, 1e-3)
            });
            var heights = stackup.InterfaceHeights();
            var (structure, port) = Rectangles(new[]
            {
                (-6e-3, -1e-3, 6e-3, 1e-3, heights[0], 24, 4),
                (-6e-3, -1e-3, 6e-3, 1e-3, heights[1], 24, 4)
            }, gapX: 0);
            var vias = new[]
            {
                new LevelVia("left", new ProbeFeed(-4e-3, 0, 0.15e-3, Segments: 3), 0, 1),
                new LevelVia("right", new ProbeFeed(4e-3, 0, 0.15e-3, Segments: 3), 0, 1)
            };
            double f = 100e6;
            var solution = new SurfaceMomSolver().SolveLevels(structure, stackup, f, new[] { port }, rhoMax: 0.03, vias);
            var z = 1 / solution.Admittance[0, 0];
            _output.WriteLine($"εr {epsR}: Zin {z.Real:e3} + j{z.Imaginary:f4} Ω, L = {z.Imaginary / (2 * Math.PI * f) * 1e9:f4} nH");
            return z.Imaginary / (2 * Math.PI * f);
        }
        double air = Inductance(1), board = Inductance(4.4);
        Assert.True(air > 0);
        Assert.Equal(air, board, 0.01 * air);
    }

    [Theory]
    [InlineData(4.5e9)]
    [InlineData(6.0e9)]
    public void AViaFedPatch_ClosesItsPowerLedger(double f)
    {
        // A 16 × 4 mm strip on the lower interface (0.8 mm of εr 2.2 over the ground), fed by a
        // gap 2 mm in from its end, joined by a 0.2 mm via at (2, 0) mm to a 20 mm patch on the
        // interface 1.5 mm above. The via's tube current and its two attachment fans now enter
        // every term: P_in = P_rad + P_sw only if they are right.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(2.2, 0, 0.8e-3), new LayeredStackup.Layer(2.2, 0, 1.5e-3)
        });
        var heights = stackup.InterfaceHeights();
        var (structure, port) = Rectangles(new[]
        {
            (-12e-3, -2e-3, 4e-3, 2e-3, heights[0], 16, 4),
            (-10e-3, -10e-3, 10e-3, 10e-3, heights[1], 10, 10)
        }, gapX: -10e-3);
        var via = new LevelVia("via", new ProbeFeed(2e-3, 0, 0.2e-3, Segments: 3), 0, 1);
        var solution = new SurfaceMomSolver().SolveLevels(structure, stackup, f, new[] { port }, rhoMax: 0.05, new[] { via });
        double input = 0.5 * solution.Admittance[0, 0].Real;
        double radiated = LevelsFarField.Compute(structure, stackup, solution).TotalRadiatedPowerWatts;
        double surfaceWave = LevelsFarField.SurfaceWavePowerWatts(structure, stackup, solution);
        var tube = solution.ViaCurrents[0][0];
        _output.WriteLine($"{f / 1e9:g3} GHz, {structure.BasisCount} RWG + {tube.Length} via unknowns: Zin {1 / solution.Admittance[0, 0]:f2}; " +
                          $"via current {tube[0].Magnitude:e3} A at the bottom, {tube[^1].Magnitude:e3} A at the top; " +
                          $"radiated {radiated / input:f4}, surface wave {surfaceWave / input:f4}, ledger {(radiated + surfaceWave) / input:f5}");
        Assert.Equal(1, (radiated + surfaceWave) / input, 0.002);
    }
}
