using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using OpenSim.App.Services;

namespace OpenSim.App;

/// <summary>Application entry point: builds the container from
/// <see cref="CompositionRoot"/> and shows the main window.</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        CompositionRoot.Register(services);
        _services = services.BuildServiceProvider();

        // Apply the persisted theme before the window materializes so the first
        // frame already carries the right tokens (no dark->light flash).
        _services.GetRequiredService<ThemeService>().ApplySaved();

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
