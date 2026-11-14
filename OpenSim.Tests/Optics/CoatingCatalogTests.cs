using System.Numerics;
using System.Text.Json;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-15: coating materials, tapers, coatings and their evaluation, the coating catalog's saved
/// forms, and OpticStudio's coating file. Coating-file text is written from the syntax and
/// examples of the OpticStudio 2025 R2 help pages saved under plans/sources/optics/coatingfile/;
/// indices are test constants.
/// </summary>
public sealed class CoatingCatalogTests
{
    private const double Deg = Math.PI / 180;

    private static CoatingCatalog QuarterWaveCatalog(double n1 = 1.38) => new()
    {
        Materials = new[] { CoatingMaterial.Constant("LOW", n1), CoatingMaterial.Constant("HIGH", 2.35) },
        Coatings = new Coating[]
        {
            new LayerCoating { Name = "QW", Layers = new[] { new CoatingLayer { Material = "LOW", Thickness = 0.25 } } }
        }
    };

    private static CoatingConditions Normal(double micrometers = 0.55) => new()
    {
        Incident = 1, Substrate = 1.52, Micrometers = micrometers, PrimaryMicrometers = 0.55
    };

    /// <summary>The help's example ("The COAT Data Section"): n = 1.4, T = 0.25 at a primary
    /// wavelength of 0.550 µm is "0.0982 micrometers thick" (the help rounds to four digits).</summary>
    [Fact]
    public void RelativeThickness_IsWavesOfThePrimaryWavelengthInTheLayer()
    {
        var catalog = new CoatingCatalog
        {
            Materials = new[] { CoatingMaterial.Constant("M", 1.4) },
            Coatings = new Coating[] { new LayerCoating { Name = "C", Layers = new[] { new CoatingLayer { Material = "M", Thickness = 0.25 } } } }
        };
        var (layers, _) = CoatingEvaluator.Stack(catalog, (LayerCoating)catalog.Coatings[0], Normal());
        Assert.Equal(0.55 * 0.25 / 1.4, layers[0].Thickness, 15);
        Assert.Equal(0.0982, layers[0].Thickness, 4);

        var absolute = new LayerCoating { Name = "A", Layers = new[] { new CoatingLayer { Material = "M", Thickness = 0.0982, IsAbsolute = true } } };
        Assert.Equal(0.0982, CoatingEvaluator.Stack(catalog, absolute, Normal()).Layers[0].Thickness, 15);
    }

    /// <summary>A quarter-wave coating evaluated through the catalog gives the closed form, and its
    /// response at normal incidence has zero retardance and diattenuation in this program's
    /// reporting convention (reflection retardance φp − φs + π).</summary>
    [Fact]
    public void QuarterWaveCoating_GivesTheClosedForm_AndNoRetardanceAtNormalIncidence()
    {
        var catalog = QuarterWaveCatalog();
        var response = CoatingEvaluator.Evaluate(catalog, catalog.FindCoating("qw"), Normal());
        double expected = Math.Pow((1.52 - 1.38 * 1.38) / (1.52 + 1.38 * 1.38), 2);
        Assert.Equal(expected, response.S.R, 14);
        Assert.Equal(expected, response.AverageReflection, 14);
        Assert.Equal(0, response.ReflectionRetardance, 12);
        Assert.Equal(0, response.TransmissionRetardance, 12);
        Assert.Equal(0, response.ReflectionDiattenuation, 12);
        Assert.Equal(1, response.S.R + response.S.T + response.S.A, 14);
    }

