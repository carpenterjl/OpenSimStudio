using OpenSim.Optics;
using OpenSim.Optics.Analysis.StrayLight;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-46 gate (plan): "A plane-parallel plate's ghost sits at the closed-form displacement with
/// intensity R1 R2; the generator enumerates n(n−1)/2 pairs." Every expected value is derived in
/// the comments from the test's own inputs (thickness, index, distances: arbitrary). The
/// reflectances are the normal-incidence Fresnel reflectance R = ((n₁ − n₂)/(n₁ + n₂))², derived
/// from the Fresnel amplitude at normal incidence; the ideal coating "I.99" reflecting 1 % is the
/// help's own example (2025 R2 help, "Ghost Focus Generator", Ghost Reflector Coating,
/// plans/sources/optics/op-46-help/).
/// </summary>
public sealed class GhostFocusGateTests
{
    // An axial object point s0 in front of a plate of thickness t and index n, the stop on the
    // plate's front face, the image surface t2 behind its rear face (lengths in meters).
    private const double S0 = 0.1, T = 0.01, N = 1.5, T2 = 0.05, Epd = 0.004;

    internal static SequentialLens Plate(double objectDistance = S0, double image = T2) => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = objectDistance },
            new SequentialSurface { Thickness = T, Medium = ParaxialMedium.Constant(N), IsStop = true },
            new SequentialSurface { Thickness = image },
            new SequentialSurface(),
        },
    };

    internal static OpticalSystemData Sys(double fieldHeight = 0) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = Epd },
        Fields = new FieldSet
        {
            Type = FieldType.ObjectHeight,
            Points = fieldHeight == 0 ? new[] { new FieldPoint() } : new[] { new FieldPoint(), new FieldPoint { Y = fieldHeight } },
        },
    };

    private static GhostFocusRun Run(SequentialLens lens, OpticalSystemData system, string? coating = null, bool twice = true) =>
        GhostFocusGenerator.Run(lens, system, new GhostFocusOptions { DoubleBounce = twice, ReflectorCoating = coating, PupilGrid = 5 }, CoatingCatalog.Empty);

    [Fact]
    public void ThePlatesGhost_SitsAtTheClosedFormDisplacement()
    {
        // Derivation. The paraxial marginal ray leaves the object point with slope u0 and meets
        // the front face at y1 = s0·u0. In the glass its slope is u0/n (n·u is unchanged at a
        // plane), so after one pass y2 = y1 + t·u0/n and it leaves with slope u0: it crosses the
        // axis (virtually) at z = −y2/u0 = −(s0 + t/n) from the rear face — the main image. The
        // ghost reflected at the rear face and then the front face crosses the glass three times
        // (the reduced thickness t/n is added each time: after a mirror both the thickness and
        // the index change sign), so it leaves at y = s0·u0 + 3t·u0/n with slope u0 and crosses at
        // −(s0 + 3t/n). Measured from the image surface (t2 behind the rear face): main
        // −(s0 + t/n) − t2, ghost −(s0 + 3t/n) − t2; the ghost sits 2t/n before the main image.
        var lens = Plate();
        var system = Sys();
        var run = Run(lens, system);
        var ghost = run.Ghosts.Single(g => g.FirstBounce == 2 && g.SecondBounce == 1);
        Assert.Null(ghost.Error);
        double expected = -(S0 + 3 * T / N) - T2;
        Assert.InRange(ghost.FocusFromImage!.Value - expected, -1e-15, 1e-15);

        var p = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        double main = p.ParaxialImageDistance!.Value - T2;
        Assert.InRange(main - (-(S0 + T / N) - T2), -1e-15, 1e-15);
        Assert.InRange(ghost.FocusFromImage.Value - main - (-2 * T / N), -1e-15, 1e-15);

        // The pupil ghost size: the marginal ray on the image surface, 2|s0·u0 + 3t·u0/n + t2·u0|,
        // with u0 = (EPD/2)/s0 (the stop, and so the entrance pupil, is the front face).
        double u0 = Epd / 2 / S0;
        Assert.InRange(ghost.BeamDiameterAtImage - 2 * Math.Abs((S0 + 3 * T / N + T2) * u0), -1e-16, 1e-16);
    }

    [Fact]
    public void ThePlatesGhost_HasIntensityR1R2()
    {
        // Uncoated: both bounces are glass–air interfaces met from the glass, R = ((n − 1)/(n + 1))².
        // With transmission the ghost also crosses the front face inward and the rear face outward,
        // each transmitting 1 − R (no absorption): R²(1 − R)².
        double r = (N - 1) / (N + 1), fresnel = r * r;
        var ghost = Run(Plate(), Sys()).Ghosts.Single(g => g.FirstBounce == 2 && g.SecondBounce == 1);
        Assert.InRange(ghost.FirstReflectance - fresnel, -1e-15, 1e-15);
        Assert.InRange(ghost.SecondReflectance!.Value - fresnel, -1e-15, 1e-15);
        Assert.InRange(ghost.RelativeIntensity - fresnel * fresnel, -1e-16, 1e-16);
        Assert.InRange(ghost.PathTransmittance - fresnel * fresnel * (1 - fresnel) * (1 - fresnel), -1e-15, 1e-15);

        // The help's ghost reflector coating I.99 reflects 1 % at every bounce: R1·R2 = 1e-4.
        var coated = Run(Plate(), Sys(), "I.99").Ghosts.Single(g => g.FirstBounce == 2 && g.SecondBounce == 1);
        Assert.InRange(coated.RelativeIntensity - 0.01 * 0.01, -1e-16, 1e-16);

        // The uncoated image surface (air on both sides) reflects nothing.
        var image = Run(Plate(), Sys()).Ghosts.Single(g => g.FirstBounce == 3 && g.SecondBounce == 2);
        Assert.InRange(image.FirstReflectance, -1e-18, 1e-18);
    }

    [Fact]
    public void TheGenerator_EnumeratesNChoose2Pairs()
    {
        // The plate: surfaces 1 and 2 change the index and the image surface is a candidate first
        // bounce (help), n = 3 → 3 pairs. Two plates: n = 5 → 10 pairs; single bounce: n ghosts.
        var plate = Run(Plate(), Sys());
        Assert.Equal(new[] { 1, 2, 3 }, plate.Candidates);
        Assert.Equal(3 * 2 / 2, plate.Ghosts.Count);

        var two = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = S0 },
                new SequentialSurface { Thickness = T, Medium = ParaxialMedium.Constant(N), IsStop = true },
                new SequentialSurface { Thickness = 0.02 },
                new SequentialSurface { Thickness = 0.005, Medium = ParaxialMedium.Constant(1.7) },
                new SequentialSurface { Thickness = T2 },
                new SequentialSurface(),
            },
        };
        var run = Run(two, Sys());
        Assert.Equal(5, run.Candidates.Count);
        Assert.Equal(5 * 4 / 2, run.Ghosts.Count);
        Assert.Equal(run.Ghosts.Count, run.Ghosts.Select(g => (g.FirstBounce, g.SecondBounce)).Distinct().Count());
        Assert.All(run.Ghosts, g => { Assert.Null(g.Error); Assert.True(g.FirstBounce > g.SecondBounce); });
        Assert.Equal(5, Run(two, Sys(), twice: false).Ghosts.Count);
    }

    [Fact]
    public void TheRealGhostRay_AndTheGhostOperands_AgreeWithTheClosedForms()
    {
        // The real marginal ray (Py = 1, ray aiming off) runs from the object point to the edge of
        // the entrance pupil on the front face: tan θ0 = (EPD/2)/s0. In the glass sin θ1 = sin θ0/n
        // (Snell). It crosses the glass three times and the air s0 + t2, so on the image surface
        // y = (s0 + t2)·tan θ0 + 3t·tan θ1.
        var lens = Plate();
        var system = Sys();
        double tan0 = Epd / 2 / S0, sin0 = tan0 / Math.Sqrt(1 + tan0 * tan0), sin1 = sin0 / N;
        double expected = (S0 + T2) * tan0 + 3 * T * sin1 / Math.Sqrt(1 - sin1 * sin1);

        var ghost = GhostSystems.Build(lens, 2, 1);
        var trace = SequentialTrace.Create(lens, system);
        var launch = new RayLauncher(lens, system).Launch(trace, 0, 0, 0, 1);
        var final = SequentialTrace.Create(ghost.Lens, system).Trace(launch);
        Assert.True(final.IsOk);
        Assert.InRange(final.Position.Y - expected, -1e-15, 1e-15);

        // The operands in lens units (millimeters by default): GPRY that height; GPIM Mode 1 one
        // over the ghost focus distance |s0 + 3t/n + t2|; GPRT the uncoated R²(1 − R)² at the
        // ray's angles (checked against normal incidence only loosely: the angles are small).
        var e = MeritEvaluator.Evaluate(new MeritFunction(new[]
        {
            MeritOperandRow.Of("GPRY", 2, 1, 0, 1, 0, 0, 0, 1),
            MeritOperandRow.Of("GPIM", 2, 1, 0, 1, 1),
            MeritOperandRow.Of("GPIM", -1, -1, 0, 1, 1),
            MeritOperandRow.Of("GPSY", 2, 1, 0, 1, 0, 0, 0, 1),
        }), new SingleMeritConfiguration(lens, system));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        Assert.InRange(e.Rows[0].Value - expected * 1e3, -1e-12, 1e-12);
        double focus = (S0 + 3 * T / N + T2) * 1e3;
        Assert.InRange(e.Rows[1].Value - 1 / focus, -1e-15, 1e-15);
        // Wildcards: surfaces 1 and 2 only (the image surface has no index change), one pair.
        Assert.InRange(e.Rows[2].Value - 1 / focus, -1e-15, 1e-15);
        // GPSY: the paraxial marginal ray height on the image surface, (s0 + 3t/n + t2)·u0.
        Assert.InRange(e.Rows[3].Value - (S0 + 3 * T / N + T2) * tan0 * 1e3, -1e-12, 1e-12);
    }

    [Fact]
    public void ThePupilGhost_IsTheStopImagedThroughTheGhostPath()
    {
        // The chief ray passes the centre of the stop (the front face) with slope ū0; through the
        // ghost path it leaves the rear face at ȳ = 3t·ū0/n with slope ū0, so the ghost images the
        // stop at −3t/n from the rear face, −3t/n − t2 from the image surface. GPIM Mode 0 is one
        // over its distance in millimeters.
        var lens = Plate();
        var system = Sys(fieldHeight: 0.002);
        var ghost = Run(lens, system).Ghosts.Single(g => g.FirstBounce == 2 && g.SecondBounce == 1);
        double expected = -3 * T / N - T2;
        Assert.InRange(ghost.PupilFromImage!.Value - expected, -1e-15, 1e-15);
        var e = MeritEvaluator.Evaluate(new MeritFunction(new[] { MeritOperandRow.Of("GPIM", 2, 1, 0, 1, 0) }), new SingleMeritConfiguration(lens, system));
        Assert.False(e.HasFailures, string.Join("; ", e.Errors));
        Assert.InRange(e.Rows[0].Value - 1 / (Math.Abs(expected) * 1e3), -1e-15, 1e-15);
        // The ghost pupil is the stop seen through the plate, so its diameter is the stop's:
        // magnification 1 (a plate has no power).
        Assert.InRange(ghost.PupilMagnification!.Value - 1, -1e-14, 1e-14);
    }
}
