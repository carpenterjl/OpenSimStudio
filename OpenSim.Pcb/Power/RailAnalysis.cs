using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Pcb.Extrude;
using OpenSim.Pcb.Import;
using OpenSim.Solvers;

namespace OpenSim.Pcb.Power;

/// <summary>A supply on the rail: a regulator output (one pad or several tied together by
/// the part) with its set-point and optional output resistance.</summary>
public sealed record RailSource(string Name, IReadOnlyList<NetMesher.PadElectrode> Pads, double Volts)
{
    /// <summary>Output (series) resistance [Ω]; 0 = an ideal voltage at the pads.</summary>
    public double OutputResistance { get; init; }
}

/// <summary>A load on the rail: the pads of one consumer and the current it draws.</summary>
public sealed record RailSink(string Name, IReadOnlyList<NetMesher.PadElectrode> Pads, double Amps)
{
    /// <summary>The lowest acceptable voltage at this load [V]; null uses the rail's
    /// <see cref="RailSetup.DropLimitPercent"/>.</summary>
    public double? MinimumVolts { get; init; }
}

/// <summary>What to solve and what to hold the result against.</summary>
public sealed record RailSetup
{
    public required IReadOnlyList<RailSource> Sources { get; init; }
    public required IReadOnlyList<RailSink> Sinks { get; init; }

    /// <summary>The voltage drops are measured from [V]; 0 = the highest source set-point.</summary>
    public double NominalVolts { get; init; }

    /// <summary>Allowed drop at a load, percent of the nominal voltage. A budget the
    /// designer sets, not a standard.</summary>
    public double DropLimitPercent { get; init; } = 3.0;

    /// <summary>Current density above which copper is reported as a neck-down [A/m²];
    /// 0 = report the peak only. The default, 50 A/mm², is a common screening figure for
    /// outer-layer copper and is not a standard either.</summary>
    public double CurrentDensityLimit { get; init; } = 50e6;

    /// <summary>Current above which a via is flagged [A]; 0 = not checked.</summary>
    public double ViaCurrentLimit { get; init; }

    /// <summary>Copper conductivity [S/m] at the temperature the rail runs at.</summary>
    public double CopperConductivity { get; init; } = 5.96e7;
}

/// <param name="Volts">Voltage at the load's pads; NaN when its copper reaches no source.</param>
/// <param name="LimitVolts">The voltage it was held against.</param>
public sealed record RailSinkResult(string Name, double Amps, double Volts, double DropVolts,
    double DropPercent, double LimitVolts, bool Pass);

/// <param name="PadVolts">Voltage at the source's pads (below the set-point by the drop in
/// its output resistance).</param>
/// <param name="Amps">Current the source delivers.</param>
public sealed record RailSourceResult(string Name, double SetVolts, double PadVolts, double Amps,
    double OutputLossWatts);

/// <summary>The current one plated via carries, the largest over the dielectric gaps it crosses.</summary>
public sealed record RailViaResult(Point2 Position, double Diameter, int FromLayer, int ToLayer,
    double Amps, bool OverLimit);

/// <summary>A place where the current density is highest: a connected patch of copper
/// over the limit, or (with nothing over it) the single densest spot.</summary>
/// <param name="Where">"L2", or "via L1–L2" inside a barrel.</param>
/// <param name="PeakDensity">Highest element current density in the patch [A/m²].</param>
/// <param name="MeanDensity">Volume mean over the patch [A/m²].</param>
/// <param name="ExtentMeters">Diagonal of the patch's bounding box.</param>
public sealed record RailHotSpot(Point2 Position, string Where, double PeakDensity, double MeanDensity,
    double ExtentMeters, int Elements, bool OverLimit);

/// <summary>The rail-level DC result.</summary>
public sealed record RailReport
{
    public required double NominalVolts { get; init; }
    public required IReadOnlyList<RailSourceResult> Sources { get; init; }
    public required IReadOnlyList<RailSinkResult> Sinks { get; init; }
    public required IReadOnlyList<RailViaResult> Vias { get; init; }
    public required IReadOnlyList<RailHotSpot> HotSpots { get; init; }

    /// <summary>Number of separate copper patches over the current-density limit
    /// (<see cref="HotSpots"/> lists the worst of them).</summary>
    public required int PatchesOverLimit { get; init; }

    /// <summary>Power dissipated in the copper [W].</summary>
    public required double CopperLossWatts { get; init; }

    /// <summary>Every load within its voltage limit, no copper over the density limit, no
    /// via over its current limit.</summary>
    public required bool Pass { get; init; }

