using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;

namespace OpenSim.Meshing;

/// <summary>
/// Routes a mesh request to the mesher its settings name.
/// <para>
/// The method is a property of the body's PERSISTED settings rather than of the viewmodel
/// that happened to start the run, which is what makes "mesh every body" and a reloaded
/// project mesh the way the project says — with no UI state involved. Registering this as
/// the single <see cref="IMeshGenerator"/> keeps every existing consumer, which asks for
/// one generator and calls it, unchanged.
/// </para>
/// </summary>
public sealed class MeshGeneratorSelector : IMeshGenerator
{
    private readonly IMeshGenerator _delaunay;
    private readonly IMeshGenerator _lattice;

    public MeshGeneratorSelector(DelaunayMeshGenerator delaunay, StructuredLatticeMeshGenerator lattice)
    {
        _delaunay = delaunay;
        _lattice = lattice;
    }

    public string Name => "Mesh generator (by method)";

    /// <summary>The mesher a given set of settings selects. Null means Delaunay, so every
    /// project written before methods existed routes exactly where it always did.</summary>
    public IMeshGenerator For(MeshSettings settings) =>
        settings.Method == MeshMethod.StructuredLattice ? _lattice : _delaunay;

    public FeMesh Generate(TriangleMesh geometry, MeshSettings settings,
        CancellationToken cancellationToken = default) =>
        For(settings).Generate(geometry, settings, cancellationToken);
}
