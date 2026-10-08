using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;

namespace OpenSim.Pcb.Thermal;

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
}

/// <summary>One part's temperatures and where its heat goes.</summary>
public sealed record ComponentTemperature
{
    public required string RefDes { get; init; }
    public string? Part { get; init; }
    public required double PowerWatts { get; init; }
    public required double JunctionKelvin { get; init; }

    /// <summary>Case top [K].</summary>
    public required double CaseKelvin { get; init; }

    /// <summary>Mean board temperature under the part [K].</summary>
    public required double BoardKelvin { get; init; }

    /// <summary>Heat into the board [W]; negative when the board heats the part.</summary>
    public required double ToBoardWatts { get; init; }

    /// <summary>Heat out of the case top [W].</summary>
    public required double FromTopWatts { get; init; }

    /// <summary>(T_j − T_ambient) / P as this board gives it [K/W]; NaN without power.</summary>
    public required double ThetaJa { get; init; }

    public required double ContactArea { get; init; }
}

public sealed record ComponentThermalReport
{
    /// <summary>Hottest junction first.</summary>
    public required IReadOnlyList<ComponentTemperature> Components { get; init; }
    public required double AmbientKelvin { get; init; }
    public required double PeakBoardKelvin { get; init; }

    /// <summary>Total heat the parts put into the board [W].</summary>
    public required double IntoBoardWatts { get; init; }

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
        foreach (var c in Components)
            lines.Add($"{c.RefDes}{(c.Part is null ? "" : $" ({c.Part})")}: Tj {c.JunctionKelvin - 273.15:f1} °C, " +
                      $"case {c.CaseKelvin - 273.15:f1} °C, board {c.BoardKelvin - 273.15:f1} °C; " +
                      $"{c.PowerWatts:g4} W — {c.ToBoardWatts:g3} W into the board, {c.FromTopWatts:g3} W from the top" +
                      (double.IsNaN(c.ThetaJa) ? "." : $"; θJA here {c.ThetaJa:g4} K/W."));
        return lines;
    }
}

/// <summary>
/// Junction temperatures of parts on a board. Each part is the two-resistor model of its
/// datasheet: the junction feeds the board through θJB over the part's footprint and the
/// case top through θJC, and the case top feeds the air or a heatsink. With the junction
/// eliminated, what the board sees under a part is an exchange of conductance
/// 1/(θJB + R_top) with a source at T_ambient + P·R_top, R_top being θJC plus the case
/// top's resistance to ambient — a convection condition on the
/// footprint, so the board and all its parts are one ordinary heat solve and the split of
/// each part's power between board and top comes out of it rather than being assumed.
/// The board node of the model is the footprint's area-mean temperature.
/// </summary>
public static class ComponentThermalAnalysis
{
    /// <summary>One part's eliminated network: the exchange its footprint has with the board.</summary>
    internal readonly record struct PartNetwork(double Conductance, double Source, double Top, bool Mounted);

    /// <summary>The board conditions with every part's footprint exchange added, the parts'
    /// networks, and the air temperature at the parts.</summary>
    internal static (List<BoundaryCondition> Conditions, PartNetwork[] Network, double Ambient) Conditions(
        BoardThermalMesh mesh, ComponentThermalSetup setup)
    {
        var components = setup.Components;
        if (components.Count != mesh.ComponentFaceIds.Count)
            throw new InvalidOperationException(
                "The component list is not the one the board was meshed with; mesh the board again.");
        double ambient = setup.AmbientKelvin ?? setup.Environment?.AmbientTemperature
            ?? setup.BoardConditions.OfType<Convection>().Select(c => (double?)c.AmbientTemperature).FirstOrDefault()
            ?? throw new InvalidOperationException("Give the air temperature at the parts (an environment or an ambient).");

        var conditions = new List<BoundaryCondition>(setup.BoardConditions);
        var network = new PartNetwork[components.Count];
        for (int k = 0; k < components.Count; k++)
        {
            var part = components[k];
            part.Validate();
            int face = mesh.ComponentFaceIds[k];
            if (face < 0) continue;
            // Junction to ambient by way of the top: θJC, then the case top's own path.
            double top = part.ThetaJc + part.TopResistance();
            if (double.IsPositiveInfinity(top))
            {
                // Nothing leaves through the top: all the power goes into the board.
                network[k] = new PartNetwork(0, 0, top, true);
                if (part.PowerWatts != 0)
                    conditions.Add(new HeatFlux { Name = part.RefDes, FaceIds = new[] { face }, TotalPower = part.PowerWatts });
                continue;
            }
            double series = part.ThetaJb + top;
            if (series <= 0)
                throw new InvalidOperationException(
                    $"{part.RefDes}: θJB and the case-to-ambient resistance are both zero, which ties the board to the air.");
            double conductance = 1.0 / series, source = ambient + part.PowerWatts * top;
            network[k] = new PartNetwork(conductance, source, top, true);
            conditions.Add(new Convection
            {
                Name = part.RefDes, FaceIds = new[] { face },
                Coefficient = conductance / mesh.ComponentContactArea[k], AmbientTemperature = source
            });
        }
        return (conditions, network, ambient);
    }

