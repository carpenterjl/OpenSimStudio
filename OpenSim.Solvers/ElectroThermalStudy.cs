using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// The body the heat is solved on when it is not the electrical mesh: the whole board as a
/// thermal mesh (every layer's copper as a share of its elements, planes, other nets, parts),
/// while the current flows in the explicit copper of one net. The Joule heat is handed over by
/// <see cref="MeshTransfer"/> and the temperature handed back the same way.
/// </summary>
public sealed record SeparateThermalModel
{
    public required FeMesh Mesh { get; init; }
    public required Material Material { get; init; }
    public IReadOnlyDictionary<int, Material>? RegionMaterials { get; init; }

    /// <summary>Per-element conductivity tensor (an orthotropic board), or null for the
    /// materials' own conductivity.</summary>
    public IReadOnlyList<ConductivityTensor>? Conductivity { get; init; }
}

/// <summary>The input of a two-way coupled DC conduction and heat solve.</summary>
public sealed record ElectroThermalInput
{
    /// <summary>The mesh, with the face ids the terminals name.</summary>
    public required FeMesh Mesh { get; init; }

    /// <summary>The SAME nodes and elements with the face ids the thermal conditions and
    /// the environment should see, when those differ (a board mesh tags every pad as a
    /// face of its own, which the environment would treat as a separate small plate).
    /// Null uses <see cref="Mesh"/>. Not used with <see cref="ThermalModel"/>.</summary>
    public FeMesh? ThermalMesh { get; init; }

    /// <summary>A different body for the heat (see <see cref="SeparateThermalModel"/>). The
    /// thermal conditions, the environment and <see cref="ExtraHeatSource"/> then belong to
    /// its mesh.</summary>
    public SeparateThermalModel? ThermalModel { get; init; }

    public required Material Material { get; init; }
    public IReadOnlyDictionary<int, Material>? RegionMaterials { get; init; }

    public required IReadOnlyList<ConductionTerminal> Terminals { get; init; }

    /// <summary>Fixed temperatures, heat flows and convection placed by hand.</summary>
    public IReadOnlyList<BoundaryCondition> ThermalConditions { get; init; } = Array.Empty<BoundaryCondition>();

    /// <summary>Surroundings (natural or forced convection, radiation) on every face the
    /// conditions above do not claim.</summary>
    public EnvironmentSettings? Environment { get; init; }

    /// <summary>
    /// The heat solve of every pass, when it is not the steady conduction solve with the
    /// environment's correlation film: a conjugate CFD solve, for one. It is handed the
    /// thermal input (mesh, materials, conditions, the heat of the pass, and the environment)
    /// and must return the temperature as a "Temperature" field. Steady solves only.
    /// </summary>
    public Func<SolveInput, CancellationToken, SolveOutput>? Cooling { get; init; }

    /// <summary>Heat that is not Joule heat, per element of the thermal mesh [W/m³]
    /// (component losses).</summary>
    public IReadOnlyList<double>? ExtraHeatSource { get; init; }

    /// <summary>The temperature [K] the first electrical solve assumes everywhere — the
    /// "cold" state the uncoupled answer belongs to. Null: the environment's ambient, or
    /// 293.15 K without one.</summary>
    public double? StartTemperature { get; init; }

    /// <summary>The loop stops when no node moves by more than this [K] in one pass (and a
    /// transient step when its last repeat moved no node by more).</summary>
    public double Tolerance { get; init; } = 0.01;

    public int MaxIterations { get; init; } = 60;
}

/// <summary>Both answers of an electro-thermal solve: with and without the feedback.</summary>
public sealed record ElectroThermalResult
{
    /// <summary>The electrical solution at the converged temperatures.</summary>
    public required TerminalConductionResult Electrical { get; init; }

    /// <summary>The converged nodal temperature [K] of the thermal mesh.</summary>
    public required double[] Temperature { get; init; }

    /// <summary>The temperature of every electrical element [K]: its mean nodal temperature,
    /// or with a separate thermal model the thermal field at its centroid.</summary>
    public required double[] ElementTemperature { get; init; }

    /// <summary>σ per element at the converged temperature [S/m] (0 for insulators).</summary>
    public required double[] ElementConductivity { get; init; }

    /// <summary>The electrical solution with every conductor at the start temperature —
    /// what a DC solve without self-heating reports.</summary>
    public required TerminalConductionResult ColdElectrical { get; init; }

    /// <summary>σ per element at the start temperature.</summary>
    public required double[] ColdConductivity { get; init; }

