using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.ImageSimulation;
using OpenSim.Optics.PhysicalOptics;
using OpenSim.Optics.Scripting;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.Tests.Optics;

// wire-13: the conformance macros of POP, the ZBF keywords, IMA, IMASUM and IMAE, and
// GETTEXTFILE's codes for the Universal Plot (OP-45), physical optics (OP-49) and image simulation
// (OP-42) analyses. Each follows its help page under plans/sources/optics/op-99-help/; the expected
// values are derived in the comments or are this program's own engines run directly.
public sealed partial class ZplConformanceTests
{
    /// <summary>A 32 × 32 beam file in centimeters (so the keywords' conversion to the singlet's
    /// millimeters shows), dx = dy = 0.01 cm, with arbitrary samples (not physical: chosen to make
    /// every sample and both parts distinct).</summary>
    private static ZbfBeam TestBeam(bool polarized = false, double dx = 0.01)
    {
        const int n = 32;
        var ex = new Complex[n, n];
        var ey = new Complex[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                ex[r, c] = new Complex(Math.Sin(r + 0.3 * c), Math.Cos(0.7 * r - c)) * 1e-2;
                ey[r, c] = new Complex(Math.Cos(0.2 * r + c), Math.Sin(r - 0.4 * c)) * 1e-3;
            }
        return new ZbfBeam
        {
            Nx = n, Ny = n, IsPolarized = polarized, Unit = LensUnit.Centimeter, Dx = dx, Dy = dx,
            PilotX = new ZbfPilot(0.5, 0.25, 0.02), PilotY = new ZbfPilot(0.5, 0.25, 0.02),
            Wavelength = 0.000055, Index = 1, Ex = ex, Ey = polarized ? ey : null,
        };
    }

    private void WriteBeam(string name, ZbfBeam beam) => ZbfFile.Write(Path.Combine(_dir, name), beam);

    private ZbfBeam ReadBeam(string name) => ZbfFile.Read(Path.Combine(_dir, name));

    // ================================================================ ZBFPROPERTIES, ZBFREAD, ZBFWRITE

    private const string BeamPropertiesMacro =
        "ZBFPROPERTIES \"a.zbf\", 1\nFOR k, 1, 15, 1\nOPTRETURN k - 1, VEC1(k)\nNEXT\n"
        + "DECLARE B, DOUBLE, 3, 32, 32, 2\nDECLARE P, DOUBLE, 1, 15\nZBFREAD \"a.ZBF\", B, P\n"
        + "OPTRETURN 15, B(3, 5, 1)\nOPTRETURN 16, B(3, 5, 2)\nOPTRETURN 17, P(12)\nOPTRETURN 18, P(3)\n"
        + "ZBFWRITE \"b\", B, P";

    [Fact]
    public void Zbfproperties_Zbfread_AndZbfwrite_FollowTheHelpsLayout()
    {
        var beam = TestBeam();
        WriteBeam("a.ZBF", beam);
        var r = Run(BeamPropertiesMacro).OptReturn;
        // Derived: the file is in cm, the singlet in mm, so every length is ×10 (help, ZBFSUM: "The
        // length units in the ZBF files are converted automatically to the current lens units").
        // The power is Σ(Ex·Ex*) ("Ex*Ex + Ey*Ey is in units of watts", ZBF Binary Format) and the
        // peak irradiance the largest sample's power over dx·dy in mm².
        double total = 0, peak = 0;
        foreach (var z in beam.Ex) { double p = z.Real * z.Real + z.Imaginary * z.Imaginary; total += p; peak = Math.Max(peak, p); }
        var expected = new double[] { 32, 32, 0.1, 0.1, 0.2, 0.2, 5, 5, 2.5, 2.5, 0.00055, total, peak / (0.1 * 0.1), 0, 1 };
        for (int k = 0; k < 15; k++) Near(expected[k], r[k], 1e-14);
        // beam(i, j, k): i along x (column i − 1), j along y (row j − 1), k = 1 real, 2 imaginary.
        Assert.Equal(beam.Ex[4, 2].Real, r[15]);
        Assert.Equal(beam.Ex[4, 2].Imaginary, r[16]);
        Near(total, r[17], 1e-14);
        Near(0.1, r[18], 1e-14);
        // ZBFWRITE "b" appends the extension and writes the current lens units: the same samples,
        // the lengths in mm.
        var b = ReadBeam("b.ZBF");
        Assert.Equal(LensUnit.Millimeter, b.Unit);
        Assert.False(b.IsPolarized);
        Near(0.1, b.Dx, 1e-14);
        Near(2.5, b.PilotX.Rayleigh, 1e-14);
        Near(5, b.PilotY.Position, 1e-14);
        Near(0.2, b.PilotY.Waist, 1e-14);
        Near(0.00055, b.Wavelength, 1e-14);
        for (int row = 0; row < 32; row++)
            for (int c = 0; c < 32; c++) Assert.Equal(beam.Ex[row, c], b.Ex[row, c]);
    }

