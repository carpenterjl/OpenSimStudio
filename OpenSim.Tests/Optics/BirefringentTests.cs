using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Polarization;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Birefringent;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-53: birefringent media (docs/Optics-Conventions.md §62) against closed forms.
/// <para>
/// <b>Calcite.</b> n_o and n_e come from G. Ghosh, "Dispersion-equation coefficients for the
/// refractive index and birefringence of calcite and quartz crystals", Opt. Commun. 163 (1999)
/// 95–102, as the refractiveindex.info database gives them (plans/sources/optics/riidb/database/
/// data/main/CaCO3/nk/Ghosh-o.yml and Ghosh-e.yml, "type: formula 2"): ordinary
/// 0.73358749 0.96464345 1.94325203e-2 1.82831454 120, extraordinary 0.35859695 0.82427830
/// 1.06689543e-2 0.14429128 120, evaluated by <see cref="RefractiveIndexInfoFormulas"/> (formula
/// 2, n² − 1 = C1 + Σ C(2j)λ²/(λ² − C(2j+1))) at the system's primary wavelength. They enter
/// the lenses as constant indices, so the tests do not depend on the air conversion.
/// </para>
/// <para>
/// <b>Walk-off, derived.</b> OpticStudio 2025 R2 help, "Birefringent In and Birefringent Out"
/// (plans/sources/optics/op-53-help/), equations 3–5: (1/n(θ_w))² = (cos θ_w/n_o)² +
/// (sin θ_w/n_e)², and the angle α between the wave vector and the ray,
/// tan α = (n_e² − n_o²) tan θ_w / (n_e² + (n_o tan θ_w)²). At normal incidence on a plate the
/// tangential wave vector is zero, so both waves keep k̂ = ẑ; the extraordinary ray leaves the
/// normal by α toward (n_e &gt; n_o) or away from the axis and crosses a plate of thickness d
/// displaced by d·tan α; its optical path (phase) is N·Δr = n(θ_w)·d since N = n(θ_w)ẑ and Δr
/// has z part d. The ray formula of <see cref="UniaxialCrystal.RayDirection"/>,
/// ŝ ∝ N/n_e² + Δ(N·â)â, gives for k̂ = ẑ, â = (0, sin θ, cos θ):
/// tan α = s_y/s_z = Δ cos θ sin θ / (1/n_e² + Δ cos² θ); multiplying by n_o²n_e²,
/// = (n_e² − n_o²) cos θ sin θ / (n_o² sin² θ + n_e² cos² θ), which is equation 5 after dividing
/// by cos² θ.
/// </para>
/// </summary>
public sealed class BirefringentTests
{
    private static readonly OpticalSystemData Sys = new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.002 },
    };

    private static readonly double Micrometers = Sys.Wavelengths.Primary.Micrometers;

    // Ghosh 1999 via refractiveindex.info (class comment).
    private static readonly double[] GhoshOrdinary = { 0.73358749, 0.96464345, 1.94325203e-2, 1.82831454, 120 };
    private static readonly double[] GhoshExtraordinary = { 0.35859695, 0.82427830, 1.06689543e-2, 0.14429128, 120 };
    private static double No => RefractiveIndexInfoFormulas.Evaluate(2, GhoshOrdinary, Micrometers);
    private static double Ne => RefractiveIndexInfoFormulas.Evaluate(2, GhoshExtraordinary, Micrometers);

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static SequentialSurface In(int mode, Vector3D axis, double thickness, bool stop = false, double? no = null, double? ne = null) => new()
    {
        Shape = new BirefringentInShape(mode, axis, paraxIgnore: 1, extraordinary: ParaxialMedium.Constant(ne ?? Ne)),
        Medium = ParaxialMedium.Constant(no ?? No), Thickness = thickness, IsStop = stop,
    };

    private static SequentialSurface Out(double thickness) => new() { Shape = new BirefringentOutShape(), Thickness = thickness };

    private static void Close(double expected, double actual, double tolerance, string what = "") =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    private static Vector3D Along(double yDegrees) =>
        new(0, Math.Sin(yDegrees * Math.PI / 180), Math.Cos(yDegrees * Math.PI / 180));

    // ---------------------------------------------------------------- gate: walk-off

    [Fact]
    public void ACalcitePlate_WalksTheExtraordinaryRayOff_ByTheClosedForm()
    {
        const double d = 0.002, thetaW = 45;
        Assert.True(No > 1.6 && Ne < 1.5 && Ne > 1.4, $"calcite indices n_o {No}, n_e {Ne}");
        foreach (int mode in new[] { 0, 1 })
        {
            var lens = Lens(In(mode, Along(thetaW), d, stop: true), Out(0.001));
            var trace = SequentialTrace.Create(lens, Sys);
            Assert.NotNull(trace.Birefringence);
            var path = trace.TracePath(trace.NewRay(Vector3D.Zero, Vector3D.UnitZ));
            Assert.True(path.Final.IsOk, path.Final.Status.ToString());
            var exit = path.Hits[2];
            double t = Math.Tan(thetaW * Math.PI / 180);
            if (mode == 0)
            {
                Assert.Equal(0, exit.Position.Y);
                Close(No * d, exit.SegmentOpticalPath, 1e-16, "ordinary optical path");
            }
            else
            {
                double tanAlpha = (Ne * Ne - No * No) * t / (Ne * Ne + No * t * No * t);   // help eq. 5
                Close(d * tanAlpha, exit.Position.Y, 1e-15, "walk-off displacement");
                double c = Math.Cos(thetaW * Math.PI / 180), s = Math.Sin(thetaW * Math.PI / 180);
                double nTheta = 1 / Math.Sqrt(c * c / (No * No) + s * s / (Ne * Ne));   // help eq. 3
                Close(nTheta * d, exit.SegmentOpticalPath, 1e-16, "extraordinary optical path (phase)");
                Close(d / Math.Cos(Math.Atan(tanAlpha)), exit.SegmentLength, 1e-16, "geometric length along the ray");
                var wave = path.Hits[1].WaveNormal!.Value;
                Close(nTheta, wave.Length, 1e-14, "|N| = n(θ_w)");
                Close(0, Vector3D.Dot(Vector3D.Cross(wave, path.Hits[1].Direction), Along(thetaW)), 1e-15, "k, s and the axis coplanar");
            }
            // Out of the plate the ray is parallel to the incident one again.
            Close(0, path.Final.Direction.Y, 1e-15, "exit direction y");
            Assert.Null(path.Hits[2].WaveNormal);
        }
    }

    [Fact]
    public void TheRayDirection_MatchesTheHelpsWalkOffAngle_AtManyAngles()
    {
        var crystal = new UniaxialCrystal(No, Ne, Vector3D.UnitZ);
        for (double deg = 1; deg < 90; deg += 7)
        {
            double th = deg * Math.PI / 180, t = Math.Tan(th);
            var k = new Vector3D(0, Math.Sin(th), Math.Cos(th));
            var s = crystal.RayDirection(CrystalMode.Extraordinary, k * crystal.EffectiveIndex(k));
            double tanAlpha = (Ne * Ne - No * No) * t / (Ne * Ne + No * t * No * t);
            Close(Math.Abs(Math.Atan(tanAlpha)), crystal.WalkOffAngle(k), 1e-14, $"α at θ_w = {deg}°");
            Close(0, Vector3D.Dot(Vector3D.Cross(k, s), Vector3D.UnitZ), 1e-15, "coplanar");
        }
    }

    // ---------------------------------------------------------------- gate: along the optic axis

    [Fact]
    public void AlongTheOpticAxis_BothModesCoincide()
    {
        // Normal incidence with the axis along the normal, and oblique incidence (10°) with the
        // axis along the refracted ordinary wave: the extraordinary sheet touches the ordinary
        // sphere there, so both modes refract alike.
        double thetaO = Math.Asin(Math.Sin(10 * Math.PI / 180) / No) * 180 / Math.PI;
        foreach (var (incidence, axis) in new[] { (0.0, Vector3D.UnitZ), (10.0, Along(thetaO)) })
        {
            RayPath Run(int mode)
            {
                var trace = SequentialTrace.Create(Lens(In(mode, axis, 0.003, stop: true), Out(0.002)), Sys);
                return trace.TracePath(trace.NewRay(Vector3D.Zero, Along(incidence)));
            }
            var o = Run(0);
            var e = Run(1);
            Assert.True(o.Final.IsOk && e.Final.IsOk);
            for (int h = 1; h < o.Hits.Count; h++)
            {
                Close(o.Hits[h].Position.Y, e.Hits[h].Position.Y, 1e-17, $"y at hit {h}, incidence {incidence}°");
                Close(o.Hits[h].Direction.Y, e.Hits[h].Direction.Y, 1e-14, $"direction at hit {h}");
                Close(o.Hits[h].OpticalPath, e.Hits[h].OpticalPath, 1e-17, $"optical path at hit {h}");
            }
            Close(No, e.Hits[1].WaveNormal!.Value.Length, 1e-14, "the extraordinary index along the axis is n_o");
        }
    }

    // ---------------------------------------------------------------- gate: half-wave plate

    [Theory]
    [InlineData(22.5)]
    [InlineData(10)]
    [InlineData(-35)]
    public void AHalfWavePlate_RotatesLinearPolarization_ByTwiceTheAxisAngle(double axisDegrees)
    {
        // Mode 2: the ordinary ray is traced and the extraordinary part keeps its phase. At
        // normal incidence with the axis in the plate, θ_w = 90°: n_e exactly, no walk-off, and
        // the retardance is 2π(n_e − n_o)d/λ (λ in the system's air, like the indices); d makes
        // it −π.
        double lambda = Micrometers * 1e-6;
        double d = lambda / (2 * Math.Abs(Ne - No));
        double th = axisDegrees * Math.PI / 180;
        var axis = new Vector3D(Math.Cos(th), Math.Sin(th), 0);
        var lens = Lens(In(2, axis, d, stop: true), Out(0.001));
        var tracer = new PolarizationTracer(SequentialTrace.Create(lens, Sys), CoatingCatalog.Empty);
        var result = tracer.TraceRay(tracer.Trace.NewRay(Vector3D.Zero, Vector3D.UnitZ), JonesVector.AlongS);
        Assert.True(result.Final.IsOk);
        Assert.Equal(1, result.LaunchField.X.Real);   // the launch field is x̂ (JonesVector.LaunchBasis, K = +z)

        // Expected (derived): x̂ = cos θ â − sin θ ô with ô = ẑ × â. Each part crosses the two
        // faces with the power-normalised normal-incidence amplitude t̃ = √(n2/n1)·2n1/(n1 + n2)
        // (PolarizationTraceTests' Fresnel form), so through the plate a = 4n/(1 + n)². A
        // retardance of π flips one part: E ∝ a_e cos θ â + a_o sin θ ô, linear at
        // θ + atan((a_o/a_e) tan θ) from x̂, which is 2θ when a_o = a_e.
        double ae = 4 * Ne / ((1 + Ne) * (1 + Ne)), ao = 4 * No / ((1 + No) * (1 + No));
        double expected = th + Math.Atan(ao / ae * Math.Tan(th));
        var e = result.Final.Field!.Value;
        Assert.True(e.Z.Magnitude < 1e-15, "transverse");
        double cross = (e.X * Complex.Conjugate(e.Y)).Imaginary;
        Close(0, cross, 1e-15, "linear (no circular part)");
        double orientation = 0.5 * Math.Atan2(2 * (e.X * Complex.Conjugate(e.Y)).Real, e.X.Magnitude * e.X.Magnitude - e.Y.Magnitude * e.Y.Magnitude);
        Close(expected, orientation, 1e-12, "orientation");
        Close(ae * ae * Math.Cos(th) * Math.Cos(th) + ao * ao * Math.Sin(th) * Math.Sin(th), result.Transmission, 1e-14, "power");
        // The Fresnel faces bend it from exactly 2θ by atan((a_o/a_e) tan θ) − θ; bounded here.
        Assert.True(Math.Abs(orientation - 2 * th) <= Math.Abs(Math.Atan(ao / ae * Math.Tan(th)) - th) + 1e-12);
    }

    [Fact]
    public void Mode0_KeepsOnlyTheOrdinaryPart_AndMode1OnlyTheExtraordinary()
    {
        double th = 30 * Math.PI / 180;
        var axis = new Vector3D(Math.Cos(th), Math.Sin(th), 0);
        foreach (int mode in new[] { 0, 1 })
        {
            var tracer = new PolarizationTracer(SequentialTrace.Create(Lens(In(mode, axis, 0.001, stop: true), Out(0.001)), Sys), CoatingCatalog.Empty);
            var result = tracer.TraceRay(tracer.Trace.NewRay(Vector3D.Zero, Vector3D.UnitZ), JonesVector.AlongS);
            double n = mode == 0 ? No : Ne;
            double a = 4 * n / ((1 + n) * (1 + n));
            double part = mode == 0 ? Math.Sin(th) : Math.Cos(th);   // x̂ = cos θ â − sin θ ô
            Close(a * a * part * part, result.Transmission, 1e-14, $"mode {mode} power");
            var e = result.Final.Field!.Value;
            var along = mode == 0 ? new Vector3D(-Math.Sin(th), Math.Cos(th), 0) : axis;
            Close(Math.Sqrt(result.Transmission), Math.Abs(e.Along(along).Magnitude), 1e-14, "the field lies along the mode's direction");
        }
    }

    // ---------------------------------------------------------------- gate: Wollaston prism

    [Fact]
    public void AWollastonPrism_DeviatesEachMode_BySnellsLawAtTheCementedFace()
    {
        // Two calcite wedges cemented at β = 20° (a coordinate break tilts the face about x),
        // the first with its axis along y, the second along x, as the help's sample ("the
        // crystal axis oriented at 90 degrees to each other"). A ray along z meets the first
        // face normally; in both wedges k̂ stays in the y-z plane, at right angles to either axis,
        // so θ_w = 90°, the extraordinary index is n_e and there is no walk-off. Derived:
        // ordinary then extraordinary, n_o sin β = n_e sin β′ and at the exit face (normal ẑ)
        // n_e sin(β′ − β) = sin δ; extraordinary then ordinary, n_e sin β = n_o sin β″ and
        // n_o sin(β″ − β) = sin δ. The two leave on opposite sides of the axis.
        const double beta = 20 * Math.PI / 180;
        SequentialLens Prism(int m1, int m2) => Lens(
            In(m1, Vector3D.UnitY, 0.005, stop: true),
            new SequentialSurface { CoordinateBreak = new CoordinateBreak(new TiltDecenter(0, 0, 20, 0, 0)) },
            In(m2, Vector3D.UnitX, 0),
            new SequentialSurface { CoordinateBreak = new CoordinateBreak(new TiltDecenter(0, 0, -20, 0, 0)), Thickness = 0.005 },
            Out(0.01));
        double Deviation(int m1, int m2)
        {
            var trace = SequentialTrace.Create(Prism(m1, m2), Sys);
            var final = trace.Trace(trace.NewRay(Vector3D.Zero, Vector3D.UnitZ));
            Assert.True(final.IsOk, final.Status.ToString());
            return final.Direction.Y;
        }
        double sinA = Math.Abs(Ne * Math.Sin(Math.Asin(No * Math.Sin(beta) / Ne) - beta));
        double sinB = Math.Abs(No * Math.Sin(Math.Asin(Ne * Math.Sin(beta) / No) - beta));
        double a = Deviation(0, 1), b = Deviation(1, 0);
        Close(sinA, Math.Abs(a), 1e-14, "ordinary→extraordinary deviation sine");
        Close(sinB, Math.Abs(b), 1e-14, "extraordinary→ordinary deviation sine");
        Assert.True(a * b < 0, $"the two modes leave on opposite sides ({a}, {b})");
        // Mode 0 in both wedges: the ordinary index on both sides of the cemented face, no deviation.
        Close(0, Deviation(0, 0), 1e-15, "ordinary throughout");
    }

    // ---------------------------------------------------------------- the interface solver

    private static (double Rs, double Rp, double Ts, double Tp) Fresnel(double n1, double n2, double thetaDegrees)
    {
        // PolarizationTraceTests' closed forms (derived in its class comment), power-normalised.
        double c1 = Math.Cos(thetaDegrees * Math.PI / 180);
        double s2 = n1 * Math.Sin(thetaDegrees * Math.PI / 180) / n2;
        double c2 = Math.Sqrt(1 - s2 * s2);
        double rs = (n1 * c1 - n2 * c2) / (n1 * c1 + n2 * c2);
        double rp = (n2 * c1 - n1 * c2) / (n2 * c1 + n1 * c2);
        double scale = Math.Sqrt(n2 * c2 / (n1 * c1));
        return (rs, rp, 2 * n1 * c1 / (n1 * c1 + n2 * c2) * scale, 2 * n1 * c1 / (n2 * c1 + n1 * c2) * scale);
    }

    [Fact]
    public void TheInterfaceSolver_GivesFresnelsCoefficients_BetweenIsotropicMedia_AndForEqualIndices()
    {
        const double theta = 30, n1 = 1.0, n2 = 1.5;
        var kappa = new Vector3D(n1 * Math.Sin(theta * Math.PI / 180), 0, 0);
        var (rs, rp, ts, tp) = Fresnel(n1, n2, theta);
        var iso = AnisotropicInterface.Solve(OpticalSide.Isotropic(n1), OpticalSide.Isotropic(n2), kappa, Vector3D.UnitZ, Vector3D.UnitX);
        var S = CrystalMode.Ordinary;
        var P = CrystalMode.Extraordinary;
        Close(Math.Abs(ts), iso.Transmission(S, S).Magnitude, 1e-14, "t_s");
        Close(Math.Abs(tp), iso.Transmission(P, P).Magnitude, 1e-14, "t_p");
        Close(Math.Abs(rs), iso.Reflection(S, S).Magnitude, 1e-14, "r_s");
        Close(Math.Abs(rp), iso.Reflection(P, P).Magnitude, 1e-14, "r_p");
        Close(0, iso.Transmission(P, S).Magnitude, 1e-14, "no s to p");

        // A "crystal" with n_o = n_e and any axis is isotropic: the transmitted power of each
        // incident polarization, summed over the two modes, is Fresnel's.
        var crystal = new UniaxialCrystal(n2, n2, new Vector3D(0.3, -0.5, 0.8));
        var eq = AnisotropicInterface.Solve(OpticalSide.Isotropic(n1), OpticalSide.Of(crystal), kappa, Vector3D.UnitZ, Vector3D.UnitX);
        double Power(Func<CrystalMode, Complex> a) => a(S).Magnitude * a(S).Magnitude + a(P).Magnitude * a(P).Magnitude;
        Close(ts * ts, Power(m => eq.Transmission(m, S)), 1e-14, "T_s");
        Close(tp * tp, Power(m => eq.Transmission(m, P)), 1e-14, "T_p");
        Close(rs * rs, Power(m => eq.Reflection(m, S)), 1e-14, "R_s");
    }

    [Fact]
    public void TheInterfaceSolver_ConservesPower_IntoOutOfAndBetweenCrystals()
    {
        var calcite = new UniaxialCrystal(No, Ne, new Vector3D(0.3, -0.5, 0.8));
        var turned = new UniaxialCrystal(No, Ne, new Vector3D(-0.7, 0.1, 0.2));
        var cases = new (OpticalSide Before, OpticalSide After, double Sin)[]
        {
            (OpticalSide.Isotropic(1.0), OpticalSide.Of(calcite), Math.Sin(35 * Math.PI / 180)),
            (OpticalSide.Of(calcite), OpticalSide.Isotropic(1.0), 0.4),
            (OpticalSide.Of(calcite), OpticalSide.Of(turned), 0.9),
            (OpticalSide.Of(calcite), OpticalSide.Isotropic(1.0), 1.2),   // both transmitted waves evanescent: total internal reflection
        };
        foreach (var (before, after, sin) in cases)
        {
            var kappa = new Vector3D(sin * 0.8, sin * 0.6, 0);
            var solved = AnisotropicInterface.Solve(before, after, kappa, Vector3D.UnitZ, Vector3D.UnitX);
            foreach (var b in new[] { CrystalMode.Ordinary, CrystalMode.Extraordinary })
            {
                if (!solved.Incident[(int)b].Propagating) continue;
                double total = 0;
                foreach (var m in new[] { CrystalMode.Ordinary, CrystalMode.Extraordinary })
                    total += Math.Pow(solved.Reflection(m, b).Magnitude, 2) + Math.Pow(solved.Transmission(m, b).Magnitude, 2);
                Close(1, total, 1e-13, $"power of incident {b} ({before.IsCrystal}→{after.IsCrystal}, κ = {sin})");
            }
        }
    }

    [Fact]
    public void TheExtraordinaryWave_LiesOnTheNormalSurface_AndTravelsForward()
    {
        var c = new UniaxialCrystal(No, Ne, new Vector3D(0.2, 0.9, -0.4));
        var kappa = new Vector3D(0.3, -0.2, 0);
        var w = UniaxialOptics.Wave(OpticalSide.Of(c), CrystalMode.Extraordinary, kappa, Vector3D.UnitZ, +1);
        Assert.True(w.Propagating);
        var n = w.RealWaveVector;
        double na = Vector3D.Dot(n, c.Axis);
        Close(1, na * na / (No * No) + (n.LengthSquared - na * na) / (Ne * Ne), 1e-15, "normal surface");
        Close(kappa.X, n.X, 1e-16);
        Close(kappa.Y, n.Y, 1e-16);
        Assert.True(w.Ray.Z > 0);
        Close(c.EffectiveIndex(n), n.Length, 1e-14, "|N| = n(θ_w)");
    }

    // ---------------------------------------------------------------- refusals and the lens rules

    [Fact]
    public void BrokenSequences_AreRefusedByName()
    {
        var noOut = Lens(In(0, Vector3D.UnitZ, 0.001, stop: true));
        Assert.Contains("reaches the image", Assert.Throws<ArgumentException>(() => SequentialTrace.Create(noOut, Sys)).Message);
        var inside = Lens(In(0, Vector3D.UnitZ, 0.001, stop: true), new SequentialSurface { Curvature = 10, Thickness = 0.001 }, Out(0.001));
        Assert.Contains("Surface 2 lies inside the crystal that begins at surface 1", Assert.Throws<ArgumentException>(() => SequentialTrace.Create(inside, Sys)).Message);
        var lonely = Lens(new SequentialSurface { Thickness = 0.001, IsStop = true }, Out(0.001));
        Assert.Contains("needs a crystal before it", Assert.Throws<ArgumentException>(() => SequentialTrace.Create(lonely, Sys)).Message);
        var zeroAxis = Lens(In(0, Vector3D.Zero, 0.001, stop: true), Out(0.001));
        Assert.Contains("crystal axis (parameters 2–4)", Assert.Throws<ArgumentException>(() => SequentialTrace.Create(zeroAxis, Sys)).Message);
        var noE = Lens(new SequentialSurface { Shape = new BirefringentInShape(0, Vector3D.UnitZ), Medium = ParaxialMedium.Constant(1.6), Thickness = 0.001, IsStop = true }, Out(0.001));
        Assert.Contains("no extraordinary index", Assert.Throws<ArgumentException>(() => SequentialTrace.Create(noE, Sys)).Message);
        var coated = Lens(In(0, Vector3D.UnitZ, 0.001, stop: true) with { Coating = "AR" }, Out(0.001));
        Assert.Contains("coatings on a birefringent interface are not built",
            Assert.Throws<InvalidOperationException>(() => new PolarizationTracer(SequentialTrace.Create(coated, Sys), CoatingCatalog.Empty)).Message);

        var layout = SurfaceTypes.Find(BirefringentInShape.Name)!;
        Assert.Contains("gradient mode 1 (parameter 13)", Assert.Throws<ArgumentException>(() => layout.Build(n => n == 13 ? 1 : n == 4 ? 1 : 0)).Message);
        Assert.Contains("shape 1 (parameter 6)", Assert.Throws<ArgumentException>(() => layout.Build(n => n == 6 ? 1 : 0)).Message);
        Assert.Contains("must be from 0 to 3", Assert.Throws<ArgumentException>(() => layout.Build(n => n == 1 ? 4 : 0)).Message);

        // Paraxial rays through an extraordinary mode need Parax Ignore 1.
        var e = Lens(new SequentialSurface
        {
            Shape = new BirefringentInShape(1, Vector3D.UnitX, extraordinary: ParaxialMedium.Constant(Ne)),
            Medium = ParaxialMedium.Constant(No), Thickness = 0.001, IsStop = true,
        }, Out(0.001));
        Assert.Empty(e.Validate());
        Assert.Contains("Parax Ignore (parameter 5)", Assert.Throws<ArgumentException>(() => e.ToParaxialLens()).Message);
        Lens(In(1, Vector3D.UnitX, 0.001, stop: true), Out(0.001)).ToParaxialLens();   // Parax Ignore 1: the ordinary index
    }

    [Fact]
    public void ALensWithoutBirefringentSurfaces_HasNoCrystals_AndRecordsNoWaveVectors()
    {
        var lens = Lens(
            new SequentialSurface { Curvature = 20, Thickness = 0.004, Medium = ParaxialMedium.Constant(1.5), IsStop = true },
            new SequentialSurface { Curvature = -20, Thickness = 0.05 });
        var trace = SequentialTrace.Create(lens, Sys);
        Assert.Null(trace.Birefringence);
        var path = trace.TracePath(trace.NewRay(new Vector3D(0, 0.0005, 0), Vector3D.UnitZ));
        Assert.All(path.Hits, h => Assert.Null(h.WaveNormal));
        // Each segment is still n·length, bit for bit.
        Assert.All(path.Hits.Skip(1), h => Assert.Equal(h.IndexBefore * h.SegmentLength, h.SegmentOpticalPath));
    }

    [Fact]
    public void ACrystalWithEqualIndices_TracesAsTheSameGlass()
    {
        // n_e = n_o: every mode is the isotropic glass; the trace through In/Out matches the
        // Standard surfaces to rounding (the refraction formula differs, so not to the bit).
        const double n = 1.62;
        var standard = Lens(
            new SequentialSurface { Curvature = 25, Thickness = 0.004, Medium = ParaxialMedium.Constant(n), IsStop = true },
            new SequentialSurface { Curvature = -15, Thickness = 0.03 });
        var crystal = Lens(
            new SequentialSurface
            {
                Shape = new BirefringentInShape(1, new Vector3D(0.3, 0.4, 0.5), paraxIgnore: 1, extraordinary: ParaxialMedium.Constant(n)),
                Curvature = 25, Thickness = 0.004, Medium = ParaxialMedium.Constant(n), IsStop = true,
            },
            new SequentialSurface { Shape = new BirefringentOutShape(), Curvature = -15, Thickness = 0.03 });
        var a = SequentialTrace.Create(standard, Sys);
        var b = SequentialTrace.Create(crystal, Sys);
        var launch = a.NewRay(new Vector3D(0.0003, 0.0007, 0), Along(3));
        var pa = a.TracePath(launch);
        var pb = b.TracePath(launch);
        for (int h = 1; h < pa.Hits.Count; h++)
        {
            Close(pa.Hits[h].Position.X, pb.Hits[h].Position.X, 1e-17);
            Close(pa.Hits[h].Position.Y, pb.Hits[h].Position.Y, 1e-17);
            Close(pa.Hits[h].Direction.Y, pb.Hits[h].Direction.Y, 1e-15);
            Close(pa.Hits[h].OpticalPath, pb.Hits[h].OpticalPath, 1e-16);
        }
    }

    // ---------------------------------------------------------------- saved lenses

    [Fact]
    public void ASavedBirefringentIn_FindsItsExtraordinaryGlass_ByTheMinusESuffix_AndRoundTrips()
    {
        var all = RefractiveIndexInfoSeed.Catalogs.SelectMany(c => c.Materials).ToList();
        var calcite = all.Single(m => m.Name == "CaCO3-Ghosh-o") with { Name = "CALCITE", Catalog = "TEST" };
        var calciteE = all.Single(m => m.Name == "CaCO3-Ghosh-e") with { Name = "CALCITE-E", Catalog = "TEST" };
        OpticalMaterial? Find(string name)
        {
            string bare = name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : name;
            return bare.Equals("CALCITE", StringComparison.OrdinalIgnoreCase) ? calcite
                : bare.Equals("CALCITE-E", StringComparison.OrdinalIgnoreCase) ? calciteE : null;
        }
        var lens = Lens(
            new SequentialSurface
            {
                Shape = new BirefringentInShape(3, new Vector3D(0, 1, 1), drawLength: 0.002, paraxIgnore: 1, extraordinary: ParaxialMedium.Of(calciteE)),
                Medium = ParaxialMedium.Of(calcite), Thickness = 0.002, IsStop = true,
            },
            new SequentialSurface { Shape = new BirefringentOutShape(), Thickness = 0.01 });
        var saved = SavedSurfaces.FromLens(lens, LensUnit.Millimeter);
        Assert.Equal("Birefringent In", saved[1].Type);
        Assert.Equal("TEST:CALCITE", saved[1].Material);
        Assert.Equal(3, SavedSurfaces.Value(saved[1], 1));
        Close(2, SavedSurfaces.Value(saved[1], 0), 1e-15, "the draw length in millimeters");
        var back = SavedSurfaces.ToLens(saved, LensUnit.Millimeter, Find);
        var shape = Assert.IsType<BirefringentInShape>(back.Surfaces[1].Shape);
        Assert.Same(calciteE, shape.Extraordinary!.Material);
        Assert.Equal(3, shape.Mode);

        var a = SequentialTrace.Create(lens, Sys);
        var b = SequentialTrace.Create(back, Sys);
        var launch = a.NewRay(Vector3D.Zero, Along(5));
        var ra = a.Trace(launch);
        var rb = b.Trace(launch);
        Assert.True(ra.IsOk && rb.IsOk);
        Close(ra.Position.Y, rb.Position.Y, 1e-17, "image height after the millimeter round trip");
        Close(ra.OpticalPath, rb.OpticalPath, 1e-16, "optical path after the millimeter round trip");

        // A solve on a parameter keeps the extraordinary glass.
        var changed = SurfaceTypes.WithParameter(back.Surfaces[1], 1, 1);
        Assert.Same(calciteE, ((BirefringentInShape)changed.Shape!).Extraordinary!.Material);

        // Without the "-E" glass the lens is refused by name.
        var ex = Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(saved, LensUnit.Millimeter, n => n.EndsWith("-E", StringComparison.Ordinal) ? null : Find(n)));
        Assert.Contains("Surface 1: the extraordinary glass 'TEST:CALCITE-E' is not in the active catalogs", ex.Message);

        // A crystal built with a constant extraordinary index has no saved form.
        Assert.Contains("is saved by its glass name only",
            Assert.Throws<ArgumentException>(() => SavedSurfaces.FromLens(Lens(In(0, Vector3D.UnitZ, 0.001, stop: true), Out(0.001)), LensUnit.Millimeter)).Message);
    }

    [Fact]
    public void TheTwoTypes_AreRegistered_WithTheHelpsColumns()
    {
        var bin = SurfaceTypes.Find("Birefringent In")!;
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }, bin.Parameters.Select(p => p.Number));
        Assert.Equal("Mode", bin.Parameter(1)!.Name);
        Assert.Equal((0, 3), bin.Parameter(1)!.Range!.Value);
        Assert.Equal(1, bin.Parameter(0)!.LengthPower);
        Assert.True(bin.Parameter(13)!.IsExtraData);
        var bout = SurfaceTypes.Find("Birefringent Out")!;
        Assert.Equal(new[] { 6, 7, 8, 9, 10, 11, 12 }, bout.Parameters.Select(p => p.Number));
        Assert.Equal(new[] { -1, -3, -5, -7, -9 }, bout.Parameters.Where(p => p.Number >= 8).Select(p => p.LengthPower));
    }
}
