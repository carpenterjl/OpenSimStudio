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
            BoundaryScope.Validate(bc, input.Mesh);
        }
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;

        progress?.Report(new SolverProgress("Assembling stiffness matrix", 0.05));
        var assembler = StructuralSurfaceLoads.AssemblerFor(mesh, input.Material);
        var stiffness = assembler.AssembleStiffness(cancellationToken);
        log.Add($"Assembled {stiffness.RowCount} DOF system " +
                $"({StructuralSurfaceLoads.ElementName(mesh)}), {stiffness.NonZeroCount} non-zeros.");

        // Boundary triangles carry only corner indices; on a quadratic mesh their
        // mid-edge nodes must be addressed too — for pinning AND for loads.
        var edgeMid = mesh.IsQuadratic ? QuadraticMeshBuilder.BuildEdgeMidMap(mesh) : null;

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.25));
        var loads = BuildLoadVector(mesh, input.BoundaryConditions, edgeMid, log);
        var prescribed = StructuralSurfaceLoads.BuildPrescribedDofs(
            mesh, input.BoundaryConditions, edgeMid, log);

        progress?.Report(new SolverProgress("Solving linear system", 0.35));
        var result = ConstrainedSystemSolver.Solve(stiffness, loads, prescribed,
            cancellationToken: cancellationToken);
        log.Add($"Conjugate gradient converged in {result.Iterations.Iterations} iterations " +
                $"(residual {result.Iterations.ResidualNorm:g3}).");

        progress?.Report(new SolverProgress("Recovering stresses", 0.85));
        var fields = BuildResultFields(mesh, assembler, input.Material, result.Displacements);
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
    /// <para>
    /// A HEXAHEDRAL mesh is loaded through its QUAD skin instead
    /// (<see cref="StructuralSurfaceLoads.QuadLoadWeights"/>). Its triangular skin exists for
    /// rendering, contact and edge extraction, but it is not a load surface: the QUAD8 face of
    /// a HEX20 has its own consistent weights — A/3 per mid-side node and MINUS A/12 per
    /// corner — and the triangulation's diagonal is not an element edge at all.
    /// </para>
    /// </summary>
    private static double[] BuildLoadVector(FeMesh mesh, IReadOnlyList<BoundaryCondition> conditions,
        Dictionary<(int, int), int>? edgeMid, List<string> log)
    {
        if (mesh.IsHex) return BuildQuadLoadVector(mesh, conditions, edgeMid!, log);

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
                    // Areas once: the total and each triangle's share read the same numbers.
                    var areas = new double[triangles.Count];
                    double totalArea = 0;
                    for (int i = 0; i < triangles.Count; i++)
                    {
                        areas[i] = TriangleArea(mesh, triangles[i]);
                        totalArea += areas[i];
                    }
                    if (totalArea <= 0)
                        throw new InvalidOperationException($"Force '{bc.Name}': selected faces have zero area.");
                    for (int i = 0; i < triangles.Count; i++)
                        AddTriangleShare(triangles[i], force.TotalForce * (areas[i] / totalArea / 3.0));
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

    private static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

    private static IReadOnlyList<IResultField> BuildResultFields(FeMesh mesh,
        IElasticityAssembler assembler, Material material, double[] u)
    {
        var displacement = new Vector3D[mesh.NodeCount];
        for (int i = 0; i < mesh.NodeCount; i++)
            displacement[i] = new Vector3D(u[i * 3], u[i * 3 + 1], u[i * 3 + 2]);

        // One strain evaluation per element, stress derived from it. ElementStress would
        // recompute the same strain internally — on a quadratic mesh that is a second
        // four-Gauss-point pass per element for a tensor already in hand.
        var stress = new SymmetricTensor[mesh.ElementCount];
        var strain = new SymmetricTensor[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            strain[e] = assembler.ElementStrain(e, u);
            stress[e] = assembler.StressFromStrain(strain[e]);
        }

        // Volume-weighted nodal averages for smooth contours. Equivalent elastic strain is
        // averaged alongside von Mises rather than derived from the averaged stress: the
        // two agree exactly per element (E·ε_eq = σ_vm), and averaging each invariant
        // separately keeps that true of the nodal values as well.
        var nodalVm = new double[mesh.NodeCount];
        var nodalStrain = new double[mesh.NodeCount];
        var nodalWeight = new double[mesh.NodeCount];
        double nu = material.PoissonRatio;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double vm = stress[e].VonMises();
            double eq = strain[e].EquivalentStrain(nu);
            double w = mesh.ElementVolume(e);
            foreach (int n in mesh.GetElementNodes(e))     // all 10 nodes when quadratic
            {
                nodalVm[n] += vm * w;
                nodalStrain[n] += eq * w;
                nodalWeight[n] += w;
            }
        }
        for (int i = 0; i < mesh.NodeCount; i++)
            if (nodalWeight[i] > 0)
            {
                nodalVm[i] /= nodalWeight[i];
                nodalStrain[i] /= nodalWeight[i];
            }

        var fields = new List<IResultField>
        {
            new NodalVectorField("Displacement", "m", displacement),
            new NodalScalarField("Stress (von Mises)", "Pa", nodalVm),
            new NodalScalarField("Equivalent elastic strain", "m/m", nodalStrain),
            new ElementTensorField("Stress tensor", "Pa", stress),
            new ElementTensorField("Strain tensor", "-", strain)
        };

        // Safety factor is offered ONLY when the material carries a strength. A default
        // would be a fabricated allowable, and a brittle material has no yield point at all.
        if (material.YieldStrength is { } yield)
            fields.Add(new NodalScalarField("Safety factor (yield)", "-",
                SafetyFactor(nodalVm, yield)));
        if (material.UltimateTensileStrength is { } ultimate)
            fields.Add(new NodalScalarField("Safety factor (ultimate)", "-",
                SafetyFactor(nodalVm, ultimate)));

        return fields;
    }

    /// <summary>
    /// Strength / von Mises, per node. Capped rather than left to run to infinity where the
    /// stress vanishes: an unstressed node is not usefully "infinitely safe", and one
    /// infinity would flatten the whole colour scale so no real margin could be read off it.
    /// The cap is not encoded in the field name; it is stated here and in the exported report.
    /// </summary>
    private const double MaxReportedSafetyFactor = 15.0;

    private static double[] SafetyFactor(double[] nodalVonMises, double strength)
    {
        var factors = new double[nodalVonMises.Length];
        for (int i = 0; i < factors.Length; i++)
            factors[i] = nodalVonMises[i] <= 0
                ? MaxReportedSafetyFactor
                : Math.Min(strength / nodalVonMises[i], MaxReportedSafetyFactor);
        return factors;
    }

    /// <summary>The hexahedral load path: consistent QUAD8 loads over the quad skin.</summary>
    private static double[] BuildQuadLoadVector(FeMesh mesh,
        IReadOnlyList<BoundaryCondition> conditions, Dictionary<(int, int), int> edgeMid,
        List<string> log)
    {
        var loads = new double[mesh.NodeCount * 3];

        foreach (var bc in conditions)
        {
            switch (bc)
            {
                case ForceLoad force:
                {
                    var quads = mesh.GetFaceQuads(force.FaceIds);
                    var weights = quads
                        .Select(q => StructuralSurfaceLoads.QuadLoadWeights(mesh, q, edgeMid))
                        .ToList();

                    // The shape functions sum to one, so the per-node area integrals of a face
                    // sum to its area — total area comes out of the very numbers the shares
                    // are built from.
                    double totalArea = weights.Sum(w => w.Areas.Sum());
                    if (totalArea <= 0)
                        throw new InvalidOperationException($"Force '{bc.Name}': selected faces have zero area.");

                    foreach (var (nodes, areas, _) in weights)
                        for (int k = 0; k < nodes.Length; k++)
                            AddNodalForce(loads, nodes[k], force.TotalForce * (areas[k] / totalArea));

                    log.Add($"Force '{bc.Name}': {force.TotalForce.Length:g4} N over {quads.Count} face quads.");
                    break;
                }
                case PressureLoad pressure:
                {
                    var quads = mesh.GetFaceQuads(pressure.FaceIds);
                    double totalForce = 0;
                    foreach (var q in quads)
                    {
                        var (nodes, areas, areaVectors) =
                            StructuralSurfaceLoads.QuadLoadWeights(mesh, q, edgeMid);
                        for (int k = 0; k < nodes.Length; k++)
                            AddNodalForce(loads, nodes[k], -areaVectors[k] * pressure.Magnitude);
                        totalForce += areas.Sum() * pressure.Magnitude;
                    }
                    log.Add($"Pressure '{bc.Name}': {pressure.Magnitude:g4} Pa, resultant {totalForce:g4} N.");
                    break;
                }
            }
        }
        return loads;
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
