using OpenSim.Rf.Si;
using Xunit.Abstractions;

namespace OpenSim.Tests.Si;

/// <summary>
/// FU-28: named foils. The Cannonball conversion against the geometry it states and the
/// worked example its application note prints; the built-in catalog well formed and sourced;
/// the gauge nearest a copper thickness; a user's foils file adding and overriding entries;
/// a catalog foil raising the calculator's conductor loss by its own factor.
/// </summary>
public class CopperFoilTests
{
    private readonly ITestOutputHelper _output;

    public CopperFoilTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Cannonball_IsTheStackGeometry()
    {
        // 14 spheres 9 + 4 + 1 on a tile of side 6r: two square pyramids of edge 2r (height r√2 each)
        // plus one diameter is the stack's height, the RMS height of a triangular profile of
        // peak-to-valley Rz, i.e. Rz/(2√3). So r = Rz/(4√3(1 + √2)) = Rz/16.73.
        double stackHeight = 2 * Math.Sqrt(2) + 2;                 // in units of r
        Assert.Equal(1e-6 / (2 * Math.Sqrt(3)) / stackHeight, SurfaceRoughness.CannonballRadius(1e-6), 1e-20);
        Assert.Equal(16.73, 1e-6 / SurfaceRoughness.CannonballRadius(1e-6), 2);

        // Polar application note AP8195 (2017), the worked RTF example, printed to three decimals:
        // drum side Rz 3.175 µm → r = 0.190 µm; matte side after micro-etch 4.443 µm → 0.266 µm.
        Assert.Equal(0.190, SurfaceRoughness.CannonballRadius(3.175e-6) * 1e6, 3);
        Assert.Equal(0.266, SurfaceRoughness.CannonballRadius(4.443e-6) * 1e6, 3);

        // The surface ratio 14·4πr²/(6r)² = 14π/9 and the saturation 1 + 1.5·S = 8.33 (Simonovich,
        // "Cannonball-Huray Model Demystified", 2019, equation 10).
        Assert.Equal(14 * Math.PI / 9, SurfaceRoughness.CannonballSurfaceRatio, 12);
        var rough = SurfaceRoughness.Cannonball(2.5e-6);
        Assert.Equal(8.33, 1 + 1.5 * rough.SurfaceRatio, 2);
        Assert.Equal(1 + 1.5 * rough.SurfaceRatio, rough.Factor(1e20, 5.8e7), 2);
        Assert.Equal(1.0, rough.Factor(1e3, 5.8e7), 3);
        Assert.Equal(SurfaceRoughness.CannonballRadius(2.5e-6), rough.SphereRadiusMeters, 1e-20);
        Assert.Contains("Cannonball from Rz 2.5 µm", rough.Describe());

        // An RMS value is the stack's height itself: the same radius as the Rz of the triangular
        // profile with that RMS, 2√3·Rq.
        Assert.Equal(SurfaceRoughness.CannonballRadius(2 * Math.Sqrt(3) * 0.4e-6),
            SurfaceRoughness.CannonballRadiusFromRms(0.4e-6), 1e-20);
        Assert.Equal(0.4e-6 / (2 * (1 + Math.Sqrt(2))), SurfaceRoughness.CannonballFromRms(0.4e-6).SphereRadiusMeters, 1e-20);

        // At 10 GHz on copper the factor orders the foils as their sheets do.
        double standard = SurfaceRoughness.Cannonball(8.4e-6).Factor(10e9, 5.8e7);
        double hvlp = rough.Factor(10e9, 5.8e7);
        double noProfile = SurfaceRoughness.Cannonball(0.6e-6).Factor(10e9, 5.8e7);
        _output.WriteLine($"10 GHz: Rz 8.4 µm → {standard:g4}, 2.5 µm → {hvlp:g4}, 0.6 µm → {noProfile:g4}");
        Assert.True(standard > hvlp && hvlp > noProfile && noProfile > 1);
    }

