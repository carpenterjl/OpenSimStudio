using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Solvers;

namespace OpenSim.Pcb.Thermal;

/// <summary>
/// The air inside an enclosure as one node: the parts' case tops breathe it and (by default)
/// the board's environment cools to it, and it loses heat to the outside through a
/// conductance — a through-flow's ṁ·c_p, or the walls' overall conductance. What the parts
/// and the board give the air warms it, and every part sees the air the others warmed.
/// </summary>
public sealed record EnclosureAir
{
    /// <summary>Conductance from the enclosure's air to the outside ambient [W/K]. Required;
    /// nothing stands in for it.</summary>
    public required double ConductanceToAmbient { get; init; }

    /// <summary>Heat capacity of the air and whatever warms with it [J/K]; 0 = none.</summary>
    public double CapacityJoulesPerKelvin { get; init; }

    /// <summary>The board's environment cools it to this air (its convection correlations
    /// and its radiation see the air's temperature as the surroundings). False: only the
    /// parts' case tops see the air; the board keeps the environment's own ambient.</summary>
    public bool CoolsTheBoard { get; init; } = true;

    public void Validate()
    {
        if (!(ConductanceToAmbient > 0) || double.IsInfinity(ConductanceToAmbient))
            throw new InvalidOperationException("The enclosure air needs a positive, finite conductance to the outside (W/K).");
        if (!(CapacityJoulesPerKelvin >= 0) || double.IsInfinity(CapacityJoulesPerKelvin))
            throw new InvalidOperationException("The enclosure air's heat capacity cannot be negative.");
    }
}

/// <summary>The parts, and what cools the board and the tops of the parts.</summary>
public sealed record ComponentThermalSetup
{
    /// <summary>In the order the mesh was built with.</summary>
    public required IReadOnlyList<ThermalComponent> Components { get; init; }

    /// <summary>The surroundings of the board's free surface.</summary>
    public EnvironmentSettings? Environment { get; init; }

    /// <summary>Conditions placed by hand on the board (face 0 = top, 1 = bottom,
    /// 2… = edges).</summary>
    public IReadOnlyList<BoundaryCondition> BoardConditions { get; init; } = Array.Empty<BoundaryCondition>();

    /// <summary>Air temperature at the case tops [K]; null = the environment's ambient.</summary>
    public double? AmbientKelvin { get; init; }

    /// <summary>The enclosure's air as a node of its own; null = the parts' tops see the
    /// ambient directly.</summary>
    public EnclosureAir? Air { get; init; }
}

/// <summary>One part's temperatures and where its heat goes.</summary>
public sealed record ComponentTemperature
{
    public required string RefDes { get; init; }
    public string? Part { get; init; }
    public required double PowerWatts { get; init; }
    public required double JunctionKelvin { get; init; }

    /// <summary>Case top [K] (a compact model's first exposed node); NaN when nothing of the
    /// part is in the air.</summary>
    public required double CaseKelvin { get; init; }

    /// <summary>Mean board temperature under the part [K].</summary>
    public required double BoardKelvin { get; init; }

    /// <summary>Heat into the board [W]; negative when the board heats the part.</summary>
    public required double ToBoardWatts { get; init; }

    /// <summary>Heat out of the case into the air [W].</summary>
    public required double FromTopWatts { get; init; }

    /// <summary>(T_j − T_ambient) / P as this board gives it [K/W]; NaN without power.</summary>
    public required double ThetaJa { get; init; }

    public required double ContactArea { get; init; }

    /// <summary>Every node of the part's model with its temperature [K] (junction, case and
    /// board for the two-resistor model; the vendor's nodes for a compact model).</summary>
    public IReadOnlyList<(string Node, double Kelvin)> Nodes { get; init; } = Array.Empty<(string, double)>();
}

public sealed record ComponentThermalReport
{
    /// <summary>Hottest junction first.</summary>
    public required IReadOnlyList<ComponentTemperature> Components { get; init; }
    public required double AmbientKelvin { get; init; }
    public required double PeakBoardKelvin { get; init; }

    /// <summary>Total heat the parts put into the board [W].</summary>
    public required double IntoBoardWatts { get; init; }

    /// <summary>The enclosure air's temperature [K]; null without an enclosure.</summary>
    public double? AirKelvin { get; init; }

