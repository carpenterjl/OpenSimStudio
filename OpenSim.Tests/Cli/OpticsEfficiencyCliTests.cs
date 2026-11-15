using OpenSim.Cli;

namespace OpenSim.Tests.Cli;

/// <summary>OP-47: <c>opensim optics efficiency</c> (docs/Optics-Conventions.md §61). The grating
/// is DiffractionEfficiencyTests' EMpy case (n 1.5 lamellar on n 1.5, period 1.5 µm, depth 0.5 µm,
/// λ 1 µm, 10°, TE, 40 harmonics), whose transmitted +1 order EMpy gives as 0.238894148962097.</summary>
public sealed class OpticsEfficiencyCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opensim-op47-" + Guid.NewGuid().ToString("N"));

    public OpticsEfficiencyCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliRunner.Run(args, stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Efficiency_PrintsTheOrders_AndWritesCsv()
    {
        string csv = Path.Combine(_dir, "orders.csv");
        var (code, output, error) = Run("optics", "efficiency", "--period", "1.5", "--wavelength", "1", "--depth", "0.5",
            "--angle", "10", "--harmonics", "40", "--out", csv);
        Assert.True(code == 0, error);
        Assert.Contains("0.23889415", output);
        Assert.Contains("sum reflected", output);
        var lines = File.ReadAllLines(csv);
        Assert.Equal("order,reflected,transmitted,reflected_angle_deg,transmitted_angle_deg", lines[0]);
        Assert.Equal(1 + 81, lines.Length);
    }

    [Fact]
    public void Efficiency_NeedsThePeriodAndWavelength()
    {
        var (code, _, error) = Run("optics", "efficiency", "--wavelength", "1");
        Assert.NotEqual(0, code);
        Assert.Contains("--period", error);
        Assert.Contains("efficiency", CliRunner.OpticsSubcommands);
    }
}
