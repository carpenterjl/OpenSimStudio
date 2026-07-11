using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>
/// The far-field radiation pattern on a (θ, φ) sphere grid: time-averaged radiation
/// intensity U [W/sr], total radiated power (integrated with Gauss–Legendre in cosθ ×
/// uniform φ — trapezoid is spectrally accurate on a periodic integrand), and
/// directivity. θ nodes come from the Gauss rule, so the grid carries its own exact
/// integration weights; the poles are approached but not sampled.
/// </summary>
public sealed record FarFieldPattern(
    IReadOnlyList<double> ThetaRadians,
    IReadOnlyList<double> PhiRadians,
    double[,] IntensityWattsPerSteradian,
    double TotalRadiatedPowerWatts,
    double MaxDirectivity)
{
    public double Directivity(int thetaIndex, int phiIndex) =>
        4 * Math.PI * IntensityWattsPerSteradian[thetaIndex, phiIndex] / TotalRadiatedPowerWatts;
}

/// <summary>
/// Computes the far field of a solved thin-wire structure from its basis currents:
/// the radiation vector N(θ,φ) = Σ ∫ I(s) t̂ e^{jk r̂·r′} ds (per-element Gauss — at
/// λ/10 elements the phase factor is slow, so 8 points are exact to machine noise),
/// then U = (ωµ₀/4π)²·|N_t|²/(2η). Peak phasors throughout, matching the solver.
/// </summary>
public static class FarFieldEvaluator
{
    public static FarFieldPattern Compute(WireStructure wire, MomSolution solution,
        int thetaCount = 32, int phiCount = 64)
    {
        double omega = 2 * Math.PI * solution.FrequencyHz;
        double k = omega / RfConstants.SpeedOfLight;
        return IntegratePattern(omega, wire.Ground is not null, thetaCount, phiCount,
            direction => RadiationVector(wire, solution, k, direction));
    }

    /// <summary>The (θ, φ) grid + intensity + power + directivity machinery, shared by
    /// the wire and surface evaluators (one radiation-vector callback each).
    /// Gauss–Legendre in u = cosθ carries the sphere weights exactly. Over a ground
    /// plane only the upper hemisphere radiates (u ∈ [0, 1]) — the intensity below is
    /// identically zero and is not sampled, and Directivity = 4πU/P then correctly
    /// reports the image-theory doubling (a λ/4 monopole shows D ≈ 3.28, not 1.64).</summary>
    internal static FarFieldPattern IntegratePattern(double omega, bool hemisphere,
        int thetaCount, int phiCount,
        Func<Vector3D, (Complex X, Complex Y, Complex Z)> radiationVector)
    {
        double eta = Math.Sqrt(RfConstants.Mu0 / RfConstants.Eps0);
        var (uNodes, uWeights) = hemisphere
            ? GaussLegendre.Rule(thetaCount, 0, 1)
            : GaussLegendre.Rule(thetaCount, -1, 1);
        var theta = uNodes.Select(Math.Acos).ToArray();
        var phi = Enumerable.Range(0, phiCount).Select(i => 2 * Math.PI * i / phiCount).ToArray();
        double phiWeight = 2 * Math.PI / phiCount;

        var intensity = new double[thetaCount, phiCount];
        double totalPower = 0;
        double prefactor = omega * RfConstants.Mu0 / (4 * Math.PI);

        for (int ti = 0; ti < thetaCount; ti++)
        {
            double sinTheta = Math.Sin(theta[ti]);
            double cosTheta = uNodes[ti];
            for (int pi = 0; pi < phiCount; pi++)
            {
                var direction = new Vector3D(
                    sinTheta * Math.Cos(phi[pi]),
                    sinTheta * Math.Sin(phi[pi]),
                    cosTheta);
                var n = radiationVector(direction);

                // Transverse part |N|² − |N·r̂|²  (with complex N·r̂).
                Complex radial = n.X * direction.X + n.Y * direction.Y + n.Z * direction.Z;
                double transverseSquared =
                    n.X.Magnitude * n.X.Magnitude + n.Y.Magnitude * n.Y.Magnitude +
                    n.Z.Magnitude * n.Z.Magnitude - radial.Magnitude * radial.Magnitude;
                if (transverseSquared < 0) transverseSquared = 0;   // rounding at pattern nulls

                double u = prefactor * prefactor * transverseSquared / (2 * eta);
                intensity[ti, pi] = u;
                totalPower += uWeights[ti] * phiWeight * u;
            }
        }

        double maxDirectivity = 0;
        foreach (double u in intensity)
            maxDirectivity = Math.Max(maxDirectivity, 4 * Math.PI * u / totalPower);

        return new FarFieldPattern(theta, phi, intensity, totalPower, maxDirectivity);
    }

