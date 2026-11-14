using System.Globalization;
using System.Text;
using OpenSim.Core.Numerics;
using OpenSim.Core.Numerics.Optimization;

namespace OpenSim.Optics.Materials;

/// <summary>One measured index: wavelength (µm) and refractive index.</summary>
public sealed record IndexSample(double Micrometers, double Index);

/// <summary>The outcome of <see cref="DispersionFit.Fit"/>.</summary>
public sealed record DispersionFitResult
{
    public required DispersionFormula Formula { get; init; }

    /// <summary>C1, C2, … as the formula reads them. For the Sellmeier sums the terms are put in
    /// increasing order of their resonance constant.</summary>
    public required IReadOnlyList<double> Coefficients { get; init; }

    public required IReadOnlyList<IndexSample> Samples { get; init; }

    /// <summary>Fitted minus measured index, one per sample.</summary>
    public required IReadOnlyList<double> Residuals { get; init; }

    public double RmsResidual => Math.Sqrt(Residuals.Sum(r => r * r) / Residuals.Count);
    public double MaxResidual => Residuals.Max(Math.Abs);

    /// <summary>
    /// How strongly each coefficient follows the data: the length of row j of the pseudo-inverse
    /// of the Jacobian ∂n/∂C at the solution, so a change δn in the data moves Cj by at most
    /// Sensitivity[j]·‖δn‖₂ to first order. Multiplied by the RMS residual it is the usual
    /// standard-error estimate. Infinite when the data do not determine the coefficient.
    /// </summary>
    public required IReadOnlyList<double> Sensitivity { get; init; }

    /// <summary>The condition number of the column-scaled Jacobian; large values mean
    /// coefficients that trade off against each other.</summary>
    public required double ConditionNumber { get; init; }

    /// <summary>How the nonlinear refinement stopped; null for a purely linear fit.</summary>
    public LeastSquaresStatus? Status { get; init; }

    public double MinMicrometers => Samples.Min(s => s.Micrometers);
    public double MaxMicrometers => Samples.Max(s => s.Micrometers);

    /// <summary>The fit as a material valid over the measured range.</summary>
    public OpticalMaterial ToMaterial(string name) => new()
    {
        Name = name,
        Comment = $"Fitted ({DispersionFormulas.Name(Formula)}) to {Samples.Count} measured indices.",
        FormulaNumber = (int)Formula,
        Coefficients = Coefficients.ToArray(),
        MinMicrometers = MinMicrometers,
        MaxMicrometers = MaxMicrometers,
    };

    /// <summary>
    /// The NM, CD and LD lines of an AGF entry for the fit, in the field order of the ZemaxGlass
    /// user manual (zemaxglass_manual.txt, the AGF layout): NM name, formula, glass code, nd, Vd,
    /// exclude-substitution 0, status 0 (standard), melt frequency left out. nd and Vd are
    /// computed from the fit when its range contains F, d and C; otherwise both print as 0 and
    /// the glass code as 0. No catalog writer exists yet; this text is for pasting.
    /// </summary>
    public string ToAgf(string name)
    {
        var ci = CultureInfo.InvariantCulture;
        var m = ToMaterial(name);
        string nd = "0", vd = "0", code = "0";
        if (GlassProperties.TryCompute(m, out var p, out _))
        {
            nd = p!.Nd.ToString("F6", ci);
            vd = p.Vd.ToString("F4", ci);
            code = (Math.Round((p.Nd - 1) * 1000) * 1000 + Math.Round(p.Vd * 10)).ToString("F0", ci);
        }
        var sb = new StringBuilder();
        sb.Append("NM ").Append(name.Replace(' ', '_')).Append(' ').Append((int)Formula).Append(' ').Append(code)
          .Append(' ').Append(nd).Append(' ').Append(vd).Append(" 0 0").Append("\r\n");
        sb.Append("CD");
        foreach (double c in Coefficients) sb.Append(' ').Append(c.ToString("0.0000000000E+00", ci));
        sb.Append("\r\n");
        sb.Append("LD ").Append(MinMicrometers.ToString("0.00000000E+00", ci)).Append(' ').Append(MaxMicrometers.ToString("0.00000000E+00", ci)).Append("\r\n");
        return sb.ToString();
    }
}

