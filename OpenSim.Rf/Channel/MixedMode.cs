using System.Numerics;

namespace OpenSim.Rf.Channel;

/// <summary>Which single-ended ports (0-based) make the two differential ports.</summary>
public readonly record struct DifferentialPairing(int Plus1, int Minus1, int Plus2, int Minus2)
{
    /// <summary>The pairing of a four-port file in the given port order: the near pair is
    /// differential port 1, the far pair differential port 2.</summary>
    public static DifferentialPairing For(PortOrder order) => order == PortOrder.NearOddFarEven
        ? new DifferentialPairing(0, 2, 1, 3)
        : new DifferentialPairing(0, 1, 2, 3);
}

/// <summary>
/// Mixed-mode S-parameters of a four-port: the two port pairs seen as a differential and a
/// common-mode port each. With single-ended reference Z₀ at every port, the differential
/// ports are referred to 2·Z₀ and the common-mode ports to Z₀/2, and the conversion is the
/// orthogonal change of basis S_mm = M·S·Mᵀ with rows (1, −1)/√2 and (1, 1)/√2 per pair.
/// The result is ordered d1, d2, c1, c2: the upper-left 2×2 is S_dd, the lower-right S_cc,
/// the lower-left S_cd (differential in, common out) and the upper-right S_dc.
/// </summary>
public static class MixedMode
{
    public const int Differential = 0, Common = 1;

    public static Complex[,] FromSingleEnded(Complex[,] s, DifferentialPairing pairing)
    {
        if (s.GetLength(0) != 4 || s.GetLength(1) != 4)
            throw new ArgumentException("Mixed-mode conversion takes a four-port.", nameof(s));
        var ports = new[] { pairing.Plus1, pairing.Minus1, pairing.Plus2, pairing.Minus2 };
        if (ports.Distinct().Count() != 4 || ports.Any(p => p is < 0 or > 3))
            throw new ArgumentException("The pairing must name each of the four ports once.", nameof(pairing));
        double r = Math.Sqrt(0.5);
        // Rows: d1, d2, c1, c2 over the file's ports.
        var m = new double[4, 4];
        m[0, pairing.Plus1] = r; m[0, pairing.Minus1] = -r;
        m[1, pairing.Plus2] = r; m[1, pairing.Minus2] = -r;
        m[2, pairing.Plus1] = r; m[2, pairing.Minus1] = r;
        m[3, pairing.Plus2] = r; m[3, pairing.Minus2] = r;
        var result = new Complex[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                Complex sum = Complex.Zero;
                for (int a = 0; a < 4; a++)
                {
                    if (m[i, a] == 0) continue;
                    for (int b = 0; b < 4; b++)
                        if (m[j, b] != 0) sum += m[i, a] * s[a, b] * m[j, b];
                }
                result[i, j] = sum;
            }
        return result;
    }

    /// <summary>One 2×2 block of a mixed-mode matrix: response mode (rows) to stimulus mode
    /// (columns). Block(mm, Differential, Differential) is S_dd; Block(mm, Common,
    /// Differential) is S_cd, the conversion of a differential signal to common mode.</summary>
    public static Complex[,] Block(Complex[,] mixed, int responseMode, int stimulusMode)
    {
        var block = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                block[i, j] = mixed[2 * responseMode + i, 2 * stimulusMode + j];
        return block;
    }

    /// <summary>The mixed-mode matrices of a four-port channel at its own frequencies.</summary>
    public static IReadOnlyList<Complex[,]> Of(ImportedChannel channel, DifferentialPairing pairing)
    {
        if (channel.Ports != 4)
            throw new InvalidOperationException($"Mixed-mode S-parameters need a four-port file; this one has {channel.Ports}.");
        return channel.FrequenciesHz.Select(f => FromSingleEnded(channel.At(f), pairing)).ToList();
    }

    /// <summary>Mixed-mode Touchstone text: ports d1, d2, c1, c2. Touchstone v1 holds one
    /// reference, so the file is written against the single-ended one and the comment
    /// lines say what each port is referred to.</summary>
    public static string ToTouchstone(ImportedChannel channel, DifferentialPairing pairing) =>
        OpenSim.Rf.Si.TouchstoneWriter.Write(channel.FrequenciesHz, Of(channel, pairing), channel.ReferenceOhms, new[]
        {
            $"differential port 1 (file ports {pairing.Plus1 + 1} and {pairing.Minus1 + 1}), referred to {2 * channel.ReferenceOhms:g4} ohm",
            $"differential port 2 (file ports {pairing.Plus2 + 1} and {pairing.Minus2 + 1}), referred to {2 * channel.ReferenceOhms:g4} ohm",
            $"common-mode port 1, referred to {channel.ReferenceOhms / 2:g4} ohm",
            $"common-mode port 2, referred to {channel.ReferenceOhms / 2:g4} ohm"
        });
}
