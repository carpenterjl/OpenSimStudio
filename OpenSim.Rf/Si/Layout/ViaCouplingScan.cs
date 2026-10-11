using System.Globalization;
using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Polygons;
using OpenSim.Rf.Pdn;

namespace OpenSim.Rf.Si.Layout;

/// <summary>Two nets whose vias cross the same plane pair, and how much one's edge puts on the other
/// through it.</summary>
/// <param name="TransferOhms">The largest |Z₂₁| between the two nets' vias in the plane pair over the
/// scan's band, with every via joining the two planes shorted.</param>
/// <param name="FrequencyHz">Where in the band that largest value lies.</param>
/// <param name="Fraction">|Z₂₁|/(2Z₀) of the aggressor's swing: the aggressor's current V/Z₀ through
/// its barrel raises Z₂₁·I across the victim's barrel, which drives half of it into each matched end.</param>
public sealed record ViaCouplingPair(string NetA, string NetB, Point2 AtA, Point2 AtB, int UpperPlane, int LowerPlane,
    double TransferOhms, double FrequencyHz, double Fraction)
{
    public string Describe()
    {
        static string F(double v) => v.ToString("g3", CultureInfo.InvariantCulture);
        return $"{NetA} / {NetB}: vias at ({F(AtA.X * 1e3)}, {F(AtA.Y * 1e3)}) and ({F(AtB.X * 1e3)}, {F(AtB.Y * 1e3)}) mm "
            + $"both cross the plane pair L{UpperPlane}–L{LowerPlane} — |Z₂₁| {F(TransferOhms)} Ω at {F(FrequencyHz / 1e6)} MHz, "
            + $"{F(Fraction * 100)} % of the swing";
    }
}

public sealed record ViaCouplingReport(IReadOnlyList<ViaCouplingPair> Pairs, IReadOnlyList<string> Notes);

/// <summary>
/// FU-29 — via-to-via coupling through the plane pairs a signal crosses. A via that carries a signal
/// from a layer above two planes to one below them carries its current through the cavity between
/// them: it is a port of that plane pair, and every other via crossing the same pair sees the voltage
/// its current raises there. The plane pair is solved on its real outline — where both planes have
/// copper, holes of via clearances filled — by <see cref="PlanePair"/> (finite elements, open edges,
/// the dielectric and both planes' losses), with every via of the planes' net that joins the two
/// planes as a port held shorted (Z ← Z_ss − Z_sg·Z_gg⁻¹·Z_gs). The coupling between two nets is the
/// largest |Z₂₁| between their vias over a band up to the edge's knee 0.35/t_r — a cavity resonance
/// below the knee included — as a fraction |Z₂₁|/(2Z₀) of the aggressor's swing.
/// </summary>
public static class ViaCouplingScan
{
    private static readonly IPolygonOps Ops = new ClipperPolygonOps();

    /// <summary>The plane under a via is looked for on a ring this far out from its barrel — past the
    /// clearance hole the via sits in.</summary>
    private const double ClearanceReachMeters = 0.6e-3;
    private const int RingSamples = 8;

    private sealed record Cavity(CopperIsland Upper, CopperIsland Lower);

    private sealed record Port(CopperNet Net, Via Via);

