using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Merit.Catalogue;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-55B: the operand catalogue's coverage and lookup. The summary table below is copied from
/// OpticStudio's 2025 R2 help, "Optimization Operands Summary Table" (saved as
/// plans/sources/optics/op-54-help/Optimization_Operands_Summary_Table.txt), category by
/// category, as printed (MNPD and NSRM appear twice there, POPD under fiber coupling and POP).
/// </summary>
public sealed class OperandCatalogueCoverageTests
{
    private static readonly (string Category, string Codes, bool Op55A)[] SummaryTable =
    {
        ("First-order optical properties", "AMAG CARD ENPP EFFL EFLA EFLX EFLY EPDI EXPD EXPP ISFN ISNA LINV OBSN PIMH PMAG POWF POWP POWR SFNO TFNO WFNO", true),
        ("Aberrations", "ABCD ANAC ANAR ANAX ANAY ANCX ANCY ASTI AXCL BIOC BIOD BSER COMA DIMX DISA DISC DISG DIST FCGS FCGT FCUR LACL LONA OPDC OPDM OPDX OSCD PETC PETZ RSCE RSCH RSRE RSRH RWCE RWCH RWRE RWRH SMIA SPCH SPHA TRAC TRAD TRAE TRAI TRAR TRAX TRAY TRCX TRCY ZERN", true),
        ("MTF data", "GMTA GMTN GMTS GMTT GMTX MSWA MSWN MSWS MSWT MTFA MSWX MTFN MTFS MTFT MTFX MTHA MTHN MTHS MTHT MTHX MECA MECS MECT", true),
        ("PSF/Strehl ratio data", "STRH", true),
        ("Encircled energy", "DENC DENF ERFP GENC GENF XENC XENF", true),
        ("Constraints on lens data", "COGT COLT COVA CTGT CTLT CTVA CVGT CVLT CVVA BLTH DCRV DMGT DMLT DMVA DPHS DSAG DSLP ETGT ETLT ETVA FTGT FTLT MNCA MNCG MNCT MNCV MNEA MNEG MNET MNPD MXCA MXCG MXCT MXCV MXEA MXEG MXET MNSD MXSD OMMI OMMX OMSD QSLP TGTH TTGT TTHI TTLT TTVA XNEA XNET XNEG XXEA XXEG XXET ZTHI", false),
        ("Constraints on lens properties", "CVOL MNDT MXDT PSLP SAGX SAGY SCRV SPHS SSLP SSAG STHI TMAS TOTR VOLU NORX NORY NORZ NORD SCUR SDRV", false),
        ("Constraints on parameter data", "PMGT PMLT PMVA", false),
        ("Constraints on glass data", "GCOS GTCE INDX MNAB MNIN MNPD MXAB MXIN MXPD RGLA", false),
        ("Constraints on paraxial ray data", "PANA PANB PANC PARA PARB PARC PARR PARX PARY PARZ PATX PATY YNIP", true),
        ("Constraints on real ray data", "CEHX CEHY CENX CENY CNAX CNAY CNPX CNPY DXDX DXDY DYDX DYDY HHCN HYLD IMAE MNRE MNRI MXRE MXRI OPTH PLEN RAED RAEN RAGA RAGB RAGC RAGX RAGY RAGZ RAID RAIN RANG REAA REAB REAC REAR REAX REAY REAZ RENA RENB RENC RETX RETY", true),
        ("Constraints on TrueFreeForm surface data", "GOPT", false),
        ("Constraints on element positions", "GLCA GLCB GLCC GLCR GLCX GLCY GLCZ", false),
        ("Changing system data", "CONF IMSF PRIM SVIG WLEN CVIG FDMO FDRE", false),
        ("General math operands", "ABSO ACOS ASIN ATAN CONS COSI DIFF DIVB DIVI EQUA LOGE LOGT MAXX MINN OPGT OPLT OPVA OSUM PROB PROD QSUM RECI SQRT SUMM SINE TANG ABGT ABLT", false),
        ("Multi-configuration (zoom) data", "CONF MCOL MCOG MCOV ZTHI", false),
        ("Gaussian beam data", "GBPD GBPP GBPR GBPS GBPW GBPZ GBSD GBSP GBSR GBSS GBSW", false),
        ("Gradient index control operands", "DLTN GRMN GRMX InGT InLT InVA LPTD", false),
        ("Foucault analysis", "FOUC", true),
        ("Ghost focus control", "GPIM GPRT GPRX GPRY GPSX GPSY", false),
        ("Fiber coupling operands", "FICL FICP POPD", false),
        ("Relative illumination operand", "RELI EFNO", true),
        ("Optimization with ZPL macros", "ZPLM", false),
        ("User defined operands", "UDOC", false),
        ("Merit function control operands", "BLNK DMFS ENDX GOTO OOFF SKIN SKIS USYM", false),
        ("Constraints on non-sequential object data", "FREZ NPGT NPLT NPVA NPXG NPXL NPXV NPYG NPYL NPYV NPZG NPZL NPZV NSRM NTXG NTXL NTXV NTYG NTYL NTYV NTZG NTZL NTZV", false),
        ("Non-sequential ray tracing and detector operands", "NPAF NSDC NSDD NSDE NSDP NSLT NSRA NSRM NSRW NSST NSTR NSTW NSRD", false),
        ("Constraints on construction optics for optically fabricated holograms", "CMFV", false),
        ("Constraints on optical coatings, polarization ray trace data", "CMGT CMLT CMVA CODA CEGT CELT CEVA CIGT CILT CIVA", false),
        ("Physical Optics Propagation (POP) results", "POPD POPI", false),
        ("Best Fit Sphere data", "BFSD", true),
        ("Tolerance sensitivity data", "TOLR", true),
        ("Thermal Coefficient of Expansion data", "TCGT TCLT TCVA", false),
    };

