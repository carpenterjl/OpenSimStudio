using Microsoft.Extensions.DependencyInjection;
using OpenSim.App.ViewModels;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Persistence;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;

namespace OpenSim.App.Services;

/// <summary>
/// The application's service registrations, in one place so that the view-model tests
/// (OpenSim.App.Tests) compose the view models exactly as the running application does —
/// the same singletons, the same constructor wiring — and differ only in the services they
/// replace (the log, which needs a dispatcher, and the user-profile libraries).
/// </summary>
public static class CompositionRoot
{
    public static void Register(IServiceCollection services)
    {
        // Core modules registered against their plugin-seam interfaces. StepImporter is
        // also registered concretely: the view model needs ImportWithNotes (the advisory
        // notes channel), which the seam interface deliberately does not carry.
        services.AddSingleton<IGeometryImporter, StlImporter>();
        services.AddSingleton<OpenSim.Geometry.Step.StepImporter>();
        services.AddSingleton<IGeometryImporter>(sp =>
            sp.GetRequiredService<OpenSim.Geometry.Step.StepImporter>());
        // Both meshers are registered, and the selector is what everything resolves: the
        // method lives in each body's persisted settings, not in the caller.
        services.AddSingleton<DelaunayMeshGenerator>();
        services.AddSingleton<StructuredLatticeMeshGenerator>();
        services.AddSingleton<IMeshGenerator, MeshGeneratorSelector>();
        services.AddSingleton<ISolver, LinearStaticSolver>();
        services.AddSingleton<ISolver, ElectricalConductionSolver>();
        services.AddSingleton<ISolver, HeatConductionSolver>();
        services.AddSingleton<ISolver, TransientThermalSolver>();
        services.AddSingleton<ISolver, ModalAnalysisSolver>();
        services.AddSingleton<ISolver, HarmonicElectricSolver>();
        services.AddSingleton<ISolver, ElectrostaticSolver>();
        services.AddSingleton<JouleHeatingStudy>();

        services.AddSingleton<MaterialLibrary>();
        services.AddSingleton<ColormapLibrary>();
        services.AddSingleton<ProjectSerializer>();

        // Shared app state + the per-concern view models the shell exposes.
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<ProjectSession>();
        services.AddSingleton<RecentProjectsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<GeometryViewModel>();
        services.AddSingleton<MaterialsViewModel>();
        services.AddSingleton<MeshingViewModel>();
        services.AddSingleton<BoundaryConditionsViewModel>();
        services.AddSingleton<ScopeSelectionViewModel>();
        services.AddSingleton<ResultsViewModel>();
        services.AddSingleton<ElectrodesViewModel>();
        services.AddSingleton<PowerRailViewModel>();
        services.AddSingleton<InductanceViewModel>();
        services.AddSingleton<AntennaViewModel>();
        services.AddSingleton<SignalIntegrityViewModel>();
        services.AddSingleton<MagneticsViewModel>();
        services.AddSingleton<EmiViewModel>();
        services.AddSingleton<BodiesViewModel>();
        services.AddSingleton<EnvironmentViewModel>();
        services.AddSingleton<FlowVisualizationViewModel>();
        services.AddSingleton<CfdSetupViewModel>();
        services.AddSingleton<ColormapViewModel>();
        services.AddSingleton<StudyRailViewModel>();
        services.AddSingleton<SceneViewModel>();
        services.AddSingleton<SolveViewModel>();
        services.AddSingleton<PcbViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
