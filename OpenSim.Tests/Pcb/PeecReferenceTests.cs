using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Extraction;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// FU-13: the filament (PEEC) extractor against the references the owner chose — FastHenry 3.0.1
/// (M.I.T.'s; built here from the FastHenry2 master source with MSVC, unpatched numerics, reproducing
/// the distribution's pin-con7 example output to every printed digit with FastHenry's default
/// options), a 2D finite-element solve of the isolated rectangle in MATLAB R2025b's PDE Toolbox
/// (converged to 2.3e-6, checked against the Bessel wire and an independent integral-equation solve),
/// and Haefner's measured AC resistance of rectangular copper bars (Proc. IRE 25(4), 1937, Figs. 7–8,
/// digitised and re-read independently). The decks, outputs, scripts and digitised curves are in
/// OpenSim.Tests/References/FU-13.
/// </summary>
public class PeecReferenceTests
{
    private const double Mu0 = 4e-7 * Math.PI;
    private const double Sigma = 5.8e7;
    private readonly ITestOutputHelper _output;
    public PeecReferenceTests(ITestOutputHelper output) => _output = output;

    /// <summary>R_ac/R_dc of a straight bar 500 widths long (the 2D section's limit: at 20 widths the
    /// partial resistance reads about 1.4 % low, at 100 widths within 0.3 % of 500).</summary>
    private static double BarRatio(double w, double t, double f, int across, int through)
    {
        double l = 500 * w;
        var model = new PeecModel { Solver = PeecSolver.Dense };
        int a = model.AddNode(new Vector3D(0, 0, 0)), b = model.AddNode(new Vector3D(l, 0, 0));
        model.AddBar(a, b, w, t, Sigma, acrossWidth: across, acrossThickness: through);
        model.AddPort("bar", a, b);
        return model.Solve(new[] { f })[0].Resistance(0, 0) / (l / (Sigma * w * t));
    }

    [Theory]
    [InlineData(1e-3, 35e-6, 1, 1.3696283018)]
    [InlineData(1e-3, 35e-6, 4, 3.3187151983)]
    [InlineData(0.2e-3, 35e-6, 2, 1.4326883850)]
    [InlineData(0.2e-3, 35e-6, 8, 4.9040344097)]
    [InlineData(1e-3, 0.25e-3, 2, 1.3332974672)]
    [InlineData(1e-3, 0.25e-3, 8, 4.4496368732)]
    [InlineData(1e-3, 1e-3, 2, 1.0381337145)]
    [InlineData(1e-3, 1e-3, 4, 1.4325255725)]
    [InlineData(1e-3, 1e-3, 8, 2.6699307793)]
    public void AnIsolatedBar_HasTheFiniteElementAcResistance(double w, double t, double thicknessOverSkinDepth, double fem)
    {
        // Copper 5.8e7 S/m, PCB strips (1 mm and 0.2 mm × 35 µm) and thicker bars, against MATLAB's
        // converged 2D R_ac/R_dc of the isolated rectangle (fem is the Rac_over_Rdc_finest column of
        // References/FU-13/matlab-strip/strip_rac_summary.csv; its uncertainty_abs is at most 1.2e-5 on
        // these rows). Filaments 21 × 9 (21 × 21 for the square), graded by PeecModel's fixed ratio of 2
        // toward the faces. Measured: +0.34 / −0.00 % (1 mm strip, t/δ 1 / 4), +0.17 / +1.03 % (0.2 mm,
        // 2 / 8), +0.08 / +0.97 % (0.25 mm bar, 2 / 8), −0.18 / −0.40 / +0.98 % (square, 2 / 4 / 8). More
        // filaments (31 × 13, 41 × 17) move these by under 0.03 %:
        // with a ratio of 2 a strip at depth d is about d wide whatever the count, so the cells at a
        // skin depth's distance stay about a skin depth wide and the remaining +1 % at t/δ = 8 is the
        // grading's, not the count's.
        double delta = t / thicknessOverSkinDepth, f = 1 / (Math.PI * Mu0 * Sigma * delta * delta);
        double ratio = BarRatio(w, t, f, 21, w == t ? 21 : 9);
        _output.WriteLine($"{w * 1e3} × {t * 1e3} mm at t/δ {thicknessOverSkinDepth}: R/Rdc {ratio:f6} against the FEM's {fem:f6} ({(ratio / fem - 1) * 100:f3} %)");
        Assert.Equal(1, ratio / fem, thicknessOverSkinDepth >= 8 ? 0.016 : 0.006);
    }

