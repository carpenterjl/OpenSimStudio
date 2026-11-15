using OpenSim.Core.Model;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Sequential;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics;

/// <summary>
/// Wave 10 wiring: OP-25's freeform types in the Zemax lens file reader and writer
/// (docs/Optics-Conventions.md sections 52 and 55). The TYPE codes come from OpticStudio 2025 R2
/// help, "SETSURFACEPROPERTY, SURP" (see <see cref="ZemaxKeywords.SurfaceTypes"/>); the numbers
/// below are inputs, not reference values. Parameter numbers follow each type's layout in
/// <see cref="SurfaceTypes"/>: 0–12 are PARM, 13 and up are XDAT n − 12.
/// </summary>
public sealed class ZemaxFreeformTypesTests
{
    private readonly ITestOutputHelper _out;

    public ZemaxFreeformTypesTests(ITestOutputHelper output) => _out = output;

    private static OpticsSurfaceParameterSettings P(int number, double value) => new() { Number = number, Value = value };

    private static List<OpticsSurfaceParameterSettings> Numbered(int first, params double[] values) =>
        values.Select((v, i) => P(first + i, v)).ToList();

    public static TheoryData<string, string> Types => new()
    {
        { "Polynomial", "POLYNOMI" },
        { "Extended Polynomial", "XPOLYNOM" },
        { "Zernike Fringe Sag", "FZERNSAG" },
        { "Zernike Standard Sag", "SZERNSAG" },
        { "Zernike Annular Standard Sag", "AZERNSAG" },
        { "Off-Axis Conic Freeform", "OFFAXISCF" },
        { "Radial NURBS", "RADNURBS" },
    };

    /// <summary>One surface of the type, with numbers in its parameter and extra-data columns.</summary>
    private static OpticsSurfaceSettings Freeform(string type) => type switch
    {
        "Polynomial" => new() { Type = type, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(1, 1e-3), P(2, -2e-7), P(5, 5e-4), P(8, 3e-12) } },
        "Extended Polynomial" => new() { Type = type, Radius = 80, Thickness = 3, Material = "N-BK7", IsStop = true, Conic = -0.2,
            ExtraData = new() { P(13, 3), P(14, 10), P(15, 1e-3), P(16, -2e-4), P(17, 5e-5) } },
        "Zernike Fringe Sag" or "Zernike Standard Sag" => new() { Type = type, Radius = -120, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(0, 1), P(2, 1e-6), P(9, 0.1), P(10, -0.2) },
            ExtraData = Numbered(13, 4, 12, 0.01, -0.002, 0.003, 4e-4) },
        "Zernike Annular Standard Sag" => new() { Type = type, Radius = 90, Thickness = 3, Material = "N-BK7", IsStop = true,
            Parameters = new() { P(0, 1), P(1, 2e-4) },
            ExtraData = Numbered(13, 0.3, 3, 10, 0.02, -0.001, 5e-4) },
        "Off-Axis Conic Freeform" => new() { Type = type, Radius = 200, Thickness = 3, Material = "N-BK7", IsStop = true, Conic = -1,
            ExtraData = Numbered(13, 5, 3, 20, 1e-3, -1e-4, 2e-5) },
        "Radial NURBS" => new() { Type = type, Thickness = 3, Material = "N-BK7", IsStop = true,
            // 4 control points: y, z, w of point n in parameters 3n + 11, 3n + 12, 3n + 13. Point 1
            // is at y = z = 0, and a zero is neither written nor read back, so 14 and 15 are absent.
            ExtraData = new[] { P(13, 4) }.Concat(Numbered(16, 1, 2, 0.01, 1, 4, 0.04, 0.8, 6, 0.09, 1)).ToList() },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No freeform test surface."),
    };

    private static OpticsSettings LensWith(OpticsSurfaceSettings freeform)
    {
        var lens = ZemaxLensFileTests.EverySupportedFeature();
        var s = lens.Surfaces!;
        return lens with { Surfaces = new() { s[0], s[1], freeform, new() { Thickness = 0 } } };
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void ExportThenImport_IsTheIdentity_ForEachMappedFreeformType(string type, string code)
    {
        Assert.NotNull(SurfaceTypes.Find(type));
        var lens = LensWith(Freeform(type));
        var written = ZemaxLensWriter.Write(lens);
        Assert.Empty(written.Warnings);
        Assert.Contains($"  TYPE {code}\n", written.Text.Replace("\r\n", "\n"));
        _out.WriteLine(written.Text);
        var read = ZemaxLensReader.Read(written.Text, catalogs: null);
        Assert.Empty(read.Warnings);
        ZemaxLensFileTests.AssertSameLens(lens, read.Optics);
    }

    private const string GridSagFile =
        "VERS 1\nMODE SEQ\nNAME G\nUNIT MM X W X CM MR CPMM\nENPD 10\nFTYP 0 0 1 1 0 0 0\nXFLN 0\nYFLN 0\nFWGN 1\nWAVM 1 0.55 1\nPWAV 1\n" +
        "SURF 0\n  TYPE STANDARD\n  CURV 0 0 0 0 0 \"\"\n  DISZ INFINITY\n" +
        "SURF 1\n  TYPE GRID_SAG\n  CURV 0.01 0 0 0 0 \"\"\n  DISZ 5\n" +
        "SURF 2\n  TYPE STANDARD\n  DISZ 0\n";

    [Fact]
    public void AGridSag_IsRefusedByName_BothWays()
    {
        var ex = Assert.Throws<ZemaxFileException>(() => ZemaxLensReader.Read(GridSagFile, null));
        Assert.Contains(ex.Problems, p => p.Contains("Line 17") && p.Contains("TYPE GRID_SAG") && p.Contains(".DAT"));

        var lens = LensWith(new OpticsSurfaceSettings { Type = "Grid Sag", Radius = 100, Thickness = 3, Material = "N-BK7", IsStop = true });
        var wex = Assert.Throws<ZemaxFileException>(() => ZemaxLensWriter.Write(lens));
        Assert.Contains(wex.Problems, p => p.StartsWith("Surface 2") && p.Contains("'Grid Sag' is not written") && p.Contains(".DAT"));
    }
}
