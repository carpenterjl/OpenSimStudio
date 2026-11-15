using System.Text;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.EnclosedEnergy;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-41: the enclosed-energy engine's identities (the autocorrelation sum against a direct
/// integral of OP-38's PSF, a tilt as a shift, the whole period, the polychromatic shares,
/// determinism), the geometric fractions, the smallest enclosing circle, the line/edge spread,
/// the IMA reader, the extended source, and the four analyses. Expected values are derived in the
/// comments. The gate is <c>Gates/EnclosedEnergyGateTests</c>. OpticStudio's behaviour is read
/// from its 2025 R2 help pages saved under plans/sources/optics/op-41-help/.
/// </summary>
public sealed class EnclosedEnergyTests
{
    private const double Lambda = 0.6e-6;

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    private static SequentialSurface S(double radius, double thickness, double n = 1, double k = 0, bool stop = false,
        bool mirror = false, SurfaceShape? shape = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, Shape = shape,
    };

    private static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance } }.Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd, double[] angleFields, params double[] wavelengths) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = angleFields.Length == 0 ? new[] { new FieldPoint() } : angleFields.Select(a => new FieldPoint { Y = a }).ToArray(),
        },
        Wavelengths = wavelengths.Length == 0 ? new WavelengthSet()
            : new WavelengthSet { Wavelengths = wavelengths.Select(w => new Wavelength { Micrometers = w }).ToArray() },
    };

    private static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    // A singlet near its paraxial focus, as in PsfAnalysisTests.
    private static SequentialLens Singlet() => Lens(double.PositiveInfinity, S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));

    // An ideal paraxial lens of focal length f (OP-27), the image δ beyond its focus.
    private static SequentialLens IdealLens(double f, double defocus) =>
        Lens(double.PositiveInfinity, S(double.PositiveInfinity, f + defocus, stop: true, shape: new ParaxialShape(f)));

    // An aberrated pupil with unequal, one negative, pupil scales and a non-unit image index.
    private static PupilFunction Comatic(int n) => PupilFunction.FromFunctions(n,
        (x, y) => 1 - 0.2 * (x * x + y * y), (x, y) => 0.3 * (x * x + y * y) * y + 0.15 * x, 0.04, -0.05, Lambda, 1.3);

    /// <summary>The energy of one PSF period in <see cref="PsfEngine.Sample"/>'s units: the sum
    /// is I = |U|²/(ΣA)², and Parseval over one period gives ∫|U|² = ΣA²·T_x·T_y (each node's
    /// plane wave carries |p|² per unit area over the period, cross terms integrate to zero).</summary>
    private static double CellEnergy(PupilFunction p, double tx, double ty) => p.PowerSum * tx * ty / (p.AmplitudeSum * p.AmplitudeSum);

    // ---- The diffraction engine ----

    /// <summary>The autocorrelation sum equals a direct quadrature of OP-38's PSF (the matrix
    /// Fourier transform evaluated anywhere) over each region, off the reference point, for an
    /// aberrated, apodized pupil with a reversed axis. The PSF is a trigonometric polynomial
    /// with phase spans below 60 rad over these regions, so 96-point Gauss–Legendre rules are
    /// exact to rounding, and a uniform rule over a full period (or full circle) is exact for its
    /// degree; 1e-10 allows for summing ~10⁴ terms.</summary>
    [Fact]
    public void TheAutocorrelationSum_EqualsADirectIntegralOfThePsf_OverEachRegion()
    {
        const int n = 16;
        var p = Comatic(n);
        var kernel = DiffractionEnclosedEnergyKernel.FromPupil(p);
        double dp = 2.0 / (n - 1);
        double tx = Lambda / (1.3 * 0.04 * dp), ty = Lambda / (1.3 * 0.05 * dp);
        Close(tx, kernel.PeriodX, 1e-12 * tx, "period in x");
        Close(ty, kernel.PeriodY, 1e-12 * ty, "period in y");
        double cell = CellEnergy(p, tx, ty);
        double x0 = 0.07 * tx, y0 = -0.05 * ty;
        double h = 0.3 * Math.Min(tx, ty);

        // Ensquared: a 96 × 96 Gauss–Legendre grid over the square.
        var (gx, wx) = GaussLegendre.Rule(96, x0 - h, x0 + h);
        var (gy, wy) = GaussLegendre.Rule(96, y0 - h, y0 + h);
        var image = PsfEngine.Sample(p, gx, gy);
        double square = 0;
        for (int r = 0; r < gy.Length; r++)
            for (int c = 0; c < gx.Length; c++) square += wy[r] * wx[c] * image.Intensity[r, c];
        Close(square / cell, kernel.Fraction(EnclosedEnergyType.Ensquared, h, x0, y0), 1e-10, "ensquared");

        // X-only: Gauss–Legendre across the slit, a uniform rule over one period along it
        // (64 points integrate the period's trigonometric polynomial of degree ≤ n − 1 exactly).
        var ys = Enumerable.Range(0, 64).Select(j => j * ty / 64).ToArray();
        image = PsfEngine.Sample(p, gx, ys);
        double slit = 0;
        for (int r = 0; r < ys.Length; r++)
            for (int c = 0; c < gx.Length; c++) slit += ty / 64 * wx[c] * image.Intensity[r, c];
        Close(slit / cell, kernel.Fraction(EnclosedEnergyType.XOnly, h, x0, y0), 1e-10, "x-only");

        // Y-only, the same turned.
        var xs = Enumerable.Range(0, 64).Select(j => j * tx / 64).ToArray();
        image = PsfEngine.Sample(p, xs, gy);
        slit = 0;
        for (int r = 0; r < gy.Length; r++)
            for (int c = 0; c < xs.Length; c++) slit += tx / 64 * wy[r] * image.Intensity[r, c];
        Close(slit / cell, kernel.Fraction(EnclosedEnergyType.YOnly, h, x0, y0), 1e-10, "y-only");

        // Encircled: Gauss–Legendre in r (48 points), 160 equal steps in θ (the PSF on a circle
        // of radius r is a trigonometric polynomial in θ of degree below |k|max·r < 50).
        var (gr, wr) = GaussLegendre.Rule(48, 0, h);
        double disk = 0;
        for (int i = 0; i < gr.Length; i++)
        {
            var px = new double[1];
            var py = new double[1];
            double ring = 0;
            for (int j = 0; j < 160; j++)
            {
                double t = 2 * Math.PI * j / 160;
                px[0] = x0 + gr[i] * Math.Cos(t);
                py[0] = y0 + gr[i] * Math.Sin(t);
                ring += PsfEngine.Sample(p, px, py).Intensity[0, 0];
            }
            disk += wr[i] * gr[i] * ring * 2 * Math.PI / 160;
        }
        Close(disk / cell, kernel.Fraction(EnclosedEnergyType.Encircled, h, x0, y0), 1e-10, "encircled");
    }

    /// <summary>A tilt W = t·Px moves the image by Δx = t·λ/(n·a_x) and changes nothing else: on
    /// the nodes, p_k gains e^{2πi·t·Px_k}, so C_m gains e^{2πi·t·ΔP·m_x} = e^{i·k_m·Δx}
    /// (k_m = 2π·n·a_x·ΔP·m_x/λ), which the reference offset Δx takes off again exactly.</summary>
    [Fact]
    public void ATiltedPupil_EnclosesTheUntiltedEnergy_AboutItsShiftedImage()
    {
        const int n = 24;
        const double t = 1.5, ax = -0.04, ay = 0.03, index = 1.2;
        var flat = PupilFunction.FromFunctions(n, (_, _) => 1, (x, y) => 0.1 * (x * x - y * y), ax, ay, Lambda, index);
        var tilted = PupilFunction.FromFunctions(n, (_, _) => 1, (x, y) => 0.1 * (x * x - y * y) + t * x, ax, ay, Lambda, index);
        var k0 = DiffractionEnclosedEnergyKernel.FromPupil(flat);
        var k1 = DiffractionEnclosedEnergyKernel.FromPupil(tilted);
        double dx = t * Lambda / (index * ax);
        foreach (var type in Enum.GetValues<EnclosedEnergyType>())
            foreach (double f in new[] { 0.05, 0.2, 0.45 })
            {
                double size = f * k0.MaximumSize(type) * 2;
                Close(k0.Fraction(type, size), k1.Fraction(type, size, dx, 0), 1e-12, $"{type} at {f} of the period");
            }
    }

    /// <summary>A slit or square of half-width T/2 is the whole period: sin(k_m·T/2) = sin(π·m) = 0
    /// for every m ≠ 0, so only C_0 remains and the fraction is 1.</summary>
    [Fact]
    public void TheWholePeriod_HoldsAllTheEnergy()
    {
        var p = Comatic(20);
        var k = DiffractionEnclosedEnergyKernel.FromPupil(p);
        Close(1, k.Fraction(EnclosedEnergyType.XOnly, k.PeriodX / 2, 0.3e-6, 0.1e-6), 1e-12, "x slit over the period");
        Close(1, k.Fraction(EnclosedEnergyType.YOnly, k.PeriodY / 2, -0.2e-6, 0.4e-6), 1e-12, "y slit over the period");
        var square = PupilFunction.FromFunctions(20, (_, _) => 1, (x, y) => 0.2 * x * y, 0.04, 0.04, Lambda);
        var ks = DiffractionEnclosedEnergyKernel.FromPupil(square);
        Close(1, ks.Fraction(EnclosedEnergyType.Ensquared, ks.PeriodX / 2), 1e-12, "square over the period");
        Assert.Equal(0, k.Fraction(EnclosedEnergyType.Encircled, 0));
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => k.Fraction(EnclosedEnergyType.Encircled, k.MaximumSize(EnclosedEnergyType.Encircled) * 1.01));
        Assert.Contains("period", e.Message);
    }

    /// <summary>Polychromatic: each pupil's fraction weighted by weight × transmission, the shares
    /// summing to 1. An annular pupil passes less light (its transmission is below 1).</summary>
    [Fact]
    public void ThePolychromaticFraction_WeighsEachWavelength_ByWeightTimesTransmission()
    {
        var p1 = PupilFunction.FromFunctions(32, (_, _) => 1, (_, _) => 0, 0.05, 0.05, 0.5e-6);
        var p2 = PupilFunction.FromFunctions(32, (x, y) => x * x + y * y < 0.25 ? 0 : 1, (x, y) => 0.05 * x * x, 0.05, 0.05, 0.7e-6);
        Assert.True(p2.Transmission < 0.9);
        var setup = DiffractionEnclosedEnergy.FromPupils(new[] { p1, p2 }, new[] { 1.0, 3.0 });
        double s1 = 1 * p1.Transmission, s2 = 3 * p2.Transmission;
        Close(s1 / (s1 + s2), setup.Shares[0], 1e-15, "share 1");
        Close(s2 / (s1 + s2), setup.Shares[1], 1e-15, "share 2");
        double r = 0.4 * setup.MaximumSize(EnclosedEnergyType.Encircled);
        double expected = setup.Shares[0] * setup.Kernels[0].Fraction(EnclosedEnergyType.Encircled, r)
            + setup.Shares[1] * setup.Kernels[1].Fraction(EnclosedEnergyType.Encircled, r);
        Close(expected, setup.Fraction(EnclosedEnergyType.Encircled, r), 1e-15, "polychromatic fraction");
        Close(Math.Min(setup.Kernels[0].MaximumSize(EnclosedEnergyType.Encircled), setup.Kernels[1].MaximumSize(EnclosedEnergyType.Encircled)),
            setup.MaximumSize(EnclosedEnergyType.Encircled), 0, "the shortest period limits the size");
    }

    [Fact]
    public void TheDiffractionEngine_IsBitwiseTheSame_AtAnyDegreeOfParallelism()
    {
        var p = Comatic(24);
        var k1 = DiffractionEnclosedEnergyKernel.FromPupil(p, 1);
        var sizes = EnclosedEnergyRegions.Sizes(k1.MaximumSize(EnclosedEnergyType.Encircled), 20);
        var reference = k1.Fractions(EnclosedEnergyType.Encircled, sizes, 1e-7, -2e-7, 1);
        foreach (int? dop in new int?[] { 2, 3, null })
        {
            var k = DiffractionEnclosedEnergyKernel.FromPupil(p, dop);
            Assert.Equal(reference, k.Fractions(EnclosedEnergyType.Encircled, sizes, 1e-7, -2e-7, dop));
        }
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 3.0 }, 0.486, 0.588, 0.656);
        var a = DiffractionEnclosedEnergy.Prepare(lens, system, 1, new[] { 0, 1, 2 }, 24, EnclosedEnergyReference.Centroid, maxDegreeOfParallelism: 1);
        var b = DiffractionEnclosedEnergy.Prepare(lens, system, 1, new[] { 0, 1, 2 }, 24, EnclosedEnergyReference.Centroid, maxDegreeOfParallelism: null);
        var s = EnclosedEnergyRegions.Sizes(a.DefaultSize, 10);
        Assert.Equal(a.Fractions(EnclosedEnergyType.Ensquared, s, 1), b.Fractions(EnclosedEnergyType.Ensquared, s, null));
    }

    [Fact]
    public void TheDiffractionSetup_RefusesTheMiddleReference_AndStatesItsReference()
    {
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 3.0 });
        Assert.Throws<ArgumentException>(() => DiffractionEnclosedEnergy.Prepare(lens, system, 0, new[] { 0 }, 24, EnclosedEnergyReference.Middle));
        var setup = DiffractionEnclosedEnergy.Prepare(lens, system, 1, new[] { 0 }, 24, EnclosedEnergyReference.Vertex);
        Assert.Contains(setup.Notes, n => n.Contains("vertex"));
        // Off axis the vertex lies far from the chief ray (3° at f ≈ 64 mm is about 3.4 mm),
        // beyond a quarter of the PSF's period: warned.
        Assert.Contains(setup.Warnings, w => w.Contains("quarter of the PSF's period"));
    }

    // ---- Geometric ----

    /// <summary>A hand-built set (weights in brackets) about the origin: (0, 0)[1], (3, 4)[1],
    /// (−2, 0)[2], (0, −1)[1], (1, 1)[1]; total 6. Distances are inclusive.</summary>
    [Fact]
    public void TheGeometricFractions_CountTheWeightInsideEachRegion()
    {
        var pts = new[]
        {
            new SpotPoint(0, 0, 1), new SpotPoint(3, 4, 1), new SpotPoint(-2, 0, 2), new SpotPoint(0, -1, 1), new SpotPoint(1, 1, 1),
        };
        double F(EnclosedEnergyType t, double s) => GeometricEnclosedEnergy.Fraction(pts, 0, 0, t, s);
        Close(1.0 / 6, F(EnclosedEnergyType.Encircled, 0), 1e-15, "r ≤ 0: the point at the reference");
        Close(2.0 / 6, F(EnclosedEnergyType.Encircled, 1), 1e-15, "r ≤ 1: (0,0), (0,−1)");
        Close(5.0 / 6, F(EnclosedEnergyType.Encircled, 2), 1e-15, "r ≤ 2: adds (1,1) at √2 and (−2,0)");
        Close(1, F(EnclosedEnergyType.Encircled, 5), 0, "r ≤ 5: all");
        Close(3.0 / 6, F(EnclosedEnergyType.XOnly, 1), 1e-15, "|x| ≤ 1: (0,0), (0,−1), (1,1)");
        Close(5.0 / 6, F(EnclosedEnergyType.YOnly, 1), 1e-15, "|y| ≤ 1: all but (3,4)");
        Close(3.0 / 6, F(EnclosedEnergyType.Ensquared, 1), 1e-15, "square 1: (0,0), (0,−1), (1,1)");
        Close(5.0 / 6, F(EnclosedEnergyType.Ensquared, 2), 1e-15, "square 2: adds (−2,0)");
        Close(1, F(EnclosedEnergyType.Ensquared, 4), 0, "square 4: all");
        // About another reference: the centroid (0·1 + 3 − 4 + 0 + 1)/6 = 0, (0 + 4 + 0 − 1 + 1)/6 = 2/3.
        var c = GeometricEnclosedEnergy.ReferencePoint(pts, EnclosedEnergyReference.Centroid, null, "test");
        Close(0, c.X, 1e-15, "centroid x");
        Close(4.0 / 6, c.Y, 1e-15, "centroid y");
        Assert.Throws<InvalidOperationException>(() => GeometricEnclosedEnergy.ReferencePoint(pts, EnclosedEnergyReference.ChiefRay, null, "test"));
    }

    [Fact]
    public void TheSmallestEnclosingCircle_IsTheCircleThroughTheBoundaryPoints()
    {
        // 40 points on the circle of centre (1, 2) and radius 3, and 30 inside.
        var pts = new List<SpotPoint>();
        for (int i = 0; i < 40; i++) pts.Add(new SpotPoint(1 + 3 * Math.Cos(0.3 + i * 0.157), 2 + 3 * Math.Sin(0.3 + i * 0.157), 1));
        for (int i = 0; i < 30; i++) pts.Add(new SpotPoint(1 + 2 * Math.Cos(i), 2 + 1.5 * Math.Sin(2 * i), 1));
        var (x, y, r) = GeometricEnclosedEnergy.SmallestEnclosingCircle(pts);
        Close(1, x, 1e-10, "centre x");
        Close(2, y, 1e-10, "centre y");
        Close(3, r, 1e-10, "radius");
        // An obtuse triangle: the circle on its longest side.
        var (tx, ty, tr) = GeometricEnclosedEnergy.SmallestEnclosingCircle(new[] { new SpotPoint(0, 0, 1), new SpotPoint(4, 0, 1), new SpotPoint(1, 1, 1) });
        Close(2, tx, 1e-14, "obtuse centre x");
        Close(0, ty, 1e-14, "obtuse centre y");
        Close(2, tr, 1e-14, "obtuse radius");
        var m = GeometricEnclosedEnergy.ReferencePoint(pts, EnclosedEnergyReference.Middle, null, "test");
        Close(1, m.X, 1e-10, "middle x");
    }

    /// <summary>
    /// An ideal paraxial lens (OP-27) sends every ray of an axial field through its focus, so on a
    /// plane δ beyond the focus the ray from pupil point (Px, Py) lands at −(Px, Py)·(D/2)·δ/f
    /// (similar triangles). The geometric fraction within r is then the share of pupil nodes
    /// with ρ ≤ r·f/((D/2)·δ), and within a square of half-width h the share with
    /// max(|Px|, |Py|) ≤ h·f/((D/2)·δ). The sizes are taken midway between neighbouring node
    /// distances so that no node sits on a boundary.
    /// </summary>
    [Fact]
    public void ADefocusedIdealLens_EnclosesTheShareOfPupilNodes_InsideTheScaledRegion()
    {
        const double f = 0.1, delta = 0.002, epd = 0.02;
        var lens = IdealLens(f, delta);
        var system = Sys(epd, Array.Empty<double>());
        const int n = 16;
        var spot = GeometricEnclosedEnergy.Trace(lens, system, 0, new[] { 0 }, n, EnclosedEnergyReference.ChiefRay);
        var nodes = GeometricEnclosedEnergy.PupilGrid(n);
        Assert.Equal(nodes.Count, spot.Points.Count);
        double scale = epd / 2 * delta / f;
        Close(0, spot.ReferenceX, 1e-15, "chief x");
        Close(0, spot.ReferenceY, 1e-15, "chief y");
        foreach (var type in new[] { EnclosedEnergyType.Encircled, EnclosedEnergyType.Ensquared, EnclosedEnergyType.XOnly })
        {
            var d = nodes.Select(q => EnclosedEnergyRegions.Distance(type, q.Px, q.Py)).Distinct().OrderBy(v => v).ToArray();
            for (int i = 1; i < d.Length; i += Math.Max(1, d.Length / 7))
            {
                double mid = (d[i - 1] + d[i]) / 2;
                if (d[i] - d[i - 1] < 1e-9) continue;
                double share = nodes.Count(q => EnclosedEnergyRegions.Distance(type, q.Px, q.Py) <= mid) / (double)nodes.Count;
                Close(share, spot.Fraction(type, mid * scale), 1e-15, $"{type} at node distance {mid:g6}");
            }
        }
    }

    [Fact]
    public void TheGeometricLineAndEdgeSpread_BinTheRaysAndAccumulateThem()
    {
        // Rays at y = −1.5, −0.2, 0.1, 0.1, 1.7 (weights 1, 2, 1, 1, 1) about the reference;
        // four bins across ±2 (width 1, centres −1.5, −0.5, 0.5, 1.5). X orientation runs along y.
        var pts = new[]
        {
            new SpotPoint(9, -1.5, 1), new SpotPoint(-3, -0.2, 2), new SpotPoint(0, 0.1, 1), new SpotPoint(5, 0.1, 1), new SpotPoint(2, 1.7, 1),
        };
        var (c, line, edge) = GeometricEnclosedEnergy.LineEdgeSpread(pts, 0, 0, parallelToX: true, maxRadius: 2, bins: 4);
        Assert.Equal(new[] { -1.5, -0.5, 0.5, 1.5 }, c);
        // Bin weights 1, 2, 2, 1 → over the largest (2).
        Assert.Equal(new[] { 0.5, 1, 1, 0.5 }, line);
        // Weight at or below each centre over 6: −1.5 holds the ray at −1.5 (1); −0.5: 1; 0.5: 5; 1.5: 5.
        Close(1.0 / 6, edge[0], 1e-15, "edge at −1.5");
        Close(1.0 / 6, edge[1], 1e-15, "edge at −0.5");
        Close(5.0 / 6, edge[2], 1e-15, "edge at 0.5");
        Close(5.0 / 6, edge[3], 1e-15, "edge at 1.5");
    }

    // ---- IMA files and the extended source ----

    [Fact]
    public void TheImaReader_ReadsTextAndBinaryFiles_AndRefusesMalformedOnes()
    {
        // Text: the size, then rows of digits, the top row first (help, "The IMA format").
        var text = ImaFile.Parse(Encoding.ASCII.GetBytes("3\r\n100\r\n020\r\n009\r\n"), "t.ima");
        Assert.False(text.Binary);
        Assert.Equal(3, text.Size);
        Assert.Equal(1, text.Maps[0][0, 0]);
        Assert.Equal(2, text.Maps[0][1, 1]);
        Assert.Equal(9, text.Maps[0][2, 2]);
        // Binary: int16 0, width 2, maps 2, then each map row by row (column fastest).
        var bytes = new byte[] { 0, 0, 2, 0, 2, 0, 10, 20, 30, 40, 250, 0, 0, 255 };
        var binary = ImaFile.Parse(bytes, "b.ima");
        Assert.True(binary.Binary);
        Assert.Equal(2, binary.Maps.Count);
        Assert.Equal(20, binary.Maps[0][0, 1]);
        Assert.Equal(30, binary.Maps[0][1, 0]);
        Assert.Equal(250, binary.Maps[1][0, 0]);
        Assert.Equal(255, binary.Maps[1][1, 1]);
        Assert.Throws<FormatException>(() => ImaFile.Parse(Encoding.ASCII.GetBytes("3\n100\n020\n"), "short.ima"));
        Assert.Throws<FormatException>(() => ImaFile.Parse(Encoding.ASCII.GetBytes("2\n1a\n00\n"), "letter.ima"));
        Assert.Throws<FormatException>(() => ImaFile.Parse(new byte[] { 0, 0, 3, 0, 1, 0, 1, 2 }, "truncated.ima"));
        Assert.Throws<ArgumentException>(() => ImaFile.Read("relative.ima"));
    }

    /// <summary>
    /// A one-pixel source S = 1° wide on the axis of an ideal lens at its focus: a ray of field
    /// (θx, θy) lands at f·(tan θx, tan θy) (OP-27's paraxial surface at infinite conjugates), so
    /// every ray lies in the square of half-width f·tan(S/2), and half the field width in x,
    /// |θx| ≤ S/4, maps to |x| ≤ f·tan(S/4). The x positions in the pixel come from the Sobol
    /// sequence's first dimension (the van der Corput sequence; the y positions its second, also a
    /// (0, 1)-sequence in base 2), whose share of N points in an
    /// interval of length ½ is within (log₂N + 1)/N of ½; 0.01 allows that at N = 4000.
    /// </summary>
    [Fact]
    public void AOnePixelSource_ThroughAnIdealLens_FillsTheSquareItsFieldMapsTo()
    {
        const double f = 0.1;
        var lens = IdealLens(f, 0);
        var system = Sys(0.01, Array.Empty<double>());
        var image = ImaFile.Parse(Encoding.ASCII.GetBytes("1\n5\n"), "dot.ima");
        var spot = ExtendedSourceEnclosedEnergy.Trace(lens, system, new ExtendedSourceRequest
        {
            Image = image, Field = 0, FieldSize = 1, Wavelengths = new[] { 0 }, RaysThousands = 4, Reference = EnclosedEnergyReference.ChiefRay,
        });
        Assert.Equal(4000, spot.Launched);
        Assert.Equal(4000, spot.Points.Count);
        double half = f * Math.Tan(0.5 * Math.PI / 180);
        Close(1, spot.Fraction(EnclosedEnergyType.Ensquared, half * (1 + 1e-9)), 0, "all inside the image of the pixel");
        Assert.True(spot.Fraction(EnclosedEnergyType.Ensquared, half * 0.95) < 1, "the image fills the square");
        Close(0.5, spot.Fraction(EnclosedEnergyType.XOnly, f * Math.Tan(0.25 * Math.PI / 180)), 0.01, "half the field width in x");
        Close(0.5, spot.Fraction(EnclosedEnergyType.YOnly, f * Math.Tan(0.25 * Math.PI / 180)), 0.01, "half the field width in y");
    }

    [Fact]
    public void TheExtendedSource_SharesRaysByPixelValue_AndKeepsEachPixelsLight()
    {
        // Two lit pixels, values 1 and 3, side by side in x: 1000 rays shared 250 : 750, each
        // ray weighing value/count, so each pixel carries its value's share of the light: the
        // left half (x < 0 at the chief ray, an ideal lens at focus) holds 1/4.
        const double f = 0.1;
        var lens = IdealLens(f, 0);
        var system = Sys(0.01, Array.Empty<double>());
        var image = ImaFile.Parse(Encoding.ASCII.GetBytes("2\n13\n00\n"), "two.ima");
        var spot = ExtendedSourceEnclosedEnergy.Trace(lens, system, new ExtendedSourceRequest
        {
            Image = image, Field = 0, FieldSize = 1, Wavelengths = new[] { 0 }, RaysThousands = 1,
        });
        Assert.Equal(1000, spot.Launched);
        double left = spot.Points.Where(p => p.X < 0).Sum(p => p.Weight) / spot.Points.Sum(p => p.Weight);
        Close(0.25, left, 1e-12, "the left pixel's share");
        // The top row is +y: every ray lands at y > 0.
        Assert.All(spot.Points, p => Assert.True(p.Y > 0));
        // Vignetting factors are refused unless removed.
        var vignetted = system with { Fields = system.Fields with { Points = new[] { new FieldPoint { CompressionY = 0.1 } } } };
        Assert.Throws<InvalidOperationException>(() => ExtendedSourceEnclosedEnergy.Trace(lens, vignetted, new ExtendedSourceRequest
        {
            Image = image, Field = 0, FieldSize = 1, Wavelengths = new[] { 0 },
        }));
    }

    // ---- The analyses ----

    [Fact]
    public void TheFourAnalyses_AreRegistered_AndComputeWithTheirAssumptions()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        foreach (var id in new[] { DiffractionEnclosedEnergyAnalysis.SavedId, GeometricEnclosedEnergyAnalysis.SavedId,
                     GeometricLineEdgeSpreadAnalysis.SavedId, ExtendedSourceEnclosedEnergyAnalysis.SavedId })
        {
            Assert.True(registry.TryGet(id, out var analysis));
            Assert.Equal("Enclosed energy", analysis.Category);
        }
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 3.0 }, 0.486, 0.588, 0.656);
        var context = Context(system);

        var diffraction = new DiffractionEnclosedEnergyAnalysis(_ => lens).Compute(context,
            new DiffractionEnclosedEnergySettings { Sampling = 32, ShowDiffractionLimit = true }, default);
        var plot = Assert.IsType<LinePlot>(Assert.Single(diffraction.Plots));
        Assert.Equal(3, plot.Curves.Count); // two fields and the diffraction limit
        Assert.True(plot.Curves[2].Dashed);
        Assert.All(plot.Curves, c => Assert.InRange(c.Y[^1], 0, 1 + 1e-9));
        Assert.Contains(diffraction.Assumptions, a => a.Contains("Diffraction limit"));

        var geometric = new GeometricEnclosedEnergyAnalysis(_ => lens).Compute(context,
            new GeometricEnclosedEnergySettings { Sampling = 16, Field = 2, Reference = EnclosedEnergyReference.Middle, Type = EnclosedEnergyType.Ensquared }, default);
        var gp = Assert.IsType<LinePlot>(Assert.Single(geometric.Plots));
        Close(1, gp.Curves[0].Y[^1], 1e-15, "the default maximum holds every ray");
        Assert.Throws<ArgumentException>(() => new GeometricEnclosedEnergyAnalysis(_ => lens).Compute(context,
            new GeometricEnclosedEnergySettings { Sampling = 16, Type = EnclosedEnergyType.XOnly, MultiplyByDiffractionLimit = true }, default));
        var multiplied = new GeometricEnclosedEnergyAnalysis(_ => lens).Compute(context,
            new GeometricEnclosedEnergySettings { Sampling = 16, Field = 1, MultiplyByDiffractionLimit = true }, default);
        Assert.Contains(multiplied.Assumptions, a => a.Contains("1 − J₀(v)² − J₁(v)²"));

        var spread = new GeometricLineEdgeSpreadAnalysis(_ => lens).Compute(context,
            new GeometricLineEdgeSpreadSettings { Sampling = 16, Field = 2 }, default);
        Assert.Equal(4, Assert.IsType<LinePlot>(Assert.Single(spread.Plots)).Curves.Count);

        string dir = Path.Combine(Path.GetTempPath(), "opensim-op41-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "bar.ima");
            File.WriteAllText(file, "3\n010\n010\n010\n");
            var extended = new ExtendedSourceEnclosedEnergyAnalysis(_ => lens).Compute(context, new ExtendedSourceEnclosedEnergySettings
            {
                File = file, FieldSize = 0.05, RaysThousands = 1, Type = ExtendedSourceType.XDistribution, Field = 1,
            }, default);
            Assert.Contains("FWHM", extended.Text);
            Assert.Contains(extended.Assumptions, a => a.Contains("bar.ima"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
