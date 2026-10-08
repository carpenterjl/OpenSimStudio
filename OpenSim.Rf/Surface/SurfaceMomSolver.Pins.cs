using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>A vertical pin through the substrate, from the ground plane up to the sheet metal:
/// a coaxial feed with a port at its base (<see cref="IsPort"/>), or a shorting pin whose base is
/// welded to the ground. A lumped part at a pin's base is a port terminated afterwards
/// (<see cref="OpenSim.Rf.Network.PortTermination"/>).</summary>
public sealed record VerticalPin(string Name, ProbeFeed Geometry, bool IsPort = true);

/// <summary>A series gap on the sheet metal used as a port (a lumped part in a trace, or a feed
/// across a cut), named for the network it ends up in.</summary>
public sealed record NamedSurfacePort(string Name, SurfacePort Port);

/// <summary>The currents of a pin array for one excitation: the raw RWG sheet currents, and per pin
/// its tube current at each node (index 0 = ground contact, last = the junction's) at its node
/// heights; the port voltages and currents (currents into the ports).</summary>
public sealed record PinArrayCurrents(Complex[] RawEdgeCurrents, Complex[][] TubeCurrents, double[][] TubeNodes,
    Complex[] PortVolts, Complex[] PortCurrents)
{
    /// <summary>½·Re Σ V·I* — the power the ports deliver [W].</summary>
    public double InputPowerWatts => 0.5 * PortVolts.Zip(PortCurrents, (v, i) => (v * Complex.Conjugate(i)).Real).Sum();
}

/// <summary>One frequency of a pin-array solve: the admittance matrix over the ports (pin bases
/// first, in pin order, then the sheet ports), every shorting pin closed.</summary>
public sealed record PinArraySolution(double FrequencyHz, IReadOnlyList<string> PortNames, Complex[,] Admittance)
{
    /// <summary>The system solution per port (1 V on it, the others shorted) and where in it the
    /// sheet, each pin's tube and each junction sit.</summary>
    internal Complex[][]? PortSolutions { get; init; }
    internal int EdgeCount { get; init; }
    internal int[] TubeStart { get; init; } = Array.Empty<int>();
    internal int[] JunctionIndex { get; init; } = Array.Empty<int>();
    internal double[][] Nodes { get; init; } = Array.Empty<double[]>();

    /// <summary>The currents for the given port voltages (one per port, in port order), by
    /// superposing the solve's unit excitations.</summary>
    public PinArrayCurrents Currents(IReadOnlyList<Complex> portVolts)
    {
        var solutions = PortSolutions ?? throw new InvalidOperationException("This solution carries no currents.");
        int ports = PortNames.Count;
        if (portVolts.Count != ports)
            throw new ArgumentException($"{ports} port voltage(s) are needed.", nameof(portVolts));
        var x = new Complex[solutions[0].Length];
        for (int q = 0; q < ports; q++)
            for (int i = 0; i < x.Length; i++) x[i] += portVolts[q] * solutions[q][i];
        var edges = x.Take(EdgeCount).ToArray();
        var tubes = new Complex[TubeStart.Length][];
        for (int p = 0; p < tubes.Length; p++)
        {
            int segments = Nodes[p].Length - 1;
            tubes[p] = new Complex[segments + 1];
            for (int n = 0; n < segments; n++) tubes[p][n] = x[TubeStart[p] + n];
            tubes[p][segments] = x[JunctionIndex[p]];
        }
        var currents = new Complex[ports];
        for (int p = 0; p < ports; p++)
            for (int q = 0; q < ports; q++) currents[p] += Admittance[p, q] * portVolts[q];
        return new PinArrayCurrents(edges, tubes, Nodes, portVolts.ToArray(), currents);
    }

    /// <summary>Z = Y⁻¹ over the same ports.</summary>
    public Complex[,] Impedance()
    {
        int n = PortNames.Count;
        var y = new ComplexDenseMatrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                y[i, j] = Admittance[i, j];
        var lu = ComplexLu.Factor(y);
        var z = new Complex[n, n];
        for (int j = 0; j < n; j++)
        {
            var e = new Complex[n];
            e[j] = 1;
            var column = lu.Solve(e);
            for (int i = 0; i < n; i++) z[i, j] = column[i];
        }
        return z;
    }
}