    /// <summary>The codes of the table, the InGT family expanded to I1GT … I6GT (n = 1 to 6, the
    /// help's six points).</summary>
    private static IEnumerable<(string Category, string Code)> Codes(bool includeOp55A) =>
        SummaryTable.Where(t => includeOp55A || !t.Op55A)
            .SelectMany(t => t.Codes.Split(' ', StringSplitOptions.RemoveEmptyEntries).SelectMany(code =>
                code.StartsWith("In", StringComparison.Ordinal)
                    ? Enumerable.Range(1, 6).Select(n => (t.Category, $"I{n}{code[2..]}"))
                    : new[] { (t.Category, code) }));

    private static string[] Uncovered(bool includeOp55A)
    {
        var registry = MeritOperandRegistry.CreateDefault();
        return Codes(includeOp55A)
            .Where(x => registry.Find(x.Code) is null && NotBuiltOperands.Find(x.Code) is null)
            .Select(x => $"{x.Code} ({x.Category})").Distinct().ToArray();
    }

    /// <summary>Every code of OP-55B's categories (and the categories OP-54, OP-46, OP-59 and
    /// OP-99 filled) is registered or listed as not built.</summary>
    [Fact]
    public void EveryCodeOfTheConstraintAndSystemCategories_IsRegisteredOrListedAsNotBuilt()
    {
        var missing = Uncovered(includeOp55A: false);
        Assert.True(missing.Length == 0, "Neither registered nor listed as not built: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The whole summary table: every code is registered by OP-54, OP-55A, OP-55B or an earlier
    /// item, or is in the not-built table (NotBuiltOperands, which includes OP-55A's
    /// ImagingOperands.NotBuilt).
    /// </summary>
    [Fact]
    public void EveryCodeOfTheSummaryTable_IsRegisteredOrListedAsNotBuilt()
    {
        var missing = Uncovered(includeOp55A: true);
        Assert.True(missing.Length == 0, "Neither registered nor listed as not built: " + string.Join(", ", missing));
    }

    [Fact]
    public void NoCodeIsBothRegisteredAndNotBuilt_AndEveryNotBuiltEntryCitesAReason()
    {
        var registry = MeritOperandRegistry.CreateDefault();
        Assert.Empty(NotBuiltOperands.All.Where(e => registry.Find(e.Code) is not null).Select(e => e.Code));
        Assert.Equal(NotBuiltOperands.All.Count, NotBuiltOperands.All.Select(e => e.Code).Distinct().Count());
        foreach (var e in NotBuiltOperands.All.Where(e => e.Category != NotBuiltOperands.ImagingCategory))
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Code);
            Assert.False(string.IsNullOrWhiteSpace(e.Reason), e.Code);
            Assert.True(e.WaitsFor is null || System.Text.RegularExpressions.Regex.IsMatch(e.WaitsFor, "^OP-[0-9]+$"), e.Code);
        }
    }