    public required IReadOnlyList<string> Assumptions { get; init; }
    public required IReadOnlyList<string> Log { get; init; }

    /// <summary>Potential, current density and power density on the mesh.</summary>
    public required IReadOnlyList<IResultField> Fields { get; init; }

    /// <summary>The underlying terminal solve (conductance matrix, element fields).</summary>
    public required TerminalConductionResult Solution { get; init; }

    /// <summary>One line per load, worst first, then sources, vias and neck-downs — the
    /// text the panel and the log show.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            $"Rail {(Pass ? "PASS" : "FAIL")} — nominal {NominalVolts:g5} V, copper loss {CopperLossWatts * 1e3:g4} mW."
        };
        foreach (var s in Sinks.OrderBy(s => double.IsNaN(s.Volts) ? double.NegativeInfinity : s.Volts - s.LimitVolts))
            lines.Add(double.IsNaN(s.Volts)
                ? $"  load {s.Name}: NOT CONNECTED to a source."
                : $"  load {s.Name}: {s.Volts:g6} V at {s.Amps:g4} A — drop {s.DropVolts * 1e3:g4} mV " +
                  $"({s.DropPercent:f2} %), limit {s.LimitVolts:g5} V: {(s.Pass ? "pass" : "FAIL")}.");
        foreach (var s in Sources)
            lines.Add($"  source {s.Name}: {s.Amps:g4} A at {s.PadVolts:g6} V" +
                      (s.OutputLossWatts > 0 ? $" (set {s.SetVolts:g5} V, {s.OutputLossWatts * 1e3:g3} mW in its output resistance)." : "."));
        foreach (var v in Vias.OrderByDescending(v => v.Amps).Take(10))
            lines.Add($"  via ({v.Position.X * 1e3:f2}, {v.Position.Y * 1e3:f2}) mm L{v.FromLayer}–L{v.ToLayer}, " +
                      $"⌀{v.Diameter * 1e3:g3} mm: {v.Amps:g4} A{(v.OverLimit ? " — OVER LIMIT" : "")}.");
        if (Vias.Count > 10) lines.Add($"  …and {Vias.Count - 10} more vias carrying less.");
        foreach (var h in HotSpots)
            lines.Add($"  {(h.OverLimit ? "neck-down" : "densest copper")} on {h.Where} at " +
                      $"({h.Position.X * 1e3:f2}, {h.Position.Y * 1e3:f2}) mm: peak {h.PeakDensity * 1e-6:g4} A/mm², " +
                      $"mean {h.MeanDensity * 1e-6:g4} A/mm² over {h.ExtentMeters * 1e3:g3} mm ({h.Elements} elements).");
        if (PatchesOverLimit > HotSpots.Count)
            lines.Add($"  …and {PatchesOverLimit - HotSpots.Count} more patches over the limit.");
        return lines;
    }
}

/// <summary>
/// Rail-level DC power integrity on one meshed net: every source and load at once, the
/// voltage at each load against a limit, the current in each via, and where the copper is
/// most heavily loaded.
/// <para>
/// The field solve is <see cref="TerminalConductionSolver"/> on the net's copper mesh
/// (<see cref="NetMesher"/>): each terminal an equipotential, sources with their output
/// resistance, loads as currents. What this class adds is the board's vocabulary — pads,
/// layers, vias — on both sides of that solve.
/// </para>
/// </summary>
public static class RailAnalysis
{
    /// <summary>How many current-density patches the report lists.</summary>
    public const int MaxHotSpots = 10;

    /// <param name="mesh">The net as meshed by <see cref="NetMesher"/>; the terminals'
    /// pads are matched to its electrodes by layer and position, so pads picked on another
    /// mesh of the same net (a refinement, the board body) still resolve.</param>
    /// <param name="elementConductivity">σ per element [S/m] to solve with instead of the
    /// uniform <see cref="RailSetup.CopperConductivity"/> — the electro-thermal study
    /// passes σ(T) here. Zero marks an element that does not conduct.</param>
    public static RailReport Solve(NetMesher.Result mesh, CopperNet net, RailSetup setup,
        IReadOnlyList<double>? elementConductivity = null, CancellationToken cancellationToken = default)
    {
        var fe = mesh.Body.Mesh ?? throw new InvalidOperationException("The net has no mesh.");
        var terminals = Terminals(mesh, setup);
        var sigma = elementConductivity ?? UniformConductivity(fe, setup.CopperConductivity);
        // Region ids double as material groups: nodal averages never cross copper/laminate.
        var solution = TerminalConductionSolver.Solve(fe, sigma, terminals,
            fe.ElementRegionIds, cancellationToken);
        return Report(mesh, net, setup, solution, sigma, temperatureCorrected: elementConductivity is not null);
    }

