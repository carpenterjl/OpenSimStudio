using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Network;

/// <summary>
/// A response as a delay times a rational function with stable poles,
/// H(f) = e^{−j2πfτ}·(d + Σ r_k/(s − p_k)), s = jf/f_n — causal by construction: every
/// term is the transform of a function that is zero before t = τ. Real coefficients: the
/// poles and residues come in conjugate pairs (a complex pole is listed once, its conjugate
/// implied), so H(−f) = H(f)* and H(0) is real.
/// </summary>
public sealed class RationalResponse
{
    internal RationalResponse(Complex[] poles, Complex[] residues, double constant, double delaySeconds,
        double frequencyScaleHz)
    {
        Poles = poles;
        Residues = residues;
        Constant = constant;
        DelaySeconds = delaySeconds;
        FrequencyScaleHz = frequencyScaleHz;
    }

    /// <summary>Real poles and the upper member of each complex pair, in units of
    /// <see cref="FrequencyScaleHz"/> (s = jf/f_n).</summary>
    public IReadOnlyList<Complex> Poles { get; }

    public IReadOnlyList<Complex> Residues { get; }
    public double Constant { get; }
    public double DelaySeconds { get; }
    public double FrequencyScaleHz { get; }

    /// <summary>Poles counted with their conjugates.</summary>
    public int Order => Poles.Sum(p => p.Imaginary == 0 ? 1 : 2);

    public Complex At(double frequencyHz) =>
        Delay(frequencyHz) * DelayFree(new Complex(0, frequencyHz / FrequencyScaleHz));

    internal Complex Delay(double frequencyHz) => Complex.FromPolarCoordinates(1, -2 * Math.PI * frequencyHz * DelaySeconds);

    internal Complex DelayFree(Complex s)
    {
        Complex sum = Constant;
        for (int k = 0; k < Poles.Count; k++)
        {
            sum += Residues[k] / (s - Poles[k]);
            if (Poles[k].Imaginary != 0) sum += Complex.Conjugate(Residues[k]) / (s - Complex.Conjugate(Poles[k]));
        }
        return sum;
    }

    /// <summary>The same poles and delay with the residues and constant moved by a fit of
    /// <paramref name="change"/> (delay-free values at the given frequencies) — how a
    /// correction is added without moving a pole.</summary>
    internal RationalResponse WithAdded(IReadOnlyList<double> frequenciesHz, IReadOnlyList<Complex> change,
        IReadOnlyList<double>? weights = null)
    {
        var (residues, constant) = VectorFit.FitResidues(Poles, frequenciesHz, change, FrequencyScaleHz, weights);
        return new RationalResponse(Poles.ToArray(), Residues.Zip(residues, (a, b) => a + b).ToArray(),
            Constant + constant, DelaySeconds, FrequencyScaleHz);
    }
}

/// <summary>
/// Vector fitting (Gustavsen and Semlyen, 1999; the relaxed form of Gustavsen, 2006): poles
/// are moved to the zeros of a weighting function σ(s) found together with σ·H by one linear
/// least-squares solve, repeatedly, unstable ones reflected into the left half plane; the
/// residues are then a second linear solve. The fit is causal and stable whatever the data,
/// so how closely it can follow the data measures how nearly causal the data is.
/// </summary>
public static class VectorFit
{
    /// <param name="values">The response at <paramref name="frequenciesHz"/> with
    /// <paramref name="delaySeconds"/> still in it.</param>
    /// <param name="polePairs">Starting complex pole pairs, spread over the band.</param>
    public static RationalResponse Fit(IReadOnlyList<double> frequenciesHz, IReadOnlyList<Complex> values,
        int polePairs, double delaySeconds = 0, int iterations = 20)
    {
        if (frequenciesHz.Count != values.Count) throw new ArgumentException("One value per frequency.");
        if (polePairs < 1) throw new ArgumentOutOfRangeException(nameof(polePairs));
        if (frequenciesHz.Count < 2 * polePairs + 2)
            throw new ArgumentException($"{frequenciesHz.Count} points cannot carry {polePairs} pole pairs.");
        double scale = frequenciesHz.Max();
        if (!(scale > 0)) throw new ArgumentException("The band must reach above 0 Hz.");
        var flat = new Complex[values.Count];
        for (int k = 0; k < flat.Length; k++)
            flat[k] = values[k] * Complex.FromPolarCoordinates(1, 2 * Math.PI * frequenciesHz[k] * delaySeconds);

        double low = Math.Max(frequenciesHz.Min(), scale / 1000) / scale;
        var poles = new List<Complex>();
        for (int k = 0; k < polePairs; k++)
        {
            double beta = polePairs == 1 ? 0.5 * (low + 1) : low + (1 - low) * k / (polePairs - 1);
            poles.Add(new Complex(-beta / 100, beta));
        }

        RationalResponse? best = null;
        double bestError = double.MaxValue, size = flat.Max(v => v.Magnitude);
        int stale = 0;
        for (int iteration = 0; iteration <= iterations; iteration++)
        {
            var (residues, constant) = FitResidues(poles, frequenciesHz, flat, scale);
            var model = new RationalResponse(poles.ToArray(), residues, constant, delaySeconds, scale);
            double error = 0;
            for (int k = 0; k < flat.Length; k++)
                error = Math.Max(error, (model.DelayFree(new Complex(0, frequenciesHz[k] / scale)) - flat[k]).Magnitude);
            stale = error < 0.99 * bestError ? 0 : stale + 1;
            if (error < bestError) { bestError = error; best = model; }
            // Settled: at rounding level, or three relocations without a 1 % gain.
            if (iteration == iterations || bestError <= 1e-13 * size || stale >= 3) break;
            poles = Relocate(poles, frequenciesHz, flat, scale);
        }
        return best!;
    }

