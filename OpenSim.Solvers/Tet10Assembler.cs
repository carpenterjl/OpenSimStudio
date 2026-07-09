using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Assembly of the global stiffness system for linear isotropic elasticity over
/// 10-node quadratic tetrahedra (straight-edged/subparametric, so the barycentric
/// gradients ∇Lᵢ are constant per element). Quadratic shape functions on barycentric
/// coordinates: corner Nᵢ = Lᵢ(2Lᵢ−1), mid-edge N₍ᵢⱼ₎ = 4LᵢLⱼ. DOF numbering is
/// (node·3 + axis), identical to TET4 — mid-edge nodes are ordinary nodes.
/// </summary>
public sealed class Tet10Assembler : IElasticityAssembler
{
    // 4-point Gauss rule on the tetrahedron, degree-2 exact. The stiffness integrand
    // ∇Nᵢ·∇Nⱼ is degree ≤ 2 in the barycentric coordinates on a straight-edged tet,
    // so this rule integrates the element stiffness EXACTLY — no quadrature error
    // enters the patch test. Points are the barycentric permutations of (a, b, b, b).
    private const double GaussA = 0.5854101966249685;   // (5 + 3√5)/20
    private const double GaussB = 0.1381966011250105;   // (5 −  √5)/20

    private static readonly double[][] GaussPoints =
    {
        new[] { GaussA, GaussB, GaussB, GaussB },
        new[] { GaussB, GaussA, GaussB, GaussB },
        new[] { GaussB, GaussB, GaussA, GaussB },
        new[] { GaussB, GaussB, GaussB, GaussA }
    };

    // 14-point degree-5 symmetric rule (Keast) for the consistent mass: the integrand
    // NᵢNⱼ is degree 4, beyond the degree-2 stiffness rule. This rule's weights are ALL
    // POSITIVE, which makes the quadrature mass a Gram matrix — SPD is guaranteed on any
    // positive-volume tet. (A more compact degree-4 rule with a negative weight could
    // yield an indefinite element mass on distorted elements — disqualifying for an
    // eigensolver.) Barycentric orbits: 4×(a,b,b,b), 4×(c,d,d,d), 6×(e,e,f,f); the
    // weights are normalized to sum to 1 (they multiply the element volume).
    private const double MassA = 0.0673422422100983;    // 1 − 3·MassB
    private const double MassB = 0.3108859192633005;
    private const double MassW1 = 0.1126879257180162;
    private const double MassC = 0.7217942490673264;    // 1 − 3·MassD
    private const double MassD = 0.0927352503108912;
    private const double MassW2 = 0.0734930431163619;
    private const double MassE = 0.0455037041256497;
    private const double MassF = 0.4544962958743503;    // ½ − MassE
    private const double MassW3 = 0.0425460207770812;

    private static readonly (double[] L, double W)[] MassQuadrature = BuildMassQuadrature();

    private static (double[] L, double W)[] BuildMassQuadrature()
    {
        var points = new List<(double[], double)>();
        for (int i = 0; i < 4; i++)
        {
            var p1 = new[] { MassB, MassB, MassB, MassB };
            p1[i] = MassA;
            points.Add((p1, MassW1));
            var p2 = new[] { MassD, MassD, MassD, MassD };
            p2[i] = MassC;
            points.Add((p2, MassW2));
        }
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
            {
                var p = new[] { MassF, MassF, MassF, MassF };
                p[i] = MassE;
                p[j] = MassE;
                points.Add((p, MassW3));
            }
        return points.ToArray();
    }

    private readonly FeMesh _mesh;
    private readonly double _lambda;
    private readonly double _mu;
    private readonly double _density;

    /// <summary>Constant barycentric gradients ∇L₀..∇L₃ per element, cached for recovery.</summary>
    private readonly Vector3D[][] _cornerGradients;

    public Tet10Assembler(FeMesh mesh, Material material)
    {
        if (!mesh.IsQuadratic)
            throw new InvalidOperationException("Tet10Assembler requires a quadratic (TET10) mesh.");
        material.ValidateMechanical();
        _mesh = mesh;
        double e = material.YoungsModulus;
        double nu = material.PoissonRatio;
        _lambda = e * nu / ((1 + nu) * (1 - 2 * nu));
        _mu = e / (2 * (1 + nu));
        _density = material.Density;

        _cornerGradients = new Vector3D[mesh.ElementCount][];
        for (int i = 0; i < mesh.ElementCount; i++)
            _cornerGradients[i] = Tet4ShapeGradients.Compute(mesh, i);
    }

    public int DofCount => _mesh.NodeCount * 3;

