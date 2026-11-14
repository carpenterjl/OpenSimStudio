namespace OpenSim.Optics.Materials;

/// <summary>
/// The nine dispersion formulas of the refractiveindex.info database, by the number its data
/// files give them ("type: formula 2"). These are not OpticStudio's thirteen and their numbers
/// mean different things (OpticStudio's 2 is Sellmeier 1; the database's 2 is "Sellmeier-2"), so
/// a material says which numbering it uses (<see cref="FormulaNumbering"/>).
/// <para>
/// Every expression and name is copied from the database's own definition, M. Polyanskiy,
/// "Dispersion formulas", RefractiveIndex.INFO, 2014-06-29 (database/doc/Dispersion formulas.pdf,
/// one page), formulas 1–9. λ is in micrometres (the database's unit: each page's title in
/// catalog-nk.yml states its range in µm, e.g. "Malitson 1965: n 0.21–6.7 µm" beside
/// <c>wavelength_range: 0.21 6.7</c>). Coefficients are C1, C2, … in file order, stored from index 0.
/// </para>
/// <para>
/// <b>Terms a file leaves out.</b> The definitions run to C17 (C11 for formulas 5 and 6), and the
/// files list only the coefficients they use: Malitson's fused silica gives seven for formula 1,
/// Debenham's ZnS nine for formula 4. A formula is read as a sum of terms, and the terms the file
/// gives coefficients for are evaluated; the rest are absent. A file that stops part-way through
/// a term (formula 1 with C2 but no C3) is refused, because the term cannot be evaluated and
/// dropping it would change the index silently.
/// </para>
/// </summary>
public static class RefractiveIndexInfoFormulas
{
    /// <summary>True for 1 to 9.</summary>
    public static bool IsDefined(int number) => number is >= 1 and <= 9;

    /// <summary>The name the definition gives the formula.</summary>
    public static string Name(int number) => number switch
    {
        1 => "Sellmeier (preferred)",
        2 => "Sellmeier-2",
        3 => "Polynomial",
        4 => "RefractiveIndex.INFO",
        5 => "Cauchy",
        6 => "Gases",
        7 => "Herzberger",
        8 => "Retro",
        9 => "Exotic",
        _ => throw new ArgumentOutOfRangeException(nameof(number), number, "The refractiveindex.info formulas are numbered 1 to 9.")
    };

    /// <summary>"refractiveindex.info formula 2 (Sellmeier-2)", or a note for a number outside 1–9.</summary>
    public static string NameOf(int number) => IsDefined(number)
        ? $"refractiveindex.info formula {number} ({Name(number)})"
        : $"refractiveindex.info formula {number} (not one of the database's formulas 1–9)";

    /// <summary>The sizes of the formula's terms in coefficient order: C1 alone, then the groups
    /// of coefficients each further term reads. A file's coefficient count must end on a term.</summary>
    internal static IReadOnlyList<int> TermSizes(int number) => number switch
    {
        1 or 2 or 3 => new[] { 1, 2, 2, 2, 2, 2, 2, 2, 2 },
        4 => new[] { 1, 4, 4, 2, 2, 2, 2 },
        5 or 6 => new[] { 1, 2, 2, 2, 2, 2 },
        7 => new[] { 1, 1, 1, 1, 1, 1 },
        8 => new[] { 1, 2, 1 },
        9 => new[] { 1, 2, 3 },
        _ => throw new ArgumentOutOfRangeException(nameof(number), number, "The refractiveindex.info formulas are numbered 1 to 9.")
    };

    /// <summary>Null when <paramref name="count"/> coefficients make whole terms of the formula;
    /// otherwise what is wrong, for a message.</summary>
    public static string? CheckCoefficientCount(int number, int count)
    {
        var sizes = TermSizes(number);
        int total = 0;
        var allowed = new List<int>();
        foreach (int size in sizes)
        {
            total += size;
            allowed.Add(total);
            if (total == count) return null;
        }
        return $"{NameOf(number)} takes {string.Join(", ", allowed)} coefficients (whole terms); {count} were given.";
    }

