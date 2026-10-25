using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Solvers.Magnetics;

namespace OpenSim.Pcb.Magnetics;

/// <summary>One turn of a planar winding as it crosses the cut: radii [R0, R1] and heights
/// [Z0, Z1] (z up, the board's top surface at 0).</summary>
public sealed record WindingTurn(string Net, int LayerOrder, double R0, double R1, double Z0, double Z1)
{
    public string Name => $"{Net} L{LayerOrder} r{R0 * 1e3:F3}";
}

/// <summary>
/// A pot or ER core reduced to its axisymmetric equivalent around the winding axis: a centre post
/// of radius <see cref="PostRadius"/>, a window out to <see cref="WindowRadius"/>, an outer wall
/// to <see cref="OuterRadius"/>, plates of <see cref="PlateThickness"/> above and below a window
/// <see cref="WindowHeight"/> tall centred on the board, and an air gap
/// <see cref="CentreGap"/> across the centre post at mid-height. An ER core's round post maps
/// exactly; its outer legs do not (their area is what <see cref="OuterRadius"/> should preserve).
/// </summary>
public sealed record AxisymmetricCore(double PostRadius, double WindowRadius, double OuterRadius,
    double PlateThickness, double WindowHeight, double CentreGap, MagneticMaterial Material);

/// <summary>The model of a planar magnetic: the axisymmetric FEM model and which turns belong to
/// which winding.</summary>
public sealed record PlanarMagneticModel(MagneticModel2D Model, IReadOnlyDictionary<string, IReadOnlyList<WindingTurn>> Windings,
    bool SolidTurns, IReadOnlyList<string> Notes)
{
    /// <summary>Self-inductance of a winding [H] from a static or time-harmonic solve in which only
    /// that winding carries current: Σ over its turns of flux linkage / I.</summary>
    public Complex Inductance(MagneticSolution2D solution, string net)
    {
        var turns = Windings[net];
        if (SolidTurns)
        {
            Complex v = turns.Aggregate(Complex.Zero, (s, t) => s + solution.ConductorImpedance(t.Name));
            return v / new Complex(0, 2 * Math.PI * solution.FrequencyHz);
        }
        var current = Model.Coils.First(c => c.Name == turns[0].Name).Current;
        Complex total = Complex.Zero;
        foreach (var t in turns) total += solution.FluxLinkage(t.Name);
        return total / current;
    }

    /// <summary>AC resistance of a winding [Ω] (solid turns, time-harmonic): Σ Re(V_turn / I).</summary>
    public double Resistance(MagneticSolution2D solution, string net) =>
        Windings[net].Sum(t => solution.ConductorImpedance(t.Name).Real);

    /// <summary>Strand-level copper loss of every winding [W] (stranded turns with a strand
    /// diameter, time-harmonic): the sum over all turns of transport and proximity loss. The
    /// driven winding's effective AC resistance is 2·(this)/|I|², since every winding's loss loads
    /// the drive.</summary>
    public double StrandLossWatts(MagneticSolution2D solution) =>
        Windings.Values.SelectMany(ts => ts).Sum(t => solution.StrandLoss(t.Name).TotalWatts);
}

/// <summary>The turns of a cut averaged around the revolution, and what the averaging saw: the
/// spiral's radius spread (its pitch) and any angles dropped over a lead-out.</summary>
public sealed record SpiralCut(IReadOnlyList<WindingTurn> Turns, IReadOnlyList<string> Notes);

/// <summary>A straight lead-out trace from a winding's end to its terminal, priced as a PEEC
/// rectangular bar in series with that winding: DC resistance plus partial self-inductance. It is
/// not in the field solve, so it adds to Z_ii only — no mutual to the rings or to other
/// lead-outs, and no skin effect in the trace itself.</summary>
public sealed record LeadOut(string Net, double Length, double Width, double Thickness, double Conductivity = 5.8e7)
{
    public Complex SeriesImpedance(double frequencyHz) => new(
        Length / (Conductivity * Width * Thickness),
        2 * Math.PI * frequencyHz * PartialInductance.SelfInductance(Length, Width, Thickness));
}

