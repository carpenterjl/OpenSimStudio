using System.Numerics;
using OpenSim.Core.Signals;
using OpenSim.Rf.Channel;
using OpenSim.Rf.Network;
using OpenSim.Rf.Si;
using PortTermination = OpenSim.Rf.Channel.PortTermination;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Si;

/// <summary>
/// FU-31 — channel import robustness: Touchstone 2.0/2.1 (against the examples of the IBIS
/// Open Forum's Touchstone File Format Specification 2.1, 2024), causality and passivity
/// enforcement, lossy layer peeling, and eyes between non-resistive terminations.
/// </summary>
public class ChannelRobustnessTests
{
    private readonly ITestOutputHelper _out;
    public ChannelRobustnessTests(ITestOutputHelper output) => _out = output;

    // ---------------- Touchstone 2: the specification's own examples ----------------

    private const string Example6 = """
        ! 4-port S-parameter data
        ! The option line reference resistance is overridden by the [Reference]
        ! keyword arguments
        ! Data cannot be represented using 1.0 syntax
        ! but can be represented using 1.1 syntax
        [Version] 2.1
        # GHz S MA R 50
        [Number of Ports] 4
        [Number of Frequencies] 1
        [Reference] 50 75 0.01 0.01
        [Matrix Format] Full
        [Network Data]
        5.00000 0.60 161.24 0.40 -42.20 0.42 -66.58 0.53 -79.34 ! row 1
                0.40 -42.20 0.60 161.20 0.53 -79.34 0.42 -66.58 ! row 2
                0.42 -66.58 0.53 -79.34 0.60 161.24 0.40 -42.20 ! row 3
                0.53 -79.34 0.42 -66.58 0.40 -42.20 0.60 161.24 ! row 4
        [End]
        """;

    private const string Example7 = """
        ! 4-port S-parameter data
        ! The option line reference resistance is overridden by the [Reference]
        ! keyword arguments
        ! Note that [Reference] arguments are split across two lines
        ! Data cannot be represented using 1.0 syntax
        ! but can be represented using 1.1 syntax
        [Version] 2.1
        # GHz S MA R 50
        [Number of Ports] 4
        [Number of Frequencies] 1
        [Reference] 50 75
        0.01 0.01
        [Matrix Format] Lower
        [Network Data]
        5.00000 0.60 161.24                                     ! row 1
                0.40 -42.20 0.60 161.20                         ! row 2
                0.42 -66.58 0.53 -79.34 0.60 161.24             ! row 3
                0.53 -79.34 0.42 -66.58 0.40 -42.20 0.60 161.24 ! row 4
        [End]
        """;

    private static Complex Ma(double magnitude, double degrees) => Complex.FromPolarCoordinates(magnitude, degrees * Math.PI / 180);

    /// <summary>Example 6's matrix as printed (rows 1–4).</summary>
    private static Complex[,] Example6Matrix() => new[,]
    {
        { Ma(0.60, 161.24), Ma(0.40, -42.20), Ma(0.42, -66.58), Ma(0.53, -79.34) },
        { Ma(0.40, -42.20), Ma(0.60, 161.20), Ma(0.53, -79.34), Ma(0.42, -66.58) },
        { Ma(0.42, -66.58), Ma(0.53, -79.34), Ma(0.60, 161.24), Ma(0.40, -42.20) },
        { Ma(0.53, -79.34), Ma(0.42, -66.58), Ma(0.40, -42.20), Ma(0.60, 161.24) }
    };

