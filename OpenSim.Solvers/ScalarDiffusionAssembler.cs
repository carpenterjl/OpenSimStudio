using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// Assembly of the global system for a steady scalar diffusion field over TET4
/// elements: K_ij = ∫ c ∇Nᵢ·∇Nⱼ dV with a per-element coefficient c (electrical
/// conductivity σ, thermal conductivity k). One DOF per node.
/// </summary>
public sealed class ScalarDiffusionAssembler
{
    private readonly FeMesh _mesh;
    private readonly Func<int, double> _coefficient;

    /// <summary>Shape-function gradients per element, cached for flux recovery.</summary>
    private readonly Vector3D[][] _gradients;

    /// <param name="coefficient">Diffusion coefficient of one element (must be positive).</param>
    public ScalarDiffusionAssembler(FeMesh mesh, Func<int, double> coefficient)
    {
        _mesh = mesh;
        _coefficient = coefficient;
        _gradients = new Vector3D[mesh.ElementCount][];
        for (int i = 0; i < mesh.ElementCount; i++)
            _gradients[i] = Tet4ShapeGradients.Compute(mesh, i);
    }

    public int DofCount => _mesh.NodeCount;

    /// <summary>A Robin (convective) surface term h·∫NᵢNⱼdA added on one boundary triangle.</summary>
    public readonly record struct RobinTerm(BoundaryTriangle Triangle, double Coefficient);

    /// <summary>
    /// Assembles the global diffusion matrix, optionally augmented with Robin surface
    /// terms (which keep the system symmetric positive definite).
    /// </summary>
    public CsrMatrix AssembleStiffness(IReadOnlyList<RobinTerm>? robinTerms = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new SparseMatrixBuilder(DofCount, DofCount);
        Span<int> nodes = stackalloc int[4];
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            if ((el & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            double cv = _coefficient(el) * _mesh.ElementVolume(el);
            var g = _gradients[el];
            var e = _mesh.Elements[el];
            nodes[0] = e.N0; nodes[1] = e.N1; nodes[2] = e.N2; nodes[3] = e.N3;

            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    builder.Add(nodes[i], nodes[j], cv * Vector3D.Dot(g[i], g[j]));
        }

        if (robinTerms is not null)
        {
            // Consistent surface mass matrix of a linear triangle: ∫NᵢNⱼdA = A/12·(1+δᵢⱼ).
            Span<int> tn = stackalloc int[3];
            foreach (var term in robinTerms)
            {
                var t = term.Triangle;
                tn[0] = t.A; tn[1] = t.B; tn[2] = t.C;
                double a12 = term.Coefficient * TriangleArea(t) / 12.0;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        builder.Add(tn[i], tn[j], i == j ? 2 * a12 : a12);
            }
        }
        return builder.Build();
    }

    /// <summary>
    /// Assembles the consistent mass (capacity) matrix M_ij = ∫ c NᵢNⱼ dV with a
    /// per-element coefficient c (ρ·c_p for thermal capacity). For a linear tet the
    /// integral is analytic: M_ij = c·V/20·(1+δᵢⱼ) — exact, no quadrature.
    /// </summary>
    public CsrMatrix AssembleMass(Func<int, double> massCoefficient,
        CancellationToken cancellationToken = default)
    {
        var builder = new SparseMatrixBuilder(DofCount, DofCount);
        Span<int> nodes = stackalloc int[4];
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            if ((el & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            double cv20 = massCoefficient(el) * _mesh.ElementVolume(el) / 20.0;
            var e = _mesh.Elements[el];
            nodes[0] = e.N0; nodes[1] = e.N1; nodes[2] = e.N2; nodes[3] = e.N3;

            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    builder.Add(nodes[i], nodes[j], i == j ? 2 * cv20 : cv20);
        }
        return builder.Build();
    }

    /// <summary>Constant field gradient ∇φ of one element from the global solution vector.</summary>
    public Vector3D ElementGradient(int element, ReadOnlySpan<double> values)
    {
        var e = _mesh.Elements[element];
        var g = _gradients[element];
        return g[0] * values[e.N0] + g[1] * values[e.N1] + g[2] * values[e.N2] + g[3] * values[e.N3];
    }

    private double TriangleArea(BoundaryTriangle t) =>
        0.5 * Vector3D.Cross(_mesh.Nodes[t.B] - _mesh.Nodes[t.A], _mesh.Nodes[t.C] - _mesh.Nodes[t.A]).Length;
}
