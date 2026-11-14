using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenSim.Optics.Materials;

/// <summary>
/// A number as a catalog prints it: the value and the text it was read from. The text is kept
/// because how many digits a vendor printed is information: a printed nd of "1.62004" claims
/// five decimals, "1.90138E+00" also five, "1.000000" six. <see cref="HalfLastDigit"/> is the
/// rounding half-width those digits imply, which <see cref="CatalogSelfCheck"/> compares against.
/// </summary>
public sealed partial record PrintedNumber(double Value, string Text)
{
    /// <summary>Half a unit in the last printed digit, 0.5 × 10^(e − d), where d is the number
    /// of digits after the mantissa's decimal point and e the exponent (0 without one).
    /// "1.62004" gives 5e−6, "3.16400E+01" gives 5e−5, "52.3" gives 0.05, "2" gives 0.5.</summary>
    public double HalfLastDigit
    {
        get
        {
            var m = Layout().Match(Text.Trim());
            if (!m.Success) return double.NaN;
            int decimals = m.Groups["frac"].Success ? m.Groups["frac"].Value.Length : 0;
            int exponent = m.Groups["exp"].Success ? int.Parse(m.Groups["exp"].Value, CultureInfo.InvariantCulture) : 0;
            return 0.5 * Math.Pow(10, exponent - decimals);
        }
    }

    /// <summary>Reads a printed decimal number (optional sign, digits, optional fraction,
    /// optional E exponent). False for anything else, including the absent-value marks.</summary>
    public static bool TryParse(string text, out PrintedNumber number)
    {
        number = null!;
        string t = text.Trim();
        if (!Layout().IsMatch(t)) return false;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            return false;
        number = new PrintedNumber(value, t);
        return true;
    }

    public override string ToString() => Text;

    [GeneratedRegex(@"^(?=[+-]?\.?\d)[+-]?(?<int>\d*)(?:\.(?<frac>\d*))?(?:[eE](?<exp>[+-]?\d+))?$")]
    private static partial Regex Layout();
}
