using System.IO;
using System.Text.Json;
using OpenSim.Core.Model;

namespace OpenSim.Core.Persistence;

/// <summary>
/// Saves and loads a lens on its own (.osslens): the same JSON as a project's optics settings,
/// with a name and its own format version. A lens moves between projects this way, and a
/// project's optics can be exported and re-imported without the rest of the project.
/// </summary>
public sealed class OpticsFileSerializer
{
    public const string FileExtension = ".osslens";
    public const string FileFilter = "OpenSim Studio lens (*.osslens)|*.osslens";

    /// <summary>The lens-file format this build writes: 1 = the optics settings of project
    /// format 5 (system data). Raised, as the project's number is, when a file can carry
    /// fields an older build would not read.</summary>
    public const int CurrentFormatVersion = 1;

    public void Save(OpticsLensFile lens, string filePath)
    {
        if (lens.Optics is null) throw new ArgumentException("A lens file needs the optics settings.", nameof(lens));
        lens.FormatVersion = CurrentFormatVersion;
        File.WriteAllText(filePath, JsonSerializer.Serialize(lens, ProjectSerializer.Options));
    }

    /// <summary>Reads a lens file. Throws <see cref="InvalidDataException"/> naming the file
    /// when it is not JSON or holds no optics settings (a project file opened by mistake).</summary>
    public OpticsLensFile Load(string filePath)
    {
        OpticsLensFile? lens;
        try
        {
            lens = JsonSerializer.Deserialize<OpticsLensFile>(File.ReadAllText(filePath), ProjectSerializer.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{filePath}' is not a valid lens file: {ex.Message}", ex);
        }
        if (lens?.Optics is null)
            throw new InvalidDataException($"'{filePath}' holds no optical system; is it a lens file ({FileExtension})?");
        return lens;
    }
}
