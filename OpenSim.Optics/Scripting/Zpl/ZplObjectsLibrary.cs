using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.CadExport;
using OpenSim.Optics.FiberCoupling;
using OpenSim.Optics.NonSequential.Editing;

namespace OpenSim.Optics.Scripting.Zpl;

// wire-14: FICL (OP-50's single-mode engine), EXPORTCAD (OP-96's CadExporter) and the
// non-sequential editor data (OP-70's saved objects: NOBJ, NPAR, NPOS, NPRO, OBJC,
// SETNSCPARAMETER, SETNSCPOSITION, SETNSCPROPERTY), each as it is and following its OpticStudio
// 2025 R2 help page under plans/sources/optics/op-99-help/ (docs/Optics-Conventions.md §56,
// "Fiber coupling, CAD export and non-sequential objects").

public static partial class ZplLibrary
{
    private static void RegisterFiberCadAndObjects()
    {
        RegisterFicl();
        RegisterExportCad();
        RegisterNonSequentialObjects();
    }

    /// <summary>A whole-number flag of a VEC array that must be 0 or 1.</summary>
    private static bool Flag(double v, string what)
    {
        int i = Int(v, what);
        if (i is not (0 or 1)) throw new ZplRuntimeException($"{what} is {i}; it must be 0 or 1.");
        return i == 1;
    }

    // ================================================================ FICL

    private static void RegisterFicl()
    {
        Fn("FICL", 1, 1, "The single-mode fiber coupling efficiency S·T (OP-50's Single Mode Fiber Coupling engine) with the 19 settings the help (Using the FICL() Function) puts in VECn positions 0 to 18: sampling (1 → 32 × 32, 2 → 64 × 64, …), wavelength and field numbers, ignore source, source and receiver NA x, source angles, receiver tilts and decenters (lens units), the two align-to-chief-ray flags, polarization (1 is refused: not built), source and receiver NA y (0 → NA x), Huygens. Flags must be 0 or 1.", (m, v) =>
        {
            int n = Int(v, 0, "the vector number");
            var vec = Vector(m, n, "FICL");
            if (m.Session.VectorSize < 18) throw new ZplRuntimeException($"VEC{n} holds positions 0 to {m.Session.VectorSize}; FICL reads 0 to 18 (SETVECSIZE).");
            string P(int k, string name) => $"VEC{n}({k}), {name},";
            int samp = Int(vec[0], P(0, "the sampling"));
            if (samp is < 1 or > 7) throw new ZplRuntimeException($"{P(0, "the sampling")} is {samp}; it must be 1 (32 × 32) to 7 (2048 × 2048).");
            int waves = m.Session.Current.SystemData?.Wavelengths?.Count ?? 0;
            int fields = m.Session.Current.SystemData?.Fields?.Count ?? 0;
            int wave = Int(vec[1], P(1, "the wavelength number"));
            if (wave < 1 || wave > waves) throw new ZplRuntimeException($"{P(1, "the wavelength number")} is {wave}; it must be 1 to {waves}.");
            int field = Int(vec[2], P(2, "the field number"));
            if (field < 1 || field > fields) throw new ZplRuntimeException($"{P(2, "the field number")} is {field}; it must be 1 to {fields}.");
            double snaX = vec[4], rnaX = vec[5];
            double snaY = vec[16] == 0 ? snaX : vec[16];   // help: "if zero then NAy = NAx"
            double rnaY = vec[17] == 0 ? rnaX : vec[17];
            var settings = new SingleModeFiberCouplingSettings
            {
                Sampling = 32 << (samp - 1),
                Wavelength = wave,
                Field = field,
                IgnoreSourceFiber = Flag(vec[3], P(3, "ignore source")),
                SourceNaX = snaX, SourceNaY = snaY,
                ReceiverNaX = rnaX, ReceiverNaY = rnaY,
                SourceAngleX = vec[6], SourceAngleY = vec[7],
                TiltX = vec[8], TiltY = vec[9],
                DecenterX = vec[10], DecenterY = vec[11], DecenterZ = vec[12],
                AlignSourceToChiefRay = Flag(vec[13], P(13, "align source to chief ray")),
                AlignReceiverToChiefRay = Flag(vec[14], P(14, "align receiver to chief ray")),
                UsePolarization = Flag(vec[15], P(15, "use polarization")),
                UseHuygens = Flag(vec[18], P(18, "use Huygens integral")),
            };
            var lens = m.Session.Lens;
            var notes = new List<string>();
            var system = SystemDataSettings.FromSettings(m.Session.Current.SystemData, notes);
            try
            {
                return N(SingleModeFiberCoupling.Compute(lens, system, settings.ToRequest(system, "FICL"), m.Session.CancellationToken).Coupling);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                throw new ZplRuntimeException(e.Message);
            }
        });
    }

