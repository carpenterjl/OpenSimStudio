using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-44: the reports (docs/Optics-Conventions.md §29). Every expected value is derived in the
/// test: element volumes from the spherical-cap volume π h²(R − h/3) and ∫ 2πr·α r⁴ dr, cardinal
/// points from the thick-lens relations the conjugate equation gives in air and in an immersed
/// image space, and the round trip from the report's own text. Radii, thicknesses, indices and
/// the fixture glass's density (2.5 g/cc) are arbitrary test inputs, not reference data.
/// </summary>
public sealed class ReportTests
{
    private const double Mm = 1e-3;

    private static SequentialSurface Sphere(double radiusMm, double thicknessMm, ParaxialMedium? medium = null, double semiMm = 0, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radiusMm * Mm),
        Thickness = thicknessMm * Mm,
        Medium = medium ?? ParaxialMedium.Air,
        SemiDiameter = semiMm > 0 ? semiMm * Mm : null,
        IsStop = stop,
    };

    private static readonly OpticalMaterial Glass = new()
    {
        Name = "TG1", Catalog = "TESTCAT", Density = 2.5,
        Tabulated = new TabulatedIndex { Points = new[] { new TabulatedIndexPoint(0.4, 1.6, 0), new TabulatedIndexPoint(0.8, 1.6, 0) } },
    };

    private static readonly OpticalCatalog Catalog = new() { Name = "TESTCAT", Materials = new[] { Glass } };

    private static OpticsAnalysisContext Context(string name = "Report") =>
        new(name, new OpticsSettings()) { Catalogs = new[] { Catalog } };

    private static void Close(double expected, double actual, double relative, string what) =>
        Assert.True(Math.Abs(actual - expected) <= relative * Math.Max(Math.Abs(expected), 1e-300),
            $"{what}: {actual:R} is not within {relative:G3} of {expected:R}.");

    /// <summary>The spherical cap of a sphere of radius R cut at height h: π h²(R − h/3).</summary>
    private static double Cap(double radius, double h) => Math.PI * h * h * (radius - h / 3);

    // ---- Element volume (the gate) ----

    /// <summary>
    /// The gate: a biconvex lens with both faces at one semi-diameter a is a cylinder of the edge
    /// thickness plus a spherical cap on each side (derived: h = R − √(R² − a²) is each face's
    /// sag at a, the edge thickness is t − h₁ − h₂).
    /// </summary>
    [Fact]
    public void ElementVolume_OfABiconvexLens_IsTheCylinderPlusTwoSphericalCaps()
    {
        double r1 = 50 * Mm, r2 = 80 * Mm, t = 8 * Mm, a = 12 * Mm;
        var front = Sphere(50, 8);
        var back = Sphere(-80, 0);
        double h1 = r1 - Math.Sqrt(r1 * r1 - a * a), h2 = r2 - Math.Sqrt(r2 * r2 - a * a);
        double expected = Math.PI * a * a * (t - h1 - h2) + Cap(r1, h1) + Cap(r2, h2);
        var v = ElementVolume.Compute(front, a, back, a, t, "test");
        Assert.True(v.Exact);
        Close(expected, v.CubicMeters, 1e-13, "biconvex volume");
        Close(expected * 1e6, v.CubicCentimeters, 1e-13, "volume in cc");
    }

    /// <summary>
    /// Unequal semi-diameters: the smaller (back) face is squared up, a plane annulus at its edge
    /// sag out to the front's semi-diameter. Derived from the caps: the glass is the cylinder of
    /// radius a₁ and length t, less the front cap's complement π a₁² h₁ − cap₁, less the back's
    /// complement π a₂² h₂ − cap₂, less the annulus π(a₁² − a₂²)·h₂.
    /// </summary>
    [Fact]
    public void ElementVolume_SquaresUpTheSmallerFace()
    {
        double r1 = 50 * Mm, r2 = 80 * Mm, t = 8 * Mm, a1 = 12 * Mm, a2 = 9 * Mm;
        double h1 = r1 - Math.Sqrt(r1 * r1 - a1 * a1), h2 = r2 - Math.Sqrt(r2 * r2 - a2 * a2);
        double expected = Math.PI * a1 * a1 * t
                          - (Math.PI * a1 * a1 * h1 - Cap(r1, h1))
                          - (Math.PI * a2 * a2 * h2 - Cap(r2, h2))
                          - Math.PI * (a1 * a1 - a2 * a2) * h2;
        var v = ElementVolume.Compute(Sphere(50, 8), a1, Sphere(-80, 0), a2, t, "test");
        Close(expected, v.CubicMeters, 1e-13, "squared-up volume");
    }

    /// <summary>An Even Asphere with no coefficients is the sphere, integrated numerically; on a
    /// plane, α₂r⁴ adds ∫₀^a 2πr·α₂r⁴ dr = π α₂ a⁶/3 of sag volume (derived).</summary>
    [Fact]
    public void ElementVolume_OfOtherShapes_IsNumericalAndAgreesWithClosedForms()
    {
        double a = 12 * Mm, t = 8 * Mm;
        var sphere = ElementVolume.Compute(Sphere(50, 8), a, Sphere(-80, 0), a, t, "sphere");
        var asphere = ElementVolume.Compute(Sphere(50, 8) with { Shape = new EvenAsphere(Array.Empty<double>()) }, a,
            Sphere(-80, 0) with { Shape = new EvenAsphere(Array.Empty<double>()) }, a, t, "asphere");
        Assert.False(asphere.Exact);
        Assert.Contains("Gauss–Legendre", asphere.Method);
        Close(sphere.CubicMeters, asphere.CubicMeters, 1e-12, "zero-coefficient asphere against the sphere");

        double alpha2 = 2e3;   // m^-3: a sag of α₂a⁴ ≈ 41 µm at a = 12 mm.
        var plate = ElementVolume.Compute(new SequentialSurface { Shape = new EvenAsphere(new[] { 0, alpha2 }) }, a,
            new SequentialSurface(), a, t, "plate");
        Close(Math.PI * a * a * t - Math.PI * alpha2 * Math.Pow(a, 6) / 3, plate.CubicMeters, 1e-13, "r⁴ asphere on a plate");
    }

    /// <summary>A sag b·x² (not rotationally symmetric) integrates to b·π a⁴/4 over the disc
    /// (derived: ∫∫ r² cos²θ r dr dθ = π a⁴/4), through the r × θ rule.</summary>
    [Fact]
    public void ElementVolume_OfANonRotationalShape_UsesTheTwoDimensionalRule()
    {
        double a = 10 * Mm, t = 5 * Mm, b = 1.5;   // b in 1/m: a sag of 0.15 mm at the rim.
        var cylinder = new SequentialSurface { Shape = new QuadraticX(b) };
        var v = ElementVolume.Compute(cylinder, a, new SequentialSurface(), a, t, "cylinder");
        Assert.Contains("trapezoid", v.Method);
        Close(Math.PI * a * a * t - b * Math.PI * Math.Pow(a, 4) / 4, v.CubicMeters, 1e-13, "x² sag");
    }

    private sealed class QuadraticX(double b) : SurfaceShape
    {
        public override string TypeName => "Test x²";
        public override double Sag(double x, double y, SequentialSurface surface) => b * x * x;
        public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface, out Vector3D point, out double distance) =>
            throw new NotSupportedException();
        public override Vector3D Normal(Vector3D point, SequentialSurface surface) => throw new NotSupportedException();
    }

    [Fact]
    public void ElementVolume_RefusesCrossingFaces()
    {
        // A 1 mm thick biconvex lens at a = 12 mm: the faces cross before the edge.
        var ex = Assert.Throws<InvalidOperationException>(() => ElementVolume.Compute(Sphere(50, 1), 12 * Mm, Sphere(-80, 0), 12 * Mm, 1 * Mm, "Element 1–2"));
        Assert.Contains("Element 1–2", ex.Message);
        Assert.Contains("cross", ex.Message);
    }

    /// <summary>The prescription's element volume and mass are the gate's closed form and the
    /// fixture density times it.</summary>
    [Fact]
    public void Prescription_ListsTheElementVolumeAndMass()
    {
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                Sphere(50, 8, ParaxialMedium.Of(Glass), 12, stop: true),
                Sphere(-80, 60, semiMm: 12),
                new SequentialSurface(),
            }
        };
        double r1 = 50 * Mm, r2 = 80 * Mm, t = 8 * Mm, a = 12 * Mm;
        double h1 = r1 - Math.Sqrt(r1 * r1 - a * a), h2 = r2 - Math.Sqrt(r2 * r2 - a * a);
        double cc = (Math.PI * a * a * (t - h1 - h2) + Cap(r1, h1) + Cap(r2, h2)) * 1e6;
        var result = new PrescriptionDataAnalysis(_ => lens).Compute(Context(), new PrescriptionDataSettings(), default);
        var section = result.Sections.Single(s => s.Heading == "Element volume");
        var row = Assert.Single(section.TableRows);
        Assert.Equal("1–2", row[0]);
        Close(cc, double.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture), 1e-7, "listed volume (g8)");
        Close(cc * 2.5, double.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture), 1e-7, "listed mass (g8)");
        Assert.Contains("sphere (exact)", row[5]);
    }

    // ---- The prescription read back (the gate) ----

    /// <summary>A lens with a tilted element, an asphere, a coordinate break, an aperture, a
    /// coating and a comment holding the separator and quotes.</summary>
    private static SequentialLens TiltedAsphereLens() => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            new SequentialSurface { Thickness = 5 * Mm, IsStop = true, SemiDiameter = 8 * Mm, Comment = "stop" },
            Sphere(40, 6, ParaxialMedium.Of(Glass), 10) with
            {
                TiltBefore = new TiltDecenter(0, 0.5 * Mm, 2, 0, 0),
                Coating = "AR test",
                Aperture = new CircularAperture(0, 9.5 * Mm),
            },
            Sphere(-120, 20, semiMm: 10) with
            {
                Conic = -0.5,
                Shape = new EvenAsphere(new[] { 0, 1.25e3, -3e7 }),
                TiltAfter = SurfaceTiltAfter.Reverse(2),
                Comment = "back \"asphere\" | edge",
            },
            new SequentialSurface
            {
                CoordinateBreak = new CoordinateBreak(new TiltDecenter(0.1 * Mm, 0, 0, 1, 0, TiltDecenterOrder.TiltThenDecenter)),
                Thickness = 60 * Mm,
            },
            new SequentialSurface(),
        }
    };

    /// <summary>
    /// The gate: the prescription's text reads back (<see cref="PrescriptionReader"/>) into
    /// exactly the saved surfaces of the lens, and those convert to the same lens (to the
    /// rounding of the lens-unit conversion) with the same surface placement.
    /// </summary>
    [Fact]
    public void Prescription_ReadBack_ReproducesTheLens()
    {
        var lens = TiltedAsphereLens();
        var context = Context();
        var text = new PrescriptionDataAnalysis(_ => lens).Compute(context, new PrescriptionDataSettings(), default).Text;
        var back = PrescriptionReader.Read(text);
        Assert.Equal(LensUnit.Millimeter, back.Unit);
        Assert.Equal(JsonSerializer.Serialize(SavedSurfaces.FromLens(lens, LensUnit.Millimeter)), JsonSerializer.Serialize(back.Surfaces));

        var again = SavedSurfaces.ToLens(back.Surfaces, back.Unit, name => ProjectLens.FindGlass(name, context.Catalogs));
        Assert.Equal(lens.Surfaces.Count, again.Surfaces.Count);
        for (int j = 0; j < lens.Surfaces.Count; j++)
        {
            var (s, r) = (lens.Surfaces[j], again.Surfaces[j]);
            string w = $"surface {j}";
            Close(s.Curvature, r.Curvature, 1e-14, w + " curvature");
            if (!double.IsInfinity(s.Thickness)) Close(s.Thickness, r.Thickness, 1e-14, w + " thickness");
            Assert.Equal(s.Conic, r.Conic);
            Assert.Equal(s.IsStop, r.IsStop);
            Assert.Equal(s.Comment, r.Comment);
            Assert.Equal(s.Coating, r.Coating);
            Assert.Same(s.Medium.Material, r.Medium.Material);
            Assert.Equal(s.Shape?.TypeName, r.Shape?.TypeName);
            Assert.Equal(s.TiltAfter?.Kind, r.TiltAfter?.Kind);
        }
        var alpha = ((EvenAsphere)again.Surfaces[3].Shape!).Alpha;
        Close(1.25e3, alpha[1], 1e-14, "α2");
        Close(-3e7, alpha[2], 1e-14, "α3");
        Close(0.5 * Mm, again.Surfaces[2].TiltBefore!.Value.DecenterY, 1e-14, "decenter Y");
        Assert.Equal(2, again.Surfaces[2].TiltBefore!.Value.TiltXDegrees);
        Assert.Equal(TiltDecenterOrder.TiltThenDecenter, again.Surfaces[4].CoordinateBreak!.Values.Order);
        Close(9.5 * Mm, ((CircularAperture)again.Surfaces[2].Aperture!).MaxRadius, 1e-14, "aperture");

        var p1 = SurfacePlacement.Compute(lens);
        var p2 = SurfacePlacement.Compute(again);
        for (int j = 1; j < lens.Surfaces.Count; j++)
            Assert.True((p1.Global(j).Translation - p2.Global(j).Translation).Length <= 1e-15
                        && (p1.Global(j).Col2 - p2.Global(j).Col2).Length <= 1e-14, $"surface {j} is placed differently");

        // The tilted element still gets its volume: surfaces 2 and 3 are coaxial.
        var volume = new PrescriptionDataAnalysis(_ => lens).Compute(context, new PrescriptionDataSettings(), default)
            .Sections.Single(s => s.Heading == "Element volume");
        Assert.DoesNotContain("not computed", Assert.Single(volume.TableRows)[5]);
    }

    [Fact]
    public void PrescriptionReader_RefusesOtherText_NamingTheLine()
    {
        Assert.Contains("not a prescription report", Assert.Throws<FormatException>(() => PrescriptionReader.Read("Hello")).Message);
        var text = new PrescriptionDataAnalysis(_ => TiltedAsphereLens()).Compute(Context(), new PrescriptionDataSettings(), default).Text;
        var broken = text.Replace("  OBJ | Standard", "  XYZ | Standard");
        Assert.Contains("line", Assert.Throws<FormatException>(() => PrescriptionReader.Read(broken)).Message);
    }

    /// <summary>Reports carry no timestamp: two runs give the same bytes.</summary>
    [Fact]
    public void Reports_AreTheSameByteForByte_OnRepeat()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var context = SingletProject();
        foreach (var id in new[] { SystemDataAnalysis.SavedId, PrescriptionDataAnalysis.SavedId, SurfaceDataAnalysis.SavedId,
                     CardinalPointsAnalysis.SavedId, SystemSummaryGraphicAnalysis.SavedId })
        {
            Assert.True(registry.TryGet(id, out var analysis), id);
            var a = analysis.Compute(context, analysis.CreateDefaultSettings(), default);
            var b = analysis.Compute(context, analysis.CreateDefaultSettings(), default);
            Assert.DoesNotContain("No surfaces", a.Sections.Select(s => s.Heading));
            Assert.Equal(a.Text, b.Text);
            Assert.Equal(OpticsAnalysisReport.Compose("P", OpticsMode.Sequential, new[] { new OpticsAnalysisReportEntry(id, a, "") }).ToMarkdown(),
                         OpticsAnalysisReport.Compose("P", OpticsMode.Sequential, new[] { new OpticsAnalysisReportEntry(id, b, "") }).ToMarkdown());
        }
    }

    // ---- Cardinal points ----

    private static OpticsSurfaceSettings S(double? radius, double thickness, double? index = null, bool stop = false, double? semi = null) => new()
    {
        Radius = radius, Thickness = thickness, Index = index, IsStop = stop, SemiDiameter = semi,
    };

    private static List<OpticsSurfaceSettings> Saved(params OpticsSurfaceSettings[] surfaces) =>
        new[] { new OpticsSurfaceSettings { ThicknessIsInfinite = true } }.Concat(surfaces).Append(new OpticsSurfaceSettings()).ToList();

    /// <summary>A singlet in air (n = 1.5, radii 40 and −120 mm, 6 mm thick), as a project.</summary>
    private static OpticsAnalysisContext SingletProject(double? imageIndex = null) =>
        new("Singlet", new OpticsSettings { Surfaces = Saved(S(40, 6, 1.5, stop: true), S(-120, 90, imageIndex)) });

    private static ParaxialSystem Model(OpticsAnalysisContext context) =>
        ParaxialSystem.Create(ProjectLens.Paraxial(context)!, OpticalSystemData.Default);

    [Fact]
    public void CardinalPlanes_OfTheWholeLens_MatchTheParaxialEngine_AndAntiPlanesLieTwoFocalLengthsOut()
    {
        var model = Model(SingletProject());
        var c = CardinalPlanes.Compute(model, 1, 2);
        Close(model.BackFocalLength!.Value, c.Focal!.Value.Image, 1e-13, "back focal");
        Close(model.FrontFocalLength!.Value, c.Focal!.Value.Object, 1e-13, "front focal");
        Close(model.RearPrincipalPlane!.Value, c.Principal!.Value.Image, 1e-12, "rear principal");
        Close(model.FrontPrincipalPlane!.Value, c.Principal!.Value.Object, 1e-12, "front principal");
        Close(model.EffectiveFocalLength!.Value, c.EffectiveFocalLength!.Value, 1e-14, "EFL");
        // In air (n = n′ = 1) the conjugate equation z′ = (m − A)/C, z = (D − 1/m)/C gives
        // z′(−1) − z′(+1) = −2/C = 2f and z(−1) − z(+1) = 2/C = −2f: the anti-principal planes
        // lie 2f beyond H′ and 2f before H (the 2f–2f conjugates), and the nodal planes
        // coincide with the principal ones.
        double f = model.EffectiveFocalLength!.Value;
        Close(c.Principal!.Value.Image + 2 * f, c.AntiPrincipal!.Value.Image, 1e-12, "image anti-principal");
        Close(c.Principal!.Value.Object - 2 * f, c.AntiPrincipal!.Value.Object, 1e-12, "object anti-principal");
        Close(c.Principal!.Value.Image, c.Nodal!.Value.Image, 1e-12, "nodal = principal in air");
        Close(c.AntiPrincipal!.Value.Image, c.AntiNodal!.Value.Image, 1e-12, "anti-nodal = anti-principal in air");
    }

    /// <summary>With the image in a medium of index n′ the nodal points move from the principal
    /// points by (n′ − n)/φ (derived from the conjugate equation with m = n/n′), and they equal
    /// the paraxial engine's nodal points.</summary>
    [Fact]
    public void CardinalPlanes_WithAnImmersedImage_SeparateNodalFromPrincipal()
    {
        var model = Model(SingletProject(imageIndex: 1.33));
        var c = CardinalPlanes.Compute(model, 1, 2);
        Assert.Equal(1.33, c.ImageIndex);
        Close(model.RearNodalPoint!.Value, c.Nodal!.Value.Image, 1e-12, "rear nodal");
        Close(model.FrontNodalPoint!.Value, c.Nodal!.Value.Object, 1e-12, "front nodal");
        Close(c.Principal!.Value.Image + (1.33 - 1) / c.Power, c.Nodal!.Value.Image, 1e-12, "nodal shift");
    }

    /// <summary>The planes of surfaces 3–4 of a two-lens system are those of a lens made of only
    /// those two surfaces.</summary>
    [Fact]
    public void CardinalPlanes_OfASurfaceRange_AreThoseOfThatPartAlone()
    {
        var both = new OpticsAnalysisContext("Two", new OpticsSettings
        {
            Surfaces = Saved(S(40, 6, 1.5, stop: true), S(-120, 10), S(60, 4, 1.7), S(200, 50)),
        });
        var part = new OpticsAnalysisContext("Part", new OpticsSettings
        {
            Surfaces = Saved(S(60, 4, 1.7, stop: true), S(200, 50)),
        });
        var range = CardinalPlanes.Compute(Model(both), 3, 4);
        var alone = CardinalPlanes.Compute(Model(part), 1, 2);
        Close(alone.EffectiveFocalLength!.Value, range.EffectiveFocalLength!.Value, 1e-13, "EFL");
        Close(alone.Principal!.Value.Image, range.Principal!.Value.Image, 1e-12, "rear principal");
        Close(alone.AntiNodal!.Value.Object, range.AntiNodal!.Value.Object, 1e-12, "front anti-nodal");

        var analysis = new CardinalPointsAnalysis();
        var result = analysis.Compute(both, new CardinalPointsSettings { FirstSurface = 3, LastSurface = 4 }, default);
        Assert.Contains("Anti-nodal", result.Text);
        Assert.Throws<ArgumentException>(() => analysis.Compute(both, new CardinalPointsSettings { FirstSurface = 4, LastSurface = 3 }, default));
    }

    // ---- Surface data, system data, report graphic ----

    [Fact]
    public void SurfaceData_ElementPowerOfASinglet_IsTheSystemPower()
    {
        var context = SingletProject();
        var model = Model(context);
        var element = CardinalPlanes.Compute(model, 1, 2);
        Assert.Equal(model.Power, element.Power);
        var result = new SurfaceDataAnalysis().Compute(context, new SurfaceDataSettings { Surface = 1 }, default);
        Assert.Contains("Element focal length", result.Text);
        Assert.Contains("Edge thickness X", result.Text);
        Assert.Throws<ArgumentException>(() => new SurfaceDataAnalysis().Compute(context, new SurfaceDataSettings { Surface = 3 }, default));
    }

    [Fact]
    public void SystemData_ReusesTheSummaryAndTheParaxialData()
    {
        var result = new SystemDataAnalysis().Compute(SingletProject(), new SystemReportSettings(), default);
        var headings = result.Sections.Select(s => s.Heading).ToList();
        Assert.Equal(new[] { "System", "Environment", "System inputs", "System data", "ABCD matrix" }, headings.Take(5));
        Assert.DoesNotContain("Paraxial ray data", headings);
        Assert.DoesNotContain("Cardinal points", headings);
    }

    [Fact]
    public void ReportGraphic_GathersEachPositionsResult_AndRefusesUnknownIds()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(ReportGraphicAnalysis.SavedId, out var graphic));
        var settings = new ReportGraphicSettings
        {
            Position1 = SystemSummaryGraphicAnalysis.SavedId, Position2 = CardinalPointsAnalysis.SavedId,
            Position3 = "", Position4 = GaussianBeamAnalysis.SavedId,
        };
        var context = SingletProject();
        var result = graphic.Compute(context, settings, default);
        Assert.Equal("Positions", result.Sections[0].Heading);
        Assert.Equal(4, result.Sections[0].TableRows.Count);
        Assert.Equal("empty", result.Sections[0].TableRows[2][2]);
        Assert.Contains(result.Sections, s => s.Heading.StartsWith("2. Cardinal points:", StringComparison.Ordinal));
        Assert.All(result.Plots, p => Assert.StartsWith("4. ", p.Title));
        Assert.NotEmpty(result.Plots);

        Assert.Contains("NoSuchAnalysis", Assert.Throws<ArgumentException>(() =>
            graphic.Compute(context, settings with { Position3 = "NoSuchAnalysis" }, default)).Message);
        Assert.Throws<ArgumentException>(() => graphic.Compute(context, settings with { Position1 = ReportGraphicAnalysis.SavedId }, default));
    }
}