    public required IReadOnlyList<IResultField> Fields { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }
    public required IReadOnlyList<string> Log { get; init; }

    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            Components.Count == 0
                ? "No components on the board."
                : $"Hottest junction {Components[0].JunctionKelvin - 273.15:f1} °C ({Components[0].RefDes}); " +
                  $"board peak {PeakBoardKelvin - 273.15:f1} °C; ambient {AmbientKelvin - 273.15:f1} °C; " +
                  $"{IntoBoardWatts:g4} W into the board."
        };
        if (AirKelvin is { } air)
            lines.Add($"Enclosure air {air - 273.15:f1} °C ({air - AmbientKelvin:f1} K above the outside).");
        foreach (var c in Components) lines.Add(ComponentThermalAnalysis.Describe(c));
        return lines;
    }
}

/// <summary>A part transient: the parts' temperatures over time.</summary>
public sealed record ComponentThermalTransientReport
{
    /// <summary>The transient solve itself (frames on the board mesh, each with the network's state).</summary>
    public required SolveOutput Thermal { get; init; }

    /// <summary>Per stored step: the time [s], every part's temperatures (hottest junction
    /// first) and the enclosure air's.</summary>
    public required IReadOnlyList<(double Time, IReadOnlyList<ComponentTemperature> Parts, double? AirKelvin)> History { get; init; }

    public required double AmbientKelvin { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }
    public required IReadOnlyList<string> Log { get; init; }

    /// <summary>The parts at the last step.</summary>
    public IReadOnlyList<ComponentTemperature> Components => History[^1].Parts;

    /// <summary>One part's junction over time.</summary>
    public IReadOnlyList<(double Time, double Kelvin)> JunctionHistory(string refDes) =>
        History.Select(h => (h.Time, h.Parts.First(p => p.RefDes == refDes).JunctionKelvin)).ToList();

    /// <summary>The hottest a part's junction got, and when.</summary>
    public (double Time, double Kelvin) PeakJunction(string refDes) => JunctionHistory(refDes).MaxBy(h => h.Kelvin);

    public IReadOnlyList<string> Describe()
    {
        var (end, parts, air) = History[^1];
        var lines = new List<string>
        {
            parts.Count == 0
                ? $"After {end:g4} s: no components on the board."
                : $"After {end:g4} s the hottest junction is {parts[0].JunctionKelvin - 273.15:f1} °C ({parts[0].RefDes})" +
                  (air is { } a ? $"; enclosure air {a - 273.15:f1} °C." : ".")
        };
        foreach (var part in parts)
        {
            var (when, peak) = PeakJunction(part.RefDes);
            lines.Add($"{part.RefDes}: Tj {part.JunctionKelvin - 273.15:f1} °C at the end, peak {peak - 273.15:f1} °C at {when:g4} s.");
        }
        return lines;
    }
}

/// <summary>One part's thermal impedance on this board.</summary>
public sealed record ComponentImpedance
{
    public required string RefDes { get; init; }

    /// <summary>Junction rise per watt after the part is switched on, over time.</summary>
    public required ThermalImpedanceCurve Junction { get; init; }

    /// <summary>The case top's; null when the part has no exposed node.</summary>
    public ThermalImpedanceCurve? Case { get; init; }

    /// <summary>The power the curve was taken at [W] (the part's, or 1 W).</summary>
    public required double PowerWatts { get; init; }

    public required IReadOnlyList<string> Assumptions { get; init; }
    public required IReadOnlyList<string> Log { get; init; }

    /// <summary>θJA on this board [K/W]: the curve's steady value.</summary>
    public double ThetaJa => Junction.SteadyKelvinPerWatt;
}

/// <summary>
/// Junction temperatures of parts on a board. Each part is a lumped network solved WITH the
/// board (<see cref="ThermalNetwork"/>): by default the two-resistor model of its datasheet —
/// the junction node takes the power and feeds the footprint through θJB (an isothermal
/// junction over a uniform conductance per area, 1/(θJB·A)) and the case through θJC; the
/// case loses heat to the air through its film or a heatsink — or the vendor's compact model
/// (<see cref="CompactThermalModel"/>). The board node of the model is the footprint's
/// area-mean temperature. The junction and the case can hold heat, so a transient and a
/// pulsed load are the same solve; an enclosure's air can be a node every part and the board
/// breathe (<see cref="EnclosureAir"/>).
/// </summary>
public static class ComponentThermalAnalysis
{
    public const string NetworkName = "parts";
    public const string AmbientNode = "ambient";
    public const string AirNode = "air";

    /// <summary>The network node of one part's model node.</summary>
    public static string NodeName(string refDes, string node) => $"{refDes}.{node}";

