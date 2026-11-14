using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Wavefront;

/// <summary>
/// Least-squares fitting of Zernike coefficients to a sampled wavefront: minimise
/// Σ wᵢ (Σ_j c_j Z_j(xᵢ, yᵢ) − Wᵢ)² over the unmasked points.
/// <para>
/// Never by projection (c_j = ⟨W, Z_j⟩): the terms are orthogonal only as integrals over the
/// whole disk or annulus, so on a grid of samples, and still less on a vignetted or masked
/// pupil, projection mixes the terms. Least squares returns the coefficients exactly whenever
/// the wavefront lies in the span of the terms, whatever the sampling.
/// </para>
/// <para>
/// Method: the rows are scaled by √wᵢ and the columns to unit length, the matrix is reduced to
/// its triangular factor R by Householder QR (the same reflections as
/// <see cref="HouseholderLeastSquares"/>, kept here because the fit needs R itself), and R —
/// only terms × terms — is decomposed by <see cref="RealSvd"/>. The singular values of R are
/// those of the scaled design matrix, so the rank and condition number cost no pass over the
/// points, and a rank-deficient fit returns the minimum-norm solution instead of failing.
/// </para>
/// </summary>
public static class ZernikeFit
{
    private const double Epsilon = 2.220446049250313e-16;

    /// <summary>
    /// Fits the terms of <paramref name="basis"/> to values at pupil points.
    /// </summary>
    /// <param name="basis">The terms to fit.</param>
    /// <param name="x">Normalised pupil x of each point.</param>
    /// <param name="y">Normalised pupil y of each point.</param>
    /// <param name="values">The wavefront at each point.</param>
    /// <param name="weights">Optional weight per point, finite and 0 or more (0 leaves the point
    /// out). Default 1.</param>
    /// <param name="mask">Optional: false leaves the point out (vignetted, obscured or invalid).
    /// A masked point's value is not read, so it may be NaN.</param>
    /// <param name="rankTolerance">Singular values at or below this times the largest count as
    /// zero. Default max(points, terms) × machine epsilon.</param>
    public static ZernikeFitResult Fit(
        ZernikeBasis basis,
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        IReadOnlyList<double> values,
        IReadOnlyList<double>? weights = null,
        IReadOnlyList<bool>? mask = null,
        double rankTolerance = -1)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(values);
        int count = x.Count;
        if (y.Count != count || values.Count != count)
            throw new ArgumentException("x, y and values need one entry per point.");
        if (weights is not null && weights.Count != count)
            throw new ArgumentException("One weight per point.", nameof(weights));
        if (mask is not null && mask.Count != count)
            throw new ArgumentException("One mask entry per point.", nameof(mask));

