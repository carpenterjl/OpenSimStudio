using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-101: every parallel path in OpenSim.Optics is bitwise identical to its sequential form at
/// any degree of parallelism, and a repeated run gives the same bits (CONTRIBUTING.md, "Writing
/// numerical tests"; the pattern of Rf/RfDeterminismTests). The parallel paths, found by
/// searching OpenSim.Optics for ParallelSlots and Parallel.: <see cref="BatchTracer"/> (one slot
/// per field and wavelength) and <see cref="SemiDiameterSizing"/> (the same, sizing apertures),
/// which BatchTracer also calls when a floating aperture needs sizes. The Core paths optics
/// uses (Fft2D, MatrixFourierTransform, DampedLeastSquares) have their own determinism tests
/// in Core/DenseTransformsTests and Core/OptimizerTests.
/// </summary>
public sealed class OpticsDeterminismTests
{
    private static long B(double v) => BitConverter.DoubleToInt64Bits(v);

    private static readonly int?[] Dops = { 1, 2, 3, 7, null };

    private static SequentialSurface S(double radius, double thickness, double n = 1, double k = 0, bool stop = false,
        SurfaceAperture? aperture = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, Aperture = aperture,
    };

    /// <summary>A conic triplet with floating apertures on two surfaces (so sizing runs inside
    /// the batch trace) and a fixed circular aperture smaller than the beam on surface 5, so
    /// some rays are vignetted and failed rays are compared too.</summary>
    private static SequentialLens Triplet() => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            S(0.05, 0.008, 1.6, k: -0.5, stop: true),
            S(-0.08, 0.002, aperture: new FloatingAperture()),
            S(0.1, 0.005, 1.75, k: 2.0),
            S(0.06, 0.003, aperture: new FloatingAperture()),
            S(0.08, 0.006, 1.5, k: -3, aperture: new CircularAperture(0, 0.006)),
            S(-0.06, 0.06),
            new SequentialSurface(),
        }
    };

    private static OpticalSystemData System() => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.02, SemiDiameterMargin = 0.0002, SemiDiameterMarginPercent = 3 },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = new[] { new FieldPoint(), new FieldPoint { Y = 4 }, new FieldPoint { X = -3, Y = 6 }, new FieldPoint { Y = 10 } }
        },
        Wavelengths = new WavelengthSet
        {
            Wavelengths = new[] { new Wavelength { Micrometers = 0.486 }, new Wavelength { Micrometers = 0.588 }, new Wavelength { Micrometers = 0.656 } }
        },
    };

    private static void SameRay(Ray a, Ray b, string where)
    {
        Assert.True(a.Status == b.Status, $"{where}: status {a.Status} vs {b.Status}");
        Assert.True(a.FailedAtSurface == b.FailedAtSurface, $"{where}: failed at {a.FailedAtSurface} vs {b.FailedAtSurface}");
        foreach (var (x, y, what) in new[]
        {
            (a.Position.X, b.Position.X, "x"), (a.Position.Y, b.Position.Y, "y"), (a.Position.Z, b.Position.Z, "z"),
            (a.Direction.X, b.Direction.X, "L"), (a.Direction.Y, b.Direction.Y, "M"), (a.Direction.Z, b.Direction.Z, "N"),
            (a.OpticalPath, b.OpticalPath, "optical path"), (a.Wavelength, b.Wavelength, "wavelength"),
        })
            Assert.True(B(x) == B(y), $"{where}: {what} {x:R} vs {y:R}");
    }

    private static void SameResult(BatchTraceResult a, BatchTraceResult b, string label)
    {
        Assert.Equal(a.Rays.Count, b.Rays.Count);
        Assert.Equal(a.ChiefRays.Count, b.ChiefRays.Count);
        for (int i = 0; i < a.Rays.Count; i++)
        {
            SameRay(a.Rays[i].Ray, b.Rays[i].Ray, $"{label}, ray {i}");
            Assert.True(B(a.Rays[i].PathFromChief) == B(b.Rays[i].PathFromChief), $"{label}, ray {i}: path from chief");
        }
        for (int i = 0; i < a.ChiefRays.Count; i++) SameRay(a.ChiefRays[i], b.ChiefRays[i], $"{label}, chief {i}");
    }

    [Fact]
    public void TheBatchTrace_WithAutomaticSemiDiameters_IsBitwiseTheSame_AtEveryDop_AndOnRepeat()
    {
        var lens = Triplet();
        var system = System();
        var request = new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(15) };
        var serial = BatchTracer.Trace(lens, system, request, maxDegreeOfParallelism: 1);
        // The case is not trivial: some rays pass, and some are vignetted on surface 5.
        Assert.Contains(serial.Rays, r => r.Ray.IsOk);
        Assert.Contains(serial.Rays, r => !r.Ray.IsOk);
        foreach (var dop in Dops)
            for (int repeat = 0; repeat < 2; repeat++)
                SameResult(serial, BatchTracer.Trace(lens, system, request, maxDegreeOfParallelism: dop), $"DOP {dop?.ToString() ?? "unbounded"}, run {repeat + 1}");
    }

    [Fact]
    public void TheBatchTrace_OfASubsetOfFieldsAndWavelengths_IsTheSameRaysAsTheFullTrace()
    {
        // The slot of a field and wavelength depends only on them, not on which others are traced.
        var lens = Triplet();
        var system = System();
        var pupil = BatchTracer.SquareGrid(9);
        var semi = SemiDiameterSizing.Compute(lens, system, maxDegreeOfParallelism: 1).Clear;
        var full = BatchTracer.Trace(lens, system, new BatchTraceRequest { Pupil = pupil, SemiDiameters = semi }, maxDegreeOfParallelism: null);
        var subset = BatchTracer.Trace(lens, system, new BatchTraceRequest
        {
            Pupil = pupil, SemiDiameters = semi, Fields = new[] { 2, 0 }, Wavelengths = new[] { 1 },
        }, maxDegreeOfParallelism: 3);
        for (int f = 0; f < 2; f++)
            for (int p = 0; p < pupil.Count; p++)
            {
                int field = subset.Fields[f];
                SameRay(full.At(field, 1, p).Ray, subset.At(f, 0, p).Ray, $"field {field}, pupil {p}");
                Assert.True(B(full.At(field, 1, p).PathFromChief) == B(subset.At(f, 0, p).PathFromChief));
            }
    }

    [Fact]
    public void SemiDiameterSizing_IsBitwiseTheSame_AtEveryDop_AndOnRepeat()
    {
        var lens = Triplet();
        var system = System();
        foreach (int rim in new[] { 4, 16 })
        {
            var serial = SemiDiameterSizing.Compute(lens, system, rimPoints: rim, maxDegreeOfParallelism: 1);
            Assert.All(serial.Clear.Skip(1), c => Assert.True(c > 0));
            foreach (var dop in Dops)
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    var other = SemiDiameterSizing.Compute(lens, system, rimPoints: rim, maxDegreeOfParallelism: dop);
                    string label = $"rim {rim}, DOP {dop?.ToString() ?? "unbounded"}, run {repeat + 1}";
                    Assert.Equal(serial.Clear.Count, other.Clear.Count);
                    for (int j = 0; j < serial.Clear.Count; j++)
                    {
                        Assert.True(B(serial.Clear[j]) == B(other.Clear[j]), $"{label}: clear semi-diameter of surface {j}");
                        Assert.True(B(serial.Mechanical[j]) == B(other.Mechanical[j]), $"{label}: mechanical semi-diameter of surface {j}");
                        Assert.Equal(serial.Automatic[j], other.Automatic[j]);
                    }
                    // The notes come out in slot order, whatever order the slots finished in.
                    Assert.Equal(serial.Notes, other.Notes);
                }
        }
    }
}