    /// <summary>How one part was put into the network: which of its nodes do what.</summary>
    internal sealed record PartNodes(string Junction, string? Case, IReadOnlyList<string> Board,
        IReadOnlyList<string> Exposed, IReadOnlyList<string> All, bool Mounted)
    {
        public static readonly PartNodes OffBoard = new("", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false);
    }

    /// <summary>The parts and the air as one network on the board mesh, how each part is wired,
    /// and the air temperature at the parts. The network is null when no part is on the board
    /// and there is no enclosure.</summary>
    internal sealed record Wiring(ThermalNetwork? Network, PartNodes[] Parts, double Ambient, string Air);

    internal static Wiring Prepare(BoardThermalMesh mesh, ComponentThermalSetup setup, Func<ThermalComponent, double>? powerOf = null)
    {
        var components = setup.Components;
        if (components.Count != mesh.ComponentFaceIds.Count)
            throw new InvalidOperationException(
                "The component list is not the one the board was meshed with; mesh the board again.");
        double ambient = setup.AmbientKelvin ?? setup.Environment?.AmbientTemperature
            ?? setup.BoardConditions.OfType<Convection>().Select(c => (double?)c.AmbientTemperature).FirstOrDefault()
            ?? throw new InvalidOperationException("Give the air temperature at the parts (an environment or an ambient).");
        setup.Air?.Validate();
        if (setup.Air is { CoolsTheBoard: true } && setup.Environment is null)
            throw new InvalidOperationException(
                "The enclosure air cools the board through the environment; give one, or set CoolsTheBoard false so only the parts' tops see the air.");

        var nodes = new List<ThermalNetworkNode> { new() { Name = AmbientNode, FixedKelvin = ambient } };
        var links = new List<ThermalNetworkLink>();
        var attachments = new List<ThermalNetworkAttachment>();
        string air = AmbientNode;
        if (setup.Air is { } enclosure)
        {
            air = AirNode;
            nodes.Add(new ThermalNetworkNode { Name = AirNode, CapacityJoulesPerKelvin = enclosure.CapacityJoulesPerKelvin });
            links.Add(new ThermalNetworkLink(AirNode, AmbientNode, enclosure.ConductanceToAmbient));
        }

        var parts = new PartNodes[components.Count];
        for (int k = 0; k < components.Count; k++)
        {
            var part = components[k];
            part.Validate();
            int face = mesh.ComponentFaceIds[k];
            if (face < 0)
            {
                parts[k] = PartNodes.OffBoard;
                continue;
            }
            double power = powerOf?.Invoke(part) ?? part.PowerWatts;
            string Name(string node) => NodeName(part.RefDes, node);
            static double Conductance(double kelvinPerWatt) => kelvinPerWatt <= 0 ? double.PositiveInfinity : 1 / kelvinPerWatt;

            if (part.CompactModel is { } model)
            {
                var exposed = model.Nodes.Where(n => n.Role == CompactNodeRole.Exposed).ToList();
                foreach (var node in model.Nodes)
                {
                    bool junction = node.Role == CompactNodeRole.Junction;
                    nodes.Add(new ThermalNetworkNode
                    {
                        Name = Name(node.Name), PowerWatts = junction ? power : 0, PowerProfile = junction ? part.PowerProfile : null,
                        CapacityJoulesPerKelvin = node.CapacityJoulesPerKelvin
                    });
                    switch (node.Role)
                    {
                        case CompactNodeRole.Board:
                            attachments.Add(new ThermalNetworkAttachment(Name(node.Name), new[] { face }, double.PositiveInfinity));
                            break;
                        case CompactNodeRole.Exposed:
                            double top = part.CaseToAmbient is { } given && exposed.Count == 1
                                ? given
                                : part.CaseFilmCoefficient > 0
                                    ? 1 / (part.CaseFilmCoefficient * (node.ExposedAreaSquareMeters ?? part.CaseArea))
                                    : double.PositiveInfinity;
                            if (!double.IsPositiveInfinity(top))
                                links.Add(new ThermalNetworkLink(Name(node.Name), air, Conductance(top)));
                            break;
                    }
                }
                foreach (var resistor in model.Resistors)
                    links.Add(new ThermalNetworkLink(Name(resistor.From), Name(resistor.To), Conductance(resistor.KelvinPerWatt)));
                parts[k] = new PartNodes(Name(model.Junction.Name),
                    exposed.Count > 0 ? Name(exposed[0].Name) : null,
                    model.Nodes.Where(n => n.Role == CompactNodeRole.Board).Select(n => Name(n.Name)).ToList(),
                    exposed.Select(n => Name(n.Name)).ToList(),
                    model.Nodes.Select(n => Name(n.Name)).ToList(), true);
                continue;
            }

            // The two-resistor model: junction → board through θJB, junction → case through θJC,
            // case → air through its film or heatsink. A zero resistance makes its two nodes one.
            nodes.Add(new ThermalNetworkNode
            {
                Name = Name("junction"), PowerWatts = power, PowerProfile = part.PowerProfile,
                CapacityJoulesPerKelvin = part.JunctionCapacity
            });
            nodes.Add(new ThermalNetworkNode { Name = Name("case"), CapacityJoulesPerKelvin = part.CaseCapacity });
            nodes.Add(new ThermalNetworkNode { Name = Name("board") });
            attachments.Add(new ThermalNetworkAttachment(Name("board"), new[] { face }, double.PositiveInfinity));
            links.Add(new ThermalNetworkLink(Name("junction"), Name("board"), Conductance(part.ThetaJb)));
            links.Add(new ThermalNetworkLink(Name("junction"), Name("case"), Conductance(part.ThetaJc)));
            double caseTop = part.TopResistance();
            if (!double.IsPositiveInfinity(caseTop))
                links.Add(new ThermalNetworkLink(Name("case"), air, Conductance(caseTop)));
            parts[k] = new PartNodes(Name("junction"), Name("case"), new[] { Name("board") }, new[] { Name("case") },
                new[] { Name("junction"), Name("case"), Name("board") }, true);
        }

        ThermalNetwork? network = parts.Any(p => p.Mounted) || setup.Air is not null
            ? new ThermalNetwork
            {
                Name = NetworkName, Nodes = nodes, Links = links, Attachments = attachments,
                EnvironmentAmbientNode = setup.Air is { CoolsTheBoard: true } ? AirNode : null
            }
            : null;
        return new Wiring(network, parts, ambient, air);
    }