    [Fact]
    public void EveryConstraintOperand_IsRegistered_AndFoundByCodeAndName()
    {
        var registry = MeritOperandRegistry.CreateDefault();
        var mine = ConstraintOperands.Definitions().ToArray();
        Assert.NotEmpty(mine);
        Assert.Equal(registry.All.Count, registry.All.Select(d => d.Code).Distinct().Count());
        foreach (var d in mine)
        {
            Assert.Equal(d.Code, registry.Find(d.Code.ToLowerInvariant())!.Code);
            Assert.Contains(registry.Search(d.Code), x => x.Code == d.Code);
            Assert.Equal(d.Code, registry.Search(d.Code)[0].Code);   // the exact code first
            Assert.Contains(registry.Search(d.Name), x => x.Code == d.Code);
            Assert.False(string.IsNullOrWhiteSpace(d.Description), d.Code);
            Assert.False(string.IsNullOrWhiteSpace(d.Source), d.Code);
        }
        Assert.Contains(registry.Search("edge thickness value"), d => d.Code == "ETVA");
        Assert.Contains(registry.Search("global vertex"), d => d.Code == "GLCZ");
    }

    [Fact]
    public void NotBuiltLookup_FindsCodesAndNumberedFamilies()
    {
        Assert.Equal("OP-89", NotBuiltOperands.Find("nsrw")!.WaitsFor);   // OP-85 built NSLT; roadway lighting (OP-89) is the probe now
        Assert.Null(NotBuiltOperands.Find("NSLT"));                       // OP-85
        Assert.Null(NotBuiltOperands.Find("NSDD"));                       // OP-88
        Assert.Equal("InGT", NotBuiltOperands.Find("I3GT")!.Code);
        Assert.Equal("InVA", NotBuiltOperands.Find("I6VA")!.Code);
        Assert.Null(NotBuiltOperands.Find("I7GT"));   // the help names six points
        Assert.Equal("PnVA", NotBuiltOperands.Find("P2VA")!.Code);
        Assert.Null(NotBuiltOperands.Find("XXXX"));
        Assert.Null(NotBuiltOperands.Find("ETVA"));
    }

    [Fact]
    public void Describe_NamesRegisteredNotBuiltAndUnknownCodes()
    {
        Assert.StartsWith("Edge thickness value (ETVA):", MeritOperandCatalogue.Describe("etva"));
        string nsrw = MeritOperandCatalogue.Describe("NSRW");   // OP-85 built NSLT; NSRW waits for OP-89
        Assert.StartsWith("NSRW (OpticStudio: Non-sequential roadway lighting data", nsrw);
        Assert.Contains("is not built yet: needs OP-89", nsrw);
        Assert.StartsWith("Non-sequential LightningTrace (NSLT):", MeritOperandCatalogue.Describe("NSLT"));   // OP-85
        Assert.StartsWith("Non-sequential incoherent detector data (NSDD):", MeritOperandCatalogue.Describe("NSDD"));
        Assert.Equal("there is no operand 'QQQQ'.", MeritOperandCatalogue.Describe("QQQQ"));
        Assert.Contains("needs OP-28", MeritOperandCatalogue.Unknown("I2LT"));
    }

    [Fact]
    public void ValidateAndFileReading_NameANotBuiltOperand()
    {
        var registry = MeritOperandRegistry.CreateDefault();
        // OP-49 built POPD and OP-50 FICL and FICP; UDOC (user-defined operands, OP-100) is the not-built probe now.
        var problems = MeritEvaluator.Validate(new MeritFunction(new[] { MeritOperandRow.Of("UDOC") }), registry, 1);
        Assert.Contains(problems, p => p.StartsWith("Row 1: UDOC (OpticStudio:") && p.Contains("needs OP-100"));
        var notes = new List<string>();
        var function = MeritFunctionSettings.FromSettings(new OpticsMeritFunctionSettings
        {
            Operands = new List<OpticsMeritOperandSettings> { new() { Type = "GBSW" }, new() { Type = "ZZZZ" } },
        }, registry, notes);
        Assert.Equal(new[] { "OOFF", "OOFF" }, function.Rows.Select(r => r.Code));
        Assert.Contains(notes, n => n.Contains("GBSW (OpticStudio:") && n.Contains("needs OP-48") && n.Contains("OOFF"));
        Assert.Contains(notes, n => n.Contains("'ZZZZ' is not one this version knows"));
    }
}
