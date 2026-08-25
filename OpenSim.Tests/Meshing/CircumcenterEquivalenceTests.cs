using OpenSim.Core.Numerics;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// The circumcenter solve lost its array allocations. That is only worth doing if it changed
/// nothing: it feeds the refiner's Steiner-point placement, the sliver cull and the smoother's
/// accept/reject decision, so a shift in the last bit could flip which tetrahedra a mesh keeps.
///
/// The retired array form is reproduced here as the oracle. Both evaluate Cramer's rule with
/// the same expansion, so the gate is bitwise equality — a tolerance here would be admitting
/// the mesh might come out different.
/// </summary>
public class CircumcenterEquivalenceTests
{
    /// <summary>The implementation as it was: a matrix, a right-hand side, and a clone per
    /// column.</summary>
    private static Vector3D? ArrayFormCircumcenter(Vector3D a, Vector3D b, Vector3D c, Vector3D d)
    {
        var ba = b - a; var ca = c - a; var da = d - a;
        double[,] m =
        {
            { ba.X, ba.Y, ba.Z },
            { ca.X, ca.Y, ca.Z },
            { da.X, da.Y, da.Z }
        };
        double[] rhs =
        {
            0.5 * (b.LengthSquared - a.LengthSquared),
            0.5 * (c.LengthSquared - a.LengthSquared),
            0.5 * (d.LengthSquared - a.LengthSquared)
        };

        static double Det3(double[,] x) =>
            x[0, 0] * (x[1, 1] * x[2, 2] - x[1, 2] * x[2, 1])
          - x[0, 1] * (x[1, 0] * x[2, 2] - x[1, 2] * x[2, 0])
          + x[0, 2] * (x[1, 0] * x[2, 1] - x[1, 1] * x[2, 0]);

        static double Replaced(double[,] x, double[] r, int column)
        {
            var copy = (double[,])x.Clone();
            for (int i = 0; i < 3; i++) copy[i, column] = r[i];
            return Det3(copy);
        }

        double det = Det3(m);
        if (Math.Abs(det) < 1e-300) return null;

        return new Vector3D(
            Replaced(m, rhs, 0) / det,
            Replaced(m, rhs, 1) / det,
            Replaced(m, rhs, 2) / det);
    }

    [Fact]
    public void TheAllocationFreeCircumcenter_IsBitwiseTheArrayForm()
    {
        var random = new Random(20250824);

        for (int trial = 0; trial < 5000; trial++)
        {
            // A spread of scales, because the determinant's conditioning depends on them.
            double scale = Math.Pow(10, random.NextDouble() * 6 - 3);
            Vector3D Point() => new(
                (random.NextDouble() * 2 - 1) * scale,
                (random.NextDouble() * 2 - 1) * scale,
                (random.NextDouble() * 2 - 1) * scale);

            var a = Point(); var b = Point(); var c = Point(); var d = Point();

            var expected = ArrayFormCircumcenter(a, b, c, d);
            var actual = MeshQuality.Circumcenter(a, b, c, d);

            Assert.Equal(expected.HasValue, actual.HasValue);
            if (!expected.HasValue) continue;

            Assert.Equal(expected!.Value.X, actual!.Value.X);
            Assert.Equal(expected.Value.Y, actual.Value.Y);
            Assert.Equal(expected.Value.Z, actual.Value.Z);
        }
    }

    /// <summary>Degenerate inputs still decline rather than returning a number — the guard the
    /// refiner relies on to leave a flat tetrahedron alone.</summary>
    [Fact]
    public void ADegenerateTetrahedron_StillHasNoCircumcenter()
    {
        // All four points coplanar.
        var a = new Vector3D(0, 0, 0);
        var b = new Vector3D(1, 0, 0);
        var c = new Vector3D(0, 1, 0);
        var d = new Vector3D(1, 1, 0);

        Assert.Null(MeshQuality.Circumcenter(a, b, c, d));
        Assert.Null(ArrayFormCircumcenter(a, b, c, d));
    }

    /// <summary>A regular tetrahedron is equidistant from its circumcenter — the sanity check
    /// that says both forms compute a circumcenter at all, not merely the same number.</summary>
    [Fact]
    public void TheCircumcenterIsEquidistantFromEveryVertex()
    {
        var a = new Vector3D(1, 1, 1);
        var b = new Vector3D(1, -1, -1);
        var c = new Vector3D(-1, 1, -1);
        var d = new Vector3D(-1, -1, 1);

        var centre = MeshQuality.Circumcenter(a, b, c, d);
        Assert.NotNull(centre);

        double r = Vector3D.Distance(centre!.Value, a);
        Assert.Equal(r, Vector3D.Distance(centre.Value, b), 12);
        Assert.Equal(r, Vector3D.Distance(centre.Value, c), 12);
        Assert.Equal(r, Vector3D.Distance(centre.Value, d), 12);
        Assert.Equal(0.0, centre.Value.X, 12);
        Assert.Equal(0.0, centre.Value.Y, 12);
        Assert.Equal(0.0, centre.Value.Z, 12);
    }
}
