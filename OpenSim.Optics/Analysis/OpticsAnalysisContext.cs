using OpenSim.Core.Model;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Analysis;

/// <summary>
/// What an analysis computes from: an immutable snapshot of the optical system taken on the UI
/// thread when the change happened, so a compute on a background thread never sees an edit
/// half made. The system is the project's optics record (<see cref="OpticsSettings"/>); as the
/// system editors arrive they add their data there, and analyses read it from here.
/// </summary>
public sealed record OpticsAnalysisContext(string ProjectName, OpticsSettings System)
{
    /// <summary>The active glass catalogs at the snapshot, in search order (OP-14's material
    /// analyses read them); empty when the caller supplies none. Catalogs are immutable records,
    /// so the list is safe to read on the compute thread.</summary>
    public IReadOnlyList<OpticalCatalog> Catalogs { get; init; } = Array.Empty<OpticalCatalog>();

    /// <summary>The mode the snapshot is in; an unknown saved name reads as the default.</summary>
    public OpticsMode Mode =>
        OpticsModes.TryParseSavedName(System.Mode, out var mode) ? mode : OpticsModes.All[0];

    /// <summary>The first glass of that name (any case) in <see cref="Catalogs"/> (the coating plots name their media this way, OP-15), or null.</summary>
    public OpticalMaterial? FindGlass(string name)
    {
        foreach (var catalog in Catalogs)
            if (catalog.Find(name) is { } glass) return glass;
        return null;
    }
}