    [Theory]
    [InlineData(1, 4.0, 1.7561, 1.755404287)]
    [InlineData(1, 6.5, 2.6861, 2.714023436)]
    [InlineData(2, 6.0, 2.4020, 2.429171138)]
    [InlineData(6, 6.5, 2.0688, 2.100020747)]
    [InlineData(12, 8.0, 2.1283, 2.116576071)]
    [InlineData(12, 10.0, 2.6475, 2.605417289)]
    public void HaefnersMeasuredBars_HaveTheirAcResistance(double widthOverThickness, double p, double measured, double fem)
    {
        // Haefner (1937) measured soft-drawn copper bars (Table I: a/c, his "shape", 1.00, 2.07, 4.95,
        // 10.0 and 2400, 60–65 ft of conductor; the table also lists Kennelly, Laws and Pierce's 8, 16
        // and 24, 200 ft) and plotted R_ac/R_dc against his similitude variable p = √(2µ₀f/R′_dc), R′_dc
        // per metre — the same for any bar of that shape at that p. Fig. 8's test curves: shapes 1 and 2
        // are his specimens 1 and 2 (a/c 1.00 and 2.07, nominal 2); no specimen has a/c 6 or 12, so those
        // curves were not measured on a bar of that shape. Read at half-integer p off the verified
        // digitisation (±0.005–0.01); MATLAB's FEM of the ideal rectangle at the same points. Measured:
        // PEEC against Haefner +0.28 / +2.02 / +1.89 / +1.55 / −0.58 / −1.53 %; against the FEM +0.32 /
        // +0.97 / +0.76 / +0.04 / −0.02 / +0.06 %. The FEM itself sits −0.04 / +1.04 / +1.13 / +1.51 /
        // −0.55 / −1.59 % off the measurements (rounded edges, shape 2's 2.07, the measurement, the
        // curves drawn between shapes and the reading), so the measured band is the wider. The FEM band
        // is graded by t/δ like AnIsolatedBar's: 0.6 % below t/δ 4, where the deviations are within
        // 0.06 %, and 1.2 % above, where they are +0.32 / +0.97 / +0.76 %.
        double t = 1e-3, w = widthOverThickness * t;
        double f = p * p / (Sigma * w * t) / (2 * Mu0);
        double ratio = BarRatio(w, t, f, 21, 21);
        _output.WriteLine($"shape {widthOverThickness}, p {p}: R/Rdc {ratio:f5}, Haefner {measured:f4} ({(ratio / measured - 1) * 100:f2} %), " +
                          $"FEM {fem:f5} ({(ratio / fem - 1) * 100:f2} %)");
        double tOverDelta = p * Math.Sqrt(Math.PI * t / (2 * w));   // t/δ = p·√(πt/2w): 5.0 / 8.1 / 5.3 / 3.3 / 2.9 / 3.6
        Assert.Equal(1, ratio / fem, tOverDelta < 4 ? 0.006 : 0.012);
        Assert.Equal(1, ratio / measured, 0.025);
    }

    private static readonly double[] Frequencies = { 1e2, 1e6, 1e8, 1e9 };

    private static LoopExtractionResult PourLoop(bool slot, double pitch, bool idealEnds, int planeStrips)
    {
        // ExtractionTests' pour loop: a 6 mm trace 0.4 mm × 35 µm, its copper 0.3 mm over a 12 × 6 mm
        // pour (35 µm), with or without the 0.6 × 4 mm slot across under it.
        double z = 0.3e-3 + 35e-6;
        var chain = new[] { new TraceSegment3D(new Vector3D(0, 0, z), new Vector3D(6e-3, 0, z), 0.4e-3, 35e-6) };
        var solid = new Polygon2(new[] { new Point2(-3e-3, -3e-3), new Point2(9e-3, -3e-3), new Point2(9e-3, 3e-3), new Point2(-3e-3, 3e-3) });
        var shape = slot ? new Polygon2(solid.Outer, new IReadOnlyList<Point2>[]
            { new[] { new Point2(2.7e-3, -2e-3), new Point2(2.7e-3, 2e-3), new Point2(3.3e-3, 2e-3), new Point2(3.3e-3, -2e-3) } }) : solid;
        return LoopExtraction.ChainOverPlane(chain, new[] { shape }, (-35e-6, 0), Frequencies, new LoopExtractionOptions
        {
            PlanePitchMeters = pitch, PlaneMarginMeters = 3e-3, IdealEndConnections = idealEnds, PlaneStripsAcrossThickness = planeStrips
        });
    }

