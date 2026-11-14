using OpenSim.Optics.Materials;
using OpenSim.Optics;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-12: the refractiveindex.info page reader and its YAML subset, on small inline pages. The
/// layout (REFERENCES, COMMENTS, DATA blocks with type, wavelength_range, coefficients and data,
/// CONDITIONS, PROPERTIES) is the database's own (database/data/…, plans/sources/optics/riidb);
/// the numbers in the made-up pages are chosen for the test.
/// </summary>
public sealed class RefractiveIndexInfoReaderTests
{
    [Fact]
    public void ATabulatedNkPage_GivesTheTable_WithK_AndItsRange()
    {
        var m = RefractiveIndexInfoReader.Read("""
            # this file is part of refractiveindex.info database
            REFERENCES: |
                A. Author.
                <a href="https://doi.org/x"><i>J. Test</i> <b>1</b>, 2 (2000)</a><br>
                * a note
            COMMENTS: |
                Room temperature.
            DATA:
              - type: tabulated nk
                data: |
                    0.5 1.50 0.0
                    1.0 1.40 0.2
                    2.0 1.30 0.4
            CONDITIONS:
                temperature: 293.15
            """, "main/X/nk/Test.yml", "X-Test", "RII-MAIN", "X (Test material)", "Test 2000: n,k 0.5–2 µm");

        Assert.Equal(FormulaNumbering.RefractiveIndexInfo, m.Numbering);
        Assert.Null(m.FormulaNumber);
        Assert.True(m.Tabulated!.HasExtinction);
        Assert.Equal((0.5, 2.0), m.ValidRange);
        Assert.Equal(1.45, m.RelativeIndex(0.75), 1e-15);            // linear between 1.50 and 1.40
        Assert.Equal(0.3, m.ExtinctionCoefficient(1.5)!.Value, 1e-15);
        Assert.Equal((0.5, 2.0), m.ExtinctionRange);
        Assert.Equal("Room temperature.", m.Comment);

        var s = m.Source!;
        Assert.Equal("main/X/nk/Test.yml", s.PagePath);
        Assert.Equal("RII-MAIN", m.Catalog);
        Assert.StartsWith("A. Author.\n<a href=", s.References);
        Assert.Equal("A. Author.\nJ. Test 1, 2 (2000)\n* a note", s.PlainReferences);
        Assert.Equal(new KeyValuePair<string, string>("temperature", "293.15"), Assert.Single(s.Conditions));
        Assert.Contains("page main/X/nk/Test.yml", s.Citation);
    }

    [Fact]
    public void ATabulatedNPage_HasNoK_AndAFormulaWithTabulatedK_KeepsBothRanges()
    {
        var nOnly = RefractiveIndexInfoReader.Read("""
            REFERENCES: |
                R.
            DATA:
              - type: tabulated n
                data: |
                    1.2 3.5167
                    1.22 3.5133
            """, "p.yml", "N");
        Assert.False(nOnly.Tabulated!.HasExtinction);
        Assert.Null(nOnly.ExtinctionCoefficient(1.21));
        Assert.Null(nOnly.ExtinctionRange);
        Assert.Equal(3.5167, nOnly.RelativeIndex(1.2));

        // The layout of database/data/glass/misc/soda-lime/nk/Rubin-clear.yml: formula 5 over
        // 0.31–4.6 µm and a k table; here the k table is narrower than the formula's range.
        var both = RefractiveIndexInfoReader.Read("""
            REFERENCES: |
                R.
            NOTES: |
                The develop branch's name for COMMENTS.
            DATA:
              - type: formula 5
                wavelength_range: 0.31 4.6
                coefficients: 1.5 0.01 -2
              - type: tabulated k
                data: |
                    0.40 1e-6
                    0.50 3e-6
            """, "q.yml", "F");
        Assert.Equal((0.31, 4.6), both.ValidRange);
        Assert.Equal((0.40, 0.50), both.ExtinctionRange);
        Assert.Equal(1.5 + 0.01 / (2.0 * 2.0), both.RelativeIndex(2.0), 1e-15);   // n = C1 + C2 λ^C3
        Assert.Equal(2e-6, both.ExtinctionCoefficient(0.45)!.Value, 1e-18);
        var outside = Assert.Throws<ArgumentOutOfRangeException>(() => both.ExtinctionCoefficient(1.0));
        Assert.Contains("outside the k table's range 0.4–0.5 µm", outside.Message);
        Assert.Equal("The develop branch's name for COMMENTS.", both.Comment);
    }

