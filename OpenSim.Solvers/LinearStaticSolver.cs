using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Linear static structural solver over TET4 elements: small displacements, linear
/// isotropic elasticity. Produces displacement, element stress, and nodal-averaged
/// von Mises result fields.
/// </summary>
public sealed class LinearStaticSolver : ISolver
{
    public string Name => "Linear static (structural)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        input.Material.ValidateMechanical();

        if (input.RegionMaterials is { Count: > 0 })
            throw new InvalidOperationException(
                "The structural solver supports a single material; multi-material (region) solves " +
                "are only available for the electrical and thermal solvers.");

        foreach (var bc in input.BoundaryConditions)
            if (bc is not (FixedSupport or ForceLoad or PressureLoad))
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to a structural solve. " +
                    "Use fixed supports, forces, and pressures.");

        if (!input.BoundaryConditions.OfType<FixedSupport>().Any())
            throw new InvalidOperationException(
                "At least one fixed support is required; an unconstrained body cannot be solved statically.");
        if (!input.BoundaryConditions.Any(bc => bc is ForceLoad or PressureLoad))
            throw new InvalidOperationException("No loads are applied; the solution would be identically zero.");

        foreach (var bc in input.BoundaryConditions)
        {
            if (bc.FaceIds.Count == 0)
                throw new InvalidOperationException($"Boundary condition '{bc.Name}' has no faces assigned.");
            if (input.Mesh.GetFaceNodes(bc.FaceIds).Count == 0)
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' targets faces that do not exist on the mesh.");
        }
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;

        progress?.Report(new SolverProgress("Assembling stiffness matrix", 0.05));
        IElasticityAssembler assembler = mesh.IsQuadratic
            ? new Tet10Assembler(mesh, input.Material)
            : new Tet4Assembler(mesh, input.Material);
        var stiffness = assembler.AssembleStiffness(cancellationToken);
        log.Add($"Assembled {stiffness.RowCount} DOF system ({(mesh.IsQuadratic ? "TET10" : "TET4")}), " +
                $"{stiffness.NonZeroCount} non-zeros.");

        // Boundary triangles carry only corner indices; on a quadratic mesh their
        // mid-edge nodes must be addressed too — for pinning AND for loads.
        var edgeMid = mesh.IsQuadratic ? QuadraticMeshBuilder.BuildEdgeMidMap(mesh) : null;

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.25));
        var loads = BuildLoadVector(mesh, input.BoundaryConditions, edgeMid, log);
        var prescribed = BuildPrescribedDofs(mesh, input.BoundaryConditions, edgeMid, log);

        progress?.Report(new SolverProgress("Solving linear system", 0.35));
        var result = ConstrainedSystemSolver.Solve(stiffness, loads, prescribed,
            cancellationToken: cancellationToken);
        log.Add($"Conjugate gradient converged in {result.Iterations.Iterations} iterations " +
                $"(residual {result.Iterations.ResidualNorm:g3}).");

        progress?.Report(new SolverProgress("Recovering stresses", 0.85));
        var fields = BuildResultFields(mesh, assembler, result.Displacements);
        progress?.Report(new SolverProgress("Done", 1.0));

        return new SolveOutput { Fields = fields, Log = log };
    }

    /// <summary>
    /// Consistent nodal loads for uniform surface traction. Linear (T3) faces carry ⅓
    /// of a triangle's share to each corner. Quadratic (T6) faces use the classic
    /// consistent-load result for straight-sided quadratic triangles:
    /// ∫N_corner dA = 0 and ∫N_mid dA = A/3, i.e. each MID-EDGE node takes ⅓ of the
    /// triangle's share and the corners take none. Force loads are area-weighted so
    /// the resultant is exact; pressure acts along the inward normal.
    /// </summary>
    private static double[] BuildLoadVector(FeMesh mesh, IReadOnlyList<BoundaryCondition> conditions,
        Dictionary<(int, int), int>? edgeMid, List<string> log)
    {
        var loads = new double[mesh.NodeCount * 3];

        void AddTriangleShare(BoundaryTriangle t, Vector3D share)
        {
            if (edgeMid is null)
            {
                AddNodalForce(loads, t.A, share);
                AddNodalForce(loads, t.B, share);
                AddNodalForce(loads, t.C, share);
            }
            else
            {
                AddNodalForce(loads, edgeMid[Edge(t.A, t.B)], share);
                AddNodalForce(loads, edgeMid[Edge(t.B, t.C)], share);
                AddNodalForce(loads, edgeMid[Edge(t.C, t.A)], share);
            }
        }

        foreach (var bc in conditions)
        {
            switch (bc)
            {
                case ForceLoad force:
                {
                    var triangles = mesh.GetFaceTriangles(force.FaceIds);
                    double totalArea = triangles.Sum(t => TriangleArea(mesh, t));
                    if (totalArea <= 0)
                        throw new InvalidOperationException($"Force '{bc.Name}': selected faces have zero area.");
                    foreach (var t in triangles)
                        AddTriangleShare(t, force.TotalForce * (TriangleArea(mesh, t) / totalArea / 3.0));
                    log.Add($"Force '{bc.Name}': {force.TotalForce.Length:g4} N over {triangles.Count} face triangles.");
                    break;
                }
                case PressureLoad pressure:
                {
                    var triangles = mesh.GetFaceTriangles(pressure.FaceIds);
                    double totalForce = 0;
                    foreach (var t in triangles)
                    {
                        // Outward area vector; pressure pushes inward.
                        var areaVec = 0.5 * Vector3D.Cross(
                            mesh.Nodes[t.B] - mesh.Nodes[t.A],
                            mesh.Nodes[t.C] - mesh.Nodes[t.A]);
                        AddTriangleShare(t, -areaVec * (pressure.Magnitude / 3.0));
                        totalForce += areaVec.Length * pressure.Magnitude;
                    }
                    log.Add($"Pressure '{bc.Name}': {pressure.Magnitude:g4} Pa, resultant {totalForce:g4} N.");
                    break;
                }
            }
        }
        return loads;
    }

    private static Dictionary<int, double> BuildPrescribedDofs(FeMesh mesh,
        IReadOnlyList<BoundaryCondition> conditions, Dictionary<(int, int), int>? edgeMid,
        List<string> log)
    {
        var prescribed = new Dictionary<int, double>();
        foreach (var support in conditions.OfType<FixedSupport>())
        {
            var nodes = new HashSet<int>(mesh.GetFaceNodes(support.FaceIds));
            if (edgeMid is not null)
            {
                // Pinning only the corners of a quadratic face leaves its mid-edge
                // nodes free — spurious compliance at the support. Pin them too.
                foreach (var t in mesh.GetFaceTriangles(support.FaceIds))
                {
                    nodes.Add(edgeMid[Edge(t.A, t.B)]);
                    nodes.Add(edgeMid[Edge(t.B, t.C)]);
                    nodes.Add(edgeMid[Edge(t.C, t.A)]);
                }
            }
            foreach (int node in nodes)
            {
                prescribed[node * 3] = 0;
                prescribed[node * 3 + 1] = 0;
                prescribed[node * 3 + 2] = 0;
            }
            log.Add($"Fixed support '{support.Name}': {nodes.Count} nodes fully constrained.");
        }
        return prescribed;
    }

    private static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

    private static IReadOnlyList<IResultField> BuildResultFields(FeMesh mesh,
        IElasticityAssembler assembler, double[] u)
    {
        var displacement = new Vector3D[mesh.NodeCount];
        for (int i = 0; i < mesh.NodeCount; i++)
            displacement[i] = new Vector3D(u[i * 3], u[i * 3 + 1], u[i * 3 + 2]);

        var stress = new SymmetricTensor[mesh.ElementCount];
        var strain = new SymmetricTensor[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            stress[e] = assembler.ElementStress(e, u);
            strain[e] = assembler.ElementStrain(e, u);
        }

        // Volume-weighted nodal average of element von Mises for smooth contours.
        var nodalVm = new double[mesh.NodeCount];
        var nodalWeight = new double[mesh.NodeCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double vm = stress[e].VonMises();
            double w = mesh.ElementVolume(e);
            foreach (int n in mesh.GetElementNodes(e))     // all 10 nodes when quadratic
            {
                nodalVm[n] += vm * w;
                nodalWeight[n] += w;
            }
        }
        for (int i = 0; i < mesh.NodeCount; i++)
            if (nodalWeight[i] > 0)
                nodalVm[i] /= nodalWeight[i];

        return new IResultField[]
        {
            new NodalVectorField("Displacement", "m", displacement),
            new NodalScalarField("Stress (von Mises)", "Pa", nodalVm),
            new ElementTensorField("Stress tensor", "Pa", stress),
            new ElementTensorField("Strain tensor", "-", strain)
        };
    }

    private static double TriangleArea(FeMesh mesh, BoundaryTriangle t) =>
        0.5 * Vector3D.Cross(mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length;

    private static void AddNodalForce(double[] loads, int node, Vector3D force)
    {
        loads[node * 3] += force.X;
        loads[node * 3 + 1] += force.Y;
        loads[node * 3 + 2] += force.Z;
    }
}
