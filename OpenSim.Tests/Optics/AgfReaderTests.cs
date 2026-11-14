using System.Text;
using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-11: the AGF reader on two committed fixtures. OP11_FIXTURE.agf is written for these
/// tests (its CC lines say how its printed values were made); TEST_CAT.agf is AGFFileReader.jl's
/// MIT test catalog (<c>test/TEST_CAT.agf</c>, licence beside it), whose expected field values
/// are AGFFileReader's own (<c>test/runtests.jl</c>, <c>TEST_CAT_VALUES</c>, lines 121–164).
/// </summary>
public sealed class AgfReaderTests
{
    internal static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Optics", "Fixtures", name);

    private static string FixtureText() => File.ReadAllText(FixturePath("OP11_FIXTURE.agf"));

    /// <summary>The fixture's text with one kind of line end, whatever git checked out.</summary>
    private static string WithLineEnds(string text, string end) => text.Replace("\r\n", "\n").Replace("\n", end);

    [Fact]
    public void TheFixture_ReadsEveryGlass_AndEveryFormulaNumber()
    {
        var catalog = AgfReader.Read(FixturePath("OP11_FIXTURE.agf"));
        Assert.Equal("OP11_FIXTURE", catalog.Name);
        Assert.Equal(21, catalog.Materials.Count);
        Assert.Equal(Enumerable.Range(1, 13), catalog.Materials.Take(13).Select(m => m.FormulaNumber!.Value));
        Assert.All(catalog.Materials, m => Assert.Equal("OP11_FIXTURE", m.Catalog));
    }