    [Theory]
    [InlineData("""
        REFERENCES: |
            R.
        DATA:
          - type: tabulated n2
            data: |
                0.8 3.30e-20
                2.0 3.50e-20
        """, "nonlinear index n2")]
    [InlineData("""
        REFERENCES: |
            R.
        DATA:
          - type: tabulated k
            data: |
                0.5 0.1
                0.6 0.1
        """, "gives no refractive index")]
    [InlineData("""
        REFERENCES: |
            R.
        DATA:
          - type: formula 2
            wavelength_range: 0.3 2
            coefficients: 0 1 0
          - type: tabulated n
            data: |
                0.5 1.5
                0.6 1.5
        """, "gives n twice")]
    [InlineData("""
        REFERENCES: |
            R.
        DATA:
          - type: tabulated nk
            data: |
                0.5 1.5 0
                0.6 1.5 0
          - type: tabulated k
            data: |
                0.5 0.1
                0.6 0.1
        """, "gives k twice")]
    [InlineData("""
        REFERENCES: |
            R.
        DATA:
          - type: tabulated nk
            data: |
                0.5 1.5 0
                0.5 1.4 0
        """, "must increase")]
    [InlineData("""
        REFERENCES: |
            R.
        DATA:
          - type: tabulated nk
            data: |
                0.5 1.5 -0.1
                0.6 1.4 0
        """, "k must be zero or positive")]
    [InlineData("""
        REFERENCES: |
            R.
        DATA:
          - type: formula 1
            wavelength_range: 0.3 2
            coefficients: 0 1 0
            temperature: 20
        """, "'temperature' is not read")]
    [InlineData("""
        REFERENCES: |
            R.
        DATA:
          - type: formula 1
            wavelength_range: 2 0.3
            coefficients: 0 1 0
        """, "wavelength_range must be two wavelengths")]
    [InlineData("""
        REFERENCES: |
            R.
        DATA:
          - type: tabulated nk
            data: |
                0.5 1.5
                0.6 1.4 0
        """, "line 6: a row of 3 numbers was expected")]
    [InlineData("""
        NAMES:
          - BK7
        ABOUT: |
            An about.yml file.
        """, "has no DATA block")]
    public void APageOutsideTheSubset_IsRefused_NamingWhy(string yaml, string expected)
    {
        var e = Assert.Throws<InvalidDataException>(() => RefractiveIndexInfoReader.Read(yaml, "bad.yml", "B"));
        Assert.Contains(expected, e.Message);
        Assert.StartsWith("bad.yml", e.Message);
    }

    /// <summary>
    /// A page whose PROPERTIES state nd and Vd can be checked by OP-11's self-check. This is the
    /// database's copy of SCHOTT N-BK7 (database/data/specs/schott/optical/N-BK7.yml, which
    /// cites the SCHOTT Zemax catalog 2017-01-20b), shortened to the lines the test needs; its
    /// coefficients, range, CONDITIONS and PROPERTIES lines are copied as the file prints them,
    /// including the nested thermal_dispersion list. nd 1.5168 and Vd 64.17 are the file's own.
    /// </summary>
    [Fact]
    public void APageStatingNdAndVd_PassesTheCatalogSelfCheck()
    {
        var m = RefractiveIndexInfoReader.Read("""
            REFERENCES: |
                <a href="https://refractiveindex.info/download/data/2017/schott_2017-01-20b.agf">SCHOTT Zemax catalog 2017-01-20b</a>
            COMMENTS: |
                step 0.5 available
            DATA:
              - type: formula 2
                wavelength_range: 0.3 2.5
                coefficients: 0 1.03961212 0.00600069867 0.231792344 0.0200179144 1.01046945 103.560653
            CONDITIONS:
                temperature: 293
            PROPERTIES:
                thermal_dispersion:
                  - type: formula A
                    coefficients: 1.86e-06 1.31e-08 -1.37e-11 4.34e-07 6.27e-10 0.17
                nd: 1.5168
                Vd: 64.17
                glass_code: 517642.251
                density:
                  - value: 2510
            """, "specs/schott/optical/N-BK7.yml", "N-BK7");

        Assert.Equal("1.5168", m.Nd!.Text);
        Assert.Equal("64.17", m.Vd!.Text);
        var check = CatalogSelfCheck.Check(m);
        Assert.Equal(GlassCheckOutcome.Passed, check.Outcome);
        Assert.Equal(1.5168, m.RelativeIndex(SpectralLines.LowerD.Micrometers), 5e-5);   // half the last printed digit

        var properties = m.Source!.Properties.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("formula A", properties["thermal_dispersion.1.type"]);
        Assert.Equal("2510", properties["density.1.value"]);
        Assert.Null(m.Thermal);                                       // formula A is kept as text, not used
    }

