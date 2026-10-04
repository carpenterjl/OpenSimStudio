using System.Diagnostics;
using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Fix 11 — the quadrature of the wire-to-sheet junction's 1/ρ disc.
///
/// <para>The disc's self term ∬ D·D′ G_A was integrated with the SAME Gauss nodes for the inner
/// and the outer integral, so every node met itself at zero separation and added µ₀/(4πa): a
/// series inductance at the junction growing as b²/a (b the mesh size, a the wire radius), where
/// the true integral grows only as ln(b/a). Because it is purely reactive, no power check saw it.</para>
///
/// <para>The oracle here does not share anything with the production rule. For a CIRCULAR disc
/// of radius b the four-fold integral collapses (Lipschitz integral for 1/√(R² + a²), then the
/// Bessel addition theorem, whose m = 1 term is the only one cos(φ − φ′) keeps) to</para>
/// <code>
///   ∬ D·D′ /√(R² + a²) = b · ∫₀^∞ e^{−(a/b)x} (1 − J₀(x))² / x² dx
/// </code>
/// <para>— one dimension, smooth, and evaluated below by plain panelled Gauss. A 32-wedge
/// polygonal fan lies between the discs of its apothem and of its circumradius, 0.5 % apart.</para>
/// </summary>
public class JunctionQuadratureTests
{
    private readonly ITestOutputHelper _output;
    public JunctionQuadratureTests(ITestOutputHelper output) => _output = output;

    private const double MuOver4Pi = 1e-7;

    /// <summary>b·∫₀^∞ e^{−εx}(1 − J₀(x))²/x² dx with ε = a/b.</summary>
    private static double CircularDiscSelf(double a, double b)
    {
        double eps = a / b;
        var (nodes, weights) = GaussLegendre.Rule(8, 0, 1);
        double sum = 0;
        const double oscillatoryEnd = 4000;
        for (double x0 = 0; x0 < oscillatoryEnd; x0 += 0.5)
            for (int i = 0; i < nodes.Length; i++)
            {
                double x = x0 + 0.5 * nodes[i];
                double f = 1 - Bessel.J0(x);
                sum += 0.5 * weights[i] * Math.Exp(-eps * x) * f * f / (x * x);
            }
        // Beyond: (1 − J₀)² averages to 1 + 1/(πx); the −2J₀ term integrates to ~x^−2.5.
        for (double lx = Math.Log(oscillatoryEnd); lx < Math.Log(1e9); lx += 0.05)
            for (int i = 0; i < nodes.Length; i++)
            {
                double x = Math.Exp(lx + 0.05 * nodes[i]);
                sum += 0.05 * weights[i] * x * Math.Exp(-eps * x) * (1 + 1 / (Math.PI * x)) / (x * x);
            }
        return b * sum;
    }

