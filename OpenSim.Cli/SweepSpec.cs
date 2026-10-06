using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Core.Studies;
using OpenSim.Geometry;
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

    public SweepParameter ToParameter()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("Every sweep parameter needs a name.");
        if (string.IsNullOrWhiteSpace(Target)) throw new InvalidOperationException($"Parameter '{Name}' names no target.");
        if (Values is { Length: > 0 })
            return new SweepParameter { Name = Name, Values = Values, NominalValue = Nominal, Unit = Unit };
        if (Nominal is not { } nominal)
            throw new InvalidOperationException($"Parameter '{Name}' needs either values or a nominal with a tolerance.");
        if (TolerancePercent is { } pct) return SweepParameter.Tolerance(Name, nominal, pct / 100, Points, Unit);
        if (PlusMinus is { } pm) return SweepParameter.PlusMinus(Name, nominal, pm, Points, Unit);
        throw new InvalidOperationException($"Parameter '{Name}' has a nominal but no tolerancePercent or plusMinus.");
    }
}

/// <summary>A sweep job: a project, an analysis, the parameters and how they combine.</summary>
public sealed class SweepSpec
{
    public string Project { get; set; } = "";
    public string? Analysis { get; set; }
    public string Mode { get; set; } = "OneAtATime";
    public List<SweepParameterSpec> Parameters { get; set; } = new();

    /// <summary>Outputs to keep (summary names, or "Max &lt;field&gt; (&lt;unit&gt;)"); null keeps every one.</summary>
    public List<string>? Outputs { get; set; }

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
        if (string.IsNullOrWhiteSpace(spec.Project)) throw new InvalidDataException("The sweep job names no project.");
        if (!Path.IsPathRooted(spec.Project))
            spec.Project = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", spec.Project));
        if (spec.Parameters.Count == 0) throw new InvalidDataException("The sweep job has no parameters.");
        return spec;
    }

    public SweepPlan Plan() => new()
    {
        Parameters = Parameters.Select(p => p.ToParameter()).ToList(),
        Mode = Enum.TryParse<SweepMode>(Mode, true, out var mode) ? mode
            : throw new InvalidOperationException($"Unknown sweep mode '{Mode}'; use FullFactorial, OneAtATime or Corners.")
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
/// <item><c>geometry:&lt;body&gt;.sizeX|sizeY|sizeZ</c> — a box body's dimension; re-meshes.</item>
/// <item><c>environment.ambientTemperature</c></item>
/// <item><c>settings.duration|timeStep|initialTemperature|minFrequency|maxFrequency</c></item>
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
                if (geometry.Vertices.Count != 8 || geometry.Triangles.Count != 12)
                    throw new InvalidOperationException(
                        $"Body '{name}' is not a box primitive ({geometry.Vertices.Count} vertices); only a box's size can be swept.");
                var bounds = Aabb.FromPoints(geometry.Vertices);
                var size = bounds.Max - bounds.Min;
                double sx = size.X, sy = size.Y, sz = size.Z;
                switch (property.ToLowerInvariant())
                {
                    case "sizex": sx = value; break;
                    case "sizey": sy = value; break;
                    case "sizez": sz = value; break;
                    default: throw new InvalidOperationException($"Geometry target '{property}' is not known; use sizeX, sizeY or sizeZ.");
                }
                body.Geometry = PrimitiveFactory.CreateBox(sx, sy, sz);
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
                    "minfrequency" => settings with { MinFrequency = value },
                    "maxfrequency" => settings with { MaxFrequency = value },
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
    public string Mode { get; set; } = "OneAtATime";
    public List<SweepParameterSpec>? Parameters { get; set; }
    public int PanelsPerTrace { get; set; } = 48;

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
