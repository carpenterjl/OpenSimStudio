using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization.HighYield;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-62's lenses. Every lens is in millimeters with constant indices, so no glass catalog is
/// read; radii, thicknesses, indices and the focal length are arbitrary test inputs.
/// </summary>
internal static class HighYieldFixtures
{
    public static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    public const double ShellIndex = 1.5;
    public const double EntrancePupil = 10;
    public const double FocalLength = 100;

    /// <summary>
    /// A zero-thickness glass shell in front of an ideal lens, object at infinity, one on-axis
    /// field: surface 1 (the stop) radius <paramref name="radius"/>, index 1.5 after it, thickness
    /// 0; surface 2 the same radius (a curvature pickup of surface 1), air after it, 5 mm to the
    /// Paraxial surface 3 (focal length 100 mm, OPD mode 2), 100 mm to the image.
    /// <para>
    /// Derived: with zero thickness a ray meets surface 2 where it left surface 1, at the same
    /// normal, and refracts back to its incoming direction, so the shell changes no ray (to
    /// rounding) whatever its curvature; the ideal lens then focuses the collimated beam on the
    /// image. The nominal image is perfect for every radius. On axis a ray at height h meets the
    /// sphere at angle θ with sin θ = c·h (the sphere's normal at (0, h, z) is (0, −ch, 1 − cz)
    /// of unit length, since c(h² + z²) = 2z), so its High-Yield term is
    /// 0.5·(1 − √(1 − c²h²)) at both surfaces.
    /// </para>
    /// </summary>
    public static OpticsSettings Shell(double radius = 15, bool variable = false) => new()
    {
        SystemData = new OpticsSystemSettings
        {
            ApertureValue = EntrancePupil,
            Fields = new() { new OpticsFieldSettings() },
            Wavelengths = new() { new OpticsWavelengthSettings { Micrometers = 0.55 } },
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new()
            {
                Radius = radius, Thickness = 0, Index = ShellIndex, IsStop = true,
                Flags = variable ? new() { new() { Column = "Radius", Kind = "Variable" } } : null,
            },
            new()
            {
                Radius = radius, Thickness = 5,
                Flags = new() { new() { Column = "Radius", Kind = "Pickup", PickupSurface = 1, PickupColumn = "Radius", PickupScale = 1 } },
            },
            new()
            {
                Type = "Paraxial", Thickness = FocalLength,
                Parameters = new() { new() { Number = 1, Value = FocalLength }, new() { Number = 2, Value = 2 } },
            },
            new(),
        },
    };

    /// <summary>τ of an on-axis ray at normalised pupil radius ρ on the shell of radius R
    /// (derived on <see cref="Shell"/>).</summary>
    public static double ShellTau(double radius, double rho)
    {
        double ch = rho * EntrancePupil / 2 / radius;
        return 0.5 * (1 - Math.Sqrt(1 - ch * ch));
    }

    public static IMeritConfigurations Configurations(OpticsSettings lens) => SingleMeritConfiguration.FromProject(lens, NoCatalogs);
}

/// <summary>
/// OP-62: the High-Yield penalty τ = |n − n′|(1 − R·N) (Zemax Knowledgebase KA-01837, saved
/// under plans/sources/optics/op-62-help/), the HYLD operand, the rows the Optimize window
/// writes, and the block's replacement and removal. Closed forms are derived in each test.
/// </summary>
public sealed class HighYieldTests
{
    private static Vector3D Dir(double angle) => new(Math.Sin(angle), 0, Math.Cos(angle));