    /// <summary>The setup as solver terminals — sources first, then loads, in setup order —
    /// after checking it.</summary>
    public static IReadOnlyList<ConductionTerminal> Terminals(NetMesher.Result mesh, RailSetup setup)
    {
        if (mesh.Body.Mesh is null) throw new InvalidOperationException("The net has no mesh.");
        if (setup.Sources.Count == 0)
            throw new InvalidOperationException("A rail needs at least one source.");
        if (setup.Sinks.Count == 0)
            throw new InvalidOperationException("A rail needs at least one load.");
        if (!(setup.CopperConductivity > 0))
            throw new InvalidOperationException("The copper conductivity must be positive.");
        foreach (var sink in setup.Sinks)
            if (!(sink.Amps >= 0))
                throw new InvalidOperationException(
                    $"Load '{sink.Name}': the current must be zero or positive (a load draws current; " +
                    "something that supplies is a source).");

        var terminals = new List<ConductionTerminal>();
        foreach (var source in setup.Sources)
            terminals.Add(new ConductionTerminal
            {
                Name = source.Name,
                FaceIds = Faces(mesh, source.Name, source.Pads),
                SourceVolts = source.Volts,
                SeriesResistance = source.OutputResistance
            });
        foreach (var sink in setup.Sinks)
            terminals.Add(new ConductionTerminal
            {
                Name = sink.Name,
                FaceIds = Faces(mesh, sink.Name, sink.Pads),
                LoadCurrent = sink.Amps
            });
        return terminals;
    }

    /// <summary>
    /// Reads a terminal solve of this setup (terminals as <see cref="Terminals"/> orders
    /// them) back in the board's terms.
    /// </summary>
    /// <param name="sigma">The σ per element the solve used.</param>
    public static RailReport Report(NetMesher.Result mesh, CopperNet net, RailSetup setup,
        TerminalConductionResult solution, IReadOnlyList<double> sigma, bool temperatureCorrected)
    {
        var fe = mesh.Body.Mesh ?? throw new InvalidOperationException("The net has no mesh.");
        double nominal = setup.NominalVolts > 0 ? setup.NominalVolts : setup.Sources.Max(s => s.Volts);
        var sources = new List<RailSourceResult>();
        for (int i = 0; i < setup.Sources.Count; i++)
        {
            var state = solution.Terminals[i];
            var source = setup.Sources[i];
            sources.Add(new RailSourceResult(source.Name, source.Volts, state.Volts, state.Amps,
                source.OutputResistance * state.Amps * state.Amps));
        }
        var sinks = new List<RailSinkResult>();
        for (int i = 0; i < setup.Sinks.Count; i++)
        {
            var state = solution.Terminals[setup.Sources.Count + i];
            var sink = setup.Sinks[i];
            double limit = sink.MinimumVolts ?? nominal * (1 - setup.DropLimitPercent / 100.0);
            double drop = nominal - state.Volts;
            sinks.Add(new RailSinkResult(sink.Name, sink.Amps, state.Volts, drop,
                100.0 * drop / nominal, limit, state.Connected && state.Volts >= limit));
        }

        var centroids = new Vector3D[fe.ElementCount];
        for (int e = 0; e < fe.ElementCount; e++)
        {
            var el = fe.Elements[e];
            centroids[e] = (fe.Nodes[el.N0] + fe.Nodes[el.N1] + fe.Nodes[el.N2] + fe.Nodes[el.N3]) * 0.25;
        }
        var vias = ViaCurrents(mesh, net, solution, centroids, setup.ViaCurrentLimit);
        var (hotSpots, patches) = HotSpots(mesh, fe, solution, sigma, centroids, setup.CurrentDensityLimit);

        var assumptions = new List<string>
        {
            "every source and load pad is an equipotential (a soldered pin); the pads of one terminal " +
                "are tied together off the board",
            "a pad electrode is the mesh faces whose centre lies in the pad, so its edge is resolved to " +
                "a fraction of an element — refine the mesh when a span between pads is only a few elements long",
            "DC conduction in this net's copper only — the return path's drop is a second run on the " +
                "ground net and adds to what the load sees",
            !temperatureCorrected
                ? $"uniform copper conductivity {setup.CopperConductivity:g4} S/m (no self-heating); " +
                  "copper thickness from the stackup"
                : "conductivity per element as supplied (temperature-corrected); copper thickness from the stackup",
            mesh.ViaPlating > 0
                ? $"vias are plated barrels with a {mesh.ViaPlating * 1e6:g3} µm wall, the bore open"
                : "no via barrels in this mesh",
            "a current-density peak at a re-entrant corner grows as the mesh is refined; the patch mean " +
                "and extent are the figures that hold",
            $"limits are settings, not standards: {setup.DropLimitPercent:g3} % drop" +
                (setup.CurrentDensityLimit > 0 ? $", {setup.CurrentDensityLimit * 1e-6:g3} A/mm²" : "") +
                (setup.ViaCurrentLimit > 0 ? $", {setup.ViaCurrentLimit:g3} A per via" : "")
        };

        return new RailReport
        {
            NominalVolts = nominal,
            Sources = sources,
            Sinks = sinks,
            Vias = vias,
            HotSpots = hotSpots,
            PatchesOverLimit = patches,
            CopperLossWatts = solution.TotalPower,
            Pass = sinks.All(s => s.Pass) && patches == 0 && !vias.Any(v => v.OverLimit),
            Assumptions = assumptions,
            Log = solution.Log,
            Fields = solution.Fields,
            Solution = solution
        };
    }

