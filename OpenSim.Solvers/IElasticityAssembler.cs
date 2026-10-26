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
    /// The strain at each of the element's nodes, in <c>GetElementNodes</c> order: the element's
    /// own displacement field evaluated there — the TET4's constant, the TET10's linear field at
    /// its corners and mid-edges, the HEX20's quadratic field at its twenty nodes. Nodal stress
    /// recovery (D6) takes these in place of the element mean, so a bending surface keeps its
    /// surface stress instead of the mean that read it (1 − 1/n) low with n elements through the
    /// thickness. <paramref name="result"/> has at least as many entries as the element has nodes.
    /// </summary>
    void NodalStrains(int element, ReadOnlySpan<double> displacements, Span<SymmetricTensor> result);

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
