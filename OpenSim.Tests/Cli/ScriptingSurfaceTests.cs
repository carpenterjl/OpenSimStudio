using System.Reflection;
using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Geometry;
using OpenSim.Rf.Si;
using Xunit;

namespace OpenSim.Tests.Cli;

/// <summary>
/// FU-46 (owner's decision 2026-10-25): no scripting language of OpenSim's own — JGraph drives
/// OpenSim's libraries in-process through its MATLAB-style .NET interop (<c>NET.addAssembly</c>, a
/// dotted name reaches a type, a function handle becomes a delegate). What OpenSim owes that is a
/// public, stable, scriptable surface: the entry points <c>docs/Scripting-from-JGraph.md</c> names,
/// reachable by name through reflection (as a script reaches them) with the signatures the document
/// shows. This pins them; renaming or re-typing one fails here before it fails a user's script.
/// The worked script itself (<c>examples/jgraph/opensim_from_jgraph.m</c>) was run under JGraph's
/// <c>jgraph -batch</c> on 2026-10-25 and its numbers are the ones the direct call below gives.
/// </summary>
public class ScriptingSurfaceTests
{
    /// <summary>The assemblies a script adds (OpenSim.Core … OpenSim.Cfd, then opensim.dll), named
    /// through one type each so they are loaded whether or not a test touched them yet.</summary>
    private static readonly System.Reflection.Assembly[] Added =
    {
        typeof(SimProject).Assembly, typeof(PrimitiveFactory).Assembly, typeof(OpenSim.Meshing.DelaunayMeshGenerator).Assembly,
        typeof(OpenSim.Solvers.Magnetics.MagneticSolver2D).Assembly, typeof(OpenSim.Pcb.Magnetics.PlanarMagneticsStudy).Assembly,
        typeof(ImpedanceCalculator).Assembly, typeof(OpenSim.Cfd.CartesianGrid).Assembly, typeof(StudyRunner).Assembly,
    };

    private static Type Named(string fullName)
    {
        var type = Added.Select(a => a.GetType(fullName)).FirstOrDefault(t => t is not null);
        Assert.True(type is not null, $"The scripting surface names the type '{fullName}'.");
        return type!;
    }

