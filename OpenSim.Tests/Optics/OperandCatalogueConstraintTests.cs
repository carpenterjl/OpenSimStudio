using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Merit.Catalogue;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Diffractive;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-55B: the constraint and system-data operands (OpenSim.Optics/Merit/Catalogue/Constraint*.cs)
/// against the quantity each wraps. Every expected value is derived in the test from the
/// fixture's own inputs (sphere sags s = R − √(R² − r²), spherical caps, the Gaussian beam's
/// closed forms) or read from the engine the operand adapts (the surface placement, the trace's
/// indices, OP-48's Gaussian beam trace). Radii, thicknesses, indices and the test glass are
/// arbitrary inputs, not catalog data. Operand definitions: OpticStudio 2025 R2 help, saved under
/// plans/sources/optics/op-54-help/ and op-55b-help/, cited in the operands' sources.
/// </summary>
public sealed class OperandCatalogueConstraintTests
{
    // ---- Fixture (millimeters) ----

    private const double Mm = 1e-3;

    /// <summary>A synthetic test glass (not a real glass): tabulated n = 1.53 at 0.4 µm and 1.51
    /// at 0.7 µm, printed nd 1.52, Vd 60, ΔPgF 0.002, TCE 7.1, density 2.5 g/cm³.</summary>
    private static readonly OpticalMaterial TestGlass = new()
    {
        Name = "TEST", Catalog = "TESTCAT",
        Tabulated = new TabulatedIndex { Points = new[] { new TabulatedIndexPoint(0.4, 1.53, 0), new TabulatedIndexPoint(0.7, 1.51, 0) } },
        Nd = new PrintedNumber(1.52, "1.52"), Vd = new PrintedNumber(60, "60"), DeltaPgF = 0.002, ThermalExpansion = 7.1, Density = 2.5,
    };

    private static SequentialSurface S(double radiusMm, double thicknessMm, double semiMm, ParaxialMedium? medium = null) => new()
    {
        Curvature = double.IsInfinity(radiusMm) ? 0 : 1 / (radiusMm * Mm), Thickness = thicknessMm * Mm,
        SemiDiameter = semiMm * Mm, Medium = medium ?? ParaxialMedium.Air,
    };

