using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Lightning;   // wire-17
using OpenSim.Optics.NonSequential.RayDatabase;
using OpenSim.Optics.NonSequential.Sources;

namespace OpenSim.Optics.Scripting.Zpl;

// wire-15: the non-sequential trace and the detectors. NSTR and NSTR2 trace the system's own
// sources through OP-77's runner (ProjectNscSources.Trace); the detector data they leave is kept
// in the session, read by NSDD, NSDE, NSDP and NSDC (NscDetectorQueries), cleared by NSDD, NSDE
// and NSDP with object 0 or −n, saved and loaded by SAVEDETECTOR and LOADDETECTOR (OP-79's
// NscDetectorFile); LOST reads the last trace's ledger. Each follows its OpticStudio 2025 R2 help
// page under plans/sources/optics/op-99-help/ (NSTR, NSTR2, SAVEDETECTOR (keywords),
// LOADDETECTOR (keywords), Numeric Functions) and, for the pixel and data codes the functions
// share with the operands, op-55b-help/ "Non-sequential Ray Tracing and Detector Operands"
// (docs/Optics-Conventions.md §56 and §73).
// wire-16: NSTR's scatter flag reaches OP-80's surface scattering and its save arguments OP-83's
// recorder and ZRD files; ZRDPLAYBACK, ZRDFILTER, ZRDSUM and ZRDAPPEND follow their help pages
// (op-99-help/) onto OP-83's NscZrdFile.Read/Subset/Join/Write and NscRayPlayback.Play (§75).
// wire-17: NSLT onto OP-85's ProjectNscLightning.Trace (§79); the filters of NSTR's save,
// ZRDPLAYBACK and ZRDFILTER come through NscRayDatabaseProject.Filter, so OP-84's path filters _n
// and saved sequences work from macros (§78).

public static partial class ZplLibrary
{
    private static OpticsAnalysisContext NscContext(ZplInterpreter m) =>
        new("Macro", m.Session.Current) { Catalogs = m.Session.Catalogs };

    private static ZplRuntimeException NscFailed(Exception e, string who) =>
        new($"{who}: {e.Message.Split('\n')[0].TrimEnd('\r')}");

    /// <summary>The data on detector object <paramref name="number"/>: what traces and LOADDETECTOR
    /// left, or the detector's empty data (a detector never traced, or cleared, reads zero).</summary>
    private static NscDetectorData NscDetector(ZplInterpreter m, double surface, int number, string who)
    {
        NscSurface(surface, who);
        if (m.Session.NscDetectors.TryGetValue(number, out var held)) return held;
        var context = NscContext(m);
        NscScene? scene;
        try { scene = ProjectNonSequential.Scene(context, m.Session.Unit); }
        catch (ArgumentException e) { throw NscFailed(e, who); }
        int count = scene?.Objects.Count ?? 0;
        if (number < 1 || number > count)
            throw new ZplRuntimeException($"{who}: object {number} does not exist; the system has {count} non-sequential object{(count == 1 ? "" : "s")}.");
        double primary = SystemDataSettings.FromSettings(context.System.SystemData, new List<string>()).Wavelengths.Primary.Micrometers;
        return NscDetectorQueries.Empty(scene!, number, primary)
            ?? throw new ZplRuntimeException($"{who}: object {number} ({scene!.Objects[number - 1].Geometry.TypeName}) is not a detector object (Detector Rectangle, Color or Polar).");
    }

    /// <summary>NSDD, NSDE and NSDP's clearing: object 0 clears every detector, −n detector n; 0 is returned.</summary>
    private static bool NscClear(ZplInterpreter m, double surface, int number, string who)
    {
        NscSurface(surface, who);
        if (number > 0) return false;
        if (number == 0) m.Session.NscDetectors.Clear();
        else m.Session.NscDetectors.Remove(-number);
        return true;
    }

