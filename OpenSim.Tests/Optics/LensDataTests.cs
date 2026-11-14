using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-31: the saved surfaces and their conversion to the real trace, the analyses reading the
/// project's surfaces, and the lens editing tools. Every expected value is derived in the test
/// from the definitions of docs/Optics-Conventions.md sections 3, 11 and 13 (a uniform scale
/// scales the focal length; a ray from a centre of curvature meets the sphere along its normal);
/// radii, thicknesses and indices are arbitrary test inputs, not reference data.
/// </summary>
public sealed class LensDataTests
{
    // ---- Builders (lens units: millimeters, the default) ----

    private static OpticsSurfaceSettings S(double? radius, double thickness, double? index = null, bool stop = false,
        double? semiDiameter = null) => new()
    {
        Radius = radius, Thickness = thickness, Index = index, IsStop = stop, SemiDiameter = semiDiameter,
    };

    private static OpticsSurfaceSettings Object(double distance) =>
        double.IsPositiveInfinity(distance) ? new OpticsSurfaceSettings { ThicknessIsInfinite = true } : new OpticsSurfaceSettings { Thickness = distance };

    private static List<OpticsSurfaceSettings> Lens(double objectDistance, params OpticsSurfaceSettings[] surfaces) =>
        new[] { Object(objectDistance) }.Concat(surfaces).Append(new OpticsSurfaceSettings()).ToList();

    private static LensEditContext Context(OpticsSystemSettings? system = null) =>
        new(system ?? new OpticsSystemSettings(), Array.Empty<OpticalCatalog>());

    /// <summary>A singlet in air: n = 1.5, 6 mm thick, front radius 40, back −120, then 90 mm.</summary>
    private static List<OpticsSurfaceSettings> Singlet() =>
        Lens(double.PositiveInfinity, S(40, 6, 1.5, stop: true), S(-120, 90));

    private static ParaxialSystem Paraxial(IReadOnlyList<OpticsSurfaceSettings> surfaces, LensEditContext? context = null)
    {
        context ??= Context();
        return ParaxialSystem.Create(context.ToLens(surfaces).ToParaxialLens(), context.System());
    }

    private static void Close(double expected, double actual, double relative, string what) =>
        Assert.True(Math.Abs(actual - expected) <= relative * Math.Max(Math.Abs(expected), 1e-300),
            $"{what}: {actual:R} is not within {relative:G3} of {expected:R}.");

    private static string Json(object o) => JsonSerializer.Serialize(o);

    // ---- Saved surfaces to and from the trace ----

