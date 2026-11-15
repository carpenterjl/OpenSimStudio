using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Diffractive;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-26: diffractive, grating and holographic surfaces (docs/Optics-Conventions.md §57) against
/// closed forms derived in each test's comments. The rule under test is
/// <see cref="DiffractiveShape"/>'s; each type's definition is its OpticStudio 2025 R2 help page,
/// cited on the class under test. Wavelengths, frequencies, coefficients, distances and indices
/// are arbitrary test inputs, not reference data. The phase acts with the wavelength in the
/// system's air (the table wavelength), so the closed forms use
/// OpticsConventions.MicrometersToMeters of the table value, as the trace does.
/// </summary>
public sealed class DiffractiveSurfaceTests
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;   // 2^-52
    private const double Micrometers = 0.6328;

    private static SequentialSurface Surface(double thickness, SurfaceShape? shape = null, double n = 1, bool stop = false,
        bool mirror = false) => new()
    {
        Thickness = thickness, Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, Shape = shape,
    };

    private static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd, params double[] micrometers) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Wavelengths = new WavelengthSet
        {
            Wavelengths = (micrometers.Length == 0 ? new[] { Micrometers } : micrometers).Select(w => new Wavelength { Micrometers = w }).ToArray(),
        },
    };

    private static void Close(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    private static Vector3D Unit(double x, double y, double z) => new Vector3D(x, y, z) / Math.Sqrt(x * x + y * y + z * z);

    /// <summary>The direction after surface 1 of a ray launched at <paramref name="start"/> along <paramref name="d"/>.</summary>
    private static Vector3D DirectionAfter(SequentialLens lens, Vector3D start, Vector3D d, OpticalSystemData? system = null)
    {
        var trace = SequentialTrace.Create(lens, system ?? Sys(0.01));
        var path = trace.TracePath(trace.NewRay(start, d));
        Assert.True(path.Final.IsOk, $"ray failed: {path.Final.Status} at surface {path.Final.FailedAtSurface}");
        return path.Hits[1].Direction;
    }

    // ---- Gate: the grating equation in plane and conical mounting at every order ----

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PlaneGrating_InPlaneMounting_ObeysTheGratingEquation(int order)
    {
        // Derivation. The help's n₂·sin θ₂ − n₁·sin θ₁ = M·λ·T with the lines along x: the y
        // direction cosine m obeys n₂·m′ = n₁·m + M·λ·T, the x cosine stays 0, and the ray goes on
        // forward with N′ = √(1 − m′²). Air into n₂ = 1.5, θ₁ = 10°, T = 0.6 lines/µm.
        double t = 0.6, n2 = 1.5, s1 = Math.Sin(10 * Math.PI / 180);
        double lambda = OpticsConventions.MicrometersToMeters(Micrometers);
        var lens = Lens(double.PositiveInfinity, Surface(0.05, new DiffractionGrating(t, order), n: n2, stop: true));
        var d = new Vector3D(0, s1, Math.Sqrt(1 - s1 * s1));
        var after = DirectionAfter(lens, new Vector3D(0, 0.001, -0.01), d);
        double m2 = (s1 + order * lambda * (t * 1e6)) / n2;
        Close(0, after.X, 0, "L′");
        Close(m2, after.Y, 16 * Eps, "M′");
        Close(Math.Sqrt(1 - m2 * m2), after.Z, 16 * Eps, "N′");
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReflectionGrating_InConicalMounting_ObeysTheVectorGratingEquation(int order)
    {
        // Derivation. In conical mounting the incident ray has a component along the lines (x):
        // that component is unchanged (the phase does not vary along x), the y cosine gains
        // M·λ·T, and in reflection the normal component changes sign: L′ = L,
        // M′ = M + M·λ·T, N′ = −√(1 − L′² − M′²). T = 0.3 lines/µm in air.
        double t = 0.3;
        double lambda = OpticsConventions.MicrometersToMeters(Micrometers);
        var lens = Lens(double.PositiveInfinity, Surface(-0.05, new DiffractionGrating(t, order), stop: true, mirror: true));
        var d = Unit(0.35, -0.2, 0.9);
        var after = DirectionAfter(lens, new Vector3D(0.001, 0.002, -0.01) - 0.01 * d, d);
        double l2 = d.X, m2 = d.Y + order * lambda * (t * 1e6);
        Close(l2, after.X, 16 * Eps, "L′");
        Close(m2, after.Y, 16 * Eps, "M′");
        Close(-Math.Sqrt(1 - l2 * l2 - m2 * m2), after.Z, 16 * Eps, "N′");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void ReflectionGrating_AtLittrow_ReturnsTheRayOnItself(int order)
    {
        // Derivation. Littrow: M′ = −M with M′ = M + M·λ·T, so M = −M·λ·T/2; the x cosine is 0
        // and reflection reverses the normal component, so d′ = −d.
        double t = 0.6;
        double lambda = OpticsConventions.MicrometersToMeters(Micrometers);
        double s = order * lambda * (t * 1e6) / 2;
        var lens = Lens(double.PositiveInfinity, Surface(-0.05, new DiffractionGrating(t, order), stop: true, mirror: true));
        var d = new Vector3D(0, -s, Math.Sqrt(1 - s * s));
        var after = DirectionAfter(lens, new Vector3D(0, 0.003, -0.01), d);
        Close(-d.X, after.X, 16 * Eps, "L′");
        Close(-d.Y, after.Y, 16 * Eps, "M′");
        Close(-d.Z, after.Z, 16 * Eps, "N′");
    }

    [Fact]
    public void Grating_EvanescentOrder_FailsWithItsCode()
    {
        // λ·T = 0.6328·2 > 1 at normal incidence in air: no propagating first order.
        var lens = Lens(double.PositiveInfinity, Surface(0.05, new DiffractionGrating(2, 1), stop: true));
        var trace = SequentialTrace.Create(lens, Sys(0.01));
        var ray = trace.Trace(trace.NewRay(new Vector3D(0, 0.001, -0.01), Vector3D.UnitZ));
        Assert.Equal(RayStatus.EvanescentOrder, ray.Status);
        Assert.Equal(1, ray.FailedAtSurface);
    }

    [Fact]
    public void Grating_HasNoParaxialModel_AndSaysWhy()
    {
        var lens = Lens(double.PositiveInfinity, Surface(0.05, new DiffractionGrating(0.6, 1), stop: true));
        var ex = Assert.Throws<ArgumentException>(() => lens.ToParaxialLens());
        Assert.Contains("deviates the axis ray", ex.Message);
        Assert.Contains("Surface 1", ex.Message);
    }

    // ---- Gate: Binary 2 with only the quadratic term ----

    [Fact]
    public void BinaryOptic2_QuadraticTermOnly_FocusesAtTheDiffractiveFocalLength_ScalingAsOneOverLambda()
    {
        // Derivation. "Binary 2": Φ = M·Σ Aᵢ·ρ²ⁱ, ρ = r/R. With A₁ alone, ∂Φ/∂y = 2·M·A₁·y/R²,
        // and the rule m′ = m + (λ/2π)·∂Φ/∂y gives m′ = −y/f for a collimated axial beam in air,
        // f = −R²/(2·A₁·M·λ/2π) (the plan's f = −1/(2·A₁·m·λ/2π) with R = 1). Exactly, not only
        // paraxially: the cosine is linear in y, so a ray at height h crosses the axis at
        // z = h·N′/(h/f) = f·√(1 − h²/f²). The paraxial engine's EFL is f at each wavelength, and
        // f(λ₂)/f(λ₁) = λ₁/λ₂. "a positive power binary optic has a negative quadratic phase
        // coefficient": A₁ < 0 gives f > 0, and the extra path (λ/2π)·Φ is negative.
        double r = 0.01, a1 = -1000, order = 1;
        double[] waves = { 0.55, 1.1 };
        var shape = new BinaryOptic2(order, Array.Empty<double>(), r, new[] { a1 });
        var lens = Lens(double.PositiveInfinity, Surface(0.5, shape, stop: true));
        var system = Sys(0.02, waves);
        for (int w = 0; w < waves.Length; w++)
        {
            double lambda = OpticsConventions.MicrometersToMeters(waves[w]);
            double f = -r * r / (2 * a1 * order * lambda / (2 * Math.PI));
            Assert.True(f > 0);
            var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), system, w);
            Close(f, paraxial.EffectiveFocalLength!.Value, 16 * Eps * f, $"EFL at {waves[w]} µm");

            var trace = SequentialTrace.Create(lens, system, w);
            foreach (double h in new[] { 0.001, 0.004, 0.009 })
            {
                var path = trace.TracePath(trace.NewRay(new Vector3D(0, h, -0.01), Vector3D.UnitZ));
                Assert.True(path.Final.IsOk);
                var hit = path.Hits[1];
                Close(-h / f, hit.Direction.Y, 16 * Eps, "M′ = −h/f");
                double crossing = -hit.Position.Y * hit.Direction.Z / hit.Direction.Y;
                Close(f * Math.Sqrt(1 - h * h / (f * f)), crossing, 64 * Eps * f, $"axis crossing at h = {h}");
                // Segment: 0.01 m of air to the plane, plus (λ/2π)·M·A₁·(h/R)².
                double extra = lambda / (2 * Math.PI) * order * a1 * (h / r) * (h / r);
                Assert.True(extra < 0);
                Close(0.01 + extra, hit.SegmentOpticalPath, 16 * Eps * 0.01, "segment optical path");
            }
        }
        double f1 = ParaxialSystem.Create(lens.ToParaxialLens(), system, 0).EffectiveFocalLength!.Value;
        double f2 = ParaxialSystem.Create(lens.ToParaxialLens(), system, 1).EffectiveFocalLength!.Value;
        Close(waves[1] / waves[0], f1 / f2, 16 * Eps, "f(λ₁)/f(λ₂) = λ₂/λ₁");
    }

    // ---- Gate: a two-point hologram images its construction point perfectly ----

    [Fact]
    public void Hologram2_ImagesItsFirstConstructionPointOnItsSecond_WithEqualPaths()
    {
        // Derivation. Hologram 2: Φ = m·(2π/λc)·(D₁ + D₂), ∇Φ = m·(2π/λc)·(rₒ − rᵣ) with
        // rₒ = (P − Q₁)/|P − Q₁| diverging from Q₁ and rᵣ = (Q₂ − P)/|Q₂ − P| converging to Q₂.
        // A ray from Q₁ arrives along rₒ; with m = −1 and λp = λc its tangential part becomes
        // rₒ − (rₒ − rᵣ) = rᵣ, and since Q₂ lies beyond the plane the normal part keeps its sign:
        // the ray leaves along rᵣ, through Q₂. Its path is |P − Q₁| + |Q₂ − P| + (λ/2π)·Φ =
        // |Q₁| + |Q₂| for every P (D = |P − Q| − |Q|), so all paths are equal (Fermat).
        var q1 = new Vector3D(0, 0.002, -0.1);
        var q2 = new Vector3D(0.001, -0.003, 0.15);
        var shape = new HologramShape(2, q1, q2, Micrometers, -1);
        var lens = Lens(0.1, Surface(0.15, shape, stop: true));
        var trace = SequentialTrace.Create(lens, Sys(0.02));
        var object_ = new Vector3D(0, 0.002, -0.1);
        double sumSq = 0, pathMin = double.MaxValue, pathMax = double.MinValue;
        int n = 0;
        for (int i = -4; i <= 4; i++)
            for (int j = -4; j <= 4; j++)
            {
                var target = new Vector3D(0.002 * i, 0.002 * j, 0);
                var ray = trace.Trace(trace.NewRay(object_, (target - object_).Normalized()));
                Assert.True(ray.IsOk, $"ray failed: {ray.Status}");
                double dx = ray.Position.X - q2.X, dy = ray.Position.Y - q2.Y;
                sumSq += dx * dx + dy * dy;
                pathMin = Math.Min(pathMin, ray.OpticalPath);
                pathMax = Math.Max(pathMax, ray.OpticalPath);
                n++;
            }
        double rms = Math.Sqrt(sumSq / n);
        Assert.True(rms <= 64 * Eps * 0.15, $"RMS spot {rms:R} m");
        Assert.True(pathMax - pathMin <= 64 * Eps * 0.25, $"path spread {pathMax - pathMin:R} m");
        Close(q1.Length + q2.Length, pathMax, 64 * Eps * 0.25, "path = |Q₁| + |Q₂|");
    }

    [Fact]
    public void Hologram1_SendsTheRayAwayFromItsSecondPoint()
    {
        // Derivation. Hologram 1: both beams diverge, rᵣ = (P − Q₂)/|P − Q₂|. A ray from Q₁ with
        // m = −1, λp = λc leaves along rᵣ: a virtual image at Q₂ (both points before the plane).
        var q1 = new Vector3D(0.001, 0, -0.2);
        var q2 = new Vector3D(-0.002, 0.001, -0.05);
        var lens = Lens(0.2, Surface(0.1, new HologramShape(1, q1, q2, Micrometers, -1), stop: true));
        var trace = SequentialTrace.Create(lens, Sys(0.02));
        var p = new Vector3D(0.004, -0.003, 0);
        var path = trace.TracePath(trace.NewRay(q1, (p - q1).Normalized()));
        Assert.True(path.Final.IsOk);
        var expected = (path.Hits[1].Position - q2).Normalized();
        var after = path.Hits[1].Direction;
        Close(expected.X, after.X, 16 * Eps, "L′");
        Close(expected.Y, after.Y, 16 * Eps, "M′");
        Close(expected.Z, after.Z, 16 * Eps, "N′");
    }

    // ---- the other types ----

    [Fact]
    public void ZernikeFringePhase_DefocusTerm_HasThePowerOfItsQuadratic()
    {
        // Derivation. Fringe term 4 is the n = 2, m = 0 term (ZernikeIndex, after Wyant and
        // Creath's Table III, asserted below); Fringe terms are "normalized to have unity magnitude
        // at the edge of the pupil" (help, "Zernike Fringe Coefficients"), so it is the radial
        // polynomial R₂⁰ = Σₖ (−1)ᵏ(2 − k)!/(k!·((1 − k)!)²)·ρ^(2 − 2k) = 2ρ² − 1. Then
        // Φ = 2π·M·A₄·(2ρ² − 1) has ∂²Φ/∂y² = 2π·M·A₄·4/R² at the vertex and the paraxial power
        // is φ = −λ·Φ″/(2π) = −4·M·A₄·λ/R². A₄ = −2 waves, R = 5 mm, M = 1.
        Assert.Equal(new ZernikeTerm(2, 0), ZernikeIndex.FromFringe(4));
        double r = 0.005, a4 = -2;
        var shape = new ZernikePhaseShape(ZernikeOrdering.Fringe, 1, true, r, new[] { 0, 0, 0, a4 });
        var lens = Lens(double.PositiveInfinity, Surface(0.5, shape, stop: true));
        var system = Sys(0.01);
        double lambda = OpticsConventions.MicrometersToMeters(Micrometers);
        double phi = -4 * a4 * lambda / (r * r);
        Close(1 / phi, ParaxialSystem.Create(lens.ToParaxialLens(), system).EffectiveFocalLength!.Value, 64 * Eps / phi, "EFL");

        // The real ray: m′ = (λ/2π)·∂Φ/∂y = λ·M·A₄·4y/R² = −y·φ exactly.
        var trace = SequentialTrace.Create(lens, system);
        var path = trace.TracePath(trace.NewRay(new Vector3D(0, 0.003, -0.01), Vector3D.UnitZ));
        Close(-0.003 * phi, path.Hits[1].Direction.Y, 64 * Eps, "M′");
    }

    [Fact]
    public void VariableLineSpaceGrating_LocalFrequencyIsTheHelpPagesFormula()
    {
        // Derivation. The rule gives M′ = M + M·λ·T(y) on a plane in air, T in lines per meter;
        // T(y) is the page's formula with y, L and λ₀ in meters. Normal incidence at y = 2 mm.
        double order = 1, l = 0.5, ca = 0.2, cb = 0.4, lambda0 = 0.5;
        var lens = Lens(double.PositiveInfinity, Surface(-0.05, new VariableLineSpaceGrating(order, l, ca, cb, lambda0), stop: true, mirror: true));
        double y = 0.002;
        double t = 1 / OpticsConventions.MicrometersToMeters(lambda0)
            * ((l * cb - y) / Math.Sqrt(y * y + l * l - 2 * l * y * cb) - (l * ca - y) / Math.Sqrt(y * y + l * l - 2 * l * y * ca));
        double lambda = OpticsConventions.MicrometersToMeters(Micrometers);
        var after = DirectionAfter(lens, new Vector3D(0, y, -0.01), Vector3D.UnitZ);
        double m2 = order * lambda * t;
        Close(m2, after.Y, 64 * Eps, "M′");
        Close(-Math.Sqrt(1 - m2 * m2), after.Z, 64 * Eps, "N′");
    }

    [Fact]
    public void PlainRefraction_IsRecovered_WhenThePhaseIsZero()
    {
        // Order 0: the tangential part of n·d is conserved, Snell's law (vector form).
        var d = Unit(0.1, 0.3, 1);
        var grating = Lens(double.PositiveInfinity, Surface(0.05, new DiffractionGrating(0.6, 0), n: 1.5, stop: true));
        var plain = Lens(double.PositiveInfinity, Surface(0.05, null, n: 1.5, stop: true));
        var a = DirectionAfter(grating, new Vector3D(0, 0, -0.01), d);
        var b = DirectionAfter(plain, new Vector3D(0, 0, -0.01), d);
        Close(b.X, a.X, 16 * Eps, "L′");
        Close(b.Y, a.Y, 16 * Eps, "M′");
        Close(b.Z, a.Z, 16 * Eps, "N′");
    }

    // ---- layouts ----

    public static TheoryData<string, Dictionary<int, double>> Layouts() => new()
    {
        { "Diffraction Grating", new() { [1] = 0.6, [2] = -1 } },
        { "Toroidal Grating", new() { [1] = 0.2, [2] = 1.2, [3] = 2 } },
        { "Variable Line Space Grating", new() { [1] = 1, [2] = 0.5, [3] = 0.2, [4] = 0.4, [5] = 0.5 } },
        { "Binary Optic 1", new() { [0] = 1, [1] = 1e-3, [9] = 1, [13] = 5, [14] = 0.01, [15] = 0, [16] = 0, [17] = -10, [18] = 2, [19] = -10 } },
        { "Binary Optic 2", new() { [0] = 2, [1] = 0.5, [13] = 2, [14] = 0.01, [15] = -100, [16] = 3 } },
        { "Zernike Fringe Phase", new() { [0] = 1, [1] = 1, [13] = 4, [14] = 0.005, [15] = 0, [16] = 0.1, [17] = 0, [18] = -2 } },
        { "Zernike Standard Phase", new() { [0] = -1, [1] = 0, [13] = 2, [14] = 0.005, [15] = 0.3, [16] = 0.1 } },
        { "Zernike Annular Phase", new() { [0] = 1, [1] = 1, [3] = 0.3, [13] = 1, [14] = 0.005, [15] = 0.2 } },
        { "Hologram 1", new() { [1] = 0, [2] = 0.01, [3] = -0.1, [4] = 0, [5] = 0, [6] = -0.2, [7] = 0.6328, [8] = 1, [13] = 0 } },
        { "Hologram 2", new() { [1] = 0, [2] = 0.01, [3] = -0.1, [4] = 0, [5] = 0, [6] = 0.2, [7] = 0.6328, [8] = -1, [13] = 0 } },
        { "Toroidal Hologram", new() { [1] = 0.3, [2] = 1e-3, [13] = 0, [14] = 0.01, [15] = -0.1, [16] = 0, [17] = 0, [18] = 0.2, [19] = 0.6328, [20] = -1, [21] = -1, [22] = 0 } },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void EveryLayout_ReadsBackWhatItBuilds(string type, Dictionary<int, double> values)
    {
        var layout = SurfaceTypes.Find(type);
        Assert.NotNull(layout);
        var shape = layout!.Build(n => values.TryGetValue(n, out double v) ? v : 0)!;
        Assert.Equal(type, shape.TypeName);
        var read = layout.Read(shape);
        foreach (var (number, value) in values)
            Assert.Equal(value, read.TryGetValue(number, out double r) ? r : 0);
        foreach (var (number, value) in read)
            Assert.Equal(values.TryGetValue(number, out double v) ? v : 0, value);
    }

    [Fact]
    public void Holograms_RefuseWhatIsNotBuilt_ByName()
    {
        var h1 = SurfaceTypes.Find("Hologram 1")!;
        var values = new Dictionary<int, double> { [3] = -0.1, [6] = -0.2, [7] = 0.6328, [8] = 1, [13] = 1 };
        Assert.Contains("volume hologram", Assert.Throws<ArgumentException>(() => h1.Build(n => values.TryGetValue(n, out double v) ? v : 0)).Message);
        values[13] = 0;
        values[7] = -0.6328;
        Assert.Contains("negative construction wavelength", Assert.Throws<ArgumentException>(() => h1.Build(n => values.TryGetValue(n, out double v) ? v : 0)).Message);
        var torus = SurfaceTypes.Find("Toroidal Hologram")!;
        var t = new Dictionary<int, double> { [15] = -0.1, [18] = -0.2, [19] = 0.6328, [20] = 1, [21] = 0 };
        Assert.Contains("converge/diverge flag", Assert.Throws<ArgumentException>(() => torus.Build(n => t.TryGetValue(n, out double v) ? v : 0)).Message);
    }
}
