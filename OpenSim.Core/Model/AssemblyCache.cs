namespace OpenSim.Core.Model;

/// <summary>
/// Keeps the parts of an assembly solve that depend only on the MESHES, so pressing Solve a
/// second time does not redo them.
///
/// Every solve of an assembly merged the bodies and re-ran contact detection, and both are
/// substantial — the merge copies every node and element, and detection projects every
/// boundary triangle's vertices onto the opposing surface. Neither depends on materials, on
/// boundary conditions, or on the environment, which are what actually change between one
/// solve and the next while a user is working.
///
/// <para>
/// The key is the ORDERED IDENTITY of the bodies' meshes, not a version number.
/// <see cref="FeMesh"/> is immutable and meshing always produces a new instance, so reference
/// identity already answers "is this the same mesh?" exactly, with nothing to keep in sync.
/// </para>
/// <para>
/// What is deliberately NOT cached: the resolved boundary conditions and the material map.
/// Those move whenever the user edits a condition or reassigns a material, with no mesh change
/// to invalidate anything — caching them is how a stale scope would reach a solver.
/// </para>
/// </summary>
public sealed class AssemblyCache
{
    private FeMesh?[] _meshKey = Array.Empty<FeMesh?>();
    private FeMeshAssembler.MergedGeometry? _geometry;

    private object? _contactKey;
    private IReadOnlyList<ContactInterface>? _contacts;

    /// <summary>How many times a merge was served from the cache — for a log line, and for the
    /// tests that check the cache is actually being hit.</summary>
    public int MergeHits { get; private set; }

    /// <summary>The merged geometry of these bodies, computed once per set of meshes.</summary>
    public FeMeshAssembler.MergedGeometry Merge(IReadOnlyList<Body> bodies)
    {
        if (_geometry is not null && MatchesKey(bodies))
        {
            MergeHits++;
            return _geometry;
        }

        _geometry = FeMeshAssembler.MergeMeshes(bodies);
        _meshKey = bodies.Select(b => b.Mesh).ToArray();
        // A new merge invalidates whatever was detected on the old one.
        _contactKey = null;
        _contacts = null;
        return _geometry;
    }

    /// <summary>
    /// The full assembly: a cached merge, with conditions and materials resolved fresh.
    /// </summary>
    public FeMeshAssembler.AssembledMesh Assemble(IReadOnlyList<Body> bodies)
    {
        var geometry = Merge(bodies);
        var (conditions, materials) = FeMeshAssembler.ResolveConditions(bodies, geometry);
        return new FeMeshAssembler.AssembledMesh(geometry.Mesh, geometry.NodeBases, geometry.ElementBases,
            geometry.FaceIdBases, conditions, materials,
            geometry.EdgeIdBases, geometry.VertexIdBases);
    }

    /// <summary>
    /// Contact interfaces for the current merge, detected once per (merge, settings) pair.
    /// <paramref name="settingsKey"/> is whatever value identifies the detection parameters —
    /// a change of conductance or tolerance must miss.
    /// </summary>
    public IReadOnlyList<ContactInterface> Contacts(object settingsKey,
        Func<IReadOnlyList<ContactInterface>> detect)
    {
        if (_contacts is not null && Equals(_contactKey, settingsKey)) return _contacts;
        _contacts = detect();
        _contactKey = settingsKey;
        return _contacts;
    }

    /// <summary>Drops everything. The session raises this when a mesh or the geometry
    /// changes — the same signal that already discards the assembled mesh.</summary>
    public void Clear()
    {
        _geometry = null;
        _meshKey = Array.Empty<FeMesh?>();
        _contactKey = null;
        _contacts = null;
    }

    private bool MatchesKey(IReadOnlyList<Body> bodies)
    {
        if (_meshKey.Length != bodies.Count) return false;
        for (int i = 0; i < bodies.Count; i++)
            if (!ReferenceEquals(_meshKey[i], bodies[i].Mesh))
                return false;
        return true;
    }
}
