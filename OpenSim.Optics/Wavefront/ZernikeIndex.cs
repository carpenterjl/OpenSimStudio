namespace OpenSim.Optics.Wavefront;

/// <summary>
/// The single-index orderings of the Zernike terms, to and from (n, m), and the conversion of
/// coefficients between the Fringe and Standard sets.
/// <para>
/// <b>Noll (Standard and Annular).</b> Noll, "Zernike polynomials and atmospheric turbulence",
/// JOSA 66, 207 (1976), as tabulated in OpticStudio's "Zernike Standard Coefficients" help (terms
/// 1–28, which cites Noll as its definition): terms run by increasing n, and within one n by
/// increasing |m|; of the two terms of one |m| &gt; 0, the even index is the cos term and the odd
/// index the sin term. So j = 2 is ρ cos θ, 3 is ρ sin θ, 5 is sin 2θ, 6 is cos 2θ, 7 is the
/// sin coma and 8 the cos coma.
/// </para>
/// <para>
/// <b>Fringe.</b> Wyant and Creath, "Basic Wavefront Aberration Theory for Optical Metrology",
/// Applied Optics and Optical Engineering XI (1992), Table III, numbered from 0; the Fringe index
/// is theirs plus 1, and agrees term by term with OpticStudio's "Zernike Fringe Coefficients"
/// table. Terms 1–36 run in blocks of g = (n + |m|)/2 = 0 … 5; block g holds Fringe indices
/// g² + 1 … (g + 1)², with |m| falling from g to 0 and each cos term before its sin term. Term 37
/// is the 12th-degree spherical term (n = 12, m = 0), not the first term of block 6: both tables
/// end with it, and OpticStudio stops there. There is no Fringe index above 37.
/// </para>
/// </summary>
public static class ZernikeIndex
{
    /// <summary>The number of Fringe terms (Wyant and Creath's 0 … 36; OpticStudio's 1 … 37).</summary>
    public const int FringeTermCount = 37;

    /// <summary>The term with Noll index <paramref name="j"/> (1-based).</summary>
    public static ZernikeTerm FromNoll(int j)
    {
        if (j < 1) throw new ArgumentOutOfRangeException(nameof(j), j, "Noll indices start at 1.");
        // n is the degree whose run first(n) = n(n+1)/2 + 1 … (n+1)(n+2)/2 holds j.
        int n = (int)Math.Floor((Math.Sqrt(8.0 * j + 1) - 1) / 2) - 1;
        if (n < 0) n = 0;
        while ((long)(n + 1) * (n + 2) / 2 < j) n++;
        while (n > 0 && (long)n * (n + 1) / 2 >= j) n--;
        int position = j - (n * (n + 1) / 2 + 1);
        int absM = (n & 1) == 0
            ? (position == 0 ? 0 : 2 * ((position + 1) / 2))
            : 2 * (position / 2) + 1;
        if (absM == 0) return new ZernikeTerm(n, 0);
        return new ZernikeTerm(n, (j & 1) == 0 ? absM : -absM);
    }

    /// <summary>The Noll index (1-based) of a term.</summary>
    public static int ToNoll(ZernikeTerm term)
    {
        int n = term.N, absM = term.AbsM;
        int first = n * (n + 1) / 2 + 1;
        if (absM == 0) return first;
        // The two terms of this |m| sit at positions |m| − 1 and |m| within degree n.
        int a = first + absM - 1, b = first + absM;
        bool wantEven = term.M > 0;
        return ((a & 1) == 0) == wantEven ? a : b;
    }

    /// <summary>The term with Fringe index <paramref name="j"/>, 1 … 37.</summary>
    public static ZernikeTerm FromFringe(int j)
    {
        if (j < 1 || j > FringeTermCount)
            throw new ArgumentOutOfRangeException(nameof(j), j, $"Fringe indices run from 1 to {FringeTermCount}.");
        if (j == FringeTermCount) return new ZernikeTerm(12, 0);
        int g = (int)Math.Ceiling(Math.Sqrt(j)) - 1;
        while (g * g >= j) g--;
        while ((g + 1) * (g + 1) < j) g++;
        // From the end of the block: 0 is m = 0, then 2|m| − 1 for sin and 2|m| for cos.
        int fromEnd = (g + 1) * (g + 1) - j;
        int absM = (fromEnd + 1) / 2;
        int n = 2 * g - absM;
        if (absM == 0) return new ZernikeTerm(n, 0);
        return new ZernikeTerm(n, (fromEnd & 1) == 0 ? absM : -absM);
    }

