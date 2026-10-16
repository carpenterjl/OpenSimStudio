using System.Globalization;
using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Extraction;
using Xunit.Abstractions;

namespace OpenSim.Tests.Extraction;

/// <summary>
/// The wideband (rational-fit) SPICE subcircuit of FU-33: the netlist, read back and solved as
/// a circuit (a small nodal analysis over its R, L, C, V, E and F elements — what any SPICE
/// does), must reproduce the swept impedance it was fitted to, at every sweep point.
/// </summary>
public class WidebandSpiceTests
{
    private readonly ITestOutputHelper _output;
    public WidebandSpiceTests(ITestOutputHelper output) => _output = output;

    private static TraceSegment3D Bar(double x0, double y0, double x1, double y1, double w = 0.3e-3) =>
        new(new Vector3D(x0, y0, 0), new Vector3D(x1, y1, 0), w, 35e-6);

    [Fact]
    public void TheWidebandSubcircuit_ReproducesTheSweptImpedance()
    {
        // Two coupled traces swept over four decades: R and L move with the skin and
        // proximity effect, which a single-frequency subcircuit cannot carry.
        var frequencies = Enumerable.Range(0, 13).Select(k => 1e5 * Math.Pow(10, k / 3.0)).ToArray();
        var two = LoopExtraction.TwoChains(new[] { Bar(0, 0, 15e-3, 0) }, "A",
            new[] { Bar(0, 0.6e-3, 15e-3, 0.6e-3) }, "B", frequencies);
        var result = ParasiticExport.SpiceWideband("pair", two.PortNames, frequencies,
            two.Points.Select(p => p.Impedance).ToList());
        foreach (string note in result.Notes) _output.WriteLine(note);
        _output.WriteLine($"worst fit {result.WorstFitFraction:p3}, min eig Re Z {result.MinimumResistanceEigenvalue:g3} ohm, "
            + $"{result.NegativeElements} negative elements");
        Assert.True(result.WorstFitFraction < 1e-3, $"fit {result.WorstFitFraction}");

        var circuit = SpiceNetlist.Parse(result.Text);
        for (int k = 0; k < frequencies.Length; k++)
        {
            var z = new Complex[2, 2];
            for (int q = 0; q < 2; q++)
            {
                var v = circuit.Solve(frequencies[k], drivePositive: $"p{q + 1}", "n1", "n2");
                for (int p = 0; p < 2; p++)
                    z[p, q] = v($"p{p + 1}") - v($"n{p + 1}");
            }
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    var data = two.Points[k].Impedance[i, j];
                    double allowed = 3 * result.WorstFitFraction * data.Magnitude + 1e-9 * two.Points[k].Impedance[i, i].Magnitude;
                    Assert.True((z[i, j] - data).Magnitude <= allowed,
                        $"{frequencies[k]:g3} Hz Z[{i},{j}]: netlist {z[i, j]}, data {data}");
                }
        }
    }

    [Fact]
    public void AWidebandModel_NeedsASweep()
    {
        var two = LoopExtraction.TwoChains(new[] { Bar(0, 0, 15e-3, 0) }, "A",
            new[] { Bar(0, 0.6e-3, 15e-3, 0.6e-3) }, "B", new[] { 1e6, 1e7, 1e8 });
        Assert.Throws<ArgumentException>(() => ParasiticExport.SpiceWideband("pair", two.PortNames,
            new[] { 1e6, 1e7, 1e8 }, two.Points.Select(p => p.Impedance).ToList()));
    }
}

/// <summary>A minimal AC nodal analysis over the elements the wideband export writes
/// (R, L, C, zero-volt V sensors, E and F controlled sources), to read a netlist back the way
/// a simulator would.</summary>
internal sealed class SpiceNetlist
{
    private sealed record Element(char Kind, string Name, string[] Nodes, double Value, string? Control);

    private readonly List<Element> _elements = new();

