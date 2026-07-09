using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Library semantics: user materials ADD/OVERRIDE by name on top of built-ins, problems
/// warn instead of silently replacing the library, and everything persists round-trip.
/// Every test uses a private temp directory — never the real AppData.
/// </summary>
public sealed class MaterialLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oss-mat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string UserFile => Path.Combine(_dir, "materials.json");

    private void WriteUserFile(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(UserFile, json);
    }

    [Fact]
    public void BuiltIns_AreValid_Unique_AndFlagged()
    {
        var library = new MaterialLibrary(_dir);

        Assert.Equal(20, library.Materials.Count);
        Assert.Empty(library.LoadWarnings);
        Assert.Equal(library.Materials.Count, library.Materials.Select(m => m.Name).Distinct().Count());
        foreach (var material in library.Materials)
        {
            Assert.True(material.IsBuiltIn, $"{material.Name} must be flagged built-in.");
            material.ValidateMechanical();
            if (material.ThermalConductivity is not null) material.ValidateThermal();
            if (material.ElectricalConductivity is not null) material.ValidateElectrical();
            Assert.Matches("^#[0-9A-Fa-f]{6}$", material.Color);
        }
    }

    [Fact]
    public void UserFile_Adds_AndOverridesByName()
    {
        WriteUserFile("""
        [
          { "name": "Unobtainium", "youngsModulus": 5e11, "poissonRatio": 0.25, "density": 12000 },
          { "name": "Copper (annealed)", "youngsModulus": 999e9, "poissonRatio": 0.34, "density": 8960 }
        ]
        """);
        var library = new MaterialLibrary(_dir);

        Assert.Equal(21, library.Materials.Count);                // 20 built-ins + 1 added
        var added = Assert.Single(library.Materials, m => m.Name == "Unobtainium");
        Assert.False(added.IsBuiltIn);

        var copper = Assert.Single(library.Materials, m => m.Name == "Copper (annealed)");
        Assert.Equal(999e9, copper.YoungsModulus);                // overridden, not duplicated
        Assert.False(copper.IsBuiltIn);                           // an override is user-defined
    }

    [Fact]
    public void MalformedFile_KeepsBuiltIns_AndWarns()
    {
        WriteUserFile("{ this is not json ");
        var library = new MaterialLibrary(_dir);

        Assert.Equal(20, library.Materials.Count);
        var warning = Assert.Single(library.LoadWarnings);
        Assert.Contains("malformed", warning);
    }

    [Fact]
    public void InvalidEntryAmongValid_LoadsValid_AndNamesTheBadOne()
    {
        WriteUserFile("""
        [
          { "name": "Good", "youngsModulus": 1e9, "poissonRatio": 0.3, "density": 1000 },
          { "name": "Bad nu", "youngsModulus": 1e9, "poissonRatio": 0.7, "density": 1000 },
          { "name": "", "youngsModulus": 1e9, "poissonRatio": 0.3, "density": 1000 }
        ]
        """);
        var library = new MaterialLibrary(_dir);

        Assert.Contains(library.Materials, m => m.Name == "Good");
        Assert.DoesNotContain(library.Materials, m => m.Name == "Bad nu");
        Assert.Equal(2, library.LoadWarnings.Count);
        Assert.Contains(library.LoadWarnings, w => w.Contains("Bad nu"));
    }

    [Fact]
    public void AddUpdateRemove_PersistAcrossInstances()
    {
        var library = new MaterialLibrary(_dir);
        library.AddOrUpdateUserMaterial(new Material
        {
            Name = "Custom PLA", YoungsModulus = 3.2e9, PoissonRatio = 0.35, Density = 1250
        });

        var reloaded = new MaterialLibrary(_dir);
        var custom = Assert.Single(reloaded.Materials, m => m.Name == "Custom PLA");
        Assert.Equal(3.2e9, custom.YoungsModulus);
        Assert.False(custom.IsBuiltIn);

        reloaded.RemoveUserMaterial("Custom PLA");
        var final = new MaterialLibrary(_dir);
        Assert.DoesNotContain(final.Materials, m => m.Name == "Custom PLA");
        Assert.Equal(20, final.Materials.Count);
    }

    [Fact]
    public void RemovingBuiltIn_RefusesLoudly_RemovingOverride_RestoresBuiltIn()
    {
        var library = new MaterialLibrary(_dir);
        Assert.Throws<InvalidOperationException>(() => library.RemoveUserMaterial("Copper (annealed)"));

        library.AddOrUpdateUserMaterial(new Material
        {
            Name = "Copper (annealed)", YoungsModulus = 999e9, PoissonRatio = 0.34, Density = 8960
        });
        Assert.False(Assert.Single(library.Materials, m => m.Name == "Copper (annealed)").IsBuiltIn);

        library.RemoveUserMaterial("Copper (annealed)");
        var copper = Assert.Single(library.Materials, m => m.Name == "Copper (annealed)");
        Assert.True(copper.IsBuiltIn);                            // built-in restored in place
        Assert.Equal(110e9, copper.YoungsModulus);
        Assert.Equal(20, library.Materials.Count);
    }

    [Fact]
    public void SavedUserFile_ContainsOnlyUserMaterials()
    {
        var library = new MaterialLibrary(_dir);
        library.AddOrUpdateUserMaterial(new Material
        {
            Name = "Only mine", YoungsModulus = 1e9, PoissonRatio = 0.3, Density = 1000
        });

        string json = File.ReadAllText(UserFile);
        Assert.Contains("Only mine", json);
        Assert.DoesNotContain("Structural steel", json);          // built-ins never serialized
    }

    [Fact]
    public void LegacyExeAdjacentFile_IsHonored_WithDeprecationWarning()
    {
        string legacyDir = _dir + "-legacy";
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "materials.json"), """
        [ { "name": "Legacy metal", "youngsModulus": 1e9, "poissonRatio": 0.3, "density": 1000 } ]
        """);
        try
        {
            var library = new MaterialLibrary(_dir, legacyDir);
            Assert.Contains(library.Materials, m => m.Name == "Legacy metal");
            Assert.Contains(library.LoadWarnings, w => w.Contains("deprecated"));

            // Once an AppData file exists, the legacy file is no longer consulted.
            WriteUserFile("[]");
            var modern = new MaterialLibrary(_dir, legacyDir);
            Assert.DoesNotContain(modern.Materials, m => m.Name == "Legacy metal");
            Assert.Empty(modern.LoadWarnings);
        }
        finally
        {
            Directory.Delete(legacyDir, recursive: true);
        }
    }

    [Fact]
    public void InvalidMaterial_IsRejectedOnAdd()
    {
        var library = new MaterialLibrary(_dir);
        Assert.Throws<InvalidOperationException>(() => library.AddOrUpdateUserMaterial(new Material
        {
            Name = "Broken", YoungsModulus = -1, PoissonRatio = 0.3, Density = 1000
        }));
        Assert.Throws<InvalidOperationException>(() => library.AddOrUpdateUserMaterial(new Material
        {
            Name = "  ", YoungsModulus = 1e9, PoissonRatio = 0.3, Density = 1000
        }));
    }
}