    /// <summary>Each mounted part's temperatures from the board temperature, hottest junction
    /// first, and the heat the parts put into the board.</summary>
    internal static (List<ComponentTemperature> Parts, double IntoBoard) Temperatures(BoardThermalMesh mesh,
        IReadOnlyList<ThermalComponent> components, PartNetwork[] network, IReadOnlyList<double> temperature, double ambient)
    {
        // Area-mean board temperature under each part.
        var weighted = new double[components.Count];
        var area = new double[components.Count];
        foreach (var t in mesh.Mesh.BoundaryTriangles)
        {
            int k = t.FaceId - Extrude.PcbMeshGenerator.PadFaceBase;
            if (k < 0 || k >= components.Count) continue;
            double a = 0.5 * Vector3D.Cross(mesh.Mesh.Nodes[t.B] - mesh.Mesh.Nodes[t.A],
                mesh.Mesh.Nodes[t.C] - mesh.Mesh.Nodes[t.A]).Length;
            weighted[k] += a * (temperature[t.A] + temperature[t.B] + temperature[t.C]) / 3.0;
            area[k] += a;
        }

        var results = new List<ComponentTemperature>();
        double intoBoard = 0;
        for (int k = 0; k < components.Count; k++)
        {
            var part = components[k];
            var (conductance, source, top, mounted) = network[k];
            if (!mounted) continue;
            double board = weighted[k] / area[k];
            double toBoard = double.IsPositiveInfinity(top) ? part.PowerWatts : conductance * (source - board);
            double fromTop = part.PowerWatts - toBoard;
            double junction = board + part.ThetaJb * toBoard;
            double @case = junction - part.ThetaJc * fromTop;
            intoBoard += toBoard;
            results.Add(new ComponentTemperature
            {
                RefDes = part.RefDes, Part = part.Part, PowerWatts = part.PowerWatts,
                JunctionKelvin = junction, CaseKelvin = @case, BoardKelvin = board,
                ToBoardWatts = toBoard, FromTopWatts = fromTop,
                ThetaJa = part.PowerWatts > 0 ? (junction - ambient) / part.PowerWatts : double.NaN,
                ContactArea = area[k]
            });
        }
        results.Sort((a, b) => b.JunctionKelvin.CompareTo(a.JunctionKelvin));
        return (results, intoBoard);
    }

    public static ComponentThermalReport Solve(BoardThermalMesh mesh, ComponentThermalSetup setup,
        IProgress<SolverProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var components = setup.Components;
        var (conditions, network, ambient) = Conditions(mesh, setup);

        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh.Mesh,
            Material = mesh.Laminate,
            RegionMaterials = mesh.RegionMaterials,
            ElementThermalConductivity = mesh.Conductivity,
            BoundaryConditions = conditions,
            Environment = setup.Environment
        }, progress, cancellationToken);
        var temperature = ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values;
        var (results, intoBoard) = Temperatures(mesh, components, network, temperature, ambient);

        var log = new List<string>(mesh.Notes);
        log.AddRange(output.Log);
        var assumptions = new List<string>
        {
            "each part is the two-resistor model (θJC to the case top, θJB to the board): one junction temperature, " +
                "no heat through the leads or the package sides other than what θJB already holds; the model's board " +
                "node is the mean board temperature under the footprint",
            "θJB and θJC are the datasheet's, measured on the JEDEC test board and cold plate; on another board they " +
                "are the same numbers by assumption, which is the model's known weakness",
            "copper is spread within each element of its layer (planes conduct as planes, narrow traces only as their " +
                "share of copper); via barrels add copper to the dielectric they cross, in every direction",
            "the case top loses heat through the film coefficient or heatsink resistance given for the part, " +
                $"to {ambient - 273.15:f1} °C; the footprint area takes no part in the board's own cooling",
            setup.Environment is not null
                ? $"board surface cooled by the environment ({setup.Environment.Describe()}), one coefficient per face"
                : "board cooled by the conditions given, nothing else",
            "steady state; no Joule heat from the copper; parts do not shade or heat each other through the air"
        };

        return new ComponentThermalReport
        {
            Components = results,
            AmbientKelvin = ambient,
            PeakBoardKelvin = temperature.Max(),
            IntoBoardWatts = intoBoard,
            Fields = output.Fields,
            Assumptions = assumptions,
            Log = log
        };
    }
}