    [Fact]
    public void BuiltInCatalog_IsSourcedAndWellFormed()
    {
        var catalog = new FoilCatalog();
        Assert.True(catalog.Foils.Count >= 25, $"{catalog.Foils.Count} foils");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var foil in catalog.Foils)
        {
            Assert.False(string.IsNullOrWhiteSpace(foil.Vendor), foil.Name);
            Assert.False(string.IsNullOrWhiteSpace(foil.Name), foil.Vendor);
            Assert.True(foil.Source.Length > 40, $"{foil.FullName}: source '{foil.Source}'");
            Assert.True(names.Add(foil.FullName), $"duplicate {foil.FullName}");
            Assert.NotEmpty(foil.Gauges);
            for (int i = 0; i < foil.Gauges.Count; i++)
            {
                var gauge = foil.Gauges[i];
                Assert.True(gauge.ThicknessMicrons > 0 && gauge.RoughnessMicrons > 0, foil.FullName);
                if (i > 0) Assert.True(gauge.ThicknessMicrons > foil.Gauges[i - 1].ThicknessMicrons, $"{foil.FullName}: gauges out of order");
                if (gauge.RoughnessLowMicrons is { } low) Assert.True(low < gauge.RoughnessMicrons, foil.FullName);
                Assert.True(foil.Roughness(gauge).IsHuray);
            }
            Assert.Same(foil, catalog.Find(foil.FullName));
            Assert.Same(foil, catalog.Find(foil.FullName.ToUpperInvariant()));
        }