/// <summary>
/// Several vertical pins in one layered solve — the probe-fed system of
/// <see cref="SolveProbeFed(SurfaceStructure, LayeredKernelTable, ProbeFeed, double)"/> with one
/// tube and one junction unknown PER PIN, which is what a shorting pin, an inverted-F antenna, a
/// PIFA on its ground, or a patch with two feeds needs.
///
/// <para>Every pin's own terms are the single-probe terms, computed by the same arithmetic. The
/// new entries are the cross terms between two pins i and j:</para>
/// <list type="bullet">
/// <item>tube i against tube j — G_A^zz and K_Φ of the medium at the axis spacing, a Gauss product
/// per element pair, panels split when the spacing is short against an element;</item>
/// <item>tube i against junction j's sheet part — the by-parts coupling the single probe uses for
/// its own junction, with tube i's coupling integrals read on junction j's half-RWG triangles;</item>
/// <item>junction i's sheet part against junction j's — disc against disc (one fan's outer rule over
/// the other's potential), each disc against the other's half-RWGs, and the half-RWGs against each
/// other by the ordinary layered pair moments.</item>
/// </list>
/// <para><b>The junction vertex term.</b> The tube–sheet coupling integrates the tube's horizontal
/// vector potential A_h = −∇V by parts onto the sheet current's divergence. A junction's disc D has
/// divergence δ(v) at its vertex as well as the half-RWGs' distributed −l/A; the δ's scalar part
/// cancels against the tube top's end, but its vector part leaves −jω·V(v), the coupling potential's
/// vector part read at the vertex. The single-probe solve left this term out until it was found here. It vanishes when the
/// medium is homogeneous (G_A^xz = 0 for air over the ground), which is why the εr = 1 gates never
/// saw it; with a dielectric it does not. Measured on a feed pin and a shorting pin 9 mm apart under
/// a 60 mm plate at 30 MHz (a two-post loop inductance, which cannot depend on εr): 2.371 nH in
/// air; at εr = 4.4, 1.668 nH without the term, 2.549 nH with only each pin's own vertex term,
/// 1.496 nH with only the cross terms, 2.376 nH with both. Both solves include it: here own and
/// cross.</para>
/// <para>Every cross entry is computed once and written to both (i, j) and (j, i), so the matrix is
/// complex-symmetric as the single-probe one is. Two pins whose attachment fans (the triangles at
/// the pin vertex and their outward neighbours) touch are refused: the disc integrals assume the
/// fans are apart.</para>
/// </summary>
public sealed partial class SurfaceMomSolver
{
    /// <summary>Facts every consumer of a pin-array result must show next to it.</summary>
    public static IReadOnlyList<string> PinArrayAssumptions { get; } = new[]
    {
        "Zero-thickness sheet and pins; perfect conductors unless a sheet or wire surface impedance is given, which adds the sheet's and each tube's ohmic loss (the ground plane stays perfect).",
        "A grounded layered stackup (infinite PEC ground); all sheet metal at one interface. Metal on two levels joined by a via is not modelled.",
        "Each pin is a thin tube from the ground to the sheet, attached by the 1/ρ junction mode at a mesh vertex; a port pin is driven by a delta gap at its base, a shorting pin's base is welded to the ground.",
        "Pins' attachment fans must not touch; the tube-to-tube coupling uses the axis spacing.",
        "The far field and surface-wave power of a pin array (LayeredFarField with the pins) add every tube's vertical leg and junction coherently, each tube at its own position, for any port excitation."
    };

