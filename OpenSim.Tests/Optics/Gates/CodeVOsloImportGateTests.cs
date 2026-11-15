using System.Globalization;
using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Interop.CodeV;
using OpenSim.Optics.Interop.Oslo;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-95's gate (plan): "A lens entered by hand and the same lens imported from each format trace
/// identically." Two lenses are entered here as saved records, and written in each format from
/// the command rules of docs/Optics-Conventions.md section 54 (the files are the test's own, not
/// copied from a vendor). Each import must give the hand-entered record exactly, and the same
/// rays through the hand-entered and the imported lens must come out bit for bit the same. The
/// first lens is rotationally symmetric (aspheric doublet, catalog glasses, vignetted field,
/// focus shift); the second folds the axis with a tilted mirror and has decentered and tilted
/// lens surfaces, which CODE V and OSLO give with their own tilt signs (converted by the readers).
/// <para>
/// A cross-check against CODE V's own output follows: RayOptics 0.9.10 (BSD 3-clause) ships
/// <c>codev/tests/ag_dblgauss.seq</c> with the CODE V listing of the same lens,
/// <c>ag_dblgauss.lis</c>. The imported lens must reproduce the listing's focal length and
/// paraxial marginal-ray heights, read from the listing at run time. The files are under the
/// gitignored <c>plans/sources/optics</c>; without them that test does nothing and says so.
/// </para>
/// </summary>
public sealed class CodeVOsloImportGateTests
{
    private readonly ITestOutputHelper _out;

    public CodeVOsloImportGateTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// A two-glass test catalog, "TESTCAT". The glass data are inputs, not reference values: N-BK7
    /// carries the Sellmeier coefficients of the OP-11 fixture's SELLMEIER1-BK7 (AGFFileReader.jl's
    /// Examples_N_BK7, MIT, as OpenSim.Tests/Optics/Fixtures/OP11_FIXTURE.agf says) and N-SF5 the
    /// made-up formula-1 coefficients of that fixture's SCHOTT-FORM.
    /// </summary>
    internal static OpticalCatalog TestCatalog() => AgfReader.ParseText(
        "NM N-BK7 2 517642 1.516800 64.1673 0 1 -\n" +
        "CD 1.03961212 0.00600069867 0.231792344 0.0200179144 1.01046945 103.560653 0 0 0 0\n" +
        "LD 0.3 2.5\n" +
        "NM N-SF5 1 516641 1.516372 64.0608 0 0 1\n" +
        "CD 2.27 -0.0101 0.0110 0.00012 0 0 0 0 0 0\n" +
        "LD 0.3 2.5\n", "TESTCAT");

    internal static IReadOnlyList<OpticalCatalog> Catalogs() => new[] { TestCatalog() };

    private static OpticsSurfaceParameterSettings P(int number, double value) => new() { Number = number, Value = value };

    // ------------------------------------------------------------------ lens 1: aspheric doublet

    /// <summary>The doublet as entered by hand. The numbers are inputs.</summary>
    internal static OpticsSettings DoubletByHand() => new()
    {
        Mode = OpticsModes.SavedName(OpticsMode.Sequential),
        Materials = new OpticsMaterialsSettings { ActiveCatalogs = new() { "TESTCAT" } },
        SystemData = new OpticsSystemSettings
        {
            Title = "Gate doublet",
            LensUnit = ConventionNames.LensUnits.SavedName(LensUnit.Millimeter),
            ApertureType = ApertureTypes.Names.SavedName(ApertureType.EntrancePupilDiameter),
            ApertureValue = 20,
            FieldType = FieldTypes.Names.SavedName(FieldType.Angle),
            Fields = new() { new() { X = 0, Y = 0 }, new() { X = 0, Y = 7, Weight = 2, CompressionY = 0.25 } },
            Wavelengths = new() { new() { Micrometers = 0.5875618, Weight = 2 }, new() { Micrometers = 0.4861327, Weight = 1 }, new() { Micrometers = 0.6562725, Weight = 1 } },
            PrimaryWavelength = 1,
            TemperatureCelsius = 20,
            PressureAtmospheres = 1,
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new()
            {
                Type = "Even Asphere", Radius = 50, Thickness = 6, Material = "TESTCAT:N-BK7", Conic = -0.5, IsStop = true,
                Parameters = new() { P(2, 1e-6), P(3, -2e-9) },
            },
            new() { Radius = -40, Thickness = 3, Material = "TESTCAT:N-SF5" },
            new() { Radius = -150, Thickness = 45.5 },
            new() { },
        },
    };