    [Fact]
    public void APageWithoutNd_IsNotChecked_WithTheReasonGiven()
    {
        var m = RefractiveIndexInfoReader.Read("""
            REFERENCES: |
                R.
            DATA:
              - type: formula 1
                wavelength_range: 0.3 2
                coefficients: 0 1 0
            """, "p.yml", "P");
        var check = CatalogSelfCheck.Check(m);
        Assert.Equal(GlassCheckOutcome.NotChecked, check.Outcome);
        Assert.Contains("states no nd and Vd", check.Reason);
    }
}

/// <summary>OP-12: the YAML subset the reader is built on.</summary>
public sealed class YamlSubsetTests
{
    [Fact]
    public void BlockScalars_QuotedScalars_Comments_AndCompactSequenceItems()
    {
        var root = (YamlMapping)YamlSubset.Parse("""
            ---
            # a comment
            A: plain text # trailing comment
            B: "double \"quoted\" \u00b5m"
            C: 'single ''quoted'''
            D: |
                line one
                  indented

                after a blank
            E: >
                folded
                lines

                paragraph
            F:
              - x: 1
                y: 2
              - plain item
            G:
            - at the key's indentation
            """, "t.yml")!;

        Assert.Equal("plain text", ((YamlScalar)root["A"]!).Value);
        Assert.Equal("double \"quoted\" µm", ((YamlScalar)root["B"]!).Value);
        Assert.Equal("single 'quoted'", ((YamlScalar)root["C"]!).Value);
        Assert.Equal("line one\n  indented\n\nafter a blank\n", ((YamlScalar)root["D"]!).Value);
        Assert.Equal("folded lines\nparagraph\n", ((YamlScalar)root["E"]!).Value);
        var f = (YamlSequence)root["F"]!;
        var first = (YamlMapping)f.Items[0];
        Assert.Equal("2", ((YamlScalar)first["y"]!).Value);
        Assert.Equal("plain item", ((YamlScalar)f.Items[1]).Value);
        Assert.Equal("at the key's indentation", ((YamlScalar)((YamlSequence)root["G"]!).Items[0]).Value);
    }

    [Theory]
    [InlineData("A: [1, 2]", "line 1: flow collections")]
    [InlineData("A: &anchor x", "line 1: anchors, aliases and tags")]
    [InlineData("A: \"open", "line 1: a double-quoted scalar is not closed")]
    [InlineData("A:\n\t- x", "line 2: a tab in the indentation")]
    [InlineData("A: 1\nA: 2", "line 2: the key 'A' appears twice")]
    [InlineData("A: 1\n  B: 2", "line 2: unexpected indentation")]
    [InlineData("just text", "line 1: expected 'key: value'")]
    public void ConstructsOutsideTheSubset_AreRefused_NamingTheLine(string yaml, string expected)
    {
        var e = Assert.Throws<InvalidDataException>(() => YamlSubset.Parse(yaml, "t.yml"));
        Assert.Contains("t.yml " + expected, e.Message);
    }
}
