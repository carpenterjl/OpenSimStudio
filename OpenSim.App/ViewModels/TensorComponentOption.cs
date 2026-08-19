using OpenSim.Core.Results;

namespace OpenSim.App.ViewModels;

/// <summary>
/// One row of the tensor-component picker. A display record rather than a bare enum so the
/// combo shows engineering names ("Shear XY") instead of enum identifiers.
/// </summary>
public sealed record TensorComponentOption(string Label, TensorComponent Component)
{
    public override string ToString() => Label;

    /// <summary>
    /// Every component offered, von Mises FIRST so it is the default and a tensor field
    /// opens showing exactly what it showed before components existed.
    /// </summary>
    public static IReadOnlyList<TensorComponentOption> All { get; } = new[]
    {
        new TensorComponentOption("Equivalent (von Mises)", TensorComponent.VonMises),
        new TensorComponentOption("Max principal", TensorComponent.MaxPrincipal),
        new TensorComponentOption("Mid principal", TensorComponent.MidPrincipal),
        new TensorComponentOption("Min principal", TensorComponent.MinPrincipal),
        new TensorComponentOption("Max shear", TensorComponent.MaxShear),
        new TensorComponentOption("Normal XX", TensorComponent.XX),
        new TensorComponentOption("Normal YY", TensorComponent.YY),
        new TensorComponentOption("Normal ZZ", TensorComponent.ZZ),
        new TensorComponentOption("Shear XY", TensorComponent.XY),
        new TensorComponentOption("Shear YZ", TensorComponent.YZ),
        new TensorComponentOption("Shear ZX", TensorComponent.ZX)
    };
}