    /// <summary>Largest |model − data| over the points.</summary>
    public static double LargestDeviation(RationalResponse model, IReadOnlyList<double> frequenciesHz,
        IReadOnlyList<Complex> values)
    {
        double worst = 0;
        for (int k = 0; k < values.Count; k++)
            worst = Math.Max(worst, (model.At(frequenciesHz[k]) - values[k]).Magnitude);
        return worst;
    }

    /// <summary>The real basis: 1/(s − a) for a real pole; 1/(s − p) + 1/(s − p*) and
    /// j/(s − p) − j/(s − p*) for a complex pair, whose coefficients are the residue's real
    /// and imaginary parts.</summary>
    private static int Basis(IReadOnlyList<Complex> poles, Complex s, Span<Complex> row)
    {
        int column = 0;
        foreach (Complex p in poles)
        {
            if (p.Imaginary == 0)
                row[column++] = 1 / (s - p);
            else
            {
                Complex a = 1 / (s - p), b = 1 / (s - Complex.Conjugate(p));
                row[column++] = a + b;
                row[column++] = Complex.ImaginaryOne * (a - b);
            }
        }
        return column;
    }

    private static int Columns(IReadOnlyList<Complex> poles) => poles.Sum(p => p.Imaginary == 0 ? 1 : 2);

    internal static (Complex[] Residues, double Constant) FitResidues(IReadOnlyList<Complex> poles,
        IReadOnlyList<double> frequenciesHz, IReadOnlyList<Complex> values, double scale,
        IReadOnlyList<double>? weights = null)
    {
        int n = Columns(poles), points = values.Count;
        var a = new double[2 * points, n + 1];
        var rhs = new double[2 * points];
        Span<Complex> row = stackalloc Complex[n];
        for (int k = 0; k < points; k++)
        {
            double w = weights?[k] ?? 1;
            Basis(poles, new Complex(0, frequenciesHz[k] / scale), row);
            for (int j = 0; j < n; j++)
            {
                a[2 * k, j] = w * row[j].Real;
                a[2 * k + 1, j] = w * row[j].Imaginary;
            }
            a[2 * k, n] = w;
            rhs[2 * k] = w * values[k].Real;
            rhs[2 * k + 1] = w * values[k].Imaginary;
        }
        var x = HouseholderLeastSquares.Solve(a, rhs);
        var residues = new Complex[poles.Count];
        int column = 0;
        for (int i = 0; i < poles.Count; i++)
        {
            if (poles[i].Imaginary == 0) residues[i] = x[column++];
            else { residues[i] = new Complex(x[column], x[column + 1]); column += 2; }
        }
        return (residues, x[n]);
    }

