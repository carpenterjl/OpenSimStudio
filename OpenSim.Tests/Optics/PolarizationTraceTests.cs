using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Polarization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-51: the polarization ray trace against closed forms derived in the comments here. Yun,
/// Crabtree and Chipman (Appl. Opt. 50, 2011) and Chipman, Lam and Young's textbook were not on
/// hand; nothing is copied from them. Radii, thicknesses, indices and transmittances are
/// arbitrary test inputs, not reference data.
/// <para>
/// <b>Fresnel coefficients, derived.</b> Take the interface z = 0 with normal η̂ = ẑ, the ray
/// k̂_i = (sin θ, 0, cos θ), ŝ = k̂_i × η̂ / |…| = −ŷ, p̂ = k̂ × ŝ for each of the incident,
/// reflected k̂_r = (sin θ, 0, −cos θ) and transmitted k̂_t = (sin θ_t, 0, cos θ_t) waves:
/// p̂_i = (cos θ, 0, −sin θ), p̂_r = (−cos θ, 0, −sin θ), p̂_t = (cos θ_t, 0, −sin θ_t). The
/// magnetic field of a plane wave is H ∝ n k̂ × E, so E = E_s ŝ gives H ∝ n E_s p̂ and E = E_p p̂
/// gives H ∝ −n E_p ŝ. Tangential E and H continuous:
/// s: E_i + E_r = E_t and n1 cos θ (E_i − E_r) = n2 cos θ_t E_t, so
/// r_s = (n1 c1 − n2 c2)/(n1 c1 + n2 c2), t_s = 2 n1 c1/(n1 c1 + n2 c2);
/// p: c1 (E_i − E_r) = c2 E_t and n1 (E_i + E_r) = n2 E_t, so
/// r_p = (n2 c1 − n1 c2)/(n2 c1 + n1 c2), t_p = 2 n1 c1/(n2 c1 + n1 c2),
/// with c1 = cos θ, c2 = cos θ_t, n1 sin θ = n2 sin θ_t. At normal incidence r_p = −r_s: that is
/// the s/p frame of the trace. The power through the interface per unit area scales as
/// n·cos·|E|², so the power-normalised amplitudes are r and t·√(n2 c2 / (n1 c1)), and
/// R + T = 1 follows from the two continuity equations.
/// </para>
/// </summary>
public sealed class PolarizationTraceTests
{
    // ---- Builders ----

    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false, bool mirror = false,
        string? coating = null, OpticalMaterial? glass = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = glass is null ? ParaxialMedium.Constant(n) : ParaxialMedium.Of(glass), IsStop = stop, IsMirror = mirror, Coating = coating,
    };

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static readonly OpticalSystemData Sys = new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
    };

    private static PolarizationTracer Tracer(SequentialLens lens, CoatingCatalog? catalog = null, PolarizationOptions? options = null) =>
        new(SequentialTrace.Create(lens, Sys), catalog ?? CoatingCatalog.Empty, options);

    private static void Close(double expected, double actual, double tolerance, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    private static void Close(Complex expected, Complex actual, double tolerance, string what = "")
    {
        Assert.True((actual - expected).Magnitude <= tolerance,
            $"{what} expected {expected}, got {actual} (difference {(actual - expected).Magnitude:R}, allowed {tolerance:R}).");
    }

    private static Ray RayAt(PolarizationTracer tracer, double thetaDegrees)
    {
        double t = thetaDegrees * Math.PI / 180;
        return tracer.Trace.NewRay(Vector3D.Zero, new Vector3D(0, Math.Sin(t), Math.Cos(t)));
    }

    /// <summary>The closed forms of the class comment: (r_s, r_p, power-normalised t_s, t_p).</summary>
    private static (double Rs, double Rp, double Ts, double Tp) Fresnel(double n1, double n2, double thetaDegrees)
    {
        double c1 = Math.Cos(thetaDegrees * Math.PI / 180);
        double s2 = n1 * Math.Sin(thetaDegrees * Math.PI / 180) / n2;
        double c2 = Math.Sqrt(1 - s2 * s2);
        double rs = (n1 * c1 - n2 * c2) / (n1 * c1 + n2 * c2);
        double rp = (n2 * c1 - n1 * c2) / (n2 * c1 + n1 * c2);
        double scale = Math.Sqrt(n2 * c2 / (n1 * c1));
        return (rs, rp, 2 * n1 * c1 / (n1 * c1 + n2 * c2) * scale, 2 * n1 * c1 / (n2 * c1 + n1 * c2) * scale);
    }

    private static CoatingCatalog QuarterWaveCatalog() => new()
    {
        Materials = new[] { CoatingMaterial.Constant("LOW", 1.38), CoatingMaterial.Constant("HIGH", 2.1) },
        Coatings = new Coating[]
        {
            new LayerCoating { Name = "QW", Layers = new[] { new CoatingLayer { Material = "LOW", Thickness = 0.25 } } },
            new LayerCoating
            {
                Name = "HL", Layers = new[]
                {
                    new CoatingLayer { Material = "HIGH", Thickness = 0.25 }, new CoatingLayer { Material = "LOW", Thickness = 0.25 },
                }
            },
        }
    };

    // ---- Gate: Fresnel amplitudes and energy conservation at a lossless interface ----

    [Fact]
    public void BareInterface_AmplitudesAreTheFresnelClosedForms_AndRPlusTIsOne()
    {
        // Surface 1: air to 1.5; surface 2: 1.5 back to air (critical angle asin(1/1.5) ≈ 41.8°).
        var tracer = Tracer(Lens(S(double.PositiveInfinity, 0.005, 1.5, stop: true), S(double.PositiveInfinity, 0.01)));
        foreach (var (surface, n1, n2, maxAngle) in new[] { (1, 1.0, 1.5, 89.0), (2, 1.5, 1.0, 41.0) })
        {
            for (double theta = 0; theta <= maxAngle; theta += maxAngle / 20)
            {
                var (rs, rp, ts, tp) = tracer.InterfaceAmplitudes(surface, theta);
                var f = Fresnel(n1, n2, theta);
                string at = $"surface {surface}, {theta}°:";
                Close(f.Rs, rs, 1e-14, at + " r_s");
                Close(f.Rp, rp, 1e-14, at + " r_p");
                Close(f.Ts, ts, 1e-14, at + " t_s");
                Close(f.Tp, tp, 1e-14, at + " t_p");
                Close(1, rs.Magnitude * rs.Magnitude + ts.Magnitude * ts.Magnitude, 1e-14, at + " R_s + T_s");
                Close(1, rp.Magnitude * rp.Magnitude + tp.Magnitude * tp.Magnitude, 1e-14, at + " R_p + T_p");
            }
        }
    }

    [Fact]
    public void LosslessCoating_ConservesEnergy_ForSAndP()
    {
        // A dielectric stack absorbs nothing, so R + T = 1 for each polarization at any angle.
        var tracer = Tracer(Lens(S(double.PositiveInfinity, 0.005, 1.52, stop: true, coating: "HL"), S(double.PositiveInfinity, 0.01, coating: "QW")),
            QuarterWaveCatalog());
        foreach (int surface in new[] { 1, 2 })
            for (double theta = 0; theta <= 40; theta += 4)
            {
                var (rs, rp, ts, tp) = tracer.InterfaceAmplitudes(surface, theta);
                Close(1, rs.Magnitude * rs.Magnitude + ts.Magnitude * ts.Magnitude, 1e-13, $"surface {surface}, {theta}°: s");
                Close(1, rp.Magnitude * rp.Magnitude + tp.Magnitude * tp.Magnitude, 1e-13, $"surface {surface}, {theta}°: p");
            }
    }

    // ---- Gate: Brewster incidence ----

    [Fact]
    public void AtBrewsterIncidence_PReflectionVanishes_AndPTransmitsEverything()
    {
        // r_p = 0 ⇔ n2 c1 = n1 c2; with Snell n1 s1 = n2 s2 this gives s1 c1 = s2 c2, sin 2θ1 =
        // sin 2θ2, θ1 + θ2 = π/2, so tan θ_B = n2/n1.
        const double n = 1.5;
        double brewster = Math.Atan(n) * 180 / Math.PI;
        var tracer = Tracer(Lens(S(double.PositiveInfinity, 0.01, n, stop: true)));
        var (rs, rp, _, _) = tracer.InterfaceAmplitudes(1, brewster);
        Assert.True(rp.Magnitude < 1e-15, $"r_p at Brewster is {rp.Magnitude:R}.");
        Close(Fresnel(1, n, brewster).Rs, rs, 1e-14, "r_s at Brewster");

        // Through the trace: the ray (0, sin θ_B, cos θ_B) has the plane of incidence y–z. With the
        // X axis reference, P = K × X̂ = (0, cos θ, −sin θ) lies in that plane, so (0, 1) is p light.
        var ray = RayAt(tracer, brewster);
        var p = tracer.TraceRay(ray, JonesVector.AlongP);
        var s = tracer.TraceRay(ray, JonesVector.AlongS);
        Assert.True(p.Final.IsOk);
        Close(1, p.Transmission, 1e-14, "p transmission at Brewster");
        Close(1 - rs.Magnitude * rs.Magnitude, s.Transmission, 1e-14, "s transmission at Brewster");
        Assert.True(p.Surfaces[0].Response!.Value.P.R < 1e-30);
    }

    // ---- Gate: normal incidence through an uncoated lens ----

    [Fact]
    public void NormalIncidenceThroughAnUncoatedLens_TransmitsTheProductOfOneMinusR()
    {
        // The axial ray meets both vertices of a biconvex lens at normal incidence, where
        // R = ((n − 1)/(n + 1))² for either polarization, so T = (1 − R)².
        const double n = 1.5;
        var tracer = Tracer(Lens(S(0.05, 0.005, n, stop: true), S(-0.05, 0.09)));
        var ray = tracer.Trace.NewRay(Vector3D.Zero, Vector3D.UnitZ);
        double r = (n - 1) / (n + 1) * ((n - 1) / (n + 1));
        foreach (var jones in new[] { JonesVector.AlongS, JonesVector.AlongP, JonesVector.FromAmplitudes(1, 1, 0, 90) })
        {
            var path = tracer.TraceRay(ray, jones);
            Close((1 - r) * (1 - r), path.Transmission, 1e-14, "transmission");
            Close(1 - r, path.Surfaces[0].RelativeTransmission, 1e-14, "surface 1");
            Close(1 - r, path.Surfaces[1].RelativeTransmission, 1e-14, "surface 2");
            Close(1, path.Surfaces[2].RelativeTransmission, 1e-14, "image");
            Close(path.Transmission, path.Final.Intensity, 0, "Ray.Intensity");
            Assert.NotNull(path.Final.Field);
            Close(path.Transmission, path.Final.Field!.Value.NormSquared, 1e-15, "|Ray.Field|²");
        }
        Close((1 - r) * (1 - r), tracer.Transmission(tracer.Trace.TracePath(ray), new SystemPolarization { Unpolarized = true }), 1e-14, "unpolarized");
    }

    [Fact]
    public void NormalIncidenceThroughAnAbsorbingGlass_AddsTheInternalTransmittance()
    {
        // A glass of index 1.6 whose IT table gives t = 0.9 through 10 mm at both ends of its range:
        // α = −ln(0.9)/10 per mm everywhere, so 5 mm transmit 0.9^(5/10) inside the glass, and the
        // lens (1 − R)²·0.9^0.5 with R = (0.6/2.6)².
        var glass = new OpticalMaterial
        {
            Name = "ABSORB", Catalog = "test",
            Tabulated = new TabulatedIndex { Points = new[] { new TabulatedIndexPoint(0.4, 1.6, 0), new TabulatedIndexPoint(0.8, 1.6, 0) } },
            Transmittance = new[] { new TransmittancePoint(0.4, 0.9, 10), new TransmittancePoint(0.8, 0.9, 10) },
        };
        var tracer = Tracer(Lens(S(0.05, 0.005, stop: true, glass: glass), S(-0.05, 0.09)));
        var path = tracer.TraceRay(tracer.Trace.NewRay(Vector3D.Zero, Vector3D.UnitZ), JonesVector.AlongS);
        double r = 0.6 / 2.6 * (0.6 / 2.6);
        double it = Math.Pow(0.9, 0.5);
        Close(it, path.Surfaces[1].BulkTransmittance, 1e-15, "internal transmittance");
        Close((1 - r) * it, path.Surfaces[1].RelativeTransmission, 1e-14, "relative transmission of surface 2");
        Close((1 - r) * (1 - r) * it, path.Transmission, 1e-14, "transmission");

        var lossless = Tracer(Lens(S(0.05, 0.005, stop: true, glass: glass), S(-0.05, 0.09)), options: new PolarizationOptions { BulkAbsorption = false });
        Close((1 - r) * (1 - r), lossless.TraceRay(lossless.Trace.NewRay(Vector3D.Zero, Vector3D.UnitZ), JonesVector.AlongS).Transmission, 1e-14, "bulk off");
    }

    [Fact]
    public void BulkAbsorption_InterpolatesAlphaLinearly_AndRefusesWavelengthsOutsideTheTable()
    {
        // α_i = −ln(t_i)/τ_i per mm; between 0.5 and 0.7 µm α is linear in λ, so at 0.6 µm it is
        // the mean of the two ends.
        var glass = new OpticalMaterial
        {
            Name = "IT", Catalog = "test",
            Tabulated = new TabulatedIndex { Points = new[] { new TabulatedIndexPoint(0.3, 1.5, 0), new TabulatedIndexPoint(1.0, 1.5, 0) } },
            Transmittance = new[] { new TransmittancePoint(0.7, 0.99, 25), new TransmittancePoint(0.5, 0.8, 10), new TransmittancePoint(0.6, 0.5, null) },
        };
        double a5 = -Math.Log(0.8) / 10 * 1000, a7 = -Math.Log(0.99) / 25 * 1000;
        var medium = ParaxialMedium.Of(glass);
        Close((a5 + a7) / 2, BulkAbsorption.Coefficient(medium, 0.6), 1e-12, "α at 0.6 µm (the point without a thickness is left out)");
        Close(a5, BulkAbsorption.Coefficient(medium, 0.5), 0, "α at a table point");
        var ex = Assert.Throws<InvalidOperationException>(() => BulkAbsorption.Coefficient(medium, 0.45));
        Assert.Contains("IT", ex.Message);
        Assert.Contains("0.5–0.7", ex.Message);
        Assert.Equal(0, BulkAbsorption.Coefficient(ParaxialMedium.Constant(1.5), 0.55));
    }

    // ---- Gate: unpolarized is the mean of two orthogonal states ----

    [Fact]
    public void UnpolarizedTransmission_IsTheMeanOfAnyTwoOrthogonalStates()
    {
        // |P·E|² is a Hermitian quadratic form E^H G E; its mean over any orthonormal pair of
        // Jones vectors is tr(G)/2, so (1, 0) and (0, 1), the two circular states and a rotated
        // linear pair all give the same mean.
        var lens = Lens(S(0.04, 0.006, 1.6, stop: true, coating: "HL"), S(-0.03, 0.02, coating: "QW"), S(-0.1, -0.03, mirror: true));
        var tracer = Tracer(lens, QuarterWaveCatalog());
        var launch = tracer.Trace.NewRay(new Vector3D(0.0012, -0.0021, 0), new Vector3D(0.08, 0.15, 1));
        var path = tracer.Trace.TracePath(launch);
        Assert.True(path.Final.IsOk);
        double unpolarized = tracer.Transmission(path, new SystemPolarization { Unpolarized = true });
        double Mean(JonesVector a, JonesVector b) => (tracer.TracePath(path, a).Transmission + tracer.TracePath(path, b).Transmission) / 2;
        double h = 1 / Math.Sqrt(2);
        Close(Mean(JonesVector.AlongS, JonesVector.AlongP), unpolarized, 0, "definition");
        Close(unpolarized, Mean(new JonesVector(h, new Complex(0, h)), new JonesVector(h, new Complex(0, -h))), 1e-14, "circular pair");
        double a = 0.37;
        Close(unpolarized, Mean(new JonesVector(Math.Cos(a), Math.Sin(a)), new JonesVector(-Math.Sin(a), Math.Cos(a))), 1e-14, "rotated pair");
        // A polarizing system: the two linear states differ, so the test is not trivial.
        Assert.True(Math.Abs(tracer.TracePath(path, JonesVector.AlongS).Transmission - tracer.TracePath(path, JonesVector.AlongP).Transmission) > 1e-6);
    }

    // ---- Gate: the field stays transverse ----

    [Fact]
    public void TheFieldStaysTransverseToTheRay_AtEverySurface()
    {
        var lens = Lens(S(0.04, 0.006, 1.6, stop: true, coating: "HL"), S(-0.03, 0.02, coating: "QW"), S(-0.1, -0.03, mirror: true));
        var tracer = Tracer(lens, QuarterWaveCatalog());
        var trace = tracer.Trace;
        int traced = 0;
        foreach (var (x, y) in new[] { (0.0, 0.0), (0.002, 0.0), (-0.001, 0.0025), (0.003, -0.002) })
            for (int m = 0; m < 8; m++)
            {
                double b = m * Math.PI / 4, a = 0.12;
                var launch = trace.NewRay(new Vector3D(x, y, 0), new Vector3D(Math.Sin(a) * Math.Cos(b), Math.Sin(a) * Math.Sin(b), Math.Cos(a)));
                foreach (var jones in new[] { JonesVector.AlongS, JonesVector.FromAmplitudes(0.6, 0.8, 10, -70) })
                {
                    var path = tracer.TraceRay(launch, jones);
                    if (!path.Final.IsOk) continue;
                    traced++;
                    Assert.True(path.LaunchField.Along(path.Path.Hits[0].Direction).Magnitude <= 1e-15);
                    foreach (var s in path.Surfaces)
                    {
                        var placement = trace.GlobalPlacement(s.Surface);
                        var kIn = placement.ApplyDirection(s.Hit.IncomingDirection);
                        var kOut = placement.ApplyDirection(s.Hit.Direction);
                        double scale = Math.Sqrt(s.FieldOut.NormSquared);
                        Assert.True(s.FieldIn.Along(kIn).Magnitude <= 1e-13 * scale, $"surface {s.Surface}: E·k before is {s.FieldIn.Along(kIn).Magnitude:R}.");
                        Assert.True(s.FieldOut.Along(kOut).Magnitude <= 1e-13 * scale, $"surface {s.Surface}: E·k after is {s.FieldOut.Along(kOut).Magnitude:R}.");
                    }
                    Assert.True(path.Final.Field!.Value.Along(path.Final.Direction).Magnitude <= 1e-13);
                }
            }
        Assert.True(traced >= 32, $"Only {traced} rays reached the image.");
    }

    // ---- The s/p frame and the ray-equivalent phase ----

    [Fact]
    public void NormalIncidenceOnAMirror_MultipliesTheWholeFieldByRs()
    {
        // At normal incidence ŝ is any transverse unit vector, p̂_out = k̂_out × ŝ = −p̂_in, and
        // r_p = −r_s in this frame, so P = r_s(ŝŝᵀ + p̂p̂ᵀ): the field vector is scaled by r_s
        // whatever its polarization (a circular state keeps its rotation in fixed axes).
        var tracer = Tracer(Lens(S(double.PositiveInfinity, -0.01, stop: true, mirror: true)));
        var path = tracer.TraceRay(tracer.Trace.NewRay(Vector3D.Zero, Vector3D.UnitZ), JonesVector.FromAmplitudes(1, 1, 0, 90));
        var surface = path.Surfaces[0];
        Close(-surface.AmplitudeS, surface.AmplitudeP, 1e-15, "a_p = −a_s");
        Close(surface.AmplitudeS * surface.FieldIn.X, surface.FieldOut.X, 1e-15, "Ex");
        Close(surface.AmplitudeS * surface.FieldIn.Y, surface.FieldOut.Y, 1e-15, "Ey");
        // The uncoated mirror is aluminium (0.7 + 7.0i here): R = |(1 − N)/(1 + N)|² at normal incidence.
        var aluminium = CoatingEvaluator.UncoatedMirrorIndex;
        var rs = (1 - aluminium) / (1 + aluminium);
        Close(rs.Magnitude * rs.Magnitude, path.Transmission, 1e-14, "mirror reflectance");
    }

    [Fact]
    public void AnIndexMatchedLayer_LeavesTheRayEquivalentCoefficientsAtTheBareValues()
    {
        // A layer of the incident medium's own index is only more of that medium. Its thin-film
        // coefficient refers the field to the layer's outer face, a distance D before the surface,
        // so t_field = t_bare·e^(iψ) and r_field = r_bare·e^(2iψ), ψ = 2π n0 D cos θ / λ (a plane
        // wave's phase from z = −D to 0 at a fixed transverse point). The ray-equivalent
        // coefficients must be the bare ones exactly.
        var catalog = new CoatingCatalog
        {
            Materials = new[] { CoatingMaterial.Constant("AIR", 1.0), CoatingMaterial.Constant("AL", 0.7, 7.0) },
            Coatings = new Coating[]
            {
                new LayerCoating { Name = "SPACER", Layers = new[] { new CoatingLayer { Material = "AIR", Thickness = 0.3, IsAbsolute = true } } },
                new LayerCoating
                {
                    Name = "MSPACER", Layers = new[]
                    {
                        new CoatingLayer { Material = "AIR", Thickness = 0.3, IsAbsolute = true },
                        new CoatingLayer { Material = "AL", Thickness = 1, IsAbsolute = true },
                    }
                },
            }
        };
        double lambda = Sys.Wavelengths.Primary.Micrometers;
        var coated = Tracer(Lens(S(double.PositiveInfinity, 0.01, 1.5, stop: true, coating: "SPACER")), catalog);
        var bare = Tracer(Lens(S(double.PositiveInfinity, 0.01, 1.5, stop: true)), catalog);
        var coatedMirror = Tracer(Lens(S(double.PositiveInfinity, -0.01, stop: true, mirror: true, coating: "MSPACER")), catalog);
        var bareMirror = Tracer(Lens(S(double.PositiveInfinity, -0.01, stop: true, mirror: true)), catalog);
        foreach (double theta in new[] { 0.0, 15, 35, 60 })
        {
            double psi = 2 * Math.PI * 0.3 * Math.Cos(theta * Math.PI / 180) / lambda;
            var c = coated.TraceRay(RayAt(coated, theta), JonesVector.AlongS).Surfaces[0];
            var b = bare.TraceRay(RayAt(bare, theta), JonesVector.AlongS).Surfaces[0];
            Close(b.AmplitudeS, c.AmplitudeS, 1e-13, $"{theta}°: ray a_s");
            Close(b.AmplitudeP, c.AmplitudeP, 1e-13, $"{theta}°: ray a_p");
            Close(b.FieldAmplitudeS * Complex.FromPolarCoordinates(1, psi), c.FieldAmplitudeS, 1e-13, $"{theta}°: field a_s");
            Close(0.3e-6, c.CoatingThickness, 1e-22, "thickness");

            var cm = coatedMirror.TraceRay(RayAt(coatedMirror, theta), JonesVector.AlongS).Surfaces[0];
            var bm = bareMirror.TraceRay(RayAt(bareMirror, theta), JonesVector.AlongS).Surfaces[0];
            Close(bm.AmplitudeS, cm.AmplitudeS, 1e-13, $"{theta}°: mirror ray a_s");
            Close(bm.AmplitudeP, cm.AmplitudeP, 1e-13, $"{theta}°: mirror ray a_p");
            Close(bm.FieldAmplitudeP * Complex.FromPolarCoordinates(1, 2 * psi), cm.FieldAmplitudeP, 1e-13, $"{theta}°: mirror field a_p");
        }

        var unconverted = Tracer(Lens(S(double.PositiveInfinity, 0.01, 1.5, stop: true, coating: "SPACER")), catalog,
            new PolarizationOptions { ConvertThinFilmPhase = false });
        var u = unconverted.TraceRay(RayAt(unconverted, 20), JonesVector.AlongS).Surfaces[0];
        Assert.Equal(u.FieldAmplitudeS, u.AmplitudeS);
    }

    [Fact]
    public void LaunchBasis_FollowsOpticStudiosThreeRules()
    {
        // Help, "Defining the Initial Polarization": X P = K × X̂, S = P × K; Y S = Ŷ × K, P = K × S;
        // Z S = K × Ẑ, P = K × S. For K = +z the X and Y rules give S = x̂, P = ŷ.
        foreach (var reference in PolarizationReferences.Names.All)
        {
            var (s, p) = JonesVector.LaunchBasis(Vector3D.UnitZ, reference);
            Close(1, s.X, 1e-15, $"{reference} S");
            Close(1, p.Y, 1e-15, $"{reference} P");
        }
        var k = new Vector3D(0.2, -0.3, 0.9).Normalized();
        var (sx, px) = JonesVector.LaunchBasis(k, PolarizationReference.XAxis);
        var expectedP = Vector3D.Cross(k, Vector3D.UnitX).Normalized();
        Close(0, (px - expectedP).Length, 1e-15, "X rule P");
        Close(0, (sx - Vector3D.Cross(expectedP, k)).Length, 1e-15, "X rule S");
        var (sy, _) = JonesVector.LaunchBasis(k, PolarizationReference.YAxis);
        Close(0, (sy - Vector3D.Cross(Vector3D.UnitY, k).Normalized()).Length, 1e-15, "Y rule S");
        var (sz, _) = JonesVector.LaunchBasis(k, PolarizationReference.ZAxis);
        Close(0, (sz - Vector3D.Cross(k, Vector3D.UnitZ).Normalized()).Length, 1e-15, "Z rule S");
    }

    [Fact]
    public void ACoatingTheCatalogLacks_IsRefused_NamingTheSurface()
    {
        var lens = Lens(S(double.PositiveInfinity, 0.01, 1.5, stop: true, coating: "NOPE"));
        var ex = Assert.Throws<InvalidOperationException>(() => Tracer(lens, QuarterWaveCatalog()));
        Assert.Contains("surface 1", ex.Message);
        Assert.Contains("'NOPE'", ex.Message);
        Assert.Contains("QW", ex.Message);
    }

    // ---- System polarization settings ----

    [Fact]
    public void SystemPolarization_RoundTripsThroughTheFileForm_AndAnOldFileGetsTheDefault()
    {
        var p = new SystemPolarization
        {
            Unpolarized = true, Jx = 0.25, Jy = 1.0 / 3, XPhaseDegrees = -12.5, YPhaseDegrees = 0.1 + 0.2,
            Reference = PolarizationReference.ZAxis, ConvertThinFilmPhase = false,
        };
        var notes = new List<string>();
        var data = new OpticalSystemData { Polarization = p };
        var settings = SystemDataSettings.ToSettings(data);
        Assert.Equal("ZAxis", settings.Polarization!.Reference);
        Assert.Equal(p, SystemDataSettings.FromSettings(settings, notes).Polarization);
        Assert.Empty(notes);

        Assert.Equal(SystemPolarization.Default, SystemDataSettings.FromSettings(new OpticsSystemSettings(), notes).Polarization);
        Assert.Empty(notes);
        var odd = SystemDataSettings.FromSettings(new OpticsSystemSettings { Polarization = new OpticsPolarizationSettings { Reference = "WAxis" } }, notes);
        Assert.Equal(PolarizationReference.XAxis, odd.Polarization.Reference);
        Assert.Contains(notes, n => n.Contains("'WAxis'"));

        Assert.NotEmpty(new OpticalSystemData { Polarization = new SystemPolarization { Jx = 0, Jy = 0 } }.Validate());
        Assert.Empty(new OpticalSystemData { Polarization = new SystemPolarization { Jx = 0, Jy = 0, Unpolarized = true } }.Validate());
    }

    [Fact]
    public void JonesVector_IsNormalised_AndItsPhasesAreOpticStudios()
    {
        // A phase φ in OpticStudio's convention is e^(−iφ) in this program's (§12, §21).
        var j = JonesVector.FromAmplitudes(3, 4, 0, 90);
        Close(1, j.Intensity, 1e-15, "intensity");
        Close(new Complex(0.6, 0), j.X, 1e-15, "Jx");
        Close(new Complex(0, -0.8), j.Y, 1e-15, "Jy");
        Assert.Throws<ArgumentException>(() => JonesVector.FromAmplitudes(0, 0, 0, 0));
    }

    // ---- The analyses ----

    [Fact]
    public void TheFivePolarizationAnalyses_AreRegistered_AndSaySoWhenThereAreNoSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        foreach (var id in new[]
        {
            PolarizationRayTraceAnalysis.SavedId, PolarizationPupilMapAnalysis.SavedId, PolarizationTransmissionAnalysis.SavedId,
            PolarizationPhaseAberrationAnalysis.SavedId, PolarizationTransmissionFanAnalysis.SavedId,
        })
        {
            Assert.True(registry.TryGet(id, out var analysis), id);
            Assert.Equal("Polarization", analysis.Category);
            var result = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), analysis.CreateDefaultSettings(), default);
            Assert.Equal(new[] { "No surfaces" }, result.Sections.Select(s => s.Heading));
        }
    }

    [Fact]
    public void TheAnalyses_RunOnALens()
    {
        var lens = Lens(S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.09));
        var context = new OpticsAnalysisContext("P", new OpticsSettings
        {
            SystemData = SystemDataSettings.ToSettings(new OpticalSystemData
            {
                Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
                Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint(), new FieldPoint { Y = 5 } } },
            })
        });

        var rayTrace = new PolarizationRayTraceAnalysis(_ => lens).Compute(context, new PolarizationRayTraceSettings { Py = 0.5, Jy = 1 }, default);
        Assert.Equal(new[] { "Ray", "Surfaces", "System polarization matrix (surface 1's frame)" }, rayTrace.Sections.Select(s => s.Heading));
        Assert.Equal(lens.Surfaces.Count - 1, rayTrace.Sections[1].TableRows.Count);

        var map = new PolarizationPupilMapAnalysis(_ => lens).Compute(context, new PolarizationPupilMapSettings { Sampling = 5 }, default);
        Assert.Equal(2, ((LinePlot)map.Plots[0]).Curves.Count);
        Assert.Equal(BatchTracer.SquareGrid(5).Count, map.Sections[1].TableRows.Count);

        // The integrated transmission of an uncoated singlet is a percentage near the normal-incidence (1 − R)².
        var transmission = new PolarizationTransmissionAnalysis(_ => lens).Compute(context, new PolarizationTransmissionSettings { Unpolarized = true, Sampling = 6 }, default);
        Assert.Equal(2, transmission.Sections[0].TableRows.Count);
        double r = 0.04;   // ((1.5 − 1)/(1.5 + 1))²
        double percent = double.Parse(transmission.Sections[0].TableRows[0][2], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(percent <= 100 && percent > 90, $"integrated transmission {percent}%");
        Assert.Equal(3, transmission.Sections.Count);

        var phase = new PolarizationPhaseAberrationAnalysis(_ => lens).Compute(context, new PolarizationPhaseAberrationSettings { Field = 2, Points = 7 }, default);
        Assert.Equal(2, phase.Plots.Count);

        var fan = new PolarizationTransmissionFanAnalysis(_ => lens).Compute(context, new PolarizationTransmissionFanSettings { Points = 5 }, default);
        Assert.Equal(2, fan.Plots.Count);
        Assert.Equal(2, ((LinePlot)fan.Plots[0]).Curves.Count);
        // The chief ray of the axial field is at normal incidence on both vertices.
        Close((1 - r) * (1 - r), ((LinePlot)fan.Plots[0]).Curves[0].Y[2], 1e-14, "axial chief ray in the fan");
    }
}