    /// <summary>The coating multiplier scales the thickness (2 makes the quarter wave a half wave,
    /// which is absent), and the index offset adds to n.</summary>
    [Fact]
    public void MultiplierAndOffsets_ChangeThicknessAndIndex()
    {
        var catalog = QuarterWaveCatalog();
        var bare = CoatingEvaluator.Evaluate(catalog, null, Normal());
        var doubled = CoatingEvaluator.Evaluate(catalog, catalog.FindCoating("QW"), Normal() with { All = new CoatingLayerAdjustment(Multiplier: 2) });
        Assert.Equal(bare.S.R, doubled.S.R, 14);

        var offset = Normal() with { All = new CoatingLayerAdjustment(IndexOffset: 0.1, ExtinctionOffset: 0.01) };
        var (layers, _) = CoatingEvaluator.Stack(catalog, (LayerCoating)catalog.FindCoating("QW")!, offset);
        Assert.Equal(new Complex(1.48, 0.01), layers[0].Index);
        Assert.Equal(0.55 * 0.25 / 1.38, layers[0].Thickness, 15);     // the thickness uses the material's own n0

        var perLine = Normal() with { Lines = new[] { new CoatingLayerAdjustment(Multiplier: 3) }, All = new CoatingLayerAdjustment(Multiplier: 0.5) };
        Assert.Equal(1.5 * 0.55 * 0.25 / 1.38, CoatingEvaluator.Stack(catalog, (LayerCoating)catalog.FindCoating("QW")!, perLine).Layers[0].Thickness, 15);

        Assert.Throws<InvalidOperationException>(() => CoatingEvaluator.Evaluate(catalog, catalog.FindCoating("QW"),
            Normal() with { All = new CoatingLayerAdjustment(Multiplier: 11) }));
        Assert.Throws<InvalidOperationException>(() => CoatingEvaluator.Evaluate(catalog, catalog.FindCoating("QW"),
            Normal() with { All = new CoatingLayerAdjustment(ExtinctionOffset: -0.1) }));
    }

    /// <summary>The help's own example ("Defining Replicated Groups of Coating Layers"): 1GROUP
    /// with loop index 3 on its first and third lines is the same eleven layers as 3GROUPS.</summary>
    [Fact]
    public void LoopIndex_WritesOutTheGroup_AsTheHelpsExample()
    {
        var oneGroup = new LayerCoating
        {
            Name = "1GROUP",
            Layers = new[] { L("MAT0", 0.25, 0), L("MAT1", 0.25, 3), L("MAT2", 0.50, 0), L("MAT3", 0.25, 3), L("MAT4", 0.25, 0) }
        };
        string[] threeGroups = { "MAT0", "MAT1", "MAT2", "MAT3", "MAT1", "MAT2", "MAT3", "MAT1", "MAT2", "MAT3", "MAT4" };
        Assert.Equal(threeGroups, oneGroup.Expand().Select(e => e.Layer.Material));

        var unmatched = new LayerCoating { Name = "U", Layers = new[] { L("A", 0.25, 2), L("B", 0.25, 0) } };
        Assert.Contains("no later line closes", Assert.Throws<InvalidOperationException>(() => unmatched.Expand()).Message);
        var mismatched = new LayerCoating { Name = "M", Layers = new[] { L("A", 0.25, 2), L("B", 0.25, 3) } };
        Assert.Throws<InvalidOperationException>(() => mismatched.Expand());
        var one = new LayerCoating { Name = "O", Layers = new[] { L("A", 0.25, 1), L("B", 0.25, 1) } };
        Assert.Equal(2, one.Expand().Count);

        static CoatingLayer L(string m, double t, int loop) => new() { Material = m, Thickness = t, LoopIndex = loop };
    }