    public static ViaCouplingReport Run(BoardLayoutIndex index, BoardStackup stackup, double riseTimeSeconds,
        double referenceOhms = 50, double minPlaneAreaSquareMeters = 100e-6, int maxShortingVias = 200,
        int frequencyPoints = 12)
    {
        var board = index.Board;
        var notes = new List<string>();
        var ports = new Dictionary<Cavity, List<Port>>();
        var planeNets = new HashSet<CopperNet>(ReferenceEqualityComparer.Instance);

        CopperIsland? PlaneAround(int layer, Via via, CopperNet own)
        {
            double r = via.Diameter / 2 + ClearanceReachMeters;
            var votes = new Dictionary<CopperIsland, int>(ReferenceEqualityComparer.Instance);
            for (int k = 0; k < RingSamples; k++)
            {
                var (sin, cos) = Math.SinCos(2 * Math.PI * k / RingSamples);
                var island = index.LargeIslandAt(layer, via.Position + new Point2(r * cos, r * sin), minPlaneAreaSquareMeters);
                if (island is null || ReferenceEquals(index.NetOf(island), own)) continue;
                votes[island] = votes.TryGetValue(island, out int v) ? v + 1 : 1;
            }
            var best = votes.OrderByDescending(kv => kv.Value).FirstOrDefault();
            return best.Value >= RingSamples * 3 / 4 ? best.Key : null;
        }

        foreach (var net in board.Nets)
            foreach (var bridge in net.StitchingVias)
            {
                if (!bridge.Via.Plated || bridge.Layers.Count < 2) continue;
                int first = bridge.Layers.Min(), last = bridge.Layers.Max();
                var planes = new List<(int Layer, CopperIsland Island)>();
                for (int layer = first + 1; layer < last; layer++)
                    if (PlaneAround(layer, bridge.Via, net) is { } island) planes.Add((layer, island));
                for (int k = 0; k + 1 < planes.Count; k++)
                {
                    var cavity = new Cavity(planes[k].Island, planes[k + 1].Island);
                    if (!ports.TryGetValue(cavity, out var list)) ports[cavity] = list = new List<Port>();
                    list.Add(new Port(net, bridge.Via));
                    if (index.NetOf(planes[k].Island) is { } a) planeNets.Add(a);
                    if (index.NetOf(planes[k + 1].Island) is { } b) planeNets.Add(b);
                }
            }

        double knee = 0.35 / riseTimeSeconds;
        var frequencies = Enumerable.Range(0, frequencyPoints)
            .Select(i => knee / 30 * Math.Pow(30, (double)i / (frequencyPoints - 1))).ToArray();
        var best = new Dictionary<(CopperNet, CopperNet), ViaCouplingPair>();
        int solvedCavities = 0, shortedTotal = 0;
        foreach (var (cavity, list) in ports)
        {
            var signals = list.Where(p => !planeNets.Contains(p.Net)).ToList();
            if (signals.Select(p => p.Net).Distinct().Count() < 2) continue;
            int upper = cavity.Upper.LayerOrder, lower = cavity.Lower.LayerOrder;
            var shape = Shape(cavity);
            if (shape.Count == 0) continue;
            var inside = new PolygonSetIndex(shape);
            signals = signals.Where(p => inside.Contains(p.Via.Position)).ToList();
            if (signals.Select(p => p.Net).Distinct().Count() < 2) continue;

            // The vias that join the two planes: one net on both, a barrel reaching both.
            var upperNet = index.NetOf(cavity.Upper);
            var shorts = new List<Via>();
            if (upperNet is not null && ReferenceEquals(upperNet, index.NetOf(cavity.Lower)))
                shorts = upperNet.StitchingVias
                    .Where(b => b.Layers.Contains(upper) && b.Layers.Contains(lower) && inside.Contains(b.Via.Position))
                    .Select(b => b.Via)
                    .OrderBy(v => signals.Min(s => (s.Via.Position - v.Position).Length))
                    .Take(maxShortingVias).ToList();
            int shortsAvailable = upperNet?.StitchingVias.Count(b => b.Layers.Contains(upper) && b.Layers.Contains(lower)) ?? 0;
            if (shortsAvailable > shorts.Count && shorts.Count == maxShortingVias)
                notes.Add($"Plane pair L{upper}–L{lower}: the {maxShortingVias} stitching vias nearest the signal vias "
                    + $"are in the solve, {shortsAvailable - shorts.Count} farther ones are not.");

            double separation = 0, overEps = 0, lossOverEps = 0;
            for (int gap = upper; gap < lower; gap++)
            {
                double d = stackup.GapThicknessOf(gap);
                separation += d;
                overEps += d / stackup.PermittivityOf(gap);
                lossOverEps += d * stackup.LossTangentOf(gap) / stackup.PermittivityOf(gap);
            }
            double copperBetween = Enumerable.Range(upper + 1, Math.Max(0, lower - upper - 1)).Sum(stackup.CopperThicknessOf);
            separation += copperBetween;
            var planePorts = signals.Select((p, i) => new PlanePort($"s{i}", p.Via.Position, p.Via.Diameter / 2))
                .Concat(shorts.Select((v, i) => new PlanePort($"g{i}", v.Position, v.Diameter / 2))).ToList();
            Complex[][,] z;
            try
            {
                var pair = new PlanePair(new PlanePairSpec
                {
                    Shape = shape,
                    SeparationMeters = separation,
                    RelativePermittivity = Math.Max(1, (separation - copperBetween) / overEps),
                    LossTangent = overEps > 0 ? lossOverEps / overEps : 0,
                    UpperCopperThicknessMeters = stackup.CopperThicknessOf(upper),
                    LowerCopperThicknessMeters = stackup.CopperThicknessOf(lower),
                    Ports = planePorts,
                    MaxFrequencyHz = knee,
                });
                z = pair.Sweep(frequencies).ToArray();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                notes.Add($"Plane pair L{upper}–L{lower}: not solved ({ex.Message}).");
                continue;
            }
            solvedCavities++;
            shortedTotal += shorts.Count;

            int ns = signals.Count;
            for (int f = 0; f < frequencies.Length; f++)
            {
                var reduced = Shorted(z[f], ns, shorts.Count);
                for (int a = 0; a < ns; a++)
                    for (int b = 0; b < ns; b++)
                    {
                        var (na, nb) = (signals[a].Net, signals[b].Net);
                        if (ReferenceEquals(na, nb) || na.Id > nb.Id) continue;
                        double transfer = reduced[a, b].Magnitude;
                        if (best.TryGetValue((na, nb), out var known) && known.TransferOhms >= transfer) continue;
                        best[(na, nb)] = new ViaCouplingPair(BoardLayoutIndex.NameOf(na), BoardLayoutIndex.NameOf(nb),
                            signals[a].Via.Position, signals[b].Via.Position, upper, lower, transfer, frequencies[f],
                            transfer / (2 * referenceOhms));
                    }
            }
        }

        notes.Insert(0, $"Via-to-via coupling through {solvedCavities} plane pair(s) that signals cross: each pair solved on "
            + "its real outline (where both planes have copper, via clearances filled; open edges), every via joining its "
            + $"two planes shorted ({shortedTotal} in all), |Z₂₁| between two nets' vias at {frequencyPoints} frequencies from "
            + $"{knee / 30 / 1e6:g3} MHz to the edge's knee {knee / 1e6:g3} MHz, as |Z₂₁|/(2·{referenceOhms:g3} Ω) of the "
            + "swing. Decoupling capacitors between two planes of different nets are not in it, nor are the vias' own barrels "
            + "outside the plane pair.");
        return new ViaCouplingReport(
            best.Values.OrderByDescending(p => p.Fraction).ThenBy(p => p.NetA, StringComparer.Ordinal)
                .ThenBy(p => p.NetB, StringComparer.Ordinal).ToList(), notes);
    }

