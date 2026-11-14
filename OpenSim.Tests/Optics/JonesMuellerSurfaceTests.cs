using System.Globalization;
using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Polarization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-52: the Jones Matrix and Mueller Matrix surfaces in the polarization ray trace
/// (docs/Optics-Conventions.md §30, docs/Optics-Gates.md "Jones and Mueller matrix surfaces").
/// <para>
/// Sources: OpticStudio 2025 R2 help, "Jones Matrix (sequential surfaces, lens data editor)" and
/// "Defining Polarizing Components" (its table "Sample Jones Matrices"); Wikipedia, "Mueller
/// calculus" (the matrix A and the table of Mueller matrices) and "Stokes parameters"; all saved
/// under plans/sources/optics/op-52-help/. Expected values not copied from those are derived in
/// the comments here. Radii, thicknesses, angles and factors are arbitrary test inputs.
/// </para>
/// <para>
/// <b>Setting.</b> Every matrix surface sits in air on the axis of a collimated beam, so the
/// interface it also is (index 1 to index 1, uncoated) transmits s and p with amplitude 1
/// (t = 2n1c1/(n1c1 + n2c2) = 1 with the power factor √(n2c2/n1c1) = 1), and only the matrix
/// acts. The launch along +z with the X-axis rule has S = x̂, P = ŷ (P = K × X̂ = ŷ,
/// S = P × K = x̂), so a Jones vector (Jx, Jy) is the field (Jx, Jy, 0), and the local axes of
/// every surface are the global ones.
/// </para>
/// </summary>
public sealed class JonesMuellerSurfaceTests
{
    // ---- Builders ----

    private static SequentialSurface Plane(double thickness, SurfaceShape? shape = null, bool stop = false) => new()
    {
        Thickness = thickness, Medium = ParaxialMedium.Constant(1), IsStop = stop, Shape = shape,
    };

    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness, Medium = ParaxialMedium.Constant(n), IsStop = stop,
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

    private static PolarizationTracer Tracer(SequentialLens lens) => new(SequentialTrace.Create(lens, Sys), CoatingCatalog.Empty);

    private static PolarizedPath Axial(PolarizationTracer tracer, JonesVector jones) =>
        tracer.TraceRay(tracer.Trace.NewRay(Vector3D.Zero, Vector3D.UnitZ), jones);

    /// <summary>A Jones Matrix surface from a matrix in OpticStudio's convention (Ar + i·Ai …).</summary>
    private static JonesMatrixShape Jones(Complex a, Complex b, Complex c, Complex d) =>
        new(a.Real, a.Imaginary, b.Real, b.Imaginary, c.Real, c.Imaginary, d.Real, d.Imaginary);

    /// <summary>
    /// The ideal linear polarizer transmitting along (cos θ, sin θ): the projector
    /// [[c², cs], [cs, s²]] (derived: it keeps the component along (c, s) and removes the one
    /// at right angles). At θ = 0 it is the help's "X analyzer" [[(1,0), (0,0)], [(0,0), (0,0)]].
    /// </summary>
    private static JonesMatrixShape Polarizer(double thetaDegrees)
    {
        double t = thetaDegrees * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        return Jones(c * c, c * s, c * s, s * s);
    }

    /// <summary>
    /// A quarter-wave plate with its axes at 45°: the help's "Quarter-Wave plate in X direction"
    /// diag((0, 1), (1, 0)) = diag(i, 1) (OpticStudio's convention) rotated by 45°,
    /// R·diag(a, b)·Rᵀ = ½[[a + b, a − b], [a − b, a + b]] for R the 45° rotation (derived).
    /// </summary>
    private static JonesMatrixShape QuarterWaveAt45()
    {
        Complex a = Complex.ImaginaryOne, b = Complex.One;
        return Jones((a + b) / 2, (a - b) / 2, (a - b) / 2, (a + b) / 2);
    }

