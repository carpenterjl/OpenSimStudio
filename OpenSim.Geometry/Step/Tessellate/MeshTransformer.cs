using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step.Schema;

namespace OpenSim.Geometry.Step.Tessellate;

/// <summary>
/// Places a tessellated solid into the assembly frame.
/// <para>
/// Assemblies are tessellated ONCE PER SOLID in the part's own frame and transformed
/// afterwards. That is not just a speed choice: a rigid motion preserves every distance
/// the tessellator's tolerances are expressed in (chord tolerance, weld tolerance, the
/// minimum-spacing floor), so the transformed mesh has exactly the connectivity the
/// untransformed one had — the watertightness proven at tessellation time survives the
/// placement. Tessellating a part twice in two frames would instead risk two different
/// vertex sets for one part.
/// </para>
/// Topology (triangles and their face ids) is therefore shared BY REFERENCE between
/// instances of the same part; only the vertex positions differ.
/// </summary>
internal static class MeshTransformer
{
    public static TriangleMesh Apply(TriangleMesh mesh, StepRigidTransform transform)
    {
        if (transform.IsIdentity) return mesh;
        var vertices = new Vector3D[mesh.Vertices.Count];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = transform.Apply(mesh.Vertices[i]);
        return new TriangleMesh(vertices, mesh.Triangles, mesh.TriangleFaceIds);
    }
}
