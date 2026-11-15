using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;
using OpenSim.Tests.Optics.Gates;

namespace OpenSim.Tests.Optics;

/// <summary>
/// The MTF and Moore–Elliott contrast operands (<see cref="MtfOperands"/>, wave 7 wiring of OP-39
/// into OP-56) and the Optimization Wizard's Contrast criterion. Definitions: OpticStudio 2025 R2
/// help, "MTF Data" (plans/sources/optics/op-39-help/), "Optimizing for MTF" (op-36-help/, with
/// its equations in op-39-help/images/) and "Other Settings" (op-56-help/). Expected values are the
/// engines' own (MtfEngine, OPDC) or derived in the comments. The singlet is the OP-56 gate's
/// (F/3.3 at 0.5 µm: an arbitrary input).
/// </summary>
public sealed class MtfOperandsTests
{
    private static OpticalSystemData Sys() => DefaultMeritGateTests.Sys(0.02, new[] { 0.0, 10 }, micrometers: new[] { 0.5, 0.6 });

    private static MeritEvaluation Eval(SequentialLens lens, OpticalSystemData system, params MeritOperandRow[] rows) =>
        MeritEvaluator.Evaluate(new MeritFunction(rows), new SingleMeritConfiguration(lens, system));

    [Fact]
    public void TheOperands_AreRegistered_WithTheHelpsParameters()
    {
        var registry = MeritOperandRegistry.CreateDefault();
        foreach (var code in new[] { "MTFT", "MTFS", "MTFA", "GMTT", "GMTS", "GMTA" })
        {
            var d = registry.Get(code);
            Assert.Equal(("Samp", "Wave", "Field", "Freq"), (d.Int1, d.Int2, d.DataLabel(1), d.DataLabel(2)));
        }
        Assert.Equal("Data Type", registry.Get("MTFA").DataLabel(4));
        Assert.Equal("!Scl", registry.Get("GMTA").DataLabel(3));
        foreach (var code in new[] { "MECT", "MECS", "MECA" })
            Assert.Equal(new[] { "Hx", "Hy", "Px", "Py", "Freq" }, registry.Get(code).Data);
        Assert.Equal(32, MtfOperands.GridOf(1));
        Assert.Equal(1024, MtfOperands.GridOf(MtfOperands.MaximumSamp));
    }

    [Theory]
    [InlineData(1)]   // one wavelength
    [InlineData(0)]   // polychromatic ("use 0 for polychromatic")
    public void MtfOperands_AreTheEnginesValue(int wave)
    {
        var lens = DefaultMeritGateTests.Singlet();
        var system = Sys();
        const double freq = 20;
        var e = Eval(lens, system,
            MeritOperandRow.Of("MTFT", 1, wave, 0, 1, 2, freq),
            MeritOperandRow.Of("MTFS", 1, wave, 0, 1, 2, freq),
            MeritOperandRow.Of("MTFA", 1, wave, 0, 1, 2, freq),
            MeritOperandRow.Of("GMTT", 1, wave, 0, 1, 2, freq),
            MeritOperandRow.Of("GMTA", 1, wave, 0, 1, 2, freq));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        var waves = wave == 0 ? new[] { 0, 1 } : new[] { 0 };
        var fft = MtfEngine.Evaluate(lens, system, 1, waves, freq, new MtfOptions { Sampling = 32 });
        var geo = MtfEngine.Evaluate(lens, system, 1, waves, freq, new MtfOptions { Method = MtfMethod.Geometric, Sampling = 32 });
        Assert.Equal(fft.Tangential, e.Rows[0].Value, 14);
        Assert.Equal(fft.Sagittal, e.Rows[1].Value, 14);
        Assert.Equal(fft.Average, e.Rows[2].Value, 14);
        Assert.Equal(geo.Tangential, e.Rows[3].Value, 14);
        Assert.Equal(geo.Average, e.Rows[4].Value, 14);
    }

