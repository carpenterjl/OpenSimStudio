using System.Reflection;
using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Geometry;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Scripting;
using OpenSim.Optics.Tolerancing;
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
        typeof(OpticsScripting).Assembly,   // OP-98: OpenSim.Optics.dll
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

    private static void HasReadableProperty(string typeName, string property, string propertyTypeName, bool isStatic = false)
    {
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var p = Named(typeName).GetProperty(property, flags);
        Assert.True(p is not null && p.CanRead && p.GetMethod!.IsPublic, $"{typeName}.{property} is a public readable{(isStatic ? " static" : "")} property.");
        Assert.Equal(propertyTypeName, Describe(p!.PropertyType));
    }

    private static void HasConstructor(string typeName, params string[] parameterTypeNames)
    {
        var type = Named(typeName);
        Assert.True(type.GetConstructors().Any(c => c.GetParameters().Select(p => Describe(p.ParameterType)).Take(parameterTypeNames.Length).SequenceEqual(parameterTypeNames)),
            $"new {typeName}({string.Join(", ", parameterTypeNames)}, ...) is public.");
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

    // ------------------------------------------------------------------ OP-98: optics

    /// <summary>
    /// OP-98: the optics entry points docs/Scripting-from-JGraph.md ("Optics") lists, every type
    /// and member <c>examples/jgraph/optics_from_jgraph.m</c> names among them. Settings records
    /// are C# records with init-only setters; a script sets them with a dot like any property
    /// (JGraph's interop writes them by reflection, checked under jgraph -batch on 2026-11-14), so
    /// <see cref="HasSettableProperty"/> accepts them.
    /// </summary>
    [Fact]
    public void TheOpticsEntryPoints_TheDocumentNames_ArePublic_WithTheSignaturesItShows()
    {
        const string M = "OpenSim.Core.Model.";
        // The system: the optics record and its editors' data (system data, lens data, merit
        // function, multi-configuration and tolerance data), saved with the project.
        HasSettableProperty(M + "SimProject", "Optics", "OpticsSettings");
        Assert.NotNull(Named(M + "OpticsSettings").GetConstructor(Type.EmptyTypes));
        HasSettableProperty(M + "OpticsSettings", "SystemData", "OpticsSystemSettings");
        HasSettableProperty(M + "OpticsSettings", "Surfaces", "List<OpticsSurfaceSettings>");
        HasSettableProperty(M + "OpticsSettings", "MeritFunction", "OpticsMeritFunctionSettings");
        HasSettableProperty(M + "OpticsSettings", "MultiConfiguration", "OpticsMultiConfigSettings");
        HasSettableProperty(M + "OpticsSettings", "Tolerances", "OpticsToleranceSettings");
        HasSettableProperty(M + "OpticsSettings", "Materials", "OpticsMaterialsSettings");
        HasSettableProperty(M + "OpticsSystemSettings", "ApertureValue", "Double");
        HasSettableProperty(M + "OpticsSystemSettings", "ApertureType", "String");
        HasSettableProperty(M + "OpticsSystemSettings", "Fields", "List<OpticsFieldSettings>");
        HasSettableProperty(M + "OpticsSystemSettings", "Wavelengths", "List<OpticsWavelengthSettings>");
        HasSettableProperty(M + "OpticsSystemSettings", "PrimaryWavelength", "Int32");
        HasSettableProperty(M + "OpticsFieldSettings", "Y", "Double");
        HasSettableProperty(M + "OpticsWavelengthSettings", "Micrometers", "Double");
        HasReadableProperty("OpenSim.Optics.SpectralLines", "F", "SpectralLine", isStatic: true);
        HasReadableProperty("OpenSim.Optics.SpectralLines", "LowerD", "SpectralLine", isStatic: true);
        HasReadableProperty("OpenSim.Optics.SpectralLines", "C", "SpectralLine", isStatic: true);
        HasReadableProperty("OpenSim.Optics.SpectralLine", "Micrometers", "Double");
        HasSettableProperty(M + "OpticsSurfaceSettings", "Radius", "Double?");
        HasSettableProperty(M + "OpticsSurfaceSettings", "Thickness", "Double");
        HasSettableProperty(M + "OpticsSurfaceSettings", "ThicknessIsInfinite", "Boolean");
        HasSettableProperty(M + "OpticsSurfaceSettings", "Material", "String");
        HasSettableProperty(M + "OpticsSurfaceSettings", "IsStop", "Boolean");
        HasSettableProperty(M + "OpticsSurfaceSettings", "Flags", "List<OpticsCellFlagSettings>");
        HasSettableProperty(M + "OpticsCellFlagSettings", "Column", "String");
        HasSettableProperty(M + "OpticsCellFlagSettings", "Kind", "String");
        HasSettableProperty(M + "OpticsCellFlagSettings", "Solve", "String");
        HasSettableProperty(M + "OpticsCellFlagSettings", "SolveParameters", "List<Double>");
        HasSettableProperty(M + "OpticsMultiConfigSettings", "ConfigurationCount", "Int32");
        HasSettableProperty(M + "OpticsMultiConfigSettings", "CurrentConfiguration", "Int32");
        HasSettableProperty(M + "OpticsMultiConfigSettings", "Operands", "List<OpticsConfigOperandSettings>");
        HasMethod("OpenSim.Optics.MultiConfig.MultiConfiguration", "ConfigurationCount", "OpticsSettings");
        HasMethod("OpenSim.Optics.MultiConfig.MultiConfiguration", "SettingsFor", "OpticsSettings", "Int32", "IReadOnlyList<OpticalCatalog>");
        HasMethod("OpenSim.Optics.MultiConfig.MultiConfiguration", "WithValue", "OpticsSettings", "MultiConfigCell", "Double");

        // Materials, and the lens as the engines see it.
        HasReadableProperty("OpenSim.Optics.Materials.GlassCatalogLibrary", "BuiltInCatalogs", "IReadOnlyList<OpticalCatalog>", isStatic: true);
        HasMethod("OpenSim.Optics.Scripting.OpticsScripting", "Catalogs", "OpticsSettings");
        HasMethod("OpenSim.Optics.Scripting.OpticsScripting", "Load", "String");
        HasMethod("OpenSim.Optics.Analysis.ProjectLens", "From", "OpticsSettings", "IReadOnlyList<OpticalCatalog>");
        HasMethod("OpenSim.Optics.SystemDataSettings", "FromSettings", "OpticsSystemSettings", "ICollection<String>");

        // The merit function: rows, the Optimization Wizard, evaluation.
        HasMethod("OpenSim.Optics.Merit.MeritOperandRow", "Of", "String", "Int32", "Int32", "Double", "Double", "Double[]");
        HasConstructor("OpenSim.Optics.Merit.MeritFunction", "IEnumerable<MeritOperandRow>");
        HasMethod("OpenSim.Optics.Merit.MeritFunctionSettings", "ToSettings", "MeritFunction");
        HasMethod("OpenSim.Optics.Merit.MeritFunctionSettings", "FromSettings", "OpticsMeritFunctionSettings", "MeritOperandRegistry", "ICollection<String>");
        HasSettableProperty("OpenSim.Optics.Merit.DefaultMeritOptions", "Criterion", "MeritWizardCriterion");
        HasSettableProperty("OpenSim.Optics.Merit.DefaultMeritOptions", "Rings", "Int32");
        HasMethod("OpenSim.Optics.Scripting.OpticsScripting", "WithDefaultMerit", "OpticsSettings", "IReadOnlyList<OpticalCatalog>", "DefaultMeritOptions");
        HasMethod("OpenSim.Optics.Scripting.OpticsScripting", "EvaluateMerit", "OpticsSettings", "IReadOnlyList<OpticalCatalog>");
        HasReadableProperty("OpenSim.Optics.Merit.MeritEvaluation", "Value", "Double");
        HasReadableProperty("OpenSim.Optics.Merit.MeritEvaluation", "Rows", "IReadOnlyList<MeritRowResult>");
        HasReadableProperty("OpenSim.Optics.Merit.MeritRowResult", "Value", "Double");

        // The local optimizer (OP-57).
        HasMethod("OpenSim.Optics.Optimization.LocalOptimizer", "Run", "OpticsSettings", "IReadOnlyList<OpticalCatalog>",
            "LocalOptimizationOptions", "MeritFunction", "MeritOperandRegistry");
        HasSettableProperty("OpenSim.Optics.Optimization.LocalOptimizationOptions", "Algorithm", "LocalOptimizationAlgorithm");
        HasSettableProperty("OpenSim.Optics.Optimization.LocalOptimizationOptions", "Cycles", "LocalOptimizationCycles");
        HasReadableProperty("OpenSim.Optics.Optimization.LocalOptimizationResult", "StartMerit", "Double");
        HasReadableProperty("OpenSim.Optics.Optimization.LocalOptimizationResult", "Merit", "Double");
        HasReadableProperty("OpenSim.Optics.Optimization.LocalOptimizationResult", "Cycles", "Int32");
        HasReadableProperty("OpenSim.Optics.Optimization.LocalOptimizationResult", "Stop", "LocalOptimizationStop");
        HasReadableProperty("OpenSim.Optics.Optimization.LocalOptimizationResult", "Optimized", "OpticsSettings");
        // The Global and Hammer searches (OP-58).
        const string GO = "OpenSim.Optics.Optimization.Global.";
        HasMethod(GO + "GlobalSearch", "Run", "OpticsSettings", "IReadOnlyList<OpticalCatalog>", "GlobalSearchOptions", "MeritFunction", "MeritOperandRegistry");
        HasMethod(GO + "HammerSearch", "Run", "OpticsSettings", "IReadOnlyList<OpticalCatalog>", "HammerSearchOptions", "MeritFunction", "MeritOperandRegistry");
        Assert.NotNull(Named(GO + "GlobalSearchOptions").GetConstructor(Type.EmptyTypes));
        HasSettableProperty(GO + "GlobalSearchOptions", "Local", "LocalOptimizationOptions");
        HasSettableProperty(GO + "GlobalSearchOptions", "PopulationSize", "Int32?");
        HasSettableProperty(GO + "GlobalSearchOptions", "Generations", "Int32");
        HasSettableProperty(GO + "GlobalSearchOptions", "MaxEvaluationsPerTrial", "Int32");
        HasSettableProperty(GO + "GlobalSearchOptions", "CurvatureLimit", "Double?");
        HasSettableProperty(GO + "GlobalSearchOptions", "Seed", "Int64");
        HasSettableProperty(GO + "GlobalSearchOptions", "SaveCount", "Int32");
        HasSettableProperty(GO + "GlobalSearchOptions", "TimeLimit", "TimeSpan?");
        HasSettableProperty(GO + "GlobalSearchOptions", "MaxDegreeOfParallelism", "Int32?");
        Assert.NotNull(Named(GO + "HammerSearchOptions").GetConstructor(Type.EmptyTypes));
        HasSettableProperty(GO + "HammerSearchOptions", "Local", "LocalOptimizationOptions");
        HasSettableProperty(GO + "HammerSearchOptions", "Rounds", "Int32");
        HasSettableProperty(GO + "HammerSearchOptions", "TrialsPerRound", "Int32");
        HasSettableProperty(GO + "HammerSearchOptions", "StepFraction", "Double");
        HasSettableProperty(GO + "HammerSearchOptions", "GlassChangeProbability", "Double");
        HasSettableProperty(GO + "HammerSearchOptions", "MaxEvaluationsPerTrial", "Int32");
        HasSettableProperty(GO + "HammerSearchOptions", "CurvatureLimit", "Double?");
        HasSettableProperty(GO + "HammerSearchOptions", "Seed", "Int64");
        HasSettableProperty(GO + "HammerSearchOptions", "TimeLimit", "TimeSpan?");
        HasReadableProperty(GO + "GlobalSearchResult", "StartMerit", "Double");
        HasReadableProperty(GO + "GlobalSearchResult", "Best", "IReadOnlyList<GlobalDesign>");
        HasReadableProperty(GO + "GlobalSearchResult", "Variables", "IReadOnlyList<LensVariable>");
        HasReadableProperty(GO + "GlobalSearchResult", "Stop", "GlobalSearchStop");
        HasReadableProperty(GO + "GlobalSearchResult", "Trials", "Int32");
        HasReadableProperty(GO + "GlobalSearchResult", "FailedTrials", "Int32");
        HasReadableProperty(GO + "GlobalSearchResult", "Log", "IReadOnlyList<String>");
        HasMethod(GO + "GlobalSearchResult", "ApplyTo", "OpticsSettings", "GlobalDesign");
        HasReadableProperty(GO + "GlobalDesign", "Design", "OpticsSettings");
        HasReadableProperty(GO + "GlobalDesign", "Merit", "Double");
        HasReadableProperty(GO + "GlobalDesign", "ConstraintsMet", "Boolean");
        HasReadableProperty(GO + "GlobalDesign", "Values", "IReadOnlyList<Double>");
        HasReadableProperty(GO + "GlobalDesign", "Glasses", "IReadOnlyList<String>");
        HasReadableProperty(GO + "GlobalDesign", "Trial", "Int32");

        // Every analysis by id (the registry), and its result.
        HasMethod("OpenSim.Optics.Scripting.OpticsScripting", "AnalysisIds");
        HasMethod("OpenSim.Optics.Scripting.OpticsScripting", "AnalysisSettings", "String");
        HasMethod("OpenSim.Optics.Scripting.OpticsScripting", "Analyze", "OpticsSettings", "IReadOnlyList<OpticalCatalog>", "String", "Object");
        HasMethod("OpenSim.Optics.Analysis.OpticsAnalysisRegistry", "CreateDefault");
        HasReadableProperty("OpenSim.Optics.Analysis.OpticsAnalysisRegistry", "All", "IReadOnlyList<IOpticsAnalysis>");
        HasMethod("OpenSim.Optics.Analysis.IOpticsAnalysis", "Compute", "OpticsAnalysisContext", "Object", "CancellationToken");
        HasConstructor("OpenSim.Optics.Analysis.OpticsAnalysisContext", "String", "OpticsSettings");
        HasSettableProperty("OpenSim.Optics.Analysis.OpticsAnalysisContext", "Catalogs", "IReadOnlyList<OpticalCatalog>");
        HasSettableProperty("OpenSim.Optics.Analysis.FftMtfSettings", "Sampling", "Int32");
        HasReadableProperty("OpenSim.Optics.Analysis.OpticsAnalysisResult", "Text", "String");
        HasReadableProperty("OpenSim.Optics.Analysis.OpticsAnalysisResult", "Plots", "IReadOnlyList<AnalysisPlot>");
        HasReadableProperty("OpenSim.Optics.Analysis.LinePlot", "Curves", "IReadOnlyList<AnalysisCurve>");
        HasReadableProperty("OpenSim.Optics.Analysis.LinePlot", "XLabel", "String");
        HasReadableProperty("OpenSim.Optics.Analysis.LinePlot", "YLabel", "String");
        HasReadableProperty("OpenSim.Optics.Analysis.AnalysisPlot", "Title", "String");
        HasReadableProperty("OpenSim.Optics.Analysis.AnalysisCurve", "Name", "String");
        HasReadableProperty("OpenSim.Optics.Analysis.AnalysisCurve", "X", "IReadOnlyList<Double>");
        HasReadableProperty("OpenSim.Optics.Analysis.AnalysisCurve", "Y", "IReadOnlyList<Double>");
        Assert.Contains(FftMtfAnalysis.SavedId, OpticsScripting.AnalysisIds());

        // One ray with OpticStudio's RAYE / RAYV codes.
        HasMethod("OpenSim.Optics.Scripting.OpticsScripting", "TraceRay", "OpticsSettings", "IReadOnlyList<OpticalCatalog>",
            "Double", "Double", "Double", "Double", "Int32");
        HasReadableProperty("OpenSim.Optics.Scripting.ScriptRay", "Error", "Int32");
        HasReadableProperty("OpenSim.Optics.Scripting.ScriptRay", "Vignetted", "Int32");
        HasReadableProperty("OpenSim.Optics.Scripting.ScriptRay", "Path", "RayPath");

        // The tolerance data and the tolerancer (OP-63, OP-64).
        Assert.NotNull(Named("OpenSim.Optics.Tolerancing.ToleranceOperandRow").GetConstructor(Type.EmptyTypes));
        HasSettableProperty("OpenSim.Optics.Tolerancing.ToleranceOperandRow", "Code", "String");
        HasSettableProperty("OpenSim.Optics.Tolerancing.ToleranceOperandRow", "Int1", "Int32");
        HasSettableProperty("OpenSim.Optics.Tolerancing.ToleranceOperandRow", "Int2", "Int32");
        HasSettableProperty("OpenSim.Optics.Tolerancing.ToleranceOperandRow", "Min", "Double");
        HasSettableProperty("OpenSim.Optics.Tolerancing.ToleranceOperandRow", "Max", "Double");
        HasConstructor("OpenSim.Optics.Tolerancing.ToleranceTable", "IEnumerable<ToleranceOperandRow>");
        HasMethod("OpenSim.Optics.Tolerancing.ToleranceDataSettings", "FromSettings", "OpticsToleranceSettings", "ICollection<String>");
        HasMethod("OpenSim.Optics.Tolerancing.ToleranceDataSettings", "ToSettings", "ToleranceTable");
        HasMethod("OpenSim.Optics.Tolerancing.ToleranceAnalysis", "QuickSensitivity", "OpticsSettings", "ToleranceTable", "IReadOnlyList<OpticalCatalog>", "ToleranceRunOptions");
        HasMethod("OpenSim.Optics.Tolerancing.ToleranceAnalysis", "Run", "OpticsSettings", "ToleranceTable", "IReadOnlyList<OpticalCatalog>", "ToleranceRunOptions");
        HasSettableProperty("OpenSim.Optics.Tolerancing.ToleranceRunOptions", "Mode", "ToleranceMode");
        HasSettableProperty("OpenSim.Optics.Tolerancing.ToleranceRunOptions", "Criterion", "ToleranceCriterion");
        HasReadableProperty("OpenSim.Optics.Tolerancing.ToleranceRunResult", "Nominal", "Double");
        HasReadableProperty("OpenSim.Optics.Tolerancing.ToleranceRunResult", "Unit", "String");
        HasReadableProperty("OpenSim.Optics.Tolerancing.ToleranceRunResult", "Rows", "IReadOnlyList<ToleranceRowResult>");
        HasReadableProperty("OpenSim.Optics.Tolerancing.ToleranceRowResult", "Row", "Int32");
        HasReadableProperty("OpenSim.Optics.Tolerancing.ToleranceRowResult", "Code", "String");
        HasReadableProperty("OpenSim.Optics.Tolerancing.ToleranceRowResult", "AtMin", "ToleranceTrial");
        HasReadableProperty("OpenSim.Optics.Tolerancing.ToleranceRowResult", "AtMax", "ToleranceTrial");
        HasReadableProperty("OpenSim.Optics.Tolerancing.ToleranceTrial", "Change", "Double");
        // Monte Carlo tolerancing (OP-65).
        const string T = "OpenSim.Optics.Tolerancing.";
        HasMethod(T + "MonteCarloAnalysis", "Run", "OpticsSettings", "ToleranceTable", "IReadOnlyList<OpticalCatalog>", "ToleranceRunOptions", "MonteCarloOptions");
        HasMethod(T + "MonteCarloAnalysis", "TrialLens", "OpticsSettings", "ToleranceTable", "IReadOnlyList<OpticalCatalog>", "ToleranceRunOptions", "MonteCarloOptions", "Int32");
        HasMethod("OpenSim.Optics.Scripting.OpticsScripting", "MonteCarlo", "OpticsSettings", "IReadOnlyList<OpticalCatalog>", "MonteCarloOptions");
        Assert.NotNull(Named(T + "MonteCarloOptions").GetConstructor(Type.EmptyTypes));
        HasSettableProperty(T + "MonteCarloOptions", "Trials", "Int32");
        HasSettableProperty(T + "MonteCarloOptions", "Seed", "Int32");
        HasSettableProperty(T + "MonteCarloOptions", "Statistics", "ToleranceStatistics");
        HasSettableProperty(T + "MonteCarloOptions", "StandardDeviations", "Int32");
        HasSettableProperty(T + "MonteCarloOptions", "YieldLimit", "Double?");
        HasSettableProperty(T + "MonteCarloOptions", "Percentiles", "IReadOnlyList<Double>");
        HasReadableProperty(T + "MonteCarloResult", "Seed", "Int32");
        HasReadableProperty(T + "MonteCarloResult", "Nominal", "Double");
        HasReadableProperty(T + "MonteCarloResult", "Unit", "String");
        HasReadableProperty(T + "MonteCarloResult", "Trials", "IReadOnlyList<MonteCarloTrial>");
        HasReadableProperty(T + "MonteCarloResult", "Statistics", "MonteCarloStatistics");
        HasReadableProperty(T + "MonteCarloResult", "Yield", "MonteCarloYield");
        HasReadableProperty(T + "MonteCarloResult", "Evaluated", "Int32");
        HasReadableProperty(T + "MonteCarloResult", "Failed", "Int32");
        HasMethod(T + "MonteCarloResult", "ReportLines");
        HasMethod(T + "MonteCarloResult", "HistogramPlot", "Int32?", "Double?", "Double?");
        HasReadableProperty(T + "MonteCarloTrial", "Index", "Int32");
        HasReadableProperty(T + "MonteCarloTrial", "Criterion", "Double");
        HasReadableProperty(T + "MonteCarloTrial", "Change", "Double");
        HasReadableProperty(T + "MonteCarloTrial", "Failure", "String");
        HasReadableProperty(T + "MonteCarloTrial", "Draws", "IReadOnlyList<MonteCarloDraw>");
        HasReadableProperty(T + "MonteCarloStatistics", "Mean", "Double");
        HasReadableProperty(T + "MonteCarloStatistics", "StandardDeviation", "Double");
        HasReadableProperty(T + "MonteCarloStatistics", "Percentiles", "IReadOnlyList<MonteCarloPercentile>");
        HasReadableProperty(T + "MonteCarloYield", "Fraction", "Double");
        // OP-17: test plate lists and fitting.
        const string TP = "OpenSim.Optics.Optimization.TestPlates.";
        HasMethod(TP + "TestPlateList", "Read", "String");
        HasMethod(TP + "TestPlateList", "Parse", "String", "String");
        HasMethod(TP + "TestPlateList", "Write", "String");
        HasReadableProperty(TP + "TestPlateList", "Plates", "IReadOnlyList<TestPlate>");
        HasMethod(TP + "TestPlateFitting", "Run", "OpticsSettings", "TestPlateList", "IReadOnlyList<OpticalCatalog>", "TestPlateFitOptions", "MeritFunction", "MeritOperandRegistry");
        Assert.NotNull(Named(TP + "TestPlateFitOptions").GetConstructor(Type.EmptyTypes));
        HasSettableProperty(TP + "TestPlateFitOptions", "Method", "TestPlateFitMethod");
        HasSettableProperty(TP + "TestPlateFitOptions", "Optimization", "LocalOptimizationOptions");
        HasReadableProperty(TP + "TestPlateFitResult", "Fitted", "OpticsSettings");
        HasReadableProperty(TP + "TestPlateFitResult", "Merit", "Double");
        HasReadableProperty(TP + "TestPlateFitResult", "Steps", "IReadOnlyList<TestPlateFitStep>");
        HasMethod(TP + "TestPlateFitResult", "ReportLines");
        HasReadableProperty(TP + "TestPlateFitStep", "Surface", "Int32");
        HasReadableProperty(TP + "TestPlateFitStep", "Plate", "TestPlate");
        HasReadableProperty(TP + "TestPlateFitStep", "Merit", "Double");
    }

    /// <summary>The lens of <c>examples/jgraph/optics_from_jgraph.m</c>, built with the same
    /// values: entrance pupil 10 mm, fields 0° and 3°, the F, d and C lines from
    /// <see cref="SpectralLines"/> (SCHOTT TIE-29, Table 2.3-1) with d primary, a 4 mm fused-silica
    /// singlet (the built-in catalogs' SiO2-Malitson) with the stop on its front, both radii
    /// variable, the back distance a marginal-ray-height solve (the paraxial focus); and the
    /// merit row EFFL (primary wavelength) = 100 mm, weight 1. Radii and the target are the
    /// script's arbitrary inputs, not a design from a source.</summary>
    internal static OpticsSettings ScriptSinglet()
    {
        static OpticsCellFlagSettings Variable(string column) => new() { Column = column, Kind = "Variable" };
        return new OpticsSettings
        {
            SystemData = new OpticsSystemSettings
            {
                ApertureValue = 10,
                Fields = new() { new() { Y = 0 }, new() { Y = 3 } },
                Wavelengths = new[] { SpectralLines.F, SpectralLines.LowerD, SpectralLines.C }
                    .Select(l => new OpticsWavelengthSettings { Micrometers = l.Micrometers }).ToList(),
                PrimaryWavelength = 2,
            },
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = 50, Thickness = 4, Material = "SiO2-Malitson", IsStop = true, Flags = new() { Variable("Radius") } },
                new()
                {
                    Radius = -200, Thickness = 95,
                    Flags = new()
                    {
                        Variable("Radius"),
                        new() { Column = "Thickness", Kind = "Solve", Solve = "Marginal ray height", SolveParameters = new() { 0, 0 } },
                    },
                },
                new(),
            },
            MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new List<MeritOperandRow> { MeritOperandRow.Of("EFFL", 0, 2, 100, 1, new double[6]) })),
        };
    }

    /// <summary>
    /// OP-98 gate, without the interop: <c>examples/jgraph/optics_from_jgraph.m</c> step by step
    /// through the same entry points: the default merit function, the local optimizer, one ray,
    /// Quick Sensitivity and the FFT MTF by its registry id. The checks are what must hold
    /// whatever the numbers: the optimizer lowers the merit and brings EFFL nearer its target;
    /// the start and end merit it reports are the facade's evaluation of the same records; the
    /// MTF is a modulation |OTF(ν)|/|OTF(0)|, so it is 1 at ν = 0 up to rounding and lies in
    /// [0, 1] (|OTF(ν)| ≤ OTF(0) by the Cauchy–Schwarz inequality on the pupil autocorrelation;
    /// the polychromatic MTF is a weighted mean of such ratios, so the bound holds for it too).
    /// jgraph -batch printed, on 2026-11-14, merit 4.65323 → 0.0282677 in 21 cycles,
    /// R1 = 56.0911 mm, R2 = −245.387 mm, RAYE = RAYV = 0 and MTF(0) = 1 on five curves; those
    /// numbers are not asserted here (they are the optimizer's, pinned by its own gates).
    /// </summary>
    [Fact]
    public void TheOpticsScript_ByDirectCall_BuildsOptimisesAndPlotsMtf()
    {
        var catalogs = GlassCatalogLibrary.BuiltInCatalogs;
        var optics = OpticsScripting.WithDefaultMerit(ScriptSinglet(), catalogs);
        var before = OpticsScripting.EvaluateMerit(optics, catalogs);
        Assert.Equal("EFFL", before.Rows[0].Row.Code);       // the wizard's rows follow the existing row
        Assert.True(before.Rows.Count > 1);
        Assert.False(before.HasFailures, string.Join(Environment.NewLine, before.Errors));

        var opt = LocalOptimizer.Run(optics, catalogs);
        Assert.False(opt.Cancelled);
        // The optimizer starts from the cells written back from their values (a radius as
        // 1/(1/R)), so its start merit is the facade's to rounding, not bitwise.
        Assert.Equal(before.Value, opt.StartMerit, before.Value * 1e-9);
        Assert.True(opt.Merit < opt.StartMerit, $"The merit went from {opt.StartMerit} to {opt.Merit}.");
        var after = OpticsScripting.EvaluateMerit(opt.Optimized, catalogs);
        Assert.Equal(opt.Merit, after.Value, opt.Merit * 1e-9);
        Assert.True(Math.Abs(after.Rows[0].Value - 100) < Math.Abs(before.Rows[0].Value - 100),
            $"EFFL went from {before.Rows[0].Value} to {after.Rows[0].Value} mm against a target of 100 mm.");

        var ray = OpticsScripting.TraceRay(opt.Optimized, catalogs, 0, 1, 0, 1, 2);
        Assert.Equal(0, ray.Error);
        Assert.Equal(0, ray.Vignetted);
        Assert.Equal(RayStatus.Ok, ray.Status);

        var table = new ToleranceTable(new[]
        {
            new ToleranceOperandRow { Code = "TRAD", Int1 = 1, Int2 = 0, Min = -0.5, Max = 0.5 },
            new ToleranceOperandRow { Code = "TRAD", Int1 = 2, Int2 = 0, Min = -0.5, Max = 0.5 },
        });
        var tol = ToleranceAnalysis.QuickSensitivity(opt.Optimized, table, catalogs);
        Assert.True(tol.Nominal > 0 && double.IsFinite(tol.Nominal));
        Assert.Equal(2, tol.Rows.Count);
        Assert.All(tol.Rows, r => Assert.True(r.AtMin.Succeeded && r.AtMax.Succeeded, $"row {r.Row}: {r.AtMin.Failure ?? r.AtMax.Failure}"));

        var settings = Assert.IsType<FftMtfSettings>(OpticsScripting.AnalysisSettings(FftMtfAnalysis.SavedId)) with { Sampling = 64 };
        var result = OpticsScripting.Analyze(opt.Optimized, catalogs, FftMtfAnalysis.SavedId, settings);
        var plot = Assert.IsType<LinePlot>(result.Plots[0]);
        // Two fields, tangential and sagittal each, and one diffraction-limit curve (T and S agree
        // on the on-axis reference field, so MtfRuns draws one).
        Assert.Equal(5, plot.Curves.Count);
        foreach (var c in plot.Curves)
        {
            Assert.Equal(0, c.X[0]);
            Assert.True(Math.Abs(c.Y[0] - 1) <= 1e-12, $"{c.Name}: MTF(0) = {c.Y[0]:R}.");
            Assert.All(c.Y, y => Assert.True(y >= 0 && y <= 1 + 1e-12, $"{c.Name}: an MTF value {y:R} outside [0, 1]."));
        }
    }
}
