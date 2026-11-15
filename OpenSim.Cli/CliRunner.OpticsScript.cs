using OpenSim.Optics.Tolerancing;

namespace OpenSim.Cli;

// OP-67: optics tolerance --script <file.tsc> (docs/Optics-Conventions.md §60). The script runs in
// every trial of the sensitivity or Monte Carlo run in place of the compensation, as the Tolerance
// Data Editor's script block runs it; this file only reads the options.

public static partial class CliRunner
{
    /// <summary>
    /// <c>--script FILE</c>: the user script criterion with the script read from FILE;
    /// <c>--merit-folder DIR</c> (LOADMERIT's files) and <c>--script-out DIR</c> (OPENFILE and
    /// SAVE) default to the saved folders, else the script's folder.
    /// </summary>
    private static ToleranceRunOptions WithScriptOption(Dictionary<string, string> o, ToleranceRunOptions options)
    {
        if (!o.TryGetValue("script", out var path))
        {
            if (o.ContainsKey("merit-folder") || o.ContainsKey("script-out"))
                throw new UsageException("--merit-folder and --script-out go with --script FILE.");
            return options;
        }
        if (path == "true") throw new UsageException("--script needs the tolerance script's file, for example --script ALIGN.TSC.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new InvalidOperationException($"The tolerance script '{full}' does not exist.");
        string folder = Path.GetDirectoryName(full)!;
        string? Folder(string key) => o.TryGetValue(key, out var v) ? (v == "true" ? throw new UsageException($"--{key} needs a folder.") : Path.GetFullPath(v)) : null;
        var saved = options.Script;
        return options with
        {
            Criterion = ToleranceCriterion.UserScript,
            Script = (saved ?? new ToleranceScriptOptions()) with
            {
                Text = File.ReadAllText(full),
                Name = Path.GetFileName(full),
                MeritFolder = Folder("merit-folder") ?? saved?.MeritFolder ?? folder,
                OutputFolder = Folder("script-out") ?? saved?.OutputFolder ?? folder,
                MacroFolder = saved?.MacroFolder ?? folder,
            },
        };
    }
}
