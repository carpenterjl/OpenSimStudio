using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSim.App.ViewModels;

/// <summary>
/// One tickable row of the scope panel: a geometric edge or vertex of the active body,
/// with the label the user identifies it by.
/// </summary>
public partial class ScopeItem : ObservableObject
{
    public ScopeItem(int id, string label)
    {
        Id = id;
        Label = label;
    }

    /// <summary>The geometric edge or vertex id this row selects.</summary>
    public int Id { get; }

    /// <summary>What the row reads, e.g. "Edge 3 — faces 2 and 0, 20.0 mm".</summary>
    public string Label { get; }

    [ObservableProperty] private bool _isSelected;
}