    /// <summary>The complex far-field E (θ̂/φ̂ transverse components recombined as a
    /// Cartesian phasor) at distance r in the given direction — the quantity the
    /// near-field probe must reproduce at kr ≫ 1 (consistency gate).</summary>
    public static (Complex Ex, Complex Ey, Complex Ez) FarElectricField(WireStructure wire,
        MomSolution solution, Vector3D direction, double distance)
    {
        double omega = 2 * Math.PI * solution.FrequencyHz;
        double k = omega / RfConstants.SpeedOfLight;
        var n = RadiationVector(wire, solution, k, direction);

        Complex radial = n.X * direction.X + n.Y * direction.Y + n.Z * direction.Z;
        Complex spherical = Complex.Exp(new Complex(0, -k * distance)) / distance;
        Complex factor = new Complex(0, -1) * omega * RfConstants.Mu0 / (4 * Math.PI) * spherical;
        return (factor * (n.X - radial * direction.X),
                factor * (n.Y - radial * direction.Y),
                factor * (n.Z - radial * direction.Z));
    }

    private static (Complex X, Complex Y, Complex Z) RadiationVector(WireStructure wire,
        MomSolution solution, double k, Vector3D direction)
    {
        var nodeCurrents = NodeCurrents(wire, solution);
        Complex nx = Complex.Zero, ny = Complex.Zero, nz = Complex.Zero;

        void AddSegment(Vector3D start, Vector3D tangent, double length,
            Complex startCurrent, Complex endCurrent)
        {
            var (nodes, weights) = GaussLegendre.Rule(8, 0, length);
            Complex integral = Complex.Zero;
            for (int i = 0; i < nodes.Length; i++)
            {
                double u = nodes[i] / length;
                Complex current = startCurrent * (1 - u) + endCurrent * u;
                var point = start + tangent * nodes[i];
                double phase = k * Vector3D.Dot(direction, point);
                integral += weights[i] * current * Complex.Exp(new Complex(0, phase));
            }
            nx += integral * tangent.X;
            ny += integral * tangent.Y;
            nz += integral * tangent.Z;
        }

        for (int e = 0; e < wire.ElementCount; e++)
        {
            var start = wire.ElementStart(e);
            var end = wire.ElementEnd(e);
            double length = wire.ElementLength(e);
            var tangent = wire.ElementDirection(e);
            Complex startCurrent = nodeCurrents[e];
            Complex endCurrent = nodeCurrents[(e + 1) % wire.Nodes.Count];
            AddSegment(start, tangent, length, startCurrent, endCurrent);

            // Ground plane: the image element (mirrored + endpoint-swapped, so its
            // current runs end→start) radiates too — same mapping as the solver's
            // image pass, no hand signs.
            if (wire.Ground is { } ground)
            {
                var imageStart = ThinWireMomSolver.Mirror(end, ground.SurfaceZ);
                var imageEnd = ThinWireMomSolver.Mirror(start, ground.SurfaceZ);
                AddSegment(imageStart, (imageEnd - imageStart) / length, length,
                    endCurrent, startCurrent);
            }
        }
        return (nx, ny, nz);
    }

    /// <summary>Current phasor per NODE (basis coefficients at basis nodes, exactly zero
    /// at open ends — a GROUNDED end carries its basis coefficient, peak current at a
    /// monopole base) — the rooftop expansion is linear between nodes, so this is the
    /// whole current distribution. Shared by the far-field and near-field evaluators.</summary>
    internal static Complex[] NodeCurrents(WireStructure wire, MomSolution solution)
    {
        var currents = new Complex[wire.Nodes.Count];
        for (int b = 0; b < wire.BasisCount; b++)
            currents[wire.BasisNode(b)] = solution.BasisCurrents[b];
        return currents;
    }
}
