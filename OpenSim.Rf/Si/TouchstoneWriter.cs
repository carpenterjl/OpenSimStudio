using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpenSim.Rf.Si;

/// <summary>
/// Touchstone v1 (.sNp) export — the SI interchange artifact. Real/imaginary format,
/// Hz frequency unit, invariant culture. The 2-port data order follows the spec's
/// special case (S11 S21 S12 S22 — COLUMN-major, unlike every other size); n ≥ 3 writes
/// row-major with at most four S-pairs per line, the frequency only on the first.
/// </summary>
public static class TouchstoneWriter
{
    public static string Write(IReadOnlyList<double> frequenciesHz,
        IReadOnlyList<Complex[,]> scattering, double referenceOhms = 50)
    {
        if (frequenciesHz.Count != scattering.Count)
            throw new ArgumentException("One S matrix per frequency point is required.");
        if (frequenciesHz.Count == 0)
            throw new ArgumentException("At least one frequency point is required.");
        int n = scattering[0].GetLength(0);
        foreach (var s in scattering)
            if (s.GetLength(0) != n || s.GetLength(1) != n)
                throw new ArgumentException("Every S matrix must be the same square size.");

        var text = new StringBuilder();
        text.AppendLine($"! OpenSim Studio {n}-port S-parameters (SI track)");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"# HZ S RI R {referenceOhms:G}"));

        for (int p = 0; p < frequenciesHz.Count; p++)
        {
            var s = scattering[p];
            if (n <= 2)
            {
                var line = new StringBuilder(Num(frequenciesHz[p]));
                // n = 2 is the spec's column-major special case: S11 S21 S12 S22.
                foreach (var (i, j) in n == 1
                             ? new[] { (0, 0) }
                             : new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                    line.Append(' ').Append(Num(s[i, j].Real)).Append(' ').Append(Num(s[i, j].Imaginary));
                text.AppendLine(line.ToString());
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    var line = new StringBuilder(i == 0 ? Num(frequenciesHz[p]) : "");
                    int onLine = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (onLine == 4) { text.AppendLine(line.ToString()); line.Clear(); onLine = 0; }
                        if (line.Length > 0) line.Append(' ');
                        line.Append(Num(s[i, j].Real)).Append(' ').Append(Num(s[i, j].Imaginary));
                        onLine++;
                    }
                    text.AppendLine(line.ToString());
                }
            }
        }
        return text.ToString();
    }

    private static string Num(double value) =>
        value.ToString("G12", CultureInfo.InvariantCulture);
}
