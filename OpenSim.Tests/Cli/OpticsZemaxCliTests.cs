using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Tests.Optics;

namespace OpenSim.Tests.Cli;

/// <summary>OP-94: "opensim optics export" and "opensim optics import".</summary>
public sealed class OpticsZemaxCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-zemax-cli-tests", Guid.NewGuid().ToString("N"));

    public OpticsZemaxCliTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void ExportThenImport_OnTheCommandLine_GivesTheLensBack()
    {
        var lens = ZemaxLensFileTests.EverySupportedFeature();
        string osslens = Path.Combine(_dir, "lens" + OpticsFileSerializer.FileExtension);
        new OpticsFileSerializer().Save(new OpticsLensFile { Name = "lens", Optics = lens }, osslens);

        string zmx = Path.Combine(_dir, "lens.zmx");
        var export = Run("optics", "export", osslens, "--out", zmx);
        Assert.True(export.Code == 0, export.Err);
        Assert.True(File.Exists(zmx));

        // The glasses are not in the built-in catalogs, so the import lists them and goes on.
        string back = Path.Combine(_dir, "back" + OpticsFileSerializer.FileExtension);
        var import = Run("optics", "import", zmx, "--out", back, "--lenient", "--builtin-catalogs");
        Assert.True(import.Code == 0, import.Err);
        Assert.Contains("Not read:", import.Out);
        ZemaxLensFileTests.AssertSameLens(lens, new OpticsFileSerializer().Load(back).Optics!);

        // Strict, the same import fails with exit code 2, naming a glass.
        var strict = Run("optics", "import", zmx, "--out", back, "--builtin-catalogs");
        Assert.Equal(2, strict.Code);
        Assert.Contains("N-BK7", strict.Err);
    }
}