    /// <summary>Pins and sheet ports over a single grounded slab.</summary>
    public PinArraySolution SolvePins(SurfaceStructure surface, LayeredKernelTable kernel,
        IReadOnlyList<VerticalPin> pins, IReadOnlyList<NamedSurfacePort>? sheetPorts = null)
    {
        var set = new VerticalKernelSet(kernel.Substrate, kernel.FrequencyHz);
        var nodes = pins.Select(p => ProbeAssembly.TubeNodes(kernel.Substrate, p.Geometry)).ToArray();
        return SolvePinsCore(surface, new LayeredKernelSplit(kernel), new LayeredRadialGaKernel(kernel),
            kernel.FrequencyHz, set, nodes, kernel.Substrate.ThicknessMeters, pins, sheetPorts);
    }

    /// <summary>Pins and sheet ports over a multi-layer grounded stackup (metal at the table's
    /// source interface).</summary>
    public PinArraySolution SolvePins(SurfaceStructure surface, MultiLayerKernelTable kernel,
        IReadOnlyList<VerticalPin> pins, IReadOnlyList<NamedSurfacePort>? sheetPorts = null)
    {
        var set = new MultiLayerVerticalKernelSet(kernel.Stackup, kernel.FrequencyHz);
        var nodes = pins.Select(p => ProbeAssembly.TubeNodes(kernel.Stackup, kernel.SourceInterface, p.Geometry)).ToArray();
        double metal = nodes.Length > 0 ? nodes[0][^1] : kernel.Stackup.TotalThicknessMeters;
        return SolvePinsCore(surface, new LayeredKernelSplit(kernel), new MultiLayerRadialGaKernel(kernel),
            kernel.FrequencyHz, set, nodes, metal, pins, sheetPorts);
    }

    /// <summary>One pin's own terms — exactly those of the single-probe solve.</summary>
    private sealed class PinTerms
    {
        public required VerticalPin Pin { get; init; }
        public required AttachmentFan Fan { get; init; }
        public required double[] Nodes { get; init; }
        public int Segments => Nodes.Length - 1;
        public required Complex[,] TriangleIntegrals { get; init; }  // [triangle, tube basis]
        public required Complex[,] Coupling { get; init; }           // [RWG, tube basis]
        public required Complex[] JunctionTube { get; init; }        // tube basis vs own junction sheet part
        public required Complex[] DiscV { get; init; }
        public required Complex DiscVHalf { get; init; }
        public required Complex DiscDD { get; init; }
        public required Complex[] HalfRow { get; init; }
        public required Complex HalfHalf { get; init; }
        public required ComplexDenseMatrix Block { get; init; }
        public required HashSet<int> Footprint { get; init; }
        public required Func<double, Complex[]> VertexVector { get; init; }
    }