        // A bare name finds the foil when it is unique; a miss names the near matches.
        Assert.Equal("Circuit Foil BF-TZA", catalog.Find("BF-TZA")!.FullName);
        Assert.Equal("Isola HVLP (VLP2)", catalog.Find("hvlp (vlp2)")!.FullName);
        Assert.Null(catalog.Find("nothing of the kind"));
        var miss = Assert.Throws<KeyNotFoundException>(() => catalog.Require("TZA-B9"));
        Assert.Contains("Did you mean", miss.Message);
        Assert.Contains("Circuit Foil TZA-B", miss.Message);
        Assert.Contains("The catalog has", Assert.Throws<KeyNotFoundException>(() => catalog.Require("zzz")).Message);
    }

    [Fact]
    public void GaugeFor_PicksTheNearestWeightInLogThickness()
    {
        var tza = new FoilCatalog().Require("Circuit Foil TZA");      // 12, 18, 35, 70, 105, 140, 175, 210 µm
        Assert.Equal(35, tza.GaugeFor(35e-6).ThicknessMicrons);
        Assert.Equal(35, tza.GaugeFor(30e-6).ThicknessMicrons);
        Assert.Equal(18, tza.GaugeFor(25e-6).ThicknessMicrons);      // ln(25/18) = 0.329 < ln(35/25) = 0.336
        Assert.Equal(70, tza.GaugeFor(50e-6).ThicknessMicrons);      // ln(70/50) = 0.336 < ln(50/35) = 0.357
        Assert.Equal(210, tza.GaugeFor(1e-3).ThicknessMicrons);
        Assert.Equal(12, tza.GaugeFor(0).ThicknessMicrons);
        var single = new FoilCatalog().Require("Isola HVLP3 (VLP1)");
        Assert.Equal(35, single.GaugeFor(200e-6).ThicknessMicrons);
    }

    [Fact]
    public void Roughness_OfACatalogFoil_IsTheCannonballOfItsSheetValue()
    {
        var catalog = new FoilCatalog();

        // An Rz sheet: BF-TZA's JIS limit 2.5 µm at ½ oz.
        var bfTza = catalog.Require("Circuit Foil BF-TZA").Roughness(18e-6);
        Assert.Equal(SurfaceRoughness.CannonballRadius(2.5e-6), bfTza.SphereRadiusMeters, 1e-20);
        Assert.Equal(SurfaceRoughness.CannonballSurfaceRatio, bfTza.SurfaceRatio);
        Assert.Contains("BF-TZA", bfTza.Describe());
        Assert.Contains("18 µm", bfTza.Describe());

        // A range: the top of TZA's 1 oz range, 5.0–8.4 µm.
        var tza = catalog.Require("Circuit Foil TZA").Roughness(35e-6);
        Assert.Equal(SurfaceRoughness.CannonballRadius(8.4e-6), tza.SphereRadiusMeters, 1e-20);
        Assert.Contains("5–8.4 µm", tza.Describe());

        // An RMS sheet: Rogers' rolled foil, Sq 0.4 µm, by the RMS route.
        var rolled = catalog.Require("Rogers Rolled foil").Roughness(35e-6);
        Assert.Equal(SurfaceRoughness.CannonballRadiusFromRms(0.4e-6), rolled.SphereRadiusMeters, 1e-20);
        Assert.Contains("RMS 0.4 µm typical", rolled.Describe());

        // Smoother foils have smaller spheres, so lower loss at every frequency.
        Assert.True(rolled.Factor(5e9, 5.8e7) < bfTza.Factor(5e9, 5.8e7));
        Assert.True(bfTza.Factor(5e9, 5.8e7) < tza.Factor(5e9, 5.8e7));
        foreach (var line in catalog.Require("Circuit Foil BF-TZA").Describe()) _output.WriteLine(line);
    }

    [Fact]
    public void UserFile_AddsAndOverrides()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opensim-foils", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "foils.json");
            File.WriteAllText(path, """
                [
                  { "vendor": "Circuit Foil", "name": "BF-TZA", "family": "HVLP",
                    "gauges": [ { "thicknessMicrons": 35, "roughnessMicrons": 2.0 } ],
                    "source": "my own copy of the sheet, the 1 oz column" },
                  { "vendor": "Acme", "name": "X1", "statistic": "RootMeanSquare", "treatedSide": "drum side",
                    "gauges": [ { "thicknessMicrons": 18, "roughnessMicrons": 0.7, "isTypical": true },
                                { "thicknessMicrons": 35, "roughnessMicrons": 0.9, "isTypical": true } ],
                    "source": "Acme foil data sheet rev 2" }
                ]
                """);
            var builtIn = new FoilCatalog();
            var merged = builtIn.WithFile(path);
            Assert.Equal(builtIn.Foils.Count + 1, merged.Foils.Count);

            var overridden = merged.Require("Circuit Foil BF-TZA");
            Assert.Equal(2.0, Assert.Single(overridden.Gauges).RoughnessMicrons);
            Assert.StartsWith("my own copy", overridden.Source);
            Assert.Equal(2.5, builtIn.Require("Circuit Foil BF-TZA").GaugeFor(35e-6).RoughnessMicrons);   // the built-in list is untouched
            Assert.Equal(SurfaceRoughness.CannonballRadius(2.0e-6), overridden.Roughness(35e-6).SphereRadiusMeters, 1e-20);

            var acme = merged.Require("X1");
            Assert.Equal(RoughnessStatistic.RootMeanSquare, acme.Statistic);
            Assert.Equal(SurfaceRoughness.CannonballRadiusFromRms(0.9e-6), acme.Roughness(35e-6).SphereRadiusMeters, 1e-20);
            Assert.Contains("Acme X1", merged.Names);

            // The built-in catalog round-trips through the same file format.
            string copy = Path.Combine(dir, "builtin.json");
            FoilCatalog.WriteJson(copy, FoilCatalog.BuiltIn);
            var read = FoilCatalog.ReadJson(copy);
            Assert.Equal(FoilCatalog.BuiltIn.Count, read.Count);
            for (int i = 0; i < read.Count; i++)
            {
                var a = FoilCatalog.BuiltIn[i];
                var b = read[i];
                Assert.Equal(a.FullName, b.FullName);
                Assert.Equal(a.Statistic, b.Statistic);
                Assert.Equal(a.Source, b.Source);
                Assert.Equal(a.Note, b.Note);
                Assert.Equal(a.Gauges, b.Gauges);
            }

            // A file without gauges is refused by name.
            File.WriteAllText(path, """[ { "vendor": "Acme", "name": "X2", "gauges": [], "source": "s" } ]""");
            Assert.Contains("X2", Assert.Throws<InvalidDataException>(() => FoilCatalog.ReadJson(path)).Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CatalogFoil_RaisesTheConductorLossByItsFactor()
    {
        const double sigma = 5.8e7;
        var foil = new FoilCatalog().Require("Circuit Foil BF-TZA").Roughness(35e-6);
        var spec = new LineSpec { WidthMeters = 0.2e-3, HeightMeters = 0.1e-3, ThicknessMeters = 35e-6, FrequencyHz = 10e9 };
        var smooth = ImpedanceCalculator.Solve(spec).Modes[0];
        var rough = ImpedanceCalculator.Solve(spec with { Model = RlgcModel.Board with { Roughness = foil } }).Modes[0];
        double k = foil.Factor(10e9, sigma);
        double ratio = rough.ConductorLossDbPerMeter / smooth.ConductorLossDbPerMeter;
        _output.WriteLine($"BF-TZA at 10 GHz: factor {k:g4}, conductor loss {smooth.ConductorLossDbPerMeter:g4} → " +
                          $"{rough.ConductorLossDbPerMeter:g4} dB/m (×{ratio:g4})");
        Assert.True(k > 1.3, $"factor {k}");
        Assert.InRange(ratio, 1 + 0.8 * (k - 1), k);
        Assert.Equal(smooth.DielectricLossDbPerMeter, rough.DielectricLossDbPerMeter, 0.01 * smooth.DielectricLossDbPerMeter);
    }
}
