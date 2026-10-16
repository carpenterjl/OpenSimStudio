using System.Numerics;
using OpenSim.Pcb.Thermal;
using OpenSim.Rf.Pdn;
using OpenSim.Rf.Si;

namespace OpenSim.Tests.Pdn;

/// <summary>FU-32: capacitor values from a bill of materials or a placement file, and the
/// vendor's measured model found by part number.</summary>
public class CapacitorBomTests
{
    [Theory]
    [InlineData("100n", 100e-9)]
    [InlineData("100nF", 100e-9)]
    [InlineData("100 nF", 100e-9)]
    [InlineData("0.1uF", 100e-9)]
    [InlineData("0.1 µF", 100e-9)]
    [InlineData("4u7", 4.7e-6)]
    [InlineData("2n2", 2.2e-9)]
    [InlineData("1000pF", 1e-9)]
    [InlineData("10uF/16V X5R", 10e-6)]
    [InlineData("100nF 10% 16V", 100e-9)]
    [InlineData("1mF", 1e-3)]
    [InlineData("47UF", 47e-6)]
    public void Values_ReadAsCapacitance(string value, double farads)
    {
        double? read = CapacitorBom.ParseCapacitance(value);
        Assert.NotNull(read);
        Assert.Equal(farads, read!.Value, farads * 1e-12);
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("10k")]
    [InlineData("BLM18PG221")]
    [InlineData("")]
    public void ValuesThatSayNothingOfTheirScale_ReadAsNothing(string value) =>
        Assert.Null(CapacitorBom.ParseCapacitance(value));

    [Fact]
    public void ABillOfMaterials_GivesEachDesignatorItsRow()
    {
        string bom = string.Join("\n",
            "Board BOM, rev B",
            "\"Reference\",\"Value\",\"Footprint\",\"Qty\",\"MPN\",\"ESR (mΩ)\",\"ESL [nH]\"",
            "\"C1,C2\",\"100n\",\"C_0402\",\"2\",\"GRM155R71C104KA88\",\"30\",\"0.4\"",
            "\"C5-C7\",\"10u\",\"C_0805\",\"3\",\"GRM21BR61A106KE19\",\"5m\",\"700p\"",
            "\"R1\",\"10k\",\"R_0402\",\"1\",\"\",\"\",\"\"",
            "\"C9\",\"4u7\",\"C_0603\",\"1\",\"\",\"\",\"\"");
        var parts = CapacitorBom.Read(bom, out var notes);
        Assert.Equal(new[] { "C1", "C2", "C5", "C6", "C7", "R1", "C9" }, parts.Select(p => p.RefDes));
        var c2 = parts.Single(p => p.RefDes == "C2");
        Assert.Equal(100e-9, c2.Farads!.Value, 1e-18);
        Assert.Equal(0.030, c2.EsrOhms!.Value, 1e-12);             // bare number in the header's mΩ
        Assert.Equal(0.4e-9, c2.EslHenries!.Value, 1e-18);         // bare number in the header's nH
        Assert.Equal("GRM155R71C104KA88", c2.PartNumber);
        var c6 = parts.Single(p => p.RefDes == "C6");
        Assert.Equal(10e-6, c6.Farads!.Value, 1e-15);
        Assert.Equal(5e-3, c6.EsrOhms!.Value, 1e-12);              // the value's own prefix wins
        Assert.Equal(0.7e-9, c6.EslHenries!.Value, 1e-18);
        Assert.Null(parts.Single(p => p.RefDes == "R1").Farads);
        Assert.Null(parts.Single(p => p.RefDes == "C9").EsrOhms);
        Assert.Contains(notes, n => n.Contains("7 part(s)"));

        // An Altium export: semicolons, "Designator" and "Comment".
        var altium = CapacitorBom.Read("Comment;Designator;Footprint\n100nF;C3, C4;0402\n", out _);
        Assert.Equal(new[] { "C3", "C4" }, altium.Select(p => p.RefDes));
        Assert.All(altium, p => Assert.Equal(100e-9, p.Farads!.Value, 1e-18));

        Assert.Throws<InvalidOperationException>(() => CapacitorBom.Read("Value,Qty\n100n,2\n", out _));
    }

    [Fact]
    public void APlacementFile_GivesItsValueColumn()
    {
        string pos = string.Join("\n",
            "### Footprint positions",
            "# Ref     Val       Package        PosX       PosY       Rot  Side",
            "C1        100n      C_0402         10.0000    20.0000    0.0  top",
            "C2        4u7       C_0603         12.0000    20.0000    0.0  top",
            "U1        STM32     LQFP-48        30.0000    30.0000    0.0  top");
        var placed = ComponentPlacement.ReadPlacementFile(pos, out _);
        var parts = CapacitorBom.FromPlacement(placed);
        Assert.Equal(100e-9, parts.Single(p => p.RefDes == "C1").Farads!.Value, 1e-18);
        Assert.Equal(4.7e-6, parts.Single(p => p.RefDes == "C2").Farads!.Value, 1e-15);
        Assert.Null(parts.Single(p => p.RefDes == "U1").Farads);
    }

    [Fact]
    public void TheVendorsFile_IsFoundByPartNumber()
    {
        // A two-port shunt measurement written from a known part, under the part number's name.
        var part = new CapacitorModel { CapacitanceFarads = 100e-9, EsrOhms = 0.02, EslHenries = 0.45e-9 };
        var frequencies = PdnAnalysis.LogSweep(1e5, 1e9, 10);
        var s = frequencies.Select(f =>
        {
            Complex z = part.Impedance(f);
            Complex s21 = 2 * z / (2 * z + 50), s11 = -50 / (2 * z + 50);
            return new[,] { { s11, s21 }, { s21, s11 } };
        }).ToList();
        string folder = Path.Combine(Path.GetTempPath(), "opensim-vendor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "GRM155R71C104KA88.s2p"), TouchstoneWriter.Write(frequencies, s, 50, new[] { "P1", "P2" }));
            File.WriteAllText(Path.Combine(folder, "unrelated.s2p"), TouchstoneWriter.Write(frequencies, s, 50, new[] { "P1", "P2" }));
            var found = CapacitorBom.VendorModel(folder, "grm155r71c104ka88", out string? path);
            Assert.NotNull(found);
            Assert.EndsWith("GRM155R71C104KA88.s2p", path);
            foreach (double f in new[] { 1e5, 1e7, 1e9 })
            {
                Complex expected = part.Impedance(f), read = found!.Impedance(f);
                Assert.True((read - expected).Magnitude < 1e-6 * expected.Magnitude, $"{f:g3} Hz: {read} against {expected}");
            }
            Assert.Null(CapacitorBom.VendorModel(folder, "GRM21BR61A106KE19", out _));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
