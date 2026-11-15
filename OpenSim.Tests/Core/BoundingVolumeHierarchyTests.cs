using OpenSim.Core.Geometry;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Core;

/// <summary>OP-70: the public first-hit bounding-volume hierarchy and the robust ray origin
/// (Pharr, Jakob and Humphreys, Physically Based Rendering, 4th ed., sections 6.8.2, 6.8.6, 7.3).</summary>
public sealed class BoundingVolumeHierarchyTests
{
    private struct NearestSphere : IBvhRayVisitor
    {
        public Vector3D[] Centers;
        public double Radius;
        public Vector3D Origin, Direction;
        public double Best;
        public int BestIndex;
        public int Visits;

        public void Visit(int primitive, ref double tMax)
        {
            Visits++;
            double t = Hit(Centers[primitive], Radius, Origin, Direction);
            if (t > 0 && t < Best) { Best = t; BestIndex = primitive; tMax = Math.Min(tMax, t); }
        }
    }

    /// <summary>The nearer root of |o + t·d − c|² = r² for a unit d (the test's own closed form).</summary>
    private static double Hit(Vector3D c, double r, Vector3D o, Vector3D d)
    {
        var oc = o - c;
        double b = Vector3D.Dot(oc, d), disc = b * b - (oc.LengthSquared - r * r);
        if (disc < 0) return double.PositiveInfinity;
        double t0 = -b - Math.Sqrt(disc), t1 = -b + Math.Sqrt(disc);
        return t0 > 0 ? t0 : t1 > 0 ? t1 : double.PositiveInfinity;
    }

    [Fact]
    public void FirstHit_IsTheBruteForceNearest_ForRandomSpheresAndRays_AndPrunes()
    {
        var rng = new SplitMix64(42);
        int n = 500;
        double radius = 0.02;
        var centers = new Vector3D[n];
        var boxes = new Aabb[n];
        for (int i = 0; i < n; i++)
        {
            centers[i] = new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble());
            boxes[i] = new Aabb(centers[i] - new Vector3D(radius, radius, radius), centers[i] + new Vector3D(radius, radius, radius));
        }
        var bvh = new BoundingVolumeHierarchy(boxes);
        Assert.Equal(n, bvh.Count);
        int hits = 0;
        long visits = 0;
        for (int k = 0; k < 2000; k++)
        {
            var o = new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()) * 1.2 - new Vector3D(0.1, 0.1, 0.1);
            var d = new Vector3D(rng.NextNormal(), rng.NextNormal(), rng.NextNormal()).Normalized();
            double best = double.PositiveInfinity;
            int bestIndex = -1;
            for (int i = 0; i < n; i++)
            {
                double t = Hit(centers[i], radius, o, d);
                if (t < best) { best = t; bestIndex = i; }
            }
            var v = new NearestSphere { Centers = centers, Radius = radius, Origin = o, Direction = d, Best = double.PositiveInfinity, BestIndex = -1 };
            bvh.Traverse(o, d, 0, double.PositiveInfinity, ref v);
            Assert.Equal(bestIndex, v.BestIndex);
            if (bestIndex >= 0) { Assert.Equal(best, v.Best); hits++; }
            visits += v.Visits;
        }
        Assert.True(hits > 100);
        Assert.True(visits < 2000L * n / 10, $"{visits} visits: the tree is not pruning");
    }

    [Fact]
    public void TheRayBoxTest_KeepsGrazingRays_AlongAFaceAndThroughAnEdge()
    {
        var box = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1));
        // Along the face y = 1 (zero y component, origin on the slab boundary).
        Assert.True(BoundingVolumeHierarchy.RayBox(new Vector3D(-1, 1, 0.5), Vector3D.UnitX, box, 0, double.PositiveInfinity, out var t0, out var t1));
        Assert.Equal(1, t0);
        Assert.True(t1 >= 2);
        // Through the edge x = 1, z = 0 at 45°.
        var d = new Vector3D(1, 0, -1).Normalized();
        Assert.True(BoundingVolumeHierarchy.RayBox(new Vector3D(0, 0.5, 1), d, box, 0, double.PositiveInfinity, out _, out _));
        Assert.False(BoundingVolumeHierarchy.RayBox(new Vector3D(2, 0.5, 0.5), Vector3D.UnitX, box, 0, double.PositiveInfinity, out _, out _));
    }

    [Fact]
    public void TheOffsetOrigin_LeavesTheErrorBox_OnTheSideTheRayLeavesToward()
    {
        var p = new Vector3D(0.1, -0.2, 0.3);
        var n = new Vector3D(1, 2, 2) / 3;
        var error = RobustRayOrigin.ErrorBox(0.6);
        double d = Math.Abs(n.X) * error.X + Math.Abs(n.Y) * error.Y + Math.Abs(n.Z) * error.Z;
        var outward = RobustRayOrigin.Offset(p, error, n, n);
        var inward = RobustRayOrigin.Offset(p, error, n, -n);
        Assert.True(Vector3D.Dot(outward - p, n) >= d * 0.999, "outward offset too small");
        Assert.True(Vector3D.Dot(inward - p, n) <= -d * 0.999, "inward offset too small");
        Assert.True((outward - p).Length < 1e-13, "the offset is a few rounding units, not a visible shift");
        Assert.Equal(32 * Math.Pow(2, -53) / (1 - 32 * Math.Pow(2, -53)), BoundingVolumeHierarchy.Gamma(32));
    }
}
