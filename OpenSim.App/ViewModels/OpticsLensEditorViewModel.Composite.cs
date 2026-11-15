using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Composite;

namespace OpenSim.App.ViewModels;

// OP-32: composite add-ons of the current surface (docs/Optics-Conventions.md §58). OpticStudio
// keeps each add-on as a row above its base with "Composite Surface: Add sag to next surface"
// checked (help, "Composite Surface"); this editor lists them on the base surface instead, with
// the help's Composite page fields (decenter x, y; tilt x, y, z) and the toolbar's Activate /
// Ignore Composite Add-ons. Every change is one undo step through Apply.
public sealed partial class OpticsLensEditorViewModel
{
    /// <summary>The types an add-on may be: the help's supported list, limited to the types the
    /// saved lens can hold (<see cref="CompositeSurfaces.SupportedTypes"/>).</summary>
    public IReadOnlyList<string> AddOnTypes { get; } = CompositeSurfaces.SupportedTypes
        .Where(t => t == SavedSurfaces.StandardType || SurfaceTypes.Find(t) is not null).ToArray();

    /// <summary>One line per add-on of the current surface.</summary>
    [ObservableProperty] private IReadOnlyList<string> _addOnList = Array.Empty<string>();

    /// <summary>The add-on the fields show (0-based), or −1 for none.</summary>
    [ObservableProperty] private int _selectedAddOn = -1;

    // The selected add-on's fields, in lens units and degrees.
    [ObservableProperty] private string _addOnType = SavedSurfaces.StandardType;
    [ObservableProperty] private string _addOnRadius = "";
    [ObservableProperty] private double _addOnConic;
    [ObservableProperty] private string _addOnParameters = "";
    [ObservableProperty] private double _addOnDecenterX;
    [ObservableProperty] private double _addOnDecenterY;
    [ObservableProperty] private double _addOnTiltX;
    [ObservableProperty] private double _addOnTiltY;
    [ObservableProperty] private double _addOnTiltZ;
    [ObservableProperty] private bool _addOnIgnored;
    [ObservableProperty] private string _addOnComment = "";

    /// <summary>The add-ons as the row's property text: JSON, empty for none.</summary>
    private static string FormatAddOns(List<OpticsCompositeAddOnSettings>? addOns) =>
        addOns is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(addOns) : "";