/// <summary>The winding impedance matrix at one frequency: Z_ij = V_i per ampere driven through
/// winding j with every other winding open (each turn a solid conductor, so R carries skin and
/// proximity effect and M carries the core's eddy reaction).</summary>
public sealed record WindingImpedanceMatrix(IReadOnlyList<string> Nets, System.Numerics.Complex[,] Entries, double FrequencyHz)
{
    /// <summary>What was added outside the field solve (lead-out series terms).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    private int Index(string net)
    {
        for (int i = 0; i < Nets.Count; i++) if (Nets[i] == net) return i;
        throw new ArgumentException($"No winding '{net}'.");
    }

    public Complex Z(string a, string b) => Entries[Index(a), Index(b)];

    /// <summary>Im Z / ω [H]: self-inductance on the diagonal, mutual off it.</summary>
    public double InductanceHenries(string a, string b) => Z(a, b).Imaginary / (2 * Math.PI * FrequencyHz);

    public double CouplingCoefficient(string a, string b) =>
        InductanceHenries(a, b) / Math.Sqrt(InductanceHenries(a, a) * InductanceHenries(b, b));
}

/// <summary>
/// Planar magnetics from the layout: the windings' copper is cut along a radial line from the
/// winding axis, layer by layer, and every crossing becomes one turn of an axisymmetric model —
/// the section a planar transformer or inductor is designed on. A spiral is not a set of rings,
/// so the section is exact only in the rotationally symmetric limit; the cut angle chooses which
/// crossing pitch is seen, and two angles bracket the spiral's own asymmetry.
/// </summary>
public static class PlanarWindingSection
{
    public static IReadOnlyList<string> Assumptions { get; } = new[]
    {
        "Axisymmetric: each crossing of the cut is a full ring at that radius. CutAveraged folds the spiral's pitch in by averaging each turn's radii around the revolution; lead-outs enter only as PEEC series terms on their own winding's Z_ii (no field coupling).",
        "Every turn of a winding carries the winding current in the same sense (series aiding).",
        "Copper thickness and layer heights from the board stackup; dielectric is non-magnetic.",
        "A core is its axisymmetric equivalent (pot core exact; an ER/E core's legs map to the area-preserving wall — ErCore.PlanarLegSection checks the real legs on a planar cut)."
    };

    /// <summary>The turns of <paramref name="nets"/> crossing the ray from <paramref name="center"/>
    /// at <paramref name="angle"/> [rad], out to <paramref name="maxRadius"/>.</summary>
    public static IReadOnlyList<WindingTurn> Cut(PcbBoard board, IReadOnlyList<string> nets, Point2 center, double angle,
        double maxRadius, BoardStackup stackup) =>
        Cut(board, Resolve(board, nets), center, angle, maxRadius, stackup);

    /// <summary>The same for the nets themselves — a board whose nets carry no names (a Gerber set
    /// without X2 attributes) can be cut too; such a winding is named "Net {Id}".</summary>
    public static IReadOnlyList<WindingTurn> Cut(PcbBoard board, IReadOnlyList<CopperNet> nets, Point2 center, double angle,
        double maxRadius, BoardStackup stackup) =>
        Cut(board, nets.Select(n => (WindingName(n), n)).ToList(), center, angle, maxRadius, stackup);

    /// <summary>The name a net's winding goes by: its design name, or "Net {Id}" without one.</summary>
    public static string WindingName(CopperNet net) => net.Name ?? $"Net {net.Id}";

    private static List<(string Name, CopperNet Net)> Resolve(PcbBoard board, IReadOnlyList<string> nets) =>
        nets.Select(name => (name, board.Nets.FirstOrDefault(n => n.Name == name) ?? throw new ArgumentException($"No net named '{name}'.")))
            .ToList();

