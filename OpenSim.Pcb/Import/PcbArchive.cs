using System.IO.Compression;

namespace OpenSim.Pcb.Import;

/// <summary>One file read from a board archive: its name and text content.</summary>
public sealed record ArchiveFile(string Name, string Text);

/// <summary>
/// Reads a set of Gerber/drill files from a ZIP archive or a folder. Filters to the
/// text-based fabrication extensions and skips obvious non-Gerber content.
/// </summary>
public static class PcbArchive
{
    private static readonly string[] Extensions =
        { ".gbr", ".gbl", ".gtl", ".gto", ".gbo", ".gbs", ".gts", ".gko", ".gm1",
          ".drl", ".xln", ".txt", ".nc", ".apr" };

    public static IReadOnlyList<ArchiveFile> Read(string path)
    {
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path)
                .Where(IsCandidate)
                .Select(f => new ArchiveFile(Path.GetFileName(f), File.ReadAllText(f)))
                .ToList();

        if (!File.Exists(path))
            throw new FileNotFoundException($"Board archive not found: {path}");

        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var files = new List<ArchiveFile>();
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                if (entry.Length == 0 || !IsCandidate(entry.FullName)) continue;
                using var reader = new StreamReader(entry.Open());
                files.Add(new ArchiveFile(Path.GetFileName(entry.FullName), reader.ReadToEnd()));
            }
            return files;
        }

        // A single Gerber file.
        return new[] { new ArchiveFile(Path.GetFileName(path), File.ReadAllText(path)) };
    }

    private static bool IsCandidate(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        // Altium aperture library (.apr / .APR_LIB) and macro files are not layers.
        if (name.EndsWith(".APR_LIB", StringComparison.OrdinalIgnoreCase)) return false;
        return Extensions.Contains(ext);
    }
}
