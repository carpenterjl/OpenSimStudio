using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step;
using OpenSim.Geometry.Step.Schema;
using OpenSim.Geometry.Step.Tessellate;
using Xunit;

namespace OpenSim.Tests.Geometry.Step;

public class NurbsTests
{
    /// <summary>Degree-2 rational quarter circle, radius r, in the XY plane.</summary>
    private static StepBSplineCurve QuarterCircle(double r) => new(
        Id: 1, Degree: 2,
        ControlPoints: new[] { new Vector3D(r, 0, 0), new Vector3D(r, r, 0), new Vector3D(0, r, 0) },
        Knots: new[] { 0.0, 0, 0, 1, 1, 1 },
        Weights: new[] { 1.0, Math.Sqrt(2) / 2, 1.0 });

    [Fact]
    public void RationalQuarterCircle_LiesOnTheExactCircle()
    {
        var c = QuarterCircle(2.5);
        for (int i = 0; i <= 32; i++)
        {
            double t = i / 32.0;
            var p = c.Point(t);
            Assert.Equal(2.5, Math.Sqrt(p.X * p.X + p.Y * p.Y), 12);
            Assert.Equal(0.0, p.Z, 15);
        }
        // Clamped ends are the control endpoints exactly.
        Assert.Equal(0.0, Vector3D.Distance(c.Point(0), new Vector3D(2.5, 0, 0)), 15);
        Assert.Equal(0.0, Vector3D.Distance(c.Point(1), new Vector3D(0, 2.5, 0)), 15);
    }

    [Fact]
    public void CurveDerivative_MatchesCentralDifference()
    {
        var c = QuarterCircle(1.0);
        const double h = 1e-6;
        for (int i = 1; i < 16; i++)
        {
            double t = i / 16.0;
            var analytic = c.Derivative(t);
            var numeric = (c.Point(t + h) - c.Point(t - h)) / (2 * h);
            Assert.True(Vector3D.Distance(analytic, numeric) < 1e-5 * analytic.Length,
                $"derivative mismatch at t={t}");
        }
    }

    [Fact]
    public void CurveParameterOf_RecoversEndpointAndInteriorParameters()
    {
        var c = QuarterCircle(1.0);
        Assert.Equal(0.0, c.ParameterOf(new Vector3D(1, 0, 0)), 10);
        Assert.Equal(1.0, c.ParameterOf(new Vector3D(0, 1, 0)), 10);
        var mid = c.Point(0.37);
        Assert.Equal(0.37, c.ParameterOf(mid), 10);
    }

    [Fact]
    public void RationalQuarterCylinderSurface_AllPointsOnRadius_AndPartialsMatch()
    {
        // Degree 2×1: the quarter-circle rows extruded from z = 0 to z = 3.
        double w = Math.Sqrt(2) / 2;
        var s = new StepBSplineSurface(
            Id: 2, DegreeU: 2, DegreeV: 1, CountU: 3, CountV: 2,
            ControlPoints: new[]
            {
                new Vector3D(1, 0, 0), new Vector3D(1, 0, 3),
                new Vector3D(1, 1, 0), new Vector3D(1, 1, 3),
                new Vector3D(0, 1, 0), new Vector3D(0, 1, 3)
            },
            KnotsU: new[] { 0.0, 0, 0, 1, 1, 1 },
            KnotsV: new[] { 0.0, 0, 1, 1 },
            Weights: new[] { 1.0, 1.0, w, w, 1.0, 1.0 });

        const double h = 1e-6;
        for (int i = 0; i <= 8; i++)
        for (int j = 0; j <= 4; j++)
        {
            double u = i / 8.0, v = j / 4.0;
            var p = s.Point(u, v);
            Assert.Equal(1.0, Math.Sqrt(p.X * p.X + p.Y * p.Y), 12);
            Assert.Equal(3.0 * v, p.Z, 12);

            if (i > 0 && i < 8 && j > 0 && j < 4)
            {
                var du = (s.Point(u + h, v) - s.Point(u - h, v)) / (2 * h);
                var dv = (s.Point(u, v + h) - s.Point(u, v - h)) / (2 * h);
                Assert.True(Vector3D.Distance(s.PartialU(u, v), du) < 1e-5 * du.Length);
                Assert.True(Vector3D.Distance(s.PartialV(u, v), dv) < 1e-5 * dv.Length);
            }
        }
    }

    [Fact]
    public void SurfaceInvertNear_RoundTripsAnInteriorPoint()
    {
        double w = Math.Sqrt(2) / 2;
        var s = new StepBSplineSurface(
            Id: 3, DegreeU: 2, DegreeV: 1, CountU: 3, CountV: 2,
            ControlPoints: new[]
            {
                new Vector3D(1, 0, 0), new Vector3D(1, 0, 3),
                new Vector3D(1, 1, 0), new Vector3D(1, 1, 3),
                new Vector3D(0, 1, 0), new Vector3D(0, 1, 3)
            },
            KnotsU: new[] { 0.0, 0, 0, 1, 1, 1 },
            KnotsV: new[] { 0.0, 0, 1, 1 },
            Weights: new[] { 1.0, 1.0, w, w, 1.0, 1.0 });

        var p = s.Point(0.63, 0.41);
        var inverted = s.InvertNear(p, 0.5, 0.5, acceptTolMeters: 1e-10);
        Assert.NotNull(inverted);
        Assert.Equal(0.0, Vector3D.Distance(s.Point(inverted.Value.U, inverted.Value.V), p), 10);
    }

    [Fact]
    public void ExpandKnots_CountMismatch_FailsNamingTheInstance()
    {
        // 3 control points, degree 2 → need 6 flat knots; provide 5.
        var ex = Assert.Throws<StepImportException>(() =>
            Nurbs.ExpandKnots(new long[] { 3, 2 }, new[] { 0.0, 1.0 }, 3, 2, id: 77));
        Assert.Contains("#77", ex.Message);
        Assert.Contains("6", ex.Message);
    }

    [Fact]
    public void ExpandKnots_NonIncreasingKnots_Fail()
    {
        var ex = Assert.Throws<StepImportException>(() =>
            Nurbs.ExpandKnots(new long[] { 3, 3 }, new[] { 1.0, 1.0 }, 3, 2, id: 78));
        Assert.Contains("strictly increasing", ex.Message);
    }
}
