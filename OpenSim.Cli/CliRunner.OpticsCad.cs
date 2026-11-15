using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.CadExport;

namespace OpenSim.Cli;

// OP-96: CAD export on the command line (docs/Optics-Conventions.md §70). "optics export" writes a
// CAD file when --format names one or --out has a CAD extension; otherwise it is OP-94's .zmx export.

public static partial class CliRunner
{
    /// <summary>True when an "optics export" asks for a CAD file rather than a .zmx.</summary>
    private static bool IsCadExport(Dictionary<string, string> o) =>
        o.ContainsKey("format") || (o.TryGetValue("out", out var p) && p != "true" && CadExporter.IsCadExtension(p));

    private static int OpticsCadExport(string source, Dictionary<string, string> o, TextWriter stdout)
    {
        OpticsSettings optics = string.Equals(Path.GetExtension(source), OpticsFileSerializer.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? new OpticsFileSerializer().Load(source).Optics ?? new OpticsSettings()
            : new ProjectSerializer().Load(source).Optics
              ?? throw new InvalidOperationException($"The project '{source}' has no optical system.");
        var formats = new Dictionary<string, CadExportFormat>
        {
            ["step"] = CadExportFormat.Step, ["stp"] = CadExportFormat.Step, ["iges"] = CadExportFormat.Iges, ["igs"] = CadExportFormat.Iges,
            ["stl"] = CadExportFormat.Stl, ["dxf"] = CadExportFormat.Dxf,
        };
        CadExportFormat format;
        if (o.TryGetValue("format", out var f))
        {
            if (f.Equals("sat", StringComparison.OrdinalIgnoreCase))
                throw new UsageException("--format sat: SAT files are not built (plan item OP-76 builds them with their importer).");
            format = Choice(o, "format", CadExportFormat.Step, formats);
        }
        else
        {
            try { format = CadExporter.FormatFromExtension(o["out"]); }
            catch (NotSupportedException ex) { throw new UsageException(ex.Message); }
        }
        string output = o.TryGetValue("out", out var outPath) && outPath != "true"
            ? outPath
            : Path.ChangeExtension(source, CadExporter.Extension(format));

        int Whole(string key, int fallback)
        {
            double v = Number(o, key, fallback);
            return v == Math.Floor(v) && v >= 0 && v <= int.MaxValue ? (int)v : throw new UsageException($"--{key} takes a whole number, not '{o[key]}'.");
        }
        var settings = new CadExportSettings
        {
            Format = format,
            FirstSurface = Whole("first", 1),
            LastSurface = Whole("last", 0),
            EdgeStyle = Choice(o, "edges", CadEdgeStyle.Squared, new() { ["squared"] = CadEdgeStyle.Squared, ["tapered"] = CadEdgeStyle.Tapered, ["flat"] = CadEdgeStyle.FlatToNext }),
            MirrorSubstrateThickness = Number(o, "substrate", 0),
            Tolerance = Number(o, "tolerance", 1e-4),
            RayPattern = Choice(o, "rays", CadRayPattern.YFan, new()
            {
                ["none"] = CadRayPattern.None, ["yfan"] = CadRayPattern.YFan, ["xfan"] = CadRayPattern.XFan, ["xyfan"] = CadRayPattern.XYFan,
                ["ring"] = CadRayPattern.Ring, ["grid"] = CadRayPattern.Grid,
            }),
            NumberOfRays = Whole("nrays", 7),
            Field = Whole("field", 0),
            Wavelength = Whole("wavelength", 0),
            DeleteVignetted = o.ContainsKey("delete-vignetted"),
            StlBinary = !o.ContainsKey("ascii"),
        };
        if (settings.Validate() is { Count: > 0 } problems) throw new UsageException(string.Join(Environment.NewLine, problems));
        var catalogs = LoadedCatalogs(o, line => stdout.WriteLine(line));
        var result = CadExporter.Export(optics, catalogs, settings, output);
        foreach (var n in result.Notes) stdout.WriteLine(n);
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {output} ({format})."));
        return 0;
    }
}
