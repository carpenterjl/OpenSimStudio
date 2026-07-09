using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// Constant gradients of the four linear TET4 shape functions, shared by the
/// elasticity and scalar-diffusion assemblers.
/// </summary>
internal static class Tet4ShapeGradients
{
    /// <summary>
    /// Gradients built from the inverse of the edge matrix J = [d1 d2 d3];
    /// ∇N₀ = −(∇N₁+∇N₂+∇N₃).
    /// </summary>
    public static Vector3D[] Compute(FeMesh mesh, int element)
    {
        var e = mesh.Elements[element];
        var p0 = mesh.Nodes[e.N0];
        var d1 = mesh.Nodes[e.N1] - p0;
        var d2 = mesh.Nodes[e.N2] - p0;
        var d3 = mesh.Nodes[e.N3] - p0;

        double det = Vector3D.Dot(d1, Vector3D.Cross(d2, d3));
        if (Math.Abs(det) < 1e-300)
            throw new InvalidOperationException($"Element {element} is degenerate (zero volume).");

        var r1 = Vector3D.Cross(d2, d3) / det;
        var r2 = Vector3D.Cross(d3, d1) / det;
        var r3 = Vector3D.Cross(d1, d2) / det;
        var r0 = -(r1 + r2 + r3);
        return new[] { r0, r1, r2, r3 };
    }
}
