using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Interop.Zemax;

namespace OpenSim.Cli;

// OP-94: Zemax lens files on the command line (docs/Optics-Conventions.md section 52):
// "optics import" reads a .zmx (or the lens in a .zar) into a lens file, "optics export" writes a
// project's or lens file's optical system as a .zmx.

public static partial class CliRunner
{
    private static int OpticsZemaxImport(string source, Dictionary<string, string> o, TextWriter stdout)
    {
        string output = o.TryGetValue("out", out var outPath) && outPath != "true"
            ? outPath
            : Path.ChangeExtension(source, OpticsFileSerializer.FileExtension);
        var catalogs = LoadedCatalogs(o, line => stdout.WriteLine(line));
        var result = ZemaxLensFiles.Import(source, catalogs, new ZemaxImportOptions { UnsupportedAsWarnings = o.ContainsKey("lenient") });
        foreach (var w in result.Warnings) stdout.WriteLine("Not read: " + w);
        foreach (var n in result.Notes) stdout.WriteLine(n);
        string name = result.Optics.SystemData?.Title is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(source);
        new OpticsFileSerializer().Save(new OpticsLensFile { Name = name, Optics = result.Optics }, output);
        stdout.WriteLine($"Imported {source} ({result.Encoding}, {result.Optics.Surfaces?.Count ?? 0} surfaces) to {output}"
            + (result.Warnings.Count > 0 ? $"; {result.Warnings.Count} items not read." : "."));
        return 0;
    }

    private static int OpticsZemaxExport(string source, Dictionary<string, string> o, TextWriter stdout)
    {
        OpticsSettings optics = string.Equals(Path.GetExtension(source), OpticsFileSerializer.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? new OpticsFileSerializer().Load(source).Optics ?? new OpticsSettings()
            : new ProjectSerializer().Load(source).Optics
              ?? throw new InvalidOperationException($"The project '{source}' has no optical system.");
        string output = o.TryGetValue("out", out var outPath) && outPath != "true"
            ? outPath
            : Path.ChangeExtension(source, ZemaxLensFiles.LensExtension);
        var result = ZemaxLensFiles.Export(optics, output, new ZemaxExportOptions { UnsupportedAsWarnings = o.ContainsKey("lenient") });
        foreach (var w in result.Warnings) stdout.WriteLine("Not written: " + w);
        foreach (var n in result.Notes) stdout.WriteLine(n);
        stdout.WriteLine($"Wrote {output}" + (result.Warnings.Count > 0 ? $"; {result.Warnings.Count} items not written." : "."));
        return 0;
    }
}
