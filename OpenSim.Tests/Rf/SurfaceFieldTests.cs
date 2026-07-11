using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Rf;
using OpenSim.Rf.Surface;
using Xunit;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Ground-plane surfaces (the RWG image sign nailed by the strip-monopole ≡ ½
/// strip-dipole DISCRETE identity + the hemisphere power balance), the strip loop
/// against the small-loop limit, the polygon (copper-island) meshing gates, and the
/// builder's typed failures.
/// </summary>
public class SurfaceFieldTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;

    /// <summary>A vertical strip in the xz plane (y = 0), one cell across, consistent
    /// +y normals, alternating diagonals — bitwise-matched discretizations for the
    /// monopole/dipole identity (rows must be EVEN so the dipole's diagonal parity
    /// continues the monopole's across z = 0, and mirroring flips a cell's diagonal
    /// exactly as the alternation does).</summary>
    private static SurfaceStructure VerticalStrip(double width, double zMin, double zMax,
        int rows, GroundPlane? ground)
    {
        var vertices = new List<Vector3D>(2 * (rows + 1));
        for (int j = 0; j <= rows; j++)
        {
            double z = zMin + (zMax - zMin) * j / rows;
            vertices.Add(new Vector3D(-width / 2, 0, z));
            vertices.Add(new Vector3D(width / 2, 0, z));
        }
        int Index(int i, int j) => 2 * j + i;
        var triangles = new List<(int, int, int)>(2 * rows);
        for (int j = 0; j < rows; j++)
        {
            int v00 = Index(0, j), v10 = Index(1, j);
            int v01 = Index(0, j + 1), v11 = Index(1, j + 1);
            if (j % 2 == 0)
            {
                triangles.Add((v00, v11, v10));
                triangles.Add((v00, v01, v11));
            }
            else
            {
                triangles.Add((v00, v01, v10));
                triangles.Add((v10, v01, v11));
            }
        }
        return new SurfaceStructure(vertices, triangles, ground);
    }

    private static int EdgeBasis(SurfaceStructure s, int v1, int v2)
    {
        for (int e = 0; e < s.Edges.Count; e++)
            if (s.Edges[e].V1 == Math.Min(v1, v2) && s.Edges[e].V2 == Math.Max(v1, v2))
                return e;
        throw new InvalidOperationException("expected edge not found");
    }

    [Fact]
    public void StripMonopole_InputImpedance_IsExactlyHalfTheStripDipoles()
    {
        // The surface image sign, nailed at machine precision: the monopole+image
        // system is the exact symmetric reduction of the dipole system (bitwise-matched
        // node z = ±j·dz), so Z_mono = Z_dip/2 up to summation-order roundoff. The
        // grounded rim edge's half-RWG + its image IS the dipole's center RWG.
        double h = 0.25 * Lambda, width = Lambda / 100;
        int rows = 10;
        var monopole = VerticalStrip(width, 0, h, rows, new GroundPlane(0));
        var dipole = VerticalStrip(width, -h, h, 2 * rows, null);

        // The monopole's grounded base edge exists and is its port.
        int monoPort = EdgeBasis(monopole, 0, 1);
        Assert.Equal(-1, monopole.Edges[monoPort].MinusTriangle);
        int dipolePort = EdgeBasis(dipole, 2 * rows, 2 * rows + 1);   // the z = 0 row

        var solver = new SurfaceMomSolver();
        var monoSolution = solver.Solve(monopole, Frequency,
            new SurfacePort(new[] { monoPort }, new Vector3D(0, 0, 1)));
        var dipSolution = solver.Solve(dipole, Frequency,
            new SurfacePort(new[] { dipolePort }, new Vector3D(0, 0, 1)));

        Complex half = dipSolution.InputImpedance / 2;
        Assert.True((monoSolution.InputImpedance - half).Magnitude <= 1e-8 * half.Magnitude,
            $"Z_mono = {monoSolution.InputImpedance} vs Z_dip/2 = {half} " +
            $"(rel {(monoSolution.InputImpedance - half).Magnitude / half.Magnitude:g2})");
    }

    [Fact]
    public void StripMonopole_HemispherePowerBalance_Holds()
    {
        double h = 0.25 * Lambda, width = Lambda / 100;
        var monopole = VerticalStrip(width, 0, h, 10, new GroundPlane(0));
        int port = EdgeBasis(monopole, 0, 1);
        var solution = new SurfaceMomSolver().Solve(monopole, Frequency,
            new SurfacePort(new[] { port }, new Vector3D(0, 0, 1)));

        double inputPower = 0.5 * (Complex.One / solution.InputImpedance).Real;
        var pattern = SurfaceFarFieldEvaluator.Compute(monopole, solution);
        Assert.InRange(pattern.TotalRadiatedPowerWatts / inputPower, 0.98, 1.02);

        // Image-theory doubling, like the wire monopole.
        Assert.InRange(pattern.MaxDirectivity, 0.96 * 3.286, 1.04 * 3.286);
    }

    [Fact]
    public void SurfaceFieldProbe_ReturnsExactlyZeroBelowThePlane()
    {
        double h = 0.25 * Lambda, width = Lambda / 100;
        var monopole = VerticalStrip(width, 0, h, 10, new GroundPlane(0));
        int port = EdgeBasis(monopole, 0, 1);
        var solution = new SurfaceMomSolver().Solve(monopole, Frequency,
            new SurfacePort(new[] { port }, new Vector3D(0, 0, 1)));
        var map = SurfaceFieldProbe.Evaluate(monopole, solution, new[]
        {
            new Vector3D(0.3 * Lambda, 0, -0.1 * Lambda),
            new Vector3D(0.3 * Lambda, 0, 0.15 * Lambda)
        });
        Assert.Equal(0.0, map.Magnitude[0]);
        Assert.True(map.Magnitude[1] > 0);
    }

    // ------------------------------------------------------------------
    // Strip loop (polygon-with-hole meshing + small-loop physics)
    // ------------------------------------------------------------------

    private static Polygon2 SquareRing(double midSide, double stripWidth)
    {
        double outer = midSide / 2 + stripWidth / 2;
        double inner = midSide / 2 - stripWidth / 2;
        static IReadOnlyList<Point2> Square(double halfSide, bool ccw)
        {
            var ring = new List<Point2>
            {
                new(halfSide, -halfSide), new(halfSide, halfSide),
                new(-halfSide, halfSide), new(-halfSide, -halfSide)
            };
            if (!ccw) ring.Reverse();
            return ring;
        }
        return new Polygon2(Square(outer, ccw: true), new[] { Square(inner, ccw: false) });
    }

    [Fact]
    public void StripLoop_RadiationResistance_SitsOnTheHighSideOfTheSmallLoopLimit()
    {
        // R = 320π⁴(A/λ²)² is the uniform-current small-loop limit with A the
        // CENTERLINE area; a fed loop's current is slightly non-uniform and RWG strips
        // carry width effects — the MoM value sits on the HIGH side, mirroring the
        // wire-loop gate's one-sided band. C = 4·midSide = 0.1λ, where the wire loop
        // measured 1.13× (at 0.16λ this strip measured 1.37× — the (kC)² trend).
        double midSide = Lambda / 40, width = Lambda / 400;
        var ring = SquareRing(midSide, width);
        var grid = SurfaceMeshBuilder.BuildFromPolygon(ring, maxEdgeLength: width, z: 0,
            feedHint: new Point2(midSide / 2, 0));
        Assert.NotNull(grid.Structure);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, Frequency, grid.Port!);

        double area = midSide * midSide;
        double expected = 320 * Math.Pow(Math.PI, 4) * Math.Pow(area / (Lambda * Lambda), 2);
        Assert.InRange(solution.InputImpedance.Real, 0.98 * expected, 1.30 * expected);
        Assert.True(solution.InputImpedance.Imaginary > 0,
            $"X = {solution.InputImpedance.Imaginary:g4} — a small loop must be inductive");

        // Power balance holds on the unstructured mesh too.
        double inputPower = 0.5 * (Complex.One / solution.InputImpedance).Real;
        var pattern = SurfaceFarFieldEvaluator.Compute(grid.Structure!, solution);
        Assert.InRange(pattern.TotalRadiatedPowerWatts / inputPower, 0.98, 1.02);
    }

    [Fact]
    public void PolygonMesh_IsAreaFaithful_AndBitwiseRepeatable()
    {
        double midSide = Lambda / 40, width = Lambda / 400;
        var ring = SquareRing(midSide, width);
        var first = SurfaceMeshBuilder.BuildFromPolygon(ring, width, 0, new Point2(midSide / 2, 0));
        var second = SurfaceMeshBuilder.BuildFromPolygon(ring, width, 0, new Point2(midSide / 2, 0));
        Assert.NotNull(first.Structure);

        double outer = midSide + width, inner = midSide - width;
        double exactArea = outer * outer - inner * inner;
        Assert.InRange(first.Structure!.TotalArea(), 0.995 * exactArea, 1.005 * exactArea);

        // Bitwise determinism: identical vertices and triangles across builds.
        Assert.Equal(first.Structure.Vertices.Count, second.Structure!.Vertices.Count);
        for (int i = 0; i < first.Structure.Vertices.Count; i++)
            Assert.Equal(first.Structure.Vertices[i], second.Structure.Vertices[i]);
        Assert.Equal(first.Structure.Triangles, second.Structure.Triangles);

        // Every triangle edge lies on 1 (boundary) or 2 (interior) triangles — the
        // manifoldness the SurfaceStructure constructor enforces; reaching here without
        // a typed failure plus a nonzero basis count is the gate.
        Assert.True(first.Structure.BasisCount > 0);
    }

    // ------------------------------------------------------------------
    // Typed failures
    // ------------------------------------------------------------------

    [Fact]
    public void Builders_FailTyped_OnCapHeightAndDegeneracy()
    {
        var capped = SurfaceMeshBuilder.BuildRectangularPlate(
            Lambda, Lambda, Lambda / 100, maxUnknowns: 50);
        Assert.Null(capped.Structure);
        Assert.Contains("cap", capped.FailureReason);

        var buried = SurfaceMeshBuilder.BuildPatchOverGround(
            Lambda / 10, Lambda / 10, heightAboveGround: -0.01, groundZ: 0, Lambda / 20);
        Assert.Null(buried.Structure);
        Assert.Contains("height", buried.FailureReason);

        var flat = SurfaceMeshBuilder.BuildRectangularPlate(
            Lambda / 10, Lambda / 10, Lambda / 20, z: 0, ground: new GroundPlane(0));
        Assert.Null(flat.Structure);
        Assert.Contains("ground plane", flat.FailureReason);
    }

    [Fact]
    public void SurfaceStructure_RejectsInconsistentOrientation_Typed()
    {
        // Two triangles sharing edge (0,1) traversed the SAME way — a flipped mesh.
        var vertices = new[]
        {
            new Vector3D(0, 0, 0), new Vector3D(1, 0, 0),
            new Vector3D(0, 1, 0), new Vector3D(0, -1, 0)
        };
        var ex = Assert.Throws<InvalidOperationException>(() => new SurfaceStructure(
            vertices, new[] { (0, 1, 2), (0, 1, 3) }, null));
        Assert.Contains("non-manifold or inconsistently oriented", ex.Message);
    }
}