    /// <summary>The temperature the cold solution's Joule heat produces [K]: one-way
    /// coupling, the first pass of the loop.</summary>
    public required double[] OneWayTemperature { get; init; }

    /// <summary><see cref="OneWayTemperature"/> at the electrical elements.</summary>
    public required double[] OneWayElementTemperature { get; init; }

    public required double StartTemperature { get; init; }

    /// <summary>Electrical/thermal passes after the first.</summary>
    public required int Iterations { get; init; }

    /// <summary>The converged electrical fields followed by the thermal ones, when both are
    /// on one mesh; with a separate thermal model, the electrical fields and the thermal
    /// model's temperature (and rise) interpolated to the electrical mesh's nodes.</summary>
    public required IReadOnlyList<IResultField> Fields { get; init; }

    /// <summary>The thermal fields on the thermal mesh (temperature, heat flux, film, rise).</summary>
    public required IReadOnlyList<IResultField> ThermalFields { get; init; }

    /// <summary>Joule heat the thermal model received in the last pass [W] — the copper loss,
    /// kept exactly by the transfer.</summary>
    public required double HeatIntoThermalWatts { get; init; }

    public required IReadOnlyList<string> Log { get; init; }
}

/// <summary>A coupled transient: the thermal frames and the electrical state at the end.</summary>
public sealed record ElectroThermalTransientResult
{
    /// <summary>The thermal transient's own output (frames over time on the thermal mesh).</summary>
    public required SolveOutput Thermal { get; init; }

    /// <summary>The electrical solution at the last step's temperatures.</summary>
    public required TerminalConductionResult Electrical { get; init; }

    /// <summary>σ per element of that solution [S/m].</summary>
    public required double[] ElementConductivity { get; init; }

    /// <summary>Per step: the time [s], the copper loss [W] and the hottest electrical
    /// element [K].</summary>
    public required IReadOnlyList<(double Time, double LossWatts, double PeakKelvin)> History { get; init; }

    /// <summary>Coupled electrical solves over all steps (the repeats of each step included).</summary>
    public required int ElectricalSolves { get; init; }

    public required IReadOnlyList<string> Log { get; init; }
}

/// <summary>
/// Two-way coupled Joule heating: the conductor's resistivity follows its temperature
/// (<see cref="Material.ElectricalConductivityAt"/>), the Joule heat follows the resistivity,
/// and the two solves are repeated until the temperature stops moving.
/// <para>
/// The steady loop is a plain fixed point — solve the currents at the last temperature, solve
/// the temperature for that heat, repeat — started from the cold state, so its first pass IS
/// the one-way answer and is reported beside the converged one. With currents imposed
/// (loads) the heat grows with temperature, and the fixed point exists only while cooling
/// outruns that growth: when it does not, the iterates climb without limit, which is
/// thermal runaway and is reported as such rather than as a failure to converge.
/// </para>
/// <para>
/// The transient is backward Euler for the pair: each step's heat is solved at that step's
/// own end temperature, repeating the step until the two agree.
/// </para>
/// <para>
/// Elements whose conductivity is more than 10⁸ below the best conductor are insulators:
/// they carry no current and only conduct heat (the laminate of a board).
/// </para>
/// </summary>
public static class ElectroThermalStudy
{
    /// <summary>A temperature no board survives; an iterate beyond it is a runaway.</summary>
    private const double RunawayKelvin = 2500;

    /// <summary>What one solve and the next share: which elements conduct, σ(T), the heat
    /// handed to the thermal mesh and the temperature handed back.</summary>
    private sealed class Coupling
    {
        public required FeMesh Mesh { get; init; }
        public required FeMesh ThermalMesh { get; init; }
        public required Material[] Materials { get; init; }
        public required bool[] Conducts { get; init; }
        public required bool AnyCoefficient { get; init; }
        public required double Start { get; init; }
        public MeshTransfer? Transfer { get; init; }
        public required ElectroThermalInput Input { get; init; }

        /// <summary>Temperature of each electrical element for a thermal nodal field.</summary>
        public double[] ElementTemperature(IReadOnlyList<double> thermalNodal)
        {
            if (Transfer is not null) return Transfer.ElementValues(thermalNodal);
            var values = new double[Mesh.ElementCount];
            for (int e = 0; e < values.Length; e++)
            {
                var el = Mesh.Elements[e];
                values[e] = 0.25 * (thermalNodal[el.N0] + thermalNodal[el.N1] + thermalNodal[el.N2] + thermalNodal[el.N3]);
            }
            return values;
        }