    // ================================================================ EXPORTCAD

    /// <summary>The highest VEC1 position EXPORTCAD reads (help, EXPORTCAD: VEC1(1) to VEC1(24)).</summary>
    private const int ExportCadPositions = 24;

    private static void RegisterExportCad()
    {
        Kw("EXPORTCAD", "Writes the sequential lens as IGES (VEC1(1) = 0), STEP (1) or STL (3) through OP-96's CAD export, with the help's VEC1(1) to VEC1(24); a name without a folder is in the macro folder. Read: the file type, the spline points (16 to 512; checked, not used: the writers fit to the tolerance), first and last surface, surfaces as solids (1 for STEP, 0 for IGES), the ray pattern (XY, X, Y, ring, none, grid), number of rays, wavelength and field (0 all), delete vignetted, the tolerance code. Refused by name: SAT (OP-76); layers, dummy surfaces and their thickness, the list and solid-beam patterns, the NSC trace flags, chosen or all configurations, mixed-mode object ranges, footprints and the chief ray when not 0; a filter string; a system in non-sequential mode.", (m, a) =>
        {
            if (a.Count is < 1 or > 2) throw new ZplRuntimeException("EXPORTCAD takes filename [, filter string].");
            if (a.Has(1)) throw new ZplRuntimeException("EXPORTCAD: the filter string (non-sequential rays) is not bound: non-sequential export is not built (filter strings themselves are, OP-83).");
            if (OpticsModes.TryParseSavedName(m.Session.System.Mode, out var mode) && mode == OpticsMode.NonSequential)
                throw new ZplRuntimeException("EXPORTCAD: the system is in non-sequential mode; exporting non-sequential objects is not built (OP-96 exports the sequential lens).");
            string path = ZplSession.Resolve(a.FileName(m, 0, "the CAD file name"), m.Session.MacroFolder, "macro folder");
            if (m.Session.VectorSize < ExportCadPositions) throw new ZplRuntimeException($"EXPORTCAD reads VEC1(1) to VEC1({ExportCadPositions}); VEC1 holds {m.Session.VectorSize} (SETVECSIZE).");
            var v = m.Session.Vectors[0];
            int I(int k, string name) => Int(v[k], $"VEC1({k}), {name},");
            void Zero(int k, string name, string why)
            {
                if (v[k] != 0) throw new ZplRuntimeException($"EXPORTCAD: VEC1({k}), {name}, is {ZplInterpreter.Num(v[k])}; only 0 is bound ({why}).");
            }

            int type = I(1, "the file type");
            var format = type switch
            {
                0 => CadExportFormat.Iges,
                1 => CadExportFormat.Step,
                2 => throw new ZplRuntimeException("EXPORTCAD: VEC1(1) = 2 asks for SAT; SAT files are not built (plan item OP-76)."),
                3 => CadExportFormat.Stl,
                _ => throw new ZplRuntimeException($"EXPORTCAD: VEC1(1), the file type, is {type}; it must be 0 (IGES), 1 (STEP), 2 (SAT, not built) or 3 (STL)."),
            };
            if (CadExporter.IsCadExtension(path))
            {
                CadExportFormat? named = null;
                try { named = CadExporter.FormatFromExtension(path); } catch (NotSupportedException) { }
                if (named != format)
                    throw new ZplRuntimeException($"EXPORTCAD: the file '{Path.GetFileName(path)}' has the extension of another format than VEC1(1) = {type} ({format}).");
            }
            int spline = I(2, "the number of spline points");
            if (spline is not (16 or 32 or 64 or 128 or 256 or 512))
                throw new ZplRuntimeException($"EXPORTCAD: VEC1(2), the number of spline points, is {spline}; the help allows 16, 32, 64, 128, 256 or 512.");
            int first = I(3, "the first surface"), last = I(4, "the last surface");
            if (first < 1 || last < 1) throw new ZplRuntimeException($"EXPORTCAD: VEC1(3) and VEC1(4), the first and last surfaces, are {first} and {last}; each must be a surface number (1 or more).");
            Zero(5, "the ray layer", "layers are not written");
            Zero(6, "the lens layer", "layers are not written");
            if (Flag(v[7], "EXPORTCAD: VEC1(7), export dummy surfaces,"))
                throw new ZplRuntimeException("EXPORTCAD: VEC1(7) = 1 exports dummy surfaces, which is not built (OP-96 exports lens elements and mirrors).");
            bool solids = Flag(v[8], "EXPORTCAD: VEC1(8), surfaces as solids,");
            if (format == CadExportFormat.Step && !solids)
                throw new ZplRuntimeException("EXPORTCAD: VEC1(8) = 0 asks for surfaces in a STEP file; OP-96's STEP writer writes solids only (set VEC1(8) = 1).");
            if (format == CadExportFormat.Iges && solids)
                throw new ZplRuntimeException("EXPORTCAD: VEC1(8) = 1 asks for solids in an IGES file; OP-96's IGES writer writes surfaces only (set VEC1(8) = 0, or write STEP).");
            int patternCode = I(9, "the ray pattern");
            var pattern = patternCode switch
            {
                0 => CadRayPattern.XYFan,
                1 => CadRayPattern.XFan,
                2 => CadRayPattern.YFan,
                3 => CadRayPattern.Ring,
                4 => throw new ZplRuntimeException("EXPORTCAD: VEC1(9) = 4, the list ray pattern, is not built."),
                5 => CadRayPattern.None,
                6 => CadRayPattern.Grid,
                7 => throw new ZplRuntimeException("EXPORTCAD: VEC1(9) = 7, solid beams, is not built."),
                _ => throw new ZplRuntimeException($"EXPORTCAD: VEC1(9), the ray pattern, is {patternCode}; it must be 0 to 7."),
            };
            int rays = I(10, "the number of rays");
            int wave = I(11, "the wave number"), field = I(12, "the field number");
            bool deleteVignetted = Flag(v[13], "EXPORTCAD: VEC1(13), delete vignetted,");
            Zero(14, "the dummy surface thickness", "dummy surfaces are not exported");
            Zero(15, "split NSC rays", "non-sequential rays are not exported");
            Zero(16, "scatter NSC rays", "non-sequential rays are not exported");
            Zero(17, "NSC polarization", "non-sequential rays are not exported");
            Zero(18, "the configuration", "a chosen configuration or all configurations is not exported; SETCONFIG first and use 0 for the current one");
            int tol = I(19, "the tolerance");
            // Help: "Use 0 for 1.0E-4, 1 for 1.0E-5, 2 for 1.0E-6, and 3 for 1.0E-7. Note that if
            // any other value is entered it will default to 1E-4."
            double tolerance = tol is >= 0 and <= 3 ? CadExportSettings.ToleranceChoices[tol] : CadExportSettings.ToleranceChoices[0];
            Zero(20, "the first non-sequential object", "mixed mode is plan item OP-86");
            Zero(21, "the last non-sequential object", "mixed mode is plan item OP-86");
            Zero(22, "export ray footprints", "footprint export is not built");
            Zero(23, "the footprint layer", "footprint export is not built");
            Zero(24, "export the chief ray", "a separate chief ray is not built");

            var settings = new CadExportSettings
            {
                Format = format, FirstSurface = first, LastSurface = last, Tolerance = tolerance,
                RayPattern = pattern, NumberOfRays = rays, Wavelength = wave, Field = field, DeleteVignetted = deleteVignetted,
            };
            if (settings.Validate() is { Count: > 0 } problems) throw new ZplRuntimeException("EXPORTCAD: " + string.Join(" ", problems));
            CadExporter.Export(m.Session.Current, m.Session.Catalogs, settings, path, cancellation: m.Session.CancellationToken);
            if (!File.Exists(path)) throw new ZplRuntimeException($"EXPORTCAD: the export wrote no file '{path}'.");
        });
    }