    /// <summary>KA-01837: "If n′ &gt; n, this is the incident ray vector, else the refracted ray
    /// vector." Air to glass reads the incident angle α; glass to air the refracted angle β, here
    /// with sin β = 1.5 sin α (Snell, derived). τ = |n − n′|(1 − cos).</summary>
    [Fact]
    public void Tau_ReadsTheIncidentRayIntoTheHigherIndex_AndTheRefractedRayOutOfIt()
    {
        var n = new Vector3D(0, 0, 1);
        double alpha = 0.3, beta = Math.Asin(1.5 * Math.Sin(alpha));
        double into = HighYieldPenalty.Tau(1.0, 1.5, Dir(alpha), Dir(Math.Asin(Math.Sin(alpha) / 1.5)), n);
        Assert.Equal(0.5 * (1 - Math.Cos(alpha)), into, 14);
        double outOf = HighYieldPenalty.Tau(1.5, 1.0, Dir(alpha), Dir(beta), n);
        Assert.Equal(0.5 * (1 - Math.Cos(beta)), outOf, 14);
        // The larger of the two angles either way (the eGuide's "whichever is larger").
        Assert.True(outOf > 0.5 * (1 - Math.Cos(alpha)));
        // No index change: zero; along the normal: zero.
        Assert.Equal(0, HighYieldPenalty.Tau(1.5, 1.5, Dir(alpha), Dir(alpha), n));
        Assert.Equal(0, HighYieldPenalty.Tau(1.0, 1.5, Dir(0), Dir(0), n));
        // The normal's sense does not matter: R·N is read as the cosine of an angle of 0 to 90°.
        Assert.Equal(into, HighYieldPenalty.Tau(1.0, 1.5, Dir(alpha), Dir(alpha), new Vector3D(0, 0, -1)), 15);
    }

    /// <summary>At a small angle 1 − cos α cancels; the computed form sin²/(1 + cos) keeps the
    /// digits. Exact identity (derived): 1 − cos α = 2 sin²(α/2).</summary>
    [Fact]
    public void Tau_KeepsItsDigits_AtSmallAngles()
    {
        foreach (double alpha in new[] { 1e-3, 1e-6, 1e-9 })
        {
            double expected = 0.5 * 2 * Math.Pow(Math.Sin(alpha / 2), 2);
            double tau = HighYieldPenalty.Tau(1.0, 1.5, Dir(alpha), Dir(alpha), new Vector3D(0, 0, 1));
            Assert.True(Math.Abs(tau - expected) <= 1e-13 * expected, $"α {alpha}: τ {tau:R} against {expected:R}");
        }
    }

    [Fact]
    public void TheRegistry_KnowsHyld_WithTheEditorsColumns()
    {
        var d = MeritOperandRegistry.CreateDefault().Find("HYLD");
        Assert.NotNull(d);
        Assert.Equal("Surf", d!.Int1);
        Assert.Equal("Wave", d.Int2);
        Assert.Equal(new[] { "Hx", "Hy", "Px", "Py" }, Enumerable.Range(1, 4).Select(d.DataLabel));
        Assert.NotNull(d.Ray);
    }

    /// <summary>HYLD on the shell against the closed form 0.5·(1 − √(1 − c²h²)) at both shell
    /// surfaces (derived on <see cref="HighYieldFixtures.Shell"/>), zero for the chief ray and at
    /// the Paraxial surface (air on both sides), and a failure naming the object surface.</summary>
    [Fact]
    public void Hyld_OnTheShell_IsTheClosedForm()
    {
        var lens = HighYieldFixtures.Shell(radius: 15);
        var rows = new List<MeritOperandRow>();
        var points = new (double Px, double Py)[] { (0, 1), (0.6, 0.3), (-0.2, -0.7) };
        foreach (var (px, py) in points)
            for (int s = 1; s <= 2; s++)
                rows.Add(MeritOperandRow.Of("HYLD", s, 1, 0, 1, 0, 0, px, py));
        rows.Add(MeritOperandRow.Of("HYLD", 1, 1, 0, 1, 0, 0, 0, 0));
        rows.Add(MeritOperandRow.Of("HYLD", 3, 1, 0, 1, 0, 0, 0, 1));
        rows.Add(MeritOperandRow.Of("HYLD", 0, 1, 0, 1, 0, 0, 0, 1));
        var e = MeritEvaluator.Evaluate(new MeritFunction(rows), HighYieldFixtures.Configurations(lens));

        for (int i = 0; i < points.Length; i++)
        {
            double expected = HighYieldFixtures.ShellTau(15, Math.Sqrt(points[i].Px * points[i].Px + points[i].Py * points[i].Py));
            for (int s = 0; s < 2; s++)
            {
                var r = e.Rows[2 * i + s];
                Assert.Equal(MeritRowStatus.Evaluated, r.Status);
                Assert.True(Math.Abs(r.Value - expected) <= 1e-12 * expected, $"row {r.Number}: {r.Value:R} against {expected:R}");
            }
        }
        Assert.Equal(0, e.Rows[6].Value);
        Assert.Equal(0, e.Rows[7].Value);
        Assert.Equal(MeritRowStatus.Failed, e.Rows[8].Status);
        Assert.Contains("object surface", e.Rows[8].Error);
    }