    private PinArraySolution SolvePinsCore(SurfaceStructure surface, in LayeredKernelSplit split,
        IRadialGaKernel gaKernel, double frequencyHz, VerticalKernels set, double[][] nodes,
        double metalHeight, IReadOnlyList<VerticalPin> pins, IReadOnlyList<NamedSurfacePort>? sheetPorts)
    {
        if (pins.Count == 0) throw new ArgumentException("At least one pin is needed.", nameof(pins));
        sheetPorts ??= Array.Empty<NamedSurfacePort>();
        foreach (var port in sheetPorts) ValidateLayeredSurface(surface, port.Port);
        if (!pins.Any(p => p.IsPort) && sheetPorts.Count == 0)
            throw new ArgumentException("No port: at least one pin or sheet gap must be a port.", nameof(pins));
        var names = pins.Where(p => p.IsPort).Select(p => p.Name).Concat(sheetPorts.Select(p => p.Name)).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ArgumentException("Port names must be distinct.", nameof(pins));

        double omega = 2 * Math.PI * frequencyHz;
        var jOmega = new Complex(0, omega);
        var terms = new PinTerms[pins.Count];
        for (int i = 0; i < pins.Count; i++)
        {
            int vertex = ResolveProbeVertex(surface, pins[i].Geometry);
            terms[i] = BuildPinTerms(surface, split, gaKernel, omega, set, nodes[i], metalHeight, pins[i], vertex);
        }
        for (int i = 0; i < pins.Count; i++)
            for (int j = i + 1; j < pins.Count; j++)
                if (terms[i].Footprint.Overlaps(terms[j].Footprint))
                    throw new ArgumentException(
                        $"Pins '{pins[i].Name}' and '{pins[j].Name}' are too close for the mesh: their junction "
                        + "fans share triangles. Move them apart or use a finer mesh.", nameof(pins));

        // Unknowns: [RWG | pin 0: tube bases, junction | pin 1: ... ].
        int nEdges = surface.BasisCount;
        var tubeStart = new int[pins.Count];
        var junctionIndex = new int[pins.Count];
        int next = nEdges;
        for (int i = 0; i < pins.Count; i++)
        {
            tubeStart[i] = next;
            next += terms[i].Segments;
            junctionIndex[i] = next++;
        }
        int total = next;
        var z = new ComplexDenseMatrix(total, total);
        var zCc = AssembleLayeredCore(surface, split, omega, MaxDegreeOfParallelism);
        for (int a = 0; a < nEdges; a++)
            for (int b = 0; b < nEdges; b++)
                z[a, b] = zCc[a, b];

        // Each pin's own blocks: the single-probe layout.
        for (int i = 0; i < pins.Count; i++)
        {
            var t = terms[i];
            int seg = t.Segments, s0 = tubeStart[i], jI = junctionIndex[i];
            for (int m = 0; m < nEdges; m++)
            {
                for (int n = 0; n < seg; n++)
                {
                    z[m, s0 + n] = t.Coupling[m, n];
                    z[s0 + n, m] = t.Coupling[m, n];
                }
                Complex zmJ = t.Coupling[m, seg] + t.DiscV[m] + t.HalfRow[m];
                z[m, jI] = zmJ;
                z[jI, m] = zmJ;
            }
            for (int n = 0; n < seg; n++)
            {
                for (int n2 = 0; n2 < seg; n2++)
                    z[s0 + n, s0 + n2] = t.Block[n, n2];
                Complex znJ = t.Block[n, seg] + t.JunctionTube[n];
                z[s0 + n, jI] = znJ;
                z[jI, s0 + n] = znJ;
            }
            z[jI, jI] = t.Block[seg, seg] + 2 * t.JunctionTube[seg]
                + t.DiscDD + 2 * t.DiscVHalf + t.HalfHalf;
        }

        // Cross terms between pins.
        for (int i = 0; i < pins.Count; i++)
            for (int j = i + 1; j < pins.Count; j++)
            {
                var ti = terms[i];
                var tj = terms[j];
                int segI = ti.Segments, segJ = tj.Segments;
                var tubeTube = TubeMutualBlock(set, ti, tj, omega);
                var tubeIvsJ = JunctionTubeCross(surface, ti, tj);   // tube i bases vs junction j's sheet part
                var tubeJvsI = JunctionTubeCross(surface, tj, ti);   // tube j bases vs junction i's sheet part
                Complex sheetSheet = jOmega * ti.Fan.DiscAgainst(gaKernel, surface, tj.Fan)
                    + DiscAgainstHalves(surface, gaKernel, ti.Fan, tj.Fan, jOmega)
                    + DiscAgainstHalves(surface, gaKernel, tj.Fan, ti.Fan, jOmega)
                    + HalvesAgainstHalves(surface, split, omega, ti.Fan, tj.Fan);

                int sI = tubeStart[i], sJ = tubeStart[j], jI = junctionIndex[i], jJ = junctionIndex[j];
                void Put(int r, int c, Complex v) { z[r, c] = v; z[c, r] = v; }
                for (int n = 0; n < segI; n++)
                {
                    for (int n2 = 0; n2 < segJ; n2++) Put(sI + n, sJ + n2, tubeTube[n, n2]);
                    Put(sI + n, jJ, tubeTube[n, segJ] + tubeIvsJ[n]);
                }
                for (int n2 = 0; n2 < segJ; n2++)
                    Put(jI, sJ + n2, tubeTube[segI, n2] + tubeJvsI[n2]);
                Put(jI, jJ, tubeTube[segI, segJ] + tubeIvsJ[segI] + tubeJvsI[segJ] + sheetSheet);
            }

        // Ohmic loss: the sheet's surface impedance on the RWG block, each pin's on its tube.
        SheetLoss.AddTo(z, surface, SheetImpedance?.Invoke(frequencyHz) ?? Complex.Zero);
        if (WireSurfaceImpedance?.Invoke(frequencyHz) is { } wireZ && wireZ != Complex.Zero)
            for (int i = 0; i < pins.Count; i++)
            {
                Complex perMeter = wireZ / (2 * Math.PI * pins[i].Geometry.RadiusMeters);
                foreach (var (m, n, value) in TubeMass(terms[i].Nodes, tubeStart[i], junctionIndex[i]))
                    z[m, n] += perMeter * value;
            }

        // Ports: pin bases (the base half hat has f(0) = 1) and sheet gaps.
        var portSigns = sheetPorts.Select(p => PortSigns(surface, p.Port)).ToArray();
        var portPins = Enumerable.Range(0, pins.Count).Where(i => pins[i].IsPort).ToArray();
        int portCount = portPins.Length + sheetPorts.Count;
        var lu = ComplexLu.Factor(z, MaxDegreeOfParallelism);
        var y = new Complex[portCount, portCount];
        var solutions = new Complex[portCount][];
        for (int q = 0; q < portCount; q++)
        {
            var rhs = new Complex[total];
            if (q < portPins.Length) rhs[tubeStart[portPins[q]]] = 1;
            else
            {
                var port = sheetPorts[q - portPins.Length].Port;
                var signs = portSigns[q - portPins.Length];
                for (int k = 0; k < port.EdgeBases.Count; k++)
                    rhs[port.EdgeBases[k]] = signs[k] * surface.Edges[port.EdgeBases[k]].Length;
            }
            var x = lu.Solve(rhs);
            solutions[q] = x;
            for (int p = 0; p < portCount; p++)
            {
                if (p < portPins.Length) { y[p, q] = x[tubeStart[portPins[p]]]; continue; }
                var port = sheetPorts[p - portPins.Length].Port;
                var signs = portSigns[p - portPins.Length];
                Complex current = Complex.Zero;
                for (int k = 0; k < port.EdgeBases.Count; k++)
                    current += signs[k] * surface.Edges[port.EdgeBases[k]].Length * x[port.EdgeBases[k]];
                y[p, q] = current;
            }
        }
        return new PinArraySolution(frequencyHz, names, y)
        {
            PortSolutions = solutions, EdgeCount = nEdges, TubeStart = tubeStart, JunctionIndex = junctionIndex,
            Nodes = terms.Select(t => t.Nodes).ToArray()
        };
    }