    // ================================================================ Non-sequential objects

    /// <summary>The built object type of a saved object (wire-15: the editor's catalogue,
    /// <see cref="NscObjectCatalogue.Default"/>: OP-70's geometry, OP-77's sources and OP-79's
    /// detectors), each with the help's type code (SETNSCPROPERTY: "The NSC Object type codes are
    /// listed below. These codes are not case sensitive.").</summary>
    private static NscObjectTypeDescriptor NscType(OpticsNscObjectSettings o, int number, string who)
    {
        string t = (o.Type ?? "").Trim();
        var d = NscObjectCatalogue.Default.Find(t);
        if (d?.ZplCode is null)
            throw new ZplRuntimeException($"{who}: object {number}'s type '{t}' is not built. {NscObjectCatalogue.Default.NotBuiltMessage(t)}");
        return d;
    }

    /// <summary>Checks the non-sequential surface argument. The objects are the system's own
    /// non-sequential section ("If the program mode is set to Non-Sequential, use 1", NSTR,
    /// SETNSCPROPERTY); a non-sequential component surface inside a sequential lens is mixed
    /// mode, plan item OP-86.</summary>
    private static void NscSurface(double surface, string who)
    {
        if (surface != 1)
            throw new ZplRuntimeException($"{who}: the surface is {ZplInterpreter.Num(surface)}; only 1 is bound (the system's non-sequential objects, as in non-sequential mode). Non-sequential component surfaces in a sequential lens (mixed mode) are plan item OP-86.");
    }

