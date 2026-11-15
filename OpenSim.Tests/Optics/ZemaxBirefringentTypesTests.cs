using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Birefringent;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics;

/// <summary>
/// Wave 12 wiring: OP-53's Birefringent In and Birefringent Out in the Zemax lens file reader
/// and writer (docs/Optics-Conventions.md sections 52 and 62). The TYPE codes BIRE__IN and
/// BIRE_OUT come from OpticStudio 2025 R2 help, "SETSURFACEPROPERTY, SURP" (see
/// <see cref="ZemaxKeywords.SurfaceTypes"/>); the numbers below are inputs, not reference values.
/// The GLAS line names the ordinary glass and the extraordinary glass is that name with "-E"
/// appended (help, "Birefringent In and Birefringent Out"). The calcite glasses are the
/// refractiveindex.info seed's Ghosh entries renamed, as in <see cref="BirefringentTests"/>.
/// </summary>
public sealed class ZemaxBirefringentTypesTests
{
    private readonly ITestOutputHelper _out;

    public ZemaxBirefringentTypesTests(ITestOutputHelper output) => _out = output;

    private static OpticsSurfaceParameterSettings P(int number, double value) => new() { Number = number, Value = value };

    private static (OpticalMaterial Ordinary, OpticalMaterial Extraordinary) Calcite()
    {
        var all = RefractiveIndexInfoSeed.Catalogs.SelectMany(c => c.Materials).ToList();
        return (all.Single(m => m.Name == "CaCO3-Ghosh-o") with { Name = "CALCITE", Catalog = "TEST" },
                all.Single(m => m.Name == "CaCO3-Ghosh-e") with { Name = "CALCITE-E", Catalog = "TEST" });
    }

    private static OpticsSettings Lens(OpticsSurfaceSettings crystal)
    {
        var lens = ZemaxLensFileTests.EverySupportedFeature();
        var s = lens.Surfaces!;
        return lens with
        {
            Surfaces = new()
            {
                s[0], s[1], crystal,
                new() { Type = BirefringentOutShape.Name, Radius = -60, Thickness = 10 },
                new() { Thickness = 0 },
            },
        };
    }

    private static OpticsSurfaceSettings Crystal() => new()
    {
        Type = BirefringentInShape.Name, Radius = 80, Thickness = 3, Material = "CALCITE", IsStop = true,
        Parameters = new() { P(0, 2), P(1, 1), P(2, 0.5), P(4, 1), P(5, 1) },
    };

    [Fact]
    public void ExportThenImport_IsTheIdentity_ForBirefringentInAndOut()
    {
        var lens = Lens(Crystal());
        var written = ZemaxLensWriter.Write(lens);
        Assert.Empty(written.Warnings);
        string text = written.Text.Replace("\r\n", "\n");
        _out.WriteLine(text);
        Assert.Contains("  TYPE BIRE__IN\n", text);
        Assert.Contains("  TYPE BIRE_OUT\n", text);
        Assert.Contains("  GLAS CALCITE 0 ", text);
        Assert.DoesNotContain("CALCITE-E", text);
        var read = ZemaxLensReader.Read(written.Text, catalogs: null);
        Assert.Empty(read.Warnings);
        ZemaxLensFileTests.AssertSameLens(lens, read.Optics);
    }

    private const string Head =
        "VERS 1\nMODE SEQ\nNAME B\nUNIT MM X W X CM MR CPMM\nENPD 2\nFTYP 0 0 1 1 0 0 0\nXFLN 0\nYFLN 0\nFWGN 1\nWAVM 1 0.55 1\nPWAV 1\n" +
        "SURF 0\n  TYPE STANDARD\n  CURV 0 0 0 0 0 \"\"\n  DISZ INFINITY\n";