    [Fact]
    public void SavedSurfaces_ConvertToTheTrace_InMeters()
    {
        var saved = Lens(250,
            new OpticsSurfaceSettings
            {
                Radius = 40, Thickness = 6, Index = 1.5, IsStop = true, Conic = -0.5, SemiDiameter = 8, ChipZone = 0.5,
                MechanicalSemiDiameter = 9, Comment = "front", Coating = "AR",
                Aperture = new OpticsSurfaceApertureSettings { Type = SavedSurfaces.CircularAperture, MinRadius = 1, MaxRadius = 7, DecenterY = 0.25 },
            },
            new OpticsSurfaceSettings { Radius = -120, Thickness = 20, IsIgnored = false, SkipRaysTo = true },
            new OpticsSurfaceSettings { Radius = null, Thickness = -10, Material = "mirror" });
        var lens = SavedSurfaces.ToLens(saved, LensUnit.Millimeter, _ => null);

        Assert.Equal(0.25, lens.Surfaces[0].Thickness);
        var front = lens.Surfaces[1];
        Assert.Equal(OpticsConventions.CurvatureFromRadius(40 * 1e-3), front.Curvature);
        Assert.Equal(6 * 1e-3, front.Thickness);
        Assert.Equal(1.5, front.Medium.Index);
        Assert.Equal(-0.5, front.Conic);
        Assert.Equal(8 * 1e-3, front.SemiDiameter);
        Assert.Equal(9 * 1e-3, front.MechanicalSemiDiameter);
        Assert.Equal(0.5 * 1e-3, front.ChipZone);
        Assert.Equal("front", front.Comment);
        Assert.True(front.IsStop);
        var aperture = Assert.IsType<CircularAperture>(front.Aperture);
        Assert.Equal((1e-3, 7e-3, 0.25e-3), (aperture.MinRadius, aperture.MaxRadius, aperture.DecenterY));
        Assert.True(lens.Surfaces[2].SkipRaysTo);
        Assert.Null(lens.Surfaces[2].SemiDiameter);                       // automatic
        Assert.Equal(0, lens.Surfaces[3].Curvature);                       // no radius: a plane
        Assert.True(lens.Surfaces[3].IsMirror);                            // MIRROR in any case

        // In inches every length is 25.4 mm.
        var inches = SavedSurfaces.ToLens(saved, LensUnit.Inch, _ => null);
        Assert.Equal(6 * 0.0254, inches.Surfaces[1].Thickness);

        // Back again: the same lens, each length within an ulp of the division.
        var back = SavedSurfaces.FromLens(lens, LensUnit.Millimeter);
        Assert.Equal(saved.Count, back.Count);
        Close(40, back[1].Radius!.Value, 4e-16, "radius");
        Close(6, back[1].Thickness, 4e-16, "thickness");
        Assert.True(back[0].Thickness == 250 && !back[0].ThicknessIsInfinite);
        Assert.Equal(SavedSurfaces.Mirror, back[3].Material);
        Assert.Equal(SavedSurfaces.CircularAperture, back[1].Aperture!.Type);
    }

