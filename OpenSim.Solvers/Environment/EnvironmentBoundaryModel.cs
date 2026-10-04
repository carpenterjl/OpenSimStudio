using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers.Environment;

/// <summary>
/// Turns an <see cref="EnvironmentSettings"/> and a mesh into a <see cref="SurfaceFilmModel"/>
/// that depends on the current surface temperature: it splits the exposed skin into panels,
/// classifies each one, and evaluates the convection correlations plus the factored
/// radiation coefficient on it.
/// <para>
/// <b>Precedence.</b> A geometric face carrying ANY user thermal boundary condition is
/// wholly owned by the user — no environment convection and no environment radiation is
/// added to it. Merging the two would silently double up the surface exchange on exactly
/// the face a user chose to specify by hand, and there is no way to display that.
/// </para>
/// <para>
/// <b>Contacts.</b> The part of the skin a thermal contact couples faces the other body,
/// not the surroundings: it carries the contact conductance only. Each triangle's film is
/// scaled by its exposed fraction, a wholly buried triangle is left out of its panel, and
/// panel areas and length scales are those of the exposed part.
/// </para>
/// </summary>
public sealed class EnvironmentBoundaryModel
{
    /// <summary>
    /// The floor on the natural-convection Nusselt number.
    /// <para>
    /// The boundary-layer correlations were fitted well above their Rayleigh bands' lower
    /// ends and extrapolate to ZERO as Ra → 0, which would claim a body in still air loses
    /// no heat at small temperature differences. What actually happens is that the fluid
    /// still CONDUCTS, and Nu = 1 is that conduction limit (h = k_f/L). Flooring there is
    /// both physically honest and what keeps the surface exchange positive-definite, so a
    /// steady solve whose only anchor is the environment stays well-posed at ΔT → 0.
    /// </para>
    /// </summary>
    public const double QuiescentNusseltFloor = 1.0;

    private const double FlatnessSpreadDegrees = 15.0;
    private const double HorizontalConeDegrees = 45.0;

    private readonly FeMesh _mesh;
    private readonly EnvironmentSettings _environment;
    private readonly FluidProperties? _fluid;
    private readonly BoundaryAdjacency _adjacency;
    private readonly List<SurfacePanel> _panels;
    private readonly double[] _exposed;
    private readonly SortedSet<string> _notes = new();

    private EnvironmentBoundaryModel(FeMesh mesh, EnvironmentSettings environment,
        BoundaryAdjacency adjacency, List<SurfacePanel> panels, double[] exposed)
    {
        _mesh = mesh;
        _environment = environment;
        _fluid = environment.ResolveFluid();
        _adjacency = adjacency;
        _panels = panels;
        _exposed = exposed;
    }

    /// <summary>Per boundary triangle, the fraction of its area open to the surroundings
    /// (the rest lies inside a body-to-body contact).</summary>
    public IReadOnlyList<double> ExposedFractions => _exposed;

    /// <summary>The exposed panels, in ascending face-id order.</summary>
    public IReadOnlyList<SurfacePanel> Panels => _panels;

    /// <summary>The environment this model evaluates.</summary>
    public EnvironmentSettings Environment => _environment;

    /// <summary>
    /// Throws when the environment cannot be evaluated with the given materials — today,
    /// when radiation is on and a participating material has no emissivity. Called from the
    /// solvers' Validate so the failure arrives before any assembly work.
    /// </summary>
    public static void ValidateMaterials(SolveInput input)
    {
        if (input.Environment is not { IncludeRadiation: true }) return;
        input.Material.ValidateRadiative();
        if (input.RegionMaterials is null) return;
        foreach (var material in input.RegionMaterials.Values)
            material.ValidateRadiative();
    }

    /// <summary>
    /// Builds the model, or returns null when the environment acts on nothing: no
    /// environment at all, or every exterior face already claimed by a user condition. A
    /// null model means the solver takes its ordinary linear path, which is what makes
    /// "the user specified everything by hand" bitwise identical to having no environment.
    /// </summary>
    public static EnvironmentBoundaryModel? Build(SolveInput input, List<string> log)
    {
        var environment = input.Environment;
        if (environment is null) return null;

        var mesh = input.Mesh;
        var claimed = new HashSet<int>();
        foreach (var bc in input.BoundaryConditions)
            if (bc is FixedTemperature or HeatFlux or Convection)
                claimed.UnionWith(bc.FaceIds);

        var adjacency = BoundaryAdjacency.Build(mesh);
        var exposed = ContactInterface.ExposedFractions(mesh.BoundaryTriangles.Count,
            input.ThermalContacts);
        double buriedArea = 0;
        var byFace = new SortedDictionary<int, List<int>>();
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            int faceId = mesh.BoundaryTriangles[t].FaceId;
            if (claimed.Contains(faceId)) continue;
            buriedArea += (1 - exposed[t]) * adjacency.Areas[t];
            if (!(exposed[t] > 0)) continue;       // wholly inside a joint
            if (!byFace.TryGetValue(faceId, out var list))
                byFace[faceId] = list = new List<int>();
            list.Add(t);
        }

