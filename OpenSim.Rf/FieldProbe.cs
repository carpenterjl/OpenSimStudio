using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>
/// The electric field sampled at a set of free-space points: complex Cartesian phasor
/// components (peak convention, like the solver), the peak magnitude
/// |E| = √(|Ex|²+|Ey|²+|Ez|²), and the t = 0 snapshot Re(E) used to draw arrows (an
/// elliptically polarized field has no single direction; the snapshot is the honest
/// drawable and is documented as such).
/// </summary>
public sealed record FieldMap(
    IReadOnlyList<Vector3D> Points,
    IReadOnlyList<(Complex X, Complex Y, Complex Z)> E,
    IReadOnlyList<double> Magnitude,
    IReadOnlyList<Vector3D> Snapshot);

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

        for (int p = 0; p < points.Count; p++)
        {
            Complex ax = Complex.Zero, ay = Complex.Zero, az = Complex.Zero;   // vector potential integrals
            Complex gx = Complex.Zero, gy = Complex.Zero, gz = Complex.Zero;   // ∇Φ integrals
            var point = points[p];

            // Inside a PEC (at or below a ground plane) the field is identically zero —
            // the honest value, returned exactly rather than summing near-cancelling
            // real/image contributions.
            if (wire.Ground is { } plane && point.Z <= plane.SurfaceZ)
            {
                fields[p] = (Complex.Zero, Complex.Zero, Complex.Zero);
                magnitudes[p] = 0;
                snapshots[p] = new Vector3D(0, 0, 0);
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
        }
        return new FieldMap(points, fields, magnitudes, snapshots);
    }

    private static double DistanceToSegment(Vector3D point, Vector3D start, Vector3D direction,
        double length)
    {
        double u = Math.Clamp(Vector3D.Dot(point - start, direction), 0, length);
        return (point - (start + direction * u)).Length;
    }
}