    /// <summary>The single-probe terms of one pin (fan, coupling tables, tube block, junction
    /// self terms) — the same steps, in the same order, as <c>SolveProbeFedCore</c>.</summary>
    private PinTerms BuildPinTerms(SurfaceStructure surface, in LayeredKernelSplit split,
        IRadialGaKernel gaKernel, double omega, VerticalKernels set, double[] tubeNodes,
        double metalHeight, VerticalPin pin, int vertex)
    {
        var probe = pin.Geometry;
        var fan = new AttachmentFan(surface, vertex, probe.RadiusMeters);
        int segments = tubeNodes.Length - 1;

        var (gaussNodes, gaussWeights) = GaussLegendre.Rule(2, 0, 1);
        var zNodes = new double[2 * segments];
        for (int e = 0; e < segments; e++)
            for (int q = 0; q < 2; q++)
                zNodes[2 * e + q] = tubeNodes[e] + (tubeNodes[e + 1] - tubeNodes[e]) * gaussNodes[q];
        double maxRho = 0;
        var axis = new Vector3D(probe.X, probe.Y, surface.Vertices[vertex].Z);
        foreach (var v in surface.Vertices)
        {
            double dx = v.X - probe.X, dy = v.Y - probe.Y;
            maxRho = Math.Max(maxRho, Math.Sqrt(dx * dx + dy * dy));
        }
        double tableRhoMax = Math.Sqrt(maxRho * maxRho + probe.RadiusMeters * probe.RadiusMeters) * 1.02;
        var tables = new ProbeCouplingTables(set, metalHeight, zNodes, tableRhoMax, MaxDegreeOfParallelism);

        int tubeBases = segments + 1;
        var valueWeights = new double[tubeBases][];
        var slopeWeights = new double[tubeBases][];
        for (int n = 0; n < tubeBases; n++)
        {
            valueWeights[n] = new double[zNodes.Length];
            slopeWeights[n] = new double[zNodes.Length];
            for (int e = 0; e < segments; e++)
            {
                double h = tubeNodes[e + 1] - tubeNodes[e];
                for (int q = 0; q < 2; q++)
                {
                    int idx = 2 * e + q;
                    double weight = gaussWeights[q] * h;
                    if (n == e + 1)
                    {
                        valueWeights[n][idx] += weight * gaussNodes[q];
                        slopeWeights[n][idx] += weight / h;
                    }
                    if (n == e)
                    {
                        valueWeights[n][idx] += weight * (1 - gaussNodes[q]);
                        slopeWeights[n][idx] += -weight / h;
                    }
                }
            }
        }

        var (l1, l2, l3, wq) = TriangleQuadrature.Rule(6);
        var triangleIntegrals = new Complex[surface.Triangles.Count, tubeBases];
        var minusJOmega = new Complex(0, -omega);
        var overJOmega = 1 / new Complex(0, omega);
        var jOmega = new Complex(0, omega);
        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            var verts = PVertices(surface, t);
            foreach (var (pa, pb, pc) in OuterPanels(verts, verts, new List<Vector3D> { axis }))
            {
                double panelArea = Vector3D.Cross(pb - pa, pc - pa).Length / 2;
                for (int i = 0; i < wq.Length; i++)
                {
                    var r = pa * l1[i] + pb * l2[i] + pc * l3[i];
                    double dx = r.X - probe.X, dy = r.Y - probe.Y;
                    double rhoEff = Math.Sqrt(dx * dx + dy * dy + probe.RadiusMeters * probe.RadiusMeters);
                    double weight = wq[i] * panelArea;
                    for (int q = 0; q < zNodes.Length; q++)
                    {
                        var (gxz, kPhi) = tables.Evaluate(q, rhoEff);
                        for (int n = 0; n < tubeBases; n++)
                        {
                            double vw = valueWeights[n][q];
                            double sw = slopeWeights[n][q];
                            if (vw == 0 && sw == 0) continue;
                            triangleIntegrals[t, n] += weight * (minusJOmega * vw * gxz + overJOmega * sw * kPhi);
                        }
                    }
                }
            }
        }