        public double[] SigmaAt(IReadOnlyList<double>? thermalNodal)
        {
            var elementT = thermalNodal is null ? null : ElementTemperature(thermalNodal);
            var sigma = new double[Mesh.ElementCount];
            for (int e = 0; e < sigma.Length; e++)
                if (Conducts[e]) sigma[e] = Materials[e].ElectricalConductivityAt(elementT?[e] ?? Start);
            return sigma;
        }

        public TerminalConductionResult Electrical(double[] sigma, CancellationToken token) =>
            TerminalConductionSolver.Solve(Mesh, sigma, Input.Terminals, Mesh.ElementRegionIds, token);

        /// <summary>Heat per thermal element [W/m³]: the Joule heat, handed over, plus the rest.</summary>
        public double[] HeatSource(TerminalConductionResult electrical)
        {
            var joule = Transfer is not null ? Transfer.Heat(electrical.ElementPowerDensity) : electrical.ElementPowerDensity.ToArray();
            if (Input.ExtraHeatSource is { } extra)
                for (int e = 0; e < joule.Length; e++) joule[e] += extra[e];
            return joule;
        }

        public SolveInput ThermalInput(double[] source) => Input.ThermalModel is { } model
            ? new SolveInput
            {
                Mesh = model.Mesh,
                Material = model.Material,
                RegionMaterials = model.RegionMaterials,
                ElementThermalConductivity = model.Conductivity,
                BoundaryConditions = Input.ThermalConditions,
                Environment = Input.Environment,
                ElementHeatSource = source
            }
            : new SolveInput
            {
                Mesh = ThermalMesh,
                Material = Input.Material,
                RegionMaterials = Input.RegionMaterials,
                BoundaryConditions = Input.ThermalConditions,
                Environment = Input.Environment,
                ElementHeatSource = source
            };
    }

    private static Coupling Prepare(ElectroThermalInput input, List<string> log)
    {
        var mesh = input.Mesh;
        FeMesh thermalMesh;
        MeshTransfer? transfer = null;
        if (input.ThermalModel is { } model)
        {
            if (input.ThermalMesh is not null)
                throw new ArgumentException("Give a thermal mesh with the electrical mesh's nodes, or a separate thermal model, not both.");
            thermalMesh = model.Mesh;
            transfer = MeshTransfer.Build(mesh, thermalMesh);
            log.Add($"Separate thermal model: {thermalMesh.ElementCount} elements; the Joule heat of {mesh.ElementCount} " +
                    "electrical elements is handed to the thermal element holding each one's centroid" +
                    (transfer.Outside > 0 ? $" ({transfer.Outside} lie outside it and went to the nearest)." : "."));
        }
        else
        {
            thermalMesh = input.ThermalMesh ?? mesh;
            if (thermalMesh.NodeCount != mesh.NodeCount || thermalMesh.ElementCount != mesh.ElementCount)
                throw new ArgumentException("The thermal mesh must have the nodes and elements of the electrical mesh.");
        }
        if (input.ExtraHeatSource is not null && input.ExtraHeatSource.Count != thermalMesh.ElementCount)
            throw new ArgumentException("ExtraHeatSource must have one entry per element of the thermal mesh.");

        int elements = mesh.ElementCount;
        var materials = new Material[elements];
        double best = 0;
        for (int e = 0; e < elements; e++)
        {
            materials[e] = input.RegionMaterials?.GetValueOrDefault(mesh.RegionOf(e)) ?? input.Material;
            best = Math.Max(best, materials[e].ElectricalConductivity ?? 0);
        }
        if (!(best > 0))
            throw new InvalidOperationException(
                "No material in the mesh has an electrical conductivity; nothing can carry the current.");
        var conducts = new bool[elements];
        bool anyCoefficient = false;
        for (int e = 0; e < elements; e++)
        {
            double sigma = materials[e].ElectricalConductivity ?? 0;
            conducts[e] = sigma * ElectricalConductionSolver.ConductivitySpreadLimit >= best;
            anyCoefficient |= conducts[e] && materials[e].ResistivityTemperatureCoefficient is not null;
        }
        if (!anyCoefficient)
            log.Add("No conductor has a resistivity temperature coefficient: the conductivity does not " +
                    "follow the temperature, and the coupled answer is the one-way answer.");

        return new Coupling
        {
            Mesh = mesh, ThermalMesh = thermalMesh, Materials = materials, Conducts = conducts,
            AnyCoefficient = anyCoefficient, Transfer = transfer, Input = input,
            Start = input.StartTemperature ?? input.Environment?.AmbientTemperature ?? 293.15
        };
    }