    /// <summary>
    /// Gradients of the 10 shape functions at barycentric point L, ordered like
    /// <see cref="FeMesh.GetElementNodes"/>: 4 corners, then mids 01,02,03,12,13,23.
    /// Corner: ∇Nᵢ = (4Lᵢ−1)∇Lᵢ; mid-edge: ∇N₍ᵢⱼ₎ = 4(Lᵢ∇Lⱼ + Lⱼ∇Lᵢ).
    /// </summary>
    private static void ShapeGradients(Vector3D[] gl, double[] L, Vector3D[] result)
    {
        for (int i = 0; i < 4; i++)
            result[i] = gl[i] * (4 * L[i] - 1);
        int k = 4;
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                result[k++] = (gl[j] * L[i] + gl[i] * L[j]) * 4;
    }

    /// <summary>Values of the 10 shape functions at barycentric point L, same ordering:
    /// corner Nᵢ = Lᵢ(2Lᵢ−1); mid-edge N₍ᵢⱼ₎ = 4LᵢLⱼ.</summary>
    private static void ShapeValues(double[] L, double[] result)
    {
        for (int i = 0; i < 4; i++)
            result[i] = L[i] * (2 * L[i] - 1);
        int k = 4;
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                result[k++] = 4 * L[i] * L[j];
    }

    /// <summary>Consistent mass via the degree-5 rule (see the constants above): the
    /// NᵢNⱼ integrand is degree 4 on a straight-edged tet, so the element mass is exact.</summary>
    public CsrMatrix AssembleMass(CancellationToken cancellationToken = default)
    {
        var builder = new SparseMatrixBuilder(DofCount, DofCount);
        var shape = new double[10];
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            if ((el & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            double rhoV = _density * _mesh.ElementVolume(el);
            var nodes = _mesh.GetElementNodes(el);

            foreach (var (point, weight) in MassQuadrature)
            {
                ShapeValues(point, shape);
                double w = rhoV * weight;
                for (int i = 0; i < 10; i++)
                    for (int j = 0; j < 10; j++)
                    {
                        double value = w * shape[i] * shape[j];
                        for (int a = 0; a < 3; a++)
                            builder.Add(nodes[i] * 3 + a, nodes[j] * 3 + a, value);
                    }
            }
        }
        return builder.Build();
    }

    public CsrMatrix AssembleStiffness(CancellationToken cancellationToken = default)
    {
        var builder = new SparseMatrixBuilder(DofCount, DofCount);
        var g = new Vector3D[10];
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            if ((el & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            double volume = _mesh.ElementVolume(el);
            double weight = volume / 4.0;                       // equal Gauss weights
            var gl = _cornerGradients[el];
            var nodes = _mesh.GetElementNodes(el);

            foreach (var point in GaussPoints)
            {
                ShapeGradients(gl, point, g);
                for (int i = 0; i < 10; i++)
                {
                    for (int j = 0; j < 10; j++)
                    {
                        // Same isotropic 3x3 block as TET4, per Gauss point:
                        // K_ij += w·(λ·g_i⊗g_j + μ·g_j⊗g_i + μ·(g_i·g_j)·I)
                        var gi = g[i];
                        var gj = g[j];
                        double dot = Vector3D.Dot(gi, gj);
                        for (int a = 0; a < 3; a++)
                        {
                            for (int b = 0; b < 3; b++)
                            {
                                double value = weight * (_lambda * gi[a] * gj[b] + _mu * gj[a] * gi[b]);
                                if (a == b) value += weight * _mu * dot;
                                builder.Add(nodes[i] * 3 + a, nodes[j] * 3 + b, value);
                            }
                        }
                    }
                }
            }
        }
        return builder.Build();
    }

    /// <summary>
    /// Element strain: symmetric gradient averaged over the 4 Gauss points (strain
    /// varies linearly over a TET10, so the Gauss average equals the centroid value
    /// while reusing the assembly quadrature).
    /// </summary>
    public SymmetricTensor ElementStrain(int element, ReadOnlySpan<double> displacements)
    {
        var gl = _cornerGradients[element];
        var nodes = _mesh.GetElementNodes(element);
        var g = new Vector3D[10];

        double dxx = 0, dyy = 0, dzz = 0, dxy = 0, dyz = 0, dzx = 0;
        foreach (var point in GaussPoints)
        {
            ShapeGradients(gl, point, g);
            for (int i = 0; i < 10; i++)
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
        }
        const double inv = 1.0 / 4.0;
        return new SymmetricTensor(dxx * inv, dyy * inv, dzz * inv, dxy * inv, dyz * inv, dzx * inv);
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
