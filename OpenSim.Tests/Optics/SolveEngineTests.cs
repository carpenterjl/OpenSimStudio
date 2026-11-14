using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Solves;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-29 gate: each solve checked by its own definition, as OpticStudio's 2025 R2 help states it
/// (ansyshelp.ansys.com, read 2026-11-14, saved in plans/sources/optics/solves/). Every expected
/// value is either the definition itself evaluated on the solved lens, a closed form derived in
/// the test's comment, or (one case) the help's own worked number, cited where used. Radii,
/// thicknesses and indices are arbitrary test inputs, not reference data.
/// </summary>
public sealed class SolveEngineTests
{
    // ---- Builders ----

    private static SequentialSurface S(double radius, double thickness, double n = 1, double k = 0, bool stop = false,
        bool mirror = false, double? semiDiameter = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, SemiDiameter = semiDiameter,
    };

    private static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) =>
        LensIn(1, objectDistance, surfaces);

    private static SequentialLens LensIn(double objectIndex, double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance, Medium = ParaxialMedium.Constant(objectIndex) } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd = 0.01, params double[] angleFields) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = angleFields.Length == 0 ? new[] { new FieldPoint() } : angleFields.Select(a => new FieldPoint { Y = a }).ToArray()
        },
    };

    /// <summary>A singlet at infinite conjugates: surfaces 1 (stop) and 2, image 3.</summary>
    private static SequentialLens Singlet() => Lens(double.PositiveInfinity,
        S(0.05, 0.005, 1.5, stop: true), S(-0.1, 0.09));

    /// <summary>Four surfaces (an air-spaced doublet): 1 (stop) to 4, image 5.</summary>
    private static SequentialLens Doublet() => Lens(double.PositiveInfinity,
        S(0.05, 0.005, 1.5, stop: true), S(-0.1, 0.002), S(-0.08, 0.004, 1.6), S(0.2, 0.08));

    private static readonly OpticalSystemData Field5 = Sys(0.01, 0, 5);

    private static ParaxialSystem Paraxial(SequentialLens lens, OpticalSystemData system) =>
        ParaxialSystem.Create(lens.ToParaxialLens(), system);

    private static SolveResult Solve(SequentialLens lens, OpticalSystemData system, params SurfaceSolve[] solves)
    {
        var result = SolveEngine.Apply(lens, system, solves);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        return result;
    }

    private static void Close(double expected, double actual, double tolerance, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    /// <summary>Vertex z of each surface from surface 1's vertex (no tilts).</summary>
    private static double Z(SequentialLens lens, int j)
    {
        double z = 0;
        for (int i = 1; i < j; i++) z += lens.Surfaces[i].Thickness;
        return z;
    }

    // ---- Curvature solves ----

    [Fact]
    public void MarginalRayAngle_SetsTheExitSlope_AndTheHelpsFocalLength()
    {
        // "Curvature Solves": an exit angle of −EPD/2/EFL gives that EFL. With a 10 mm pupil and
        // −0.1 the EFL is 0.005/0.1 = 0.05 m (the help's example scaled to this pupil).
        var system = Sys(0.01);
        var result = Solve(Singlet(), system, new MarginalRayAngleSolve { Surface = 2, Angle = -0.1 });
        var p = Paraxial(result.Lens, system);
        Close(-0.1, p.MarginalRay.Slopes[2], 1e-15, "marginal slope after surface 2");
        Close(0.05, p.EffectiveFocalLength!.Value, 1e-15, "EFL");
        Assert.Equal(result.Statuses[0].Value, result.Lens.Surfaces[2].Curvature);
    }

    [Fact]
    public void ChiefRayAngle_SetsTheChiefSlope()
    {
        var result = Solve(Singlet(), Field5, new ChiefRayAngleSolve { Surface = 2, Angle = 0.02 });
        Close(0.02, Paraxial(result.Lens, Field5).ChiefRay.Slopes[2], 1e-15, "chief slope after surface 2");
    }

    [Fact]
    public void FNumber_GivesExitSlopeMinusOneOverTwoF_AndThatFNumber()
    {
        // "Curvature Solves": the exit angle is −1/2F. For an object at infinity the marginal ray
        // enters at y = EPD/2 and leaves with u′ = −y/EFL, so EFL/EPD = 1/(2|u′|) = F (derived).
        var system = Sys(0.01);
        var result = Solve(Singlet(), system, new FNumberSolve { Surface = 2, FNumber = 4 });
        var p = Paraxial(result.Lens, system);
        Close(-1.0 / 8, p.MarginalRay.Slopes[2], 1e-15, "exit slope");
        Close(4, p.ImageSpaceFNumber!.Value, 1e-13, "F/#");
    }

    [Fact]
    public void RayNormalSolves_PutTheCentreOfCurvatureOnTheIncidentRay_SoItIsNotBent()
    {
        // Normal to the ray: the incident ray y + u·z passes through the centre z = R (from the
        // vertex), y + u·R = 0. Then ν′ = ν − y·c·(n′ − n) with c = −u/y gives n′u′ = n′u: the ray
        // leaves undeviated (derived).
        var lens = Doublet();
        var marginal = Solve(lens, Field5, new MarginalRayNormalSolve { Surface = 3 });
        var p = Paraxial(marginal.Lens, Field5);
        double r = marginal.Lens.Surfaces[3].Radius;
        Close(0, p.MarginalRay.Heights[3] + p.MarginalRay.Slopes[2] * r, 1e-17, "marginal ray through the centre");
        Close(p.MarginalRay.Slopes[2], p.MarginalRay.Slopes[3], 1e-15, "marginal slope unchanged");

        var chief = Solve(lens, Field5, new ChiefRayNormalSolve { Surface = 3 });
        var q = Paraxial(chief.Lens, Field5);
        double rc = chief.Lens.Surfaces[3].Radius;
        Close(0, q.ChiefRay.Heights[3] + q.ChiefRay.Slopes[2] * rc, 1e-17, "chief ray through the centre");
        Close(q.ChiefRay.Slopes[2], q.ChiefRay.Slopes[3], 1e-15, "chief slope unchanged");
    }

    [Fact]
    public void Aplanatic_MeetsTheConditionAndImagesTheAxialPointWithoutAberration()
    {
        // Object point in glass (n = 1.5) 0.02 m before surface 1, refracting into air (n′ = 1).
        // The solve makes u′/n′ = u/n. Derived (SolveEngine.AplanaticCurvature): R = n·l/(n + n′)
        // with l = −0.02 the object from the vertex, so R = −0.012, the centre at z = −0.012 and
        // the virtual image at R + R·n/n′ = −0.03. A sphere images that pair of points with no
        // spherical aberration at any aperture, so every real ray from the axial point must
        // leave pointing at z = −0.03.
        var system = Sys(0.01);
        var lens = LensIn(1.5, 0.02, S(0.1, 0.05, stop: true));
        var result = Solve(lens, system, new AplanaticSolve { Surface = 1 });
        var p = Paraxial(result.Lens, system);
        Close(p.MarginalRay.Slopes[0] / 1.5, p.MarginalRay.Slopes[1] / 1.0, 1e-15, "u′/n′ against u/n");
        Close(-0.012, result.Lens.Surfaces[1].Radius, 1e-15, "radius");

        var solved = result.Lens;
        var trace = SequentialTrace.Create(solved, system);
        var launcher = new RayLauncher(solved, system);
        foreach (double py in new[] { 0.1, 0.25, 0.5, 0.75, 1.0 })
        {
            var hit = trace.TracePath(launcher.Launch(trace, 0, 0, 0, py)).Hits.First(h => h.Surface == 1);
            double crossing = hit.Position.Z - hit.Position.Y * hit.Direction.Z / hit.Direction.Y;
            Close(-0.03, crossing, 1e-14, $"axis crossing of the ray at Py = {py}");
        }
    }

    [Fact]
    public void ElementPower_GivesThatPowerToTheTwoSurfaces()
    {
        // The element's power is −C of the matrix from just before surface 1 to just after surface 2.
        var result = Solve(Singlet(), Field5, new ElementPowerSolve { Surface = 2, Power = 15 });
        var p = Paraxial(result.Lens, Field5);
        Close(15, -p.Matrix(1, 2).C, 1e-13, "element power");
    }

    [Fact]
    public void ConcentricSolves_PlaceTheCentreOfCurvatureWhereTheyAreAsked()
    {
        var lens = Doublet();
        var withSurface = Solve(lens, Field5, new ConcentricWithSurfaceSolve { Surface = 3, Of = 1 }).Lens;
        Close(Z(withSurface, 1), Z(withSurface, 3) + withSurface.Surfaces[3].Radius, 1e-17, "centre of 3 on vertex 1");

        var withRadius = Solve(lens, Field5, new ConcentricWithRadiusSolve { Surface = 4, Of = 2 }).Lens;
        Close(Z(withRadius, 2) + withRadius.Surfaces[2].Radius, Z(withRadius, 4) + withRadius.Surfaces[4].Radius, 1e-16,
            "centre of 4 on centre of 2");
    }

    // ---- Thickness solves ----

    [Fact]
    public void MarginalRayHeight_LeavesTheParaxialMarginalRayAtThatHeightOnTheNextSurface()
    {
        // The gate: a zero-height solve on the last thickness puts the image at the paraxial focus.
        var system = Sys(0.01);
        var focus = Solve(Singlet(), system, new MarginalRayHeightSolve { Surface = 2 }).Lens;
        var p = Paraxial(focus, system);
        Close(0, p.MarginalRay.Heights[3], 1e-17, "marginal height on the image");
        Close(p.ParaxialImageDistance!.Value, focus.Surfaces[2].Thickness, 1e-16, "thickness = paraxial image distance");

        var lens = Doublet();
        var h = Solve(lens, system, new MarginalRayHeightSolve { Surface = 2, Height = 0.001 }).Lens;
        Close(0.001, Paraxial(h, system).MarginalRay.Heights[3], 1e-17, "marginal height on surface 3");
    }

    [Fact]
    public void MarginalRayHeight_WithAPupilZone_UsesTheRealRay()
    {
        // "Thickness Solves": a non-zero zone traces the real ray of that Py from the on-axis
        // field; its height on the next vertex plane is then the target (here the image plane).
        var system = Sys(0.02);
        var lens = Solve(Singlet(), system, new MarginalRayHeightSolve { Surface = 2, PupilZone = 0.7 }).Lens;
        var trace = SequentialTrace.Create(lens, system);
        var ray = trace.Trace(new RayLauncher(lens, system).Launch(trace, 0, 0, 0, 0.7));
        Assert.True(ray.IsOk);
        Close(0, ray.Position.Y, 1e-16, "real 0.7-zone ray height on the image");
        // Spherical aberration moves the zonal focus away from the paraxial one.
        Assert.NotEqual(Paraxial(lens, system).ParaxialImageDistance!.Value, lens.Surfaces[2].Thickness);
    }

    [Fact]
    public void MarginalRayHeight_BeforeTheStop_IsRefused()
    {
        // "Restrictions": marginal ray height solves may not be placed before the stop.
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5), S(-0.1, 0.01, stop: true), S(double.PositiveInfinity, 0.09));
        var result = SolveEngine.Apply(lens, Sys(), new[] { new MarginalRayHeightSolve { Surface = 1 } });
        Assert.False(result.Succeeded);
        Assert.Contains("before the stop", result.Errors[0]);
        Assert.Same(lens, result.Lens);
    }

    [Fact]
    public void ChiefRayHeight_PutsTheChiefRayAtThatHeight()
    {
        var lens = Solve(Doublet(), Field5, new ChiefRayHeightSolve { Surface = 4, Height = 0.0005 }).Lens;
        Close(0.0005, Paraxial(lens, Field5).ChiefRay.Heights[5], 1e-17, "chief height on the image");
    }

    /// <summary>The conic sag z = c·r²/(1 + √(1 − (1 + k)c²r²)) (the root of
    /// c(r² + (1 + k)z²) − 2z = 0 through the vertex, Conventions §13), written out here.</summary>
    private static double ConicSagAt(SequentialSurface s, double r)
    {
        double c = s.Curvature;
        return c * r * r / (1 + Math.Sqrt(1 - (1 + s.Conic) * c * c * r * r));
    }

    [Fact]
    public void EdgeThickness_GivesTheStatedEdgeFromTheTwoSags()
    {
        // "Edge Thickness": E = Z(i+1) − Z(i) + T, both sags at the same radial height.
        var lens = Lens(double.PositiveInfinity, S(0.03, 0.004, 1.5, k: -0.6, stop: true), S(-0.04, 0.05, k: 1.5));
        var system = Sys(0.01);
        var given = Solve(lens, system, new EdgeThicknessSolve { Surface = 1, EdgeThickness = 0.0015, RadialHeight = 0.006 }).Lens;
        var s1 = given.Surfaces[1];
        Close(0.0015, ConicSagAt(given.Surfaces[2], 0.006) - ConicSagAt(s1, 0.006) + s1.Thickness, 1e-17, "edge at 6 mm");

        // Radial height 0: this surface's mechanical semi-diameter ("Thickness Solves").
        var automatic = Solve(lens, system, new EdgeThicknessSolve { Surface = 1, EdgeThickness = 0.001 }).Lens;
        double r = SemiDiameterSizing.Compute(automatic, system).Mechanical[1];
        var a1 = automatic.Surfaces[1];
        Close(0.001, ConicSagAt(automatic.Surfaces[2], r) - ConicSagAt(a1, r) + a1.Thickness, 1e-17, "edge at the semi-diameter");
    }

    [Fact]
    public void EdgeThickness_BeyondTheHemisphere_FailsLoudly()
    {
        var lens = Lens(double.PositiveInfinity, S(0.005, 0.004, 1.5, stop: true), S(-0.04, 0.05));
        var result = SolveEngine.Apply(lens, Sys(), new[] { new EdgeThicknessSolve { Surface = 1, EdgeThickness = 0.001, RadialHeight = 0.006 } });
        Assert.False(result.Succeeded);
        Assert.Contains("Surface 1, thickness, edge thickness solve", result.Errors[0]);
        Assert.Contains("not defined", result.Errors[0]);
        Assert.Equal(0.004, result.Lens.Surfaces[1].Thickness);
    }

    [Fact]
    public void OpticalPathDifference_ZeroAtTheRim_FocusesAParaboloidOnItsFocus()
    {
        // A paraboloidal mirror of radius R images infinity on its focus at R/2 with no
        // aberration (RealRayTraceTests derives it), so the only thickness at which the rim ray's
        // OPD is zero is R/2 (−0.1 m here, negative after the mirror).
        var system = Sys(0.05);
        var lens = Lens(double.PositiveInfinity, S(-0.2, -0.09, k: -1, stop: true, mirror: true));
        var solved = Solve(lens, system, new OpticalPathDifferenceSolve { Surface = 1, PupilZone = 1 }).Lens;
        Close(-0.1, solved.Surfaces[1].Thickness, 1e-14, "image distance");

        // A non-zero OPD target is met as the solve's own measure gives it.
        var quarter = Solve(lens, system, new OpticalPathDifferenceSolve { Surface = 1, PupilZone = 1, Waves = 0.25 }).Lens;
        Close(0.25, SolveEngine.OnAxisOpdWaves(quarter, system, 1), 1e-8, "OPD in waves");
        Assert.NotEqual(-0.1, quarter.Surfaces[1].Thickness);
    }

    [Fact]
    public void PositionAndCompensator_KeepTheirSums()
    {
        var lens = Doublet();
        // Reference before: thicknesses 2 through 4 sum to the length.
        var before = Solve(lens, Field5, new PositionSolve { Surface = 4, ReferenceSurface = 2, Length = 0.1 }).Lens;
        Close(0.1, before.Surfaces[2].Thickness + before.Surfaces[3].Thickness + before.Surfaces[4].Thickness, 1e-16, "sum from the reference");
        // Reference after: thicknesses 1 and 2 sum to the length.
        var after = Solve(lens, Field5, new PositionSolve { Surface = 1, ReferenceSurface = 3, Length = 0.01 }).Lens;
        Close(0.01, after.Surfaces[1].Thickness + after.Surfaces[2].Thickness, 1e-17, "sum to the reference");
        // Same surface: the thickness is the length.
        Assert.Equal(0.07, Solve(lens, Field5, new PositionSolve { Surface = 4, ReferenceSurface = 4, Length = 0.07 }).Lens.Surfaces[4].Thickness);
        // Compensator: T = S − R.
        var comp = Solve(lens, Field5, new CompensatorSolve { Surface = 3, ReferenceSurface = 1, Sum = 0.012 }).Lens;
        Close(0.012, comp.Surfaces[3].Thickness + comp.Surfaces[1].Thickness, 1e-17, "compensated sum");
    }

    [Fact]
    public void CenterOfCurvature_PutsTheNextVertexOnTheCentre()
    {
        var lens = Solve(Doublet(), Field5, new CenterOfCurvatureSolve { Surface = 3, Of = 2 }).Lens;
        Close(Z(lens, 2) + lens.Surfaces[2].Radius, Z(lens, 4), 1e-16, "vertex 4 on the centre of 2");
    }

    [Fact]
    public void PupilPosition_PutsTheNextSurfaceAtTheExitPupil()
    {
        // On the last surface the pupil of the image space is the exit pupil, so the image surface
        // lands on it and every chief ray crosses the axis there. Works with on-axis fields only.
        foreach (var system in new[] { Field5, Sys(0.01) })
        {
            var lens = Solve(Doublet(), system, new PupilPositionSolve { Surface = 4 }).Lens;
            var p = Paraxial(lens, Field5);
            Close(p.ExitPupilPosition!.Value, lens.Surfaces[4].Thickness, 1e-16, "image on the exit pupil");
            Close(0, p.ChiefRay.Heights[5], 1e-17, "chief height at the exit pupil");
        }
    }

    // ---- Material solves ----

    [Fact]
    public void ModelGlass_GivesTheRequestedNdAndVd_AndNeedsAFit()
    {
        var fit = ModelGlassFit.Train(new[] { "N-BK7", "SF57", "N-PK51", "F2", "N-LAF2" }.Select(GlassToolsTests.Schott));
        var options = new SolveOptions { ModelGlass = fit };
        var result = SolveEngine.Apply(Singlet(), Sys(), new[] { new ModelGlassSolve { Surface = 1, Nd = 1.62, Vd = 36, DeltaPgF = 0.002 } }, options);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        var p = GlassProperties.Compute(result.Lens.Surfaces[1].Medium.Material!);
        Close(1.62, p.Nd, 1e-14, "nd");
        Close(36, p.Vd, 1e-10, "Vd");
        Assert.Equal(2, result.Passes);   // the second pass finds the same material object

        var none = SolveEngine.Apply(Singlet(), Sys(), new[] { new ModelGlassSolve { Surface = 1, Nd = 1.62, Vd = 36 } });
        Assert.False(none.Succeeded);
        Assert.Contains("model-glass fit", none.Errors[0]);
    }

    [Fact]
    public void GlassOffset_MovesNdFromTheGlassAsEntered_AndReapplyingDoesNotAddItTwice()
    {
        var glass = GlassToolsTests.Schott("N-BK7");
        var lens = Singlet();
        lens = lens with { Surfaces = lens.Surfaces.Select((s, j) => j == 1 ? s with { Medium = ParaxialMedium.Of(glass) } : s).ToArray() };
        var solve = new GlassOffsetSolve { Surface = 1, DeltaNd = 0.001, DeltaVd = -0.5 };
        double nd = GlassProperties.Compute(glass).Nd;
        var once = Solve(lens, Sys(), solve).Lens;
        Close(nd + 0.001, GlassProperties.Compute(once.Surfaces[1].Medium.Material!).Nd, 1e-14, "nd once");
        var twice = Solve(once, Sys(), solve).Lens;
        Close(nd + 0.001, GlassProperties.Compute(twice.Surfaces[1].Medium.Material!).Nd, 1e-14, "nd after re-applying");

        var constant = SolveEngine.Apply(Singlet(), Sys(), new[] { solve });
        Assert.False(constant.Succeeded);
        Assert.Contains("catalog glass", constant.Errors[0]);
    }

    [Fact]
    public void Substitute_LeavesTheGlassAndChecksTheCatalog()
    {
        var glass = GlassToolsTests.Schott("N-BK7");
        var lens = Singlet();
        lens = lens with { Surfaces = lens.Surfaces.Select((s, j) => j == 1 ? s with { Medium = ParaxialMedium.Of(glass) } : s).ToArray() };
        var catalogs = new[] { new OpticalCatalog { Name = "schott", Materials = new[] { glass } } };
        var ok = SolveEngine.Apply(lens, Sys(), new[] { new SubstituteGlassSolve { Surface = 1, Catalog = "SCHOTT" } }, new SolveOptions { Catalogs = catalogs });
        Assert.True(ok.Succeeded);
        Assert.Same(lens.Surfaces[1].Medium, ok.Lens.Surfaces[1].Medium);
        Assert.Contains("not built", ok.Statuses[0].Note);

        var unknown = SolveEngine.Apply(lens, Sys(), new[] { new SubstituteGlassSolve { Surface = 1, Catalog = "hoya" } }, new SolveOptions { Catalogs = catalogs });
        Assert.False(unknown.Succeeded);
        Assert.Contains("hoya", unknown.Errors[0]);
    }

    // ---- Semi-diameter solves ----

    [Fact]
    public void SemiDiameterSolves_AutomaticAndMaximum()
    {
        var system = Field5;
        var lens = Doublet();
        lens = lens with { Surfaces = lens.Surfaces.Select((s, j) => j == 3 ? s with { SemiDiameter = 0.02 } : s).ToArray() };
        var automatic = Solve(lens, system, new AutomaticSemiDiameterSolve { Surface = 3 }).Lens;
        Assert.Null(automatic.Surfaces[3].SemiDiameter);

        var maximum = Solve(lens, system, new MaximumSemiDiameterSolve { Surface = 3 });
        double expected = SemiDiameterSizing.Compute(automatic, system).Clear[3];
        Assert.Equal((double?)expected, maximum.Lens.Surfaces[3].SemiDiameter);
        Assert.Contains("OP-30", maximum.Statuses[0].Note);
    }

    // ---- Pickups ----

    [Fact]
    public void CurvaturePickup_ScalesTheCurvature_AsTheHelpsExampleSays()
    {
        // "Curvature Solves": "if the targeted radius is 2 and the Scale Factor in the curvature
        // pickup solve is 0.5, the calculated value shown in the Lens Data Editor will be
        // 1/((1/2)*0.5) = 4" (lens units; meters here).
        var lens = Lens(double.PositiveInfinity, S(2, 0.005, 1.5, stop: true), S(-0.1, 0.09));
        var solved = Solve(lens, Sys(), new PickupSolve { Surface = 2, Target = SolveColumn.Curvature, FromSurface = 1, Scale = 0.5 }).Lens;
        Close(4, solved.Surfaces[2].Radius, 1e-15, "picked-up radius");
    }

    [Fact]
    public void Pickups_TakeOffsetPlusScaleTimesTheSource()
    {
        // "Thickness Solves" and "Parameter Solves": T = O + S·V.
        var lens = Doublet();
        lens = lens with { Surfaces = lens.Surfaces.Select((s, j) => j == 1 ? s with { ThermalExpansion = 7.1, Conic = -0.5 } : s).ToArray() };
        var result = Solve(lens, Field5,
            new PickupSolve { Surface = 3, Target = SolveColumn.Thickness, FromSurface = 1, Scale = 2, Offset = 0.001 },
            new PickupSolve { Surface = 3, Target = SolveColumn.Conic, FromSurface = 1, Scale = -2 },
            new PickupSolve { Surface = 3, Target = SolveColumn.Tce, FromSurface = 1 },
            new PickupSolve { Surface = 3, Target = SolveColumn.Material, FromSurface = 1 },
            new PickupSolve { Surface = 2, Target = SolveColumn.SemiDiameter, FromSurface = 1, Scale = 0.5 },
            new PickupSolve { Surface = 4, Target = SolveColumn.Thickness, FromSurface = 2, FromColumn = SolveColumn.SemiDiameter, Scale = 10 });
        var s = result.Lens.Surfaces;
        Assert.Equal(0.001 + 2 * 0.005, s[3].Thickness);
        Assert.Equal(1.0, s[3].Conic);
        Assert.Equal(7.1, s[3].ThermalExpansion);
        Assert.Same(s[1].Medium, s[3].Medium);
        // The source semi-diameter is automatic: the sizing of the lens as it stands.
        double clear = SemiDiameterSizing.Compute(lens, Field5).Clear[1];
        Assert.Equal((double?)(0.5 * clear), s[2].SemiDiameter);
        Assert.Equal(10 * (0.5 * clear), s[4].Thickness);
    }

    /// <summary>A plane with parameters, standing in for the shapes of OP-23 and OP-27.</summary>
    private sealed class ParameterPlane : SurfaceShape, IParameterizedShape
    {
        private readonly double[] _p;
        public ParameterPlane(params double[] p) => _p = p;
        public override string TypeName => "Test plane";
        public override double Sag(double x, double y, SequentialSurface surface) => 0;
        public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface, out Vector3D point, out double distance)
        {
            point = new Vector3D(x, y, 0);
            distance = 0;
            return true;
        }
        public override Vector3D Normal(Vector3D point, SequentialSurface surface) => Vector3D.UnitZ;
        public int ParameterCount => _p.Length;
        public double GetParameter(int index) => _p[index];
        public SurfaceShape WithParameter(int index, double value)
        {
            var copy = (double[])_p.Clone();
            copy[index] = value;
            return new ParameterPlane(copy);
        }
    }

    [Fact]
    public void ParameterPickup_ReadsAndWritesTheShapesParameters()
    {
        var lens = Doublet();
        lens = lens with
        {
            Surfaces = lens.Surfaces.Select((s, j) => j switch
            {
                2 => s with { Curvature = 0, Shape = new ParameterPlane(1, 2, 3) },
                3 => s with { Curvature = 0, Shape = new ParameterPlane(0, 0) },
                _ => s,
            }).ToArray()
        };
        var result = Solve(lens, Field5,
            new PickupSolve { Surface = 3, Target = SolveColumn.Parameter, TargetParameter = 1, FromSurface = 2, FromParameter = 2, Scale = 2, Offset = 1 });
        Assert.Equal(7, ((IParameterizedShape)result.Lens.Surfaces[3].Shape!).GetParameter(1));

        var standard = SolveEngine.Apply(lens, Field5, new[] { new PickupSolve { Surface = 4, Target = SolveColumn.Parameter, FromSurface = 2 } });
        Assert.False(standard.Succeeded);
        Assert.Contains("Standard", standard.Errors[0]);
    }

    [Fact]
    public void Pickups_AgainstTheHelpsRestrictions_AreRefused()
    {
        var lens = Doublet();
        var later = SolveEngine.Validate(lens, new[] { new PickupSolve { Surface = 2, Target = SolveColumn.Thickness, FromSurface = 3 } });
        Assert.Contains(later, p => p.Contains("before the target"));
        // On one surface a pickup may read an earlier column (conic from thickness), not a later one.
        var sameSurfaceLater = SolveEngine.Validate(lens, new[] { new PickupSolve { Surface = 2, Target = SolveColumn.Thickness, FromSurface = 2, FromColumn = SolveColumn.Conic } });
        Assert.Contains(sameSurfaceLater, p => p.Contains("before the target"));
        var sameSurfaceEarlier = SolveEngine.Validate(lens, new[] { new PickupSolve { Surface = 2, Target = SolveColumn.Conic, FromSurface = 2, FromColumn = SolveColumn.Thickness } });
        Assert.Empty(sameSurfaceEarlier);
        var curvatureOffset = SolveEngine.Validate(lens, new[] { new PickupSolve { Surface = 2, Target = SolveColumn.Curvature, FromSurface = 1, Offset = 1 } });
        Assert.Contains(curvatureOffset, p => p.Contains("no offset"));
        var materialScale = SolveEngine.Validate(lens, new[] { new PickupSolve { Surface = 3, Target = SolveColumn.Material, FromSurface = 1, Scale = 2 } });
        Assert.Contains(materialScale, p => p.Contains("no scale"));
        var twice = SolveEngine.Validate(lens, new SurfaceSolve[]
        {
            new MarginalRayHeightSolve { Surface = 4 }, new PupilPositionSolve { Surface = 4 },
        });
        Assert.Contains(twice, p => p.Contains("one solve"));
    }

    // ---- Iteration, re-application, failures ----

    [Fact]
    public void Iteration_ResolvesASolveThatReadsALaterSurface()
    {
        // Position on surface 1 against surface 3 (t1 + t2 = L) and t2 picked up as 0.5·t1:
        // the fixed point is t1 = L − 0.5·t1, t1 = 2L/3 (derived). One pass cannot get there,
        // because the position solve reads t2 before the pickup sets it.
        var lens = Doublet();
        const double length = 0.012;
        var solves = new SurfaceSolve[]
        {
            new PositionSolve { Surface = 1, ReferenceSurface = 3, Length = length },
            new PickupSolve { Surface = 2, Target = SolveColumn.Thickness, FromSurface = 1, Scale = 0.5 },
        };
        var result = SolveEngine.Apply(lens, Field5, solves, new SolveOptions { MaxPasses = 200 });
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        Assert.True(result.Passes > 2);
        Close(2 * length / 3, result.Lens.Surfaces[1].Thickness, 1e-13, "t1");
        Close(length / 3, result.Lens.Surfaces[2].Thickness, 1e-13, "t2");

        var single = SolveEngine.Apply(lens, Field5, solves, new SolveOptions { Iterate = false });
        Assert.True(single.Succeeded);
        Assert.Equal(1, single.Passes);
        Assert.Equal(length - 0.002, single.Lens.Surfaces[1].Thickness);

        // With a scale of 1 there is no fixed point but t1 = t2 = L/2 and the passes oscillate
        // from any other start; the engine says so instead of returning the last pass.
        var oscillating = SolveEngine.Apply(lens, Field5, new SurfaceSolve[]
        {
            solves[0], new PickupSolve { Surface = 2, Target = SolveColumn.Thickness, FromSurface = 1, Scale = 1 },
        });
        Assert.False(oscillating.Succeeded);
        Assert.Contains("did not converge", oscillating.Errors[0]);
    }

    [Fact]
    public void Reapplying_ToASolvedLens_ChangesNothing_AndFollowsAVariable()
    {
        // The optimizer's use: solves are re-applied after every change of a variable.
        var system = Sys(0.01);
        var solves = new SurfaceSolve[] { new MarginalRayAngleSolve { Surface = 4, Angle = -0.1 }, new MarginalRayHeightSolve { Surface = 4 } };
        var first = Solve(Doublet(), system, solves);
        var again = Solve(first.Lens, system, solves);
        Assert.Equal(1, again.Passes);
        Assert.Equal(first.Lens.Surfaces[4].Curvature, again.Lens.Surfaces[4].Curvature);
        Assert.Equal(first.Lens.Surfaces[4].Thickness, again.Lens.Surfaces[4].Thickness);

        var changed = first.Lens with
        {
            Surfaces = first.Lens.Surfaces.Select((s, j) => j == 2 ? s with { Thickness = 0.004 } : s).ToArray()
        };
        var followed = Solve(changed, system, solves).Lens;
        var p = Paraxial(followed, system);
        Close(-0.1, p.MarginalRay.Slopes[4], 1e-15, "angle after the change");
        Close(0, p.MarginalRay.Heights[5], 1e-17, "focus after the change");
        Assert.Equal(new[] { new SurfaceCell(4, SolveColumn.Curvature), new SurfaceCell(4, SolveColumn.Thickness) }, SolveEngine.SolvedCells(solves));
    }

    [Fact]
    public void ASolveThatCannotBeMet_FailsLoudlyAndNamesItsCell()
    {
        // A plane between two air spaces has no power: no curvature can bend the ray there.
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true), S(-0.1, 0.01), S(double.PositiveInfinity, 0.09));
        var result = SolveEngine.Apply(lens, Sys(), new[] { new MarginalRayAngleSolve { Surface = 3, Angle = 0 } });
        Assert.False(result.Succeeded);
        Assert.StartsWith("Surface 3, curvature, marginal ray angle solve:", result.Errors[0]);
        Assert.Contains("indices", result.Errors[0]);
        Assert.False(result.Statuses[0].Ok);
        Assert.Throws<InvalidOperationException>(() => result.LensOrThrow());

        var chiefOnAxis = SolveEngine.Apply(Singlet(), Sys(), new[] { new ChiefRayAngleSolve { Surface = 2, Angle = 0 } });
        Assert.Contains("on the axis", chiefOnAxis.Errors[0]);

        var notBuilt = SolveEngine.Apply(Singlet(), Sys(), new[] { new ParameterChiefRaySolve { Surface = 1, Parameter = 0 } });
        Assert.Contains("OP-21", notBuilt.Errors[0]);
    }
}