    /// <summary>The rows follow the default merit function's rays: one HYLD row per pupil point
    /// and refracting surface (1 and 2; the Paraxial surface 3 has air on both sides), each
    /// weighted W × the criterion row's weight at that point. W = 2 scales exactly in binary, so
    /// the weights are compared bit for bit.</summary>
    [Fact]
    public void Generate_WritesOneRowPerWizardRayAndRefractingSurface()
    {
        var lens = HighYieldFixtures.Shell();
        var configurations = HighYieldFixtures.Configurations(lens);
        var options = new DefaultMeritOptions { AssumeAxialSymmetry = false };
        var wizard = DefaultMeritWizard.Generate(configurations, 1, options);
        var trac = wizard.Rows.Where(r => r.Code == "TRAC").ToArray();
        Assert.Equal(18, trac.Length);                                    // 3 rings × 6 arms, one field and wavelength

        var hy = HighYieldMerit.Generate(configurations, 1, options, weight: 2);
        Assert.Equal(PupilIntegration.GaussianQuadrature, hy.Integration);
        Assert.Equal(new[] { 1, 2 }, hy.Surfaces[1]);
        Assert.Equal(36, hy.OperandCount);
        Assert.Equal(37, hy.Rows.Count);
        Assert.Equal("BLNK", hy.Rows[0].Code);
        Assert.StartsWith(HighYieldMerit.HeaderPrefix, hy.Rows[0].Comment);
        var hyld = hy.Rows.Skip(1).ToArray();
        Assert.All(hyld, r => { Assert.Equal("HYLD", r.Code); Assert.Equal(1, r.Int2); Assert.Equal(0, r.Target); });
        for (int i = 0; i < trac.Length; i++)
            for (int s = 0; s < 2; s++)
            {
                var r = hyld[2 * i + s];
                Assert.Equal(s + 1, r.Int1);
                Assert.Equal(trac[i].DataAt(3), r.DataAt(3));
                Assert.Equal(trac[i].DataAt(4), r.DataAt(4));
                Assert.Equal(2 * trac[i].Weight, r.Weight);
            }
        Assert.Empty(hy.Notes);

        Assert.Throws<ArgumentException>(() => HighYieldMerit.Generate(configurations, 1, options, weight: 0));
        Assert.Throws<ArgumentException>(() => HighYieldMerit.Generate(configurations, 1, options, weight: double.NaN));
        Assert.Throws<ArgumentException>(() => HighYieldMerit.Generate(configurations, 1, options with { Rings = 0 }, weight: 1));
    }

