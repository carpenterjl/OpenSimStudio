using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.StrayLight;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Tests.Optics.Gates;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-46 beyond the gate: the ghost construction, single bounces, refusals, YNI, the PAL/Freeform
/// power maps and the analyses' registration. Definitions from OpticStudio 2025 R2's help pages
/// saved under plans/sources/optics/op-46-help/ (cited at each use); every expected value is
/// derived in the comments from the test's inputs (radii, indices, distances: arbitrary).
/// </summary>
public sealed class StrayLightTests
{
    private const double R = 0.05, N = 1.5, Epd = 0.01;

    /// <summary>One refracting sphere (radius R, index N), stop on it, object at infinity.</summary>
    private static SequentialLens Sphere() => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            new SequentialSurface { Curvature = 1 / R, Thickness = 0.15, Medium = ParaxialMedium.Constant(N), IsStop = true },
            new SequentialSurface(),
        },
    };

    private static OpticalSystemData AngleSys(params double[] fields) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = Epd },
        Fields = new FieldSet { Type = FieldType.Angle, Points = fields.Select(f => new FieldPoint { Y = f }).ToArray() },
    };

    private static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    // ---- Ghost construction ----

    [Fact]
    public void TheGhostLens_FollowsTheDoublePassRule()
    {
        // Bounce at 2 then 1 of the plate: 0, 1 (in), 2 as a mirror with −t1, 1 as a mirror with
        // +t1, 2 (out) with t2, image. The second bounce's ghost index is 2j − i = 3.
        var ghost = GhostSystems.Build(GhostFocusGateTests.Plate(), 2, 1);
        var s = ghost.Lens.Surfaces;
        Assert.Equal(6, s.Count);
        Assert.True(s[2].IsMirror && s[3].IsMirror);
        Assert.Equal(-0.01, s[2].Thickness);
        Assert.Equal(0.01, s[3].Thickness);
        Assert.Equal(0.05, s[4].Thickness);
        Assert.Equal(3, ghost.SecondBounceIndex);
        Assert.Equal(new[] { GhostPass.End, GhostPass.In, GhostPass.FirstBounce, GhostPass.SecondBounce, GhostPass.Out, GhostPass.End },
            ghost.Origins.Select(o => o.Pass));
        Assert.Equal("GH002001", ghost.FileStem);
        Assert.True(s[1].IsStop);
    }

    [Fact]
    public void ASingleBounce_FocusesWhereThePlateMirrorsTheObject()
    {
        // Single bounce at the plate's rear face: the marginal ray reaches it at y = s0·u0 + t·u0/n,
        // comes back through the glass (+t/n again) and leaves the front face travelling back with
        // ν = u0 in a space of signed index −1, so its slope is −u0 and it crosses the axis at
        // z = −y/u = s0 + 2t/n from the front face. The ghost's image surface is the object plane,
        // at −s0, so the focus is 2·s0 + 2t/n from it (the object mirrored in the rear face, seen
        // through the glass).
        const double s0 = 0.1, t = 0.01, n = 1.5;
        var run = GhostFocusGenerator.Run(GhostFocusGateTests.Plate(), GhostFocusGateTests.Sys(),
            new GhostFocusOptions { DoubleBounce = false, PupilGrid = 3 }, CoatingCatalog.Empty);
        var ghost = run.Ghosts.Single(g => g.FirstBounce == 2);
        Assert.Null(ghost.Error);
        Assert.InRange(ghost.FocusFromImage!.Value - (2 * s0 + 2 * t / n), -1e-15, 1e-15);
    }

    [Fact]
    public void TheGhostInternalFocus_IsFlagged_WhenTheMarginalRayCrossesTheAxisInGlass()
    {
        // A thick glass sphere focusing inside itself: a ghost path returning through the glass
        // after a bounce on the rear face crosses the axis in the glass when its paraxial heights
        // change sign there. Take a converging front face R = 0.05, n = 1.5 (paraxial focus
        // n·R/(n − 1) = 0.15 behind it) and a plate 0.2 thick: the forward pass already crosses in
        // the glass, so the first ghost surface (the front face, in) is flagged.
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Curvature = 1 / R, Thickness = 0.2, Medium = ParaxialMedium.Constant(N), IsStop = true },
                new SequentialSurface { Thickness = 0.05 },
                new SequentialSurface(),
            },
        };
        var run = GhostFocusGenerator.Run(lens, AngleSys(0), new GhostFocusOptions { PupilGrid = 3 }, CoatingCatalog.Empty);
        var ghost = run.Ghosts.Single(g => g.FirstBounce == 2 && g.SecondBounce == 1);
        Assert.Null(ghost.Error);
        Assert.True(ghost.Surfaces.Single(s => s.GhostSurface == 1).InternalFocus);
    }

    [Fact]
    public void Refusals_NameTheSurface()
    {
        var plate = GhostFocusGateTests.Plate();
        Assert.Contains("larger than the second", Assert.Throws<ArgumentException>(() => GhostSystems.Build(plate, 1, 2)).Message);
        var tilted = plate with
        {
            Surfaces = plate.Surfaces.Select((s, j) => j == 2 ? s with { TiltBefore = new TiltDecenter(0, 1e-3, 0, 0, 0) } : s).ToArray(),
        };
        Assert.Contains("surface 2", Assert.Throws<ArgumentException>(() => GhostSystems.Build(tilted, 2, 1)).Message);

        // A coating name the catalog does not have fails that ghost, naming the surface and the coating.
        var coated = plate with
        {
            Surfaces = plate.Surfaces.Select((s, j) => j == 1 ? s with { Coating = "AR" } : s).ToArray(),
        };
        var run = GhostFocusGenerator.Run(coated, GhostFocusGateTests.Sys(), new GhostFocusOptions { PupilGrid = 3 }, CoatingCatalog.Empty);
        var failed = run.Ghosts.Single(g => g.FirstBounce == 2 && g.SecondBounce == 1);
        Assert.Contains("surface 1", failed.Error);
        Assert.Contains("'AR'", failed.Error);
    }

    // ---- YNI ----

    [Fact]
    public void Yni_IsYTimesNTimesI()
    {
        // One sphere, object at infinity: the marginal ray arrives parallel (u = 0) at
        // y = EPD/2 in air (n = 1); the paraxial angle of incidence is i = u + y·c = y/R, so
        // YNI = y·n·i = y²/R (help, "YNI Contributions": "the product of the paraxial marginal ray
        // height times the index times the angle of incidence").
        double y = Epd / 2, expected = y * y / R;
        var list = YniContributions.Compute(Sphere(), AngleSys(0), 0);
        Assert.Single(list);
        Assert.InRange(list[0].Yni - expected, -1e-18, 1e-18);

        // YNIP in lens units (millimeters by default).
        var e = MeritEvaluator.Evaluate(new MeritFunction(new[] { MeritOperandRow.Of("YNIP", 1, 1, 0, 1) }), new SingleMeritConfiguration(Sphere(), AngleSys(0)));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        Assert.InRange(e.Rows[0].Value - expected * 1e3, -1e-14, 1e-14);

        var result = new YniContributionsAnalysis(_ => Sphere()).Compute(Context(AngleSys(0)), new YniContributionsSettings(), CancellationToken.None);
        Assert.Contains("YNI contributions", result.Text);
    }

    // ---- Power maps ----

    [Fact]
    public void ThePowerMaps_GiveTheSurfacePowerOnTheAxis()
    {
        // On the axis a sphere between air and glass has power (n − 1)/R in diopters (R in m):
        // a parallel pencil of height ±δ leaves with n′·u′ = ∓δ(n − 1)/R. The pupil map's centre
        // (field on axis, pupil centre) and the field map's centre are that ray. The central
        // difference's error is O(δ²/R²) with δ = 1e-4 of the pupil radius, far below the tolerance.
        double power = (N - 1) / R;
        var system = AngleSys(0, 5);
        var pupil = new PowerPupilMapAnalysis(_ => Sphere()).Compute(Context(system),
            new PowerPupilMapSettings { Sampling = 5, Field = 1, Surface = 1 }, CancellationToken.None);
        var map = Assert.IsType<AnalysisMapPlot>(pupil.Plots.Single());
        Assert.InRange(map.Field.Values[2, 2] - power, -1e-6, 1e-6);

        var cylinder = new PowerPupilMapAnalysis(_ => Sphere()).Compute(Context(system),
            new PowerPupilMapSettings { Sampling = 5, Field = 1, Surface = 1, Data = PowerMapData.Cylinder }, CancellationToken.None);
        var cyl = Assert.IsType<AnalysisMapPlot>(cylinder.Plots.Single()).Field.Values;
        Assert.InRange(cyl[2, 2], 0, 1e-6);
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 5; c++)
                Assert.True(double.IsNaN(cyl[r, c]) || cyl[r, c] >= 0, "the help's cylinder is never negative");

        var field = new PowerFieldMapAnalysis(_ => Sphere()).Compute(Context(system),
            new PowerFieldMapSettings { Sampling = 5, Surface = 1 }, CancellationToken.None);
        Assert.InRange(Assert.IsType<AnalysisMapPlot>(field.Plots.Single()).Field.Values[2, 2] - power, -1e-6, 1e-6);

        // The EFL form: 1/power in lens units (mm): 1000·R/(n − 1).
        var efl = new PowerFieldMapAnalysis(_ => Sphere()).Compute(Context(system),
            new PowerFieldMapSettings { Sampling = 5, Surface = 1, EffectiveFocalLength = true }, CancellationToken.None);
        Assert.InRange(Assert.IsType<AnalysisMapPlot>(efl.Plots.Single()).Field.Values[2, 2] - 1e3 / power, -1e-5, 1e-5);
    }

    [Fact]
    public void TheLocalPowerForm_SplitsIntoSphereAndCylinder()
    {
        // P(θ) = A + B cos 2θ + C sin 2θ: maximum A + √(B² + C²), minimum A − √(B² + C²), the
        // cylinder their difference; X is θ = 0, Y θ = 90°, astigmatic X − Y = 2B.
        var form = new LocalPower.Form(3, 1, 0, Math.PI / 2);
        Assert.Equal(3, form.Value(PowerMapData.Spherical, false));
        Assert.Equal(4, form.Value(PowerMapData.Maximum, false));
        Assert.Equal(2, form.Value(PowerMapData.Minimum, false));
        Assert.Equal(2, form.Value(PowerMapData.Cylinder, false));
        Assert.Equal(4, form.Value(PowerMapData.XDirection, false), 15);
        Assert.Equal(2, form.Value(PowerMapData.YDirection, false), 15);
        Assert.Equal(2, form.Value(PowerMapData.Astigmatic, false), 15);
        Assert.Equal(2, form.Value(PowerMapData.Tangential, false), 15);
        Assert.Equal(4, form.Value(PowerMapData.Sagittal, false), 15);
        // EFL: cylinder |1/min − 1/max| = 1/2 − 1/4 (help: the difference of the focal lengths).
        Assert.Equal(0.25, form.Value(PowerMapData.Cylinder, true), 15);
    }

    // ---- Registration ----

    [Fact]
    public void TheAnalysesAndOperands_AreRegistered()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        foreach (var id in new[] { GhostFocusAnalysis.SavedId, YniContributionsAnalysis.SavedId, PowerPupilMapAnalysis.SavedId, PowerFieldMapAnalysis.SavedId })
            Assert.True(registry.TryGet(id, out _), id);
        var operands = MeritOperandRegistry.CreateDefault();
        foreach (var code in new[] { "GAOI", "GPIM", "GPRT", "GPRX", "GPRY", "GPSX", "GPSY", "YNIP" })
            Assert.NotNull(operands.Find(code));
        Assert.Equal(("Surf1", "Surf2", "Mode"), (operands.Get("GPIM").Int1, operands.Get("GPIM").Int2, operands.Get("GPIM").DataLabel(1)));
        Assert.Equal(new[] { "Surf3", "SetVig", "Hx", "Hy", "Px", "Py" }, operands.Get("GAOI").Data);
    }

    [Fact]
    public void TheGhostFocusAnalysis_ListsEveryGhost()
    {
        var result = new GhostFocusAnalysis(_ => GhostFocusGateTests.Plate()).Compute(Context(GhostFocusGateTests.Sys()),
            new GhostFocusSettings { PupilGrid = 3 }, CancellationToken.None);
        Assert.Contains("Ghost 2 → 1", result.Text);
        Assert.Contains("n(n − 1)/2 for n = 3", result.Text);
        Assert.NotEmpty(result.Assumptions);
        Assert.Single(result.Plots);

        var imageOnly = new GhostFocusAnalysis(_ => GhostFocusGateTests.Plate()).Compute(Context(GhostFocusGateTests.Sys()),
            new GhostFocusSettings { PupilGrid = 3, ImageSurfaceOnly = true }, CancellationToken.None);
        Assert.DoesNotContain("on the way in", imageOnly.Text);
    }

    [Fact]
    public void GaoiAndGprt_TraceTheGhostRay()
    {
        // GAOI at the image (Surf3 = 0) of an on-axis chief ray through the plate ghost: the ray
        // runs along the axis, so the angle is 0. GPRT of that ray is the normal-incidence
        // R²(1 − R)² with R = ((n − 1)/(n + 1))² (Fresnel), as in the gate.
        var lens = GhostFocusGateTests.Plate();
        var e = MeritEvaluator.Evaluate(new MeritFunction(new[]
        {
            MeritOperandRow.Of("GAOI", 2, 1, 0, 1, 0, 0, 0, 0, 0, 0),
            MeritOperandRow.Of("GPRT", 2, 1, 0, 1, 0, 0, 0, 0),
            MeritOperandRow.Of("GAOI", 2, 1, 0, 1, 0, 1, 0, 0, 0, 0),
        }), new SingleMeritConfiguration(lens, GhostFocusGateTests.Sys()));
        Assert.InRange(e.Rows[0].Value, -1e-12, 1e-12);
        double r = (1.5 - 1) / (1.5 + 1), f = r * r;
        Assert.InRange(e.Rows[1].Value - f * f * (1 - f) * (1 - f), -1e-15, 1e-15);
        Assert.Contains(e.Errors, x => x.Contains("SetVig"));
    }
}