    private static List<OpticsNscObjectSettings> NscObjects(ZplInterpreter m) =>
        m.Session.System.NonSequential?.Objects ?? new List<OpticsNscObjectSettings>();

    private static OpticsNscObjectSettings NscObject(ZplInterpreter m, double surface, double obj, string who, out int number)
    {
        NscSurface(surface, who);
        var objects = NscObjects(m);
        number = Int(obj, $"{who}: the object number");
        if (number < 1 || number > objects.Count)
            throw new ZplRuntimeException($"{who}: object {number} does not exist; the system has {objects.Count} non-sequential object{(objects.Count == 1 ? "" : "s")}{(objects.Count > 0 ? $" (1 to {objects.Count})" : "")}.");
        return objects[number - 1];
    }

    private static void SetNscObject(ZplInterpreter m, int number, OpticsNscObjectSettings o)
    {
        var ns = m.Session.System.NonSequential!;
        var list = new List<OpticsNscObjectSettings>(ns.Objects!);
        list[number - 1] = o;
        m.Session.System = m.Session.System with { NonSequential = ns with { Objects = list } };
    }

    private static string NscPositionName(int code) => code switch
    {
        1 => "x", 2 => "y", 3 => "z", 4 => "tilt x", 5 => "tilt y", 6 => "tilt z", _ => "",
    };

    private static int NscPositionCode(double code, string who)
    {
        int c = Int(code, $"{who}: the code");
        if (c is < 1 or > 6)
            throw new ZplRuntimeException($"{who}: position code {c} is not bound; codes 1 to 6 are x, y, z, tilt x, tilt y, tilt z (SETNSCPOSITION). The reference object is SETNSCPROPERTY and NPRO code 2.");
        return c;
    }

    private static int NscParameter(OpticsNscObjectSettings o, int number, double param, string who)
    {
        var type = NscType(o, number, who);
        int p = Int(param, $"{who}: the parameter number");
        int saved = o.Parameters?.Count ?? 0;
        int count = Math.Max(type.Parameters.Count, saved);   // Source Radial may save intensities past its columns
        if (p < 1 || p > count)
            throw new ZplRuntimeException($"{who}: parameter {p} is not one of object {number}'s; a {type.TypeName} has parameters 1 to {count}.");
        if (p > saved)
            throw new ZplRuntimeException($"{who}: object {number} holds {saved} of a {type.TypeName}'s {type.Parameters.Count} parameters, so parameter {p} is not set.");
        return p;
    }

    private static int NscFace(OpticsNscObjectSettings o, int number, double face, string who)
    {
        var type = NscType(o, number, who);
        int f = Int(face, $"{who}: the face number");
        if (!type.Faces.ContainsKey(f))
            throw new ZplRuntimeException($"{who}: face {f} is not one of object {number}'s; a {type.TypeName} has face{(type.Faces.Count == 1 ? " " : "s ")}{string.Join(", ", type.Faces.Keys.OrderBy(k => k))}.");
        return f;
    }

    /// <summary>SETNSCPROPERTY's codes that the saved objects hold.</summary>
    private const string NscPropertyCodesNote = "0 type (the help's NSC_ codes of the built types: NSC_SPHE, NSC_RECT, NSC_SLEN, the sources and NSC_DETE, NSC_DETC, NSC_DETP), 1 comment, 2 reference object, 3 inside of, 4 material, 5 face coating, 15 global XYZ rotation order, 20 \"Use Pixel Interpolation\" (Detector Rectangle and Color), 29 \"Face Is\" (0 Object Default, 1 Reflective, 2 Absorbing); on source objects the Sources tab: 101 random polarization, 102 reverse rays, 103 Jones x, 104 Jones y, 105 phase x, 106 phase y, 107 initial phase (degrees), 108 coherence length, 109 pre-propagation (lens units), 110 sampling (0 random, 1 Sobol), 111 bulk scatter (0 many, 1 once, 2 never), 112 array mode (0 none, 1 rectangular, 2 circular, 3 hexapolar, 4 hexagonal), 114 spectrum steps, 115 start and 116 end wavelength (µm), 161 and 162 the array integers, 165 and 166 the array sizes; " + ScatterCodesNote;

