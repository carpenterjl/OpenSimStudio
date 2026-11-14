namespace OpenSim.Optics.Materials;

/// <summary>
/// The thirteen dispersion formulas: their names, how many coefficients each reads, and the
/// expression. Wavelength λ is in micrometers, in air at the catalog's reference conditions,
/// and the result is the index relative to that air, as an AGF catalog's coefficients are fitted.
/// <para>
/// Every expression is copied from AGFFileReader.jl (MIT, Microsoft), <c>src/utilities.jl</c>,
/// function <c>index</c>, lines 133–183, and checked term by term against ZemaxGlass.py (MIT,
/// N. Hagen), <c>get_dispersion</c>, lines 845–888. The two agree on every formula except
/// Sellmeier 2 (number 4); see <see cref="IsVerified"/>. The coefficients are C1…C10 in the
/// order of the CD line, stored here from index 0.
/// </para>
/// <para>
/// AGFFileReader also evaluates numbers −2 (Cauchy) and −1 (a fitted model taken from GOptical).
/// Those are its own extensions, not OpticStudio formulas, and are not evaluated here; nor is 0,
/// which ZemaxGlass uses internally for air and vacuum.
/// </para>
/// </summary>
public static class DispersionFormulas
{
    /// <summary>True for the numbers 1 to 13.</summary>
    public static bool IsDefined(int number) => number is >= 1 and <= 13;

    /// <summary>The formula's name as OpticStudio's list writes it.</summary>
    public static string Name(DispersionFormula formula) => formula switch
    {
        DispersionFormula.Schott => "Schott",
        DispersionFormula.Sellmeier1 => "Sellmeier 1",
        DispersionFormula.Herzberger => "Herzberger",
        DispersionFormula.Sellmeier2 => "Sellmeier 2",
        DispersionFormula.Conrady => "Conrady",
        DispersionFormula.Sellmeier3 => "Sellmeier 3",
        DispersionFormula.HandbookOfOptics1 => "Handbook of Optics 1",
        DispersionFormula.HandbookOfOptics2 => "Handbook of Optics 2",
        DispersionFormula.Sellmeier4 => "Sellmeier 4",
        DispersionFormula.Extended1 => "Extended 1",
        DispersionFormula.Sellmeier5 => "Sellmeier 5",
        DispersionFormula.Extended2 => "Extended 2",
        DispersionFormula.Extended3 => "Extended 3",
        _ => throw new ArgumentOutOfRangeException(nameof(formula), formula, "Not one of the thirteen dispersion formulas.")
    };

    /// <summary>The name of a formula number as a file prints it, or a description of a number
    /// that is not one of the thirteen.</summary>
    public static string NameOf(int number) =>
        IsDefined(number) ? $"{number} ({Name((DispersionFormula)number)})" : $"{number} (not one of OpticStudio's formulas 1–13)";

    /// <summary>How many of C1…C10 the formula reads (the highest index in its expression).</summary>
    public static int CoefficientCount(DispersionFormula formula) => formula switch
    {
        DispersionFormula.Schott => 6,
        DispersionFormula.Sellmeier1 => 6,
        DispersionFormula.Herzberger => 6,
        DispersionFormula.Sellmeier2 => 5,
        DispersionFormula.Conrady => 3,
        DispersionFormula.Sellmeier3 => 8,
        DispersionFormula.HandbookOfOptics1 => 4,
        DispersionFormula.HandbookOfOptics2 => 4,
        DispersionFormula.Sellmeier4 => 5,
        DispersionFormula.Extended1 => 8,
        DispersionFormula.Sellmeier5 => 10,
        DispersionFormula.Extended2 => 8,
        DispersionFormula.Extended3 => 9,
        _ => throw new ArgumentOutOfRangeException(nameof(formula), formula, "Not one of the thirteen dispersion formulas.")
    };

    /// <summary>
    /// False for Sellmeier 2 (number 4) only. The two readers this program follows disagree on
    /// its second term: AGFFileReader (utilities.jl line 147) has
    /// n² − 1 = C1 + C2 λ²/(λ² − C3²) + C4 λ²/(λ² − C5²), ZemaxGlass (ZemaxGlass.py line 855) has
    /// n² − 1 = C1 + C2 λ²/(λ² − C3²) + C4/(λ² − C5²). Neither form has been confirmed against
    /// OpticStudio, and no glass in the 23 catalogs read for this item uses formula 4, so the
    /// catalogs cannot decide it. <see cref="Evaluate"/> uses AGFFileReader's form, whose second
    /// term has the λ²/(λ² − C) shape every other Sellmeier term has; the AGF reader names the
    /// glass in a note whenever a file uses formula 4.
    /// </summary>
    public static bool IsVerified(DispersionFormula formula) => formula != DispersionFormula.Sellmeier2;

