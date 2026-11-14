using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-101, the part of OP-10's gate "a saved lens reloads to bitwise-identical ray-trace output"
/// that exists today. Surfaces are not saved until OP-31, so the lens itself is built in the
/// test; what the files carry is the system data (aperture, fields, wavelengths, environment,
/// units). The file form of the system data, saved in a project and in a lens file and read
/// back, must give a system whose batch trace is the same to the bit as the system read from
/// the record before saving. When OP-31 saves surfaces this test should grow to save the lens too.
/// </summary>
public sealed class LensFileTraceGateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("opensim-op101-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static long B(double v) => BitConverter.DoubleToInt64Bits(v);

    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop,
    };

    private static readonly SequentialLens Lens = new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = 0.4 },
            S(0.05, 0.006, 1.62, stop: true), S(-0.035, 0.002, 1.72), S(-0.2, 0.08),
            new SequentialSurface(),
        }
    };

    /// <summary>The file form of a system with values that are not exact in binary, in lens units
    /// of millimeters, three wavelengths and object-height fields, saved through every path.</summary>
    private static OpticsSystemSettings Saved() => SystemDataSettings.ToSettings(new OpticalSystemData
    {
        Units = new SystemUnits { LensUnit = LensUnit.Millimeter },
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.0123 },
        Fields = new FieldSet
        {
            Type = FieldType.ObjectHeight,
            Points = new[] { new FieldPoint(), new FieldPoint { Y = 0.0071 }, new FieldPoint { X = 0.0033, Y = -0.0101 } }
        },
        Wavelengths = new WavelengthSet
        {
            Wavelengths = new[] { new Wavelength { Micrometers = 0.4861 }, new Wavelength { Micrometers = 0.5876 }, new Wavelength { Micrometers = 0.6563 } },
            PrimaryIndex = 1
        },
        Environment = new OpticsEnvironment { TemperatureCelsius = 23.7, PressureAtmospheres = 0.93 },
    });

    private static BatchTraceResult Trace(OpticsSystemSettings settings)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(settings, notes);
        Assert.Empty(notes);
        return BatchTracer.Trace(Lens, system, new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(9) });
    }

    private static void SameBits(BatchTraceResult a, BatchTraceResult b, string label)
    {
        Assert.Equal(a.Rays.Count, b.Rays.Count);
        for (int i = 0; i < a.Rays.Count; i++)
        {
            var (x, y) = (a.Rays[i].Ray, b.Rays[i].Ray);
            Assert.Equal(x.Status, y.Status);
            foreach (var (u, v) in new[]
            {
                (x.Position.X, y.Position.X), (x.Position.Y, y.Position.Y), (x.Position.Z, y.Position.Z),
                (x.Direction.X, y.Direction.X), (x.Direction.Y, y.Direction.Y), (x.Direction.Z, y.Direction.Z),
                (x.OpticalPath, y.OpticalPath), (x.Wavelength, y.Wavelength), (a.Rays[i].PathFromChief, b.Rays[i].PathFromChief),
            })
                Assert.True(B(u) == B(v), $"{label}, ray {i}: {u:R} vs {v:R}");
        }
    }

    [Fact]
    public void TheSystemData_SavedAndReloaded_TracesToTheSameBits_ThroughTheLensFileAndTheProject()
    {
        var saved = Saved();
        var before = Trace(saved);
        Assert.All(before.Rays, r => Assert.True(r.Ray.IsOk));

        string lensPath = Path.Combine(_directory, "lens" + OpticsFileSerializer.FileExtension);
        var lensFiles = new OpticsFileSerializer();
        lensFiles.Save(new OpticsLensFile { Name = "Gate", Optics = new OpticsSettings { SystemData = saved } }, lensPath);
        SameBits(before, Trace(lensFiles.Load(lensPath).Optics!.SystemData!), "lens file");

        string projectPath = Path.Combine(_directory, "lens.ossproj");
        var projects = new ProjectSerializer();
        projects.Save(new SimProject { Name = "Gate", AnalysisType = "Optics", Optics = new OpticsSettings { SystemData = saved } }, projectPath);
        SameBits(before, Trace(projects.Load(projectPath).Optics!.SystemData!), "project file");
    }
}
