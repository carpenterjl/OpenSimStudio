using OpenSim.Core.Model;

namespace OpenSim.Tests.Core;

/// <summary>
/// D24: a project's own material values against the library's of the same name — what the
/// divergence line names, and the order a solve resolves a name in.
/// </summary>
public class ProjectMaterialsTests
{
    private static Material Alloy(string name, double k) => new()
    {
        Name = name, YoungsModulus = 1e11, PoissonRatio = 0.3, Density = 8000, ThermalConductivity = k
    };

    [Fact]
    public void Divergence_NamesEachDifferingProperty()
    {
        var library = Alloy("Alloy", 100) with { Emissivity = 0.2, IsBuiltIn = true };
        // The built-in flag is the library's bookkeeping, not a value.
        Assert.True(MaterialDivergence.SameValues(library with { IsBuiltIn = false }, library));
        Assert.Null(MaterialDivergence.Describe(library with { IsBuiltIn = false }, library));

        var project = library with { ThermalConductivity = 120, Emissivity = null, Color = "#FF0000" };
        Assert.False(MaterialDivergence.SameValues(project, library));
        Assert.Equal(new[] { "thermal conductivity 120 vs 100 W/(m·K)", "emissivity unset vs 0.2", "colour #FF0000 vs #B0B0B0" },
            MaterialDivergence.Differences(project, library));
        Assert.Equal("'Alloy': the project's values differ from the library's (thermal conductivity 120 vs 100 W/(m·K), " +
                     "emissivity unset vs 0.2, colour #FF0000 vs #B0B0B0); the project's are used.",
            MaterialDivergence.Describe(project, library));

        // Two values that print alike to six figures are printed in full.
        Assert.Equal("thermal conductivity 100.0000001 vs 100 W/(m·K)",
            Assert.Single(MaterialDivergence.Differences(library with { ThermalConductivity = 100.0000001 }, library)));
    }

    [Fact]
    public void Lookup_OverrideThenProjectThenLibrary_OneLinePerMaterial()
    {
        var library = new[] { Alloy("Alloy", 100), Alloy("Laminate", 0.3) };
        var ours = Alloy("Alloy", 120);
        var lines = new List<string>();
        var lookup = new ProjectMaterialLookup(new Material?[] { null, ours }, library, null, lines.Add);
        Assert.Same(ours, lookup.Of(ours));
        Assert.Same(ours, lookup.ByName("Alloy"));
        Assert.Same(library[1], lookup.ByName("Laminate"));
        Assert.Null(lookup.ByName("Unobtainium"));
        // Met twice, logged once.
        Assert.StartsWith("Material 'Alloy': the project's values differ from the library's (thermal conductivity 120 vs 100",
            Assert.Single(lines));

        // The library's own values: nothing to say.
        var quiet = new List<string>();
        var same = Alloy("Alloy", 100);
        Assert.Same(same, new ProjectMaterialLookup(new[] { same }, library, null, quiet.Add).Of(same));
        Assert.Empty(quiet);

        // A sweep's override of the name wins over both, and is not a divergence.
        var swept = Alloy("Alloy", 150);
        var overridden = new ProjectMaterialLookup(new[] { ours }, library,
            new Dictionary<string, Material> { ["Alloy"] = swept }, quiet.Add);
        Assert.Same(swept, overridden.Of(ours));
        Assert.Same(swept, overridden.ByName("Alloy"));
        Assert.Empty(quiet);

        // The first body's copy of a name (the solved body's) wins over another body's.
        var theirs = Alloy("Alloy", 130);
        Assert.Same(theirs, new ProjectMaterialLookup(new[] { theirs, ours }, library, null, _ => { }).ByName("Alloy"));
    }
}