    /// <summary>A regular n-gon fan of circumradius b around a centre vertex, with one more ring
    /// outside it so every fan wedge has a neighbour.</summary>
    private static (SurfaceStructure Surface, int Centre) PolygonFan(int n, double b)
    {
        var vertices = new List<Vector3D> { Vector3D.Zero };
        for (int ring = 1; ring <= 2; ring++)
            for (int i = 0; i < n; i++)
            {
                double phi = 2 * Math.PI * i / n;
                vertices.Add(new Vector3D(ring * b * Math.Cos(phi), ring * b * Math.Sin(phi), 0));
            }
        var triangles = new List<(int, int, int)>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            int a1 = 1 + i, b1 = 1 + j, a2 = 1 + n + i, b2 = 1 + n + j;
            triangles.Add((0, a1, b1));
            triangles.Add((a1, a2, b2));
            triangles.Add((a1, b2, b1));
        }
        return (new SurfaceStructure(vertices, triangles, null), 0);
    }

    /// <summary>The rule this fix replaced, kept here so the defect stays measured: 6 × 6 Gauss
    /// per wedge for the outer integral and the SAME 6 × 6 for the inner one.</summary>
    private static double SharedNodeDiscSelf(SurfaceStructure surface, AttachmentFan fan, double a)
    {
        var (nodes, weights) = GaussLegendre.Rule(6, 0, 1);
        var v = fan.VertexPosition;
        var points = new List<(Vector3D R, Vector3D W)>();
        foreach (var wedge in fan.Wedges)
        {
            var (ia, ib, ic) = surface.Triangles[wedge.Triangle];
            var (u, w) = ia == fan.Vertex ? (ib, ic) : ib == fan.Vertex ? (ia, ic) : (ia, ib);
            var eu = surface.Vertices[u] - v;
            var ew = surface.Vertices[w] - v;
            double cross = Vector3D.Cross(eu, ew - eu).Length;
            for (int si = 0; si < nodes.Length; si++)
            {
                var e = eu * (1 - nodes[si]) + ew * nodes[si];
                double scale = cross / (2 * Math.PI * e.LengthSquared);
                for (int ti = 0; ti < nodes.Length; ti++)
                    points.Add((v + e * nodes[ti], e * (scale * weights[si] * weights[ti])));
            }
        }
        double sum = 0;
        foreach (var (r1, w1) in points)
            foreach (var (r2, w2) in points)
                sum += Vector3D.Dot(w1, w2) / Math.Sqrt((r1 - r2).LengthSquared + a * a);
        return sum;
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.03)]
    [InlineData(0.01)]
    [InlineData(0.003)]
    [InlineData(0.001)]
    public void DiscSelf_MatchesTheClosedFormDisc_AtEveryRadius(double ratio)
    {
        const double b = 0.03;
        double a = ratio * b;
        var (surface, centre) = PolygonFan(32, b);
        var fan = new AttachmentFan(surface, centre, a);
        var kernel = new FreeSpaceRadialGaKernel(1e-9);   // static: G = µ₀/(4πρ)

        var watch = Stopwatch.StartNew();
        double value = fan.DiscSelf(kernel, surface).Real / MuOver4Pi;
        watch.Stop();
        double inner = CircularDiscSelf(a, b * Math.Cos(Math.PI / 32));
        double outer = CircularDiscSelf(a, b);
        double before = SharedNodeDiscSelf(surface, fan, a);
        _output.WriteLine($"a/b {ratio}: DiscSelf {value:g7}, circle bracket [{inner:g7}, {outer:g7}], "
            + $"shared-node rule {before:g7} ({before / outer:f2}x), {watch.ElapsedMilliseconds} ms");

        Assert.InRange(value, inner * 0.998, outer * 1.002);
    }

    [Fact]
    public void TheSharedNodeRule_WasWrongByTheFactorsTheAuditMeasured()
    {
        // The defect, pinned so it cannot come back unnoticed. On this 32-wedge fan it reads
        // 1.13× at a/b = 0.03 and 1.85× at 0.003; on a six-wedge mesh fan, with fewer nodes
        // sharing the same disc, the audit's replication measured 1.17× and 3.5×.
        const double b = 0.03;
        var (surface, centre) = PolygonFan(32, b);
        double At(double ratio)
        {
            var fan = new AttachmentFan(surface, centre, ratio * b);
            return SharedNodeDiscSelf(surface, fan, ratio * b) / CircularDiscSelf(ratio * b, b);
        }
        Assert.InRange(At(0.1), 0.95, 1.08);
        Assert.InRange(At(0.03), 1.08, 1.4);
        Assert.True(At(0.003) > 1.5, $"shared-node rule at a/b 0.003: {At(0.003):f2}x");
    }

    [Theory]
    [InlineData(0.03)]
    [InlineData(0.003)]
    public void TheDefaultRules_AgreeWithMuchTighterOnes(double ratio)
    {
        // On a real mesh fan (a structured plate with a snapped vertex, so the wedges are
        // unequal), the shipped orders against higher ones at a finer cell-to-distance ratio.
        // Measured against order 8 at ratio 0.5: 1.4e-4 at a/b = 0.003, 5e-5 at 0.03.
        double edge = 0.03, a = ratio * edge;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(0.3, 0.3, edge, z: 0,
            portFraction: 0, snapVertex: (0.004, -0.007));
        Assert.True(grid.Structure is not null, grid.FailureReason);
        var surface = grid.Structure!;
        int vertex = Enumerable.Range(0, surface.Vertices.Count).MinBy(i =>
            (surface.Vertices[i] - new Vector3D(0.004, -0.007, 0)).Length);
        var fan = new AttachmentFan(surface, vertex, a);
        var kernel = new FreeSpaceRadialGaKernel(2 * Math.PI * 1e9 / 299792458.0);

        var shipped = fan.DiscSelf(kernel, surface);
        var tight = fan.DiscSelf(kernel, surface, outerOrder: 6, sPanels: 2, innerOrder: 6,
            innerResolve: 0.6);
        _output.WriteLine($"a/b {ratio}: self {shipped} vs {tight}");
        Assert.True((shipped - tight).Magnitude <= 2e-3 * tight.Magnitude,
            $"self term {shipped} vs {tight}");

        // The potential itself, at points inside the fan, on its rim and just outside.
        var v = fan.VertexPosition;
        foreach (var offset in new[]
                 {
                     new Vector3D(0.3 * a, 0.2 * a, 0), new Vector3D(2 * a, -a, 0),
                     new Vector3D(0.3 * edge, 0.1 * edge, 0), new Vector3D(-0.5 * edge, 0.45 * edge, 0),
                     new Vector3D(edge, 0.02 * edge, 0), new Vector3D(1.6 * edge, -1.1 * edge, 0)
                 })
        {
            var p = fan.DiscPotential(kernel, surface, v + offset);
            var q = fan.DiscPotential(kernel, surface, v + offset, 8, 0.5);
            double scale = Math.Sqrt(q.Ax.Magnitude * q.Ax.Magnitude + q.Ay.Magnitude * q.Ay.Magnitude);
            double error = Math.Sqrt(Math.Pow((p.Ax - q.Ax).Magnitude, 2)
                + Math.Pow((p.Ay - q.Ay).Magnitude, 2));
            Assert.True(error <= 2e-3 * scale, $"potential at {offset}: off by {error / scale:e2}");
        }
    }

    private static (Complex OnPlate, Complex OverImage) Monopole(double radiusOverLambda,
        double plateWavelengths, int divisions)
    {
        const double f = 300e6, lambda = 299792458.0 / f, height = 0.25 * lambda;
        double radius = radiusOverLambda * lambda;
        var reference = WireGridBuilder.Build(CanonicalAntennas.Monopole(height, radius),
            maxElementLength: height / 10, ground: new GroundPlane(0));
        var image = new ThinWireMomSolver().Solve(reference.Structure!, f,
            reference.Structure!.NearestBasis(Vector3D.Zero)).InputImpedance;

        var grid = SurfaceMeshBuilder.BuildRectangularPlate(plateWavelengths * lambda,
            plateWavelengths * lambda, lambda / divisions, z: 0, portFraction: 0, snapVertex: (0, 0));
        var wire = WireGridBuilder.Build(
            new[] { new WireSegment(Vector3D.Zero, new Vector3D(0, 0, height), radius) },
            maxElementLength: height / 10, attachmentPoint: Vector3D.Zero);
        Assert.True(grid.Structure is not null && wire.Structure is not null);
        var onPlate = new SurfaceMomSolver()
            .SolveWireAttached(grid.Structure!, wire.Structure!, f, 0).InputImpedance;
        return (onPlate, image);
    }

    [Fact]
    public void MonopoleReactance_FollowsTheWireRadius_OnlyAsTheImageMonopoleDoes()
    {
        // The plan's gate. A quarter-wave monopole on a 1λ plate at one mesh (λ/8), with the wire
        // radius changed 16-fold. The only radius dependence physics allows is the wire's own
        // ln(1/a), which the image-theory monopole has too, so the DIFFERENCE between the two
        // must not depend on the radius. Measured −5.86, −4.89, −4.46 Ω (the finite plate). The
        // shared-node self term added about +7, +28 and +110 Ω at these three radii.
        var differences = new List<double>();
        foreach (double radius in new[] { 1.0 / 500, 1.0 / 2000, 1.0 / 8000 })
        {
            var (onPlate, image) = Monopole(radius, 1.0, 8);
            _output.WriteLine($"a = λ·{radius:g3}: plate {onPlate}, image {image}");
            differences.Add(onPlate.Imaginary - image.Imaginary);
            Assert.InRange(onPlate.Real / image.Real, 0.9, 1.1);
        }
        Assert.True(differences.Max() - differences.Min() < 2.5,
            "X(plate) − X(image) by radius: " + string.Join(", ", differences.Select(d => d.ToString("f2"))));
        Assert.All(differences, d => Assert.InRange(d, -8.0, 0.0));
    }

    [Fact]
    public void MonopoleOnALargerPlate_StaysNearTheImageTheoryMonopole()
    {
        // 1.5λ plate at λ/6: measured 38.3 + j18.1 against 41.2 + j22.0. The resistance
        // oscillates about the image value as the plate grows (43.4 at 1λ, 38.3 here), which is
        // edge diffraction; the reactance closes slowly (−4.9 Ω at 1λ, −3.9 Ω here).
        var (onPlate, image) = Monopole(1.0 / 2000, 1.5, 6);
        _output.WriteLine($"plate {onPlate}, image {image}");
        Assert.InRange(onPlate.Real / image.Real, 0.88, 1.12);
        Assert.InRange(onPlate.Imaginary - image.Imaginary, -6.0, 0.0);
    }

    [Fact]
    public void OuterFluxMismatch_IsSeventeenPercentOnAnEquilateralFan_AndLargeOnASkewedOne()
    {
        // Hexagonal fan: every wedge equilateral. D·n against the uniform half-RWG density is
        // +10.3 % at the foot of the perpendicular and −17.3 % at the edge ends.
        var (hex, centre) = PolygonFan(6, 0.01);
        Assert.Equal(1 - Math.Sqrt(3) / 2 * 3 / Math.PI, new AttachmentFan(hex, centre, 1e-4).OuterFluxMismatch, 9);

        // The same fan with its centre pulled to a fifth of the way from one rim vertex.
        var vertices = hex.Vertices.ToList();
        vertices[0] = vertices[1] * 0.8;
        var skewed = new SurfaceStructure(vertices, hex.Triangles.ToList(), null);
        Assert.True(new AttachmentFan(skewed, 0, 1e-4).OuterFluxMismatch > 0.5);
    }
}
