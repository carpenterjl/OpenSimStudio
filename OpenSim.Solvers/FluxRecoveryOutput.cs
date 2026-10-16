using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// What a scalar solve adds when <see cref="SolveInput.FluxRecovery"/> is set: the recovered
/// flux, the element error field, the estimated error and the probes — one implementation
/// for the conduction and the heat solver.
/// </summary>
internal static class FluxRecoveryOutput
{
    /// <summary>The probes must name surfaces; checked with the rest of a solve's input.</summary>
    public static void Validate(SolveInput input)
    {
        if (input.FluxRecovery is not { } settings) return;
        var names = new HashSet<string>();
        foreach (var probe in settings.Probes)
        {
            probe.Validate();
            if (!names.Add(probe.Name))
                throw new InvalidOperationException($"Two flux probes are named '{probe.Name}'.");
            if (probe.FaceIds is { } faces && input.Mesh.GetFaceTriangles(faces).Count == 0)
                throw new InvalidOperationException(
                    $"Flux probe '{probe.Name}' names faces the mesh does not have ({string.Join(", ", faces)}).");
        }
    }

    /// <param name="flux">The flux's name and unit ("Current density", "A/m²").</param>
    /// <param name="flow">What a probe integrates and its unit ("current", "A").</param>
    public static void Add(SolveInput input, ScalarDiffusionAssembler assembler, IReadOnlyList<double> solution,
        Func<int, ConductivityTensor> conductivity, IReadOnlyList<double>? source,
        FluxProbeEvaluator.KnownOutflow known, (string Name, string Unit) flux, (string Name, string Unit) flow,
        List<IResultField> fields, Dictionary<string, double> summary, List<string> log,
        CancellationToken cancellationToken)
    {
        if (input.FluxRecovery is not { } settings) return;
        var groups = ScalarSolverHelpers.MaterialGroups(input, out var groupNames);
        var recovery = FluxRecovery.Recover(assembler, solution, conductivity, groups, groupNames, cancellationToken);

        fields.Add(new NodalVectorField($"{flux.Name} (recovered)", flux.Unit, recovery.Nodal, recovery.Sides));
        fields.Add(new ElementScalarField($"{flux.Name} error (element)", flux.Unit, recovery.ElementError));
        double percent = 100 * recovery.RelativeError;
        summary[$"{flux.Name} error estimate (%)"] = percent;
        log.Add($"{flux.Name} recovered over {(assembler.IsQuadratic ? "quadratic" : "linear")} patches around " +
                $"every corner node, one per material; the estimated error of the computed {flux.Name.ToLowerInvariant()} " +
                $"is {percent:g3} % in the energy norm (the element field shows |recovered − computed|)." +
                (recovery.FallbackNodes > 0
                    ? $" {recovery.FallbackNodes} node(s) had no well-posed patch and took the mean of their elements."
                    : ""));

        foreach (var probe in settings.Probes)
        {
            var result = FluxProbeEvaluator.Evaluate(probe, recovery, assembler, source, known);
            summary[$"Probe '{probe.Name}' {flow.Name} ({flow.Unit})"] = result.Value;
            log.Add(probe.IsFaces
                ? $"Probe '{probe.Name}': {result.Section:g6} {flow.Unit} leaving through faces " +
                  $"{string.Join(", ", probe.FaceIds!)} ({result.Area:g4} m², the recovered {flux.Name.ToLowerInvariant()})."
                : result.Conservative is { } conservative
                    ? $"Probe '{probe.Name}': {conservative:g6} {flow.Unit} through the plane (the solve's own balance of " +
                      $"the side the normal points to); the recovered {flux.Name.ToLowerInvariant()} over the section " +
                      $"({result.Area:g4} m²) gives {result.Section:g6} {flow.Unit}."
                    : $"Probe '{probe.Name}': {result.Section:g6} {flow.Unit} through the plane inside its box " +
                      $"({result.Area:g4} m², the recovered {flux.Name.ToLowerInvariant()}).");
        }
    }