    /// <summary>
    /// The index given by formula <paramref name="number"/> with coefficients <paramref name="c"/>
    /// (C1 at index 0) at <paramref name="micrometers"/>. The caller checks the wavelength against
    /// the valid range. NaN where the formula gives n² ≤ 0 (no real index): the caller names the
    /// material. Throws <see cref="ArgumentException"/> when the coefficients do not make whole terms.
    /// </summary>
    public static double Evaluate(int number, IReadOnlyList<double> c, double micrometers)
    {
        if (!IsDefined(number))
            throw new ArgumentOutOfRangeException(nameof(number), number, $"{NameOf(number)} is not evaluated.");
        if (CheckCoefficientCount(number, c.Count) is { } problem) throw new ArgumentException(problem, nameof(c));
        double w = micrometers, w2 = w * w;
        // C(k) is the k-th coefficient, 1-based as the definition writes it; absent terms are 0.
        double C(int k) => k <= c.Count ? c[k - 1] : 0.0;
        bool Has(int k) => k <= c.Count;
        switch (number)
        {
            case 1:
            {
                // n² − 1 = C1 + Σ_{j=1..8} C(2j) λ²/(λ² − C(2j+1)²)
                double s = C(1);
                for (int k = 2; Has(k); k += 2) s += C(k) * w2 / (w2 - C(k + 1) * C(k + 1));
                return Root(1 + s);
            }
            case 2:
            {
                // n² − 1 = C1 + Σ_{j=1..8} C(2j) λ²/(λ² − C(2j+1))
                double s = C(1);
                for (int k = 2; Has(k); k += 2) s += C(k) * w2 / (w2 - C(k + 1));
                return Root(1 + s);
            }
            case 3:
            {
                // n² = C1 + Σ_{j=1..8} C(2j) λ^C(2j+1)
                double s = C(1);
                for (int k = 2; Has(k); k += 2) s += C(k) * Math.Pow(w, C(k + 1));
                return Root(s);
            }
            case 4:
            {
                // n² = C1 + C2 λ^C3/(λ² − C4^C5) + C6 λ^C7/(λ² − C8^C9)
                //      + C10 λ^C11 + C12 λ^C13 + C14 λ^C15 + C16 λ^C17
                double s = C(1);
                if (Has(2)) s += C(2) * Math.Pow(w, C(3)) / (w2 - Math.Pow(C(4), C(5)));
                if (Has(6)) s += C(6) * Math.Pow(w, C(7)) / (w2 - Math.Pow(C(8), C(9)));
                for (int k = 10; Has(k); k += 2) s += C(k) * Math.Pow(w, C(k + 1));
                return Root(s);
            }
            case 5:
            {
                // n = C1 + Σ_{j=1..5} C(2j) λ^C(2j+1)
                double n = C(1);
                for (int k = 2; Has(k); k += 2) n += C(k) * Math.Pow(w, C(k + 1));
                return n;
            }
            case 6:
            {
                // n − 1 = C1 + Σ_{j=1..5} C(2j)/(C(2j+1) − λ⁻²)
                double s = C(1);
                for (int k = 2; Has(k); k += 2) s += C(k) / (C(k + 1) - 1 / w2);
                return 1 + s;
            }
            case 7:
            {
                // n = C1 + C2/(λ² − 0.028) + C3 (1/(λ² − 0.028))² + C4 λ² + C5 λ⁴ + C6 λ⁶; 0.028 as printed.
                double l = 1 / (w2 - 0.028);
                return C(1) + C(2) * l + C(3) * l * l + C(4) * w2 + C(5) * w2 * w2 + C(6) * w2 * w2 * w2;
            }
            case 8:
            {
                // (n² − 1)/(n² + 2) = C1 + C2 λ²/(λ² − C3) + C4 λ²; solved for n²: n² = (1 + 2r)/(1 − r).
                double r = C(1) + C(2) * w2 / (w2 - C(3)) + C(4) * w2;
                return Root((1 + 2 * r) / (1 - r));
            }
            case 9:
            {
                // n² = C1 + C2/(λ² − C3) + C4 (λ − C5)/((λ − C5)² + C6)
                double s = C(1);
                if (Has(2)) s += C(2) / (w2 - C(3));
                if (Has(4)) s += C(4) * (w - C(5)) / ((w - C(5)) * (w - C(5)) + C(6));
                return Root(s);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(number), number, $"{NameOf(number)} is not evaluated.");
        }
    }

    /// <summary>√(n²), NaN when n² is not positive (the caller reports it).</summary>
    private static double Root(double nSquared) => nSquared > 0 && double.IsFinite(nSquared) ? Math.Sqrt(nSquared) : double.NaN;
}
