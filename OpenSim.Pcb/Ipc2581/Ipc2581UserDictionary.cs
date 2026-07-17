using OpenSim.Core.Geometry2D;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// The IPC-2581 user-primitive dictionary: <c>EntryUser id</c> → arbitrary LOCAL-space
/// features (stroked paths, fills, primitive flashes — an <c>EntryUser</c> body is a
/// <c>UserSpecial</c> of ordinary feature elements), later placed by
/// <c>UserPrimitiveRef</c> under the running Location/Xform. The body is parsed by the
/// SAME feature reader the conductor Sets use (into a scratch <see cref="Ipc2581Net"/>
/// with an empty layer ref), so user figures support exactly what real features support
/// — one grammar, never two.
/// </summary>
public sealed class Ipc2581UserDictionary
{
    private readonly Dictionary<string, Ipc2581Net> _entries = new();

    public int Count => _entries.Count;

    /// <summary>Registers a parsed entry (local coordinates, empty layer refs).</summary>
    public void Add(string id, Ipc2581Net localFeatures) => _entries[id] = localFeatures;

    /// <summary>
    /// Places an entry's features into <paramref name="target"/> on
    /// <paramref name="layerRef"/> under the given placement. Returns false when the id
    /// is unknown (the caller warns — never a silent misrender).
    /// </summary>
    public bool Flash(string id, string layerRef, Ipc2581Net target, Ipc2581Transform placement)
    {
        if (!_entries.TryGetValue(id, out var local)) return false;

        foreach (var trace in local.Traces)
            target.Traces.Add(new Ipc2581Trace(layerRef, placement.ApplyPath(trace.Path), trace.Width));
        foreach (var fill in local.Fills)
            target.Fills.Add(new Ipc2581Fill(layerRef, placement.Apply(fill.Shape)));
        foreach (var pad in local.Pads)
            target.Pads.Add(new Ipc2581PadFlash(layerRef, placement.Apply(pad.Center),
                placement.Apply(pad.Shape), pad.ComponentRef, pad.Pin));
        return true;
    }
}