    [Theory]
    // FastHenry's Zc.mat at 100 Hz, 1 MHz, 100 MHz, 1 GHz: R [Ω] then L [H] (Im Z/ω), six digits.
    [InlineData(true, 0.3e-3, true, 0, new[] { 0.00875348, 0.00997531, 0.031634, 0.0617759 }, new[] { 3.69599e-09, 3.12252e-09, 2.88927e-09, 2.86355e-09 })]
    [InlineData(false, 0.3e-3, true, 1, new[] { 0.00831747, 0.00919316, 0.0255361, 0.0537172 }, new[] { 2.9402e-09, 2.38602e-09, 2.21591e-09, 2.19476e-09 })]
    [InlineData(false, 6e-3 / 19, false, 0, new[] { 0.00835731, 0.00923969, 0.0303489, 0.0601851 }, new[] { 2.9935e-09, 2.43985e-09, 2.24633e-09, 2.22147e-09 })]
    public void ThePourLoop_IsFastHenrysOnTheSameGrid(bool slot, double pitch, bool idealEnds, int planeStrips,
        double[] fastHenryR, double[] fastHenryL)
    {
        // The same filaments in FastHenry (decks in References/FU-13/fasthenry, written by
        // pour_decks.py): the plane a uniform G grid with its nodes at OpenSim's cell centres, the
        // trace 7 × 5 strips graded by 2 (OpenSim's choice for a sweep to 1 GHz), the plane 3 strips
        // (or 1) graded by 2, the port and the far-end join on the same grid nodes, solved by
        // FastHenry's dense product (GMRES to 1e-7, equal to its LU in all six digits). Cases: the
        // slotted pour on the test's 0.3 mm grid with ideal joins (4439 filaments, OpenSim's
        // iterative path); the solid pour with one strip through the plane (1575, the dense path);
        // and the solid pour with the real end joins — the trace's wire down to the plane — on a
        // 6/19 mm grid that has a node under each end (4197, iterative). On the 0.3 mm grid the far
        // end is not a node and the join adds a diagonal bar, which FastHenry couples by its own
        // formula for filaments meeting at a point and OpenSim by the centre-line formula: there R
        // differs by 3.4 % at 100 MHz and L by 0.5 %, so that case is not gated. Measured: every value
        // within 1.1e-5 relative of FastHenry's, about five of its six printed digits.
        var result = PourLoop(slot, pitch, idealEnds, planeStrips);
        for (int k = 0; k < Frequencies.Length; k++)
        {
            double r = result.Points[k].Resistance(0, 0), l = result.Points[k].Inductance(0, 0);
            _output.WriteLine($"{Frequencies[k]:g3} Hz: R {r * 1e3:f5} mΩ (FastHenry {fastHenryR[k] * 1e3:f5}), L {l * 1e9:f5} nH (FastHenry {fastHenryL[k] * 1e9:f5})");
            Assert.Equal(1, r / fastHenryR[k], 3e-5);
            Assert.Equal(1, l / fastHenryL[k], 3e-5);
        }
        // With the default caps the trace's outer strips are far thicker than a skin depth at 1 GHz:
        // the result says so (raised to 15 × 9 trace and 9 plane strips, OpenSim agrees with FastHenry's
        // refined run to 1.8e-5 — 108.836 mΩ, 2.13410 nH against 108.838 and 2.13411 at 1 GHz for the solid
        // pour on the 6/19 mm grid with ideal ends, k09_p9_T2_solid — and the default's R reads 13 / 25 /
        // 45 % low at 100 MHz / 300 MHz / 1 GHz; not run here, 30 min).
        Assert.Contains(result.Assumptions, a => a.StartsWith("Resistance under-resolved above about"));
    }

    [Fact]
    public void TheResolutionNote_AppearsOnlyWhereTheStripCapsBind()
    {
        // 0.4 mm × 35 µm: with 7 strips across, the outermost is 18 µm, 0.7 δ at 6.47 MHz.
        var bar = new[] { new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(10e-3, 0, 0), 0.4e-3, 35e-6) };
        var low = LoopExtraction.Chain(bar, new[] { 1e6 });
        var high = LoopExtraction.Chain(bar, new[] { 1e9 });
        Assert.DoesNotContain(low.Assumptions, a => a.Contains("under-resolved"));
        string note = Assert.Single(high.Assumptions, a => a.StartsWith("Resistance under-resolved above about"));
        _output.WriteLine(note);
        Assert.Contains("6.47 MHz", note);
        // Raised caps meet the target at 1 GHz: no note.
        var raised = LoopExtraction.Chain(bar, new[] { 1e9 }, new LoopExtractionOptions { MaxStripsAcrossWidth = 21, MaxStripsAcrossThickness = 11 });
        Assert.DoesNotContain(raised.Assumptions, a => a.Contains("under-resolved"));
    }
}