    [Fact]
    public void WhatIsNotBuilt_FailsTheRow_NamingIt()
    {
        var lens = DefaultMeritGateTests.Singlet();
        var e = Eval(lens, Sys(),
            MeritOperandRow.Of("MTFT", 1, 1, 0, 1, 2, 20, 0, 3),     // Data Type 3 (phase)
            MeritOperandRow.Of("MTFT", 1, 1, 0, 1, 0, 20),           // Field 0 (diffraction limit)
            MeritOperandRow.Of("MTFT", 7, 1, 0, 1, 2, 20));          // Samp 7
        Assert.Equal(3, e.Errors.Count);
        Assert.Contains(e.Errors, x => x.Contains("Data Type"));
        Assert.Contains(e.Errors, x => x.Contains("Field 0"));
        Assert.Contains(e.Errors, x => x.Contains("Samp 7"));
    }

    [Fact]
    public void Contrast_IsTheOpdDifferenceAcrossTheShift_OfTheHelpsEquation()
    {
        // Help, "Optimizing for MTF": ξ_c = 1/(λ·F/#), and the merit drives φ(x_j, y_k) −
        // φ(x_j − Δ, y_k) to zero. With the cutoff where the pupils just touch (Δ = D; the reading
        // of OP-39's Contrast Loss Map, Conventions §10 item 34), Δ/(D/2) = 2ξ·λ·F/#; MECT and MECS
        // are the OPD differences (OPDC, waves) across that shift along y and x.
        var lens = DefaultMeritGateTests.Singlet();
        var system = Sys();
        const double freq = 100, px = 0.2, py = 0.4;
        double fno = ParaxialSystem.Create(lens.ToParaxialLens(), system, 0).WorkingFNumber!.Value;
        double delta = 2 * freq * 1000 * 0.5e-6 * fno;                 // cycles/m × m: derived from the help's equation
        Assert.Equal(delta, MtfOperands.ContrastShift(lens, system, 1, freq), 14);
        Assert.InRange(delta, 0.05, 0.5);                           // well inside the pupil for this test
        var e = Eval(lens, system,
            MeritOperandRow.Of("MECT", 0, 1, 0, 1, 0, 1, px, py, freq),
            MeritOperandRow.Of("MECS", 0, 1, 0, 1, 0, 1, px, py, freq),
            MeritOperandRow.Of("MECA", 0, 1, 0, 1, 0, 1, px, py, freq),
            MeritOperandRow.Of("OPDC", 0, 1, 0, 1, 0, 1, px, py),
            MeritOperandRow.Of("OPDC", 0, 1, 0, 1, 0, 1, px, py - delta),
            MeritOperandRow.Of("OPDC", 0, 1, 0, 1, 0, 1, px - delta, py));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        double a = e.Rows[3].Value, t = e.Rows[4].Value, s = e.Rows[5].Value;
        Assert.Equal(a - t, e.Rows[0].Value, 12);
        Assert.Equal(a - s, e.Rows[1].Value, 12);
        Assert.Equal(((a - t) + (a - s)) / 2, e.Rows[2].Value, 12);
        Assert.NotEqual(0, e.Rows[0].Value);

        // A partner outside the pupil is refused.
        var outside = Eval(lens, system, MeritOperandRow.Of("MECT", 0, 1, 0, 1, 0, 1, 0, -0.99, freq));
        Assert.Contains(outside.Errors, x => x.Contains("outside the pupil"));
    }

