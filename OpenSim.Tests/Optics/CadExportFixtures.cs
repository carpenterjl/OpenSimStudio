using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Export;
using OpenSim.Geometry.Step.Schema;
using OpenSim.Optics;
using OpenSim.Optics.CadExport;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-96 fixtures. The lens data (radii, thicknesses, semi-diameters, index, asphere terms) are
/// arbitrary inputs; every expected value in the tests is derived in closed form here or computed
/// by an independent routine (ElementVolume, a dense meridian quadrature), never typed in.
/// </summary>
internal static class CadExportFixtures
{
    public const double R1 = 0.050, R2 = -0.200, T = 0.006, Semi = 0.0125;

    public static readonly DateTime Stamp = new(2026, 11, 15, 12, 0, 0);

    /// <summary>A biconvex singlet, R1 = 50 mm, R2 = −200 mm, 6 mm thick, semi-diameters 12.5 mm
    /// (fixed, so no ray is traced to size them), as the project saves it (millimeters).</summary>
    public static OpticsSettings SingletSettings(double? backSemiMm = null, double? mechanicalMm = null) => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 20, Title = "singlet" },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = R1 * 1e3, Thickness = T * 1e3, Index = 1.6, IsStop = true, SemiDiameter = Semi * 1e3, MechanicalSemiDiameter = mechanicalMm },
            new() { Radius = R2 * 1e3, Thickness = 90, SemiDiameter = backSemiMm ?? Semi * 1e3 },
            new(),
        },
    };

    public static SequentialSurface Sphere(double radius, double thickness, double index, double semi, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius),
        Thickness = thickness,
        Medium = index == 1 ? ParaxialMedium.Air : ParaxialMedium.Constant(index),
        SemiDiameter = semi,
        IsStop = stop,
    };

    public static SequentialLens Lens(params SequentialSurface[] middle)
    {
        var all = new List<SequentialSurface> { new() { Thickness = double.PositiveInfinity } };
        all.AddRange(middle);
        all.Add(new SequentialSurface());
        return new SequentialLens { Surfaces = all };
    }

    /// <summary>The singlet with an Even Asphere front: R = 50 mm, k = −0.5, α2 = −1000 m⁻³ on r⁴.</summary>
    public static SequentialLens AsphereSinglet() => Lens(
        Sphere(R1, T, 1.6, Semi, stop: true) with { Conic = -0.5, Shape = new EvenAsphere(new[] { 0.0, -1000.0 }) },
        Sphere(R2, 0.09, 1, Semi));

    public static SequentialLens Singlet() => Lens(Sphere(R1, T, 1.6, Semi, stop: true), Sphere(R2, 0.09, 1, Semi));

    public static CadExportSettings GeometryOnly(CadExportFormat format = CadExportFormat.Step) =>
        new() { Format = format, RayPattern = CadRayPattern.None };

    public static CadScene Scene(SequentialLens lens, CadExportSettings settings) =>
        CadGeometryBuilder.Build(lens, OpticalSystemData.Default, settings, new List<string>());

    // ---------------- closed forms (derived) ----------------

    /// <summary>|sag| of a sphere of radius |R| at r: h = |R| − √(R² − r²).</summary>
    public static double CapHeight(double radius, double r) => Math.Abs(radius) - Math.Sqrt(radius * radius - r * r);

    /// <summary>
    /// ∫₀^a 2πr·sag(r) dr for a sphere of signed radius R: the cylinder over the rim less the cap,
    /// sign(R)·(π a² h − π h²(3|R| − h)/3), with h the cap height (derived: the cap between the
    /// surface and its chord plane has the textbook volume π h²(3|R| − h)/3, and the region between
    /// the vertex plane and the surface is the cylinder π a² h minus it).
    /// </summary>
    public static double SagIntegral(double radius, double a)
    {
        double h = CapHeight(radius, a);
        return Math.Sign(radius) * (Math.PI * a * a * h - Math.PI * h * h * (3 * Math.Abs(radius) - h) / 3);
    }

    /// <summary>The singlet's volume, π a² t + W(back) − W(front) (both semi-diameters a).</summary>
    public static double SingletVolume() => Math.PI * Semi * Semi * T + SagIntegral(R2, Semi) - SagIntegral(R1, Semi);

    /// <summary>The sag of a sphere of signed radius R at r (the rationalised root).</summary>
    public static double SphereSag(double radius, double r)
    {
        double c = 1 / radius;
        return c * r * r / (1 + Math.Sqrt(1 - c * c * r * r));
    }

    /// <summary>The edge thickness of the singlet at its semi-diameter.</summary>
    public static double SingletEdge() => T + SphereSag(R2, Semi) - SphereSag(R1, Semi);

    /// <summary>
    /// The volume of a closed body of revolution from a dense polygon of its exact meridian: the sum
    /// of frusta π(rᵢ² + rᵢrᵢ₊₁ + rᵢ₊₁²)(zᵢ₊₁ − zᵢ)/3 (Pappus for each straight piece, derived), with
    /// every cap sampled at <paramref name="samples"/> radii; the error of the polygon falls as the
    /// square of the spacing.
    /// </summary>
    public static double MeridianVolume(RevolvedBody body, int samples = 20000)
    {
        var pts = new List<(double R, double Z)>();
        foreach (var piece in body.Pieces)
        {
            var local = new List<(double R, double Z)>();
            if (piece is CapPiece cap)
            {
                for (int k = 0; k <= samples; k++)
                {
                    double r = cap.RimRadius * k / samples;
                    local.Add((r, cap.VertexZ + cap.Shape.Sag(r)));
                }
                if (cap.TowardAxis) local.Reverse();
            }
            else local.AddRange(new[] { piece.Start, piece.End });
            if (pts.Count > 0) local.RemoveAt(0);
            pts.AddRange(local);
        }
        double v = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var (ra, za) = pts[i];
            var (rb, zb) = pts[i + 1];
            v += Math.PI * (ra * ra + ra * rb + rb * rb) * (zb - za) / 3;
        }
        return Math.Abs(v);
    }

    /// <summary>The total area of a closed body of revolution, Σ π(rᵢ + rᵢ₊₁)·|segment| over the
    /// same dense meridian polygon (derived: the lateral area of each frustum).</summary>
    public static double MeridianArea(RevolvedBody body, int samples = 20000)
    {
        double area = 0;
        foreach (var piece in body.Pieces)
        {
            var pts = new List<(double R, double Z)>();
            if (piece is CapPiece cap)
                for (int k = 0; k <= samples; k++)
                {
                    double r = cap.RimRadius * k / samples;
                    pts.Add((r, cap.VertexZ + cap.Shape.Sag(r)));
                }
            else pts.AddRange(new[] { piece.Start, piece.End });
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var (ra, za) = pts[i];
                var (rb, zb) = pts[i + 1];
                area += Math.PI * (ra + rb) * Math.Sqrt((rb - ra) * (rb - ra) + (zb - za) * (zb - za));
            }
        }
        return area;
    }

    public static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), "opensim-cad-tests", Guid.NewGuid().ToString("N") + extension);

    public static double Area(TriangleMesh mesh, Func<int, bool> face) =>
        Enumerable.Range(0, mesh.Triangles.Count).Where(t => face(mesh.TriangleFaceIds[t])).Sum(mesh.TriangleArea);

    // ---------------- STEP topology helpers ----------------

    /// <summary>Points along an edge use, start to end, from the resolved curve.</summary>
    public static List<Vector3D> Sample(StepEdgeUse use, int samples = 16)
    {
        var e = use.Edge;
        var pts = new List<Vector3D>();
        double t0 = e.Curve.ParameterOf(e.Start.Point);
        double t1;
        if (e.IsRing) t1 = t0 + (e.CurveSameSense ? 2 * Math.PI : -2 * Math.PI);
        else
        {
            t1 = e.Curve.ParameterOf(e.End.Point);
            if (e.Curve.Period is double p)
            {
                if (e.CurveSameSense) while (t1 <= t0) t1 += p;
                else while (t1 >= t0) t1 -= p;
            }
        }
        for (int k = 0; k < samples; k++) pts.Add(e.Curve.Point(t0 + (t1 - t0) * k / samples));
        pts.Add(e.Curve.Point(t1));
        if (!use.Forward) pts.Reverse();
        return pts;
    }

    /// <summary>A loop's vector area (Newell).</summary>
    public static Vector3D VectorArea(StepLoop loop)
    {
        var pts = new List<Vector3D>();
        foreach (var use in loop.Edges)
        {
            var s = Sample(use);
            pts.AddRange(s.Take(s.Count - 1));
        }
        var area = Vector3D.Zero;
        for (int i = 0; i < pts.Count; i++) area += Vector3D.Cross(pts[i], pts[(i + 1) % pts.Count]);
        return area * 0.5;
    }

    public static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
