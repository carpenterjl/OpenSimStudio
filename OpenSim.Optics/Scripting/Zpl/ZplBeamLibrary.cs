using System.Numerics;
using System.Text.Json;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.ImageSimulation;
using OpenSim.Optics.PhysicalOptics;

namespace OpenSim.Optics.Scripting.Zpl;

// wire-13: the keywords of physical optics propagation and beam files (POP, ZBFREAD, ZBFWRITE,
// ZBFPROPERTIES, ZBFCLR, ZBFMULT, ZBFTILT, ZBFSUM; OP-49's engine and ZbfFile as they are) and of
// the geometric image analysis (IMA, IMASUM, IMAE; OP-42's analysis and BimFile). Each follows
// its OpticStudio 2025 R2 help page under plans/sources/optics/op-99-help/
// (docs/Optics-Conventions.md §56, "Beam and image files").

public static partial class ZplLibrary
{
    private static void RegisterBeamsAndImages()
    {
        RegisterPop();
        RegisterBeamFiles();
        RegisterImageFiles();
    }

    // ================================================================ Folders and saved settings

    /// <summary>A beam file's path: a name without a folder is in the beam (&lt;pop&gt;) folder,
    /// and the extension is made ZBF ("OpticStudio requires the filename extension to be ZBF, and
    /// will append or replace the extension as required", ZBFCLR).</summary>
    private static string BeamPath(ZplInterpreter m, ZplArgs a, int i, string what)
    {
        string path = ZplSession.Resolve(a.FileName(m, i, what), m.Session.PopFolder, "beam (pop) folder");
        return string.Equals(Path.GetExtension(path), ".zbf", StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, ".ZBF");
    }

    private static string ImagePath(ZplInterpreter m, string name) =>
        ZplSession.Resolve(name, m.Session.ImagesFolder, "images folder");

    /// <summary>The settings "previously saved for the current lens" (POP, IMA): the lens's
    /// last-used settings of the analysis, or its defaults.</summary>
    private static T SavedSettings<T>(ZplInterpreter m, IOpticsAnalysis analysis) where T : class
    {
        JsonElement? saved = m.Session.System.Analyses?.LastSettings is { } last && last.TryGetValue(analysis.Id, out var s) ? s : null;
        return (T)OpticsAnalysisSettingsCodec.Load(analysis, saved, out _);
    }

    // ================================================================ POP

    private static void RegisterPop()
    {
        Kw("POP", "Runs the Physical Optics Propagation analysis (OP-49) with the lens's saved settings (its last-used settings, or the defaults) and writes the end beam to the ZBF file, in the beam folder; lastsurface replaces the end surface. When the saved settings save the beam at all surfaces, those files (name_nnnn.ZBF) are written too. A settings file is not read (fails).", (m, a) =>
        {
            if (a.Count is < 1 or > 3) throw new ZplRuntimeException("POP takes outfilename [, lastsurface, settingsfilename].");
            if (a.Has(2)) throw new ZplRuntimeException("POP: settings files are not read in this version (OpticStudio's .CFG files are not this program's settings); the lens's saved Physical Optics Propagation settings are used.");
            string path = BeamPath(m, a, 0, "the output ZBF file");
            _ = m.Session.Surfaces;   // the lens has surfaces, or an error naming that
            var analysis = new PhysicalOpticsPropagationAnalysis();
            var settings = SavedSettings<PhysicalOpticsSettings>(m, analysis);
            if (a.Has(1)) settings = settings with { EndSurface = m.Session.SurfaceNumber(a.Num(m, 1, "lastsurface"), "POP") };
            settings = settings with { SaveOutputBeamTo = path };
            OpticsScripting.Analyze(m.Session.System, m.Session.Catalogs, PhysicalOpticsPropagationAnalysis.SavedId, settings);
            if (!File.Exists(path)) throw new ZplRuntimeException($"POP: the propagation wrote no beam file '{path}'.");
        });
    }

    // ================================================================ ZBF files

