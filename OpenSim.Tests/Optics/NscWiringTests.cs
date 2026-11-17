using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.Tests.Optics;

/// <summary>
/// wire-16: the joins between wave 16's items and the rest of the program: the Detector Viewer's
/// "Scatter NSC Rays" (help, "Ray Trace"; OP-80's scattering through OP-77's runner) and the
/// tolerancing configuration wrappers passing the trial project to the non-sequential merit rows
/// (OP-88's <see cref="IMeritConfigurations.SettingsFor"/>).
/// </summary>
public sealed class NscWiringTests
{
    /// <summary>A Source Point (1 W, 500 rays, 10° semi-cone along +z), a Lambertian MIRROR
    /// Rectangle (±5 mm, fraction 1, one scatter ray) 10 mm away and an absorbing Detector
    /// Rectangle in the plane x = 8 mm beside the beam (as ZplConformanceTests'
    /// MirrorAndSideDetector, with the scattering saved). Arbitrary test inputs; without
    /// scattering no ray reaches the detector (the cone and its reflection stay within 3.6 mm of
    /// the axis, derived from the geometry).</summary>
    private static OpticsSettings ScatteringMirror() => new()
    {
        SystemData = new OpticsSystemSettings { Title = "NSC", Wavelengths = new() { new() { Micrometers = 0.555 } } },
        NonSequential = new OpticsNonSequentialSettings
        {
            Objects = new()
            {
                new() { Type = "Source Point", Parameters = new() { 10, 500, 1, 0, 0, 10 } },
                new()
                {
                    Type = "Rectangle", Z = 10, Material = "MIRROR", Parameters = new() { 5, 5 },
                    Faces = new() { new() { Face = 0, ScatterModel = "Lambertian", ScatterFraction = 1, ScatterRays = 1 } },
                },
                new() { Type = "Detector Rectangle", X = 8, Z = 5, TiltY = 90, Material = "ABSORB", Parameters = new() { 5, 5, 4, 4 } },
            },
        },
    };

    [Fact]
    public void DetectorViewer_ScatterNscRays_IsOffByDefault_AndTurnsScatteringOn()
    {
        Assert.False(new NscDetectorViewerSettings().ScatterRays);
        var context = new OpticsAnalysisContext("P", ScatteringMirror()) { Catalogs = Array.Empty<OpticalCatalog>() };
        string Hits(OpticsAnalysisResult r) => r.Sections.SelectMany(s => s.Pairs).First(p => p.Key == "Hits").Value;

        var off = new NscDetectorViewerAnalysis().Compute(context, new NscDetectorViewerSettings { Detector = 3 }, default);
        Assert.StartsWith("0 spatial", Hits(off));
        Assert.Contains(off.Assumptions, a => a.Contains("Scatter NSC Rays is off"));

        var on = new NscDetectorViewerAnalysis().Compute(context, new NscDetectorViewerSettings { Detector = 3, ScatterRays = true }, default);
        Assert.DoesNotContain("0 spatial", Hits(on));
        Assert.Contains(on.Assumptions, a => a.Contains("Scattering on (Scatter NSC Rays)"));
    }

    [Fact]
    public void ToleranceConfigurationWrappers_PassTheTrialProjectToTheNonSequentialRows()
    {
        var inner = new ProjectMeritConfigurations(ScatteringMirror(), Array.Empty<OpticalCatalog>());
        var expected = inner.SettingsFor(1);
        Assert.NotNull(expected);
        IMeritConfigurations aimed = new AimedConfigurations(inner, inner);
        IMeritConfigurations focused = new FocusedConfigurations(inner, new[] { 0.0 });
        Assert.Same(expected, aimed.SettingsFor(1));
        Assert.Same(expected, focused.SettingsFor(1));
    }
}