    /// <summary>
    /// The index relative to air given by <paramref name="formula"/> with coefficients
    /// <paramref name="c"/> (C1 at index 0) at <paramref name="micrometers"/>. The caller checks
    /// the wavelength against the material's valid range first; this only does arithmetic.
    /// Throws when fewer coefficients are given than the formula reads.
    /// </summary>
    public static double Evaluate(DispersionFormula formula, IReadOnlyList<double> c, double micrometers)
    {
        int needed = CoefficientCount(formula);
        if (c.Count < needed)
            throw new ArgumentException(
                $"Dispersion formula {NameOf((int)formula)} reads {needed} coefficients; {c.Count} were given.", nameof(c));
        double w = micrometers, w2 = w * w;
        switch (formula)
        {
            case DispersionFormula.Schott:
                // n² = C1 + C2 λ² + C3 λ⁻² + C4 λ⁻⁴ + C5 λ⁻⁶ + C6 λ⁻⁸
                return Math.Sqrt(c[0] + c[1] * w2 + c[2] / w2 + c[3] / (w2 * w2) + c[4] / (w2 * w2 * w2) + c[5] / (w2 * w2 * w2 * w2));
            case DispersionFormula.Sellmeier1:
                // n² − 1 = C1 λ²/(λ² − C2) + C3 λ²/(λ² − C4) + C5 λ²/(λ² − C6)
                return Math.Sqrt(1 + c[0] * w2 / (w2 - c[1]) + c[2] * w2 / (w2 - c[3]) + c[4] * w2 / (w2 - c[5]));
            case DispersionFormula.Herzberger:
            {
                // n = C1 + C2 L + C3 L² + C4 λ² + C5 λ⁴ + C6 λ⁶, L = 1/(λ² − 0.028); 0.028 as both sources print it.
                double l = 1 / (w2 - 0.028);
                return c[0] + c[1] * l + c[2] * l * l + c[3] * w2 + c[4] * w2 * w2 + c[5] * w2 * w2 * w2;
            }
            case DispersionFormula.Sellmeier2:
                // n² − 1 = C1 + C2 λ²/(λ² − C3²) + C4 λ²/(λ² − C5²): AGFFileReader's form, UNVERIFIED (see IsVerified).
                return Math.Sqrt(1 + c[0] + c[1] * w2 / (w2 - c[2] * c[2]) + c[3] * w2 / (w2 - c[4] * c[4]));
            case DispersionFormula.Conrady:
                // n = C1 + C2/λ + C3/λ^3.5
                return c[0] + c[1] / w + c[2] / Math.Pow(w, 3.5);
            case DispersionFormula.Sellmeier3:
                // n² − 1 = Σ_{k=1..4} C(2k−1) λ²/(λ² − C(2k))
                return Math.Sqrt(1 + SellmeierSum(c, w2, 4));
            case DispersionFormula.HandbookOfOptics1:
                // n² = C1 + C2/(λ² − C3) − C4 λ²
                return Math.Sqrt(c[0] + c[1] / (w2 - c[2]) - c[3] * w2);
            case DispersionFormula.HandbookOfOptics2:
                // n² = C1 + C2 λ²/(λ² − C3) − C4 λ²
                return Math.Sqrt(c[0] + c[1] * w2 / (w2 - c[2]) - c[3] * w2);
            case DispersionFormula.Sellmeier4:
                // n² = C1 + C2 λ²/(λ² − C3) + C4 λ²/(λ² − C5)
                return Math.Sqrt(c[0] + c[1] * w2 / (w2 - c[2]) + c[3] * w2 / (w2 - c[4]));
            case DispersionFormula.Extended1:
                // n² = C1 + C2 λ² + C3 λ⁻² + C4 λ⁻⁴ + C5 λ⁻⁶ + C6 λ⁻⁸ + C7 λ⁻¹⁰ + C8 λ⁻¹²
                return Math.Sqrt(c[0] + c[1] * w2 + InversePowers(c, 2, 6, w2));
            case DispersionFormula.Sellmeier5:
                // n² − 1 = Σ_{k=1..5} C(2k−1) λ²/(λ² − C(2k))
                return Math.Sqrt(1 + SellmeierSum(c, w2, 5));
            case DispersionFormula.Extended2:
                // n² = C1 + C2 λ² + C3 λ⁻² + C4 λ⁻⁴ + C5 λ⁻⁶ + C6 λ⁻⁸ + C7 λ⁴ + C8 λ⁶
                return Math.Sqrt(c[0] + c[1] * w2 + InversePowers(c, 2, 4, w2) + c[6] * w2 * w2 + c[7] * w2 * w2 * w2);
            case DispersionFormula.Extended3:
                // n² = C1 + C2 λ² + C3 λ⁴ + C4 λ⁻² + C5 λ⁻⁴ + C6 λ⁻⁶ + C7 λ⁻⁸ + C8 λ⁻¹⁰ + C9 λ⁻¹²
                return Math.Sqrt(c[0] + c[1] * w2 + c[2] * w2 * w2 + InversePowers(c, 3, 6, w2));
            default:
                throw new ArgumentOutOfRangeException(nameof(formula), formula, "Not one of the thirteen dispersion formulas.");
        }
    }

    /// <summary>Σ C(2k−1) λ²/(λ² − C(2k)) for k = 1…terms.</summary>
    private static double SellmeierSum(IReadOnlyList<double> c, double w2, int terms)
    {
        double sum = 0;
        for (int k = 0; k < terms; k++) sum += c[2 * k] * w2 / (w2 - c[2 * k + 1]);
        return sum;
    }

    /// <summary>c[first] λ⁻² + c[first+1] λ⁻⁴ + … over <paramref name="count"/> terms.</summary>
    private static double InversePowers(IReadOnlyList<double> c, int first, int count, double w2)
    {
        double sum = 0, power = 1;
        for (int k = 0; k < count; k++)
        {
            power /= w2;
            sum += c[first + k] * power;
        }
        return sum;
    }
}
