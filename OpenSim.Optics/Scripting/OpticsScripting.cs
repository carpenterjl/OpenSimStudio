using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Scripting;

// OP-98: the scripting surface's facade (docs/Scripting-from-JGraph.md, "Optics";
// docs/Optics-Conventions.md §47). Everything else a script calls is the library's own public
// types, pinned by OpenSim.Tests/Cli/ScriptingSurfaceTests.cs; these few members exist where a
// script would otherwise repeat several internal-feeling steps.

/// <summary>
/// One real ray traced by <see cref="OpticsScripting.TraceRay"/>, with the error codes of
/// OpticStudio's ZPL ray trace (help, "RAYTRACE", 2025 R2, saved under
/// plans/sources/optics/op-98-help/): <see cref="Error"/> is RAYE, <see cref="Vignetted"/> is RAYV.
/// </summary>
public sealed record ScriptRay
{
    /// <summary>The ray surface by surface, in meters as every trace is (not in lens units;
    /// docs/Optics-Conventions.md §1).</summary>
    public required RayPath Path { get; init; }

    /// <summary>RAYE: 0 when the ray reached the image; +n when it missed surface n; −n when it
    /// was totally internally reflected at surface n; −9999 when it could not be launched (the
    /// ray aimer did not converge). An iteration that did not converge at surface n, or an
    /// evanescent diffraction order there, is reported as +n (the help names no code for them;
    /// this program's choice, §47).</summary>
    public required int Error { get; init; }

    /// <summary>RAYV: the surface at which the ray was vignetted, or 0.</summary>
    public required int Vignetted { get; init; }

    /// <summary>Why the ray stopped (<see cref="RayStatus.Ok"/> when it reached the image).</summary>
    public RayStatus Status => Path.Status;

    /// <summary>The launch problem when <see cref="Error"/> is −9999, else null.</summary>
    public string? LaunchFailure { get; init; }
}

/// <summary>
/// Entry points for scripts (JGraph through its .NET interop, Python through pythonnet) that
/// drive the optics model in-process: load a project's lens, find its glass catalogs, run any
/// analysis of the registry by id, write the default merit function, evaluate the merit
/// function and trace one ray. Optimization, tolerancing and the editors' data are the library's
/// own public types (<see cref="Optimization.LocalOptimizer"/>,
/// <see cref="Tolerancing.ToleranceAnalysis"/>, <see cref="OpticsSettings"/>), listed in
/// docs/Scripting-from-JGraph.md. Every member fails loudly, naming what is wrong.
/// </summary>
public static class OpticsScripting
{
    /// <summary>The optics record of the project saved at <paramref name="projectPath"/>
    /// (<see cref="SimProject.Optics"/>). Throws <see cref="InvalidOperationException"/> when the
    /// project has no optics system.</summary>
    public static OpticsSettings Load(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var project = new ProjectSerializer().Load(projectPath);
        return project.Optics
            ?? throw new InvalidOperationException($"The project '{projectPath}' has no optics system: open it in the Optics workspace and save it with a lens first.");
    }

    /// <summary>
    /// The glass catalogs <paramref name="system"/> draws from: the user's glass library (the
    /// built-in catalogs plus the catalog folder, as the application loads it), narrowed to the
    /// system's active catalogs in their order, or every catalog when it names none
    /// (<see cref="GlassLookup.ActiveCatalogs"/>). Throws <see cref="InvalidOperationException"/>
    /// when the system names active catalogs of which none is loaded.
    /// </summary>
    public static IReadOnlyList<OpticalCatalog> Catalogs(OpticsSettings system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var library = new GlassCatalogLibrary();
        var names = system.Materials?.ActiveCatalogs;
        var active = GlassLookup.ActiveCatalogs(library.Catalogs, names);
        if (names is { Count: > 0 } && active.Count == 0)
            throw new InvalidOperationException(
                $"None of the system's active glass catalogs ({string.Join(", ", names)}) is loaded from '{library.CatalogFolder}'.");
        return active;
    }

    /// <summary>The ids of every analysis (<see cref="OpticsAnalysisRegistry.CreateDefault"/>),
    /// in the registry's order.</summary>
    public static IReadOnlyList<string> AnalysisIds() =>
        OpticsAnalysisRegistry.CreateDefault().All.Select(a => a.Id).ToArray();

    /// <summary>A new settings record of analysis <paramref name="id"/> holding its defaults;
    /// set its properties and pass it to <see cref="Analyze"/>.</summary>
    public static object AnalysisSettings(string id) => Find(id).CreateDefaultSettings();

    /// <summary>
    /// Runs analysis <paramref name="id"/> on <paramref name="system"/> (its current
    /// configuration, as every analysis computes) with the glass catalogs
    /// <paramref name="catalogs"/> and <paramref name="settings"/> (null: the analysis's
    /// defaults). The result carries the text, the plots (<see cref="LinePlot"/>,
    /// <see cref="AnalysisScatterPlot"/>, <see cref="AnalysisMapPlot"/>) and the assumptions.
    /// Throws what the analysis throws (an <see cref="ArgumentException"/> or
    /// <see cref="InvalidOperationException"/> naming the surface, field or setting).
    /// </summary>
    public static OpticsAnalysisResult Analyze(OpticsSettings system, IReadOnlyList<OpticalCatalog> catalogs, string id, object? settings = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(catalogs);
        var analysis = Find(id);
        var context = new OpticsAnalysisContext("Script", system) { Catalogs = catalogs };
        return analysis.Compute(context, settings ?? analysis.CreateDefaultSettings(), CancellationToken.None);
    }