    private static string File(string glassLine, string extra = "") => Head +
        "SURF 1\n  STOP\n  TYPE BIRE__IN\n  CURV 0 0 0 0 0 \"\"\n  PARM 1 1\n  PARM 4 1\n  PARM 5 1\n" + extra + "  DISZ 2\n" + glassLine +
        "SURF 2\n  TYPE BIRE_OUT\n  CURV 0 0 0 0 0 \"\"\n  DISZ 10\n" +
        "SURF 3\n  TYPE STANDARD\n  DISZ 0\n";

    /// <summary>With both calcite glasses in the catalogs the file reads, and the saved lens
    /// finds its extraordinary glass by the "-E" rule; without CALCITE-E the import is refused,
    /// naming the missing glass.</summary>
    [Fact]
    public void ABireInFile_ReadsTheOrdinaryGlass_AndNeedsItsMinusETwinInTheCatalogs()
    {
        var (o, e) = Calcite();
        var both = new OpticalCatalog { Name = "TEST", Materials = new[] { o, e } };
        var read = ZemaxLensReader.Read(File("  GLAS CALCITE 0 0 0 0 0 0 0 0 0 0\n"), new[] { both });
        Assert.Empty(read.Warnings);
        var s = read.Optics.Surfaces!;
        Assert.Equal(BirefringentInShape.Name, s[1].Type);
        Assert.Equal(BirefringentOutShape.Name, s[2].Type);
        Assert.Equal("CALCITE", s[1].Material);
        var lens = SavedSurfaces.ToLens(s, LensUnit.Millimeter, both.Find);
        var shape = Assert.IsType<BirefringentInShape>(lens.Surfaces[1].Shape);
        Assert.Equal(1, shape.Mode);
        Assert.Same(e, shape.Extraordinary!.Material);

        var onlyOrdinary = new OpticalCatalog { Name = "TEST", Materials = new[] { o } };
        var ex = Assert.Throws<ZemaxFileException>(() => ZemaxLensReader.Read(File("  GLAS CALCITE 0 0 0 0 0 0 0 0 0 0\n"), new[] { onlyOrdinary }));
        Assert.Contains(ex.Problems, p => p.Contains("BIRE__IN") && p.Contains("'CALCITE-E'"));
    }

    /// <summary>A Birefringent In whose glass is a model glass has no "-E" glass: refused by
    /// name on import and on export.</summary>
    [Fact]
    public void ABireInWithoutAGlassName_IsRefusedByName_BothWays()
    {
        var ex = Assert.Throws<ZemaxFileException>(() => ZemaxLensReader.Read(File("  GLAS ___BLANK 1 0 1.6 50 0 0 0 0 0 0\n"), catalogs: null));
        Assert.Contains(ex.Problems, p => p.Contains("BIRE__IN surface needs a GLAS line with a glass name"));

        var lens = Lens(Crystal() with { Material = null, Index = 1.6 });
        var wex = Assert.Throws<ZemaxFileException>(() => ZemaxLensWriter.Write(lens));
        Assert.Contains(wex.Problems, p => p.StartsWith("Surface 2") && p.Contains("Birefringent In is written with its glass name only") && p.Contains("a constant index"));
    }

    /// <summary>XDAT 1 is the Birefringent In's parameter 13, the gradient mode, as the help
    /// says ("the Gradient Mode is defined by Extra Data Value 1"); a non-zero mode then reads
    /// into the saved record and is refused by the layout when the lens is built.</summary>
    [Fact]
    public void Xdat1_IsTheGradientMode_Parameter13()
    {
        var read = ZemaxLensReader.Read(File("  GLAS CALCITE 0 0 0 0 0 0 0 0 0 0\n", "  XDAT 1 2 0 0 1 0 0 \"\"\n"), catalogs: null);
        Assert.Equal(2, read.Optics.Surfaces![1].ExtraData!.Single(x => x.Number == 13).Value);
        var (o, e) = Calcite();
        var both = new OpticalCatalog { Name = "TEST", Materials = new[] { o, e } };
        var ex = Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(read.Optics.Surfaces!, LensUnit.Millimeter, both.Find));
        Assert.Contains("gradient mode 2 (parameter 13)", ex.Message);
    }
}
