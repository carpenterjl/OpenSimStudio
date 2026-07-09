using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Assembly of the global stiffness system for linear isotropic elasticity over
/// 4-node tetrahedra (constant-strain elements). DOF numbering is (node·3 + axis).
/// </summary>
public sealed class Tet4Assembler : IElasticityAssembler
{
    private readonly FeMesh _mesh;
    private readonly double _lambda;
    private readonly double _mu;
    private readonly double _density;

    /// <summary>Shape-function gradients per element, cached for stress recovery.</summary>
    private readonly Vector3D[][] _gradients;

    public Tet4Assembler(FeMesh mesh, Material material)
    {
        material.ValidateMechanical();
        _mesh = mesh;
        double e = material.YoungsModulus;
        double nu = material.PoissonRatio;
        _lambda = e * nu / ((1 + nu) * (1 - 2 * nu));
        _mu = e / (2 * (1 + nu));
        _density = material.Density;

        _gradients = new Vector3D[mesh.ElementCount][];
        for (int i = 0; i < mesh.ElementCount; i++)
            _gradients[i] = Tet4ShapeGradients.Compute(mesh, i);
    }

    public int DofCount => _mesh.NodeCount * 3;

    /// <summary>Assembles the global stiffness matrix.</summary>
    public CsrMatrix AssembleStiffness(CancellationToken cancellationToken = default)
    {
        var builder = new SparseMatrixBuilder(DofCount, DofCount);
        Span<int> nodes = stackalloc int[4];
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            if ((el & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            double volume = _mesh.ElementVolume(el);
            var g = _gradients[el];
            var e = _mesh.Elements[el];
            nodes[0] = e.N0; nodes[1] = e.N1; nodes[2] = e.N2; nodes[3] = e.N3;

            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    // 3x3 block: K_ij = V·(λ·g_i⊗g_j + μ·g_j⊗g_i + μ·(g_i·g_j)·I)
                    var gi = g[i];
                    var gj = g[j];
                    double dot = Vector3D.Dot(gi, gj);
                    for (int a = 0; a < 3; a++)
                    {
                        for (int b = 0; b < 3; b++)
                        {
                            double value = volume * (_lambda * gi[a] * gj[b] + _mu * gj[a] * gi[b]);
                            if (a == b) value += volume * _mu * dot;
                            builder.Add(nodes[i] * 3 + a, nodes[j] * 3 + b, value);
                        }
                    }
                }
            }
        }
        return builder.Build();
    }

    /// <summary>
    /// Consistent mass matrix. For a linear tet the integral is analytic —
    /// ∫ NᵢNⱼ dV = V/20·(1+δᵢⱼ) — so the element mass is exact, and the same nodal
    /// coupling repeats on each of the three axis DOFs (no cross-axis inertia terms).
    /// </summary>
    public CsrMatrix AssembleMass(CancellationToken cancellationToken = default)
    {
        var builder = new SparseMatrixBuilder(DofCount, DofCount);
        Span<int> nodes = stackalloc int[4];
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            if ((el & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            double rv20 = _density * _mesh.ElementVolume(el) / 20.0;
            var e = _mesh.Elements[el];
            nodes[0] = e.N0; nodes[1] = e.N1; nodes[2] = e.N2; nodes[3] = e.N3;

            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double value = i == j ? 2 * rv20 : rv20;
                    for (int a = 0; a < 3; a++)
                        builder.Add(nodes[i] * 3 + a, nodes[j] * 3 + a, value);
                }
        }
        return builder.Build();
    }

    /// <summary>
    /// Element strain tensor from a global displacement vector: ε = sym(Σ uᵢ ⊗ ∇Nᵢ).
    /// </summary>
    public SymmetricTensor ElementStrain(int element, ReadOnlySpan<double> displacements)
    {
        var e = _mesh.Elements[element];
        var g = _gradients[element];
        Span<int> nodes = stackalloc int[] { e.N0, e.N1, e.N2, e.N3 };

        double dxx = 0, dyy = 0, dzz = 0, dxy = 0, dyz = 0, dzx = 0;
        for (int i = 0; i < 4; i++)
        {
            double ux = displacements[nodes[i] * 3];
            double uy = displacements[nodes[i] * 3 + 1];
            double uz = displacements[nodes[i] * 3 + 2];
            var gi = g[i];
            dxx += ux * gi.X;
            dyy += uy * gi.Y;
            dzz += uz * gi.Z;
            dxy += 0.5 * (ux * gi.Y + uy * gi.X);
            dyz += 0.5 * (uy * gi.Z + uz * gi.Y);
            dzx += 0.5 * (uz * gi.X + ux * gi.Z);
        }
        return new SymmetricTensor(dxx, dyy, dzz, dxy, dyz, dzx);
    }

    /// <summary>Element stress from strain: σ = λ·tr(ε)·I + 2μ·ε.</summary>
    public SymmetricTensor ElementStress(int element, ReadOnlySpan<double> displacements)
    {
        var eps = ElementStrain(element, displacements);
        double trace = eps.XX + eps.YY + eps.ZZ;
        return new SymmetricTensor(
            _lambda * trace + 2 * _mu * eps.XX,
            _lambda * trace + 2 * _mu * eps.YY,
            _lambda * trace + 2 * _mu * eps.ZZ,
            2 * _mu * eps.XY,
            2 * _mu * eps.YZ,
            2 * _mu * eps.ZX);
    }
}