        int nEdges = surface.BasisCount;
        var coupling = new Complex[nEdges, tubeBases];
        for (int t = 0; t < surface.Triangles.Count; t++)
            foreach (var (basis, sign, _) in surface.TriangleSupports[t])
            {
                double div = sign * surface.Edges[basis].Length / surface.TriangleAreas[t];
                for (int n = 0; n < tubeBases; n++)
                    coupling[basis, n] += div * triangleIntegrals[t, n];
            }

        var junctionTube = new Complex[tubeBases];
        foreach (var wedge in fan.Wedges)
        {
            double div = -wedge.Gamma * surface.Edges[wedge.EdgeBasis].Length
                / surface.TriangleAreas[wedge.NeighborTriangle];
            for (int n = 0; n < tubeBases; n++)
                junctionTube[n] += div * triangleIntegrals[wedge.NeighborTriangle, n];
        }
        // The vector part of the coupling potential at a junction vertex a lateral distance ρ
        // from this tube's axis (see the class remarks on the disc's divergence).
        Complex[] VertexVector(double rho)
        {
            double rhoEff = rho == 0 ? probe.RadiusMeters : Math.Sqrt(rho * rho + probe.RadiusMeters * probe.RadiusMeters);
            var result = new Complex[tubeBases];
            for (int q = 0; q < zNodes.Length; q++)
            {
                var (gxz, _) = tables.Evaluate(q, rhoEff);
                for (int n = 0; n < tubeBases; n++) result[n] += minusJOmega * valueWeights[n][q] * gxz;
            }
            return result;
        }
        {
            var own = VertexVector(0);
            for (int n = 0; n < tubeBases; n++) junctionTube[n] += own[n];
        }

