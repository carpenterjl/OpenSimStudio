using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Solvers;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Test-side access to the internal elasticity assembler contract, so a gate can exercise
/// TET4 and TET10 through the same calls the solver makes.
/// </summary>
internal sealed class AssemblerProbe
{
    private readonly IElasticityAssembler _inner;

    public AssemblerProbe(FeMesh mesh, Material material, ElementOrder order) =>
        _inner = order == ElementOrder.Quadratic
            ? new Tet10Assembler(mesh, material)
            : new Tet4Assembler(mesh, material);

    public SymmetricTensor ElementStrain(int element, ReadOnlySpan<double> u) =>
        _inner.ElementStrain(element, u);

    public SymmetricTensor ElementStress(int element, ReadOnlySpan<double> u) =>
        _inner.ElementStress(element, u);

    public SymmetricTensor StressFromStrain(SymmetricTensor strain) =>
        _inner.StressFromStrain(strain);

    public OpenSim.Core.Numerics.CsrMatrix AssembleStiffness() => _inner.AssembleStiffness();

    public OpenSim.Core.Numerics.CsrMatrix AssembleMass() => _inner.AssembleMass();
}
