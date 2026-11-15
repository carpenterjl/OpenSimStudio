using System.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis;

/// <summary>How one of the three engines is run (OP-39).</summary>
internal sealed record MtfMethodSettings
{
    public MtfMethod Method { get; init; }
    /// <summary>Pupil nodes per side (the Huygens pupil sampling).</summary>
    public int Sampling { get; init; } = 64;
    public int ImageSampling { get; init; } = 64;
    /// <summary>Huygens image spacing, meters; 0 for the help's default.</summary>
    public double ImageDelta { get; init; }
    public bool MultiplyByDiffractionLimit { get; init; } = true;
    public bool CheckApertures { get; init; } = true;
    public bool AllowOpdLimitations { get; init; }
    /// <summary>FFT: keep the whole lag plane (surface MTF).</summary>
    public bool KeepPlane { get; init; }
}

/// <summary>The OTF of one field, wavelength set and focus by one engine, ready to evaluate at
/// any frequency (cycles per meter; afocal, per radian).</summary>
internal sealed class MtfPoint
{
    public required Func<double, MtfOrientation, Complex> Otf { get; init; }
    /// <summary>The OTF at (fx, fy), when the engine kept the plane (FFT) or can sum it (Huygens).</summary>
    public Func<double, double, Complex>? Otf2D { get; init; }
    /// <summary>The largest diffraction cutoff along y (tangential) and x (sagittal) over the wavelengths.</summary>
    public required double CutoffTangential { get; init; }
    public required double CutoffSagittal { get; init; }
    public double Cutoff => Math.Max(CutoffTangential, CutoffSagittal);
    /// <summary>Huygens: the image grid's Nyquist frequency 1/(2Δ); infinity otherwise.</summary>
    public double Nyquist { get; init; } = double.PositiveInfinity;
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>The MTF units (help, "ZemaxMTFUnits MTFUnits": "MTF units for focal systems may be
/// cycles per millimeter or cycles per milliradian"; "Afocal Image Space": in afocal mode "MTF is
/// measured in cycles per angle", milliradians by default).</summary>
internal static class MtfUnitsCheck
{
    public static void Check(OpticalSystemData system, string who)
    {
        if (!system.Aperture.AfocalImageSpace && system.Units.MtfUnit == MtfUnit.CyclesPerMilliradian)
            throw new InvalidOperationException($"{who}: the MTF unit is cycles per milliradian, which for a focal system means object-space frequency (help, \"FFT MTF\"); that conversion is not built. Set the MTF unit to cycles per millimeter.");
    }

    public static string Label(OpticalSystemData system) => system.Aperture.AfocalImageSpace ? "cycles/mrad" : "cycles/mm";

    public static string Note(OpticalSystemData system) => system.Aperture.AfocalImageSpace
        ? "Afocal image space: spatial frequency in cycles per milliradian of image-space angle (the help's default afocal unit; other afocal angle units are not built)."
        : "Spatial frequency in cycles per millimeter on the image surface (help, \"FFT MTF\": \"MTF in cycles per mm is computed in image space\").";
}

/// <summary>The engines run on the lens: one point, the diffraction limit, presentation helpers.</summary>
internal static class MtfCore
{
    /// <summary>The OTF of one field row and wavelength rows (several and <paramref name="poly"/>:
    /// polychromatic) by the chosen engine.</summary>
    public static MtfPoint Evaluate(MtfMethodSettings m, SequentialLens lens, OpticalSystemData system, int field, int[] rows, bool poly,
        CancellationToken cancellation) => m.Method switch
    {
        MtfMethod.Fft => Fft(m, lens, system, field, rows, poly, unaberrated: false, cancellation),
        MtfMethod.Huygens => Huygens(m, lens, system, field, rows, poly, cancellation),
        MtfMethod.Geometric => Geometric(m, lens, system, field, rows, poly, cancellation),
        _ => throw new ArgumentOutOfRangeException(nameof(m), m.Method, "Unknown MTF method."),
    };

    private static string Label(bool poly, double lambda) => poly ? $"{StudyReport.Number(lambda * 1e6, "g6")} µm: " : "";

    public static MtfPoint Fft(MtfMethodSettings m, SequentialLens lens, OpticalSystemData system, int field, int[] rows, bool poly, bool unaberrated,
        CancellationToken cancellation)
    {
        var options = PsfRuns.Options(poly, m.CheckApertures, m.AllowOpdLimitations || unaberrated);
        var pupils = new List<MtfPupil>();
        foreach (int r in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            pupils.Add(MtfEngine.BuildPupil(lens, system, field, r, m.Sampling, options, unaberrated, m.KeepPlane, cancellation));
        }
        var weights = rows.Select(r => system.Wavelengths.Wavelengths[r].Weight).ToArray();
        var warnings = new List<string>();
        foreach (var p in pupils) warnings.AddRange(p.Warnings.Select(w => Label(poly, p.Wavelength) + w));
        if (pupils.Count == 1)
        {
            var p = pupils[0];
            return new MtfPoint
            {
                Otf = p.Otf, Otf2D = m.KeepPlane ? new Func<double, double, Complex>(p.Otf) : null, CutoffTangential = p.CutoffY, CutoffSagittal = p.CutoffX,
                Notes = p.Notes, Warnings = warnings,
            };
        }
        var e = MtfEngine.EnergyWeights(pupils, weights);
        double total = e.Sum();
        Complex Otf2(double fx, double fy)
        {
            Complex s = 0;
            for (int i = 0; i < pupils.Count; i++) if (e[i] > 0) s += e[i] * pupils[i].Otf(fx, fy);
            return s / total;
        }
        return new MtfPoint
        {
            Otf = (f, o) => MtfEngine.PolychromaticOtf(pupils, weights, f, o),
            Otf2D = m.KeepPlane ? new Func<double, double, Complex>(Otf2) : null,
            CutoffTangential = pupils.Max(p => p.CutoffY), CutoffSagittal = pupils.Max(p => p.CutoffX),
            Notes = pupils[0].Notes, Warnings = warnings,
        };
    }

    public static MtfPoint Huygens(MtfMethodSettings m, SequentialLens lens, OpticalSystemData system, int field, int[] rows, bool poly,
        CancellationToken cancellation)
    {
        var options = PsfRuns.Options(false, m.CheckApertures, m.AllowOpdLimitations);
        int centreWave = poly && rows.Contains(system.Wavelengths.PrimaryIndex) ? system.Wavelengths.PrimaryIndex : rows[0];
        var centre = PsfEngine.BuildHuygens(lens, system, field, centreWave, m.Sampling, options, null, false, cancellation);
        var pupils = new List<HuygensPupil>();
        foreach (int r in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            pupils.Add(r == centreWave ? centre : PsfEngine.BuildHuygens(lens, system, field, r, m.Sampling, options, centre.Centre, false, cancellation));
        }
        double longest = pupils.Max(p => p.Wavelength);
        double delta = m.ImageDelta > 0 ? m.ImageDelta : PsfEngine.DefaultHuygensDelta(poly ? longest : pupils[0].Wavelength, centre.FNumber, m.Sampling);
        var (xs, ys) = PsfEngine.Grid(m.ImageSampling, delta, delta);
        var weights = rows.Select(r => system.Wavelengths.Wavelengths[r].Weight).ToArray();
        var lambdas = pupils.Select(p => p.Wavelength).ToArray();
        var image = pupils.Count > 1
            ? PsfEngine.Combine(lambdas, weights, i => PsfEngine.Huygens(pupils[i], xs, ys, 1, cancellation), xs, ys)
            : PsfEngine.Huygens(pupils[0], xs, ys, 1, cancellation);
        var warnings = new List<string>();
        foreach (var p in pupils)
        {
            warnings.AddRange(PsfEngine.HuygensWarnings(p, xs, ys).Select(w => Label(poly, p.Wavelength) + w));
            warnings.AddRange(p.Warnings.Select(w => Label(poly, p.Wavelength) + w));
        }
        // The image edge: light there means the PSF does not fit the grid and the MTF is wrong at low frequency.
        double edge = 0, peak = image.MaximumSample;
        int last = m.ImageSampling - 1;
        for (int i = 0; i <= last; i++)
            edge = Math.Max(edge, Math.Max(Math.Max(image.Intensity[0, i], image.Intensity[last, i]), Math.Max(image.Intensity[i, 0], image.Intensity[i, last])));
        if (peak > 0 && edge > 1e-3 * peak)
            warnings.Add($"The Huygens PSF reaches the edge of its {m.ImageSampling} × {m.ImageSampling} grid ({StudyReport.Number(edge / peak, "g3")} of its peak there), so light outside the grid is missing from the MTF; raise the image sampling or the image delta.");
        var notes = new List<string>(centre.Notes)
        {
            $"Huygens PSF on {m.ImageSampling} × {m.ImageSampling} points at {StudyReport.Number(delta * 1e6, "g6")} µm{(m.ImageDelta > 0 ? " (chosen)" : $" (the help's default λF/√n_p, F = {centre.FNumber:g5} from the fitted pupil scale)")}; the MTF is its transform Σ I·e^{{−2πiν·y}} / Σ I (help, \"Huygens MTF\": \"an FFT of the Huygens PSF\"), evaluated at each frequency directly. Meaningful below the grid's Nyquist frequency 1/(2Δ) = {StudyReport.Number(1 / (2 * delta) / MtfEngine.PerMtfUnit, "g6")} cycles/mm.",
        };
        return new MtfPoint
        {
            Otf = (f, o) => MtfEngine.PsfOtf(image, f, o),
            Otf2D = (fx, fy) => MtfEngine.PsfOtf(image, fx, fy),
            CutoffTangential = pupils.Max(p => 2 * p.ImageIndex * Math.Abs(p.ScaleY) / p.Wavelength),
            CutoffSagittal = pupils.Max(p => 2 * p.ImageIndex * Math.Abs(p.ScaleX) / p.Wavelength),
            Nyquist = 1 / (2 * delta), Notes = notes, Warnings = warnings,
        };
    }

    public static MtfPoint Geometric(MtfMethodSettings m, SequentialLens lens, OpticalSystemData system, int field, int[] rows, bool poly,
        CancellationToken cancellation)
    {
        var spots = new List<GeometricSpot>();
        foreach (int r in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            spots.Add(MtfEngine.BuildGeometric(lens, system, field, r, m.Sampling, m.CheckApertures, cancellation));
        }
        var weights = rows.Select(r => system.Wavelengths.Wavelengths[r].Weight).ToArray();
        if (spots.Count > 1 && !(weights.Sum() > 0)) throw new ArgumentException("Geometric MTF: every wavelength weight is zero.");
        var warnings = new List<string>();
        foreach (var s in spots) warnings.AddRange(s.Warnings.Select(w => Label(poly, s.Wavelength) + w));
        if (m.MultiplyByDiffractionLimit)
            for (int i = 0; i < spots.Count; i++)
                if (spots[i].Diffraction is null)
                    throw new InvalidOperationException($"Geometric MTF (field {field + 1}, wavelength {rows[i] + 1}): the image-space ray directions do not change across the pupil, so there is no diffraction limit to multiply by; turn \"Multiply by diffraction limit\" off.");
        double x0 = spots[0].ChiefX, y0 = spots[0].ChiefY;
        Complex One(int i, double f, MtfOrientation o)
        {
            var s = spots[i];
            var g = MtfEngine.GeometricOtf(s.Rays, f, o);
            // The rays are relative to their own chief ray; bring every wavelength to the first's.
            double shift = o == MtfOrientation.Tangential ? s.ChiefY - y0 : s.ChiefX - x0;
            if (shift != 0) g *= PsfEngine.Turn(-f * shift);
            if (m.MultiplyByDiffractionLimit) g *= s.Diffraction!.Otf(f, o).Magnitude;
            return g;
        }
        Complex Combined(double f, MtfOrientation o)
        {
            if (spots.Count == 1) return One(0, f, o);
            Complex sum = 0;
            double total = 0;
            for (int i = 0; i < spots.Count; i++)
            {
                if (weights[i] == 0) continue;
                sum += weights[i] * One(i, f, o);
                total += weights[i];
            }
            return sum / total;
        }
        var notes = new List<string>(spots[0].Notes)
        {
            "Geometric MTF: Σ w·e^{−2πiν·y} / Σ w over the rays (x for sagittal), the transform of the rays' line spread function, evaluated exactly for the ray set (no binning).",
            m.MultiplyByDiffractionLimit
                ? "Multiplied by the diffraction-limited MTF of the same field's pupil (the same amplitudes and vignetting, no OPD, OP-38's fitted pupil scale), wavelength by wavelength (help: \"Should always be used\")."
                : "Not multiplied by the diffraction limit: the pure geometric approximation, which does not fall to zero at the diffraction cutoff.",
        };
        if (spots.Count > 1) notes.Add("Polychromatic: each wavelength's OTF weighted by its weight, every wavelength's rays measured from the first wavelength's chief ray so lateral colour shows.");
        double ct = spots.Max(s => s.CutoffY), cs = spots.Max(s => s.CutoffX);
        return new MtfPoint
        {
            Otf = Combined, CutoffTangential = double.IsFinite(ct) ? ct : double.NaN, CutoffSagittal = double.IsFinite(cs) ? cs : double.NaN,
            Notes = notes, Warnings = warnings,
        };
    }

    /// <summary>
    /// The diffraction limit (help, "Diffraction Limited"): the MTF with "the optical path
    /// difference … set to zero regardless of the actual (aberrated) optical path", at the reference
    /// field: "the axial field point" when a field at (0, 0) is defined, "If no (0, 0) field point
    /// is defined, then the field coordinates of field position 1". Computed by the FFT engine on
    /// the unaberrated pupil (apodization, vignetting and F/#s kept).
    /// </summary>
    public static MtfPoint DiffractionLimit(MtfMethodSettings m, SequentialLens lens, OpticalSystemData system, int[] rows, bool poly,
        CancellationToken cancellation)
    {
        int reference = ReferenceField(system);
        var fft = m with { Method = MtfMethod.Fft, KeepPlane = false };
        return Fft(fft, lens, system, reference, rows, poly, unaberrated: true, cancellation);
    }

    /// <summary>The help's reference field: the first field at (0, 0), else field 1 (0-based).</summary>
    public static int ReferenceField(OpticalSystemData system)
    {
        var points = system.Fields.Points;
        for (int i = 0; i < points.Count; i++) if (points[i].X == 0 && points[i].Y == 0) return i;
        return 0;
    }

    /// <summary>The value a type shows: modulation, real, imaginary part, or phase in degrees.</summary>
    public static double Shown(Complex otf, MtfDataType type) => type switch
    {
        MtfDataType.Modulation or MtfDataType.SquareWave => otf.Magnitude,
        MtfDataType.RealPart => otf.Real,
        MtfDataType.ImaginaryPart => otf.Imaginary,
        MtfDataType.Phase => otf.Magnitude == 0 ? 0 : otf.Phase * 180 / Math.PI,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown MTF data type."),
    };

    /// <summary>A point's value of a type at one frequency (per meter), the square wave by Coltman's series.</summary>
    public static double Value(MtfPoint point, double frequency, MtfOrientation o, MtfDataType type)
    {
        if (type != MtfDataType.SquareWave) return Shown(point.Otf(frequency, o), type);
        double cutoff = o == MtfOrientation.Tangential ? point.CutoffTangential : point.CutoffSagittal;
        return MtfEngine.SquareWave(f => point.Otf(f, o).Magnitude, frequency, cutoff);
    }

    public static string TypeLabel(MtfDataType type) => type switch
    {
        MtfDataType.Modulation => "Modulation",
        MtfDataType.RealPart => "Real part of the OTF",
        MtfDataType.ImaginaryPart => "Imaginary part of the OTF",
        MtfDataType.Phase => "Phase of the OTF (degrees)",
        MtfDataType.SquareWave => "Square-wave modulation",
        _ => type.ToString(),
    };

    public static string TypeNote(MtfDataType type) => type switch
    {
        MtfDataType.SquareWave => "Square wave (help, \"FFT MTF\"): S(ν) = (4/π)[M(ν) − M(3ν)/3 + M(5ν)/5 − …], M the sine-wave modulation, summed over the odd harmonics below the cutoff (Coltman's series; derived in MtfEngine.SquareWave).",
        MtfDataType.Phase => "Phase of the OTF in degrees, against the chief ray (the OPD's reference point); 0 where the modulation is 0.",
        MtfDataType.RealPart or MtfDataType.ImaginaryPart => "Real and imaginary parts of the OTF, the phase origin at the chief ray (the OPD's reference point).",
        _ => "Modulation: |OTF|, the response to a sine-wave target.",
    };

    // ------------------------------------------------------------------ Arbitrary fields

    /// <summary>The lens and system for fields between the table's: RmsCore's preparation (the
    /// vignetting factors replaced by apertures when asked, floating semi-diameters frozen), here
    /// without refusing afocal image space.</summary>
    public static RmsSetup Prepare(SequentialLens lens, OpticalSystemData system, bool removeVignettingFactors, List<string> notes,
        CancellationToken cancellation)
    {
        bool replaced = false;
        if (removeVignettingFactors && system.Fields.Points.Any(p => p.IsVignetted))
        {
            var r = VignettingTools.ReplaceWithApertures(lens, system, cancellation: cancellation);
            lens = r.Lens;
            system = r.System;
            replaced = true;
            notes.AddRange(r.Notes);
        }
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
        {
            var clear = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
            int stop = lens.StopIndex;
            lens = lens with
            {
                Surfaces = lens.Surfaces.Select((s, j) => s.Aperture is FloatingAperture && s.SemiDiameter is null && j != stop
                    ? s with { SemiDiameter = clear[j] } : s).ToArray(),
            };
            notes.Add("Floating apertures keep the semi-diameters sized for the system's own fields and wavelengths; the sampled fields do not resize them.");
        }
        return new RmsSetup
        {
            Lens = lens, System = system, Launcher = new RayLauncher(lens, system), Notes = notes, ReplacedVignetting = replaced,
        };
    }

    /// <summary>The OTF at a field position between the table's (appended as one more field
    /// point, RmsCore.Extended) and a focus shift (meters).</summary>
    public static MtfPoint AtField(MtfMethodSettings m, RmsSetup setup, double fieldX, double fieldY, int[] rows, bool poly, double focus,
        string label, CancellationToken cancellation)
    {
        var point = new RmsPoint { FieldX = fieldX, FieldY = fieldY, Rows = rows, Polychromatic = poly, Focus = focus, Label = label };
        var (system, field, waveRows) = RmsCore.Extended(setup, point);
        var lens = RmsCore.Focused(setup.Lens, focus);
        return Evaluate(m, lens, system, field, waveRows, poly, cancellation);
    }

    /// <summary>n evenly spaced frequencies from 0 to <paramref name="top"/>.</summary>
    public static double[] Frequencies(int n, double top)
    {
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = i == n - 1 ? top : top * i / (n - 1);
        return v;
    }

    public static string WaveText(OpticalSystemData system, int[] rows, bool poly) =>
        poly ? "polychromatic" : $"{StudyReport.Number(system.Wavelengths.Wavelengths[rows[0]].Micrometers, "g6")} µm";
}