        // The points that take part, validated: a bad point fails loudly with its index.
        var used = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            if (mask is not null && !mask[i]) continue;
            double w = weights is null ? 1.0 : weights[i];
            if (!double.IsFinite(w) || w < 0)
                throw new ArgumentException($"Point {i}: the weight {w} must be finite and 0 or more.", nameof(weights));
            if (w == 0) continue;
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]) || !double.IsFinite(values[i]))
                throw new ArgumentException($"Point {i}: the coordinates and value must be finite (mask the point out instead).");
            if (!basis.Contains(x[i], y[i]))
                throw new ArgumentException(
                    $"Point {i} at (x, y) = ({x[i]:g6}, {y[i]:g6}) is outside the pupil ε = {basis.Obscuration:g6} ≤ ρ ≤ 1 the terms are defined on.");
            used.Add(i);
        }

        int terms = basis.TermCount, rows = used.Count;
        if (rows < terms)
            throw new ArgumentException($"{rows} usable points cannot determine {terms} Zernike terms.");

        // The weighted design matrix, column-major for the reflections.
        var columns = new double[terms][];
        for (int j = 0; j < terms; j++) columns[j] = new double[rows];
        var rhs = new double[rows];
        var rowValues = new double[terms];
        double weightSum = 0;
        for (int r = 0; r < rows; r++)
        {
            int i = used[r];
            double w = weights is null ? 1.0 : weights[i];
            weightSum += w;
            double root = Math.Sqrt(w);
            basis.EvaluateAll(x[i], y[i], rowValues);
            for (int j = 0; j < terms; j++) columns[j][r] = root * rowValues[j];
            rhs[r] = root * values[i];
        }

        var columnScale = new double[terms];
        for (int j = 0; j < terms; j++)
        {
            double norm = 0;
            foreach (double v in columns[j]) norm += v * v;
            norm = Math.Sqrt(norm);
            columnScale[j] = norm > 0 ? 1 / norm : 0;
            for (int r = 0; r < rows; r++) columns[j][r] *= columnScale[j];
        }

        var triangle = HouseholderTriangle(columns, rhs, rows, terms);
        var svd = RealSvd.Decompose(triangle);
        double tolerance = rankTolerance >= 0 ? rankTolerance : Math.Max(rows, terms) * Epsilon;
        int rank = svd.Rank(tolerance);
        var top = new double[terms];
        Array.Copy(rhs, top, terms);
        var scaled = svd.Solve(top, tolerance);
        var coefficients = new double[terms];
        for (int j = 0; j < terms; j++) coefficients[j] = scaled[j] * columnScale[j];

        // Residuals from the original values, not from the reduced system.
        double weightedSquares = 0, maxResidual = 0;
        for (int r = 0; r < rows; r++)
        {
            int i = used[r];
            double w = weights is null ? 1.0 : weights[i];
            basis.EvaluateAll(x[i], y[i], rowValues);
            double fit = 0;
            for (int j = 0; j < terms; j++) fit += coefficients[j] * rowValues[j];
            double residual = fit - values[i];
            weightedSquares += w * residual * residual;
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
        }

        double condition = rank < terms ? double.PositiveInfinity : svd.ConditionNumber;
        return new ZernikeFitResult(
            coefficients,
            Math.Sqrt(weightedSquares / weightSum),
            maxResidual,
            rank,
            condition,
            (double[])svd.SingularValues.Clone(),
            rows);
    }

    /// <summary>
    /// Reduces the columns to upper-triangular R by Householder reflections, applying them to
    /// <paramref name="rhs"/> as well; returns R (terms × terms). A column that is already zero
    /// below the diagonal is left as it is, so a rank-deficient matrix gives a zero on R's
    /// diagonal for the SVD to find.
    /// </summary>
    private static double[,] HouseholderTriangle(double[][] q, double[] rhs, int rows, int columns)
    {
        for (int k = 0; k < columns; k++)
        {
            var v = q[k];
            double norm = 0;
            for (int i = k; i < rows; i++) norm += v[i] * v[i];
            norm = Math.Sqrt(norm);
            if (norm == 0) continue;
            double alpha = v[k] > 0 ? -norm : norm;
            v[k] -= alpha;
            double vv = 0;
            for (int i = k; i < rows; i++) vv += v[i] * v[i];
            for (int j = k + 1; j < columns; j++) Reflect(v, q[j], k, rows, vv);
            Reflect(v, rhs, k, rows, vv);
            // The reflected column k is (…, α, 0, …, 0).
            for (int i = k + 1; i < rows; i++) v[i] = 0;
            v[k] = alpha;
        }
        var r = new double[columns, columns];
        for (int j = 0; j < columns; j++)
            for (int i = 0; i <= j; i++) r[i, j] = q[j][i];
        return r;
    }

    private static void Reflect(double[] v, double[] target, int from, int rows, double vv)
    {
        double dot = 0;
        for (int i = from; i < rows; i++) dot += v[i] * target[i];
        double factor = 2 * dot / vv;
        for (int i = from; i < rows; i++) target[i] -= factor * v[i];
    }
}
