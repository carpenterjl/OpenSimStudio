using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Core.Studies;
using OpenSim.Geometry;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Si;

namespace OpenSim.Cli;

/// <summary>One parameter of a sweep job file.</summary>
public sealed class SweepParameterSpec
{
    public string Name { get; set; } = "";

    /// <summary>What the parameter sets — see <see cref="ProjectTargets"/> for a project
    /// sweep; a <see cref="LineSpec"/> property name for an impedance sweep.</summary>
    public string Target { get; set; } = "";

    /// <summary>Explicit values; or a nominal with a tolerance.</summary>
    public double[]? Values { get; set; }
    public double? Nominal { get; set; }
    public double? TolerancePercent { get; set; }
    public double? PlusMinus { get; set; }
    public int Points { get; set; } = 3;
    public string? Unit { get; set; }

    /// <summary>Monte Carlo: "Uniform" (default) over the range, or "Normal" about the nominal.</summary>
    public string? Distribution { get; set; }

    /// <summary>Monte Carlo, normal: σ; null takes a sixth of the range (±tolerance = ±3σ).</summary>
    public double? Sigma { get; set; }

    public SweepParameter ToParameter()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("Every sweep parameter needs a name.");
        if (string.IsNullOrWhiteSpace(Target)) throw new InvalidOperationException($"Parameter '{Name}' names no target.");
        var distribution = Distribution is null ? SweepDistribution.Uniform
            : Enum.TryParse<SweepDistribution>(Distribution, true, out var d) ? d
            : throw new InvalidOperationException($"Parameter '{Name}': distribution '{Distribution}' is not Uniform or Normal.");
        SweepParameter p;
        if (Values is { Length: > 0 })
            p = new SweepParameter { Name = Name, Values = Values, NominalValue = Nominal, Unit = Unit };
        else if (Nominal is not { } nominal)
            throw new InvalidOperationException($"Parameter '{Name}' needs either values or a nominal with a tolerance.");
        else if (TolerancePercent is { } pct) p = SweepParameter.Tolerance(Name, nominal, pct / 100, Points, Unit);
        else if (PlusMinus is { } pm) p = SweepParameter.PlusMinus(Name, nominal, pm, Points, Unit);
        else throw new InvalidOperationException($"Parameter '{Name}' has a nominal but no tolerancePercent or plusMinus.");
        return p with { Distribution = distribution, StandardDeviation = Sigma };
    }
}

/// <summary>A board re-import sweep: the net of a Gerber set or IPC-2581 file that each run
/// re-meshes with the swept stackup, the two pads its DC resistance is measured between,
/// and the frequency its trace line is reported at.</summary>
public sealed class BoardSweepSpec
{
    /// <summary>The board: a Gerber ZIP or folder, or an IPC-2581 file.</summary>
    public string Path { get; set; } = "";

    /// <summary>The net's design name (X2 %TO.N / IPC-2581), or "Net N" by id.</summary>
    public string Net { get; set; } = "";

    /// <summary>The source and sink pads: "U1.3" (component.pin), or "x,y" in mm (the nearest pad centre).</summary>
    public string Source { get; set; } = "";
    public string Sink { get; set; } = "";

    /// <summary>Also report the net's trace as a transmission line (Z0, RLGC) over the
    /// board's reference planes at <see cref="FrequencyHz"/>.</summary>
    public bool Rlgc { get; set; } = true;
    public double FrequencyHz { get; set; } = 1e9;

    /// <summary>The net mesh's edge length [m]; 0 = automatic.</summary>
    public double TargetEdgeLength { get; set; }

    /// <summary>The stackup every run starts from; null takes the board file's (IPC-2581) or
    /// the defaults (35 µm copper, 1.6 mm board split evenly across the gaps, FR4).</summary>
    public PcbStackupSettings? Stackup { get; set; }

    public string CopperMaterial { get; set; } = "Copper (annealed)";
}

