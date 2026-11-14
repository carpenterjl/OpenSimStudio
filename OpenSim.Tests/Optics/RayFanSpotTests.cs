using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-34: pupil sampling, ray fans, spot diagrams and footprints. The gate (plan): a perfect
/// system's spot radius is zero; a singlet's ray fan is cubic in the pupil coordinate in the
/// small-aperture limit; the footprint of an on-axis beam is the paraxial marginal-ray beam
/// diameter in the paraxial limit. The Seidel item (OP-36) is not built, so the third-order
/// coefficient is DERIVED in the test (a concave spherical mirror's exact transverse
/// aberration, expanded). Counts and rules of the samplings and the spot radii are from the
/// Ansys OpticStudio 2025 R2 help, cited at each use; every other expected value is derived in
/// the test's comments. Radii, distances and apertures are arbitrary test inputs.
/// </summary>
public sealed class RayFanSpotTests
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;   // 2^-52

    // ---- Builders ----

    private static SequentialSurface S(double radius, double thickness, double n = 1, double k = 0, bool stop = false,
        bool mirror = false, SurfaceShape? shape = null, SurfaceAperture? aperture = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, Shape = shape, Aperture = aperture,
    };

    private static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd, params double[] fields) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = fields.Length == 0 ? new[] { new FieldPoint() } : fields.Select(f => new FieldPoint { Y = f }).ToArray()
        },
    };

    private static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    private static void Close(double expected, double actual, double tolerance, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    /// <summary>A concave paraboloid, R = −0.2 m (focus f = 0.1 m), image at its focus.</summary>
    private static SequentialLens Paraboloid(SurfaceAperture? aperture = null) =>
        Lens(double.PositiveInfinity, S(-0.2, -0.1, k: -1, stop: true, mirror: true, aperture: aperture));

    // ---- Pupil sampling ----

    [Fact]
    public void Hexapolar_HasTheHelpsRingCounts_AndStartsOnPlusX()
    {
        // Help, "Hexapolar Rings": a sampling of 5 is 1 + 6 + 12 + 18 + 24 + 30 = 91 rays; ring 1
        // has 6 rays every 60°, the first at 0° (on +x); each ring 6 more than the last.
        var five = PupilSampling.Hexapolar(5);
        Assert.Equal(91, five.Count);
        Assert.Equal(91, PupilSampling.HexapolarCount(5));
        Assert.Equal((0.0, 0.0), five[0]);
        Assert.Equal((1.0 / 5, 0.0), five[1]);
        int start = 1;
        for (int k = 1; k <= 5; k++)
        {
            var ring = five.Skip(start).Take(6 * k).ToArray();
            Assert.Equal(6 * k, ring.Length);
            Assert.Equal(k / 5.0, ring[0].Px, 15);
            Assert.Equal(0.0, ring[0].Py);
            foreach (var (px, py) in ring) Close(k / 5.0, Math.Sqrt(px * px + py * py), 4 * Eps, $"ring {k} radius");
            start += 6 * k;
        }
    }

    [Fact]
    public void Dithered_IsReproducible_InsideThePupil_AndAsManyAsTheHexapolar()
    {
        var a = PupilSampling.Dithered(6);
        var b = PupilSampling.Dithered(6);
        Assert.Equal(PupilSampling.HexapolarCount(6), a.Count);
        Assert.Equal(a, b);   // fixed seed: the same bits on every call
        Assert.All(a, p => Assert.True(p.Px * p.Px + p.Py * p.Py <= 1));
        Assert.NotEqual(a, PupilSampling.Dithered(6, seed: 1));
        Assert.Equal(BatchTracer.SquareGrid(8), PupilSampling.Square(8));
    }

    [Fact]
    public void TheFootprintGrid_Is2nPlus1Across_AndTheFanIs2nPlus1Long()
    {
        // Help, "Footprint Diagram": "a setting of 10 will trace a grid of 21 x 21 rays".
        var grid = PupilSampling.HalfPupilGrid(10);
        var all = Enumerable.Range(-10, 21).SelectMany(i => Enumerable.Range(-10, 21).Select(k => (k / 10.0, i / 10.0))).ToArray();
        Assert.Equal(all.Count(p => p.Item1 * p.Item1 + p.Item2 * p.Item2 <= 1), grid.Count);
        Assert.Contains((0.0, 1.0), grid);
        Assert.Contains((1.0, 0.0), grid);
        var fan = PupilSampling.Fan(4);
        Assert.Equal(new[] { -1, -0.75, -0.5, -0.25, 0, 0.25, 0.5, 0.75, 1 }, fan);
    }

    // ---- RMS and GEO on hand-built rays ----

    [Fact]
    public void SpotRadii_FollowTheHelpsDefinitions_OnAHandBuiltSet()
    {
        // Rays (±1, 0) of weight 1 and (0, ±2) of weight 3. Weighted centroid (0, 0). About it
        // the squared distances 1, 1, 4, 4 average (1 + 1 + 12 + 12)/8 = 3.25; x part 2/8, y part
        // 24/8; GEO 2. About a chief ray at (1, 0): 0, 4, 5, 5 average (0 + 4 + 15 + 15)/8 = 4.25,
        // GEO √5. Middle (equal extremes) and vertex are both (0, 0) here.
        var points = new[] { new SpotPoint(1, 0, 1), new SpotPoint(-1, 0, 1), new SpotPoint(0, 2, 3), new SpotPoint(0, -2, 3) };
        var c = SpotStatistics.Compute(points, SpotReference.Centroid, (1, 0));
        Close(0, c.CentroidX, 0); Close(0, c.CentroidY, 0);
        Close(Math.Sqrt(3.25), c.Rms, 4 * Eps, "RMS about centroid");
        Close(0.5, c.RmsX, 4 * Eps, "RMS x");
        Close(Math.Sqrt(3), c.RmsY, 4 * Eps, "RMS y");
        Close(2, c.Geo, 0, "GEO about centroid");
        var chief = SpotStatistics.Compute(points, SpotReference.ChiefRay, (1, 0));
        Close(Math.Sqrt(4.25), chief.Rms, 4 * Eps, "RMS about chief");
        Close(Math.Sqrt(5), chief.Geo, 4 * Eps, "GEO about chief");

        // An asymmetric set (0, 0), (0, 0), (4, 0), equal weights: centroid x = 4/3, middle x = 2.
        // About the centroid the squares are 16/9, 16/9, 64/9, mean 32/9; GEO 8/3. About the
        // middle GEO 2 and RMS √((4 + 4 + 4)/3) = 2. About the vertex GEO 4, RMS √(16/3).
        var skew = new[] { new SpotPoint(0, 0, 1), new SpotPoint(0, 0, 1), new SpotPoint(4, 0, 1) };
        var sc = SpotStatistics.Compute(skew, SpotReference.Centroid, (0, 0));
        Close(4.0 / 3, sc.ReferenceX, 4 * Eps); Close(Math.Sqrt(32.0 / 9), sc.Rms, 4 * Eps); Close(8.0 / 3, sc.Geo, 4 * Eps);
        var sm = SpotStatistics.Compute(skew, SpotReference.Middle, (0, 0));
        Close(2, sm.ReferenceX, 0); Close(2, sm.Rms, 4 * Eps); Close(2, sm.Geo, 0);
        var sv = SpotStatistics.Compute(skew, SpotReference.Vertex, (9, 9));
        Close(4, sv.Geo, 0); Close(Math.Sqrt(16.0 / 3), sv.Rms, 4 * Eps);
        Assert.Throws<ArgumentException>(() => SpotStatistics.Compute(Array.Empty<SpotPoint>(), SpotReference.Centroid, (0, 0)));
    }

    // ---- Gate 1: a perfect system's spot radius is zero ----

    [Fact]
    public void AParaboloidOnAxis_HasZeroSpotRadius_AboutChiefAndCentroid()
    {
        // The paraboloid is stigmatic on axis (derived in RealRayTraceTests.ParabolicMirror_...):
        // every ray ends on the focus, so every radius is rounding only, a few ulps of f.
        double f = 0.1;
        var lens = Paraboloid();
        var system = Sys(0.08);
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest { Fields = new[] { 0 }, Wavelengths = new[] { 0 }, Pupil = PupilSampling.Hexapolar(8) });
        foreach (var reference in new[] { SpotReference.ChiefRay, SpotReference.Centroid, SpotReference.Middle })
        {
            var spot = SpotCore.Compute(sample, system, 0, new[] { 0 }, reference, 0, null);
            Assert.Equal(PupilSampling.HexapolarCount(8), spot.Points.Count);
            Close(0, spot.Statistics!.Rms, 64 * Eps * f, $"RMS about {reference}");
            Close(0, spot.Statistics.Geo, 64 * Eps * f, $"GEO about {reference}");
        }

        var result = new StandardSpotAnalysis(_ => lens).Compute(Context(system), new StandardSpotSettings(), default);
        var plot = Assert.IsType<AnalysisScatterPlot>(Assert.Single(result.Plots));
        Assert.Contains(plot.Series, s => s.Name == "Airy disk");
        Assert.Contains(result.Sections[0].Lines, l => l.Contains("Every ray launched reached"));
    }

    [Fact]
    public void AnIdealParaxialLens_HasZeroSpotRadius_AtEveryField()
    {
        // OP-27's Paraxial surface at infinite conjugates sends a field θ to f·tan θ for every
        // pupil ray (derived in IdealSurfaceTests.Paraxial_ObjectAtInfinity_...): zero spots.
        double f = 0.1;
        var lens = Lens(double.PositiveInfinity, S(double.PositiveInfinity, f, stop: true, shape: new ParaxialShape(f)));
        var system = Sys(0.05, 0, 5, 20);
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest { Fields = new[] { 0, 1, 2 }, Wavelengths = new[] { 0 }, Pupil = PupilSampling.Dithered(6) });
        for (int fi = 0; fi < 3; fi++)
        {
            var spot = SpotCore.Compute(sample, system, fi, new[] { 0 }, SpotReference.ChiefRay, 0, null);
            Close(0, spot.Statistics!.Geo, 256 * Eps * f, $"GEO, field {fi}");
            Close(f * Math.Tan(new[] { 0.0, 5, 20 }[fi] * Math.PI / 180), spot.Chief!.Value.Y, 256 * Eps * f, $"chief height, field {fi}");
        }
    }

    // ---- Gate 2: a singlet's ray fan is cubic, coefficient derived ----

    [Fact]
    public void ASphericalMirrorsRayFan_IsItsExactAberration_AndCubicWithTheDerivedCoefficient()
    {
        // Derivation. A concave sphere of radius ρ = |R| (R < 0, Conventions §3), centre C at
        // z = R, a ray parallel to the axis at height h meets it at z_Q = R + √(R² − h²); the
        // angle of incidence α has cos α = √(R² − h²)/ρ, and the reflected ray crosses the axis at
        // z_A = R + ρ/(2 cos α). On the paraxial focal plane z = R/2 it is at
        // y_B = h (z_A − R/2)/(z_A − z_Q) (similar triangles; RealTraceGateTests derives the same).
        // The reflected ray makes 2α with the axis and runs from (z_Q, h) through (z_A, 0), so the
        // same height is y_B = h − ρ(cos α − 1/2) tan 2α (distances measured toward C). With
        // cos α = 1 − α²/2 + …, tan 2α = 2α + 8α³/3 + … and h = ρ(α − α³/6 + …):
        // y_B = ρ(α − α³/6) − ρ(1/2 − α²/2)(2α + 8α³/3) + O(α⁵) = −ρα³/2 + O(α⁵) = −h³/(2ρ²) + O(h⁵), so with
        // h = Py·D/2 the fan is a₃·Py³ + a₅·Py⁵ + … with a₃ = −(D/2)³/(2ρ²) and no linear term.
        double r = -0.2, rho = 0.2, D = 0.004;
        var lens = Lens(double.PositiveInfinity, S(r, r / 2, stop: true, mirror: true));
        var system = Sys(D);
        var result = new RayAberrationAnalysis(_ => lens).Compute(Context(system), new RayAberrationSettings { RaysEachSide = 20 }, default);
        var tangential = Assert.IsType<LinePlot>(result.Plots[0]);
        var curve = Assert.Single(tangential.Curves);
        Assert.Equal(41, curve.X.Count);

        double tolerance = 64 * Eps * rho;   // meters: the trace's rounding (RealTraceGateTests)
        var py = curve.X.ToArray();
        var ey = curve.Y.Select(v => v / 1e6).ToArray();   // µm to m
        for (int i = 0; i < py.Length; i++)
        {
            double h = py[i] * D / 2;
            double cos = Math.Sqrt(r * r - h * h) / rho;
            double zQ = r + Math.Sqrt(r * r - h * h), zA = r + rho / (2 * cos);
            double yB = h == 0 ? 0 : h * (zA - r / 2) / (zA - zQ);
            Close(yB, ey[i], tolerance, $"EY at Py = {py[i]}");
        }

        // Odd least-squares fit e = a₁P + a₃P³ + a₅P⁵ over the fan (normal equations).
        var (a1, a3, _) = FitOdd(py, ey);
        double a3Derived = -Math.Pow(D / 2, 3) / (2 * rho * rho);
        // The fit's a₃ is the Taylor coefficient up to the neglected a₇ ~ a₃·(D/2ρ)⁴ ≈ 1e-8·a₃ and the
        // trace noise (~1e-15 m against a₃ ≈ 1e-7 m); 1e-6 relative leaves room for both.
        Close(a3Derived, a3, 1e-6 * Math.Abs(a3Derived), "third-order coefficient");
        Close(0, a1, 1e-6 * Math.Abs(a3Derived), "linear term (paraxial focus)");

        // The sagittal fan of an on-axis sphere is the same curve in x.
        var sagittal = Assert.IsType<LinePlot>(result.Plots[1]);
        for (int i = 0; i < py.Length; i++) Close(curve.Y[i], sagittal.Curves[0].Y[i], tolerance * 1e6, $"EX = EY at {py[i]}");
    }

    private static (double A1, double A3, double A5) FitOdd(double[] p, double[] e)
    {
        var m = new double[3, 3];
        var b = new double[3];
        for (int i = 0; i < p.Length; i++)
        {
            var phi = new[] { p[i], Math.Pow(p[i], 3), Math.Pow(p[i], 5) };
            for (int j = 0; j < 3; j++)
            {
                b[j] += phi[j] * e[i];
                for (int k = 0; k < 3; k++) m[j, k] += phi[j] * phi[k];
            }
        }
        // Gaussian elimination, 3 × 3.
        for (int c = 0; c < 3; c++)
            for (int rr = c + 1; rr < 3; rr++)
            {
                double factor = m[rr, c] / m[c, c];
                for (int k = c; k < 3; k++) m[rr, k] -= factor * m[c, k];
                b[rr] -= factor * b[c];
            }
        var x = new double[3];
        for (int c = 2; c >= 0; c--)
        {
            double s = b[c];
            for (int k = c + 1; k < 3; k++) s -= m[c, k] * x[k];
            x[c] = s / m[c, c];
        }
        return (x[0], x[1], x[2]);
    }

    // ---- Gate 3: the on-axis footprint is the paraxial beam in the paraxial limit ----

    [Fact]
    public void TheOnAxisFootprint_IsTheParaxialMarginalBeam_InTheParaxialLimit()
    {
        // A glass singlet (n = 1.5) with the stop on its front: the footprint's largest radial
        // height on each surface is the marginal ray's (the grid holds (0, ±1) and (±1, 0)),
        // which the paraxial marginal ray gives in the limit of a small aperture. With D/2 = 1e-5 m
        // on 0.05 m radii the real–paraxial difference is of relative order (h/R)² ≈ 4e-8.
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.09));
        foreach (double d in new[] { 2e-5, 0.02 })
        {
            var system = Sys(d);
            var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), system);
            for (int surface = 1; surface <= 2; surface++)
            {
                var sample = RaySampler.Trace(lens, system, new RaySampleRequest
                {
                    Fields = new[] { 0 }, Wavelengths = new[] { 0 }, Pupil = PupilSampling.HalfPupilGrid(4), Surface = surface,
                });
                double largest = sample.Rays.Where(r => r.Passed).Max(r => Math.Sqrt(r.Position.X * r.Position.X + r.Position.Y * r.Position.Y));
                double marginal = Math.Abs(paraxial.MarginalRay.Heights[surface]);
                if (d < 1e-3)
                    Close(2 * marginal, 2 * largest, 1e-6 * 2 * marginal, $"beam diameter on surface {surface}");
                else if (surface == 2)
                    Assert.True(Math.Abs(largest - marginal) > 1e-4 * marginal, "at full aperture the real footprint should differ from the paraxial one");
            }
        }

        var analysis = new FootprintDiagramAnalysis(_ => lens).Compute(Context(Sys(0.02)), new FootprintDiagramSettings { Surface = 2 }, default);
        Assert.StartsWith("Rays through: 100 %", analysis.Sections[0].Lines[0]);
    }

    // ---- Failed rays are counted, not dropped ----

    [Fact]
    public void RaysStoppedByAnAperture_AreCountedAndNamed()
    {
        // Hexapolar 4 rings on D = 0.08: ring k at h = 0.04·k/4. A circular aperture of radius
        // 0.8·0.04 on the mirror stops ring 4 only (24 rays): 1 + 6 + 12 + 18 = 37 of 61 pass.
        var lens = Paraboloid(new CircularAperture(0, 0.032));
        var system = Sys(0.08);
        var result = new StandardSpotAnalysis(_ => lens).Compute(Context(system),
            new StandardSpotSettings { RayDensity = 4, Reference = SpotReference.Centroid }, default);
        var row = Assert.Single(result.Sections[0].TableRows);
        Assert.Equal("37 of 61", row[^1]);
        Assert.Contains(result.Sections[0].Lines, l => l.Contains("24 stopped by an aperture at surface 1"));

        var footprint = new FootprintDiagramAnalysis(_ => lens).Compute(Context(system), new FootprintDiagramSettings { Surface = 1, DeleteVignetted = true }, default);
        Assert.Contains(footprint.Sections[0].Lines, l => l.Contains("stopped by an aperture at surface 1"));
    }

    // ---- Airy radius and through focus on the paraboloid ----

    [Fact]
    public void TheWorkingFNumber_OfAParaboloid_IsOneOverTwoSinTheta()
    {
        // Help, "Working F/#": W = 1/(2 n sin θ). On the paraboloid z = −h²/(4f) the marginal
        // ray from height h runs to the focus (0, −f) over √(h² + (f − h²/(4f))²) = f + h²/(4f),
        // so sin θ = h/(f + h²/(4f)); on axis the four marginal rays agree. n = 1.
        double f = 0.1, h = 0.04;
        var lens = Paraboloid();
        var system = Sys(2 * h);
        var trace = SequentialTrace.Create(lens, system);
        var w = RealWorkingFNumber.Compute(trace, new RayLauncher(lens, system), 0, 0);
        Assert.True(w.Succeeded, w.Failure);
        double sin = h / (f + h * h / (4 * f));
        Close(1 / (2 * sin), w.Value, 1e-13 / (2 * sin), "working F/#");
        // Help, "Standard Spot Diagram": Airy radius = 1.22 λ F/#.
        Close(1.22 * 0.55e-6 * w.Value, RealWorkingFNumber.AiryRadius(0.55e-6, w.Value), 1e-25);
    }

    [Fact]
    public void ThroughFocus_OnAParaboloid_GrowsAsTheMarginalSlope()
    {
        // Every ray passes the focus; at a defocus dz along z it is |dz|·tan θ from the axis.
        // From the triangle above, cos θ = (f − h²/(4f))/(f + h²/(4f)), so the outermost ring
        // (h = D/2) gives GEO = |dz|·h/(f − h²/(4f)).
        double f = 0.1, h = 0.04, dz = 50e-6;
        var lens = Paraboloid();
        var system = Sys(2 * h);
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest { Fields = new[] { 0 }, Wavelengths = new[] { 0 }, Pupil = PupilSampling.Hexapolar(6) });
        foreach (int k in new[] { -2, -1, 1, 2 })
        {
            var spot = SpotCore.Compute(sample, system, 0, new[] { 0 }, SpotReference.ChiefRay, k * dz, null);
            Close(Math.Abs(k * dz) * h / (f - h * h / (4 * f)), spot.Statistics!.Geo, 1e-12 * Math.Abs(k * dz) + 64 * Eps * f, $"GEO at {k}·dz");
        }
        var result = new ThroughFocusSpotAnalysis(_ => lens).Compute(Context(system), new ThroughFocusSpotSettings(), default);
        Assert.Equal(5, result.Plots.Count);
    }

    // ---- Full field, incident angle, Y-Ybar on the ideal lens ----

    [Fact]
    public void TheFullFieldSpot_PlacesEachFieldAtItsChiefOffsetFromFieldOne()
    {
        // Each field's (zero-size) spot sits at f·tan θ; relative to field 1's chief ray (θ = 0)
        // that is f·tan θ, whatever the exaggeration. Lens units are mm (the default).
        double f = 0.1;
        var lens = Lens(double.PositiveInfinity, S(double.PositiveInfinity, f, stop: true, shape: new ParaxialShape(f)));
        var system = Sys(0.05, 0, 5, 20);
        var result = new FullFieldSpotAnalysis(_ => lens).Compute(Context(system), new FullFieldSpotSettings { Exaggerate = 10 }, default);
        var plot = Assert.IsType<AnalysisScatterPlot>(Assert.Single(result.Plots));
        Assert.Equal(3, plot.Series.Count);
        for (int i = 0; i < 3; i++)
        {
            double y = f * Math.Tan(new[] { 0.0, 5, 20 }[i] * Math.PI / 180) * 1e3;
            foreach (var p in plot.Series[i].Points)
            {
                Close(y, p.Y, 4096 * Eps * f * 1e3, $"field {i + 1} y");
                Close(0, p.X, 4096 * Eps * f * 1e3, $"field {i + 1} x");
            }
        }
    }

    [Fact]
    public void IncidentAngles_OnAnIdealLens_FollowItsSlopeRule()
    {
        // The Paraxial surface (n = 1) maps a ray's slope u = M/N to u′ = u − x/f
        // (IdealSurfaceTests). With the stop on it, the chief ray (x = 0) keeps θ and lands at
        // f·tan θ; the upper and lower marginal rays (x = ±D/2) leave at tan θ ∓ D/(2f).
        double f = 0.1, D = 0.02, max = 20;
        var lens = Lens(double.PositiveInfinity, S(double.PositiveInfinity, f, stop: true, shape: new ParaxialShape(f)));
        var system = Sys(D, 0, max);
        var result = new IncidentAngleAnalysis(_ => lens).Compute(Context(system), new IncidentAngleSettings { FieldDensity = 5 }, default);
        var plot = Assert.IsType<LinePlot>(Assert.Single(result.Plots));
        for (int i = 0; i < 5; i++)
        {
            double t = Math.Tan(max * i / 4.0 * Math.PI / 180);
            double tol = 1e-12;
            Close(f * t * 1e3, plot.Curves[1].X[i], 256 * Eps * f * 1e3, "image height (mm)");
            Close(Math.Atan(t + D / (2 * f)) * 180 / Math.PI, plot.Curves[0].Y[i], tol, "lower marginal");
            Close(Math.Atan(t) * 180 / Math.PI, plot.Curves[1].Y[i], tol, "chief");
            Close(Math.Atan(t - D / (2 * f)) * 180 / Math.PI, plot.Curves[2].Y[i], tol, "upper marginal");
        }

        // Y-Ybar: on the lens y = D/2, ȳ = 0; at the image y = 0, ȳ = f·tan 20° (the paraxial
        // chief ray of an angle field has slope tan θ, Conventions §11).
        var yy = new YYbarAnalysis(_ => lens).Compute(Context(system), new YYbarSettings(), default);
        var c = Assert.Single(Assert.IsType<LinePlot>(Assert.Single(yy.Plots)).Curves);
        double mm = 64 * Eps * f * 1e3;   // a few roundings of lengths no larger than f, in mm
        Close(D / 2 * 1e3, c.Y[0], mm, "y on the lens"); Close(0, c.X[0], mm, "ȳ on the lens");
        Close(0, c.Y[1], mm, "y at the image"); Close(f * Math.Tan(max * Math.PI / 180) * 1e3, c.X[1], mm, "ȳ at the image");
    }

    // ---- Sampler: intermediate surfaces, determinism, the registry ----

    [Fact]
    public void TheSampler_ReadsTheSameRaysThroughEitherPath_AndIsBitwiseTheSameAtAnyParallelism()
    {
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.09));
        var system = Sys(0.02, 0, 3, 6) with
        {
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 0.5 }, new Wavelength { Micrometers = 0.6 } } }
        };
        var request = new RaySampleRequest { Fields = new[] { 0, 1, 2 }, Wavelengths = new[] { 0, 1 }, Pupil = PupilSampling.Hexapolar(4) };
        var batch = RaySampler.Trace(lens, system, request);                                  // image: BatchTracer
        var launcher = new RayLauncher(lens, system);
        for (int fi = 0; fi < 3; fi++)
            for (int wi = 0; wi < 2; wi++)
            {
                var trace = SequentialTrace.Create(lens, system, wi);
                var (hx, hy) = system.Fields.Normalized(fi);
                for (int p = 0; p < request.Pupil.Count; p++)
                {
                    var (px, py) = request.Pupil[p];
                    var viaPath = RaySampler.AtSurface(trace, launcher, hx, hy, px, py, lens.ImageIndex, default);
                    Assert.Equal(viaPath.Position, batch.At(fi, wi, p).Position);
                }
            }
        var one = RaySampler.Trace(lens, system, request with { Surface = 2, MaxDegreeOfParallelism = 1 });
        var many = RaySampler.Trace(lens, system, request with { Surface = 2, MaxDegreeOfParallelism = null });
        Assert.Equal(one.Rays, many.Rays);
        Assert.Equal(one.Chiefs, many.Chiefs);
    }

    [Fact]
    public void EveryOp34Analysis_IsRegistered_AndSaysSoWhenThereAreNoSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        foreach (var id in new[]
        {
            RayAberrationAnalysis.SavedId, StandardSpotAnalysis.SavedId, ThroughFocusSpotAnalysis.SavedId, FullFieldSpotAnalysis.SavedId,
            MatrixSpotAnalysis.SavedId, FootprintDiagramAnalysis.SavedId, YYbarAnalysis.SavedId, IncidentAngleAnalysis.SavedId,
        })
        {
            Assert.True(registry.TryGet(id, out var analysis), id);
            var empty = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), analysis.CreateDefaultSettings(), default);
            Assert.Equal(new[] { "No surfaces" }, empty.Sections.Select(s => s.Heading));
        }
    }

    [Fact]
    public void TheMatrixSpot_HasOneSpotPerFieldAndWavelength()
    {
        double f = 0.1;
        var lens = Lens(double.PositiveInfinity, S(double.PositiveInfinity, f, stop: true, shape: new ParaxialShape(f)));
        var system = Sys(0.05, 0, 10) with
        {
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 0.5 }, new Wavelength { Micrometers = 0.6 }, new Wavelength { Micrometers = 0.7 } } }
        };
        var result = new MatrixSpotAnalysis(_ => lens).Compute(Context(system), new MatrixSpotSettings(), default);
        Assert.Equal(6, result.Plots.Count);
        Assert.Equal(6, result.Sections[0].TableRows.Count);
        Assert.Throws<ArgumentException>(() => new StandardSpotAnalysis(_ => lens).Compute(Context(system), new StandardSpotSettings { RayDensity = 2 }, default));
    }
}
