using OpenSim.Core.Model;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Sequential;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics;

/// <summary>
/// Wave 11 wiring: OP-26's diffractive, grating and holographic types in the Zemax lens file
/// reader and writer (docs/Optics-Conventions.md sections 52 and 57), and OP-32's composite
/// add-ons refused by the writer. The TYPE codes come from OpticStudio 2025 R2 help,
/// "SETSURFACEPROPERTY, SURP" (see <see cref="ZemaxKeywords.SurfaceTypes"/>); the numbers below
/// are inputs, not reference values. Parameter numbers follow each type's layout in
/// <see cref="SurfaceTypes"/>: 0–12 are PARM, 13 and up are XDAT n − 12.
/// </summary>
public sealed class ZemaxDiffractiveTypesTests
{
    private readonly ITestOutputHelper _out;

    public ZemaxDiffractiveTypesTests(ITestOutputHelper output) => _out = output;

    private static OpticsSurfaceParameterSettings P(int number, double value) => new() { Number = number, Value = value };

    private static List<OpticsSurfaceParameterSettings> Numbered(int first, params double[] values) =>
        values.Select((v, i) => P(first + i, v)).ToList();

    public static TheoryData<string, string> Types => new()
    {
        { "Diffraction Grating", "DGRATING" },
        { "Toroidal Grating", "TOROGRAT" },
        { "Variable Line Space Grating", "VARLSGRT" },
        { "Binary Optic 1", "BINARY_1" },
        { "Binary Optic 2", "BINARY_2" },
        { "Zernike Fringe Phase", "FZERNPHA" },
        { "Zernike Standard Phase", "SZERNPHA" },
        { "Zernike Annular Phase", "AZERNPHA" },
        { "Hologram 1", "HOLOGRM1" },
        { "Hologram 2", "HOLOGRM2" },
        { "Toroidal Hologram", "TOROHOLO" },
    };

    /// <summary>One surface of the type, with non-zero numbers in its parameter and extra-data
    /// columns (a zero is neither written nor read back).</summary>
    private static OpticsSurfaceSettings Diffractive(string type) => type switch
    {
        "Diffraction Grating" => new() { Type = type, Radius = 200, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(1, 0.6), P(2, 1) } },
        "Toroidal Grating" => new() { Type = type, Radius = 150, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(1, 80), P(2, 0.3), P(3, -1) } },
        // "Only plane gratings are supported": no radius or conic.
        "Variable Line Space Grating" => new() { Type = type, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(1, 1), P(2, 500), P(3, 0.9), P(4, 0.8), P(5, 0.6) } },
        "Binary Optic 1" => new() { Type = type, Radius = 120, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(0, 1), P(1, -2e-4), P(9, 1) },
            ExtraData = Numbered(13, 3, 10, 0.5, -0.25, 0.125) },
        "Binary Optic 2" => new() { Type = type, Radius = -90, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(0, 1), P(1, 1e-4), P(2, -3e-8) },
            ExtraData = Numbered(13, 2, 10, -50, 3) },
        "Zernike Fringe Phase" or "Zernike Standard Phase" => new() { Type = type, Radius = 100, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(0, 1), P(1, 1) },
            ExtraData = Numbered(13, 4, 10, 0.2, -0.1, 0.05, 0.3) },
        "Zernike Annular Phase" => new() { Type = type, Radius = 100, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(0, -1), P(1, 1), P(3, 0.3) },
            ExtraData = Numbered(13, 3, 10, 0.2, -0.1, 0.05) },
        "Hologram 1" or "Hologram 2" => new() { Type = type, Radius = 250, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = Numbered(1, 1, 2, -100, -1, 3, -120, 0.6328, 1) },
        "Toroidal Hologram" => new() { Type = type, Radius = 250, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(1, 80), P(2, 1e-4) },
            ExtraData = Numbered(13, 1, 2, -100, -1, 3, -120, 0.6328, 1, -1) },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No diffractive test surface."),
    };

    private static OpticsSettings LensWith(OpticsSurfaceSettings surface)
    {
        var lens = ZemaxLensFileTests.EverySupportedFeature();
        var s = lens.Surfaces!;
        return lens with { Surfaces = new() { s[0], s[1], surface, new() { Thickness = 0 } } };
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void ExportThenImport_IsTheIdentity_ForEachMappedDiffractiveType(string type, string code)
    {
        Assert.NotNull(SurfaceTypes.Find(type));
        var lens = LensWith(Diffractive(type));
        var written = ZemaxLensWriter.Write(lens);
        Assert.Empty(written.Warnings);
        Assert.Contains($"  TYPE {code}\n", written.Text.Replace("\r\n", "\n"));
        _out.WriteLine(written.Text);
        var read = ZemaxLensReader.Read(written.Text, catalogs: null);
        Assert.Empty(read.Warnings);
        ZemaxLensFileTests.AssertSameLens(lens, read.Optics);
    }

    /// <summary>A diffraction grating as RayOptics reads one (zmxread.py handle_types_and_params:
    /// PARM 1 the lines per micrometer, PARM 2 the order) reads into the layout's parameters 1
    /// and 2.</summary>
    [Fact]
    public void ADGratingFile_ReadsLinesAndOrderIntoParameters1And2()
    {
        const string file =
            "VERS 1\nMODE SEQ\nNAME G\nUNIT MM X W X CM MR CPMM\nENPD 10\nFTYP 0 0 1 1 0 0 0\nXFLN 0\nYFLN 0\nFWGN 1\nWAVM 1 0.55 1\nPWAV 1\n" +
            "SURF 0\n  TYPE STANDARD\n  CURV 0 0 0 0 0 \"\"\n  DISZ INFINITY\n" +
            "SURF 1\n  STOP\n  TYPE DGRATING\n  CURV 0 0 0 0 0 \"\"\n  PARM 1 0.6\n  PARM 2 -1\n  DISZ 5\n" +
            "SURF 2\n  TYPE STANDARD\n  DISZ 0\n";
        var read = ZemaxLensReader.Read(file, catalogs: null);
        var s = read.Optics.Surfaces![1];
        Assert.Equal("Diffraction Grating", s.Type);
        Assert.Equal(0.6, s.Parameters!.Single(p => p.Number == 1).Value);
        Assert.Equal(-1, s.Parameters!.Single(p => p.Number == 2).Value);
    }

    /// <summary>A surface with composite add-ons is refused by name: no source shows how a .zmx
    /// stores them, and before wave 11 the writer dropped them silently.</summary>
    [Fact]
    public void ASurfaceWithCompositeAddOns_IsRefusedByName()
    {
        var lens = LensWith(new OpticsSurfaceSettings
        {
            Radius = 100, Thickness = 3, Material = "N-BK7", IsStop = true,
            AddOns = new() { new OpticsCompositeAddOnSettings { Radius = -2000 }, new OpticsCompositeAddOnSettings { Type = "Even Asphere", Parameters = new() { P(2, 1e-6) } } },
        });
        var ex = Assert.Throws<ZemaxFileException>(() => ZemaxLensWriter.Write(lens));
        Assert.Contains(ex.Problems, p => p.StartsWith("Surface 2") && p.Contains("2 composite add-ons are not written"));

        var lenient = ZemaxLensWriter.Write(lens, new ZemaxExportOptions { UnsupportedAsWarnings = true });
        Assert.Contains(lenient.Warnings, w => w.Contains("composite add-ons are not written"));
    }
}
