using System.IO;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.App.Views.Analyses;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;

namespace OpenSim.App.Tests.Analyses;

/// <summary>
/// OP-14 in the app: the analysis windows hand the material analyses the lens's active glass
/// catalogs, and a glass map is drawn with the scatter plot control, each point named.
/// </summary>
public sealed class MaterialAnalysesHostTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly AppComposition _app = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "op14-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _app.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Two built-in catalogs of made-up formula-2 glasses (the Sellmeier 1 coefficients
    /// of OpenSim.Tests' OP11_FIXTURE.agf SELLMEIER1-BK7, scaled), valid 0.3–2.5 µm.</summary>
    private static OpticalCatalog Catalog(string name, params string[] glasses) => new()
    {
        Name = name,
        Materials = glasses.Select((g, i) => new OpticalMaterial
        {
            Name = g, Catalog = name, FormulaNumber = 2, MinMicrometers = 0.3, MaxMicrometers = 2.5,
            Coefficients = new[] { 1.03961212 * (1 + 0.1 * i), 0.00600069867, 0.231792344, 0.0200179144, 1.01046945, 103.560653 }
        }).ToArray()
    };

    private (OpticsAnalysesViewModel Host, ProjectSession Session) NewHost()
    {
        var library = new GlassCatalogLibrary(_directory, new[] { Catalog("A", "A1", "A2"), Catalog("B", "B1", "B2", "B3") });
        var registry = new OpticsAnalysisRegistry();
        registry.Register(new GlassMapAnalysis());
        var session = new ProjectSession(_app.Log);
        return (new OpticsAnalysesViewModel(session, _app.Log, registry, library) { Debounce = TimeSpan.Zero }, session);
    }

    [Fact]
    public void TheSnapshot_CarriesTheActiveCatalogs_InTheirOrder()
    {
        var (host, session) = NewHost();
        Assert.Equal(new[] { "A", "B" }, host.Snapshot().Catalogs.Select(c => c.Name));     // none named: every catalog
        session.PublishOptics(new OpticsSettings { Materials = new OpticsMaterialsSettings { ActiveCatalogs = new() { "B", "Z" } } });
        Assert.Equal(new[] { "B" }, host.Snapshot().Catalogs.Select(c => c.Name));
    }

    [Fact]
    public async Task AGlassMapWindow_DrawsTheActiveCatalogsGlasses()
    {
        var (host, session) = NewHost();
        session.PublishOptics(new OpticsSettings { Materials = new OpticsMaterialsSettings { ActiveCatalogs = new() { "B" } } });
        var window = host.Open(host.Available.Single());
        await host.WhenIdleAsync().WaitAsync(Wait);
        Assert.Equal("Up to date", window.Status);
        var plot = Assert.IsType<AnalysisScatterPlot>(window.Plots.Single());
        Assert.Equal(new[] { "B1", "B2", "B3" }, plot.Series.Single().Points.Select(p => p.Label));

        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var control = AnalysisPlotViewConverter.Scatter(plot);
                var series = control.Series.Single();
                Assert.Equal("B", series.Name);
                Assert.Equal(new[] { "B1", "B2", "B3" }, series.Labels);
                Assert.Equal(plot.Series[0].Points.Select(p => p.X), series.Points.Select(p => p.X));
                Assert.False(control.EqualAspect);
            }
            catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
