namespace OpenSim.Optics.Interop.Zemax;

/// <summary>
/// A Zemax lens file (or archive) that cannot be read, or an optical system that cannot be
/// written as one, in the strict mode (OP-94, docs/Optics-Conventions.md section 52). Every
/// problem is collected first and listed, one line each naming the keyword, the line and the
/// surface (reading) or the field and the surface (writing).
/// </summary>
public sealed class ZemaxFileException : Exception
{
    public ZemaxFileException(string what, IReadOnlyList<string> problems)
        : base(what + Environment.NewLine + string.Join(Environment.NewLine, problems))
    {
        Problems = problems;
    }

    /// <summary>One line per problem.</summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>Choices for reading a Zemax lens file.</summary>
public sealed class ZemaxImportOptions
{
    /// <summary>False (the default): content this build does not read (a keyword, a surface
    /// type, a solve code, a merit-function, tolerance or configuration row, a glass not in the
    /// catalogs) fails the import, every problem listed. True: each is listed in
    /// <see cref="ZemaxImportResult.Warnings"/> instead and left out of the lens (a glass not
    /// found keeps its name, so the lens names it when traced).</summary>
    public bool UnsupportedAsWarnings { get; init; }

    /// <summary>Glass names to replace, file name → catalog name (case ignored), used only
    /// when the file's glass is not in the catalogs; each use is listed in the notes.</summary>
    public IReadOnlyDictionary<string, string>? GlassSubstitutions { get; init; }
}

/// <summary>Choices for writing a Zemax lens file.</summary>
public sealed class ZemaxExportOptions
{
    /// <summary>False (the default): anything in the optical system the written subset cannot
    /// hold fails the export, every item listed. True: each is listed in
    /// <see cref="ZemaxExportResult.Warnings"/> and not written.</summary>
    public bool UnsupportedAsWarnings { get; init; }
}

/// <summary>A lens read from a Zemax file.</summary>
/// <param name="Optics">The optical model as the project and lens files save it.</param>
/// <param name="Warnings">Content not read (only with
/// <see cref="ZemaxImportOptions.UnsupportedAsWarnings"/>; empty otherwise).</param>
/// <param name="Notes">What was read only in part or deliberately not read (display settings,
/// fields of a line this build does not interpret, glass substitutions), one line each.</param>
/// <param name="ArchiveCatalogs">Glass catalogs found inside a .zar archive (empty for a .zmx).</param>
/// <param name="Encoding">The text encoding the file was read in.</param>
public sealed record ZemaxImportResult(
    OpenSim.Core.Model.OpticsSettings Optics,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes,
    IReadOnlyList<Materials.OpticalCatalog> ArchiveCatalogs,
    string Encoding);

/// <summary>A lens written as a Zemax file.</summary>
/// <param name="Text">The file's text (lines joined by CR LF).</param>
/// <param name="Warnings">Content not written (only with
/// <see cref="ZemaxExportOptions.UnsupportedAsWarnings"/>; empty otherwise).</param>
/// <param name="Notes">What was written in a form re-reading gives differently, one line each.</param>
public sealed record ZemaxExportResult(string Text, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes);