    /// <summary>The 15 beam values of ZBFPROPERTIES and ZBFREAD, positions 1 to 15: nx, ny, dx,
    /// dy, waist x, waist y, position x, position y, Rayleigh x, Rayleigh y, wavelength, total
    /// power, peak irradiance, is-polarized, media index. Lengths in the current lens units ("The
    /// length units in the ZBF files are converted automatically to the current lens units",
    /// ZBFSUM); power in watts ("Ex*Ex + Ey*Ey is in units of watts", Zemax Beam File (ZBF)
    /// Binary Format); peak irradiance in watts per lens unit².</summary>
    private static double[] BeamValues(ZplInterpreter m, ZbfBeam b)
    {
        double k = OpticsConventions.MetersPer(b.Unit) / OpticsConventions.MetersPer(m.Session.Unit);
        double total = 0, peak = 0;
        for (int r = 0; r < b.Ny; r++)
            for (int c = 0; c < b.Nx; c++)
            {
                double p = Power(b.Ex[r, c]) + (b.Ey is { } ey ? Power(ey[r, c]) : 0);
                total += p;
                peak = Math.Max(peak, p);
            }
        double dx = b.Dx * k, dy = b.Dy * k;
        return new[]
        {
            b.Nx, b.Ny, dx, dy, b.PilotX.Waist * k, b.PilotY.Waist * k, b.PilotX.Position * k, b.PilotY.Position * k,
            b.PilotX.Rayleigh * k, b.PilotY.Rayleigh * k, b.Wavelength * k, total, peak / (dx * dy), b.IsPolarized ? 1 : 0, b.Index,
        };
    }

