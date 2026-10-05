using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>The input of a two-way coupled DC conduction and steady heat solve.</summary>
public sealed record ElectroThermalInput
{
    /// <summary>The mesh, with the face ids the terminals name.</summary>
    public required FeMesh Mesh { get; init; }

    /// <summary>The SAME nodes and elements with the face ids the thermal conditions and
    /// the environment should see, when those differ (a board mesh tags every pad as a
    /// face of its own, which the environment would treat as a separate small plate).
    /// Null uses <see cref="Mesh"/>.</summary>
    public FeMesh? ThermalMesh { get; init; }

    public required Material Material { get; init; }
    public IReadOnlyDictionary<int, Material>? RegionMaterials { get; init; }

    public required IReadOnlyList<ConductionTerminal> Terminals { get; init; }

    /// <summary>Fixed temperatures, heat flows and convection placed by hand.</summary>
    public IReadOnlyList<BoundaryCondition> ThermalConditions { get; init; } = Array.Empty<BoundaryCondition>();

    /// <summary>Surroundings (natural or forced convection, radiation) on every face the
    /// conditions above do not claim.</summary>
    public EnvironmentSettings? Environment { get; init; }

    /// <summary>Heat that is not Joule heat, per element [W/m³] (component losses).</summary>
    public IReadOnlyList<double>? ExtraHeatSource { get; init; }

    /// <summary>The temperature [K] the first electrical solve assumes everywhere — the
    /// "cold" state the uncoupled answer belongs to. Null: the environment's ambient, or
    /// 293.15 K without one.</summary>
    public double? StartTemperature { get; init; }

    /// <summary>The loop stops when no node moves by more than this [K] in one pass.</summary>
    public double Tolerance { get; init; } = 0.01;

    public int MaxIterations { get; init; } = 60;
}

/// <summary>Both answers of an electro-thermal solve: with and without the feedback.</summary>
public sealed record ElectroThermalResult
{
    /// <summary>The electrical solution at the converged temperatures.</summary>
    public required TerminalConductionResult Electrical { get; init; }

    /// <summary>The converged nodal temperature [K].</summary>
    public required double[] Temperature { get; init; }

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

    public required double StartTemperature { get; init; }

    /// <summary>Electrical/thermal passes after the first.</summary>
    public required int Iterations { get; init; }

    /// <summary>The converged electrical fields followed by the thermal ones.</summary>
    public required IReadOnlyList<IResultField> Fields { get; init; }

    public required IReadOnlyList<string> Log { get; init; }
}

/// <summary>
/// Two-way coupled Joule heating at steady state: the conductor's resistivity follows its
/// temperature (<see cref="Material.ElectricalConductivityAt"/>), the Joule heat follows
/// the resistivity, and the two solves are repeated until the temperature stops moving.
/// <para>
/// The loop is a plain fixed point — solve the currents at the last temperature, solve the
/// temperature for that heat, repeat — started from the cold state, so its first pass IS
/// the one-way answer and is reported beside the converged one. With currents imposed
/// (loads) the heat grows with temperature, and the fixed point exists only while cooling
/// outruns that growth: when it does not, the iterates climb without limit, which is
/// thermal runaway and is reported as such rather than as a failure to converge.
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

    public static ElectroThermalResult Solve(ElectroThermalInput input,
        IProgress<SolverProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var mesh = input.Mesh;
        var thermalMesh = input.ThermalMesh ?? mesh;
        if (thermalMesh.NodeCount != mesh.NodeCount || thermalMesh.ElementCount != mesh.ElementCount)
            throw new ArgumentException("The thermal mesh must have the nodes and elements of the electrical mesh.");
        if (input.ExtraHeatSource is not null && input.ExtraHeatSource.Count != mesh.ElementCount)
            throw new ArgumentException("ExtraHeatSource must have one entry per element.");

        var log = new List<string>();
        int elements = mesh.ElementCount;
        Material MaterialOf(int e) =>
            input.RegionMaterials?.GetValueOrDefault(mesh.RegionOf(e)) ?? input.Material;

        // Which elements conduct, decided once at the reference conductivity.
        var materials = new Material[elements];
        double best = 0;
        for (int e = 0; e < elements; e++)
        {
            materials[e] = MaterialOf(e);
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

        double start = input.StartTemperature ?? input.Environment?.AmbientTemperature ?? 293.15;
        double[] SigmaAt(IReadOnlyList<double>? nodal)
        {
            var sigma = new double[elements];
            for (int e = 0; e < elements; e++)
            {
                if (!conducts[e]) continue;
                double t = start;
                if (nodal is not null)
                {
                    var el = mesh.Elements[e];
                    t = 0.25 * (nodal[el.N0] + nodal[el.N1] + nodal[el.N2] + nodal[el.N3]);
                }
                sigma[e] = materials[e].ElectricalConductivityAt(t);
            }
            return sigma;
        }

        var thermal = new HeatConductionSolver();
        SolveOutput Heat(TerminalConductionResult electrical)
        {
            var source = new double[elements];
            for (int e = 0; e < elements; e++)
                source[e] = electrical.ElementPowerDensity[e] + (input.ExtraHeatSource?[e] ?? 0);
            return thermal.Solve(new SolveInput
            {
                Mesh = thermalMesh,
                Material = input.Material,
                RegionMaterials = input.RegionMaterials,
                BoundaryConditions = input.ThermalConditions,
                Environment = input.Environment,
                ElementHeatSource = source
            }, cancellationToken: cancellationToken);
        }
        static double[] TemperatureOf(SolveOutput output) =>
            ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values.ToArray();

        // ---- Pass 0: cold currents, and the temperature they produce (one-way).
        progress?.Report(new SolverProgress("Cold electrical solve", 0.05));
        var coldSigma = SigmaAt(null);
        var cold = TerminalConductionSolver.Solve(mesh, coldSigma, input.Terminals,
            mesh.ElementRegionIds, cancellationToken);
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
        if (anyCoefficient)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                iterations++;
                sigmaNow = SigmaAt(temperature);
                electrical = TerminalConductionSolver.Solve(mesh, sigmaNow, input.Terminals,
                    mesh.ElementRegionIds, cancellationToken);
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
        var fields = new List<IResultField>(electrical.Fields);
        fields.AddRange(heat.Fields);
        fields.Add(new NodalScalarField("Temperature rise", "K", rise));

        log.AddRange(electrical.Log.Select(l => $"[Electrical] {l}"));
        log.AddRange(heat.Log.Select(l => $"[Thermal] {l}"));
        progress?.Report(new SolverProgress("Done", 1.0));
        return new ElectroThermalResult
        {
            Electrical = electrical,
            Temperature = temperature,
            ElementConductivity = sigmaNow,
            ColdElectrical = cold,
            ColdConductivity = coldSigma,
            OneWayTemperature = oneWay,
            StartTemperature = start,
            Iterations = iterations,
            Fields = fields,
            Log = log
        };
    }
}
