using System.Globalization;
using System.Numerics;

namespace OpenSim.Rf.Si;

/// <summary>Network data read from a Touchstone file, as S-parameters.</summary>
public sealed record TouchstoneData(
    IReadOnlyList<double> FrequenciesHz,
    IReadOnlyList<Complex[,]> Scattering,
    double ReferenceOhms)
{
    public int Ports => Scattering.Count > 0 ? Scattering[0].GetLength(0) : 0;
}

/// <summary>
/// Touchstone v1 (.sNp) import: the option line's frequency unit, parameter kind (S, Y or Z —
/// Y and Z are normalised to the reference in v1), number format (RI, MA, DB) and reference
/// resistance; the two-port column-major data order; data wrapped over several lines; and
/// comments. The noise block a two-port file may carry after its network data is skipped.
/// Version 2 keyword files are refused by name.
/// </summary>
public static class TouchstoneReader
{
    public static TouchstoneData ReadFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension.Length < 4 || extension[1] != 's' || extension[^1] != 'p'
            || !int.TryParse(extension[2..^1], out int ports) || ports < 1)
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}': the port count is read from the extension (.s1p, .s2p, …).");
        return Read(File.ReadAllText(path), ports);
    }

    public static TouchstoneData Read(string text, int ports)
    {
        if (ports < 1) throw new ArgumentOutOfRangeException(nameof(ports));
        double unit = 1e9, reference = 50;
        string kind = "S", format = "MA";
        bool optionSeen = false;
        var numbers = new List<double>();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int comment = line.IndexOf('!');
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
                throw new InvalidDataException(
                    "Touchstone version 2 files (keyword lines such as [Version]) are not read; export version 1.");
            if (line.StartsWith('#'))
            {
                if (optionSeen) continue;                       // only the first option line counts
                optionSeen = true;
                var tokens = line[1..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tokens.Length; i++)
                {
                    string token = tokens[i].ToUpperInvariant();
                    switch (token)
                    {
                        case "HZ": unit = 1; break;
                        case "KHZ": unit = 1e3; break;
                        case "MHZ": unit = 1e6; break;
                        case "GHZ": unit = 1e9; break;
                        case "S": case "Y": case "Z": kind = token; break;
                        case "G": case "H":
                            throw new InvalidDataException($"Touchstone {token}-parameter files are not read.");
                        case "RI": case "MA": case "DB": format = token; break;
                        case "R":
                            if (i + 1 >= tokens.Length || !double.TryParse(tokens[++i], NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out reference) || !(reference > 0))
                                throw new InvalidDataException("The option line's reference resistance is not a positive number.");
                            break;
                        default:
                            throw new InvalidDataException($"Unknown token '{tokens[i]}' on the Touchstone option line.");
                    }
                }
                continue;
            }
            foreach (string token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    throw new InvalidDataException($"'{token}' in the Touchstone data is not a number.");
                numbers.Add(value);
            }
        }

        int perPoint = 1 + 2 * ports * ports;
        var frequencies = new List<double>();
        var matrices = new List<Complex[,]>();
        for (int at = 0; at + perPoint <= numbers.Count; at += perPoint)
        {
            double f = numbers[at] * unit;
            // A two-port file may continue with noise parameters: their frequencies start
            // again from below the last network frequency.
            if (frequencies.Count > 0 && f <= frequencies[^1]) break;
            var m = new Complex[ports, ports];
            for (int k = 0; k < ports * ports; k++)
            {
                double a = numbers[at + 1 + 2 * k], b = numbers[at + 2 + 2 * k];
                Complex value = format switch
                {
                    "RI" => new Complex(a, b),
                    "MA" => Complex.FromPolarCoordinates(a, b * Math.PI / 180),
                    _ => Complex.FromPolarCoordinates(Math.Pow(10, a / 20), b * Math.PI / 180)
                };
                // Two ports: S11 S21 S12 S22. Every other size: row by row.
                int row = ports == 2 ? k % 2 : k / ports;
                int column = ports == 2 ? k / 2 : k % ports;
                m[row, column] = value;
            }
            frequencies.Add(f);
            matrices.Add(kind switch
            {
                "Z" => NetworkParameters.ImpedanceToScattering(Scale(m, reference), reference),
                "Y" => NetworkParameters.ImpedanceToScattering(
                    NetworkParameters.Invert(Scale(m, 1 / reference)), reference),
                _ => m
            });
        }
        if (frequencies.Count == 0)
            throw new InvalidDataException($"No {ports}-port network data was found in the Touchstone text.");
        return new TouchstoneData(frequencies, matrices, reference);
    }

    private static Complex[,] Scale(Complex[,] m, double factor)
    {
        int n = m.GetLength(0);
        var result = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                result[i, j] = m[i, j] * factor;
        return result;
    }
}