    /// <summary>Surface 1–2: a biconvex singlet R 50 / −50, 5 thick, test glass, clear
    /// semi-diameter 10, mechanical 11 on surface 1, TCE column 23 on surface 1. Air 40. Surface
    /// 3–4: a meniscus R 30 / 60, 4 thick, n = 1.6 constant, semi-diameter 8. Air 46 to the
    /// image (semi-diameter 10).</summary>
    private static SequentialLens Lens() => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            S(50, 5, 10, ParaxialMedium.Of(TestGlass)) with { IsStop = true, MechanicalSemiDiameter = 11 * Mm, ThermalExpansion = 23 },
            S(-50, 40, 10),
            S(30, 4, 8, ParaxialMedium.Constant(1.6)),
            S(60, 46, 8),
            S(double.PositiveInfinity, 0, 10),
        }
    };

    private static OpticalSystemData Sys() => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
        Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint() } },
    };

    /// <summary>The sag of a sphere of radius R at r (mm), derived: c r²/(1 + √(1 − c²r²)).</summary>
    private static double Sag(double radius, double r) => r * r / radius / (1 + Math.Sqrt(1 - r * r / (radius * radius)));

    private static MeritOperandRow R(string code, int int1 = 0, int int2 = 0, double target = 0, params double[] data) =>
        MeritOperandRow.Of(code, int1, int2, target, 1, data);

    private static MeritRowResult Row(IMeritConfigurations configurations, MeritOperandRow row) =>
        MeritEvaluator.Evaluate(new MeritFunction(new[] { row }), configurations).Rows[0];

    private static double V(MeritOperandRow row, SequentialLens? lens = null, OpticalSystemData? system = null)
    {
        var r = Row(new SingleMeritConfiguration(lens ?? Lens(), system ?? Sys()), row);
        Assert.True(r.Status == MeritRowStatus.Evaluated, $"{row.Code}: {r.Error}");
        return r.Value;
    }

    private static string Refused(MeritOperandRow row, SequentialLens? lens = null)
    {
        var r = Row(new SingleMeritConfiguration(lens ?? Lens(), Sys()), row);
        Assert.Equal(MeritRowStatus.Failed, r.Status);
        return r.Error!;
    }

    private static void Close(double expected, double actual, double tolerance, string what = "") =>
        Assert.True(Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected)),
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}).");

    // ---- Lens data ----

    [Fact]
    public void ConicBounds_ReadTheConic()
    {
        var lens = Lens();
        var surfaces = lens.Surfaces.ToArray();
        surfaces[1] = surfaces[1] with { Conic = -0.5 };
        lens = lens with { Surfaces = surfaces };
        Assert.Equal(-0.5, V(R("COGT", 1, target: 0), lens));      // violated: the value is the conic
        Assert.Equal(-1, V(R("COGT", 1, target: -1), lens));       // holds: the target
        Assert.Equal(-0.5, V(R("COLT", 1, target: -1), lens));
        Assert.Equal(0, V(R("COLT", 1, target: 0), lens));
    }

    [Fact]
    public void Diameter_IsTwiceTheModesSemiDiameter()
    {
        Assert.Equal(22, V(R("DMVA", 1, 0)), 12);   // mechanical 11
        Assert.Equal(20, V(R("DMVA", 1, 1)), 12);   // clear 10
        Assert.Equal(22, V(R("DMGT", 1, 0, target: 25)), 12);
        Assert.Equal(15, V(R("DMGT", 1, 0, target: 15)), 12);
        Assert.Equal(22, V(R("DMLT", 1, 0, target: 15)), 12);
    }

    [Fact]
    public void EdgeThickness_OfTheBiconvexSinglet_IsDerived()
    {
        // e(a) = 5 + s(−50, a) − s(50, a) = 5 − 2 s(50, a).
        double clear = 5 - 2 * Sag(50, 10), mech = 5 - 2 * Sag(50, 11);
        for (int code = 0; code <= 3; code++)
        {
            Close(clear, V(R("ETVA", 1, code, 0, 1)), 1e-12, $"ETVA code {code}");
            Close(mech, V(R("ETVA", 1, code, 0, 0)), 1e-12, $"ETVA mech code {code}");
        }
        Close(clear, V(R("ETGT", 1, 0, 10, 1)), 1e-12, "ETGT violated");
        Assert.Equal(1, V(R("ETGT", 1, 0, 1, 1)), 12);
        Close(clear, V(R("ETLT", 1, 0, 1, 1)), 1e-12, "ETLT violated");
    }

    [Fact]
    public void FullThickness_IsTheExtremeOverTheZone()
    {
        // Biconvex: the least thickness is at the edge, the greatest at the vertex.
        double edge = 5 - 2 * Sag(50, 10);
        Close(edge, V(R("FTGT", 1, 1, target: 10)), 1e-12, "FTGT");
        Close(5, V(R("FTLT", 1, 1, target: 1)), 1e-12, "FTLT");
    }

    [Fact]
    public void BlankThickness_OfTheMeniscus_IsDerived()
    {
        // Meniscus R 30 / 60, 4 thick, a = 8: the back face's greatest z is 4 + s(60, 8) at the
        // edge, the front face's least z is 0 at the vertex.
        Close(4 + Sag(60, 8), V(R("BLTH", 3, 0, 0, 1)), 1e-12, "BLTH +y");
        Close(4 + Sag(60, 8), V(R("BLTH", 3, 4, 0, 1)), 1e-12, "BLTH all axes");
        // Biconvex: the vertex thickness.
        Close(5, V(R("BLTH", 1, 0, 0, 1)), 1e-12, "BLTH biconvex");
    }

    [Fact]
    public void CurvatureRange_SumsTheViolations()
    {
        // Curvatures (1/mm) of surfaces 1–4: 1/50, −1/50, 1/30, 1/60.
        double t = 0.02;
        Close(t - ((t + 0.02) + (t - 1.0 / 60)), V(R("MNCV", 1, 4, t)), 1e-13, "MNCV");
        Close(t + (1.0 / 30 - t), V(R("MXCV", 1, 4, t)), 1e-13, "MXCV");
    }

    /// <summary>The +y edge thicknesses (clear semi-diameters) of surfaces 1–4, derived.</summary>
    private static double[] Edges() => new[]
    {
        5 - 2 * Sag(50, 10),                 // glass
        40 + Sag(30, 10) + Sag(50, 10),      // air: s(−50, 10) = −s(50, 10)
        4 + Sag(60, 8) - Sag(30, 8),         // glass
        46 - Sag(60, 8),                     // air, to the plane image
    };

    [Theory]
    [InlineData("MNET", "XNET")]
    [InlineData("MXET", "XXET")]
    [InlineData("MXEA", "XXEA")]
    [InlineData("MXEG", "XXEG")]
    public void EdgeRanges_AlongY_AndRoundThePerimeter_MatchTheDerivedEdges(string yCode, string perimeterCode)
    {
        var e = Edges();
        bool atLeast = yCode.StartsWith("MN");
        double target = atLeast ? 100 : 0;
        int[] which = yCode switch { "MXEA" => new[] { 1, 3 }, "MXEG" => new[] { 0, 2 }, _ => new[] { 0, 1, 2, 3 } };
        double expected = atLeast ? target - which.Sum(i => target - e[i]) : target + which.Sum(i => e[i]);
        Close(expected, V(R(yCode, 1, 4, target, 0, 1)), 1e-12, yCode);
        // Rotationally symmetric surfaces: every perimeter point has the +y edge thickness.
        Close(expected, V(R(perimeterCode, 1, 4, target, 0, 1)), 1e-12, perimeterCode);
    }

    [Fact]
    public void MinimumPerimeterEdges_AirAndGlass_AndZone()
    {
        var e = Edges();
        Close(100 - (100 - e[1]) - (100 - e[3]), V(R("XNEA", 1, 4, 100, 0, 1)), 1e-12, "XNEA");
        Close(100 - (100 - e[0]) - (100 - e[2]), V(R("XNEG", 1, 4, 100, 0, 1)), 1e-12, "XNEG");
        // Zone 0.5 on the singlet: e = 5 − 2 s(50, 5).
        Close(5 - 2 * Sag(50, 5), V(R("MXEG", 1, 1, 0, 0.5, 1)), 1e-12, "MXEG zone");
    }

    [Fact]
    public void SemiDiameterRanges()
    {
        // Clear 10, 10, 8, 8; mechanical 11, then clear + chip zone 0: 10, 8, 8.
        Assert.Equal(9 - 2, V(R("MNSD", 1, 4, 9)), 12);
        Assert.Equal(9 + 2, V(R("MXSD", 1, 4, 9)), 12);
        Assert.Equal(10.5 - (0.5 + 2.5 + 2.5), V(R("OMMI", 1, 4, 10.5)), 12);
        Assert.Equal(10.5 + 0.5, V(R("OMMX", 1, 4, 10.5)), 12);
        Assert.Equal(11, V(R("OMSD", 1)), 12);
        Assert.Equal(10, V(R("OMSD", 2)), 12);
    }

    [Fact]
    public void GlassThicknessSum_CountsTheGlassSpaces()
    {
        Assert.Equal(5 + 4, V(R("TGTH", 1, 4)), 12);
        Assert.Equal(5, V(R("TGTH", 1, 2)), 12);
    }

    [Fact]
    public void TotalThickness_IsTheAxialExtent()
    {
        // Biconvex: the vertices bound it, 5. Meniscus: from the front vertex to the back edge.
        Close(5, V(R("TTVA", 1, 0)), 1e-12, "TTVA biconvex");
        Close(4 + Sag(60, 8), V(R("TTVA", 3, 1)), 1e-12, "TTVA meniscus");
        Close(5, V(R("TTGT", 1, 0, 6)), 1e-12, "TTGT violated");
        Assert.Equal(6, V(R("TTLT", 1, 0, 6)), 12);
    }

    [Fact]
    public void ZoomThickness_IsTheSpreadOverConfigurations()
    {
        var a = Lens();
        var surfaces = a.Surfaces.ToArray();
        surfaces[2] = surfaces[2] with { Thickness = 42 * Mm };
        var configs = new Configs(new[] { a, a with { Surfaces = surfaces } });
        // TTHI 1..2: 45 and 47 → spread 2.
        var r = Row(configs, R("ZTHI", 1, 2, 0));
        Assert.True(r.Status == MeritRowStatus.Evaluated, r.Error);
        Assert.Equal(2, r.Value, 12);
        Assert.Equal(3, Row(configs, R("ZTHI", 1, 2, 3)).Value, 12);
        Assert.Equal(0.5, V(R("ZTHI", 1, 2, 0.5)), 12);   // one configuration: no spread
    }

    // ---- Lens properties ----

    [Fact]
    public void CylinderVolume_IsPiRSquaredTimesTheVertexExtent()
    {
        // Vertices at 0, 5, 45, 49; largest clear semi-diameter 10, mechanical 11.
        Close(Math.PI * 100 * 49, V(R("CVOL", 1, 4, 0, 1)), 1e-12, "CVOL clear");
        Close(Math.PI * 121 * 49, V(R("CVOL", 1, 4, 0, 0)), 1e-12, "CVOL mechanical");
    }

    [Fact]
    public void DiameterToThickness_RatiosOfTheGlassElements()
    {
        // Glass after surfaces 1 (20/5 = 4) and 3 (16/4 = 4).
        Assert.Equal(5 - 2, V(R("MNDT", 1, 4, 5, 1)), 12);
        Assert.Equal(3 + 2, V(R("MXDT", 1, 4, 3, 1)), 12);
    }

    [Fact]
    public void SagAtTheEdge_AndAtAPoint()
    {
        Close(Sag(50, 10), V(R("SAGX", 1)), 1e-12, "SAGX");
        Close(Sag(50, 10), V(R("SAGY", 1)), 1e-12, "SAGY");
        Close(-Sag(50, 10), V(R("SAGY", 2)), 1e-12, "SAGY back");
        Close(Sag(50, 5), V(R("SSAG", 1, 1, 0, 3, 4)), 1e-12, "SSAG mode 1");
        Close(Sag(50, 5), V(R("SSAG", 1, 2, 0, 0.3, 0.4)), 1e-12, "SSAG mode 2");
        Close(Sag(50, 10), V(R("SSAG", 1, 0, 0, 12, 0)), 1e-12, "SSAG mode 0 flat edge");
        Close(Sag(50, 12), V(R("SSAG", 1, 1, 0, 12, 0)), 1e-12, "SSAG mode 1 beyond the edge");
        Assert.Equal(0, V(R("SSAG", 1, 1, 0, 3, 4, 0, 1)), 12);   // the vertex sphere removed from a sphere
    }

    /// <summary>A paraboloid (conic −1) of vertex radius 50 on surface 1: z = r²/100.</summary>
    private static SequentialLens Paraboloid()
    {
        var lens = Lens();
        var surfaces = lens.Surfaces.ToArray();
        surfaces[1] = surfaces[1] with { Conic = -1 };
        return lens with { Surfaces = surfaces };
    }

    [Fact]
    public void SagSlopeCurvature_OfAParaboloid_WithTheVertexSphereRemoved()
    {
        var p = Paraboloid();
        double r = 5;
        Close(r * r / 100 - Sag(50, r), V(R("SSAG", 1, 1, 0, 3, 4, 0, 1), p), 1e-12, "SSAG remove 1");
        // Slope dz/dr = r/50 tangentially; sphere's r/√(2500 − r²).
        Close(r / 50, V(R("SSLP", 1, 1, 0, 3, 4, 0, 0, 0, 0), p), 1e-12, "SSLP tangential");
        Close(0.6 * r / 50, V(R("SSLP", 1, 1, 0, 3, 4, 0, 0, 0, 2), p), 1e-12, "SSLP x");
        Close(0, V(R("SSLP", 1, 1, 0, 3, 4, 0, 0, 0, 1), p), 1e-12, "SSLP sagittal");
        Close(r / 50 - r / Math.Sqrt(2500 - r * r), V(R("SSLP", 1, 1, 0, 3, 4, 0, 1, 0, 0), p), 1e-12, "SSLP remove 1");
        // Normal curvatures of z = r²/100 (z′ = r/50, z″ = 1/50): tangential z″/(1 + z′²)^1.5,
        // sagittal z′/(r √(1 + z′²)) (Monge patch, derived).
        double w2 = 1 + r * r / 2500;
        Close(1.0 / 50 / Math.Pow(w2, 1.5), V(R("SCRV", 1, 1, 0, 3, 4, 0, 0, 0, 0), p), 1e-12, "SCRV tangential");
        Close(1.0 / 50 / Math.Sqrt(w2), V(R("SCRV", 1, 1, 0, 3, 4, 0, 0, 0, 1), p), 1e-12, "SCRV sagittal");
        // A sphere's normal curvature is 1/R in every direction.
        Close(0.02, V(R("SCRV", 1, 1, 0, 3, 4, 0, 0, 0, 3)), 1e-12, "SCRV sphere");
    }

    [Fact]
    public void SurfaceCurvatureAndDerivatives()
    {
        // Sphere R 50 at (3, 4), r = 5.
        Close(0.02, V(R("SCUR", 1, 0, 0, 3, 4)), 1e-12, "SCUR tangential");
        Close(0.02, V(R("SCUR", 1, 1, 0, 3, 4)), 1e-12, "SCUR sagittal");
        Close(0, V(R("SCUR", 1, 2, 0, 3, 4)), 1e-12, "SCUR T − S");
        Close(0.02, V(R("SCUR", 1, 4, 0, 3, 4)), 1e-12, "SCUR x");
        Close(5 * 0.02, V(R("SCUR", 1, 8, 0, 3, 4)), 1e-12, "SCUR |R·Sc|");
        Close(5 * 0.02, V(R("SCUR", 1, 9, 0, 3, 4)), 1e-12, "SCUR max |R·Sc| (at the far end)");
        // Paraboloid: the largest |T − S| over the 50 points is at (3, 4) itself (it grows with r).
        double w2 = 1 + 25.0 / 2500, ts = 1.0 / 50 / Math.Pow(w2, 1.5) - 1.0 / 50 / Math.Sqrt(w2);
        Close(Math.Abs(ts), V(R("SCUR", 1, 3, 0, 3, 4), Paraboloid()), 1e-12, "SCUR max |T − S|");
        // z(r) = 50 − √(2500 − r²): z′ = r/√(2500 − r²), z″ = 2500/(2500 − r²)^1.5, z′/r = 1/√(2500 − r²).
        Close(5 / Math.Sqrt(2475), V(R("SDRV", 1, 0, 0, 3, 4)), 1e-12, "SDRV first tangential");
        Close(0, V(R("SDRV", 1, 1, 0, 3, 4)), 1e-12, "SDRV first sagittal");
        Close(2500 / Math.Pow(2475, 1.5), V(R("SDRV", 1, 2, 0, 3, 4)), 1e-12, "SDRV second tangential");
        Close(1 / Math.Sqrt(2475), V(R("SDRV", 1, 3, 0, 3, 4)), 1e-12, "SDRV second sagittal");
    }

    [Fact]
    public void ThicknessAtAPoint()
    {
        Close(5 - 2 * Sag(50, 6), V(R("STHI", 1, 1, 0, 0, 6)), 1e-12, "STHI");
        Close(5 - 2 * Sag(50, 10), V(R("STHI", 1, 1, 0, 0, 15)), 1e-12, "STHI flat beyond the clear edge");
    }

    [Fact]
    public void VolumeAndMass_OfTheSinglet_AreTheCylinderPlusTwoCaps()
    {
        // Biconvex R ±50, a = 10, t = 5: a cylinder of the edge thickness t − 2s plus two
        // spherical caps π s² (R − s/3) (derived), mm³ → cm³ by 10⁻³.
        double s = Sag(50, 10);
        double cc = (Math.PI * 100 * (5 - 2 * s) + 2 * Math.PI * s * s * (50 - s / 3)) * 1e-3;
        Close(cc, V(R("VOLU", 1, 1)), 1e-12, "VOLU");
        Close(cc * 2.5, V(R("TMAS", 1, 2, 0, 1)), 1e-12, "TMAS");
        Assert.Contains("density", Refused(R("TMAS", 3, 3, 0, 1)));   // n = 1.6 constant: no catalog density
    }

    [Fact]
    public void SurfaceNormal_AndNormalDistance()
    {
        // Sphere R 50 at (3, 4): n = (−x, −y, √(R² − r²))/R.
        Close(-3.0 / 50, V(R("NORX", 1, 0, 0, 3, 4)), 1e-12, "NORX");
        Close(-4.0 / 50, V(R("NORY", 1, 0, 0, 3, 4)), 1e-12, "NORY");
        Close(Math.Sqrt(2475) / 50, V(R("NORZ", 1, 0, 0, 3, 4)), 1e-12, "NORZ");
        Close(-4.0 / 50, V(R("NORY", 1, 1, 0, 3, 4)), 1e-12, "NORY global (untilted)");
        Close(5, V(R("NORD", 1, 0, 0, 0, 0)), 1e-12, "NORD on axis");
        // The normal line passes through surface 1's centre C1 = (0, 0, 50); surface 2 is the
        // sphere of radius 50 about C2 = (0, 0, −45). With P1 = C1 − 50 n, the line is
        // C1 + u n, u = s − 50; |C1 − C2 + u n| = 50 gives u² + 190 n_z u + 6525 = 0, near root
        // u = −95 n_z + √(9025 n_z² − 6525) (derived).
        double nz = Math.Sqrt(2475) / 50;
        Close(50 - 95 * nz + Math.Sqrt(9025 * nz * nz - 6525), V(R("NORD", 1, 0, 0, 3, 4)), 1e-12, "NORD off axis");
    }

    /// <summary>A plane Binary Optic 2 (order 1, A₁ = 100 rad on ρ², normalisation radius 10 mm)
    /// as surface 1, clear semi-diameter 10, then 100 mm to the image.</summary>
    private static SequentialLens Binary() => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            new SequentialSurface
            {
                Thickness = 100 * Mm, SemiDiameter = 10 * Mm, IsStop = true,
                Shape = new BinaryOptic2(1, Array.Empty<double>(), 10 * Mm, new[] { 100.0 }),
            },
            new SequentialSurface { SemiDiameter = 10 * Mm },
        }
    };

    [Fact]
    public void PhaseAndPhaseSlope_OfABinaryOptic2()
    {
        // Φ = M A₁ ρ², ρ = r/10: at r = 5, Φ = 25 rad; dΦ/dr = 2 A₁ r/100 = 10 rad/mm (derived).
        Close(25 / (2 * Math.PI), V(R("SPHS", 1, 1, 0, 3, 4), Binary()), 1e-12, "SPHS");
        Close(25 / (2 * Math.PI), V(R("SPHS", 1, 2, 0, 0.3, 0.4), Binary()), 1e-12, "SPHS mode 2");
        Close(10 / (2 * Math.PI), V(R("PSLP", 1, 1, 0, 3, 4, 0, 0), Binary()), 1e-12, "PSLP tangential");
        Close(0.8 * 10 / (2 * Math.PI), V(R("PSLP", 1, 1, 0, 3, 4, 0, 3), Binary()), 1e-12, "PSLP y");
        Close(0, V(R("PSLP", 1, 1, 0, 3, 4, 0, 1), Binary()), 1e-12, "PSLP sagittal");
        Assert.Contains("not a phase surface", Refused(R("SPHS", 1, 1, 0, 3, 4)));
        Assert.Contains("not built", Refused(R("SPHS", 1, 1, 0, 3, 4, 0, 2), Binary()));
    }

    [Fact]
    public void PointOperands_RefuseWhatIsNotBuilt_ByName()
    {
        Assert.Contains("Off-axis", Refused(R("SSAG", 1, 1, 0, 3, 4, 1)));
        Assert.Contains("Base", Refused(R("SSAG", 1, 1, 0, 3, 4, 0, 3)));
        Assert.Contains("Composite", Refused(R("SSLP", 1, 1, 0, 3, 4, 0, 4)));
        Assert.Contains("Mode (Int2)", Refused(R("DMVA", 1, 2)));
    }

    // ---- Parameter, glass, positions, system ----

    /// <summary>Surface 1 a coordinate break (decenter x 2 mm, tilt about x 10°), surface 2 a
    /// plane stop, 10 mm to the image.</summary>
    private static SequentialLens Broken() => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            new SequentialSurface { CoordinateBreak = new CoordinateBreak(new TiltDecenter(2 * Mm, 0, 10, 0, 0)) },
            new SequentialSurface { Thickness = 10 * Mm, SemiDiameter = 5 * Mm, IsStop = true },
            new SequentialSurface { SemiDiameter = 5 * Mm },
        }
    };

    [Fact]
    public void ParameterOperands_ReadTheEditorsValue()
    {
        Assert.Equal(2, V(R("PMVA", 1, 1), Broken()), 12);    // decenter x, lens units
        Assert.Equal(10, V(R("PMVA", 1, 3), Broken()), 12);   // tilt about x, degrees
        Assert.Equal(2, V(R("PMGT", 1, 1, 3), Broken()), 12);
        Assert.Equal(2, V(R("PMLT", 1, 1, 1), Broken()), 12);   // 2 > 1: violated, the value
        Assert.Equal(3, V(R("PMLT", 1, 1, 3), Broken()), 12);
        Assert.Contains("no parameters", Refused(R("PMVA", 1, 1)));   // a Standard surface
    }

    [Fact]
    public void GlassOperands_ReadTheGlassMap()
    {
        Assert.Equal(7.1, V(R("GTCE", 1)), 12);
        var trace = SequentialTrace.Create(Lens(), Sys());
        Close(trace.Indices[1], V(R("INDX", 1, 1)), 1e-12, "INDX against the trace's index");
        Close(1.6, V(R("INDX", 3, 1)), 1e-14, "INDX constant");
        // Only surface 1's medium is a glass (Nd 1.52, Vd 60, ΔPgF 0.002); constants are skipped.
        Assert.Equal(70 - 10, V(R("MNAB", 1, 4, 70)), 12);
        Assert.Equal(50 + 10, V(R("MXAB", 1, 4, 50)), 12);
        Close(1.6 - 0.08, V(R("MNIN", 1, 4, 1.6)), 1e-14, "MNIN");
        Close(1.52, V(R("MXIN", 1, 4, 1.5)), 1e-14, "MXIN");
        Close(0.002, V(R("MNPD", 1, 4, 0.005)), 1e-14, "MNPD");
        Close(0.002, V(R("MXPD", 1, 4, 0)), 1e-14, "MXPD");
        Assert.Contains("no glass", Refused(R("MNAB", 3, 4, 50)));
    }

    [Fact]
    public void GlobalCoordinates_MatchTheSurfacePlacement()
    {
        Assert.Equal(45, V(R("GLCZ", 3)), 12);
        Assert.Equal(0, V(R("GLCX", 3)), 12);
        Assert.Equal(1, V(R("GLCC", 3)), 12);
        // A decentered, tilted surface against OP-21's placement (the Global vertex report's engine).
        var lens = Broken();
        var t = SurfacePlacement.Compute(lens).RelativeTo(2, 1);
        Close(t.Translation.X / Mm, V(R("GLCX", 2), lens), 1e-12, "GLCX");
        Close(t.Translation.Y / Mm, V(R("GLCY", 2), lens), 1e-12, "GLCY");
        Close(t.Translation.Z / Mm, V(R("GLCZ", 2), lens), 1e-12, "GLCZ");
        Close(t.Col2.Y, V(R("GLCB", 2), lens), 1e-14, "GLCB");
        Close(t.Col2.Z, V(R("GLCC", 2), lens), 1e-14, "GLCC");
        Close(t.Col1.Z, V(R("GLCR", 2, 8), lens), 1e-14, "GLCR R32");
        Close(t.Col2.Y, V(R("GLCR", 2, 6), lens), 1e-14, "GLCR R23");
    }

    [Fact]
    public void Wavelength_IsTheTablesValue()
    {
        var sys = Sys() with { Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 0.486 }, new Wavelength { Micrometers = 0.587 } } } };
        Assert.Equal(0.587, V(R("WLEN", 0, 2), system: sys));
        Assert.Contains("not a wavelength", Refused(R("WLEN", 0, 3)));
    }

    [Fact]
    public void MultiConfigurationOperands_ReadTheTable()
    {
        var configs = new Configs(new[] { Lens(), Lens() }) { Table = new double[,] { { 3, 4 }, { 7, 9 } } };
        Assert.Equal(9, Row(configs, R("MCOV", 2, 2)).Value, 12);
        Assert.Equal(4, Row(configs, R("MCOG", 1, 2, 5)).Value, 12);   // 4 < 5: violated, the value
        Assert.Equal(3, Row(configs, R("MCOG", 1, 2, 3)).Value, 12);   // holds: the target
        Assert.Equal(4, Row(configs, R("MCOL", 1, 2, 3)).Value, 12);
        var single = Row(new SingleMeritConfiguration(Lens(), Sys()), R("MCOV", 1, 1));
        Assert.Equal(MeritRowStatus.Failed, single.Status);
        Assert.Contains("multi-configuration table", single.Error);
    }

    // ---- Gaussian beam ----

    [Fact]
    public void ParaxialGaussianBeam_InObjectSpace_IsTheClosedForm()
    {
        // W0 = 0.5 mm at 10 mm after surface 1, λ = 0.55 µm, M² = 1: in object space the beam
        // reaches surface 1 at z = −10 mm from its waist; z_R = π W0²/λ, w = W0 √(1 + (z/z_R)²),
        // R = z (1 + (z_R/z)²), θ = λ/(π W0) (the Gaussian beam's closed forms, derived from
        // q = z + i z_R).
        double w0 = 0.5, z = -10, lambda = 0.55e-3, zr = Math.PI * w0 * w0 / lambda;
        double[] d = { 1, 0.5, 10, 1 };
        Close(w0, V(R("GBPW", 0, 1, 0, d)), 1e-12, "GBPW");
        Close(zr, V(R("GBPZ", 0, 1, 0, d)), 1e-12, "GBPZ");
        Close(z, V(R("GBPP", 0, 1, 0, d)), 1e-12, "GBPP");
        Close(w0 * Math.Sqrt(1 + z * z / (zr * zr)), V(R("GBPS", 0, 1, 0, d)), 1e-12, "GBPS");
        Close(z * (1 + zr * zr / (z * z)), V(R("GBPR", 0, 1, 0, d)), 1e-10, "GBPR");
        Close(lambda / (Math.PI * w0), V(R("GBPD", 0, 1, 0, d)), 1e-12, "GBPD");
        // M² = 4: the real waist is M·W0 (W0 is the embedded waist, help: Paraxial Gaussian Beam).
        Close(2 * w0, V(R("GBPW", 0, 1, 0, 1, 0.5, 10, 4)), 1e-12, "GBPW M² 4");
        // M2 Factor 0 (an empty column) is read as 1.
        Close(w0, V(R("GBPW", 0, 1, 0, 0, 0.5, 10, 0)), 1e-12, "GBPW M² 0");
    }

    [Fact]
    public void ParaxialGaussianBeam_AfterTheLens_MatchesTheBeamTrace()
    {
        var lens = Lens();
        var model = ParaxialSystem.Create(lens.ToParaxialLens(), Sys(), 0);
        var trace = GaussianBeamTrace.Create(model, GaussianBeamInput.Round(0.5 * Mm, -20 * Mm, 1.5));
        // W0 is embedded: the real waist 0.5 mm with M² 1.5 is an embedded W0 = 0.5/√1.5.
        double embedded = 0.5 / Math.Sqrt(1.5);
        double[] d = { 0, embedded, -20, 1.5 };
        for (int j = 1; j <= 4; j++)
        {
            var st = trace.Y[j];
            Close(st.BeamRadius / Mm, V(R("GBPS", j, 1, 0, d)), 1e-12, $"GBPS {j}");
            Close(st.WaistRadius / Mm, V(R("GBPW", j, 1, 0, d)), 1e-12, $"GBPW {j}");
            Close(st.DistanceFromWaist / Mm, V(R("GBPP", j, 1, 0, d)), 1e-12, $"GBPP {j}");
            Close(st.RayleighRange / Mm, V(R("GBPZ", j, 1, 0, d)), 1e-12, $"GBPZ {j}");
            Close(st.Divergence, V(R("GBPD", j, 1, 0, d)), 1e-12, $"GBPD {j}");
        }
    }

    // ---- TCE ----

    [Fact]
    public void TceOperands_ReadTheTceColumn()
    {
        Assert.Equal(23, V(R("TCVA", 1)), 12);
        Assert.Equal(23, V(R("TCGT", 1, 0, 30)), 12);
        Assert.Equal(20, V(R("TCGT", 1, 0, 20)), 12);
        Assert.Equal(23, V(R("TCLT", 1, 0, 20)), 12);
        Assert.Contains("TCVA", Refused(R("GTCE", 3)));   // a constant index: no catalog glass
    }

    /// <summary>Configurations given as lenses, with a multi-configuration table of numbers.</summary>
    private sealed class Configs : IMeritConfigurations
    {
        private readonly SequentialLens[] _lenses;

        public Configs(SequentialLens[] lenses) => _lenses = lenses;

        public double[,]? Table { get; init; }

        public int Count => _lenses.Length;

        public SequentialLens LensFor(int configuration) => _lenses[configuration - 1];

        public OpticalSystemData SystemFor(int configuration) => Sys();

        public double MultiConfigValue(int operand, int configuration) =>
            Table is { } t ? t[operand - 1, configuration - 1] : throw new ArgumentException("no table");
    }
}
