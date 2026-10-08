using OpenSim.Core.Interfaces;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Thermal;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Pcb.Extrude;
using OpenSim.Pcb.Import;
using OpenSim.Solvers;

namespace OpenSim.Pcb.Power;

/// <summary>A rail with the heat it makes: the electrical setup plus the materials and the
/// cooling.</summary>
public sealed record RailThermalSetup
{
    /// <summary>Sources, loads and limits. Its <see cref="RailSetup.CopperConductivity"/>
    /// is not used here — the conductivity comes from <see cref="Copper"/>, at temperature.</summary>
    public required RailSetup Rail { get; init; }

    /// <summary>The copper: conductivity, its temperature coefficient, thermal conductivity.</summary>
    public required Material Copper { get; init; }

    /// <summary>The laminate (thermal conductivity; it carries no current). Unused by a
    /// copper-only mesh.</summary>
    public required Material Laminate { get; init; }

    /// <summary>The surroundings that cool every exposed face.</summary>
    public EnvironmentSettings? Environment { get; init; }

    /// <summary>Thermal conditions placed by hand (face ids of the thermal mesh:
    /// 0 = top, 1 = bottom, 2… = edges).</summary>
    public IReadOnlyList<BoundaryCondition> ThermalConditions { get; init; } = Array.Empty<BoundaryCondition>();

    /// <summary>
    /// Emissivity of the board's surface, copper and laminate alike — a board is covered
    /// by solder mask (≈ 0.9) whatever lies under it. Null keeps each material's own value
    /// (bare copper radiates a sixth of that).
    /// </summary>
    public double? SurfaceEmissivity { get; init; } = 0.9;

    /// <summary>The loop stops when no node moves by more than this [K] in one pass.</summary>
    public double Tolerance { get; init; } = 0.01;

    /// <summary>
    /// The heat solve of every pass when it is not the environment's correlation film — a
    /// conjugate CFD solve of the air around the board. Handed the thermal input with the
    /// environment on it. Null: the environment's film or the conditions given.
    /// </summary>
    public Func<SolveInput, CancellationToken, SolveOutput>? Cooling { get; init; }

    /// <summary>
    /// The whole board as the body the heat spreads in (<see cref="BoardThermalMesher"/>):
    /// every layer's copper as its share of the elements, so planes and other nets spread
    /// the heat, and the parts on their footprints. The rail's own copper still carries the
    /// current explicitly; its heat is handed to the board elements that hold it. Null: the
    /// net's own mesh is the thermal body (its laminate, if meshed with the board).
    /// Thermal conditions then name the board mesh's faces.
    /// </summary>
    public BoardThermalMesh? Board { get; init; }

    /// <summary>The parts on <see cref="Board"/>, in the order it was meshed with, with their
    /// losses and two-resistor models.</summary>
    public IReadOnlyList<ThermalComponent> Components { get; init; } = Array.Empty<ThermalComponent>();
}

/// <summary>The electro-thermal result of a rail: both rail reports and the temperatures.</summary>
public sealed record RailThermalReport
{
    /// <summary>The rail at its operating temperature (temperature-corrected IR drop).</summary>
    public required RailReport Rail { get; init; }

    /// <summary>The rail with all copper at the start temperature — the plain DC answer.</summary>
    public required RailReport ColdRail { get; init; }

    public required double StartKelvin { get; init; }

    /// <summary>Hottest copper [K], with the resistivity feedback.</summary>
    public required double PeakCopperKelvin { get; init; }

    /// <summary>Hottest copper [K] from the cold currents alone (one-way coupling).</summary>
    public required double OneWayPeakCopperKelvin { get; init; }

    public required Point2 HottestAt { get; init; }

    /// <summary>The layer the hottest copper is on ("L1"), or a via barrel.</summary>
    public required string HottestWhere { get; init; }

    public required int Iterations { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }
    public required IReadOnlyList<string> Log { get; init; }

    /// <summary>Converged electrical fields, temperature, heat flux, film coefficient and
    /// temperature rise, on the mesh.</summary>
    public required IReadOnlyList<IResultField> Fields { get; init; }

    public required ElectroThermalResult Solution { get; init; }

    /// <summary>The parts' temperatures, hottest junction first (with a whole-board model).</summary>
    public IReadOnlyList<ComponentTemperature> Components { get; init; } = Array.Empty<ComponentTemperature>();

