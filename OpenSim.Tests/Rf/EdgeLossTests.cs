using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Si;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-37: the loss of the current along a zero-thickness sheet's free edge. The model's current
/// there goes as 1/√x, whose ∫|J|² diverges, so the sheet's heat rose with every refinement of the
/// mesh. A real strip is t thick, and its edge current is finite: the loss of the thick edge equals
/// the zero-thickness 1/√x integrated up to a stopping distance Δ from the edge, Δ = t/(4π·e^π)
/// (<see cref="SheetLoss.StoppingDistance"/>, derived there from the conformal map of a thick edge).
/// </summary>
public class EdgeLossTests
{
    private readonly ITestOutputHelper _out;
    public EdgeLossTests(ITestOutputHelper output) => _out = output;

    /// <summary>∫σ²ds/Q² [1/m] of an isolated thick strip W × t: its surface charge, which is its
    /// TEM surface current, by a free-space Galerkin BEM with cosine-graded panels on all four faces
    /// (Σ_j σ_j·∬ln|r − r′| = −2πε₀V·l_i, Σ σ_j l_j = 1).</summary>
    private static double ThickStripLoss(double w, double t, int perFace, int perWall)
    {
        var panels = new List<ThickConductorBem.Segment>();
        void Face(double x0, double z0, double x1, double z1, int count)
        {
            double px = x0, pz = z0;
            for (int j = 1; j <= count; j++)
            {
                double s = 0.5 * (1 - Math.Cos(Math.PI * j / count));
                double x = x0 + (x1 - x0) * s, z = z0 + (z1 - z0) * s;
                panels.Add(new ThickConductorBem.Segment(px, pz, x, z, 0));
                (px, pz) = (x, z);
            }
        }
        Face(-w / 2, 0, w / 2, 0, perFace);
        Face(w / 2, 0, w / 2, t, perWall);
        Face(w / 2, t, -w / 2, t, perFace);
        Face(-w / 2, t, -w / 2, 0, perWall);
        int n = panels.Count;
        var a = new ComplexDenseMatrix(n + 1, n + 1);
        var rhs = new Complex[n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) a[i, j] = ThickConductorBem.LogMoment(panels[i], panels[j]);
            a[i, n] = panels[i].Length;
            a[n, i] = panels[i].Length;
        }
        rhs[n] = 1;
        var sigma = ComplexLu.Factor(a).Solve(rhs);
        double loss = 0;
        for (int i = 0; i < n; i++) loss += sigma[i].Real * sigma[i].Real * panels[i].Length;
        return loss;
    }

    /// <summary>The zero-thickness strip's ∫|J|²ds/I² over both faces, its exact current
    /// (I/π)/√(a² − x²) shared by the faces and integrated to Δ from each edge.</summary>
    private static double ThinStripLoss(double w, double delta) => Math.Log((w - delta) / delta) / (Math.PI * Math.PI * w);

    [Fact]
    public void TheStoppingDistance_IsAThickStripsSurfaceLoss()
    {
        // The perfect-conductor limit the derivation is about, on a strip 912 thicknesses wide.
        // Measured: thick/thin − 1 = −3.95 % at W/t = 57, −1.66 % at 228, −0.48 % at 912 — the
        // strip's finite width, which makes a thick strip electrically wider by about
        // (t/π)(½ + ½ln(2πW/t)) per edge (−3.4 %, −1.1 %, −0.3 % by that estimate). The Δ that
        // matches at 912 is t/274 against the derived t/291.
        double t = 1e-3, w = 912 * t;
        double thick = ThickStripLoss(w, t, 1200, 120);
        double delta = SheetLoss.StoppingDistance(t);
        double thin = ThinStripLoss(w, delta);
        double fitted = w / (1 + Math.Exp(Math.PI * Math.PI * w * thick));
        _out.WriteLine($"thick {thick * t:e5}, thin to t/{t / delta:f1} {thin * t:e5} ({thick / thin - 1:p2}); the Δ that matches is t/{t / fitted:f1}");
        Assert.InRange(thick / thin - 1, -0.01, 0.01);
    }

    /// <summary>A strip w × l in the z = 0 plane, <paramref name="across"/> cells across and
    /// <paramref name="along"/> along, diagonals alternating so the mesh is symmetric about the
    /// strip's axis, and a gap port across its middle row.</summary>
    private static (SurfaceStructure Structure, SurfacePort Port) Strip(double w, double l, int across, int along)
    {
        var vertices = new List<Vector3D>();
        for (int j = 0; j <= along; j++)
            for (int i = 0; i <= across; i++)
                vertices.Add(new Vector3D(-w / 2 + w * i / across, -l / 2 + l * j / along, 0));
        int V(int i, int j) => j * (across + 1) + i;
        var triangles = new List<(int, int, int)>();
        for (int j = 0; j < along; j++)
            for (int i = 0; i < across; i++)
                if ((i + j) % 2 == 0)
                {
                    triangles.Add((V(i, j), V(i + 1, j), V(i + 1, j + 1)));
                    triangles.Add((V(i, j), V(i + 1, j + 1), V(i, j + 1)));
                }
                else
                {
                    triangles.Add((V(i, j), V(i + 1, j), V(i, j + 1)));
                    triangles.Add((V(i + 1, j), V(i + 1, j + 1), V(i, j + 1)));
                }
        var structure = new SurfaceStructure(vertices, triangles, null);
        double middle = vertices[V(0, along / 2)].Y;
        var port = new List<int>();
        for (int e = 0; e < structure.Edges.Count; e++)
            if (vertices[structure.Edges[e].V1].Y == middle && vertices[structure.Edges[e].V2].Y == middle) port.Add(e);
        return (structure, new SurfacePort(port, new Vector3D(0, 1, 0)));
    }

    /// <summary>∫|I(y)|²dy: the current through each transverse row of edges (I_n·ℓ_n with the
    /// sign of its T⁺ → T⁻ crossing along +y), by the trapezoid over the rows.</summary>
    private static double RowCurrentSquared(SurfaceStructure s, IReadOnlyList<Complex> currents, double rowPitch)
    {
        var rows = new SortedDictionary<double, Complex>();
        for (int e = 0; e < s.Edges.Count; e++)
        {
            var edge = s.Edges[e];
            double y = s.Vertices[edge.V1].Y;
            if (s.Vertices[edge.V2].Y != y) continue;
            double sign = s.TriangleCentroids[edge.MinusTriangle].Y > s.TriangleCentroids[edge.PlusTriangle].Y ? 1 : -1;
            rows[y] = (rows.TryGetValue(y, out var sum) ? sum : 0) + sign * currents[e] * edge.Length;
        }
        double total = 0;
        foreach (var current in rows.Values) total += rowPitch * (current.Real * current.Real + current.Imaginary * current.Imaginary);
        return total;   // the end rows are the rim, where I = 0
    }

    [Fact]
    public void AStripDipolesHeat_StopsGrowingWithTheMesh_AndIsTheStripsResistance()
    {
        // A 94 × 2 mm copper strip dipole, 35 µm thick, in free space near its first resonance
        // (δ = 1.7 µm at 1.5 GHz), meshed 2, 4 and 8 cells across. Its heat against ½R′∫|I|²dy with
        // R′ the zero-thickness strip's resistance to the stopping distance: the edge machinery
        // carries that R′ whatever the mesh, while the zero-thickness sheet's own loss grows as the
        // edge rows narrow (the 1/√x current's ∫|J|² resolved ever further).
        double w = 2e-3, l = 94e-3, t = 35e-6, sigma = 5.8e7, f = 1.5e9;
        int along = 48;
        var edge = new SheetEdge(t, sigma);
        Complex sheet = SheetLoss.CopperSheet(f, sigma, t, bothFaces: true);
        double rs = SheetLoss.RoundWire(f, sigma).Real;
        double reference = rs * ThinStripLoss(w, edge.StoppingDistance);
        var corrected = new List<double>();
        var plain = new List<double>();
        foreach (int across in new[] { 2, 4, 8 })
        {
            var (structure, port) = Strip(w, l, across, along);
            double Ratio(SheetEdge? model, out double resistance)
            {
                var solver = new SurfaceMomSolver { SheetImpedance = _ => sheet, SheetEdge = model };
                var solution = solver.Solve(structure, f, port);
                double heat = SheetLoss.OhmicPower(structure, solution.EdgeCurrents, sheet, model, f);
                double rows = RowCurrentSquared(structure, solution.EdgeCurrents, l / along);
                resistance = solution.InputImpedance.Real;
                return heat / (0.5 * reference * rows);
            }
            corrected.Add(Ratio(edge, out double rinEdge));
            plain.Add(Ratio(null, out double rinPlain));
            _out.WriteLine($"{across} across: heat / ½R′∫|I|² = {corrected[^1]:f4} with the edge model, {plain[^1]:f4} without; " +
                           $"Re Zin {rinEdge:f3} vs {rinPlain:f3} Ω");
        }
        double spread = corrected.Max() - corrected.Min();
        _out.WriteLine($"spread over the meshes: {spread:p2} with the edge model, {plain.Max() - plain.Min():p2} without");
        // Measured 0.9993, 1.0000, 0.9773 with the edge model; 0.508, 0.581, 0.644 without.
        Assert.All(corrected, r => Assert.InRange(r, 0.96, 1.04));
        Assert.True(spread < 0.3 * (plain.Max() - plain.Min()));
    }
}