    /// <summary>
    /// The outward current density a conduction solve's conditions give on the skin: the
    /// uniform density of an injected current (inflow, so negative), nothing through a face
    /// no condition holds, and unknown on a voltage or an equipotential terminal.
    /// </summary>
    public static FluxProbeEvaluator.KnownOutflow ConductionOutflow(SolveInput input)
    {
        var mesh = input.Mesh;
        var value = new double?[mesh.BoundaryTriangles.Count];
        Array.Fill(value, 0.0);
        var index = Indices(mesh);
        foreach (var current in input.BoundaryConditions.OfType<CurrentFlow>().Where(c => !c.Equipotential))
            AddUniform(mesh, index, value, current.FaceIds, -current.TotalCurrent);
        foreach (var bc in input.BoundaryConditions)
            if (bc is VoltagePotential or CurrentFlow { Equipotential: true })
                foreach (var t in mesh.GetFaceTriangles(bc.FaceIds))
                    value[index[t]] = null;
        return (t, _) => value[t];
    }

    /// <summary>
    /// The outward heat flux a thermal solve's conditions give on the skin: the uniform
    /// density of a heat flow (inflow, so negative), h·(T − T∞) of a convection with the
    /// solved temperature at the point, nothing through a face no condition holds, and
    /// unknown on a fixed temperature.
    /// </summary>
    public static FluxProbeEvaluator.KnownOutflow ThermalOutflow(SolveInput input, ScalarDiffusionAssembler assembler,
        IReadOnlyList<double> temperature)
    {
        var mesh = input.Mesh;
        var uniform = new double?[mesh.BoundaryTriangles.Count];
        Array.Fill(uniform, 0.0);
        var films = new List<(double H, double Ambient)>?[mesh.BoundaryTriangles.Count];
        var index = Indices(mesh);
        foreach (var flow in input.BoundaryConditions.OfType<HeatFlux>())
            AddUniform(mesh, index, uniform, flow.FaceIds, -flow.TotalPower);
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
            foreach (var (t, h) in ConvectionTerms.Triangles(mesh, convection))
                (films[index[t]] ??= new List<(double, double)>()).Add((h, convection.AmbientTemperature));
        foreach (var fixedTemperature in input.BoundaryConditions.OfType<FixedTemperature>())
            foreach (var t in mesh.GetFaceTriangles(fixedTemperature.FaceIds))
                uniform[index[t]] = null;

        return (ti, x) =>
        {
            if (uniform[ti] is not { } outflow) return null;
            if (films[ti] is not { } list) return outflow;
            double at = TriangleValue(assembler, mesh.BoundaryTriangles[ti], temperature, x);
            foreach (var (h, ambient) in list) outflow += h * (at - ambient);
            return outflow;
        };
    }

    private static Dictionary<BoundaryTriangle, int> Indices(FeMesh mesh)
    {
        var index = new Dictionary<BoundaryTriangle, int>(mesh.BoundaryTriangles.Count);
        for (int i = 0; i < mesh.BoundaryTriangles.Count; i++) index[mesh.BoundaryTriangles[i]] = i;
        return index;
    }

    private static void AddUniform(FeMesh mesh, Dictionary<BoundaryTriangle, int> index, double?[] value,
        IReadOnlyList<int> faces, double total)
    {
        var triangles = mesh.GetFaceTriangles(faces);
        double area = triangles.Sum(t => ScalarSolverHelpers.TriangleArea(mesh, t));
        foreach (var t in triangles) value[index[t]] += total / area;
    }

    /// <summary>The field at a point of a boundary triangle, interpolated over its three or
    /// six nodes.</summary>
    private static double TriangleValue(ScalarDiffusionAssembler assembler, BoundaryTriangle t,
        IReadOnlyList<double> field, Vector3D x)
    {
        var mesh = assembler.Mesh;
        Vector3D a = mesh.Nodes[t.A], b = mesh.Nodes[t.B], c = mesh.Nodes[t.C];
        var normal = Vector3D.Cross(b - a, c - a);
        double twice = normal.LengthSquared;
        Span<double> l = stackalloc double[3];
        l[0] = Vector3D.Dot(Vector3D.Cross(b - x, c - x), normal) / twice;
        l[1] = Vector3D.Dot(Vector3D.Cross(c - x, a - x), normal) / twice;
        l[2] = 1 - l[0] - l[1];
        var nodes = assembler.TriangleNodes(t);
        if (nodes.Length == 3) return l[0] * field[t.A] + l[1] * field[t.B] + l[2] * field[t.C];
        Span<double> n = stackalloc double[6];
        ScalarElements.Tri6Values(l, n);
        double sum = 0;
        for (int i = 0; i < 6; i++) sum += n[i] * field[nodes[i]];
        return sum;
    }
}
