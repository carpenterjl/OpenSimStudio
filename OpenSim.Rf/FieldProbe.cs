using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>
/// The electromagnetic field sampled at a set of free-space points: complex Cartesian
/// phasor components (peak convention, like the solver), the peak magnitude
/// |E| = √(|Ex|²+|Ey|²+|Ez|²), and the t = 0 snapshot Re(E) used to draw arrows (an
/// elliptically polarized field has no single direction; the snapshot is the honest
/// drawable and is documented as such). The magnetic field triple <see cref="H"/> (A/m)
/// and its magnitude/snapshot are populated by the free-space evaluators (SI Stage S7,
/// H = ∇×A/µ₀); they stay null on layered maps where the H spectral kernels are a named
/// follow-up.
/// </summary>
public sealed record FieldMap(
    IReadOnlyList<Vector3D> Points,
    IReadOnlyList<(Complex X, Complex Y, Complex Z)> E,
    IReadOnlyList<double> Magnitude,
    IReadOnlyList<Vector3D> Snapshot)
{
    /// <summary>Complex magnetic-field phasor per point (A/m); null when H was not computed.</summary>
    public IReadOnlyList<(Complex X, Complex Y, Complex Z)>? H { get; init; }
    /// <summary>|H| = √(|Hx|²+|Hy|²+|Hz|²) per point (A/m); null when H was not computed.</summary>
    public IReadOnlyList<double>? HMagnitude { get; init; }
    /// <summary>t = 0 snapshot Re(H) per point; null when H was not computed.</summary>
    public IReadOnlyList<Vector3D>? HSnapshot { get; init; }
}

/// <summary>
/// Near-field evaluation from the solved currents: E = −jωA − ∇Φ with
/// A = µ₀/4π ∫ I t̂ g dS and Φ from the piecewise-constant charge −(1/jω)·dI/ds, using
/// the analytic kernel gradient ∇g = (r−r′)·e^{−jkR}(−jkR−1)/R³. The reduced-kernel
/// bump R = √(d² + a²) keeps every value finite — points on or inside the wire return
/// surface-scale approximations rather than NaN. Elements close to a sample point get
/// a panelled quadrature so the 1/R³ variation is resolved.
/// </summary>
public static class FieldProbe
{
    public static FieldMap Evaluate(WireStructure wire, MomSolution solution,
        IReadOnlyList<Vector3D> points)
    {
        double omega = 2 * Math.PI * solution.FrequencyHz;
        double k = omega / RfConstants.SpeedOfLight;
        var nodeCurrents = FarFieldEvaluator.NodeCurrents(wire, solution);

        var fields = new (Complex X, Complex Y, Complex Z)[points.Count];
        var magnitudes = new double[points.Count];
        var snapshots = new Vector3D[points.Count];
        var hFields = new (Complex X, Complex Y, Complex Z)[points.Count];
        var hMagnitudes = new double[points.Count];
        var hSnapshots = new Vector3D[points.Count];

        for (int p = 0; p < points.Count; p++)
        {
            Complex ax = Complex.Zero, ay = Complex.Zero, az = Complex.Zero;   // vector potential integrals
            Complex gx = Complex.Zero, gy = Complex.Zero, gz = Complex.Zero;   // ∇Φ integrals
            Complex hx = Complex.Zero, hy = Complex.Zero, hz = Complex.Zero;   // ∇×A integrals (H·4π)
            var point = points[p];

            // Inside a PEC (at or below a ground plane) the field is identically zero —
            // the honest value, returned exactly rather than summing near-cancelling
            // real/image contributions.
            if (wire.Ground is { } plane && point.Z <= plane.SurfaceZ)
            {
                fields[p] = (Complex.Zero, Complex.Zero, Complex.Zero);
                magnitudes[p] = 0;
                snapshots[p] = new Vector3D(0, 0, 0);
                hFields[p] = (Complex.Zero, Complex.Zero, Complex.Zero);
                hMagnitudes[p] = 0;
                hSnapshots[p] = new Vector3D(0, 0, 0);
                continue;
            }

            void AddSegment(Vector3D start, Vector3D tangent, double length, double c,
                Complex startCurrent, Complex endCurrent)
            {
                // Piecewise-constant line charge from current continuity.
                Complex charge = (startCurrent - endCurrent) / (Complex.ImaginaryOne * omega * length);

                // Points near the element see 1/R³ variation on the scale of their
                // distance: resolve it with panels instead of hoping.
                double distance = DistanceToSegment(point, start, tangent, length);
                int panels = distance < 2 * length ? 4 : 1;

                for (int panel = 0; panel < panels; panel++)
                {
                    var (nodes, weights) = GaussLegendre.Rule(8,
                        length * panel / panels, length * (panel + 1) / panels);
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        double u = nodes[i] / length;
                        var source = start + tangent * nodes[i];
                        var separation = point - source;
                        double r = Math.Sqrt(separation.LengthSquared + c * c);
                        Complex g = Complex.Exp(new Complex(0, -k * r)) / r;
                        Complex current = startCurrent * (1 - u) + endCurrent * u;

                        Complex wA = weights[i] * current * g;
                        ax += wA * tangent.X;
                        ay += wA * tangent.Y;
                        az += wA * tangent.Z;

                        // ∇g = (r−r′)·e^{−jkR}(−jkR−1)/R³
                        Complex dg = g * (new Complex(0, -k * r) - 1) / (r * r);
                        Complex wPhi = weights[i] * charge * dg;
                        gx += wPhi * separation.X;
                        gy += wPhi * separation.Y;
                        gz += wPhi * separation.Z;

                        // H = ∇×A/µ₀ = (1/4π)∫ I (∇g × t̂), and ∇g = separation·dg, so the
                        // per-point contribution is I·dg·(separation × t̂) — the curl carries
                        // no charge/∇Φ term (that part is curl-free). separation × t̂ is a
                        // real vector (both operands real); the current/dg weight is complex.
                        var cross = Vector3D.Cross(separation, tangent);
                        Complex wH = weights[i] * current * dg;
                        hx += wH * cross.X;
                        hy += wH * cross.Y;
                        hz += wH * cross.Z;
                    }
                }
            }

            for (int e = 0; e < wire.ElementCount; e++)
            {
                var start = wire.ElementStart(e);
                var end = wire.ElementEnd(e);
                double length = wire.ElementLength(e);
                var tangent = wire.ElementDirection(e);
                double c = wire.ElementRadii[e];
                Complex startCurrent = nodeCurrents[e];
                Complex endCurrent = nodeCurrents[(e + 1) % wire.Nodes.Count];
                AddSegment(start, tangent, length, c, startCurrent, endCurrent);

                // Ground plane: the image element (mirrored + endpoint-swapped, current
                // running end→start) contributes too — the solver's image mapping.
                if (wire.Ground is { } ground)
                {
                    var imageStart = ThinWireMomSolver.Mirror(end, ground.SurfaceZ);
                    var imageEnd = ThinWireMomSolver.Mirror(start, ground.SurfaceZ);
                    AddSegment(imageStart, (imageEnd - imageStart) / length, length, c,
                        endCurrent, startCurrent);
                }
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

    private static double DistanceToSegment(Vector3D point, Vector3D start, Vector3D direction,
        double length)
    {
        double u = Math.Clamp(Vector3D.Dot(point - start, direction), 0, length);
        return (point - (start + direction * u)).Length;
    }
}