    /// <summary>The report's mean τ at each shell surface is the quadrature mean of the closed
    /// form over the rule's points, Σ wᵢ τ(ρᵢ) (the row weights are the rule's weights times W);
    /// the HYLD rows alone are 100 % of the merit function.</summary>
    [Fact]
    public void Report_GivesTheQuadratureMeanOfTau_SurfaceBySurface()
    {
        var lens = HighYieldFixtures.Shell(radius: 15);
        var configurations = HighYieldFixtures.Configurations(lens);
        var hy = HighYieldMerit.Generate(configurations, 1, new DefaultMeritOptions { AssumeAxialSymmetry = false }, weight: 1);
        var report = HighYieldMerit.Report(MeritEvaluator.Evaluate(new MeritFunction(hy.Rows), configurations));

        double expected = PupilQuadrature.Gaussian(3, 6).Mean((px, py) => HighYieldFixtures.ShellTau(15, Math.Sqrt(px * px + py * py)));
        Assert.Equal(2, report.Surfaces.Count);
        foreach (var s in report.Surfaces)
        {
            Assert.Equal(1, s.Configuration);
            Assert.Equal(18, s.Rows);
            Assert.True(Math.Abs(s.MeanTau - expected) <= 1e-12 * expected, $"surface {s.Surface}: {s.MeanTau:R} against {expected:R}");
            Assert.True(s.MaxTau >= s.MeanTau);
        }
        Assert.Equal(100, report.Contribution, 9);
        Assert.Empty(report.Failures);
    }

    /// <summary>The block goes at the end; writing it again replaces it; Without removes it; HYLD
    /// rows outside it, rows after it and a jump into it are refused by name.</summary>
    [Fact]
    public void TheBlock_IsReplacedAndRemoved_AndStrayRowsAreRefused()
    {
        var lens = HighYieldFixtures.Shell();
        var hy = HighYieldMerit.Generate(HighYieldFixtures.Configurations(lens), 1, new DefaultMeritOptions(), weight: 1);
        var existing = new MeritFunction(new[] { MeritOperandRow.Of("EFFL", 0, 1, 100, 1) });

        var once = HighYieldMerit.WithRows(existing, hy.Rows);
        Assert.Equal(1 + hy.Rows.Count, once.Rows.Count);
        Assert.Equal(2, HighYieldMerit.FindBlock(once));
        var twice = HighYieldMerit.WithRows(once, hy.Rows);
        Assert.Equal(once.Rows, twice.Rows);
        Assert.Equal(existing.Rows, HighYieldMerit.Without(twice).Rows);
        Assert.Null(HighYieldMerit.FindBlock(existing));
        Assert.Same(existing, HighYieldMerit.Without(existing));

        var stray = new MeritFunction(new[] { MeritOperandRow.Of("HYLD", 1, 1, 0, 1) }.Concat(once.Rows));
        Assert.Contains("row 1 is outside", Assert.Throws<ArgumentException>(() => HighYieldMerit.WithRows(stray, hy.Rows)).Message);
        var after = new MeritFunction(once.Rows.Append(MeritOperandRow.Of("EFFL", 0, 1, 100, 1)));
        Assert.Contains("at the end", Assert.Throws<ArgumentException>(() => HighYieldMerit.Without(after)).Message);
        var jump = new MeritFunction(new[] { MeritOperandRow.Of("GOTO", 3) }.Concat(once.Rows));
        Assert.Contains("jumps to row 3", Assert.Throws<ArgumentException>(() => HighYieldMerit.Without(jump)).Message);
    }

    /// <summary>A mirror has no High-Yield term in the source; asking for one fails naming the surface.</summary>
    [Fact]
    public void Tau_RefusesAMirror()
    {
        var hit = new OpenSim.Optics.Sequential.SurfaceHit { Surface = 4, Normal = new Vector3D(0, 0, 1), IncomingDirection = Dir(0.1), Direction = Dir(0.1), IndexBefore = 1, IndexAfter = 1 };
        Assert.Contains("surface 4 is a mirror", Assert.Throws<ArgumentException>(() => HighYieldPenalty.Tau(hit, mirror: true)).Message);
        Assert.Equal(0, HighYieldPenalty.Tau(hit, mirror: false));
    }
}
