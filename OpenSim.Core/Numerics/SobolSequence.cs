using System.Globalization;
using System.Reflection;

namespace OpenSim.Core.Numerics;

/// <summary>
/// Sobol low-discrepancy points on the unit cube, in Gray-code order, from the direction numbers
/// of S. Joe and F. Y. Kuo, "Constructing Sobol sequences with better two-dimensional
/// projections", SIAM J. Sci. Comput. 30, 2635–2654 (2008): the table new-joe-kuo-6.21201
/// (their recommended choice, last updated 16 September 2010), bundled unchanged with its
/// licence in OpenSim.Core/Numerics/Data.
/// <para>
/// A point is a fixed function of its index, so points can be computed in any order and on any
/// thread and are always the same. Point 0 is the origin; callers that must not sample the
/// corner start at index 1.
/// </para>
/// <para>
/// The authors chose the numbers so that earlier dimensions have the better projections. Put the
/// most important variables first.
/// </para>
/// </summary>
public sealed class SobolSequence
{
    /// <summary>The number of dimensions in the bundled table.</summary>
    public const int MaxDimensions = 21201;

    /// <summary>Points are 32-bit fractions, so indices run from 0 to 2³² − 1.</summary>
    public const long MaxIndex = uint.MaxValue;

    private const int Bits = 32;
    private const string TableResource = "OpenSim.Core.Numerics.Data.new-joe-kuo-6.21201";

    private static readonly Lazy<TableRow[]> Table = new(LoadTable);

    private readonly uint[] _directions;   // [dimension * Bits + bit]

    public int Dimensions { get; }

    public SobolSequence(int dimensions)
    {
        if (dimensions < 1 || dimensions > MaxDimensions)
            throw new ArgumentOutOfRangeException(nameof(dimensions),
                $"Sobol points are available in 1 to {MaxDimensions} dimensions; {dimensions} was asked for.");
        Dimensions = dimensions;
        _directions = new uint[dimensions * Bits];

        // First dimension: the van der Corput sequence, v_i = 2^(32 − i).
        for (int i = 0; i < Bits; i++) _directions[i] = 1u << (Bits - 1 - i);

        var table = dimensions > 1 ? Table.Value : Array.Empty<TableRow>();
        for (int j = 1; j < dimensions; j++)
        {
            var row = table[j - 1];
            int s = row.M.Length;
            int offset = j * Bits;
            for (int i = 0; i < Math.Min(s, Bits); i++)
                _directions[offset + i] = row.M[i] << (Bits - 1 - i);
            for (int i = s; i < Bits; i++)
            {
                uint v = _directions[offset + i - s];
                v ^= v >> s;
                for (int k = 1; k < s; k++)
                    if (((row.A >> (s - 1 - k)) & 1) != 0)
                        v ^= _directions[offset + i - k];
                _directions[offset + i] = v;
            }
        }
    }

    /// <summary>Writes point number <paramref name="index"/> into
    /// <paramref name="destination"/>, one coordinate in [0, 1) per dimension.</summary>
    public void Point(long index, Span<double> destination)
    {
        if (index < 0 || index > MaxIndex) throw new ArgumentOutOfRangeException(nameof(index));
        if (destination.Length < Dimensions)
            throw new ArgumentException("The destination is shorter than the number of dimensions.", nameof(destination));
        uint gray = (uint)index ^ ((uint)index >> 1);
        for (int j = 0; j < Dimensions; j++)
        {
            int offset = j * Bits;
            uint x = 0;
            uint g = gray;
            for (int bit = 0; g != 0; bit++, g >>= 1)
                if ((g & 1) != 0) x ^= _directions[offset + bit];
            destination[j] = x * (1.0 / 4294967296.0);
        }
    }

    /// <summary>Point number <paramref name="index"/> as a new array.</summary>
    public double[] Point(long index)
    {
        var point = new double[Dimensions];
        Point(index, point);
        return point;
    }

    private readonly record struct TableRow(uint A, uint[] M);

    /// <summary>Reads the bundled table. Its columns are d (dimension, from 2), s (degree of the
    /// primitive polynomial), a (the polynomial's inner coefficients as a number) and the s
    /// initial direction numbers m.</summary>
    private static TableRow[] LoadTable()
    {
        using var stream = typeof(SobolSequence).GetTypeInfo().Assembly.GetManifestResourceStream(TableResource)
            ?? throw new InvalidOperationException($"The Sobol direction-number table '{TableResource}' is missing from OpenSim.Core.");
        using var reader = new StreamReader(stream);
        var rows = new List<TableRow>(MaxDimensions - 1);
        reader.ReadLine();                                      // the heading line
        var separators = new[] { ' ', '\t' };
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            int d = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int s = int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (d != rows.Count + 2 || parts.Length != 3 + s)
                throw new InvalidDataException($"The Sobol direction-number table is damaged at dimension {d}.");
            uint a = uint.Parse(parts[2], CultureInfo.InvariantCulture);
            var m = new uint[s];
            for (int i = 0; i < s; i++) m[i] = uint.Parse(parts[3 + i], CultureInfo.InvariantCulture);
            rows.Add(new TableRow(a, m));
        }
        if (rows.Count != MaxDimensions - 1)
            throw new InvalidDataException($"The Sobol direction-number table has {rows.Count + 1} dimensions; {MaxDimensions} were expected.");
        return rows.ToArray();
    }
}