    [Fact]
    public void EveryEncodingAndLineEnd_ReadsTheSameCatalog()
    {
        string text = FixtureText();
        string reference = Describe(AgfReader.ParseText(WithLineEnds(text, "\n"), "F"));
        var variants = new Dictionary<string, byte[]>
        {
            ["ASCII, LF"] = Encoding.ASCII.GetBytes(WithLineEnds(text, "\n")),
            ["ASCII, CRLF"] = Encoding.ASCII.GetBytes(WithLineEnds(text, "\r\n")),
            ["UTF-8 with BOM, CRLF"] = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(WithLineEnds(text, "\r\n"))).ToArray(),
            ["UTF-16 LE with BOM, CRLF"] = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(WithLineEnds(text, "\r\n"))).ToArray(),
            ["UTF-16 LE with BOM, LF"] = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(WithLineEnds(text, "\n"))).ToArray(),
            ["UTF-16 LE without BOM"] = Encoding.Unicode.GetBytes(WithLineEnds(text, "\r\n")),
            ["UTF-16 BE with BOM"] = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray(),
        };
        foreach (var (what, bytes) in variants)
            Assert.True(reference == Describe(AgfReader.Parse(bytes, "F")), $"{what} reads differently.");
    }

    /// <summary>Every field of every glass, as text, so two reads compare in one string.</summary>
    private static string Describe(OpticalCatalog c) =>
        string.Join("\n", c.Comments) + "\n" + string.Join("\n", c.Notes) + "\n" + string.Join("\n", c.Materials.Select(m =>
            $"{m.Name}|{m.FormulaNumber}|{m.GlassCode}|{m.Nd}|{m.Vd}|{m.ExcludeSubstitution}|{m.StatusNumber}|{m.MeltFrequency}|{m.Comment}|" +
            $"{m.ThermalExpansion}|{m.ThermalExpansionHigh}|{m.Density}|{m.DeltaPgF}|{m.IgnoreThermalExpansion}|{string.Join(",", m.Coefficients)}|" +
            $"{m.Thermal}|{m.RelativeCost}|{m.ClimaticResistance}|{m.StainResistance}|{m.AcidResistance}|{m.AlkaliResistance}|{m.PhosphateResistance}|" +
            $"{m.MinMicrometers}|{m.MaxMicrometers}|{string.Join(",", m.Transmittance)}"));

    [Fact]
    public void FieldsAreReadInTheManualsOrder()
    {
        var catalog = AgfReader.Read(FixturePath("OP11_FIXTURE.agf"));
        var schott = catalog.Materials[0];
        Assert.Equal("SCHOTT-FORM", schott.Name);
        Assert.Equal("516641", schott.GlassCode);
        Assert.Equal("1.516372", schott.Nd!.Text);
        Assert.Equal(64.0608, schott.Vd!.Value);
        Assert.False(schott.ExcludeSubstitution);
        Assert.Equal(GlassStatus.Standard, schott.Status);
        Assert.Equal((int?)1, schott.MeltFrequency);
        Assert.Equal("Formula 1 (Schott)", schott.Comment);
        Assert.Equal((double?)7.1, schott.ThermalExpansion);
        Assert.Equal(8.2, schott.ThermalExpansionHigh);
        Assert.Equal(2.51, schott.Density);
        Assert.Equal(-0.0009, schott.DeltaPgF);
        Assert.False(schott.IgnoreThermalExpansion);
        Assert.Equal(new[] { 2.27, -0.0101, 0.0110, 0.00012, 0, 0, 0, 0, 0, 0 }, schott.Coefficients);
        Assert.Equal(new ThermalData(1.86e-6, 1.31e-8, -1.37e-11, 4.34e-7, 6.27e-10, 0.17, 20.0), schott.Thermal);
        Assert.Equal(new double?[] { 1.0, 2.0, 0.0, 1.0, 2.3, 2.3 },
            new[] { schott.RelativeCost, schott.ClimaticResistance, schott.StainResistance, schott.AcidResistance, schott.AlkaliResistance, schott.PhosphateResistance });
        Assert.Equal(new double?[] { 0.3, 2.5 }, new[] { schott.MinMicrometers, schott.MaxMicrometers });
        Assert.Equal(new[] { new TransmittancePoint(0.35, 0.92, 25), new TransmittancePoint(0.40, 0.992, 25), new TransmittancePoint(0.50, 0.994, null) },
            schott.Transmittance);
    }

    [Fact]
    public void WrappedLines_ContinueThePreviousRecord()
    {
        var bk7 = AgfReader.Read(FixturePath("OP11_FIXTURE.agf")).Find("SELLMEIER1-BK7")!;
        Assert.Equal(new[] { 1.03961212, 0.00600069867, 0.231792344, 0.0200179144, 1.01046945, 103.560653, 0, 0, 0, 0 }, bk7.Coefficients);
        Assert.Equal(new ThermalData(1.86e-6, 1.31e-8, -1.37e-11, 4.34e-7, 6.27e-10, 0.17, 20.0), bk7.Thermal);
        Assert.Equal("Formula 2 (Sellmeier 1) with the coefficients of AGFFileReader's Examples_N_BK7", bk7.Comment);
    }

    [Fact]
    public void AbsentMarks_EmptyTdLines_AndShortLines_ReadAsAbsent()
    {
        var catalog = AgfReader.Read(FixturePath("OP11_FIXTURE.agf"));
        var bk7 = catalog.Find("SELLMEIER1-BK7")!;
        Assert.Null(bk7.RelativeCost);                      // "_"
        Assert.Equal(1.0, bk7.ClimaticResistance);
        Assert.Null(bk7.AlkaliResistance);                  // "-"
        Assert.Null(bk7.PhosphateResistance);
        Assert.Null(bk7.MeltFrequency);                     // "-" on the NM line
        Assert.Equal(GlassStatus.Preferred, bk7.Status);

        var herzberger = catalog.Find("HERZBERGER-FORM")!;
        Assert.Null(herzberger.Thermal);                    // an empty TD line, as SCHOTT writes
        Assert.Null(herzberger.ThermalExpansion);           // an ED line of "-"
        Assert.Null(herzberger.Density);
        Assert.False(herzberger.IgnoreThermalExpansion);

        var sellmeier2 = catalog.Find("SELLMEIER2-FORM")!;  // no ED, TD, OD or IT lines at all
        Assert.Null(sellmeier2.Thermal);
        Assert.Null(sellmeier2.Density);
        Assert.Empty(sellmeier2.Transmittance);
        Assert.True(sellmeier2.ExcludeSubstitution);
        Assert.Equal(GlassStatus.Special, sellmeier2.Status);

        Assert.Equal((int?)5, catalog.Find("CONRADY-FORM")!.FormulaNumber);   // printed "5.0"
        Assert.Equal(GlassStatus.Melt, catalog.Find("CONRADY-FORM")!.Status);
        Assert.Equal("1.51680E+00", catalog.Find("BK7-ENOTATION")!.Nd!.Text);
    }

    [Fact]
    public void Comments_KeepTheFilesOrder_IncludingLeadingTextAndBangLines()
    {
        var comments = AgfReader.Read(FixturePath("OP11_FIXTURE.agf")).Comments;
        Assert.StartsWith("Fixture catalog for the OpenSim Studio AGF reader tests", comments[0]);
        Assert.StartsWith("Written for OpenSim.Tests", comments[1]);
        Assert.Equal("A line starting with an exclamation mark is a catalog comment.", comments[^1]);
        Assert.Equal(7, comments.Count);
    }

    [Fact]
    public void TheReaderNotes_Formula4_UnknownFormulas_SkippedLines_AndDuplicates()
    {
        var notes = AgfReader.Read(FixturePath("OP11_FIXTURE.agf")).Notes;
        Assert.Contains(notes, n => n.Contains("'SELLMEIER2-FORM'") && n.Contains("formula 4") && n.Contains("unverified"));
        Assert.Contains(notes, n => n.Contains("MD line was skipped"));
        Assert.Contains(notes, n => n.Contains("'SELLMEIER1-BK7' appears 2 times"));
        Assert.Contains(notes, n => n.Contains("'CAUCHY-FORM'") && n.Contains("-2"));
        Assert.Equal(4, notes.Count);
    }

    [Fact]
    public void DuplicateNames_AreKept_AndFindReturnsTheFirst()
    {
        var catalog = AgfReader.Read(FixturePath("OP11_FIXTURE.agf"));
        Assert.Equal(2, catalog.Materials.Count(m => m.Name == "SELLMEIER1-BK7"));
        Assert.Equal(GlassStatus.Preferred, catalog.Find("sellmeier1-bk7")!.Status);
    }

    [Fact]
    public void AMalformedNumber_FailsLoudly_NamingCatalogLineGlassAndField()
    {
        string text = "CC test\nNM GOOD 2 0 1.5 60 0 0 0\nNM BAD 2 0 1.5 60 0 0 0\nCD 1.0 0.01 abc 0 0 0\n";
        var ex = Assert.Throws<InvalidDataException>(() => AgfReader.ParseText(text, "broken"));
        Assert.Contains("Catalog 'broken', line 4", ex.Message);
        Assert.Contains("'BAD'", ex.Message);
        Assert.Contains("C3", ex.Message);
        Assert.Contains("'abc'", ex.Message);

        var early = Assert.Throws<InvalidDataException>(() => AgfReader.ParseText("CD 1 2 3\nNM A 2 0 1.5 60\n", "early"));
        Assert.Contains("before the first NM line", early.Message);

        var fraction = Assert.Throws<InvalidDataException>(() => AgfReader.ParseText("NM A 2.5 0 1.5 60\n", "f"));
        Assert.Contains("not a whole number", fraction.Message);

        var absent = Assert.Throws<InvalidDataException>(() => AgfReader.ParseText("NM A 2 0 1.5 60\nCD 1 - 3\n", "a"));
        Assert.Contains("cannot be absent", absent.Message);
    }

    /// <summary>AGFFileReader's test catalog against its own expected table (runtests.jl lines
    /// 121–164). Where AGFFileReader writes a missing value as a default, this reader keeps it
    /// as printed; the four glasses print every field, so the values agree.</summary>
    [Fact]
    public void AgfFileReadersTestCatalog_ReadsToItsOwnExpectedValues()
    {
        var catalog = AgfReader.Read(FixturePath("TEST_CAT.agf"));
        Assert.Equal(new[] { "MG463&", "MG523", "5MG448", "MG452*" }, catalog.Materials.Select(m => m.Name));
        Assert.All(catalog.Materials, m =>
        {
            Assert.Equal(DispersionFormula.Sellmeier1, m.Formula);
            Assert.Equal(1.0, m.Nd!.Value);
            Assert.Equal(0.0, m.Vd!.Value);
            Assert.Equal((int?)-1, m.MeltFrequency);
            Assert.Equal(0.0, m.DeltaPgF);
            Assert.Equal(new double?[] { 2.0, 14.0 }, new[] { m.MinMicrometers, m.MaxMicrometers });
            Assert.Equal(10, m.Coefficients.Count);
            Assert.All(m.Coefficients.Skip(6), c => Assert.Equal(0.0, c));
        });
        var first = catalog.Materials[0];
        Assert.True(first.ExcludeSubstitution);                 // printed 5
        Assert.Equal((int?)6, first.StatusNumber);                     // not one of 0–4: kept, and noted
        Assert.Null(first.Status);
        Assert.Contains(catalog.Notes, n => n.Contains("'MG463&' has status 6"));
        Assert.True(first.IgnoreThermalExpansion);               // printed 3
        Assert.Equal(new[] { 19.8, 18.3, 30.2, 25.7 }, catalog.Materials.Select(m => m.ThermalExpansion!.Value));
        Assert.Equal(new[] { 4.63, 5.23, 4.48, 4.52 }, catalog.Materials.Select(m => m.Density!.Value));
        Assert.Equal(new[] { 6.781409960E+00, 9.868538020E-02, 3.469529960E-03, 2.277823700E+00, 2.126055280E+00, 3.227490100E+03 },
            first.Coefficients.Take(6));
        Assert.Equal(new[] { -2.545621409E-07, 1.258087492E-10, 1.119703940E-11, 2.184273853E-05, -2.788674395E-09, -1.948154058E+00, 2.000000000E+01 },
            new[] { catalog.Materials[3].Thermal! }.SelectMany(t => new[] { t.D0, t.D1, t.D2, t.E0, t.E1, t.LambdaTk, t.ReferenceTemperatureCelsius }));
        Assert.Equal(20.02, first.Thermal!.ReferenceTemperatureCelsius);
        Assert.Equal(new double?[] { 1.1, 1.2, 1.3, 1.4, 1.5, 1.6 },
            new[] { first.RelativeCost, first.ClimaticResistance, first.StainResistance, first.AcidResistance, first.AlkaliResistance, first.PhosphateResistance });
        Assert.Equal(-1.0, catalog.Materials[1].RelativeCost);  // printed −1: kept as printed
        Assert.Equal(new[] { new TransmittancePoint(2.0, 1.0, 3.0), new TransmittancePoint(14.0, 4.0, 5.0) }, first.Transmittance);
        Assert.Equal("Direct replacement for Schott IRG26 or Vitran IG6", first.Comment);
        Assert.Equal("Updated September 28th 2018", catalog.Comments.Single());
    }
}
