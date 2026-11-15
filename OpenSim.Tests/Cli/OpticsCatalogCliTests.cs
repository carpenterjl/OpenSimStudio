using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Tests.Optics;

namespace OpenSim.Tests.Cli;

/// <summary>OP-16: "opensim optics catalog". The lens files are written with OP-94's writer;
/// the 100 mm focal length is R1/(n − 1) = 50/0.5 (derived), the radii and index are inputs.</summary>
public sealed class OpticsCatalogCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-catalog-cli-tests", Guid.NewGuid().ToString("N"));

    public OpticsCatalogCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliRunner.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static OpticsSurfaceSettings S(double? r, double t, double? n = null) => new() { Radius = r, Thickness = t, Index = n };

    [Fact]
    public void ACatalogSearch_PrintsTheReport_AndSavesThePrivateCatalog()
    {
        string vendor = Path.Combine(_dir, "ACME");
        Directory.CreateDirectory(vendor);
        ZemaxLensFiles.Export(LensCatalogTests.Lens("PCX-100", 10, new[] { S(50, 4, 1.5), S(null, 96) }), Path.Combine(vendor, "PCX-100.zmx"));
        ZemaxLensFiles.Export(LensCatalogTests.Lens("MEN-200", 10, new[] { S(50, 3, 1.5), S(100, 190) }), Path.Combine(vendor, "MEN-200.zmx"));

        string saved = Path.Combine(_dir, "acme" + LensCatalog.FileExtension);
        var run = Run("optics", "catalog", vendor, "--efl", "95:105", "--save", saved, "--builtin-catalogs");
        Assert.True(run.Code == 0, run.Err);
        Assert.Contains("Catalog report: ACME", run.Out);
        Assert.Contains("1 lens.", run.Out);
        Assert.Contains("PCX-100", run.Out);
        Assert.Equal(2, LensCatalog.Load(saved).Lenses.Count);

        var shape = Run("optics", "catalog", saved, "--shape", "M");
        Assert.True(shape.Code == 0, shape.Err);
        Assert.Contains("1 lens.", shape.Out);
        Assert.Contains("MEN-200", shape.Out);

        // Usage errors exit with code 1, naming the problem.
        var badShape = Run("optics", "catalog", saved, "--shape", "X");
        Assert.Equal(1, badShape.Code);
        Assert.Contains("'X'", badShape.Err);
        Assert.Equal(1, Run("optics", "catalog", saved, "--efl", "5").Code);
    }
}