        if (byFace.Count == 0)
        {
            log.Add(buriedArea > 0
                ? "Environment: every face is either inside a body-to-body contact or carries a " +
                  "user thermal condition, so the environment adds nothing to this solve."
                : "Environment: every exterior face carries a user thermal condition, so the " +
                  "environment adds nothing to this solve.");
            return null;
        }

        var up = UpDirection(environment, log);
        var flowDirection = environment.FlowSpeed > 0
            ? environment.FlowVelocity / environment.FlowVelocity.Length
            : new Vector3D(0, 0, 0);
        var flowExtents = RegionFlowExtents(mesh, adjacency, flowDirection);

        var panels = new List<SurfacePanel>(byFace.Count);
        foreach (var (faceId, triangles) in byFace)
        {
            var panel = BuildPanel(input, adjacency, exposed, faceId, triangles, up, flowExtents);
            // A face of zero area exchanges nothing, and dividing by it would put a NaN into
            // the matrix instead of saying so.
            if (!(panel.Area > 0))
            {
                log.Add($"Environment: face {faceId} has zero area and is not exposed to the " +
                        "environment (check the mesh for degenerate boundary triangles).");
                continue;
            }
            panels.Add(panel);
        }
        if (panels.Count == 0) return null;

        LogAssumptions(environment, panels, claimed, log);
        if (buriedArea > 0)
            log.Add($"  {buriedArea:g4} m² of skin lies inside body-to-body contacts and takes no " +
                    "convection or radiation (it exchanges heat through the contact only).");
        return new EnvironmentBoundaryModel(mesh, environment, adjacency, panels, exposed);
    }

    /// <summary>
    /// The film model at the given nodal temperature field: one coefficient per panel
    /// (convection from the correlations at the panel's mean surface temperature, plus the
    /// factored radiation coefficient), NaN on every triangle the user claimed.
    /// </summary>
    public SurfaceFilmModel Evaluate(IReadOnlyList<double> nodalTemperature)
    {
        var coefficients = new double[_mesh.BoundaryTriangles.Count];
        Array.Fill(coefficients, double.NaN);

        double ambient = _environment.AmbientTemperature;
        double gravity = _environment.Gravity.Length;
        double speed = _environment.FlowSpeed;

        foreach (var panel in _panels)
        {
            double surface = MeanTemperature(panel, nodalTemperature);
            double h = 0;

            if (_fluid is not null)
            {
                double film = 0.5 * (surface + ambient);
                if (!_fluid.Covers(film))
                    _notes.Add($"Fluid '{_fluid.Name}': film temperature {film:F1} K is outside the " +
                               $"property table {_fluid.TemperatureRange.Min:F0}…" +
                               $"{_fluid.TemperatureRange.Max:F0} K; end-row properties were used.");
                var state = _fluid.AtTemperature(film);
                h += ConvectionCoefficient(panel, state, surface - ambient, gravity, speed);
            }

            if (_environment.IncludeRadiation && panel.Emissivity > 0)
                h += ConvectionCorrelations.RadiativeFilmCoefficient(panel.Emissivity, surface, ambient);

            // A triangle partly inside a joint exchanges over its exposed part only; the
            // Robin term integrates h over the whole triangle, so the fraction scales h.
            foreach (int t in panel.TriangleIndices)
                coefficients[t] = h * _exposed[t];
        }

        return new SurfaceFilmModel
        {
            TriangleFilmCoefficient = coefficients,
            ReferenceTemperature = ambient,
            Origin = "Stage 1 correlations — " + _environment.Describe()
        };
    }

    /// <summary>Notes gathered while evaluating (out-of-band correlations, clamped fluid
    /// properties), each once. Drained by the solver after convergence so the log carries
    /// them without one line per Picard iterate.</summary>
    public IReadOnlyList<string> DrainNotes()
    {
        var notes = _notes.ToList();
        _notes.Clear();
        return notes;
    }

    private double ConvectionCoefficient(SurfacePanel panel, FluidState state,
        double excess, double gravity, double speed)
    {
        double length = panel.CharacteristicLength;
        double rayleigh = ConvectionCorrelations.Rayleigh(state, gravity, excess, length);
        double prandtl = state.Prandtl;

        // Below its density maximum water expands on COOLING (β < 0), so the warm side is
        // the heavy one and the plume runs the other way. Which side of a horizontal plate
        // the plume leaves from is what selects between the two correlations.
        bool plumeRises = (excess >= 0) == (state.ThermalExpansion >= 0);

        double natural;
        switch (panel.Shape)
        {
            case PanelShape.HorizontalPlateUpward:
            case PanelShape.HorizontalPlateDownward:
                if (panel.FacesUpward == plumeRises)
                {
                    natural = ConvectionCorrelations.NaturalHorizontalPlateBuoyant(rayleigh);
                    NoteBand(ConvectionCorrelations.HorizontalPlateBuoyantBand, rayleigh);
                }
                else
                {
                    natural = ConvectionCorrelations.NaturalHorizontalPlateStagnant(rayleigh);
                    NoteBand(ConvectionCorrelations.HorizontalPlateStagnantBand, rayleigh);
                }
                break;
            default:
                natural = ConvectionCorrelations.NaturalVerticalPlate(rayleigh, prandtl);
                NoteBand(ConvectionCorrelations.VerticalPlateBand, rayleigh);
                break;
        }

        natural = Math.Max(natural, QuiescentNusseltFloor);
        double naturalCoefficient = natural * state.ThermalConductivity / length;
        if (speed <= 0 || panel.FlowLength <= 0) return naturalCoefficient;

        double reynolds = ConvectionCorrelations.Reynolds(state, speed, panel.FlowLength);
        NoteBand(ConvectionCorrelations.FlatPlateBand, reynolds);
        double forcedCoefficient = ConvectionCorrelations.ForcedFlatPlate(reynolds, prandtl)
                                   * state.ThermalConductivity / panel.FlowLength;
        return ConvectionCorrelations.BlendMixedConvection(forcedCoefficient, naturalCoefficient);
    }

    private void NoteBand(ValidityBand band, double value)
    {
        if (!band.Contains(value)) _notes.Add(band.Note(value));
    }

    private double MeanTemperature(SurfacePanel panel, IReadOnlyList<double> nodal)
    {
        double sum = 0;
        foreach (int t in panel.TriangleIndices)
        {
            var bt = _mesh.BoundaryTriangles[t];
            sum += _exposed[t] * _adjacency.Areas[t] * (nodal[bt.A] + nodal[bt.B] + nodal[bt.C]) / 3.0;
        }
        return sum / panel.Area;
    }

    private static Vector3D UpDirection(EnvironmentSettings environment, List<string> log)
    {
        double magnitude = environment.Gravity.Length;
        if (magnitude > 0) return -environment.Gravity / magnitude;
        log.Add("Environment: gravity is zero, so +Z is taken as 'up' for classifying surfaces " +
                "(with no buoyancy there is no natural convection to orient anyway).");
        return new Vector3D(0, 0, 1);
    }

    /// <summary>Streamwise extent of each region's bounding box [m] — the flat-plate run
    /// length every panel of that body shares.</summary>
    private static Dictionary<int, double> RegionFlowExtents(FeMesh mesh,
        BoundaryAdjacency adjacency, Vector3D flowDirection)
    {
        var extents = new Dictionary<int, double>();
        if (flowDirection.Length == 0) return extents;

        var span = new Dictionary<int, (double Min, double Max)>();
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            var bt = mesh.BoundaryTriangles[t];
            int region = adjacency.Regions[t];
            Extend(bt.A);
            Extend(bt.B);
            Extend(bt.C);

            void Extend(int node)
            {
                double s = Vector3D.Dot(mesh.Nodes[node], flowDirection);
                span[region] = span.TryGetValue(region, out var prior)
                    ? (Math.Min(prior.Min, s), Math.Max(prior.Max, s))
                    : (s, s);
            }
        }
        foreach (var (region, (min, max)) in span)
            extents[region] = max - min;
        return extents;
    }

    private static SurfacePanel BuildPanel(SolveInput input, BoundaryAdjacency adjacency,
        double[] exposed, int faceId, List<int> triangles, Vector3D up,
        Dictionary<int, double> flowExtents)
    {
        var mesh = input.Mesh;
        double area = 0;
        var weightedNormal = new Vector3D(0, 0, 0);
        foreach (int t in triangles)
        {
            area += exposed[t] * adjacency.Areas[t];
            weightedNormal += adjacency.OutwardNormals[t] * (exposed[t] * adjacency.Areas[t]);
        }

        double normalLength = weightedNormal.Length;
        // A fully closed panel (a whole sphere as one face) cancels its own normals; there is
        // no meaningful mean direction, so it is treated as curved.
        var meanNormal = normalLength > 1e-12 * Math.Max(area, 1e-30)
            ? weightedNormal / normalLength
            : new Vector3D(0, 0, 0);

        double spread = 0;
        if (meanNormal.Length > 0)
            foreach (int t in triangles)
                spread = Math.Max(spread,
                    Math.Acos(Math.Clamp(Vector3D.Dot(adjacency.OutwardNormals[t], meanNormal), -1, 1)));
        else
            spread = Math.PI;
        double spreadDegrees = spread * 180.0 / Math.PI;

        double alignment = meanNormal.Length > 0 ? Vector3D.Dot(meanNormal, up) : 0;
        double horizontalCosine = Math.Cos(HorizontalConeDegrees * Math.PI / 180.0);
        var shape = spreadDegrees > FlatnessSpreadDegrees
            ? PanelShape.CurvedSurface
            : alignment > horizontalCosine
                ? PanelShape.HorizontalPlateUpward
                : alignment < -horizontalCosine
                    ? PanelShape.HorizontalPlateDownward
                    : PanelShape.VerticalPlate;

        double length = shape is PanelShape.HorizontalPlateUpward or PanelShape.HorizontalPlateDownward
            ? AreaOverPerimeter(mesh, triangles, area)
            : VerticalExtent(mesh, triangles, up);
        if (!(length > 0)) length = Math.Sqrt(area);

        int region = adjacency.Regions[triangles[0]];
        var material = input.RegionMaterials?.GetValueOrDefault(region) ?? input.Material;

        return new SurfacePanel
        {
            FaceId = faceId,
            TriangleIndices = triangles,
            Area = area,
            MeanNormal = meanNormal,
            NormalSpreadDegrees = spreadDegrees,
            Shape = shape,
            CharacteristicLength = length,
            FlowLength = flowExtents.GetValueOrDefault(region),
            Emissivity = input.Environment!.IncludeRadiation ? material.Emissivity ?? 0 : 0,
            Region = region,
            MaterialName = material.Name
        };
    }

    private static double VerticalExtent(FeMesh mesh, List<int> triangles, Vector3D up)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (int t in triangles)
        {
            var bt = mesh.BoundaryTriangles[t];
            Extend(bt.A);
            Extend(bt.B);
            Extend(bt.C);
        }
        return max - min;

        void Extend(int node)
        {
            double h = Vector3D.Dot(mesh.Nodes[node], up);
            if (h < min) min = h;
            if (h > max) max = h;
        }
    }

    /// <summary>L_c = A/P for a horizontal plate, with the perimeter taken as the panel's
    /// free edges — those belonging to exactly one of its triangles.</summary>
    private static double AreaOverPerimeter(FeMesh mesh, List<int> triangles, double area)
    {
        var counts = new Dictionary<(int, int), int>();
        foreach (int t in triangles)
        {
            var bt = mesh.BoundaryTriangles[t];
            Count(bt.A, bt.B);
            Count(bt.B, bt.C);
            Count(bt.C, bt.A);
        }

        double perimeter = 0;
        foreach (var ((a, b), count) in counts)
            if (count == 1)
                perimeter += (mesh.Nodes[b] - mesh.Nodes[a]).Length;
        return perimeter > 0 ? area / perimeter : 0;

        void Count(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
    }

    private static void LogAssumptions(EnvironmentSettings environment,
        IReadOnlyList<SurfacePanel> panels, HashSet<int> claimed, List<string> log)
    {
        log.Add($"Environment: {environment.Describe()}; {panels.Count} exposed face(s), " +
                $"{panels.Sum(p => p.Area):g4} m² total" +
                (claimed.Count > 0
                    ? $" ({claimed.Count} face(s) left to the user's own conditions)."
                    : "."));

        const int detailLimit = 30;
        if (panels.Count <= detailLimit)
            foreach (var panel in panels)
                log.Add($"  face {panel.FaceId}: {Describe(panel.Shape)}, {panel.Area:g4} m², " +
                        $"L = {panel.CharacteristicLength:g4} m" +
                        (panel.FlowLength > 0 ? $", flow run {panel.FlowLength:g4} m" : "") +
                        (environment.IncludeRadiation
                            ? $", ε = {panel.Emissivity:g3} ({panel.MaterialName})"
                            : ""));
        else
            foreach (var group in panels.GroupBy(p => p.Shape).OrderBy(g => g.Key))
                log.Add($"  {group.Count()} face(s) as {Describe(group.Key)}, " +
                        $"{group.Sum(p => p.Area):g4} m².");

        if (panels.Any(p => p.Shape == PanelShape.CurvedSurface))
            log.Add("  Curved faces use the vertical-plate correlation over their vertical " +
                    "extent — a stated approximation; cylinder correlations are not auto-selected.");
    }

    private static string Describe(PanelShape shape) => shape switch
    {
        PanelShape.VerticalPlate => "vertical plate",
        PanelShape.HorizontalPlateUpward => "horizontal plate facing up",
        PanelShape.HorizontalPlateDownward => "horizontal plate facing down",
        _ => "curved surface"
    };
}
