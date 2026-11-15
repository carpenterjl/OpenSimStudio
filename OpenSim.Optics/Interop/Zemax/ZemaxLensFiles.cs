using OpenSim.Core.Model;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Interop.Zemax;

/// <summary>
/// Zemax lens files on disk (OP-94, docs/Optics-Conventions.md section 52): import of a .zmx
/// lens file or of the lens inside a .zar archive, and export of the supported subset as .zmx.
/// </summary>
public static class ZemaxLensFiles
{
    public const string LensExtension = ".zmx";
    public const string ArchiveExtension = ".zar";

    /// <summary>The open-dialog filter for both kinds.</summary>
    public const string ImportFilter = "Zemax lens files (*.zmx;*.zar)|*.zmx;*.zar|All files (*.*)|*.*";

    /// <summary>The save-dialog filter.</summary>
    public const string ExportFilter = "Zemax lens file (*.zmx)|*.zmx";

    /// <summary>
    /// Reads a .zmx file, or the one .zmx inside a .zar archive (an archive's .agf glass
    /// catalogs are read too, searched first, and returned in the result; its other files are
    /// named in the notes). See <see cref="ZemaxLensReader.Read"/> for the rest.
    /// </summary>
    public static ZemaxImportResult Import(string path, IReadOnlyList<OpticalCatalog>? catalogs, ZemaxImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bytes = File.ReadAllBytes(path);
        if (!string.Equals(Path.GetExtension(path), ArchiveExtension, StringComparison.OrdinalIgnoreCase))
        {
            var (text, encoding) = ZemaxText.Decode(bytes);
            return ZemaxLensReader.Read(text, catalogs, options, encoding);
        }
        return ImportArchive(bytes, Path.GetFileName(path), catalogs, options);
    }

    /// <summary>The lens inside an archive's bytes (<see cref="Import"/>).</summary>
    public static ZemaxImportResult ImportArchive(byte[] archive, string archiveName, IReadOnlyList<OpticalCatalog>? catalogs, ZemaxImportOptions? options = null)
    {
        var entries = ZarArchive.Read(archive);
        var lenses = entries.Where(e => e.Name.EndsWith(LensExtension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (lenses.Count != 1)
            throw new InvalidDataException($"The archive '{archiveName}' holds {lenses.Count} lens files ({string.Join(", ", lenses.Select(e => e.Name))}); one is read.");
        var notes = new List<string>();
        var archiveCatalogs = new List<OpticalCatalog>();
        foreach (var e in entries)
        {
            if (ReferenceEquals(e, lenses[0])) continue;
            if (e.Name.EndsWith(".agf", StringComparison.OrdinalIgnoreCase))
            {
                archiveCatalogs.Add(AgfReader.Parse(e.Contents, Path.GetFileNameWithoutExtension(e.Name), $"{archiveName}:{e.Name}"));
                notes.Add($"The archive's glass catalog '{e.Name}' was used to check glass names; to trace the lens, copy it into the glass catalog folder (Materials panel).");
            }
            else notes.Add($"The archive's file '{e.Name}' was not read.");
        }
        var (text, encoding) = ZemaxText.Decode(lenses[0].Contents);
        var result = ZemaxLensReader.Read(text, catalogs, options, encoding, archiveCatalogs);
        return result with { Notes = notes.Concat(result.Notes).ToArray() };
    }

    /// <summary>Writes the optical model as a .zmx file (UTF-16 LE with a byte-order mark);
    /// see <see cref="ZemaxLensWriter.Write"/>. Nothing is written when the export fails.</summary>
    public static ZemaxExportResult Export(OpticsSettings optics, string path, ZemaxExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var result = ZemaxLensWriter.Write(optics, options);
        File.WriteAllBytes(path, ZemaxText.Encode(result.Text));
        return result;
    }
}
