using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-21: coordinate breaks, surface tilts and decenters, coordinate returns, global vertices,
/// and the lens tools, against closed forms derived in the comments of each test (Snell's law in
/// a tilted plate, reflection by a tilted plane mirror, the stop-shift argument for a concentric
/// mirror relay). Radii, thicknesses, angles and indices are arbitrary test inputs, not
/// reference data; no published value is used.
/// </summary>
public sealed class CoordinateBreakTests
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;   // 2^-52

    // ---- Builders ----

    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false, bool mirror = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror,
    };

    private static SequentialSurface Plane(double thickness, double n = 1, bool stop = false) =>
        S(double.PositiveInfinity, thickness, n, stop);

    private static SequentialSurface Cb(TiltDecenter values, double thickness = 0) => new()
    {
        CoordinateBreak = new CoordinateBreak(values), Thickness = thickness,
    };

    private static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd = 0.01, params double[] angleFields) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = angleFields.Length == 0 ? new[] { new FieldPoint() } : angleFields.Select(a => new FieldPoint { Y = a }).ToArray()
        },
    };

    private static void Close(double expected, double actual, double tolerance, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    private static void Close(Vector3D expected, Vector3D actual, double tolerance, string what = "")
    {
        Close(expected.X, actual.X, tolerance, what + " x");
        Close(expected.Y, actual.Y, tolerance, what + " y");
        Close(expected.Z, actual.Z, tolerance, what + " z");
    }

    private static void SameFrame(RigidTransform expected, RigidTransform actual, double tolerance, string what = "")
    {
        Close(expected.Col0, actual.Col0, tolerance, what + " x axis");
        Close(expected.Col1, actual.Col1, tolerance, what + " y axis");
        Close(expected.Col2, actual.Col2, tolerance, what + " z axis");
        Close(expected.Translation, actual.Translation, tolerance, what + " vertex");
    }

    /// <summary>A traced ray in the global frame.</summary>
    private static (Vector3D Position, Vector3D Direction) Global(SequentialTrace trace, Ray ray)
    {
        var frame = trace.GlobalPlacement(trace.Lens.ImageIndex);
        return (frame.Apply(ray.Position), frame.ApplyDirection(ray.Direction));
    }

    // ---- Gate: a tilted plane-parallel plate ----

    /// <summary>
    /// A ray along +z meets a plate of index n tilted by a: inside it makes the angle b with the
    /// plate normal, sin a = n·sin b (Snell). It runs t/cos b to the back face (t measured along
    /// the normal), at the angle a − b to its first direction, so it leaves parallel to it,
    /// displaced sideways by (t/cos b)·sin(a − b) = t·sin(a − b)/cos b. A positive tilt about x
    /// turns the plate normal to (0, −sin a, cos a) (Conventions §4); the ray bends toward the
    /// normal, so it leaves at y = −t·sin(a − b)/cos b. About y the normal is (sin a, 0, cos a)
    /// and it leaves at x = +t·sin(a − b)/cos b.
    /// </summary>
    [Theory]
    [InlineData(0, 30.0, TiltDecenterOrder.DecenterThenTilt)]
    [InlineData(0, -17.5, TiltDecenterOrder.TiltThenDecenter)]
    [InlineData(1, 41.0, TiltDecenterOrder.DecenterThenTilt)]
    [InlineData(1, 12.25, TiltDecenterOrder.TiltThenDecenter)]
    public void ATiltedPlate_DisplacesARay_BySinOfAMinusBOverCosB(int axis, double degrees, TiltDecenterOrder order)
    {
        double n = 1.6, t = 0.012;
        double a = degrees * Math.PI / 180, b = Math.Asin(Math.Sin(a) / n);
        double displacement = t * Math.Sin(a - b) / Math.Cos(b);
        var expected = axis == 0 ? new Vector3D(0, -displacement, 0) : new Vector3D(displacement, 0, 0);
        // The decenters change nothing for planes that extend without limit.
        var tilt = axis == 0 ? new TiltDecenter(0.003, -0.002, degrees, 0, 0, order) : new TiltDecenter(-0.001, 0.004, 0, degrees, 0, order);

        // As coordinate breaks: the break, the plate, the break reversed.
        var breaks = Lens(double.PositiveInfinity, Cb(tilt), Plane(t, n, stop: true), Plane(0), Cb(tilt.Reversed(), 0.05));
        // As surface tilts: before on the front face (surface 2), the front face's "before"
        // reversed after the back face. A tilt before surface 1 is not allowed with the object at
        // infinity (OpticStudio's rule), so a dummy comes first.
        var surfaceTilts = Lens(double.PositiveInfinity,
            Plane(0),
            Plane(t, n, stop: true) with { TiltBefore = tilt },
            Plane(0.05) with { TiltAfter = SurfaceTiltAfter.Reverse(2) });

        foreach (var lens in new[] { breaks, surfaceTilts })
        {
            Assert.Empty(lens.Validate());
            var trace = SequentialTrace.Create(lens, Sys());
            var ray = trace.Trace(trace.NewRay(Vector3D.Zero, Vector3D.UnitZ));
            Assert.True(ray.IsOk, $"{ray.Status} at {ray.FailedAtSurface}");
            var (p, d) = Global(trace, ray);
            double tol = 64 * Eps * t;
            Close(0, d.X, 8 * Eps, "direction x");
            Close(0, d.Y, 8 * Eps, "direction y");
            // The line leaves parallel to z: its transverse position is the displacement.
            Close(expected.X, p.X, tol, "displacement x");
            Close(expected.Y, p.Y, tol, "displacement y");
            // Reversing the break restores the orientation exactly in rotation.
            SameFrame(RigidTransform.Identity with { Translation = trace.GlobalPlacement(lens.ImageIndex).Translation },
                trace.GlobalPlacement(lens.ImageIndex), 8 * Eps, "image frame");
        }
    }

    // ---- Gate: a fold mirror pair returns the axis ----

    /// <summary>
    /// A plane mirror tilted by 45° about x between two breaks of 45° (Add Fold Mirror with a
    /// reflect angle of 90°) has the normal (0, −sin 45°, cos 45°); a ray along +z reflects to
    /// d − 2(d·n)n = (0, 1, 0). The frame after the fold is Rx(90°), whose z is (0, −1, 0), so
    /// the ray travels along its −z and the thicknesses after it are negative. A second fold of
    /// −90° (tilts −45° in the turned frame, Rx(90°)·Rx(−45°) = Rx(45°)) reflects (0, 1, 0) back
    /// to (0, 0, 1). The axis leaves parallel to the one that came in, displaced by the distance
    /// L between the folds along +y, and the image lies L nearer along z than in the unfolded
    /// lens: its vertex moves by (0, L, −L).
    /// <para>
    /// Each fold maps a point (x, y, z) of the unfolded space after it to (x, y, −z) in the
    /// folded local frame (the reflection about the mirror plane is Rx(θ)·diag(1, 1, −1)), so
    /// after two folds the local image coordinates and directions equal the unfolded lens's.
    /// </para>
    /// </summary>
    [Fact]
    public void AFoldMirrorPair_ReturnsTheAxis_AndImagesAsTheUnfoldedLens()
    {
        double gap = 0.04;
        var unfolded = Lens(double.PositiveInfinity,
            S(0.1, 0.005, 1.5, stop: true), S(-0.1, 0.05), Plane(gap), Plane(0.03), Plane(0.02));
        var once = CoordinateBreakTools.AddFoldMirror(unfolded, 3, 90, FoldTiltAxis.X);
        // The second dummy (surface 4) is now surface 6, after the break, the mirror and the break.
        Assert.True(once.Surfaces[4].IsMirror);
        Assert.Equal(-gap, once.Surfaces[5].Thickness);
        Assert.Equal(-0.03, once.Surfaces[6].Thickness);
        var folded = CoordinateBreakTools.AddFoldMirror(once, 6, -90, FoldTiltAxis.X);

        var system = Sys(0.01, 0, 2);
        var traceU = SequentialTrace.Create(unfolded, system);
        var traceF = SequentialTrace.Create(folded, system);
        double tol = 1e-15;

        // The image surface's frame: same axes, vertex moved by (0, L, −L).
        var imageU = traceU.GlobalPlacement(unfolded.ImageIndex);
        var imageF = traceF.GlobalPlacement(folded.ImageIndex);
        SameFrame(imageU with { Translation = imageU.Translation + new Vector3D(0, gap, -gap) }, imageF, tol, "image frame");

        // The axis ray: back along +z, displaced by L in y.
        var axisF = traceF.Trace(traceF.NewRay(Vector3D.Zero, Vector3D.UnitZ));
        var (p, d) = Global(traceF, axisF);
        Close(Vector3D.UnitZ, d, 16 * Eps, "axis direction");
        Close(gap, p.Y, tol, "axis height");

        // The launcher works on the folded lens (its paraxial model is the unfolded one), and every
        // ray lands where it lands in the unfolded lens, in local image coordinates.
        var launchU = new RayLauncher(unfolded, system);
        var launchF = new RayLauncher(folded, system);
        foreach (var (hy, px, py) in new[] { (0.0, 0.0, 0.0), (0.0, 0.0, 1.0), (1.0, 0.7, -0.3), (1.0, -1.0, 0.0) })
        {
            var u = traceU.Trace(launchU.Launch(traceU, 0, hy, px, py));
            var f = traceF.Trace(launchF.Launch(traceF, 0, hy, px, py));
            Assert.True(u.IsOk && f.IsOk);
            Close(u.Position, f.Position, tol, "image point");
            Close(u.Direction, f.Direction, 16 * Eps, "image direction");
            Close(u.OpticalPath, f.OpticalPath, tol, "optical path");
        }

        // The paraxial model of the folded lens is the unfolded one.
        var pu = ParaxialSystem.Create(unfolded.ToParaxialLens(), system);
        var pf = ParaxialSystem.Create(folded.ToParaxialLens(), system);
        Close(pu.EffectiveFocalLength!.Value, pf.EffectiveFocalLength!.Value, 1e-14, "EFL");
        Assert.True(folded.IsAxial(out _));

        // Deleting both folds gives a lens that traces as the unfolded one.
        var back = CoordinateBreakTools.DeleteFoldMirror(CoordinateBreakTools.DeleteFoldMirror(folded, 7), 4);
        Assert.Equal(unfolded.Surfaces.Count - 2, back.Surfaces.Count);   // the two fold dummies are gone
        Assert.DoesNotContain(back.Surfaces, s => s.IsCoordinateBreak || s.IsMirror);
        var traceB = SequentialTrace.Create(back, system);
        SameFrame(imageU, traceB.GlobalPlacement(back.ImageIndex), tol, "image frame after deleting");
    }

    /// <summary>A mirror with a 45° tilt before and the same tilt picked up after it (option b of
    /// the help's after-surface options) is the fold Add Fold Mirror builds with two breaks.</summary>
    [Fact]
    public void AFoldMirrorFromSurfaceTilts_TracesAsTheOneFromBreaks()
    {
        var unfolded = Lens(double.PositiveInfinity, S(0.1, 0.005, 1.5, stop: true), S(-0.1, 0.05), Plane(0.04), Plane(0.03));
        var fromBreaks = CoordinateBreakTools.AddFoldMirror(unfolded, 3, 90, FoldTiltAxis.Y);
        var fromTilts = Lens(double.PositiveInfinity, S(0.1, 0.005, 1.5, stop: true), S(-0.1, 0.05),
            new SequentialSurface
            {
                IsMirror = true, Thickness = -0.04,
                TiltBefore = new TiltDecenter(0, 0, 0, 45, 0), TiltAfter = SurfaceTiltAfter.PickUpThis,
            },
            Plane(-0.03));
        var system = Sys(0.01, 0, 2);
        var a = SequentialTrace.Create(fromBreaks, system);
        var b = SequentialTrace.Create(fromTilts, system);
        SameFrame(a.GlobalPlacement(fromBreaks.ImageIndex), b.GlobalPlacement(fromTilts.ImageIndex), 1e-15, "image frame");
        var ray = new Vector3D(0.001, -0.002, 1);
        var ra = a.Trace(a.NewRay(new Vector3D(0.002, 0.001, 0), ray));
        var rb = b.Trace(b.NewRay(new Vector3D(0.002, 0.001, 0), ray));
        Close(ra.Position, rb.Position, 1e-15, "image point");
        Close(ra.Direction, rb.Direction, 16 * Eps, "direction");
    }

    // ---- Gate: an Offner relay built with breaks ----

    /// <summary>
    /// The Offner relay: a concave mirror of radius R1 = 0.2 m, a convex one of R1/2 (the stop)
    /// and the concave one again, all concentric about C; object and image planes pass through C.
    /// It is built here with breaks: a break decenters the mirrors' axis by −h from the launch
    /// frame, so the object point at the launch origin is at +h from C's axis, and a break
    /// before the image decenters by −h again, centring the image of the field point (at −h in
    /// the mirrors' frame, magnification −1) on the image surface.
    /// <para>
    /// <b>Third order (derived).</b> A ray from C meets each mirror along its normal, so the
    /// paraxial incidence A = n(y·c + u) of the marginal ray from C is zero at every mirror, and
    /// with it every surface's Seidel spherical term S1 ∝ A². By the stop-shift relations
    /// S2* = S2 + E·S1 and S3* = S3 + 2E·S2 + E²·S1, and S2 = S3 = 0 for a surface whose stop is at
    /// its centre of curvature (the chief ray along a normal), each mirror's astigmatism is zero
    /// for any stop, so the relay's third-order astigmatism is zero. Third-order astigmatism grows
    /// as h²; the next order as h⁴. The tangential-minus-sagittal focus separation therefore
    /// grows sixteen-fold, not four-fold, when h doubles. That is tested; the coefficients of the
    /// higher orders are not derived, so the ratio is allowed 14 to 18.5. With two radii in the
    /// ratio 2 the Petzval sum 2/R1 − 2/R2 + 2/R1 = 0 as well; field flatness is not tested.
    /// </para>
    /// <para>
    /// The same relay without breaks, the object at y = h, traces identically (shifted by h),
    /// and C itself is imaged onto C to rounding.
    /// </para>
    /// </summary>
    [Fact]
    public void AnOffnerRelayBuiltWithBreaks_IsAnastigmaticToThirdOrder()
    {
        double r1 = 0.2, r2 = 0.1;
        SequentialLens Offner(double h) => Lens(0,
            Cb(new TiltDecenter(0, -h, 0, 0, 0), r1),
            S(-r1, -(r1 - r2), mirror: true),
            S(-r2, r1 - r2, mirror: true, stop: true),
            S(-r1, -r1, mirror: true),
            Cb(new TiltDecenter(0, -h, 0, 0, 0)));
        var system = Sys();

        // Not axial: the paraxial model refuses it, naming the surface.
        var offAxis = Offner(0.01);
        var refusal = Assert.Throws<InvalidOperationException>(() => offAxis.ToParaxialLens());
        Assert.Contains("surface 2", refusal.Message);
        Assert.False(offAxis.IsAxial(out _));

        // The same relay with the object at +h instead of the breaks.
        var axial = Offner(0);
        var traceA = SequentialTrace.Create(axial, system);
        var traceB = SequentialTrace.Create(offAxis, system);
        foreach (var d in new[] { Vector3D.UnitZ, new Vector3D(0.02, -0.03, 1), new Vector3D(-0.05, 0.04, 1) })
        {
            var ra = traceA.Trace(traceA.NewRay(new Vector3D(0, 0.01, 0), d));
            var rb = traceB.Trace(traceB.NewRay(Vector3D.Zero, d));
            Assert.True(ra.IsOk && rb.IsOk);
            Close(ra.Position.X, rb.Position.X, 1e-16, "x");
            Close(ra.Position.Y + 0.01, rb.Position.Y, 1e-16, "y");
            Close(ra.Direction, rb.Direction, 4 * Eps, "direction");
            // From C every ray returns to C.
            var c = traceA.Trace(traceA.NewRay(Vector3D.Zero, d));
            Close(0, c.Position.X, 1e-15, "C imaged on C, x");
            Close(0, c.Position.Y, 1e-15, "C imaged on C, y");
        }

        double Separation(double h)
        {
            var trace = SequentialTrace.Create(Offner(h), system);
            double delta = 1e-4;
            var central = trace.Trace(trace.NewRay(Vector3D.Zero, Vector3D.UnitZ));
            double Focus(Vector3D offset)
            {
                double sum = 0;
                foreach (int sign in new[] { 1, -1 })
                {
                    var r = trace.Trace(trace.NewRay(Vector3D.Zero, Vector3D.UnitZ + offset * (sign * delta)));
                    sum += ClosestApproach(central.Position, central.Direction, r.Position, r.Direction);
                }
                return sum / 2;
            }
            return Focus(Vector3D.UnitY) - Focus(Vector3D.UnitX);
        }
        double s1 = Separation(0.005), s2 = Separation(0.01), s4 = Separation(0.02);
        Assert.True(Math.Abs(s2) > 1e-7, $"the separation at h = 10 mm is {s2:R}; the fifth-order term was expected to be measurable");
        double ratio1 = s2 / s1, ratio2 = s4 / s2;
        Assert.InRange(ratio1, 14, 18.5);
        Assert.InRange(ratio2, 14, 18.5);
    }

    /// <summary>The distance along line 1 (p1 + s·d1) to its closest approach to line 2.</summary>
    private static double ClosestApproach(Vector3D p1, Vector3D d1, Vector3D p2, Vector3D d2)
    {
        var w = p1 - p2;
        double a = Vector3D.Dot(d1, d1), b = Vector3D.Dot(d1, d2), c = Vector3D.Dot(d2, d2);
        double d = Vector3D.Dot(d1, w), e = Vector3D.Dot(d2, w);
        return (b * e - c * d) / (a * c - b * b);
    }

    // ---- Tilt signs and global vertices ----

    /// <summary>
    /// The sign reading of Conventions §4 and §15: a positive tilt about x is right-handed
    /// (+y turns toward +z), so the new z is Rx(90°)·(0, 0, 1) = (0, −1, 0) and the vertex a
    /// thickness t after the break is at (0, −t, 0); about y (+z toward +x) at (t, 0, 0); about z
    /// alone the axis does not move but the new x is (0, 1, 0).
    /// </summary>
    [Fact]
    public void TiltSigns_PlaceTheNextVertexAsTheRightHandRuleSays()
    {
        double t = 0.025;
        var cases = new (TiltDecenter Tilt, Vector3D Vertex, Vector3D XAxis)[]
        {
            (new TiltDecenter(0, 0, 90, 0, 0), new Vector3D(0, -t, 0), Vector3D.UnitX),
            (new TiltDecenter(0, 0, 0, 90, 0), new Vector3D(t, 0, 0), new Vector3D(0, 0, -1)),
            (new TiltDecenter(0, 0, 0, 0, 90), new Vector3D(0, 0, t), Vector3D.UnitY),
            (new TiltDecenter(0.001, 0.002, 0, 0, 0), new Vector3D(0.001, 0.002, t), Vector3D.UnitX),
        };
        foreach (var (tilt, vertex, xAxis) in cases)
        {
            // Surface 1 has no thickness, so the break starts at the global origin.
            var lens = Lens(double.PositiveInfinity, Plane(0, stop: true), Cb(tilt, t), Plane(0.01));
            var placement = SurfacePlacement.Compute(lens);
            var v = placement.Global(3).Translation;
            Assert.Equal(vertex, v);   // quarter turns are exact (OpticsConventions.SinCosDegrees)
            Assert.Equal(xAxis, placement.Global(3).Col0);
        }
    }

    [Fact]
    public void LocalAndGlobalPoints_ConvertBothWays()
    {
        var lens = Lens(double.PositiveInfinity, Plane(0.01, stop: true), Cb(new TiltDecenter(0.002, -0.001, 12, -7, 31), 0.03), S(0.05, 0.01, 1.5), Plane(0.02));
        var placement = SurfacePlacement.Compute(lens);
        var local = new Vector3D(0.003, -0.004, 0.0005);
        var global = placement.LocalToGlobal(3, local);
        Close(local, placement.GlobalToLocal(3, global), 1e-16, "round trip");
        // The vertex of surface 3 is the break's decenter plus the rotated thickness.
        var rotation = new TiltDecenter(0, 0, 12, -7, 31).ToTransform();
        Close(new Vector3D(0.002, -0.001, 0.01) + rotation.ApplyDirection(new Vector3D(0, 0, 0.03)), placement.Global(3).Translation, 1e-16, "vertex");
    }

    // ---- Coordinate returns ----

    [Theory]
    [InlineData(TiltDecenterOrder.DecenterThenTilt)]
    [InlineData(TiltDecenterOrder.TiltThenDecenter)]
    public void ACoordinateReturnXYZ_PutsTheNextSurfaceOnTheTarget(TiltDecenterOrder order)
    {
        var lens = Lens(double.PositiveInfinity,
            Plane(0.01, stop: true),
            S(0.08, 0.004, 1.5) with { TiltBefore = new TiltDecenter(0.0005, 0, 3, -2, 0) },
            Plane(0.02),
            Cb(new TiltDecenter(0.004, -0.003, 10, 20, -5, TiltDecenterOrder.TiltThenDecenter), 0.05),
            Plane(0.03),
            new SequentialSurface { CoordinateBreak = CoordinateBreak.ReturnTo(2, CoordinateReturnMode.OrientationXYZ, order), Thickness = 123 },
            Plane(0.01));
        var placement = SurfacePlacement.Compute(lens);
        SameFrame(placement.Global(2), placement.Global(7), 1e-15, "surface 7 on surface 2");
        Assert.NotEqual(123, placement.Thickness(6));
        // The trace uses the same frames.
        var trace = SequentialTrace.Create(lens, Sys());
        SameFrame(placement.Global(7), trace.GlobalPlacement(7), 0, "trace frame");
    }

    [Fact]
    public void CoordinateReturns_OrientationOnly_AndXY_SolveWhatTheySay()
    {
        SequentialLens With(CoordinateReturnMode mode, TiltDecenterOrder order) => Lens(double.PositiveInfinity,
            Plane(0.01, stop: true),
            Cb(new TiltDecenter(0.004, -0.003, 10, 20, -5), 0.05),
            Plane(0.03),
            new SequentialSurface
            {
                CoordinateBreak = new CoordinateBreak(new TiltDecenter(0.001, 0.002, 0, 0, 0, order)) { Return = mode, ReturnToSurface = 1 },
                Thickness = 0.04,
            },
            Plane(0.01));

        foreach (var order in new[] { TiltDecenterOrder.DecenterThenTilt, TiltDecenterOrder.TiltThenDecenter })
        {
            // Orientation only: the axes are surface 1's; the decenters and thickness are kept.
            var only = SurfacePlacement.Compute(With(CoordinateReturnMode.OrientationOnly, order));
            var rel = only.RelativeTo(4, 1);
            Close(Vector3D.UnitX, rel.Col0, 1e-15);
            Close(Vector3D.UnitY, rel.Col1, 1e-15);
            Assert.Equal(0.001, only.ResolvedBefore(4).DecenterX);
            Assert.Equal(0.04, only.Thickness(4));

            // XY: also on surface 1's z axis; the thickness is kept.
            var xy = SurfacePlacement.Compute(With(CoordinateReturnMode.OrientationXY, order));
            var relXY = xy.RelativeTo(4, 1);
            Close(Vector3D.UnitZ, relXY.Col2, 1e-15);
            Close(0, relXY.Translation.X, 1e-16, "x on the axis");
            Close(0, relXY.Translation.Y, 1e-16, "y on the axis");
            Close(relXY.Translation.Z + 0.04, xy.RelativeTo(5, 1).Translation.Z, 1e-16, "thickness kept");
        }
    }

    [Fact]
    public void AReturnAtRightAngles_IsRefusedInTheOrderDecenterThenTilt()
    {
        var lens = Lens(double.PositiveInfinity,
            Plane(0.01, stop: true),
            Cb(new TiltDecenter(0, 0, 90, 0, 0), 0.05),
            new SequentialSurface { CoordinateBreak = CoordinateBreak.ReturnTo(1, CoordinateReturnMode.OrientationXYZ, TiltDecenterOrder.DecenterThenTilt) },
            Plane(0.01));
        var e = Assert.Throws<InvalidOperationException>(() => SurfacePlacement.Compute(lens));
        Assert.Contains("tilt then decenter", e.Message);
    }

    /// <summary>A break the ray runs parallel to leaves it where it is; the optical path is the
    /// straight length.</summary>
    [Fact]
    public void ARayParallelToABreak_CrossesIt()
    {
        var lens = Lens(double.PositiveInfinity,
            Plane(0.01, stop: true),
            Cb(new TiltDecenter(0, 0, 90, 0, 0)),
            Cb(new TiltDecenter(0, 0, -90, 0, 0), 0.03),
            Plane(0.01));
        var trace = SequentialTrace.Create(lens, Sys());
        var path = trace.TracePath(trace.NewRay(Vector3D.Zero, Vector3D.UnitZ));
        Assert.True(path.Final.IsOk);
        Assert.Equal(0.04, path.Hits[4].GlobalPosition.Z, 15);
        Assert.Equal(0.04, path.Hits[4].OpticalPath, 15);
    }

    // ---- Lens tools ----

    /// <summary>
    /// Tilting a group about a pivot p (in the group's starting frame) by Rx(a) moves the first
    /// surface's vertex from 0 to p + Rx(a)·(0 − p); with p = (0, 0, pz) that is
    /// (0, pz·sin a, pz − pz·cos a), Rx(a)·(0, 0, −pz) being (0, pz·sin a, −pz·cos a). Every
    /// surface after the group stays where it was.
    /// </summary>
    [Fact]
    public void TiltDecenterElements_TurnsTheGroupAboutThePivot_AndRestoresTheRest()
    {
        var lens = Lens(double.PositiveInfinity, Plane(0.02, stop: true), S(0.05, 0.006, 1.5), S(-0.05, 0.03), Plane(0.02), Plane(0.01));
        double a = 7, pz = 0.003;
        var tilted = CoordinateBreakTools.TiltDecenterElements(lens, 2, 3, new TiltDecenter(0, 0, a, 0, 0), new Vector3D(0, 0, pz));
        var before = SurfacePlacement.Compute(lens);
        var after = SurfacePlacement.Compute(tilted);
        // Inserted: dummy, to-pivot, tilt, from-pivot before the group; return and restore after.
        Assert.Equal(lens.Surfaces.Count + 6, tilted.Surfaces.Count);
        int first = 6;   // surface 2 moved past the four inserted surfaces
        double s = Math.Sin(a * Math.PI / 180), c = Math.Cos(a * Math.PI / 180);
        var start = before.VertexFrame(2);
        Close(start.Apply(new Vector3D(0, pz * s, pz - pz * c)), after.Global(first).Translation, 1e-16, "tilted vertex");
        // The surfaces after the group, and the image, have not moved.
        for (int k = 4; k < lens.Surfaces.Count; k++)
            SameFrame(before.Global(k), after.Global(k + 6), 1e-15, $"surface {k}");
        // The return target is the inserted dummy.
        Assert.Equal(2, tilted.Surfaces[first + 2].CoordinateBreak!.ReturnToSurface);
    }

    [Fact]
    public void LocalToGlobal_AndBack_MoveNoSurface()
    {
        var lens = Lens(double.PositiveInfinity,
            Plane(0.02, stop: true),
            S(0.05, 0.006, 1.5) with { TiltBefore = new TiltDecenter(0.0002, 0, 1, 0, 0) },
            S(-0.05, 0.03),
            Cb(new TiltDecenter(0.001, 0.002, 5, -3, 2), 0.01),
            S(0.04, 0.004, 1.6),
            Plane(0.02),
            Cb(new TiltDecenter(-0.002, 0.001, -4, 6, 0, TiltDecenterOrder.TiltThenDecenter), 0.015),
            Plane(0.01));
        var original = SurfacePlacement.Compute(lens);
        var global = CoordinateBreakTools.LocalToGlobal(lens, 2, 8, 1);
        // Global To Local over the whole lens: surface 1 to the image (neither is a break).
        var local = CoordinateBreakTools.GlobalToLocal(global, 1, global.ImageIndex, TiltDecenterOrder.DecenterThenTilt);
        foreach (var rewritten in new[] { global, local })
        {
            var placement = SurfacePlacement.Compute(rewritten);
            var a = Enumerable.Range(0, lens.Surfaces.Count).Where(j => !lens.Surfaces[j].IsCoordinateBreak).ToArray();
            var b = Enumerable.Range(0, rewritten.Surfaces.Count).Where(j => !rewritten.Surfaces[j].IsCoordinateBreak).ToArray();
            Assert.Equal(a.Length, b.Length);
            for (int i = 1; i < a.Length; i++)
                SameFrame(original.Global(a[i]), placement.Global(b[i]), 1e-15, $"surface {a[i]}");
        }
        // Local To Global gave each of the three groups three breaks, the first a return to surface 1.
        Assert.Equal(lens.Surfaces.Count - 2 + 9, global.Surfaces.Count);
        Assert.Equal(CoordinateReturnMode.OrientationXYZ, global.Surfaces[2].CoordinateBreak!.Return);
        Assert.Equal(1, global.Surfaces[2].CoordinateBreak!.ReturnToSurface);
        // Global To Local left one explicit break per run.
        Assert.All(local.Surfaces.Where(s => s.IsCoordinateBreak), s => Assert.Equal(CoordinateReturnMode.None, s.CoordinateBreak!.Return));
    }

    [Fact]
    public void Decompose_ReproducesATransform_InEitherOrder()
    {
        var m = RigidTransform.Compose(new TiltDecenter(0.003, -0.002, 17, -23, 41).ToTransform(), RigidTransform.FromTranslation(new Vector3D(0.001, 0.004, 0.02)));
        foreach (var order in new[] { TiltDecenterOrder.DecenterThenTilt, TiltDecenterOrder.TiltThenDecenter })
        {
            var (values, t) = CoordinateBreakTools.Decompose(m, order);
            var back = RigidTransform.Compose(values.ToTransform(), RigidTransform.FromTranslation(new Vector3D(0, 0, t)));
            SameFrame(m, back, 1e-15, order.ToString());
        }
    }

    // ---- Validation and the paraxial model ----

    [Fact]
    public void Validation_NamesEveryPlacementProblem()
    {
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                Plane(0.01, stop: true) with { TiltBefore = new TiltDecenter(0, 0, 1, 0, 0) },
                new SequentialSurface { CoordinateBreak = new CoordinateBreak(), IsMirror = true },
                new SequentialSurface { CoordinateBreak = CoordinateBreak.ReturnTo(5) },
                Plane(0.01) with { TiltAfter = SurfaceTiltAfter.Reverse(3) },
                new SequentialSurface { CoordinateBreak = new CoordinateBreak() },
            }
        };
        var problems = lens.Validate();
        Assert.Contains(problems, p => p.Contains("Surface 1") && p.Contains("object at infinity"));
        Assert.Contains(problems, p => p.Contains("Surface 2") && p.Contains("cannot be a mirror"));
        Assert.Contains(problems, p => p.Contains("Surface 3") && p.Contains("prior surface"));
        Assert.Contains(problems, p => p.Contains("Surface 4") && p.Contains("coordinate break"));
        Assert.Contains(problems, p => p.Contains("Image surface") && p.Contains("coordinate breaks"));
        Assert.Throws<ArgumentException>(() => SequentialTrace.Create(lens, Sys()));
    }

    [Fact]
    public void TheParaxialModel_IsUnchangedForAnAxialLens_AndCopiesBreaksAsPlanes()
    {
        var plain = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true), Plane(0.02), S(-0.05, 0.09));
        var withBreak = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true), Cb(TiltDecenter.None, 0.02), S(-0.05, 0.09));
        var a = plain.ToParaxialLens();
        var b = withBreak.ToParaxialLens();
        for (int j = 0; j < a.Surfaces.Count; j++)
        {
            Assert.Equal(a.Surfaces[j].Curvature, b.Surfaces[j].Curvature);
            Assert.Equal(a.Surfaces[j].Thickness, b.Surfaces[j].Thickness);
        }
        Assert.Equal(1.5, b.Surfaces[2].Medium.Index);   // the break keeps the medium before it
        Assert.True(withBreak.IsAxial(out var reason));
        Assert.Null(reason);

        // A decentered lens surface is not axial.
        var decentered = Lens(double.PositiveInfinity, Plane(0.01, stop: true), S(0.05, 0.005, 1.5) with { TiltBefore = new TiltDecenter(0.001, 0, 0, 0, 0) }, Plane(0.05));
        var e = Assert.Throws<InvalidOperationException>(() => decentered.ToParaxialLens());
        Assert.Contains("surface 2", e.Message);
    }

    // ---- The analysis ----

    [Fact]
    public void TheGlobalVertexReport_IsRegistered_AndListsEverySurface()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(GlobalVertexAnalysis.SavedId, out var empty));
        var context = new OpticsAnalysisContext("P", new OpticsSettings());
        Assert.Equal(new[] { "No surfaces" }, empty.Compute(context, empty.CreateDefaultSettings(), default).Sections.Select(s => s.Heading));

        var lens = Lens(double.PositiveInfinity, Plane(0.01, stop: true), Cb(new TiltDecenter(0, 0, 90, 0, 0), 0.025), Plane(0.01));
        var analysis = new GlobalVertexAnalysis(_ => lens);
        var result = analysis.Compute(context, new GlobalVertexSettings(), default);
        Assert.Equal(new[] { "Vertices", "Rotation matrices", "Coordinate breaks" }, result.Sections.Select(s => s.Heading));
        Assert.Equal(lens.Surfaces.Count - 1, result.Sections[0].TableRows.Count);   // no row for an object at infinity
        Assert.Contains("not axial", string.Join(" ", result.Assumptions));
        Assert.Throws<ArgumentException>(() => analysis.Compute(context, new GlobalVertexSettings { ReferenceSurface = 9 }, default));
    }
}
