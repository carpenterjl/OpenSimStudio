using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Far-field and near-field gates. The strongest single check is energy conservation:
/// the power the delta-gap feed delivers (½·Re(V·I*)) must equal the power leaving
/// through the far-field sphere — an end-to-end identity across the matrix assembly,
/// the solve, the radiation integrals, and the sphere quadrature.
/// </summary>
public class FieldEvaluationTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;
    private static readonly double Eta = Math.Sqrt(4e-7 * Math.PI / (1.0 / (4e-7 * Math.PI * 299_792_458.0 * 299_792_458.0)));

    private static (WireStructure Wire, MomSolution Solution) Dipole(double lengthOverLambda,
        int elements, double radius)
    {
        double length = lengthOverLambda * Lambda;
        var grid = WireGridBuilder.Build(
            new[] { new WireSegment(new Vector3D(0, 0, -length / 2), new Vector3D(0, 0, length / 2), radius) },
            maxElementLength: length / elements);
        Assert.NotNull(grid.Structure);
        var wire = grid.Structure!;
        var solution = new ThinWireMomSolver().Solve(wire, Frequency, wire.NearestBasis(Vector3D.Zero));
        return (wire, solution);
    }

    private static double InputPower(MomSolution solution, WireStructure wire)
    {
        int feed = wire.NearestBasis(Vector3D.Zero);
        return 0.5 * (Complex.One * Complex.Conjugate(solution.BasisCurrents[feed])).Real;
    }

    [Fact]
    public void PowerBalance_HalfWaveDipole()
    {
        var (wire, solution) = Dipole(0.5, 40, Lambda / 2000);
        var pattern = FarFieldEvaluator.Compute(wire, solution);
        double pIn = InputPower(solution, wire);
        Assert.True(Math.Abs(pattern.TotalRadiatedPowerWatts / pIn - 1) < 0.02,
            $"P_rad = {pattern.TotalRadiatedPowerWatts:g6} vs P_in = {pIn:g6}");
    }

    [Fact]
    public void PowerBalance_ShortDipole()
    {
        // A short dipole is the harder case: the input power is a tiny real part on a
        // huge reactance, so any radiation-term error in the matrix shows up here first.
        var (wire, solution) = Dipole(0.05, 20, Lambda / 20000);
        var pattern = FarFieldEvaluator.Compute(wire, solution);
        double pIn = InputPower(solution, wire);
        Assert.True(Math.Abs(pattern.TotalRadiatedPowerWatts / pIn - 1) < 0.02,
            $"P_rad = {pattern.TotalRadiatedPowerWatts:g6} vs P_in = {pIn:g6}");
    }

    [Fact]
    public void ShortDipole_Pattern_IsSinSquaredTheta_AndAxiallySymmetric()
    {
        var (wire, solution) = Dipole(0.05, 20, Lambda / 20000);
        var pattern = FarFieldEvaluator.Compute(wire, solution);

        // U(θ)/sin²θ constant over θ (1%), U independent of φ (quadrature-exact).
        int equator = pattern.ThetaRadians.Count / 2;
        double reference = pattern.IntensityWattsPerSteradian[equator, 0]
                           / Math.Pow(Math.Sin(pattern.ThetaRadians[equator]), 2);
        for (int ti = 0; ti < pattern.ThetaRadians.Count; ti++)
        {
            double expected = reference * Math.Pow(Math.Sin(pattern.ThetaRadians[ti]), 2);
            Assert.True(Math.Abs(pattern.IntensityWattsPerSteradian[ti, 0] - expected) <= 0.01 * expected,
                $"θ = {pattern.ThetaRadians[ti]:g4}: U = {pattern.IntensityWattsPerSteradian[ti, 0]:g6} vs sin²θ fit {expected:g6}");
            for (int pi = 1; pi < pattern.PhiRadians.Count; pi++)
                Assert.Equal(pattern.IntensityWattsPerSteradian[ti, 0],
                    pattern.IntensityWattsPerSteradian[ti, pi],
                    pattern.IntensityWattsPerSteradian[ti, 0] * 1e-9);
        }
    }

    [Fact]
    public void Directivity_MatchesTheClassicValues()
    {
        var (shortWire, shortSolution) = Dipole(0.05, 20, Lambda / 20000);
        double shortD = FarFieldEvaluator.Compute(shortWire, shortSolution).MaxDirectivity;
        Assert.InRange(shortD, 1.5 * 0.98, 1.5 * 1.02);          // Hertzian/short dipole: 1.50

        var (halfWire, halfSolution) = Dipole(0.5, 40, Lambda / 2000);
        double halfD = FarFieldEvaluator.Compute(halfWire, halfSolution).MaxDirectivity;
        Assert.InRange(halfD, 1.641 * 0.98, 1.641 * 1.02);       // half-wave dipole: 1.641
    }

    [Fact]
    public void NearField_MatchesTheHertzianDipole_WithTheSolvedMoment()
    {
        // Sharp-benchmark style: take the current moment M = Σ t̂·L·(I_a+I_b)/2 FROM the
        // solution (dividing out the 1/Ω current-shape physics) and compare the probe
        // against the exact Hertzian fields of that moment. At r = 0.5λ the finite-size
        // correction is O((l/r)²) ≈ 0.2% for l = 0.02λ — so 2% is a real gate on the
        // A/∇Φ evaluation, not slack.
        var (wire, solution) = Dipole(0.02, 16, Lambda / 20000);
        var nodeCurrents = FarFieldEvaluator.NodeCurrents(wire, solution);
        Complex moment = Complex.Zero;                            // z-directed by construction
        for (int e = 0; e < wire.ElementCount; e++)
            moment += wire.ElementLength(e) * wire.ElementDirection(e).Z
                      * 0.5 * (nodeCurrents[e] + nodeCurrents[(e + 1) % wire.Nodes.Count]);

        double k = 2 * Math.PI / Lambda;
        foreach (double theta in new[] { Math.PI / 2, Math.PI / 4 })
        {
            double r = 0.5 * Lambda;
            var point = new Vector3D(r * Math.Sin(theta), 0, r * Math.Cos(theta));
            var map = FieldProbe.Evaluate(wire, solution, new[] { point });
            var (ex, ey, ez) = map.E[0];

            Complex phase = Complex.Exp(new Complex(0, -k * r));
            Complex jkr = new Complex(0, k * r);
            Complex er = Eta * moment * Math.Cos(theta) / (2 * Math.PI * r * r) * (1 + 1 / jkr) * phase;
            Complex et = Complex.ImaginaryOne * Eta * k * moment * Math.Sin(theta) / (4 * Math.PI * r)
                         * (1 + 1 / jkr - 1 / (k * r * k * r)) * phase;

            // Spherical → Cartesian at φ = 0: r̂ = (sinθ, 0, cosθ), θ̂ = (cosθ, 0, −sinθ).
            Complex expectedX = er * Math.Sin(theta) + et * Math.Cos(theta);
            Complex expectedZ = er * Math.Cos(theta) - et * Math.Sin(theta);

            double scale = Math.Sqrt(expectedX.Magnitude * expectedX.Magnitude
                                     + expectedZ.Magnitude * expectedZ.Magnitude);
            Assert.True((ex - expectedX).Magnitude < 0.02 * scale,
                $"θ={theta:g3}: Ex {ex} vs Hertzian {expectedX}");
            Assert.True((ez - expectedZ).Magnitude < 0.02 * scale,
                $"θ={theta:g3}: Ez {ez} vs Hertzian {expectedZ}");
            Assert.True(ey.Magnitude < 1e-9 * scale, "Ey must vanish in the φ = 0 plane");
        }
    }

    [Fact]
    public void NearFieldProbe_ReducesToTheFarField_AtLargeDistance()
    {
        var (wire, solution) = Dipole(0.5, 40, Lambda / 2000);
        var direction = new Vector3D(1, 0, 0);                    // θ = 90°, broadside
        double r = 20 * Lambda;

        var probe = FieldProbe.Evaluate(wire, solution, new[] { direction * r });
        var far = FarFieldEvaluator.FarElectricField(wire, solution, direction, r);

        double scale = Math.Sqrt(far.Ex.Magnitude * far.Ex.Magnitude
                                 + far.Ey.Magnitude * far.Ey.Magnitude
                                 + far.Ez.Magnitude * far.Ez.Magnitude);
        Assert.True((probe.E[0].X - far.Ex).Magnitude < 0.01 * scale);
        Assert.True((probe.E[0].Y - far.Ey).Magnitude < 0.01 * scale);
        Assert.True((probe.E[0].Z - far.Ez).Magnitude < 0.01 * scale);
    }

    [Fact]
    public void FieldMap_CarriesMagnitudeAndSnapshot_Consistently()
    {
        var (wire, solution) = Dipole(0.5, 30, Lambda / 2000);
        var points = new[] { new Vector3D(0.3 * Lambda, 0.1 * Lambda, 0), new Vector3D(0, 0.4 * Lambda, 0.2 * Lambda) };
        var map = FieldProbe.Evaluate(wire, solution, points);

        for (int i = 0; i < points.Length; i++)
        {
            var (ex, ey, ez) = map.E[i];
            double magnitude = Math.Sqrt(ex.Magnitude * ex.Magnitude + ey.Magnitude * ey.Magnitude
                                         + ez.Magnitude * ez.Magnitude);
            Assert.Equal(magnitude, map.Magnitude[i], magnitude * 1e-12);
            Assert.Equal(ex.Real, map.Snapshot[i].X, Math.Abs(ex.Real) * 1e-12 + 1e-300);
            // The snapshot never exceeds the peak envelope.
            Assert.True(map.Snapshot[i].Length <= magnitude * (1 + 1e-12));
        }

        var repeat = FieldProbe.Evaluate(wire, solution, points);
        for (int i = 0; i < points.Length; i++)
            Assert.Equal(map.E[i], repeat.E[i]);                  // bitwise deterministic
    }

    // ------------------------------------------------------------------
    // Magnetic field (SI Stage S7): H = ∇×A/µ₀, computed alongside E.
    // ------------------------------------------------------------------

    [Fact]
    public void NearFieldH_MatchesTheHertzianDipole_WithTheSolvedMoment()
    {
        // The exact analog of the E Hertzian gate: the infinitesimal-dipole magnetic field
        // is H_φ = j·k·M·sinθ/(4π r)·(1 + 1/(jkr))·e^{−jkr} (no η — H is ∇×A/µ₀). At φ = 0,
        // φ̂ = ŷ, so the whole field lands on Hy; Hx/Hz must vanish. 2% is a real gate on
        // the curl evaluation (the O((l/r)²) finite-size correction is ~0.2%).
        var (wire, solution) = Dipole(0.02, 16, Lambda / 20000);
        var nodeCurrents = FarFieldEvaluator.NodeCurrents(wire, solution);
        Complex moment = Complex.Zero;
        for (int e = 0; e < wire.ElementCount; e++)
            moment += wire.ElementLength(e) * wire.ElementDirection(e).Z
                      * 0.5 * (nodeCurrents[e] + nodeCurrents[(e + 1) % wire.Nodes.Count]);

        double k = 2 * Math.PI / Lambda;
        foreach (double theta in new[] { Math.PI / 2, Math.PI / 4 })
        {
            double r = 0.5 * Lambda;
            var point = new Vector3D(r * Math.Sin(theta), 0, r * Math.Cos(theta));
            var map = FieldProbe.Evaluate(wire, solution, new[] { point });
            Assert.NotNull(map.H);
            var (hx, hy, hz) = map.H![0];

            Complex phase = Complex.Exp(new Complex(0, -k * r));
            Complex jkr = new Complex(0, k * r);
            Complex hPhi = Complex.ImaginaryOne * k * moment * Math.Sin(theta) / (4 * Math.PI * r)
                           * (1 + 1 / jkr) * phase;

            double scale = hPhi.Magnitude;
            Assert.True((hy - hPhi).Magnitude < 0.02 * scale,
                $"θ={theta:g3}: Hy {hy} vs Hertzian {hPhi}");
            Assert.True(hx.Magnitude < 1e-9 * scale, "Hx must vanish in the φ = 0 plane");
            Assert.True(hz.Magnitude < 1e-9 * scale, "Hz must vanish in the φ = 0 plane");

            // |H| bookkeeping mirrors |E|.
            Assert.Equal(scale, map.HMagnitude![0], scale * 0.02);
        }
    }

    [Fact]
    public void FarZoneH_SatisfiesTheWaveImpedanceRelation_AndOutwardPoynting()
    {
        // The evaluator-agnostic Maxwell identity: in the radiation zone the fields are a
        // transverse plane wave, H = (r̂ × E)/η₀. Sampling both from the SAME probe pass,
        // ‖η₀ H − r̂ × E‖ decays like 1/(kr); at r = 20λ (kr ≈ 126) the residual is ~1%.
        var (wire, solution) = Dipole(0.5, 40, Lambda / 2000);
        foreach (var dir in new[]
                 {
                     new Vector3D(1, 0, 0), new Vector3D(0, 1, 0),
                     Unit(new Vector3D(1, 1, 1)), Unit(new Vector3D(1, 0, 2)),
                 })
        {
            double r = 20 * Lambda;
            var map = FieldProbe.Evaluate(wire, solution, new[] { dir * r });
            var (ex, ey, ez) = map.E[0];
            var (hx, hy, hz) = map.H![0];

            // r̂ × E (real r̂, complex E).
            Complex cx = dir.Y * ez - dir.Z * ey;
            Complex cy = dir.Z * ex - dir.X * ez;
            Complex cz = dir.X * ey - dir.Y * ex;
            double cScale = Math.Sqrt(cx.Magnitude * cx.Magnitude + cy.Magnitude * cy.Magnitude
                                      + cz.Magnitude * cz.Magnitude);
            Assert.True((Eta * hx - cx).Magnitude < 0.02 * cScale
                        && (Eta * hy - cy).Magnitude < 0.02 * cScale
                        && (Eta * hz - cz).Magnitude < 0.02 * cScale,
                $"dir {dir.X:g2},{dir.Y:g2},{dir.Z:g2}: η₀H {Eta * hx:g3} vs r̂×E {cx:g3}");

            // Time-averaged Poynting ½Re(E×H*) points outward (radiated power leaves).
            Complex sx = ey * Complex.Conjugate(hz) - ez * Complex.Conjugate(hy);
            Complex sy = ez * Complex.Conjugate(hx) - ex * Complex.Conjugate(hz);
            Complex sz = ex * Complex.Conjugate(hy) - ey * Complex.Conjugate(hx);
            double radial = 0.5 * (sx.Real * dir.X + sy.Real * dir.Y + sz.Real * dir.Z);
            Assert.True(radial > 0, $"Poynting must point outward (got S·r̂ = {radial:g3})");
        }
    }

    private static Vector3D Unit(Vector3D v) => v / v.Length;
}
