using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Materials;

namespace OpenSim.App.ViewModels;

/// <summary>One layer of the selected layer coating as the panel edits it.</summary>
public sealed partial class OpticsCoatingLayerRow : ObservableObject
{
    [ObservableProperty] private string _material = "";
    [ObservableProperty] private double _thickness = 0.25;
    [ObservableProperty] private bool _isAbsolute;
    [ObservableProperty] private int _loopIndex;
    [ObservableProperty] private string _taper = "";
}

/// <summary>One row of the selected coating material as the panel edits it: µm, n, k (k ≥ 0 absorbs).</summary>
public sealed partial class OpticsCoatingIndexRow : ObservableObject
{
    [ObservableProperty] private double _micrometers = 0.55;
    [ObservableProperty] private double _n = 1.5;
    [ObservableProperty] private double _k;
}

/// <summary>
/// The Optics workspace's coating catalog (OP-15): the coating materials and coatings of the
/// lens, an editor for materials (n, k rows), layer coatings (material, thickness, loop, taper)
/// and ideal coatings (T, R, total internal reflection), and import and export of OpticStudio
/// coating files and this program's JSON catalog. It belongs to <see cref="OpticsViewModel"/>,
/// which saves the catalog with the lens and publishes it with the system, so the coating plots
/// recompute after every edit. IDEAL2 and table coatings and tapers are shown and kept, but are
/// edited in a file and imported: the panel has no editor for them.
/// </summary>
public sealed partial class OpticsCoatingCatalogViewModel : ObservableObject
{
    private readonly Action _changed;
    private CoatingCatalog _catalog = CoatingCatalog.Empty;

    /// <summary>Set while the rows are rebuilt from the catalog, so their changes are not taken
    /// for the user's edits.</summary>
    private bool _rebuilding;

    public OpticsCoatingCatalogViewModel(Action changed) => _changed = changed;

    /// <summary>The catalog as it stands.</summary>
    public CoatingCatalog Catalog => _catalog;

    public ObservableCollection<string> CoatingNames { get; } = new();
    public ObservableCollection<string> MaterialNames { get; } = new();

    /// <summary>The tapers by name, for the panel's list (tapers are imported, not edited).</summary>
    public string TaperSummary => _catalog.Tapers.Count == 0 ? "No tapers." : "Tapers: " + string.Join(", ", _catalog.Tapers.Select(t => t.Name));

    public ObservableCollection<OpticsCoatingLayerRow> Layers { get; } = new();
    public ObservableCollection<OpticsCoatingIndexRow> IndexRows { get; } = new();

    [ObservableProperty] private string? _selectedCoatingName;
    [ObservableProperty] private string? _selectedMaterialName;

    /// <summary>A name for the next coating or material added, or for a rename.</summary>
    [ObservableProperty] private string _newName = "";

    /// <summary>What the last action did.</summary>
    [ObservableProperty] private string _status = "";

    // The selected ideal coating's values.
    [ObservableProperty] private double _idealTransmission;
    [ObservableProperty] private double _idealReflection;
    [ObservableProperty] private double _idealTotalInternalReflection = 1;

    public Coating? SelectedCoating => _catalog.FindCoating(SelectedCoatingName);
    public CoatingMaterial? SelectedMaterial => _catalog.FindMaterial(SelectedMaterialName);

    public bool IsLayerCoatingSelected => SelectedCoating is LayerCoating;
    public bool IsIdealCoatingSelected => SelectedCoating is IdealCoating;
    public bool HasSelectedMaterial => SelectedMaterial is not null;