/// <summary>
/// Fitting a dispersion formula to measured indices (OpticStudio's Glass Fitting; plan item
/// OP-14). The fit minimises Σ (n_formula(λᵢ) − nᵢ)² over the formula's coefficients:
/// <list type="bullet">
/// <item>Herzberger and Conrady are linear in n: one Householder least-squares solve
/// (<see cref="HouseholderLeastSquares"/>), which is the exact minimiser.</item>
/// <item>Schott and Extended 1–3 are linear in n²: a least-squares solve on n², each row
/// weighted by 1/(2nᵢ) so its residual approximates the index residual, then damped least
/// squares (<see cref="DampedLeastSquares"/>) on the index residual itself, with the analytic
/// Jacobian.</item>
/// <item>Sellmeier 1, 3, 4, 5 and Handbook of Optics 1 and 2 have resonance constants that enter
/// nonlinearly. For each choice of resonances from a grid (sixteen values: eight geometrically
/// spaced from 0.01·λmin² to 0.7·λmin², eight from 1.5·λmax² to 1000·λmax², in µm², so no pole
/// falls among the data) the remaining coefficients are linear, and the weighted solve above
/// gives them; the best grid point starts damped least squares over every coefficient.</item>
/// </list>
/// Sellmeier 2 (formula 4) is refused: its form is unverified (<see cref="DispersionFormulas.IsVerified"/>).
/// The fit fails loudly when the samples cannot determine the formula (fewer samples than
/// coefficients, repeated or non-positive wavelengths, non-finite indices).
/// </summary>
public static class DispersionFit
{
    /// <summary>The formulas this fit offers.</summary>
    public static IReadOnlyList<DispersionFormula> Fittable { get; } = Enum.GetValues<DispersionFormula>()
        .Where(f => f != DispersionFormula.Sellmeier2).ToArray();

    /// <summary>Fits <paramref name="formula"/> to <paramref name="samples"/>.</summary>
    public static DispersionFitResult Fit(IReadOnlyList<IndexSample> samples, DispersionFormula formula,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!Fittable.Contains(formula))
            throw new NotSupportedException($"Dispersion formula {DispersionFormulas.NameOf((int)formula)} is not fitted: its form is not verified against OpticStudio.");
        int count = DispersionFormulas.CoefficientCount(formula);
        Validate(samples, count, formula);
        var data = samples.OrderBy(s => s.Micrometers).ToArray();

        double[] start = formula switch
        {
            DispersionFormula.Herzberger or DispersionFormula.Conrady => LinearInIndex(data, formula),
            DispersionFormula.Schott or DispersionFormula.Extended1 or DispersionFormula.Extended2 or DispersionFormula.Extended3 =>
                LinearInSquare(data, formula, Array.Empty<double>()).Coefficients,
            _ => GridStart(data, formula, cancellation),
        };

        double[] c = start;
        LeastSquaresStatus? status = null;
        if (formula is not (DispersionFormula.Herzberger or DispersionFormula.Conrady))
        {
            var result = DampedLeastSquares.Solve(
                (x, r) => { for (int i = 0; i < data.Length; i++) r[i] = DispersionFormulas.Evaluate(formula, x, data[i].Micrometers) - data[i].Index; },
                data.Length, start,
                new LeastSquaresOptions
                {
                    Jacobian = (x, jac) =>
                    {
                        var gradient = new double[x.Length];
                        for (int i = 0; i < data.Length; i++)
                        {
                            Gradient(formula, x, data[i].Micrometers, gradient);
                            for (int j = 0; j < x.Length; j++) jac[i, j] = gradient[j];
                        }
                    },
                    CancellationToken = cancellation,
                });
            c = result.X;
            status = result.Status;
        }
        c = Canonical(formula, c);