    /// <summary>Each mounted part's temperatures from the network's state, hottest junction
    /// first, and the heat the parts put into the board.</summary>
    internal static (List<ComponentTemperature> Parts, double IntoBoard) Temperatures(BoardThermalMesh mesh,
        IReadOnlyList<ThermalComponent> components, Wiring wiring, ThermalNetworkState state,
        Func<ThermalComponent, double>? powerOf = null)
    {
        var network = wiring.Network!;
        var results = new List<ComponentTemperature>();
        double intoBoard = 0;
        for (int k = 0; k < components.Count; k++)
        {
            var part = components[k];
            var nodes = wiring.Parts[k];
            if (!nodes.Mounted) continue;
            double power = powerOf?.Invoke(part) ?? part.PowerWatts;
            double junction = state.Of(nodes.Junction);
            double board = state.Of(nodes.Board[0]);
            double @case = nodes.Case is { } caseNode ? state.Of(caseNode) : double.NaN;
            // Into the board: what the part's board nodes gave their faces. From the top: the
            // exposed nodes' links to the air.
            double toBoard = 0, fromTop = 0;
            for (int a = 0; a < network.Attachments.Count; a++)
                if (nodes.Board.Contains(network.Attachments[a].Node)) toBoard += state.AttachmentWatts[a];
            for (int l = 0; l < network.Links.Count; l++)
            {
                var link = network.Links[l];
                if (link.To == wiring.Air && nodes.Exposed.Contains(link.From)) fromTop += state.LinkWatts[l];
            }
            intoBoard += toBoard;
            results.Add(new ComponentTemperature
            {
                RefDes = part.RefDes, Part = part.Part, PowerWatts = power,
                JunctionKelvin = junction, CaseKelvin = @case, BoardKelvin = board,
                ToBoardWatts = toBoard, FromTopWatts = fromTop,
                ThetaJa = power > 0 ? (junction - wiring.Ambient) / power : double.NaN,
                ContactArea = mesh.ComponentContactArea[k],
                Nodes = nodes.All.Select(n => (n[(part.RefDes.Length + 1)..], state.Of(n))).ToList()
            });
        }
        results.Sort((a, b) => b.JunctionKelvin.CompareTo(a.JunctionKelvin));
        return (results, intoBoard);
    }

