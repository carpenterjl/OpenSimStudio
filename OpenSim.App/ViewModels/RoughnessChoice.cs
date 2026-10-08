using System.Collections.ObjectModel;
using OpenSim.Rf.Si;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The copper-roughness entry shared by the impedance calculator and the SI panel: a model
/// name and its parameters, turned into a <see cref="SurfaceRoughness"/>. There are no
/// default foil values: a rough model is used only with the numbers the user enters (from the
/// laminate or foil data sheet).
/// </summary>
public static class RoughnessChoice
{
    public const string Smooth = "Smooth copper";
    public const string Hammerstad = "Hammerstad (RMS)";
    public const string Huray = "Huray (sphere radius, ratio)";

    public static ObservableCollection<string> Models() => new() { Smooth, Hammerstad, Huray };

    /// <summary>Null for smooth copper. <paramref name="sizeUm"/> is the RMS roughness for
    /// Hammerstad and the sphere radius for Huray; <paramref name="surfaceRatio"/> is Huray's
    /// sphere area per flat area.</summary>
    public static SurfaceRoughness? Build(string? model, double sizeUm, double surfaceRatio) => model switch
    {
        Hammerstad => sizeUm > 0
            ? SurfaceRoughness.Hammerstad(sizeUm * 1e-6)
            : throw new ArgumentException("Hammerstad roughness needs the RMS roughness in µm (from the foil's data sheet)."),
        Huray => sizeUm > 0 && surfaceRatio > 0
            ? SurfaceRoughness.Huray(sizeUm * 1e-6, surfaceRatio)
            : throw new ArgumentException("Huray roughness needs the sphere radius in µm and the surface ratio (from the foil's data sheet)."),
        _ => null
    };

    /// <summary>The board model with the chosen roughness.</summary>
    public static RlgcModel Model(string? model, double sizeUm, double surfaceRatio) =>
        Build(model, sizeUm, surfaceRatio) is { } roughness
            ? RlgcModel.Board with { Roughness = roughness }
            : RlgcModel.Board;
}
