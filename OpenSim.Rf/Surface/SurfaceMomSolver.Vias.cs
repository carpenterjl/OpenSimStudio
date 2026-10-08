using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>A via joining metal on two interfaces of a stackup: a thin vertical tube at
/// (<see cref="ProbeFeed.X"/>, <see cref="ProbeFeed.Y"/>) of the geometry's radius, from the
/// lower interface to the upper, attached at each end to a mesh vertex of that level's sheet.</summary>
public sealed record LevelVia(string Name, ProbeFeed Geometry, int LowerInterface, int UpperInterface);

/// <summary>A multi-level solve with vias: the sheet ports' admittance and, per port excitation
/// (1 V on it, the others shorted), the raw RWG currents and each via's tube currents at its
/// nodes (index 0 the lower junction's, last the upper's).</summary>
public sealed record LevelsSolution(double FrequencyHz, Complex[,] Admittance, Complex[][] EdgeCurrents,
    IReadOnlyList<LevelVia> Vias, double[][] ViaNodes, Complex[][][] ViaCurrents,
    int[] LowerVertices, int[] UpperVertices);

/// <summary>
/// FU-34 — vias between metal levels. A via is the probe's tube (<see cref="ProbeAssembly"/>)
/// run between two interfaces instead of from the ground, with a junction at EACH end: the
/// tube's bottom half-hat and the lower sheet's attachment fan drawn INTO the vertex make one
/// unknown (current leaves the lower sheet up the tube), the top half-hat and the upper fan
/// drawn out of the vertex another; interior hats in between. Every term is one the pin solver
/// already computes for a tube ending on one sheet, here read at both sheets' heights:
///  • tube against RWG — the coupling tables of the vertical kernels at the RWG's own level;
///  • tube against each fan — the by-parts coupling on the fan's half-RWG triangles plus the
///    vertex term (the disc's vector part read at the vertex);
///  • each fan against RWG and against itself — the pin's disc and half-RWG terms, with the
///    in-plane kernel on its own level and the cross-level kernel on the other;
///  • the two fans against each other — disc/disc, disc/halves and halves/halves across levels.
/// Every entry is computed once and written to both (m, n) and (n, m).
///
/// <para>Several vias add the pin array's cross terms between them: tube against tube at the axis
/// spacing, each tube against the other's fans, and fan against fan on any two levels. Fans must
/// not share triangles. Sheet gap ports only.</para>
/// </summary>
public sealed partial class SurfaceMomSolver
{
    /// <summary>One via's own terms: its tube, its two fans, and the tube's coupling to every
    /// triangle (each read at its level's height).</summary>
    private sealed class ViaTerms
    {
        public required LevelVia Via { get; init; }
        public required double[] Nodes { get; init; }
        public int Segments => Nodes.Length - 1;
        public required AttachmentFan[] Fans { get; init; }          // [0] lower, [1] upper
        public required int[] FanLevels { get; init; }
        public required Complex[,] TriangleIntegrals { get; init; } // [triangle, tube basis]
        public required Complex[,] Coupling { get; init; }          // [RWG, tube basis]
        public required Func<int, double, Complex[]> VertexVector { get; init; } // (level, ρ) → per tube basis
        public required Complex[][] FanRows { get; init; }          // [end][RWG]: fan against every RWG
        public required ComplexDenseMatrix Block { get; init; }
        public required HashSet<int> Footprint { get; init; }
    }

