using System.Globalization;
using System.Numerics;
using System.Text;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Extraction;

/// <summary>A capacitor of an exported subcircuit, between two of its pins (or pin and
/// ground, "0").</summary>
public sealed record ExportCapacitor(string NodeA, string NodeB, double Farads);

/// <summary>
/// Extracted resistance and inductance as files a circuit simulator reads.
/// </summary>
public static class ParasiticExport
{
    /// <summary>
    /// A SPICE subcircuit for a port impedance matrix Z = R + jωL at ONE frequency: port k
    /// lies between pins <c>pk</c> and <c>nk</c> and is a resistor R_kk and an inductor L_kk
    /// in series; inductors are coupled by K statements (k = L_ij/√(L_ii·L_jj)), and the
    /// resistance two ports share (R_ij, the loss one port's current causes in the other's
    /// path) by current-controlled voltage sources. R and L move with frequency through
    /// skin and proximity effect, and a subcircuit of fixed elements holds them at the
    /// frequency named in its header — the Touchstone export carries the whole sweep.
    /// </summary>
    public static string SpiceSubcircuit(string name, IReadOnlyList<string> portNames, Complex[,] impedance,
        double frequencyHz, IReadOnlyList<ExportCapacitor>? capacitors = null)
    {
        int n = impedance.GetLength(0);
        if (portNames.Count != n) throw new ArgumentException($"Expected {n} port names.");
        if (!(frequencyHz > 0)) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        double omega = 2 * Math.PI * frequencyHz;
        var r = new double[n, n];
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                r[i, j] = impedance[i, j].Real;
                l[i, j] = impedance[i, j].Imaginary / omega;
            }
        for (int i = 0; i < n; i++)
            if (!(l[i, i] > 0 && r[i, i] >= 0))
                throw new InvalidOperationException(
                    $"Port {i + 1} ({portNames[i]}) has no positive inductance at {frequencyHz:g4} Hz; "
                    + "a series R–L subcircuit cannot represent it.");

        string id = new(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        var text = new StringBuilder();
        text.AppendLine($"* OpenSim Studio extracted parasitics: {name}");
        text.AppendLine(Inv($"* R and L as solved at {frequencyHz:G6} Hz (they change with frequency)."));
        for (int k = 0; k < n; k++) text.AppendLine($"* port {k + 1} (p{k + 1}, n{k + 1}): {portNames[k]}");
        text.AppendLine($".subckt {id} {string.Join(" ", Enumerable.Range(1, n).Select(k => $"p{k} n{k}"))}");
        for (int i = 0; i < n; i++)
        {
            int k = i + 1;
            text.AppendLine(Inv($"R{k} p{k} a{k} {r[i, i]:G9}"));
            text.AppendLine($"V{k} a{k} b{k} 0");
            // The shared-resistance sources sit between the inductor and the negative pin.
            var shared = Enumerable.Range(0, n).Where(j => j != i && Math.Abs(r[i, j]) > 1e-9 * Math.Sqrt(Math.Max(r[i, i] * r[j, j], 1e-300))).ToList();
            string node = shared.Count == 0 ? $"n{k}" : $"c{k}_0";
            text.AppendLine(Inv($"L{k} b{k} {node} {l[i, i]:G9}"));
            for (int s = 0; s < shared.Count; s++)
            {
                int j = shared[s];
                string next = s == shared.Count - 1 ? $"n{k}" : $"c{k}_{s + 1}";
                text.AppendLine(Inv($"H{k}_{j + 1} c{k}_{s} {next} V{j + 1} {r[i, j]:G9}"));
            }
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double k = l[i, j] / Math.Sqrt(l[i, i] * l[j, j]);
                if (Math.Abs(k) < 1e-9) continue;
                text.AppendLine(Inv($"K{i + 1}_{j + 1} L{i + 1} L{j + 1} {Math.Clamp(k, -0.999999, 0.999999):G9}"));
            }
        if (capacitors is not null)
            for (int c = 0; c < capacitors.Count; c++)
                text.AppendLine(Inv($"C{c + 1} {capacitors[c].NodeA} {capacitors[c].NodeB} {capacitors[c].Farads:G9}"));
        text.AppendLine($".ends {id}");
        return text.ToString();
    }

    /// <summary>The swept port impedance as Touchstone S-parameters.</summary>
    public static string Touchstone(IReadOnlyList<double> frequenciesHz, IReadOnlyList<Complex[,]> impedance,
        IReadOnlyList<string> portNames, double referenceOhms = 50) =>
        TouchstoneWriter.Write(frequenciesHz,
            impedance.Select(z => NetworkParameters.ImpedanceToScattering(z, referenceOhms)).ToList(),
            referenceOhms, portNames);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