/// <summary>A sweep job: a project (or a board), an analysis, the parameters and how they combine.</summary>
public sealed class SweepSpec
{
    public string Project { get; set; } = "";
    public string? Analysis { get; set; }
    public string Mode { get; set; } = "OneAtATime";
    public List<SweepParameterSpec> Parameters { get; set; } = new();

    /// <summary>Monte Carlo: the number of random points and the generator's seed.</summary>
    public int Samples { get; set; } = 100;
    public int Seed { get; set; } = 1;

    /// <summary>Outputs to keep (summary names, or "Max &lt;field&gt; (&lt;unit&gt;)"); null keeps every one.</summary>
    public List<string>? Outputs { get; set; }

    /// <summary>Monte Carlo: [low, high] per output; the report gives the fraction of samples inside.</summary>
    public Dictionary<string, double[]>? Limits { get; set; }

    /// <summary>A board re-import sweep in place of a project.</summary>
    public BoardSweepSpec? Board { get; set; }

    public StudySettings? Settings { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SweepSpec Load(string path)
    {
        var spec = JsonSerializer.Deserialize<SweepSpec>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException($"'{path}' is not a sweep job.");
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        if (spec.Board is { } board)
        {
            if (string.IsNullOrWhiteSpace(board.Path)) throw new InvalidDataException("The board sweep names no board path.");
            if (string.IsNullOrWhiteSpace(board.Net)) throw new InvalidDataException("The board sweep names no net.");
            if (string.IsNullOrWhiteSpace(board.Source) || string.IsNullOrWhiteSpace(board.Sink))
                throw new InvalidDataException("The board sweep needs a source and a sink pad (\"U1.3\" or \"x,y\" in mm).");
            if (!Path.IsPathRooted(board.Path)) board.Path = Path.GetFullPath(Path.Combine(dir, board.Path));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(spec.Project)) throw new InvalidDataException("The sweep job names no project (or board).");
            if (!Path.IsPathRooted(spec.Project)) spec.Project = Path.GetFullPath(Path.Combine(dir, spec.Project));
        }
        if (spec.Parameters.Count == 0) throw new InvalidDataException("The sweep job has no parameters.");
        if (spec.Limits is { } limits)
            foreach (var (output, band) in limits)
                if (band is not { Length: 2 } || !(band[0] <= band[1]))
                    throw new InvalidDataException($"The limit for '{output}' must be [low, high].");
        return spec;
    }

    public SweepPlan Plan() => ParsePlan(Parameters, Mode, Samples, Seed);

    internal static SweepPlan ParsePlan(IEnumerable<SweepParameterSpec> parameters, string mode, int samples, int seed) => new()
    {
        Parameters = parameters.Select(p => p.ToParameter()).ToList(),
        Mode = Enum.TryParse<SweepMode>(mode, true, out var m) ? m
            : throw new InvalidOperationException($"Unknown sweep mode '{mode}'; use FullFactorial, OneAtATime, Corners or MonteCarlo."),
        Samples = samples,
        Seed = seed
    };
}

