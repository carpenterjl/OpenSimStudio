using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// The two-dimensional complex DFT of a rows × columns array, as 1-D transforms of every row and
/// then every column by <see cref="Fft"/>. Any size is transformed exactly (Bluestein for lengths
/// that are not a power of two), so no padding is forced on the caller. Convention:
/// X[p, q] = Σ x[j, k]·e^{−2πi(jp/rows + kq/columns)}; the inverse divides by rows × columns.
/// <para>
/// Index 0 is the origin in both domains. <see cref="Shift"/> moves the origin to the centre
/// sample, index n/2 (integer division), and <see cref="PadCentered"/> pads about that centre,
/// which is how pupils and point spread functions are laid out.
/// </para>
/// <para>
/// Rows and columns are independent, so the result is bitwise identical at any thread count.
/// </para>
/// </summary>
public static class Fft2D
{
    /// <summary>The forward 2-D DFT.</summary>
    /// <param name="maxDegreeOfParallelism">The thread limit; null for no limit.</param>
    public static Complex[,] Forward(Complex[,] samples, int? maxDegreeOfParallelism = 1)
        => Transform(samples, inverse: false, maxDegreeOfParallelism);

    /// <summary>The inverse 2-D DFT (includes the 1/(rows × columns) factor).</summary>
    public static Complex[,] Inverse(Complex[,] spectrum, int? maxDegreeOfParallelism = 1)
        => Transform(spectrum, inverse: true, maxDegreeOfParallelism);

    private static Complex[,] Transform(Complex[,] data, bool inverse, int? threads)
    {
        int rows = data.GetLength(0), columns = data.GetLength(1);
        if (rows == 0 || columns == 0) throw new ArgumentException("FFT input must be non-empty.");
        var byRow = ParallelSlots.Compute(rows, threads, i =>
        {
            var line = new Complex[columns];
            for (int k = 0; k < columns; k++) line[k] = data[i, k];
            return inverse ? Fft.Inverse(line) : Fft.Forward(line);
        });
        var byColumn = ParallelSlots.Compute(columns, threads, k =>
        {
            var line = new Complex[rows];
            for (int i = 0; i < rows; i++) line[i] = byRow[i][k];
            return inverse ? Fft.Inverse(line) : Fft.Forward(line);
        });
        var result = new Complex[rows, columns];
        for (int i = 0; i < rows; i++)
            for (int k = 0; k < columns; k++) result[i, k] = byColumn[k][i];
        return result;
    }

    /// <summary>Moves the origin from index 0 to the centre sample, index n/2, in both
    /// directions (the usual "fftshift").</summary>
    public static T[,] Shift<T>(T[,] data) => Roll(data, forward: true);

    /// <summary>Undoes <see cref="Shift"/> exactly, for odd sizes too: moves the centre sample
    /// back to index 0.</summary>
    public static T[,] InverseShift<T>(T[,] data) => Roll(data, forward: false);

    private static T[,] Roll<T>(T[,] data, bool forward)
    {
        int rows = data.GetLength(0), columns = data.GetLength(1);
        int rowStep = forward ? rows / 2 : rows - rows / 2;
        int columnStep = forward ? columns / 2 : columns - columns / 2;
        var result = new T[rows, columns];
        for (int i = 0; i < rows; i++)
        {
            int toRow = (i + rowStep) % rows;
            for (int k = 0; k < columns; k++) result[toRow, (k + columnStep) % columns] = data[i, k];
        }
        return result;
    }

    /// <summary>Embeds the array in a larger array of zeros so that its centre sample (index n/2)
    /// lands on the centre sample of the result. Padding a pupil this way samples its transform
    /// more finely without moving it.</summary>
    public static Complex[,] PadCentered(Complex[,] data, int rows, int columns)
    {
        int r = data.GetLength(0), c = data.GetLength(1);
        if (rows < r || columns < c)
            throw new ArgumentException("The padded size must be at least the size of the data.");
        int rowOffset = rows / 2 - r / 2, columnOffset = columns / 2 - c / 2;
        var result = new Complex[rows, columns];
        for (int i = 0; i < r; i++)
            for (int k = 0; k < c; k++) result[i + rowOffset, k + columnOffset] = data[i, k];
        return result;
    }

    /// <summary>Cuts out the rows × columns block whose centre sample is the centre sample of the
    /// array: the reverse of <see cref="PadCentered"/>.</summary>
    public static Complex[,] CropCentered(Complex[,] data, int rows, int columns)
    {
        int r = data.GetLength(0), c = data.GetLength(1);
        if (rows < 1 || columns < 1 || rows > r || columns > c)
            throw new ArgumentException("The cropped size must be between 1 and the size of the data.");
        int rowOffset = r / 2 - rows / 2, columnOffset = c / 2 - columns / 2;
        var result = new Complex[rows, columns];
        for (int i = 0; i < rows; i++)
            for (int k = 0; k < columns; k++) result[i, k] = data[i + rowOffset, k + columnOffset];
        return result;
    }
}