        var discV = new Complex[nEdges];
        for (int t = 0; t < surface.Triangles.Count; t++)
        {
            if (surface.TriangleSupports[t].Count == 0) continue;
            var verts = PVertices(surface, t);
            foreach (var (pa, pb, pc) in OuterPanels(verts, verts, new List<Vector3D> { fan.VertexPosition }))
            {
                double panelArea = Vector3D.Cross(pb - pa, pc - pa).Length / 2;
                for (int i = 0; i < wq.Length; i++)
                {
                    var r = pa * l1[i] + pb * l2[i] + pc * l3[i];
                    var (ax, ay) = fan.DiscPotential(gaKernel, surface, r);
                    foreach (var (basis, sign, opposite) in surface.TriangleSupports[t])
                    {
                        var fDir = r - surface.Vertices[opposite];
                        double scale = sign * surface.Edges[basis].Length / (2 * surface.TriangleAreas[t]);
                        discV[basis] += jOmega * wq[i] * panelArea * scale * (fDir.X * ax + fDir.Y * ay);
                    }
                }
            }
        }
        Complex discVHalf = DiscAgainstHalves(surface, gaKernel, fan, fan, jOmega);
        Complex discDD = jOmega * fan.DiscSelf(gaKernel, surface);
        var halfRow = HalvesAgainstRwg(surface, split, omega, fan);
        Complex halfHalf = HalvesAgainstHalves(surface, split, omega, fan, fan);

        var block = ProbeAssembly.ProbeSelfBlock(set, tubeNodes, probe, omega,
            includeTopBasis: true, MaxDegreeOfParallelism);