    internal static string Describe(ComponentTemperature c) =>
        $"{c.RefDes}{(c.Part is null ? "" : $" ({c.Part})")}: Tj {c.JunctionKelvin - 273.15:f1} °C, " +
        (double.IsNaN(c.CaseKelvin) ? "" : $"case {c.CaseKelvin - 273.15:f1} °C, ") +
        $"board {c.BoardKelvin - 273.15:f1} °C; " +
        $"{c.PowerWatts:g4} W — {c.ToBoardWatts:g3} W into the board, {c.FromTopWatts:g3} W from the top" +
        (double.IsNaN(c.ThetaJa) ? "." : $"; θJA here {c.ThetaJa:g4} K/W.");

    private static SolveInput Input(BoardThermalMesh mesh, ComponentThermalSetup setup, Wiring wiring) => new()
    {
        Mesh = mesh.Mesh,
        Material = mesh.Laminate,
        RegionMaterials = mesh.RegionMaterials,
        ElementThermalConductivity = mesh.Conductivity,
        BoundaryConditions = setup.BoardConditions,
        Environment = setup.Environment,
        ThermalNetworks = wiring.Network is { } network ? new[] { network } : null
    };

    private static double? AirOf(Wiring wiring, ThermalNetworkState? state) =>
        wiring.Air == AirNode && state is not null ? state.Of(AirNode) : null;

    private static List<string> Assumptions(ComponentThermalSetup setup, double ambient, string stateLine)
    {
        var components = setup.Components;
        bool compact = components.Any(c => c.CompactModel is not null), twoResistor = components.Any(c => c.CompactModel is null);
        var assumptions = new List<string>();
        if (twoResistor)
            assumptions.Add("each part without a compact model is the two-resistor model (θJC to the case top, θJB to the board): " +
                            "one isothermal junction feeding the footprint through a uniform conductance per area 1/(θJB·A) and the " +
                            "case through θJC; no heat through the leads or the package sides other than what θJB already holds; " +
                            "the model's board node is the mean board temperature under the footprint");
        if (compact)
            assumptions.Add("a part with a compact model is the vendor's network: its board nodes sit at the footprint's mean " +
                            "temperature and feed it through their resistances to the junction, its exposed nodes lose heat to the air " +
                            "through the case film on their own areas; the numbers are the vendor's and are not checked here");
        assumptions.Add("θJB and θJC are the datasheet's, measured on the JEDEC test board and cold plate; on another board they " +
                        "are the same numbers by assumption, which is the model's known weakness");
        assumptions.Add("copper is spread within each element of its layer (planes conduct as planes, narrow traces only as their " +
                        "share of copper); via barrels add copper to the dielectric they cross, in every direction");
        assumptions.Add(setup.Air is { } air
            ? "the case tops lose heat through the film coefficient or heatsink resistance given for the part to the ENCLOSURE AIR, " +
              $"one node that loses {air.ConductanceToAmbient:g3} W/K to the outside at {ambient - 273.15:f1} °C" +
              (air.CoolsTheBoard ? " and that the board's environment cools to as well (its radiation sees the enclosure at the air's temperature)" : "") +
              "; the footprint area takes no part in the board's own cooling"
            : "the case top loses heat through the film coefficient or heatsink resistance given for the part, " +
              $"to {ambient - 273.15:f1} °C; the footprint area takes no part in the board's own cooling");
        assumptions.Add(setup.Environment is not null
            ? $"board surface cooled by the environment ({setup.Environment.Describe()}), one coefficient per face"
            : "board cooled by the conditions given, nothing else");
        assumptions.Add(stateLine);
        return assumptions;
    }

    public static ComponentThermalReport Solve(BoardThermalMesh mesh, ComponentThermalSetup setup,
        IProgress<SolverProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var wiring = Prepare(mesh, setup);
        var output = new HeatConductionSolver().Solve(Input(mesh, setup, wiring), progress, cancellationToken);
        var temperature = ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values;
        var state = output.Networks?.FirstOrDefault(s => s.Network == NetworkName);
        var (results, intoBoard) = wiring.Network is null || state is null
            ? (new List<ComponentTemperature>(), 0.0)
            : Temperatures(mesh, setup.Components, wiring, state);

        var log = new List<string>(mesh.Notes);
        log.AddRange(output.Log);
        return new ComponentThermalReport
        {
            Components = results,
            AmbientKelvin = wiring.Ambient,
            PeakBoardKelvin = temperature.Max(),
            IntoBoardWatts = intoBoard,
            AirKelvin = AirOf(wiring, state),
            Fields = output.Fields,
            Assumptions = Assumptions(setup, wiring.Ambient,
                "steady state; no Joule heat from the copper (the rail analysis adds it); parts heat each other through the board" +
                (setup.Air is not null ? " and through the enclosure's air" : ", not through the air")),
            Log = log
        };
    }

