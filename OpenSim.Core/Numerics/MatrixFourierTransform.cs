using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// The matrix Fourier transform: the discrete Fourier sum evaluated at any chosen set of output
/// frequencies, as two matrix products E_rows · f · E_columnsᵀ. Unlike the FFT, the output
/// sampling is free: a small patch of the transform can be sampled as finely as wanted (a zoom
/// on a point spread function, say) without padding the input to a huge array. The cost is
/// outputRows × inputColumns × (inputRows + outputColumns) multiplications.
/// <para>
/// Reference: R. Soummer, L. Pueyo, A. Sivaramakrishnan and R. J. Vanderbei, "Fast computation of
/// Lyot-style coronagraph propagation", Optics Express 15(24), 15935 (2007).
/// </para>
/// <para>
/// Each output row is computed on its own, so the result is bitwise identical at any thread
/// count.
/// </para>
/// </summary>
public static class MatrixFourierTransform
{
    /// <summary>
    /// F[p, q] = Σ f[j, k]·e^{∓2πi(x_j·u_p + y_k·v_q)}, with the minus sign for the forward
    /// transform and plus for the inverse. No scale factor is applied: to approximate the
    /// continuous transform the caller multiplies by the sample area.
    /// </summary>
    /// <param name="input">Samples f[j, k].</param>
    /// <param name="rowPositions">x_j, one per input row.</param>
    /// <param name="columnPositions">y_k, one per input column.</param>
    /// <param name="rowFrequencies">u_p, one per output row, in cycles per unit of x.</param>
    /// <param name="columnFrequencies">v_q, one per output column, in cycles per unit of y.</param>
    /// <param name="inverse">True for the plus sign in the exponent.</param>
    /// <param name="maxDegreeOfParallelism">The thread limit; null for no limit.</param>
    public static Complex[,] Transform(Complex[,] input, IReadOnlyList<double> rowPositions,
        IReadOnlyList<double> columnPositions, IReadOnlyList<double> rowFrequencies,
        IReadOnlyList<double> columnFrequencies, bool inverse = false, int? maxDegreeOfParallelism = 1)
    {
        int rows = input.GetLength(0), columns = input.GetLength(1);
        if (rowPositions.Count != rows) throw new ArgumentException("One position per input row.", nameof(rowPositions));
        if (columnPositions.Count != columns) throw new ArgumentException("One position per input column.", nameof(columnPositions));
        int outRows = rowFrequencies.Count, outColumns = columnFrequencies.Count;
        double sign = inverse ? 1 : -1;

        var columnKernel = new Complex[outColumns][];
        for (int q = 0; q < outColumns; q++)
        {
            var line = new Complex[columns];
            for (int k = 0; k < columns; k++) line[k] = Phase(sign, columnPositions[k], columnFrequencies[q]);
            columnKernel[q] = line;
        }

        var byRow = ParallelSlots.Compute(outRows, maxDegreeOfParallelism, p =>
        {
            var partial = new Complex[columns];
            for (int j = 0; j < rows; j++)
            {
                Complex phase = Phase(sign, rowPositions[j], rowFrequencies[p]);
                for (int k = 0; k < columns; k++) partial[k] += phase * input[j, k];
            }
            var line = new Complex[outColumns];
            for (int q = 0; q < outColumns; q++)
            {
                var kernel = columnKernel[q];
                Complex sum = Complex.Zero;
                for (int k = 0; k < columns; k++) sum += partial[k] * kernel[k];
                line[q] = sum;
            }
            return line;
        });

        var result = new Complex[outRows, outColumns];
        for (int p = 0; p < outRows; p++)
            for (int q = 0; q < outColumns; q++) result[p, q] = byRow[p][q];
        return result;
    }

    /// <summary>
    /// A zoomed transform laid out like a shifted FFT. Input sample j sits at position j − n/2
    /// (the centre sample is the origin). The output covers <paramref name="rowSpan"/> by
    /// <paramref name="columnSpan"/> bins of the input's own DFT, centred on the given bin, with
    /// output sample p at bin centre + (p − outputRows/2)·span/outputRows. A DFT bin is 1/n
    /// cycles per sample.
    /// <para>
    /// With the output size and the span both equal to the input size and the centre at 0, the
    /// result equals <c>Fft2D.Shift(Fft2D.Forward(Fft2D.InverseShift(input)))</c>.
    /// </para>
    /// </summary>
    public static Complex[,] Zoom(Complex[,] input, int outputRows, int outputColumns,
        double rowSpan, double columnSpan, double rowCentre = 0, double columnCentre = 0,
        bool inverse = false, int? maxDegreeOfParallelism = 1)
    {
        if (outputRows < 1 || outputColumns < 1) throw new ArgumentException("The output needs at least one sample each way.");
        int rows = input.GetLength(0), columns = input.GetLength(1);
        return Transform(input, Centered(rows, 1.0), Centered(columns, 1.0),
            Bins(outputRows, rowSpan, rowCentre, rows), Bins(outputColumns, columnSpan, columnCentre, columns),
            inverse, maxDegreeOfParallelism);
    }

    private static double[] Centered(int count, double step)
    {
        var positions = new double[count];
        for (int i = 0; i < count; i++) positions[i] = (i - count / 2) * step;
        return positions;
    }

    private static double[] Bins(int count, double span, double centre, int inputCount)
    {
        var frequencies = new double[count];
        for (int p = 0; p < count; p++)
            frequencies[p] = (centre + (p - count / 2) * span / count) / inputCount;
        return frequencies;
    }

    /// <summary>e^{sign·2πi·x·u}, with the whole turns taken out before the trigonometry so a
    /// large product keeps its phase digits.</summary>
    private static Complex Phase(double sign, double position, double frequency)
    {
        double turns = Math.IEEERemainder(position * frequency, 1.0);
        double angle = sign * 2 * Math.PI * turns;
        return new Complex(Math.Cos(angle), Math.Sin(angle));
    }
}
