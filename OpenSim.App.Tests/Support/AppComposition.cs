using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using OpenSim.App.Services;
using OpenSim.Core.Persistence;

namespace OpenSim.App.Tests.Support;

/// <summary>A log that keeps its entries in memory. The application's <see cref="LogService"/>
/// marshals onto the dispatcher through <c>Application.Current</c>, which a test has none of.</summary>
public sealed class ListLogService : ILogService
{
    public ObservableCollection<string> Entries { get; } = new();
    public void Append(string message) => Entries.Add(message);
}

/// <summary>
/// The application's container, composed exactly as the running application composes it
/// (<see cref="CompositionRoot.Register"/>) with the log replaced (see
/// <see cref="ListLogService"/>) and the three user-profile services — the material and
/// colormap libraries and the recent-projects list — rooted at a private temporary directory
/// instead of the user's profile. Dispose it to remove that directory.
/// </summary>
public sealed class AppComposition : IDisposable
{
    private readonly string _userDirectory;

    public AppComposition()
    {
        _userDirectory = Path.Combine(Path.GetTempPath(), "OpenSim.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDirectory);

        var services = new ServiceCollection();
        CompositionRoot.Register(services);
        // Later registrations win for a single resolve: these stand in for the app's own.
        services.AddSingleton<ILogService>(Log);
        services.AddSingleton(new MaterialLibrary(_userDirectory));
        services.AddSingleton(new ColormapLibrary(_userDirectory));
        services.AddSingleton(new RecentProjectsService(_userDirectory));
        Services = services.BuildServiceProvider();
    }

    public ListLogService Log { get; } = new();

    /// <summary>The private profile directory; a test's own files go here too.</summary>
    public string UserDirectory => _userDirectory;

    public ServiceProvider Services { get; }

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Services.Dispose();
        try
        {
            Directory.Delete(_userDirectory, recursive: true);
            string parent = Path.GetDirectoryName(_userDirectory)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (IOException) { }               // another test's directory appeared meanwhile, or a file is still open
        catch (UnauthorizedAccessException) { }
    }
}