    /// <summary>The doublet as a CODE V sequence file: radius mode, wavelengths in nm, the
    /// vignetting of field 2 as VUY = VLY = 0.25 (VCY = 0.25, VDY = 0), the image at a focus shift
    /// of 0.5 after the last thickness of 45, glass names without their hyphen.</summary>
    internal const string DoubletSeq =
        "! OP-95 gate lens, written for the test\n" +
        "RDM;LEN \"VERSION: test\"\n" +
        "TITLE 'Gate doublet'\n" +
        "EPD 20; DIM M\n" +
        "WL 587.5618 486.1327 656.2725\n" +
        "REF 1\n" +
        "WTW 2 1 1\n" +
        "XAN 0 0\nYAN 0 7\nWTF 1 2\n" +
        "VUY 0 0.25\nVLY 0 0.25\n" +
        "SO 0 1e20\n" +
        "S 50 6 NBK7_TESTCAT\n" +
        "  STO\n  K -0.5\n  ASP\n  A 1e-06; B -2e-09\n" +
        "S -40 3 NSF5_TESTCAT\n" +
        "S -150 &\n   45\n" +
        "SI 0 0.5\n" +
        "GO\n";

    /// <summary>The doublet as an OSLO lens file: the beam radius 10 at surface 1 (object at
    /// infinity), the reference field angle 7 with a field table (fractions 0 and 1, pupil bounds
    /// ±0.75 for the second), wavelengths in µm (the primary first), the image surface's
    /// thickness a focus shift.</summary>
    internal const string DoubletLen =
        "// OP-95 gate lens, written for the test\n" +
        "LEN NEW \"Gate doublet\" 1.0 4\n" +
        "UNI 1.0; EBR 10.0; ANG 7.0\n" +
        "WV 0.5875618 0.4861327 0.6562725\n" +
        "WW 2 1 1\n" +
        "TH 1.0e20\nAIR\n" +
        "NXT\nRD 50.0\nTH 6.0\nCC -0.5\nAD 1e-06\nAE -2e-09\nGLA N-BK7\nAST\n" +
        "NXT\nRD -40.0\nTH 3.0\nGLA N-SF5\n" +
        "NXT\nRD -150.0\nTH 45.0\nAIR\n" +
        "NXT\nTH 0.5\n" +
        "END 4\n" +
        "RST NEW\n" +
        "F 1 0 0 0 0 0 -1 1 -1 1 1\n" +
        "F 2 1 0 0 0 0 -0.75 0.75 -1 1 2\n" +
        "END\n";

    // ------------------------------------------------------------------ lens 2: folded, tilted