    /// <summary>The taper factor as the help's TAPR page defines it, computed by hand here.</summary>
    [Fact]
    public void TaperFactor_FollowsTheHelpsDefinition()
    {
        // Radial: 1 + 0.1 r + 0.02 r², decentered by (1, −2) and rotated by 30° (rotation does not change r).
        var radial = new CoatingTaper
        {
            Name = "R", DecenterX = 1, DecenterY = -2, RotationDegrees = 30,
            RadialTerms = new Dictionary<int, double> { [0] = 1, [1] = 0.1, [2] = 0.02 }
        };
        double r = Math.Sqrt(Math.Pow(4 - 1, 2) + Math.Pow(2 + 2, 2));
        Assert.Equal(1 + 0.1 * r + 0.02 * r * r, radial.Factor(4, 2), 14);

        // Cosine: Δ1 = 1 gives exactly cos α, sin α = r / R ("To define a coating whose thickness
        // varies exactly as cos θ, … Δ0 … zero and … Δ1 … 1.0").
        var cosine = new CoatingTaper { Name = "C", CosineTerms = new Dictionary<int, double> { [1] = 1 } };
        Assert.Equal(Math.Sqrt(1 - Math.Pow(5.0 / 20, 2)), cosine.Factor(3, 4, surfaceRadius: 20), 15);
        Assert.Equal(1, cosine.Factor(3, 4), 15);                                       // a plane
        Assert.Throws<InvalidOperationException>(() => cosine.Factor(30, 40, surfaceRadius: 20));

        // Polynomial: E0 = 1, E1 = x′, E2 = y′, E3 = x′², E4 = x′y′, E5 = y′², E6 = x′³ (the help's list).
        Assert.Equal(new[] { (0, 0), (1, 0), (0, 1), (2, 0), (1, 1), (0, 2), (3, 0) },
            Enumerable.Range(0, 7).Select(CoatingTaper.PolynomialPowers));
        Assert.Equal((0, 5), CoatingTaper.PolynomialPowers(20));                         // inferred continuation
        var poly = new CoatingTaper
        {
            Name = "P", RotationDegrees = 90,
            PolynomialTerms = new Dictionary<int, double> { [0] = 1, [1] = 0.5, [4] = 0.25 }
        };
        // θ = 90°: x′ = −y, y′ = x. The point (2, −1) keeps the sum positive (a negative sum
        // counts as zero, below), so the value checks the rotation and the terms.
        double xp = 1, yp = 2;
        Assert.Equal(1 + 0.5 * xp + 0.25 * xp * yp, poly.Factor(2, -1), 14);

        // A negative sum counts as zero; a taper with no terms gives zero, as defined.
        Assert.Equal(0, new CoatingTaper { Name = "N", RadialTerms = new Dictionary<int, double> { [0] = -1 } }.Factor(0, 0));
        Assert.Equal(0, new CoatingTaper { Name = "E" }.Factor(1, 1));
    }

    [Fact]
    public void ATaperedLayer_ScalesItsThickness()
    {
        var catalog = QuarterWaveCatalog() with
        {
            Tapers = new[] { new CoatingTaper { Name = "T", RadialTerms = new Dictionary<int, double> { [0] = 1, [2] = 0.01 } } },
            Coatings = new Coating[] { new LayerCoating { Name = "QW", Layers = new[] { new CoatingLayer { Material = "LOW", Thickness = 0.25, Taper = "T" } } } }
        };
        var conditions = Normal() with { X = 3, Y = 4 };
        var (layers, _) = CoatingEvaluator.Stack(catalog, (LayerCoating)catalog.Coatings[0], conditions);
        Assert.Equal(0.55 * 0.25 / 1.38 * (1 + 0.01 * 25), layers[0].Thickness, 15);
    }

