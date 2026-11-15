using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Tests.Optics;

namespace OpenSim.Tests.Cli;

/// <summary>OP-96: "opensim optics export" writing a CAD file.</summary>
public sealed class OpticsCadCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-cad-cli-tests", Guid.NewGuid().ToString("N"));

    public OpticsCadCliTests() => Directory.CreateDirectory(_dir);

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

    private string Lens()
    {
        string osslens = Path.Combine(_dir, "singlet" + OpticsFileSerializer.FileExtension);
        new OpticsFileSerializer().Save(new OpticsLensFile { Name = "singlet", Optics = CadExportFixtures.SingletSettings() }, osslens);
        return osslens;
    }

    [Fact]
    public void Export_ByTheOutExtension_OrByFormat_WritesTheCadFile()
    {
        string lens = Lens();
        string step = Path.Combine(_dir, "singlet.step");
        var a = Run("optics", "export", lens, "--out", step, "--builtin-catalogs");
        Assert.True(a.Code == 0, a.Err);
        Assert.StartsWith("ISO-10303-21;", File.ReadAllText(step));
        Assert.Contains("STEP AP214: 1 solid(s)", a.Out);

        var b = Run("optics", "export", lens, "--format", "dxf", "--rays", "none", "--builtin-catalogs");
        Assert.True(b.Code == 0, b.Err);
        Assert.True(File.Exists(Path.Combine(_dir, "singlet.dxf")));

        var c = Run("optics", "export", lens, "--format", "sat");
        Assert.Equal(1, c.Code);
        Assert.Contains("OP-76", c.Err);
    }
}
