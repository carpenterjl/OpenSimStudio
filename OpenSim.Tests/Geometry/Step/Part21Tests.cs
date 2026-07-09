using OpenSim.Geometry.Step;
using OpenSim.Geometry.Step.Part21;
using Xunit;

namespace OpenSim.Tests.Geometry.Step;

public class Part21Tests
{
    /// <summary>Wraps DATA-section text in a minimal valid exchange structure.</summary>
    private static string Wrap(string data) =>
        "ISO-10303-21;\nHEADER;\n" +
        "FILE_DESCRIPTION((''),'2;1');\n" +
        "FILE_NAME('model','2026-07-08',(''),(''),'pp','Test Exporter','');\n" +
        "FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 }'));\n" +
        "ENDSEC;\nDATA;\n" + data + "\nENDSEC;\nEND-ISO-10303-21;\n";

    [Fact]
    public void SimpleInstance_ParsesArgumentsAndHeader()
    {
        var file = Part21Parser.Parse(Wrap("#1=CARTESIAN_POINT('origin',(1.,2.5,-3.5E-2));"));

        Assert.Equal("AUTOMOTIVE_DESIGN", file.SchemaName);
        Assert.Equal("model", file.ModelName);
        Assert.Equal("Test Exporter", file.OriginatingSystem);

        var rec = file.Get(1).Single();
        Assert.Equal("CARTESIAN_POINT", rec.Keyword);
        Assert.Equal("origin", rec.Args[0].AsText());
        var coords = rec.Args[1].AsList();
        Assert.Equal(1.0, coords[0].AsReal());
        Assert.Equal(2.5, coords[1].AsReal());
        Assert.Equal(-3.5e-2, coords[2].AsReal());
    }

    [Fact]
    public void ValueKinds_NullDerivedEnumRefTypedNested_AllParse()
    {
        var rec = Part21Parser.Parse(Wrap(
            "#7=THING($,*,.MILLI.,#42,LENGTH_MEASURE(25.4),((1,2),()));")).Get(7).Single();

        Assert.True(rec.Args[0].IsNull);
        Assert.IsType<StepValue.Derived>(rec.Args[1]);
        Assert.Equal("MILLI", rec.Args[2].AsEnum());
        Assert.Equal(42, rec.Args[3].AsRef());
        var typed = Assert.IsType<StepValue.Typed>(rec.Args[4]);
        Assert.Equal("LENGTH_MEASURE", typed.Keyword);
        Assert.Equal(25.4, typed.Args[0].AsReal());
        var nested = rec.Args[5].AsList();
        Assert.Equal(2, nested[0].AsList().Count);
        Assert.Empty(nested[1].AsList());
    }

    [Fact]
    public void StringEscapes_QuoteBackslashAndUnicode_Decode()
    {
        var rec = Part21Parser.Parse(Wrap(
            @"#1=T('it''s a 100\\ test \X2\00E92764\X0\ \X\E9\ end');")).Get(1).Single();
        // '' → ', \\ → \, \X2\00E9 2764\X0\ → é❤ (UTF-16BE units), \X\E9\ → é (Latin-1).
        Assert.Equal("it's a 100\\ test é❤ é end", rec.Args[0].AsText());
    }

    [Fact]
    public void StringEscape_UnknownDirective_FailsWithLineNumber()
    {
        var ex = Assert.Throws<StepParseException>(() => Part21Parser.Parse(Wrap(@"#1=T('bad \Q\');")));
        Assert.Contains(@"'\Q'", ex.Message);
        Assert.Contains($"line {7 + 1}", ex.Message); // data line is line 8 of the wrapper
    }

    [Fact]
    public void ComplexInstance_RecordsLookedUpByKeyword_AnyOrder()
    {
        // Record order shuffled relative to what exporters write — order must not matter.
        var inst = Part21Parser.Parse(Wrap(
            "#11=(SI_UNIT(.MILLI.,.METRE.)NAMED_UNIT(*)LENGTH_UNIT());")).Get(11);

        Assert.True(inst.IsComplex);
        Assert.Equal(3, inst.Records.Count);
        Assert.True(inst.Has("LENGTH_UNIT"));
        Assert.Equal("METRE", inst.Find("SI_UNIT")!.Args[1].AsEnum());
        // Single() on a complex instance is a misuse — it must throw, naming the id.
        Assert.Contains("#11", Assert.Throws<StepImportException>(() => inst.Single()).Message);
    }

    [Fact]
    public void MissingSemicolon_FailsWithLineNumber()
    {
        var ex = Assert.Throws<StepParseException>(() =>
            Part21Parser.Parse(Wrap("#1=POINT('a')\n#2=POINT('b');")));
        Assert.Contains("line 9", ex.Message); // the '#' of #2 on the next line
    }

    [Fact]
    public void DuplicateInstanceId_FailsNamingBothLines()
    {
        var ex = Assert.Throws<StepParseException>(() =>
            Part21Parser.Parse(Wrap("#5=A(1);\n#5=B(2);")));
        Assert.Contains("duplicate instance #5", ex.Message);
        Assert.Contains("line 8", ex.Message);  // first definition
        Assert.Contains("line 9", ex.Message);  // duplicate
    }

    [Fact]
    public void Comments_AndWrappedStrings_KeepLineCountRight()
    {
        // A multi-line comment and an exporter-wrapped string precede the error.
        var ex = Assert.Throws<StepParseException>(() => Part21Parser.Parse(Wrap(
            "/* two\nline comment */\n#1=T('wrapped\nstring');\n#2=broken~;")));
        Assert.Contains("line 12", ex.Message);
    }

    [Fact]
    public void MissingReferencedInstance_ThrowsNamingTheId()
    {
        var file = Part21Parser.Parse(Wrap("#1=A(#99);"));
        Assert.Contains("#99", Assert.Throws<StepImportException>(() => file.Get(99)).Message);
    }

    [Fact]
    public void InstanceTable_EnumeratesInAscendingIdOrder()
    {
        var file = Part21Parser.Parse(Wrap("#30=C();\n#10=A();\n#20=B();"));
        Assert.Equal(new[] { 10, 20, 30 }, file.Instances.Keys);
    }

    // ---- real-file intake (soft-skip when the example is not present) ----

    internal static string? FindExampleStepFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Example_Model.step");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void ExampleModel_Parses_WithExpectedEntityCensus()
    {
        string? path = FindExampleStepFile();
        if (path is null) return; // example not present in this checkout

        var file = Part21Parser.ParseFile(path);

        Assert.Equal("AUTOMOTIVE_DESIGN", file.SchemaName);
        // Census from the Fusion 360 export: 65 ADVANCED_FACEs, one MANIFOLD_SOLID_BREP,
        // 7 complex instances (units + presentation contexts).
        int faces = file.Instances.Values.Count(i => i.Has("ADVANCED_FACE"));
        int solids = file.Instances.Values.Count(i => i.Has("MANIFOLD_SOLID_BREP"));
        int complex = file.Instances.Values.Count(i => i.IsComplex);
        Assert.Equal(65, faces);
        Assert.Equal(1, solids);
        Assert.Equal(7, complex);
    }
}
