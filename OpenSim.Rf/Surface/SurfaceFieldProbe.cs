using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// Near-field evaluation from solved RWG currents: E = −jωA − ∇Φ with
/// A ∝ Σ_T ∫ J g dS′ and the surface charge σ = −(1/jω)·div J, piecewise CONSTANT per
/// triangle (RWG divergence is constant — the surface analog of the wire probe's
/// piecewise-constant line charge). A triangle within two diameters of the sample point
/// is split as the solver splits its kernel, g = 1/R + (e^{−jkR} − 1)/R: the static part
/// is the ANALYTIC Wilton–Rao potential and its gradient (<see cref="TrianglePotentials"/>
/// — A from I0 and Iρ since the RWG current is affine, ∇Φ from the constant charge times
/// ∇I0, and ∇×A = ∇I0 × J(r) because the affine current's Jacobian is a multiple of the
/// identity), the smooth remainder a plain rule on two 1:4 subdivision levels. So a point
/// ON the metal returns the discrete solution's own tangential field (the normal field's
/// ±σ/2ε₀ jump is averaged to its principal value, zero, there), and the field a fraction
/// of an element off the sheet is no longer limited by a regularized 1/R³ quadrature
/// (FU-50: the on-metal tangential residual of a λ/40 strip went from 5.3 % to about 2 %
/// of the gap field). Far triangles keep the plain rule with its slight regularization
/// R ← √(R² + ε²), ε ≤ diameter/50. Points at or below a ground plane return exactly
/// E = 0 (PEC interior); above it, image triangles contribute with (−Jx, −Jy, +Jz) and
/// NEGATED charge (div flips with the image sign). Reuses <see cref="FieldMap"/>.
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
        var hFields = new (Complex X, Complex Y, Complex Z)[points.Count];
        var hMagnitudes = new double[points.Count];
        var hSnapshots = new Vector3D[points.Count];

        for (int p = 0; p < points.Count; p++)
        {
            var point = points[p];
            if (surface.Ground is { } plane && point.Z <= plane.SurfaceZ)
            {
                fields[p] = (Complex.Zero, Complex.Zero, Complex.Zero);
                magnitudes[p] = 0;
                snapshots[p] = new Vector3D(0, 0, 0);
                hFields[p] = (Complex.Zero, Complex.Zero, Complex.Zero);
                hMagnitudes[p] = 0;
                hSnapshots[p] = new Vector3D(0, 0, 0);
                continue;
            }

            Complex ax = Complex.Zero, ay = Complex.Zero, az = Complex.Zero;
            Complex gx = Complex.Zero, gy = Complex.Zero, gz = Complex.Zero;
            Complex hx = Complex.Zero, hy = Complex.Zero, hz = Complex.Zero;   // ∇×A integrals (H·4π)

            void AddTriangle(Vector3D va, Vector3D vb, Vector3D vc, double area,
                IReadOnlyList<(int Basis, double Sign, int Opposite)> supports,
                Func<Vector3D, Vector3D> mapPosition, double jxySign, double chargeSign)
            {
                double diameter = Math.Max((vb - va).Length,
                    Math.Max((vc - vb).Length, (va - vc).Length));
                // Kernel regularization of the FAR rule (beyond two diameters): diameter/50
                // for a point in the triangle's plane, fading to diameter/5000 once the point
                // stands a tenth of a diameter off it. Near triangles take the analytic
                // static kernel below and need none.
                var mappedA = mapPosition(va);
                var planeNormal = Vector3D.Cross(mapPosition(vb) - mappedA, mapPosition(vc) - mappedA);
                double offPlane = planeNormal.Length > 0
                    ? Math.Abs(Vector3D.Dot(point - mappedA, planeNormal)) / planeNormal.Length
                    : 0;
                double epsilon = diameter / 50 * Math.Clamp(1 - 10 * offPlane / diameter, 0.01, 1);

                // Constant surface charge from continuity, σ = −div J/(jω) (independent of
                // position). The minus is the physics: without it the scalar-potential
                // part of E comes out reversed.
                Complex charge = Complex.Zero;
                foreach (var (basis, sign, _) in supports)
                    charge += solution.EdgeCurrents[basis] * (sign * surface.Edges[basis].Length / area);
                charge *= -chargeSign / (Complex.ImaginaryOne * omega);

                var centroid = (va + vb + vc) / 3;
                bool near = (point - mapPosition(centroid)).Length < 2 * diameter;

                // The current on the (mapped) triangle as a function of the mapped position
                // x: J(x) = K·x − P, with K = s·Σ coefficient (s = ±1 the image sign, since
                // the mirror pushforward with (−Jx, −Jy, +Jz) is −1 times the RWG on the
                // mirrored triangle) and P = s·Σ coefficient·mapped opposite vertex. Affine
                // in x, Jacobian K·I.
                Complex kSum = Complex.Zero;
                Complex px = Complex.Zero, py = Complex.Zero, pz = Complex.Zero;
                foreach (var (basis, sign, opposite) in supports)
                {
                    Complex coefficient = solution.EdgeCurrents[basis]
                        * (jxySign * sign * surface.Edges[basis].Length / (2 * area));
                    var mappedOpposite = mapPosition(surface.Vertices[opposite]);
                    kSum += coefficient;
                    px += coefficient * mappedOpposite.X;
                    py += coefficient * mappedOpposite.Y;
                    pz += coefficient * mappedOpposite.Z;
                }
                (Complex X, Complex Y, Complex Z) CurrentAt(Vector3D x) =>
                    (kSum * x.X - px, kSum * x.Y - py, kSum * x.Z - pz);

                if (near)
                {
                    // Static part, analytic: ∫J/R = J(ρ)·I0 + K·Iρ; ∇∫σ/R = σ·∇I0;
                    // ∫∇(1/R) × J = ∇I0 × J(r) (J(r′) = J(r) + K(r′ − r) and d × d = 0).
                    var ma = mapPosition(va);
                    var mb = mapPosition(vb);
                    var mc = mapPosition(vc);
                    var (i0, iRho, projection) = TrianglePotentials.Integrals(ma, mb, mc, point);
                    var gradient = TrianglePotentials.Gradient(ma, mb, mc, point);
                    var (jpx, jpy, jpz) = CurrentAt(projection);
                    ax += jpx * i0 + kSum * iRho.X;
                    ay += jpy * i0 + kSum * iRho.Y;
                    az += jpz * i0 + kSum * iRho.Z;
                    gx += charge * gradient.X;
                    gy += charge * gradient.Y;
                    gz += charge * gradient.Z;
                    var (jrx, jry, jrz) = CurrentAt(point);
                    hx += gradient.Y * jrz - gradient.Z * jry;
                    hy += gradient.Z * jrx - gradient.X * jrz;
                    hz += gradient.X * jry - gradient.Y * jrx;

                    // Smooth remainder (e^{−jkR} − 1)/R and its gradient
                    // separation·[e^{−jkR}(−jkR − 1) + 1]/R³ → −(k²/2)·separation/R at R → 0:
                    // bounded, so a plain rule on the subdivided panels; a node ON the
                    // observation point contributes the limit (−jk for the kernel, 0 for the
                    // direction-less gradient).
                    foreach (var (ta, tb, tc) in Split((va, vb, vc)).SelectMany(Split))
                    {
                        double panelArea = Vector3D.Cross(tb - ta, tc - ta).Length / 2;
                        for (int i = 0; i < w.Length; i++)
                        {
                            var mapped = mapPosition(ta * l1[i] + tb * l2[i] + tc * l3[i]);
                            var (jx, jy, jz) = CurrentAt(mapped);
                            var separation = point - mapped;
                            double r = separation.Length;
                            double weight = w[i] * panelArea;
                            Complex gs, dgs;
                            if (r == 0)
                            {
                                gs = new Complex(0, -k);
                                dgs = Complex.Zero;
                            }
                            else
                            {
                                var (sin, cos) = Math.SinCos(k * r);
                                gs = new Complex((cos - 1) / r, -sin / r);
                                // e^{−jkR}(−jkR − 1) + 1 = (1 − cos − kR·sin) + j(sin − kR·cos)
                                dgs = new Complex(1 - cos - k * r * sin, sin - k * r * cos) / (r * r * r);
                            }
                            ax += weight * gs * jx;
                            ay += weight * gs * jy;
                            az += weight * gs * jz;
                            Complex wPhi = weight * charge * dgs;
                            gx += wPhi * separation.X;
                            gy += wPhi * separation.Y;
                            gz += wPhi * separation.Z;
                            Complex wH = weight * dgs;
                            hx += wH * (separation.Y * jz - separation.Z * jy);
                            hy += wH * (separation.Z * jx - separation.X * jz);
                            hz += wH * (separation.X * jy - separation.Y * jx);
                        }
                    }
                    return;
                }

                {
                    var (ta, tb, tc) = (va, vb, vc);
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

                        // H = ∇×A/µ₀ = (1/4π)Σ∫ (∇g × J), ∇g = separation·dg → dg·(separation × J).
                        // J is complex, so this is a complex cross product; no charge term.
                        Complex wH = weight * dg;
                        hx += wH * (separation.Y * jz - separation.Z * jy);
                        hy += wH * (separation.Z * jx - separation.X * jz);
                        hz += wH * (separation.X * jy - separation.Y * jx);
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

            Complex hFactor = 1.0 / (4 * Math.PI);
            Complex bx = hFactor * hx, by = hFactor * hy, bz = hFactor * hz;
            hFields[p] = (bx, by, bz);
            hMagnitudes[p] = Math.Sqrt(
                bx.Magnitude * bx.Magnitude + by.Magnitude * by.Magnitude + bz.Magnitude * bz.Magnitude);
            hSnapshots[p] = new Vector3D(bx.Real, by.Real, bz.Real);
        }
        return new FieldMap(points, fields, magnitudes, snapshots)
        {
            H = hFields, HMagnitude = hMagnitudes, HSnapshot = hSnapshots
        };
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
