using System.Collections.ObjectModel;
using System.IO;
using OpenSim.Rf.Si;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The copper-roughness entry shared by the impedance calculator and the SI panel: a model
/// name and its parameters, turned into a <see cref="SurfaceRoughness"/>. A rough model is used
/// only with the numbers the user enters (from the laminate or foil data sheet), or with a foil
/// picked by name from the <see cref="FoilCatalog"/> — whose entries each name the vendor
/// document they were read from — converted by the Cannonball stack. There are no hidden
/// defaults.
/// </summary>
public static class RoughnessChoice
{
    public const string Smooth = "Smooth copper";
    public const string Hammerstad = "Hammerstad (RMS)";
    public const string Huray = "Huray (sphere radius, ratio)";
    public const string Foil = "Named foil (data-sheet Rz → Huray)";

    public static ObservableCollection<string> Models() => new() { Smooth, Hammerstad, Huray, Foil };

    /// <summary>The built-in catalog plus any foils file loaded this session.</summary>
    public static FoilCatalog Catalog { get; private set; } = new();

    /// <summary>The catalog's full names, for the pickers (one shared list, so a file loaded in
    /// the SI panel shows in the calculator too).</summary>
    public static ObservableCollection<string> FoilNames { get; } = new(Catalog.Names);

    /// <summary>Merge a user's foils file (a JSON array of foils) into the catalog; an entry with
    /// a built-in foil's vendor and name replaces it. Returns a line for the status text.</summary>
    public static string LoadFoilFile(string path)
    {
        var added = FoilCatalog.ReadJson(path);
        Catalog = Catalog.Merge(added);
        FoilNames.Clear();
        foreach (var name in Catalog.Names) FoilNames.Add(name);
        return $"{added.Count} foil(s) read from {Path.GetFileName(path)}; the list has {Catalog.Foils.Count}.";
    }

    /// <summary>Null for smooth copper. <paramref name="sizeUm"/> is the RMS roughness for
    /// Hammerstad and the sphere radius for Huray; <paramref name="surfaceRatio"/> is Huray's
    /// sphere area per flat area; <paramref name="foil"/> and <paramref name="copperThicknessUm"/>
    /// pick the named foil's gauge.</summary>
    public static SurfaceRoughness? Build(string? model, double sizeUm, double surfaceRatio,
        string? foil = null, double copperThicknessUm = 0) => model switch
    {
        Hammerstad => sizeUm > 0
            ? SurfaceRoughness.Hammerstad(sizeUm * 1e-6)
            : throw new ArgumentException("Hammerstad roughness needs the RMS roughness in µm (from the foil's data sheet)."),
        Huray => sizeUm > 0 && surfaceRatio > 0
            ? SurfaceRoughness.Huray(sizeUm * 1e-6, surfaceRatio)
            : throw new ArgumentException("Huray roughness needs the sphere radius in µm and the surface ratio (from the foil's data sheet)."),
        Foil => foil is { Length: > 0 }
            ? Catalog.Require(foil).Roughness(copperThicknessUm * 1e-6)
            : throw new ArgumentException("Pick a foil from the list (or load a foils file with your own)."),
        _ => null
    };

    /// <summary>The board model with the chosen roughness.</summary>
    public static RlgcModel Model(string? model, double sizeUm, double surfaceRatio,
        string? foil = null, double copperThicknessUm = 0) =>
        Build(model, sizeUm, surfaceRatio, foil, copperThicknessUm) is { } roughness
            ? RlgcModel.Board with { Roughness = roughness }
            : RlgcModel.Board;
}