/// <summary>Conversions between impedance and scattering matrices for one real reference
/// resistance at every port.</summary>
public static class NetworkParameters
{
    /// <summary>S = (Z − Z₀)(Z + Z₀)⁻¹.</summary>
    public static Complex[,] ImpedanceToScattering(Complex[,] z, double referenceOhms)
    {
        int n = z.GetLength(0);
        var plus = new Complex[n, n];
        var minus = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                plus[i, j] = z[i, j] + (i == j ? referenceOhms : 0);
                minus[i, j] = z[i, j] - (i == j ? referenceOhms : 0);
            }
        return Multiply(minus, Invert(plus));
    }

    /// <summary>Z = Z₀(I + S)(I − S)⁻¹.</summary>
    public static Complex[,] ScatteringToImpedance(Complex[,] s, double referenceOhms)
    {
        int n = s.GetLength(0);
        var plus = new Complex[n, n];
        var minus = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                plus[i, j] = (i == j ? 1 : 0) + s[i, j];
                minus[i, j] = (i == j ? 1 : 0) - s[i, j];
            }
        var z = Multiply(plus, Invert(minus));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                z[i, j] *= referenceOhms;
        return z;
    }

    public static Complex[,] Multiply(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0), m = b.GetLength(1), inner = b.GetLength(0);
        var c = new Complex[n, m];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < inner; k++)
            {
                Complex aik = a[i, k];
                if (aik == Complex.Zero) continue;
                for (int j = 0; j < m; j++) c[i, j] += aik * b[k, j];
            }
        return c;
    }

    /// <summary>Inverse by Gauss–Jordan elimination with partial pivoting (port-count sizes).</summary>
    public static Complex[,] Invert(Complex[,] matrix)
    {
        int n = matrix.GetLength(0);
        var a = (Complex[,])matrix.Clone();
        var inverse = new Complex[n, n];
        for (int i = 0; i < n; i++) inverse[i, i] = 1;
        for (int k = 0; k < n; k++)
        {
            int pivot = k;
            for (int i = k + 1; i < n; i++)
                if (a[i, k].Magnitude > a[pivot, k].Magnitude) pivot = i;
            if (a[pivot, k] == Complex.Zero)
                throw new InvalidOperationException("The network matrix is singular.");
            if (pivot != k)
                for (int j = 0; j < n; j++)
                {
                    (a[k, j], a[pivot, j]) = (a[pivot, j], a[k, j]);
                    (inverse[k, j], inverse[pivot, j]) = (inverse[pivot, j], inverse[k, j]);
                }
            Complex scale = 1 / a[k, k];
            for (int j = 0; j < n; j++) { a[k, j] *= scale; inverse[k, j] *= scale; }
            for (int i = 0; i < n; i++)
            {
                if (i == k) continue;
                Complex factor = a[i, k];
                if (factor == Complex.Zero) continue;
                for (int j = 0; j < n; j++)
                {
                    a[i, j] -= factor * a[k, j];
                    inverse[i, j] -= factor * inverse[k, j];
                }
            }
        }
        return inverse;
    }
}