    /// <summary>True when the term has a Fringe index, with that index (1 … 37).</summary>
    public static bool TryToFringe(ZernikeTerm term, out int j)
    {
        if (term.N == 12 && term.M == 0)
        {
            j = FringeTermCount;
            return true;
        }
        int twiceG = term.N + term.AbsM;
        int g = twiceG / 2;
        if (g > 5)
        {
            j = 0;
            return false;
        }
        j = (g + 1) * (g + 1) - 2 * term.AbsM + (term.M < 0 ? 1 : 0);
        return true;
    }

    /// <summary>The Fringe index of a term; throws when the term is not one of the 37.</summary>
    public static int ToFringe(ZernikeTerm term)
    {
        if (!TryToFringe(term, out int j))
            throw new ArgumentOutOfRangeException(nameof(term), term, "This term is not one of the 37 Fringe terms.");
        return j;
    }

    /// <summary>The term with index <paramref name="j"/> in an ordering. Annular terms use
    /// Noll's order.</summary>
    public static ZernikeTerm FromIndex(ZernikeOrdering ordering, int j) => ordering switch
    {
        ZernikeOrdering.Fringe => FromFringe(j),
        ZernikeOrdering.Standard or ZernikeOrdering.Annular => FromNoll(j),
        _ => throw new ArgumentOutOfRangeException(nameof(ordering), ordering, "Unknown Zernike ordering."),
    };

    /// <summary>The index of a term in an ordering.</summary>
    public static int ToIndex(ZernikeOrdering ordering, ZernikeTerm term) => ordering switch
    {
        ZernikeOrdering.Fringe => ToFringe(term),
        ZernikeOrdering.Standard or ZernikeOrdering.Annular => ToNoll(term),
        _ => throw new ArgumentOutOfRangeException(nameof(ordering), ordering, "Unknown Zernike ordering."),
    };

    /// <summary>The Noll index of Fringe term <paramref name="fringeIndex"/>.</summary>
    public static int FringeToNoll(int fringeIndex) => ToNoll(FromFringe(fringeIndex));

    /// <summary>The Fringe index of Noll term <paramref name="nollIndex"/>, if it has one.</summary>
    public static bool TryNollToFringe(int nollIndex, out int fringeIndex) =>
        TryToFringe(FromNoll(nollIndex), out fringeIndex);

    /// <summary>
    /// The factor in front of R_n^m in a term: 1 for Fringe; √(n + 1) for m = 0 and √(2(n + 1))
    /// otherwise for Standard and Annular (Noll's normalisation, the factors printed in
    /// OpticStudio's Standard and Annular tables).
    /// </summary>
    public static double Normalization(ZernikeOrdering ordering, ZernikeTerm term) => ordering switch
    {
        ZernikeOrdering.Fringe => 1.0,
        ZernikeOrdering.Standard or ZernikeOrdering.Annular =>
            Math.Sqrt((term.M == 0 ? 1.0 : 2.0) * (term.N + 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(ordering), ordering, "Unknown Zernike ordering."),
    };

    /// <summary>
    /// Re-expresses circular-pupil coefficients from one ordering in another. The Fringe and
    /// Standard terms are the same functions up to the normalisation factor, so this is exact
    /// term by term (no fit): c_to = c_from · N_from / N_to. Element 0 is term 1. Converting to
    /// Fringe throws if a non-zero coefficient has no Fringe term. Annular coefficients belong to
    /// one obscuration and are not converted here.
    /// </summary>
    public static double[] ConvertCoefficients(
        IReadOnlyList<double> coefficients, ZernikeOrdering from, ZernikeOrdering to)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        if (from == ZernikeOrdering.Annular || to == ZernikeOrdering.Annular)
            throw new ArgumentException("Annular coefficients depend on the obscuration and are not converted between orderings.");
        var terms = new ZernikeTerm[coefficients.Count];
        int length = 0;
        for (int i = 0; i < coefficients.Count; i++)
        {
            terms[i] = FromIndex(from, i + 1);
            if (to == ZernikeOrdering.Fringe && !TryToFringe(terms[i], out _))
            {
                if (coefficients[i] != 0)
                    throw new ArgumentException($"Term {i + 1} ({terms[i]}) has no Fringe term, and its coefficient is not zero.", nameof(coefficients));
                continue;
            }
            length = Math.Max(length, ToIndex(to, terms[i]));
        }
        var result = new double[length];
        for (int i = 0; i < coefficients.Count; i++)
        {
            if (to == ZernikeOrdering.Fringe && !TryToFringe(terms[i], out _)) continue;
            double scale = Normalization(from, terms[i]) / Normalization(to, terms[i]);
            result[ToIndex(to, terms[i]) - 1] = coefficients[i] * scale;
        }
        return result;
    }
}