    /// <summary>
    /// <paramref name="system"/> with the Optimization Wizard's default merit function
    /// (<see cref="DefaultMeritWizard.Generate"/>, docs/Optics-Conventions.md §40) inserted into
    /// its merit function at <see cref="DefaultMeritOptions.StartAt"/> (null: the row after DMFS,
    /// else the end), as the Merit Function Editor's wizard does. Throws
    /// <see cref="ArgumentException"/> naming invalid options, a configuration that cannot be
    /// built, or a saved row this version does not know.
    /// </summary>
    public static OpticsSettings WithDefaultMerit(OpticsSettings system, IReadOnlyList<OpticalCatalog> catalogs, DefaultMeritOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new DefaultMeritOptions();
        var registry = MeritOperandRegistry.CreateDefault();
        var existing = SavedMerit(system, registry);
        var configurations = new ProjectMeritConfigurations(system, catalogs);
        int current = Math.Clamp(MultiConfiguration.CurrentConfiguration(system), 1, configurations.Count);
        var generated = DefaultMeritWizard.Generate(configurations, current, options);
        int startAt = options.StartAt ?? DefaultMeritWizard.DefaultStartAt(existing);
        var merged = DefaultMeritWizard.Insert(existing, generated.Rows, startAt, registry);
        return system with { MeritFunction = MeritFunctionSettings.ToSettings(merged) };
    }

    /// <summary>
    /// The saved merit function of <paramref name="system"/> evaluated over every configuration
    /// (<see cref="MeritEvaluator.Evaluate"/>): <see cref="MeritEvaluation.Value"/> is the merit
    /// value, <see cref="MeritEvaluation.Rows"/> each row's value and contribution. A row that
    /// cannot be computed fails alone and is named in <see cref="MeritEvaluation.Errors"/>.
    /// Throws <see cref="ArgumentException"/> for an empty or invalid merit function.
    /// </summary>
    public static MeritEvaluation EvaluateMerit(OpticsSettings system, IReadOnlyList<OpticalCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(catalogs);
        var registry = MeritOperandRegistry.CreateDefault();
        var function = SavedMerit(system, registry);
        if (function.Rows.Count == 0)
            throw new ArgumentException("The merit function has no rows: build one (WithDefaultMerit, or rows saved with MeritFunctionSettings.ToSettings) first.");
        return MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(system, catalogs), registry);
    }

    /// <summary>
    /// One real ray of normalised field (<paramref name="hx"/>, <paramref name="hy"/>) and pupil
    /// (<paramref name="px"/>, <paramref name="py"/>) at wavelength row
    /// <paramref name="wavelength"/> (1 for the first; 0 the primary), through the current
    /// configuration of <paramref name="system"/>, as the Single Ray Trace analysis traces it,
    /// with OpticStudio's RAYE and RAYV codes (<see cref="ScriptRay"/>). Throws
    /// <see cref="ArgumentException"/> when the system has no surfaces, a wavelength row that
    /// does not exist, or a lens that cannot be built.
    /// </summary>
    public static ScriptRay TraceRay(OpticsSettings system, IReadOnlyList<OpticalCatalog> catalogs,
        double hx, double hy, double px, double py, int wavelength = 0)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(catalogs);
        var current = system.MultiConfiguration is null ? system : MultiConfiguration.CurrentSettings(system, catalogs);
        var lens = ProjectLens.From(current, catalogs)
            ?? throw new ArgumentException("The system has no surfaces: add them (OpticsSettings.Surfaces) so there is something to trace.");
        var notes = new List<string>();
        var data = SystemDataSettings.FromSettings(current.SystemData, notes);
        int count = data.Wavelengths.Wavelengths.Count;
        if (wavelength < 0 || wavelength > count)
            throw new ArgumentException($"Trace ray: the wavelength row must be 0 (primary) or 1 to {count} (it is {wavelength}).");
        int? row = wavelength == 0 ? null : wavelength - 1;
        IReadOnlyList<double>? semiDiameters = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semiDiameters = SemiDiameterSizing.Compute(lens, data).Clear;
        var trace = SequentialTrace.Create(lens, data, row, semiDiameters);
        var aimed = new RayLauncher(lens, data).LaunchAimed(trace, hx, hy, px, py);
        var path = trace.TracePath(aimed.Ray);
        if (aimed.Failure is { } failure)
            return new ScriptRay { Path = path, Error = -9999, Vignetted = 0, LaunchFailure = failure };
        int at = path.FailedAtSurface;
        return path.Status switch
        {
            RayStatus.Ok => new ScriptRay { Path = path, Error = 0, Vignetted = 0 },
            RayStatus.Vignetted => new ScriptRay { Path = path, Error = 0, Vignetted = at },
            RayStatus.TotalInternalReflection => new ScriptRay { Path = path, Error = -at, Vignetted = 0 },
            _ => new ScriptRay { Path = path, Error = at, Vignetted = 0 },   // missed; no convergence and evanescent: §47
        };
    }

    private static IOpticsAnalysis Find(string id)
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        if (registry.TryGet(id, out var analysis)) return analysis;
        throw new ArgumentException($"There is no analysis '{id}'; OpticsScripting.AnalysisIds() lists the {registry.All.Count} there are.");
    }

    private static MeritFunction SavedMerit(OpticsSettings system, MeritOperandRegistry registry)
    {
        var notes = new List<string>();
        var function = MeritFunctionSettings.FromSettings(system.MeritFunction, registry, notes);
        if (notes.Count > 0)
            throw new ArgumentException("The saved merit function has rows this version cannot read:" + Environment.NewLine + string.Join(Environment.NewLine, notes));
        return function;
    }
}