    /// <summary>The row's add-ons from its property: the base's own list while the text is
    /// unchanged (or the row type has no such property), else the text read back.</summary>
    private static List<OpticsCompositeAddOnSettings>? ReadAddOns(Controls.Spreadsheet.SheetRow row, List<OpticsCompositeAddOnSettings>? known)
    {
        if (!row.Properties.TryGetValue(OpticsLensEditorSchema.CompositeAddOnsProperty, out var value)) return known;
        string text = value.Text ?? "";
        if (text == FormatAddOns(known)) return known;
        if (text.Trim().Length == 0) return null;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<OpticsCompositeAddOnSettings>>(text);
            return list is { Count: > 0 } ? list : null;
        }
        catch (System.Text.Json.JsonException) { return known; }
    }

    partial void OnCurrentRowChanged(int value) => RefreshCompositeAddOns();

    partial void OnSelectedAddOnChanged(int value) => LoadAddOnFields();

    private List<OpticsCompositeAddOnSettings> CurrentAddOns()
    {
        int row = CurrentRow;
        if (row < 0 || row >= Document.RowCount) return new();
        return ToSettings()[row].AddOns?.Select(a => a ?? new OpticsCompositeAddOnSettings()).ToList() ?? new();
    }

    /// <summary>Re-reads the current surface's add-ons into the list and the fields.</summary>
    private void RefreshCompositeAddOns()
    {
        var list = CurrentAddOns();
        AddOnList = list.Select((a, k) => Summary(a, k)).ToArray();
        int selected = list.Count == 0 ? -1 : Math.Clamp(SelectedAddOn < 0 ? 0 : SelectedAddOn, 0, list.Count - 1);
        if (selected == SelectedAddOn) LoadAddOnFields();
        else SelectedAddOn = selected;
    }

    private static string Summary(OpticsCompositeAddOnSettings a, int k)
    {
        var inv = CultureInfo.InvariantCulture;
        string place = a.DecenterX != 0 || a.DecenterY != 0 || a.TiltX != 0 || a.TiltY != 0 || a.TiltZ != 0
            ? string.Create(inv, $", decenter ({a.DecenterX:G6}, {a.DecenterY:G6}), tilt ({a.TiltX:G6}°, {a.TiltY:G6}°, {a.TiltZ:G6}°)")
            : "";
        return $"{k + 1}: {a.Type ?? SavedSurfaces.StandardType}{place}{(a.IsIgnored ? ", ignored" : "")}{(string.IsNullOrEmpty(a.Comment) ? "" : " — " + a.Comment)}";
    }

    private void LoadAddOnFields()
    {
        var list = CurrentAddOns();
        var a = SelectedAddOn >= 0 && SelectedAddOn < list.Count ? list[SelectedAddOn] : new OpticsCompositeAddOnSettings();
        var inv = CultureInfo.InvariantCulture;
        AddOnType = a.Type ?? SavedSurfaces.StandardType;
        AddOnRadius = a.Radius is double r ? r.ToString("R", inv) : "";
        AddOnConic = a.Conic;
        AddOnParameters = string.Join("; ", (a.Parameters ?? new()).Concat(a.ExtraData ?? new())
            .OrderBy(p => p.Number).Select(p => string.Create(inv, $"{p.Number}={p.Value:R}")));
        AddOnDecenterX = a.DecenterX;
        AddOnDecenterY = a.DecenterY;
        AddOnTiltX = a.TiltX;
        AddOnTiltY = a.TiltY;
        AddOnTiltZ = a.TiltZ;
        AddOnIgnored = a.IsIgnored;
        AddOnComment = a.Comment ?? "";
    }

    /// <summary>Replaces the current surface's add-ons as one undo step.</summary>
    private bool ApplyAddOns(List<OpticsCompositeAddOnSettings> list, string description, string done)
    {
        int row = CurrentRow;
        if (row <= 0 || row >= Document.RowCount)
        {
            Status = $"{description}: pick a surface after the object (the object surface cannot have composite add-ons).";
            return false;
        }
        var surfaces = ToSettings();
        surfaces[row] = surfaces[row] with { AddOns = list.Count == 0 ? null : list };
        if (Apply(surfaces, description) is { } refusal)
        {
            Status = refusal;
            return false;
        }
        Status = done;
        return true;
    }

    [RelayCommand]
    private void AddCompositeAddOn()
    {
        var list = CurrentAddOns();
        list.Add(new OpticsCompositeAddOnSettings());
        if (ApplyAddOns(list, "Add composite add-on", $"Composite add-on {list.Count} added to surface {CurrentRow} (a Standard plane: it adds nothing until it is given a shape).")) SelectedAddOn = list.Count - 1;
    }

    [RelayCommand]
    private void RemoveCompositeAddOn()
    {
        var list = CurrentAddOns();
        int k = SelectedAddOn;
        if (k < 0 || k >= list.Count)
        {
            Status = "Remove composite add-on: the current surface has no add-on selected.";
            return;
        }
        list.RemoveAt(k);
        ApplyAddOns(list, "Remove composite add-on", $"Composite add-on {k + 1} removed from surface {CurrentRow}.");
    }

    /// <summary>Writes the fields into the selected add-on (its grid and own semi-diameter are
    /// kept while its type is unchanged).</summary>
    [RelayCommand]
    private void ApplyCompositeAddOn()
    {
        var list = CurrentAddOns();
        int k = SelectedAddOn;
        if (k < 0 || k >= list.Count)
        {
            Status = "Apply composite add-on: the current surface has no add-on selected; add one first.";
            return;
        }
        if (!TryParseAddOnFields(list[k], out var edited, out string? error))
        {
            Status = $"Apply composite add-on: {error}";
            return;
        }
        list[k] = edited;
        ApplyAddOns(list, "Edit composite add-on", $"Composite add-on {k + 1} of surface {CurrentRow} updated.");
    }

    private bool TryParseAddOnFields(OpticsCompositeAddOnSettings old, out OpticsCompositeAddOnSettings edited, out string? error)
    {
        edited = old;
        error = null;
        var inv = CultureInfo.InvariantCulture;
        string type = string.IsNullOrWhiteSpace(AddOnType) ? SavedSurfaces.StandardType : AddOnType.Trim();
        double? radius = null;
        string rt = AddOnRadius.Trim();
        if (rt.Length > 0 && !rt.Equals("Infinity", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(rt, NumberStyles.Float, inv, out double r) || !double.IsFinite(r) || r == 0)
            {
                error = $"the radius '{AddOnRadius}' is not a finite, non-zero number (leave it empty for a plane).";
                return false;
            }
            radius = r;
        }
        var parameters = new SortedDictionary<int, double>();
        foreach (var item in AddOnParameters.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, inv, out int n) || n < 0
                || !double.TryParse(parts[1], NumberStyles.Float, inv, out double v) || !double.IsFinite(v))
            {
                error = $"'{item}' is not a parameter as number=value (e.g. 2=1e-5).";
                return false;
            }
            parameters[n] = v;
        }
        bool sameType = string.Equals(old.Type ?? SavedSurfaces.StandardType, type, StringComparison.Ordinal);
        List<OpticsSurfaceParameterSettings>? Part(bool extra)
        {
            var list = parameters.Where(p => p.Value != 0 && (p.Key >= SurfaceTypes.FirstExtraData) == extra)
                .Select(p => new OpticsSurfaceParameterSettings { Number = p.Key, Value = p.Value }).ToList();
            return list.Count == 0 ? null : list;
        }
        edited = old with
        {
            Type = type == SavedSurfaces.StandardType ? null : type,
            Radius = radius,
            Conic = AddOnConic,
            Parameters = Part(extra: false),
            ExtraData = Part(extra: true),
            GridSag = sameType ? old.GridSag : null,
            DecenterX = AddOnDecenterX,
            DecenterY = AddOnDecenterY,
            TiltX = AddOnTiltX,
            TiltY = AddOnTiltY,
            TiltZ = AddOnTiltZ,
            IsIgnored = AddOnIgnored,
            Comment = string.IsNullOrEmpty(AddOnComment) ? null : AddOnComment,
        };
        return true;
    }

    /// <summary>OpticStudio's Activate Composite Add-ons: every add-on of the lens active.</summary>
    [RelayCommand]
    private void ActivateCompositeAddOns() => SetAllAddOnsIgnored(false, "Activate Composite Add-ons");

    /// <summary>OpticStudio's Ignore Composite Add-ons: every add-on of the lens ignored.</summary>
    [RelayCommand]
    private void IgnoreCompositeAddOns() => SetAllAddOnsIgnored(true, "Ignore Composite Add-ons");

    private void SetAllAddOnsIgnored(bool ignore, string name)
    {
        var surfaces = CompositeSurfaces.SetAllIgnored(ToSettings(), ignore, out int changed);
        if (changed == 0)
        {
            Status = $"{name}: no add-on to change.";
            return;
        }
        Status = Apply(surfaces, name) ?? $"{name}: {changed} add-on{(changed == 1 ? "" : "s")} {(ignore ? "ignored" : "activated")}.";
    }
}