    /// <summary>IDEAL and the I.xx shorthand, as their help pages define them.</summary>
    [Fact]
    public void IdealCoatings_FollowTheirDefinitions()
    {
        var catalog = new CoatingCatalog();
        Assert.True(IdealCoating.TryParseSimpleName("I.75", out double t));
        Assert.Equal(0.75, t);
        var simple = new IdealCoating { Name = "I.75", Transmission = 0.75, Reflection = 0.25, IsSimple = true };
        var r = CoatingEvaluator.Evaluate(catalog, simple, Normal());
        Assert.Equal((0.25, 0.75, 0.0), (r.S.R, r.S.T, r.S.A));
        Assert.Equal(Math.PI, r.S.ReflectionPhase);                  // low to high index: π
        Assert.Equal(0, r.S.TransmissionPhase);

        // T + R above 1 is scaled to 1; A = 1 − R − T.
        var scaled = CoatingEvaluator.Evaluate(catalog, new IdealCoating { Name = "X", Transmission = 0.9, Reflection = 0.3 }, Normal());
        Assert.Equal(0.9 / 1.2, scaled.P.T, 15);
        Assert.Equal(0.3 / 1.2, scaled.P.R, 15);
        var lossy = CoatingEvaluator.Evaluate(catalog, new IdealCoating { Name = "Y", Transmission = 0.5, Reflection = 0.2 }, Normal());
        Assert.Equal(0.3, lossy.S.A, 15);

        // From high to low index the reflection phase is zero; under TIR the TIR intensity is reflected.
        var down = Normal() with { Incident = 1.52, Substrate = 1.0 };
        Assert.Equal(0, CoatingEvaluator.Evaluate(catalog, new IdealCoating { Name = "Y", Transmission = 0.5, Reflection = 0.2 }, down).S.ReflectionPhase);
        var tir = CoatingEvaluator.Evaluate(catalog, new IdealCoating { Name = "Z", Transmission = 0.5, Reflection = 0.2, TotalInternalReflection = 0.9 },
            down with { AngleDegrees = 60 });
        Assert.Equal((0.9, 0.0), (tir.S.R, tir.S.T));
    }

    [Fact]
    public void Ideal2_ChangesTheReflectionSignFromHighToLowIndex_UnlessFlagged()
    {
        var coating = new Ideal2Coating
        {
            Name = "I2", SReflection = new Complex(0.3, 0.1), STransmission = new Complex(0.9, 0),
            PReflection = new Complex(0.2, 0), PTransmission = new Complex(0, 0.95)
        };
        var up = CoatingEvaluator.Evaluate(CoatingCatalog.Empty, coating, Normal());
        Assert.Equal(0.1, up.S.R, 15);
        Assert.Equal(0.81, up.S.T, 15);
        Assert.Equal(Math.Atan2(0.1, 0.3), up.S.ReflectionPhase, 15);
        Assert.Equal(Math.PI / 2, up.P.TransmissionPhase, 15);

        var down = Normal() with { Incident = 1.52, Substrate = 1.0 };
        var flipped = CoatingEvaluator.Evaluate(CoatingCatalog.Empty, coating, down);
        Assert.Equal(CoatingResponse.Wrap(up.S.ReflectionPhase + Math.PI), flipped.S.ReflectionPhase, 14);
        var kept = CoatingEvaluator.Evaluate(CoatingCatalog.Empty, coating with { NoPiFlag = true }, down);
        Assert.Equal(up.S.ReflectionPhase, kept.S.ReflectionPhase, 15);
    }

    /// <summary>A table coating interpolates linearly in cos θ between angles, holds the end
    /// angles beyond them, interpolates linearly in wavelength, and refuses a wavelength outside.</summary>
    [Fact]
    public void TableCoating_InterpolatesInCosineOfAngle()
    {
        var table = new TableCoating
        {
            Name = "TAB",
            Angles = new[]
            {
                new CoatingTableAngle(0, new[] { new CoatingTableRow(0.4, 0.1, 0.1, 0.9, 0.9), new CoatingTableRow(0.8, 0.2, 0.2, 0.8, 0.8) }),
                new CoatingTableAngle(60, new[] { new CoatingTableRow(0.4, 0.3, 0.5, 0.7, 0.5, 10, 20, 0, 0), new CoatingTableRow(0.8, 0.4, 0.6, 0.6, 0.4) }),
            }
        };
        Assert.Empty(table.Validate(CoatingCatalog.Empty));
        double u = (Math.Cos(30 * Deg) - 1) / (Math.Cos(60 * Deg) - 1);
        var row = CoatingEvaluator.Interpolate(table, 30, 0.4);
        Assert.Equal(0.1 + u * (0.3 - 0.1), row.Rs, 15);
        Assert.Equal(u * 20, row.PhaseRp, 13);
        Assert.Equal(0.3, CoatingEvaluator.Interpolate(table, 75, 0.4).Rs, 15);          // beyond 60°: the 60° data
        Assert.Equal(0.15, CoatingEvaluator.Interpolate(table, 0, 0.6).Rs, 15);
        Assert.Throws<ArgumentOutOfRangeException>(() => CoatingEvaluator.Interpolate(table, 0, 0.9));

        var response = CoatingEvaluator.Evaluate(CoatingCatalog.Empty, table, Normal(0.4) with { AngleDegrees = 60 });
        Assert.Equal(1 - 0.5 - 0.5, response.P.A, 15);
        Assert.Equal(20 * Deg, response.P.ReflectionPhase, 15);
    }

