using System.IO;
using System.Text.Json;
using OpenSim.Core.Model;

namespace OpenSim.Core.Persistence;

/// <summary>
/// The material library: built-in engineering materials overlaid by an offline-editable
/// user file. User materials ADD to the built-ins, or OVERRIDE one by name; deleting an
/// override restores the built-in on the next load. The user file lives at
/// <c>%AppData%/OpenSimStudio/materials.json</c> (a legacy exe-adjacent file is still
/// honored with a deprecation warning). Load problems never fail startup, but they are
/// never silent either — <see cref="LoadWarnings"/> is surfaced to the log panel.
/// </summary>
public sealed class MaterialLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _userDirectory;
    private readonly string _legacyDirectory;
    private readonly List<Material> _materials = new();
    private readonly List<string> _loadWarnings = new();

    public IReadOnlyList<Material> Materials => _materials;

    /// <summary>Problems found while loading the user file — log these at startup.</summary>
    public IReadOnlyList<string> LoadWarnings => _loadWarnings;

    public MaterialLibrary() : this(DefaultUserDirectory, AppContext.BaseDirectory) { }

    /// <summary>Test seam: a library rooted at private directories.</summary>
    internal MaterialLibrary(string userDirectory, string? legacyDirectory = null)
    {
        _userDirectory = userDirectory;
        _legacyDirectory = legacyDirectory ?? userDirectory;
        _materials.AddRange(BuiltIn());
        LoadUserMaterials();
    }

    private static string DefaultUserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSimStudio");

    private string UserFile => Path.Combine(_userDirectory, "materials.json");

    private void LoadUserMaterials()
    {
        string path = UserFile;
        if (!File.Exists(path))
        {
            // Legacy location (next to the exe) — honored, but nudged toward AppData.
            string legacy = Path.Combine(_legacyDirectory, "materials.json");
            if (!File.Exists(legacy) || legacy == path) return;
            path = legacy;
            _loadWarnings.Add($"materials.json next to the executable is deprecated; " +
                              $"move it to {UserFile}.");
        }

        List<Material>? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<List<Material>>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            _loadWarnings.Add($"User material file '{path}' is malformed and was ignored: {e.Message}");
            return;
        }
        if (loaded is null) return;

        foreach (var material in loaded)
        {
            if (string.IsNullOrWhiteSpace(material.Name))
            {
                _loadWarnings.Add($"User material file '{path}': entry without a name skipped.");
                continue;
            }
            try
            {
                material.ValidateMechanical();
            }
            catch (InvalidOperationException e)
            {
                _loadWarnings.Add($"User material '{material.Name}' skipped: {e.Message}");
                continue;
            }
            // User entries are user-defined regardless of what the JSON claims.
            Overlay(material with { IsBuiltIn = false });
        }
    }

    /// <summary>Replaces a same-named material or appends.</summary>
    private void Overlay(Material material)
    {
        int existing = _materials.FindIndex(m => m.Name == material.Name);
        if (existing >= 0) _materials[existing] = material;
        else _materials.Add(material);
    }

    /// <summary>Adds or updates a user material and persists the user file immediately.</summary>
    public void AddOrUpdateUserMaterial(Material material)
    {
        if (string.IsNullOrWhiteSpace(material.Name))
            throw new InvalidOperationException("A material needs a non-empty name.");
        material.ValidateMechanical();
        Overlay(material with { IsBuiltIn = false });
        SaveUserMaterials();
    }

    /// <summary>
    /// Removes a user material (persisting immediately). Removing an override of a
    /// built-in name restores the built-in. Refuses built-ins loudly.
    /// </summary>
    public void RemoveUserMaterial(string name)
    {
        int index = _materials.FindIndex(m => m.Name == name);
        if (index < 0)
            throw new InvalidOperationException($"No material named '{name}' exists.");
        if (_materials[index].IsBuiltIn)
            throw new InvalidOperationException(
                $"'{name}' is a built-in material and cannot be deleted. " +
                "User overrides of built-ins can be deleted (restoring the built-in).");

        var builtIn = BuiltIn().FirstOrDefault(m => m.Name == name);
        if (builtIn is not null) _materials[index] = builtIn;
        else _materials.RemoveAt(index);
        SaveUserMaterials();
    }

    /// <summary>Writes only the user-defined materials to the AppData file.</summary>
    public void SaveUserMaterials()
    {
        Directory.CreateDirectory(_userDirectory);
        var user = _materials.Where(m => !m.IsBuiltIn).ToList();
        File.WriteAllText(UserFile, JsonSerializer.Serialize(user, JsonOptions));
    }

    /// <summary>
    /// The built-in database. Values are room-temperature engineering-handbook numbers
    /// (MatWeb / CRC / manufacturer datasheets); electrical conductivities of polymers
    /// and ceramics are nominal insulator values so a mistaken electrical solve on them
    /// fails the σ &gt; 0 sanity of the result rather than silently conducting.
    /// </summary>
    private static List<Material> BuiltIn() => new()
    {
        // ---- Metals ----
        new Material
        {
            Name = "Structural steel", YoungsModulus = 200e9, PoissonRatio = 0.30, Density = 7850,
            ThermalConductivity = 45, SpecificHeat = 480, ElectricalConductivity = 1.45e6,
            Color = "#8C9BAB", IsBuiltIn = true
        },
        new Material
        {
            // AISI 304 annealed: k and σ notably below carbon steel.
            Name = "Stainless steel 304", YoungsModulus = 193e9, PoissonRatio = 0.29, Density = 8000,
            ThermalConductivity = 16.2, SpecificHeat = 500, ElectricalConductivity = 1.39e6,
            Color = "#AEB6BD", IsBuiltIn = true
        },
        new Material
        {
            Name = "Aluminum 6061-T6", YoungsModulus = 68.9e9, PoissonRatio = 0.33, Density = 2700,
            ThermalConductivity = 167, SpecificHeat = 896, ElectricalConductivity = 2.5e7,
            Color = "#C8CDD2", IsBuiltIn = true
        },
        new Material
        {
            Name = "Aluminum 7075-T6", YoungsModulus = 71.7e9, PoissonRatio = 0.33, Density = 2810,
            ThermalConductivity = 130, SpecificHeat = 960, ElectricalConductivity = 1.9e7,
            Color = "#BAC4CE", IsBuiltIn = true
        },
        new Material
        {
            Name = "Copper (annealed)", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
            ThermalConductivity = 401, SpecificHeat = 385, ElectricalConductivity = 5.96e7,
            Color = "#C87533", IsBuiltIn = true
        },
        new Material
        {
            Name = "Brass (C26000)", YoungsModulus = 110e9, PoissonRatio = 0.31, Density = 8530,
            ThermalConductivity = 120, SpecificHeat = 380, ElectricalConductivity = 1.6e7,
            Color = "#C9A44C", IsBuiltIn = true
        },
        new Material
        {
            // Ti-6Al-4V (grade 5): poor conductor both thermally and electrically.
            Name = "Titanium Ti-6Al-4V", YoungsModulus = 113.8e9, PoissonRatio = 0.342, Density = 4430,
            ThermalConductivity = 6.7, SpecificHeat = 526, ElectricalConductivity = 5.8e5,
            Color = "#9AA0A8", IsBuiltIn = true
        },
        new Material
        {
            Name = "Gold (pure)", YoungsModulus = 79e9, PoissonRatio = 0.44, Density = 19300,
            ThermalConductivity = 318, SpecificHeat = 129, ElectricalConductivity = 4.1e7,
            Color = "#D4AF37", IsBuiltIn = true
        },
        new Material
        {
            Name = "Silver (pure)", YoungsModulus = 83e9, PoissonRatio = 0.37, Density = 10490,
            ThermalConductivity = 429, SpecificHeat = 235, ElectricalConductivity = 6.3e7,
            Color = "#D8D8D8", IsBuiltIn = true
        },
        new Material
        {
            Name = "Nickel (pure)", YoungsModulus = 200e9, PoissonRatio = 0.31, Density = 8908,
            ThermalConductivity = 90.9, SpecificHeat = 444, ElectricalConductivity = 1.43e7,
            Color = "#B8B8A8", IsBuiltIn = true
        },
        new Material
        {
            // SAC305 (96.5Sn/3Ag/0.5Cu) lead-free solder.
            Name = "Solder SAC305", YoungsModulus = 51e9, PoissonRatio = 0.40, Density = 7380,
            ThermalConductivity = 58, SpecificHeat = 230, ElectricalConductivity = 7.6e6,
            Color = "#A9A9B0", IsBuiltIn = true
        },

        // ---- Polymers ----
        new Material
        {
            Name = "PLA (3D printed)", YoungsModulus = 3.5e9, PoissonRatio = 0.36, Density = 1240,
            ThermalConductivity = 0.13, SpecificHeat = 1800, ElectricalConductivity = 1e-16,
            Color = "#E8D44D", IsBuiltIn = true
        },
        new Material
        {
            Name = "ABS", YoungsModulus = 2.3e9, PoissonRatio = 0.35, Density = 1040,
            ThermalConductivity = 0.17, SpecificHeat = 1400, ElectricalConductivity = 1e-16,
            Color = "#D9822B", IsBuiltIn = true
        },
        new Material
        {
            Name = "Polycarbonate", YoungsModulus = 2.38e9, PoissonRatio = 0.37, Density = 1200,
            ThermalConductivity = 0.20, SpecificHeat = 1250, ElectricalConductivity = 1e-16,
            RelativePermittivity = 2.9, Color = "#8FD0E8", IsBuiltIn = true
        },
        new Material
        {
            Name = "Nylon 6/6", YoungsModulus = 2.9e9, PoissonRatio = 0.39, Density = 1140,
            ThermalConductivity = 0.25, SpecificHeat = 1670, ElectricalConductivity = 1e-16,
            RelativePermittivity = 3.6, Color = "#E8E4D8", IsBuiltIn = true
        },
        new Material
        {
            Name = "PTFE", YoungsModulus = 0.50e9, PoissonRatio = 0.46, Density = 2200,
            ThermalConductivity = 0.25, SpecificHeat = 1000, ElectricalConductivity = 1e-16,
            RelativePermittivity = 2.1, Color = "#F4F4F4", IsBuiltIn = true
        },

        // ---- Ceramics / glass / semiconductors ----
        new Material
        {
            Name = "Alumina (96%)", YoungsModulus = 300e9, PoissonRatio = 0.21, Density = 3720,
            ThermalConductivity = 25, SpecificHeat = 880, ElectricalConductivity = 1e-14,
            RelativePermittivity = 9.4, Color = "#EDE6DA", IsBuiltIn = true
        },
        new Material
        {
            Name = "FR4 (PCB laminate)", YoungsModulus = 24e9, PoissonRatio = 0.14, Density = 1850,
            ThermalConductivity = 0.29, SpecificHeat = 1100, ElectricalConductivity = 1e-14,
            RelativePermittivity = 4.4, Color = "#4C7A3F", IsBuiltIn = true
        },
        new Material
        {
            // Single-crystal ⟨100⟩. σ left null: it spans ~8 decades with doping — a
            // user must set it consciously rather than inherit a misleading default.
            Name = "Silicon (single-crystal)", YoungsModulus = 130e9, PoissonRatio = 0.28, Density = 2329,
            ThermalConductivity = 148, SpecificHeat = 700, ElectricalConductivity = null,
            RelativePermittivity = 11.7, Color = "#5B6770", IsBuiltIn = true
        },
        new Material
        {
            Name = "Borosilicate glass", YoungsModulus = 63e9, PoissonRatio = 0.20, Density = 2230,
            ThermalConductivity = 1.14, SpecificHeat = 830, ElectricalConductivity = 1e-16,
            RelativePermittivity = 4.6, Color = "#C7E6E2", IsBuiltIn = true
        }
    };
}