    public static ElectroThermalResult Solve(ElectroThermalInput input,
        IProgress<SolverProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        var coupling = Prepare(input, log);
        double start = coupling.Start;

        var thermal = new HeatConductionSolver();
        double heatWatts = 0;
        SolveOutput Heat(TerminalConductionResult electrical)
        {
            var source = coupling.HeatSource(electrical);
            heatWatts = 0;
            for (int e = 0; e < source.Length; e++) heatWatts += source[e] * coupling.ThermalMesh.ElementVolume(e);
            var thermalInput = coupling.ThermalInput(source);
            return input.Cooling is { } cooling
                ? cooling(thermalInput, cancellationToken)
                : thermal.Solve(thermalInput, cancellationToken: cancellationToken);
        }
        static double[] TemperatureOf(SolveOutput output) =>
            ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values.ToArray();

        // ---- Pass 0: cold currents, and the temperature they produce (one-way).
        progress?.Report(new SolverProgress("Cold electrical solve", 0.05));
        var coldSigma = coupling.SigmaAt(null);
        var cold = coupling.Electrical(coldSigma, cancellationToken);
        var heat = Heat(cold);
        var oneWay = TemperatureOf(heat);
        log.Add($"Cold ({start:F2} K everywhere): {cold.TotalPower:g5} W of Joule heat; " +
                $"one-way peak temperature {oneWay.Max():F2} K.");

        // ---- The loop.
        var temperature = oneWay;
        var electrical = cold;
        var sigmaNow = coldSigma;
        int iterations = 0;
        double change = double.PositiveInfinity;
        if (coupling.AnyCoefficient)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                iterations++;
                sigmaNow = coupling.SigmaAt(temperature);
                electrical = coupling.Electrical(sigmaNow, cancellationToken);
                heat = Heat(electrical);
                var next = TemperatureOf(heat);

                change = 0;
                double peak = double.NegativeInfinity;
                for (int n = 0; n < next.Length; n++)
                {
                    change = Math.Max(change, Math.Abs(next[n] - temperature[n]));
                    peak = Math.Max(peak, next[n]);
                }
                temperature = next;
                progress?.Report(new SolverProgress(
                    $"Electro-thermal pass {iterations} (Δ = {change:g3} K, peak {peak:F1} K)",
                    Math.Min(0.95, 0.1 + 0.85 * iterations / 12.0)));

                if (change < input.Tolerance) break;
                if (!(peak < RunawayKelvin))
                    throw new InvalidOperationException(
                        $"Thermal runaway: after {iterations} passes the conductor is at {peak:F0} K and still " +
                        "rising. With the load currents fixed, the heat grows with temperature faster than " +
                        "the cooling removes it, so there is no steady state. Reduce the current, widen the " +
                        "copper, or improve the cooling.");
                if (iterations >= input.MaxIterations)
                    throw new InvalidOperationException(
                        $"The electro-thermal loop did not settle in {iterations} passes (still moving by " +
                        $"{change:g3} K, peak {peak:F1} K). The design is close to thermal runaway: each pass " +
                        "adds almost as much heat as the last.");
            }
            log.Add($"Converged in {iterations} electro-thermal pass(es) (last change {change:g3} K): " +
                    $"{electrical.TotalPower:g5} W of Joule heat, peak temperature {temperature.Max():F2} K.");
        }

        var rise = new double[temperature.Length];
        for (int n = 0; n < rise.Length; n++) rise[n] = temperature[n] - start;
        var thermalFields = new List<IResultField>(heat.Fields)
        {
            new NodalScalarField("Temperature rise", "K", rise)
        };
        var fields = new List<IResultField>(electrical.Fields);
        if (input.ThermalModel is null) fields.AddRange(thermalFields);
        else
        {
            // The thermal model's temperature where the electrical mesh is, so its body shows it too.
            var onMesh = MeshTransfer.AtNodes(coupling.Mesh, coupling.ThermalMesh, temperature);
            fields.Add(new NodalScalarField("Temperature", "K", onMesh));
            fields.Add(new NodalScalarField("Temperature rise", "K", onMesh.Select(v => v - start).ToArray()));
        }

        log.AddRange(electrical.Log.Select(l => $"[Electrical] {l}"));
        log.AddRange(heat.Log.Select(l => $"[Thermal] {l}"));
        progress?.Report(new SolverProgress("Done", 1.0));
        return new ElectroThermalResult
        {
            Electrical = electrical,
            Temperature = temperature,
            ElementTemperature = coupling.ElementTemperature(temperature),
            ElementConductivity = sigmaNow,
            ColdElectrical = cold,
            ColdConductivity = coldSigma,
            OneWayTemperature = oneWay,
            OneWayElementTemperature = coupling.ElementTemperature(oneWay),
            StartTemperature = start,
            Iterations = iterations,
            Fields = fields,
            ThermalFields = thermalFields,
            HeatIntoThermalWatts = heatWatts,
            Log = log
        };
    }

    /// <summary>
    /// The coupled transient from a uniform start at <see cref="ElectroThermalInput.StartTemperature"/>
    /// (or the settings' initial temperature): backward Euler for the pair, each step's Joule
    /// heat solved at the step's own end temperature by repeating the step until the
    /// temperatures it is given and the ones it produces agree to the tolerance. The thermal
    /// mesh's materials need a density and a specific heat.
    /// </summary>
    public static ElectroThermalTransientResult SolveTransient(ElectroThermalInput input,
        TransientThermalSettings settings, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (input.Cooling is not null)
            throw new ArgumentException("A coupled transient is solved with the environment's cooling; a cooling solve is for the steady state.");
        if (settings.PowerProfile is { Kind: not PowerProfileKind.Constant })
            throw new ArgumentException("A power profile does not apply to Joule heat, which follows the circuit; give the extra heat its own solve.");
        var log = new List<string>();
        var coupling = Prepare(input with { StartTemperature = input.StartTemperature ?? settings.InitialTemperature }, log);

        TerminalConductionResult? electrical = null;
        double[]? sigmaNow = null;
        IReadOnlyList<double>? given = null;
        int solves = 0, repeats = 0;
        var history = new List<(double, double, double)>();
        double lastPeak = coupling.Start;

        IReadOnlyList<double> Schedule(int step, double time, IReadOnlyList<double> temperature)
        {
            cancellationToken.ThrowIfCancellationRequested();
            given = temperature;
            var sigma = coupling.SigmaAt(temperature);
            sigmaNow = sigma;
            electrical = coupling.Electrical(sigma, cancellationToken);
            solves++;
            return coupling.HeatSource(electrical);
        }

        bool Accepted(int step, IReadOnlyList<double> produced)
        {
            double change = 0, peak = double.NegativeInfinity;
            for (int n = 0; n < produced.Count; n++)
            {
                change = Math.Max(change, Math.Abs(produced[n] - given![n]));
                peak = Math.Max(peak, produced[n]);
            }
            if (!(peak < RunawayKelvin))
                throw new InvalidOperationException(
                    $"Thermal runaway at step {step}: the conductor is at {peak:F0} K and still rising.");
            bool accepted = !coupling.AnyCoefficient || change < input.Tolerance;
            if (!accepted)
            {
                repeats++;
                if (repeats > input.MaxIterations * Math.Max(1, step))
                    throw new InvalidOperationException(
                        $"Step {step}: the coupled step did not settle (still moving by {change:g3} K). Reduce the time step.");
                return false;
            }
            var elementT = coupling.ElementTemperature(produced);
            double hottest = double.NegativeInfinity;
            for (int e = 0; e < elementT.Length; e++)
                if (coupling.Conducts[e]) hottest = Math.Max(hottest, elementT[e]);
            history.Add((step * settings.TimeStep, electrical!.TotalPower, hottest));
            lastPeak = hottest;
            return true;
        }

        var thermalInput = coupling.ThermalInput(new double[coupling.ThermalMesh.ElementCount]) with
        {
            ElementHeatSource = null,
            TransientThermal = settings with { InitialTemperature = coupling.Start },
            ElementHeatSourceSchedule = Schedule,
            HeatSourceStepAccepted = Accepted
        };
        var output = new TransientThermalSolver().Solve(thermalInput, progress, cancellationToken);
        log.Add($"Coupled transient: {history.Count} steps, {solves} electrical solves " +
                $"({(double)solves / Math.Max(1, history.Count):F1} per step); hottest conductor {lastPeak:F2} K at the end.");
        log.AddRange(output.Log.Select(l => $"[Thermal] {l}"));
        return new ElectroThermalTransientResult
        {
            Thermal = output,
            Electrical = electrical!,
            ElementConductivity = sigmaNow!,
            History = history,
            ElectricalSolves = solves,
            Log = log
        };
    }
}