    private static ZplRuntimeException NscPropertyNotBound(int code, string who)
    {
        string why = code switch
        {
            17 => "\"Object Is A Detector\" (objects as detectors) is not built; OP-79 built the detector objects Detector Rectangle, Color and Polar, and OP-70's detecting stand-in is not that checkbox",
            6 => "scatter profiles (named sets of Coat/Scatter settings) are not built; set the face's scatter codes 7 to 12 instead",   // wire-16
            >= 21 and <= 28 => "user defined scattering loads a scatter DLL, and no DLLs are loaded",   // wire-16
            13 or 14 => "user defined apertures on objects are plan item OP-72",
            16 or 18 or 19 or 30 => "Consider Objects, Ignore Objects and \"Rays Ignore This Object\" are not built (Conventions §69)",
            >= 81 and <= 86 => "volume physics is plan item OP-81",
            >= 91 and <= 96 or >= 121 and <= 124 => "diffraction and gradient index in objects are plan item OP-82",
            113 => "the source colour mode is numbered by the dialog's list, which the help does not print",
            117 => "this build saves a source spectrum as a table, not a spectrum file name (spectrum files are plan item OP-78)",
            >= 181 and <= 183 => "the source colour-mode arguments are not saved with a source in this build",
            >= 163 and <= 164 => "the help's SETNSCPROPERTY table does not list it",
            >= 141 and <= 145 => "the drawing options are not saved with an object in this build",
            >= 171 and <= 177 => "birefringent objects are plan item OP-82",
            >= 200 and <= 203 => "the index and model glass data of an object are not bound",
            _ => "the help's SETNSCPROPERTY table does not list it",
        };
        return new ZplRuntimeException($"{who}: property code {code} is not bound ({why}). Bound codes: {NscPropertyCodesNote}.");
    }

    // wire-15: the Sources tab (SETNSCPROPERTY codes 101–116, 161–166 as the help lists them)
    // and the detectors' "Use Pixel Interpolation" (code 20), on the saved records of OP-77 and OP-79.

    private static readonly HashSet<int> SourceCodes = new() { 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 114, 115, 116, 161, 162, 165, 166 };

    private static readonly string[] SamplingNames = { "Random", "Sobol" };
    private static readonly string[] BulkScatterNames = { "Many", "Once", "Never" };
    private static readonly string[] ArrayNames = { "None", "Rectangular", "Circular", "Hexapolar", "Hexagonal" };

    private static void DetectorFlat(OpticsNscObjectSettings o, int number, string who)
    {
        var t = NscType(o, number, who);
        if (t.TypeName is not (OpenSim.Optics.NonSequential.Detectors.NscDetectorTypes.Rectangle or OpenSim.Optics.NonSequential.Detectors.NscDetectorTypes.Color))
            throw new ZplRuntimeException($"{who}: code 20 (\"Use Pixel Interpolation\") is a Detector Rectangle and Detector Color property; object {number} is a {t.TypeName}.");
    }

    private static OpticsNscSourceSettings SourceTab(OpticsNscObjectSettings o, int number, string who)
    {
        var t = NscType(o, number, who);
        if (t.Category != NscObjectCategory.Source)
            throw new ZplRuntimeException($"{who}: codes 101 to 166 are the Sources tab's; object {number} is a {t.TypeName}, not a source.");
        return o.Source ?? new OpticsNscSourceSettings();
    }

    private static int NameCode(string? saved, string[] names, int fallback, string what, int number, string who)
    {
        if (string.IsNullOrWhiteSpace(saved)) return fallback;
        int i = Array.FindIndex(names, n => string.Equals(n, saved.Trim(), StringComparison.Ordinal));
        return i >= 0 ? i : throw new ZplRuntimeException($"{who}: object {number}'s {what} '{saved}' is not one of {string.Join(", ", names)}.");
    }

