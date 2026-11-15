using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Scripting;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-98: the scripting facade (<see cref="OpticsScripting"/>; docs/Optics-Conventions.md §47).
/// The worked script's path is <c>Cli/ScriptingSurfaceTests</c>; here, the facade's own rules:
/// OpticStudio's RAYE / RAYV codes (help, "RAYTRACE", 2025 R2, saved under
/// plans/sources/optics/op-98-help/: RAYE negative for total internal reflection at the surface
/// whose number is its absolute value, positive for the surface missed, −9999 when the ray cannot
/// be launched; RAYV the surface at which the ray was vignetted, or zero), analyses by id, the
/// wizard's insertion and loading a project. Lenses use constant indices (no catalog) and
/// arbitrary radii; each geometry is worked out in its comment.
/// </summary>
public sealed class OpticsScriptingTests
{
    private static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    private static OpticsSettings Lens(double epd, params OpticsSurfaceSettings[] surfaces) => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = epd },
        Surfaces = new[] { new OpticsSurfaceSettings { ThicknessIsInfinite = true } }.Concat(surfaces).Append(new OpticsSurfaceSettings()).ToList(),
    };

    [Fact]
    public void ARayThatReachesTheImage_HasNoErrorCodes()
    {
        var optics = Lens(10, new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true }, new() { Radius = -50, Thickness = 45 });
        var ray = OpticsScripting.TraceRay(optics, NoCatalogs, 0, 0, 0, 1);
        Assert.Equal(RayStatus.Ok, ray.Status);
        Assert.Equal(0, ray.Error);
        Assert.Equal(0, ray.Vignetted);
        Assert.Null(ray.LaunchFailure);
    }

    /// <summary>A sphere of radius 5 mm cannot be met by a ray parallel to the axis at height
    /// 10 mm (Py = 1 of a 20 mm entrance pupil, the stop on that surface): RAYE = +1.</summary>
    [Fact]
    public void ARayThatMissesSurfaceN_HasErrorPlusN()
    {
        var optics = Lens(20, new() { Radius = 5, Thickness = 4, Index = 1.5, IsStop = true }, new() { Thickness = 10 });
        var ray = OpticsScripting.TraceRay(optics, NoCatalogs, 0, 0, 0, 1);
        Assert.Equal(RayStatus.MissedSurface, ray.Status);
        Assert.Equal(1, ray.Error);
        Assert.Equal(0, ray.Vignetted);
    }

    /// <summary>A flat-fronted block of index 1.5, 5 mm thick, with a back radius of −10 mm: the
    /// ray parallel to the axis at height 8 mm (Py = 1 of 16 mm) crosses the flat undeviated and
    /// meets the back sphere at sin θ = 8/10 to its normal (the back surface's sag there is −4 mm,
    /// so it lies 1 mm behind the front), and 1.5 × 0.8 = 1.2 &gt; 1: total internal reflection at
    /// surface 2, RAYE = −2.</summary>
    [Fact]
    public void ARayTotallyReflectedAtSurfaceN_HasErrorMinusN()
    {
        var optics = Lens(16, new() { Thickness = 5, Index = 1.5, IsStop = true }, new() { Radius = -10, Thickness = 10 });
        var ray = OpticsScripting.TraceRay(optics, NoCatalogs, 0, 0, 0, 1);
        Assert.Equal(RayStatus.TotalInternalReflection, ray.Status);
        Assert.Equal(-2, ray.Error);
        Assert.Equal(0, ray.Vignetted);
    }

    /// <summary>A circular aperture of radius 3 mm on surface 2 of a weak singlet, a marginal ray
    /// at about 5 mm there (EPD 10 mm, f ≈ 100 mm, 4 mm behind the stop): RAYV = 2, RAYE = 0 as
    /// the help keeps the two apart; the chief ray passes.</summary>
    [Fact]
    public void ARayVignettedAtSurfaceN_HasVignettedN_AndNoError()
    {
        var optics = Lens(10,
            new() { Radius = 100, Thickness = 4, Index = 1.5, IsStop = true },
            new() { Radius = -100, Thickness = 95, Aperture = new() { Type = SavedSurfaces.CircularAperture, MaxRadius = 3 } });
        var ray = OpticsScripting.TraceRay(optics, NoCatalogs, 0, 0, 0, 1);
        Assert.Equal(RayStatus.Vignetted, ray.Status);
        Assert.Equal(0, ray.Error);
        Assert.Equal(2, ray.Vignetted);
        Assert.Equal(0, OpticsScripting.TraceRay(optics, NoCatalogs, 0, 0, 0, 0).Vignetted);
    }

    [Fact]
    public void TraceRay_RefusesAnEmptySystemAndAMissingWavelength_ByName()
    {
        Assert.Contains("no surfaces", Assert.Throws<ArgumentException>(() => OpticsScripting.TraceRay(new OpticsSettings(), NoCatalogs, 0, 0, 0, 0)).Message);
        var optics = Lens(10, new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true }, new() { Radius = -50, Thickness = 45 });
        Assert.Contains("wavelength row", Assert.Throws<ArgumentException>(() => OpticsScripting.TraceRay(optics, NoCatalogs, 0, 0, 0, 0, 7)).Message);
    }

    [Fact]
    public void AnalysisIds_AreTheRegistrys_AndAnUnknownIdIsRefusedByName()
    {
        Assert.Equal(OpticsAnalysisRegistry.CreateDefault().All.Select(a => a.Id), OpticsScripting.AnalysisIds());
        Assert.Contains("'NoSuchAnalysis'", Assert.Throws<ArgumentException>(() => OpticsScripting.AnalysisSettings("NoSuchAnalysis")).Message);
        Assert.IsType<FftMtfSettings>(OpticsScripting.AnalysisSettings(FftMtfAnalysis.SavedId));
    }

    [Fact]
    public void Analyze_WithoutSettings_IsTheAnalysisWithItsDefaults()
    {
        var optics = Lens(10, new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true }, new() { Radius = -50, Thickness = 45 });
        var analysis = new ParaxialDataAnalysis();
        var direct = analysis.Compute(new OpticsAnalysisContext("Script", optics), analysis.CreateDefaultSettings(), default);
        Assert.Equal(direct.Text, OpticsScripting.Analyze(optics, NoCatalogs, analysis.Id).Text);
    }

    /// <summary>The wizard's rows go after DMFS when there is one (help, DMFS: the row after it
    /// is the default Start At), else at the end; rows before stay where they are.</summary>
    [Fact]
    public void WithDefaultMerit_InsertsAfterDmfs_OrAppends()
    {
        var optics = Lens(10, new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true }, new() { Radius = -50, Thickness = 45 });
        static OpticsSettings With(OpticsSettings o, params string[] codes) =>
            o with { MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(codes.Select(c => MeritOperandRow.Of(c, 0, 1, 0, c == "EFFL" ? 1 : 0)))) };
        var appended = OpticsScripting.WithDefaultMerit(With(optics, "EFFL"), NoCatalogs).MeritFunction!.Operands!;
        Assert.Equal("EFFL", appended[0].Type);
        Assert.True(appended.Count > 1);
        var inserted = OpticsScripting.WithDefaultMerit(With(optics, "DMFS", "EFFL"), NoCatalogs).MeritFunction!.Operands!;
        Assert.Equal("DMFS", inserted[0].Type);
        Assert.Equal("EFFL", inserted[^1].Type);
        Assert.Equal(appended.Count + 1, inserted.Count);
    }

    [Fact]
    public void EvaluateMerit_RefusesAnEmptyOrUnreadableMeritFunction()
    {
        var optics = Lens(10, new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true }, new() { Radius = -50, Thickness = 45 });
        Assert.Contains("no rows", Assert.Throws<ArgumentException>(() => OpticsScripting.EvaluateMerit(optics, NoCatalogs)).Message);
        var unknown = optics with { MeritFunction = new OpticsMeritFunctionSettings { Operands = new() { new() { Type = "NOPE", Weight = 1 } } } };
        Assert.Contains("'NOPE'", Assert.Throws<ArgumentException>(() => OpticsScripting.EvaluateMerit(unknown, NoCatalogs)).Message);
    }

    [Fact]
    public void Load_ReturnsTheSavedOpticsRecord_AndRefusesAProjectWithout()
    {
        var optics = Lens(10, new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true }, new() { Radius = -50, Thickness = 45 });
        string dir = Path.Combine(Path.GetTempPath(), "OpenSim-OP98-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string with = Path.Combine(dir, "lens" + ProjectSerializer.FileExtension);
            new ProjectSerializer().Save(new SimProject { Name = "Lens", Optics = optics }, with);
            var loaded = OpticsScripting.Load(with);
            Assert.Equal(4, loaded.Surfaces!.Count);
            Assert.Equal(50.0, loaded.Surfaces[1].Radius);
            Assert.Equal(10.0, loaded.SystemData!.ApertureValue);

            string without = Path.Combine(dir, "empty" + ProjectSerializer.FileExtension);
            new ProjectSerializer().Save(new SimProject { Name = "Empty" }, without);
            Assert.Contains("no optics system", Assert.Throws<InvalidOperationException>(() => OpticsScripting.Load(without)).Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