    /// <summary>One relaxed pole relocation: solve Σc·φ + d − H·(Σc̃·φ + d̃) = 0 in least
    /// squares with Re Σ_k σ(s_k) = K fixing the scale, then the zeros of σ are the
    /// eigenvalues of A − b·c̃ᵀ/d̃.</summary>
    private static List<Complex> Relocate(IReadOnlyList<Complex> poles, IReadOnlyList<double> frequenciesHz,
        IReadOnlyList<Complex> values, double scale)
    {
        int n = Columns(poles), points = values.Count;
        int unknowns = 2 * n + 2;
        var a = new double[2 * points + 1, unknowns];
        var rhs = new double[2 * points + 1];
        Span<Complex> row = stackalloc Complex[n];
        double norm = 0;
        foreach (Complex v in values) norm += v.Magnitude * v.Magnitude;
        norm = Math.Sqrt(norm);
        var sums = new double[n];
        for (int k = 0; k < points; k++)
        {
            Basis(poles, new Complex(0, frequenciesHz[k] / scale), row);
            Complex h = values[k];
            for (int j = 0; j < n; j++)
            {
                a[2 * k, j] = row[j].Real;
                a[2 * k + 1, j] = row[j].Imaginary;
                Complex hp = -h * row[j];
                a[2 * k, n + 1 + j] = hp.Real;
                a[2 * k + 1, n + 1 + j] = hp.Imaginary;
                sums[j] += row[j].Real;
            }
            a[2 * k, n] = 1;
            a[2 * k, 2 * n + 1] = -h.Real;
            a[2 * k + 1, 2 * n + 1] = -h.Imaginary;
        }
        double weight = Math.Max(norm, 1e-300) / points;
        for (int j = 0; j < n; j++) a[2 * points, n + 1 + j] = weight * sums[j];
        a[2 * points, 2 * n + 1] = weight * points;
        rhs[2 * points] = weight * points;
        var x = HouseholderLeastSquares.Solve(a, rhs);
        double dTilde = x[2 * n + 1];
        var cTilde = x.AsSpan(n + 1, n).ToArray();
        if (Math.Abs(dTilde) < 1e-8)
        {
            // σ's constant too small to divide by: fix it and solve again without the
            // relaxation row.
            dTilde = dTilde < 0 ? -1e-8 : 1e-8;
            var fixedA = new double[2 * points, 2 * n + 1];
            var fixedRhs = new double[2 * points];
            for (int r = 0; r < 2 * points; r++)
            {
                for (int c = 0; c <= 2 * n; c++) fixedA[r, c] = a[r, c];
                fixedRhs[r] = -a[r, 2 * n + 1] * dTilde;
            }
            var y = HouseholderLeastSquares.Solve(fixedA, fixedRhs);
            cTilde = y.AsSpan(n + 1, n).ToArray();
        }

        // State-space form of σ: A block diagonal (a; or [α β; −β α] per pair), b = 1 or (2, 0).
        var z = new double[n, n];
        var b = new double[n];
        int at = 0;
        foreach (Complex p in poles)
        {
            if (p.Imaginary == 0)
            {
                z[at, at] = p.Real;
                b[at] = 1;
                at++;
            }
            else
            {
                z[at, at] = p.Real; z[at, at + 1] = p.Imaginary;
                z[at + 1, at] = -p.Imaginary; z[at + 1, at + 1] = p.Real;
                b[at] = 2;
                at += 2;
            }
        }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) z[i, j] -= b[i] * cTilde[j] / dTilde;
        var zeros = NonsymmetricEigenvalues.Compute(z);
        return Stabilize(zeros);
    }

    /// <summary>Reflect right-half-plane poles, and pair the eigenvalues of a real matrix:
    /// one entry per real pole and per conjugate pair.</summary>
    private static List<Complex> Stabilize(Complex[] eigenvalues)
    {
        var result = new List<Complex>();
        var upper = new List<Complex>();
        var lower = new List<Complex>();
        foreach (Complex e in eigenvalues)
        {
            double re = e.Real, im = e.Imaginary;
            if (re > 0) re = -re;
            if (re == 0) re = -1e-6 * Math.Max(Math.Abs(im), 1e-3);
            double size = Math.Max(Math.Abs(re) + Math.Abs(im), 1e-12);
            if (Math.Abs(im) <= 1e-9 * size) result.Add(new Complex(re, 0));
            else if (im > 0) upper.Add(new Complex(re, im));
            else lower.Add(new Complex(re, -im));
        }
        // Rounding can leave a pair's members on different sides of the threshold: the
        // unmatched ones of the larger side become real poles, the nearest the axis first.
        var larger = upper.Count >= lower.Count ? upper : lower;
        int surplus = Math.Abs(upper.Count - lower.Count);
        larger.Sort((x, y) => x.Imaginary.CompareTo(y.Imaginary));
        for (int k = 0; k < surplus; k++) result.Add(new Complex(larger[k].Real, 0));
        larger.RemoveRange(0, surplus);
        result.AddRange(larger);   // the lower ones were stored conjugated
        return result;
    }
}