    /// <summary>Sheet metal on several interfaces joined by vias.</summary>
    public LevelsSolution SolveLevels(SurfaceStructure surface, LayeredStackup stackup, double frequencyHz,
        IReadOnlyList<SurfacePort> ports, double rhoMax, IReadOnlyList<LevelVia> vias)
    {
        if (vias.Count == 0) throw new ArgumentException("At least one via is needed (or use the overload without).", nameof(vias));
        if (ports.Count == 0) throw new ArgumentException("At least one port is needed.", nameof(ports));
        var asm = AssembleLevels(surface, stackup, frequencyHz, rhoMax, MaxDegreeOfParallelism);
        var heights = stackup.InterfaceHeights();
        double omega = 2 * Math.PI * frequencyHz;
        var jOmega = new Complex(0, omega);
        var set = new MultiLayerVerticalKernelSet(stackup, frequencyHz);
        int nEdges = surface.BasisCount;
        var (l1, l2, l3, wq) = TriangleQuadrature.Rule(6);

        IRadialGaKernel Radial(int observationLevel, int sourceLevel) => observationLevel == sourceLevel
            ? new MultiLayerRadialGaKernel(asm.Own[sourceLevel])
            : new CrossLevelRadialGaKernel(asm.Cross[(Math.Min(observationLevel, sourceLevel), Math.Max(observationLevel, sourceLevel))]);
        (SurfaceMoments A, SurfaceMoments Phi) Moments(int p, int q)
        {
            int lp = asm.TriangleLevel[p], lq = asm.TriangleLevel[q];
            if (lp != lq)
                return CrossLevelPairMoments(surface, p, q, asm.Cross[(Math.Min(lp, lq), Math.Max(lp, lq))]);
            var (a, phi) = LayeredPairMoments(surface, p, q, new LayeredKernelSplit(asm.Own[lp]));
            return p == q ? (a.Symmetrized(), phi.Symmetrized()) : (a, phi);
        }
        // ⟨test fan, E(source fan)⟩, both drawn outward: disc/disc, disc/halves both ways, halves/halves.
        Complex FanFan(AttachmentFan test, int testLevel, AttachmentFan source, int sourceLevel)
        {
            var kernel = Radial(testLevel, sourceLevel);
            return jOmega * test.DiscAgainst(kernel, surface, source)
                + DiscAgainstHalves(surface, kernel, source, test, jOmega)
                + DiscAgainstHalves(surface, kernel, test, source, jOmega)
                + HalvesAgainstHalvesLevels(surface, Moments, omega, test, source);
        }
        // The fan (outward) against every RWG basis, on every level.
        Complex[] FanRow(AttachmentFan fan, int level)
        {
            var row = HalvesAgainstRwgLevels(surface, Moments, omega, fan);
            for (int t = 0; t < surface.Triangles.Count; t++)
            {
                if (surface.TriangleSupports[t].Count == 0) continue;
                var kernel = Radial(asm.TriangleLevel[t], level);
                var verts = PVertices(surface, t);
                var panels = asm.TriangleLevel[t] == level
                    ? OuterPanels(verts, verts, new List<Vector3D> { fan.VertexPosition })
                    : new[] { verts }.AsEnumerable();
                foreach (var (pa, pb, pc) in panels)
                {
                    double panelArea = Vector3D.Cross(pb - pa, pc - pa).Length / 2;
                    for (int i = 0; i < wq.Length; i++)
                    {
                        var r = pa * l1[i] + pb * l2[i] + pc * l3[i];
                        var (ax, ay) = fan.DiscPotential(kernel, surface, r);
                        foreach (var (basis, sign, opposite) in surface.TriangleSupports[t])
                        {
                            var fDir = r - surface.Vertices[opposite];
                            double scale = sign * surface.Edges[basis].Length / (2 * surface.TriangleAreas[t]);
                            row[basis] += jOmega * wq[i] * panelArea * scale * (fDir.X * ax + fDir.Y * ay);
                        }
                    }
                }
            }
            return row;
        }

        var terms = new ViaTerms[vias.Count];
        for (int v = 0; v < vias.Count; v++)
            terms[v] = BuildViaTerms(surface, stackup, asm, set, vias[v], omega, FanRow);
        for (int i = 0; i < terms.Length; i++)
            for (int k = i + 1; k < terms.Length; k++)
                if (terms[i].Footprint.Overlaps(terms[k].Footprint))
                    throw new ArgumentException($"Vias '{vias[i].Name}' and '{vias[k].Name}' are too close for the mesh: "
                        + "their attachment fans share triangles.", nameof(vias));

        // Tube i's bases against fan (k, end): the by-parts coupling on the fan's half-RWG
        // triangles plus the vertex term at the distance from tube i's axis to that vertex.
        Complex[] TubeFan(ViaTerms tube, ViaTerms owner, int end)
        {
            var fan = owner.Fans[end];
            var result = new Complex[tube.Segments + 1];
            foreach (var wedge in fan.Wedges)
            {
                double div = -wedge.Gamma * surface.Edges[wedge.EdgeBasis].Length / surface.TriangleAreas[wedge.NeighborTriangle];
                for (int n = 0; n <= tube.Segments; n++) result[n] += div * tube.TriangleIntegrals[wedge.NeighborTriangle, n];
            }
            double dx = tube.Via.Geometry.X - fan.VertexPosition.X, dy = tube.Via.Geometry.Y - fan.VertexPosition.Y;
            var vertex = tube.VertexVector(owner.FanLevels[end], Math.Sqrt(dx * dx + dy * dy));
            for (int n = 0; n <= tube.Segments; n++) result[n] += vertex[n];
            return result;
        }

        // Unknowns: [RWG | per via: hats 1..seg−1, J_lower, J_upper];
        // J_lower = tube basis 0 − lower fan, J_upper = tube basis seg + upper fan.
        var start = new int[vias.Count];
        int next = nEdges;
        for (int v = 0; v < vias.Count; v++) { start[v] = next; next += terms[v].Segments + 1; }
        int total = next;
        int Index(int v, int basis)   // tube basis 0..seg → unknown
        {
            int seg = terms[v].Segments;
            return basis == 0 ? start[v] + seg - 1 : basis == seg ? start[v] + seg : start[v] + basis - 1;
        }
        double[] endSign = { -1, 1 };

        var z = new ComplexDenseMatrix(total, total);
        for (int a = 0; a < nEdges; a++)
            for (int b = 0; b < nEdges; b++) z[a, b] = asm.Matrix[a, b];
        void Put(int r, int c, Complex value) { z[r, c] = value; z[c, r] = value; }
        for (int v = 0; v < vias.Count; v++)
        {
            var t = terms[v];
            for (int m = 0; m < nEdges; m++)
                for (int n = 0; n <= t.Segments; n++)
                {
                    Complex value = t.Coupling[m, n];
                    if (n == 0) value += endSign[0] * t.FanRows[0][m];
                    if (n == t.Segments) value += endSign[1] * t.FanRows[1][m];
                    Put(m, Index(v, n), value);
                }
        }
        for (int i = 0; i < vias.Count; i++)
            for (int k = i; k < vias.Count; k++)
            {
                var ti = terms[i];
                var tk = terms[k];
                var tubeTube = i == k ? ToArray(ti.Block) : TubeMutual(set, ti, tk, omega);
                var iOnK = new[] { TubeFan(ti, tk, 0), TubeFan(ti, tk, 1) };   // tube i vs fans of k
                var kOnI = new[] { TubeFan(tk, ti, 0), TubeFan(tk, ti, 1) };   // tube k vs fans of i
                var fanFan = new Complex[2, 2];
                for (int a = 0; a < 2; a++)
                    for (int b = 0; b < 2; b++)
                        fanFan[a, b] = FanFan(ti.Fans[a], ti.FanLevels[a], tk.Fans[b], tk.FanLevels[b]);
                for (int n = 0; n <= ti.Segments; n++)
                    for (int n2 = 0; n2 <= tk.Segments; n2++)
                    {
                        if (i == k && n2 < n) continue;
                        Complex value = tubeTube[n, n2];
                        int endN = n == 0 ? 0 : n == ti.Segments ? 1 : -1;
                        int endN2 = n2 == 0 ? 0 : n2 == tk.Segments ? 1 : -1;
                        if (endN2 >= 0) value += endSign[endN2] * iOnK[endN2][n];
                        if (endN >= 0) value += endSign[endN] * kOnI[endN][n2];
                        if (endN >= 0 && endN2 >= 0) value += endSign[endN] * endSign[endN2] * fanFan[endN, endN2];
                        Put(Index(i, n), Index(k, n2), value);
                    }
            }
        SheetLoss.AddTo(z, surface, SheetImpedance?.Invoke(frequencyHz) ?? Complex.Zero);

        var signs = ports.Select(p => PortSigns(surface, p)).ToArray();
        var lu = ComplexLu.Factor(z, MaxDegreeOfParallelism);
        var y = new Complex[ports.Count, ports.Count];
        var edgeCurrents = new Complex[ports.Count][];
        var viaCurrents = new Complex[ports.Count][][];
        for (int q = 0; q < ports.Count; q++)
        {
            var rhs = new Complex[total];
            for (int i = 0; i < ports[q].EdgeBases.Count; i++)
                rhs[ports[q].EdgeBases[i]] = signs[q][i] * surface.Edges[ports[q].EdgeBases[i]].Length;
            var x = lu.Solve(rhs);
            edgeCurrents[q] = x.Take(nEdges).ToArray();
            viaCurrents[q] = new Complex[vias.Count][];
            for (int v = 0; v < vias.Count; v++)
            {
                var tube = new Complex[terms[v].Segments + 1];
                for (int n = 0; n <= terms[v].Segments; n++) tube[n] = x[Index(v, n)];
                viaCurrents[q][v] = tube;
            }
            for (int p = 0; p < ports.Count; p++)
            {
                Complex current = Complex.Zero;
                for (int i = 0; i < ports[p].EdgeBases.Count; i++)
                    current += signs[p][i] * surface.Edges[ports[p].EdgeBases[i]].Length * x[ports[p].EdgeBases[i]];
                y[p, q] = current;
            }
        }
        return new LevelsSolution(frequencyHz, y, edgeCurrents, vias, terms.Select(t => t.Nodes).ToArray(), viaCurrents,
            terms.Select(t => t.Fans[0].Vertex).ToArray(), terms.Select(t => t.Fans[1].Vertex).ToArray());
    }