    private static double SourceProperty(OpticsNscObjectSettings o, int number, int code, string who)
    {
        var s = SourceTab(o, number, who);
        var spectrum = new OpenSim.Optics.NonSequential.Sources.NscSpectrum();
        return code switch
        {
            101 => s.RandomPolarization ?? true ? 1 : 0,   // the defaults are ProjectNscSources.Properties's
            102 => s.ReverseRays == true ? 1 : 0,
            103 => s.Jx ?? 1,
            104 => s.Jy ?? 0,
            105 => s.XPhase ?? 0,
            106 => s.YPhase ?? 0,
            107 => s.InitialPhase ?? 0,
            108 => s.CoherenceLength ?? 0,
            109 => s.PrePropagation ?? 0,
            110 => NameCode(s.Sampling, SamplingNames, 1, "sampling method", number, who),
            111 => NameCode(s.BulkScatter, BulkScatterNames, 0, "bulk scatter method", number, who),
            112 => NameCode(s.ArrayType, ArrayNames, 0, "array type", number, who),
            114 => s.SpectrumCount ?? spectrum.Count,
            115 => s.WavelengthFrom ?? spectrum.FromMicrometers,
            116 => s.WavelengthTo ?? spectrum.ToMicrometers,
            161 => s.ArrayCount1 ?? 1,
            162 => s.ArrayCount2 ?? 1,
            165 => s.ArraySize1 ?? 0,
            _ => s.ArraySize2 ?? 0,
        };
    }

    private static OpticsNscObjectSettings WithSourceProperty(OpticsNscObjectSettings o, int number, int code, double v)
    {
        const string who = "SETNSCPROPERTY";
        var s = SourceTab(o, number, who);
        string Named(string[] names, string what)
        {
            int i = Int(v, $"{who}: the {what}");
            if (i < 0 || i >= names.Length)
                throw new ZplRuntimeException($"{who}: the {what} is {i}; it must be {string.Join(", ", names.Select((n, k) => $"{k} ({n})"))}.");
            return names[i];
        }
        s = code switch
        {
            101 => s with { RandomPolarization = Flag(v, $"{who}: the random polarization flag") },
            102 => s with { ReverseRays = Flag(v, $"{who}: the reverse rays flag") },
            103 => s with { Jx = v },
            104 => s with { Jy = v },
            105 => s with { XPhase = v },
            106 => s with { YPhase = v },
            107 => s with { InitialPhase = v },
            108 => s with { CoherenceLength = v },
            109 => s with { PrePropagation = v },
            110 => s with { Sampling = Named(SamplingNames, "sampling method") },
            111 => s with { BulkScatter = Named(BulkScatterNames, "bulk scatter method") },
            112 => s with { ArrayType = Named(ArrayNames, "array mode") },
            114 => s with { SpectrumCount = Int(v, $"{who}: the number of spectrum steps") },
            115 => s with { WavelengthFrom = v },
            116 => s with { WavelengthTo = v },
            161 => s with { ArrayCount1 = Int(v, $"{who}: array integer 1") },
            162 => s with { ArrayCount2 = Int(v, $"{who}: array integer 2") },
            165 => s with { ArraySize1 = v },
            _ => s with { ArraySize2 = v },
        };
        return o with { Source = s };
    }

