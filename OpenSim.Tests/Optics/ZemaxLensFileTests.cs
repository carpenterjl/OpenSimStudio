using System.Text;
using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-94: Zemax lens files (docs/Optics-Conventions.md section 52). The lens files here are
/// written in the tests from the keyword rules section 52 lists; none is copied from a vendor.
/// </summary>
public sealed class ZemaxLensFileTests
{
    private readonly ITestOutputHelper _out;

    public ZemaxLensFileTests(ITestOutputHelper output) => _out = output;

    private static OpticsSurfaceParameterSettings P(int number, double value) => new() { Number = number, Value = value };

    private static OpticsCellFlagSettings Pickup(string column, int from, double scale = 1) =>
        new() { Column = column, Kind = SavedSurfaces.PickupFlag, PickupSurface = from, PickupScale = scale };

    private static OpticsCellFlagSettings Variable(string column) => new() { Column = column, Kind = SavedSurfaces.VariableFlag };

    /// <summary>
    /// A lens using every surface type the file subset holds (Standard, Even, Odd, Extended,
    /// Extended Odd and Q-Type aspheres, Toroidal, Coordinate Break, Paraxial, a mirror), every
    /// solve it holds (curvature, thickness, material, semi-diameter and parameter pickups;
    /// curvature, thickness and parameter variables), each kind of medium (catalog name, model
    /// glass, constant index, mirror, air), circular and floating apertures with a decenter,
    /// three vignetted fields and three weighted wavelengths. The numbers are inputs, not
    /// reference values. Parameter numbers follow the layouts of <see cref="SurfaceTypes"/>, with
    /// flags in the reader's canonical order (radius, thickness, material, semi-diameter, parameters).
    /// </summary>
    internal static OpticsSettings EverySupportedFeature() => new()
    {
        Mode = OpticsModes.SavedName(OpticsMode.Sequential),
        Materials = new OpticsMaterialsSettings { ActiveCatalogs = new() { "SCHOTT", "OHARA" } },
        SystemData = new OpticsSystemSettings
        {
            Title = "OP-94 round trip",
            Notes = string.Concat(Enumerable.Repeat("A note long enough to need three NOTE lines. ", 10)),
            LensUnit = ConventionNames.LensUnits.SavedName(LensUnit.Millimeter),
            ApertureType = ApertureTypes.Names.SavedName(ApertureType.EntrancePupilDiameter),
            ApertureValue = 25,
            TelecentricObjectSpace = false,
            AfocalImageSpace = false,
            FieldType = FieldTypes.Names.SavedName(FieldType.Angle),
            Fields = new()
            {
                new() { X = 0, Y = 0 },
                new() { X = 0, Y = 7, Weight = 2, DecenterY = 0.05, CompressionY = 0.1 },
                new() { X = 1.5, Y = 10, Weight = 0.5, DecenterX = -0.02, CompressionX = 0.2, TiltDegrees = 3 },
            },
            Wavelengths = new() { new() { Micrometers = 0.4861327, Weight = 1 }, new() { Micrometers = 0.5875618, Weight = 2 }, new() { Micrometers = 0.6562725, Weight = 1 } },
            PrimaryWavelength = 2,
            TemperatureCelsius = 25,
            PressureAtmospheres = 0.9,
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Comment = "front", Radius = 50, Thickness = 5, Material = "N-BK7", Coating = "AR", SemiDiameter = 12.5, Conic = -0.5 },
            new()
            {
                Type = "Even Asphere", Radius = -80, Thickness = 2, IsStop = true,
                Parameters = new() { P(2, 1e-6), P(3, -2e-9) },
                Aperture = new OpticsSurfaceApertureSettings { Type = SavedSurfaces.CircularAperture, MinRadius = 0, MaxRadius = 11, DecenterX = 0.1, DecenterY = -0.2 },
                Flags = new() { Variable(SavedSurfaces.RadiusColumn), Variable(SavedSurfaces.ThicknessColumn) },
            },
            new()
            {
                Type = "Even Asphere", Radius = 80, Thickness = 5, Material = "N-BK7", SemiDiameter = 12.5,
                Parameters = new() { P(2, 1e-6) },
                Flags = new()
                {
                    Pickup(SavedSurfaces.RadiusColumn, 2, -1), Pickup(SavedSurfaces.ThicknessColumn, 1), Pickup(SavedSurfaces.MaterialColumn, 1),
                    Pickup(SavedSurfaces.SemiDiameterColumn, 1), Pickup(SavedSurfaces.ParameterColumn(2), 2),
                },
            },
            new()
            {
                Type = "Odd Asphere", Radius = -60, Thickness = 3, ModelGlass = new OpticsModelGlassSettings { Nd = 1.6, Vd = 40, DeltaPgF = 0.001 },
                Parameters = new() { P(1, 1e-4), P(3, 1e-7) },
            },
            new()
            {
                Type = "Extended Asphere", Radius = 100, Thickness = 4, Index = 1.5,
                ExtraData = new() { P(13, 3), P(14, 10), P(15, 0.001), P(16, -0.0005), P(17, 0.0001) },
            },
            new()
            {
                Type = "Extended Odd Asphere", Radius = -100, Thickness = 2,
                ExtraData = new() { P(13, 2), P(14, 10), P(15, 1e-3), P(16, 2e-4) },
            },
            new()
            {
                Type = "Q-Type Asphere", Radius = 200, Thickness = 3, Material = "N-SF5",
                ExtraData = new() { P(13, 1), P(14, 3), P(15, 12), P(16, 1e-3), P(17, -2e-4), P(18, 5e-5) },
            },
            new()
            {
                Type = "Toroidal", Radius = 150, Thickness = 4,
                Parameters = new() { P(0, 1), P(1, 75), P(2, 1e-5) },
                ExtraData = new() { P(13, 3), P(14, 10), P(15, 0.001) },
            },
            new()
            {
                Type = SurfaceTypes.CoordinateBreak, Thickness = 0,
                Parameters = new() { P(1, 0.5), P(2, 0.25), P(3, 1.5), P(6, 1) },
                Flags = new() { Variable(SavedSurfaces.ParameterColumn(2)) },
            },
            new()
            {
                Type = "Paraxial", Thickness = 10, SemiDiameter = 6,
                Parameters = new() { P(1, 50), P(2, 3) },
                Aperture = new OpticsSurfaceApertureSettings { Type = SavedSurfaces.FloatingAperture },
            },
            new() { Radius = -300, Thickness = -20, Material = SavedSurfaces.Mirror },
            new() { Thickness = 0 },
        },
    };

    /// <summary>
    /// Every number but the radii must come back bit for bit ("R" writes the shortest text that
    /// reads back to the same double). A radius is written as the curvature 1/R and read back as
    /// 1/c: two correctly rounded divisions, each with relative error at most 2⁻⁵³, so the radius
    /// moves by at most (1 + 2⁻⁵³)/(1 − 2⁻⁵³) − 1 ≈ 2⁻⁵² relative (derived); 2⁻⁵¹ is allowed.
    /// </summary>
    internal static void AssertSameLens(OpticsSettings expected, OpticsSettings actual)
    {
        var e = expected.Surfaces!;
        var a = actual.Surfaces!;
        Assert.Equal(e.Count, a.Count);
        double bound = Math.Pow(2, -51);
        for (int j = 0; j < e.Count; j++)
        {
            if (e[j].Radius is double r) Assert.True(a[j].Radius is double r2 && Math.Abs(r2 - r) <= bound * Math.Abs(r), $"surface {j}: radius {r:R} came back as {a[j].Radius:R}.");
            else Assert.Null(a[j].Radius);
        }
        static string Json(OpticsSettings s) => JsonSerializer.Serialize(s with { Surfaces = s.Surfaces!.Select(x => x with { Radius = null }).ToList() },
            new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(Json(expected), Json(actual));
    }

    /// <summary>OP-94's gate, first half: export then import is the identity on the supported subset.</summary>
    [Fact]
    public void ExportThenImport_IsTheIdentity_OverEverySupportedSurfaceTypeAndSolve()
    {
        var lens = EverySupportedFeature();
        var written = ZemaxLensWriter.Write(lens);
        Assert.Empty(written.Warnings);
        _out.WriteLine(written.Text);
        var read = ZemaxLensReader.Read(written.Text, catalogs: null);
        Assert.Empty(read.Warnings);
        AssertSameLens(lens, read.Optics);

        // Through the bytes on disk too: UTF-16 LE with a byte-order mark.
        string path = Path.Combine(Path.GetTempPath(), $"op94-{Guid.NewGuid():N}.zmx");
        try
        {
            ZemaxLensFiles.Export(lens, path);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xFE, bytes[1]);
            AssertSameLens(lens, ZemaxLensFiles.Import(path, catalogs: null).Optics);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EveryTypeCode_ReadsBackAsItsType()
    {
        foreach (var (code, type) in ZemaxKeywords.SurfaceTypes)
        {
            Assert.True(ZemaxKeywords.TryTypeName(code, out string? read));
            Assert.Equal(type, read);
            Assert.Equal(code, ZemaxKeywords.CodeOf(type));
            if (type is not null) Assert.NotNull(SurfaceTypes.Find(type));
        }
    }

    private const string Minimal =
        "VERS 1\nMODE SEQ\nNAME Minimal\nUNIT MM X W X CM MR CPMM\nENPD 10\nFTYP 0 0 1 1 0 0 0\nXFLN 0\nYFLN 0\nFWGN 1\nWAVM 1 0.55 1\nPWAV 1\n" +
        "SURF 0\n  TYPE STANDARD\n  CURV 0 0 0 0 0 \"\"\n  DISZ INFINITY\n" +
        "SURF 1\n  TYPE STANDARD\n  CURV 0.01 0 0 0 0 \"\"\n  DISZ 5\n" +
        "SURF 2\n  TYPE STANDARD\n  DISZ 0\n";

    private const string WithUnsupported =
        "VERS 1\nMODE SEQ\nNAME T\nUNIT MM X W X CM MR CPMM\nENPD 10\nFTYP 0 0 1 1 0 0 0\nXFLN 0\nYFLN 0\nFWGN 1\nWAVM 1 0.55 1\nPWAV 1\n" +
        "SURF 0\n  TYPE STANDARD\n  CURV 0 0 0 0 0 \"\"\n  DISZ INFINITY\n" +
        "SURF 1\n  TYPE BICONICX\n  CURV 0.01 2 0 0 0 \"\"\n  DISZ 5\n  FOOO 1\n" +
        "SURF 2\n  TYPE STANDARD\n  DISZ 0\n" +
        "EFFL 0 2 0 0 0 0 100 1\n";

    [Fact]
    public void UnsupportedContent_FailsLoudly_NamingEachKeyword_OrIsListedAsWarnings()
    {
        var ex = Assert.Throws<ZemaxFileException>(() => ZemaxLensReader.Read(WithUnsupported, null));
        Assert.Equal(4, ex.Problems.Count);
        Assert.Contains(ex.Problems, p => p.Contains("Line 17") && p.Contains("TYPE BICONICX") && p.Contains("surface 1"));
        Assert.Contains(ex.Problems, p => p.Contains("Line 18") && p.Contains("CURV") && p.Contains("solve code 2"));
        Assert.Contains(ex.Problems, p => p.Contains("Line 20") && p.Contains("FOOO"));
        Assert.Contains(ex.Problems, p => p.Contains("Line 24") && p.Contains("EFFL") && p.Contains("after the surfaces"));

        var lenient = ZemaxLensReader.Read(WithUnsupported, null, new ZemaxImportOptions { UnsupportedAsWarnings = true });
        Assert.Equal(ex.Problems, lenient.Warnings);
        Assert.Equal(3, lenient.Optics.Surfaces!.Count);
        Assert.Contains(lenient.Notes, n => n.StartsWith("VERS: not read"));
    }

    [Fact]
    public void Utf16AndUtf8_ReadTheSame()
    {
        var a = ZemaxLensReader.Read(ZemaxText.Decode(ZemaxText.Encode(Minimal)).Text, null);
        var (text, encoding) = ZemaxText.Decode(Encoding.UTF8.GetBytes(Minimal));
        Assert.Equal("UTF-8", encoding);
        var b = ZemaxLensReader.Read(text, null);
        var (text16, encoding16) = ZemaxText.Decode(Encoding.Unicode.GetBytes(Minimal));   // no byte-order mark
        Assert.StartsWith("UTF-16 LE", encoding16);
        var c = ZemaxLensReader.Read(text16, null);
        Assert.Equal(JsonSerializer.Serialize(a.Optics), JsonSerializer.Serialize(b.Optics));
        Assert.Equal(JsonSerializer.Serialize(a.Optics), JsonSerializer.Serialize(c.Optics));
        Assert.Equal(100, a.Optics.Surfaces![1].Radius!.Value, 12);
    }

    [Fact]
    public void AGlassNotInTheCatalogs_Fails_UnlessSubstituted()
    {
        var catalog = AgfReader.Read(AgfReaderTests.FixturePath("TEST_CAT.agf"));
        string text = Minimal.Replace("  DISZ 5\n", "  DISZ 5\n  GLAS NOPE 0 0 1.5 50 0 0 0 0 0 0\n");
        var ex = Assert.Throws<ZemaxFileException>(() => ZemaxLensReader.Read(text, new[] { catalog }));
        Assert.Contains(ex.Problems, p => p.Contains("GLAS NOPE") && p.Contains("surface 1"));

        var sub = ZemaxLensReader.Read(text, new[] { catalog }, new ZemaxImportOptions { GlassSubstitutions = new Dictionary<string, string> { ["nope"] = "MG523" } });
        Assert.Equal("MG523", sub.Optics.Surfaces![1].Material);
        Assert.Contains(sub.Notes, n => n.Contains("'NOPE'") && n.Contains("MG523"));
    }

    [Fact]
    public void Export_RefusesWhatTheSubsetCannotHold_ListingEveryItem()
    {
        var lens = EverySupportedFeature();
        var surfaces = lens.Surfaces!.ToList();
        surfaces[1] = surfaces[1] with { TiltBefore = new OpticsTiltDecenterSettings { TiltX = 1 } };
        surfaces[2] = surfaces[2] with { Aperture = new OpticsSurfaceApertureSettings { Type = SavedSurfaces.RectangularAperture, HalfWidthX = 1, HalfWidthY = 2 } };
        surfaces[4] = surfaces[4] with { Type = "Biconic" };
        lens = lens with
        {
            Surfaces = surfaces,
            MeritFunction = new OpticsMeritFunctionSettings { Operands = new() { new OpticsMeritOperandSettings() } },
        };
        var ex = Assert.Throws<ZemaxFileException>(() => ZemaxLensWriter.Write(lens));
        Assert.Equal(4, ex.Problems.Count);
        Assert.Contains(ex.Problems, p => p.StartsWith("Surface 1") && p.Contains("tilt/decenter before"));
        Assert.Contains(ex.Problems, p => p.StartsWith("Surface 2") && p.Contains("Rectangular"));
        Assert.Contains(ex.Problems, p => p.StartsWith("Surface 4") && p.Contains("Biconic"));
        Assert.Contains(ex.Problems, p => p.StartsWith("Merit function"));

        var lenient = ZemaxLensWriter.Write(lens, new ZemaxExportOptions { UnsupportedAsWarnings = true });
        Assert.Equal(ex.Problems, lenient.Warnings);
    }

    // ------------------------------------------------------------------ the archive

    /// <summary>LZW as the reader decodes it (<see cref="ZarArchive"/>): the encoder emits the code
    /// for the longest known prefix and adds prefix + next byte; the width is the decoder's, which
    /// before reading code i has 256 + max(0, i − 1) entries and widens when 2^width ≤ entries.</summary>
    private static byte[] Lzw(byte[] data)
    {
        var dict = new Dictionary<string, int>();
        for (int i = 0; i < 256; i++) dict[((char)i).ToString()] = i;
        var codes = new List<int>();
        string w = "";
        foreach (byte b in data)
        {
            string wc = w + (char)b;
            if (dict.ContainsKey(wc)) { w = wc; continue; }
            codes.Add(dict[w]);
            dict[wc] = dict.Count;
            w = ((char)b).ToString();
        }
        if (w.Length > 0) codes.Add(dict[w]);
        var bits = new List<bool>();
        int width = 8;
        for (int i = 0; i < codes.Count; i++)
        {
            int entries = 256 + Math.Max(0, i - 1);
            if ((1 << width) <= entries) width++;
            for (int k = width - 1; k >= 0; k--) bits.Add(((codes[i] >> k) & 1) == 1);
        }
        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }

    /// <summary>One archive block in the layout <see cref="ZarArchive"/> documents.</summary>
    private static byte[] Block(string name, byte[] contents, bool later)
    {
        int header = later ? 0x288 - 2 : 0x14C - 2;
        var block = new byte[2 + header + contents.Length];
        block[0] = later ? (byte)0xEC : (byte)0xEA;
        block[1] = later ? (byte)0x03 : (byte)0x00;
        if (later)
        {
            BitConverter.GetBytes((ulong)contents.Length).CopyTo(block, 2 + 0x0E);
            Encoding.Unicode.GetBytes(name).CopyTo(block, 2 + 0x2E);
        }
        else
        {
            BitConverter.GetBytes((uint)contents.Length).CopyTo(block, 2 + 0x0A);
            Encoding.UTF8.GetBytes(name).CopyTo(block, 2 + 0x1E);
        }
        contents.CopyTo(block, 2 + header);
        return block;
    }

    [Fact]
    public void Lzw_DecodesWhatTheDocumentedEncoderWrites_IncludingTheSelfReferenceCase()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ABABABA TOBEORNOTTOBEORTOBEORNOT ", 400)) + "AAAAAAAAAAAAAAAAAAAA");
        Assert.Equal(data, ZarArchive.Lzw(Lzw(data)));
    }

    [Fact]
    public void AnArchive_GivesItsLens_AndChecksGlassesAgainstItsCatalogs()
    {
        string lens = Minimal.Replace("  DISZ 5\n", "  DISZ 5\n  GLAS MG523 0 0 1.5 50 0 0 0 0 0 0\n").Replace("PWAV 1\n", "PWAV 1\nGCAT TEST_CAT\n");
        byte[] agf = File.ReadAllBytes(AgfReaderTests.FixturePath("TEST_CAT.agf"));
        var archive = Block("LENS.ZMX.LZW", Lzw(ZemaxText.Encode(lens)), later: true)
            .Concat(Block("TEST_CAT.AGF", agf, later: false))
            .Concat(Block("LENS.SES", new byte[] { 1, 2, 3 }, later: false)).ToArray();

        var entries = ZarArchive.Read(archive);
        Assert.Equal(new[] { "LENS.ZMX", "TEST_CAT.AGF", "LENS.SES" }, entries.Select(e => e.Name));
        Assert.Equal(agf, entries[1].Contents);

        var read = ZemaxLensFiles.ImportArchive(archive, "lens.zar", catalogs: Array.Empty<OpticalCatalog>());
        Assert.Equal("MG523", read.Optics.Surfaces![1].Material);
        Assert.Equal("TEST_CAT", Assert.Single(read.ArchiveCatalogs).Name);
        Assert.Contains(read.Notes, n => n.Contains("LENS.SES") && n.Contains("not read"));
        Assert.Equal(JsonSerializer.Serialize(ZemaxLensReader.Read(lens, new[] { read.ArchiveCatalogs[0] }).Optics), JsonSerializer.Serialize(read.Optics));
    }

    [Fact]
    public void AnArchiveBlock_OfAnUnknownLayout_FailsNamingTheByte()
    {
        var archive = Block("A.ZMX", ZemaxText.Encode(Minimal), later: true);
        archive[0] = 0xAB;
        var ex = Assert.Throws<InvalidDataException>(() => ZarArchive.Read(archive));
        Assert.Contains("0xAB", ex.Message);
    }
}
