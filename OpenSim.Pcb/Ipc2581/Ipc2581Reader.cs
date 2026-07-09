using OpenSim.Pcb.Import;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// Reads an IPC-2581 revision B file (<c>.cvg</c> / <c>.xml</c>) into a
/// <see cref="PcbBoard"/> — the same output as the Gerber <see cref="PcbBoardReader"/>,
/// so the whole net-selection / meshing / electrode workflow downstream is shared. The
/// bonus over Gerber: real net names, an authoritative stackup, and file-declared via
/// connectivity.
/// </summary>
public sealed class Ipc2581Reader
{
    public PcbBoard Read(string path)
    {
        var design = new Ipc2581Parser().Parse(path);
        return new Ipc2581BoardBuilder().Build(design);
    }

    public PcbBoard Read(Stream stream)
    {
        var design = new Ipc2581Parser().Parse(stream);
        return new Ipc2581BoardBuilder().Build(design);
    }

    /// <summary>Whether a file extension is an IPC-2581 candidate.</summary>
    public static bool Matches(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".cvg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }
}
