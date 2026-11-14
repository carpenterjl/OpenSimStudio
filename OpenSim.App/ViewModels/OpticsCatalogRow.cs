using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSim.App.ViewModels;

/// <summary>One glass catalog in the materials panel: its name, size, where it came from, and
/// whether the lens draws glasses from it.</summary>
public partial class OpticsCatalogRow : ObservableObject
{
    public OpticsCatalogRow(string name, int glassCount, string origin)
    {
        Name = name;
        GlassCount = glassCount;
        Origin = origin;
    }

    public string Name { get; }
    public int GlassCount { get; }

    /// <summary>"built in", "user folder", or "not loaded" for a saved active catalog this
    /// machine does not have.</summary>
    public string Origin { get; }

    public string Description => $"{Name} — {GlassCount} glasses, {Origin}";

    [ObservableProperty] private bool _isActive = true;
}
