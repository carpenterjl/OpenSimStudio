using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSim.App.Analyses;

/// <summary>Where a docked window sits.</summary>
public enum DockZone
{
    /// <summary>Beside the viewport, on its right; panes stack top to bottom.</summary>
    Right = 0,

    /// <summary>Under the viewport; panes stand side by side.</summary>
    Bottom = 1,

    /// <summary>In a window of its own.</summary>
    Float = 2,
}

/// <summary>A window's place in a <see cref="DockLayout{T}"/>: its zone, its pane (tab group)
/// in that zone counted from 0, and whether it is the tab on show. A floating window's pane is 0.</summary>
public sealed record DockPlacement(DockZone Zone, int Pane, bool Selected);

/// <summary>One tab group: windows shown one at a time, the selected one on top.</summary>
public sealed partial class DockPane<T> : ObservableObject where T : class
{
    public DockPane(DockZone zone) => Zone = zone;

    public DockZone Zone { get; }

    public ObservableCollection<T> Items { get; } = new();

    [ObservableProperty] private T? _selected;
}

/// <summary>
/// The arrangement of the analysis windows around the viewport: a right zone and a bottom zone,
/// each a row of panes, each pane a group of tabs, and the floating windows. First-party docking
/// (the plan's rule 3 rules out a docking library), kept free of any UI type so that every move
/// and the saved form are tested without a window. The view binds to the collections; the
/// operations here are the only way windows move, and each keeps the layout tidy: a pane left
/// empty disappears, and a pane always shows one of its tabs.
/// </summary>
public sealed class DockLayout<T> where T : class
{
    public ObservableCollection<DockPane<T>> Right { get; } = new();
    public ObservableCollection<DockPane<T>> Bottom { get; } = new();
    public ObservableCollection<T> Floating { get; } = new();

    /// <summary>Raised after any window moves, opens or closes.</summary>
    public event EventHandler? Changed;

    /// <summary>Every window in saved order: the right zone's panes, then the bottom's, tab by
    /// tab, then the floating windows.</summary>
    public IEnumerable<T> Items =>
        Right.SelectMany(p => p.Items).Concat(Bottom.SelectMany(p => p.Items)).Concat(Floating);

    public bool Contains(T item) => Find(item) is not null;

    /// <summary>Where <paramref name="item"/> is, or null when it is not in the layout.</summary>
    public DockPlacement? Find(T item)
    {
        foreach (var (zone, panes) in Zones())
            for (int p = 0; p < panes.Count; p++)
                if (panes[p].Items.Contains(item))
                    return new DockPlacement(zone, p, ReferenceEquals(panes[p].Selected, item));
        return Floating.Contains(item) ? new DockPlacement(DockZone.Float, 0, true) : null;
    }

    /// <summary>The pane holding <paramref name="item"/>; null for a floating or absent window.</summary>
    public DockPane<T>? PaneOf(T item) =>
        Right.Concat(Bottom).FirstOrDefault(p => p.Items.Contains(item));

    /// <summary>Puts <paramref name="item"/> (new, or moved from wherever it is) into
    /// <paramref name="zone"/>: as a tab of the zone's last pane, or with
    /// <paramref name="newPane"/> in a pane of its own after the others (side by side in the
    /// bottom zone, one above the other in the right). It becomes the tab on show.</summary>
    public void Dock(T item, DockZone zone, bool newPane = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        Detach(item);
        if (zone == DockZone.Float)
            Floating.Add(item);
        else
        {
            var panes = PanesOf(zone);
            if (newPane || panes.Count == 0) panes.Add(new DockPane<T>(zone));
            var pane = panes[^1];
            pane.Items.Add(item);
            pane.Selected = item;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes <paramref name="item"/> a tab of <paramref name="pane"/>, on show.</summary>
    public void TabWith(T item, DockPane<T> pane)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(pane);
        if (!PanesOf(pane.Zone).Contains(pane))
            throw new ArgumentException("The pane is not part of this layout.", nameof(pane));
        if (pane.Items.Contains(item))
        {
            pane.Selected = item;
            return;
        }
        Detach(item);
        pane.Items.Add(item);
        pane.Selected = item;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes <paramref name="item"/> out of the layout. False when it was not in it.</summary>
    public bool Remove(T item)
    {
        if (!Detach(item)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Empties the layout.</summary>
    public void Clear()
    {
        Right.Clear();
        Bottom.Clear();
        Floating.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every window with its place, in <see cref="Items"/> order: the saved form.</summary>
    public IReadOnlyList<(T Item, DockPlacement Placement)> Placements() =>
        Items.Select(i => (i, Find(i)!)).ToList();

    /// <summary>
    /// Rebuilds the layout from saved placements, replacing what is there. Pane numbers need
    /// not be contiguous (a file may have lost a window whose analysis this build lacks): panes
    /// keep their saved order and empty ones are not made. A pane with no tab marked selected,
    /// or several, shows its first marked or else its first.
    /// </summary>
    public void Restore(IEnumerable<(T Item, DockPlacement Placement)> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        Right.Clear();
        Bottom.Clear();
        Floating.Clear();
        var list = placements.ToList();
        foreach (var zone in new[] { DockZone.Right, DockZone.Bottom })
        {
            var panes = PanesOf(zone);
            foreach (var group in list.Where(p => p.Placement.Zone == zone)
                         .GroupBy(p => p.Placement.Pane).OrderBy(g => g.Key))
            {
                var pane = new DockPane<T>(zone);
                foreach (var (item, _) in group)
                    if (!pane.Items.Contains(item)) pane.Items.Add(item);
                pane.Selected = group.FirstOrDefault(p => p.Placement.Selected).Item ?? pane.Items[0];
                panes.Add(pane);
            }
        }
        foreach (var (item, placement) in list)
            if (placement.Zone == DockZone.Float && !Floating.Contains(item)) Floating.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private ObservableCollection<DockPane<T>> PanesOf(DockZone zone) => zone switch
    {
        DockZone.Right => Right,
        DockZone.Bottom => Bottom,
        _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, "A floating window has no pane.")
    };

    private IEnumerable<(DockZone Zone, ObservableCollection<DockPane<T>> Panes)> Zones()
    {
        yield return (DockZone.Right, Right);
        yield return (DockZone.Bottom, Bottom);
    }

    /// <summary>Takes the item out of wherever it is, without raising <see cref="Changed"/>;
    /// drops a pane it leaves empty and moves the pane's selection to a neighbour.</summary>
    private bool Detach(T item)
    {
        if (Floating.Remove(item)) return true;
        foreach (var (_, panes) in Zones())
            foreach (var pane in panes)
            {
                int index = pane.Items.IndexOf(item);
                if (index < 0) continue;
                bool wasSelected = ReferenceEquals(pane.Selected, item);
                pane.Items.RemoveAt(index);
                if (pane.Items.Count == 0)
                    panes.Remove(pane);
                else if (wasSelected || pane.Selected is null)
                    pane.Selected = pane.Items[Math.Min(index, pane.Items.Count - 1)];
                return true;
            }
        return false;
    }
}