    /// <summary>
    /// The parts and the board from a uniform start, over time: backward Euler with the
    /// junctions' and cases' capacities (<see cref="ThermalComponent.JunctionCapacity"/>,
    /// <see cref="ThermalComponent.CaseCapacity"/>) and each part's own power profile
    /// (<see cref="ThermalComponent.PowerProfile"/>) or the settings'. A part without
    /// capacities follows its board and the air at once.
    /// </summary>
    public static ComponentThermalTransientReport SolveTransient(BoardThermalMesh mesh, ComponentThermalSetup setup,
        TransientThermalSettings settings, IProgress<SolverProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var wiring = Prepare(mesh, setup);
        if (wiring.Network is null)
            throw new InvalidOperationException("No part is on the board; there is nothing to follow in time.");
        var output = new TransientThermalSolver().Solve(Input(mesh, setup, wiring) with { TransientThermal = settings },
            progress, cancellationToken);
        var history = new List<(double, IReadOnlyList<ComponentTemperature>, double?)>();
        foreach (var frame in output.Frames!)
        {
            var state = frame.Networks!.First(s => s.Network == NetworkName);
            history.Add((frame.Value, Temperatures(mesh, setup.Components, wiring, state).Parts, AirOf(wiring, state)));
        }
        var log = new List<string>(mesh.Notes);
        log.AddRange(output.Log);
        return new ComponentThermalTransientReport
        {
            Thermal = output,
            History = history,
            AmbientKelvin = wiring.Ambient,
            Assumptions = Assumptions(setup, wiring.Ambient,
                $"transient from {settings.InitialTemperature - 273.15:f1} °C everywhere (parts and air included), backward Euler in " +
                $"steps of {settings.TimeStep:g3} s; a junction or case without a capacity has no thermal mass of its own; " +
                "no Joule heat from the copper"),
            Log = log
        };
    }

    /// <summary>
    /// One part's thermal impedance on this board: the junction's (and case's) rise per watt
    /// against time after the part alone is switched on, the other parts passive at zero
    /// power (<see cref="ThermalImpedanceSolver"/>). Its steady value is the part's θJA on
    /// this board; <see cref="ThermalImpedanceCurve.RiseAt"/> gives the junction under a
    /// pulsed load by superposition.
    /// </summary>
    public static ComponentImpedance ThermalImpedance(BoardThermalMesh mesh, ComponentThermalSetup setup, string refDes,
        ThermalImpedanceSettings? settings = null, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int index = -1;
        for (int k = 0; k < setup.Components.Count; k++)
            if (setup.Components[k].RefDes == refDes) index = k;
        if (index < 0) throw new InvalidOperationException($"No part '{refDes}' is in the component list.");
        var part = setup.Components[index];
        double power = part.PowerWatts > 0 ? part.PowerWatts : 1.0;
        var wiring = Prepare(mesh, setup, p => ReferenceEquals(p, part) ? power : 0);
        if (!wiring.Parts[index].Mounted)
            throw new InvalidOperationException($"{refDes}: its footprint is off the board.");
        var nodes = wiring.Parts[index];
        var probes = new List<ThermalProbe> { new() { Name = "junction", Network = NetworkName, Node = nodes.Junction } };
        if (nodes.Case is { } caseNode) probes.Add(new ThermalProbe { Name = "case", Network = NetworkName, Node = caseNode });
        var result = ThermalImpedanceSolver.Solve(Input(mesh, setup, wiring), probes, settings, progress, cancellationToken);
        var log = new List<string>(mesh.Notes);
        log.AddRange(result.Log);
        return new ComponentImpedance
        {
            RefDes = refDes,
            Junction = result.Curves[0],
            Case = result.Curves.Count > 1 ? result.Curves[1] : null,
            PowerWatts = power,
            Assumptions = Assumptions(setup, wiring.Ambient,
                $"the step response of {refDes} alone at {power:g3} W, the other parts passive at zero power; the board's " +
                "surface coefficients are held at the steady state of that load, so the curve is the response about it; " +
                "a pulsed load's junction follows by superposition of the curve (a linear model)"),
            Log = log
        };
    }
}
