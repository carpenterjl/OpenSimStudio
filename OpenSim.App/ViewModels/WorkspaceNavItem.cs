using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSim.App.ViewModels;

/// <summary>One row of the 56px workspace nav rail (design screens 02–16).</summary>
public sealed partial class WorkspaceNavItem : ObservableObject
{
    public WorkspaceNavItem(WorkspaceKind kind, string shortLabel, string iconKey, string automationId)
    {
        Kind = kind;
        ShortLabel = shortLabel;
        IconKey = iconKey;
        AutomationId = automationId;
    }

    public WorkspaceKind Kind { get; }

    /// <summary>The rail's 9px label ("Struct", "Elec", …).</summary>
    public string ShortLabel { get; }

    /// <summary>Resource key into Themes/Icons.xaml.</summary>
    public string IconKey { get; }

    /// <summary>Stable UIA id for the smokes (click-by-id rule).</summary>
    public string AutomationId { get; }

    /// <summary>The lucide geometry, resolved once from the application dictionary.</summary>
    public System.Windows.Media.Geometry? Icon =>
        Application.Current.TryFindResource(IconKey) as System.Windows.Media.Geometry;

    [ObservableProperty] private bool _isActive;
}