    private static Complex[,] ToArray(ComplexDenseMatrix m)
    {
        var a = new Complex[m.Rows, m.Rows];
        for (int i = 0; i < m.Rows; i++)
            for (int j = 0; j < m.Rows; j++) a[i, j] = m[i, j];
        return a;
    }

    private ViaTerms BuildViaTerms(SurfaceStructure surface, LayeredStackup stackup, LevelsAssembly asm,
        VerticalKernels set, LevelVia via, double omega, Func<AttachmentFan, int, Complex[]> fanRow)
    {
        var heights = stackup.InterfaceHeights();
        int lo = via.LowerInterface, hi = via.UpperInterface;
        if (!(lo < hi)) throw new ArgumentException($"Via '{via.Name}': the lower interface must be below the upper one.");
        if (!asm.Own.ContainsKey(lo) || !asm.Own.ContainsKey(hi))
            throw new ArgumentException($"Via '{via.Name}' ends on a level with no metal.");
        var geometry = via.Geometry;
        var nodes = ViaNodes(stackup, lo, hi, geometry);
        int seg = nodes.Length - 1;
        var fans = new[]
        {
            new AttachmentFan(surface, LevelVertex(surface, asm.TriangleLevel, lo, geometry), geometry.RadiusMeters),
            new AttachmentFan(surface, LevelVertex(surface, asm.TriangleLevel, hi, geometry), geometry.RadiusMeters)
        };

        // Tube basis weights on the 2-point Gauss nodes of each element (bases 0..seg).
        var (gaussNodes, gaussWeights) = GaussLegendre.Rule(2, 0, 1);
        var zNodes = new double[2 * seg];
        for (int e = 0; e < seg; e++)
            for (int q = 0; q < 2; q++) zNodes[2 * e + q] = nodes[e] + (nodes[e + 1] - nodes[e]) * gaussNodes[q];
        int tubeBases = seg + 1;
        var valueWeights = new double[tubeBases][];
        var slopeWeights = new double[tubeBases][];
        for (int n = 0; n < tubeBases; n++)
        {
            valueWeights[n] = new double[zNodes.Length];
            slopeWeights[n] = new double[zNodes.Length];
            for (int e = 0; e < seg; e++)
            {
                double h = nodes[e + 1] - nodes[e];
                for (int q = 0; q < 2; q++)
                {
                    int idx = 2 * e + q;
                    double weight = gaussWeights[q] * h;
                    if (n == e + 1) { valueWeights[n][idx] += weight * gaussNodes[q]; slopeWeights[n][idx] += weight / h; }
                    if (n == e) { valueWeights[n][idx] += weight * (1 - gaussNodes[q]); slopeWeights[n][idx] += -weight / h; }
                }
            }
        }
        double maxRho = surface.Vertices.Max(p => Math.Sqrt((p.X - geometry.X) * (p.X - geometry.X) + (p.Y - geometry.Y) * (p.Y - geometry.Y)));
        double tableRho = Math.Sqrt(maxRho * maxRho + geometry.RadiusMeters * geometry.RadiusMeters) * 1.02;
        // A table at EVERY level with metal: the tube couples to sheets it does not touch too.
        var tables = asm.Own.Keys.ToDictionary(level => level,
            level => new ProbeCouplingTables(set, heights[level], zNodes, tableRho, MaxDegreeOfParallelism));

        var minusJOmega = new Complex(0, -omega);
        var overJOmega = 1 / new Complex(0, omega);
        var (l1, l2, l3, wq) = TriangleQuadrature.Rule(6);
        var triangleIntegrals = new Complex[surface.Triangles.Count, tubeBases];
        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            var table = tables[asm.TriangleLevel[t]];
            var verts = PVertices(surface, t);
            var axisHere = new Vector3D(geometry.X, geometry.Y, verts.Item1.Z);
            foreach (var (pa, pb, pc) in OuterPanels(verts, verts, new List<Vector3D> { axisHere }))
            {
                double panelArea = Vector3D.Cross(pb - pa, pc - pa).Length / 2;
                for (int i = 0; i < wq.Length; i++)
                {
                    var r = pa * l1[i] + pb * l2[i] + pc * l3[i];
                    double dx = r.X - geometry.X, dy = r.Y - geometry.Y;
                    double rhoEff = Math.Sqrt(dx * dx + dy * dy + geometry.RadiusMeters * geometry.RadiusMeters);
                    double weight = wq[i] * panelArea;
                    for (int q = 0; q < zNodes.Length; q++)
                    {
                        var (gxz, kPhi) = table.Evaluate(q, rhoEff);
                        for (int n = 0; n < tubeBases; n++)
                        {
                            double vw = valueWeights[n][q], sw = slopeWeights[n][q];
                            if (vw == 0 && sw == 0) continue;
                            triangleIntegrals[t, n] += weight * (minusJOmega * vw * gxz + overJOmega * sw * kPhi);
                        }
                    }
                }
            }
        }
        var coupling = new Complex[surface.BasisCount, tubeBases];
        for (int t = 0; t < surface.Triangles.Count; t++)
            foreach (var (basis, sign, _) in surface.TriangleSupports[t])
            {
                double div = sign * surface.Edges[basis].Length / surface.TriangleAreas[t];
                for (int n = 0; n < tubeBases; n++) coupling[basis, n] += div * triangleIntegrals[t, n];
            }
        Complex[] VertexVector(int level, double rho)
        {
            double rhoEff = Math.Sqrt(rho * rho + geometry.RadiusMeters * geometry.RadiusMeters);
            var result = new Complex[tubeBases];
            for (int q = 0; q < zNodes.Length; q++)
            {
                var (gxz, _) = tables[level].Evaluate(q, rhoEff);
                for (int n = 0; n < tubeBases; n++) result[n] += minusJOmega * valueWeights[n][q] * gxz;
            }
            return result;
        }
        var footprint = new HashSet<int>();
        foreach (var fan in fans)
            foreach (var wedge in fan.Wedges) { footprint.Add(wedge.Triangle); footprint.Add(wedge.NeighborTriangle); }
        return new ViaTerms
        {
            Via = via, Nodes = nodes, Fans = fans, FanLevels = new[] { lo, hi },
            TriangleIntegrals = triangleIntegrals, Coupling = coupling, VertexVector = VertexVector,
            FanRows = new[] { fanRow(fans[0], lo), fanRow(fans[1], hi) },
            Block = ProbeAssembly.ProbeSelfBlock(set, nodes, geometry, omega, includeTopBasis: true, MaxDegreeOfParallelism),
            Footprint = footprint
        };
    }

    /// <summary>Two vias' tube bases against each other at their axis spacing (the pin array's
    /// tube-to-tube term, for tubes between any heights).</summary>
    private static Complex[,] TubeMutual(VerticalKernels set, ViaTerms ti, ViaTerms tj, double omega)
    {
        double dx = ti.Via.Geometry.X - tj.Via.Geometry.X, dy = ti.Via.Geometry.Y - tj.Via.Geometry.Y;
        double rho = Math.Sqrt(dx * dx + dy * dy);
        var jOmega = new Complex(0, omega);
        var overJOmega = 1 / jOmega;
        var (gn, gw) = GaussLegendre.Rule(8, 0, 1);
        List<(double Z, double W, int Element, double T, double H)> Points(double[] nodes)
        {
            var list = new List<(double, double, int, double, double)>();
            for (int e = 0; e + 1 < nodes.Length; e++)
            {
                double h = nodes[e + 1] - nodes[e];
                int panels = Math.Clamp((int)Math.Ceiling(h / rho), 1, 16);
                for (int p = 0; p < panels; p++)
                    for (int q = 0; q < gn.Length; q++)
                    {
                        double t = (p + gn[q]) / panels;
                        list.Add((nodes[e] + h * t, gw[q] * h / panels, e, t, h));
                    }
            }
            return list;
        }
        var pi = Points(ti.Nodes);
        var pj = Points(tj.Nodes);
        var result = new Complex[ti.Segments + 1, tj.Segments + 1];
        foreach (var a in pi)
            foreach (var b in pj)
            {
                var (gzz, _, kphi) = set.Evaluate(rho, a.Z, b.Z);
                for (int sa = 0; sa < 2; sa++)
                {
                    int na = a.Element + sa;
                    double va = sa == 1 ? a.T : 1 - a.T, da = (sa == 1 ? 1.0 : -1.0) / a.H;
                    for (int sb = 0; sb < 2; sb++)
                    {
                        int nb = b.Element + sb;
                        double vb = sb == 1 ? b.T : 1 - b.T, db = (sb == 1 ? 1.0 : -1.0) / b.H;
                        result[na, nb] += a.W * b.W * (jOmega * va * vb * gzz + overJOmega * da * db * kphi);
                    }
                }
            }
        return result;
    }

    /// <summary>Tube node heights from the lower interface to the upper, a node at every interface
    /// between (as <see cref="ProbeAssembly.TubeNodes(LayeredStackup, int?, ProbeFeed)"/>).</summary>
    internal static double[] ViaNodes(LayeredStackup stackup, int lower, int upper, ProbeFeed geometry)
    {
        var heights = stackup.InterfaceHeights();
        double span = heights[upper] - heights[lower];
        var nodes = new List<double> { heights[lower] };
        for (int i = lower + 1; i <= upper; i++)
        {
            double thickness = stackup.Layers[i].ThicknessMeters;
            int count = Math.Max(1, (int)Math.Round(geometry.Segments * thickness / span));
            double h = thickness / count;
            if (h < 2 * geometry.RadiusMeters)
                throw new InvalidOperationException(
                    $"Layer {i} ({thickness:g4} m) is too thin for the via bore: elements of {h:g4} m against radius "
                    + $"{geometry.RadiusMeters:g4} m break the reduced thin-wire kernel's element ≳ 2·radius floor.");
            for (int k = 1; k < count; k++) nodes.Add(heights[i - 1] + thickness * k / count);
            nodes.Add(heights[i]);
        }
        if (nodes.Count < 3)
            throw new InvalidOperationException("A via needs at least two elements (one per junction); raise its segment count.");
        return nodes.ToArray();
    }

    /// <summary>The mesh vertex of <paramref name="level"/>'s metal at the via's position.</summary>
    private static int LevelVertex(SurfaceStructure surface, int[] triangleLevels, int level, ProbeFeed geometry)
    {
        int best = -1;
        double bestD = double.MaxValue, diameter = 0;
        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            if (triangleLevels[t] != level) continue;
            var (a, b, c) = surface.Triangles[t];
            foreach (int v in new[] { a, b, c })
            {
                double dx = surface.Vertices[v].X - geometry.X, dy = surface.Vertices[v].Y - geometry.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                diameter = Math.Max(diameter, d);
                if (d < bestD) { bestD = d; best = v; }
            }
        }
        if (best < 0 || bestD > 1e-9 * Math.Max(diameter, 1e-12))
            throw new ArgumentException(
                $"The via at ({geometry.X * 1e3:g6}, {geometry.Y * 1e3:g6}) mm needs a mesh vertex of the level-{level} metal there.");
        return best;
    }

    /// <summary><see cref="HalvesAgainstRwg"/> with the pair moments chosen per pair of levels.</summary>
    private static Complex[] HalvesAgainstRwgLevels(SurfaceStructure surface,
        Func<int, int, (SurfaceMoments A, SurfaceMoments Phi)> moments, double omega, AttachmentFan fan)
    {
        int nEdges = surface.BasisCount;
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);
        var row = new Complex[nEdges];
        var pArr = new Vector3D[3];
        var qArr = new Vector3D[3];
        var pIdx = new int[3];
        var qIdx = new int[3];
        foreach (var wedge in fan.Wedges)
        {
            int q = wedge.NeighborTriangle;
            double lI = surface.Edges[wedge.EdgeBasis].Length;
            double areaQ = surface.TriangleAreas[q];
            (qArr[0], qArr[1], qArr[2]) = PVertices(surface, q);
            (qIdx[0], qIdx[1], qIdx[2]) = surface.Triangles[q];
            int oppLocalQ = LocalIndex(qIdx, wedge.NeighborOpposite);
            for (int p = 0; p < surface.Triangles.Count; p++)
            {
                if (surface.TriangleSupports[p].Count == 0) continue;
                var (mA, mPhi) = moments(p, q);
                (pArr[0], pArr[1], pArr[2]) = PVertices(surface, p);
                (pIdx[0], pIdx[1], pIdx[2]) = surface.Triangles[p];
                double areaP = surface.TriangleAreas[p];
                foreach (var (basisM, signM, oppositeM) in surface.TriangleSupports[p])
                {
                    double lM = surface.Edges[basisM].Length;
                    int oppLocalM = LocalIndex(pIdx, oppositeM);
                    Complex dotSum = Complex.Zero;
                    for (int a = 0; a < 3; a++)
                    {
                        var fA = pArr[a] - pArr[oppLocalM];
                        for (int b = 0; b < 3; b++)
                            dotSum += Vector3D.Dot(fA, qArr[b] - qArr[oppLocalQ]) * mA[a, b];
                    }
                    Complex vector = vectorFactor * (signM * -1.0 * lM * lI / (4 * areaP * areaQ)) * dotSum;
                    Complex charge = chargeFactor * (signM * -1.0 * lM * lI / (areaP * areaQ)) * mPhi.M00;
                    row[basisM] += wedge.Gamma * (vector + charge);
                }
            }
        }
        return row;
    }

    /// <summary><see cref="HalvesAgainstHalves"/> with the pair moments chosen per pair of levels.</summary>
    private static Complex HalvesAgainstHalvesLevels(SurfaceStructure surface,
        Func<int, int, (SurfaceMoments A, SurfaceMoments Phi)> moments, double omega, AttachmentFan test, AttachmentFan source)
    {
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants.Eps0 * omega);
        var pArr = new Vector3D[3];
        var qArr = new Vector3D[3];
        var pIdx = new int[3];
        var qIdx = new int[3];
        Complex total = Complex.Zero;
        foreach (var wedge in source.Wedges)
        {
            int q = wedge.NeighborTriangle;
            double lI = surface.Edges[wedge.EdgeBasis].Length;
            double areaQ = surface.TriangleAreas[q];
            (qArr[0], qArr[1], qArr[2]) = PVertices(surface, q);
            (qIdx[0], qIdx[1], qIdx[2]) = surface.Triangles[q];
            int oppLocalQ = LocalIndex(qIdx, wedge.NeighborOpposite);
            foreach (var wedge2 in test.Wedges)
            {
                int p2 = wedge2.NeighborTriangle;
                double lJ = surface.Edges[wedge2.EdgeBasis].Length;
                double areaP2 = surface.TriangleAreas[p2];
                (pArr[0], pArr[1], pArr[2]) = PVertices(surface, p2);
                (pIdx[0], pIdx[1], pIdx[2]) = surface.Triangles[p2];
                int oppLocalP2 = LocalIndex(pIdx, wedge2.NeighborOpposite);
                var (mA2, mPhi2) = moments(p2, q);
                Complex dotSum2 = Complex.Zero;
                for (int a = 0; a < 3; a++)
                {
                    var fA = pArr[oppLocalP2] - pArr[a];
                    for (int b = 0; b < 3; b++)
                        dotSum2 += Vector3D.Dot(fA, qArr[oppLocalQ] - qArr[b]) * mA2[a, b];
                }
                Complex vector2 = vectorFactor * (lJ * lI / (4 * areaP2 * areaQ)) * dotSum2;
                Complex charge2 = chargeFactor * (lJ * lI / (areaP2 * areaQ)) * mPhi2.M00;
                total += wedge2.Gamma * wedge.Gamma * (vector2 + charge2);
            }
        }
        return total;
    }
}

/// <summary>The cross-level table as a radial G_A source: the potential on one level of a
/// horizontal current on another, at their lateral separation.</summary>
public sealed class CrossLevelRadialGaKernel : IRadialGaKernel
{
    private readonly CrossLevelKernelTable _table;
    public CrossLevelRadialGaKernel(CrossLevelKernelTable table) => _table = table;
    public Complex EvaluateGa(double rho) => _table.EvaluateKernels(rho).GA;
}
