using OpenSim.Core.Model;
using OpenSim.Geometry.Export;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.CadExport;

/// <summary>What one export wrote.</summary>
public sealed record CadExportResult(string Path, CadExportFormat Format, CadWriteReport Report, IReadOnlyList<string> Notes);

/// <summary>
/// CAD export of a sequential lens (OP-96, docs/Optics-Conventions.md §70): the bodies and rays of
/// <see cref="CadGeometryBuilder"/> written by the format writers of OpenSim.Geometry.Export, in the
/// system's lens unit, with the export tolerance converted from lens units to meters.
/// </summary>
public static class CadExporter
{
    /// <summary>The file filter of the save dialog.</summary>
    public const string FileFilter = "STEP AP214 (*.step;*.stp)|*.step;*.stp|IGES 5.3 (*.igs;*.iges)|*.igs;*.iges|STL (*.stl)|*.stl|DXF 2-D cross-section (*.dxf)|*.dxf";

    /// <summary>The file extension of each format.</summary>
    public static string Extension(CadExportFormat format) => format switch
    {
        CadExportFormat.Step => ".step",
        CadExportFormat.Iges => ".igs",
        CadExportFormat.Stl => ".stl",
        CadExportFormat.Dxf => ".dxf",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown CAD format."),
    };

    /// <summary>The format a file name asks for; anything else is refused by name.</summary>
    public static CadExportFormat FormatFromExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".step" or ".stp" => CadExportFormat.Step,
            ".igs" or ".iges" => CadExportFormat.Iges,
            ".stl" => CadExportFormat.Stl,
            ".dxf" => CadExportFormat.Dxf,
            ".sat" => throw new NotSupportedException("CAD export: SAT files are not built (plan item OP-76 builds them with their importer)."),
            ".pcd" => throw new NotSupportedException("CAD export: point clouds (PCD) are not built."),
            _ => throw new NotSupportedException($"CAD export: '{ext}' is not a CAD format this program writes (STEP .step/.stp, IGES .igs/.iges, STL .stl, DXF .dxf)."),
        };
    }

    /// <summary>True when a file name is one of the CAD formats (or a refused CAD one such as SAT).</summary>
    public static bool IsCadExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".step" or ".stp" or ".igs" or ".iges" or ".stl" or ".dxf" or ".sat" or ".pcd";

    /// <summary>Exports a project's optics record (its solved lens and system data).</summary>
    public static CadExportResult Export(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, CadExportSettings settings,
        string path, DateTime? timestamp = null, string? name = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        var notes = new List<string>();
        var lens = ProjectLens.From(optics, catalogs)
                   ?? throw new InvalidOperationException("CAD export: the optical system has no sequential surfaces.");
        var system = SystemDataSettings.FromSettings(optics.SystemData, notes);
        name ??= optics.SystemData?.Title is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(path);
        var result = Export(lens, system, settings, path, timestamp, name, cancellation);
        return result with { Notes = notes.Concat(result.Notes).ToList() };
    }

    /// <summary>Exports a lens with its system data.</summary>
    public static CadExportResult Export(SequentialLens lens, OpticalSystemData system, CadExportSettings settings, string path,
        DateTime? timestamp = null, string name = "lens", CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(path);
        var notes = new List<string>();
        var scene = CadGeometryBuilder.Build(lens, system, settings, notes, cancellation);
        var options = Options(system, settings, timestamp, name);
        var report = Write(scene, path, settings, options);
        notes.AddRange(report.Notes);
        return new CadExportResult(path, settings.Format, report, notes);
    }

    /// <summary>The writer options of an export: the lens unit and the tolerance in meters.</summary>
    public static CadWriteOptions Options(OpticalSystemData system, CadExportSettings settings, DateTime? timestamp, string name)
    {
        var unit = system.Units.LensUnit switch
        {
            LensUnit.Millimeter => CadLengthUnit.Millimeter,
            LensUnit.Centimeter => CadLengthUnit.Centimeter,
            LensUnit.Inch => CadLengthUnit.Inch,
            LensUnit.Meter => CadLengthUnit.Meter,
            var u => throw new ArgumentOutOfRangeException(nameof(system), u, "Unknown lens unit."),
        };
        return new CadWriteOptions
        {
            Unit = unit,
            Tolerance = settings.Tolerance * OpticsConventions.MetersPer(system.Units.LensUnit),
            Name = name,
            Timestamp = timestamp,
        };
    }

    public static CadWriteReport Write(CadScene scene, string path, CadExportSettings settings, CadWriteOptions options) => settings.Format switch
    {
        CadExportFormat.Step => StepWriter.Write(scene, path, options),
        CadExportFormat.Iges => IgesWriter.Write(scene, path, options),
        CadExportFormat.Stl => StlWriter.Write(scene, path, options, settings.StlBinary),
        CadExportFormat.Dxf => DxfWriter.Write(CrossSectionLinework.Build(scene, options.Tolerance), path, options),
        _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Format, "Unknown CAD format."),
    };
}
