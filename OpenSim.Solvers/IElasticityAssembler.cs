using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Shared contract of the elasticity assemblers so <see cref="LinearStaticSolver"/>
/// can pick TET4 or TET10 assembly by the mesh's element order.
/// </summary>
internal interface IElasticityAssembler
{
    int DofCount { get; }
    CsrMatrix AssembleStiffness(CancellationToken cancellationToken = default);

    /// <summary>Consistent mass matrix M_ij = ∫ ρ NᵢNⱼ dV per axis (block-diagonal in
    /// the axis: x, y and z share the same nodal coupling). SPD by construction.</summary>
    CsrMatrix AssembleMass(CancellationToken cancellationToken = default);

    SymmetricTensor ElementStrain(int element, ReadOnlySpan<double> displacements);
    SymmetricTensor ElementStress(int element, ReadOnlySpan<double> displacements);

    /// <summary>
    /// Hooke's law applied to an ALREADY COMPUTED element strain: σ = λ·tr(ε)·I + 2μ·ε.
    /// <para>
    /// Exists because recovery needs both tensors per element and <see cref="ElementStress"/>
    /// derives the strain itself — calling both recomputes it, which on TET10 means a second
    /// four-Gauss-point pass and a second array allocation for a result already in hand. The
    /// arithmetic here is byte-for-byte what <see cref="ElementStress"/> performs on the same
    /// strain, so the two paths agree bitwise.
    /// </para>
    /// </summary>
    SymmetricTensor StressFromStrain(SymmetricTensor strain);
}
