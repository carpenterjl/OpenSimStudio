using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenSim.Core.Model;

namespace OpenSim.Core.Persistence;

/// <summary>Saves and loads .ossproj files (JSON, offline, human-inspectable).</summary>
public sealed class ProjectSerializer
{
    public const string FileExtension = ".ossproj";
    public const string FileFilter = "OpenSim Studio project (*.ossproj)|*.ossproj";

    /// <summary>The format this build writes: 1 = the original schema (unversioned files
    /// read as 0), 2 = the solver settings and this number (FU-46), 3 = the antenna and
    /// signal-integrity panels' settings (D23).</summary>
    public const int CurrentFormatVersion = 3;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Save(SimProject project, string filePath)
    {
        project.FormatVersion = CurrentFormatVersion;
        File.WriteAllText(filePath, JsonSerializer.Serialize(project, Options));
    }

    public SimProject Load(string filePath)
    {
        var project = JsonSerializer.Deserialize<SimProject>(File.ReadAllText(filePath), Options);
        return project ?? throw new InvalidDataException($"'{filePath}' is not a valid project file.");
    }
}

