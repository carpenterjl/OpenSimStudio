using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.Interop;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-16: stock lens catalogs, lens search, Insert Lens and the private catalog
/// (docs/Optics-Conventions.md section 53). No vendor file is in the repository (decided
/// 2026-11-12): the "vendor" files here are written in the tests with OP-94's writer
/// (<see cref="ZemaxLensFiles.Export"/>, the keyword rules of section 52) or as text from those
/// rules, and their stated focal lengths are the closed-form values of the lenses they hold
/// (derived beside each), as a vendor's stated focal length is the design value of its file.
/// Indices (1.5, 1.7) and radii are inputs, not reference values. A folder of real vendor files
/// with their spec-sheet focal lengths is read when present (<see cref="RealVendorFiles_WhenPresent_ReproduceTheirStatedFocalLengths"/>).
/// </summary>
public sealed class LensCatalogTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "op16-" + Guid.NewGuid().ToString("N"));

    public LensCatalogTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly IReadOnlyList<OpticalCatalog> NoGlass = Array.Empty<OpticalCatalog>();

    /// <summary>A lens file's optics record: entrance pupil <paramref name="epd"/>, one field on
    /// axis, the d line (<see cref="SpectralLines"/>), stop on surface 1, object at infinity.</summary>
    internal static OpticsSettings Lens(string title, double epd, IEnumerable<OpticsSurfaceSettings> lensSurfaces, LensUnit unit = LensUnit.Millimeter,
        double? objectThickness = null)
    {
        var surfaces = new List<OpticsSurfaceSettings>
        {
            objectThickness is double t ? new() { Thickness = t } : new() { ThicknessIsInfinite = true },
        };
        surfaces.AddRange(lensSurfaces);
        surfaces[1] = surfaces[1] with { IsStop = true };
        surfaces.Add(new OpticsSurfaceSettings());
        return new OpticsSettings
        {
            Mode = OpticsModes.SavedName(OpticsMode.Sequential),
            SystemData = new OpticsSystemSettings
            {
                Title = title,
                LensUnit = ConventionNames.LensUnits.SavedName(unit),
                ApertureType = ApertureTypes.Names.SavedName(ApertureType.EntrancePupilDiameter),
                ApertureValue = epd,
                FieldType = FieldTypes.Names.SavedName(FieldType.Angle),
                Fields = new() { new() },
                Wavelengths = new() { new() { Micrometers = SpectralLines.LowerD.Micrometers } },
                PrimaryWavelength = 1,
            },
            Surfaces = surfaces,
        };
    }

    private static OpticsSurfaceSettings S(double? radius, double thickness, double? index = null, double conic = 0) =>
        new() { Radius = radius, Thickness = thickness, Index = index, Conic = conic };

    // Closed forms (derived from the two-surface paraxial trace, Conventions §11): a thick lens of
    // index n, curvatures c1, c2, thickness t in air has φ = (n−1)(c1−c2) + (n−1)² t c1 c2 / n.
    private static double ThickLensEfl(double n, double r1, double r2, double t)
    {
        double c1 = double.IsInfinity(r1) ? 0 : 1 / r1, c2 = double.IsInfinity(r2) ? 0 : 1 / r2;
        return 1 / ((n - 1) * (c1 - c2) + (n - 1) * (n - 1) * t * c1 * c2 / n);
    }

    /// <summary>The vendor folder: six lenses, a file that cannot be read, a file that is not a lens.</summary>
    private string VendorFolder(string name = "ACME")
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        void Write(string file, OpticsSettings optics) => ZemaxLensFiles.Export(optics, Path.Combine(dir, file));
        // Plano-convex, n = 1.5, R1 = 50: stated EFL R1/(n−1) = 100 mm (c2 = 0, exact for any t).
        Write("PCX-100 plano convex.zmx", Lens("PCX-100", 10, new[] { S(50, 4, 1.5), S(null, 96) }));
        // Equi-convex, n = 1.5, R = ±50, t = 5: stated EFL ThickLensEfl(1.5, 50, −50, 5).
        Write("BCX-50.zmx", Lens("BCX-50", 12, new[] { S(50, 5, 1.5), S(-50, 48) }));
        // Meniscus R1 = 50, R2 = 100, t = 3.
        Write("MEN-200.zmx", Lens("MEN-200", 10, new[] { S(50, 3, 1.5), S(100, 190) }));
        // Bi-concave R1 = −40, R2 = 60, t = 2 (negative EFL).
        Write("BCC-48.zmx", Lens("BCC-48", 8, new[] { S(-40, 2, 1.5), S(60, 10) }));
        // Cemented doublet, n 1.5 and 1.7.
        // Its EFL is about 103.6 mm (a paraxial trace, derived), so the name is only a label; the gate searches 98–102 mm.
        Write("ACH-80.zmx", Lens("ACH-80", 20, new[] { S(50, 6, 1.5), S(-40, 2, 1.7), S(-150, 75) }));
        // Conic front, plano back: aspheric; stated EFL R1/(n−1) = 120 mm (the conic has no paraxial power).
        Write("ASP-120.zmx", Lens("ASP-120", 15, new[] { S(60, 5, 1.5, conic: -0.5), S(null, 115) }));
        // A file with a keyword the reader does not know.
        File.WriteAllText(Path.Combine(dir, "BAD-1.zmx"),
            "VERS 1\nMODE SEQ\nNAME Bad\nUNIT MM X W X CM MR CPMM\nENPD 10\nFTYP 0 0 1 1 0 0 0\nXFLN 0\nYFLN 0\nFWGN 1\nWAVM 1 0.55 1\nPWAV 1\n" +
            "SURF 0\n  TYPE STANDARD\n  CURV 0 0 0 0 0 \"\"\n  DISZ INFINITY\nSURF 1\n  TYPE STANDARD\n  CURV 0.01 0 0 0 0 \"\"\n  DISZ 5\n  FOOO 1\nSURF 2\n  TYPE STANDARD\n  DISZ 0\n");
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a lens");
        return dir;
    }

    private static void Close(double expected, double? actual, double relative, string what)
    {
        Assert.True(actual.HasValue, $"{what}: no value");
        Assert.True(Math.Abs(actual!.Value - expected) <= relative * Math.Abs(expected),
            FormattableString.Invariant($"{what}: expected {expected:R}, got {actual:R}"));
    }

    // ------------------------------------------------------------------ the gate

    /// <summary>
    /// Gate: a folder of vendor files becomes a searchable catalog, and inserting a lens
    /// reproduces the file's stated focal length. The catalog lists every readable file with the
    /// stated EFL (1e-12 relative: the paraxial engine against the closed form, a few roundings),
    /// names the unreadable one, ignores the text file; an EFL search finds exactly the 100 mm
    /// lens; inserted into another lens behind a plane window, the inserted surfaces taken as a
    /// group have the stated 100 mm, and so does the whole lens (a plane window in air has no power).
    /// </summary>
    [Fact]
    public void AFolderOfVendorFiles_BecomesASearchableCatalog_AndAnInsertedLensKeepsItsStatedFocalLength()
    {
        var catalog = LensCatalog.FromFolder(VendorFolder(), NoGlass);
        Assert.Equal("ACME", catalog.Name);
        Assert.Equal(new[] { "ACH-80", "ASP-120", "BCC-48", "BCX-50", "MEN-200", "PCX-100" }, catalog.Lenses.Select(l => l.PartName));
        Assert.Single(catalog.Problems, p => p.StartsWith("BAD-1.zmx: left out") && p.Contains("FOOO"));
        Assert.DoesNotContain(catalog.Problems, p => p.Contains("readme"));

        var pcx = catalog.Lenses.Single(l => l.PartName == "PCX-100");
        Close(100, pcx.EffectiveFocalLengthMm, 1e-12, "PCX-100 EFL");
        Close(10, pcx.EntrancePupilDiameterMm, 1e-12, "PCX-100 EPD");
        Assert.Equal("PCX-100 plano convex.zmx", pcx.Source);
        Assert.Equal("(P,S,1)", pcx.Code);
        Close(ThickLensEfl(1.5, 50, -50, 5), catalog.Lenses.Single(l => l.PartName == "BCX-50").EffectiveFocalLengthMm, 1e-12, "BCX-50 EFL");
        Close(120, catalog.Lenses.Single(l => l.PartName == "ASP-120").EffectiveFocalLengthMm, 1e-12, "ASP-120 EFL");

        var found = LensCatalogSearch.Find(new[] { catalog }, new LensSearchCriteria { UseEffectiveFocalLength = true, FocalLengthMinMm = 98, FocalLengthMaxMm = 102 });
        Assert.Equal(new[] { "PCX-100" }, found.Select(l => l.PartName));

        // The current lens: a 3 mm plane window of index 1.5 at the stop, 10 mm before the image.
        var current = Lens("Window", 10, new[] { S(null, 3, 1.5), S(null, 10) });
        var file = catalog.Import(pcx, NoGlass).Optics;
        var inserted = StockLensInsertion.Insert(current, file, 3);
        Assert.Equal(3, inserted.First);
        Assert.Equal(4, inserted.Last);
        var surfaces = inserted.Settings.Surfaces!;
        Assert.Equal(6, surfaces.Count);
        Close(50, surfaces[3].Radius, 1e-15, "inserted radius");   // 1/(1/50) through the file
        Assert.False(surfaces[3].IsStop);
        Assert.True(surfaces[1].IsStop);
        Assert.Equal(96, surfaces[4].Thickness);   // the file's distance to its own image is kept

        var context = new LensEditContext(inserted.Settings.SystemData, NoGlass);
        Close(100, StockLensProperties.OfGroup(surfaces, 3, 4, context).EffectiveFocalLengthMm, 1e-12, "inserted group EFL");
        Close(100, MakeFocal.EffectiveFocalLength(surfaces, context), 1e-12, "whole lens EFL");
    }

    /// <summary>
    /// Owner-supplied vendor files: when <c>plans/sources/optics/op-16-help/vendor/</c> holds lens
    /// files and <c>stated-focal-lengths.tsv</c> (file, stated EFL in mm, the tolerance in mm the
    /// spec sheet's digits allow, the spec sheet), each file's catalog EFL and the EFL of its
    /// surfaces inserted into an empty lens match the stated value within that tolerance. Vendor
    /// files may not be redistributed (decided 2026-11-12), so the folder is not in the repository
    /// and this test does nothing where it is absent, saying so.
    /// </summary>
    [Fact]
    public void RealVendorFiles_WhenPresent_ReproduceTheirStatedFocalLengths()
    {
        string? folder = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && folder is null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "plans", "sources", "optics", "op-16-help", "vendor");
            if (Directory.Exists(candidate)) folder = candidate;
        }
        string table = folder is null ? "" : Path.Combine(folder, "stated-focal-lengths.tsv");
        if (folder is null || !File.Exists(table))
        {
            _out.WriteLine("DID NOTHING: no plans/sources/optics/op-16-help/vendor/stated-focal-lengths.tsv on this machine.");
            return;
        }
        var glass = new GlassCatalogLibrary().Catalogs;
        var catalog = LensCatalog.FromFolder(folder, glass);
        foreach (var p in catalog.Problems) _out.WriteLine("Problem: " + p);
        int rows = 0;
        foreach (string line in File.ReadAllLines(table))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split('\t');
            string file = f[0];
            double stated = double.Parse(f[1], CultureInfo.InvariantCulture), tolerance = double.Parse(f[2], CultureInfo.InvariantCulture);
            var lens = catalog.Lenses.SingleOrDefault(l => string.Equals(l.Source, file, StringComparison.OrdinalIgnoreCase));
            Assert.True(lens is not null, $"{file} is not in the catalog: {string.Join(" ", catalog.Problems)}");
            Assert.True(Math.Abs(lens!.EffectiveFocalLengthMm!.Value - stated) <= tolerance, $"{file}: catalog EFL {lens.EffectiveFocalLengthMm} mm, stated {stated} ± {tolerance} mm ({(f.Length > 3 ? f[3] : "")})");
            var optics = catalog.Import(lens, glass).Optics;
            var empty = optics with { Surfaces = new() { new() { ThicknessIsInfinite = true }, new() { IsStop = true }, new() } };
            var inserted = StockLensInsertion.Insert(empty, optics, 2, new InsertLensOptions { IgnoreObject = true });
            var context = new LensEditContext(inserted.Settings.SystemData, StockLensProperties.CatalogsFor(optics, glass, null));
            double efl = StockLensProperties.OfGroup(inserted.Settings.Surfaces!, inserted.First, inserted.Last, context).EffectiveFocalLengthMm!.Value;
            Assert.True(Math.Abs(efl - stated) <= tolerance, $"{file}: inserted EFL {efl} mm, stated {stated} ± {tolerance} mm");
            _out.WriteLine(FormattableString.Invariant($"{file}: stated {stated} mm, catalog {lens.EffectiveFocalLengthMm:G10} mm, inserted {efl:G10} mm"));
            rows++;
        }
        Assert.True(rows > 0, "stated-focal-lengths.tsv lists no file.");
    }

    // ------------------------------------------------------------------ classification and search

    [Fact]
    public void ShapesTypesAndElements_AreClassifiedFromTheSurfaces()
    {
        var catalog = LensCatalog.FromFolder(VendorFolder(), NoGlass);
        StockLens L(string part) => catalog.Lenses.Single(l => l.PartName == part);
        Assert.Equal("(B,S,1)", L("BCC-48").Code);
        Assert.Equal("(E,S,1)", L("BCX-50").Code);
        Assert.Equal("(M,S,1)", L("MEN-200").Code);
        Assert.Equal("(?,S,2)", L("ACH-80").Code);
        Assert.Equal("(P,A,1)", L("ASP-120").Code);
        Assert.True(L("BCC-48").EffectiveFocalLengthMm < 0);
        Close(ThickLensEfl(1.5, -40, 60, 2), L("BCC-48").EffectiveFocalLengthMm, 1e-12, "BCC-48 EFL");

        var mirror = Lens("M", 10, new[] { new OpticsSurfaceSettings { Radius = -200, Material = SavedSurfaces.Mirror, Thickness = -100 } });
        Assert.Equal(1, StockLensProperties.ElementCount(mirror.Surfaces!));
        Assert.Equal(StockLensShape.Other, StockLensProperties.Shape(mirror.Surfaces!));
        var toroid = Lens("T", 10, new[] { new OpticsSurfaceSettings { Type = "Toroidal", Radius = 50, Thickness = 3, Index = 1.5, Conic = -1 }, S(null, 50) });
        Assert.Equal(StockLensType.Toroidal, StockLensProperties.Type(toroid.Surfaces!));
    }

    [Fact]
    public void TheSearch_CombinesEveryCriterion_AndRefusesAnInvertedRange()
    {
        var catalog = LensCatalog.FromFolder(VendorFolder(), NoGlass);
        string[] Parts(LensSearchCriteria c) => LensCatalogSearch.Find(new[] { catalog }, c).Select(l => l.PartName).ToArray();

        Assert.Equal(6, Parts(new LensSearchCriteria()).Length);
        Assert.Equal(new[] { "BCX-50", "MEN-200" }, Parts(new LensSearchCriteria { Shapes = new HashSet<StockLensShape> { StockLensShape.Equi, StockLensShape.Meniscus } }));
        Assert.Equal(new[] { "ASP-120" }, Parts(new LensSearchCriteria { Types = new HashSet<StockLensType> { StockLensType.Aspheric } }));
        Assert.Equal(new[] { "ACH-80" }, Parts(new LensSearchCriteria { Elements = 2 }));
        Assert.Equal(new[] { "BCC-48" }, Parts(new LensSearchCriteria { UseEffectiveFocalLength = true, FocalLengthMinMm = -1000, FocalLengthMaxMm = 0 }));
        // Pupils: PCX-100 10, MEN-200 10, BCC-48 8 mm (the files' ENPD, stop on the first surface, object at infinity).
        Assert.Equal(new[] { "MEN-200", "PCX-100" }, Parts(new LensSearchCriteria { UseEntrancePupilDiameter = true, DiameterMinMm = 9, DiameterMaxMm = 11 }));
        Assert.Equal(new[] { "PCX-100" }, Parts(new LensSearchCriteria { Text = "pcx" }));
        Assert.Throws<ArgumentException>(() => LensCatalogSearch.Find(new[] { catalog }, new LensSearchCriteria { UseEffectiveFocalLength = true, FocalLengthMinMm = 5, FocalLengthMaxMm = 1 }));
    }

    /// <summary>The help's ±5% default ranges around a surface group (derived: 100 ± 5, 2 × 6 ± 5%).</summary>
    [Fact]
    public void TheDefaultRanges_ArePlusAndMinusFivePercentOfASurfaceGroup()
    {
        var current = Lens("PCX", 10, new[] { S(50, 4, 1.5) with { SemiDiameter = 6 }, S(null, 96) with { SemiDiameter = 6 } });
        var group = StockLensProperties.OfGroup(current.Surfaces!, 1, 2, new LensEditContext(current.SystemData, NoGlass));
        Close(100, group.EffectiveFocalLengthMm, 1e-12, "group EFL");
        Close(12, group.DiameterMm, 1e-15, "group diameter");
        var c = LensSearchCriteria.Around(group);
        Close(95, c.FocalLengthMinMm, 1e-12, "EFL min");
        Close(105, c.FocalLengthMaxMm, 1e-12, "EFL max");
        Close(11.4, c.DiameterMinMm, 1e-12, "diameter min");
        Close(12.6, c.DiameterMaxMm, 1e-12, "diameter max");
    }

    [Fact]
    public void TheCatalogReport_ListsTheLensesThreeTimes_ByNameEflAndPupil()
    {
        var catalog = LensCatalog.FromFolder(VendorFolder(), NoGlass);
        var criteria = new LensSearchCriteria { Elements = 1 };
        var lenses = LensCatalogSearch.Find(new[] { catalog }, criteria);
        var report = LensCatalogSearch.Report(catalog.Name, criteria, lenses);
        Assert.Equal("Catalog report: ACME", report[0]);
        Assert.Contains("Elements: 1", report);
        Assert.Contains("5 lenses.", report);
        int byName = report.ToList().IndexOf("Sorted by part name"), byEfl = report.ToList().IndexOf("Sorted by effective focal length"),
            byEpd = report.ToList().IndexOf("Sorted by entrance pupil diameter");
        Assert.True(byName > 0 && byEfl > byName && byEpd > byEfl);
        // By EFL: BCC-48 (negative) first; MEN-200 last (φ = 0.5(1/50 − 1/100) + 0.25·3/(50·100·1.5) = 0.0051 /mm,
        // about 196 mm, from ThickLensEfl's formula), after ASP-120 (120 mm).
        Assert.StartsWith("BCC-48", report[byEfl + 2]);
        Assert.StartsWith("ASP-120", report[byEfl + 5]);
        Assert.StartsWith("MEN-200", report[byEfl + 6]);
        Assert.Equal(5, byEfl - byName - 3);
    }

    // ------------------------------------------------------------------ Insert Lens

    /// <summary>Pickups of the current lens follow their surfaces, the file's own pickups are
    /// renumbered into the new numbering, and its object thickness becomes a dummy surface
    /// unless Ignore Object is set.</summary>
    [Fact]
    public void InsertLens_RenumbersPickups_AndKeepsOrIgnoresTheObjectThickness()
    {
        var current = Lens("Two", 10, new[]
        {
            S(80, 4, 1.5), S(-80, 5),
            S(60, 4, 1.5) with { Flags = new() { SurfaceFlags.Pickup(SavedSurfaces.RadiusColumn, 1, SavedSurfaces.RadiusColumn, -1) } },
            S(null, 50),
        });
        var lensFile = Lens("Pair", 10, new[]
        {
            S(40, 3, 1.5), S(-40, 2),
            S(40, 3, 1.5) with { Flags = new() { SurfaceFlags.Pickup(SavedSurfaces.RadiusColumn, 1, SavedSurfaces.RadiusColumn) } },
            S(-40, 30),
        }, objectThickness: 20);

        var kept = StockLensInsertion.Insert(current, lensFile, 3);
        var s = kept.Settings.Surfaces!;
        Assert.Equal(6 + 5, s.Count);
        Assert.Equal(20, s[3].Thickness);   // the dummy carrying the object thickness
        Assert.Null(s[3].Radius);
        Assert.Equal(3, kept.First);   // the dummy is the first inserted surface
        Assert.Equal(7, kept.Last);
        Assert.Equal(40, s[4].Radius);
        Assert.Equal(4, s[6].Flags!.Single().PickupSurface);        // file surface 1 → 4
        Assert.Equal(1, s[8].Flags!.Single().PickupSurface);        // the current lens's pickup from surface 1 stays
        Assert.Equal(60, s[8].Radius);                               // old surface 3 moved to 8

        var ignored = StockLensInsertion.Insert(current, lensFile, 3, new InsertLensOptions { IgnoreObject = true });
        Assert.Equal(6 + 4, ignored.Settings.Surfaces!.Count);
        Assert.Equal(40, ignored.Settings.Surfaces![3].Radius);
        Assert.Equal(3, ignored.Settings.Surfaces![5].Flags!.Single().PickupSurface);
        Assert.Contains(ignored.Notes, n => n.Contains("object thickness was ignored"));
    }

    [Fact]
    public void InsertLens_ChangesSignsInMirrorSpace_ConvertsUnits_AndCanReverse()
    {
        var mirror = Lens("Mirror", 10, new[] { new OpticsSurfaceSettings { Radius = -400, Material = SavedSurfaces.Mirror, Thickness = -150 } });
        var pcx = Lens("PCX", 10, new[] { S(50, 4, 1.5), S(null, 96) });
        var flipped = StockLensInsertion.Insert(mirror, pcx, 2);
        Assert.Equal(-50, flipped.Settings.Surfaces![2].Radius);
        Assert.Equal(-4, flipped.Settings.Surfaces![2].Thickness);
        Assert.Equal(-96, flipped.Settings.Surfaces![3].Thickness);
        var unflipped = StockLensInsertion.Insert(mirror, pcx, 2, new InsertLensOptions { ChangeSignInMirrorSpace = false });
        Assert.Equal(50, unflipped.Settings.Surfaces![2].Radius);

        // A lens in inches: R1 = 2 in, n = 1.5 gives 4 in (derived), inserted into a millimeter lens.
        var inches = Lens("PCX-IN", 0.5, new[] { S(2, 0.2, 1.5), S(null, 3.8) }, LensUnit.Inch);
        var empty = Lens("Empty", 10, new[] { S(null, 0) });
        var converted = StockLensInsertion.Insert(empty, inches, 2);
        double mmPerInch = OpticsConventions.MetersPer(LensUnit.Inch) / OpticsConventions.MetersPer(LensUnit.Millimeter);
        Close(2 * mmPerInch, converted.Settings.Surfaces![2].Radius, 1e-15, "converted radius");
        var context = new LensEditContext(converted.Settings.SystemData, NoGlass);
        Close(4 * mmPerInch, StockLensProperties.OfGroup(converted.Settings.Surfaces!, 2, 3, context).EffectiveFocalLengthMm, 1e-12, "converted EFL");

        var reversed = StockLensInsertion.Insert(empty, pcx, 2, new InsertLensOptions { Reverse = true });
        Assert.Null(reversed.Settings.Surfaces![2].Radius);
        Assert.Equal(-50, reversed.Settings.Surfaces![3].Radius);
        Assert.Equal(96, reversed.Settings.Surfaces![3].Thickness);
    }

    [Fact]
    public void InsertLens_RefusesAPositionOutsideTheLens()
    {
        var pcx = Lens("PCX", 10, new[] { S(50, 4, 1.5), S(null, 96) });
        var ex = Assert.Throws<ArgumentException>(() => StockLensInsertion.Insert(pcx, pcx, 4));
        Assert.Contains("before surface 1 to 3", ex.Message);
        Assert.Throws<ArgumentException>(() => StockLensInsertion.Insert(pcx, pcx, 0));
    }

    // ------------------------------------------------------------------ private catalogs and the library

    /// <summary>Make Private Lens Catalog: the file holds every lens, loads with the same search
    /// data, and a lens imports from it after the source folder is gone.</summary>
    [Fact]
    public void APrivateCatalog_RoundTrips_AndStandsOnItsOwn()
    {
        string folder = VendorFolder("Mine");
        var original = LensCatalog.FromFolder(folder, NoGlass);
        string path = Path.Combine(_root, "Library", "Mine" + LensCatalog.FileExtension);
        var loaded = LensCatalog.MakePrivate(folder, path, NoGlass);
        Assert.Equal(original.Lenses, loaded.Lenses);
        Assert.Equal(original.Problems, loaded.Problems);
        Directory.Delete(folder, recursive: true);
        var pcx = loaded.Lenses.Single(l => l.PartName == "PCX-100");
        Close(50, loaded.Import(pcx, NoGlass).Optics.Surfaces![1].Radius, 1e-15, "radius from the private catalog");

        File.WriteAllText(Path.Combine(_root, "bad" + LensCatalog.FileExtension), "{\"format\":\"something else\"}");
        Assert.Throws<InvalidDataException>(() => LensCatalog.Load(Path.Combine(_root, "bad" + LensCatalog.FileExtension)));
    }

    [Fact]
    public void TheLibrary_ReadsPrivateCatalogsAndVendorFolders_AndNamesWhatItCannotLoad()
    {
        string library = Path.Combine(_root, "Library");
        Directory.CreateDirectory(library);
        VendorFolder(Path.Combine("Library", "ACME"));
        LensCatalog.MakePrivate(VendorFolder("Mine"), Path.Combine(library, "Mine" + LensCatalog.FileExtension), NoGlass);
        File.WriteAllText(Path.Combine(library, "Broken" + LensCatalog.FileExtension), "not json");
        Directory.CreateDirectory(Path.Combine(library, "Empty"));

        var lib = LensCatalogLibrary.Open(library, NoGlass);
        Assert.Equal(new[] { "ACME", "Mine" }, lib.Catalogs.Select(c => c.Name));
        Assert.Contains(lib.Problems, p => p.StartsWith("Broken.osslcat: not loaded"));
        Assert.Contains(lib.Problems, p => p.StartsWith("ACME: BAD-1.zmx"));
        Assert.Equal(12, LensCatalogSearch.Find(lib.Catalogs, new LensSearchCriteria()).Count);

        Assert.Contains(LensCatalogLibrary.Open(Path.Combine(_root, "nowhere"), NoGlass).Problems, p => p.Contains("does not exist"));
        string user = Path.Combine(_root, "user");
        Assert.Equal(Path.Combine(user, "StockLensCatalogs"), LensCatalogLibrary.ChosenFolder(null, user));
        LensCatalogLibrary.ChooseFolder(library, user);
        Assert.Equal(Path.GetFullPath(library), LensCatalogLibrary.ChosenFolder(null, user));
    }

    [Fact]
    public void ThePartName_IsTheFirstWordOfTheFileName()
    {
        // The help's example (Make Private Lens Catalog): "Lens number 5.ZMX" → "Lens"; "L005.ZMX" → "L005".
        Assert.Equal("Lens", LensCatalog.PartNameOf("Lens number 5.ZMX"));
        Assert.Equal("L005", LensCatalog.PartNameOf("L005.ZMX"));
    }

    /// <summary>
    /// Wave 10 wiring: a catalog folder indexes CODE V .seq and OSLO .len files through OP-95's
    /// readers (docs/Optics-Conventions.md sections 53 and 54), beside a .zmx. Both files are
    /// written here from section 54's command rules (none is a vendor's). The OSLO lens is
    /// plano-convex with constant index 1.5 and R1 = 50, so its EFL is R1/(n − 1) = 100 mm
    /// (derived, as for PCX-100 above). The CODE V lens uses a model glass (the reader's only
    /// numeric glass form), so its EFL is checked against the same file read by
    /// <see cref="LensFileImport.Import"/>, which is the import the catalog must agree with.
    /// </summary>
    [Fact]
    public void ACatalogFolder_IndexesCodeVAndOsloFiles_ThroughTheirReaders()
    {
        string dir = Path.Combine(_root, "MIXED");
        Directory.CreateDirectory(dir);
        ZemaxLensFiles.Export(Lens("PCX-100", 10, new[] { S(50, 4, 1.5), S(null, 96) }), Path.Combine(dir, "PCX-100.zmx"));
        File.WriteAllText(Path.Combine(dir, "OSL-100 oslo.len"),
            "LEN NEW \"OSL-100\" 1 3\nEBR 5; ANG 0; WV 0.55\nTH 1e20\nNXT; RD 50; TH 4; GLA 1.5 1.5; AST\nNXT; TH 96\nNXT\nEND 3\n");
        string seq = Path.Combine(dir, "CDV-1 codev.seq");
        File.WriteAllText(seq, "RDM\nEPD 10\nWL 587.5618\nYAN 0\nSO 0 1e20\nS 50 4 1.5:60\nSTO\nS 0 96\nSI 0 0\n");

        // The CODE V glass 1.5:60 is a model glass, which is trained on the catalogs (section 54).
        var glass = OpenSim.Tests.Optics.Gates.CodeVOsloImportGateTests.Catalogs();
        var catalog = LensCatalog.FromFolder(dir, glass);
        foreach (var p in catalog.Problems) _out.WriteLine(p);
        Assert.Empty(catalog.Problems);
        Assert.Equal(new[] { "CDV-1", "OSL-100", "PCX-100" }, catalog.Lenses.Select(l => l.PartName));
        Assert.Equal(new[] { "CDV-1 codev.seq", "OSL-100 oslo.len", "PCX-100.zmx" }, catalog.Lenses.Select(l => l.Source));

        var oslo = catalog.Lenses.Single(l => l.PartName == "OSL-100");
        Close(100, oslo.EffectiveFocalLengthMm, 1e-12, "OSL-100 EFL");
        var codeV = catalog.Lenses.Single(l => l.PartName == "CDV-1");
        var direct = StockLensProperties.Of(LensFileImport.Import(seq, glass).Optics, glass);
        Assert.Equal(direct.EffectiveFocalLengthMm, codeV.EffectiveFocalLengthMm);
        Assert.True(codeV.EffectiveFocalLengthMm > 0);

        // Read again from the catalog (the Insert Lens path) by the same extension dispatch.
        Assert.Equal(1.5, catalog.Import(oslo, glass).Optics.Surfaces![1].Index);
        Assert.Equal(1.5, catalog.Import(codeV, glass).Optics.Surfaces![1].ModelGlass!.Nd);
    }
}