    /// <summary>σ on copper-region elements, zero elsewhere.</summary>
    public static double[] UniformConductivity(OpenSim.Core.Model.FeMesh mesh, double copperConductivity)
    {
        var sigma = new double[mesh.ElementCount];
        for (int e = 0; e < sigma.Length; e++)
            sigma[e] = mesh.RegionOf(e) == PcbStackup.CopperRegion ? copperConductivity : 0.0;
        return sigma;
    }

    /// <summary>The mesh face of each pad of a terminal, matched by layer and position.</summary>
    private static IReadOnlyList<int> Faces(NetMesher.Result mesh, string terminal,
        IReadOnlyList<NetMesher.PadElectrode> pads)
    {
        if (pads.Count == 0)
            throw new InvalidOperationException($"Terminal '{terminal}' has no pads.");
        const double tolerance = 1e-6;
        var faces = new List<int>(pads.Count);
        foreach (var pad in pads)
        {
            NetMesher.PadElectrode? match = null;
            double best = tolerance;
            foreach (var candidate in mesh.Pads)
            {
                if (candidate.LayerOrder != pad.LayerOrder) continue;
                double distance = (candidate.Center - pad.Center).Length;
                if (distance <= best) { best = distance; match = candidate; }
            }
            if (match is null)
                throw new InvalidOperationException(
                    $"Terminal '{terminal}': the pad {pad.Label} is not an electrode of this mesh (it is not " +
                    "on this net, or it sits on an inner layer where no pad face is exposed).");
            if (!faces.Contains(match.FaceId)) faces.Add(match.FaceId);
        }
        return faces;
    }

    /// <summary>
    /// Current through each stitching via: in every dielectric gap the barrel crosses, the
    /// through-thickness current is the volume integral of J_z over the wall divided by the
    /// gap height (exact for the element-wise constant J of the solve); the via's figure is
    /// the largest of its gaps.
    /// </summary>
    private static List<RailViaResult> ViaCurrents(NetMesher.Result mesh, CopperNet net,
        TerminalConductionResult solution, Vector3D[] centroids, double limit)
    {
        var result = new List<RailViaResult>();
        if (net.StitchingVias.Count == 0 || mesh.LayerZ.Count < 2) return result;
        var fe = mesh.Body.Mesh!;

        // Only barrel walls conduct between layers, so "an element inside a gap that carries
        // current" is a barrel element. Bucket them by gap once.
        var gaps = new List<(int Upper, double zLo, double zHi, List<int> Elements)>();
        foreach (int upper in mesh.LayerZ.Keys.OrderBy(k => k))
        {
            if (!mesh.LayerZ.TryGetValue(upper + 1, out var below)) continue;
            gaps.Add((upper, below.zHi, mesh.LayerZ[upper].zLo, new List<int>()));
        }
        for (int e = 0; e < fe.ElementCount; e++)
        {
            if (solution.ElementPowerDensity[e] == 0 && solution.ElementCurrentDensity[e].Length == 0) continue;
            double z = centroids[e].Z;
            foreach (var gap in gaps)
                if (z > gap.zLo && z < gap.zHi) { gap.Elements.Add(e); break; }
        }

        foreach (var bridge in net.StitchingVias)
        {
            double inner = bridge.Via.Diameter / 2, outer = inner + mesh.ViaPlating;
            double slack = 0.25 * mesh.ViaPlating;
            int from = bridge.Layers.Min(), to = bridge.Layers.Max();
            double amps = 0;
            bool found = false;
            foreach (var gap in gaps)
            {
                if (gap.Upper < from || gap.Upper >= to) continue;
                double integral = 0;
                foreach (int e in gap.Elements)
                {
                    double dx = centroids[e].X - bridge.Via.Position.X, dy = centroids[e].Y - bridge.Via.Position.Y;
                    double r = Math.Sqrt(dx * dx + dy * dy);
                    if (r < inner - slack || r > outer + slack) continue;
                    integral += solution.ElementCurrentDensity[e].Z * fe.ElementVolume(e);
                    found = true;
                }
                amps = Math.Max(amps, Math.Abs(integral) / (gap.zHi - gap.zLo));
            }
            if (!found) continue;                       // the mesh fell back to one layer: no barrel
            result.Add(new RailViaResult(bridge.Via.Position, bridge.Via.Diameter, from, to, amps,
                limit > 0 && amps > limit));
        }
        return result;
    }