/// <summary>
/// The things a board sweep can set, named as <c>stackup.&lt;property&gt;</c>:
/// <c>copperThickness</c> (every layer not listed by itself), <c>layer&lt;n&gt;.thickness</c>,
/// <c>boardThickness</c> (split evenly across unlisted gaps), <c>gap&lt;n&gt;.thickness|permittivity|lossTangent</c>
/// (gap n lies under copper layer n), <c>viaPlating</c>; and <c>mesh.targetEdgeLength</c>,
/// <c>settings.frequency</c>. Anything else goes to <see cref="ProjectTargets"/> on the
/// run's one-body project (a material property, a condition value).
/// </summary>
public static class BoardTargets
{
    public static bool Handles(string target)
    {
        string kind = target.Split('.', ':')[0].Trim().ToLowerInvariant();
        return kind == "stackup" || (kind == "mesh" && !target.Contains(':')) ||
               target.Equals("settings.frequency", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applies one value; returns the changed mesh options and frequency.</summary>
    public static (NetMeshOptions Options, double FrequencyHz) Apply(NetMeshOptions options, double frequencyHz,
        string target, double value)
    {
        var parts = target.Split('.');
        string kind = parts[0].Trim().ToLowerInvariant();
        if (kind == "settings" && parts.Length == 2 && parts[1].Equals("frequency", StringComparison.OrdinalIgnoreCase))
            return (options, value > 0 ? value : throw new InvalidOperationException("settings.frequency must be positive."));
        if (kind == "mesh" && parts.Length == 2 && parts[1].Equals("targetEdgeLength", StringComparison.OrdinalIgnoreCase))
            return (options with { TargetEdgeLength = value }, frequencyHz);
        if (kind != "stackup")
            throw new InvalidOperationException($"Target '{target}' is not a board target.");
        if (parts.Length == 2)
        {
            options = parts[1].ToLowerInvariant() switch
            {
                "copperthickness" => options with { CopperThickness = Positive(target, value) },
                "boardthickness" => options with { DefaultDielectricThickness = Positive(target, value) },
                "viaplating" => options with { ViaPlatingThickness = Positive(target, value) },
                _ => throw new InvalidOperationException(
                    $"Stackup target '{parts[1]}' is not known; use copperThickness, boardThickness, viaPlating, layer<n>.thickness or gap<n>.thickness|permittivity|lossTangent.")
            };
            return (options, frequencyHz);
        }
        if (parts.Length != 3) throw new InvalidOperationException($"Target '{target}' is not stackup.<property> or stackup.<layer|gap><n>.<property>.");
        string which = parts[1].ToLowerInvariant();
        string property = parts[2].ToLowerInvariant();
        bool layer = which.StartsWith("layer"), gap = which.StartsWith("gap");
        if (!layer && !gap || !int.TryParse(which[(layer ? 5 : 3)..], out int n) || n < 1)
            throw new InvalidOperationException($"Target '{target}': expected layer<n> or gap<n> with n ≥ 1.");
        if (layer)
        {
            if (property != "thickness") throw new InvalidOperationException($"Layer target '{parts[2]}' is not known; use thickness.");
            return (options with { LayerThickness = With(options.LayerThickness, n, Positive(target, value)) }, frequencyHz);
        }
        options = property switch
        {
            "thickness" => options with { DielectricGapThickness = With(options.DielectricGapThickness, n, Positive(target, value)) },
            "permittivity" => options with { DielectricGapPermittivity = With(options.DielectricGapPermittivity, n, Positive(target, value)) },
            "losstangent" => options with { DielectricGapLossTangent = With(options.DielectricGapLossTangent, n, value >= 0 ? value : throw new InvalidOperationException($"{target} must not be negative.")) },
            _ => throw new InvalidOperationException($"Gap target '{parts[2]}' is not known; use thickness, permittivity or lossTangent.")
        };
        return (options, frequencyHz);
    }

    private static double Positive(string target, double value) =>
        value > 0 ? value : throw new InvalidOperationException($"{target} must be positive.");

    private static Dictionary<int, double> With(IReadOnlyDictionary<int, double>? map, int key, double value)
    {
        var copy = map is null ? new Dictionary<int, double>() : new Dictionary<int, double>(map);
        copy[key] = value;
        return copy;
    }

    /// <summary>The mesh options a stackup record stands for: per-layer copper, per-gap
    /// thickness and materials indexed by (upper layer order − 1) as the project stores them.</summary>
    public static NetMeshOptions ToMeshOptions(PcbStackupSettings stackup, int copperLayers, double targetEdgeLength,
        string copperMaterial) => new()
    {
        TargetEdgeLength = targetEdgeLength,
        CopperThickness = stackup.CopperThickness,
        CopperMaterialName = copperMaterial,
        LayerThickness = stackup.CopperLayerThicknesses.Select((t, i) => (i + 1, t)).ToDictionary(x => x.Item1, x => x.Item2),
        DefaultDielectricThickness = stackup.BoardThickness /
                                     Math.Max(1, stackup.DielectricGapThicknesses.Count > 0 ? stackup.DielectricGapThicknesses.Count : copperLayers - 1),
        DielectricGapThickness = stackup.DielectricGapThicknesses.Select((t, i) => (i + 1, t)).ToDictionary(x => x.Item1, x => x.Item2),
        DielectricGapPermittivity = stackup.DielectricGapPermittivities.Select((e, i) => (i + 1, e)).ToDictionary(x => x.Item1, x => x.Item2),
        DielectricGapLossTangent = stackup.DielectricGapLossTangents.Select((d, i) => (i + 1, d)).ToDictionary(x => x.Item1, x => x.Item2)
    };
}

/// <summary>
/// The things a project sweep can set, named as <c>kind:name.property</c>:
/// <list type="bullet">
/// <item><c>condition:&lt;name&gt;.value</c> — the condition's value (volts, amps, kelvin,
/// watts, film coefficient, pressure, force magnitude); <c>.ambient</c> for a convection's ambient.</item>
/// <item><c>body:&lt;name&gt;.heatSourcePower</c></item>
/// <item><c>material:&lt;name&gt;.&lt;property&gt;</c> — thermalConductivity, specificHeat,
/// electricalConductivity, relativePermittivity, relativePermeability, density, youngsModulus,
/// poissonRatio, emissivity, dielectricStrength, resistivityTemperatureCoefficient.</item>
/// <item><c>mesh:&lt;body&gt;.targetEdgeLength</c> — re-meshes the body.</item>
/// <item><c>geometry:&lt;body&gt;.sizeX|sizeY|sizeZ</c> — a box body's dimension;
/// <c>.radius|height</c> — a cylinder body's; <c>.scale|scaleX|scaleY|scaleZ</c> — any
/// geometry scaled about the corner of its bounding box nearest the origin (a primitive
/// stays where it was built). Each re-meshes.</item>
/// <item><c>environment.ambientTemperature</c></item>
/// <item><c>settings.duration|timeStep|initialTemperature|tolerance|minFrequency|maxFrequency|pointCount|modeCount</c></item>
/// </list>
/// </summary>
public static class ProjectTargets
{
    /// <summary>Applies one value to a freshly loaded project. Returns the settings to use
    /// (changed when the target is a setting) and records material overrides.</summary>
    public static StudySettings Apply(SimProject project, string target, double value, StudySettings settings,
        Dictionary<string, Material> materialOverrides)
    {
        int colon = target.IndexOf(':');
        string kind = (colon < 0 ? target.Split('.')[0] : target[..colon]).Trim().ToLowerInvariant();
        string rest = colon < 0 ? target[(target.IndexOf('.') + 1)..] : target[(colon + 1)..];
        int dot = rest.LastIndexOf('.');
        string name = dot < 0 ? rest : rest[..dot];
        string property = (dot < 0 ? "" : rest[(dot + 1)..]).Trim();

        switch (kind)
        {
            case "condition":
            {
                bool found = false;
                foreach (var body in project.Bodies)
                    for (int i = 0; i < body.BoundaryConditions.Count; i++)
                    {
                        var bc = body.BoundaryConditions[i];
                        if (!string.Equals(bc.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                        body.BoundaryConditions[i] = SetCondition(bc, property, value);
                        found = true;
                    }
                if (!found) throw new InvalidOperationException($"No boundary condition named '{name}'.");
                return settings;
            }
            case "body":
            {
                var body = Body(project, name);
                if (!property.Equals("heatSourcePower", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Body target '{property}' is not known; use heatSourcePower.");
                body.HeatSourcePower = value;
                return settings;
            }
            case "material":
            {
                var baseMaterial = materialOverrides.GetValueOrDefault(name)
                                   ?? project.Bodies.Select(b => b.Material).FirstOrDefault(m => m is not null && m.Name == name)
                                   ?? new MaterialLibrary().Materials.FirstOrDefault(m => m.Name == name)
                                   ?? throw new InvalidOperationException($"No material named '{name}' in the project or the library.");
                var changed = SetMaterial(baseMaterial, property, value);
                materialOverrides[name] = changed;
                foreach (var body in project.Bodies)
                    if (body.Material?.Name == name) body.Material = changed;
                return settings;
            }
            case "mesh":
            {
                var body = Body(project, name);
                if (!property.Equals("targetEdgeLength", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Mesh target '{property}' is not known; use targetEdgeLength.");
                body.MeshSettings = body.MeshSettings with { TargetEdgeLength = value, Divisions = null };
                body.Mesh = null;
                return settings;
            }
            case "geometry":
            {
                var body = Body(project, name);
                var geometry = body.Geometry ?? throw new InvalidOperationException($"Body '{name}' has no geometry.");
                body.Geometry = SetGeometry(name, geometry, property, value);
                body.Mesh = null;
                return settings;
            }
            case "environment":
            {
                var env = project.Environment ?? new EnvironmentSettings();
                project.Environment = property.ToLowerInvariant() switch
                {
                    "ambienttemperature" => env with { AmbientTemperature = value },
                    _ => throw new InvalidOperationException($"Environment target '{property}' is not known; use ambientTemperature.")
                };
                return settings;
            }
            case "settings":
                return property.ToLowerInvariant() switch
                {
                    "duration" => settings with { Duration = value },
                    "timestep" => settings with { TimeStep = value },
                    "initialtemperature" => settings with { InitialTemperature = value },
                    "tolerance" => settings with { Tolerance = value },
                    "minfrequency" => settings with { MinFrequency = value },
                    "maxfrequency" => settings with { MaxFrequency = value },
                    "pointcount" => settings with { PointCount = (int)Math.Round(value) },
                    "modecount" => settings with { ModeCount = (int)Math.Round(value) },
                    _ => throw new InvalidOperationException($"Setting '{property}' is not known.")
                };
            default:
                throw new InvalidOperationException(
                    $"Target '{target}' is not understood. Use condition:, body:, material:, mesh:, geometry:, environment. or settings.");
        }
    }

    private static Body Body(SimProject project, string name) =>
        project.Bodies.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"No body named '{name}'. Bodies: {string.Join(", ", project.Bodies.Select(b => b.Name))}.");

    /// <summary>A box's size, a cylinder's radius or height, or a scale of any geometry.</summary>
    internal static TriangleMesh SetGeometry(string bodyName, TriangleMesh geometry, string property, double value)
    {
        string key = property.ToLowerInvariant();
        var bounds = Aabb.FromPoints(geometry.Vertices);
        var size = bounds.Max - bounds.Min;
        switch (key)
        {
            case "sizex" or "sizey" or "sizez":
            {
                if (geometry.Vertices.Count != 8 || geometry.Triangles.Count != 12)
                    throw new InvalidOperationException(
                        $"Body '{bodyName}' is not a box primitive ({geometry.Vertices.Count} vertices); use radius|height for a cylinder or scale for any geometry.");
                double sx = key == "sizex" ? value : size.X, sy = key == "sizey" ? value : size.Y, sz = key == "sizez" ? value : size.Z;
                return PrimitiveFactory.CreateBox(sx, sy, sz);
            }
            case "radius" or "height":
            {
                var cylinder = AsCylinder(geometry)
                               ?? throw new InvalidOperationException(
                                   $"Body '{bodyName}' is not a cylinder primitive ({geometry.Vertices.Count} vertices); use sizeX|Y|Z for a box or scale for any geometry.");
                double r = key == "radius" ? value : cylinder.Radius, h = key == "height" ? value : cylinder.Height;
                return PrimitiveFactory.CreateCylinder(r, h, cylinder.Segments);
            }
            case "scale" or "scalex" or "scaley" or "scalez":
            {
                if (!(value > 0)) throw new InvalidOperationException($"A scale must be positive, not {value}.");
                var s = new Vector3D(key is "scale" or "scalex" ? value : 1, key is "scale" or "scaley" ? value : 1, key is "scale" or "scalez" ? value : 1);
                var origin = bounds.Min;
                var vertices = geometry.Vertices.Select(v => new Vector3D(
                    origin.X + (v.X - origin.X) * s.X, origin.Y + (v.Y - origin.Y) * s.Y, origin.Z + (v.Z - origin.Z) * s.Z)).ToList();
                return new TriangleMesh(vertices, geometry.Triangles, geometry.TriangleFaceIds);
            }
            default:
                throw new InvalidOperationException(
                    $"Geometry target '{property}' is not known; use sizeX|sizeY|sizeZ (box), radius|height (cylinder) or scale|scaleX|scaleY|scaleZ.");
        }
    }

    /// <summary>Recognises <see cref="PrimitiveFactory.CreateCylinder"/>'s layout — two rings of n
    /// vertices and two centres, 4n triangles on faces 0, 1, 2, the rings at one radius about the
    /// axis — and reads its radius, height and segment count.</summary>
    private static (double Radius, double Height, int Segments)? AsCylinder(TriangleMesh geometry)
    {
        int n = (geometry.Vertices.Count - 2) / 2;
        if (n < 3 || geometry.Vertices.Count != 2 * n + 2 || geometry.Triangles.Count != 4 * n) return null;
        if (geometry.TriangleFaceIds.Any(f => f is < 0 or > 2)) return null;
        var bottom = geometry.Vertices[2 * n];
        var top = geometry.Vertices[2 * n + 1];
        double height = top.Z - bottom.Z;
        if (!(height > 0) || bottom.X != top.X || bottom.Y != top.Y) return null;
        double radius = Math.Sqrt(Math.Pow(geometry.Vertices[0].X - bottom.X, 2) + Math.Pow(geometry.Vertices[0].Y - bottom.Y, 2));
        if (!(radius > 0)) return null;
        double tol = 1e-9 * Math.Max(radius, height);
        for (int i = 0; i < 2 * n; i++)
        {
            var v = geometry.Vertices[i];
            double r = Math.Sqrt(Math.Pow(v.X - bottom.X, 2) + Math.Pow(v.Y - bottom.Y, 2));
            double z = i < n ? bottom.Z : top.Z;
            if (Math.Abs(r - radius) > tol || Math.Abs(v.Z - z) > tol) return null;
        }
        return (radius, height, n);
    }

    private static BoundaryCondition SetCondition(BoundaryCondition bc, string property, double value)
    {
        bool ambient = property.Equals("ambient", StringComparison.OrdinalIgnoreCase);
        if (!ambient && property.Length > 0 && !property.Equals("value", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Condition target '{property}' is not known; use value or ambient.");
        return bc switch
        {
            Convection c when ambient => c with { AmbientTemperature = value },
            Convection c => c with { Coefficient = value },
            _ when ambient => throw new InvalidOperationException($"'{bc.Name}' is not a convection; it has no ambient."),
            VoltagePotential v => v with { Volts = value },
            CurrentFlow i => i with { TotalCurrent = value },
            ElectricCharge q => q with { TotalCharge = value },
            FixedTemperature t => t with { Kelvin = value },
            HeatFlux q => q with { TotalPower = value },
            PressureLoad p => p with { Magnitude = value },
            ForceLoad f => f with
            {
                TotalForce = f.TotalForce.Length > 0 ? f.TotalForce.Normalized() * value : new Vector3D(0, 0, -value)
            },
            _ => throw new InvalidOperationException($"'{bc.Name}' ({bc.GetType().Name}) has no value to sweep.")
        };
    }

    private static Material SetMaterial(Material m, string property, double value) => property.ToLowerInvariant() switch
    {
        "thermalconductivity" => m with { ThermalConductivity = value },
        "specificheat" => m with { SpecificHeat = value },
        "electricalconductivity" => m with { ElectricalConductivity = value },
        "relativepermittivity" => m with { RelativePermittivity = value },
        "relativepermeability" => m with { RelativePermeability = value },
        "density" => m with { Density = value },
        "youngsmodulus" => m with { YoungsModulus = value },
        "poissonratio" => m with { PoissonRatio = value },
        "emissivity" => m with { Emissivity = value },
        "dielectricstrength" => m with { DielectricStrength = value },
        "resistivitytemperaturecoefficient" => m with { ResistivityTemperatureCoefficient = value },
        _ => throw new InvalidOperationException($"Material property '{property}' is not known.")
    };
}

/// <summary>An impedance-calculator job: a line, optionally swept over its own properties.</summary>
public sealed class ImpedanceSpec
{
    public LineSpec? Line { get; set; }

    /// <summary>A copper foil by name from the <see cref="FoilCatalog"/> ("Circuit Foil BF-TZA"):
    /// its data-sheet Rz for the line's copper thickness becomes the line's roughness by the
    /// Cannonball stack. Null is the line's own model (smooth unless "model" says otherwise).</summary>
    public string? Foil { get; set; }

    /// <summary>A foils file (JSON array of foils) merged into the catalog before the lookup;
    /// relative to the job file.</summary>
    public string? Foils { get; set; }

    public string Mode { get; set; } = "OneAtATime";
    public List<SweepParameterSpec>? Parameters { get; set; }
    public int PanelsPerTrace { get; set; } = 48;

    /// <summary>Monte Carlo: the number of random points, the seed, and [low, high] per output for the yield.</summary>
    public int Samples { get; set; } = 100;
    public int Seed { get; set; } = 1;
    public Dictionary<string, double[]>? Limits { get; set; }

    public static ImpedanceSpec Load(string path)
    {
        var spec = JsonSerializer.Deserialize<ImpedanceSpec>(File.ReadAllText(path), SweepSpec.JsonOptions)
                   ?? throw new InvalidDataException($"'{path}' is not an impedance job.");
        if (spec.Line is null) throw new InvalidDataException("The impedance job has no 'line'.");
        return spec;
    }

    /// <summary>The line with one property set by name (case-insensitive), e.g. WidthMeters.</summary>
    public static LineSpec With(LineSpec line, string property, double value)
    {
        var info = typeof(LineSpec).GetProperty(property, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                   ?? throw new InvalidOperationException(
                       $"LineSpec has no property '{property}'. Numeric ones: " + string.Join(", ",
                           typeof(LineSpec).GetProperties().Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(double?))
                               .Select(p => p.Name)));
        var copy = line with { };
        if (info.PropertyType == typeof(double)) info.SetValue(copy, value);
        else if (info.PropertyType == typeof(double?)) info.SetValue(copy, (double?)value);
        else throw new InvalidOperationException($"LineSpec.{info.Name} is not a number.");
        return copy;
    }

    /// <summary>The numbers a sweep collects from one line report.</summary>
    public static IReadOnlyDictionary<string, double> Outputs(LineReport report)
    {
        var o = new Dictionary<string, double> { ["Z0 (Ω)"] = report.ImpedanceOhms };
        if (report.DifferentialOhms is { } zd) o["Z_diff (Ω)"] = zd;
        if (report.CommonOhms is { } zc) o["Z_common (Ω)"] = zc;
        var mode = report.Modes[0];
        o["eps_eff"] = mode.EffectivePermittivity;
        o["Delay (ps/in)"] = mode.DelaySecondsPerMeter * 1e12 * 0.0254;
        o["Loss (dB/in)"] = mode.LossDbPerMeter * 0.0254;
        o["Conductor loss (dB/in)"] = mode.ConductorLossDbPerMeter * 0.0254;
        o["R (Ω/m)"] = mode.ResistanceOhmsPerMeter;
        o["L (nH/m)"] = mode.InductanceHenriesPerMeter * 1e9;
        o["C (pF/m)"] = mode.CapacitanceFaradsPerMeter * 1e12;
        if (report.NearEndCoupling is { } near) o["Near-end crosstalk (%)"] = near * 100;
        return o;
    }
}
