using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.App.Rendering;
using OpenSim.Pcb.Import;

namespace OpenSim.App.ViewModels;

/// <summary>A net in the selectable list, with its own visibility toggle.</summary>
public partial class NetRow : ObservableObject
{
    public NetRow(CopperNet net) => Net = net;

    public CopperNet Net { get; }
    public string Label => Net.Label;

    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Whether the row appears in the net list at all (false when every layer
    /// the net lives on is disabled and it has no vias — see NetVisibility.IsListed).</summary>
    [ObservableProperty] private bool _isListed = true;

    /// <summary>The "hide all except this net" context-menu toggle; the view model reacts
    /// to changes and applies/restores the other rows' visibility.</summary>
    [ObservableProperty] private bool _isSolo;
}

/// <summary>A copper layer: visibility toggle for filtering, and its copper thickness.</summary>
public partial class LayerFilter : ObservableObject
{
    public LayerFilter(int layerOrder, string? name = null)
    {
        LayerOrder = layerOrder;
        _name = name;
        var swatch = new SolidColorBrush(SceneBuilder.LayerColor(layerOrder));
        swatch.Freeze();
        Swatch = swatch;
    }

    private readonly string? _name;

    public int LayerOrder { get; }

    /// <summary>"L1" for Gerber layers; "L1 · Top Layer" when the format names its layers (IPC-2581).</summary>
    public string Label => _name is null ? $"L{LayerOrder}" : $"L{LayerOrder} · {_name}";

    /// <summary>The layer's preview color, shown as a swatch so the panel and viewport agree.</summary>
    public Brush Swatch { get; }

    [ObservableProperty] private bool _enabled = true;

    /// <summary>Copper thickness for this layer [µm] (edited in the UI). 1 oz ≈ 35 µm.</summary>
    [ObservableProperty] private double _thicknessMicrons = 35.0;

    public double ThicknessMeters => ThicknessMicrons * 1e-6;
}

/// <summary>The dielectric between two adjacent copper layers, with an editable thickness
/// and RF material (εr, tanδ). The thickness sets the true z-separation (and via-barrel
/// height); the material feeds the RF layered-media Green's function. Defaults to FR4.</summary>
public partial class DielectricGap : ObservableObject
{
    public DielectricGap(int upperLayerOrder, double thicknessMicrons,
        double relativePermittivity = 4.4, double lossTangent = 0.02)
    {
        UpperLayerOrder = upperLayerOrder;
        _thicknessMicrons = thicknessMicrons;
        _relativePermittivity = relativePermittivity;
        _lossTangent = lossTangent;
    }

    /// <summary>The upper (smaller-order) copper layer; the gap sits between it and <c>UpperLayerOrder + 1</c>.</summary>
    public int UpperLayerOrder { get; }
    public string Label => $"L{UpperLayerOrder}–L{UpperLayerOrder + 1}";

    [ObservableProperty] private double _thicknessMicrons;

    /// <summary>Relative permittivity εr of this gap's dielectric (FR4 default 4.4). RF only.</summary>
    [ObservableProperty] private double _relativePermittivity;

    /// <summary>Loss tangent tanδ of this gap's dielectric (FR4 default 0.02). RF only.</summary>
    [ObservableProperty] private double _lossTangent;

    public double ThicknessMeters => ThicknessMicrons * 1e-6;
}