    /// <summary>The thermal lines, then the corrected rail's own.</summary>
    public IReadOnlyList<string> Describe()
    {
        double rise = PeakCopperKelvin - StartKelvin, oneWay = OneWayPeakCopperKelvin - StartKelvin;
        var lines = new List<string>
        {
            $"Hottest copper {PeakCopperKelvin - 273.15:f1} °C on {HottestWhere} at " +
            $"({HottestAt.X * 1e3:f2}, {HottestAt.Y * 1e3:f2}) mm: rise {rise:f1} K above {StartKelvin - 273.15:f1} °C " +
            $"({oneWay:f1} K without the resistivity feedback; {Iterations} passes).",
            $"Copper loss {Rail.CopperLossWatts * 1e3:g4} mW hot, {ColdRail.CopperLossWatts * 1e3:g4} mW cold."
        };
        foreach (var hot in Rail.Sinks)
        {
            var cold = ColdRail.Sinks.First(s => s.Name == hot.Name);
            if (double.IsNaN(hot.Volts)) continue;
            lines.Add($"load {hot.Name}: drop {hot.DropVolts * 1e3:g4} mV hot, {cold.DropVolts * 1e3:g4} mV cold " +
                      $"— {(hot.Pass ? "pass" : "FAIL")} hot, {(cold.Pass ? "pass" : "FAIL")} cold.");
        }
        return lines;
    }
}

/// <summary>A rail's coupled transient: the march, and the rail at its end.</summary>
public sealed record RailThermalTransientReport
{
    public required ElectroThermalTransientResult Solution { get; init; }

    /// <summary>The rail at the last step's temperatures.</summary>
    public required RailReport Rail { get; init; }

    public required double StartKelvin { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }

    public IReadOnlyList<string> Describe()
    {
        var history = Solution.History;
        var (end, loss, peak) = history[^1];
        var lines = new List<string>
        {
            $"After {end:g4} s the hottest copper is {peak - 273.15:f1} °C ({peak - StartKelvin:f1} K above the start); " +
            $"copper loss {history[0].LossWatts * 1e3:g4} mW at the first step, {loss * 1e3:g4} mW at the last."
        };
        // Where the rise stands against its last step: a slow tail says the board is still warming.
        if (history.Count > 1)
        {
            double lastStep = peak - history[^2].PeakKelvin;
            lines.Add($"The last step added {lastStep:g3} K ({(peak > StartKelvin ? lastStep / (peak - StartKelvin) * 100 : 0):f2} % of the rise).");
        }
        return lines;
    }
}

/// <summary>
/// Board electro-thermal analysis of a rail: the copper loss heats the copper and the
/// laminate it sits on, the surroundings cool them, the copper's resistivity follows its
/// temperature, and the rail is re-solved until the two agree
/// (<see cref="ElectroThermalStudy"/>). The result is the temperature map, where the
/// copper is hottest, and the IR drop at operating temperature beside the cold one.
/// <para>
/// Run it on a mesh from <see cref="NetMesher.MeshNetOnBoard"/>. A copper-only mesh is
/// accepted — the copper then cools from its own surface alone, a strip in free air
/// rather than a trace on its board, and the assumptions say so.
/// </para>
/// </summary>
public static class RailThermalAnalysis
{
    /// <summary>The coupled study's input for a rail: materials, terminals, the thermal body
    /// (the net's mesh or the whole board) and its conditions, parts included.</summary>
    private static (ElectroThermalInput Input, ComponentThermalAnalysis.PartNetwork[]? Parts, double PartsAmbient,
        Material Copper, bool HasLaminate) Prepare(NetMesher.Result mesh, RailThermalSetup setup)
    {
        var fe = mesh.Body.Mesh ?? throw new InvalidOperationException("The net has no mesh.");
        if (setup.Environment is null && setup.ThermalConditions.Count == 0)
            throw new InvalidOperationException(
                "The board needs cooling: give it an environment, or a fixed temperature or convection condition.");
        setup.Copper.ValidateElectrical();

        var copper = setup.Copper;
        var laminate = setup.Laminate;
        if (setup.SurfaceEmissivity is { } emissivity)
        {
            copper = copper with { Emissivity = emissivity };
            laminate = laminate with { Emissivity = emissivity };
        }
        // The laminate takes no part in the electrical solve whatever its listed
        // conductivity; the study decides that by ratio, and FR4's 10⁻¹⁴ S/m is far below it.
        var regions = new Dictionary<int, Material>
        {
            [PcbStackup.CopperRegion] = copper,
            [PcbStackup.DielectricRegion] = laminate
        };
        bool hasLaminate = fe.ElementRegionIds?.Contains(PcbStackup.DielectricRegion) ?? false;

        var terminals = RailAnalysis.Terminals(mesh, setup.Rail);
        var board = setup.Board;
        if (board is not null)
            foreach (var (layer, (zLo, zHi)) in mesh.LayerZ)
                if (!board.LayerZ.TryGetValue(layer, out var boardZ) || Math.Abs(boardZ.zLo - zLo) > 1e-9 || Math.Abs(boardZ.zHi - zHi) > 1e-9)
                    throw new InvalidOperationException(
                        $"The net's copper on L{layer} does not stand where the board's L{layer} does: mesh the net with " +
                        "the board (NetMesher.MeshNetOnBoard) and the same stackup, so its heat lands in the right layer.");
        var conditions = setup.ThermalConditions;
        ComponentThermalAnalysis.PartNetwork[]? parts = null;
        double partsAmbient = 0;
        if (board is not null && setup.Components.Count > 0)
        {
            // The parts' footprints exchange heat with the board as their eliminated
            // two-resistor networks (see ComponentThermalAnalysis).
            var built = ComponentThermalAnalysis.Conditions(board, new ComponentThermalSetup
            {
                Components = setup.Components, Environment = setup.Environment, BoardConditions = setup.ThermalConditions
            });
            (conditions, parts, partsAmbient) = (built.Conditions, built.Network, built.Ambient);
        }
        var input = new ElectroThermalInput
        {
            Mesh = fe,
            ThermalMesh = board is null ? WithoutPadFaces(fe) : null,
            ThermalModel = board is null ? null : new SeparateThermalModel
            {
                Mesh = board.Mesh, Material = board.Laminate, RegionMaterials = board.RegionMaterials,
                Conductivity = board.Conductivity
            },
            Material = copper,
            RegionMaterials = regions,
            Terminals = terminals,
            ThermalConditions = conditions,
            Environment = setup.Environment,
            Cooling = setup.Cooling,
            Tolerance = setup.Tolerance
        };
        return (input, parts, partsAmbient, copper, hasLaminate);
    }

