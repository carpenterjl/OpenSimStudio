using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.Cli;

// OP-99: "optics macro" runs a ZPL macro on a lens (docs/Optics-Conventions.md §56).

public static partial class CliRunner
{
    /// <summary>
    /// <c>opensim optics macro &lt;macro.zpl&gt; [--lens FILE] [--macro-folder DIR] [--lens-folder DIR]
    /// [--arg NAME=VALUE[,NAME=VALUE...]] [--out TEXT] [--save-lens FILE] [--builtin-catalogs]</c>:
    /// runs the macro on the lens of a project, lens file or .zmx (none: a system with no
    /// surfaces), prints its screen output, and optionally writes the output and the changed lens.
    /// $GETARG("IsAutomated") is "True". A macro error names the macro and line; exit code 2.
    /// </summary>
    private static int OpticsMacro(string macroPath, Dictionary<string, string> o, TextWriter stdout, CancellationToken cancellation)
    {
        if (!File.Exists(macroPath)) throw new UsageException($"The macro '{macroPath}' does not exist.");
        var catalogs = LoadedCatalogs(o, _ => { });
        OpticsSettings? system = null;
        string? lensPath = null;
        if (o.TryGetValue("lens", out var lens) && lens != "true")
        {
            lensPath = lens;
            string ext = Path.GetExtension(lens).ToLowerInvariant();
            system = ext switch
            {
                ".zmx" or ".zar" => ZemaxLensFiles.Import(lens, catalogs).Optics,
                var e when e == OpticsFileSerializer.FileExtension => new OpticsFileSerializer().Load(lens).Optics ?? new OpticsSettings(),
                _ => new ProjectSerializer().Load(lens).Optics ?? throw new InvalidOperationException($"The project '{lens}' has no optical system."),
            };
        }
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o.TryGetValue("arg", out var args) && args != "true")
            foreach (var pair in args.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new UsageException($"--arg takes NAME=VALUE pairs separated by commas, not '{pair}'.");
                arguments[pair[..eq].Trim()] = pair[(eq + 1)..];
            }
        string macroFolder = o.TryGetValue("macro-folder", out var mf) && mf != "true" ? mf
            : Path.GetDirectoryName(Path.GetFullPath(macroPath))!;
        ZplMacroFolder.Current = macroFolder;   // ZPLM operands and macro solves evaluated by the macro read it
        var result = ZplMacro.RunFile(macroPath, new ZplRunOptions
        {
            System = system,
            Catalogs = catalogs,
            MacroFolder = macroFolder,
            LensFolder = o.TryGetValue("lens-folder", out var lf) && lf != "true" ? lf
                : lensPath is not null ? Path.GetDirectoryName(Path.GetFullPath(lensPath)) : macroFolder,
            LensPath = lensPath,
            Arguments = arguments,
            Automated = true,
            Output = text => stdout.Write(text),
            CancellationToken = cancellation,
        });
        if (o.TryGetValue("out", out var outPath) && outPath != "true") File.WriteAllText(outPath, result.Output);
        if (o.TryGetValue("save-lens", out var save) && save != "true")
        {
            if (string.Equals(Path.GetExtension(save), ZemaxLensFiles.LensExtension, StringComparison.OrdinalIgnoreCase))
                ZemaxLensFiles.Export(result.System, save);
            else
                new OpticsFileSerializer().Save(new OpticsLensFile { Name = Path.GetFileNameWithoutExtension(save), Optics = result.System }, save);
            stdout.WriteLine($"Saved the lens to {save}.");
        }
        return 0;
    }
}
