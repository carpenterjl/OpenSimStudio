using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.LensEditing;
using OpenSim.Optics.Sequential.Solves;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Tests.Optics;

/// <summary>
/// TC-6: every registered surface type (<see cref="SurfaceTypes"/>), coordinate breaks, surface
/// tilts and returns through the saved surfaces, and the saved solves evaluated through
/// <see cref="ProjectLens.Solve"/>. Shape coefficients, radii, thicknesses and solve targets are
/// arbitrary test inputs, not reference data; every expected value is the same computation done
/// another way (the lens built in code, <see cref="SolveEngine.Apply"/> called directly), or a
/// unit conversion derived in the test.
/// </summary>
public sealed class SavedSurfaceTypesTests
{
    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static OpticalSystemData Sys() => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.008 },
        Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint(), new FieldPoint { Y = 3 } } },
    };

    /// <summary>Stop, the surface under test (5 mm of n = 1.5 after it), a plane, 30 mm to the image.</summary>
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

    /// <summary>One shape of every registered type, on the curvature and conic it is used with.</summary>
    public static IEnumerable<object[]> Shapes()
    {
        SequentialSurface On(SurfaceShape shape, double c = 20, double k = 0) => new() { Shape = shape, Curvature = c, Conic = k };
        yield return new object[] { "Even Asphere", On(new EvenAsphere(new[] { 0.5, 100, -2e5 }), k: -0.3) };
        yield return new object[] { "Odd Asphere", On(new OddAsphere(new[] { 1e-3, 0.4, 30 })) };
        yield return new object[] { "Alternate Even", On(new AlternateEvenAsphere(new[] { 0.5, 100 })) };
        yield return new object[] { "Alternate Odd", On(new AlternateOddAsphere(new[] { 1e-3, 0.4 })) };
        yield return new object[] { "Extended Asphere", On(new ExtendedAsphere(0.006, new[] { 1e-5, -2e-6, 3e-7 })) };
        yield return new object[] { "Extended Odd Asphere", On(new ExtendedOddAsphere(0.006, new[] { 1e-6, 2e-6, -1e-6 })) };
        yield return new object[] { "Q-Type Asphere", On(new QTypeAsphere(QTypeKind.Qcon, 0.006, new[] { 1e-6, -2e-7, 5e-8 }), k: -0.5) };
        yield return new object[] { "Q-Type Asphere", On(new QTypeAsphere(QTypeKind.Qbfs, 0.006, new[] { 2e-6, 1e-7 })) };
        yield return new object[] { "Superconic", On(new SuperconicSurface(new[] { 10.0, 4.0 }, new[] { 200.0, 30.0 }), k: -0.2) };
        yield return new object[] { "Periodic", On(new PeriodicSurface(1e-6, 100, 50)) };
        yield return new object[] { "Fresnel", On(new FresnelShape(new[] { 0.5, 10 }), c: 0) };
        yield return new object[] { "Extended Fresnel", On(new ExtendedFresnelShape(new[] { 0.2 }, 10, -0.5, new[] { 1.0, 3.0 })) };
        yield return new object[] { "Cylinder Fresnel", On(new CylinderFresnelShape(new[] { 0.2 }, 10, -0.5, new[] { 1.0 })) };
        yield return new object[] { "Generalized Fresnel", On(new GeneralizedFresnelShape(new[] { 0.1 }, 0.006, new[] { 0, 1e-6, 0, 0, 1e-5 })) };
        yield return new object[] { "ABCD", On(new AbcdShape(new ParaxialMatrix(1, 0.001, -10, 1), new ParaxialMatrix(1, 0, -20, 1)), c: 0) };
        yield return new object[] { "Conjugate", On(new ConjugateShape(new Vector3D(0, 0, 0.05), new Vector3D(0.001, 0, 0.08)), c: 0) };
        yield return new object[] { "Paraxial", On(new ParaxialShape(0.1), c: 0) };
        yield return new object[] { "Paraxial XY", On(new ParaxialXYShape(10, 20), c: 0) };
        yield return new object[] { "Retro Reflect", On(new RetroReflectShape(), c: 0) };
    }

    /// <summary>The same rays through both lenses, compared bit for bit: final position,
    /// direction, optical path and status of a grid of rays at two angles.</summary>
    private static void TracesIdentically(SequentialLens expected, SequentialLens actual)
    {
        var a = SequentialTrace.Create(expected, Sys());
        var b = SequentialTrace.Create(actual, Sys());
        int ok = 0;
        foreach (double angle in new[] { 0.0, 0.03 })
            for (double x = -0.003; x <= 0.0031; x += 0.0015)
                for (double y = -0.003; y <= 0.0031; y += 0.0015)
                {
                    var dir = new Vector3D(0, Math.Sin(angle), Math.Cos(angle));
                    var ra = a.Trace(a.NewRay(new Vector3D(x, y, 0), dir));
                    var rb = b.Trace(b.NewRay(new Vector3D(x, y, 0), dir));
                    Assert.Equal(ra.Status, rb.Status);
                    Assert.Equal(ra.FailedAtSurface, rb.FailedAtSurface);
                    foreach (var (p, q) in new[] { (ra.Position.X, rb.Position.X), (ra.Position.Y, rb.Position.Y), (ra.Position.Z, rb.Position.Z),
                                 (ra.Direction.X, rb.Direction.X), (ra.Direction.Y, rb.Direction.Y), (ra.Direction.Z, rb.Direction.Z), (ra.OpticalPath, rb.OpticalPath) })
                        Assert.Equal(BitConverter.DoubleToInt64Bits(p), BitConverter.DoubleToInt64Bits(q));
                    if (ra.Status == RayStatus.Ok) ok++;
                }
        Assert.True(ok > 0 || expected.Surfaces[2].Shape is AlternateEvenAsphere or AlternateOddAsphere, "no ray reached the image");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EachRegisteredType_RoundTripsThroughTheSavedSurfaces_AndTracesBitForBit(string type, SequentialSurface surface)
    {
        var lens = Lens(surface);
        // In meters every factor is 1, so the saved values are the shape's own.
        var saved = SavedSurfaces.FromLens(lens, LensUnit.Meter);
        Assert.Equal(type, saved[2].Type);
        var back = SavedSurfaces.ToLens(saved, LensUnit.Meter, _ => null);
        Assert.Equal(type, back.Surfaces[2].Shape!.TypeName);
        Assert.Equal(Json(saved), Json(SavedSurfaces.FromLens(back, LensUnit.Meter)));
        var layout = SurfaceTypes.Find(type)!;
        Assert.Equal(Json(layout.Read(surface.Shape!)), Json(layout.Read(back.Surfaces[2].Shape!)));
        TracesIdentically(lens, back);

        // Numbers 13 on are extra data, the others parameters.
        Assert.All(saved[2].Parameters ?? new(), p => Assert.True(p.Number < SurfaceTypes.FirstExtraData));
        Assert.All(saved[2].ExtraData ?? new(), p => Assert.True(p.Number >= SurfaceTypes.FirstExtraData));

        // In millimeters each value is its SI value over 1e-3^power (derived from the column's
        // power), and reading them back gives the shape's values to rounding.
        var mm = SavedSurfaces.FromLens(lens, LensUnit.Millimeter);
        foreach (var (number, si) in layout.Read(surface.Shape!))
        {
            var info = layout.Parameter(number)!;
            double expected = si / SurfaceTypes.Factor(info.LengthPower, 1e-3);
            double actual = SavedSurfaces.Value(mm[2], number);
            Assert.Equal(expected, actual);
            if (info.IsInteger) Assert.Equal(si, actual);
        }
        var fromMm = layout.Read(SavedSurfaces.ToLens(mm, LensUnit.Millimeter, _ => null).Surfaces[2].Shape!);
        foreach (var (number, si) in layout.Read(surface.Shape!))
            Assert.True(Math.Abs(fromMm[number] - si) <= 4e-16 * Math.Abs(si), $"{type} parameter {number}: {fromMm[number]:R} vs {si:R}");
    }

    [Fact]
    public void TheRegistry_ListsEveryShapeOfWave4_ExceptTheUnsourcedOnes()
    {
        var names = SurfaceTypes.All.Select(t => t.Name).ToHashSet();
        foreach (var type in Shapes().Select(s => (string)s[0])) Assert.Contains(type, names);
        Assert.Contains(SurfaceTypes.CoordinateBreak, names);
        Assert.DoesNotContain("Odd Cosine", names);       // parameters 31–54 have no order on the help page
        Assert.DoesNotContain("Lenslet Array", names);    // a user-defined DLL whose parameters are not listed
        // Units: the Even Asphere's α_i on r^(2i) is in lens units^(1 − 2i) ("Scale Lens": units
        // change from term to term).
        var even = SurfaceTypes.Find("Even Asphere")!;
        Assert.Equal(new[] { -1, -3, -5, -7, -9, -11, -13, -15 }, even.Parameters.Select(p => p.LengthPower));
        Assert.Equal(1, SurfaceTypes.Find(SurfaceTypes.CoordinateBreak)!.Parameter(1)!.LengthPower);
        Assert.True(SurfaceTypes.Find(SurfaceTypes.CoordinateBreak)!.Parameter(3)!.IsAngle);
    }

    [Fact]
    public void SavedParameters_AreCheckedByName()
    {
        var saved = SavedSurfaces.FromLens(Lens(new SequentialSurface { Shape = new EvenAsphere(new[] { 0.5 }), Curvature = 20 }), LensUnit.Millimeter);
        var bad = saved.ToList();
        bad[2] = bad[2] with { Parameters = new() { new() { Number = 9, Value = 1 } } };
        bad[3] = bad[3] with { Type = "Paraxial", Radius = 50, Parameters = new() { new() { Number = 1, Value = 100 }, new() { Number = 2, Value = 0 } } };
        bad[1] = bad[1] with { Type = "Q-Type Asphere", ExtraData = new() { new() { Number = 13, Value = 0.5 }, new() { Number = 15, Value = 6 } } };
        var ex = Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(bad, LensUnit.Millimeter, _ => null));
        Assert.Contains("Surface 2: the Even Asphere has no parameter 9", ex.Message);
        Assert.Contains("Surface 3: a Paraxial surface has no radius", ex.Message);
        Assert.Contains("Surface 3: Paraxial: OPD mode 0 is not built", ex.Message);
        Assert.Contains("Surface 1: Q-Type Asphere: parameter 13 (Type) is 0.5; it must be a whole number", ex.Message);

        // A type that is not registered is still refused by name.
        var unknown = saved.ToList();
        unknown[2] = unknown[2] with { Type = "Toroidal NURBS" };
        ex = Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(unknown, LensUnit.Millimeter, _ => null));
        Assert.Contains("Surface 2: the surface type 'Toroidal NURBS' is saved with the lens but this build does not trace it", ex.Message);
        // A shape without a layout cannot be saved.
        var lenslet = Lens(new SequentialSurface { Shape = new LensletArrayShape(3, 3, 0.002, 0.002) });
        Assert.Contains("'Lenslet Array' cannot be saved", Assert.Throws<ArgumentException>(() => SavedSurfaces.FromLens(lenslet, LensUnit.Meter)).Message);
    }

    /// <summary>Coordinate breaks with a return, and surface tilts before and after in every
    /// after-mode, through the saved surfaces and back; the saved lens traces as the one built
    /// in code.</summary>
    [Fact]
    public void BreaksTiltsAndReturns_RoundTrip_AndTraceBitForBit()
    {
        var n = ParaxialMedium.Constant(1.5);
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Thickness = 0.002, IsStop = true },
                new SequentialSurface { CoordinateBreak = new CoordinateBreak(new TiltDecenter(0.001, -0.0005, 5, -2, 1)), Thickness = 0.004 },
                new SequentialSurface
                {
                    Curvature = 20, Thickness = 0.005, Medium = n,
                    TiltBefore = new TiltDecenter(0.0002, 0, 1.5, 0, 0, TiltDecenterOrder.TiltThenDecenter),
                    TiltAfter = SurfaceTiltAfter.ReverseThis,
                },
                new SequentialSurface { Curvature = -10, Thickness = 0.003, TiltBefore = new TiltDecenter(0, 0.0001, 0, 2, 0), TiltAfter = SurfaceTiltAfter.PickUpThis },
                new SequentialSurface { Thickness = 0.002, TiltAfter = SurfaceTiltAfter.Explicit(new TiltDecenter(0.0003, 0, 0, 0, 4, TiltDecenterOrder.TiltThenDecenter)) },
                new SequentialSurface { Thickness = 0.001, TiltAfter = SurfaceTiltAfter.Reverse(4) },
                new SequentialSurface { Thickness = 0.001, TiltAfter = SurfaceTiltAfter.PickUp(3) },
                new SequentialSurface { CoordinateBreak = CoordinateBreak.ReturnTo(2, CoordinateReturnMode.OrientationXY, TiltDecenterOrder.TiltThenDecenter), Thickness = 0.02 },
                new SequentialSurface { CoordinateBreak = new CoordinateBreak(new TiltDecenter(0.0001, 0, 3, 0, 0)) { Return = CoordinateReturnMode.OrientationOnly, ReturnToSurface = 1 }, Thickness = 0.001 },
                new SequentialSurface(),
            },
        };
        Assert.Empty(lens.Validate());
        var saved = SavedSurfaces.FromLens(lens, LensUnit.Meter);
        Assert.Equal(SurfaceTypes.CoordinateBreak, saved[2].Type);
        Assert.Equal(new[] { 1.0, 2, 3, 4, 5 }, saved[2].Parameters!.Select(p => (double)p.Number));
        Assert.Equal("OrientationXY", saved[8].CoordinateReturn!.Mode);
        Assert.Equal(2, saved[8].CoordinateReturn!.Surface);
        Assert.Equal(1, SavedSurfaces.Value(saved[8], 6));                  // tilt then decenter: order 1
        Assert.Equal("TiltThenDecenter", saved[3].TiltBefore!.Order);
        Assert.Equal("ReverseThisSurface", saved[3].TiltAfter!.Mode);
        Assert.Equal(("ReverseSurface", 4), (saved[6].TiltAfter!.Mode!, saved[6].TiltAfter!.Surface));

        var back = SavedSurfaces.ToLens(saved, LensUnit.Meter, _ => null);
        Assert.Equal(Json(saved), Json(SavedSurfaces.FromLens(back, LensUnit.Meter)));
        for (int j = 0; j < lens.Surfaces.Count; j++)
        {
            Assert.Equal(lens.Surfaces[j].CoordinateBreak, back.Surfaces[j].CoordinateBreak);
            Assert.Equal(lens.Surfaces[j].TiltBefore, back.Surfaces[j].TiltBefore);
            Assert.Equal(lens.Surfaces[j].TiltAfter, back.Surfaces[j].TiltAfter);
        }
        TracesIdentically(lens, back);

        // In millimeters the decenters are lens units and the tilts degrees.
        var mm = SavedSurfaces.FromLens(lens, LensUnit.Millimeter);
        Assert.Equal(0.001 / 1e-3, SavedSurfaces.Value(mm[2], 1));
        Assert.Equal(5, SavedSurfaces.Value(mm[2], 3));
        Assert.Equal(0.0002 / 1e-3, mm[3].TiltBefore!.DecenterX);
        Assert.Equal(1.5, mm[3].TiltBefore!.TiltX);

        // A break holds no material, radius or tilt of its own; a tilt has a known order.
        var bad = saved.ToList();
        bad[2] = bad[2] with { Material = "N-BK7", Radius = 5, TiltBefore = new OpticsTiltDecenterSettings { TiltX = 1 } };
        bad[3] = bad[3] with { TiltBefore = bad[3].TiltBefore! with { Order = "Sideways" }, CoordinateReturn = new OpticsCoordinateReturnSettings { Mode = "OrientationXY", Surface = 1 } };
        var ex = Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(bad, LensUnit.Meter, _ => null));
        Assert.Contains("Surface 2: a coordinate break takes the medium of the surface before it", ex.Message);
        Assert.Contains("Surface 2: a Coordinate Break surface has no radius", ex.Message);
        Assert.Contains("Surface 2: a coordinate break cannot have a surface tilt", ex.Message);
        Assert.Contains("Surface 3, tilt/decenter before: the order 'Sideways'", ex.Message);
        Assert.Contains("Surface 3: only a coordinate break has a coordinate return", ex.Message);
    }

    // ------------------------------------------------------------------ solves

    private static readonly OpticsSystemSettings SystemData = new()
    {
        ApertureValue = 8,
        Fields = new() { new OpticsFieldSettings(), new OpticsFieldSettings { Y = 3 } },
    };

    /// <summary>A doublet in millimeters: n = 1.5 then n = 1.6, surface 2 fixed at 8 mm
    /// semi-diameter, surface 1 with a conic and a TCE.</summary>
    private static List<OpticsSurfaceSettings> Doublet() => new()
    {
        new() { ThicknessIsInfinite = true },
        new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true, Conic = -0.5, Tce = 7.1, SemiDiameter = 8 },
        new() { Radius = -120, Thickness = 10 },
        new() { Radius = 60, Thickness = 4, Index = 1.6 },
        new() { Radius = 200, Thickness = 80 },
        new(),
    };

    private static OpticsCellFlagSettings SolveFlag(string column, string solve, params double[] parameters) =>
        new() { Column = column, Kind = SavedSurfaces.SolveFlag, Solve = solve, SolveParameters = parameters.Length > 0 ? parameters.ToList() : null };

    private const double Mm = 1e-3;   // meters per lens unit (millimeters)

    /// <summary>(surface, flag, the record the flag stands for in SI).</summary>
    public static IEnumerable<object[]> SolveCases()
    {
        object[] C(int surface, OpticsCellFlagSettings flag, SurfaceSolve solve) => new object[] { surface, flag, solve };
        const string R = SavedSurfaces.RadiusColumn, T = SavedSurfaces.ThicknessColumn;
        yield return C(4, SolveFlag(R, "Marginal ray angle", -0.1), new MarginalRayAngleSolve { Surface = 4, Angle = -0.1 });
        yield return C(4, SolveFlag(R, "Chief ray angle", 0.05), new ChiefRayAngleSolve { Surface = 4, Angle = 0.05 });
        yield return C(3, SolveFlag(R, "Marginal ray normal"), new MarginalRayNormalSolve { Surface = 3 });
        yield return C(3, SolveFlag(R, "Chief ray normal"), new ChiefRayNormalSolve { Surface = 3 });
        yield return C(3, SolveFlag(R, "Aplanatic"), new AplanaticSolve { Surface = 3 });
        yield return C(2, SolveFlag(R, "Element power", 0.01), new ElementPowerSolve { Surface = 2, Power = 0.01 / Mm });
        yield return C(3, SolveFlag(R, "Concentric with surface", 1), new ConcentricWithSurfaceSolve { Surface = 3, Of = 1 });
        yield return C(3, SolveFlag(R, "Concentric with radius", 1), new ConcentricWithRadiusSolve { Surface = 3, Of = 1 });
        yield return C(4, SolveFlag(R, "F/#", 5), new FNumberSolve { Surface = 4, FNumber = 5 });
        yield return C(4, SolveFlag(T, "Marginal ray height", 0, 0), new MarginalRayHeightSolve { Surface = 4, Height = 0 * Mm, PupilZone = 0 });
        yield return C(4, SolveFlag(T, "Marginal ray height", 0.5, 0.7), new MarginalRayHeightSolve { Surface = 4, Height = 0.5 * Mm, PupilZone = 0.7 });
        yield return C(4, SolveFlag(T, "Chief ray height", 0), new ChiefRayHeightSolve { Surface = 4, Height = 0 * Mm });
        yield return C(2, SolveFlag(T, "Edge thickness", 2, 5), new EdgeThicknessSolve { Surface = 2, EdgeThickness = 2 * Mm, RadialHeight = 5 * Mm });
        yield return C(4, SolveFlag(T, "Optical path difference", 0.25, 0.7), new OpticalPathDifferenceSolve { Surface = 4, Waves = 0.25, PupilZone = 0.7 });
        yield return C(4, SolveFlag(T, "Position", 1, 100), new PositionSolve { Surface = 4, ReferenceSurface = 1, Length = 100 * Mm });
        yield return C(4, SolveFlag(T, "Compensator", 2, 90), new CompensatorSolve { Surface = 4, ReferenceSurface = 2, Sum = 90 * Mm });
        yield return C(4, SolveFlag(T, "Center of curvature", 4), new CenterOfCurvatureSolve { Surface = 4, Of = 4 });
        yield return C(4, SolveFlag(T, "Pupil position"), new PupilPositionSolve { Surface = 4 });
        yield return C(2, SolveFlag(SavedSurfaces.SemiDiameterColumn, "Maximum"), new MaximumSemiDiameterSolve { Surface = 2 });
        yield return C(3, SurfaceFlags.Pickup(R, 1, R, -1), new PickupSolve { Surface = 3, Target = SolveColumn.Curvature, FromSurface = 1, Scale = -1 });
        yield return C(4, SurfaceFlags.Pickup(T, 2, T, 2, 3), new PickupSolve { Surface = 4, Target = SolveColumn.Thickness, FromSurface = 2, Scale = 2, Offset = 3 * Mm });
        yield return C(3, SurfaceFlags.Pickup(SavedSurfaces.MaterialColumn, 1, SavedSurfaces.MaterialColumn), new PickupSolve { Surface = 3, Target = SolveColumn.Material, FromSurface = 1 });
        yield return C(3, SurfaceFlags.Pickup(SavedSurfaces.ConicColumn, 1, SavedSurfaces.ConicColumn, 2), new PickupSolve { Surface = 3, Target = SolveColumn.Conic, FromSurface = 1, Scale = 2 });
        yield return C(3, SurfaceFlags.Pickup(SavedSurfaces.TceColumn, 1, SavedSurfaces.TceColumn), new PickupSolve { Surface = 3, Target = SolveColumn.Tce, FromSurface = 1 });
        yield return C(3, SurfaceFlags.Pickup(SavedSurfaces.SemiDiameterColumn, 1, SavedSurfaces.SemiDiameterColumn, 0.5), new PickupSolve { Surface = 3, Target = SolveColumn.SemiDiameter, FromSurface = 1, Scale = 0.5 });
        yield return C(4, SurfaceFlags.Pickup(T, 1, SavedSurfaces.SemiDiameterColumn, 3), new PickupSolve { Surface = 4, Target = SolveColumn.Thickness, FromSurface = 1, FromColumn = SolveColumn.SemiDiameter, Scale = 3 });
    }

    private static void SameSurfaces(SequentialLens expected, SequentialLens actual)
    {
        for (int j = 0; j < expected.Surfaces.Count; j++)
        {
            var e = expected.Surfaces[j];
            var a = actual.Surfaces[j];
            Assert.Equal(BitConverter.DoubleToInt64Bits(e.Curvature), BitConverter.DoubleToInt64Bits(a.Curvature));
            Assert.Equal(BitConverter.DoubleToInt64Bits(e.Thickness), BitConverter.DoubleToInt64Bits(a.Thickness));
            Assert.Equal(BitConverter.DoubleToInt64Bits(e.Conic), BitConverter.DoubleToInt64Bits(a.Conic));
            Assert.Equal(e.SemiDiameter, a.SemiDiameter);
            Assert.Equal(e.ThermalExpansion, a.ThermalExpansion);
            Assert.Equal(e.Medium.Index, a.Medium.Index);
            Assert.Equal(e.IsMirror, a.IsMirror);
        }
    }

    [Theory]
    [MemberData(nameof(SolveCases))]
    public void EachSavedSolve_EvaluatesAsTheEngineDoes(int surface, OpticsCellFlagSettings flag, SurfaceSolve direct)
    {
        var saved = Doublet();
        saved[surface] = SurfaceFlags.With(saved[surface], flag.Column!, flag);
        var project = new OpticsSettings { SystemData = SystemData, Surfaces = saved };
        var solution = ProjectLens.Solve(project, Array.Empty<OpticalCatalog>())!;

        var entered = SavedSurfaces.ToLens(saved, LensUnit.Millimeter, _ => null);
        var data = SystemDataSettings.FromSettings(SystemData, new List<string>());
        var expected = SolveEngine.Apply(entered, data, new[] { direct }, new SolveOptions { Catalogs = Array.Empty<OpticalCatalog>() });

        Assert.Equal(expected.Errors, solution.Errors);
        Assert.True(expected.Succeeded, string.Join(" ", expected.Errors));   // every case is one the engine can meet
        var cell = Assert.Single(solution.Solves);
        Assert.Equal(direct, cell.Solve);
        SameSurfaces(expected.Lens, solution.Lens);
        // The analyses read the solved lens.
        SameSurfaces(expected.Lens, ProjectLens.Sequential(new OpticsAnalysisContext("Doublet", project))!);
        Assert.Null(solution.ErrorOf(surface, flag.Column!));
    }

    /// <summary>A parameter pickup between registered types, across units (a parameter in
    /// lens units⁻¹ picking up a thickness), and between coordinate breaks.</summary>
    [Fact]
    public void ParameterPickups_UseTheRegistry_AndConvertUnits()
    {
        var saved = Doublet();
        saved[1] = saved[1] with { Type = "Even Asphere", Parameters = new() { new() { Number = 2, Value = 1e-6 } } };
        saved[3] = saved[3] with { Type = "Even Asphere" };
        saved[3] = SurfaceFlags.With(saved[3], "Par2", SurfaceFlags.Pickup("Par2", 1, "Par2", -1));
        saved[3] = SurfaceFlags.With(saved[3], "Par1", SurfaceFlags.Pickup("Par1", 2, SavedSurfaces.ThicknessColumn, 1e-4, 2e-5));
        var solution = ProjectLens.Solve(new OpticsSettings { SystemData = SystemData, Surfaces = saved }, Array.Empty<OpticalCatalog>())!;
        Assert.True(solution.Succeeded, string.Join(" ", solution.Errors));
        var shape = Assert.IsType<EvenAsphere>(solution.Lens.Surfaces[3].Shape);
        // α2 = −1 × 1e-6 mm⁻³, in SI −1e-6 × (1e-3)⁻³ (the column's power is −3).
        double alpha2 = -1e-6 / (Mm * Mm * Mm);
        Assert.True(Math.Abs(shape.Alpha[1] - alpha2) <= 1e-15 * Math.Abs(alpha2), $"{shape.Alpha[1]:R} vs {alpha2:R}");
        // α1 = 2e-5 + 1e-4 × t2, with t2 = 10 mm, in mm⁻¹; in SI that over 1e-3.
        double alpha1 = (2e-5 + 1e-4 * 10) / Mm;
        Assert.True(Math.Abs(shape.Alpha[0] - alpha1) <= 1e-14 * alpha1, $"{shape.Alpha[0]:R} vs {alpha1:R}");
        // Read back into the sheet's units.
        var cell = solution.Solves.Single(c => c.Column == "Par1");
        Assert.True(Math.Abs(SavedSolves.SavedValue(solution.Lens, saved[3], cell, Mm)!.Value - (2e-5 + 1e-4 * 10)) <= 1e-17);

        // Coordinate breaks: the second picks up the first's tilt about x, negated.
        var breaks = new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Thickness = 2, IsStop = true },
            new() { Type = SurfaceTypes.CoordinateBreak, Thickness = 5, Parameters = new() { new() { Number = 3, Value = 4 } } },
            new() { Radius = 50, Thickness = 5, Index = 1.5 },
            SurfaceFlags.With(new() { Type = SurfaceTypes.CoordinateBreak, Thickness = 30 }, "Par3", SurfaceFlags.Pickup("Par3", 2, "Par3", -1)),
            new(),
        };
        var bs = ProjectLens.Solve(new OpticsSettings { SystemData = SystemData, Surfaces = breaks }, Array.Empty<OpticalCatalog>())!;
        Assert.True(bs.Succeeded, string.Join(" ", bs.Errors));
        Assert.Equal(-4, bs.Lens.Surfaces[4].CoordinateBreak!.Values.TiltXDegrees);
    }

    [Fact]
    public void FailingSolves_AreNamed_AndTheProjectLensRefusesThem()
    {
        var saved = Doublet();
        saved[4] = SurfaceFlags.With(saved[4], SavedSurfaces.RadiusColumn, SolveFlag(SavedSurfaces.RadiusColumn, "F/#", 0));
        var project = new OpticsSettings { SystemData = SystemData, Surfaces = saved };
        var solution = ProjectLens.Solve(project, Array.Empty<OpticalCatalog>())!;
        Assert.False(solution.Succeeded);
        Assert.Contains(solution.Errors, e => e.StartsWith("Surface 4, curvature, f/# solve") && e.Contains("must not be zero"));
        var ex = Assert.Throws<ArgumentException>(() => ProjectLens.From(project, Array.Empty<OpticalCatalog>()));
        Assert.Contains("Surface 4, curvature", ex.Message);

        // A solve that cannot be met: the element power solve with equal indices either side.
        saved = Doublet();
        saved[2] = SurfaceFlags.With(saved[2] with { Index = 1.5 }, SavedSurfaces.RadiusColumn, SolveFlag(SavedSurfaces.RadiusColumn, "Element power", 0.01));
        saved[3] = saved[3] with { Index = 1.5 };
        solution = ProjectLens.Solve(project with { Surfaces = saved }, Array.Empty<OpticalCatalog>())!;
        Assert.False(solution.Succeeded);
        Assert.NotNull(solution.ErrorOf(2, SavedSurfaces.RadiusColumn));
        Assert.StartsWith("Surface 2, curvature, element power solve", solution.ErrorOf(2, SavedSurfaces.RadiusColumn));

        // Flags that cannot be translated: an unknown solve, a pickup from a surface that does
        // not exist, a solve on extra data, the parameter chief ray solve (not built).
        saved = Doublet();
        saved[1] = saved[1] with { Type = "Q-Type Asphere", ExtraData = new() { new() { Number = 15, Value = 6 } } };
        saved[1] = SurfaceFlags.With(saved[1], "Par16", SurfaceFlags.Pickup("Par16", 0, "Par16"));
        saved[2] = SurfaceFlags.With(saved[2], SavedSurfaces.RadiusColumn, SolveFlag(SavedSurfaces.RadiusColumn, "Bogus"));
        saved[3] = SurfaceFlags.With(saved[3], SavedSurfaces.ThicknessColumn, SurfaceFlags.Pickup(SavedSurfaces.ThicknessColumn, 9, SavedSurfaces.ThicknessColumn));
        solution = ProjectLens.Solve(project with { Surfaces = saved }, Array.Empty<OpticalCatalog>())!;
        Assert.Contains("Surface 1, Par16: solves and pickups on parameter 16 (extra data) are not built", solution.Errors[0]);
        Assert.Contains(solution.Errors, e => e.StartsWith("Surface 2, Radius: the solve 'Bogus'"));
        Assert.Contains(solution.Errors, e => e.StartsWith("Surface 3, Thickness: the pickup's source surface 9 does not exist"));

        var chief = new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 5, Index = 1.5, IsStop = true },
            SurfaceFlags.With(new() { Type = SurfaceTypes.CoordinateBreak, Thickness = 40 }, "Par1", SolveFlag("Par1", "Chief ray", 2, 0)),
            new(),
        };
        solution = ProjectLens.Solve(project with { Surfaces = chief }, Array.Empty<OpticalCatalog>())!;
        Assert.StartsWith("Surface 2, parameter 1, chief ray solve: the chief ray parameter solve is not built", solution.ErrorOf(2, "Par1"));

        // Chip-zone pickups are kept, not evaluated, and say so.
        saved = Doublet();
        saved[2] = SurfaceFlags.With(saved[2], SavedSurfaces.ChipZoneColumn, SurfaceFlags.Pickup(SavedSurfaces.ChipZoneColumn, 1, SavedSurfaces.ChipZoneColumn));
        solution = ProjectLens.Solve(project with { Surfaces = saved }, Array.Empty<OpticalCatalog>())!;
        Assert.True(solution.Succeeded);
        Assert.Contains(solution.NotEvaluated, n => n.StartsWith("Surface 2, ChipZone"));
    }

    [Fact]
    public void ParaxialOpdMode3_ResolvesItsConjugatesFromTheProject()
    {
        var saved = new List<OpticsSurfaceSettings>
        {
            new() { Thickness = 200 },
            new() { Type = "Paraxial", Thickness = 100, IsStop = true, Parameters = new() { new() { Number = 1, Value = 50 }, new() { Number = 2, Value = 3 } } },
            new(),
        };
        var project = new OpticsSettings { SystemData = SystemData, Surfaces = saved };
        var lens = ProjectLens.From(project, Array.Empty<OpticalCatalog>())!;
        var expected = ParaxialShape.ResolveConjugates(SavedSurfaces.ToLens(saved, LensUnit.Millimeter, _ => null), SystemDataSettings.FromSettings(SystemData, new List<string>()));
        Assert.Equal(((ParaxialShape)expected.Surfaces[1].Shape!).ObjectDistance, ((ParaxialShape)lens.Surfaces[1].Shape!).ObjectDistance);
        Assert.True(double.IsFinite(((ParaxialShape)lens.Surfaces[1].Shape!).ObjectDistance));
        Assert.Equal(3, SavedSurfaces.Value(SavedSurfaces.FromLens(lens, LensUnit.Millimeter)[1], 2));
        // Mode 2 stays at infinite conjugates.
        saved[1] = saved[1] with { Parameters = new() { new() { Number = 1, Value = 50 }, new() { Number = 2, Value = 2 } } };
        Assert.True(double.IsInfinity(((ParaxialShape)ProjectLens.From(project, Array.Empty<OpticalCatalog>())!.Surfaces[1].Shape!).ObjectDistance));
    }

    // ------------------------------------------------------------------ the editing tools

    [Fact]
    public void ScaleLens_ScalesParametersByTheirUnits_AndRefusesUnknownTypesByName()
    {
        var saved = Doublet();
        saved[1] = saved[1] with
        {
            Type = "Even Asphere", Parameters = new() { new() { Number = 1, Value = 1e-3 }, new() { Number = 2, Value = 1e-6 } },
            TiltBefore = new OpticsTiltDecenterSettings { DecenterX = 0.5, TiltX = 2 },
        };
        saved[3] = saved[3] with { Type = "Q-Type Asphere", ExtraData = new() { new() { Number = 13, Value = 1 }, new() { Number = 14, Value = 1 }, new() { Number = 15, Value = 6 }, new() { Number = 16, Value = 0.01 } } };
        saved.Insert(2, new OpticsSurfaceSettings { Type = SurfaceTypes.CoordinateBreak, Parameters = new() { new() { Number = 2, Value = 1.5 }, new() { Number = 4, Value = 3 } } });
        var scaled = ScaleLens.Apply(saved, 2).Surfaces;
        Assert.Equal(1e-3 / 2, SavedSurfaces.Value(scaled[1], 1));          // α1: lens units⁻¹
        Assert.Equal(1e-6 / 8, SavedSurfaces.Value(scaled[1], 2));          // α2: lens units⁻³
        Assert.Equal((1.0, 2.0), (scaled[1].TiltBefore!.DecenterX, scaled[1].TiltBefore!.TiltX));
        Assert.Equal((3.0, 3.0), (SavedSurfaces.Value(scaled[2], 2), SavedSurfaces.Value(scaled[2], 4)));   // decenter × 2, tilt kept
        Assert.Equal((1.0, 1.0, 12.0, 0.02), (SavedSurfaces.Value(scaled[4], 13), SavedSurfaces.Value(scaled[4], 14), SavedSurfaces.Value(scaled[4], 15), SavedSurfaces.Value(scaled[4], 16)));
        // Scaling by 2 makes the lens trace as the original at twice the size: same EFL ratio.
        var context = new LensEditContext(SystemData, Array.Empty<OpticalCatalog>());
        double before = MakeFocal.EffectiveFocalLength(Doublet(), context);
        double after = MakeFocal.EffectiveFocalLength(ScaleLens.Apply(Doublet(), 2).Surfaces, context);
        Assert.True(Math.Abs(after / before - 2) <= 1e-12);

        var unknown = Doublet();
        unknown[2] = unknown[2] with { Type = "Toroidal NURBS" };
        Assert.Contains("'Toroidal NURBS'", Assert.Throws<ArgumentException>(() => ScaleLens.Apply(unknown, 2)).Message);
        var reverse = Doublet();
        reverse[2] = reverse[2] with { Type = "Even Asphere" };
        Assert.Contains("'Even Asphere'", Assert.Throws<ArgumentException>(() => ReverseElements.Apply(reverse, 1, 2)).Message);
        var tilted = Doublet();
        tilted[2] = tilted[2] with { TiltBefore = new OpticsTiltDecenterSettings { TiltX = 1 } };
        Assert.Contains("tilt", Assert.Throws<ArgumentException>(() => ReverseElements.Apply(tilted, 1, 2)).Message);
    }
}