    [Fact]
    public void Contrast_OnAPerfectLens_IsZero()
    {
        // The paraxial (ideal) lens with the image at its focus, on axis: no OPD, so
        // every phase difference vanishes.
        double f = 0.1;
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Thickness = f, IsStop = true, Shape = new ParaxialShape(f) },
                new SequentialSurface(),
            },
        };
        var e = Eval(lens, Sys(), MeritOperandRow.Of("MECT", 0, 1, 0, 1, 0, 0, 0.3, 0.5, 50), MeritOperandRow.Of("MECS", 0, 1, 0, 1, 0, 0, 0.3, 0.5, 50));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        Assert.True(Math.Abs(e.Rows[0].Value) < 1e-6 && Math.Abs(e.Rows[1].Value) < 1e-6, $"{e.Rows[0].Value:R}, {e.Rows[1].Value:R}");
    }

    // ---- The wizard's Contrast criterion ----

    [Fact]
    public void TheWizardsContrast_WritesMectAndMecsRows_WithTheRelativeWeights()
    {
        // Help, "Optimization Function Criteria": Contrast "Optimizes the MTF of the system using the
        // Moore-Elliott Contrast method"; "Other Settings": the relative S and T weights weigh the
        // sagittal and tangential phase differences, and Spatial Frequency is the frequency.
        var lens = DefaultMeritGateTests.Singlet();
        var system = Sys();
        const double freq = 100;
        var result = DefaultMeritWizard.Generate(new SingleMeritConfiguration(lens, system), 1, new DefaultMeritOptions
        {
            Criterion = MeritWizardCriterion.Contrast, SpatialFrequency = freq, RelativeTWeight = 2, RelativeSWeight = 0.5,
        });
        var rows = result.Rows.Where(r => r.Code != "BLNK").ToArray();
        Assert.Equal(new[] { "MECT", "MECS" }, rows.Select(r => r.Code).Distinct());
        Assert.All(rows, r => Assert.Equal(freq, r.DataAt(5)));
        // Every partner point lies inside the pupil.
        foreach (var r in rows)
        {
            double delta = MtfOperands.ContrastShift(lens, system, r.Int2, freq);
            double qx = r.Code == "MECS" ? r.DataAt(3) - delta : r.DataAt(3), qy = r.Code == "MECT" ? r.DataAt(4) - delta : r.DataAt(4);
            Assert.True(qx * qx + qy * qy <= 1, $"{r.Code} at ({r.DataAt(3)}, {r.DataAt(4)})");
        }
        // Same pupil points kept both ways on this unvignetted lens? Not necessarily; the weights per
        // kept row are the pupil weight × field × wavelength × relative weight, so a T row's weight
        // over the S row's at the same point is 2/0.5.
        var t = rows.Where(r => r.Code == "MECT").ToDictionary(r => (r.Int2, r.DataAt(2), r.DataAt(3), r.DataAt(4)));
        foreach (var s in rows.Where(r => r.Code == "MECS"))
            if (t.TryGetValue((s.Int2, s.DataAt(2), s.DataAt(3), s.DataAt(4)), out var tr))
                Assert.Equal(4, tr.Weight / s.Weight, 12);
        Assert.Contains("Moore–Elliott contrast at 100 cycles/mm", result.Summary);

        var e = MeritEvaluator.Evaluate(new MeritFunction(result.Rows), new SingleMeritConfiguration(lens, system));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        Assert.True(e.Value > 0);
    }

    [Fact]
    public void TheWizardsContrast_RefusesNoFrequency_AndAFrequencyBeyondTheCutoff()
    {
        Assert.Contains(DefaultMeritWizard.Validate(new DefaultMeritOptions { Criterion = MeritWizardCriterion.Contrast }), p => p.Contains("spatial frequency"));
        Assert.Contains(DefaultMeritWizard.Validate(new DefaultMeritOptions { Criterion = MeritWizardCriterion.Contrast, SpatialFrequency = 50, RelativeSWeight = 0, RelativeTWeight = 0 }),
            p => p.Contains("S or T weight"));
        var lens = DefaultMeritGateTests.Singlet();
        // About 6.7 pupil radii at 2000 cycles/mm (2ξ·λ·F/# with F/# ≈ 3.3): no partner can be inside.
        var x = Assert.Throws<ArgumentException>(() => DefaultMeritWizard.Generate(new SingleMeritConfiguration(lens, Sys()), 1,
            new DefaultMeritOptions { Criterion = MeritWizardCriterion.Contrast, SpatialFrequency = 2000 }));
        Assert.Contains("cutoff", x.Message);
    }
}
