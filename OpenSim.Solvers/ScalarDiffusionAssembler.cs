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
        {
            // The element volume is SIGNED and multiplies every entry: an inverted element
            // would subtract its conductance from the matrix instead of adding it.
            double volume = mesh.ElementVolume(i);
            if (!(volume > 0))
                throw new InvalidOperationException(
                    $"Element {i} has a non-positive volume ({volume:g3} m³): it is inverted or " +
                    "degenerate and cannot be assembled. Re-generate the mesh.");
            _gradients[i] = Tet4ShapeGradients.Compute(mesh, i);
        }
    }

    public int DofCount => _mesh.NodeCount;

    /// <summary>A Robin (convective) surface term h·∫NᵢNⱼdA added on one boundary triangle.</summary>
    public readonly record struct RobinTerm(BoundaryTriangle Triangle, double Coefficient);

    /// <summary>
    /// Assembles the global diffusion matrix, optionally augmented with Robin surface
    /// terms (which keep the system symmetric positive definite).
    /// </summary>
    public CsrMatrix AssembleStiffness(IReadOnlyList<RobinTerm>? robinTerms = null,
        CancellationToken cancellationToken = default) =>
        AssembleStiffness(robinTerms, null, cancellationToken);

    /// <summary>
    /// Assembles the global diffusion matrix with Robin surface terms and inter-body contact
    /// coupling. Each contact stamp scatters h_c·w·s·sᵀ — a positive multiple of a rank-1
    /// Gram matrix, so the system stays symmetric positive semi-definite and the shared CG
    /// still applies. Passing null contacts reproduces the plain assembly bitwise.
    /// </summary>
    public CsrMatrix AssembleStiffness(IReadOnlyList<RobinTerm>? robinTerms,
        IReadOnlyList<ContactInterface>? contacts, CancellationToken cancellationToken)
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
            foreach (var term in robinTerms)
                AddRobinSurface(builder, _mesh, term.Triangle, term.Coefficient);

        if (contacts is not null)
        {
            // Sequential scatter in interface then stamp order: the accumulation order of a
            // matrix entry is what makes the assembly reproducible run to run.
            Span<int> cn = stackalloc int[4];
            Span<double> cs = stackalloc double[4];
            foreach (var contact in contacts)
            {
                double h = contact.Conductance;
                foreach (var stamp in contact.Stamps)
                {
                    cn[0] = stamp.Node0; cn[1] = stamp.Node1; cn[2] = stamp.Node2; cn[3] = stamp.Node3;
                    cs[0] = stamp.S0; cs[1] = stamp.S1; cs[2] = stamp.S2; cs[3] = stamp.S3;
                    double hw = h * stamp.Weight;
                    // hw·(sᵢ·sⱼ), NOT (hw·sᵢ)·sⱼ: IEEE multiplication is commutative but not
                    // associative, so only this grouping makes the (i,j) and (j,i) scatters
                    // bitwise identical — and with them the whole matrix bitwise symmetric.
                    for (int i = 0; i < 4; i++)
                        for (int j = 0; j < 4; j++)
                            builder.Add(cn[i], cn[j], hw * (cs[i] * cs[j]));
                }
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

    /// <summary>
    /// Assembles the LUMPED mass (capacity) matrix: the row sums of the consistent one on
    /// the diagonal, c·V/4 per element node. Same total capacity, no coupling between
    /// nodes.
    /// </summary>
    public CsrMatrix AssembleLumpedMass(Func<int, double> massCoefficient,
        CancellationToken cancellationToken = default)
    {
        var diagonal = new double[DofCount];
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            if ((el & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            double cv4 = massCoefficient(el) * _mesh.ElementVolume(el) / 4.0;
            var e = _mesh.Elements[el];
            diagonal[e.N0] += cv4;
            diagonal[e.N1] += cv4;
            diagonal[e.N2] += cv4;
            diagonal[e.N3] += cv4;
        }
        var builder = new SparseMatrixBuilder(DofCount, DofCount);
        for (int i = 0; i < diagonal.Length; i++) builder.Add(i, i, diagonal[i]);
        return builder.Build();
    }

    /// <summary>
    /// Scatters one Robin surface term h·∫NᵢNⱼdA into a matrix under construction. The
    /// consistent surface mass matrix of a linear triangle is analytic: A/12·(1+δᵢⱼ).
    /// <para>
    /// Exposed because the environment's film coefficients change every nonlinear iterate
    /// and are stamped onto an already-assembled volume matrix rather than triggering a
    /// full re-assembly. One implementation, so a Robin term means exactly the same thing
    /// however it arrived.
    /// </para>
    /// </summary>
    public static void AddRobinSurface(SparseMatrixBuilder builder, FeMesh mesh,
        BoundaryTriangle triangle, double coefficient)
    {
        double a12 = coefficient * SurfaceArea(mesh, triangle) / 12.0;
        Span<int> tn = stackalloc int[3];
        tn[0] = triangle.A; tn[1] = triangle.B; tn[2] = triangle.C;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                builder.Add(tn[i], tn[j], i == j ? 2 * a12 : a12);
    }

    /// <summary>Area of one boundary triangle [m²].</summary>
    public static double SurfaceArea(FeMesh mesh, BoundaryTriangle t) =>
        0.5 * Vector3D.Cross(mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length;

    /// <summary>Constant field gradient ∇φ of one element from the global solution vector.</summary>
    public Vector3D ElementGradient(int element, ReadOnlySpan<double> values)
    {
        var e = _mesh.Elements[element];
        var g = _gradients[element];
        return g[0] * values[e.N0] + g[1] * values[e.N1] + g[2] * values[e.N2] + g[3] * values[e.N3];
    }

}