    private static double NscQuery(Func<double> query, string who)
    {
        try { return query(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { throw NscFailed(e, who); }
    }

    private static void NscTrace(ZplInterpreter m, ZplArgs a, string who, bool paths)
    {
        int max = paths ? 13 : 11;
        if (a.Count < 7 || a.Count > max)
            throw new ZplRuntimeException($"{who} takes surf, source, split, scatter, usepolar, ignore_errors, random_seed [, save, savefilename, filter, zrd_format{(paths ? ", savepaths, pathfilename" : "")}]; {a.Count} arguments given.");
        NscSurface(a.Num(m, 0, "the surface"), who);
        int source = a.Int(m, 1, $"{who}: the source");
        if (source < 0) throw new ZplRuntimeException($"{who}: the source is {source}; it must be 0 (every source) or a source object's number.");
        bool split = a.Num(m, 2, "split") != 0;
        bool scatter = a.Num(m, 3, "scatter") != 0;   // wire-16: OP-80's surface scattering ("If Scatter is non-zero, then scattering is on")
        bool polar = a.Num(m, 4, "usepolar") != 0 || split;   // "If splitting is on polarization is automatically selected."
        bool ignoreErrors = a.Num(m, 5, "ignore_errors") != 0;
        int seed = a.Int(m, 6, $"{who}: the random seed");
        if (paths && a.Has(11) && a.Num(m, 11, "savepaths") != 0)
            throw new ZplRuntimeException($"{who}: savepaths is on; PAF path files are not built (Path Analysis runs on a saved ray database instead: NSTR's save arguments, then the Path Analysis analysis).");
        var context = NscContext(m);
        // wire-16: save, savefilename, filter and zrd_format (OP-83's recorder and ZRD files).
        var save = a.Has(7) && a.Num(m, 7, "save") != 0 ? NscSaveTarget(m, a, who, context) : null;
        // "If random_seed is zero, then the random number generator will be seeded with a random
        // value": the macro's own generator (RANDOMIZE seeds it) draws one.
        long traceSeed = seed != 0 ? seed : m.Session.Random.NextInt64(1, long.MaxValue);

        NscSourceRunResult run;
        try
        {
            run = ProjectNscSources.Trace(context, source == 0 ? null : new[] { source }, split, polar, ignoreErrors, traceSeed, m.Session.CancellationToken,
                recorder: save?.Recorder, scatter: scatter);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { throw NscFailed(e, who); }
        m.Session.NscLedger = run.Ledger;
        if (save is { } target)
            NscZrd(() => { NscZrdFile.Write(target.Path, target.Recorder.Database, target.Format, m.Session.Unit); return 0; }, who);
        NscKeep(m, run.Detectors ?? Array.Empty<NscDetectorData>(), who);
    }

    /// <summary>Adds detector data to what each detector holds (NSTR, NSTR2, ZRDPLAYBACK).</summary>
    private static void NscKeep(ZplInterpreter m, IEnumerable<NscDetectorData> detectors, string who)
    {
        foreach (var d in detectors)
        {
            if (m.Session.NscDetectors.TryGetValue(d.ObjectNumber, out var held))
            {
                try { m.Session.NscDetectors[d.ObjectNumber] = held.Combine(d); }
                catch (ArgumentException e)
                {
                    throw new ZplRuntimeException($"{who}: detector object {d.ObjectNumber}'s new data cannot be added to the data it holds ({e.Message}); clear it first (NSDD(1, -{d.ObjectNumber}, 0, 0)).");
                }
            }
            else m.Session.NscDetectors[d.ObjectNumber] = d;
        }
    }

    // ================================================================ wire-16: ray databases

    private sealed record NscSave(string Path, NscZrdFormat Format, NscRayRecorder Recorder);

    /// <summary>NSTR's save arguments (help, NSTR and NSTR2): savefilename (a .ZRD name; "no path
    /// should be specified", so it is in the lens file's folder, help "Ray Trace", "Saving ray data
    /// to a file"), the filter string or named filter applied to each tree before it is saved, and
    /// zrd_format 0 (uncompressed full data, also when omitted); 1 and 2 (compressed) are refused
    /// by name, and 3 is this program's full-data format, which ZRDPLAYBACK can replay
    /// (Conventions §75).</summary>
    private static NscSave NscSaveTarget(ZplInterpreter m, ZplArgs a, string who, OpticsAnalysisContext context)
    {
        string name = a.FileName(m, 8, $"{who}: savefilename");
        string extension = Path.GetExtension(name).ToUpperInvariant();
        if (extension is ".DAT" or ".SDF" or ".TM25RAY")
            throw new ZplRuntimeException($"{who}: '{name}' names a source file ({extension[1..]}); saving the rays that strike an object as a source file is not built. Give a .ZRD name for the ray database.");
        if (extension != ".ZRD")
            throw new ZplRuntimeException($"{who}: savefilename '{name}' must end in .ZRD (a ray database; help, \"Saving ray data to a file\").");
        string path = ZplSession.Resolve(name, NscFolder(m), "lens folder");
        string filterText = a.Has(9) ? a.Str(m, 9, $"{who}: the filter") : "";
        int zrd = a.Int(m, 10, $"{who}: zrd_format", 0);
        var format = zrd switch
        {
            0 => NscZrdFormat.UncompressedFullData,
            3 => NscZrdFormat.OpenSimFull,
            1 or 2 => throw new ZplRuntimeException($"{who}: zrd_format {zrd} ({(zrd == 1 ? "compressed basic data" : "compressed full data")}) is not built: the help says the compressed formats are unsuitable for reading or writing outside OpticStudio. Use 0 (uncompressed full data) or 3 (this program's full data, which ZRDPLAYBACK replays)."),
            _ => throw new ZplRuntimeException($"{who}: zrd_format is {zrd}; it must be 0 (uncompressed full data), 1 or 2 (compressed, not built) or 3 (this program's full data)."),
        };
        var filter = NscZrd(() => NscRayDatabaseProject.Filter(context, filterText), who);
        NscRayFilterFrame? frame = null;
        if (filter?.NeedsFrame == true)
            frame = NscRayFilterFrame.For(NscSceneOf(m, context, who), m.Session.Unit);
        return new NscSave(path, format, NscZrd(() => new NscRayRecorder(filter, frame), who));
    }

    private static NscScene NscSceneOf(ZplInterpreter m, OpticsAnalysisContext context, string who)
    {
        NscScene? scene;
        try { scene = ProjectNonSequential.Scene(context, m.Session.Unit); }
        catch (ArgumentException e) { throw NscFailed(e, who); }
        return scene ?? throw new ZplRuntimeException($"{who}: the system has no non-sequential objects.");
    }

    /// <summary>Runs a ray-database call, turning its refusals into the keyword's error.</summary>
    private static T NscZrd<T>(Func<T> call, string who)
    {
        try { return call(); }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or NotSupportedException
                                       or InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            throw NscFailed(e, who);
        }
    }

    /// <summary>A ZRD file argument: a name without a folder is in the lens file's folder.</summary>
    private static string NscZrdPath(ZplInterpreter m, ZplArgs a, int i, string what) =>
        ZplSession.Resolve(a.FileName(m, i, what), NscFolder(m), "lens folder");

    private static void NscDistinct(string input, string output, string who)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ZplRuntimeException($"{who}: the input and output files are both '{output}'; \"The input and output file names must be distinct\" (help, {who}).");
    }

    private static NscZrdFormat NscSameFormat(string first, string second, string who)
    {
        var f1 = NscZrd(() => NscZrdFile.Detect(first), who);
        var f2 = NscZrd(() => NscZrdFile.Detect(second), who);
        if (f1 != f2)
            throw new ZplRuntimeException($"{who}: '{Path.GetFileName(first)}' is {NscFormatName(f1)} and '{Path.GetFileName(second)}' is {NscFormatName(f2)}; the files must use the same ZRD format (help, {who}).");
        return f1;
    }

    private static string NscFormatName(NscZrdFormat f) => f switch
    {
        NscZrdFormat.UncompressedFullData => "uncompressed full data",
        NscZrdFormat.CompressedBasicData => "compressed basic data",
        NscZrdFormat.CompressedFullData => "compressed full data",
        _ => "OpenSim full data",
    };

    private static void RegisterNscRayDatabase()
    {
        Kw("ZRDPLAYBACK", "Reads a ZRD file (a name without a folder is in the lens file's folder) and adds its recorded detector hits to detector object n (0: every detector) of the system (surface 1), clearing that detector (or every detector) first when clear is non-zero, optionally only the segments that pass the filter string or named filter (OP-83's playback repeats the trace's reduction, so an unfiltered replay gives the traced data to the bit). Only this program's full-data format (NSTR zrd_format 3) holds the hits; uncompressed full data (UFD) files are refused.", (m, a) =>
        {
            const string who = "ZRDPLAYBACK";
            if (a.Count < 4 || a.Count > 5) throw new ZplRuntimeException($"{who} takes zrdfilename, surface, detector, clear [, filterstring]; {a.Count} arguments given.");
            string path = NscZrdPath(m, a, 0, $"{who}: the ZRD file");
            NscSurface(a.Num(m, 1, "the surface"), who);
            int detector = a.Int(m, 2, $"{who}: the detector");
            if (detector < 0) throw new ZplRuntimeException($"{who}: the detector is {detector}; it must be 0 (every detector) or a detector object's number.");
            bool clear = a.Num(m, 3, "clear") != 0;
            string filterText = a.Has(4) ? a.Str(m, 4, $"{who}: the filter") : "";
            var context = NscContext(m);
            var scene = NscSceneOf(m, context, who);
            var data = NscZrd(() =>
            {
                var db = NscZrdFile.Read(path, m.Session.Unit);
                var filter = NscRayDatabaseProject.Filter(context, filterText);
                var frame = filter?.NeedsFrame == true ? NscRayFilterFrame.For(scene, m.Session.Unit) : null;
                return NscRayPlayback.Play(db, scene, filter, detector, frame, m.Session.CancellationToken);
            }, who);
            if (clear)
            {
                if (detector == 0) m.Session.NscDetectors.Clear();
                else m.Session.NscDetectors.Remove(detector);
            }
            NscKeep(m, data, who);
        });
        Kw("ZRDFILTER", "Reads a ZRD file, keeps the rays and segments that pass the filter string or named filter (OP-83's subset: each tree's passing branches, renumbered) and writes them to a new file in the input's format; names without a folder are in the lens file's folder, and the two must differ.", (m, a) =>
        {
            const string who = "ZRDFILTER";
            a.Exactly(3, who);
            string input = NscZrdPath(m, a, 0, $"{who}: the input file"), output = NscZrdPath(m, a, 1, $"{who}: the output file");
            NscDistinct(input, output, who);
            string filterText = a.Str(m, 2, $"{who}: the filter");
            var context = NscContext(m);
            var filter = NscZrd(() => NscRayDatabaseProject.Filter(context, filterText), who);
            var frame = filter?.NeedsFrame == true ? NscRayFilterFrame.For(NscSceneOf(m, context, who), m.Session.Unit) : null;
            NscZrd(() =>
            {
                var format = NscZrdFile.Detect(input);
                var subset = NscZrdFile.Subset(NscZrdFile.Read(input, m.Session.Unit), filter, frame);
                NscZrdFile.Write(output, subset, format, m.Session.Unit);
                return 0;
            }, who);
        });
        Kw("ZRDSUM", "Writes the rays of two ZRD files of the same format, the first's then the second's, to a third file in that format (OP-83's join; no object numbers or coordinates are changed); names without a folder are in the lens file's folder, and the output must differ from both inputs.", (m, a) =>
        {
            const string who = "ZRDSUM";
            a.Exactly(3, who);
            string first = NscZrdPath(m, a, 0, $"{who}: the first input file"), second = NscZrdPath(m, a, 1, $"{who}: the second input file");
            string output = NscZrdPath(m, a, 2, $"{who}: the output file");
            NscDistinct(first, output, who);
            NscDistinct(second, output, who);
            var format = NscSameFormat(first, second, who);
            NscZrd(() =>
            {
                var joined = NscZrdFile.Join(NscZrdFile.Read(first, m.Session.Unit), NscZrdFile.Read(second, m.Session.Unit));
                NscZrdFile.Write(output, joined, format, m.Session.Unit);
                return 0;
            }, who);
        });
        Kw("ZRDAPPEND", "Appends the rays of the first ZRD file to the end of the second, which must exist and use the same format; the first is left unaltered (OP-83's join). Names without a folder are in the lens file's folder, and the two must differ.", (m, a) =>
        {
            const string who = "ZRDAPPEND";
            a.Exactly(2, who);
            string input = NscZrdPath(m, a, 0, $"{who}: the input file"), output = NscZrdPath(m, a, 1, $"{who}: the output file");
            NscDistinct(input, output, who);
            var format = NscSameFormat(input, output, who);
            NscZrd(() =>
            {
                var joined = NscZrdFile.Join(NscZrdFile.Read(output, m.Session.Unit), NscZrdFile.Read(input, m.Session.Unit));
                NscZrdFile.Write(output, joined, format, m.Session.Unit);
                return 0;
            }, who);
        });
    }

    private static string NscFolder(ZplInterpreter m) =>
        (m.Session.LensPath is { } lens ? Path.GetDirectoryName(lens) : null) ?? m.Session.LensFolder ?? m.Session.MacroFolder ?? "";

    /// <summary>The file's data on detector object <paramref name="number"/>: it must be of the
    /// object's type, pixel counts and size (sizes and angles to 1e-9 relative, as the file holds
    /// them in its lens unit); the object's own sizes are kept so later traces add to it.</summary>
    private static NscDetectorData Fitted(NscDetectorData detector, NscDetectorData file, int number, string name)
    {
        static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b)) || a == b;
        string Refuse(string what) => throw new ZplRuntimeException($"LOADDETECTOR: '{name}' holds a {file.TypeName} {what}; object {number} is a {detector.TypeName} that does not match.");
        switch (detector, file)
        {
            case (NscFlatDetectorData d, NscFlatDetectorData f) when d.IsColor == f.IsColor:
                if (d.PixelsX != f.PixelsX || d.PixelsY != f.PixelsY || !Same(d.HalfWidthX, f.HalfWidthX) || !Same(d.HalfWidthY, f.HalfWidthY)
                    || !Same(d.AngleXMin, f.AngleXMin) || !Same(d.AngleXMax, f.AngleXMax) || !Same(d.AngleYMin, f.AngleYMin) || !Same(d.AngleYMax, f.AngleYMax))
                    Refuse($"of {f.PixelsX} × {f.PixelsY} pixels and another size or angle range");
                return f with
                {
                    ObjectNumber = number, HalfWidthX = d.HalfWidthX, HalfWidthY = d.HalfWidthY,
                    AngleXMin = d.AngleXMin, AngleXMax = d.AngleXMax, AngleYMin = d.AngleYMin, AngleYMax = d.AngleYMax,
                };
            case (NscPolarDetectorData d, NscPolarDetectorData f):
                if (d.PolarPixels != f.PolarPixels || d.AzimuthPixels != f.AzimuthPixels || !Same(d.MaxAngle, f.MaxAngle))
                    Refuse($"of {f.PolarPixels} × {f.AzimuthPixels} pixels to {f.MaxAngle}°");
                return f with { ObjectNumber = number, MaxAngle = d.MaxAngle, RadialSize = d.RadialSize };
            default:
                Refuse("");
                return file;
        }
    }

    private static void RegisterNscTrace()
    {
        RegisterNscRayDatabase();   // wire-16
        Kw("NSTR", "Traces the system's sources (source 0: all; surface 1) through OP-77's runner with the analysis ray counts, splitting, scattering (OP-80's surface scattering; volume physics, OP-81, runs by each source's Bulk Scatter whatever the flag says), polarization (on whenever splitting is), Ignore Errors and the seed (0: one drawn from the macro's RAND generator); the detector data is added to what each detector holds (clear it with NSDD(1, 0, 0, 0)). With save on, the ray trees that pass the filter string or named filter are written to savefilename (.ZRD, in the lens file's folder) in zrd_format 0 (uncompressed full data, also when omitted) or 3 (this program's full data, which ZRDPLAYBACK replays); 1 and 2 (compressed) and source files (DAT, SDF, TM25RAY) are refused by name.", (m, a) => NscTrace(m, a, "NSTR", paths: false));
        Kw("NSTR2", "As NSTR, with NSTR2's arguments; savepaths is refused by name (PAF path files are not built; Path Analysis itself is, OP-84).", (m, a) => NscTrace(m, a, "NSTR2", paths: true));
        // wire-17: NSLT (help, op-99-help/NSLT) onto OP-85's ProjectNscLightning.Trace.
        Kw("NSLT", "Runs a LightningTrace (OP-85) of the system's sources (surface 1; source 0: all) with ray_sampling 0 (Low, 1X) to 5 (1024X) and edge_sampling 0 to 4 (256X); the detector data is added to what each detector holds, as NSTR's is (clear it with NSDD(1, 0, 0, 0)). Splitting, scattering and polarization are not used (help, \"Lightning Trace\"); LOST still reads the last NSTR trace.", (m, a) =>
        {
            const string who = "NSLT";
            a.Exactly(4, who);
            NscSurface(a.Num(m, 0, "the surface"), who);
            int source = a.Int(m, 1, $"{who}: the source");
            if (source < 0) throw new ZplRuntimeException($"{who}: the source is {source}; it must be 0 (every source) or a source object's number.");
            int ray = a.Int(m, 2, $"{who}: ray_sampling");
            if (ray < 0 || ray > NscLightningSettings.MaxLevel)
                throw new ZplRuntimeException($"{who}: ray_sampling is {ray}; \"valid values being between 0 ... and 5\" (help, NSLT).");
            int edge = a.Int(m, 3, $"{who}: edge_sampling");
            if (edge < 0 || edge > 4)
                throw new ZplRuntimeException($"{who}: edge_sampling is {edge}; \"valid values being between 0 ... and 4\" (help, NSLT).");
            NscLightningResult result;
            try { result = ProjectNscLightning.Trace(NscContext(m), ray, edge, source == 0 ? null : new[] { source }, cancellation: m.Session.CancellationToken); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { throw NscFailed(e, who); }
            NscKeep(m, result.Detectors, who);
        });
        Kw("SAVEDETECTOR", "Writes the data on a detector object (surface 1) to a DDR, DDC or DDP file (OP-79's format, Conventions §73); a name without a folder is in the lens file's folder.", (m, a) =>
        {
            a.Exactly(3, "SAVEDETECTOR");
            int number = a.Int(m, 1, "SAVEDETECTOR: the object");
            var data = NscDetector(m, a.Num(m, 0, "the surface"), number, "SAVEDETECTOR");
            string path = ZplSession.Resolve(a.FileName(m, 2, "the detector file"), NscFolder(m), "lens folder");
            try { NscDetectorFile.Write(path, data, m.Session.Unit); }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { throw NscFailed(e, "SAVEDETECTOR"); }
        });
        Kw("LOADDETECTOR", "Reads a DDR, DDC or DDP file onto a detector object (surface 1) of the same type and size, replacing its data; a name without a folder is in the lens file's folder. Detector Volume (DDV) is not built.", (m, a) =>
        {
            a.Exactly(3, "LOADDETECTOR");
            int number = a.Int(m, 1, "LOADDETECTOR: the object");
            m.Session.NscDetectors.Remove(number);
            var empty = NscDetector(m, a.Num(m, 0, "the surface"), number, "LOADDETECTOR");
            string path = ZplSession.Resolve(a.FileName(m, 2, "the detector file"), NscFolder(m), "lens folder");
            NscDetectorData read;
            try { read = NscDetectorFile.Read(path); }
            catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or NotSupportedException or FormatException)
            {
                throw NscFailed(e, "LOADDETECTOR");
            }
            m.Session.NscDetectors[number] = Fitted(empty, read, number, Path.GetFileName(path));
        });

        Fn("LOST", 1, 1, "The energy the most recent NSTR trace lost: code 1 to errors, 2 to the intensity thresholds (its ledger, source units).", (m, v) =>
        {
            int code = Int(v, 0, "the code");
            var ledger = m.Session.NscLedger ?? throw new ZplRuntimeException("no NSTR trace has run in this macro.");
            return code switch
            {
                1 => N(ledger.Errors),
                2 => N(ledger.Thresholded),
                _ => throw new ZplRuntimeException($"the code is {code}; it must be 1 (errors) or 2 (thresholds)."),
            };
        });
        Fn("NSDD", 4, 4, "Detector Rectangle data (surface 1) by the NSDD operand's pixel and data codes with # Ignored 0: pixel > 0 that pixel, 0 total, −1/−2 max/min, −3 rays, −4/−5 SD/mean of non-zero pixels, −6 to −12 centroid and RMS (Data 0 flux, 1 flux/m², 2 flux/sr); object 0 clears every detector, −n detector n, and returns 0. Refused by name: Data 3 (bitmap), 4 and 5 (objects as detectors), −13, −14 and −15.", (m, v) =>
        {
            int number = Int(v, 1, "the object");
            if (NscClear(m, Num(v, 0, "the surface"), number, "NSDD")) return N(0);
            var data = NscDetector(m, Num(v, 0, "the surface"), number, "NSDD");
            return N(NscQuery(() => NscDetectorQueries.Nsdd(data, Int(v, 2, "the pixel"), Int(v, 3, "the data"), 0, m.Session.Unit), "NSDD"));
        });
        Fn("NSDE", 6, 6, "Detector Color data (surface 1) by the NSDE operand's codes with # Ignored 0: pixel > 0, 0 (sum; chromaticity averaged over lit pixels), −1/−2, −3; angle 0 position, 1 angle space; Data 1 W, 2 W/m² or W/sr, 3 lm, 4 lm/m² or lm/sr, 5–8 CIE x, y, u′, v′, 9–11 X, Y, Z per area; object 0 or −n clears and returns 0. Data 12–21 (CRI, CCT, bitmap, spectral bins) are refused by name.", (m, v) =>
        {
            int number = Int(v, 1, "the object");
            if (NscClear(m, Num(v, 0, "the surface"), number, "NSDE")) return N(0);
            var data = NscDetector(m, Num(v, 0, "the surface"), number, "NSDE");
            return N(NscQuery(() => NscDetectorQueries.Nsde(data, Int(v, 2, "the pixel"), Int(v, 3, "the angle flag"), Int(v, 4, "the data"), 0), "NSDE"));
        });
        Fn("NSDP", 4, 4, "Detector Polar data (surface 1) by the NSDP operand's codes: pixel > 0, 0 (sum), −1/−2, −3 rays, −4 RMS angle; Data 1 W, 2 W/sr, 3 lm, 4 lm/sr, 5–8 chromaticity, 9–11 X, Y, Z per sr; object 0 or −n clears and returns 0. Pixel 0 with per-steradian data is refused (the help does not say how it sums).", (m, v) =>
        {
            int number = Int(v, 1, "the object");
            if (NscClear(m, Num(v, 0, "the surface"), number, "NSDP")) return N(0);
            var data = NscDetector(m, Num(v, 0, "the surface"), number, "NSDP");
            return N(NscQuery(() => NscDetectorQueries.Nsdp(data, Int(v, 2, "the pixel"), Int(v, 3, "the data")), "NSDP"));
        });
        Fn("NSDC", 4, 4, "Detector Rectangle coherent data (surface 1): pixel > 0 that pixel, 0 the sum; Data 0 real, 1 imaginary, 2 amplitude, 3 power (coherent irradiance × pixel area).", (m, v) =>
        {
            int number = Int(v, 1, "the object");
            var data = NscDetector(m, Num(v, 0, "the surface"), number, "NSDC");
            return N(NscQuery(() => NscDetectorQueries.Nsdc(data, Int(v, 2, "the pixel"), Int(v, 3, "the data")), "NSDC"));
        });
    }
}