    /// <summary>The selected coating's kind and, for the kinds the panel does not edit, its data.</summary>
    public string SelectedCoatingSummary => SelectedCoating switch
    {
        null => "",
        LayerCoating l => $"Layer coating, {l.Layers.Count} lines" + (TryExpandedCount(l) is int n && n != l.Layers.Count ? $" ({n} layers with the loops written out)" : "") +
                          ". Thickness in waves of the primary wavelength in the layer, or in µm when absolute.",
        IdealCoating { IsSimple: true } i => $"Simple ideal coating (I.xx): transmits {Number(i.Transmission)}, reflects the rest.",
        IdealCoating => "Ideal coating: intensity T and R, and the intensity reflected under total internal reflection. A = 1 − R − T.",
        Ideal2Coating i => $"IDEAL2 coating: s r = {Complex(i.SReflection)}, t = {Complex(i.STransmission)}; p r = {Complex(i.PReflection)}, t = {Complex(i.PTransmission)}" +
                           (i.NoPiFlag ? "; no π change" : "") + ". Edit it in a coating file and import it.",
        TableCoating t => $"Table coating: {t.Angles.Count} angles × {t.Angles.FirstOrDefault()?.Rows.Count ?? 0} wavelengths. Edit it in a coating file and import it.",
        _ => ""
    };

    /// <summary>Every problem in the catalog, one per line; empty when it can all be used.</summary>
    public string Problems => string.Join("\n", _catalog.Validate());

    private static int? TryExpandedCount(LayerCoating l)
    {
        try { return l.Expand().Count; }
        catch (InvalidOperationException) { return null; }
    }

    // ---------------------------------------------------------------- selection

    /// <summary>Set while the catalog is replaced: the selection is restored by name without
    /// rebuilding the rows.</summary>
    private bool _restoringSelection;

    partial void OnSelectedCoatingNameChanged(string? value)
    {
        if (!_restoringSelection) ShowSelectedCoating();
    }

    partial void OnSelectedMaterialNameChanged(string? value)
    {
        if (!_restoringSelection) ShowSelectedMaterial();
    }

    private void ShowSelectedCoating()
    {
        _rebuilding = true;
        try
        {
            foreach (var row in Layers) row.PropertyChanged -= OnLayerChanged;
            Layers.Clear();
            if (SelectedCoating is LayerCoating l)
                foreach (var layer in l.Layers)
                {
                    var row = new OpticsCoatingLayerRow
                    {
                        Material = layer.Material, Thickness = layer.Thickness, IsAbsolute = layer.IsAbsolute,
                        LoopIndex = layer.LoopIndex, Taper = layer.Taper ?? ""
                    };
                    row.PropertyChanged += OnLayerChanged;
                    Layers.Add(row);
                }
            if (SelectedCoating is IdealCoating i)
            {
                IdealTransmission = i.Transmission;
                IdealReflection = i.Reflection;
                IdealTotalInternalReflection = i.TotalInternalReflection;
            }
        }
        finally
        {
            _rebuilding = false;
        }
        OnPropertyChanged(nameof(SelectedCoating));
        OnPropertyChanged(nameof(IsLayerCoatingSelected));
        OnPropertyChanged(nameof(IsIdealCoatingSelected));
        OnPropertyChanged(nameof(SelectedCoatingSummary));
    }

    private void ShowSelectedMaterial()
    {
        _rebuilding = true;
        try
        {
            foreach (var row in IndexRows) row.PropertyChanged -= OnIndexRowChanged;
            IndexRows.Clear();
            if (SelectedMaterial is { } m)
                foreach (var r in m.Rows)
                {
                    var row = new OpticsCoatingIndexRow { Micrometers = r.Micrometers, N = r.N, K = r.K };
                    row.PropertyChanged += OnIndexRowChanged;
                    IndexRows.Add(row);
                }
        }
        finally
        {
            _rebuilding = false;
        }
        OnPropertyChanged(nameof(SelectedMaterial));
        OnPropertyChanged(nameof(HasSelectedMaterial));
    }

    // ---------------------------------------------------------------- edits