    public static RailThermalReport Solve(NetMesher.Result mesh, CopperNet net, RailThermalSetup setup,
        IProgress<OpenSim.Core.Interfaces.SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fe = mesh.Body.Mesh ?? throw new InvalidOperationException("The net has no mesh.");
        var (input, parts, partsAmbient, copper, hasLaminate) = Prepare(mesh, setup);
        var board = setup.Board;
        var solution = ElectroThermalStudy.Solve(input, progress, cancellationToken);

        var hot = RailAnalysis.Report(mesh, net, setup.Rail, solution.Electrical,
            solution.ElementConductivity, temperatureCorrected: true);
        var cold = RailAnalysis.Report(mesh, net, setup.Rail, solution.ColdElectrical,
            solution.ColdConductivity, temperatureCorrected: false);

        // Hottest copper: over the nodes of conducting elements, or with the board as the
        // thermal body, over the conducting elements' own temperatures.
        double peak = double.NegativeInfinity, oneWayPeak = double.NegativeInfinity;
        Vector3D at = default;
        for (int e = 0; e < fe.ElementCount; e++)
        {
            if (solution.ElementConductivity[e] == 0) continue;
            var el = fe.Elements[e];
            if (board is not null)
            {
                if (solution.ElementTemperature[e] > peak)
                {
                    peak = solution.ElementTemperature[e];
                    at = (fe.Nodes[el.N0] + fe.Nodes[el.N1] + fe.Nodes[el.N2] + fe.Nodes[el.N3]) * 0.25;
                }
                oneWayPeak = Math.Max(oneWayPeak, solution.OneWayElementTemperature[e]);
                continue;
            }
            foreach (int n in new[] { el.N0, el.N1, el.N2, el.N3 })
            {
                if (solution.Temperature[n] > peak) { peak = solution.Temperature[n]; at = fe.Nodes[n]; }
                oneWayPeak = Math.Max(oneWayPeak, solution.OneWayTemperature[n]);
            }
        }

        IReadOnlyList<ComponentTemperature> partTemperatures = parts is null
            ? Array.Empty<ComponentTemperature>()
            : ComponentThermalAnalysis.Temperatures(board!, setup.Components, parts, solution.Temperature, partsAmbient).Parts;

        var assumptions = new List<string>
        {
            "steady state; copper resistivity linear in temperature " +
                $"(α = {copper.ResistivityTemperatureCoefficient ?? 0:g3} /K about " +
                $"{copper.ResistivityReferenceTemperature - 273.15:f0} °C), evaluated per element",
            board is not null
                ? "the whole board is the thermal body: every layer's copper as its share of the board's elements, so " +
                  "planes and other nets spread the heat" +
                  (board.Conductivity is not null ? ", trace copper conducting along its traces and across them in series" : "") +
                  (setup.Components.Count > 0 ? $", and {setup.Components.Count} part(s) on their two-resistor models" : "") +
                  "; this net's copper carries the current explicitly and its heat goes to the board elements that hold it"
                : hasLaminate
                ? "only this net's copper is in the mesh — no other nets, planes or components to spread or " +
                  "add heat — so the rise is an upper bound for a board that has them"
                : "copper-only mesh: there is no laminate in it, so the copper cools from its own surface " +
                  "alone, as a strip in free air — not the trace on its board; mesh the net with the board",
            setup.Cooling is not null
                ? "cooling by the solve given (a conjugate CFD solve of the air), with the environment's ambient"
                : setup.Environment is not null
                ? $"cooling by the environment ({setup.Environment.Describe()}): one convection coefficient per " +
                  "board face at that face's MEAN temperature, radiation per surface triangle" +
                  (setup.SurfaceEmissivity is { } eps ? $", surface emissivity {eps:g2} (solder mask) everywhere" : "")
                : "cooling by the thermal conditions given, nothing else",
            "via bores and the laminate's cut edges count as exposed surface (small areas)",
        };
        assumptions.AddRange(hot.Assumptions);

        return new RailThermalReport
        {
            Rail = hot,
            ColdRail = cold,
            StartKelvin = solution.StartTemperature,
            PeakCopperKelvin = peak,
            OneWayPeakCopperKelvin = oneWayPeak,
            HottestAt = new Point2(at.X, at.Y),
            HottestWhere = RailAnalysis.Where(mesh, at.Z),
            Iterations = solution.Iterations,
            Assumptions = assumptions,
            Log = solution.Log,
            Fields = solution.Fields,
            Solution = solution,
            Components = partTemperatures
        };
    }

