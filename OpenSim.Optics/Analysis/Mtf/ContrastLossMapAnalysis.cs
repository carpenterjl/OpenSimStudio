using System.ComponentModel;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="ContrastLossMapAnalysis"/> (help, "Contrast Loss Map").</summary>
public sealed record ContrastLossMapSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil points from −1 to 1; 5 to 129.")]
    public int Sampling { get; init; } = 17;

    [DisplayName("Frequency"), Description("The spatial frequency, in MTF units; 0 for 5 % of the cutoff of the chosen field and wavelength.")]
    public double Frequency { get; init; }

    [DisplayName("Normalize"), Description("Scale the colour bar to the largest contrast loss in the map; off, 0 to 1.")]
    public bool Normalize { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>
/// OpticStudio's Contrast Loss Map (OP-39, left out of OP-36 because it needs the MTF's cutoff):
/// the Moore-Elliott contrast loss over the pupil. Help, "Contrast Loss Map": for each pupil point
/// two rays at (Px ± δ/2, Py) (sagittal) or (Px, Py ± δ/2) (tangential), and
/// "Contrast Loss = 0.5(1 − cos[φ₁(Px + δ/2, Py) − φ₂(Px − δ/2, Py)])" (its equation image, saved
/// under plans/sources/optics/op-36-help/images/eq_Contrast_Loss_Map_1.svg), φ the OPD against the
/// chief ray as a phase. δ comes from "the chosen Frequency setting and the cutoff frequency"
/// ("Optimizing for MTF"). Here δ is the pupil shift at which the autocorrelation gives the MTF at
/// that frequency, ν/c = λν/(n|a|) in normalised pupil units on each axis (derived in
/// <see cref="MtfPupil"/>; 2ν/ν_c for a circular pupil): the help's own two statements of δ disagree
/// (see docs/Optics-Conventions.md §10, item 34). A point whose pair reaches outside the unit circle,
/// or whose rays fail, has no value. The MTF and the diffraction-limited MTF at the frequency are
/// reported "in the same way as in the FFT MTF analysis, using pupil sampling 128 x 128".
/// </summary>
public sealed class ContrastLossMapAnalysis : MtfAnalysisBase<ContrastLossMapSettings>
{
    public const string SavedId = "ContrastLossMap";

    /// <summary>The MTF's pupil sampling (help: "using pupil sampling 128 x 128").</summary>
    public const int MtfSampling = 128;

    public ContrastLossMapAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Contrast loss map";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes, ContrastLossMapSettings s, CancellationToken cancellation)
    {
        MtfRuns.CheckSampling(s.Sampling, 5, 129, Name, "sampling");
        MtfRuns.CheckFrequency(s.Frequency, Name, "frequency", zeroAllowed: true);
        MtfUnitsCheck.Check(system, Name);
        if (system.Aperture.AfocalImageSpace)
            throw new InvalidOperationException($"{Name}: afocal image space is not built for the contrast loss map (its pupil shift needs a focal working F/#). Turn afocal image space off.");
        int f = Rows(s.Field, system.Fields.Points.Count, false, "field")[0];
        int w = WavelengthRow(s.Wavelength, system);
        var options = PsfRuns.Options(false, s.CheckApertures, s.AllowOpdLimitations);

        // The pupil scale (and the MTF) from OP-38's pupil function at the help's 128 × 128.
        var pupil = PsfEngine.BuildPupil(lens, system, f, w, MtfSampling, options, cancellation);
        double lambda = pupil.Wavelength, n = pupil.ImageIndex;
        double cutoffX = 2 * n * Math.Abs(pupil.ScaleX) / lambda, cutoffY = 2 * n * Math.Abs(pupil.ScaleY) / lambda;
        double cutoff = 2 * n * Math.Sqrt(Math.Abs(pupil.ScaleX * pupil.ScaleY)) / lambda;
        double nu = s.Frequency > 0 ? s.Frequency * MtfEngine.PerMtfUnit : 0.05 * cutoff;
        double dx = lambda * nu / (n * Math.Abs(pupil.ScaleX)), dy = lambda * nu / (n * Math.Abs(pupil.ScaleY));

        int g = s.Sampling;
        var points = new List<(double Px, double Py)>();
        var slots = new int[g, g, 4];
        for (int i = 0; i < g; i++)
            for (int k = 0; k < g; k++)
            {
                double px = OpdEngine.Node(k, g), py = OpdEngine.Node(i, g);
                var pair = new[] { (px + dx / 2, py), (px - dx / 2, py), (px, py + dy / 2), (px, py - dy / 2) };
                for (int j = 0; j < 4; j++)
                {
                    var (x, y) = pair[j];
                    if (px * px + py * py > 1 || x * x + y * y > 1) { slots[i, k, j] = -1; continue; }
                    slots[i, k, j] = points.Count;
                    points.Add((x, y));
                }
            }
        var opd = points.Count > 0 ? OpdEngine.Compute(lens, system, f, w, points, options, cancellation) : null;
        cancellation.ThrowIfCancellationRequested();
        var sag = new double[g, g];
        var tan = new double[g, g];
        int failed = 0;
        double Loss(int i, int k, int a, int b)
        {
            int ja = slots[i, k, a], jb = slots[i, k, b];
            if (ja < 0 || jb < 0) return double.NaN;
            var sa = opd!.Samples[ja];
            var sb = opd.Samples[jb];
            if (!sa.IsValid || !sb.IsValid) { failed++; return double.NaN; }
            return 0.5 * (1 - Math.Cos(2 * Math.PI * (sa.Waves - sb.Waves)));
        }
        for (int i = 0; i < g; i++)
            for (int k = 0; k < g; k++)
            {
                sag[i, k] = Loss(i, k, 0, 1);
                tan[i, k] = Loss(i, k, 2, 3);
            }
        double Max(double[,] m) { double v = 0; foreach (var x in m) if (double.IsFinite(x) && x > v) v = x; return v; }
        double maxT = Max(tan), maxS = Max(sag);

        // The MTF at the frequency, as the FFT MTF computes it, and its diffraction limit.
        var pairs = new List<(string, string)>
        {
            ("Field", $"{f + 1}"), ("Wavelength", $"{StudyReport.Number(system.Wavelengths.Wavelengths[w].Micrometers, "g6")} µm"),
            ("Frequency", $"{StudyReport.Number(nu / MtfEngine.PerMtfUnit, "g6")} cycles/mm{(s.Frequency > 0 ? "" : " (5 % of the cutoff)")}"),
            ("Cutoff, T and S (cycles/mm)", $"{StudyReport.Number(cutoffY / MtfEngine.PerMtfUnit, "g6")}, {StudyReport.Number(cutoffX / MtfEngine.PerMtfUnit, "g6")}"),
            ("Pupil shift δ, T and S (normalised pupil)", $"{StudyReport.Number(dy, "g6")}, {StudyReport.Number(dx, "g6")}"),
            ("Largest contrast loss, T and S", $"{StudyReport.Number(maxT, "g6")}, {StudyReport.Number(maxS, "g6")}"),
        };
        var warnings = new List<string>(PsfEngine.CheckSampling(pupil).Warnings.Concat(pupil.Warnings));
        if (warnings.Any(x => x.StartsWith("ALIASED", StringComparison.Ordinal)))
            pairs.Add(("MTF", $"Cannot compute MTF: the wavefront is too steep for a {MtfSampling} × {MtfSampling} pupil (help, \"Contrast Loss Map\")."));
        else
        {
            var mtf = MtfPupil.FromPupil(pupil);
            var dl = MtfPupil.FromPupil(pupil.Unaberrated());
            pairs.Add(("MTF, T and S", $"{StudyReport.Number(mtf.Otf(nu, MtfOrientation.Tangential).Magnitude, "g6")}, {StudyReport.Number(mtf.Otf(nu, MtfOrientation.Sagittal).Magnitude, "g6")}"));
            pairs.Add(("Diffraction-limited MTF, T and S", $"{StudyReport.Number(dl.Otf(nu, MtfOrientation.Tangential).Magnitude, "g6")}, {StudyReport.Number(dl.Otf(nu, MtfOrientation.Sagittal).Magnitude, "g6")}"));
        }
        if (failed > 0) warnings.Add($"{failed} pupil points have a ray that gives no OPD (it failed or missed the reference sphere); they are blank.");

        AnalysisMapPlot Plot(string title, double[,] values, double max) =>
            new(title, "Px", "Py", new GridField(values, -1, 1, -1, 1), "contrast loss")
            { Minimum = 0, Maximum = s.Normalize && max > 0 ? max : 1 };
        var plots = new AnalysisPlot[]
        {
            Plot($"Tangential contrast loss, field {f + 1}", tan, maxT),
            Plot($"Sagittal contrast loss, field {f + 1}", sag, maxS),
        };
        var assumptions = new List<string>
        {
            "Contrast loss 0.5(1 − cos 2π(W₁ − W₂)) of each pupil point, W₁ and W₂ the OPD in waves (against the exit-pupil sphere on the chief ray) of the rays at (Px + δ/2, Py) and (Px − δ/2, Py) (sagittal) or (Px, Py ± δ/2) (tangential) (help, \"Contrast Loss Map\").",
            $"δ = λν/(n|a|) on each axis, the pupil shift at which the pupil autocorrelation is the MTF at frequency ν (a OP-38's fitted pupil scale; 2ν/ν_c for a circular pupil). The help's two statements of δ disagree; this is the one the autocorrelation needs (docs/Optics-Conventions.md §10, item 34).",
            s.Frequency > 0 ? "Frequency as set." : $"Frequency 5 % of the cutoff 2n√|a_x·a_y|/λ of the chosen field and wavelength (help: \"When zero, the frequency will be set to 5% of the cutoff frequency\").",
            "A point whose pair of rays reaches outside the unit circle has no value (help: \"the data on that point can't be calculated\").",
            $"The MTF and diffraction-limited MTF at the frequency are the FFT MTF's on a {MtfSampling} × {MtfSampling} pupil (help).",
            "Drawn as two false-colour maps of the contrast loss, not as the help's circles; Show OPD (the orientation indicator) is not built.",
        };
        assumptions.AddRange(warnings.Select(x => "Warning: " + x));
        assumptions.AddRange(notes);
        var main = new ReportSection(Name) { Pairs = pairs };
        var sections = warnings.Count == 0 ? new[] { main } : new[] { new ReportSection("Sampling and validity warnings") { Lines = warnings }, main };
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}
