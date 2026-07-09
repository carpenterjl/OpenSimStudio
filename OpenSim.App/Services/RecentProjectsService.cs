using System.IO;
using System.Text.Json;

namespace OpenSim.App.Services;

/// <summary>One entry in the home screen's recent-projects list.</summary>
public sealed record RecentProject(string Path, string Name, DateTime LastOpenedUtc);

/// <summary>
/// The most-recently-used project list behind the home screen, persisted to
/// %AppData%/OpenSimStudio/recent.json. Missing files are pruned on load; the list is
/// capped at 10, newest first. All failures degrade to an empty list — the MRU must
/// never block startup or a save.
/// </summary>
public class RecentProjectsService
{
    private const int Capacity = 10;
    private readonly string _file;

    public RecentProjectsService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenSimStudio"))
    {
    }

    internal RecentProjectsService(string directory) => _file = Path.Combine(directory, "recent.json");

    public IReadOnlyList<RecentProject> Load()
    {
        try
        {
            if (!File.Exists(_file)) return Array.Empty<RecentProject>();
            var entries = JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(_file));
            return entries is null
                ? Array.Empty<RecentProject>()
                : entries.Where(e => File.Exists(e.Path))
                    .OrderByDescending(e => e.LastOpenedUtc)
                    .Take(Capacity)
                    .ToList();
        }
        catch
        {
            return Array.Empty<RecentProject>();
        }
    }

    /// <summary>Records a project open/save; upserts by path and persists immediately.</summary>
    public void Add(string path)
    {
        try
        {
            var entries = Load().Where(e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))
                .ToList();
            entries.Insert(0, new RecentProject(path, Path.GetFileNameWithoutExtension(path), DateTime.UtcNow));
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(entries.Take(Capacity),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Never fail an open/save over MRU bookkeeping.
        }
    }
}
