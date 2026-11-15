using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization.Interactive;
using OpenSim.Optics.Scripting;
using OpenSim.Optics.Scripting.Zpl;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-99's conformance suite: one small macro per bound keyword (and the bound functions with
/// them), each with its expected output, worked out in the comments from the keyword's help page
/// (saved under plans/sources/optics/op-99-help/) and from closed forms on the singlet below.
/// Every macro is a constant whose name ends in "Macro"; <see cref="EveryBoundName_HasAConformanceMacro"/>
/// fails when a bound keyword or function appears in none of them.
/// <para>
/// The singlet: object at infinity, entrance pupil 10 mm, fields (0, 0) and (0, 5°), one wavelength
/// 0.55 µm, surface 1 radius 50 mm, thickness 5 mm, constant index 1.5 (relative to air), the stop;
/// surface 2 plane, thickness 95 mm; surface 3 the image. Arbitrary inputs, not a design from a
/// source; no glass catalog is read.
/// </para>
/// </summary>
public sealed class ZplConformanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-zpl-tests", Guid.NewGuid().ToString("N"));

    public ZplConformanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    internal static OpticsSettings Singlet() => new()
    {
        SystemData = new OpticsSystemSettings
        {
            Title = "Singlet", ApertureValue = 10,
            Fields = new() { new(), new() { Y = 5 } },
            Wavelengths = new() { new() { Micrometers = 0.55 } },
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 5, Index = 1.5, IsStop = true, Comment = "front" },
            new() { Thickness = 95, Comment = "back" },
            new(),
        },
    };

    private ZplRunResult Run(string macro, OpticsSettings? system = null, IZplHost? host = null, IReadOnlyList<double>? pass = null,
        string? lensPath = null, IReadOnlyDictionary<string, string>? args = null) =>
        ZplMacro.Run(macro, new ZplRunOptions
        {
            System = system ?? Singlet(), Catalogs = NoCatalogs, MacroFolder = _dir, LensFolder = _dir, Host = host,
            PassValues = pass, LensPath = lensPath, Arguments = args,
        });

    private static string Lines(params string[] lines) => ZplLanguageTests.Lines(lines);

    private static void Near(double expected, double? actual, double tolerance = 1e-12)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - actual!.Value) <= tolerance * Math.Max(1, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");
    }

    // ================================================================ Coverage

    [Fact]
    public void EveryBoundName_HasAConformanceMacro()
    {
        var text = new StringBuilder();
        foreach (var f in typeof(ZplConformanceTests).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                     .Where(f => f.IsLiteral && f.Name.EndsWith("Macro", StringComparison.Ordinal)))
            text.AppendLine((string)f.GetValue(null)!);
        // The language core (IF, FOR, PRINT …) and the math and string functions are exercised in ZplLanguageTests.
        var words = new HashSet<string>(Regex.Matches(text.ToString().ToUpperInvariant(), @"\$?[A-Z_][A-Z0-9_]*").Select(m => m.Value));
        var languageCovered = new HashSet<string>(StringComparer.Ordinal)
        {
            "IF", "THEN", "ELSE", "ENDIF", "FOR", "NEXT", "GOTO", "LABEL", "GOSUB", "SUB", "RETURN", "END", "PRINT", "FORMAT", "REM",
            "DECLARE", "RELEASE", "REWIND",
            "ABSO", "ACOS", "ASIN", "ATAN", "COSI", "SINE", "TANG", "EXPE", "EXPT", "LOGE", "LOGT", "SQRT", "INTE", "SIGN", "MAGN",
            "POWR", "LVAL", "SLEN", "SVAL", "SCOM", "$GETSTRING", "$GETSTRINGC", "$LEFTSTRING", "$RIGHTSTRING", "$STR", "$QUOTE", "$TAB",
        };
        var missing = ZplLibrary.Table().Where(r => r.Bound && !words.Contains(r.Name) && !languageCovered.Contains(r.Name)).Select(r => r.Name).ToList();
        Assert.True(missing.Count == 0, "Bound without a conformance macro: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheTable_ListsEveryNameOfTheHelp_OnceEach_WithANote()
    {
        var table = ZplLibrary.Table();
        Assert.Equal(table.Count, table.Select(r => (r.Name, r.Kind)).Distinct().Count());
        Assert.All(table, r => Assert.False(string.IsNullOrWhiteSpace(r.Note), r.Name));
        // A few from each list of the help: the KEYWORDS page, the function listing, the string functions.
        foreach (var (name, kind) in new[] { ("RAYTRACE", ZplNameKind.Keyword), ("ZRDSUM", ZplNameKind.Keyword), ("ABSO", ZplNameKind.NumericFunction), ("YMAX", ZplNameKind.NumericFunction), ("$BUFFER", ZplNameKind.StringFunction), ("$UNITS", ZplNameKind.StringFunction) })
            Assert.Contains(table, r => r.Name == name && r.Kind == kind);
        Assert.Contains("| RAYTRACE | keyword | yes |", ZplLibrary.TableMarkdown());
        Assert.Contains("| BEEP | keyword | no |", ZplLibrary.TableMarkdown());
    }

    [Fact]
    public void TheLibraryPage_HoldsTheTable_Unchanged()
    {
        // docs/Optics-ZPL-Library.md publishes the table; it must be the library's own.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? page = null;
        for (; dir is not null && page is null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "docs", "Optics-ZPL-Library.md"))) page = Path.Combine(dir.FullName, "docs", "Optics-ZPL-Library.md");
        Assert.True(page is not null, "docs/Optics-ZPL-Library.md is found above the test's folder.");
        string text = File.ReadAllText(page!).Replace("\r\n", "\n");
        Assert.Contains(ZplLibrary.TableMarkdown().Replace("\r\n", "\n"), text);
        foreach (var (code, id) in ZplLibrary.TextFileCodes) Assert.Contains($"| {code} | {id} |", text);
    }

    // ================================================================ Rays

    private const string RayMacro =
        "RAYTRACE 0, 0, 0, 1\n"
        + "OPTRETURN 0, RAYY(1)\nOPTRETURN 1, RAYZ(1)\nOPTRETURN 2, RAYM(1)\nOPTRETURN 3, RAYN(1)\nOPTRETURN 4, RANY(1)\nOPTRETURN 5, RANZ(1)\n"
        + "OPTRETURN 6, RAYT(2)\nOPTRETURN 7, RAYO(2)\nOPTRETURN 8, RAYY(2)\nOPTRETURN 9, RAYY(NSUR())\nOPTRETURN 10, RAYE()\nOPTRETURN 11, RAYV()\n"
        + "OPTRETURN 12, RAYX(1)\nOPTRETURN 13, RAYL(1)\nOPTRETURN 14, RANX(1)\nOPTRETURN 15, RAYZ(2)\n"
        + "RAYTRACE 0, 1, 0, 0, 1\nOPTRETURN 16, RAYY(1)\nOPTRETURN 17, RAYM(1)\nPARAXIAL OFF\nOPTRETURN 18, PMOD()";

    [Fact]
    public void Raytrace_GivesTheClosedFormIntercepts_DirectionsAndNormals()
    {
        var r = Run(RayMacro).OptReturn;
        // Derived: the marginal ray (py = 1) enters parallel at y = EPD/2 = 5 mm and meets the sphere
        // R = 50 at z₁ = R − √(R² − y²). The normal (+z at the vertex) is (0, −y/R, √(1 − (y/R)²)).
        // Vector Snell with μ = 1/1.5 and c = d·N: t = μd + (√(1 − μ²(1 − c²)) − μc)N.
        double R = 50, y = 5, n = 1.5;
        double z1 = R - Math.Sqrt(R * R - y * y);
        double ny = -y / R, nz = Math.Sqrt(1 - ny * ny);
        double mu = 1 / n, c = nz;
        double k = Math.Sqrt(1 - mu * mu * (1 - c * c)) - mu * c;
        double ty = k * ny, tz = mu + k * nz;
        Near(y, r[0]);
        Near(z1, r[1]);
        Near(ty, r[2]);
        Near(tz, r[3]);
        Near(ny, r[4]);
        Near(nz, r[5]);
        // Surface 2 is the plane at z = 5 in surface 1's frame: path L = (5 − z₁)/t_z; optical path nL.
        double L = (5 - z1) / tz;
        Near(L, r[6]);
        Near(n * L, r[7]);
        double y2 = y + L * ty;
        Near(y2, r[8]);
        // Leaving the plane into air, n sin is kept: t'_y = 1.5 t_y; then 95 mm to the image.
        double t2y = n * ty, t2z = Math.Sqrt(1 - t2y * t2y);
        Near(y2 + 95 * t2y / t2z, r[9], 1e-11);
        Assert.Equal(0, r[10]);
        Assert.Equal(0, r[11]);
        Assert.Equal(0, r[12]);
        Assert.Equal(0, r[13]);
        Assert.Equal(0, r[14]);
        Near(0, r[15]);
        // The chief ray of the 5° field (hy = 1 is the largest field) crosses the stop at its centre,
        // the vertex of surface 1 where the normal is the axis: y = 0 and sin u' = sin 5°/1.5.
        Near(0, r[16]);
        Near(Math.Sin(5 * Math.PI / 180) / n, r[17]);
        Assert.Equal(0, r[18]);
    }

    private const string RayVignettedMacro = "RAYTRACE 0, 0, 0, 1\nOPTRETURN 0, RAYE()\nOPTRETURN 1, RAYV()\nx = RAYY(3)";

    [Fact]
    public void Raytrace_ReportsVignettingAsRayv_AndRefusesDataPastIt()
    {
        // A circular aperture of radius 1 mm on surface 2: the marginal ray (about 4.9 mm high there)
        // is vignetted at 2; RAYE stays 0 (help, RAYTRACE); there is no data at surface 3.
        var lens = Singlet();
        lens.Surfaces![2] = lens.Surfaces[2] with { Aperture = new OpticsSurfaceApertureSettings { Type = "Circular", MaxRadius = 1 } };
        var e = Assert.Throws<ZplException>(() => Run(RayVignettedMacro, lens));
        Assert.Equal(4, e.Line);
        Assert.Contains("no data at surface 3", e.Message);
        var r = Run("RAYTRACE 0, 0, 0, 1\nOPTRETURN 0, RAYE()\nOPTRETURN 1, RAYV()", lens).OptReturn;
        Assert.Equal(0, r[0]);
        Assert.Equal(2, r[1]);
        Assert.Contains("PARAXIAL ON", Assert.Throws<ZplException>(() => Run("PARAXIAL ON")).Message);
        Assert.Contains("normalised", Assert.Throws<ZplException>(() => Run("RAYTRACE 0, 1.5, 0, 0")).Message);
        Assert.Contains("RAYTRACE first", Assert.Throws<ZplException>(() => Run("x = RAYY(1)")).Message);
    }

    // ================================================================ Lens data

    private const string LensDataMacro =
        "OPTRETURN 0, NSUR()\nOPTRETURN 1, CURV(1)\nOPTRETURN 2, RADI(1)\nOPTRETURN 3, RADI(2)\nOPTRETURN 4, THIC(1)\nOPTRETURN 5, CONI(1)\n"
        + "OPTRETURN 6, CHZN(1)\nOPTRETURN 7, PARM(1, 1)\nOPTRETURN 8, INDX(1)\nOPTRETURN 9, INDX(2)\nOPTRETURN 10, SURC(\"BACK\")\n"
        + "A$ = \"nothing\"\nOPTRETURN 11, SURC(A$)\nOPTRETURN 12, SRCN(\"front\", 0)\nOPTRETURN 13, SDIA(1)\nOPTRETURN 14, MCSD(1)\n"
        + "C$ = $COMMENT(2)\nPRINT C$\nOPTRETURN 15, APMN(1)\nOPTRETURN 16, APXD(1)\nOPTRETURN 17, APYD(1)\nOPTRETURN 18, APMX(1)";

    [Fact]
    public void LensDataFunctions_ReadTheSurfaces_InLensUnits()
    {
        var result = Run(LensDataMacro);
        var r = result.OptReturn;
        Assert.Equal(new double?[] { 3, 0.02, 50, 0, 5, 0, 0, 0, 1.5, 1, 2, -1, 1 }, r.Take(13));
        // The automatic semi-diameter is the trace's sizing; the mechanical one adds the chip zone 0.
        var lens = ProjectLens.From(Singlet(), NoCatalogs)!;
        var data = SystemDataSettings.FromSettings(Singlet().SystemData, new List<string>());
        double clear = OpticsConventions.FromMeters(SemiDiameterSizing.Compute(lens, data).Clear[1], LensUnit.Millimeter);
        Assert.Equal(clear, r[13]);
        Assert.Equal(clear, r[14]);
        Assert.Equal(Lines("back"), result.Output);
        Assert.Equal(new double?[] { 0, 0, 0, 0 }, r.Skip(15).Take(4));
    }

    private const string SetSurfaceMacro =
        "SURP 2, CURV, -0.01\nSETSURFACEPROPERTY 2, 3, 90\nSURP 2, CONI, -1\nSURP 2, COMM, \"rear\"\nSURP 1, TCE, 7.1\nSURP 2, COAT, \"AR\"\n"
        + "SURP 1, SDIA, 6\nSURP 2, MCSD, 8\nSURP 2, CHZN, 0.5\nSURP 2, 14, 3\nSURP 2, 16, 0\nSURP 2, 90, 1\nSURP 2, 91, 0\nSURP 2, 92, 1\n"
        + "SURP 2, BDX, 0.1\nSURP 2, BDY, 0.2\nSURP 2, BTX, 1\nSURP 2, BTY, 2\nSURP 2, BTZ, 3\nSURP 2, BOR, 1\n"
        + "SURP 2, ADX, 0.3\nSURP 2, ADY, 0.4\nSURP 2, ATX, 4\nSURP 2, ATY, 5\nSURP 2, ATZ, 6\nSURP 2, AOR, 0\nSURP 2, APU, 0\n"
        + "OPTRETURN 0, SPRO(2, 2)\nOPTRETURN 1, SPRO(2, 3)\nOPTRETURN 2, SPRO(2, 5)\nOPTRETURN 3, SPRO(1, 7)\nOPTRETURN 4, SPRO(1, 6)\n"
        + "OPTRETURN 5, SPRO(2, 28)\nOPTRETURN 6, SPRO(2, 27)\nOPTRETURN 7, SPRO(2, 14)\nOPTRETURN 8, SPRO(2, 90)\nOPTRETURN 9, SPRO(2, 92)\n"
        + "OPTRETURN 10, SPRO(2, 61)\nOPTRETURN 11, SPRO(2, 65)\nOPTRETURN 12, SPRO(2, 60)\nOPTRETURN 13, SPRO(2, 75)\nOPTRETURN 14, SPRO(2, 70)\n"
        + "OPTRETURN 15, RADI(2)\nOPTRETURN 16, SPRO(2, 66)\nOPTRETURN 17, SPRO(2, 16)\nOPTRETURN 18, SPRO(2, 91)\n"
        + "d = SPRO(2, 1)\nA$ = $BUFFER()\nPRINT A$\nd = SPRO(2, 8)\nB$ = $BUFFER()\nPRINT B$\nC$ = $COAT(2)\nPRINT C$\n"
        + "SURP 2, TYPE, \"EVENASPH\"\nSURP 2, PARM, 0.001, 2\nOPTRETURN 19, PARM(2, 2)\nOPTRETURN 20, SPRX(2, 10, 2)\nd = SPRO(2, 0)\nT$ = $BUFFER()\nPRINT T$\n"
        + "SURP 1, GLAS, \"\"\nSURP 2, APDX, 0";

    [Fact]
    public void SetSurfaceProperty_SetsEachBoundCode_AndSproReadsItBack()
    {
        var lens = Singlet();
        lens.Surfaces![2] = lens.Surfaces[2] with { Aperture = new OpticsSurfaceApertureSettings { Type = "Circular", MaxRadius = 9 } };
        var result = Run(SetSurfaceMacro, lens);
        var r = result.OptReturn;
        Assert.Equal(new double?[] { -0.01, 90, -1, 7.1, 6, 8, 0.5, 3, 1, 1, 0.1, 3, 1, 6, 0, -100, 0, 0, 0, 0.001, 0.001 }, r.Take(21));
        Assert.Equal(Lines("rear", "AR", "AR", "EVENASPH"), result.Output);
        var s = result.System.Surfaces!;
        Assert.Equal(-100, s[2].Radius);
        Assert.Equal("Even Asphere", s[2].Type);
        Assert.Equal("TiltThenDecenter", s[2].TiltBefore!.Order);
        Assert.Equal(new[] { 0.1, 0.2, 1, 2, 3 }, new[] { s[2].TiltBefore!.DecenterX, s[2].TiltBefore!.DecenterY, s[2].TiltBefore!.TiltX, s[2].TiltBefore!.TiltY, s[2].TiltBefore!.TiltZ });
        Assert.Equal(new[] { 0.3, 0.4, 4, 5, 6 }, new[] { s[2].TiltAfter!.DecenterX, s[2].TiltAfter!.DecenterY, s[2].TiltAfter!.TiltX, s[2].TiltAfter!.TiltY, s[2].TiltAfter!.TiltZ });
        Assert.Null(s[1].Material);
        Assert.Null(s[1].Index);                       // GLAS "" makes the surface air
        Assert.True(s[2].HideRaysTo && s[2].DoNotDraw && !s[2].SkipRaysTo);
        Assert.Equal(0, s[2].Aperture!.DecenterX);
        // The input record is not changed.
        Assert.Equal(1.5, lens.Surfaces[1].Index);
    }

    private const string CoordinateBreakMacro =
        "SURP 2, TYPE, \"COORDBRK\"\nSURP 2, 76, 3\nSURP 2, 77, 1\nOPTRETURN 0, SPRO(2, 76)\nOPTRETURN 1, SPRO(2, 77)\nSURP 2, APU, 4\nOPTRETURN 2, SPRO(2, 66)";

    [Fact]
    public void SetSurfaceProperty_CoordinateReturnAndAfterPickup_UseTheHelpsCodes()
    {
        // 76: "3 for Orientation XYZ"; 77 the surface; APU 4 = reverse current surface minus 1.
        var result = Run(CoordinateBreakMacro);
        Assert.Equal(new double?[] { 3, 1, 4 }, result.OptReturn.Take(3));
        var s = result.System.Surfaces![2];
        Assert.Equal(SurfaceTypes.CoordinateBreak, s.Type);
        Assert.Equal("OrientationXYZ", s.CoordinateReturn!.Mode);
        Assert.Equal("ReverseSurface", s.TiltAfter!.Mode);
        Assert.Equal(1, s.TiltAfter.Surface);
        Assert.Contains("code 9 is not bound", Assert.Throws<ZplException>(() => Run("SURP 2, SDLL, \"x.dll\"")).Message);
        Assert.Contains("only on a coordinate break", Assert.Throws<ZplException>(() => Run("SURP 2, 76, 1")).Message);
        Assert.Contains("not built", Assert.Throws<ZplException>(() => Run("SURP 2, TYPE, \"GRINSUR1\"")).Message);
    }

    private const string StopInsertDeleteMacro =
        "STOPSURF 2\nINSERT 2\nOPTRETURN 0, NSUR()\nOPTRETURN 1, THIC(2)\nOPTRETURN 2, THIC(3)\nDELETE 2\nOPTRETURN 3, NSUR()\nOPTRETURN 4, THIC(2)";

    [Fact]
    public void StopsurfInsertAndDelete_ChangeTheSurfaces()
    {
        var result = Run(StopInsertDeleteMacro);
        Assert.Equal(new double?[] { 4, 0, 95, 3, 95 }, result.OptReturn.Take(5));
        Assert.Equal(new[] { false, false, true, false }, result.System.Surfaces!.Select(s => s.IsStop));
        Assert.Contains("cannot be the stop", Assert.Throws<ZplException>(() => Run("STOPSURF 0")).Message);
        Assert.Contains("cannot be deleted", Assert.Throws<ZplException>(() => Run("DELETE 3")).Message);
    }

    private const string SolveTypeMacro =
        "SOLVETYPE 2, TP, 1, 2\nOPTRETURN 0, THIC(2)\nSOLVETYPE 2, TF\nOPTRETURN 1, THIC(2)\nSOLVETYPE 2, CP, 1, -1\nOPTRETURN 2, CURV(2)\n"
        + "SOLVETYPE 1, CV\nSOLVETYPE 1, PV_2\nSOLVETYPE 1, SA\nSOLVETYPE 2, SU\nOPTRETURN 3, SDIA(2)";

    [Fact]
    public void Solvetype_SetsFixedVariableAndPickupSolves()
    {
        // TP from surface 1 with scale 2: thickness 2·5 = 10 (the solved value is read); TF fixes it
        // back to the 95 entered; CP from surface 1, scale −1: curvature −0.02.
        var result = Run(SolveTypeMacro);
        var r = result.OptReturn;
        Near(10, r[0]);
        Assert.Equal(95, r[1]);
        Near(-0.02, r[2]);
        var s = result.System.Surfaces!;
        Assert.Contains(s[1].Flags!, f => f.Column == "Radius" && f.Kind == "Variable");
        Assert.Contains(s[1].Flags!, f => f.Column == "Par2" && f.Kind == "Variable");
        Assert.Contains(s[2].Flags!, f => f.Column == "Radius" && f.Kind == "Pickup" && f.PickupSurface == 1 && f.PickupScale == -1);
        Assert.Null(s[1].SemiDiameter);
        Assert.Equal(r[3], s[2].SemiDiameter);   // SU fixes the automatic value
        Assert.Contains("not bound", Assert.Throws<ZplException>(() => Run("SOLVETYPE 2, TM, 0")).Message);
        Assert.Contains("no offset", Assert.Throws<ZplException>(() => Run("SOLVETYPE 2, CP, 1, 1, 3")).Message);
    }

    private const string VariablesMacro =
        "SETVAR 1, R, 1\nSETVAR 2, T, 1\nSETVAR 1, C, 1\nSETVAR 1, P3, 1\nSETVAR 1, D, 1\nSETVAR 1, C, 0\nGETVARDATA 1\n"
        + "FOR i, 0, 20, 1\nOPTRETURN i, VEC1(i)\nNEXT\nREMOVEVARIABLES\nGETVARDATA 2\nOPTRETURN 21, VEC2(0)";

    [Fact]
    public void SetvarGetvardataAndRemovevariables_FollowTheHelpsCodes()
    {
        // GETVARDATA: [0] the count, then type code, surface, parameter, object, value for each
        // (help, GETVARDATA): curvature 1 on surface 1 (value 1/50), thickness 2 on surface 2 (95),
        // TCE 7 (0), parameter 8 number 3 (0) — in the order the optimizer lists them.
        var r = Run(VariablesMacro).OptReturn;
        Assert.Equal(4, r[0]);
        var rows = Enumerable.Range(1, 4).Select(q => Enumerable.Range(5 * q - 4, 5).Select(i => r[i]!.Value).ToArray()).ToList();
        Assert.Contains(rows, x => x.SequenceEqual(new[] { 1, 1, 0, 0, 0.02 }));
        Assert.Contains(rows, x => x.SequenceEqual(new double[] { 2, 2, 0, 0, 95 }));
        Assert.Contains(rows, x => x.SequenceEqual(new double[] { 7, 1, 0, 0, 0 }));
        Assert.Contains(rows, x => x.SequenceEqual(new double[] { 8, 1, 3, 0, 0 }));
        Assert.Equal(0, r[21]);
        Assert.Contains("not bound", Assert.Throws<ZplException>(() => Run("SETVAR 1, G, 1")).Message);
    }

    // ================================================================ System data

    private const string SystemMacro =
        "SYSP 4, 1\nSYSP 11, 12\nSYSP 12, 1\nSYSP 13, 0.5\nSYSP 14, 0\nSYSP 18, 0\nSYSP 24, 25\nSYSP 25, 0.9\nSYSP 35, 1\n"
        + "SYSP 71, 0.1\nSYSP 72, 0.2\nSYSP 73, 0.3\nSYSP 75, 1\nSYSP 76, 1\nSYSP 77, 0.01\nSYSP 78, 0.02\nSYSP 110, 1\n"
        + "SETSYSTEMPROPERTY 101, 3\nSYSP 102, 3, 1.5\nSYSP 103, 3, 2.5\nSYSP 104, 3, 2\nSYSP 105, 3, 0.1\nSYSP 106, 3, 0.2\nSYSP 107, 3, 0.3\nSYSP 108, 3, 0.4\nSYSP 109, 3, 7\n"
        + "SYSP 201, 2\nSYSP 202, 2, 0.65\nSYSP 203, 2, 0.5\nPWAV 2\nSYSP 70, 2\nSETAIM 1\nSYSP 16, \"New title\"\nSYSP 17, \"a note\"\nSYSP 23, \"SCHOTT OHARA\"\n"
        + "SYSP 100, 1\nSYSP 10, 1\nSYSP 30, 1\n"
        + "OPTRETURN 0, SYPR(4)\nOPTRETURN 1, SYPR(11)\nOPTRETURN 2, SYPR(12)\nOPTRETURN 3, SYPR(13)\nOPTRETURN 4, SYPR(24)\nOPTRETURN 5, SYPR(25)\n"
        + "OPTRETURN 6, SYPR(35)\nOPTRETURN 7, SYPR(71)\nOPTRETURN 8, SYPR(78)\nOPTRETURN 9, SYPR(110)\nOPTRETURN 10, NFLD()\nOPTRETURN 11, FLDX(3)\n"
        + "OPTRETURN 12, FLDY(3)\nOPTRETURN 13, FWGT(3)\nOPTRETURN 14, FVDX(3)\nOPTRETURN 15, FVDY(3)\nOPTRETURN 16, FVCX(3)\nOPTRETURN 17, FVCY(3)\n"
        + "OPTRETURN 18, FTAN(3)\nOPTRETURN 19, NWAV()\nOPTRETURN 20, WAVL(2)\nOPTRETURN 21, WWGT(2)\nOPTRETURN 22, PWAV()\nOPTRETURN 23, SYPR(70)\n"
        + "OPTRETURN 24, FTYP()\nOPTRETURN 25, ATYP()\nOPTRETURN 26, UNIT()\nOPTRETURN 27, SYPR(100)\nOPTRETURN 28, SYPR(201)\nOPTRETURN 29, AVAL()\n"
        + "OPTRETURN 30, MAXF()\nOPTRETURN 31, SYPR(75)\nOPTRETURN 32, SYPR(14)\nd = SYPR(16)\nT$ = $BUFFER()\nPRINT T$\nL$ = $LENSNAME()\nPRINT L$\n"
        + "N$ = $NOTE(1)\nPRINT N$\nd = SYPR(23)\nG$ = $BUFFER()\nPRINT G$\nU$ = $UNITS()\nPRINT U$\nC$ = $GLASSCATALOG(2)\nPRINT C$\nOPTRETURN 33, SYPR(200)";

    [Fact]
    public void SetSystemProperty_SetsEachBoundCode_AndTheFunctionsReadIt()
    {
        var result = Run(SystemMacro);
        var r = result.OptReturn;
        // SETAIM 1 overrides SYSP 70, 2: ray aiming code 1 (paraxial). MAXF of fields (0,0), (0,5)
        // and (1.5, 2.5) is max(0, 5, √(1.5² + 2.5²)) = 5.
        Assert.Equal(new double?[]
        {
            1, 12, 1, 0.5, 25, 0.9, 1, 0.1, 0.02, 1, 3, 1.5, 2.5, 2, 0.1, 0.2, 0.3, 0.4, 7, 2, 0.65, 0.5, 2, 1, 1, 1, 1, 1, 2, 12, 5, 1, 0, 2,
        }, r.Take(34));
        Assert.Equal(Lines("New title", "New title", "a note", "SCHOTT OHARA", "CM", "OHARA"), result.Output);
        var d = result.System.SystemData!;
        Assert.Equal("ObjectHeight", d.FieldType);
        Assert.Equal("ImageSpaceFNumber", d.ApertureType);
        Assert.Equal("Centimeter", d.LensUnit);
        Assert.Equal("Rectangular", d.FieldNormalization);
        Assert.Equal(new List<string> { "SCHOTT", "OHARA" }, result.System.Materials!.ActiveCatalogs);
        Assert.Contains("code 50 is not bound", Assert.Throws<ZplException>(() => Run("SYSP 50, 100")).Message);
        Assert.Contains("value2", Assert.Throws<ZplException>(() => Run("x = SYPR(102)")).Message);
        Assert.Contains("primary wavelength", Assert.Throws<ZplException>(() => Run("SYSP 201, 2\nPWAV 2\nSYSP 201, 1")).Message);
    }

    private const string ConfigurationMacro = "SETCONFIG 1\nOPTRETURN 0, CONF()\nOPTRETURN 1, NCON()\nUPDATE\nUPDATE ALL";

    [Fact]
    public void SetconfigAndUpdate_OnASingleConfiguration()
    {
        Assert.Equal(new double?[] { 1, 1 }, Run(ConfigurationMacro).OptReturn.Take(2));
        Assert.Contains("from 1 to 1", Assert.Throws<ZplException>(() => Run("SETCONFIG 2")).Message);
        // UPDATE builds the lens: a glass no catalog holds is reported there.
        var lens = Singlet();
        lens.Surfaces![1] = lens.Surfaces[1] with { Index = null, Material = "NOSUCHGLASS" };
        var e = Assert.Throws<ZplException>(() => Run("x = 1\nUPDATE", lens));
        Assert.Equal(2, e.Line);
        Assert.Contains("NOSUCHGLASS", e.Message);
    }

    // ================================================================ Merit function and optimization

    private const string MeritMacro =
        "INSERTMFO 1\nSETOPERAND 1, 11, \"EFFL\"\nSETOPERAND 1, 3, 1\nSETOPERAND 1, 8, 90\nSETOPERAND 1, 9, 1\nINSERTMFO 2\n"
        + "SETOPERAND 2, 1, ONUM(\"TOTR\")\nSETOPERAND 2, 10, \"track\"\nSETOPERAND 2, 4, 0.25\nSETOPERAND 2, 12, 0.5\nINSERTMFO 3\nSETOPERAND 3, 10, \"a note\"\n"
        + "OPTRETURN 0, MFCN()\nOPTRETURN 1, OPER(1, 10)\nOPTRETURN 2, OPER(1, 11)\nOPTRETURN 3, OPER(1, 8)\nOPTRETURN 4, OPER(1, 9)\nOPTRETURN 5, OPER(1, 3)\n"
        + "OPTRETURN 6, OPEV(OCOD(\"EFFL\"), 0, 1, 0, 0, 0, 0)\nOPTRETURN 7, OPEW(OCOD(\"TOTR\"), 0, 0, 0, 0, 0, 0, 0, 0)\nOPTRETURN 8, OPER(2, 4)\nOPTRETURN 9, OPER(2, 12)\n"
        + "OPTRETURN 10, ONUM(\"NOPE\")\nd = OPER(3, 2)\nB$ = $BUFFER()\nPRINT B$\nd = OPER(2, 1)\nT$ = $BUFFER()\nPRINT T$\nDELETEMFO 3\n";

    [Fact]
    public void MeritFunctionKeywords_EditTheRows_AndTheFunctionsEvaluate()
    {
        // EFFL of the plano-convex singlet: power (n − 1)/R₁ = 0.5/50 (the plane adds none), so
        // f = 100 mm; TOTR = 5 + 95 = 100 mm. With EFFL target 90 weight 1 and TOTR weight 0, the
        // merit is √(1·(100 − 90)²/1) = 10, the EFFL row holding 100 % of it.
        var result = Run(MeritMacro);
        var r = result.OptReturn;
        Near(10, r[0], 1e-9);
        Near(100, r[1], 1e-9);
        Near(100, r[2], 1e-9);
        Assert.Equal(90, r[3]);
        Assert.Equal(1, r[4]);
        Assert.Equal(1, r[5]);
        Near(100, r[6], 1e-9);
        Near(100, r[7], 1e-12);
        Assert.Equal(0.25, r[8]);
        Assert.Equal(0.5, r[9]);
        Assert.Equal(0, r[10]);
        Assert.Equal(Lines("a note", "TOTR"), result.Output);
        var rows = result.System.MeritFunction!.Operands!;
        Assert.Equal(new[] { "EFFL", "TOTR" }, rows.Select(x => x.Type));
        Assert.Equal("track", rows[1].Comment);
        Assert.Empty(Run("INSERTMFO 1\nINSERTMFO 1\nDELETEMFO ALL").System.MeritFunction!.Operands!);
        Assert.Contains("MFCN() was not called", Assert.Throws<ZplException>(() => Run("INSERTMFO 1\nx = OPER(1, 10)")).Message);
    }

    private const string OptimizeMacro =
        "SETVAR 1, R, 1\nINSERTMFO 1\nSETOPERAND 1, 11, \"EFFL\"\nSETOPERAND 1, 3, 1\nSETOPERAND 1, 8, 80\nSETOPERAND 1, 9, 1\n"
        + "OPTRETURN 0, MFCN()\nOPTIMIZE\nOPTRETURN 1, MFCN()\nOPTRETURN 2, RADI(1)\nOPTIMIZE 5, 1\nOPTRETURN 3, MFCN()";

    [Fact]
    public void Optimize_DrivesTheVariableToTheTarget()
    {
        // f = R₁/(n − 1): EFFL = 80 needs R₁ = 40 (derived). Start merit |100 − 80| = 20.
        var r = Run(OptimizeMacro).OptReturn;
        Near(20, r[0], 1e-9);
        Assert.True(r[1] < 1e-6, $"merit after {r[1]}");
        Near(40, r[2], 1e-6);
        Assert.True(r[3] <= r[1] + 1e-12);
        Assert.Contains("0 (automatic) to 99", Assert.Throws<ZplException>(() => Run("OPTIMIZE 100")).Message);
    }

    private const string QuickFocusMacro = "QUICKFOCUS 0, 0\nOPTRETURN 0, THIC(2)";

    [Fact]
    public void Quickfocus_IsTheQuickFocusTool()
    {
        // The binding calls OP-60's Quick Focus with criterion 0 (RMS spot radius) about the chief
        // ray; the thickness is the tool's own result on the same record.
        var expected = QuickFocus.Run(Singlet(), NoCatalogs, new QuickFocusOptions { Criterion = QuickFocusCriterion.SpotRadius, UseCentroid = false });
        Assert.Equal(expected.Settings.Surfaces![2].Thickness, Run(QuickFocusMacro).OptReturn[0]);
        Assert.Contains("0 to 3", Assert.Throws<ZplException>(() => Run("QUICKFOCUS 4, 0")).Message);
    }

    // ================================================================ Analyses and lens files

    private const string TextFileMacro = "GETTEXTFILE \"card.txt\", Car\nA$ = \"pre.txt\"\nGETTEXTFILE A$, \"Pre\"";

    [Fact]
    public void Gettextfile_WritesTheAnalysisText()
    {
        Run(TextFileMacro);
        Assert.Equal(OpticsScripting.Analyze(Singlet(), NoCatalogs, "CardinalPoints").Text, File.ReadAllText(Path.Combine(_dir, "card.txt")));
        Assert.Equal(OpticsScripting.Analyze(Singlet(), NoCatalogs, "PrescriptionData").Text, File.ReadAllText(Path.Combine(_dir, "pre.txt")));
        Assert.Contains("'car'", Assert.Throws<ZplException>(() => Run("GETTEXTFILE \"x.txt\", car")).Message);   // case sensitive
        Assert.Contains("settings files", Assert.Throws<ZplException>(() => Run("GETTEXTFILE \"x.txt\", Car, \"s.cfg\", 1")).Message);
        Assert.Contains("CardinalPoints", string.Join(" ", ZplLibrary.TextFileCodes.Values));
    }

    private const string LensFileMacro =
        "SAVELENS \"copy.osslens\"\nSURP 2, THIC, 1\nOPTRETURN 0, THIC(2)\nLOADLENS \"copy.osslens\"\nOPTRETURN 1, THIC(2)\nSAVELENS\n"
        + "F$ = $FILENAME()\nPRINT F$\nP$ = $FILEPATH()\nPRINT P$\nD$ = $PATHNAME()\nPRINT D$";

    [Fact]
    public void SavelensAndLoadlens_RoundTripTheLens()
    {
        var result = Run(LensFileMacro);
        Assert.Equal(new double?[] { 1, 95 }, result.OptReturn.Take(2));
        string path = Path.Combine(_dir, "copy.osslens");
        Assert.Equal(path, result.LensPath);
        Assert.Equal(Lines("copy.osslens", path, _dir), result.Output);
        Assert.Equal(95, new OpticsFileSerializer().Load(path).Optics!.Surfaces![2].Thickness);
        Assert.Contains("append", Assert.Throws<ZplException>(() => Run("LOADLENS \"copy.osslens\", 2")).Message);
        Assert.Contains("no file", Assert.Throws<ZplException>(() => Run("F$ = $FILENAME()")).Message);
    }

    // ================================================================ Calling macros, buffers, values returned

    private const string ParentMacro = "CALLSETDBL 1, 3.5\nCALLSETSTR 1, \"Hello\"\nCALLMACRO child.zpl\nPRINT CALD(1)";
    private const string ChildMacro = "PRINT \"in the child\"\nPRINT CALD(1)\nA$ = $CALLSTR(1)\nPRINT A$\nCALLSETDBL 1, 7.11\nx = 1";

    [Fact]
    public void Callmacro_SharesTheBuffers_AndCopiesTheChildsOutput()
    {
        // Help, Calling a Macro from within a Macro: the child's screen output is copied into the
        // parent's when it returns, and the buffer value the child set is the parent's.
        File.WriteAllText(Path.Combine(_dir, "child.zpl"), ChildMacro);
        Assert.Equal(Lines("in the child", "3.5000", "Hello", "7.1100"), Run(ParentMacro).Output);
        // A child's error names the child and its line.
        File.WriteAllText(Path.Combine(_dir, "bad.zpl"), "x = 1\ny = z");
        var e = Assert.Throws<ZplException>(() => Run("CALLMACRO bad.zpl"));
        Assert.Equal("bad.zpl", e.Macro);
        Assert.Equal(2, e.Line);
        Assert.Contains("does not exist", Assert.Throws<ZplException>(() => Run("CALLMACRO missing.zpl")).Message);
    }

    private const string ReturnValuesMacro =
        "OPTRETURN 0, 1.25\nOPTRETURN 50, PVHX() + PVHY() + PVPX() + PVPY() + PVEX() + PVEY()\nSOLVEBEFORESTOP\nSOLVERETURN 3\nSOLVERETURN SOSO(0)";

    [Fact]
    public void OptreturnAndSolvereturn_AreTheResult_AndThePassValuesAreRead()
    {
        // 1 + 2 + 3 + 4 + 5 + 6 = 21; the last SOLVERETURN counts ("only the last SOLVERETURN called
        // is considered"): SOSO(0), the solve surface 2.
        var result = ZplMacro.Run(ReturnValuesMacro, new ZplRunOptions { System = Singlet(), PassValues = new double[] { 1, 2, 3, 4, 5, 6 }, SolveSurface = 2 });
        Assert.Equal(1.25, result.OptReturn[0]);
        Assert.Equal(21, result.OptReturn[50]);
        Assert.Null(result.OptReturn[1]);
        Assert.Equal(2, result.SolveReturn);
        Assert.Contains("0 to 50", Assert.Throws<ZplException>(() => Run("OPTRETURN 51, 1")).Message);
        Assert.Contains("not running as a solve", Assert.Throws<ZplException>(() => Run("x = SOSO(0)")).Message);
    }

    // ================================================================ Text output and files

    private const string OutputMacro =
        "OUTPUT \"out.txt\"\nPRINT \"to the file\"\nOUTPUT SCREEN\nPRINT \"to the screen\"\nOUTPUT \"out.txt\", APPEND\nPRINT \"appended\"\nOUTPUT SCREEN\nCLOSEWINDOW\nSUSPENDUPDATES\nRESUMEUPDATES";

    [Fact]
    public void Output_SendsPrintToAFileOrTheScreen()
    {
        var result = Run(OutputMacro);
        Assert.Equal(Lines("to the screen"), result.Output);
        Assert.Equal(Lines("to the file", "appended"), File.ReadAllText(Path.Combine(_dir, "out.txt")));
        // "the file will be in Unicode format" (help, OUTPUT): UTF-16 little-endian with its mark.
        Assert.Equal(new byte[] { 0xFF, 0xFE }, File.ReadAllBytes(Path.Combine(_dir, "out.txt")).Take(2));
        Assert.True(result.Quiet);
    }

    private const string ReadMacro =
        "OPEN \"data.txt\"\nREAD x, y, z\nREADNEXT a\nREADNEXT b\nREADSKIP -1\nREADSTRING L$\nREADSKIP 3\nREADSTRING M$\nOPTRETURN 5, EOFF()\nREADSTRING E$\n"
        + "OPTRETURN 0, x + y + z\nOPTRETURN 1, a\nOPTRETURN 2, b\nOPTRETURN 3, SLEN(E$)\nOPTRETURN 4, EOFF()\nPRINT L$\nPRINT M$\nCLOSE";

    [Fact]
    public void ReadKeywords_ReadNumbersAndLines()
    {
        // Line 1 "1 2 3" → x + y + z = 6; READNEXT reads 4.5 and 5.5 from line 2; READSKIP −1 ends
        // that line; READSTRING reads "hello world"; READSKIP 3 skips "abc" of "abcdef"; the next
        // READSTRING is at the end of the file: empty, and EOFF() becomes 1.
        File.WriteAllText(Path.Combine(_dir, "data.txt"), "1 2 3\n4.5 5.5\nhello world\nabcdef\n");
        var result = Run(ReadMacro);
        Assert.Equal(new double?[] { 6, 4.5, 5.5, 0, 1, 0 }, result.OptReturn.Take(6));
        Assert.Equal(Lines("hello world", "def"), result.Output);
        Assert.Contains("OPEN first", Assert.Throws<ZplException>(() => Run("READ x")).Message);
    }

    private const string FileKeywordsMacro =
        "MAKEFOLDER \"sub\"\nMAKEFOLDER \"sub\"\nCOPYFILE \"a.dat\", \"sub\\b.dat\"\nRENAMEFILE \"sub\\b.dat\", \"sub\\c.dat\"\nCOPYFILE \"a.dat\", \"d.dat\"\nDELETEFILE \"d.dat\"\n"
        + "F$ = \"*.dat\"\nFINDFILE N$, F$\nPRINT N$\nFINDFILE N$, F$\nPRINT N$\nFINDFILE N$, F$\nOPTRETURN 0, SLEN(N$)\nT$ = $TEMPFILENAME()\nDELETEFILE T$\nM$ = $MACROPATH()\nPRINT M$";

    [Fact]
    public void FileKeywords_CopyRenameDeleteAndFind()
    {
        File.WriteAllText(Path.Combine(_dir, "a.dat"), "x");
        File.WriteAllText(Path.Combine(_dir, "e.dat"), "y");
        var result = Run(FileKeywordsMacro);
        Assert.True(File.Exists(Path.Combine(_dir, "sub", "c.dat")));
        Assert.False(File.Exists(Path.Combine(_dir, "sub", "b.dat")));
        Assert.False(File.Exists(Path.Combine(_dir, "d.dat")));
        // FINDFILE lists a.dat and e.dat (d.dat is deleted first), then the empty string.
        Assert.Equal(Lines("a.dat", "e.dat", _dir), result.Output);
        Assert.Equal(0, result.OptReturn[0]);
        Assert.Contains("does not exist", Assert.Throws<ZplException>(() => Run("DELETEFILE \"nothere.dat\"")).Message);
    }

    // ================================================================ Vectors, random numbers, timer

    private const string VectorMacro =
        "SETVECSIZE 10\nVEC1 10, 3\nVEC2 1, 4\nVEC3 2, 5\nVEC4 0, 6\nOPTRETURN 0, VEC1(10) + VEC2(1.9) + VEC3(2) + VEC4(0)\nOPTRETURN 1, VEC2(5)";

    [Fact]
    public void Vectors_HoldValues_UpToTheirSize()
    {
        // 3 + 4 + 5 + 6 = 18 (subscript 1.9 is rounded down to 1); unset elements are 0.
        Assert.Equal(new double?[] { 18, 0 }, Run(VectorMacro).OptReturn.Take(2));
        Assert.Contains("vector size 10", Assert.Throws<ZplException>(() => Run("SETVECSIZE 10\nVEC1 11, 1")).Message);
    }

    private const string RandomMacro = "RANDOMIZE 7\nOPTRETURN 0, RAND(1)\nOPTRETURN 1, RAND(10)\nRANDOMIZE 7\nOPTRETURN 2, GAUS(2)\nTIMER\nOPTRETURN 3, ETIM()";

    [Fact]
    public void Randomize_RepeatsTheSeries_AndGausIsBoxMuller()
    {
        // RAND(x) = x·NextDouble() of System.Random(seed); GAUS(σ) = σ√(−2 ln(1 − u₁)) cos(2πu₂) on
        // the same generator (this program's choices, Conventions §56).
        var r = Run(RandomMacro).OptReturn;
        var g = new Random(7);
        Assert.Equal(g.NextDouble(), r[0]);
        Assert.Equal(10 * g.NextDouble(), r[1]);
        g = new Random(7);
        double u1 = 1.0 - g.NextDouble(), u2 = g.NextDouble();
        Assert.Equal(2 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2), r[2]);
        Assert.True(r[3] >= 0);
    }

    // ================================================================ The person running the macro

    private sealed class FakeHost : IZplHost
    {
        public Queue<string> Answers { get; } = new();
        public List<string> Prompts { get; } = new();
        public List<string> Pauses { get; } = new();
        public string? Input(string prompt) { Prompts.Add(prompt); return Answers.Dequeue(); }
        public void Pause(string message) => Pauses.Add(message);
    }

    private const string InputMacro = "INPUT \"Enter x:\", x\nINPUT A$\nPRINT A$\nOPTRETURN 0, x\nPAUSE \"Ready\"\nPAUSE\nPAUSE TIME, 0\nPAUSE THREADS";

    [Fact]
    public void InputAndPause_AskTheHost()
    {
        var host = new FakeHost();
        host.Answers.Enqueue("2.5");
        host.Answers.Enqueue("typed text");
        var result = Run(InputMacro, host: host);
        Assert.Equal(2.5, result.OptReturn[0]);
        Assert.Equal(Lines("typed text"), result.Output);
        Assert.Equal(new[] { "Enter x:", "?" }, host.Prompts);   // "?" when no prompt is given (help, INPUT)
        Assert.Equal(new[] { "Ready", "" }, host.Pauses);
        Assert.Contains("no host", Assert.Throws<ZplException>(() => Run("INPUT x")).Message);
    }

    private const string ArgumentsMacro = "A$ = $GETARG(\"surface\")\nB$ = $GETARG(\"IsAutomated\")\nPRINT A$\nPRINT B$\nD$ = $DATE()\nOPTRETURN 0, SLEN(D$)\nN$ = \"a\" + $NEWLINE() + \"b\"\nOPTRETURN 1, SLEN(N$)";

    [Fact]
    public void Getarg_ReadsTheArguments_CaseIgnored()
    {
        var result = Run(ArgumentsMacro, args: new Dictionary<string, string> { ["Surface"] = "3" });
        Assert.Equal(Lines("3", "False"), result.Output);
        Assert.Equal(19, result.OptReturn[0]);   // yyyy-MM-dd HH:mm:ss
        Assert.Equal(3, result.OptReturn[1]);    // a, the newline, b
        Assert.Contains("no argument 'x'", Assert.Throws<ZplException>(() => Run("A$ = $GETARG(\"x\")")).Message);
    }

    private const string GlassMacro = "G$ = $GLASS(1)\nPRINT G$";

    [Fact]
    public void Glass_NamesTheSurfacesGlass()
    {
        var lens = Singlet();
        lens.Surfaces![1] = lens.Surfaces[1] with { Index = null, Material = "N-BK7" };
        Assert.Equal(Lines("N-BK7"), Run(GlassMacro, lens).Output);
        Assert.Contains("constant index", Assert.Throws<ZplException>(() => Run(GlassMacro)).Message);
    }
}
