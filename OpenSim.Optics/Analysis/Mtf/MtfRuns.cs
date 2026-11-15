using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>What an MTF curve shows (help, "FFT MTF": "Select either modulation, real, imaginary,
/// phase, or square wave response").</summary>
public enum MtfDataType
{
    Modulation = 0,
    RealPart = 1,
    ImaginaryPart = 2,
    /// <summary>The phase of the OTF, degrees.</summary>
    Phase = 3,
    SquareWave = 4,
}

/// <summary>What a surface MTF shows (help, "FFT Surface MTF": "modulation, real part, or imaginary
/// part" of the incoherent OTF; the coherent transfer function is not built).</summary>
public enum MtfSurfaceType
{
    Modulation = 0,
    RealPart = 1,
    ImaginaryPart = 2,
}

/// <summary>What an MTF map shows (help, "FFT MTF Map": "Tangential, Sagittal, Average, Min, or Max
/// MTF"; "Geometric MTF Map": "Tangential, Sagittal, or average").</summary>
public enum MtfMapData
{
    Tangential = 0,
    Sagittal = 1,
    Average = 2,
    Minimum = 3,
    Maximum = 4,
}

/// <summary>The presentations the OP-39 analyses share: against frequency, through focus, over the
/// frequency plane, against field and over a field map.</summary>
internal static class MtfRuns
{
    /// <summary>Frequencies plotted against frequency: 101 evenly spaced from 0 to the maximum
    /// (this program's choice; the help does not say).</summary>
    public const int FrequencyPoints = 101;

    /// <summary>Points across a surface MTF: 129 × 129 from −cutoff to +cutoff (this program's choice).</summary>
    public const int SurfacePoints = 129;

    public static string F(double v) => StudyReport.Number(v, "g6");

    /// <summary>The wavelength rows of a setting (0: every row, polychromatic when more than one).</summary>
    public static (int[] Rows, bool Poly) Waves(int setting, OpticalSystemData system, string who)
    {
        var rows = FieldScanCommon.Rows(setting, system, who, zeroIsAll: true);
        return (rows, setting == 0 && rows.Length > 1);
    }

    public static IReadOnlyList<int> FieldRows(int setting, OpticalSystemData system, string who)
    {
        int count = system.Fields.Points.Count;
        if (setting < 0 || setting > count)
            throw new ArgumentException($"{who}: the field must be 0 (all) or 1 to {count} (it is {setting}).");
        return setting == 0 ? Enumerable.Range(0, count).ToArray() : new[] { setting - 1 };
    }

    public static void CheckSampling(int value, int minimum, int maximum, string who, string what)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentException($"{who}: the {what} must be {minimum} to {maximum} (it is {value}).");
    }

    public static void CheckFrequency(double value, string who, string what, bool zeroAllowed)
    {
        if (!double.IsFinite(value) || value < 0 || (!zeroAllowed && value == 0))
            throw new ArgumentException($"{who}: the {what} must be {(zeroAllowed ? "zero or " : "")}a positive number (it is {value}).");
    }

    public static string Wave(OpticalSystemData system, int[] rows, bool poly) => MtfCore.WaveText(system, rows, poly);

    private static string FieldName(OpticalSystemData system, int row)
    {
        var p = system.Fields.Points[row];
        string unit = FieldTypes.IsAngle(system.Fields.Type) ? "°" : " " + ParaxialReport.UnitName(system.Units.LensUnit);
        double x = FieldTypes.IsAngle(system.Fields.Type) ? p.X : OpticsConventions.FromMeters(p.X, system.Units.LensUnit);
        double y = FieldTypes.IsAngle(system.Fields.Type) ? p.Y : OpticsConventions.FromMeters(p.Y, system.Units.LensUnit);
        return $"Field {row + 1} ({F(x)}, {F(y)}{unit})";
    }

    private static List<string> Common(string method, MtfMethodSettings m, OpticalSystemData system, IReadOnlyList<string> pointNotes, List<string> notes)
    {
        var a = new List<string>(pointNotes) { MtfUnitsCheck.Note(system) };
        a.Add(method switch
        {
            "FFT" => $"FFT MTF: the autocorrelation of OP-38's pupil function on {m.Sampling} × {m.Sampling} nodes (OPD against the exit-pupil sphere on the chief ray), computed at whole-node shifts by the FFT and interpolated linearly between them; the cutoff of each axis is 2n|a|/λ = 1/(λF) with OP-38's fitted pupil scale a (help, \"FFT MTF\": the working F/# \"for the sagittal and tangential response separately\"). Tangential: frequency along pupil y; sagittal: along pupil x (help, \"FFT MTF\").",
            "Huygens" => "Huygens MTF: tangential is frequency along the image plane's y, sagittal along its x (help, \"Huygens MTF\").",
            _ => "Geometric MTF: tangential is frequency along the image surface's y, sagittal along its x (help, \"Geometric MTF\").",
        });
        a.Add("Polychromatic MTF: each wavelength's OTF weighted so the sum is the transform of OP-38's polychromatic PSF (weight × (λ_min/λ)² × that wavelength's PSF energy) for the diffraction engines, by its weight for the geometric engine; every wavelength referenced to the primary wavelength's chief ray.");
        a.Add("Not built: polarization, intermediate surfaces (the Surface setting), Use Dashes (classic view), scattered rays, the Huygens sum over configurations, the object-space cycles-per-milliradian unit of focal systems.");
        a.Add($"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.");
        a.AddRange(notes);
        return a;
    }

    private static IReadOnlyList<ReportSection> Sections(ReportSection main, IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? new[] { main } : new[] { new ReportSection("Sampling and validity warnings") { Lines = warnings }, main };

    // ------------------------------------------------------------------ Against frequency

    public static OpticsAnalysisResult Frequency(string name, string method, MtfMethodSettings m, SequentialLens lens, OpticalSystemData system,
        List<string> notes, int fieldSetting, int waveSetting, double maxFrequency, MtfDataType type, bool showDiffraction, CancellationToken cancellation)
    {
        MtfUnitsCheck.Check(system, name);
        CheckFrequency(maxFrequency, name, "max frequency", zeroAllowed: true);
        var fields = FieldRows(fieldSetting, system, name);
        var (rows, poly) = Waves(waveSetting, system, name);
        var points = new List<MtfPoint>();
        foreach (int f in fields)
        {
            cancellation.ThrowIfCancellationRequested();
            points.Add(MtfCore.Evaluate(m, lens, system, f, rows, poly, cancellation));
        }
        double top = maxFrequency > 0 ? maxFrequency * MtfEngine.PerMtfUnit : points.Max(p => p.Cutoff);
        if (!(top > 0) || !double.IsFinite(top))
            throw new InvalidOperationException($"{name}: there is no diffraction cutoff to plot up to; give a max frequency.");
        var nu = MtfCore.Frequencies(FrequencyPoints, top);
        var x = nu.Select(v => v / MtfEngine.PerMtfUnit).ToArray();
        var curves = new List<AnalysisCurve>();
        var warnings = new List<string>();
        var pairs = new List<(string, string)> { ("Frequency unit", MtfUnitsCheck.Label(system)), ("Wavelength", Wave(system, rows, poly)), ("Data", MtfCore.TypeLabel(type)) };
        for (int i = 0; i < fields.Count; i++)
        {
            var p = points[i];
            string label = FieldName(system, fields[i]);
            foreach (var o in new[] { MtfOrientation.Tangential, MtfOrientation.Sagittal })
            {
                var y = nu.Select(v => MtfCore.Value(p, v, o, type)).ToArray();
                curves.Add(new AnalysisCurve($"{label} {(o == MtfOrientation.Tangential ? "T" : "S")}", x, y, Dashed: o == MtfOrientation.Sagittal));
            }
            pairs.Add(($"{label} cutoff, T and S ({MtfUnitsCheck.Label(system)})", $"{F(p.CutoffTangential / MtfEngine.PerMtfUnit)}, {F(p.CutoffSagittal / MtfEngine.PerMtfUnit)}"));
            warnings.AddRange(p.Warnings.Select(w => $"{label}: {w}"));
            if (top > p.Nyquist)
                warnings.Add($"{label}: frequencies above {F(p.Nyquist / MtfEngine.PerMtfUnit)} {MtfUnitsCheck.Label(system)}, the Nyquist frequency of the Huygens image grid, are not resolved by it; shrink the image delta.");
        }
        var assumptions = Common(method, m, system, points[0].Notes, new List<string>());
        assumptions.Insert(0, MtfCore.TypeNote(type));
        assumptions.Add($"{FrequencyPoints} frequencies from 0 to {F(top / MtfEngine.PerMtfUnit)} {MtfUnitsCheck.Label(system)}{(maxFrequency > 0 ? "" : " (the largest diffraction cutoff)")}.");
        if (showDiffraction && type is MtfDataType.Modulation or MtfDataType.SquareWave)
        {
            var dl = MtfCore.DiffractionLimit(m, lens, system, rows, poly, cancellation);
            var t = nu.Select(v => MtfCore.Value(dl, v, MtfOrientation.Tangential, type)).ToArray();
            var s = nu.Select(v => MtfCore.Value(dl, v, MtfOrientation.Sagittal, type)).ToArray();
            if (t.Zip(s).All(z => Math.Abs(z.First - z.Second) <= 1e-12)) curves.Add(new AnalysisCurve("Diffraction limit", x, t, Dashed: true));
            else
            {
                curves.Add(new AnalysisCurve("Diffraction limit T", x, t, Dashed: true));
                curves.Add(new AnalysisCurve("Diffraction limit S", x, s, Dashed: true));
            }
            assumptions.Add($"Diffraction limit (help, \"Diffraction Limited\"): the FFT MTF of {FieldName(system, MtfCore.ReferenceField(system))} with the OPD set to zero (apodization, vignetting and F/#s kept); the reference field is the (0, 0) field when one is defined, else field 1.");
        }
        assumptions.AddRange(notes);
        assumptions.AddRange(warnings.Select(w => "Warning: " + w));
        var headers = new List<string> { $"Frequency ({MtfUnitsCheck.Label(system)})" };
        headers.AddRange(curves.Select(c => c.Name));
        var table = Enumerable.Range(0, nu.Length).Select(i =>
        {
            var row = new List<string> { F(x[i]) };
            row.AddRange(curves.Select(c => F(c.Y[i])));
            return (IReadOnlyList<string>)row;
        }).ToArray();
        var main = new ReportSection(name) { Pairs = pairs, TableHeaders = headers, TableRows = table };
        var plot = new LinePlot($"{name}, {Wave(system, rows, poly)}", $"Spatial frequency ({MtfUnitsCheck.Label(system)})", MtfCore.TypeLabel(type), curves);
        return OpticsAnalysisResult.FromSections(name, Sections(main, warnings), assumptions, new AnalysisPlot[] { plot });
    }

    // ------------------------------------------------------------------ Through focus

    public static OpticsAnalysisResult ThroughFocus(string name, string method, MtfMethodSettings m, SequentialLens lens, OpticalSystemData system,
        List<string> notes, int fieldSetting, int waveSetting, double frequency, double deltaFocus, int steps, MtfDataType type, bool showDiffraction,
        int? maxDegreeOfParallelism, CancellationToken cancellation)
    {
        MtfUnitsCheck.Check(system, name);
        if (system.Aperture.AfocalImageSpace)
            throw new InvalidOperationException($"{name}: in afocal image space the help's delta focus is a range of optical power in diopters; that form is not built. Turn afocal image space off.");
        CheckFrequency(frequency, name, "frequency", zeroAllowed: false);
        if (!double.IsFinite(deltaFocus) || !(deltaFocus > 0)) throw new ArgumentException($"{name}: the delta focus must be a positive number of lens units (it is {deltaFocus}).");
        CheckSampling(steps, 2, 100, name, "number of steps");
        var fields = FieldRows(fieldSetting, system, name);
        var (rows, poly) = Waves(waveSetting, system, name);
        var unit = system.Units.LensUnit;
        var shifts = MtfCore.Frequencies(steps, 2 * deltaFocus).Select(d => d - deltaFocus).ToArray();
        shifts[^1] = deltaFocus;
        double nu = frequency * MtfEngine.PerMtfUnit;
        int count = fields.Count * steps;
        var results = ParallelSlots.Compute(count, maxDegreeOfParallelism, k =>
        {
            int fi = k / steps, si = k % steps;
            var focused = RmsCore.Focused(lens, OpticsConventions.ToMeters(shifts[si], unit));
            try
            {
                var p = MtfCore.Evaluate(m, focused, system, fields[fi], rows, poly, cancellation);
                return (T: MtfCore.Value(p, nu, MtfOrientation.Tangential, type), S: MtfCore.Value(p, nu, MtfOrientation.Sagittal, type),
                    Warnings: p.Warnings, Failure: (string?)null, Notes: p.Notes);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                return (T: double.NaN, S: double.NaN, Warnings: (IReadOnlyList<string>)Array.Empty<string>(), Failure: (string?)$"{FieldName(system, fields[fi])}, focus {F(shifts[si])}: {e.Message}", Notes: (IReadOnlyList<string>)Array.Empty<string>());
            }
        }, cancellation);
        var curves = new List<AnalysisCurve>();
        var failures = new List<string>();
        var warnings = new List<string>();
        for (int fi = 0; fi < fields.Count; fi++)
        {
            string label = FieldName(system, fields[fi]);
            var t = new double[steps];
            var s = new double[steps];
            for (int si = 0; si < steps; si++)
            {
                var r = results[fi * steps + si];
                t[si] = r.T; s[si] = r.S;
                if (r.Failure is not null) failures.Add(r.Failure);
                warnings.AddRange(r.Warnings.Select(w => $"{label}, focus {F(shifts[si])}: {w}"));
            }
            curves.Add(new AnalysisCurve($"{label} T", shifts, t));
            curves.Add(new AnalysisCurve($"{label} S", shifts, s, Dashed: true));
        }
        var firstNotes = results.FirstOrDefault(r => r.Failure is null).Notes ?? Array.Empty<string>();
        var assumptions = Common(method, m, system, firstNotes, new List<string>());
        assumptions.Insert(0, MtfCore.TypeNote(type));
        assumptions.Add($"Focus shifts from {F(-deltaFocus)} to {F(deltaFocus)} {ParaxialReport.UnitName(unit)}, {steps} points, each added to the thickness of surface {lens.LastSurfaceIndex} (the surface before the image), as OP-40's RMS vs Focus does; the points are joined by straight lines (the help's spline and its 101-point text listing are not built).");
        assumptions.Add($"Frequency {F(frequency)} {MtfUnitsCheck.Label(system)}; wavelength {Wave(system, rows, poly)}.");
        if (showDiffraction && type is MtfDataType.Modulation or MtfDataType.SquareWave)
        {
            var dl = MtfCore.DiffractionLimit(m, lens, system, rows, poly, cancellation);
            double t = MtfCore.Value(dl, nu, MtfOrientation.Tangential, type), s = MtfCore.Value(dl, nu, MtfOrientation.Sagittal, type);
            curves.Add(new AnalysisCurve("Diffraction limit T", new[] { shifts[0], shifts[^1] }, new[] { t, t }, Dashed: true));
            if (Math.Abs(t - s) > 1e-12) curves.Add(new AnalysisCurve("Diffraction limit S", new[] { shifts[0], shifts[^1] }, new[] { s, s }, Dashed: true));
            assumptions.Add($"Diffraction limit: the FFT MTF of {FieldName(system, MtfCore.ReferenceField(system))} at this frequency with the OPD set to zero, at the nominal focus (help, \"Diffraction Limited\").");
        }
        assumptions.AddRange(notes);
        assumptions.AddRange(warnings.Select(w => "Warning: " + w));
        var headers = new List<string> { $"Focus shift ({ParaxialReport.UnitName(unit)})" };
        var plotted = curves.Where(c => c.X.Count == steps).ToList();
        headers.AddRange(plotted.Select(c => c.Name));
        var table = Enumerable.Range(0, steps).Select(i =>
        {
            var row = new List<string> { F(shifts[i]) };
            row.AddRange(plotted.Select(c => FieldScanCommon.N(c.Y[i], "g6")));
            return (IReadOnlyList<string>)row;
        }).ToArray();
        var lines = failures.Count == 0 ? new List<string> { "Every focus shift has a value." } : failures;
        var main = new ReportSection(name)
        {
            Pairs = new[] { ("Frequency", $"{F(frequency)} {MtfUnitsCheck.Label(system)}"), ("Wavelength", Wave(system, rows, poly)), ("Data", MtfCore.TypeLabel(type)) },
            TableHeaders = headers, TableRows = table, Lines = lines,
        };
        var plot = new LinePlot($"{name} at {F(frequency)} {MtfUnitsCheck.Label(system)}, {Wave(system, rows, poly)}", $"Focus shift ({ParaxialReport.UnitName(unit)})", MtfCore.TypeLabel(type), curves);
        return OpticsAnalysisResult.FromSections(name, Sections(main, warnings), assumptions, new AnalysisPlot[] { plot });
    }

    // ------------------------------------------------------------------ The frequency plane

    public static OpticsAnalysisResult Surface(string name, string method, MtfMethodSettings m, SequentialLens lens, OpticalSystemData system,
        List<string> notes, int fieldSetting, int waveSetting, MtfSurfaceType type, AnalysisMapStyle showAs, CancellationToken cancellation)
    {
        MtfUnitsCheck.Check(system, name);
        int count = system.Fields.Points.Count;
        if (fieldSetting < 1 || fieldSetting > count) throw new ArgumentException($"{name}: the field must be 1 to {count} (it is {fieldSetting}).");
        var (rows, poly) = Waves(waveSetting, system, name);
        var point = MtfCore.Evaluate(m with { KeepPlane = true }, lens, system, fieldSetting - 1, rows, poly, cancellation);
        double top = point.Cutoff;
        int g = SurfacePoints;
        var axis = MtfCore.Frequencies(g, 2 * top).Select(v => v - top).ToArray();
        axis[g / 2] = 0;
        var values = new double[g, g];
        var rowsOut = ParallelSlots.Compute(g, 1, r =>
        {
            var line = new double[g];
            for (int c = 0; c < g; c++)
            {
                var otf = point.Otf2D!(axis[c], axis[r]);
                line[c] = type switch
                {
                    MtfSurfaceType.RealPart => otf.Real,
                    MtfSurfaceType.ImaginaryPart => otf.Imaginary,
                    _ => otf.Magnitude,
                };
            }
            return line;
        }, cancellation);
        for (int r = 0; r < g; r++) for (int c = 0; c < g; c++) values[r, c] = rowsOut[r][c];
        string u = MtfUnitsCheck.Label(system);
        double shown = top / MtfEngine.PerMtfUnit;
        var plot = new AnalysisMapPlot($"{name}, {FieldName(system, fieldSetting - 1)}, {Wave(system, rows, poly)}", $"fx ({u})", $"fy ({u})",
            new GridField(values, -shown, shown, -shown, shown), type switch { MtfSurfaceType.RealPart => "real part", MtfSurfaceType.ImaginaryPart => "imaginary part", _ => "modulation" })
        { Style = showAs };
        var assumptions = Common(method, m, system, point.Notes, new List<string>());
        assumptions.Insert(0, $"The OTF over the frequency plane (fx, fy), {g} × {g} points from −{F(shown)} to {F(shown)} {u} (the largest diffraction cutoff); the regular MTF is its two axis cuts (help, \"FFT Surface MTF\"). " +
            (method == "FFT" ? "Between whole-node shifts of the pupil the autocorrelation is interpolated bilinearly." : "The Huygens PSF transformed at each point directly."));
        assumptions.Add("Not built: the 3-D surface plot with its Rotation and Scale settings, and the coherent transfer function.");
        assumptions.AddRange(notes);
        assumptions.AddRange(point.Warnings.Select(w => "Warning: " + w));
        var main = new ReportSection(name)
        {
            Pairs = new[]
            {
                ("Field", FieldName(system, fieldSetting - 1)), ("Wavelength", Wave(system, rows, poly)),
                ($"Cutoff, T and S ({u})", $"{F(point.CutoffTangential / MtfEngine.PerMtfUnit)}, {F(point.CutoffSagittal / MtfEngine.PerMtfUnit)}"),
            },
        };
        return OpticsAnalysisResult.FromSections(name, Sections(main, point.Warnings), assumptions, new AnalysisPlot[] { plot });
    }

    // ------------------------------------------------------------------ Against field

    public static OpticsAnalysisResult VsField(string name, string method, MtfMethodSettings m, SequentialLens lens, OpticalSystemData system,
        List<string> notes, IReadOnlyList<double> frequencySettings, int waveSetting, int fieldDensity, FieldScan scan, bool removeVignettingFactors,
        int? maxDegreeOfParallelism, CancellationToken cancellation)
    {
        MtfUnitsCheck.Check(system, name);
        // The help: "A maximum of 100 field points is allowed."
        CheckSampling(fieldDensity, 2, 100, name, "field density");
        foreach (var f in frequencySettings) CheckFrequency(f, name, "frequency", zeroAllowed: true);
        var frequencies = frequencySettings.Where(f => f > 0).ToArray();
        if (frequencies.Length == 0) throw new ArgumentException($"{name}: give at least one frequency above zero (frequencies 1 to 6; zero leaves one out).");
        var (rows, poly) = Waves(waveSetting, system, name);
        var setup = MtfCore.Prepare(lens, system, removeVignettingFactors, notes, cancellation);
        var (dx, dy) = FieldScans.Direction(scan);
        double extent = setup.System.Fields.Points.Max(p => p.X * dx + p.Y * dy);
        if (!(extent > 0))
            throw new ArgumentException($"{name}: no defined field lies in the {FieldScans.Label(scan)} direction, so there is nothing to scan; add a field there or pick another orientation.");
        var h = FieldScanCommon.Even(fieldDensity);
        var results = ParallelSlots.Compute(h.Length, maxDegreeOfParallelism, i =>
        {
            double fx = h[i] * extent * dx, fy = h[i] * extent * dy;
            string label = $"field ({F(setup.FieldShown(fx))}, {F(setup.FieldShown(fy))} {setup.FieldUnit})";
            try
            {
                var p = MtfCore.AtField(m, setup, fx, fy, rows, poly, 0, label, cancellation);
                var t = frequencies.Select(f => p.Otf(f * MtfEngine.PerMtfUnit, MtfOrientation.Tangential).Magnitude).ToArray();
                var s = frequencies.Select(f => p.Otf(f * MtfEngine.PerMtfUnit, MtfOrientation.Sagittal).Magnitude).ToArray();
                return (T: t, S: s, Warnings: p.Warnings.Select(w => $"{label}: {w}").ToArray(), Failure: (string?)null, Notes: p.Notes);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                var nan = frequencies.Select(_ => double.NaN).ToArray();
                return (T: nan, S: nan, Warnings: Array.Empty<string>(), Failure: (string?)$"{label}: {e.Message}", Notes: (IReadOnlyList<string>)Array.Empty<string>());
            }
        }, cancellation);
        var x = h.Select(v => setup.FieldShown(v * extent)).ToArray();
        var curves = new List<AnalysisCurve>();
        string u = MtfUnitsCheck.Label(system);
        for (int j = 0; j < frequencies.Length; j++)
        {
            curves.Add(new AnalysisCurve($"{F(frequencies[j])} {u} T", x, results.Select(r => r.T[j]).ToArray()));
            curves.Add(new AnalysisCurve($"{F(frequencies[j])} {u} S", x, results.Select(r => r.S[j]).ToArray(), Dashed: true));
        }
        var failures = results.Where(r => r.Failure is not null).Select(r => r.Failure!).ToList();
        var warnings = results.SelectMany(r => r.Warnings).ToList();
        var firstNotes = results.FirstOrDefault(r => r.Failure is null).Notes ?? Array.Empty<string>();
        var assumptions = Common(method, m, setup.System, firstNotes, new List<string>());
        assumptions.Insert(0, "Modulation against field at each frequency, tangential and sagittal.");
        assumptions.Add($"Fields from the axis to {F(setup.FieldShown(extent))} {setup.FieldUnit} along {FieldScans.Label(scan)}, the largest defined field in that direction; {h.Length} points, each appended to the field table as one more field (OP-40's rule), joined by straight lines.");
        assumptions.Add(setup.ReplacedVignetting ? "The vignetting factors were replaced by surface apertures (help: \"Remove Vignetting Factors\")." : "The vignetting factors, if any, are used at each sampled field as the ray launcher interpolates them.");
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(warnings.Select(w => "Warning: " + w));
        var headers = new List<string> { $"{FieldScans.Label(scan)} field ({setup.FieldUnit})" };
        headers.AddRange(curves.Select(c => c.Name));
        var table = Enumerable.Range(0, x.Length).Select(i =>
        {
            var row = new List<string> { F(x[i]) };
            row.AddRange(curves.Select(c => FieldScanCommon.N(c.Y[i], "g6")));
            return (IReadOnlyList<string>)row;
        }).ToArray();
        var main = new ReportSection(name)
        {
            Pairs = new[] { ("Wavelength", Wave(system, rows, poly)), ("Frequency unit", u) },
            TableHeaders = headers, TableRows = table, Lines = failures.Count == 0 ? new List<string> { "Every field has a value." } : failures,
        };
        var plot = new LinePlot($"{name}, {Wave(system, rows, poly)}", $"{FieldScans.Label(scan)} field ({setup.FieldUnit})", "Modulation", curves);
        return OpticsAnalysisResult.FromSections(name, Sections(main, warnings), assumptions, new AnalysisPlot[] { plot });
    }

    // ------------------------------------------------------------------ Field map

    public static OpticsAnalysisResult Map(string name, string method, MtfMethodSettings m, SequentialLens lens, OpticalSystemData system,
        List<string> notes, double xWidth, double yWidth, double frequency, int waveSetting, int xPixels, int yPixels, MtfMapData data,
        int referenceField, AnalysisMapStyle showAs, bool removeVignettingFactors, int? maxDegreeOfParallelism, CancellationToken cancellation)
    {
        MtfUnitsCheck.Check(system, name);
        CheckFrequency(frequency, name, "frequency", zeroAllowed: false);
        CheckSampling(xPixels, 2, 101, name, "x pixels");
        CheckSampling(yPixels, 2, 101, name, "y pixels");
        if (!double.IsFinite(xWidth) || !double.IsFinite(yWidth) || xWidth < 0 || yWidth < 0)
            throw new ArgumentException($"{name}: the field widths must be zero (automatic) or positive (they are {xWidth} and {yWidth}).");
        int count = system.Fields.Points.Count;
        if (referenceField < 0 || referenceField > count)
            throw new ArgumentException($"{name}: the reference field must be 0 (the field (0, 0)) or 1 to {count} (it is {referenceField}).");
        var (rows, poly) = Waves(waveSetting, system, name);
        var setup = MtfCore.Prepare(lens, system, removeVignettingFactors, notes, cancellation);
        double radial = setup.System.Fields.Points.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
        double wx = xWidth > 0 ? setup.FieldFromShown(xWidth) : 2 * radial;
        double wy = yWidth > 0 ? setup.FieldFromShown(yWidth) : 2 * radial;
        if (!(wx > 0) || !(wy > 0))
            throw new ArgumentException($"{name}: every defined field is on the axis, so an automatic field width is zero; give the x and y field widths.");
        double cx = referenceField == 0 ? 0 : setup.System.Fields.Points[referenceField - 1].X;
        double cy = referenceField == 0 ? 0 : setup.System.Fields.Points[referenceField - 1].Y;
        int nx = xPixels, ny = yPixels;
        // The help: "The MTF is computed at the center of the pixel".
        double X(int c) => cx - wx / 2 + wx * (c + 0.5) / nx;
        double Y(int r) => cy - wy / 2 + wy * (r + 0.5) / ny;
        double nu = frequency * MtfEngine.PerMtfUnit;
        var results = ParallelSlots.Compute(nx * ny, maxDegreeOfParallelism, k =>
        {
            int r = k / nx, c = k % nx;
            string label = $"field ({F(setup.FieldShown(X(c)))}, {F(setup.FieldShown(Y(r)))} {setup.FieldUnit})";
            try
            {
                var p = MtfCore.AtField(m, setup, X(c), Y(r), rows, poly, 0, label, cancellation);
                double t = p.Otf(nu, MtfOrientation.Tangential).Magnitude, s = p.Otf(nu, MtfOrientation.Sagittal).Magnitude;
                double v = data switch
                {
                    MtfMapData.Tangential => t,
                    MtfMapData.Sagittal => s,
                    MtfMapData.Average => (t + s) / 2,
                    MtfMapData.Minimum => Math.Min(t, s),
                    MtfMapData.Maximum => Math.Max(t, s),
                    _ => throw new ArgumentOutOfRangeException(nameof(data), data, "Unknown MTF map data."),
                };
                return (Value: v, Warnings: p.Warnings.Select(w => $"{label}: {w}").ToArray(), Failure: (string?)null, Notes: p.Notes);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                return (Value: double.NaN, Warnings: Array.Empty<string>(), Failure: (string?)$"{label}: {e.Message}", Notes: (IReadOnlyList<string>)Array.Empty<string>());
            }
        }, cancellation);
        var map = new double[ny, nx];
        var table = new List<IReadOnlyList<string>>();
        for (int r = ny - 1; r >= 0; r--)
        {
            var line = new List<string> { F(setup.FieldShown(Y(r))) };
            for (int c = 0; c < nx; c++)
            {
                map[r, c] = results[r * nx + c].Value;
                line.Add(FieldScanCommon.N(map[r, c], "g6"));
            }
            table.Add(line);
        }
        var failures = results.Where(v => v.Failure is not null).Select(v => v.Failure!).ToList();
        var warnings = results.SelectMany(v => v.Warnings).ToList();
        var finite = results.Select(v => v.Value).Where(double.IsFinite).ToArray();
        string fu = setup.FieldUnit;
        var headers = new List<string> { $"Y \\ X field ({fu})" };
        headers.AddRange(Enumerable.Range(0, nx).Select(c => F(setup.FieldShown(X(c)))));
        var pairs = new List<(string, string)>
        {
            ("Data", $"{data} modulation"), ("Frequency", $"{F(frequency)} {MtfUnitsCheck.Label(system)}"), ("Wavelength", Wave(system, rows, poly)),
            ("Reference field", referenceField == 0 ? "(0, 0)" : FieldName(setup.System, referenceField - 1)),
        };
        if (finite.Length > 0) { pairs.Add(("Minimum", F(finite.Min()))); pairs.Add(("Maximum", F(finite.Max()))); }
        var firstNotes = results.FirstOrDefault(v => v.Failure is null).Notes ?? Array.Empty<string>();
        var assumptions = Common(method, m, setup.System, firstNotes, new List<string>());
        assumptions.Insert(0, $"{data} modulation at {F(frequency)} {MtfUnitsCheck.Label(system)} over a {nx} × {ny} grid of field pixels, widths {F(setup.FieldShown(wx))} × {F(setup.FieldShown(wy))} {fu}{(xWidth > 0 && yWidth > 0 ? "" : " (an automatic width is twice the largest radial defined field)")}, centred on {(referenceField == 0 ? "the field (0, 0)" : $"field {referenceField}")}; each pixel's value is computed at its centre (help, \"FFT MTF Map\") at a field appended to the field table (OP-40's rule).");
        assumptions.Add(setup.ReplacedVignetting ? "The vignetting factors were replaced by surface apertures (help: \"Remove Vignetting Factors\")." : "The vignetting factors, if any, are used at each sampled field as the ray launcher interpolates them.");
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(warnings.Select(w => "Warning: " + w));
        var plots = new List<AnalysisPlot>();
        if (finite.Length > 0)
            plots.Add(new AnalysisMapPlot($"{name}, {data}, {F(frequency)} {MtfUnitsCheck.Label(system)}", $"X field ({fu})", $"Y field ({fu})",
                new GridField(map, setup.FieldShown(X(0)), setup.FieldShown(X(nx - 1)), setup.FieldShown(Y(0)), setup.FieldShown(Y(ny - 1))), "modulation")
            { Style = showAs, Minimum = 0, Maximum = 1 });
        var main = new ReportSection(name)
        {
            Pairs = pairs, TableHeaders = headers, TableRows = table, Lines = failures.Count == 0 ? new List<string> { "Every pixel has a value." } : failures,
        };
        return OpticsAnalysisResult.FromSections(name, Sections(main, warnings), assumptions, plots);
    }
}