    /// <summary>
    /// Patches of copper over the current-density limit (elements over it that share a
    /// node are one patch), worst peak first; with nothing over the limit, the single
    /// densest element so the report always says how close the copper runs.
    /// </summary>
    private static (List<RailHotSpot> Spots, int PatchesOverLimit) HotSpots(NetMesher.Result mesh,
        OpenSim.Core.Model.FeMesh fe, TerminalConductionResult solution, IReadOnlyList<double> sigma,
        Vector3D[] centroids, double limit)
    {
        int elements = fe.ElementCount;
        var density = new double[elements];
        int densest = -1;
        for (int e = 0; e < elements; e++)
        {
            if (sigma[e] == 0) continue;
            density[e] = solution.ElementCurrentDensity[e].Length;
            if (densest < 0 || density[e] > density[densest]) densest = e;
        }
        var spots = new List<RailHotSpot>();
        if (densest < 0) return (spots, 0);

        var over = new List<int>();
        if (limit > 0)
            for (int e = 0; e < elements; e++)
                if (density[e] > limit) over.Add(e);
        if (over.Count == 0)
        {
            spots.Add(Patch(mesh, fe, new[] { densest }, density, centroids, overLimit: false));
            return (spots, 0);
        }

        // Union elements through shared nodes.
        var parent = new Dictionary<int, int>();          // element → root element
        var ownerOfNode = new Dictionary<int, int>();
        int Find(int e)
        {
            while (parent[e] != e) { parent[e] = parent[parent[e]]; e = parent[e]; }
            return e;
        }
        foreach (int e in over)
        {
            parent[e] = e;
            var el = fe.Elements[e];
            foreach (int n in new[] { el.N0, el.N1, el.N2, el.N3 })
            {
                if (ownerOfNode.TryGetValue(n, out int other))
                {
                    int a = Find(e), b = Find(other);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
                else ownerOfNode[n] = e;
            }
        }
        var patches = over.GroupBy(Find).Select(g => g.ToArray()).ToList();
        foreach (var patch in patches.OrderByDescending(p => p.Max(e => density[e])).Take(MaxHotSpots))
            spots.Add(Patch(mesh, fe, patch, density, centroids, overLimit: true));
        return (spots, patches.Count);
    }

    private static RailHotSpot Patch(NetMesher.Result mesh, OpenSim.Core.Model.FeMesh fe, int[] elements,
        double[] density, Vector3D[] centroids, bool overLimit)
    {
        int peak = elements[0];
        double weighted = 0, volume = 0;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (int e in elements)
        {
            if (density[e] > density[peak]) peak = e;
            double v = fe.ElementVolume(e);
            weighted += density[e] * v;
            volume += v;
            var el = fe.Elements[e];
            foreach (int n in new[] { el.N0, el.N1, el.N2, el.N3 })
            {
                var p = fe.Nodes[n];
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
        }
        double extent = Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2) + Math.Pow(maxZ - minZ, 2));
        var at = centroids[peak];
        return new RailHotSpot(new Point2(at.X, at.Y), Where(mesh, at.Z), density[peak],
            weighted / volume, extent, elements.Length, overLimit);
    }

    /// <summary>The copper layer a height belongs to, or the gap between two (a barrel).</summary>
    internal static string Where(NetMesher.Result mesh, double z)
    {
        foreach (var (layer, (zLo, zHi)) in mesh.LayerZ)
            if (z >= zLo && z <= zHi) return $"L{layer}";
        foreach (int upper in mesh.LayerZ.Keys.OrderBy(k => k))
            if (mesh.LayerZ.TryGetValue(upper + 1, out var below) && z > below.zHi && z < mesh.LayerZ[upper].zLo)
                return $"via L{upper}–L{upper + 1}";
        return "copper";
    }
}
