using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Network;

/// <summary>
/// A two-terminal lumped part: a resistor, or an inductor or capacitor with an optional quality
/// factor (series loss ωL/Q or 1/(ωCQ), taken as constant over the band).
/// </summary>
public sealed record LumpedPart(LumpedPartKind Kind, double Value, double? Q = null)
{
    public static LumpedPart Resistor(double ohms) => new(LumpedPartKind.Resistor, ohms);
    public static LumpedPart Inductor(double henries, double? q = null) => new(LumpedPartKind.Inductor, henries, q);
    public static LumpedPart Capacitor(double farads, double? q = null) => new(LumpedPartKind.Capacitor, farads, q);

    public Complex Impedance(double frequencyHz)
    {
        double w = 2 * Math.PI * frequencyHz;
        return Kind switch
        {
            LumpedPartKind.Resistor => Value,
            LumpedPartKind.Inductor => new Complex(Q is { } q && q > 0 ? w * Value / q : 0, w * Value),
            _ => new Complex(Q is { } qc && qc > 0 ? 1 / (w * Value * qc) : 0, -1 / (w * Value)),
        };
    }

    public string Describe() => Kind switch
    {
        LumpedPartKind.Resistor => $"{Value:g4} Ω",
        LumpedPartKind.Inductor => $"{Value * 1e9:g4} nH" + (Q is { } q ? $" (Q {q:g3})" : ""),
        _ => $"{Value * 1e12:g4} pF" + (Q is { } qc ? $" (Q {qc:g3})" : ""),
    };
}

public enum LumpedPartKind { Resistor, Inductor, Capacitor }

/// <summary>
/// Closing some ports of a multi-port with lumped impedances: Z_kept = Z_kk − Z_kl·(Z_ll + Z_L)⁻¹·Z_lk.
/// This is exact for delta-gap ports — the solver's port is a pair of terminals, and a part across
/// them only adds V = −Z_L·I there — so a lumped component at an internal port, a resistor in a
/// trace or a load at a pin's base needs no new solve.
/// </summary>
public static class PortTermination
{
    /// <summary>The impedance matrix over the ports NOT in <paramref name="loads"/>; each loaded
    /// port index maps to its terminating impedance (0 is a short).</summary>
    public static Complex[,] Terminate(Complex[,] z, IReadOnlyDictionary<int, Complex> loads)
    {
        int n = z.GetLength(0);
        foreach (int port in loads.Keys)
            if (port < 0 || port >= n)
                throw new ArgumentOutOfRangeException(nameof(loads), $"Port {port} is outside 0..{n - 1}.");
        var kept = Enumerable.Range(0, n).Where(i => !loads.ContainsKey(i)).ToArray();
        var loaded = loads.Keys.OrderBy(i => i).ToArray();
        if (kept.Length == 0) throw new ArgumentException("Every port is terminated; nothing is left to see.", nameof(loads));
        var result = new Complex[kept.Length, kept.Length];
        for (int a = 0; a < kept.Length; a++)
            for (int b = 0; b < kept.Length; b++)
                result[a, b] = z[kept[a], kept[b]];
        if (loaded.Length == 0) return result;

        var zll = new ComplexDenseMatrix(loaded.Length, loaded.Length);
        for (int a = 0; a < loaded.Length; a++)
        {
            for (int b = 0; b < loaded.Length; b++) zll[a, b] = z[loaded[a], loaded[b]];
            zll[a, a] += loads[loaded[a]];
        }
        var lu = ComplexLu.Factor(zll);
        for (int b = 0; b < kept.Length; b++)
        {
            var column = new Complex[loaded.Length];
            for (int a = 0; a < loaded.Length; a++) column[a] = z[loaded[a], kept[b]];
            var solved = lu.Solve(column);
            for (int a = 0; a < kept.Length; a++)
            {
                Complex sum = Complex.Zero;
                for (int c = 0; c < loaded.Length; c++) sum += z[kept[a], loaded[c]] * solved[c];
                result[a, b] -= sum;
            }
        }
        return result;
    }

    /// <summary>Γ = (Z − Z₀)/(Z + Z₀).</summary>
    public static Complex Reflection(Complex z, double referenceOhms) => (z - referenceOhms) / (z + referenceOhms);

    /// <summary>Return loss in dB (positive), 20·log10(1/|Γ|).</summary>
    public static double ReturnLossDb(Complex z, double referenceOhms) =>
        -20 * Math.Log10(Math.Max(Reflection(z, referenceOhms).Magnitude, 1e-15));
}
