using System.Buffers.Binary;
using System.Text;

namespace OpenSim.Optics.Interop.Zemax;

/// <summary>One file inside a .zar archive, unpacked.</summary>
public sealed record ZarEntry(string Name, byte[] Contents);

/// <summary>
/// Reads OpticStudio's .zar archive (OP-94). The layout is not published by its vendor; it is
/// taken from the description in zmxtools 0.1.5 (zmxtools/zar.py, <c>read</c> and
/// <c>_decompress_lzw</c>; AGPL-3, read as documentation only, no code copied):
/// <list type="bullet">
/// <item>The archive is a sequence of blocks, each a 2-byte version, a header and the packed
/// file. A first version byte 0xEC (the later layout) has a header of 0x286 bytes after the
/// version, the packed size an unsigned 64-bit little-endian integer at header offset 0x0E, and
/// the file name in UTF-16 LE from header offset 0x2E to the first NUL. A first version byte 0xEA
/// (the earlier layout) has a header of 0x14A bytes, the size an unsigned 32-bit little-endian
/// integer at offset 0x0A, and the name in UTF-8 from offset 0x1E to the first NUL.</item>
/// <item>A name ending ".LZW" is compressed: LZW with an initial dictionary of the 256 single
/// bytes, codes read most-significant bit first, the code width starting at 9 bits and growing
/// by one whenever the dictionary has reached 2^width entries before a code is read, no clear
/// code; the ".LZW" is dropped from the name.</item>
/// </list>
/// zmxtools carries on with the later layout after an unknown version byte; this reader stops
/// with an error naming the byte and its offset instead (the house style: no guessing).
/// </summary>
public static class ZarArchive
{
    private const int LaterHeader = 0x288 - 2, EarlierHeader = 0x14C - 2;

    /// <summary>The archive's files in order; throws <see cref="InvalidDataException"/> naming the
    /// offset of a block that is not one of the two layouts or is cut short.</summary>
    public static IReadOnlyList<ZarEntry> Read(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var entries = new List<ZarEntry>();
        int pos = 0;
        while (pos + 2 <= archive.Length)
        {
            int block = pos;
            byte version = archive[pos];
            pos += 2;
            bool later = version == 0xEC;
            if (!later && version != 0xEA)
                throw new InvalidDataException($"ZAR archive: the block at byte {block} starts with version byte 0x{version:X2}, which is neither of the two known layouts (0xEC, 0xEA).");
            int headerLength = later ? LaterHeader : EarlierHeader;
            if (pos + headerLength > archive.Length)
                throw new InvalidDataException($"ZAR archive: the block at byte {block} is cut short inside its header.");
            var header = archive.AsSpan(pos, headerLength);
            pos += headerLength;
            ulong size;
            string name;
            if (later)
            {
                size = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(0x0E, 8));
                var nameBytes = header[0x2E..];
                int end = 0;
                while (end + 1 < nameBytes.Length && (nameBytes[end] != 0 || nameBytes[end + 1] != 0)) end += 2;
                name = Encoding.Unicode.GetString(nameBytes[..end]);
            }
            else
            {
                size = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x0A, 4));
                var nameBytes = header[0x1E..];
                int end = nameBytes.IndexOf((byte)0);
                name = Encoding.UTF8.GetString(end < 0 ? nameBytes : nameBytes[..end]);
            }
            if (size > (ulong)(archive.Length - pos))
                throw new InvalidDataException($"ZAR archive: the file '{name}' (block at byte {block}) says it has {size} bytes, but {archive.Length - pos} remain.");
            var data = archive.AsSpan(pos, (int)size).ToArray();
            pos += (int)size;
            if (name.EndsWith(".LZW", StringComparison.OrdinalIgnoreCase))
            {
                data = Lzw(data);
                name = name[..^4];
            }
            entries.Add(new ZarEntry(name, data));
        }
        if (pos != archive.Length)
            throw new InvalidDataException($"ZAR archive: {archive.Length - pos} stray byte(s) at the end.");
        return entries;
    }

    /// <summary>Variable-width LZW as described above (zmxtools zar.py <c>_decompress_lzw</c>).</summary>
    internal static byte[] Lzw(byte[] compressed)
    {
        var words = new List<byte[]>(4096);
        for (int i = 0; i < 256; i++) words.Add(new[] { (byte)i });
        int width = 8;
        long bit = 0, totalBits = (long)compressed.Length * 8;
        byte[] previous = Array.Empty<byte>();
        using var output = new MemoryStream(compressed.Length * 3);
        while (true)
        {
            if ((1L << width) <= words.Count) width++;
            if (bit + width > totalBits) break;
            int code = 0;
            for (int b = 0; b < width; b++, bit++)
                code = (code << 1) | ((compressed[bit >> 3] >> (7 - (int)(bit & 7))) & 1);
            byte[] word;
            if (code < words.Count) word = words[code];
            else if (code == words.Count && previous.Length > 0) word = [.. previous, previous[0]];
            else throw new InvalidDataException($"ZAR archive: a compressed file holds the code {code}, past the dictionary's {words.Count} entries.");
            output.Write(word);
            if (previous.Length > 0) words.Add([.. previous, word[0]]);
            previous = word;
        }
        return output.ToArray();
    }
}