    /// <summary>The folded lens by hand: a plane mirror tilted 22.5° about x that turns the axis
    /// by its tilt again after it (a fold), a lens surface decentered and tilted that returns to
    /// the axis after itself, and a surface with a lasting decenter and z tilt. Tilts are this
    /// program's (section 4); the files give the same surfaces in CODE V's and OSLO's signs.</summary>
    internal static OpticsSettings FoldByHand() => new()
    {
        Mode = OpticsModes.SavedName(OpticsMode.Sequential),
        Materials = new OpticsMaterialsSettings { ActiveCatalogs = new() { "TESTCAT" } },
        SystemData = new OpticsSystemSettings
        {
            Title = "Gate fold",
            LensUnit = ConventionNames.LensUnits.SavedName(LensUnit.Millimeter),
            ApertureType = ApertureTypes.Names.SavedName(ApertureType.EntrancePupilDiameter),
            ApertureValue = 10,
            FieldType = FieldTypes.Names.SavedName(FieldType.Angle),
            Fields = new() { new() },
            Wavelengths = new() { new() { Micrometers = 0.55, Weight = 1 } },
            PrimaryWavelength = 1,
            TemperatureCelsius = 20,
            PressureAtmospheres = 1,
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Thickness = 20, IsStop = true },
            new()
            {
                Thickness = -30, Material = SavedSurfaces.Mirror,
                TiltBefore = new OpticsTiltDecenterSettings { TiltX = -22.5 },
                TiltAfter = new OpticsTiltDecenterSettings { TiltX = -22.5 },
            },
            new()
            {
                Radius = -60, Thickness = -5, Material = "TESTCAT:N-BK7",
                TiltBefore = new OpticsTiltDecenterSettings { DecenterY = 0.5, TiltX = -2 },
                TiltAfter = new OpticsTiltDecenterSettings { Mode = SavedSurfaces.AfterModeName(TiltAfterKind.ReverseThisSurface) },
            },
            new()
            {
                Radius = 60, Thickness = -25,
                TiltBefore = new OpticsTiltDecenterSettings { DecenterX = 0.3, TiltZ = 10 },
            },
            new() { },
        },
    };

    /// <summary>CODE V: ADE 22.5 with BEN (bend), YDE/ADE with DAR (decenter and return), a
    /// basic XDE/CDE. ADE turns the other way from section 4's TiltX.</summary>
    internal const string FoldSeq =
        "RDM Y\nTIT 'Gate fold'\nDIM M\nEPD 10\nWL 550\nYAN 0\n" +
        "SO 0 1e20\n" +
        "S 0 20\nSTO\n" +
        "S 0 -30 REFL\nADE 22.5\nBEN\n" +
        "S -60 -5 NBK7_TESTCAT\nYDE 0.5\nADE 2\nDAR\n" +
        "S 60 -25\nXDE 0.3\nCDE 10\n" +
        "SI 0 0\n";

    /// <summary>OSLO: TLA 22.5 with BEN on a mirror, DCY/TLA with RCO (return on the surface
    /// itself), a basic DCX/TLC. TLA turns the other way from section 4's TiltX.</summary>
    internal const string FoldLen =
        "LEN NEW \"Gate fold\" 1 5\n" +
        "EBR 5; ANG 0; WV 0.55\n" +
        "TH 1e20\n" +
        "NXT; TH 20; AST\n" +
        "NXT; TH -30; RFL; TLA 22.5; BEN\n" +
        "NXT; RD -60; TH -5; GLA N-BK7; DCY 0.5; TLA 2; RCO\n" +
        "NXT; RD 60; TH -25; DCX 0.3; TLC 10\n" +
        "NXT\n" +
        "END 5\n";

    // ------------------------------------------------------------------ the comparison

    internal static string Json(OpticsSettings s) => JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// The same rays through both lenses, compared bit for bit (position, direction, optical path,
    /// status): a grid of rays from the plane of surface 1 at two angles in each wavelength, and,
    /// for an axial lens, the launcher's rays at each field and a pupil grid. Returns the number
    /// of rays that reached the image.
    /// </summary>
    internal static int AssertTraceIdentically(OpticsSettings expected, OpticsSettings actual, IReadOnlyList<OpticalCatalog> catalogs)
    {
        var la = ProjectLens.From(expected, catalogs)!;
        var lb = ProjectLens.From(actual, catalogs)!;
        var sa = SystemDataSettings.FromSettings(expected.SystemData, new List<string>());
        var sb = SystemDataSettings.FromSettings(actual.SystemData, new List<string>());
        int ok = 0;
        for (int w = 0; w < sa.Wavelengths.Wavelengths.Count; w++)
        {
            var ta = SequentialTrace.Create(la, sa, w);
            var tb = SequentialTrace.Create(lb, sb, w);
            foreach (double angle in new[] { 0.0, 0.02 })
                for (double x = -0.002; x <= 0.00201; x += 0.001)
                    for (double y = -0.002; y <= 0.00201; y += 0.001)
                    {
                        var dir = new Vector3D(0, Math.Sin(angle), Math.Cos(angle));
                        ok += Same(ta.Trace(ta.NewRay(new Vector3D(x, y, 0), dir)), tb.Trace(tb.NewRay(new Vector3D(x, y, 0), dir)));
                    }
            if (!la.IsAxial(out _)) continue;
            var launchA = new RayLauncher(la, sa);
            var launchB = new RayLauncher(lb, sb);
            foreach (double hy in new[] { 0.0, 1.0 })
                for (double px = -1; px <= 1; px += 0.5)
                    for (double py = -1; py <= 1; py += 0.5)
                        ok += Same(ta.Trace(launchA.Launch(ta, 0, hy, px, py)), tb.Trace(launchB.Launch(tb, 0, hy, px, py)));
        }
        return ok;
    }

    private static int Same(Ray a, Ray b)
    {
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.FailedAtSurface, b.FailedAtSurface);
        foreach (var (p, q) in new[] { (a.Position.X, b.Position.X), (a.Position.Y, b.Position.Y), (a.Position.Z, b.Position.Z),
                     (a.Direction.X, b.Direction.X), (a.Direction.Y, b.Direction.Y), (a.Direction.Z, b.Direction.Z), (a.OpticalPath, b.OpticalPath) })
            Assert.Equal(BitConverter.DoubleToInt64Bits(p), BitConverter.DoubleToInt64Bits(q));
        return a.Status == RayStatus.Ok ? 1 : 0;
    }

    [Fact]
    public void TheDoubletByHand_AndFromEachFormat_TraceIdentically()
    {
        var hand = DoubletByHand();
        var seq = CodeVLensReader.Read(DoubletSeq, Catalogs());
        var len = OsloLensReader.Read(DoubletLen, Catalogs());
        foreach (var n in seq.Notes.Concat(len.Notes)) _out.WriteLine(n);
        Assert.Empty(seq.Warnings);
        Assert.Empty(len.Warnings);
        Assert.Equal(Json(hand), Json(seq.Optics));
        Assert.Equal(Json(hand), Json(len.Optics));
        Assert.True(AssertTraceIdentically(hand, seq.Optics, Catalogs()) > 0, "no ray reached the image");
        Assert.True(AssertTraceIdentically(hand, len.Optics, Catalogs()) > 0, "no ray reached the image");
    }

    [Fact]
    public void TheFoldedLensByHand_AndFromEachFormat_TraceIdentically()
    {
        var hand = FoldByHand();
        var seq = CodeVLensReader.Read(FoldSeq, Catalogs());
        var len = OsloLensReader.Read(FoldLen, Catalogs());
        Assert.Equal(Json(hand), Json(seq.Optics));
        Assert.Equal(Json(hand), Json(len.Optics));
        Assert.True(AssertTraceIdentically(hand, seq.Optics, Catalogs()) > 0, "no ray reached the image");
        Assert.True(AssertTraceIdentically(hand, len.Optics, Catalogs()) > 0, "no ray reached the image");
    }

    // ------------------------------------------------------------------ CODE V's own listing

    private static string? Sources()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string folder = Path.Combine(dir.FullName, "plans", "sources", "optics");
            if (Directory.Exists(folder)) return folder;
        }
        return null;
    }

    private static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// ag_dblgauss.seq imported (glass names unchecked), each glass replaced by the constant index
    /// CODE V lists for it at the reference wavelength 587.6 nm (the listing's REFRACTIVE INDICES
    /// table), reproduces the listing's EFL (INFINITE CONJUGATES) and the paraxial marginal-ray
    /// heights HMY of its "fio" table at surface 1, the stop and the image (the image after the
    /// file's focus shift). Every expected number is read from the listing when the test runs.
    /// <para>
    /// Tolerance, derived: half a unit in the last printed digit of the expected value, plus the
    /// effect of the listing's indices being rounded to six decimals, bounded to first order by
    /// Σ over the glasses of |change of the value when that glass's index moves by 5e-7|, computed
    /// here by moving each index; that sum is taken twice over to cover the second order.
    /// </para>
    /// </summary>
    [Fact]
    public void CodeVsDoubleGauss_ReproducesTheFocalLengthAndMarginalRayOfItsOwnListing()
    {
        string? sources = Sources();
        string dir = sources is null ? "" : Path.Combine(sources, "pyref-venv", "Lib", "site-packages", "rayoptics", "codev", "tests");
        string seqPath = Path.Combine(dir, "ag_dblgauss.seq"), lisPath = Path.Combine(dir, "ag_dblgauss.lis");
        if (sources is null || !File.Exists(seqPath) || !File.Exists(lisPath))
        {
            _out.WriteLine("DID NOTHING: the RayOptics CODE V test files are not on this machine.");
            return;
        }
        var lis = File.ReadAllLines(lisPath);

        // The listing's index table: "GLASS CODE  656.30  587.60  486.10", then one row per glass.
        int header = Array.FindIndex(lis, l => l.Contains("GLASS CODE"));
        Assert.True(header > 0, "the listing has no index table");
        var waves = lis[header].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(2).ToList();
        int column = waves.IndexOf("587.60");
        Assert.True(column >= 0, "the index table has no 587.60 column");
        var indices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int i = header + 1; i < lis.Length && lis[i].Trim().Length > 0; i++)
        {
            var f = lis[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            indices[f[0]] = Num(f[1 + column]);
        }
        string eflLine = lis.First(l => l.TrimStart().StartsWith("EFL ", StringComparison.Ordinal));
        string eflText = eflLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
        int fio = Array.FindIndex(lis, l => l.Contains("HMY"));
        Assert.True(fio > 0, "the listing has no paraxial ray table");
        var heights = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = fio + 1; i < lis.Length && !lis[i].StartsWith("CODE V>", StringComparison.Ordinal); i++)
        {
            var f = lis[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 2) heights[f[0]] = f[1];
        }

        var read = CodeVLensReader.Read(File.ReadAllText(seqPath), catalogs: null);
        foreach (var n in read.Notes) _out.WriteLine(n);
        var surfaces = read.Optics.Surfaces!;
        int stop = surfaces.FindIndex(s => s.IsStop);
        int image = surfaces.Count - 1;
        var glassOf = new Dictionary<int, string>();
        for (int j = 0; j < surfaces.Count; j++)
            if (surfaces[j].Material is { } m)
            {
                var parts = m.Split(':');
                glassOf[j] = $"{parts[1]}_{parts[0]}";   // back to CODE V's name_catalog
                Assert.True(indices.ContainsKey(glassOf[j]), $"the listing has no index for {glassOf[j]}");
            }

        (double Efl, double H1, double HStop, double HImage) Evaluate(string? moved)
        {
            var optics = read.Optics with
            {
                Surfaces = surfaces.Select((s, j) => glassOf.TryGetValue(j, out var g)
                    ? s with { Material = null, Index = indices[g] + (g == moved ? 5e-7 : 0) } : s).ToList(),
            };
            var lens = ProjectLens.From(optics, Array.Empty<OpticalCatalog>())!;
            var system = SystemDataSettings.FromSettings(optics.SystemData, new List<string>());
            var p = ParaxialSystem.Create(lens.ToParaxialLens(), system);
            var h = p.MarginalRay.Heights;
            double sign = Math.Sign(h[1]);
            return (p.EffectiveFocalLength!.Value * 1e3, sign * h[1] * 1e3, sign * h[stop] * 1e3, sign * h[image] * 1e3);
        }

        var nominal = Evaluate(null);
        var spread = (Efl: 0.0, H1: 0.0, HStop: 0.0, HImage: 0.0);
        foreach (var g in glassOf.Values.Distinct())
        {
            var moved = Evaluate(g);
            spread = (spread.Efl + Math.Abs(moved.Efl - nominal.Efl), spread.H1 + Math.Abs(moved.H1 - nominal.H1),
                spread.HStop + Math.Abs(moved.HStop - nominal.HStop), spread.HImage + Math.Abs(moved.HImage - nominal.HImage));
        }

        static double HalfUnit(string printed)
        {
            int dot = printed.IndexOf('.');
            return dot < 0 ? 0.5 : 0.5 * Math.Pow(10, -(printed.Length - dot - 1));
        }
        void Check(string what, double value, string printed, double sensitivity)
        {
            double expected = Num(printed), tolerance = HalfUnit(printed) + 2 * sensitivity;
            _out.WriteLine($"{what}: {value:R} against the listing's {printed} (± {tolerance:G3})");
            Assert.InRange(value, expected - tolerance, expected + tolerance);
        }
        Check("EFL", nominal.Efl, eflText, spread.Efl);
        Check("HMY at surface 1", nominal.H1, heights["1"], spread.H1);
        Check("HMY at the stop", nominal.HStop, heights["STO"], spread.HStop);
        Check("HMY at the image", nominal.HImage, heights["IMG"], spread.HImage);
    }

    /// <summary>The other RayOptics CODE V decenter files read without a problem, and the tilt of
    /// dec_tilt_test.seq's first fold mirror (ADE 60 with BEN) comes out as section 4's −60° before
    /// and after the surface.</summary>
    [Fact]
    public void RayOpticsDecenterFiles_ReadWithoutProblems_InThisProgramsTiltSigns()
    {
        string? sources = Sources();
        string dir = sources is null ? "" : Path.Combine(sources, "pyref-venv", "Lib", "site-packages", "rayoptics", "codev", "tests");
        if (sources is null || !Directory.Exists(dir))
        {
            _out.WriteLine("DID NOTHING: the RayOptics CODE V test files are not on this machine.");
            return;
        }
        foreach (var name in new[] { "dec_tilt_test.seq", "dar_test.seq", "tilt_test.seq", "dec_test.seq" })
        {
            var read = CodeVLensReader.Read(File.ReadAllText(Path.Combine(dir, name)), catalogs: null);
            Assert.Empty(read.Warnings);
        }
        var fold = CodeVLensReader.Read(File.ReadAllText(Path.Combine(dir, "dec_tilt_test.seq")), catalogs: null).Optics.Surfaces![2];
        Assert.Equal(SavedSurfaces.Mirror, fold.Material);
        Assert.Equal(-60, fold.TiltBefore!.TiltX);
        Assert.Equal(-60, fold.TiltAfter!.TiltX);
    }
}