        var footprint = new HashSet<int>();
        foreach (var wedge in fan.Wedges)
        {
            footprint.Add(wedge.Triangle);
            footprint.Add(wedge.NeighborTriangle);
        }
        return new PinTerms
        {
            Pin = pin, Fan = fan, Nodes = tubeNodes, TriangleIntegrals = triangleIntegrals,
            Coupling = coupling, JunctionTube = junctionTube, DiscV = discV, DiscVHalf = discVHalf,
            DiscDD = discDD, HalfRow = halfRow, HalfHalf = halfHalf, Block = block, Footprint = footprint,
            VertexVector = VertexVector
        };
    }

    /// <summary>Σᵢγᵢ·jω⟨Hᵢ, A(D)⟩: the half-RWGs of <paramref name="halves"/> tested against the
    /// disc of <paramref name="disc"/> (the same fan for a pin's own term).</summary>
    private static Complex DiscAgainstHalves(SurfaceStructure surface, IRadialGaKernel gaKernel,
        AttachmentFan disc, AttachmentFan halves, Complex jOmega)
    {
        var (l1, l2, l3, wq) = TriangleQuadrature.Rule(6);
        Complex total = Complex.Zero;
        foreach (var wedge in halves.Wedges)
        {
            int t = wedge.NeighborTriangle;
            var verts = PVertices(surface, t);
            var pOpp = surface.Vertices[wedge.NeighborOpposite];
            double lI = surface.Edges[wedge.EdgeBasis].Length;
            Complex sum = Complex.Zero;
            foreach (var (pa, pb, pc) in OuterPanels(verts, verts, new List<Vector3D> { disc.VertexPosition }))
            {
                double panelArea = Vector3D.Cross(pb - pa, pc - pa).Length / 2;
                for (int i = 0; i < wq.Length; i++)
                {
                    var r = pa * l1[i] + pb * l2[i] + pc * l3[i];
                    var (ax, ay) = disc.DiscPotential(gaKernel, surface, r);
                    var fDir = pOpp - r;
                    sum += wq[i] * panelArea * (fDir.X * ax + fDir.Y * ay);
                }
            }
            total += wedge.Gamma * jOmega * (lI / (2 * surface.TriangleAreas[t])) * sum;
        }
        return total;
    }

    /// <summary>Σᵢγᵢ⟨f_m, E(Hᵢ)⟩ for every RWG basis m.</summary>
    private Complex[] HalvesAgainstRwg(SurfaceStructure surface, in LayeredKernelSplit split,
        double omega, AttachmentFan fan)
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
                var (mA, mPhi) = LayeredPairMoments(surface, p, q, split);
                if (p == q)
                {
                    mA = mA.Symmetrized();
                    mPhi = mPhi.Symmetrized();
                }
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

    /// <summary>ΣΣγᵢγⱼ⟨Hᵢ, E(Hⱼ)⟩ over the half-RWGs of two fans (the same fan for a pin's own
    /// term).</summary>
    private Complex HalvesAgainstHalves(SurfaceStructure surface, in LayeredKernelSplit split,
        double omega, AttachmentFan test, AttachmentFan source)
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
                var (mA2, mPhi2) = LayeredPairMoments(surface, p2, q, split);
                if (p2 == q)
                {
                    mA2 = mA2.Symmetrized();
                    mPhi2 = mPhi2.Symmetrized();
                }
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

    /// <summary>Tube <paramref name="tube"/>'s bases against the sheet part of
    /// <paramref name="junction"/>'s junction: the by-parts coupling read on the junction's
    /// half-RWG triangles (its disc is chargeless).</summary>
    private Complex[] JunctionTubeCross(SurfaceStructure surface, PinTerms tube, PinTerms junction)
    {
        var result = new Complex[tube.Segments + 1];
        foreach (var wedge in junction.Fan.Wedges)
        {
            double div = -wedge.Gamma * surface.Edges[wedge.EdgeBasis].Length
                / surface.TriangleAreas[wedge.NeighborTriangle];
            for (int n = 0; n <= tube.Segments; n++)
                result[n] += div * tube.TriangleIntegrals[wedge.NeighborTriangle, n];
        }
        {
            double dx = tube.Pin.Geometry.X - junction.Pin.Geometry.X, dy = tube.Pin.Geometry.Y - junction.Pin.Geometry.Y;
            var vector = tube.VertexVector(Math.Sqrt(dx * dx + dy * dy));
            for (int n = 0; n <= tube.Segments; n++) result[n] += vector[n];
        }
        return result;
    }

    /// <summary>Tube i's bases (top half hat included) against tube j's: jω∫∫f f′ G_A^zz +
    /// (1/jω)∫∫f′ḟ′ K_Φ at the axis spacing. Elements are cut into panels no longer than the
    /// spacing, with an 8-point rule on each.</summary>
    private static Complex[,] TubeMutualBlock(VerticalKernels set, PinTerms ti, PinTerms tj, double omega)
    {
        double dx = ti.Pin.Geometry.X - tj.Pin.Geometry.X, dy = ti.Pin.Geometry.Y - tj.Pin.Geometry.Y;
        double rho = Math.Sqrt(dx * dx + dy * dy);
        var jOmega = new Complex(0, omega);
        var overJOmega = 1 / jOmega;
        var (gn, gw) = GaussLegendre.Rule(8, 0, 1);

        // Quadrature points per tube: (height, weight·h, element, local coordinate t).
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
                // Basis e (falling) and e+1 (rising) live on element e.
                for (int sa = 0; sa < 2; sa++)
                {
                    int na = a.Element + sa;
                    double va = sa == 1 ? a.T : 1 - a.T;
                    double da = (sa == 1 ? 1.0 : -1.0) / a.H;
                    for (int sb = 0; sb < 2; sb++)
                    {
                        int nb = b.Element + sb;
                        double vb = sb == 1 ? b.T : 1 - b.T;
                        double db = (sb == 1 ? 1.0 : -1.0) / b.H;
                        result[na, nb] += a.W * b.W * (jOmega * va * vb * gzz + overJOmega * da * db * kphi);
                    }
                }
            }
        return result;
    }
}
