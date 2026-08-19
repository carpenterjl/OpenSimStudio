using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Gates for the strength properties a safety factor and a beyond-UTS view need. Both are
/// NULLABLE on purpose: a brittle material has no yield point at all, and inventing one
/// would put a fabricated allowable on a result field.
/// </summary>
public sealed class MaterialStrengthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oss-strength-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Built-ins over a private empty directory: no machine-wide user overlay.</summary>
    private IReadOnlyList<Material> BuiltIns() => new MaterialLibrary(_dir).Materials;

    private static Material Base(double? yield = null, double? ultimate = null) => new()
    {
        Name = "M", YoungsModulus = 2e11, PoissonRatio = 0.3, Density = 7850,
        YieldStrength = yield, UltimateTensileStrength = ultimate
    };

    [Fact]
    public void MissingStrengths_AreValid()
    {
        Base().ValidateMechanical();
        Assert.Null(Base().YieldStrength);
        Assert.Null(Base().UltimateTensileStrength);
    }

    [Fact]
    public void NonPositiveStrength_IsATypedFailure()
    {
        Assert.Throws<InvalidOperationException>(() => Base(yield: 0).ValidateMechanical());
        Assert.Throws<InvalidOperationException>(() => Base(ultimate: -1).ValidateMechanical());
    }

    /// <summary>A material cannot fracture before it yields — that ordering is physics.</summary>
    [Fact]
    public void YieldAboveUltimate_IsATypedFailure()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Base(yield: 500e6, ultimate: 400e6).ValidateMechanical());
        Assert.Contains("exceeds", ex.Message);
    }

    [Fact]
    public void EqualYieldAndUltimate_IsAllowed()
    {
        // Legitimate for a material that fractures at yield.
        Base(yield: 60e6, ultimate: 60e6).ValidateMechanical();
    }

    /// <summary>
    /// The two reference materials carry the values taken from the Ansys project files
    /// themselves, so a reproduction of that study starts from the same numbers.
    /// </summary>
    [Fact]
    public void BuiltInLibrary_CarriesTheReferenceStrengths()
    {
        var library = BuiltIns();

        var steel = library.Single(m => m.Name == "Structural steel");
        Assert.Equal(250e6, steel.YieldStrength);
        Assert.Equal(460e6, steel.UltimateTensileStrength);

        var abs = library.Single(m => m.Name == "ABS");
        Assert.Equal(27.44e6, abs.YieldStrength);
        Assert.Equal(36.26e6, abs.UltimateTensileStrength);
    }

    /// <summary>
    /// Brittle materials keep a NULL yield strength — the documented "not characterised"
    /// case, following the Silicon conductivity precedent. A safety factor is then not
    /// offered rather than computed from an invented allowable.
    /// </summary>
    [Fact]
    public void BrittleBuiltIns_HaveNoYieldStrength()
    {
        var library = BuiltIns();
        foreach (string name in new[] { "Alumina (96%)", "Borosilicate glass", "Silicon (single-crystal)" })
            Assert.Null(library.Single(m => m.Name == name).YieldStrength);
    }

    [Fact]
    public void EveryBuiltIn_PassesMechanicalValidation()
    {
        foreach (var material in BuiltIns())
            material.ValidateMechanical();
    }

    [Fact]
    public void Strengths_RoundTripThroughProjectJson()
    {
        var body = new Body { Name = "B", Material = Base(yield: 250e6, ultimate: 460e6) };
        var project = new SimProject { Name = "P" };
        project.Bodies.Add(body);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = Assert.Single(serializer.Load(path).Bodies).Material!;

            Assert.Equal(250e6, loaded.YieldStrength);
            Assert.Equal(460e6, loaded.UltimateTensileStrength);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A project written before strengths existed must still load.</summary>
    [Fact]
    public void ProjectWithoutStrengths_LoadsWithNulls()
    {
        const string legacy = """
            {
              "name": "Legacy",
              "bodies": [
                {
                  "name": "B",
                  "material": { "name": "Steel", "youngsModulus": 2e11, "poissonRatio": 0.3, "density": 7850 }
                }
              ]
            }
            """;
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            File.WriteAllText(path, legacy);
            var material = Assert.Single(new ProjectSerializer().Load(path).Bodies).Material!;

            Assert.Null(material.YieldStrength);
            Assert.Null(material.UltimateTensileStrength);
            material.ValidateMechanical();
        }
        finally { File.Delete(path); }
    }
}