    [Fact]
    public void Zbfread_OfAPolarizedFile_FillsEy_AndRefusesSmallArrays()
    {
        var beam = TestBeam(polarized: true);
        WriteBeam("p.ZBF", beam);
        var r = Run("DECLARE B, DOUBLE, 3, 32, 32, 4\nDECLARE P, DOUBLE, 1, 20\nZBFREAD \"p.ZBF\", B, P\n"
            + "OPTRETURN 0, B(7, 2, 3)\nOPTRETURN 1, B(7, 2, 4)\nOPTRETURN 2, P(14)").OptReturn;
        Assert.Equal(beam.Ey![1, 6].Real, r[0]);
        Assert.Equal(beam.Ey![1, 6].Imaginary, r[1]);
        Assert.Equal(1, r[2]);
        Assert.Contains("32 x 32 x 4", Assert.Throws<ZplException>(() => Run("DECLARE B, DOUBLE, 3, 32, 32, 2\nDECLARE P, DOUBLE, 1, 15\nZBFREAD \"p.ZBF\", B, P")).Message);
        Assert.Contains("15", Assert.Throws<ZplException>(() => Run("DECLARE B, DOUBLE, 3, 32, 32, 4\nDECLARE P, DOUBLE, 1, 14\nZBFREAD \"p.ZBF\", B, P")).Message);
        Assert.Contains("no array", Assert.Throws<ZplException>(() => Run("DECLARE P, DOUBLE, 1, 15\nZBFREAD \"p.ZBF\", Q, P")).Message);
    }

    // ================================================================ ZBFCLR, ZBFMULT, ZBFTILT, ZBFSUM

    private const string BeamEditMacro =
        "ZBFSUM 1, \"a.ZBF\", \"a.ZBF\", \"sum.ZBF\"\nZBFMULT \"m.ZBF\", 0, 1, 0, -1\nZBFMULT \"p.ZBF\", 2, 0, 0, -1\n"
        + "ZBFTILT \"t.ZBF\", 0.2, -0.1, 0.5, 0.25\nZBFCLR \"c.ZBF\"";