    private static void Close(double expected, double actual, double tolerance, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    private static string Messages(Exception e)
    {
        var parts = new List<string>();
        for (var x = e; x is not null; x = x.InnerException) parts.Add(x.Message);
        return string.Join(" | ", parts);
    }

    // ---- Gate: Malus's law through two polarizers ----

    /// <summary>
    /// Malus's law, derived: light polarized along x (after the X analyzer) has the field
    /// E (1, 0); the second polarizer gives (c², cs)·E_x, of power c⁴ + c²s² = c²(c² + s²) =
    /// cos²θ. Unpolarized light loses half at the first polarizer, so ½cos²θ; behind the second
    /// polarizer the light is fully polarized (degree 1) wherever any is left.
    /// </summary>
    [Fact]
    public void MalusLaw_ThroughTwoPolarizers_IsCosSquared()
    {
        foreach (double theta in new[] { 0.0, 15, 30, 45, 60, 75, 89 })
        {
            var tracer = Tracer(Lens(Plane(0.01, Polarizer(0), stop: true), Plane(0.01, Polarizer(theta)), Plane(0.02)));
            double cos2 = Math.Pow(Math.Cos(theta * Math.PI / 180), 2);
            var polarized = Axial(tracer, JonesVector.AlongS);
            Close(cos2, polarized.Transmission, 1e-14, $"θ = {theta}°, x-polarized launch:");
            var path = tracer.Trace.TracePath(tracer.Trace.NewRay(Vector3D.Zero, Vector3D.UnitZ));
            var unpolarized = new SystemPolarization { Unpolarized = true };
            Close(cos2 / 2, tracer.Transmission(path, unpolarized), 1e-14, $"θ = {theta}°, unpolarized launch:");
            var stokes = tracer.FinalStokes(path, unpolarized);
            Close(cos2 / 2, stokes.I, 1e-14, $"θ = {theta}°, Stokes I:");
            Close(1, stokes.DegreeOfPolarization, 1e-12, $"θ = {theta}°, degree of polarization:");
        }
    }

    [Fact]
    public void CrossedPolarizers_TransmitNothing_AndTheDegreeOfPolarizationIsUndefined()
    {
        var tracer = Tracer(Lens(Plane(0.01, Polarizer(0), stop: true), Plane(0.01, Polarizer(90)), Plane(0.02)));
        var path = Axial(tracer, JonesVector.AlongS);
        Close(0, path.Transmission, 1e-28, "crossed polarizers:");
        Assert.True(double.IsNaN(new StokesVector(0, 0, 0, 0).DegreeOfPolarization));
    }

    // ---- Gate: a quarter-wave plate at 45° turns linear into circular ----

    /// <summary>
    /// Derived: x-linear light (1, 0) through ½[[a + b, a − b], [a − b, a + b]] is
    /// ½(a + b, a − b); with a, b of equal modulus and a quarter wave apart the two components
    /// have equal moduli and a phase difference of 90°, so Q = U = 0 and |V| = I: degree of
    /// circular polarization ±1. Unitary, so the power stays 1.
    /// </summary>
    [Fact]
    public void QuarterWavePlateAt45_TurnsLinearIntoCircular()
    {
        var tracer = Tracer(Lens(Plane(0.01, QuarterWaveAt45(), stop: true), Plane(0.02)));
        var path = Axial(tracer, JonesVector.AlongS);
        var s = path.FinalStokes;
        Close(1, s.I, 1e-15, "I:");
        Close(1, Math.Abs(s.DegreeOfCircularPolarization), 1e-15, "|degree of circular polarization|:");
        Close(0, s.DegreeOfLinearPolarization, 1e-15, "degree of linear polarization:");
        Close(1, s.DegreeOfPolarization, 1e-15, "degree of polarization:");
        Assert.Null(path.MuellerSurface);
        // The same light along y comes out with the opposite handedness (derived: ½(a − b, a + b)).
        var other = Axial(tracer, JonesVector.AlongP).FinalStokes;
        Close(-s.V, other.V, 1e-15, "handedness from y-linear light:");
    }

    /// <summary>
    /// A unitary Jones matrix keeps the power at any angle of incidence: in the trace it acts in
    /// an orthonormal transverse basis (û, v̂) and leaves k̂ alone, so P_J is unitary (derived).
    /// The field stays at right angles to the ray.
    /// </summary>
    [Fact]
    public void AUnitaryJonesMatrix_KeepsThePower_AtObliqueIncidence_AndTheFieldTransverse()
    {
        var tracer = Tracer(Lens(Plane(0.01, QuarterWaveAt45(), stop: true), Plane(0.02)));
        foreach (double angle in new[] { 5.0, 20, 40 })
        {
            double t = angle * Math.PI / 180;
            var k = new Vector3D(Math.Sin(t) * 0.6, Math.Sin(t) * 0.8, Math.Cos(t));
            var path = tracer.TraceRay(tracer.Trace.NewRay(Vector3D.Zero, k), JonesVector.FromAmplitudes(1, 0.3, 0, 20));
            var at = path.Surfaces[0];
            Assert.NotNull(at.JonesMatrix);
            Close(1, at.TotalTransmission, 1e-14, $"{angle}°, power after the plate:");
            var kOut = tracer.Trace.GlobalPlacement(1).ApplyDirection(at.Hit.Direction);
            Assert.True(at.FieldOut.Along(kOut).Magnitude < 1e-14, $"{angle}°: field along the ray {at.FieldOut.Along(kOut).Magnitude:R}");
        }
    }

    // ---- Gate: a Mueller depolarizer reduces the degree of polarization by its factor ----

    /// <summary>
    /// Derived: diag(1, d, d, d) keeps I and scales Q, U, V by d, so the degree of polarization
    /// of fully polarized light becomes d. Behind it an X analyzer passes
    /// (I + Q)/2 = (1 + d)/2 of x-linear light (the analyzer's Mueller matrix, ½[[1, 1, 0, 0],
    /// [1, 1, 0, 0], 0, 0], "Mueller calculus", table "Linear polarizer (horizontal
    /// transmission)", applied to (1, d, 0, 0)), fully polarized again.
    /// </summary>
    [Fact]
    public void MuellerDepolarizer_ReducesTheDegreeOfPolarization_ByItsFactor()
    {
        foreach (double d in new[] { 0.0, 0.4, 0.9 })
        {
            var tracer = Tracer(Lens(Plane(0.01, MuellerMatrixShape.Depolarizer(d), stop: true), Plane(0.02)));
            var linear = Axial(tracer, JonesVector.AlongS);
            Close(1, linear.FinalStokes.I, 1e-15, $"d = {d}, I:");
            Close(d, linear.FinalStokes.DegreeOfPolarization, 1e-15, $"d = {d}, linear light:");
            Assert.Equal(1, linear.MuellerSurface);
            Assert.True(double.IsNaN(linear.Final.Field!.Value.X.Real), "no single field after a Mueller surface");
            Close(1, linear.Transmission, 1e-15, $"d = {d}, transmission:");

            var circular = Axial(Tracer(Lens(Plane(0.01, QuarterWaveAt45(), stop: true), Plane(0.01, MuellerMatrixShape.Depolarizer(d)), Plane(0.02))),
                JonesVector.AlongS);
            Close(d, Math.Abs(circular.FinalStokes.DegreeOfCircularPolarization), 1e-15, $"d = {d}, circular light:");

            var analysed = Axial(Tracer(Lens(Plane(0.01, MuellerMatrixShape.Depolarizer(d), stop: true), Plane(0.01, Polarizer(0)), Plane(0.02))),
                JonesVector.AlongS);
            Close((1 + d) / 2, analysed.Transmission, 1e-15, $"d = {d}, analyzer behind the depolarizer:");
            Close(1, analysed.FinalStokes.DegreeOfPolarization, 1e-14, $"d = {d}, polarized again:");
            Assert.NotNull(analysed.Surfaces[1].Coherency);
        }
    }

    [Fact]
    public void AnUnphysicalMuellerMatrix_FailsLoudly_NamingTheSurface()
    {
        var bad = new MuellerMatrixShape(new double[] { 1, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var tracer = Tracer(Lens(Plane(0.01, stop: true), Plane(0.01, bad), Plane(0.02)));
        var e = Assert.Throws<InvalidOperationException>(() => Axial(tracer, JonesVector.AlongS));
        Assert.Contains("surface 2 (Mueller Matrix)", e.Message);
    }

    // ---- The Jones and Mueller calculi agree ----

    /// <summary>
    /// M = A(J ⊗ J*)A⁻¹ ("Mueller calculus") reproduces two entries of that page's table: the
    /// X analyzer gives "Linear polarizer (horizontal transmission)" ½[[1, 1, 0, 0],
    /// [1, 1, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0]], and OpticStudio's "Intensity filter, 25%
    /// transmission" diag((0.5, 0), (0.5, 0)) gives "Attenuating filter (25% transmission)"
    /// ¼·I. And a Mueller surface holding the Mueller matrix of the quarter-wave plate gives the
    /// Stokes vector of the Jones surface.
    /// </summary>
    [Fact]
    public void TheMuellerMatrixOfAJonesMatrix_MatchesTheTable_AndTheTwoSurfacesAgree()
    {
        var analyzer = StokesVector.MuellerFromJones(1, 0, 0, 0);
        double[] horizontal = { 0.5, 0.5, 0, 0, 0.5, 0.5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 16; i++) Close(horizontal[i], analyzer[i], 1e-16, $"analyzer M[{i}]:");
        var filter = StokesVector.MuellerFromJones(0.5, 0, 0, 0.5);
        for (int i = 0; i < 16; i++) Close(i % 5 == 0 ? 0.25 : 0, filter[i], 1e-16, $"filter M[{i}]:");

        var qwp = QuarterWaveAt45();
        var mueller = new MuellerMatrixShape(StokesVector.MuellerFromJones(qwp.A, qwp.B, qwp.C, qwp.D));
        var launch = JonesVector.FromAmplitudes(0.8, 0.6, 10, 70);
        var viaJones = Axial(Tracer(Lens(Plane(0.01, qwp, stop: true), Plane(0.02))), launch).FinalStokes;
        var viaMueller = Axial(Tracer(Lens(Plane(0.01, mueller, stop: true), Plane(0.02))), launch).FinalStokes;
        Close(viaJones.I, viaMueller.I, 1e-14, "I:");
        Close(viaJones.Q, viaMueller.Q, 1e-14, "Q:");
        Close(viaJones.U, viaMueller.U, 1e-14, "U:");
        Close(viaJones.V, viaMueller.V, 1e-14, "V:");
    }

    // ---- An identity matrix surface leaves the trace unchanged, bit for bit ----

    private static void Same(double a, double b, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b), $"{what}: {a:R} vs {b:R}");

    private static void Same(Complex a, Complex b, string what)
    {
        Same(a.Real, b.Real, what + " (re)");
        Same(a.Imaginary, b.Imaginary, what + " (im)");
    }

    private static void Same(Vector3D a, Vector3D b, string what)
    {
        Same(a.X, b.X, what + ".x");
        Same(a.Y, b.Y, what + ".y");
        Same(a.Z, b.Z, what + ".z");
    }

    [Fact]
    public void AnIdentityJonesSurface_LeavesTheTraceUnchanged_BitForBit()
    {
        SequentialLens Make(SurfaceShape? shape) => Lens(S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.02), Plane(0.06, shape));
        var plain = Tracer(Make(null));
        var identity = Tracer(Make(new JonesMatrixShape(1, 0, 0, 0, 0, 0, 1, 0)));
        double t = 4 * Math.PI / 180;
        var k = new Vector3D(0.3 * Math.Sin(t), Math.Sin(t), 0);
        k = new Vector3D(k.X, k.Y, Math.Sqrt(1 - k.X * k.X - k.Y * k.Y));
        var start = new Vector3D(0.001, -0.002, 0);
        var jones = JonesVector.FromAmplitudes(0.7, 0.5, 15, -40);
        var a = plain.TraceRay(plain.Trace.NewRay(start, k), jones);
        var b = identity.TraceRay(identity.Trace.NewRay(start, k), jones);

        Assert.Equal(a.Path.Hits.Count, b.Path.Hits.Count);
        for (int h = 0; h < a.Path.Hits.Count; h++)
        {
            Same(a.Path.Hits[h].Position, b.Path.Hits[h].Position, $"hit {h} position");
            Same(a.Path.Hits[h].Direction, b.Path.Hits[h].Direction, $"hit {h} direction");
        }
        Same(a.Path.Final.OpticalPath, b.Path.Final.OpticalPath, "optical path");
        Assert.Equal(a.Surfaces.Count, b.Surfaces.Count);
        for (int i = 0; i < a.Surfaces.Count; i++)
        {
            var (x, y) = (a.Surfaces[i], b.Surfaces[i]);
            Same(x.FieldOut.X, y.FieldOut.X, $"surface {x.Surface} Ex");
            Same(x.FieldOut.Y, y.FieldOut.Y, $"surface {x.Surface} Ey");
            Same(x.FieldOut.Z, y.FieldOut.Z, $"surface {x.Surface} Ez");
            Same(x.TotalTransmission, y.TotalTransmission, $"surface {x.Surface} total transmission");
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    Same(x.Cumulative[r, c], y.Cumulative[r, c], $"surface {x.Surface} cumulative [{r},{c}]");
        }
        Assert.Null(b.Surfaces[2].JonesMatrix);
        Same(a.Final.Intensity, b.Final.Intensity, "final intensity");
        Same(a.Final.Field!.Value.X, b.Final.Field!.Value.X, "final Ex");
        Same(a.Final.Field!.Value.Y, b.Final.Field!.Value.Y, "final Ey");
    }

    // ---- The saved types ----

    [Fact]
    public void BothTypes_AreRegistered_AndRoundTripTheirParameters()
    {
        var jones = SurfaceTypes.Find("Jones Matrix")!;
        Assert.Equal(new[] { "Ar", "Ai", "Br", "Bi", "Cr", "Ci", "Dr", "Di" }, jones.Parameters.Select(p => p.Name));
        Assert.Equal(Enumerable.Range(1, 8), jones.Parameters.Select(p => p.Number));
        Assert.False(jones.UsesRadiusAndConic);
        var j = (JonesMatrixShape)jones.Build(n => n * 0.25 - 1)!;
        Assert.Equal(new Complex(-0.75, -0.5), new Complex(j.Ar, j.Ai));   // parameters 1 and 2: n·0.25 − 1
        Assert.Equal(new Complex(-0.75, 0.5), j.A);                          // this program's convention: the conjugate
        Assert.Equal(Enumerable.Range(1, 8).Select(n => n * 0.25 - 1), jones.Read(j).OrderBy(p => p.Key).Select(p => p.Value));

        var mueller = SurfaceTypes.Find("Mueller Matrix")!;
        Assert.Equal(16, mueller.Parameters.Count);
        Assert.Equal("M00", mueller.Parameters[0].Name);
        Assert.Equal("M23", mueller.Parameters[11].Name);
        Assert.True(mueller.Parameter(13)!.IsExtraData);
        var m = (MuellerMatrixShape)mueller.Build(n => n)!;
        Assert.Equal(7, m[1, 2]);                                             // parameter 4·1 + 2 + 1
        Assert.Equal(Enumerable.Range(1, 16).Select(n => (double)n), mueller.Read(m).OrderBy(p => p.Key).Select(p => p.Value));
    }

    [Fact]
    public void ASavedLens_WithBothTypes_RoundTrips()
    {
        var lens = Lens(Plane(0.01, QuarterWaveAt45(), stop: true), Plane(0.01, MuellerMatrixShape.Depolarizer(0.3)), Plane(0.02));
        var saved = SavedSurfaces.FromLens(lens, LensUnit.Millimeter);
        var back = SavedSurfaces.ToLens(saved, LensUnit.Millimeter, _ => null);
        var q = Assert.IsType<JonesMatrixShape>(back.Surfaces[1].Shape);
        Assert.Equal(QuarterWaveAt45().A, q.A);
        var d = Assert.IsType<MuellerMatrixShape>(back.Surfaces[2].Shape);
        Assert.Equal(MuellerMatrixShape.Depolarizer(0.3).Matrix, d.Matrix);
    }

    // ---- What the analyses report ----

    [Fact]
    public void TheAnalyses_ReportTheStokesVector_AndRefuseAFieldAfterAMuellerSurface()
    {
        var lens = Lens(Plane(0.01, MuellerMatrixShape.Depolarizer(0.4), stop: true), Plane(0.02));
        var context = new OpticsAnalysisContext("P", new OpticsSettings
        {
            SystemData = SystemDataSettings.ToSettings(new OpticalSystemData
            {
                Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
            })
        });
        var rayTrace = new PolarizationRayTraceAnalysis(_ => lens).Compute(context, new PolarizationRayTraceSettings { Jx = 1, Jy = 0 }, default);
        Assert.Equal(new[] { "Ray", "Surfaces", "System polarization matrix (surface 1's frame)" }, rayTrace.Sections.Select(s => s.Heading));
        var dop = rayTrace.Sections[0].Pairs.Single(p => p.Key == "Degree of polarization").Value;
        Close(0.4, double.Parse(dop, CultureInfo.InvariantCulture), 1e-7, "reported degree of polarization:");
        Assert.Contains("not defined", rayTrace.Sections[0].Pairs.Single(p => p.Key == "System diattenuation").Value);
        Assert.Empty(rayTrace.Sections[2].TableRows);
        Assert.Equal("—", rayTrace.Sections[1].TableRows[0][8]);

        var e = Assert.ThrowsAny<Exception>(() =>
            new PolarizationPupilMapAnalysis(_ => lens).Compute(context, new PolarizationPupilMapSettings { Sampling = 3 }, default));
        Assert.Contains("surface 1 is a Mueller Matrix surface", Messages(e));
        var e2 = Assert.ThrowsAny<Exception>(() =>
            new PolarizationPhaseAberrationAnalysis(_ => lens).Compute(context, new PolarizationPhaseAberrationSettings { Points = 5 }, default));
        Assert.Contains("surface 1 is a Mueller Matrix surface", Messages(e2));
    }
}