        var residuals = data.Select(s => DispersionFormulas.Evaluate(formula, c, s.Micrometers) - s.Index).ToArray();
        if (residuals.Any(r => !double.IsFinite(r)))
            throw new InvalidOperationException($"The {DispersionFormulas.Name(formula)} fit gives no real index at some measured wavelength; try another formula.");
        var (sensitivity, condition) = Conditioning(formula, c, data);
        return new DispersionFitResult
        {
            Formula = formula, Coefficients = c, Samples = data, Residuals = residuals,
            Sensitivity = sensitivity, ConditionNumber = condition, Status = status,
        };
    }

    /// <summary>
    /// Reads samples from text: one "wavelength index" pair per line (µm, then n), separated by
    /// spaces, tabs, commas or semicolons; blank lines and lines starting with '#' or '!' are
    /// skipped. Throws, naming the line, on anything else.
    /// </summary>
    public static IReadOnlyList<IndexSample> ReadSamples(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var samples = new List<IndexSample>();
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] is '#' or '!') continue;
            var parts = line.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                throw new FormatException($"Line {i + 1} ('{line}') is not a wavelength (µm) and an index.");
            samples.Add(new IndexSample(w, n));
        }
        return samples;
    }

    /// <summary>
    /// ∂n/∂Cⱼ of <paramref name="formula"/> at λ, written into <paramref name="gradient"/>
    /// (length ≥ the formula's coefficient count); returns n. The expressions are the
    /// derivatives of those in <see cref="DispersionFormulas.Evaluate"/>; for the formulas in n²,
    /// ∂n/∂C = (∂n²/∂C)/(2n).
    /// </summary>
    public static double Gradient(DispersionFormula formula, IReadOnlyList<double> c, double micrometers, double[] gradient)
    {
        double n = DispersionFormulas.Evaluate(formula, c, micrometers);
        double w = micrometers, w2 = w * w, half = 1 / (2 * n);
        Array.Clear(gradient);
        switch (formula)
        {
            case DispersionFormula.Herzberger:
            {
                var phi = ModelGlassFit.Basis(w);
                for (int j = 0; j < 6; j++) gradient[j] = phi[j];
                return n;
            }
            case DispersionFormula.Conrady:
                gradient[0] = 1;
                gradient[1] = 1 / w;
                gradient[2] = 1 / Math.Pow(w, 3.5);
                return n;
            case DispersionFormula.Sellmeier1:
            case DispersionFormula.Sellmeier3:
            case DispersionFormula.Sellmeier5:
            {
                int terms = DispersionFormulas.CoefficientCount(formula) / 2;
                for (int k = 0; k < terms; k++)
                {
                    double q = w2 - c[2 * k + 1];
                    gradient[2 * k] = w2 / q * half;
                    gradient[2 * k + 1] = c[2 * k] * w2 / (q * q) * half;
                }
                return n;
            }
            case DispersionFormula.Sellmeier4:
            {
                double q1 = w2 - c[2], q2 = w2 - c[4];
                gradient[0] = half;
                gradient[1] = w2 / q1 * half;
                gradient[2] = c[1] * w2 / (q1 * q1) * half;
                gradient[3] = w2 / q2 * half;
                gradient[4] = c[3] * w2 / (q2 * q2) * half;
                return n;
            }
            case DispersionFormula.HandbookOfOptics1:
            {
                double q = w2 - c[2];
                gradient[0] = half;
                gradient[1] = 1 / q * half;
                gradient[2] = c[1] / (q * q) * half;
                gradient[3] = -w2 * half;
                return n;
            }
            case DispersionFormula.HandbookOfOptics2:
            {
                double q = w2 - c[2];
                gradient[0] = half;
                gradient[1] = w2 / q * half;
                gradient[2] = c[1] * w2 / (q * q) * half;
                gradient[3] = -w2 * half;
                return n;
            }
            default:
            {
                var phi = SquareBasis(formula, w2);
                for (int j = 0; j < phi.Length; j++) gradient[j] = phi[j] * half;
                return n;
            }
        }
    }

    // ---------------- Starting points ----------------

    private static double[] LinearInIndex(IndexSample[] data, DispersionFormula formula)
    {
        int count = DispersionFormulas.CoefficientCount(formula);
        var a = new double[data.Length, count];
        var b = new double[data.Length];
        var g = new double[count];
        for (int i = 0; i < data.Length; i++)
        {
            // The gradient of a formula linear in n is its basis, independent of the coefficients.
            Gradient(formula, new double[count].Select((_, j) => j == 0 ? 1.0 : 0.0).ToArray(), data[i].Micrometers, g);
            for (int j = 0; j < count; j++) a[i, j] = g[j];
            b[i] = data[i].Index;
        }
        return HouseholderLeastSquares.Solve(a, b);
    }

    /// <summary>The n²-linear basis of the Schott and Extended formulas.</summary>
    private static double[] SquareBasis(DispersionFormula formula, double w2)
    {
        double i1 = 1 / w2, i2 = i1 * i1, i3 = i2 * i1, i4 = i3 * i1, i5 = i4 * i1, i6 = i5 * i1;
        return formula switch
        {
            DispersionFormula.Schott => new[] { 1, w2, i1, i2, i3, i4 },
            DispersionFormula.Extended1 => new[] { 1, w2, i1, i2, i3, i4, i5, i6 },
            DispersionFormula.Extended2 => new[] { 1, w2, i1, i2, i3, i4, w2 * w2, w2 * w2 * w2 },
            DispersionFormula.Extended3 => new[] { 1, w2, w2 * w2, i1, i2, i3, i4, i5, i6 },
            _ => throw new ArgumentOutOfRangeException(nameof(formula), formula, "Not a formula linear in n².")
        };
    }

    /// <summary>
    /// For fixed resonance constants every formula here is linear in its other coefficients in
    /// n² (or n² − 1): the weighted solve for them, and its weighted residual. Resonances are
    /// given in the order the formula reads them; an empty list means a formula with none.
    /// </summary>
    private static (double[] Coefficients, double Residual) LinearInSquare(IndexSample[] data, DispersionFormula formula, double[] resonances)
    {
        int count = DispersionFormulas.CoefficientCount(formula);
        int[] resonanceIndex = ResonanceIndices(formula);
        var linear = Enumerable.Range(0, count).Where(j => !resonanceIndex.Contains(j)).ToArray();
        bool minusOne = formula is DispersionFormula.Sellmeier1 or DispersionFormula.Sellmeier3 or DispersionFormula.Sellmeier5;
        var a = new double[data.Length, linear.Length];
        var b = new double[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            double w2 = data[i].Micrometers * data[i].Micrometers, weight = 1 / (2 * data[i].Index);
            var phi = LinearBasis(formula, w2, resonances);
            for (int j = 0; j < linear.Length; j++) a[i, j] = phi[j] * weight;
            b[i] = (data[i].Index * data[i].Index - (minusOne ? 1 : 0)) * weight;
        }
        var x = HouseholderLeastSquares.Solve(a, b);
        double residual = 0;
        for (int i = 0; i < data.Length; i++)
        {
            double s = -b[i];
            for (int j = 0; j < linear.Length; j++) s += a[i, j] * x[j];
            residual += s * s;
        }
        var c = new double[count];
        for (int j = 0; j < linear.Length; j++) c[linear[j]] = x[j];
        for (int k = 0; k < resonanceIndex.Length; k++) c[resonanceIndex[k]] = resonances[k];
        return (c, residual);
    }

    private static int[] ResonanceIndices(DispersionFormula formula) => formula switch
    {
        DispersionFormula.Sellmeier1 => new[] { 1, 3, 5 },
        DispersionFormula.Sellmeier3 => new[] { 1, 3, 5, 7 },
        DispersionFormula.Sellmeier5 => new[] { 1, 3, 5, 7, 9 },
        DispersionFormula.Sellmeier4 => new[] { 2, 4 },
        DispersionFormula.HandbookOfOptics1 or DispersionFormula.HandbookOfOptics2 => new[] { 2 },
        _ => Array.Empty<int>()
    };

    /// <summary>The basis of the linear coefficients, in coefficient order, for given resonances.</summary>
    private static double[] LinearBasis(DispersionFormula formula, double w2, double[] r) => formula switch
    {
        DispersionFormula.Sellmeier1 or DispersionFormula.Sellmeier3 or DispersionFormula.Sellmeier5 =>
            r.Select(q => w2 / (w2 - q)).ToArray(),
        DispersionFormula.Sellmeier4 => new[] { 1, w2 / (w2 - r[0]), w2 / (w2 - r[1]) },
        DispersionFormula.HandbookOfOptics1 => new[] { 1, 1 / (w2 - r[0]), -w2 },
        DispersionFormula.HandbookOfOptics2 => new[] { 1, w2 / (w2 - r[0]), -w2 },
        _ => SquareBasis(formula, w2)
    };

    /// <summary>The best grid of resonances (see the class remarks) with its linear coefficients.</summary>
    private static double[] GridStart(IndexSample[] data, DispersionFormula formula, CancellationToken cancellation)
    {
        double lo = data[0].Micrometers, hi = data[^1].Micrometers;
        var candidates = new List<double>();
        for (int k = 0; k < 8; k++) candidates.Add(lo * lo * 0.01 * Math.Pow(70, k / 7.0));
        for (int k = 0; k < 8; k++) candidates.Add(hi * hi * 1.5 * Math.Pow(1000 / 1.5, k / 7.0));
        int terms = ResonanceIndices(formula).Length;
        double[]? best = null;
        double bestResidual = double.PositiveInfinity;
        foreach (var choice in Combinations(candidates.Count, terms))
        {
            cancellation.ThrowIfCancellationRequested();
            var resonances = choice.Select(i => candidates[i]).ToArray();
            var (c, residual) = LinearInSquare(data, formula, resonances);
            if (residual < bestResidual && c.All(double.IsFinite))
            {
                bestResidual = residual;
                best = c;
            }
        }
        return best ?? throw new InvalidOperationException($"No starting point was found for the {DispersionFormulas.Name(formula)} fit.");
    }

    /// <summary>Every increasing choice of <paramref name="k"/> of 0…n−1, in lexicographic order.</summary>
    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var index = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])index.Clone();
            int i = k - 1;
            while (i >= 0 && index[i] == n - k + i) i--;
            if (i < 0) yield break;
            index[i]++;
            for (int j = i + 1; j < k; j++) index[j] = index[j - 1] + 1;
        }
    }

    /// <summary>Sellmeier sums with their terms in increasing order of resonance.</summary>
    private static double[] Canonical(DispersionFormula formula, double[] c)
    {
        if (formula is not (DispersionFormula.Sellmeier1 or DispersionFormula.Sellmeier3 or DispersionFormula.Sellmeier5)) return c;
        int terms = c.Length / 2;
        var order = Enumerable.Range(0, terms).OrderBy(k => c[2 * k + 1]).ToArray();
        var sorted = new double[c.Length];
        for (int k = 0; k < terms; k++)
        {
            sorted[2 * k] = c[2 * order[k]];
            sorted[2 * k + 1] = c[2 * order[k] + 1];
        }
        return sorted;
    }

    private static (double[] Sensitivity, double Condition) Conditioning(DispersionFormula formula, double[] c, IndexSample[] data)
    {
        int n = c.Length;
        var jac = new double[data.Length, n];
        var scaled = new double[data.Length, n];
        var g = new double[n];
        var norms = new double[n];
        for (int i = 0; i < data.Length; i++)
        {
            Gradient(formula, c, data[i].Micrometers, g);
            for (int j = 0; j < n; j++) { jac[i, j] = g[j]; norms[j] += g[j] * g[j]; }
        }
        for (int j = 0; j < n; j++) norms[j] = Math.Sqrt(norms[j]);
        for (int i = 0; i < data.Length; i++)
            for (int j = 0; j < n; j++) scaled[i, j] = norms[j] > 0 ? jac[i, j] / norms[j] : 0;

        var svd = RealSvd.Decompose(jac);
        var sigma = svd.SingularValues;
        double floor = sigma[0] * 1e-15 * Math.Max(data.Length, n);
        var sensitivity = new double[n];
        for (int j = 0; j < n; j++)
        {
            double s = 0;
            for (int k = 0; k < sigma.Length; k++)
            {
                if (sigma[k] <= floor) { if (Math.Abs(svd.V[j, k]) > 1e-12) s = double.PositiveInfinity; continue; }
                s += svd.V[j, k] * svd.V[j, k] / (sigma[k] * sigma[k]);
            }
            sensitivity[j] = Math.Sqrt(s);
        }
        var scaledSigma = RealSvd.Decompose(scaled).SingularValues;
        double condition = scaledSigma[^1] > 0 ? scaledSigma[0] / scaledSigma[^1] : double.PositiveInfinity;
        return (sensitivity, condition);
    }

    private static void Validate(IReadOnlyList<IndexSample> samples, int count, DispersionFormula formula)
    {
        if (samples.Count < count)
            throw new ArgumentException($"The {DispersionFormulas.Name(formula)} formula has {count} coefficients; {samples.Count} measured indices cannot determine them.");
        foreach (var s in samples)
        {
            if (!double.IsFinite(s.Micrometers) || s.Micrometers <= 0)
                throw new ArgumentException($"A measured wavelength of {s.Micrometers} µm is not a positive number.");
            if (!double.IsFinite(s.Index) || s.Index <= 0)
                throw new ArgumentException($"The measured index {s.Index} at {s.Micrometers} µm is not a positive number.");
        }
        if (samples.Select(s => s.Micrometers).Distinct().Count() != samples.Count)
            throw new ArgumentException("Two measured indices share a wavelength; average them or remove one.");
    }
}
