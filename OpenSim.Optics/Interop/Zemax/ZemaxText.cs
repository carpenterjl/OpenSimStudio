using System.Globalization;
using System.Text;

namespace OpenSim.Optics.Interop.Zemax;

/// <summary>
/// The text layer of a Zemax lens file (OP-94): decoding, line splitting and numbers. The
/// encodings tried follow the reference readers: UTF-16 (with or without a byte-order mark),
/// UTF-8 and, for bytes that are not UTF-8, ISO-8859-1 (Optiland 0.6.3,
/// optiland/fileio/zemax/reader/parser.py, <c>ZemaxDataParser.parse</c>: "Tries UTF-16 LE,
/// UTF-8, and ISO-8859-1"; RayOptics 0.9.10, rayoptics/zemax/zmxread.py,
/// <c>read_lens_file</c>: utf-8, utf-16, utf-8-sig, iso-8859-1). Files are written as UTF-16 LE
/// with a byte-order mark (Optiland's writer/encoder.py: "written to disk as UTF-16 LE"), lines
/// ended by CR LF as the OpticStudio-written files shipped with RayOptics are.
/// </summary>
public static class ZemaxText
{
    /// <summary>The text of a lens file's bytes and the encoding used.</summary>
    public static (string Text, string Encoding) Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8");
        if (LooksLikeUtf16LittleEndian(bytes)) return (Encoding.Unicode.GetString(bytes), "UTF-16 LE (no byte-order mark)");
        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), "ISO-8859-1");
        }
    }

    /// <summary>The bytes of a file's text: UTF-16 LE with a byte-order mark.</summary>
    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var body = Encoding.Unicode.GetBytes(text);
        var bytes = new byte[body.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        body.CopyTo(bytes, 2);
        return bytes;
    }

    /// <summary>ASCII text in UTF-16 LE has a zero in every odd byte; UTF-8 text has none.</summary>
    private static bool LooksLikeUtf16LittleEndian(byte[] bytes)
    {
        int pairs = Math.Min(bytes.Length / 2, 64);
        if (pairs < 2) return false;
        for (int i = 0; i < pairs; i++)
            if (bytes[2 * i] == 0 || bytes[2 * i + 1] != 0) return false;
        return true;
    }

    /// <summary>The whitespace-separated tokens of a line.</summary>
    public static string[] Tokens(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The text after the keyword and one separator, as written (for NAME, COMM, NOTE, COAT).</summary>
    public static string Rest(string trimmedLine, string keyword)
    {
        if (trimmedLine.Length <= keyword.Length) return "";
        return trimmedLine[(keyword.Length + 1)..];
    }

    /// <summary>A number as the files write it ("2.0E+1", "1.5", "INFINITY"); a comma decimal
    /// separator is read as a point, as Optiland's parser does for GLAS (parser.py,
    /// <c>_read_glass</c>).</summary>
    public static bool TryNumber(string token, out double value)
    {
        if (string.Equals(token, "INFINITY", StringComparison.OrdinalIgnoreCase)) { value = double.PositiveInfinity; return true; }
        return double.TryParse(token.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A number written so it reads back to the same double ("R", invariant culture).</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