    /// <summary>Where both planes have copper, cleaned, with the small holes (via clearances) filled —
    /// the PDN extraction's own rules for the same plane pair.</summary>
    private static List<Polygon2> Shape(Cavity cavity)
    {
        var overlap = Ops.Intersect(new[] { cavity.Upper.Shape }, Polygon2.OrientedRings(cavity.Lower.Shape));
        var defaults = new PdnBoardOptions();
        var shape = new List<Polygon2>();
        foreach (var polygon in PolygonCleaner.Clean(overlap, defaults.OutlineToleranceMeters))
        {
            if (polygon.Area() < defaults.MinPlaneAreaSquareMeters) continue;
            var holes = polygon.Holes.Where(h => Math.Abs(Polygon2.RingArea(h)) >= defaults.HoleFillAreaSquareMeters).ToList();
            shape.Add(new Polygon2(polygon.Outer, holes));
        }
        return shape;
    }

    /// <summary>The first <paramref name="open"/> ports' impedance matrix with the remaining
    /// <paramref name="shorted"/> ports held at zero volts: Z_ss − Z_sg·Z_gg⁻¹·Z_gs.</summary>
    internal static Complex[,] Shorted(Complex[,] z, int open, int shorted)
    {
        var result = new Complex[open, open];
        for (int i = 0; i < open; i++)
            for (int j = 0; j < open; j++) result[i, j] = z[i, j];
        if (shorted == 0) return result;
        var gg = new OpenSim.Core.Numerics.ComplexDenseMatrix(shorted, shorted);
        for (int i = 0; i < shorted; i++)
            for (int j = 0; j < shorted; j++) gg[i, j] = z[open + i, open + j];
        var lu = OpenSim.Core.Numerics.ComplexLu.Factor(gg);
        for (int j = 0; j < open; j++)
        {
            var column = new Complex[shorted];
            for (int i = 0; i < shorted; i++) column[i] = z[open + i, j];
            var solved = lu.Solve(column);
            for (int i = 0; i < open; i++)
            {
                Complex sum = Complex.Zero;
                for (int k = 0; k < shorted; k++) sum += z[i, open + k] * solved[k];
                result[i, j] -= sum;
            }
        }
        return result;
    }
}