    [Fact]
    public void Zbfclr_Zbfmult_Zbftilt_AndTheCoherentZbfsum_ChangeTheSamplesAsTheHelpSays()
    {
        var beam = TestBeam();
        foreach (var name in new[] { "a.ZBF", "m.ZBF", "t.ZBF", "c.ZBF" }) WriteBeam(name, beam);
        var pol = TestBeam(polarized: true);
        WriteBeam("p.ZBF", pol);
        Run(BeamEditMacro);
        var sum = ReadBeam("sum.ZBF");
        var m = ReadBeam("m.ZBF");
        var p = ReadBeam("p.ZBF");
        var t = ReadBeam("t.ZBF");
        var c = ReadBeam("c.ZBF");
        Assert.Equal(LensUnit.Centimeter, t.Unit);   // the header is kept
        for (int row = 0; row < 32; row++)
            for (int col = 0; col < 32; col++)
            {
                var z = beam.Ex[row, col];
                Assert.Equal(z + z, sum.Ex[row, col]);
                // ZBFMULT: Ex = Ex0(Ax + Bx·i), Ey = Ey0(Ay + By·i).
                Assert.Equal(z * new Complex(0, 1), m.Ex[row, col]);
                Assert.Equal(pol.Ex[row, col] * new Complex(2, 0), p.Ex[row, col]);
                Assert.Equal(pol.Ey![row, col] * new Complex(0, -1), p.Ey![row, col]);
                // ZBFTILT: θ = (x − cx)tx + (y − cy)ty, x and y in mm from the sample (nx/2 + 1,
                // ny/2 + 1) counted from 1, i.e. column 16 and row 16 counted from 0; dx = 0.1 mm.
                double x = (col - 16) * 0.1, y = (row - 16) * 0.1;
                var expected = z * Complex.FromPolarCoordinates(1, (x - 0.2) * 0.5 + (y + 0.1) * 0.25);
                Assert.True(Complex.Abs(expected - t.Ex[row, col]) <= 1e-15, $"ZBFTILT at row {row}, column {col}");
                Assert.Equal(Complex.Zero, c.Ex[row, col]);
            }
    }

    [Fact]
    public void Zbfsum_RefusesTheIncoherentSum_AndFilesThatDiffer_AndZbfresampleIsNotBound()
    {
        WriteBeam("a.ZBF", TestBeam());
        WriteBeam("d.ZBF", TestBeam(dx: 0.02));
        // The same beam in mm (dx 0.1 mm = 0.01 cm, the pilot ×10) sums with the cm file.
        var mm = TestBeam() with { Unit = LensUnit.Millimeter, Dx = 0.1, Dy = 0.1, PilotX = new ZbfPilot(5, 2.5, 0.2), PilotY = new ZbfPilot(5, 2.5, 0.2) };
        WriteBeam("mm.ZBF", mm);
        Run("ZBFSUM 1, \"a.ZBF\", \"mm.ZBF\", \"s.ZBF\"");
        Assert.Equal(TestBeam().Ex[3, 9] * 2, ReadBeam("s.ZBF").Ex[3, 9]);
        Assert.Contains("incoherent", Assert.Throws<ZplException>(() => Run("ZBFSUM 0, \"a.ZBF\", \"a.ZBF\", \"s.ZBF\"")).Message);
        Assert.Contains("interpolating", Assert.Throws<ZplException>(() => Run("ZBFSUM 1, \"a.ZBF\", \"d.ZBF\", \"s.ZBF\"")).Message);
        Assert.Throws<ZplNotBoundException>(() => Run("ZBFRESAMPLE \"a.ZBF\", 64, 64, 0, 0, 0, 0"));
        Assert.Throws<ZplNotBoundException>(() => Run("ZBFSHOW \"a.ZBF\""));
        Assert.Contains("does not exist", Assert.Throws<ZplException>(() => Run("ZBFCLR \"none.ZBF\"")).Message);
    }

    // ================================================================ POP

    private const string PopMacro = "POP \"beam.zbf\", 3";

    /// <summary>The singlet with saved Physical Optics Propagation settings: 32 × 32 samples
    /// (small, so the run is quick), the rest the analysis's defaults.</summary>
    private static OpticsSettings SingletWithPopSettings() => Singlet() with
    {
        Analyses = new OpticsAnalysesSettings
        {
            LastSettings = new()
            {
                [PhysicalOpticsPropagationAnalysis.SavedId] = OpticsAnalysisSettingsCodec.Save(new PhysicalOpticsSettings { SamplingX = 32, SamplingY = 32 }),
            },
        },
    };