    private static double Power(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;

    private const int BeamValueCount = 15;

    private static ZbfBeam ReadBeam(string path, string who)
    {
        if (!File.Exists(path)) throw new ZplRuntimeException($"{who}: the beam file '{path}' does not exist.");
        return ZbfFile.Read(path);
    }

    private static Complex[,] Map(Complex[,] a, Func<int, int, Complex, Complex> f)
    {
        var result = new Complex[a.GetLength(0), a.GetLength(1)];
        for (int r = 0; r < a.GetLength(0); r++)
            for (int c = 0; c < a.GetLength(1); c++) result[r, c] = f(r, c, a[r, c]);
        return result;
    }

    private static void RegisterBeamFiles()
    {
        Kw("ZBFPROPERTIES", "Puts a ZBF file's 15 beam values in VECn positions 1 to 15 (nx, ny, dx, dy, waists, positions and Rayleigh ranges x and y, wavelength, total power, peak irradiance, is-polarized, media index); lengths in the current lens units, power in watts, irradiance in watts per lens unit².", (m, a) =>
        {
            a.Exactly(2, "ZBFPROPERTIES");
            string path = BeamPath(m, a, 0, "the ZBF file");
            int n = a.Int(m, 1, "the vector");
            var vector = Vector(m, n, "ZBFPROPERTIES");
            if (m.Session.VectorSize < BeamValueCount) throw new ZplRuntimeException($"ZBFPROPERTIES: VEC{n} holds {m.Session.VectorSize} values; the beam needs {BeamValueCount} (SETVECSIZE).");
            var values = BeamValues(m, ReadBeam(path, "ZBFPROPERTIES"));
            for (int k = 0; k < BeamValueCount; k++) vector[k + 1] = values[k];
        });
        Kw("ZBFREAD", "Reads a ZBF file into two DECLAREd arrays: the field into beam(i, j, k) (i along x, j along y, k = 1, 2 Ex real and imaginary, 3, 4 Ey's when polarized), as the file stores it; the 15 beam values of ZBFPROPERTIES into property(1 to 15). The property array needs 15 elements (the help lists 15 values though it says 14).", (m, a) =>
        {
            a.Exactly(3, "ZBFREAD");
            string path = BeamPath(m, a, 0, "the ZBF file");
            var (beamArray, props) = BeamArrays(m, a, "ZBFREAD");
            var b = ReadBeam(path, "ZBFREAD");
            int depth = b.IsPolarized ? 4 : 2;
            if (beamArray.Sizes[0] < b.Nx || beamArray.Sizes[1] < b.Ny || beamArray.Sizes[2] < depth)
                throw new ZplRuntimeException($"ZBFREAD: the beam array is {string.Join(" x ", beamArray.Sizes)}; the {(b.IsPolarized ? "polarized" : "unpolarized")} beam needs at least {b.Nx} x {b.Ny} x {depth}.");
            for (int j = 0; j < b.Ny; j++)
                for (int i = 0; i < b.Nx; i++)
                {
                    beamArray.Set(new[] { i + 1, j + 1, 1 }, b.Ex[j, i].Real);
                    beamArray.Set(new[] { i + 1, j + 1, 2 }, b.Ex[j, i].Imaginary);
                    if (b.Ey is { } ey)
                    {
                        beamArray.Set(new[] { i + 1, j + 1, 3 }, ey[j, i].Real);
                        beamArray.Set(new[] { i + 1, j + 1, 4 }, ey[j, i].Imaginary);
                    }
                }
            var values = BeamValues(m, b);
            for (int k = 0; k < BeamValueCount; k++) props.Set(new[] { k + 1 }, values[k]);
        });
        Kw("ZBFWRITE", "Writes two DECLAREd arrays (laid out as ZBFREAD fills them) to a binary ZBF file in the current lens units. Total power and peak irradiance (positions 12, 13) are not read: they follow from the field. nx and ny must be powers of 2 from 32 to 16384.", (m, a) =>
        {
            a.Exactly(3, "ZBFWRITE");
            string path = BeamPath(m, a, 0, "the ZBF file");
            var (beamArray, props) = BeamArrays(m, a, "ZBFWRITE");
            double P(int k) => props.Get(new[] { k });
            int nx = Int(P(1), "ZBFWRITE: nx (property 1)"), ny = Int(P(2), "ZBFWRITE: ny (property 2)");
            if (!PopField.IsSampling(nx) || !PopField.IsSampling(ny))
                throw new ZplRuntimeException($"ZBFWRITE: nx x ny is {nx} x {ny}; each must be a power of 2 from {PopField.MinimumSampling} to {PopField.MaximumSampling}.");
            double pol = P(14);
            if (pol is not (0 or 1)) throw new ZplRuntimeException($"ZBFWRITE: the is-polarized flag (property 14) is {F(pol)}; it must be 0 or 1.");
            bool polarized = pol == 1;
            int depth = polarized ? 4 : 2;
            if (beamArray.Sizes[0] < nx || beamArray.Sizes[1] < ny || beamArray.Sizes[2] < depth)
                throw new ZplRuntimeException($"ZBFWRITE: the beam array is {string.Join(" x ", beamArray.Sizes)}; nx = {nx}, ny = {ny} {(polarized ? "polarized" : "unpolarized")} needs at least {nx} x {ny} x {depth}.");
            Complex[,] Component(int re) => Map(new Complex[ny, nx], (r, c, _) => new Complex(beamArray.Get(new[] { c + 1, r + 1, re }), beamArray.Get(new[] { c + 1, r + 1, re + 1 })));
            var beam = new ZbfBeam
            {
                Nx = nx, Ny = ny, IsPolarized = polarized, Unit = m.Session.Unit, Dx = P(3), Dy = P(4),
                PilotX = new ZbfPilot(P(7), P(9), P(5)), PilotY = new ZbfPilot(P(8), P(10), P(6)),
                Wavelength = P(11), Index = P(15), Ex = Component(1), Ey = polarized ? Component(3) : null,
            };
            if (!(beam.Dx > 0) || !(beam.Dy > 0)) throw new ZplRuntimeException($"ZBFWRITE: dx and dy (properties 3, 4) are {F(beam.Dx)} and {F(beam.Dy)}; they must be positive.");
            ZbfFile.Write(path, beam);
        });
        Kw("ZBFCLR", "Sets every sample of a ZBF file to zero and writes it back (binary, the header unchanged).", (m, a) =>
        {
            a.Exactly(1, "ZBFCLR");
            string path = BeamPath(m, a, 0, "the ZBF file");
            var b = ReadBeam(path, "ZBFCLR");
            ZbfFile.Write(path, b with { Ex = new Complex[b.Ny, b.Nx], Ey = b.IsPolarized ? new Complex[b.Ny, b.Nx] : null });
        });
        Kw("ZBFMULT", "Multiplies a ZBF file's samples, Ex by (Ax + i·Bx) and Ey by (Ay + i·By), and writes it back (binary, the header unchanged); Ay and By have no effect on an unpolarized file.", (m, a) =>
        {
            a.Exactly(5, "ZBFMULT");
            string path = BeamPath(m, a, 0, "the ZBF file");
            var fx = new Complex(a.Num(m, 1, "Ax"), a.Num(m, 2, "Bx"));
            var fy = new Complex(a.Num(m, 3, "Ay"), a.Num(m, 4, "By"));
            var b = ReadBeam(path, "ZBFMULT");
            ZbfFile.Write(path, b with { Ex = Map(b.Ex, (_, _, z) => z * fx), Ey = b.Ey is { } ey ? Map(ey, (_, _, z) => z * fy) : null });
        });
        Kw("ZBFTILT", "Adds the phase θ = (x − cx)·tx + (y − cy)·ty (radians; x, y in lens units from the sample (nx/2 + 1, ny/2 + 1)) to a ZBF file's stored phase, Ex and Ey, and writes it back (binary, the header unchanged).", (m, a) =>
        {
            a.Exactly(5, "ZBFTILT");
            string path = BeamPath(m, a, 0, "the ZBF file");
            double cx = a.Num(m, 1, "cx"), cy = a.Num(m, 2, "cy"), tx = a.Num(m, 3, "tx"), ty = a.Num(m, 4, "ty");
            var b = ReadBeam(path, "ZBFTILT");
            double k = OpticsConventions.MetersPer(b.Unit) / OpticsConventions.MetersPer(m.Session.Unit);
            double dx = b.Dx * k, dy = b.Dy * k;
            Complex Tilt(int r, int c, Complex z) =>
                z * Complex.FromPolarCoordinates(1, ((c - b.Nx / 2) * dx - cx) * tx + ((r - b.Ny / 2) * dy - cy) * ty);
            ZbfFile.Write(path, b with { Ex = Map(b.Ex, Tilt), Ey = b.Ey is { } ey ? Map(ey, Tilt) : null });
        });
        Kw("ZBFSUM", "Coherent sum (coherent ≠ 0) of two ZBF files with the same sampling, point spacing, polarization and pilot beam (so the same reference radii), written to the third file with the first file's header. Refused: the incoherent sum (the help does not say how its real-valued result is stored) and files that differ (the help's scaling and interpolation is not built).", (m, a) =>
        {
            a.Exactly(4, "ZBFSUM");
            bool coherent = a.Num(m, 0, "coherent") != 0;
            string p1 = BeamPath(m, a, 1, "the first ZBF file"), p2 = BeamPath(m, a, 2, "the second ZBF file"), output = BeamPath(m, a, 3, "the output ZBF file");
            if (!coherent)
                throw new ZplRuntimeException("ZBFSUM: the incoherent sum (coherent = 0) is not bound: the help says only that its result is \"real valued only\", not whether a sample holds the summed power or its square root.");
            var b1 = ReadBeam(p1, "ZBFSUM");
            var b2 = ReadBeam(p2, "ZBFSUM");
            double k = OpticsConventions.MetersPer(b2.Unit) / OpticsConventions.MetersPer(b1.Unit);
            static bool Same(double x, double y) => x == y || Math.Abs(x - y) <= 1e-9 * Math.Max(Math.Abs(x), Math.Abs(y));
            bool match = b1.Nx == b2.Nx && b1.Ny == b2.Ny && b1.IsPolarized == b2.IsPolarized
                && Same(b1.Dx, b2.Dx * k) && Same(b1.Dy, b2.Dy * k)
                && Same(b1.PilotX.Position, b2.PilotX.Position * k) && Same(b1.PilotX.Rayleigh, b2.PilotX.Rayleigh * k)
                && Same(b1.PilotY.Position, b2.PilotY.Position * k) && Same(b1.PilotY.Rayleigh, b2.PilotY.Rayleigh * k);
            if (!match)
                throw new ZplRuntimeException($"ZBFSUM: '{Path.GetFileName(p1)}' and '{Path.GetFileName(p2)}' differ in sampling, point spacing, polarization or pilot beam; scaling and interpolating the second file to the first is not built in this version.");
            ZbfFile.Write(output, b1 with
            {
                Ex = Map(b1.Ex, (r, c, z) => z + b2.Ex[r, c]),
                Ey = b1.Ey is { } e1 ? Map(e1, (r, c, z) => z + b2.Ey![r, c]) : null,
            });
        });
        NotBound("ZBFRESAMPLE", "Not bound: the help does not say how the beam is \"resampled and interpolated\", and physical optics propagation (OP-49) has no resampler.");
        NotBound("ZBFSHOW", "Not bound: it opens a viewer window, and macros here open no windows (the Beam File Viewer analysis shows a ZBF file).");
        NotBound("XDIFFIA", "Not bound: the Extended Diffraction Image Analysis's ZBF output is not built (OP-42).");
    }

    private static (ZplArray Beam, ZplArray Properties) BeamArrays(ZplInterpreter m, ZplArgs a, string who)
    {
        string beamName = a.Word(1) ?? throw new ZplRuntimeException($"{who}: the beam array must be the name of a DECLAREd array, not '{a.Text(1)}'.");
        string propName = a.Word(2) ?? throw new ZplRuntimeException($"{who}: the property array must be the name of a DECLAREd array, not '{a.Text(2)}'.");
        var beam = m.ArrayNamed(beamName, who);
        var props = m.ArrayNamed(propName, who);
        if (beam.Sizes.Length != 3) throw new ZplRuntimeException($"{who}: the beam array {beamName} must have 3 dimensions (nx, ny, 2 or 4); it has {beam.Sizes.Length}.");
        if (props.Sizes.Length != 1 || props.Sizes[0] < BeamValueCount)
            throw new ZplRuntimeException($"{who}: the property array {propName} must have 1 dimension of at least {BeamValueCount} elements (the help lists 15 values, positions 1 to 15).");
        return (beam, props);
    }

    // ================================================================ IMA and BIM files

    private static void RegisterImageFiles()
    {
        Kw("IMA", "Runs the Geometric Image Analysis (OP-42) with the lens's saved settings (its last-used settings, or the defaults) and writes its detector image to the BIM file (BIM appended when no extension is given), in the images folder; infilename (with its extension, in the images folder) replaces the source file. Show must not be the spot diagram.", (m, a) =>
        {
            if (a.Count is < 1 or > 2) throw new ZplRuntimeException("IMA takes outfilename [, infilename].");
            string name = a.FileName(m, 0, "the output BIM file").Trim();
            if (Path.GetExtension(name).Length == 0) name += ".BIM";
            string output = ImagePath(m, name);
            _ = m.Session.Surfaces;
            var analysis = new GeometricImageAnalysis();
            var settings = SavedSettings<GeometricImageSettings>(m, analysis);
            if (a.Has(1))
            {
                string input = a.FileName(m, 1, "the input IMA or BIM file").Trim();
                if (Path.GetExtension(input).Length == 0) throw new ZplRuntimeException($"IMA: the input file '{input}' needs its extension (help: \"The extension must be provided on the infilename\").");
                settings = settings with { File = ImagePath(m, input) };
            }
            settings = settings with { SaveAsBimFile = output };
            OpticsScripting.Analyze(m.Session.System, m.Session.Catalogs, GeometricImageAnalysis.SavedId, settings);
            if (!File.Exists(output)) throw new ZplRuntimeException($"IMA: the analysis wrote no BIM file '{output}'.");
        });
        Kw("IMASUM", "Adds two BIM files pixel by pixel into a third (the names may repeat), all in the images folder with the BIM extension; the two must have the same pixels.", (m, a) =>
        {
            a.Exactly(3, "IMASUM");
            string P(int i, string what)
            {
                string n = a.FileName(m, i, what).Trim();
                if (!string.Equals(Path.GetExtension(n), ".bim", StringComparison.OrdinalIgnoreCase))
                    throw new ZplRuntimeException($"IMASUM: '{n}' must end in .BIM (help: \"The BIM extension must be provided on all three file names\").");
                return ImagePath(m, n);
            }
            string p1 = P(0, "the first BIM file"), p2 = P(1, "the second BIM file"), output = P(2, "the output BIM file");
            foreach (var p in new[] { p1, p2 })
                if (!File.Exists(p)) throw new ZplRuntimeException($"IMASUM: the BIM file '{p}' does not exist.");
            var i1 = BimFile.Read(p1).Channels[0];
            var i2 = BimFile.Read(p2).Channels[0];
            if (i1.GetLength(0) != i2.GetLength(0) || i1.GetLength(1) != i2.GetLength(1))
                throw new ZplRuntimeException($"IMASUM: '{Path.GetFileName(p1)}' has {i1.GetLength(1)} x {i1.GetLength(0)} pixels and '{Path.GetFileName(p2)}' {i2.GetLength(1)} x {i2.GetLength(0)}; they must be the same.");
            var sum = new double[i1.GetLength(0), i1.GetLength(1)];
            for (int r = 0; r < sum.GetLength(0); r++)
                for (int c = 0; c < sum.GetLength(1); c++) sum[r, c] = i1[r, c] + i2[r, c];
            BimFile.Write(output, sum);
        });
        NotBound("IMASHOW", "Not bound: it opens a viewer window, and macros here open no windows (the IMA and BIM File Viewer analysis shows the file).");
        Fn("IMAE", 1, 1, "The Geometric Image Analysis's efficiency (a fraction) with the lens's saved settings. The seed is read and changes nothing: the rays come from a Sobol sequence (Conventions §67), so every call gives the same value.", (m, v) =>
        {
            Num(v, 0, "the seed");
            var lens = m.Session.Lens;
            var notes = new List<string>();
            var system = SystemDataSettings.FromSettings(m.Session.Current.SystemData, notes);
            var analysis = new GeometricImageAnalysis();
            var settings = SavedSettings<GeometricImageSettings>(m, analysis);
            return N(analysis.Evaluate(lens, system, notes, settings, m.Session.CancellationToken).Efficiency);
        });
    }
}
