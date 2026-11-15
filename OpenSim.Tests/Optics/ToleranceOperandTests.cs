using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-63: the tolerance operand table and the perturbation rules. The gate (plan, OP-63): each
/// operand applied at a known value produces the stated geometric change — a 1 arc-minute element
/// tilt rotates both surfaces about the pivot by that angle, read from the global vertex frames —
/// and applying then removing a perturbation restores the system bitwise. The worked examples of
/// TRAD, TCUR, TFRN and TTHI are copied from their OpticStudio 2025 R2 help pages (saved under
/// plans/sources/optics/op-63-help/), cited at each test; every other expected value is derived
/// in the test from the operand's stated rule, and the lens data are arbitrary inputs.
/// </summary>
public sealed class ToleranceOperandTests
{
    private static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    /// <summary>Two singlets in air (mm, constant indices, fixed semi-diameters), the image 40 mm
    /// after the second; a multi-configuration row on surface 4's thickness, a merit row on it, and
    /// surface 3's conic picked up from surface 1, so renumbering shows.</summary>
    private static OpticsSettings Doublet() => new()
    {
        Surfaces = new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 4, Index = 1.5, SemiDiameter = 10, IsStop = true, Conic = -0.5 },
            new() { Radius = -50, Thickness = 10, SemiDiameter = 10 },
            new()
            {
                Radius = -30, Thickness = 3, Index = 1.6, SemiDiameter = 9,
                Flags = new() { new() { Column = SavedSurfaces.ConicColumn, Kind = SavedSurfaces.PickupFlag, PickupSurface = 1, PickupColumn = SavedSurfaces.ConicColumn } },
            },
            new() { Radius = 80, Thickness = 40, SemiDiameter = 9 },
            new() { SemiDiameter = 5 },
        },
        MultiConfiguration = new()
        {
            ConfigurationCount = 2,
            CurrentConfiguration = 1,
            Operands = new() { new() { Type = "THIC", Number1 = 4, Values = new() { new() { Value = 40 }, new() { Value = 41 } } } },
        },
        MeritFunction = new() { Operands = new() { new() { Type = "CTVA", Int1 = 4, Target = 40, Weight = 1 } } },
    };

    private static ToleranceTable Table(params ToleranceOperandRow[] rows) => new(rows);

    private static ToleranceOperandRow Row(string code, int i1 = 0, int i2 = 0, int i3 = 0, double min = 0, double max = 0) =>
        new() { Code = code, Int1 = i1, Int2 = i2, Int3 = i3, Min = min, Max = max };

    private static ToleranceChange Apply(OpticsSettings s, ToleranceTable t, int row, double value, ToleranceApplyOptions? o = null) =>
        new TolerancePerturber(s, NoCatalogs).Apply(s, t, row, value, o);

    private static ToleranceChange Apply(OpticsSettings s, ToleranceOperandRow row, double value, ToleranceApplyOptions? o = null) =>
        Apply(s, Table(row), 0, value, o);

    private static SurfacePlacement Place(OpticsSettings s) =>
        SurfacePlacement.Compute(MultiConfiguration.LensFor(s, 1, NoCatalogs)!);

    private static void Near(Vector3D expected, Vector3D actual, double tol)
    {
        Assert.True((expected - actual).Length <= tol, $"expected {expected}, got {actual} (|Δ| = {(expected - actual).Length:g3}).");
    }

    private static void Near(RigidTransform expected, RigidTransform actual, double tol)
    {
        Near(expected.Col0, actual.Col0, tol);
        Near(expected.Col1, actual.Col1, tol);
        Near(expected.Col2, actual.Col2, tol);
        Near(expected.Translation, actual.Translation, tol);
    }

    // ------------------------------------------------------------------ the table

    [Fact]
    public void Registry_HoldsEveryOperandOfTheHelpSummaries()
    {
        // The codes of "Tolerance Operands" (its Summary of Tolerance Operands, which also lists the
        // controls) and of "TNPS, TNPA, TNMA", "TOFF", "MPVT", "CNPA" and "CNPS".
        string[] codes =
        {
            "ISOA", "ISOB", "ISOC", "ISOD", "TRAD", "TCUR", "TFRN", "TTHI", "TCON", "TSDI", "TSDR", "TSDX", "TSDY", "TSTX", "TSTY",
            "TIRX", "TIRY", "TIRR", "TEXI", "TEZI", "TPAI", "TPAR", "TIND", "TABB", "TCMU", "TCIO", "TCEO", "TEDR", "TEDX", "TEDY",
            "TETX", "TETY", "TETZ", "TARR", "TARX", "TARY", "TRLR", "TRLX", "TRLY", "TUDX", "TUDY", "TUTX", "TUTY", "TUTZ", "TNPS",
            "TNPA", "TMCO", "CEDV", "CMCO", "COMM", "COMP", "CPAR", "SAVE", "SEED", "STAT", "TWAV", "TOFF", "TNMA", "MPVT", "CNPA", "CNPS",
        };
        foreach (var c in codes) Assert.NotNull(ToleranceOperandRegistry.Find(c));
        Assert.Equal(codes.Length, ToleranceOperandRegistry.All.Count);
        Assert.All(ToleranceOperandRegistry.All, d => Assert.StartsWith("OpticStudio 2025 R2 help", d.Source));
        Assert.Equal(ToleranceOperandRegistry.Off, ToleranceOperandRegistry.All[0].Code);
    }

    [Fact]
    public void Settings_RoundTripTheTable_AndNameAnUnknownCode()
    {
        var table = new ToleranceTable(new[]
        {
            Row("TWAV", min: 0.6328), Row("TRAD", 1, 1, min: -0.5, max: 0.5) with { Comment = "radius", Ignore = true },
            Row("TETX", 3, 4, min: -0.1, max: 0.1) with { DoNotAdjust = true },
        }, IrregularityModel.OpticStudio);
        var saved = ToleranceDataSettings.ToSettings(table)!;
        var notes = new List<string>();
        var back = ToleranceDataSettings.FromSettings(saved, notes);
        Assert.Empty(notes);
        Assert.Equal(Json(table.Rows), Json(back.Rows));
        Assert.Equal(IrregularityModel.OpticStudio, back.IrregularityModel);
        Assert.Null(ToleranceDataSettings.ToSettings(new ToleranceTable()));

        var odd = ToleranceDataSettings.FromSettings(new OpticsToleranceSettings { Operands = new() { new() { Type = "XYZW", Comment = "c" } } }, notes);
        Assert.Equal(ToleranceOperandRegistry.Off, odd.Rows[0].Code);
        Assert.Contains("XYZW", odd.Rows[0].Comment);
        Assert.Contains(notes, n => n.Contains("XYZW"));
    }

    [Fact]
    public void Controls_SetTheContextOfTheRowsBelowThem()
    {
        var t = Table(Row("TFRN", 1), Row("TWAV", min: 0.6328), Row("MPVT", 1), Row("STAT", 1), Row("SEED", 7), Row("TETX", 1, 2),
            Row("COMP", 4, 0, min: -1, max: 1), Row("CPAR", 3, 2, min: -0.1, max: 0.1));
        Assert.Null(t.ContextAt(0).TestWavelengthMicrometers);
        var c = t.ContextAt(5);
        Assert.Equal(0.6328, c.TestWavelengthMicrometers);
        Assert.Equal(TolerancePivot.RearVertex, c.Pivot);
        Assert.Equal(ToleranceStatistics.Uniform, c.Statistics);
        Assert.Equal(7, c.Seed);
        var comps = t.Compensators();
        Assert.Equal(new[] { 7, 8 }, comps.Select(x => x.Row));
        Assert.Throws<ArgumentException>(() => Apply(Doublet(), t, 1, 1));   // a control row perturbs nothing
    }

    [Fact]
    public void Validate_FollowsTheNestingRulesOfTheHelp()
    {
        // "Nesting Rules for Monte Carlo Analysis": the valid set TETX 5 12, 5 10, 11 12 and the
        // invalid set TETX 5 12, 9 15, 5 15 (row 2 breaks rule 1, row 3 rule 2).
        var lens = new OpticsSettings { Surfaces = Enumerable.Range(0, 20).Select(_ => new OpticsSurfaceSettings()).ToList() };
        Assert.Empty(ToleranceValidation.Validate(Table(Row("TETX", 5, 12), Row("TETX", 5, 10), Row("TETX", 11, 12)), lens));
        var bad = ToleranceValidation.Validate(Table(Row("TETX", 5, 12), Row("TETX", 9, 15), Row("TETX", 5, 15)), lens);
        Assert.Contains(bad, p => p.StartsWith("Tolerance row 2") && p.Contains("rule 1"));
        Assert.Contains(bad, p => p.StartsWith("Tolerance row 3") && p.Contains("rule 2"));
        // "TETX 5-15 may be followed by another TETX 5-15 or by TETX 5-12 or TETX 13-15, but not TETX 4-13".
        Assert.Empty(ToleranceValidation.Validate(Table(Row("TETX", 5, 15), Row("TETX", 5, 15), Row("TETX", 5, 12), Row("TETX", 13, 15)), lens));
        Assert.NotEmpty(ToleranceValidation.Validate(Table(Row("TETX", 5, 15), Row("TETX", 4, 13)), lens));
        Assert.Contains(ToleranceValidation.Validate(Table(Row("TEDR", 1, 2, min: -1, max: 1)), lens), p => p.Contains("zero or more"));
    }

    // ------------------------------------------------------------------ direct values

    [Fact]
    public void Trad_GivesTheHelpsExample()
    {
        // "TRAD: Tolerance on Radius": nominal 40 mm, ±0.5: Code 0 → 39.5 and 40.5; Code 1 → 39.8 and 40.2.
        var s = Doublet() with { Surfaces = Doublet().Surfaces!.Select((x, j) => j == 2 ? x with { Radius = 40 } : x).ToList() };
        Assert.Equal(39.5, Apply(s, Row("TRAD", 2, 0), -0.5).Perturbed.Surfaces![2].Radius!.Value, 12);
        Assert.Equal(40.5, Apply(s, Row("TRAD", 2, 0), 0.5).Perturbed.Surfaces![2].Radius!.Value, 12);
        Assert.Equal(39.8, Apply(s, Row("TRAD", 2, 1), -0.5).Perturbed.Surfaces![2].Radius!.Value, 12);
        Assert.Equal(40.2, Apply(s, Row("TRAD", 2, 1), 0.5).Perturbed.Surfaces![2].Radius!.Value, 12);
    }

    [Fact]
    public void Tcur_GivesTheHelpsExample()
    {
        // "TCUR: Tolerance on Curvature": radius 100 mm, ±0.001 per mm → 111.11 and 90.909 mm (printed so).
        var s = Doublet() with { Surfaces = Doublet().Surfaces!.Select((x, j) => j == 2 ? x with { Radius = 100 } : x).ToList() };
        Assert.Equal(111.11, Apply(s, Row("TCUR", 2), -0.001).Perturbed.Surfaces![2].Radius!.Value, 2);
        Assert.Equal(90.909, Apply(s, Row("TCUR", 2), 0.001).Perturbed.Surfaces![2].Radius!.Value, 3);
    }

    [Fact]
    public void Tfrn_GivesTheHelpsExample()
    {
        // "TFRN: Tolerance on Fringes": 1 fringe at TWAV 0.6328 µm, r = 6 mm, R = 50 mm → R + ΔR = 49.956 mm.
        var s = Doublet() with { Surfaces = Doublet().Surfaces!.Select((x, j) => j == 2 ? x with { Radius = 50, SemiDiameter = 6 } : x).ToList() };
        var t = Table(Row("TWAV", min: 0.6328), Row("TFRN", 2));
        Assert.Equal(49.956, Apply(s, t, 1, 1).Perturbed.Surfaces![2].Radius!.Value, 3);
    }

    [Fact]
    public void Tthi_GivesTheHelpsExample()
    {
        // "TTHI: Tolerance on Thickness": thicknesses 3, 4, 6 on surfaces 3, 4, 5, TTHI 3 with
        // adjustment surface 5 at +0.1 → 3.1, 4.0, 5.9; TTHI 3 3 adjusts nothing.
        var surfaces = new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true }, new() { Thickness = 5, IsStop = true }, new() { Thickness = 2 },
            new() { Radius = 20, Thickness = 3, Index = 1.5 }, new() { Radius = -15, Thickness = 4, Index = 1.6 },
            new() { Radius = -40, Thickness = 6 }, new() { Thickness = 30 }, new(),
        };
        var s = new OpticsSettings { Surfaces = surfaces };
        var p = Apply(s, Row("TTHI", 3, 5), 0.1).Perturbed.Surfaces!;
        Assert.Equal(new[] { 3.1, 4.0, 5.9 }, new[] { p[3].Thickness, p[4].Thickness, p[5].Thickness }.Select(x => Math.Round(x, 12)));
        var q = Apply(s, Row("TTHI", 3, 3), 0.1).Perturbed.Surfaces!;
        Assert.Equal(6, q[5].Thickness);
    }

    [Fact]
    public void ACellAPerturbationWouldNotReach_IsRefusedByName()
    {
        // Surface 3's conic is a pickup; surface 4's thickness is a multi-configuration row.
        var s = Doublet();
        Assert.Contains("pickup", Assert.Throws<ArgumentException>(() => Apply(s, Row("TCON", 3), 0.1)).Message);
        Assert.Contains("TMCO", Assert.Throws<ArgumentException>(() => Apply(s, Row("TTHI", 4, 4), 0.1)).Message);
        Assert.Contains("OP-25", Assert.Throws<NotSupportedException>(() => Apply(s, Row("TEXI", 1, 10, 2), 1)).Message);
        Assert.Contains("non-sequential", Assert.Throws<NotSupportedException>(() => Apply(s, Row("TNPS", 1, 1, 1), 1)).Message);
        Assert.Contains("coating", Assert.Throws<NotSupportedException>(() => Apply(s, Row("TCMU", 1, 1), 1)).Message);
        Assert.Contains("Composite", Assert.Throws<NotSupportedException>(() => Apply(s, Row("ISOA", 1), 1)).Message);
    }

    [Fact]
    public void TmcoTindTparAndUserBreaks_ChangeTheirValue()
    {
        var s = Doublet();
        var mc = Apply(s, Row("TMCO", 1, 2), 0.25).Perturbed.MultiConfiguration!;
        Assert.Equal(41.25, mc.Operands![0].Values![1].Value);
        Assert.Equal(1.5 + 0.002, Apply(s, Row("TIND", 1), 0.002).Perturbed.Surfaces![1].Index!.Value, 15);

        // A model glass takes the change on nd or Vd.
        var model = s with { Surfaces = s.Surfaces!.Select((x, j) => j == 1 ? x with { Index = null, ModelGlass = new() { Nd = 1.6, Vd = 40 } } : x).ToList() };
        Assert.Equal(39.5, Apply(model, Row("TABB", 1), -0.5).Perturbed.Surfaces![1].ModelGlass!.Vd);

        // TPAR and TPAI on an Even Asphere's α2 (parameter 2): P + Δ and 1/(1/P + Δ).
        var asphere = s with { Surfaces = s.Surfaces!.Select((x, j) => j == 2 ? x with { Type = "Even Asphere", Parameters = new() { new() { Number = 2, Value = 4e-5 } } } : x).ToList() };
        Assert.Equal(4e-5 + 1e-6, SavedSurfaces.Value(Apply(asphere, Row("TPAR", 2, 2), 1e-6).Perturbed.Surfaces![2], 2), 1e-18);
        Assert.Equal(1 / (1 / 4e-5 + 100), SavedSurfaces.Value(Apply(asphere, Row("TPAI", 2, 2), 100).Perturbed.Surfaces![2], 2), 1e-18);

        // TUTX on an existing coordinate break adds to its tilt about x (parameter 3).
        var cb = s with
        {
            Surfaces = s.Surfaces!.Take(3).Append(new OpticsSurfaceSettings { Type = SurfaceTypes.CoordinateBreak }).Concat(s.Surfaces!.Skip(3)).ToList(),
            MultiConfiguration = null, MeritFunction = null,
        };
        Assert.Equal(0.2, SavedSurfaces.Value(Apply(cb, Row("TUTX", 3), 0.2).Perturbed.Surfaces![3], 3));
        Assert.Throws<ArgumentException>(() => Apply(cb, Row("TUTX", 2), 0.2));
    }

    // ------------------------------------------------------------------ the Irregular surface

    [Fact]
    public void SurfaceDecenterTiltAndTir_ConvertToTheIrregularSurface()
    {
        var s = Doublet();
        var x = Apply(s, Row("TSDX", 1), 0.05).Perturbed.Surfaces![1];
        Assert.Equal("Irregular", x.Type);
        Assert.Equal(0.05, SavedSurfaces.Value(x, 1));
        Assert.Equal(50, x.Radius);
        Assert.Equal(0.1, SavedSurfaces.Value(Apply(s, Row("TSTY", 1), 0.1).Perturbed.Surfaces![1], 4));

        // TIRX: θy = atan(ΔZx/S), ΔZ = TIR/2 at the semi-diameter S = 10 ("TIRX, TIRY").
        double tir = 0.02;
        Assert.Equal(Math.Atan(tir / 2 / 10) * 180 / Math.PI, SavedSurfaces.Value(Apply(s, Row("TIRX", 1), tir).Perturbed.Surfaces![1], 4), 15);
        Assert.Equal(Math.Atan(tir / 2 / 10) * 180 / Math.PI, SavedSurfaces.Value(Apply(s, Row("TIRY", 1), tir).Perturbed.Surfaces![1], 3), 15);

        // TSDR along the drawn direction.
        var r = Apply(s, Row("TSDR", 1), 0.1, new ToleranceApplyOptions { DirectionDegrees = 0 }).Perturbed.Surfaces![1];
        Assert.Equal(0.1, SavedSurfaces.Value(r, 1), 15);
        Assert.Equal(0, SavedSurfaces.Value(r, 2), 15);

        // The perturbed lens traces: the Irregular surface builds.
        _ = Place(Apply(s, Row("TIRY", 1), tir).Perturbed);
    }

    [Fact]
    public void Tirr_OpticStudioModelIsTheHelpsFormula_AndTheDefaultRemovesPower()
    {
        // OpticStudio's formula ΔZ = (λt/2)(W/2·ρ⁴ + W/2·ρ′y²) ("TIRR"): Zs = Za = λt·W/4 in lens units (mm).
        var s = Doublet();
        double lambdaMm = 0.6328e-3, w = 2;
        var rows = new[] { Row("TWAV", min: 0.6328), Row("TIRR", 1) };
        var os = new TolerancePerturber(s, NoCatalogs).Apply(s, new ToleranceTable(rows, IrregularityModel.OpticStudio), 1, w).Perturbed.Surfaces![1];
        Assert.Equal(lambdaMm * w / 4, SavedSurfaces.Value(os, 5), 1e-18);
        Assert.Equal(lambdaMm * w / 4, SavedSurfaces.Value(os, 6), 1e-18);
        var pr = new TolerancePerturber(s, NoCatalogs).Apply(s, new ToleranceTable(rows), 1, w).Perturbed.Surfaces![1];
        Assert.Equal(lambdaMm * w / 4 * 32 / 17, SavedSurfaces.Value(pr, 5), 1e-18);
    }

    [Fact]
    public void TirrShape_HasAPowerRemovedPeakToValleyOf17Over32()
    {
        // Independent check of IrregularityModels.TirrPowerRemovedPeakToValley: least-squares removal
        // of 1, x, y, ρ² from f = ρ⁴/2 + y²/2 on a fine grid of the unit disk (MathNet as the oracle).
        const int n = 801;
        var points = new List<(double X, double Y)>();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double x = -1 + 2.0 * i / (n - 1), y = -1 + 2.0 * k / (n - 1);
                if (x * x + y * y <= 1) points.Add((x, y));
            }
        var a = MathNet.Numerics.LinearAlgebra.Matrix<double>.Build.Dense(points.Count, 4, (r, c) =>
        {
            var (x, y) = points[r];
            return c switch { 0 => 1, 1 => x, 2 => y, _ => x * x + y * y };
        });
        var f = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.Dense(points.Count, r =>
        {
            var (x, y) = points[r];
            double rho2 = x * x + y * y;
            return rho2 * rho2 / 2 + y * y / 2;
        });
        var residual = f - a * a.QR().Solve(f);
        // The grid (spacing 1/400) approximates the disk's area weight; on it the fit moves the
        // peak-to-valley by about 1e-5 from the exact value, so 1e-4 still tells 17/32 from any
        // neighbouring simple fraction.
        Assert.Equal(IrregularityModels.TirrPowerRemovedPeakToValley, residual.Maximum() - residual.Minimum(), 1e-4);
    }

    [Fact]
    public void Tezi_SetsEqualTermsWhoseRootSumSquareIsTheRms_OnAToroidalSurface()
    {
        var s = Doublet();
        var toroid = s with { Surfaces = s.Surfaces!.Select((x, j) => j == 2 ? x with { Type = "Toroidal" } : x).ToList() };
        var t = Apply(toroid, Row("TEZI", 2, 11, 4), 1e-4).Perturbed.Surfaces![2];
        double c = 1e-4 / Math.Sqrt(8);
        for (int k = 4; k <= 11; k++) Assert.Equal(c, SavedSurfaces.Value(t, 14 + k), 1e-18);
        Assert.Equal(0, SavedSurfaces.Value(t, 14 + 3));
        Assert.Equal(11, SavedSurfaces.Value(t, 13));
        Assert.Equal(10, SavedSurfaces.Value(t, 14));                  // the clear semi-diameter
        Assert.Equal(1e-4, Math.Sqrt(Enumerable.Range(4, 8).Sum(k => Math.Pow(SavedSurfaces.Value(t, 14 + k), 2))), 15);
        Assert.Throws<NotSupportedException>(() => Apply(s, Row("TEZI", 2, 11, 4), 1e-4));
    }

    // ------------------------------------------------------------------ element tolerances (the gate)

    /// <summary>The rigid motion an element tilt by <paramref name="degrees"/> about x through the
    /// pivot (lens units in the group's start frame) makes, in the global frame (meters).</summary>
    private static RigidTransform PivotRotation(RigidTransform start, Vector3D pivotMeters, RigidTransform rotation)
    {
        var local = RigidTransform.Compose(RigidTransform.FromTranslation(pivotMeters),
            RigidTransform.Compose(rotation, RigidTransform.FromTranslation(-pivotMeters)));
        return RigidTransform.Compose(start, RigidTransform.Compose(local, start.Inverse()));
    }

    [Theory]
    [InlineData(0, 0.0)]       // front vertex: the pivot is surface 3's vertex
    [InlineData(1, 3.0)]       // MPVT 1, rear vertex: surface 4's vertex, 3 mm behind
    [InlineData(4, 1.5)]       // MPVT 4, geometric center: midway
    public void Gate_AOneArcMinuteElementTilt_RotatesBothSurfacesAboutThePivot(int pivot, double pivotZmm)
    {
        var s = Doublet();
        var table = Table(Row("MPVT", pivot), Row("TETX", 3, 4, min: -1.0 / 60, max: 1.0 / 60));
        const double degrees = 1.0 / 60;                                     // 1 arc-minute
        var change = new TolerancePerturber(s, NoCatalogs).Apply(s, table, 1, degrees);
        var before = Place(s);
        var after = Place(change.Perturbed);

        var start = before.VertexFrame(3);
        var motion = PivotRotation(start, new Vector3D(0, 0, pivotZmm * 1e-3), RigidTransform.RotationAboutX(degrees * Math.PI / 180));
        foreach (int j in new[] { 3, 4 })
        {
            int m = change.MapSurface(j);
            Near(RigidTransform.Compose(motion, before.Global(j)), after.Global(m), 1e-14);
            // The angle between the old and new surface axes is the tilt.
            var z0 = before.Global(j).Col2;
            var z1 = after.Global(m).Col2;
            double angle = Math.Atan2(Vector3D.Cross(z0, z1).Length, Vector3D.Dot(z0, z1));
            Assert.Equal(degrees * Math.PI / 180, angle, 14);
            // The distance from the pivot is kept.
            var p = start.Apply(new Vector3D(0, 0, pivotZmm * 1e-3));
            Assert.Equal((before.Global(j).Translation - p).Length, (after.Global(m).Translation - p).Length, 14);
        }
        // The surfaces before and after the group stay where they were.
        foreach (int j in new[] { 1, 2, 5 })
            Near(before.Global(j), after.Global(change.MapSurface(j)), 1e-14);
        // Removing the perturbation restores the record bitwise.
        Assert.Equal(Json(s), Json(change.Remove(change.Perturbed)));
    }

    [Fact]
    public void ElementDecenterAndZTilt_MoveTheGroupRigidly()
    {
        var s = Doublet();
        var before = Place(s);
        var dx = Apply(s, Row("TEDX", 1, 2), 0.1);
        var after = Place(dx.Perturbed);
        foreach (int j in new[] { 1, 2 })
            Near(before.Global(j).Translation + new Vector3D(1e-4, 0, 0), after.Global(dx.MapSurface(j)).Translation, 1e-14);
        foreach (int j in new[] { 3, 4, 5 })
            Near(before.Global(j), after.Global(dx.MapSurface(j)), 1e-14);

        var tz = Apply(s, Row("TETZ", 3, 4), 2);
        var motion = PivotRotation(before.VertexFrame(3), default, RigidTransform.RotationAboutZ(2 * Math.PI / 180));
        var placed = Place(tz.Perturbed);
        foreach (int j in new[] { 3, 4 }) Near(RigidTransform.Compose(motion, before.Global(j)), placed.Global(tz.MapSurface(j)), 1e-14);
    }

    [Fact]
    public void RollAngle_TurnsTheElementAboutTheRollSurfacesCenterOfCurvature()
    {
        // TARY 3 4 rolling about surface 4 (R = 80 mm, its vertex 3 mm after surface 3's): the pivot
        // is at 83 mm, and the roll in +y is the right-handed turn about x.
        var s = Doublet();
        const double degrees = 0.5;
        var change = Apply(s, Row("TARY", 3, 4, 4), degrees);
        var before = Place(s);
        var after = Place(change.Perturbed);
        var motion = PivotRotation(before.VertexFrame(3), new Vector3D(0, 0, 0.083), RigidTransform.RotationAboutX(degrees * Math.PI / 180));
        foreach (int j in new[] { 3, 4 }) Near(RigidTransform.Compose(motion, before.Global(j)), after.Global(change.MapSurface(j)), 1e-14);

        // TARX rolls toward +x: the turn about −y.
        var x = Apply(s, Row("TARX", 3, 4, 4), degrees);
        var mx = PivotRotation(before.VertexFrame(3), new Vector3D(0, 0, 0.083), RigidTransform.RotationAboutY(-degrees * Math.PI / 180));
        var px = Place(x.Perturbed);
        foreach (int j in new[] { 3, 4 }) Near(RigidTransform.Compose(mx, before.Global(j)), px.Global(x.MapSurface(j)), 1e-14);
        Assert.Equal(Json(s), Json(x.Remove(x.Perturbed)));
    }

    [Fact]
    public void RollTir_IsTheReadSurfacesSagDifferenceAtItsSemiDiameter()
    {
        // TRLY 3 4 rolling about surface 3 (R = -30 mm); the read surface is 4 (R = 80 mm, its vertex
        // 3 mm after surface 3's, semi-diameter 9 mm). The TIR is recomputed here from the perturbed
        // placement: the read surface is the sphere of radius R about its rolled center, and z(+-h)
        // its axial position at y = +-h in the group's frame.
        var s = Doublet();
        const double tir = 0.004;                                          // mm
        var change = Apply(s, Row("TRLY", 3, 4, 3), tir);
        var before = Place(s);
        var after = Place(change.Perturbed);
        var start = before.VertexFrame(3);
        var g = after.Global(change.MapSurface(4));
        double r = 80e-3, h = 9e-3;
        var center = start.InverseApply(g.Apply(new Vector3D(0, 0, r)));
        double Z(double y) => center.Z - r * Math.Sqrt(1 - (center.X * center.X + (y - center.Y) * (y - center.Y)) / (r * r));
        Assert.Equal(tir * 1e-3, Z(h) - Z(-h), 14);
        Assert.Equal(Json(s), Json(change.Remove(change.Perturbed)));
        // Surface 3's conic is a pickup, so a roll read on it is refused: the saved value is not the traced one.
        Assert.Contains("pickup", Assert.Throws<ArgumentException>(() => Apply(s, Row("TRLY", 3, 4, 4), tir)).Message);
    }

    // ------------------------------------------------------------------ renumbering and removal

    [Fact]
    public void AWrap_RenumbersWhatNamesTheSurfacesAfterIt()
    {
        var s = Doublet();
        var change = Apply(s, Row("TETX", 1, 2), 0.1);
        var p = change.Perturbed;
        int s3 = change.MapSurface(3), s4 = change.MapSurface(4);
        Assert.True(s3 > 3);
        Assert.Equal(change.MapSurface(1), SurfaceFlagsOf(p.Surfaces![s3]).PickupSurface);
        Assert.Equal(s4, p.MultiConfiguration!.Operands![0].Number1);
        Assert.Equal(s4, p.MeritFunction!.Operands![0].Int1);
        // The configuration's thickness still lands on surface 4, and the lens of each configuration builds.
        Assert.Equal(41e-3, MultiConfiguration.LensFor(p, 2, NoCatalogs)!.Surfaces[s4].Thickness, 15);
    }

    private static OpticsCellFlagSettings SurfaceFlagsOf(OpticsSurfaceSettings s) => s.Flags!.Single();

    [Theory]
    [InlineData("TRAD", 2, 0, 0, 0.3)]
    [InlineData("TCUR", 1, 0, 0, 1e-4)]
    [InlineData("TFRN", 2, 0, 0, 2.0)]
    [InlineData("TTHI", 1, 2, 0, 0.05)]
    [InlineData("TCON", 1, 0, 0, 0.1)]
    [InlineData("TSDI", 2, 0, 0, 0.2)]
    [InlineData("TSDY", 2, 0, 0, 0.01)]
    [InlineData("TSTX", 4, 0, 0, 0.05)]
    [InlineData("TIRR", 4, 0, 0, 1.0)]
    [InlineData("TIND", 3, 0, 0, 0.001)]
    [InlineData("TMCO", 1, 2, 0, 0.5)]
    [InlineData("TEDY", 1, 4, 0, 0.02)]
    [InlineData("TETY", 1, 1, 0, 0.1)]
    [InlineData("TEDR", 3, 4, 0, 0.02)]
    [InlineData("TARR", 1, 2, 1, 0.1)]
    public void ApplyingThenRemoving_RestoresTheRecordBitwise(string code, int i1, int i2, int i3, double value)
    {
        var s = Doublet();
        var change = Apply(s, Row(code, i1, i2, i3), value);
        Assert.NotEqual(Json(s), Json(change.Perturbed));
        Assert.Equal(Json(s), Json(change.Remove(change.Perturbed)));
        _ = Place(change.Perturbed);                                        // and the perturbed lens is valid
    }

    [Fact]
    public void ChainedChanges_NestAndComeOutLastInFirstOut()
    {
        // TETX 1 4 then TETX 1 2 nested inside it (its numbers mapped through the first change), then TRAD 3.
        var s = Doublet();
        var p = new TolerancePerturber(s, NoCatalogs);
        var t = Table(Row("TETX", 1, 4), Row("TETX", 1, 2), Row("TRAD", 3));
        var a = p.Apply(s, t, 0, 0.1);
        var b = p.Apply(a.Perturbed, t, 1, -0.05, new ToleranceApplyOptions { SurfaceMap = a.MapSurface });
        var c = p.Apply(b.Perturbed, t, 2, 0.5, new ToleranceApplyOptions { SurfaceMap = k => b.MapSurface(a.MapSurface(k)) });
        int r3 = b.MapSurface(a.MapSurface(3));
        Assert.Equal(-29.5, c.Perturbed.Surfaces![r3].Radius!.Value, 12);
        _ = Place(c.Perturbed);
        var back = a.Remove(b.Remove(c.Remove(c.Perturbed)));
        Assert.Equal(Json(s), Json(back));
    }
}