    [Fact]
    public void Pop_WritesTheEndBeamOfTheAnalysis_WithTheSavedSettings()
    {
        var system = SingletWithPopSettings();
        Run(PopMacro, system);
        // The analysis itself with the same saved settings, end surface 3, writing another file.
        string reference = Path.Combine(_dir, "reference.ZBF");
        OpticsScripting.Analyze(system, NoCatalogs, PhysicalOpticsPropagationAnalysis.SavedId,
            new PhysicalOpticsSettings { SamplingX = 32, SamplingY = 32, EndSurface = 3, SaveOutputBeamTo = reference });
        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(Path.Combine(_dir, "beam.ZBF")));
        Assert.Equal(32, ReadBeam("beam.ZBF").Nx);
        Assert.Contains("settings files", Assert.Throws<ZplException>(() => Run("POP \"x.zbf\", 3, \"s.cfg\"", system)).Message);
        Assert.Contains("does not exist", Assert.Throws<ZplException>(() => Run("POP \"x.zbf\", 9", system)).Message);
    }

    // ================================================================ IMA, IMAE, IMASUM

    private const string ImaMacro = "IMA \"out\", \"src.IMA\"\nOPTRETURN 0, IMAE(0)\nOPTRETURN 1, IMAE(7)\nIMASUM \"out.BIM\", \"out.BIM\", \"twice.BIM\"";

    /// <summary>The singlet with saved Geometric Image Analysis settings: a 3 × 3 cross of the
    /// macro folder, 0.1° wide, on a 10-pixel detector 0.5 mm wide, 1000 rays (arbitrary inputs).</summary>
    private OpticsSettings SingletWithImageSettings() => Singlet() with
    {
        Analyses = new OpticsAnalysesSettings
        {
            LastSettings = new()
            {
                [GeometricImageAnalysis.SavedId] = OpticsAnalysisSettingsCodec.Save(new GeometricImageSettings
                {
                    File = Path.Combine(_dir, "src.IMA"), FieldSize = 0.1, ImageSize = 0.5, Pixels = 10, RaysThousands = 1,
                }),
            },
        },
    };

    [Fact]
    public void Ima_Imae_AndImasum_RunTheGeometricImageAnalysis()
    {
        // A text IMA file: the size, then a row of digits per pixel row (help, "Geometric Image Analysis").
        File.WriteAllText(Path.Combine(_dir, "src.IMA"), "3\n010\n111\n010\n");
        var system = SingletWithImageSettings();
        var r = Run(ImaMacro, system).OptReturn;
        var settings = new GeometricImageSettings { File = Path.Combine(_dir, "src.IMA"), FieldSize = 0.1, ImageSize = 0.5, Pixels = 10, RaysThousands = 1 };
        string reference = Path.Combine(_dir, "reference.BIM");
        OpticsScripting.Analyze(system, NoCatalogs, GeometricImageAnalysis.SavedId, settings with { SaveAsBimFile = reference });
        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(Path.Combine(_dir, "out.BIM")));
        // IMAE is the analysis's efficiency, the same for any seed (Sobol rays).
        var lens = ProjectLens.From(system, NoCatalogs)!;
        var data = SystemDataSettings.FromSettings(system.SystemData, new List<string>());
        double efficiency = new GeometricImageAnalysis().Evaluate(lens, data, new List<string>(), settings, default).Efficiency;
        Assert.Equal(efficiency, r[0]);
        Assert.Equal(efficiency, r[1]);
        Assert.InRange(efficiency, 0, 1);
        // IMASUM of a file with itself doubles every pixel exactly.
        var once = BimFile.Read(Path.Combine(_dir, "out.BIM")).Channels[0];
        var twice = BimFile.Read(Path.Combine(_dir, "twice.BIM")).Channels[0];
        for (int row = 0; row < once.GetLength(0); row++)
            for (int c = 0; c < once.GetLength(1); c++) Assert.Equal(2 * once[row, c], twice[row, c]);
        Assert.Contains(".BIM", Assert.Throws<ZplException>(() => Run("IMASUM \"out.BIM\", \"out\", \"x.BIM\"", system)).Message);
        Assert.Contains("extension", Assert.Throws<ZplException>(() => Run("IMA \"o\", \"src\"", system)).Message);
        Assert.Throws<ZplNotBoundException>(() => Run("IMASHOW \"out.BIM\"", system));
        Assert.Throws<ZplNotBoundException>(() => Run("XDIFFIA \"o.ZBF\", \"src.IMA\"", system));
    }

    // ================================================================ GETTEXTFILE codes (wave 13)

    /// <summary>The codes of the String Codes page for the wave-13 analyses: UNIVERSAL_PLOT_1D,
    /// UNIVERSAL_PLOT_2D, PHYSICAL_OPTICS_PROPAGATION, BEAM_FILE_VIEWER, IMAGE_SIMULATION,
    /// GEOMETRIC_IMAGE_ANALYSIS, GEOMETRIC_BITMAP_IMAGE_ANALYSIS, PARTIALLY_COHERENT_IMAGE_ANALYSIS
    /// (twice: Dim and Pci), EXTENDED_DIFFRACTION_IMAGE_ANALYSIS, IMA/BIM_FILE_VIEWER and
    /// JPG/BMP_FILE_VIEWER.</summary>
    private static readonly (string Code, string Id)[] Wave13Codes =
    {
        ("Uni", "UniversalPlot1D"), ("Un2", "UniversalPlot2D"), ("Pop", "PhysicalOpticsPropagation"), ("Bfv", "BeamFileViewer"),
        ("Sim", "ImageSimulation"), ("Ima", "GeometricImageAnalysis"), ("Ibm", "GeometricBitmapImageAnalysis"),
        ("Dim", "PartiallyCoherentImageAnalysis"), ("Pci", "PartiallyCoherentImageAnalysis"), ("Xdi", "ExtendedDiffractionImageAnalysis"),
        ("Imv", "ImaBimFileViewer"), ("Jbv", "BitmapFileViewer"),
    };

    private const string TextFileWave13Macro = "GETTEXTFILE \"bfv.txt\", Bfv\nGETTEXTFILE \"imv.txt\", Imv\nGETTEXTFILE \"uni.txt\", Uni";

    [Fact]
    public void Gettextfile_MapsTheWave13Codes()
    {
        foreach (var (code, id) in Wave13Codes) Assert.Equal(id, ZplLibrary.TextFileCodes[code]);
        // The viewers' default settings name no file, so GETTEXTFILE fails with the analysis's own
        // message: that shows which analysis the code ran.
        foreach (var (code, id) in new[] { ("Bfv", "BeamFileViewer"), ("Imv", "ImaBimFileViewer") })
        {
            var direct = Assert.ThrowsAny<Exception>(() => OpticsScripting.Analyze(Singlet(), NoCatalogs, id));
            Assert.Contains(direct.Message, Assert.Throws<ZplException>(() => Run($"GETTEXTFILE \"x.txt\", {code}")).Message);
        }
        Assert.Throws<ZplException>(() => Run(TextFileWave13Macro));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Gettextfile_EveryWave13Code_WritesItsAnalysisText_OrFailsWithItsMessage()
    {
        foreach (var (code, id) in Wave13Codes)
        {
            string? text = null, error = null;
            try { text = OpticsScripting.Analyze(Singlet(), NoCatalogs, id).Text; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException or FormatException) { error = e.Message; }
            string file = Path.Combine(_dir, code + ".txt");
            if (text is not null)
            {
                Run($"GETTEXTFILE \"{code}.txt\", {code}");
                Assert.Equal(text, File.ReadAllText(file));
            }
            else Assert.Contains(error!, Assert.Throws<ZplException>(() => Run($"GETTEXTFILE \"{code}.txt\", {code}")).Message);
        }
    }
}
