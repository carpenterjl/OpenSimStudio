using System.IO;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;

namespace OpenSim.App.Tests.Analyses;

/// <summary>
/// OP-97's gate, the app's half: the report the app's Save report writes (the analysis windows'
/// <see cref="OpticsAnalysesViewModel.ComposeReport"/>) is, byte for byte, the library function the
/// command line writes (<see cref="OpticsBatch.Report"/> over <see cref="OpticsBatch.Run"/>), for
/// the same saved lens, the settings the project saved, and the same catalogs. OpenSim.Tests'
/// OpticsCliTests.Analyse_WritesTheLibrarysReport_ByteForByte is the command's half. The lens is an
/// arbitrary constant-index singlet (no glass catalog), not a design from a source.
/// </summary>
public sealed class OpticsReportParityTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly AppComposition _app = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "op97-parity-" + Guid.NewGuid().ToString("N"));

    public OpticsReportParityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _app.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static OpticsSettings Singlet() => new()
    {
        SystemData = new OpticsSystemSettings
        {
            ApertureValue = 4,
            Fields = new() { new OpticsFieldSettings(), new OpticsFieldSettings { Y = 2 } },
            Wavelengths = new() { new OpticsWavelengthSettings { Micrometers = 0.55 } },
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 100, Thickness = 5, Index = 1.5, IsStop = true },
            new() { Radius = -100, Thickness = 97 },
            new(),
        },
        Analyses = new OpticsAnalysesSettings
        {
            LastSettings = new()
            {
                [StandardSpotAnalysis.SavedId] = OpticsAnalysisSettingsCodec.Save(new StandardSpotSettings { RayDensity = 4 }),
            },
        },
    };

    [Fact]
    public async Task TheAppsSaveReport_IsTheCommandLinesReport_ByteForByte()
    {
        // The lens through the project file, as the command line reads it.
        string path = Path.Combine(_directory, "lens.ossproj");
        new ProjectSerializer().Save(new SimProject { Name = "Parity lens", Optics = Singlet() }, path);
        var project = new ProjectSerializer().Load(path);

        // The app: an empty catalog folder, so the library holds the built-in catalogs only, as
        // the command line's --builtin-catalogs does.
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var library = new GlassCatalogLibrary(Path.Combine(_directory, "user"));
        var session = new ProjectSession(_app.Log) { Project = project };
        var host = new OpticsAnalysesViewModel(session, _app.Log, registry, library) { Debounce = TimeSpan.Zero };
        session.PublishOptics(project.Optics!);
        host.LoadSettings(project.Optics!.Analyses);
        var ids = new[] { SystemDataAnalysis.SavedId, StandardSpotAnalysis.SavedId, SingleRayTraceAnalysis.SavedId };
        var analyses = ids.Select(id => { Assert.True(registry.TryGet(id, out var a)); return a; }).ToList();
        foreach (var analysis in analyses) host.Open(analysis);
        await host.WhenIdleAsync().WaitAsync(Wait);
        Assert.All(host.Windows, w => Assert.Equal("Up to date", w.Status));
        var app = host.ComposeReport();

        // The library function the command line calls, on the same file.
        var builtIn = GlassCatalogLibrary.BuiltInCatalogs.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var context = OpticsBatch.Context(project.Name, project.Optics!, builtIn);
        var runs = analyses.Select(a => (a, OpticsBatch.SavedSettings(a, project.Optics!, out _))).ToList();
        var cli = OpticsBatch.Report(context, OpticsBatch.Run(context, runs));

        Assert.Equal(cli.ToMarkdown(), app.ToMarkdown());
        Assert.Equal(cli.ToHtml(), app.ToHtml());
        Assert.Equal(host.Snapshot().Catalogs.Select(c => c.Name), context.Catalogs.Select(c => c.Name));
    }
}