    /// <summary>An uncoated mirror is aluminium, 0.7 − 7.0i in OpticStudio's convention (help,
    /// "Properties of Uncoated Surfaces"); nothing is transmitted.</summary>
    [Fact]
    public void UncoatedMirror_IsAluminium_AndTransmitsNothing()
    {
        var r = CoatingEvaluator.Evaluate(CoatingCatalog.Empty, null, Normal() with { IsMirror = true });
        double expected = (Math.Pow(0.3, 2) + 49) / (Math.Pow(1.7, 2) + 49);
        Assert.Equal(expected, r.S.R, 14);
        Assert.Equal(0, r.S.T);
        Assert.Equal(1 - expected, r.S.A, 14);
        Assert.Equal(new Complex(0.7, 7.0), CoatingEvaluator.UncoatedMirrorIndex);

        // A mirror coating: its last layer is the substrate.
        var catalog = new CoatingCatalog
        {
            Materials = new[] { CoatingMaterial.Constant("AL", 0.7, 7.0), CoatingMaterial.Constant("SIO2", 1.46) },
            Coatings = new Coating[] { new LayerCoating { Name = "PROT", Layers = new[] { new CoatingLayer { Material = "SIO2", Thickness = 0.5 }, new CoatingLayer { Material = "AL", Thickness = 1 } } } }
        };
        var (layers, substrate) = CoatingEvaluator.Stack(catalog, (LayerCoating)catalog.Coatings[0], Normal() with { IsMirror = true });
        Assert.Single(layers);
        Assert.Equal(new Complex(0.7, 7.0), substrate);
        // The half-wave protective layer is absent at its design wavelength.
        Assert.Equal(expected, CoatingEvaluator.Evaluate(catalog, catalog.Coatings[0], Normal() with { IsMirror = true }).S.R, 13);
    }

    [Fact]
    public void ReversedLayers_AreTheStackReadFromTheOtherSide()
    {
        var catalog = new CoatingCatalog
        {
            Materials = new[] { CoatingMaterial.Constant("H", 2.35), CoatingMaterial.Constant("L", 1.38) },
            Coatings = new Coating[]
            {
                new LayerCoating { Name = "HL", Layers = new[] { new CoatingLayer { Material = "H", Thickness = 0.3 }, new CoatingLayer { Material = "L", Thickness = 0.2 } } },
                new LayerCoating { Name = "LH", Layers = new[] { new CoatingLayer { Material = "L", Thickness = 0.2 }, new CoatingLayer { Material = "H", Thickness = 0.3 } } },
            }
        };
        var glassToAir = Normal() with { Incident = 1.52, Substrate = 1.0, AngleDegrees = 20 };
        var reversed = CoatingEvaluator.Evaluate(catalog, catalog.FindCoating("HL"), glassToAir with { ReverseLayers = true });
        var manual = CoatingEvaluator.Evaluate(catalog, catalog.FindCoating("LH"), glassToAir);
        Assert.Equal(manual, reversed);
    }

