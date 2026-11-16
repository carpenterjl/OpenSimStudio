using System.Globalization;
using System.Security.Cryptography;

namespace OpenSim.Optics.Photometry;

/// <summary>
/// The CIE's published data tables (plan item OP-79; docs/Optics-Conventions.md §73), embedded
/// unchanged from <c>Photometry/Data/</c> (CC BY-SA 4.0; see <c>Data/NOTICE.txt</c>):
/// <list type="bullet">
/// <item>the CIE 1931 2° colour-matching functions x̄, ȳ, z̄ (CIE, DOI 10.25039/CIE.DS.xvudnb9b,
/// file <c>CIE_xyz_1931_2deg.csv</c>, from CIE 018:2019 Table 6);</item>
/// <item>the photopic spectral luminous efficiency V(λ) (CIE, file <c>CIE_sle_photopic.csv</c>,
/// from CIE 018:2019 Table 1, "λ in standard air").</item>
/// </list>
/// Both run from 360 to 830 nm in 1 nm steps. Between rows the value is interpolated linearly and
/// outside the range it is zero, as the metadata files say ("interpolationMethod": "linear",
/// "extrapolationMethod": "zero"). Each file's SHA-256 is checked against the checksum its
/// metadata file publishes the first time it is read; a mismatch throws.
/// </summary>
public static class CieTables
{
    /// <summary>The first and last tabulated wavelengths, nm (metadata "wavelength_first",
    /// "wavelength_last").</summary>
    public const int FirstNanometers = 360, LastNanometers = 830;

    // SHA-256 checksums copied from the metadata files' "checksums" entries.
    private const string XyzSha256 = "fa663e3535a7e0763a745993a1f0a192eb0275ac46ad2d1befd7626841e713c1";
    private const string VSha256 = "ee5d5d17922ae645d4af52cacf6a50bdb9385749f9d2181ca312eb2b08febac2";

    private static readonly Lazy<double[][]> Xyz = new(() => Load("cie/CIE_xyz_1931_2deg.csv", XyzSha256, 3));
    private static readonly Lazy<double[][]> Photopic = new(() => Load("cie/CIE_sle_photopic.csv", VSha256, 1));

    /// <summary>The rows of the colour-matching table: [k] = {x̄, ȳ, z̄} at 360 + k nm.</summary>
    public static IReadOnlyList<double[]> ColorMatchingRows => Xyz.Value;

    /// <summary>The rows of the V(λ) table: [k][0] = V at 360 + k nm.</summary>
    public static IReadOnlyList<double[]> PhotopicRows => Photopic.Value;

    /// <summary>x̄, ȳ, z̄ at <paramref name="nanometers"/> (linear between rows, zero outside 360–830 nm).</summary>
    public static (double X, double Y, double Z) ColorMatching(double nanometers)
    {
        var t = Xyz.Value;
        if (!Interval(nanometers, out int k, out double f)) return (0, 0, 0);
        if (f == 0) return (t[k][0], t[k][1], t[k][2]);
        return (Lerp(t[k][0], t[k + 1][0], f), Lerp(t[k][1], t[k + 1][1], f), Lerp(t[k][2], t[k + 1][2], f));
    }

    /// <summary>V(λ) at <paramref name="nanometers"/> (in standard air; linear between rows, zero
    /// outside 360–830 nm).</summary>
    public static double PhotopicEfficiency(double nanometers)
    {
        var t = Photopic.Value;
        if (!Interval(nanometers, out int k, out double f)) return 0;
        return f == 0 ? t[k][0] : Lerp(t[k][0], t[k + 1][0], f);
    }

    private static double Lerp(double a, double b, double f) => a + (b - a) * f;

    private static bool Interval(double nm, out int k, out double f)
    {
        k = 0; f = 0;
        if (!double.IsFinite(nm) || nm < FirstNanometers || nm > LastNanometers) return false;
        double u = nm - FirstNanometers;
        k = (int)Math.Floor(u);
        f = u - k;
        if (k >= LastNanometers - FirstNanometers) { k = LastNanometers - FirstNanometers; f = 0; }
        return true;
    }