    private static void HasMethod(string typeName, string method, params string[] parameterTypeNames)
    {
        var type = Named(typeName);
        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.Name == method).ToList();
        Assert.True(candidates.Count > 0, $"{typeName}.{method} is public.");
        // The listed parameters lead the signature; optional ones may follow them (a script omits those).
        Assert.True(candidates.Any(m => m.GetParameters().Select(p => Describe(p.ParameterType)).Take(parameterTypeNames.Length).SequenceEqual(parameterTypeNames)),
            $"{typeName}.{method}({string.Join(", ", parameterTypeNames)}, ...) is public; found: " +
            string.Join(" | ", candidates.Select(m => string.Join(", ", m.GetParameters().Select(p => Describe(p.ParameterType))))));
    }

    private static void HasSettableProperty(string typeName, string property, string propertyTypeName)
    {
        var p = Named(typeName).GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(p is not null && p.CanWrite && p.SetMethod!.IsPublic, $"{typeName}.{property} is a public settable property.");
        Assert.Equal(propertyTypeName, Describe(p!.PropertyType));
    }

    private static string Describe(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } inner) return Describe(inner) + "?";
        if (!t.IsGenericType) return t.Name;
        return t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(",", t.GetGenericArguments().Select(Describe)) + ">";
    }

    [Fact]
    public void TheEntryPoints_TheDocumentNames_ArePublic_WithTheSignaturesItShows()
    {
        // Loading and saving a project; the material library.
        HasMethod("OpenSim.Core.Persistence.ProjectSerializer", "Load", "String");
        HasMethod("OpenSim.Core.Persistence.ProjectSerializer", "Save", "SimProject", "String");
        Assert.NotNull(Named("OpenSim.Core.Persistence.MaterialLibrary").GetConstructor(Type.EmptyTypes));

        // Building a project in the script.
        Assert.NotNull(Named("OpenSim.Core.Model.SimProject").GetConstructor(Type.EmptyTypes));
        Assert.NotNull(Named("OpenSim.Core.Model.Body").GetConstructor(Type.EmptyTypes));
        HasMethod("OpenSim.Geometry.PrimitiveFactory", "CreateBox", "Double", "Double", "Double");
        HasSettableProperty("OpenSim.Core.Model.FixedTemperature", "FaceIds", "IReadOnlyList<Int32>");
        HasSettableProperty("OpenSim.Core.Model.FixedTemperature", "Kelvin", "Double");

        // Meshing and solving as the command line's `solve` does.
        HasMethod("OpenSim.Cli.StudySettings", "FromProject", "SolverSettings");
        HasMethod("OpenSim.Cli.StudyRunner", "DefaultMesher");
        HasMethod("OpenSim.Cli.StudyRunner", "MeshBodies", "SimProject", "IMeshGenerator", "Boolean", "Action<String>");
        HasMethod("OpenSim.Cli.StudyRunner", "Solve", "SimProject", "String", "MaterialLibrary", "StudySettings", "Action<String>",
            "IProgress<SolverProgress>", "CancellationToken", "IReadOnlyDictionary<String,Material>");
        HasMethod("OpenSim.Cli.StudyRunner", "Outputs", "StudyRun");
        Assert.Contains("Thermal", StudyRunner.Analyses);

        // The calculators and studies a script calls directly.
        HasMethod("OpenSim.Rf.Si.ImpedanceCalculator", "Solve", "LineSpec");
        HasSettableProperty("OpenSim.Rf.Si.LineSpec", "WidthMeters", "Double");
        HasSettableProperty("OpenSim.Rf.Si.LineSpec", "FrequencyHz", "Double");
        HasMethod("OpenSim.Rf.Emi.EmiStudy", "Run", "EmiStudyInput", "EmiLimitLine", "EmiLimitLine");
        HasMethod("OpenSim.Pcb.Magnetics.PlanarMagneticsStudy", "Run", "PlanarMagneticsInput");
        HasMethod("OpenSim.Rf.Emi.EmiStudy", "NamedLimit", "String");
    }

    [Fact]
    public void TheWorkedScript_ByDirectCall_GivesTheNumbersJGraphGave()
    {
        // examples/jgraph/opensim_from_jgraph.m, line for line, without the interop: a 10 × 10 × 20 mm
        // copper bar at 300 K / 400 K — q = k·ΔT/L = 400 × 100 / 0.02 = 2e6 W/m² — and a 2.413 mm
        // microstrip on 0.794 mm of εr 2.2 at 3 GHz.
        var body = new Body
        {
            Name = "Bar",
            Geometry = PrimitiveFactory.CreateBox(0.01, 0.01, 0.02),
            GeometrySource = "Box 10 x 10 x 20 mm",
            MeshSettings = new MeshSettings { TargetEdgeLength = 0.005, Method = MeshMethod.StructuredLattice },
            Material = new Material
            {
                Name = "TestCopper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
                ThermalConductivity = 400, SpecificHeat = 385, ElectricalConductivity = 5.96e7
            }
        };
        body.BoundaryConditions.Add(new FixedTemperature { Name = "cold", FaceIds = new List<int> { 4 }, Kelvin = 300 });
        body.BoundaryConditions.Add(new FixedTemperature { Name = "hot", FaceIds = new List<int> { 5 }, Kelvin = 400 });
        var project = new SimProject { Name = "Bar", AnalysisType = "Thermal" };
        project.Bodies.Add(body);

        var log = new List<string>();
        var settings = StudySettings.FromProject(project.Solver);
        StudyRunner.MeshBodies(project, StudyRunner.DefaultMesher(), false, log.Add);
        var run = StudyRunner.Solve(project, "Thermal", new MaterialLibrary(), settings, log.Add);
        var outputs = StudyRunner.Outputs(run);
        Assert.Contains(log, l => l.StartsWith("Meshed 'Bar': 45 nodes, 96 elements"));
        Assert.Equal(400, outputs["Max Temperature (K)"], 9);
        Assert.Equal(300, outputs["Min Temperature (K)"], 9);
        Assert.Equal(2e6, outputs.Single(o => o.Key.StartsWith("Max Heat flux")).Value, 2e6 * 1e-9);
        Assert.Equal(2e6, outputs.Single(o => o.Key.StartsWith("Min Heat flux")).Value, 2e6 * 1e-9);

        var mode = ImpedanceCalculator.Solve(new LineSpec
        {
            WidthMeters = 2.413e-3, HeightMeters = 0.794e-3, ThicknessMeters = 1e-6,
            RelativePermittivity = 2.2, LossTangent = 0, FrequencyHz = 3e9
        }).Modes[0];
        Assert.Equal(50.4731, mode.ImpedanceOhms, 4);          // what jgraph -batch printed on 2026-10-25
        Assert.Equal(1.88472, mode.EffectivePermittivity, 5);
    }
}