    [Fact]
    public void CoatingMaterial_InterpolatesItsTable_AndRefusesOutsideIt()
    {
        var m = new CoatingMaterial
        {
            Name = "TAB", Rows = new[] { new TabulatedIndexPoint(0.4, 2.0, 0.1), new TabulatedIndexPoint(0.8, 1.8, 0.0) }
        };
        var mid = m.Index(0.6);   // linear interpolation: equal to the midpoint within rounding
        Assert.Equal(1.9, mid.Real, 14);
        Assert.Equal(0.05, mid.Imaginary, 14);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => m.Index(0.3));
        Assert.Contains("TAB", ex.Message);
        Assert.Equal(new Complex(1.7, 0.2), CoatingMaterial.Constant("K", 1.7, 0.2).Index(5.0));
        Assert.NotEmpty(CoatingMaterial.Constant("G", 1.5, -0.1).Validate());
    }

    [Fact]
    public void Catalog_ListsMissingMaterialsTapersAndDuplicates()
    {
        var catalog = new CoatingCatalog
        {
            Materials = new[] { CoatingMaterial.Constant("A", 1.5), CoatingMaterial.Constant("a", 1.6) },
            Coatings = new Coating[] { new LayerCoating { Name = "C", Layers = new[] { new CoatingLayer { Material = "B", Thickness = 0.25, Taper = "T" } } } }
        };
        var problems = catalog.Validate();
        Assert.Contains(problems, p => p.Contains("'A'") && p.Contains("unique"));
        Assert.Contains(problems, p => p.Contains("material 'B'"));
        Assert.Contains(problems, p => p.Contains("taper 'T'"));
        Assert.Throws<InvalidOperationException>(() => CoatingEvaluator.Evaluate(catalog, catalog.Coatings[0], Normal()));
    }

    // ---------------------------------------------------------------- files

    /// <summary>A coating file written from the help's syntax: comments, a MATE with a negative
    /// extinction coefficient (absorbing, OpticStudio's n − ik), a single-row MATE, a TAPR, a COAT
    /// with a loop and a taper, the I.xx shorthand, IDEAL, IDEAL2, the help's TABLE example and an
    /// ENCRYPTED coating.</summary>
    private const string SampleDat = """
        ! A sample coating file
        MATE MGF2
        0.55 1.38 0.0
        MATE METAL ! an absorbing material
        0.40 0.5 -4.0
        0.80 0.9 -6.0
        TAPR RAD
        DX 0.5
        AN 15
        RT 0 1.0
        RT 2 -0.001
        CT 1 0.5
        PT 4 0.25
        COAT AR
        MGF2 0.25
        METAL 0.01 1 2
        MGF2 0.5 0 2 RAD
        COAT I.75
        IDEAL HALF 0.5 0.4 0.95
        IDEAL2 MYI2 0.1 0.2 0.9 0.0 -0.1 0.0 0.8 0.1 1 0.9
        TABLE MYTABLECOAT
        ANGL 0
        WAVE 0.35 0.014 0.014 0.986 0.986 0.0 0.0 0.0 0.0
        WAVE 0.70 0.013 0.013 0.987 0.987 0.0 0.0 0.0 0.0
        ANGL 5
        WAVE 0.35 0.015 0.015 0.985 0.985 0.0 0.0 0.0 0.0
        WAVE 0.70 0.012 0.012 0.988 0.988 0.0 0.0 0.0 0.0
        ANGL 45
        WAVE 0.35 0.065 0.070 0.935 0.930 0.0 0.0 0.0 0.0
        WAVE 0.70 0.062 0.065 0.938 0.935 0.0 0.0 0.0 0.0
        ANGL 80
        WAVE 0.35 0.600 0.600 0.400 0.400 0.0 0.0 0.0 0.0
        WAVE 0.70 0.600 0.600 0.400 0.400 0.0 0.0 0.0 0.0
        COAT SECRET
        ENCRYPTED secret.dat
        """;

    [Fact]
    public void CoatingFile_ReadsEverySection()
    {
        var notes = new List<string>();
        var catalog = CoatingFile.ReadDat(SampleDat, notes, "sample.dat");
        Assert.Contains(notes, n => n.Contains("SECRET") && n.Contains("encrypted"));
        Assert.Empty(catalog.Validate());

        var metal = catalog.FindMaterial("METAL")!;
        Assert.Equal(new Complex(0.5, 4.0), metal.Index(0.4));            // k stored positive
        Assert.True(catalog.FindMaterial("MGF2")!.IsConstant);

        var taper = catalog.FindTaper("RAD")!;
        Assert.Equal((0.5, 0.0, 15.0), (taper.DecenterX, taper.DecenterY, taper.RotationDegrees));
        Assert.Equal(-0.001, taper.RadialTerms[2]);
        Assert.Equal(0.5, taper.CosineTerms[1]);
        Assert.Equal(0.25, taper.PolynomialTerms[4]);

        var ar = (LayerCoating)catalog.FindCoating("AR")!;
        Assert.Equal(3, ar.Layers.Count);
        Assert.False(ar.Layers[0].IsAbsolute);
        Assert.True(ar.Layers[1].IsAbsolute);
        Assert.Equal(2, ar.Layers[1].LoopIndex);
        Assert.Equal("RAD", ar.Layers[2].Taper);
        Assert.Equal(5, ar.Expand().Count);

        var simple = (IdealCoating)catalog.FindCoating("I.75")!;
        Assert.True(simple.IsSimple);
        Assert.Equal(0.75, simple.Transmission);
        var half = (IdealCoating)catalog.FindCoating("HALF")!;
        Assert.Equal((0.5, 0.4, 0.95), (half.Transmission, half.Reflection, half.TotalInternalReflection));
        var i2 = (Ideal2Coating)catalog.FindCoating("MYI2")!;
        Assert.Equal(new Complex(0.8, 0.1), i2.PTransmission);
        Assert.True(i2.NoPiFlag);
        Assert.Equal(0.9, i2.TotalInternalReflection);
        var table = (TableCoating)catalog.FindCoating("MYTABLECOAT")!;
        Assert.Equal(new[] { 0.0, 5, 45, 80 }, table.Angles.Select(a => a.AngleDegrees));
        Assert.Equal(0.070, table.Angles[2].Rows[0].Rp);
    }

    [Fact]
    public void CoatingFile_RefusesGainAndBadLines_NamingTheLine()
    {
        var gain = Assert.Throws<InvalidDataException>(() => CoatingFile.ReadDat("MATE X\n0.5 1.5 0.1\n", new List<string>(), "g.dat"));
        Assert.Contains("g.dat, line 2", gain.Message);
        Assert.Contains("negative", gain.Message);
        Assert.Throws<InvalidDataException>(() => CoatingFile.ReadDat("COAT A\nM abc\n", new List<string>()));
        Assert.Throws<InvalidDataException>(() => CoatingFile.ReadDat("TAPR T\nRT 23 1\n", new List<string>()));
        Assert.Throws<InvalidDataException>(() => CoatingFile.ReadDat("TABLE T\nWAVE 0.5 0 0 1 1\n", new List<string>()));
        Assert.Throws<InvalidDataException>(() => CoatingFile.ReadDat("0.5 1.5 0\n", new List<string>()));
    }

    /// <summary>Writing and reading back gives the same catalog, digit for digit, in both formats.</summary>
    [Fact]
    public void CoatingFile_AndJson_RoundTrip()
    {
        var notes = new List<string>();
        var original = CoatingFile.ReadDat(SampleDat, notes);
        string Saved(CoatingCatalog c) => JsonSerializer.Serialize(CoatingCatalogSettings.ToSettings(c));

        var dat = CoatingFile.ReadDat(CoatingFile.WriteDat(original), notes);
        Assert.Equal(Saved(original), Saved(dat));
        var json = CoatingFile.FromJson(CoatingFile.ToJson(original), notes);
        Assert.Equal(Saved(original), Saved(json));
        var settings = CoatingCatalogSettings.FromSettings(CoatingCatalogSettings.ToSettings(original), notes);
        Assert.Equal(Saved(original), Saved(settings));
        Assert.Contains("-6", CoatingFile.WriteDat(original));                  // k written negative again

        Assert.Throws<InvalidDataException>(() => CoatingFile.FromJson("{\"format\":\"something else\"}", notes));
        Assert.Null(CoatingCatalogSettings.ToSettings(CoatingCatalog.Empty));
        Assert.Same(CoatingCatalog.Empty, CoatingCatalogSettings.FromSettings(null, notes));
    }
}
