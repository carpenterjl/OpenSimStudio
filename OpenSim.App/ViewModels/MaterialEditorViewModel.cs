using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;

namespace OpenSim.App.ViewModels;

/// <summary>
/// View model for the material editor dialog. All parsing/validation logic lives here
/// (testable without WPF); the window is just bindings. Numeric fields are string-backed
/// so partial input never throws — empty means "not set" for the nullable properties.
/// </summary>
public partial class MaterialEditorViewModel : ObservableObject
{
    private readonly MaterialLibrary _library;

    /// <summary>Raised after the library changed (save/delete) so the owner can resync.</summary>
    public event EventHandler? LibraryChanged;

    public MaterialEditorViewModel(MaterialLibrary library)
    {
        _library = library;
        Materials = new ObservableCollection<Material>(library.Materials);
        SelectedMaterial = Materials.FirstOrDefault();
    }

    public ObservableCollection<Material> Materials { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private Material? _selectedMaterial;

    // ---------------- Edit fields (string-backed) ----------------

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _youngsModulus = "";
    [ObservableProperty] private string _poissonRatio = "";
    [ObservableProperty] private string _density = "";
    [ObservableProperty] private string _thermalConductivity = "";
    [ObservableProperty] private string _specificHeat = "";
    [ObservableProperty] private string _electricalConductivity = "";
    [ObservableProperty] private string _relativePermittivity = "";
    [ObservableProperty] private string _color = "#B0B0B0";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _selectedIsBuiltIn;

    partial void OnSelectedMaterialChanged(Material? value)
    {
        ErrorText = "";
        SelectedIsBuiltIn = value?.IsBuiltIn ?? false;
        if (value is null) return;
        Name = value.Name;
        YoungsModulus = Format(value.YoungsModulus);
        PoissonRatio = Format(value.PoissonRatio);
        Density = Format(value.Density);
        ThermalConductivity = Format(value.ThermalConductivity);
        SpecificHeat = Format(value.SpecificHeat);
        ElectricalConductivity = Format(value.ElectricalConductivity);
        RelativePermittivity = Format(value.RelativePermittivity);
        Color = value.Color;
    }

    // ---------------- Commands ----------------

    /// <summary>Prefills the fields as a copy of the selection, ready to save under a new name.</summary>
    [RelayCommand]
    private void NewFromSelected()
    {
        if (SelectedMaterial is null) return;
        Name = $"{SelectedMaterial.Name} (copy)";
        SelectedIsBuiltIn = false;
        ErrorText = "";
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var material = new Material
            {
                Name = Name.Trim(),
                YoungsModulus = ParseRequired(YoungsModulus, "Young's modulus"),
                PoissonRatio = ParseRequired(PoissonRatio, "Poisson's ratio"),
                Density = ParseRequired(Density, "density"),
                ThermalConductivity = ParseOptional(ThermalConductivity, "thermal conductivity"),
                SpecificHeat = ParseOptional(SpecificHeat, "specific heat"),
                ElectricalConductivity = ParseOptional(ElectricalConductivity, "electrical conductivity"),
                RelativePermittivity = ParseOptional(RelativePermittivity, "relative permittivity"),
                Color = string.IsNullOrWhiteSpace(Color) ? "#B0B0B0" : Color.Trim()
            };
            material.ValidateMechanical();
            if (material.ThermalConductivity is not null) material.ValidateThermal();
            if (material.ElectricalConductivity is not null) material.ValidateElectrical();

            _library.AddOrUpdateUserMaterial(material);
            RefreshFromLibrary(material.Name);
            ErrorText = "";
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            ErrorText = e.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedMaterial is null) return;
        try
        {
            string name = SelectedMaterial.Name;
            _library.RemoveUserMaterial(name);
            // If a built-in was shadowed it reappears under the same name; select it.
            RefreshFromLibrary(name);
            ErrorText = "";
        }
        catch (InvalidOperationException e)
        {
            ErrorText = e.Message;
        }
    }

    private bool CanDelete() => SelectedMaterial is { IsBuiltIn: false };

    private void RefreshFromLibrary(string? selectName)
    {
        Materials.Clear();
        foreach (var material in _library.Materials)
            Materials.Add(material);
        SelectedMaterial = Materials.FirstOrDefault(m => m.Name == selectName) ?? Materials.FirstOrDefault();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- Parsing helpers ----------------

    private static string Format(double? value) =>
        value?.ToString("g6", CultureInfo.InvariantCulture) ?? "";

    private static double ParseRequired(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException($"{what} is required.");
        return Parse(text, what);
    }

    private static double? ParseOptional(string text, string what) =>
        string.IsNullOrWhiteSpace(text) ? null : Parse(text, what);

    private static double Parse(string text, string what)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new FormatException($"{what}: '{text}' is not a number (use e.g. 2.1e11).");
        return value;
    }
}