    public static SpiceNetlist Parse(string text)
    {
        var netlist = new SpiceNetlist();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '*' or '.') continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            char kind = char.ToUpperInvariant(line[0]);
            double Value(int at) => double.Parse(t[at], CultureInfo.InvariantCulture);
            switch (kind)
            {
                case 'R' or 'L' or 'C':
                    netlist._elements.Add(new Element(kind, t[0], new[] { t[1], t[2] }, Value(3), null));
                    break;
                case 'V':
                    netlist._elements.Add(new Element('V', t[0], new[] { t[1], t[2] }, Value(3), null));
                    break;
                case 'E':
                    netlist._elements.Add(new Element('E', t[0], new[] { t[1], t[2], t[3], t[4] }, Value(5), null));
                    break;
                case 'F':
                    netlist._elements.Add(new Element('F', t[0], new[] { t[1], t[2] }, Value(4), t[3]));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected element: {line}");
            }
        }
        return netlist;
    }

    /// <summary>Drives one ampere into <paramref name="drivePositive"/>, with every node of
    /// <paramref name="grounded"/> held at zero (the first takes the ampere back out), and
    /// returns the node voltages.</summary>
    public Func<string, Complex> Solve(double frequencyHz, string drivePositive, params string[] grounded)
    {
        string driveNegative = grounded[0];
        var nodes = new Dictionary<string, int> { ["0"] = -1 };
        int NodeOf(string name)
        {
            if (!nodes.TryGetValue(name, out int id))
                nodes[name] = id = nodes.Count - 1;
            return id;
        }
        foreach (var e in _elements)
            foreach (string node in e.Nodes)
                NodeOf(node);
        NodeOf(drivePositive);
        NodeOf(driveNegative);
        int n = nodes.Count - 1;
        var branchOf = new Dictionary<string, int>();
        foreach (var e in _elements)
            if (e.Kind is 'V' or 'E')
                branchOf[e.Name] = n + branchOf.Count;
        int size = n + branchOf.Count;
        var a = new Complex[size, size];
        var rhs = new Complex[size];
        var omega = 2 * Math.PI * frequencyHz;
        void Add(int row, int column, Complex value)
        {
            if (row >= 0 && column >= 0) a[row, column] += value;
        }
        foreach (var e in _elements)
        {
            int p = NodeOf(e.Nodes[0]), q = NodeOf(e.Nodes[1]);
            switch (e.Kind)
            {
                case 'R' or 'L' or 'C':
                    Complex y = e.Kind switch
                    {
                        'R' => 1 / (Complex)e.Value,
                        'L' => 1 / new Complex(0, omega * e.Value),
                        _ => new Complex(0, omega * e.Value)
                    };
                    Add(p, p, y); Add(q, q, y); Add(p, q, -y); Add(q, p, -y);
                    break;
                case 'V':
                {
                    int b = branchOf[e.Name];
                    Add(p, b, 1); Add(q, b, -1);
                    Add(b, p, 1); Add(b, q, -1);
                    if (b >= 0) rhs[b] += e.Value;
                    break;
                }
                case 'E':
                {
                    int b = branchOf[e.Name];
                    int c = NodeOf(e.Nodes[2]), d = NodeOf(e.Nodes[3]);
                    Add(p, b, 1); Add(q, b, -1);
                    Add(b, p, 1); Add(b, q, -1);
                    Add(b, c, -e.Value); Add(b, d, e.Value);
                    break;
                }
                case 'F':
                {
                    int b = branchOf[e.Control!];
                    Add(p, b, e.Value); Add(q, b, -e.Value);
                    break;
                }
            }
        }
        rhs[NodeOf(drivePositive)] += 1;
        int negative = NodeOf(driveNegative);
        if (negative >= 0) rhs[negative] -= 1;
        // Tie the grounded terminals to the reference so every piece is determinate.
        foreach (string name in grounded)
        {
            int node = NodeOf(name);
            if (node < 0) continue;
            for (int j = 0; j < size; j++) a[node, j] = 0;
            a[node, node] = 1;
            rhs[node] = 0;
        }
        var x = GaussianSolve(a, rhs, size);
        return name => NodeOf(name) < 0 ? Complex.Zero : x[NodeOf(name)];
    }

    private static Complex[] GaussianSolve(Complex[,] a, Complex[] b, int n)
    {
        for (int k = 0; k < n; k++)
        {
            int pivot = k;
            for (int i = k + 1; i < n; i++)
                if (a[i, k].Magnitude > a[pivot, k].Magnitude) pivot = i;
            if (a[pivot, k] == Complex.Zero) throw new InvalidOperationException("Singular test circuit.");
            if (pivot != k)
            {
                for (int j = 0; j < n; j++) (a[k, j], a[pivot, j]) = (a[pivot, j], a[k, j]);
                (b[k], b[pivot]) = (b[pivot], b[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                Complex factor = a[i, k] / a[k, k];
                if (factor == Complex.Zero) continue;
                for (int j = k; j < n; j++) a[i, j] -= factor * a[k, j];
                b[i] -= factor * b[k];
            }
        }
        var x = new Complex[n];
        for (int i = n - 1; i >= 0; i--)
        {
            Complex sum = b[i];
            for (int j = i + 1; j < n; j++) sum -= a[i, j] * x[j];
            x[i] = sum / a[i, i];
        }
        return x;
    }
}
