using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Point-in-solid classification against an imported <see cref="TriangleMesh"/>.
/// A thin adapter over <see cref="PointInSolidClassifier"/> (moved to Core when the CFD
/// voxelizer became a second consumer): same voting rays, same exact ray/triangle test,
/// same candidate sets — byte-identical classifications to the pre-move implementation.
/// </summary>
public sealed class SolidClassifier
{
    private readonly PointInSolidClassifier _classifier;

    public SolidClassifier(TriangleMesh mesh)
    {
        _classifier = new PointInSolidClassifier(mesh.Vertices,
            mesh.Triangles.Select(t => (t.A, t.B, t.C)).ToList());
    }

    public bool IsInside(Vector3D point) => _classifier.IsInside(point);
}
