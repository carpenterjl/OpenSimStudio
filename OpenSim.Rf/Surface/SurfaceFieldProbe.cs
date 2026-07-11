using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// Near-field evaluation from solved RWG currents: E = −jωA − ∇Φ with
/// A ∝ Σ_T ∫ J g dS′ and the surface charge σ = −(1/jω)·div J, piecewise CONSTANT per
/// triangle (RWG divergence is constant — the surface analog of the wire probe's
/// piecewise-constant line charge). Triangles close to a sample point are subdivided
/// (two 1:4 levels) and evaluated with a slightly regularized kernel
/// R ← √(R² + ε²), ε = diameter/50 — points ON the metal return surface-scale
/// approximations rather than NaN, exactly the wire probe's documented behavior.
/// Points at or below a ground plane return exactly E = 0 (PEC interior); above it,
/// image triangles contribute with (−Jx, −Jy, +Jz) and NEGATED charge (div flips with
/// the image sign). Reuses <see cref="FieldMap"/>.
/// </summary>
public static class SurfaceFieldProbe
{
    public static FieldMap Evaluate(SurfaceStructure surface, SurfaceMomSolution solution,
        IReadOnlyList<Vector3D> points)
    {
        double omega = 2 * Math.PI * solution.FrequencyHz;
        double k = omega / RfConstants.SpeedOfLight;
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(5);

        var fields = new (Complex X, Complex Y, Complex Z)[points.Count];
        var magnitudes = new double[points.Count];
        var snapshots = new Vector3D[points.Count];

        for (int p = 0; p < points.Count; p++)
        {
            var point = points[p];
            if (surface.Ground is { } plane && point.Z <= plane.SurfaceZ)
            {
                fields[p] = (Complex.Zero, Complex.Zero, Complex.Zero);
                magnitudes[p] = 0;
                snapshots[p] = new Vector3D(0, 0, 0);
                continue;
            }

            Complex ax = Complex.Zero, ay = Complex.Zero, az = Complex.Zero;
            Complex gx = Complex.Zero, gy = Complex.Zero, gz = Complex.Zero;

            void AddTriangle(Vector3D va, Vector3D vb, Vector3D vc, double area,
                IReadOnlyList<(int Basis, double Sign, int Opposite)> supports,
                Func<Vector3D, Vector3D> mapPosition, double jxySign, double chargeSign)
            {
                double diameter = Math.Max((vb - va).Length,
                    Math.Max((vc - vb).Length, (va - vc).Length));
                double epsilon = diameter / 50;

                // Constant surface charge from div J (independent of position).
                Complex charge = Complex.Zero;
                foreach (var (basis, sign, _) in supports)
                    charge += solution.EdgeCurrents[basis] * (sign * surface.Edges[basis].Length / area);
                charge *= chargeSign / (Complex.ImaginaryOne * omega);

                var centroid = (va + vb + vc) / 3;
                bool near = (point - mapPosition(centroid)).Length < 2 * diameter;

                IEnumerable<(Vector3D A, Vector3D B, Vector3D C)> panels;
                if (near)
                    panels = Split((va, vb, vc)).SelectMany(Split);
                else
                    panels = new[] { (va, vb, vc) };

                foreach (var (ta, tb, tc) in panels)
                {
                    double panelArea = Vector3D.Cross(tb - ta, tc - ta).Length / 2;
                    for (int i = 0; i < w.Length; i++)
                    {
                        var source = ta * l1[i] + tb * l2[i] + tc * l3[i];

                        Complex jx = Complex.Zero, jy = Complex.Zero, jz = Complex.Zero;
                        foreach (var (basis, sign, opposite) in supports)
                        {
                            Complex coefficient = solution.EdgeCurrents[basis]
                                * (sign * surface.Edges[basis].Length / (2 * area));
                            var rho = source - surface.Vertices[opposite];
                            jx += coefficient * rho.X;
                            jy += coefficient * rho.Y;
                            jz += coefficient * rho.Z;
                        }
                        // Image mapping: position mirrored, current (−Jx, −Jy, +Jz).
                        var mapped = mapPosition(source);
                        jx *= jxySign;
                        jy *= jxySign;

                        var separation = point - mapped;
                        double r = Math.Sqrt(separation.LengthSquared + epsilon * epsilon);
                        Complex g = Complex.Exp(new Complex(0, -k * r)) / r;
                        double weight = w[i] * panelArea;

                        ax += weight * g * jx;
                        ay += weight * g * jy;
                        az += weight * g * jz;

                        // ∇g = (r−r′)·e^{−jkR}(−jkR−1)/R³
                        Complex dg = g * (new Complex(0, -k * r) - 1) / (r * r);
                        Complex wPhi = weight * charge * dg;
                        gx += wPhi * separation.X;
                        gy += wPhi * separation.Y;
                        gz += wPhi * separation.Z;
                    }
                }
            }

            for (int t = 0; t < surface.Triangles.Count; t++)
            {
                var supports = surface.TriangleSupports[t];
                if (supports.Count == 0) continue;
                var (ia, ib, ic) = surface.Triangles[t];
                var va = surface.Vertices[ia];
                var vb = surface.Vertices[ib];
                var vc = surface.Vertices[ic];
                double area = surface.TriangleAreas[t];

                AddTriangle(va, vb, vc, area, supports, r => r, jxySign: 1, chargeSign: 1);
                if (surface.Ground is { } ground)
                    AddTriangle(va, vb, vc, area, supports,
                        r => ThinWireMomSolver.Mirror(r, ground.SurfaceZ),
                        jxySign: -1, chargeSign: -1);
            }

            Complex aFactor = new Complex(0, -1) * omega * RfConstants.Mu0 / (4 * Math.PI);
            double phiFactor = 1 / (4 * Math.PI * RfConstants.Eps0);
            Complex ex = aFactor * ax - phiFactor * gx;
            Complex ey = aFactor * ay - phiFactor * gy;
            Complex ez = aFactor * az - phiFactor * gz;

            fields[p] = (ex, ey, ez);
            magnitudes[p] = Math.Sqrt(
                ex.Magnitude * ex.Magnitude + ey.Magnitude * ey.Magnitude + ez.Magnitude * ez.Magnitude);
            snapshots[p] = new Vector3D(ex.Real, ey.Real, ez.Real);
        }
        return new FieldMap(points, fields, magnitudes, snapshots);
    }

    private static IEnumerable<(Vector3D A, Vector3D B, Vector3D C)> Split(
        (Vector3D A, Vector3D B, Vector3D C) t)
    {
        var mab = (t.A + t.B) / 2;
        var mbc = (t.B + t.C) / 2;
        var mca = (t.C + t.A) / 2;
        yield return (t.A, mab, mca);
        yield return (mab, t.B, mbc);
        yield return (mca, mbc, t.C);
        yield return (mab, mbc, mca);
    }
}
