using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
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
        "Axisymmetric: each crossing of the cut is a full ring at that radius; the spiral's pitch and its lead-outs are not modelled.",
        "Every turn of a winding carries the winding current in the same sense (series aiding).",
        "Copper thickness and layer heights from the board stackup; dielectric is non-magnetic.",
        "A core is its axisymmetric equivalent (pot core exact, ER/E cores approximate)."
    };

    /// <summary>The turns of <paramref name="nets"/> crossing the ray from <paramref name="center"/>
    /// at <paramref name="angle"/> [rad], out to <paramref name="maxRadius"/>.</summary>
    public static IReadOnlyList<WindingTurn> Cut(PcbBoard board, IReadOnlyList<string> nets, Point2 center, double angle,
        double maxRadius, BoardStackup stackup)
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
        foreach (var name in nets)
        {
            var net = board.Nets.FirstOrDefault(n => n.Name == name) ?? throw new ArgumentException($"No net named '{name}'.");
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
        bool inside = Contains(polygon, p);
        double start = inside ? 0 : double.NaN;
        foreach (double t in hits)
        {
            if (inside) { result.Add((start, t)); inside = false; }
            else { start = t; inside = true; }
        }
        if (inside) result.Add((start, 1));
        return result;
    }

    private static bool Contains(Polygon2 polygon, Point2 p)
    {
        bool inside = false;
        foreach (var ring in Polygon2.OrientedRings(polygon))
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y)
                    && p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                    inside = !inside;
        return inside;
    }

    /// <summary>The axisymmetric model of the cut turns, with an optional core. Each winding in
    /// <paramref name="currents"/> carries that current (static or complex amplitude) in every one of
    /// its turns; windings not listed carry none. <paramref name="solidTurns"/> makes every turn a
    /// solid copper conductor (skin and proximity effect in a time-harmonic solve); otherwise turns
    /// are stranded (uniform current density).</summary>
    public static PlanarMagneticModel Build(IReadOnlyList<WindingTurn> turns, IReadOnlyDictionary<string, Complex> currents,
        AxisymmetricCore? core, bool solidTurns, double copperConductivity = 5.8e7, double? minElement = null)
    {
        if (turns.Count == 0) throw new ArgumentException("No turns: the cut crossed no copper of the windings.", nameof(turns));
        double zMin = turns.Min(t => t.Z0), zMax = turns.Max(t => t.Z1), rMax = turns.Max(t => t.R1);
        double zMid = 0.5 * (zMin + zMax);
        double thinnest = turns.Min(t => Math.Min(t.Z1 - t.Z0, t.R1 - t.R0));
        double outer = core?.OuterRadius ?? rMax;
        double halfHeight = core is null ? Math.Max(zMax - zMin, rMax) : core.WindowHeight / 2 + core.PlateThickness;
        var model = new MagneticModel2D
        {
            Geometry = MagneticGeometry.Axisymmetric,
            DomainX0 = 0, DomainX1 = 4 * outer,
            DomainY0 = zMid - halfHeight - 3 * outer, DomainY1 = zMid + halfHeight + 3 * outer,
            MaxElement = outer / 6,
            MinElement = minElement ?? Math.Max(thinnest / 2, 1e-6),
            Growth = 1.35
        };
        var notes = new List<string>();
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
                else model.Coils.Add(new StrandedCoil(turn.Name, new[] { (turn.Name, 1) }, 1, current.Real));
            }
            notes.Add($"Winding '{group.Key}': {group.Count()} turn(s) on layer(s) {string.Join(", ", group.Select(t => t.LayerOrder).Distinct())}.");
        }
        return new PlanarMagneticModel(model, windings, solidTurns, notes);
    }
}