    private void OnLayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_rebuilding) CommitLayers();
    }

    private void OnIndexRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_rebuilding) CommitIndexRows();
    }

    partial void OnIdealTransmissionChanged(double value) => CommitIdeal();
    partial void OnIdealReflectionChanged(double value) => CommitIdeal();
    partial void OnIdealTotalInternalReflectionChanged(double value) => CommitIdeal();

    private void CommitLayers()
    {
        if (SelectedCoating is not LayerCoating l) return;
        var updated = l with
        {
            Layers = Layers.Select(r => new CoatingLayer
            {
                Material = r.Material.Trim(), Thickness = r.Thickness, IsAbsolute = r.IsAbsolute, LoopIndex = r.LoopIndex,
                Taper = string.IsNullOrWhiteSpace(r.Taper) ? null : r.Taper.Trim()
            }).ToArray()
        };
        ReplaceCoating(l, updated);
    }

    private void CommitIdeal()
    {
        if (_rebuilding || SelectedCoating is not IdealCoating i) return;
        ReplaceCoating(i, i with { Transmission = IdealTransmission, Reflection = IdealReflection, TotalInternalReflection = IdealTotalInternalReflection });
    }

    private void CommitIndexRows()
    {
        if (SelectedMaterial is not { } m) return;
        var updated = m with { Rows = IndexRows.Select(r => new TabulatedIndexPoint(r.Micrometers, r.N, r.K)).ToArray() };
        SetCatalog(_catalog with { Materials = _catalog.Materials.Select(x => ReferenceEquals(x, m) ? updated : x).ToArray() }, keepRows: true);
    }

    private void ReplaceCoating(Coating old, Coating updated) =>
        SetCatalog(_catalog with { Coatings = _catalog.Coatings.Select(c => ReferenceEquals(c, old) ? updated : c).ToArray() }, keepRows: true);

    /// <summary>A name not yet used among <paramref name="taken"/>: <see cref="NewName"/> when it
    /// is free, else the stem with a number.</summary>
    private string FreshName(IEnumerable<string> taken, string stem)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        string wanted = NewName.Trim().Replace(' ', '_');
        if (wanted.Length > 0 && !set.Contains(wanted)) return wanted;
        for (int i = 1; ; i++)
            if (!set.Contains($"{stem}{i}")) return $"{stem}{i}";
    }

    [RelayCommand]
    private void AddMaterial()
    {
        string name = FreshName(_catalog.Materials.Select(m => m.Name), "MAT");
        SetCatalog(_catalog with { Materials = _catalog.Materials.Append(CoatingMaterial.Constant(name, 1.5)).ToArray() });
        SelectedMaterialName = name;
        Status = $"Added material '{name}' (n = 1.5, k = 0, a starting value to edit).";
    }

    [RelayCommand]
    private void RemoveMaterial()
    {
        if (SelectedMaterial is not { } m) return;
        SetCatalog(_catalog with { Materials = _catalog.Materials.Where(x => !ReferenceEquals(x, m)).ToArray() });
        Status = $"Removed material '{m.Name}'.";
    }

    [RelayCommand]
    private void AddIndexRow()
    {
        if (SelectedMaterial is not { } m) return;
        var last = m.Rows[^1];
        var updated = m with { Rows = m.Rows.Append(new TabulatedIndexPoint(last.Micrometers + 0.1, last.N, last.K)).ToArray() };
        SetCatalog(_catalog with { Materials = _catalog.Materials.Select(x => ReferenceEquals(x, m) ? updated : x).ToArray() });
    }

    [RelayCommand]
    private void RemoveIndexRow(OpticsCoatingIndexRow? row)
    {
        if (row is null || SelectedMaterial is not { } m || IndexRows.Count <= 1) return;
        int index = IndexRows.IndexOf(row);
        if (index < 0) return;
        var updated = m with { Rows = m.Rows.Where((_, i) => i != index).ToArray() };
        SetCatalog(_catalog with { Materials = _catalog.Materials.Select(x => ReferenceEquals(x, m) ? updated : x).ToArray() });
    }

    [RelayCommand]
    private void AddLayerCoating()
    {
        string name = FreshName(_catalog.Coatings.Select(c => c.Name), "COAT");
        SetCatalog(_catalog with { Coatings = _catalog.Coatings.Append(new LayerCoating { Name = name }).ToArray() });
        SelectedCoatingName = name;
        Status = $"Added layer coating '{name}'. Add its layers, outermost first.";
    }

    [RelayCommand]
    private void AddIdealCoating()
    {
        string name = FreshName(_catalog.Coatings.Select(c => c.Name), "IDEAL");
        SetCatalog(_catalog with { Coatings = _catalog.Coatings.Append(new IdealCoating { Name = name, Transmission = 1 }).ToArray() });
        SelectedCoatingName = name;
        Status = $"Added ideal coating '{name}' (T = 1, R = 0).";
    }

    [RelayCommand]
    private void RemoveCoating()
    {
        if (SelectedCoating is not { } c) return;
        SetCatalog(_catalog with { Coatings = _catalog.Coatings.Where(x => !ReferenceEquals(x, c)).ToArray() });
        Status = $"Removed coating '{c.Name}'.";
    }

    /// <summary>Renames the selected coating to <see cref="NewName"/>.</summary>
    [RelayCommand]
    private void RenameCoating()
    {
        if (SelectedCoating is not { } c) return;
        string name = NewName.Trim().Replace(' ', '_');
        if (name.Length == 0 || _catalog.FindCoating(name) is not null)
        {
            Status = name.Length == 0 ? "Type the new name first." : $"A coating named '{name}' already exists.";
            return;
        }
        ReplaceCoating(c, c with { Name = name });
        SelectedCoatingName = name;
        Status = $"Renamed coating '{c.Name}' to '{name}'.";
    }

    /// <summary>Renames the selected material to <see cref="NewName"/>, and every layer that uses it.</summary>
    [RelayCommand]
    private void RenameMaterial()
    {
        if (SelectedMaterial is not { } m) return;
        string name = NewName.Trim().Replace(' ', '_');
        if (name.Length == 0 || _catalog.FindMaterial(name) is not null)
        {
            Status = name.Length == 0 ? "Type the new name first." : $"A material named '{name}' already exists.";
            return;
        }
        var coatings = _catalog.Coatings.Select(c => c is LayerCoating l
            ? l with { Layers = l.Layers.Select(x => string.Equals(x.Material, m.Name, StringComparison.OrdinalIgnoreCase) ? x with { Material = name } : x).ToArray() }
            : c).ToArray();
        SetCatalog(_catalog with
        {
            Materials = _catalog.Materials.Select(x => ReferenceEquals(x, m) ? m with { Name = name } : x).ToArray(),
            Coatings = coatings
        });
        SelectedMaterialName = name;
        Status = $"Renamed material '{m.Name}' to '{name}' in the catalog and its coatings.";
    }

    [RelayCommand]
    private void AddLayer()
    {
        if (SelectedCoating is not LayerCoating l) return;
        string material = SelectedMaterialName ?? _catalog.Materials.FirstOrDefault()?.Name ?? "";
        ReplaceCoating(l, l with { Layers = l.Layers.Append(new CoatingLayer { Material = material, Thickness = 0.25 }).ToArray() });
        ShowSelectedCoating();
    }

    [RelayCommand]
    private void RemoveLayer(OpticsCoatingLayerRow? row)
    {
        if (row is null || SelectedCoating is not LayerCoating l) return;
        int index = Layers.IndexOf(row);
        if (index < 0) return;
        ReplaceCoating(l, l with { Layers = l.Layers.Where((_, i) => i != index).ToArray() });
        ShowSelectedCoating();
    }

    // ---------------------------------------------------------------- files

    /// <summary>Replaces the catalog with a file's: an OpticStudio coating file, or this
    /// program's JSON catalog (by the extension .json). Returns the status line; a file that does
    /// not read is named there and nothing changes.</summary>
    internal string ImportFile(string path)
    {
        try
        {
            var notes = new List<string>();
            string text = File.ReadAllText(path);
            string source = Path.GetFileName(path);
            var catalog = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? CoatingFile.FromJson(text, notes, source)
                : CoatingFile.ReadDat(text, notes, source);
            SetCatalog(catalog);
            Status = $"Read {source}: {catalog.Materials.Count} materials, {catalog.Tapers.Count} tapers, {catalog.Coatings.Count} coatings; it replaces the catalog." +
                     (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = $"Coating catalog not read: {e.Message}";
        }
        return Status;
    }

    /// <summary>Writes the catalog: an OpticStudio coating file for .dat, else the JSON catalog.</summary>
    internal string ExportFile(string path)
    {
        try
        {
            File.WriteAllText(path, path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                ? CoatingFile.WriteDat(_catalog)
                : CoatingFile.ToJson(_catalog));
            Status = $"Wrote {Path.GetFileName(path)}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Status = $"Coating catalog not written: {e.Message}";
        }
        return Status;
    }

    private const string FileFilter = "OpticStudio coating file (*.dat)|*.dat|OpenSim coating catalog (*.oscoat.json)|*.json|All files (*.*)|*.*";

    [RelayCommand]
    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = FileFilter, Title = "Read a coating catalog" };
        if (dialog.ShowDialog() == true) ImportFile(dialog.FileName);
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = FileFilter, Title = "Write the coating catalog", FileName = "COATINGS.DAT" };
        if (dialog.ShowDialog() == true) ExportFile(dialog.FileName);
    }

    // ---------------------------------------------------------------- the catalog

    /// <summary>Replaces the catalog, refreshes the lists and publishes. With
    /// <paramref name="keepRows"/> the rows being edited are left as they are.</summary>
    private void SetCatalog(CoatingCatalog catalog, bool keepRows = false, bool publish = true)
    {
        _catalog = catalog;
        string? coating = SelectedCoatingName, material = SelectedMaterialName;
        Sync(CoatingNames, catalog.Coatings.Select(c => c.Name));
        Sync(MaterialNames, catalog.Materials.Select(m => m.Name));
        // The lists' selection may have been cleared by the refresh: restore it by name.
        _restoringSelection = true;
        try
        {
            SelectedCoatingName = catalog.FindCoating(coating)?.Name ?? catalog.Coatings.FirstOrDefault()?.Name;
            SelectedMaterialName = catalog.FindMaterial(material)?.Name ?? catalog.Materials.FirstOrDefault()?.Name;
        }
        finally
        {
            _restoringSelection = false;
        }
        if (!keepRows)
        {
            ShowSelectedCoating();
            ShowSelectedMaterial();
        }
        else
        {
            OnPropertyChanged(nameof(SelectedCoating));
            OnPropertyChanged(nameof(SelectedMaterial));
            OnPropertyChanged(nameof(SelectedCoatingSummary));
        }
        OnPropertyChanged(nameof(Catalog));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(TaperSummary));
        if (publish) _changed();
    }

    private static void Sync(ObservableCollection<string> target, IEnumerable<string> names)
    {
        var list = names.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var n in list) target.Add(n);
    }

    /// <summary>The saved form; null for an empty catalog.</summary>
    public OpticsCoatingsSettings? ToSettings() => CoatingCatalogSettings.ToSettings(_catalog);

    /// <summary>Restores the catalog from a file; null (a file from before OP-15) is an empty
    /// catalog. Coatings of an unknown kind are left out and noted.</summary>
    public void LoadSettings(OpticsCoatingsSettings? saved, ICollection<string> notes) =>
        SetCatalog(CoatingCatalogSettings.FromSettings(saved, notes), publish: false);

    /// <summary>A new lens: an empty catalog.</summary>
    public void Reset() => SetCatalog(CoatingCatalog.Empty, publish: false);

    private static string Number(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
    private static string Complex(System.Numerics.Complex z) => $"{Number(z.Real)} {(z.Imaginary < 0 ? "−" : "+")} {Number(Math.Abs(z.Imaginary))}i";
}