    private static double[][] Load(string resource, string sha256, int columns)
    {
        using var stream = typeof(CieTables).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The CIE table '{resource}' is not embedded in OpenSim.Optics.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != sha256)
            throw new InvalidOperationException($"The CIE table '{resource}' does not match the SHA-256 its metadata publishes ({sha256}); it is {hash}. The file must be shipped unchanged.");
        var lines = System.Text.Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int count = LastNanometers - FirstNanometers + 1;
        if (lines.Length != count)
            throw new InvalidOperationException($"The CIE table '{resource}' has {lines.Length} rows; {count} were expected (360–830 nm in 1 nm steps).");
        var rows = new double[count][];
        for (int i = 0; i < count; i++)
        {
            var cells = lines[i].Split(',');
            if (cells.Length != columns + 1 || int.Parse(cells[0], CultureInfo.InvariantCulture) != FirstNanometers + i)
                throw new InvalidOperationException($"The CIE table '{resource}', row {i + 1}: '{lines[i]}' is not {FirstNanometers + i} nm with {columns} values.");
            rows[i] = new double[columns];
            for (int c = 0; c < columns; c++) rows[i][c] = double.Parse(cells[c + 1], NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        return rows;
    }
}

/// <summary>
/// Radiometric to photometric conversion and colorimetry (plan item OP-79; docs/Optics-Conventions.md
/// §73).
/// <list type="bullet">
/// <item><b>Luminous efficacy.</b> The SI fixes "the luminous efficacy of monochromatic radiation
/// of frequency 540 × 10¹² Hz, K_cd, … to be 683 when expressed in the unit lm W⁻¹" (BIPM, The
/// International System of Units, 9th edition 2019, V4.01, section 2.3.1 "The candela",
/// pp. 130–131). This program converts a radiant flux Φ_e at wavelength λ to luminous flux as
/// Φ_v = 683 lm/W · V(λ) · Φ_e with the CIE's V(λ) table (<see cref="CieTables"/>), whose
/// maximum, V = 1, is the 555 nm row (in standard air).</item>
/// <item><b>Tristimulus values</b> on the same scale: X = 683·x̄(λ)·Φ_e, Y = 683·ȳ(λ)·Φ_e,
/// Z = 683·z̄(λ)·Φ_e, so Y is the luminous flux in lumens ("ȳ(λ) is identical to V(λ)", CIE
/// e-ILV 17-23-045, Note 1).</item>
/// <item><b>Chromaticity.</b> x = X/(X+Y+Z), y = Y/(X+Y+Z) (e-ILV 17-23-053, "the quotients of
/// each of a set of three tristimulus values and their sum"); u′ = 4X/(X+15Y+3Z),
/// v′ = 9Y/(X+15Y+3Z) (e-ILV 17-23-073, CIE 1976 UCS diagram).</item>
/// <item><b>sRGB.</b> Linear sRGB from XYZ by the matrix of W3C CSS Color Module Level 4 §19
/// "Sample code for color conversions" (<c>XYZ_to_lin_sRGB</c>, D65 white, no chromatic
/// adaptation; Candidate Recommendation Draft of 12 November 2026), and the transfer function of
/// its <c>gam_sRGB</c> (12.92·v up to 0.0031308, 1.055·v^(1/2.4) − 0.055 above). IEC 61966-2-1
/// itself was not available.</item>
/// </list>
/// </summary>
public static class Photometric
{
    /// <summary>K_cd = 683 lm/W at 540 × 10¹² Hz (SI Brochure, 9th ed., §2.3.1 "The candela", pp. 130–131).</summary>
    public const double LuminousEfficacy = 683;

