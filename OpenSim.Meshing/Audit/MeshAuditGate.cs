using OpenSim.Core.Model;

namespace OpenSim.Meshing;

/// <summary>
/// The audit for a mesh that did not just come out of a mesher: one loaded from a project
/// file, which carries no verdict (<see cref="FeMesh.Audit"/> is never persisted). Called
/// before a solve so that "a mesh that fails the audit does not reach a solver" also holds
/// for meshes made before the audit existed.
/// </summary>
public static class MeshAuditGate
{
    /// <summary>
    /// Audits the body's mesh against its geometry if it has not been audited. Returns the
    /// report to attach (null when there was nothing to do); throws
    /// <see cref="MeshAuditException"/> when the mesh fails.
    /// <para>
    /// Nothing to do means: no mesh, a mesh that already carries a report, no geometry to
    /// audit against, or a PCB body — those are laid out by the 2.5-D PCB mesher from the
    /// copper image, their <see cref="Body.Geometry"/> is a preview of one region, and the
    /// audit of that mesher is not this one.
    /// </para>
    /// </summary>
    public static MeshAuditReport? AuditIfNeeded(Body body)
    {
        if (body.Mesh is not { } mesh || mesh.Audit is not null) return null;
        if (body.Geometry is not { } geometry) return null;
        if (body.RegionMaterialNames is not null || mesh.ElementRegionIds is not null) return null;

        var audit = body.MeshSettings.Method == MeshMethod.StructuredLattice
            ? StructuredLatticeMeshGenerator.BeginAudit(geometry, body.MeshSettings)
            : DelaunayMeshGenerator.BeginAudit(geometry, body.MeshSettings);
        return audit.Check(mesh);
    }
}