    /// <summary>
    /// The reference: power waves a = (V + RI)/(2√R), b = (V − RI)/(2√R) (the specification's
    /// definition) give Z = D(I + S)(I − S)⁻¹D with D = diag(√R_i); S in one reference R₀ is
    /// then (Z − R₀)(Z + R₀)⁻¹.
    /// </summary>
    private static Complex[,] ThroughImpedance(Complex[,] s, double[] references, double common)
    {
        int n = s.GetLength(0);
        var plus = new Complex[n, n];
        var minus = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                plus[i, j] = (i == j ? 1 : 0) + s[i, j];
                minus[i, j] = (i == j ? 1 : 0) - s[i, j];
            }
        var z = NetworkParameters.Multiply(plus, NetworkParameters.Invert(minus));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) z[i, j] *= Math.Sqrt(references[i] * references[j]);
        return NetworkParameters.ImpedanceToScattering(z, common);
    }

    private static double Largest(Complex[,] a, Complex[,] b)
    {
        double worst = 0;
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++) worst = Math.Max(worst, (a[i, j] - b[i, j]).Magnitude);
        return worst;
    }

    [Fact]
    public void Version2_PerPortReferences_FullAndLower_AreTheSameNetwork()
    {
        var full = TouchstoneReader.Read(Example6, null);
        var lower = TouchstoneReader.Read(Example7, 4);
        Assert.Equal("2.1", full.Version);
        Assert.Equal(new[] { 50, 75, 0.01, 0.01 }, full.FileReferencesOhms);
        Assert.Equal(50, full.ReferenceOhms);
        Assert.Equal(5e9, full.FrequenciesHz[0]);
        // Example 7 is Example 6's symmetric matrix written as its lower triangle.
        Assert.True(Largest(full.Scattering[0], lower.Scattering[0]) < 1e-15);
        var expected = ThroughImpedance(Example6Matrix(), new[] { 50, 75, 0.01, 0.01 }, 50);
        double error = Largest(full.Scattering[0], expected);
        _out.WriteLine($"Example 6 renormalized to 50 Ω against the Z route: {error:e2}");
        Assert.True(error < 1e-12);
        Assert.Contains(full.Notes, n => n.Contains("renormalized"));

        // Version 1.1: the same per-port references on the option line (Example 5's form)
        // with Example 15's data.
        const string v11 = """
            ! 4-port S-parameter data
            # GHz S MA R 50 75 0.01 0.01
            5.00000 0.60 161.24 0.40 -42.20 0.42 -66.58 0.53 -79.34 ! row 1
                    0.40 -42.20 0.60 161.20 0.53 -79.34 0.42 -66.58 ! row 2
                    0.42 -66.58 0.53 -79.34 0.60 161.24 0.40 -42.20 ! row 3
                    0.53 -79.34 0.42 -66.58 0.40 -42.20 0.60 161.24 ! row 4
            6.00000 0.57 150.37 0.40 -44.34 0.41 -81.24 0.57 -95.77 ! row 1
                    0.40 -44.34 0.57 150.37 0.57 -95.77 0.41 -81.24 ! row 2
                    0.41 -81.24 0.57 -95.77 0.57 150.37 0.40 -44.34 ! row 3
                    0.57 -95.77 0.41 -81.24 0.40 -44.34 0.57 150.37 ! row 4
            """;
        var old = TouchstoneReader.Read(v11, 4);
        Assert.Equal("1.1", old.Version);
        Assert.Equal(2, old.FrequenciesHz.Count);
        Assert.True(Largest(old.Scattering[0], full.Scattering[0]) < 1e-15);
        Assert.Throws<InvalidDataException>(() => TouchstoneReader.Read("# GHz Z MA R 50 75\n1 1 0 0 0 0 0 1 0\n", 2));
    }

    [Fact]
    public void Version2_ZIsNotNormalized_Version1ZIs()
    {
        // Example 11 "duplicates the data in Example 10, using Version 2.0/Version 2.1 syntax".
        const string example10 = """
            ! 1-port Z-parameter file, multiple frequency points
            # MHz Z MA R 75
            ! freq  magZ11 angZ11
            100    0.99   -4
            200    0.80   -22
            300    0.707  -45
            400    0.40   -62
            500    0.01   -89
            """;
        const string example11 = """
            ! 1-port Z-parameter file, multiple frequency points
            [Version] 2.1
            # MHz Z MA
            [Number of Ports] 1
            [Number of Frequencies] 5
            [Reference] 20.0
            [Network Data]
            ! freq  magZ11 angZ11
            100    74.25    -4
            200    60      -22
            300    53.025  -45
            400    30      -62
            500     0.75   -89
            [End]
            """;
        var v1 = TouchstoneReader.Read(example10, 1);
        var v2 = TouchstoneReader.Read(example11, 1);
        Assert.Equal(75, v1.ReferenceOhms);
        Assert.Equal(50, v2.ReferenceOhms);   // [Reference] has no effect on Z data
        Assert.Null(v2.FileReferencesOhms);
        for (int k = 0; k < 5; k++)
        {
            Complex Z(TouchstoneData d) => d.ReferenceOhms * (1 + d.Scattering[k][0, 0]) / (1 - d.Scattering[k][0, 0]);
            Assert.True((Z(v1) - Z(v2)).Magnitude < 1e-9 * Z(v1).Magnitude, $"point {k}: {Z(v1)} vs {Z(v2)}");
        }
        Assert.Equal(new[] { 100e6, 200e6, 300e6, 400e6, 500e6 }, v2.FrequenciesHz);
    }

    private const string Example18 = """
        ! 2-port network, S-parameter and noise data
        ! Default MA format, GHz frequencies, 50-ohm reference, S-parameters
        [Version] 2.1
        #
        [Number of Ports] 2
        [Two-Port Data Order] 21_12
        [Number of Frequencies] 2
        [Number of Noise Frequencies] 2
        [Reference] 50 25.0
        [Network Data]
        2  0.95  -26 3.57 157 0.04 76 0.66 -14
        22 0.60 -144 1.30  40 0.14 40 0.56 -85
        [Noise Data]
        4  0.7 0.64  69 19
        18 2.7 0.46 -33 20
        [End]
        """;

    [Fact]
    public void Version2_TwoPortOrder_NoiseData_AndAMissingOrderKeyword()
    {
        const string example20 = """
            ! 2-port network, S-parameter and noise data
            ! Default MA format, GHz frequencies, 50-ohm reference, S-parameters
            [Version] 2.1
            #
            [Number of Ports] 2
            [Number of Frequencies] 2
            [Number of Noise Frequencies] 2
            [Reference] 50 25.0
            [Network Data]
            ! NETWORK PARAMETERS
            2  0.95  -26 3.57 157 0.04 76 0.66 -14
            22 0.60 -144 1.30 40  0.14 40 0.56 -85
            [Noise Data]
            ! NOISE PARAMETERS
            4  0.7 0.64  69 19
            18 2.7 0.46 -33 20
            [End]
            """;
        const string example21 = """
            ! 2-port network, S-parameters
            ! Default MA format, GHz frequencies, 50-ohm reference, S-parameters
            [Version] 2.1
            #
            [Number of Ports] 2
            [Number of Frequencies] 2
            [Reference] 50 25.0
            [Two-Port Data Order] 12_21
            [Network Data]
            ! NETWORK PARAMETERS
            2  0.95 -26  3.57 157 0.04 76 0.66 -14
            22 0.60 -144 1.30  40 0.14 40 0.56 -85
            [End]
            """;
        var e18 = TouchstoneReader.Read(Example18, 2);
        var e20 = TouchstoneReader.Read(example20, 2);
        var e21 = TouchstoneReader.Read(example21, 2);
        Assert.Equal(new[] { 2e9, 22e9 }, e18.FrequenciesHz);
        var printed = new[,] { { Ma(0.95, -26), Ma(0.04, 76) }, { Ma(3.57, 157), Ma(0.66, -14) } };   // 21_12: S11 S21 S12 S22
        Assert.True(Largest(e18.Scattering[0], ThroughImpedance(printed, new[] { 50, 25.0 }, 50)) < 1e-12);
        // Example 20 leaves out the required [Two-Port Data Order]: read as 21_12, and said so.
        Assert.True(Largest(e18.Scattering[1], e20.Scattering[1]) < 1e-15);
        Assert.Contains(e20.Notes, n => n.Contains("Two-Port Data Order"));
        // 12_21 swaps S21 and S12: the renormalized matrix is the transpose.
        for (int k = 0; k < 2; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True((e21.Scattering[k][i, j] - e18.Scattering[k][j, i]).Magnitude < 1e-12);
    }

    [Fact]
    public void Version2_MixedModeY_ConvertsBackToSingleEnded()
    {
        const string example17 = """
            ! 6-port component shown; note that all six ports are used in some
            ! relationship
            [Version] 2.1
            # MHz Y RI R 50
            [Number of Ports] 6
            [Number of Frequencies] 1
            [Reference] 50 75 75 50 0.01 0.01
            # MHz Y RI R 50

            [Mixed-Mode Order] D2,3 D6,5 C2,3 C6,5 S4 S1
            [Network Data]
            5.00 8.0  9.0  2.0  -1.0  3.0 -2.0  1.0  3.0  1.0  0.1  0.2 -0.2
                 2.0 -1.0  7.0   7.0  1.8 -2.0 -1.0 -1.0 -0.5  0.5  0.2 -0.1
                 3.0 -2.0  1.8  -2.0  5.8  6.0  1.2  0.8  0.9  0.7  0.3 -0.5
                 1.0  3.0 -1.0  -1.0  1.2  0.8  6.3  8.0  2.0 -0.5  1.5  0.6
                 1.0  0.1 -0.5   0.5  0.9  0.7  2.0 -0.5  4.7 -6.0 -1.0  2.0
                 0.2 -0.2  0.2  -0.1  0.3 -0.5  1.5  0.6 -1.0  2.0  5.5 -7.0
            [End]
            """;
        // Appendix A-2's Y_mm (the same matrix as Example 17's data) and its printed Y_std.
        var mixed = new Complex[,]
        {
            { new(8, 9), new(2, -1), new(3, -2), new(1, 3), new(1, 0.1), new(0.2, -0.2) },
            { new(2, -1), new(7, 7), new(1.8, -2), new(-1, -1), new(-0.5, 0.5), new(0.2, -0.1) },
            { new(3, -2), new(1.8, -2), new(5.8, 6), new(1.2, 0.8), new(0.9, 0.7), new(0.3, -0.5) },
            { new(1, 3), new(-1, -1), new(1.2, 0.8), new(6.3, 8), new(2, -0.5), new(1.5, 0.6) },
            { new(1, 0.1), new(-0.5, 0.5), new(0.9, 0.7), new(2, -0.5), new(4.7, -6), new(-1, 2) },
            { new(0.2, -0.2), new(0.2, -0.1), new(0.3, -0.5), new(1.5, 0.6), new(-1, 2), new(5.5, -7) }
        };
        var standard = new Complex[,]
        {
            { new(9.35, 5.75), new(0.725, -0.075), new(-4.05, -4.25), new(-0.075, 1.025), new(0.7, 0.3), new(4, -2.5) },
            { new(0.725, -0.075), new(5.675, -6.5), new(-0.775, 0.225), new(-4.325, 5.5), new(2.5, 0.35), new(0.7, -0.15) },
            { new(-4.05, -4.25), new(-0.775, 0.225), new(5.75, 9.75), new(-0.375, -0.675), new(-1.7, -1.3), new(-2, 1.5) },
            { new(-0.075, 1.025), new(-4.325, 5.5), new(-0.375, -0.675), new(7.675, -10.5), new(-0.5, -0.85), new(0.3, 0.25) },
            { new(0.7, 0.3), new(2.5, 0.35), new(-1.7, -1.3), new(-0.5, -0.85), new(6.3, 8), new(1, 3) },
            { new(4, -2.5), new(0.7, -0.15), new(-2, 1.5), new(0.3, 0.25), new(1, 3), new(8, 9) }
        };
        // The appendix's printed side matrices (and so its Y_std) belong to the order
        // S6 C1,3 D1,3 S5 C2,4 D2,4, not to the D2,3 D6,5 C2,3 C6,5 S4 S1 printed above them:
        // Y_std row 6 is I₆ = I_mm1, rows 1 and 3 are I_C/2 ± I_D of the pair (1, 3), and so on.
        var appendixOrder = MixedModeDescriptor.Parse("S6 C1,3 D1,3 S5 C2,4 D2,4".Split(' '), 6);
        var converted = MixedModeDescriptor.ToSingleEnded(mixed, appendixOrder, "Y");
        double appendixError = Largest(converted, standard);
        _out.WriteLine($"Appendix A-2: Y_std from Y_mm against the printed matrix: {appendixError:e2}");
        Assert.True(appendixError < 1e-12);

        // Example 17 as a file: the second option line is ignored, the declared order used.
        var read = TouchstoneReader.Read(example17, null);
        Assert.Equal(6, read.Ports);
        Assert.Equal(5e6, read.FrequenciesHz[0]);
        var declared = MixedModeDescriptor.ToSingleEnded(mixed,
            MixedModeDescriptor.Parse("D2,3 D6,5 C2,3 C6,5 S4 S1".Split(' '), 6), "Y");
        var back = NetworkChecks.AdmittanceToScattering(declared, 50);
        Assert.True(Largest(read.Scattering[0], back) < 1e-12);
        Assert.Contains(read.Notes, n => n.Contains("Mixed-mode"));

        // Appendix A-1: S data, order D3,2 D5,4 S1 C3,2 C5,4 — the second port of a pair is
        // the reference: S_std = L·S_mm·R with the printed γ = 1/√2 matrices.
        double g = Math.Sqrt(0.5);
        var left = new double[,] { { 0, 0, 1, 0, 0 }, { -g, 0, 0, g, 0 }, { g, 0, 0, g, 0 }, { 0, -g, 0, 0, g }, { 0, g, 0, 0, g } };
        var right = new double[,] { { 0, -g, g, 0, 0 }, { 0, 0, 0, -g, g }, { 1, 0, 0, 0, 0 }, { 0, g, g, 0, 0 }, { 0, 0, 0, g, g } };
        var random = new Random(5);
        var sMixed = new Complex[5, 5];
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++) sMixed[i, j] = new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5);
        var sStandard = MixedModeDescriptor.ToSingleEnded(sMixed, MixedModeDescriptor.Parse("D3,2 D5,4 S1 C3,2 C5,4".Split(' '), 5), "S");
        double a1 = 0;
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
            {
                Complex sum = 0;
                for (int p = 0; p < 5; p++)
                    for (int q = 0; q < 5; q++) sum += left[i, p] * sMixed[p, q] * right[q, j];
                a1 = Math.Max(a1, (sum - sStandard[i, j]).Magnitude);
            }
        Assert.True(a1 < 1e-15, $"Appendix A-1 differs by {a1:e2}");

        // The descriptor rules.
        Assert.Throws<InvalidDataException>(() => MixedModeDescriptor.Parse("D1,3 S2 S3".Split(' '), 3));   // C1,3 missing
        Assert.Throws<InvalidDataException>(() => MixedModeDescriptor.Parse("D1,2 C1,2 S2".Split(' '), 3)); // port 2 twice
        Assert.Throws<InvalidDataException>(() => MixedModeDescriptor.Parse("S1 S2".Split(' '), 3));       // too few
    }

    [Fact]
    public void Version2_RefusesWhatTheSpecificationForbids()
    {
        string Without(string keyword) => string.Join('\n', Example18.Split('\n').Where(l => !l.StartsWith(keyword)));
        void Refused(string text, string why)
        {
            var ex = Assert.Throws<InvalidDataException>(() => TouchstoneReader.Read(text, null));
            _out.WriteLine($"{why}: {ex.Message}");
        }
        Refused(Without("[End]"), "no [End]");
        Refused(Without("[Number of Frequencies]"), "no [Number of Frequencies]");
        Refused(Example18 + "\n1 2 3\n", "data after [End]");
        Refused(Example18.Replace("[Number of Frequencies] 2", "[Number of Frequencies] 3"), "fewer points than declared");
        Refused(Example18.Replace("[Reference] 50 25.0", "[Reference] 50"), "one reference for two ports");
        Refused(Example18.Replace("[Number of Ports] 2", "[Number of Ports] 2\n[Number of Ports] 2"), "keyword twice");
        Refused(Example6.Replace("[Matrix Format] Full", "[Matrix Format] Full\n[Two-Port Data Order] 12_21"), "data order in a four-port");
        Refused(Example6.Replace("[Number of Ports] 4\n", "").Replace("[Number of Frequencies] 1", "[Number of Frequencies] 1\n[Number of Ports] 4"),
            "[Number of Ports] not first");
        Refused(Without("[Noise Data]").Replace("4  0.7 0.64  69 19\n", "").Replace("18 2.7 0.46 -33 20\n", ""), "noise count without noise data");
        Refused(Example18.Replace("[Version] 2.1", "[Version] 3.0"), "unknown version");
        Refused(Example18.Replace("[Reference] 50 25.0", "[Reference] 50 25.0\n[Frobnicate] 1"), "unknown keyword");
        // [Begin Information] … [End Information] is skipped whatever it holds.
        var informed = TouchstoneReader.Read(Example18.Replace("[Reference] 50 25.0",
            "[Reference] 50 25.0\n[Begin Information]\n[Some Future Keyword] 1 2 3\nfree text\n[End Information]"), null);
        Assert.Equal(2, informed.FrequenciesHz.Count);
        // The port count comes from the keyword; a disagreeing file name is noted.
        Assert.Contains(TouchstoneReader.Read(Example18, 4).Notes, n => n.Contains("file name says 4"));
    }

    // ---------------- Causality and passivity ----------------

    private static RlgcResult SingleLine(double l, double c)
        => new(1, new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
            new[,] { { l } }, new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());

    private static TouchstoneData Sample(MtlNetwork network, double start, double stop, int points, double z0 = 50)
    {
        var frequencies = Enumerable.Range(0, points).Select(k => start + (stop - start) * k / (points - 1)).ToList();
        return new TouchstoneData(frequencies, frequencies.Select(f => network.Scattering(f, z0)).ToList(), z0);
    }

    private static TouchstoneData Map(TouchstoneData data, Func<double, Complex[,], Complex[,]> change) =>
        new(data.FrequenciesHz, data.FrequenciesHz.Select((f, k) => change(f, data.Scattering[k])).ToList(), data.ReferenceOhms);

    [Fact]
    public void Repair_LeavesACausalPassiveLineAlone()
    {
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(3.6e-7, 1e-10), 0.15) });   // 60 Ω, 0.9 ns
        var data = Sample(network, 0, 8e9, 401);
        var result = ChannelRepair.Repair(data);
        double moved = 0;
        for (int k = 0; k < data.FrequenciesHz.Count; k++)
            moved = Math.Max(moved, Largest(result.Repaired.Scattering[k], data.Scattering[k]));
        _out.WriteLine(string.Join("\n", result.Describe()));
        _out.WriteLine($"largest change to the file: {moved:e2}; poles S11 {result.Poles[0, 0]}, S21 {result.Poles[1, 0]}");
        Assert.True(result.IsCausal);
        Assert.True(result.CausalDeviation < 1e-3, $"causal fit {result.CausalDeviation:e2}");
        Assert.True(moved < 1e-3);
        Assert.True(result.LargestSingularValueAfter <= 1);
    }

    [Fact]
    public void Repair_FindsAPrecursor_AndEnforcesPassivity()
    {
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(3.6e-7, 1e-10), 0.15) });
        var data = Sample(network, 0, 8e9, 401);

        // A tenth of the through signal arriving 0.4 ns before t = 0 (1.3 ns ahead of the
        // line's 0.9 ns): a response before its cause, which no causal model can follow. (An
        // early copy at positive time would be a second, faster path — causal.)
        var early = Map(data, (f, s) =>
        {
            var m = (Complex[,])s.Clone();
            Complex echo = 1 + 0.1 * Complex.FromPolarCoordinates(1, 2 * Math.PI * f * 1.3e-9);
            m[1, 0] *= echo;
            m[0, 1] *= echo;
            return m;
        });
        var acausal = ChannelRepair.Repair(early, new ChannelRepairOptions { EnforcePassivity = false });
        _out.WriteLine(string.Join("\n", acausal.Describe()));
        Assert.False(acausal.IsCausal);
        Assert.Contains(acausal.Describe(), l => l.StartsWith("NOT CAUSAL"));
        Assert.Contains(acausal.Describe(), l => l.Contains("not enforced"));

        // 3 % gain everywhere: not passive.
        var gain = Map(data, (f, s) =>
        {
            var m = (Complex[,])s.Clone();
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) m[i, j] *= 1.03;
            return m;
        });
        var passive = ChannelRepair.Repair(gain);
        _out.WriteLine(string.Join("\n", passive.Describe()));
        Assert.True(passive.LargestSingularValueBefore > 1.02);
        Assert.True(passive.LargestSingularValueAfter <= 1, $"‖S‖ still {passive.LargestSingularValueAfter:f5}");
        Assert.True(passive.IsCausal);
        Assert.InRange(passive.PassivityChange, 0.02, 0.06);
        Assert.All(new ImportedChannel(passive.Repaired).Checks, c => Assert.True(c.IsPassive(0)));
    }

    // ---------------- Lossy layer peeling ----------------

    /// <summary>
    /// ABCD of a lossy section: per metre series r·Z_c·√f·(1 + j) + jωL (skin effect with its
    /// internal inductance, the resistance proportional to the impedance as a narrower trace's
    /// is) and shunt ωC·tanδ + jωC, at 1.5e8 m/s. Then every section has the same propagation
    /// constant, and Z(f) = Z_c·k(f) with one k for all — the loss per unit delay is the same
    /// along the whole line, as lossy peeling assumes, and the reflections between sections
    /// are frequency-independent.
    /// </summary>
    private static (Complex A, Complex B, Complex C, Complex D) LossySection(double f, double zc, double length,
        double r, double tanDelta)
    {
        const double velocity = 1.5e8;
        if (f == 0) return (1, 0, 0, 1);
        double omega = 2 * Math.PI * f, l = zc / velocity, cap = 1 / (zc * velocity);
        Complex series = r * zc * Math.Sqrt(f) * new Complex(1, 1) + new Complex(0, omega * l);
        Complex shunt = new Complex(omega * cap * tanDelta, omega * cap);
        Complex z = Complex.Sqrt(series / shunt), gl = Complex.Sqrt(series * shunt) * length;
        return (Complex.Cosh(gl), z * Complex.Sinh(gl), Complex.Sinh(gl) / z, Complex.Cosh(gl));
    }

    private static Complex LossyCascadeS11(double f, double z0, double r, double tanDelta, params (double Zc, double Length)[] sections)
    {
        Complex a = 1, b = 0, c = 0, d = 1;
        foreach (var (zc, length) in sections)
        {
            var (a2, b2, c2, d2) = LossySection(f, zc, length, r, tanDelta);
            (a, b, c, d) = (a * a2 + b * c2, a * b2 + b * d2, c * a2 + d * c2, c * b2 + d * d2);
        }
        return (a + b / z0 - c * z0 - d) / (a + b / z0 + c * z0 + d);
    }

    [Fact]
    public void LossyPeeling_ReadsTheFarSections_TheLosslessOneDoesNot()
    {
        const double r = 4e-5, tanDelta = 0.004;   // about 1.5 dB per ns of round trip at 20 GHz
        // 0.15 m per ns of round trip at 1.5e8 m/s.
        var sections = new[] { (65.0, 0.075), (40.0, 0.09), (50.0, 0.06), (80.0, 0.075), (50.0, 0.06) };
        Func<double, Complex> reflection = f => LossyCascadeS11(f, 50, r, tanDelta, sections);

        // The loss from a through line of the same make, as a two-port file.
        var frequencies = Enumerable.Range(0, 801).Select(k => 25e6 * k).ToList();
        var through = new TouchstoneData(frequencies, frequencies.Select(f =>
        {
            var (a, b, c, d) = LossySection(f, 50, 0.36, r, tanDelta);
            Complex denominator = a + b / 50 + c * 50 + d;
            Complex s11 = (a + b / 50 - c * 50 - d) / denominator, s21 = 2 / denominator;
            return new[,] { { s11, s21 }, { s21, s11 } };
        }).ToList(), 50);
        var loss = TdrLoss.FromThrough(new ImportedChannel(through), 0, 1);
        // The skin term per second of one-way travel is r·v/2·√f, the dielectric one π·tanδ·f.
        _out.WriteLine($"fitted loss: {loss.ConstantNepersPerSecond:g3} + {loss.SkinNepersPerSecondRootHz:g4}·√f + " +
                       $"{loss.DielectricNepersPerSecondHz:g4}·f Np/s (exact 0 + {r * 1.5e8 / 2:g4}·√f + {Math.PI * tanDelta:g4}·f)");
        Assert.InRange(loss.SkinNepersPerSecondRootHz / (r * 1.5e8 / 2), 0.97, 1.03);
        Assert.InRange(loss.DielectricNepersPerSecondHz / (Math.PI * tanDelta), 0.97, 1.03);

        var lossless = Tdr.Profile(reflection, 20e9, 25e6, 50, maxTimeSeconds: 5e-9);
        var lossy = Tdr.Profile(reflection, 20e9, 25e6, 50, maxTimeSeconds: 5e-9, loss: loss);
        // A lossy line's impedance is Z_c·k(f) — above Z_c at low frequencies, where its
        // resistance counts — so even a uniform one peels as a slowly rising profile. Each
        // section is read against a uniform 50 Ω line of the same make at the same round-trip
        // time, peeled the same way: the ratio is Z_c/50 when the loss is taken out right.
        Func<double, Complex> uniform = f => LossyCascadeS11(f, 50, r, tanDelta, (50.0, 0.6));
        var uniformLossless = Tdr.Profile(uniform, 20e9, 25e6, 50, maxTimeSeconds: 5e-9);
        var uniformLossy = Tdr.Profile(uniform, 20e9, 25e6, 50, maxTimeSeconds: 5e-9, loss: loss);
        // Section middles in round-trip time.
        var targets = new[] { (0.5e-9, 65.0), (1.6e-9, 40.0), (2.6e-9, 50.0), (3.5e-9, 80.0) };
        double worstLossless = 0, worstLossy = 0;
        foreach (var (t, z) in targets)
        {
            double plain = 50 * lossless.At(t, true) / uniformLossless.At(t, true);
            double compensated = 50 * lossy.At(t, true) / uniformLossy.At(t, true);
            worstLossless = Math.Max(worstLossless, Math.Abs(plain / z - 1));
            worstLossy = Math.Max(worstLossy, Math.Abs(compensated / z - 1));
            _out.WriteLine($"{t * 1e9:f1} ns, {z} Ω: lossless peel {plain:f3}, lossy peel {compensated:f3} (against the uniform line); " +
                           $"peeled {lossy.At(t, true):f3}, shown {lossless.At(t):f3}");
        }
        _out.WriteLine($"worst ratio error: lossless {worstLossless:P2}, lossy {worstLossy:P2}");
        Assert.Contains(lossy.Notes, n => n.Contains("dB per ns"));
        Assert.DoesNotContain(lossy.Notes, n => n.Contains("assumes a lossless line"));
        Assert.True(worstLossy < worstLossless / 3, $"lossy {worstLossy:P2}, lossless {worstLossless:P2}");
        Assert.True(worstLossy < 0.01, $"lossy peel off by {worstLossy:P2}");

        // With no loss the lossy recursion is the lossless one.
        var none = Tdr.Profile(reflection, 20e9, 25e6, 50, maxTimeSeconds: 5e-9, loss: new TdrLoss(0, 0, 0));
        double same = 0;
        for (int k = 0; k < none.PeeledOhms!.Length; k++) same = Math.Max(same, Math.Abs(none.PeeledOhms[k] - lossless.PeeledOhms![k]));
        _out.WriteLine($"zero loss against the lossless recursion: {same:e2} Ω");
        Assert.True(same < 1e-6);
    }

    // ---------------- Non-resistive terminations ----------------

    [Fact]
    public void Termination_PackageAndDie_MatchACircuitSolvedAlongTheLine()
    {
        // 60 Ω, 0.9 ns line; 40 Ω source; the receiver 1.5 nH of package, then 75 Ω ‖ 1.2 pF at
        // the die. The die voltage per unit source by ABCD: source R, line, series L, shunt Y.
        const double zc = 60, tau = 0.9e-9, rs = 40, lpkg = 1.5e-9, rload = 75, cdie = 1.2e-12;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(3.6e-7, 1e-10), 0.15) });
        var load = new PortNetwork { ResistanceOhms = rload, DieCapacitanceFarads = cdie, PackageInductanceHenries = lpkg };
        double worst = 0;
        foreach (double f in new[] { 0.0, 0.1e9, 0.7e9, 1.9e9, 3.3e9, 5.1e9 })
        {
            var ports = new[] { new PortTermination(rs, 1), new PortTermination(50) { Network = load } };
            var volts = ChannelEye.PortVoltages(network.Scattering(f, 50), 50, ports, f);
            Complex die = volts[1] * load.At(f).DieFromPort;
            double omega = 2 * Math.PI * f, theta = omega * tau;
            Complex y = 1 / rload + new Complex(0, omega * cdie);
            // From the die back to the source: V_die, I = V_die·Y; across L; along the line.
            Complex vPort = 1 + new Complex(0, omega * lpkg) * y, iPort = y;
            Complex vNear = vPort * Math.Cos(theta) + new Complex(0, zc * Math.Sin(theta)) * iPort;
            Complex iNear = new Complex(0, Math.Sin(theta) / zc) * vPort + Math.Cos(theta) * iPort;
            Complex expected = 1 / (vNear + rs * iNear);
            worst = Math.Max(worst, (die - expected).Magnitude);
        }
        _out.WriteLine($"die voltage per unit source against the ABCD circuit: worst {worst:e2}");
        Assert.True(worst < 1e-9);
    }

    [Fact]
    public void Eye_WithCapacitiveReceiver_IsWhereTheNonlinearEngineConverges_AndAcCouplingRemovesTheMean()
    {
        // The time-stepping engine carries the receiver's capacitance with a step error; as
        // its step halves it closes on the eye, which takes the capacitor exactly per bin.
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(3.6e-7, 1e-10), 0.15) });
        var channel = new ImportedChannel(Sample(network, 0, 16e9, 1601));
        var differences = new List<double>();
        foreach (int samples in new[] { 16, 32 })
        {
            double dt = 1 / (1e9 * samples);
            var eye = ChannelEye.SingleEnded(channel, 0, 1, new ChannelEyeSetup
            {
                BitRate = 1e9, SamplesPerUi = samples, RiseFractionOfUi = 0.25, SourceOhms = 40,
                Load = new PortNetwork { ResistanceOhms = 75, DieCapacitanceFarads = 2e-12 }
            });
            var source = SourceWaveform.Trapezoid(PrbsGenerator.Generate(7, 127), samples, 0.25);
            var engine = NonlinearLink.SolveNPort(network,
                new INonlinearDriver[] { new LinearTheveninDriver(t => source[(int)Math.Round(t / dt) % source.Length], 40) },
                new INonlinearDriver[] { new LinearLoadElement(75, 2e-12) }, source.Length, dt, warmupPeriods: 2);
            double worst = 0;
            for (int n = 0; n < source.Length; n++) worst = Math.Max(worst, Math.Abs(engine.FarVolts[0][n] - eye.Waveform[n]));
            differences.Add(worst);
            _out.WriteLine($"receiver 75 Ω ‖ 2 pF, {samples} samples per bit: engine against the eye, worst {worst * 1e3:f3} mV");
        }
        Assert.True(differences[1] < differences[0] / 2, "the engine does not close on the eye as its step halves");
        Assert.True(differences[1] < 2.5e-3, $"{differences[1] * 1e3:f2} mV apart at 32 samples per bit");

        const int spu = 16;

        // A 10 nF coupling capacitor into a 50 Ω receiver: the pattern's mean is blocked.
        var coupled = ChannelEye.SingleEnded(channel, 0, 1, new ChannelEyeSetup
        {
            BitRate = 1e9, SamplesPerUi = spu, RiseFractionOfUi = 0.25,
            Load = new PortNetwork { ResistanceOhms = 50, CouplingCapacitanceFarads = 10e-9 }
        });
        var direct = ChannelEye.SingleEnded(channel, 0, 1, new ChannelEyeSetup { BitRate = 1e9, SamplesPerUi = spu, RiseFractionOfUi = 0.25 });
        double meanCoupled = coupled.Waveform.Average(), meanDirect = direct.Waveform.Average();
        _out.WriteLine($"mean at the receiver: DC coupled {meanDirect:f4} V, AC coupled {meanCoupled:e2} V; " +
                       $"eye {direct.Eye.EyeHeight:f4} V and {coupled.Eye.EyeHeight:f4} V");
        Assert.True(Math.Abs(meanCoupled) < 1e-12);
        Assert.True(meanDirect > 0.2);
        Assert.Contains(coupled.Notes, n => n.Contains("die"));
    }
}