    /// <summary>
    /// The rail's coupled transient from a uniform start (the settings' initial temperature):
    /// backward Euler for the pair, each step's copper loss at its own end temperature
    /// (<see cref="ElectroThermalStudy.SolveTransient"/>). Steady cooling only — the
    /// environment's film or the conditions given, not a CFD solve.
    /// </summary>
    public static RailThermalTransientReport SolveTransient(NetMesher.Result mesh, CopperNet net,
        RailThermalSetup setup, TransientThermalSettings settings,
        IProgress<OpenSim.Core.Interfaces.SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (setup.Cooling is not null)
            throw new InvalidOperationException("A coupled transient is cooled by the environment or the conditions given; the CFD cooling is steady.");
        var (input, _, _, copper, _) = Prepare(mesh, setup);
        var solution = ElectroThermalStudy.SolveTransient(input, settings, progress, cancellationToken);
        var rail = RailAnalysis.Report(mesh, net, setup.Rail, solution.Electrical, solution.ElementConductivity,
            temperatureCorrected: true);
        var assumptions = new List<string>
        {
            $"transient from {settings.InitialTemperature - 273.15:f1} °C everywhere, backward Euler in steps of {settings.TimeStep:g3} s; " +
            "each step's copper loss is solved at that step's own temperatures",
            $"copper resistivity linear in temperature (α = {copper.ResistivityTemperatureCoefficient ?? 0:g3} /K)",
            setup.Board is not null
                ? "the whole board is the thermal body (copper of every layer as its share of the elements)"
                : "the net's own mesh is the thermal body"
        };
        assumptions.AddRange(rail.Assumptions);
        return new RailThermalTransientReport
        {
            Solution = solution, Rail = rail, StartKelvin = settings.InitialTemperature, Assumptions = assumptions
        };
    }

    /// <summary>
    /// The same mesh with every pad face folded back into the top or bottom face it is
    /// part of. The electrical solve needs each pad as a face of its own; to the
    /// environment a pad is not a separate plate — treated as one, a 1 mm pad would get
    /// the film coefficient of a 1 mm plate, several times the board's.
    /// </summary>
    internal static FeMesh WithoutPadFaces(FeMesh mesh)
    {
        if (!mesh.BoundaryTriangles.Any(t => t.FaceId >= PcbMeshGenerator.PadFaceBase)) return mesh;
        var skin = new List<BoundaryTriangle>(mesh.BoundaryTriangles.Count);
        foreach (var t in mesh.BoundaryTriangles)
        {
            if (t.FaceId < PcbMeshGenerator.PadFaceBase) { skin.Add(t); continue; }
            var normal = OpenSim.Core.Numerics.Vector3D.Cross(
                mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]);
            skin.Add(t with { FaceId = normal.Z > 0 ? 0 : 1 });
        }
        return new FeMesh(mesh.Nodes, mesh.Elements, skin, mesh.ElementRegionIds);
    }
}