    /// <summary>The frequency at which K_cd is defined, Hz (SI Brochure, 9th ed., §2.3.1).</summary>
    public const double CandelaFrequencyHz = 540e12;

    /// <summary>The speed of light in vacuum, m/s (SI Brochure, 9th ed., §2.2 "Definition of the SI", the list of defining constants, pp. 123–124).</summary>
    public const double SpeedOfLight = 299_792_458;

    /// <summary>V(λ) for a wavelength in micrometers (the table is in standard air).</summary>
    public static double PhotopicEfficiency(double micrometers) => CieTables.PhotopicEfficiency(micrometers * 1000);

    /// <summary>Lumens per watt at <paramref name="micrometers"/>: 683·V(λ).</summary>
    public static double LumensPerWatt(double micrometers) => LuminousEfficacy * PhotopicEfficiency(micrometers);

    /// <summary>683·(x̄, ȳ, z̄)(λ): the tristimulus values of one watt at <paramref name="micrometers"/>.</summary>
    public static (double X, double Y, double Z) TristimulusPerWatt(double micrometers)
    {
        var (x, y, z) = CieTables.ColorMatching(micrometers * 1000);
        return (LuminousEfficacy * x, LuminousEfficacy * y, LuminousEfficacy * z);
    }

    /// <summary>(x, y); NaN when X + Y + Z is zero.</summary>
    public static (double x, double y) Chromaticity(double X, double Y, double Z)
    {
        double s = X + Y + Z;
        return s > 0 ? (X / s, Y / s) : (double.NaN, double.NaN);
    }

    /// <summary>(u′, v′) of the CIE 1976 UCS diagram; NaN when X + 15Y + 3Z is zero.</summary>
    public static (double u, double v) Ucs1976(double X, double Y, double Z)
    {
        double d = X + 15 * Y + 3 * Z;
        return d > 0 ? (4 * X / d, 9 * Y / d) : (double.NaN, double.NaN);
    }

    // CSS Color Module Level 4, §19, XYZ_to_lin_sRGB and lin_sRGB_to_XYZ, as exact quotients.
    private static readonly double[,] ToLinearSrgb =
    {
        { 12831.0 / 3959, -329.0 / 214, -1974.0 / 3959 },
        { -851781.0 / 878810, 1648619.0 / 878810, 36519.0 / 878810 },
        { 705.0 / 12673, -2585.0 / 12673, 705.0 / 667 },
    };

    private static readonly double[,] FromLinearSrgb =
    {
        { 506752.0 / 1228815, 87881.0 / 245763, 12673.0 / 70218 },
        { 87098.0 / 409605, 175762.0 / 245763, 12673.0 / 175545 },
        { 7918.0 / 409605, 87881.0 / 737289, 1001167.0 / 1053270 },
    };

    /// <summary>Linear-light sRGB of XYZ (Y = 1 is the white's luminance).</summary>
    public static (double R, double G, double B) LinearSrgb(double X, double Y, double Z) => Apply(ToLinearSrgb, X, Y, Z);

    /// <summary>XYZ of linear-light sRGB.</summary>
    public static (double X, double Y, double Z) XyzOfLinearSrgb(double r, double g, double b) => Apply(FromLinearSrgb, r, g, b);

    /// <summary>The sRGB transfer function (gam_sRGB of CSS Color 4 §19), odd-extended below zero.</summary>
    public static double SrgbEncode(double linear)
    {
        double a = Math.Abs(linear);
        return a > 0.0031308 ? Math.CopySign(1.055 * Math.Pow(a, 1 / 2.4) - 0.055, linear) : 12.92 * linear;
    }

    private static (double, double, double) Apply(double[,] m, double a, double b, double c) =>
        (m[0, 0] * a + m[0, 1] * b + m[0, 2] * c, m[1, 0] * a + m[1, 1] * b + m[1, 2] * c, m[2, 0] * a + m[2, 1] * b + m[2, 2] * c);
}