    private static void RegisterNonSequentialObjects()
    {
        Fn("NOBJ", 1, 1, "The number of non-sequential objects; the surface must be 1 (the system's objects, as in non-sequential mode; mixed mode is OP-86).", (m, v) =>
        {
            NscSurface(Num(v, 0, "the surface"), "NOBJ");
            return N(NscObjects(m).Count);
        });
        Fn("NPAR", 3, 3, "A parameter of an object (surface 1), as saved in lens units; the built types only (OP-70's Sphere, Rectangle and Standard Lens, the sources and the detectors), with their parameters in the editor's order.", (m, v) =>
        {
            var o = NscObject(m, Num(v, 0, "the surface"), Num(v, 1, "the object"), "NPAR", out int number);
            int p = NscParameter(o, number, Num(v, 2, "the parameter"), "NPAR");
            return N(o.Parameters![p - 1]);
        });
        Fn("NPOS", 3, 3, "x, y, z (lens units) or tilt x, y, z (degrees) of an object (surface 1) by SETNSCPOSITION's codes 1 to 6. The help's NPOS page names seven values (the reference object first) for codes 1 to 6; the codes are read as SETNSCPOSITION's, which it points to, and the reference object is NPRO code 2.", (m, v) =>
        {
            var o = NscObject(m, Num(v, 0, "the surface"), Num(v, 1, "the object"), "NPOS", out _);
            return N(NscPositionCode(Num(v, 2, "the code"), "NPOS") switch
            {
                1 => o.X ?? 0, 2 => o.Y ?? 0, 3 => o.Z ?? 0, 4 => o.TiltX ?? 0, 5 => o.TiltY ?? 0, _ => o.TiltZ ?? 0,
            });
        });
        Fn("NPRO", 4, 4, "A property of an object (surface 1) by SETNSCPROPERTY's codes the saved objects hold: " + NscPropertyCodesNote + "; string values go to $BUFFER() and 0 is returned. Any other code is refused by number.", (m, v) =>
        {
            var o = NscObject(m, Num(v, 0, "the surface"), Num(v, 1, "the object"), "NPRO", out int number);
            int code = Int(v, 2, "the code");
            ZplValue Text(string t) { m.Session.Buffer = t; return N(0); }
            switch (code)
            {
                case 0: return Text(NscType(o, number, "NPRO").ZplCode!);
                case 1: return Text(o.Comment ?? "");
                case 2: return N(o.ReferenceObject ?? 0);
                case 3: return N(o.InsideOf ?? 0);
                case 4: return Text(o.Material ?? "");
                case 5:
                {
                    int f = NscFace(o, number, Num(v, 3, "the face"), "NPRO");
                    return Text(o.Faces?.FirstOrDefault(x => (x.Face ?? 0) == f)?.Coating ?? "");
                }
                case 15: return N(o.GlobalXyzRotationOrder == true ? 1 : 0);
                case 20: DetectorFlat(o, number, "NPRO"); return N(o.DetectorPixelInterpolation == true ? 1 : 0);   // wire-15
                case var c when ScatterFaceCodes.Contains(c) || ScatterToCodes.Contains(c): return ScatterProperty(m, o, number, c, Num(v, 3, "the face"));   // wire-16
                case var c when SourceCodes.Contains(c): return N(SourceProperty(o, number, c, "NPRO"));   // wire-15
                case 29:
                {
                    int f = NscFace(o, number, Num(v, 3, "the face"), "NPRO");
                    string faceIs = (o.Faces?.FirstOrDefault(x => (x.Face ?? 0) == f)?.FaceIs ?? "Object Default").Trim();
                    return N(faceIs switch
                    {
                        "" or "Object Default" => 0,
                        "Reflective" => 1,
                        "Absorbing" => 2,
                        _ => throw new ZplRuntimeException($"NPRO: object {number}, face {f}: \"Face Is\" '{faceIs}' is not Object Default, Reflective or Absorbing."),
                    });
                }
                default: throw NscPropertyNotBound(code, "NPRO");
            }
        });
        Fn("OBJC", 1, 1, "The first non-sequential object whose comment matches the string, case ignored; −1 when none does.", (m, v) =>
        {
            string s = Str(v, 0, "the comment");
            var objects = NscObjects(m);
            for (int i = 0; i < objects.Count; i++)
                if (string.Equals(objects[i].Comment ?? "", s, StringComparison.OrdinalIgnoreCase)) return N(i + 1);
            return N(-1);
        });

        Kw("SETNSCPARAMETER", "Sets a parameter of an object (surface 1), lens units as saved; the built types only (OP-70's Sphere, Rectangle and Standard Lens, the sources and the detectors).", (m, a) =>
        {
            a.Exactly(4, "SETNSCPARAMETER");
            var o = NscObject(m, a.Num(m, 0, "the surface"), a.Num(m, 1, "the object"), "SETNSCPARAMETER", out int number);
            int p = NscParameter(o, number, a.Num(m, 2, "the parameter"), "SETNSCPARAMETER");
            var list = new List<double>(o.Parameters!) { [p - 1] = a.Num(m, 3, "the value") };
            SetNscObject(m, number, o with { Parameters = list });
        });
        Kw("SETNSCPOSITION", "Sets x, y, z (lens units) or tilt x, y, z (degrees) of an object (surface 1), codes 1 to 6.", (m, a) =>
        {
            a.Exactly(4, "SETNSCPOSITION");
            var o = NscObject(m, a.Num(m, 0, "the surface"), a.Num(m, 1, "the object"), "SETNSCPOSITION", out int number);
            int code = NscPositionCode(a.Num(m, 2, "the code"), "SETNSCPOSITION");
            double x = a.Num(m, 3, $"the {NscPositionName(code)} value");
            SetNscObject(m, number, code switch
            {
                1 => o with { X = x }, 2 => o with { Y = x }, 3 => o with { Z = x },
                4 => o with { TiltX = x }, 5 => o with { TiltY = x }, _ => o with { TiltZ = x },
            });
        });
        Kw("SETNSCPROPERTY", "Sets a property of an object (surface 1) by the help's codes the saved objects hold: " + NscPropertyCodesNote + ". A new type keeps the saved parameters (the help does not say what becomes of them). Any other code is refused by number with the plan item it waits for.", (m, a) =>
        {
            a.Exactly(5, "SETNSCPROPERTY");
            var o = NscObject(m, a.Num(m, 0, "the surface"), a.Num(m, 1, "the object"), "SETNSCPROPERTY", out int number);
            int code = a.Int(m, 2, "the code");
            var value = a.Value(m, 4, "the value");
            string Text() => value.IsString ? value.Text! : ZplInterpreter.Num(value.Number);
            int Whole(string what) => value.IsString ? throw new ZplRuntimeException($"SETNSCPROPERTY: {what} must be a number, not \"{value.Text}\".") : Int(value.Number, $"SETNSCPROPERTY: {what}");
            OpticsNscObjectSettings WithFace(int f, Func<OpticsNscFaceSettings, OpticsNscFaceSettings> change)
            {
                var faces = new List<OpticsNscFaceSettings>(o.Faces ?? new List<OpticsNscFaceSettings>());
                int i = faces.FindIndex(x => (x.Face ?? 0) == f);
                if (i < 0) faces.Add(change(new OpticsNscFaceSettings { Face = f }));
                else faces[i] = change(faces[i]);
                return o with { Faces = faces };
            }
            OpticsNscObjectSettings changed;
            switch (code)
            {
                case 0:
                {
                    string t = Text().Trim();
                    var catalogue = NscObjectCatalogue.Default;
                    var match = catalogue.Types.FirstOrDefault(x => string.Equals(x.ZplCode, t, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        var refused = catalogue.Refused.FirstOrDefault(x => string.Equals(x.ZplCode, t, StringComparison.OrdinalIgnoreCase));
                        throw new ZplRuntimeException(refused is not null
                            ? $"SETNSCPROPERTY: the object type '{t}' ({refused.TypeName}) is not built: {refused.NotBuiltReason}"
                            : $"SETNSCPROPERTY: the object type '{t}' is not built (this build has {string.Join(", ", catalogue.Types.Select(x => x.ZplCode))}; the object library is plan items OP-72 to OP-75).");
                    }
                    changed = o with { Type = match.TypeName };
                    break;
                }
                case 1: changed = o with { Comment = Text() }; break;
                case 2: changed = o with { ReferenceObject = Whole("the reference object") }; break;
                case 3: changed = o with { InsideOf = Whole("the \"inside of\" object") }; break;
                case 4: changed = o with { Material = Text().Trim() }; break;
                case 5:
                {
                    int f = NscFace(o, number, a.Num(m, 3, "the face"), "SETNSCPROPERTY");
                    string c = Text().Trim();
                    changed = WithFace(f, x => x with { Coating = c.Length == 0 ? null : c });
                    break;
                }
                case 15: changed = o with { GlobalXyzRotationOrder = Flag(value.IsString ? double.NaN : value.Number, "SETNSCPROPERTY: the global XYZ rotation order flag") }; break;
                case 20:   // wire-15
                    DetectorFlat(o, number, "SETNSCPROPERTY");
                    changed = o with { DetectorPixelInterpolation = Flag(value.IsString ? double.NaN : value.Number, "SETNSCPROPERTY: the \"Use Pixel Interpolation\" flag") };
                    break;
                case var c when ScatterFaceCodes.Contains(c) || ScatterToCodes.Contains(c):   // wire-16
                    changed = WithScatterProperty(o, number, c, a.Num(m, 3, "the face"), value);
                    break;
                case var c when SourceCodes.Contains(c):   // wire-15
                    changed = WithSourceProperty(o, number, c, value.IsString ? throw new ZplRuntimeException($"SETNSCPROPERTY: code {c} takes a number, not \"{value.Text}\".") : value.Number);
                    break;
                case 29:
                {
                    int f = NscFace(o, number, a.Num(m, 3, "the face"), "SETNSCPROPERTY");
                    int k = Whole("\"Face Is\"");
                    string faceIs = k switch
                    {
                        0 => "Object Default", 1 => "Reflective", 2 => "Absorbing",
                        _ => throw new ZplRuntimeException($"SETNSCPROPERTY: \"Face Is\" is {k}; it must be 0 (Object Default), 1 (Reflective) or 2 (Absorbing)."),
                    };
                    changed = WithFace(f, x => x with { FaceIs = faceIs });
                    break;
                }
                default: throw NscPropertyNotBound(code, "SETNSCPROPERTY");
            }
            SetNscObject(m, number, changed);
        });
    }
}
