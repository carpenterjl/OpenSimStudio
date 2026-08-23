using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

/// <summary>One entry of the meshing-method picker: the method and the label for it.</summary>
public sealed record MeshMethodOption(MeshMethod Method, string Label);
