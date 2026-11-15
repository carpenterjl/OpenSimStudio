using OpenSim.Optics.Interop.CodeV;
using OpenSim.Optics.Interop.Oslo;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Interop;

/// <summary>
/// Lens files of the three long-standing design programs on disk, read by extension: Zemax .zmx
/// and .zar (OP-94, <see cref="ZemaxLensFiles"/>), CODE V .seq (<see cref="CodeVLensReader"/>) and
/// OSLO .len (<see cref="OsloLensReader"/>), plan item OP-95, docs/Optics-Conventions.md
/// sections 52 and 54. The three readers share OP-94's option, result and exception types
/// (<see cref="ZemaxImportOptions"/>, <see cref="ZemaxImportResult"/>, <see cref="ZemaxFileException"/>).
/// </summary>
public static class LensFileImport
{
    /// <summary>The open-dialog filter for CODE V and OSLO files.</summary>
    public const string CodeVOsloFilter = "CODE V and OSLO lens files (*.seq;*.len)|*.seq;*.len|CODE V sequence files (*.seq)|*.seq|OSLO lens files (*.len)|*.len|All files (*.*)|*.*";

    /// <summary>True when the path's extension is one <see cref="Import"/> reads.</summary>
    public static bool IsLensFile(string path)
    {
        string e = Path.GetExtension(path);
        return e.Equals(ZemaxLensFiles.LensExtension, StringComparison.OrdinalIgnoreCase)
            || e.Equals(ZemaxLensFiles.ArchiveExtension, StringComparison.OrdinalIgnoreCase)
            || e.Equals(CodeVLensReader.Extension, StringComparison.OrdinalIgnoreCase)
            || e.Equals(OsloLensReader.Extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The format name of a path's extension, for messages ("Zemax", "CODE V", "OSLO").</summary>
    public static string FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".seq" => "CODE V",
        ".len" => "OSLO",
        _ => "Zemax",
    };

    /// <summary>
    /// Reads a lens file by its extension: .seq as CODE V, .len as OSLO, anything else as Zemax
    /// (.zmx or .zar). The text of a .seq or .len is decoded as a .zmx's is (UTF-16 with or
    /// without a byte-order mark, UTF-8, else ISO-8859-1, <see cref="ZemaxText.Decode"/>), which
    /// covers the readers' own choices (Optiland: UTF-8 then Latin-1 for CODE V, UTF-8 then
    /// cp1252 for OSLO).
    /// </summary>
    public static ZemaxImportResult Import(string path, IReadOnlyList<OpticalCatalog>? catalogs, ZemaxImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        string e = Path.GetExtension(path);
        if (e.Equals(CodeVLensReader.Extension, StringComparison.OrdinalIgnoreCase))
        {
            var (text, encoding) = ZemaxText.Decode(File.ReadAllBytes(path));
            return CodeVLensReader.Read(text, catalogs, options, encoding);
        }
        if (e.Equals(OsloLensReader.Extension, StringComparison.OrdinalIgnoreCase))
        {
            var (text, encoding) = ZemaxText.Decode(File.ReadAllBytes(path));
            return OsloLensReader.Read(text, catalogs, options, encoding);
        }
        return ZemaxLensFiles.Import(path, catalogs, options);
    }
}