    [Fact]
    public void CoatingAndTce_ReachTheTrace_AndComeBack()
    {
        // Wave 4 integration: the editor's coating and TCE columns feed SequentialSurface.Coating
        // (read by the polarization trace, OP-51) and ThermalExpansion (read by TCE pickups, OP-29).
        var saved = new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 5, Index = 1.5, Coating = "AR", Tce = 7.1, IsStop = true },
            new() { Radius = -50, Thickness = 45 },
            new(),
        };
        var lens = SavedSurfaces.ToLens(saved, LensUnit.Millimeter, _ => null);
        Assert.Equal("AR", lens.Surfaces[1].Coating);
        Assert.Equal(7.1, lens.Surfaces[1].ThermalExpansion);
        Assert.Null(lens.Surfaces[2].Coating);
        var back = SavedSurfaces.FromLens(lens, LensUnit.Millimeter);
        Assert.Equal("AR", back[1].Coating);
        Assert.Equal(7.1, back[1].Tce);
        Assert.Null(back[2].Coating);
    }

    [Fact]
    public void AnUnknownSurfaceType_OrGlass_IsRefusedByName()
    {
        var saved = Singlet();
        saved[2] = saved[2] with { Type = "Zernike Standard Sag", Parameters = new() { new() { Number = 2, Value = 1e-5 } } };
        saved[1] = saved[1] with { Index = null, Material = "N-NOTAGLASS" };
        var ex = Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(saved, LensUnit.Millimeter, _ => null));
        Assert.Contains("Surface 2: the surface type 'Zernike Standard Sag'", ex.Message);
        Assert.Contains("Surface 1: the glass 'N-NOTAGLASS' is not in the active catalogs", ex.Message);

        // A model glass needs catalogs to train on; a Standard surface has no parameters.
        var model = Singlet();
        model[1] = model[1] with { Index = null, ModelGlass = new OpticsModelGlassSettings { Nd = 1.6, Vd = 40 } };
        model[2] = model[2] with { Parameters = new() { new() { Number = 1, Value = 3 } } };
        ex = Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(model, LensUnit.Millimeter, _ => null));
        Assert.Contains("Surface 1: a model glass needs the glass catalogs", ex.Message);
        Assert.Contains("Surface 2: a Standard surface has no parameters", ex.Message);
    }

    [Fact]
    public void TheMaterialCell_ReadsNamesMirrorsIndicesAndModelGlasses()
    {
        var s = new OpticsSurfaceSettings();
        Assert.Equal("N-BK7", SavedSurfaces.ParseMaterial(s, "N-BK7", out _)!.Material);
        Assert.Equal(SavedSurfaces.Mirror, SavedSurfaces.ParseMaterial(s, " Mirror ", out _)!.Material);
        Assert.Equal(1.5, SavedSurfaces.ParseMaterial(s, "1.5", out _)!.Index);
        var model = SavedSurfaces.ParseMaterial(s, "1.6, 40, 0.01", out _)!.ModelGlass!;
        Assert.Equal((1.6, 40.0, 0.01), (model.Nd, model.Vd, model.DeltaPgF));
        Assert.True(SavedSurfaces.IsAir(SavedSurfaces.ParseMaterial(s with { Index = 2 }, "", out _)!));
        Assert.Null(SavedSurfaces.ParseMaterial(s, "-1", out string? error));
        Assert.Contains("positive", error);
        Assert.Null(SavedSurfaces.ParseMaterial(s, "1.6, x", out error));

        // The cell's text reads back to the same fields.
        foreach (var text in new[] { "N-BK7", "MIRROR", "1.5", "1.6, 40, 0.01", "" })
        {
            var parsed = SavedSurfaces.ParseMaterial(s, text, out _)!;
            Assert.Equal(Json(parsed), Json(SavedSurfaces.ParseMaterial(s, SavedSurfaces.FormatMaterial(parsed), out _)!));
        }
    }

    // ---- The analyses read the project's surfaces ----

    [Fact]
    public void TheAnalyses_ReadTheProjectsSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var context = new OpticsAnalysisContext("Singlet", new OpticsSettings { Surfaces = Singlet() });
        foreach (var id in new[] { ParaxialDataAnalysis.SavedId, SingleRayTraceAnalysis.SavedId, GaussianBeamAnalysis.SavedId })
        {
            Assert.True(registry.TryGet(id, out var analysis));
            var result = analysis.Compute(context, analysis.CreateDefaultSettings(), default);
            Assert.DoesNotContain("No surfaces", result.Sections.Select(s => s.Heading));
        }
        Assert.Contains("Effective focal length", registry.All.First(a => a.Id == ParaxialDataAnalysis.SavedId)
            .Compute(context, new ParaxialDataSettings(), default).Text);

        // A lens that cannot be converted fails loudly with the surface named.
        var bad = Singlet();
        bad[1] = bad[1] with { Index = null, Material = "N-NOTAGLASS" };
        Assert.True(registry.TryGet(SingleRayTraceAnalysis.SavedId, out var trace));
        var ex = Assert.Throws<ArgumentException>(() => trace.Compute(new OpticsAnalysisContext("P", new OpticsSettings { Surfaces = bad }), trace.CreateDefaultSettings(), default));
        Assert.Contains("N-NOTAGLASS", ex.Message);
    }

    // ---- Reverse Elements ----

    /// <summary>The gate: a symmetric lens reversed is the same lens, so every first-order
    /// quantity is unchanged; and reversing an asymmetric singlet keeps its effective focal
    /// length (the system matrix of the reversed thick lens has the same C element, derived
    /// from the product of the refraction and transfer matrices).</summary>
    [Fact]
    public void ReversingASymmetricLens_LeavesFirstOrderDataUnchanged()
    {
        // The stop is a plane in front, so it does not move with the reversed group.
        var symmetric = Lens(double.PositiveInfinity, S(null, 10, stop: true), S(50, 5, 1.5), S(-50, 45));
        var reversed = ReverseElements.Apply(symmetric, 2, 3).Surfaces;
        Assert.Equal(Json(symmetric), Json(reversed));
        var (a, b) = (Paraxial(symmetric), Paraxial(reversed));
        Assert.Equal(a.EffectiveFocalLength, b.EffectiveFocalLength);
        Assert.Equal(a.BackFocalLength, b.BackFocalLength);
        Assert.Equal(a.FrontFocalLength, b.FrontFocalLength);
        Assert.Equal(a.ParaxialImageDistance, b.ParaxialImageDistance);
        Assert.Equal(a.EntrancePupilDiameter, b.EntrancePupilDiameter);

        var singlet = Singlet();
        var turned = ReverseElements.Apply(singlet, 1, 2).Surfaces;
        Assert.Equal(120, turned[1].Radius);                              // −(−120)
        Assert.Equal(-40, turned[2].Radius);
        Assert.Equal(6, turned[1].Thickness);
        Assert.Equal(1.5, turned[1].Index);
        Assert.Equal(90, turned[2].Thickness);                             // the gap after the group stays
        Close(Paraxial(singlet).EffectiveFocalLength!.Value, Paraxial(turned).EffectiveFocalLength!.Value, 1e-14, "EFL of the reversed singlet");
        Assert.Equal(Json(singlet), Json(ReverseElements.Apply(turned, 1, 2).Surfaces));   // twice is the original

        // Solves on the reversed cells go; a variable radius moves with its value.
        var flagged = singlet.ToList();
        flagged[1] = SurfaceFlags.With(flagged[1], SavedSurfaces.RadiusColumn, SurfaceFlags.Variable(SavedSurfaces.RadiusColumn));
        flagged[2] = SurfaceFlags.With(flagged[2], SavedSurfaces.ThicknessColumn, new OpticsCellFlagSettings { Kind = SavedSurfaces.SolveFlag, Solve = "Marginal ray height" });
        var result = ReverseElements.Apply(flagged, 1, 2);
        Assert.NotNull(SurfaceFlags.Of(result.Surfaces[2], SavedSurfaces.RadiusColumn));
        Assert.All(result.Surfaces, s => Assert.Null(SurfaceFlags.Of(s, SavedSurfaces.ThicknessColumn)));
        Assert.Contains(result.Notes, n => n.Contains("1 solve"));

        Assert.Throws<ArgumentException>(() => ReverseElements.Apply(singlet, 0, 2));       // not the object
        Assert.Throws<ArgumentException>(() => ReverseElements.Apply(singlet, 1, 3));       // not the image
    }

    // ---- Scale Lens and Make Focal ----

    [Fact]
    public void ScaleLens_ScalesLengthsOnly()
    {
        var lens = Singlet();
        lens[1] = lens[1] with { Conic = -0.7, SemiDiameter = 10, Aperture = new OpticsSurfaceApertureSettings { Type = SavedSurfaces.RectangularAperture, HalfWidthX = 3, HalfWidthY = 4 } };
        var scaled = ScaleLens.Apply(lens, 2).Surfaces;
        Assert.Equal(80, scaled[1].Radius);
        Assert.Equal(12, scaled[1].Thickness);
        Assert.Equal(20, scaled[1].SemiDiameter);
        Assert.Equal(-0.7, scaled[1].Conic);
        Assert.Equal((6.0, 8.0), (scaled[1].Aperture!.HalfWidthX, scaled[1].Aperture!.HalfWidthY));
        Assert.Equal(1.5, scaled[1].Index);
        Assert.True(scaled[0].ThicknessIsInfinite);
        Assert.Equal(-240, scaled[2].Radius);
        Close(2 * Paraxial(lens).EffectiveFocalLength!.Value, Paraxial(scaled).EffectiveFocalLength!.Value, 1e-14, "EFL scales with the lens");

        // A range scales only its surfaces; another type is refused; a factor must be positive.
        var front = ScaleLens.Apply(lens, 3, 1, 1).Surfaces;
        Assert.Equal((120.0, -120.0), (front[1].Radius!.Value, front[2].Radius!.Value));
        var other = lens.ToList();
        other[2] = other[2] with { Type = "Zernike Standard Sag" };   // TC-6: a type that is not registered
        Assert.Contains("Zernike Standard Sag", Assert.Throws<ArgumentException>(() => ScaleLens.Apply(other, 2)).Message);
        Assert.Throws<ArgumentException>(() => ScaleLens.Apply(lens, 0));
        // The lens-unit change converts the shared columns of any type.
        Assert.Equal(-60, ScaleLens.ConvertUnits(other, 0.5).Surfaces[2].Radius);
    }

    /// <summary>The gate: Make Focal hits the target focal length to 1e-12. A uniform scale of
    /// every length at fixed indices scales the system matrix's power by 1/k exactly in exact
    /// arithmetic, so only rounding is left.</summary>
    [Theory]
    [InlineData(123.456)]
    [InlineData(10)]
    [InlineData(1234.5)]
    public void MakeFocal_HitsTheTargetFocalLength(double target)
    {
        var context = Context();
        var focal = MakeFocal.Apply(Singlet(), target, context).Surfaces;
        Close(target, MakeFocal.EffectiveFocalLength(focal, context), 1e-12, "focal length after Make Focal");

        // In inches too (the target is in lens units).
        var inches = Context(new OpticsSystemSettings { LensUnit = ConventionNames.LensUnits.SavedName(LensUnit.Inch), ApertureValue = 0.4 });
        var inInches = MakeFocal.Apply(Singlet(), target / 25.4, inches).Surfaces;
        Close(target / 25.4, MakeFocal.EffectiveFocalLength(inInches, inches), 1e-12, "focal length in inches");
    }

    [Fact]
    public void MakeFocal_RefusesASignChange_AndAnAfocalLens()
    {
        Assert.Contains("sign", Assert.Throws<ArgumentException>(() => MakeFocal.Apply(Singlet(), -50, Context())).Message);
        var flat = Lens(double.PositiveInfinity, S(null, 5, 1.5, stop: true), S(null, 10));
        Assert.Contains("afocal", Assert.Throws<ArgumentException>(() => MakeFocal.Apply(flat, 50, Context())).Message);
    }

    // ---- Make Double Pass ----

    /// <summary>
    /// The gate: a double-pass system returns to the object with the path doubled. Every surface
    /// before the mirror, and the mirror, is a sphere centred on the axial object point (the
    /// object is 10 mm before surface 1 of radius −10; surface 2 is 15 mm from the object with
    /// radius −15; the mirror 50 mm with radius −50), so every ray from that point meets each
    /// surface along its normal, is not deviated, is reflected straight back and retraces its
    /// path (derived from Snell's law with zero angle of incidence). So after Make Double Pass
    /// every ray must end on the axial object point, on the object plane, with twice the one-way
    /// optical path 10 + 1.5·5 + 35 = 52.5 mm.
    /// </summary>
    [Fact]
    public void ADoublePassSystem_ReturnsToTheObject_WithThePathDoubled()
    {
        var single = Lens(10, S(-10, 5, 1.5, stop: true), S(-15, 35), S(-50, 0));
        var result = MakeDoublePass.Apply(single, 3);
        var surfaces = result.Surfaces;
        Assert.Equal(7, surfaces.Count);
        Assert.Equal(SavedSurfaces.Mirror, surfaces[3].Material);
        Assert.Equal(-35, surfaces[3].Thickness);
        Assert.Equal((-15.0, -5.0, 1.5), (surfaces[4].Radius!.Value, surfaces[4].Thickness, surfaces[4].Index!.Value));
        Assert.Equal((-10.0, -10.0), (surfaces[5].Radius!.Value, surfaces[5].Thickness));
        Assert.True(SavedSurfaces.IsAir(surfaces[5]));
        Assert.False(surfaces[4].IsStop || surfaces[5].IsStop);
        // The copies pick their data up from the originals.
        var pickup = SurfaceFlags.Of(surfaces[4], SavedSurfaces.ThicknessColumn)!;
        Assert.Equal((SavedSurfaces.PickupFlag, 1, -1.0), (pickup.Kind, pickup.PickupSurface, pickup.PickupScale));
        Assert.Equal(2, SurfaceFlags.Of(surfaces[4], SavedSurfaces.RadiusColumn)!.PickupSurface);
        // The image lies on the object plane: the thicknesses from surface 1 sum to −10.
        Assert.Equal(-10, surfaces.Skip(1).Take(surfaces.Count - 2).Sum(s => s.Thickness));

        var context = Context();
        var lens = context.ToLens(surfaces);
        var system = context.System();
        var trace = SequentialTrace.Create(lens, system);
        var launcher = new RayLauncher(lens, system);
        foreach (var (px, py) in new[] { (0.0, 0.0), (0.0, 1.0), (1.0, 0.0), (-0.6, 0.7), (0.3, -0.4) })
        {
            var path = trace.TracePath(launcher.Launch(trace, 0, 0, px, py));
            Assert.True(path.Final.IsOk, $"pupil ({px}, {py}): {path.Status}");
            var image = path.Hits[^1];
            Assert.True(Math.Abs(image.GlobalPosition.X) < 1e-15 && Math.Abs(image.GlobalPosition.Y) < 1e-15,
                $"pupil ({px}, {py}) lands at ({image.GlobalPosition.X:R}, {image.GlobalPosition.Y:R})");
            Close(-0.010, image.GlobalPosition.Z, 1e-13, "image plane = object plane");
            double oneWay = path.Hits[3].OpticalPath;
            Close(0.010 + 1.5 * 0.005 + 0.035, oneWay, 1e-13, "one-way path");
            Close(2 * oneWay, path.Final.OpticalPath, 1e-13, "double-pass path");
        }
    }

    [Fact]
    public void MakeDoublePass_RemovesSolves_AndRefusesAnEarlierMirror()
    {
        var lens = Singlet();
        lens[1] = SurfaceFlags.With(lens[1], SavedSurfaces.RadiusColumn, new OpticsCellFlagSettings { Kind = SavedSurfaces.SolveFlag, Solve = "F/#", SolveParameters = new() { 4 } });
        var result = MakeDoublePass.Apply(lens, 2);
        Assert.Null(SurfaceFlags.Of(result.Surfaces[1], SavedSurfaces.RadiusColumn));
        Assert.Contains(result.Notes, n => n.Contains("1 solve removed"));
        Assert.Contains(result.Notes, n => n.Contains("infinity"));       // the object at infinity

        var mirrors = Lens(10, S(null, 5) with { Material = SavedSurfaces.Mirror }, S(null, -5));
        Assert.Contains("mirror", Assert.Throws<ArgumentException>(() => MakeDoublePass.Apply(mirrors, 2)).Message);
    }

    // ---- Apertures ----

    [Fact]
    public void TheApertureTools_ActOnSurfacesWithoutApertures()
    {
        var lens = Lens(double.PositiveInfinity, S(40, 6, 1.5, stop: true, semiDiameter: 6), S(-120, 90, semiDiameter: 5.5));
        lens[2] = lens[2] with { Aperture = new OpticsSurfaceApertureSettings { Type = SavedSurfaces.EllipticalAperture, HalfWidthX = 4, HalfWidthY = 5 } };

        var floating = ApertureTools.ToFloating(lens).Surfaces;
        Assert.Null(floating[0].Aperture);                                 // never the object
        Assert.Equal(SavedSurfaces.FloatingAperture, floating[1].Aperture!.Type);
        Assert.Equal(SavedSurfaces.EllipticalAperture, floating[2].Aperture!.Type);   // kept
        Assert.Equal(SavedSurfaces.FloatingAperture, floating[3].Aperture!.Type);

        var circular = ApertureTools.ToCircular(lens, Context()).Surfaces;
        Assert.Equal((0.0, 6.0), (circular[1].Aperture!.MinRadius, circular[1].Aperture!.MaxRadius));
        Assert.Equal(6, circular[1].SemiDiameter);
        Assert.Equal(SavedSurfaces.EllipticalAperture, circular[2].Aperture!.Type);
        Assert.True(circular[3].SemiDiameter > 0);                         // the image's automatic size, now fixed
        Assert.Equal(circular[3].SemiDiameter, circular[3].Aperture!.MaxRadius);

        var maximum = ApertureTools.ToMaximum(lens, Context()).Surfaces;
        Assert.Equal(ApertureTools.MaximumSolve, SurfaceFlags.Of(maximum[1], SavedSurfaces.SemiDiameterColumn)!.Solve);
        Assert.Equal(ApertureTools.ClearSemiDiameters(lens.Select(s => s.Aperture is null ? s with { SemiDiameter = null } : s).ToList(), Context())[1], maximum[1].SemiDiameter);
        Assert.Null(SurfaceFlags.Of(maximum[2], SavedSurfaces.SemiDiameterColumn));

        Assert.All(ApertureTools.RemoveAll(circular).Surfaces, s => Assert.Null(s.Aperture));
    }

    // ---- Variables ----

    [Fact]
    public void TheVariableTools_FollowTheirRules()
    {
        // Surface 3 is curved but has air on both sides; surface 4 is a plane; surface 5 has zero thickness.
        var lens = Lens(100, S(40, 6, 1.5, stop: true), S(-120, 10), S(30, 5), S(null, 0, 1.6), S(-50, 20));
        var radii = VariableTools.SetAllRadiiVariable(lens).Surfaces;
        Assert.NotNull(SurfaceFlags.Of(radii[1], SavedSurfaces.RadiusColumn));
        Assert.NotNull(SurfaceFlags.Of(radii[2], SavedSurfaces.RadiusColumn));
        Assert.Null(SurfaceFlags.Of(radii[3], SavedSurfaces.RadiusColumn));
        Assert.Null(SurfaceFlags.Of(radii[4], SavedSurfaces.RadiusColumn));
        Assert.NotNull(SurfaceFlags.Of(radii[5], SavedSurfaces.RadiusColumn));

        var thicknesses = VariableTools.SetAllThicknessesVariable(lens).Surfaces;
        Assert.Null(SurfaceFlags.Of(thicknesses[0], SavedSurfaces.ThicknessColumn));   // the object distance
        Assert.NotNull(SurfaceFlags.Of(thicknesses[1], SavedSurfaces.ThicknessColumn));
        Assert.Null(SurfaceFlags.Of(thicknesses[4], SavedSurfaces.ThicknessColumn));   // zero
        Assert.Null(SurfaceFlags.Of(thicknesses[^1], SavedSurfaces.ThicknessColumn));  // the image

        // A solved radius is left alone; Remove All Variables keeps solves.
        var solved = lens.ToList();
        solved[1] = SurfaceFlags.With(solved[1], SavedSurfaces.RadiusColumn, new OpticsCellFlagSettings { Kind = SavedSurfaces.SolveFlag, Solve = "Aplanatic" });
        var kept = VariableTools.SetAllRadiiVariable(solved);
        Assert.Equal(SavedSurfaces.SolveFlag, SurfaceFlags.Of(kept.Surfaces[1], SavedSurfaces.RadiusColumn)!.Kind);
        Assert.Contains(kept.Notes, n => n.Contains("left as"));
        var fixedAgain = VariableTools.RemoveAllVariables(kept.Surfaces).Surfaces;
        Assert.Equal(SavedSurfaces.SolveFlag, SurfaceFlags.Of(fixedAgain[1], SavedSurfaces.RadiusColumn)!.Kind);
        Assert.All(fixedAgain, s => Assert.DoesNotContain(s.Flags ?? new(), f => f.Kind == SavedSurfaces.VariableFlag));
    }

    // ---- System Check and the File Comparator ----

    [Fact]
    public void SystemCheck_ReportsErrorsAndWarnings()
    {
        Assert.DoesNotContain(SystemCheck.Run(Singlet(), Context()), f => f.Severity == CheckSeverity.Error);

        var lens = Singlet();
        lens[1] = SurfaceFlags.With(lens[1] with { Coating = "NOPE" }, SavedSurfaces.RadiusColumn,
            new OpticsCellFlagSettings { Kind = SavedSurfaces.SolveFlag, Solve = "F/#" });
        lens[2] = SurfaceFlags.With(lens[2], SavedSurfaces.RadiusColumn, SurfaceFlags.Pickup(SavedSurfaces.RadiusColumn, 1, SavedSurfaces.RadiusColumn, -1));
        var findings = SystemCheck.Run(lens, Context() with { CoatingNames = new[] { "AR" } });
        Assert.Contains(findings, f => f.Severity == CheckSeverity.Warning && f.Message.Contains("'NOPE'"));
        // TC-6: solves are evaluated, so an F/# solve with no F/# is an error naming its cell, and
        // the pickup is evaluated rather than compared with the value it holds.
        Assert.Contains(findings, f => f.Severity == CheckSeverity.Error && f.Message.Contains("Surface 1, curvature, f/# solve"));
        Assert.DoesNotContain(findings, f => f.Message.Contains("holds -120"));
        // A chip-zone pickup is kept but not evaluated, and says so.
        var chip = Singlet();
        chip[2] = SurfaceFlags.With(chip[2], SavedSurfaces.ChipZoneColumn, SurfaceFlags.Pickup(SavedSurfaces.ChipZoneColumn, 1, SavedSurfaces.ChipZoneColumn));
        Assert.Contains(SystemCheck.Run(chip, Context()), f => f.Severity == CheckSeverity.Warning && f.Message.Contains("not evaluated"));

        var broken = Singlet();
        broken[1] = broken[1] with { IsStop = false };
        Assert.Contains(SystemCheck.Run(broken, Context()), f => f.Severity == CheckSeverity.Error);

        // A ray that misses: a strongly curved surface the marginal ray cannot reach.
        var miss = Lens(double.PositiveInfinity, S(4, 6, 1.5, stop: true), S(-4, 10));
        Assert.Contains(SystemCheck.Run(miss, Context()), f => f.Message.Contains("marginal"));
    }

    [Fact]
    public void TheFileComparator_ListsDifferencesSurfaceBySurface()
    {
        var a = new OpticsSettings { SystemData = new OpticsSystemSettings(), Surfaces = Singlet() };
        Assert.Empty(FileComparator.Compare(a, a));
        Assert.Contains("same lens data", FileComparator.Format(Array.Empty<LensComparisonLine>(), "A", "B"));

        var changed = Singlet();
        changed[2] = changed[2] with { Radius = -100 };
        var b = new OpticsSettings { SystemData = new OpticsSystemSettings { ApertureValue = 12 }, Surfaces = changed };
        var lines = FileComparator.Compare(a, b);
        Assert.Equal(new[] { "System: ApertureValue", "Surface 2: Radius" }, lines.Select(l => l.Item));
        Assert.Equal(("-120", "-100"), (lines[1].Left, lines[1].Right));

        var longer = new OpticsSettings { Surfaces = Singlet().Append(new OpticsSurfaceSettings()).ToList() };
        Assert.Contains(FileComparator.Compare(a, longer), l => l.Item == "Surface 4" && l.Left == "(none)");
        Assert.True(FileComparator.Compare(a, a, differencesOnly: false).Count > 20);
    }
}
