using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using OpenSim.App.Services;
using OpenSim.App.ViewModels;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Persistence;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;

namespace OpenSim.App;

/// <summary>Application entry point: composition root for dependency injection.</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // Core modules registered against their plugin-seam interfaces. StepImporter is
        // also registered concretely: the view model needs ImportWithNotes (the advisory
        // notes channel), which the seam interface deliberately does not carry.
        services.AddSingleton<IGeometryImporter, StlImporter>();
        services.AddSingleton<OpenSim.Geometry.Step.StepImporter>();
        services.AddSingleton<IGeometryImporter>(sp =>
            sp.GetRequiredService<OpenSim.Geometry.Step.StepImporter>());
        services.AddSingleton<IMeshGenerator, DelaunayMeshGenerator>();
        services.AddSingleton<ISolver, LinearStaticSolver>();
        services.AddSingleton<ISolver, ElectricalConductionSolver>();
        services.AddSingleton<ISolver, HeatConductionSolver>();
        services.AddSingleton<ISolver, TransientThermalSolver>();
        services.AddSingleton<ISolver, ModalAnalysisSolver>();
        services.AddSingleton<ISolver, HarmonicElectricSolver>();
        services.AddSingleton<JouleHeatingStudy>();

        services.AddSingleton<MaterialLibrary>();
        services.AddSingleton<ProjectSerializer>();

        // Shared app state + the per-concern view models the shell exposes.
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<ProjectSession>();
        services.AddSingleton<RecentProjectsService>();
        services.AddSingleton<GeometryViewModel>();
        services.AddSingleton<MaterialsViewModel>();
        services.AddSingleton<MeshingViewModel>();
        services.AddSingleton<BoundaryConditionsViewModel>();
        services.AddSingleton<ResultsViewModel>();
        services.AddSingleton<ElectrodesViewModel>();
        services.AddSingleton<InductanceViewModel>();
        services.AddSingleton<AntennaViewModel>();
        services.AddSingleton<SignalIntegrityViewModel>();
        services.AddSingleton<SceneViewModel>();
        services.AddSingleton<SolveViewModel>();
        services.AddSingleton<PcbViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