    private static IReadOnlyList<WindingTurn> Cut(PcbBoard board, IReadOnlyList<(string Name, CopperNet Net)> nets, Point2 center,
        double angle, double maxRadius, BoardStackup stackup)
    {
        var dir = new Point2(Math.Cos(angle), Math.Sin(angle));
        var end = center + dir * maxRadius;
        int layerCount = board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder) : 1;
        // z of each layer's top, from the board's top surface downward.
        var top = new double[layerCount + 2];
        double z = 0;
        for (int k = 1; k <= layerCount; k++)
        {
            top[k] = z;
            z -= stackup.CopperThicknessOf(k) + (k < layerCount ? stackup.GapThicknessOf(k) : 0);
        }
        var turns = new List<WindingTurn>();
        foreach (var (name, net) in nets)
        {
            foreach (var island in net.Islands)
            {
                double t = stackup.CopperThicknessOf(island.LayerOrder);
                foreach (var (a, b) in Intervals(island.Shape, center, end))
                {
                    double r0 = a * maxRadius, r1 = b * maxRadius;
                    if (r1 - r0 <= 1e-9) continue;
                    turns.Add(new WindingTurn(name, island.LayerOrder, r0, r1, top[island.LayerOrder] - t, top[island.LayerOrder]));
                }
            }
        }
        return turns.OrderBy(t => t.Net, StringComparer.Ordinal).ThenBy(t => t.LayerOrder).ThenBy(t => t.R0).ToList();
    }

    /// <summary>
    /// The cut averaged over <paramref name="angleCount"/> angles around the axis: each turn
    /// becomes a ring at its mean radius over the revolution — the rotational average of the
    /// spiral, so its pitch no longer depends on where the cut was taken. Turns are matched by
    /// radial order per winding and layer; an angle whose crossing count differs from the majority
    /// (the ray crossed a lead-out, a terminal pad or a partial turn) is dropped for that winding
    /// with a note, and the spread of each winding's radii over the used angles (the pitch the
    /// averaging absorbed) is reported.
    /// </summary>
    public static SpiralCut CutAveraged(PcbBoard board, IReadOnlyList<string> nets, Point2 center,
        double maxRadius, BoardStackup stackup, int angleCount = 16) =>
        CutAveraged(board, Resolve(board, nets), center, maxRadius, stackup, angleCount);

    /// <summary>The same for the nets themselves (unnamed nets are named "Net {Id}").</summary>
    public static SpiralCut CutAveraged(PcbBoard board, IReadOnlyList<CopperNet> nets, Point2 center,
        double maxRadius, BoardStackup stackup, int angleCount = 16) =>
        CutAveraged(board, nets.Select(n => (WindingName(n), n)).ToList(), center, maxRadius, stackup, angleCount);

    private static SpiralCut CutAveraged(PcbBoard board, IReadOnlyList<(string Name, CopperNet Net)> nets, Point2 center,
        double maxRadius, BoardStackup stackup, int angleCount)
    {
        if (angleCount < 2) throw new ArgumentOutOfRangeException(nameof(angleCount), "Average over at least 2 angles.");
        var cuts = new List<IReadOnlyList<WindingTurn>>();
        for (int k = 0; k < angleCount; k++)
            cuts.Add(Cut(board, nets, center, 2 * Math.PI * k / angleCount, maxRadius, stackup));

        var turns = new List<WindingTurn>();
        var notes = new List<string>();
        var keys = cuts.SelectMany(c => c).Select(t => (t.Net, t.LayerOrder)).Distinct()
            .OrderBy(k => k.Net, StringComparer.Ordinal).ThenBy(k => k.LayerOrder).ToList();
        foreach (var (net, layer) in keys)
        {
            var perAngle = cuts.Select(c => c.Where(t => t.Net == net && t.LayerOrder == layer).ToList()).ToList();
            // The majority crossing count; more-frequent wins, ties to the larger count.
            int majority = perAngle.GroupBy(a => a.Count).OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key).First().Key;
            var used = perAngle.Where(a => a.Count == majority).ToList();
            if (used.Count < angleCount)
                notes.Add($"Winding '{net}' layer {layer}: {angleCount - used.Count} of {angleCount} angles dropped "
                    + $"({majority} crossings in the majority — a lead-out, terminal or partial turn crossed the others).");
            if (majority == 0) continue;
            double spread = 0;
            for (int t = 0; t < majority; t++)
            {
                double r0 = used.Average(a => a[t].R0);
                double r1 = used.Average(a => a[t].R1);
                spread = Math.Max(spread, used.Max(a => a[t].R0) - used.Min(a => a[t].R0));
                turns.Add(new WindingTurn(net, layer, r0, r1, used[0][t].Z0, used[0][t].Z1));
            }
            notes.Add($"Winding '{net}' layer {layer}: {majority} turn(s) at their mean radii over "
                + $"{used.Count} angle(s); largest radius spread over the revolution {spread * 1e3:g3} mm (the pitch).");
        }
        return new SpiralCut(
            turns.OrderBy(t => t.Net, StringComparer.Ordinal).ThenBy(t => t.LayerOrder).ThenBy(t => t.R0).ToList(),
            notes);
    }

    /// <summary>The parameter intervals [a, b] ⊂ [0, 1] of segment p→q inside the polygon.</summary>
    public static IReadOnlyList<(double A, double B)> Intervals(Polygon2 polygon, Point2 p, Point2 q)
    {
        var hits = new List<double>();
        var d = q - p;
        foreach (var ring in Polygon2.OrientedRings(polygon))
            for (int i = 0; i < ring.Count; i++)
            {
                var s = ring[i];
                var e = ring[(i + 1) % ring.Count] - s;
                double den = d.X * e.Y - d.Y * e.X;
                if (Math.Abs(den) < 1e-30) continue;
                var w = s - p;
                double t = (w.X * e.Y - w.Y * e.X) / den;
                double u = (w.X * d.Y - w.Y * d.X) / den;
                if (u >= 0 && u < 1 && t >= 0 && t <= 1) hits.Add(t);
            }
        hits.Sort();
        var result = new List<(double, double)>();
        bool inside = polygon.Contains(p);
        double start = inside ? 0 : double.NaN;
        foreach (double t in hits)
        {
            if (inside) { result.Add((start, t)); inside = false; }
            else { start = t; inside = true; }
        }
        if (inside) result.Add((start, 1));
        return result;
    }

    /// <summary>The axisymmetric model of the cut turns, with an optional core. Each winding in
    /// <paramref name="currents"/> carries that current (static or complex amplitude) in every one of
    /// its turns; windings not listed carry none. <paramref name="solidTurns"/> makes every turn a
    /// solid copper conductor (skin and proximity effect in a time-harmonic solve); otherwise turns
    /// are stranded (uniform current density).
    ///
    /// <para>Mesh: the grid starts at <see cref="MagneticModel2D.MinElement"/> on every region edge —
    /// half the thinnest turn dimension by default — and grows by 1.35 to a sixth of the outer
    /// radius. For solid turns at <paramref name="frequencyHz"/> &gt; 0 the current crowds into a
    /// skin depth δ of the turn faces that see the field (a gap's fringing field, the neighbouring
    /// turns), and when δ/8 is below that default the copper cells take δ/8 at the edges and grid
    /// lines are added at δ and 2δ inside each turn, in r and in z wherever the turn is wider or
    /// thicker than 4δ (so the lines never meet; a turn between 2δ and 4δ across is resolved by
    /// the graded spacing from its two edges alone). Measured on the FU-16 pot core at 1 MHz
    /// (δ = 66 µm, 0.8 mm × 35 µm turns): R +2.4 % of the reference at the plain default, +0.96 %
    /// with this (Richardson +0.13 %), 1.9× the nodes and 4× the time; at 100 kHz δ/8 exceeds the
    /// default and nothing changes, and a static solve never does. The cost grows with the turn
    /// count (four r lines per wide turn span the whole domain height) — a many-turn winding at
    /// several MHz pays for it. An explicit <paramref name="minElement"/> is kept as given (the
    /// lines are still added).</para>
    /// </summary>
    public static PlanarMagneticModel Build(IReadOnlyList<WindingTurn> turns, IReadOnlyDictionary<string, Complex> currents,
        AxisymmetricCore? core, bool solidTurns, double copperConductivity = 5.8e7, double? minElement = null,
        double strandDiameter = 0, int strandsPerTurn = 1, double frequencyHz = 0)
    {
        if (turns.Count == 0) throw new ArgumentException("No turns: the cut crossed no copper of the windings.", nameof(turns));
        double zMin = turns.Min(t => t.Z0), zMax = turns.Max(t => t.Z1), rMax = turns.Max(t => t.R1);
        double zMid = 0.5 * (zMin + zMax);
        double thinnest = turns.Min(t => Math.Min(t.Z1 - t.Z0, t.R1 - t.R0));
        double outer = core?.OuterRadius ?? rMax;
        double halfHeight = core is null ? Math.Max(zMax - zMin, rMax) : core.WindowHeight / 2 + core.PlateThickness;
        double defaultMin = Math.Max(thinnest / 2, 1e-6);
        double skinDepth = solidTurns && frequencyHz > 0
            ? Math.Sqrt(2 / (2 * Math.PI * frequencyHz * MagneticConstants.Mu0 * copperConductivity)) : double.PositiveInfinity;
        bool skinBinds = skinDepth / 8 < defaultMin;
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Axisymmetric,
            DomainX0 = 0, DomainX1 = 4 * outer,
            DomainY0 = zMid - halfHeight - 3 * outer, DomainY1 = zMid + halfHeight + 3 * outer,
            MaxElement = outer / 6,
            MinElement = minElement ?? (skinBinds ? skinDepth / 8 : defaultMin),
            Growth = 1.35
        };
        var notes = new List<string>();
        if (skinBinds)
        {
            foreach (var turn in turns)
            {
                if (turn.R1 - turn.R0 > 4 * skinDepth)
                    model.ExtraLinesX.AddRange(new[] { turn.R0 + skinDepth, turn.R0 + 2 * skinDepth, turn.R1 - 2 * skinDepth, turn.R1 - skinDepth });
                if (turn.Z1 - turn.Z0 > 4 * skinDepth)
                    model.ExtraLinesY.AddRange(new[] { turn.Z0 + skinDepth, turn.Z0 + 2 * skinDepth, turn.Z1 - 2 * skinDepth, turn.Z1 - skinDepth });
            }
            notes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Mesh: skin depth {skinDepth * 1e6:g3} µm at {frequencyHz / 1e3:g4} kHz — ")
                + (minElement is null
                    ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"copper cells of δ/8 = {skinDepth / 8 * 1e6:g3} µm at the turn edges")
                    : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the given minimum element {minElement * 1e6:g3} µm kept at the edges (δ/8 would be {skinDepth / 8 * 1e6:g3} µm)"))
                + ", grid lines at δ and 2δ inside each turn wider or thicker than 4δ.");
        }
        if (core is not null)
        {
            if (core.WindowHeight <= zMax - zMin) throw new ArgumentException("The core window is shorter than the winding stack.", nameof(core));
            if (core.PostRadius >= turns.Min(t => t.R0) || core.WindowRadius <= rMax)
                throw new ArgumentException("The turns do not fit in the core window.", nameof(core));
            double zb = zMid - core.WindowHeight / 2, zt = zMid + core.WindowHeight / 2;
            var m = core.Material;
            model.Regions.Add(new MagneticRegion("core bottom", 0, core.OuterRadius, zb - core.PlateThickness, zb, m));
            model.Regions.Add(new MagneticRegion("core top", 0, core.OuterRadius, zt, zt + core.PlateThickness, m));
            model.Regions.Add(new MagneticRegion("core wall", core.WindowRadius, core.OuterRadius, zb, zt, m));
            if (core.CentreGap > 0)
            {
                model.Regions.Add(new MagneticRegion("core post low", 0, core.PostRadius, zb, zMid - core.CentreGap / 2, m));
                model.Regions.Add(new MagneticRegion("core post high", 0, core.PostRadius, zMid + core.CentreGap / 2, zt, m));
            }
            else model.Regions.Add(new MagneticRegion("core post", 0, core.PostRadius, zb, zt, m));
            notes.Add($"Core: post r {core.PostRadius * 1e3:g4} mm, window to {core.WindowRadius * 1e3:g4} mm, outer {core.OuterRadius * 1e3:g4} mm, gap {core.CentreGap * 1e3:g4} mm.");
        }
        var copper = new MagneticMaterial("copper", 1, copperConductivity);
        var windings = new Dictionary<string, IReadOnlyList<WindingTurn>>();
        foreach (var group in turns.GroupBy(t => t.Net))
        {
            windings[group.Key] = group.ToList();
            currents.TryGetValue(group.Key, out var current);
            foreach (var turn in group)
            {
                model.Regions.Add(new MagneticRegion(turn.Name, turn.R0, turn.R1, turn.Z0, turn.Z1,
                    solidTurns ? copper : MagneticMaterial.Air));
                if (solidTurns) model.Conductors.Add(new SolidConductor(turn.Name, new[] { turn.Name }, current));
                else model.Coils.Add(new StrandedCoil(turn.Name, new[] { (turn.Name, 1) }, 1, current.Real)
                {
                    StrandDiameter = strandDiameter,
                    StrandsPerTurn = strandsPerTurn,
                    StrandConductivity = copperConductivity
                });
            }
            notes.Add($"Winding '{group.Key}': {group.Count()} turn(s) on layer(s) {string.Join(", ", group.Select(t => t.LayerOrder).Distinct())}.");
        }
        return new PlanarMagneticModel(model, windings, solidTurns, notes);
    }

    /// <summary>The winding impedance matrix at <paramref name="frequencyHz"/>: one solid-turn
    /// time-harmonic solve per winding, each driven with 1 A while the others are open, the
    /// voltages summed along every winding's turns. Each of <paramref name="leadOuts"/> adds its
    /// PEEC series impedance (DC resistance + partial self-inductance of the straight trace) to
    /// its own winding's diagonal entry — lead-outs couple to nothing.</summary>
    public static WindingImpedanceMatrix ImpedanceMatrix(IReadOnlyList<WindingTurn> turns, AxisymmetricCore? core,
        double frequencyHz, double copperConductivity = 5.8e7, double? minElement = null,
        IReadOnlyList<LeadOut>? leadOuts = null)
    {
        var nets = turns.Select(t => t.Net).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        var entries = new Complex[nets.Count, nets.Count];
        var solver = new MagneticSolver2D();
        for (int j = 0; j < nets.Count; j++)
        {
            var built = Build(turns, new Dictionary<string, Complex> { [nets[j]] = 1 }, core, solidTurns: true,
                copperConductivity, minElement, frequencyHz: frequencyHz);
            var solution = solver.SolveTimeHarmonic(built.Model, frequencyHz);
            for (int i = 0; i < nets.Count; i++)
                entries[i, j] = built.Windings[nets[i]].Aggregate(Complex.Zero, (s, t) => s + solution.ConductorVoltage(t.Name));
        }
        var notes = new List<string>();
        // The mesh note of the (identical) per-winding builds, once.
        var first = Build(turns, new Dictionary<string, Complex>(), core, solidTurns: true, copperConductivity, minElement, frequencyHz: frequencyHz);
        notes.AddRange(first.Notes.Where(n => n.StartsWith("Mesh:", StringComparison.Ordinal)));
        foreach (var lead in leadOuts ?? Array.Empty<LeadOut>())
        {
            int i = nets.IndexOf(lead.Net);
            if (i < 0) throw new ArgumentException($"Lead-out for '{lead.Net}': no such winding.", nameof(leadOuts));
            var z = lead.SeriesImpedance(frequencyHz);
            entries[i, i] += z;
            notes.Add($"Lead-out on '{lead.Net}': {lead.Length * 1e3:g4} × {lead.Width * 1e3:g4} × {lead.Thickness * 1e3:g4} mm "
                + $"adds {z.Real * 1e3:g4} mΩ + {z.Imaginary / (2 * Math.PI * frequencyHz) * 1e9:g4} nH in series "
                + "(PEEC partial self; no mutual to the rings or other lead-outs, no skin effect in the trace).");
        }
        return new WindingImpedanceMatrix(nets, entries, frequencyHz) { Notes = notes };
    }
}
