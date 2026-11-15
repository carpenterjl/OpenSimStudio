using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Composite;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-32: composite add-on surfaces (docs/Optics-Conventions.md §58). Gate (plan): a base plus an
/// add-on equals the single surface whose sag is their sum; an add-on with zero sag changes
/// nothing. Also: an ignored add-on equals no add-on, a decentered add-on equals the hand-shifted
/// sum, and a tilted add-on adds its departure from its own tilted vertex plane (§58's reading,
/// Conventions §10 item 54).
/// <para>
/// The semantics are those of OpticStudio 2025 R2 help, "Composite Surface", "Activate Composite
/// Add-ons" and "Ignore Composite Add-ons" (saved under plans/sources/optics/op-32-help/). Every
/// expected value is derived in the test from its inputs (the derivation is beside it); the
/// prescriptions and coefficients are arbitrary test inputs, not reference data.
/// </para>
/// </summary>
public sealed class CompositeSurfaceTests
{
    private const double C = 1 / 0.05;                       // base sphere, R = 50 mm
    private static readonly double[] Alpha = { 0.8, -120, 3.5e4 };   // α1 (1/m), α2 (1/m³), α3 (1/m⁵)

    private static OpticalSystemData Sys() => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.008 },
        Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint() } },
    };

    /// <summary>Object at infinity, a plane stop, the test surface into n = 1.5, a plane, image.</summary>
    private static SequentialLens Lens(SequentialSurface test) => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            new SequentialSurface { Thickness = 0.002, IsStop = true },
            test with { Thickness = 0.005, Medium = ParaxialMedium.Constant(1.5) },
            new SequentialSurface { Thickness = 0.03 },
            new SequentialSurface(),
        },
    };

    private static CompositeAddOn Departure(double dx = 0, double dy = 0, bool ignored = false) => new()
    {
        Shape = new EvenAsphere(Alpha),
        Placement = new TiltDecenter(dx, dy, 0, 0, 0),
        IsIgnored = ignored,
    };

    /// <summary>Every ray of a small grid at two angles through both lenses, its final
    /// position, direction and optical path compared with <paramref name="compare"/>.</summary>
    private static int TraceBoth(SequentialLens a, SequentialLens b, Action<Ray, Ray> compare)
    {
        var ta = SequentialTrace.Create(a, Sys());
        var tb = SequentialTrace.Create(b, Sys());
        int ok = 0;
        foreach (double angle in new[] { 0.0, 0.05 })
            for (double x = -0.003; x <= 0.0031; x += 0.0015)
                for (double y = -0.003; y <= 0.0031; y += 0.0015)
                {
                    var dir = new Vector3D(0, Math.Sin(angle), Math.Cos(angle));
                    var ra = ta.Trace(ta.NewRay(new Vector3D(x, y, 0), dir));
                    var rb = tb.Trace(tb.NewRay(new Vector3D(x, y, 0), dir));
                    Assert.Equal(ra.Status, rb.Status);
                    compare(ra, rb);
                    if (ra.Status == RayStatus.Ok) ok++;
                }
        Assert.True(ok > 0, "no ray reached the image");
        return ok;
    }

    private static void Bitwise(Ray a, Ray b)
    {
        foreach (var (p, q) in new[] { (a.Position.X, b.Position.X), (a.Position.Y, b.Position.Y), (a.Position.Z, b.Position.Z),
                     (a.Direction.X, b.Direction.X), (a.Direction.Y, b.Direction.Y), (a.Direction.Z, b.Direction.Z), (a.OpticalPath, b.OpticalPath) })
            Assert.Equal(BitConverter.DoubleToInt64Bits(p), BitConverter.DoubleToInt64Bits(q));
    }

    private static Action<Ray, Ray> Within(double meters) => (a, b) =>
    {
        Assert.True((a.Position - b.Position).Length <= meters, $"positions differ by {(a.Position - b.Position).Length:g3} m");
        Assert.True((a.Direction - b.Direction).Length <= meters / 0.03, $"directions differ by {(a.Direction - b.Direction).Length:g3}");
        Assert.True(Math.Abs(a.OpticalPath - b.OpticalPath) <= meters, $"optical paths differ by {a.OpticalPath - b.OpticalPath:g3} m");
    };

    // ------------------------------------------------------------------ gate

    /// <summary>Gate: a sphere plus an Even Asphere departure add-on (no radius, the α terms)
    /// is the Even Asphere of that radius and those α: sag and normal agree at points, traced
    /// rays agree to 1e-12 m (the two evaluate the same sum in a different order, so they differ
    /// by rounding only), and the paraxial curvature c + 2α1 is the same number.</summary>
    [Fact]
    public void Gate_ABasePlusAnAddOn_EqualsTheSingleSurfaceOfTheSummedSag()
    {
        var composite = new SequentialSurface { Curvature = C, AddOns = new[] { Departure() } };
        var single = new SequentialSurface { Curvature = C, Shape = new EvenAsphere(Alpha) };

        var shape = Assert.IsType<CompositeSurfaceShape>(CompositeSurfaces.EffectiveShape(composite));
        foreach (var (x, y) in new[] { (0.0, 0.0), (0.001, 0.0), (-0.002, 0.003), (0.0035, -0.001) })
        {
            Assert.True(shape.TrySag(x, y, composite, out double z, out double zx, out double zy));
            Assert.True(((SagSurfaceShape)single.Shape!).TrySag(x, y, single, out double z1, out double zx1, out double zy1));
            Assert.True(Math.Abs(z - z1) <= 1e-18, $"sag at ({x}, {y}): {z:R} vs {z1:R}");
            Assert.True(Math.Abs(zx - zx1) <= 1e-15 && Math.Abs(zy - zy1) <= 1e-15, $"slopes at ({x}, {y})");
        }

        TraceBoth(Lens(composite), Lens(single), Within(1e-12));

        var pc = ParaxialSystem.Create(Lens(composite).ToParaxialLens(), Sys());
        var ps = ParaxialSystem.Create(Lens(single).ToParaxialLens(), Sys());
        Assert.NotNull(ps.EffectiveFocalLength);
        Assert.Equal(ps.EffectiveFocalLength, pc.EffectiveFocalLength);
        Assert.Null(shape.OpdLimitation(composite));
    }

    /// <summary>Gate: an add-on whose sag is zero everywhere changes nothing, bit for bit: a
    /// Standard plane (decentered and tilted, which a plane ignores) on a sphere, and an Even
    /// Asphere with every coefficient zero, decentered, on an Even Asphere (traced as a composite,
    /// whose zero terms leave the base's bits).</summary>
    [Fact]
    public void Gate_AnAddOnWithZeroSag_ChangesNothing_BitForBit()
    {
        var sphere = new SequentialSurface { Curvature = C };
        var plane = new CompositeAddOn { Placement = new TiltDecenter(0.001, -0.002, 3, -2, 10) };
        Assert.Null(CompositeSurfaces.EffectiveShape(sphere with { AddOns = new[] { plane } }));
        TraceBoth(Lens(sphere), Lens(sphere with { AddOns = new[] { plane } }), Bitwise);

        var asphere = new SequentialSurface { Curvature = C, Shape = new EvenAsphere(Alpha) };
        var zero = new CompositeAddOn { Shape = new EvenAsphere(new double[] { 0, 0, 0 }), Placement = new TiltDecenter(0.001, -0.002, 0, 0, 0) };
        var withZero = asphere with { AddOns = new[] { zero } };
        Assert.IsType<CompositeSurfaceShape>(CompositeSurfaces.EffectiveShape(withZero));
        TraceBoth(Lens(asphere), Lens(withZero), Bitwise);
        Assert.Equal(
            ParaxialSystem.Create(Lens(asphere).ToParaxialLens(), Sys()).EffectiveFocalLength,
            ParaxialSystem.Create(Lens(withZero).ToParaxialLens(), Sys()).EffectiveFocalLength);
    }

    /// <summary>An ignored add-on is no add-on ("The sag of ignored Composite Add-On surfaces
    /// are no longer added", help "Ignore Composite Add-ons"): the surface's own shape is traced,
    /// bit for bit; next to an active one, it leaves the active one's result unchanged.</summary>
    [Fact]
    public void AnIgnoredAddOn_EqualsNoAddOn_BitForBit()
    {
        var sphere = new SequentialSurface { Curvature = C };
        var ignored = sphere with { AddOns = new[] { Departure(ignored: true) } };
        Assert.False(CompositeSurfaces.HasActiveAddOns(ignored));
        Assert.Null(CompositeSurfaces.EffectiveShape(ignored));
        TraceBoth(Lens(sphere), Lens(ignored), Bitwise);

        var one = sphere with { AddOns = new[] { Departure() } };
        var oneAndIgnored = sphere with { AddOns = new[] { Departure(0.002, 0, ignored: true), Departure() } };
        TraceBoth(Lens(one), Lens(oneAndIgnored), Bitwise);
    }

    /// <summary>A sphere plus the Even Asphere departure about (x_d, y_d), written by hand
    /// (derived): z = c r²/(1 + √(1 − c²r²)) + Σ αᵢ ρ^(2i), ρ² = (x − x_d)² + (y − y_d)², with
    /// ∂/∂x = c x/√(1 − c²r²) + Σ 2i αᵢ ρ^(2i − 2) (x − x_d).</summary>
    private sealed class HandShiftedSum(double dx, double dy) : SagSurfaceShape
    {
        public override string TypeName => "Hand-shifted sum";

        public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
        {
            double c = surface.Curvature, r2 = x * x + y * y, phi = Math.Sqrt(1 - c * c * r2);
            double u = x - dx, v = y - dy, rho2 = u * u + v * v;
            z = c * r2 / (1 + phi);
            double g = c / phi;
            dzdx = g * x;
            dzdy = g * y;
            double power = 1;           // ρ^(2i − 2)
            for (int i = 1; i <= Alpha.Length; i++)
            {
                z += Alpha[i - 1] * power * rho2;
                dzdx += 2 * i * Alpha[i - 1] * power * u;
                dzdy += 2 * i * Alpha[i - 1] * power * v;
                power *= rho2;
            }
            return true;
        }
    }

    /// <summary>A decentered add-on is the hand-shifted sum: sags at points to 1e-15 m, rays to
    /// 1e-12 m; the paraxial engine refuses it, naming the add-on (it is not symmetric about the
    /// vertex), and the lens still traces.</summary>
    [Fact]
    public void ADecenteredAddOn_MatchesTheHandShiftedSum()
    {
        const double dx = 0.0012, dy = -0.0007;
        var composite = new SequentialSurface { Curvature = C, AddOns = new[] { Departure(dx, dy) } };
        var hand = new SequentialSurface { Curvature = C, Shape = new HandShiftedSum(dx, dy) };
        var shape = (SagSurfaceShape)CompositeSurfaces.EffectiveShape(composite)!;
        foreach (var (x, y) in new[] { (0.0, 0.0), (0.001, 0.0), (-0.002, 0.003), (0.0035, -0.001) })
        {
            Assert.True(shape.TrySag(x, y, composite, out double z, out double zx, out double zy));
            Assert.True(((SagSurfaceShape)hand.Shape!).TrySag(x, y, hand, out double z1, out double zx1, out double zy1));
            Assert.True(Math.Abs(z - z1) <= 1e-15, $"sag at ({x}, {y}): {z:R} vs {z1:R}");
            Assert.True(Math.Abs(zx - zx1) <= 1e-12 && Math.Abs(zy - zy1) <= 1e-12, $"slopes at ({x}, {y})");
        }
        TraceBoth(Lens(composite), Lens(hand), Within(1e-12));

        var ex = Assert.Throws<ArgumentException>(() => Lens(composite).ToParaxialLens());
        Assert.Contains("Surface 2", ex.Message);
        Assert.Contains("Composite add-on 1", ex.Message);
    }

    /// <summary>
    /// A tilted add-on adds its departure from its own tilted vertex plane, along the base's z
    /// (§58). The tolerance on δ is 1e-15 m because the closed form subtracts two numbers near R
    /// (C_z and the root), losing about ulp(R) ≈ 3e-17 m. For a Standard sphere add-on of radius R on a plane base, tilted by T (the code's
    /// own transform), the sphere's centre is C = R·n with n = Tẑ, and on the line (x, y, t)
    /// (derived) the sphere is met at t_S = C_z − √(R² − (x − C_x)² − (y − C_y)²) (the vertex
    /// root) and the tilted plane n·p = 0 at t_P = −(n_x x + n_y y)/n_z, so δ = t_S − t_P and
    /// ∂δ/∂x = (x − C_x)/√(…) + n_x/n_z (likewise y).
    /// </summary>
    [Fact]
    public void ATiltedAddOn_AddsItsDepartureFromItsOwnTiltedPlane()
    {
        const double R = 0.2;
        var placement = new TiltDecenter(0, 0, 4, -3, 25);
        var surface = new SequentialSurface { AddOns = new[] { new CompositeAddOn { Curvature = 1 / R, Placement = placement } } };
        var shape = (SagSurfaceShape)CompositeSurfaces.EffectiveShape(surface)!;
        var n = placement.ToTransform().ApplyDirection(Vector3D.UnitZ);
        var centre = n * R;
        foreach (var (x, y) in new[] { (0.0, 0.0), (0.002, 0.0), (-0.003, 0.004), (0.005, -0.002) })
        {
            double root = Math.Sqrt(R * R - (x - centre.X) * (x - centre.X) - (y - centre.Y) * (y - centre.Y));
            double expected = centre.Z - root + (n.X * x + n.Y * y) / n.Z;
            double ex = (x - centre.X) / root + n.X / n.Z, ey = (y - centre.Y) / root + n.Y / n.Z;
            Assert.True(shape.TrySag(x, y, surface, out double z, out double zx, out double zy));
            Assert.True(Math.Abs(z - expected) <= 1e-15, $"sag at ({x}, {y}): {z:R} vs {expected:R}");
            Assert.True(Math.Abs(zx - ex) <= 1e-12 && Math.Abs(zy - ey) <= 1e-12, $"slopes at ({x}, {y})");
        }
    }

    /// <summary>The sag analyses' probe sees the composite sum (as OpticStudio's Surface Sag
    /// does by default, help "Surface Sag").</summary>
    [Fact]
    public void TheSurfaceProbe_SeesTheCompositeSum()
    {
        var composite = new SequentialSurface { Curvature = C, AddOns = new[] { Departure() } };
        var single = new SequentialSurface { Curvature = C, Shape = new EvenAsphere(Alpha) };
        var a = new SurfaceProbe(composite, 1e-6);
        var b = new SurfaceProbe(single, 1e-6);
        Assert.True(Math.Abs(a.Sag(0.002, -0.001) - b.Sag(0.002, -0.001)) <= 1e-18);
    }

    // ------------------------------------------------------------------ checks

    [Fact]
    public void TypesThatCannotBeComposite_AreRefusedByName()
    {
        var paraxial = new SequentialSurface
        {
            Curvature = C,
            AddOns = new[] { new CompositeAddOn { Shape = new OpenSim.Optics.Sequential.Special.ParaxialShape(0.1), Curvature = 0 } },
        };
        var problems = Lens(paraxial).Validate();
        Assert.Contains(problems, p => p.Contains("Surface 2, composite add-on 1") && p.Contains("Paraxial"));

        var onFresnel = new SequentialSurface
        {
            Shape = new OpenSim.Optics.Sequential.Special.FresnelShape(Array.Empty<double>()),
            AddOns = new[] { Departure() },
        };
        Assert.Contains(Lens(onFresnel).Validate(), p => p.Contains("Surface 2") && p.Contains("cannot be a composite base"));

        var lens = Lens(new SequentialSurface { Curvature = C });
        var surfaces = lens.Surfaces.ToArray();
        surfaces[0] = surfaces[0] with { AddOns = new[] { Departure() } };
        Assert.Contains((lens with { Surfaces = surfaces }).Validate(), p => p.Contains("object surface cannot have composite add-ons"));
    }

    // ------------------------------------------------------------------ saved form

    private static SequentialLens SavedLens() => Lens(new SequentialSurface
    {
        Curvature = C,
        SemiDiameter = 0.005,
        AddOns = new[]
        {
            Departure(0.0004, -0.0003),
            new CompositeAddOn { Curvature = 1 / 0.3, Conic = -0.5, Placement = new TiltDecenter(0, 0.0001, 2, -1, 15), IsIgnored = true, Comment = "spare" },
            new CompositeAddOn { Shape = new EvenAsphere(new[] { 0.0, 50 }), SemiDiameter = 0.004 },
        },
    });

    /// <summary>The add-ons save and reload with the lens: the saved form is the same after a
    /// round trip, the traces are bit for bit the same, and in millimeters each length is its SI
    /// value over 1e-3 (tilts are degrees either way).</summary>
    [Fact]
    public void AddOns_RoundTripThroughTheSavedSurfaces_AndTraceBitForBit()
    {
        var lens = SavedLens();
        var saved = SavedSurfaces.FromLens(lens, LensUnit.Meter);
        Assert.Equal(3, saved[2].AddOns!.Count);
        Assert.Equal("Even Asphere", saved[2].AddOns![0].Type);
        Assert.Null(saved[2].AddOns![1].Type);
        Assert.True(saved[2].AddOns![1].IsIgnored);
        var back = SavedSurfaces.ToLens(saved, LensUnit.Meter, _ => null);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(SavedSurfaces.FromLens(back, LensUnit.Meter)));
        TraceBoth(lens, back, Bitwise);

        var mm = SavedSurfaces.FromLens(lens, LensUnit.Millimeter);
        var a0 = mm[2].AddOns![0];
        Assert.Equal(0.0004 / 1e-3, a0.DecenterX);
        Assert.Equal(-0.0003 / 1e-3, a0.DecenterY);
        Assert.Equal(15, mm[2].AddOns![1].TiltZ);
        Assert.Equal(0.004 / 1e-3, mm[2].AddOns![2].SemiDiameter);
        var fromMm = SavedSurfaces.ToLens(mm, LensUnit.Millimeter, _ => null);
        var p0 = fromMm.Surfaces[2].AddOns![0].Placement;
        Assert.True(Math.Abs(p0.DecenterX - 0.0004) <= 1e-18 && Math.Abs(p0.DecenterY + 0.0003) <= 1e-18);
    }

    [Fact]
    public void ASavedAddOnOfAnUnknownOrBreakType_IsRefusedByName()
    {
        var saved = SavedSurfaces.FromLens(SavedLens(), LensUnit.Meter);
        var unknown = saved.Select(s => s with { }).ToList();
        unknown[2] = unknown[2] with { AddOns = new() { new() { Type = "No Such Type" } } };
        var ex = Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(unknown, LensUnit.Meter, _ => null));
        Assert.Contains("Surface 2, composite add-on 1", ex.Message);
        Assert.Contains("No Such Type", ex.Message);

        var breakAddOn = saved.Select(s => s with { }).ToList();
        breakAddOn[2] = breakAddOn[2] with { AddOns = new() { new() { Type = SurfaceTypes.CoordinateBreak } } };
        ex = Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(breakAddOn, LensUnit.Meter, _ => null));
        Assert.Contains("cannot be a composite add-on", ex.Message);
    }

    /// <summary>Activate / Ignore Composite Add-ons set every add-on's flag and count the
    /// changes (help pages of those names).</summary>
    [Fact]
    public void ActivateAndIgnore_SetEveryAddOnsFlag()
    {
        var saved = SavedSurfaces.FromLens(SavedLens(), LensUnit.Meter);
        var ignored = CompositeSurfaces.SetAllIgnored(saved, ignore: true, out int changed);
        Assert.Equal(2, changed);                      // two of the three were active
        Assert.All(ignored[2].AddOns!, a => Assert.True(a.IsIgnored));
        var active = CompositeSurfaces.SetAllIgnored(ignored, ignore: false, out changed);
        Assert.Equal(3, changed);
        Assert.All(active[2].AddOns!, a => Assert.False(a.IsIgnored));
        Assert.Null(active[1].AddOns);
    }

    /// <summary>Scale Lens scales an add-on like a surface: radius, semi-diameter and decenters
    /// by the factor, a registered type's parameters by their units, tilts unchanged.</summary>
    [Fact]
    public void ScaleLens_ScalesTheAddOns()
    {
        var saved = SavedSurfaces.FromLens(SavedLens(), LensUnit.Millimeter);
        var scaled = ScaleLens.Apply(saved, 2).Surfaces;
        var before = saved[2].AddOns!;
        var after = scaled[2].AddOns!;
        Assert.Equal(before[0].DecenterX * 2, after[0].DecenterX);
        Assert.Equal(before[1].Radius * 2, after[1].Radius);
        Assert.Equal(before[1].TiltZ, after[1].TiltZ);
        Assert.Equal(before[2].SemiDiameter * 2, after[2].SemiDiameter);
        var layout = SurfaceTypes.Find("Even Asphere")!;
        foreach (var p in before[2].Parameters!)
            Assert.Equal(p.Value * SurfaceTypes.Factor(layout.Parameter(p.Number)!.LengthPower, 2),
                after[2].Parameters!.Single(q => q.Number == p.Number).Value);
    }
}
