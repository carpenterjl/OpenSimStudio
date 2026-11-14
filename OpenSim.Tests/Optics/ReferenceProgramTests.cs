using System.Numerics;
using System.Text.Json;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-102: cross-checks against independently written programs. The reference values are stored
/// files in OpenSim.Tests/References/OP-102, written once by the committed Python scripts there
/// (gen_optiland.py, gen_rayoptics.py, gen_tmm.py; versions and how to regenerate in its
/// README.md) and never computed in the test suite. Every expected number is read from those
/// files; nothing is typed in. The lens prescriptions (lenses.py, repeated in each file) are
/// arbitrary test inputs with constant indices, not reference data.
/// <para>
/// Gates (plan, OP-102): per-surface ray coordinates within 1e-9 mm and optical path within
/// 1e-6 waves. Where a program's own model limits the agreement (RayOptics' 1e10 mm stand-in for
/// an object at infinity, in its first-order data), the test says so and derives the allowance.
/// The convention mapping between the programs and OpenSim is docs/Optics-Conventions.md §22.
/// When the reference files are absent every test here is reported as skipped with the folder
/// it looked for.
/// </para>
/// </summary>
public sealed class ReferenceProgramTests
{
    // ---- The gates ----

    private const double Mm = 1e-3;
    /// <summary>1e-9 mm, in meters.</summary>
    private const double PositionGate = 1e-9 * Mm;
    /// <summary>Direction cosines: dimensionless. The two programs agree with each other to
    /// 2e-15 (README); 1e-12 leaves room for a few hundred roundings and is still far below
    /// anything a 1e-9 mm position gate could miss over the lens lengths here.</summary>
    private const double DirectionGate = 1e-12;
    /// <summary>1e-6 waves of the table wavelength (in the files, µm), in meters.</summary>
    private static double PathGate(double wavelengthMicrometers) => 1e-6 * wavelengthMicrometers * 1e-6;

    public static readonly TheoryData<string, string> RayCases = Cases();

    private static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var program in ReferenceFiles.Programs)
            foreach (var lens in ReferenceFiles.Lenses)
                data.Add(program, lens);
        return data;
    }

    // ---- Real rays ----

    /// <summary>
    /// Each stored ray is launched in OpenSim from the program's own launch point and direction
    /// (surface 1's frame), so the comparison is of the trace alone: at every surface from 1 to
    /// the image, the local intersection point (vertex at the origin), the direction cosines
    /// after refraction or reflection, and the optical path from the launch point.
    /// </summary>
    [ReferenceTheory]
    [MemberData(nameof(RayCases))]
    public void RealRays_MatchTheReferenceProgram_SurfaceBySurface(string program, string lensName)
    {
        var file = ReferenceFiles.Load(program, lensName);
        var trace = SequentialTrace.Create(file.Lens, file.System);
        double pathGate = PathGate(file.WavelengthMicrometers);
        int checkedHits = 0;
        foreach (var r in file.Rays)
        {
            var path = trace.TracePath(trace.NewRay(r.LaunchPosition, r.LaunchDirection));
            string ray = $"{program} {lensName} ray (Hx {r.Hx}, Hy {r.Hy}, Px {r.Px}, Py {r.Py})";
            Assert.True(path.Status == RayStatus.Ok, $"{ray}: OpenSim stopped it ({path.Status} at surface {path.FailedAtSurface}).");
            Assert.Equal(r.Surfaces.Count + 1, path.Hits.Count);
            for (int j = 1; j < path.Hits.Count; j++)
            {
                var hit = path.Hits[j];
                var expected = r.Surfaces[j - 1];
                string where = $"{ray}, surface {j}";
                Near(expected.Position, hit.Position, PositionGate, where + " position");
                Near(expected.Direction, hit.Direction, DirectionGate, where + " direction");
                Close(expected.OpticalPath, hit.OpticalPath, pathGate, where + " optical path");
                checkedHits++;
            }
        }
        Assert.True(checkedHits >= 24 * 2, $"only {checkedHits} surface hits compared");
    }

    /// <summary>
    /// OpenSim's launcher (ray aiming off, paraxial entrance pupil of the primary wavelength)
    /// against Optiland's default "paraxial" ray aimer, which follows the same rule (README):
    /// the directions agree, and OpenSim's start point lies on Optiland's ray line. Only the
    /// line is compared, because the two place the start point differently along it (OpenSim on
    /// the plane through surface 1's vertex at right angles to the ray for an object at
    /// infinity, Optiland a chosen distance in front of the entrance pupil). RayOptics' launch
    /// rays are built by the generator script, not by RayOptics, so they are not compared here.
    /// </summary>
    [ReferenceTheory]
    [MemberData(nameof(LensNames))]
    public void Launcher_PutsEachRayOnOptilandsLine(string lensName)
    {
        var file = ReferenceFiles.Load("optiland", lensName);
        var trace = SequentialTrace.Create(file.Lens, file.System);
        var launcher = new RayLauncher(file.Lens, file.System);
        foreach (var r in file.Rays)
        {
            var ours = launcher.Launch(trace, r.Hx, r.Hy, r.Px, r.Py);
            string ray = $"optiland {lensName} ray (Hx {r.Hx}, Hy {r.Hy}, Px {r.Px}, Py {r.Py})";
            Near(r.LaunchDirection, ours.Direction, DirectionGate, ray + " launch direction");
            // The point of the reference line nearest our start point.
            var offset = ours.Position - r.LaunchPosition;
            var onLine = r.LaunchPosition + r.LaunchDirection * Vector3D.Dot(offset, r.LaunchDirection);
            Near(onLine, ours.Position, PositionGate, ray + " launch point off the reference line");
        }
    }

    public static readonly TheoryData<string> LensNames = Names();

    private static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var lens in ReferenceFiles.Lenses) data.Add(lens);
        return data;
    }

    // ---- First-order data ----

    /// <summary>
    /// Optiland's paraxial data (paraxial.py) against OP-19. Mapping (§22): f2 is n′/Φ with the
    /// signed image-space index (−100 for a concave mirror), OpenSim's ImageSpaceFocalLength; f1
    /// is −n/Φ, ObjectSpaceFocalLength; F1, P1 and EPL are measured from surface 1 like
    /// OpenSim's front focal length, front principal plane and entrance pupil; F2, P2 and XPL are
    /// measured from the image surface, so the last thickness is added to compare with OpenSim's
    /// back focal length, rear principal plane and exit pupil (from the last surface); XPD is
    /// 2·(marginal height at the exit pupil), compared in magnitude.
    /// </summary>
    [ReferenceTheory]
    [MemberData(nameof(LensNames))]
    public void ParaxialData_MatchesOptiland(string lensName)
    {
        var file = ReferenceFiles.Load("optiland", lensName);
        var p = ParaxialSystem.Create(file.Lens.ToParaxialLens(), file.System);
        double tLast = file.Lens.Surfaces[file.Lens.LastSurfaceIndex].Thickness;
        double gate = PositionGate;
        string w = $"optiland {lensName}";
        Close(file.Paraxial("f2") * Mm, p.ImageSpaceFocalLength!.Value, gate, w + " f2 / image-space focal length");
        Close(file.Paraxial("f1") * Mm, p.ObjectSpaceFocalLength!.Value, gate, w + " f1 / object-space focal length");
        Close(file.Paraxial("F1") * Mm, p.FrontFocalLength!.Value, gate, w + " F1 / front focal length");
        Close(file.Paraxial("F2") * Mm + tLast, p.BackFocalLength!.Value, gate, w + " F2 + t / back focal length");
        Close(file.Paraxial("P1") * Mm, p.FrontPrincipalPlane!.Value, gate, w + " P1 / front principal plane");
        Close(file.Paraxial("P2") * Mm + tLast, p.RearPrincipalPlane!.Value, gate, w + " P2 + t / rear principal plane");
        Close(file.Paraxial("EPL") * Mm, p.EntrancePupilPosition!.Value, gate, w + " EPL / entrance pupil");
        Close(file.Paraxial("EPD") * Mm, p.EntrancePupilDiameter!.Value, gate, w + " EPD");
        Close(file.Paraxial("XPL") * Mm + tLast, p.ExitPupilPosition!.Value, gate, w + " XPL + t / exit pupil");
        Close(Math.Abs(file.Paraxial("XPD")) * Mm, Math.Abs(p.ExitPupilDiameter!.Value), gate, w + " XPD");
    }

    /// <summary>
    /// RayOptics' first-order data (parax/firstorder.py, compute_first_order) against OP-19.
    /// Mapping (§22): efl = fl_img = n_k/power, n_k signed by the mirrors (OpenSim's
    /// ImageSpaceFocalLength); fl_obj = n_0/power, the negative of OpenSim's object-space focal
    /// length; ffl, pp1 and enp_dist from surface 1; bfl and ppk from the last surface;
    /// exp_dist = img_dist − h̄_k/ū_k, which is the exit pupil from the last surface only when
    /// the image surface is at the paraxial image, so exp_dist − img_dist + t_last is compared.
    /// <para>
    /// RayOptics stands in 1e10 mm for an object at infinity. The system-matrix quantities (efl,
    /// fl_obj, ffl, bfl, pp1, ppk) do not depend on the object and are held to the 1e-9 mm gate.
    /// The conjugate-dependent ones (pupils, img_dist) then belong to an object at L = 1e10 mm:
    /// by Newton's equation x·x′ = −f·f′ an image moves by f²/L, and the pupils, images of the
    /// stop through part of the lens, by the same order with that part's focal length, which
    /// the system's |EFL| bounds in these lenses. So they are allowed 1e-9 mm + 2·EFL²/L (7.2e-5 mm
    /// for the two-mirror lens, 5e-7 mm for the triplet). The stored files sit at about half of
    /// that allowance where the stand-in shows (img_dist moves by f²/L exactly), and well inside
    /// it elsewhere (README).
    /// With a finite object there is no stand-in and the gate is 1e-9 mm.
    /// </para>
    /// </summary>
    [ReferenceTheory]
    [MemberData(nameof(LensNames))]
    public void ParaxialData_MatchesRayOptics(string lensName)
    {
        var file = ReferenceFiles.Load("rayoptics", lensName);
        var p = ParaxialSystem.Create(file.Lens.ToParaxialLens(), file.System);
        double tLast = file.Lens.Surfaces[file.Lens.LastSurfaceIndex].Thickness;
        string w = $"rayoptics {lensName}";
        double gate = PositionGate;
        Close(file.Paraxial("efl") * Mm, p.ImageSpaceFocalLength!.Value, gate, w + " efl / image-space focal length");
        Close(-file.Paraxial("fl_obj") * Mm, p.ObjectSpaceFocalLength!.Value, gate, w + " −fl_obj / object-space focal length");
        Close(file.Paraxial("ffl") * Mm, p.FrontFocalLength!.Value, gate, w + " ffl");
        Close(file.Paraxial("bfl") * Mm, p.BackFocalLength!.Value, gate, w + " bfl");
        Close(file.Paraxial("pp1") * Mm, p.FrontPrincipalPlane!.Value, gate, w + " pp1");
        Close(file.Paraxial("ppk") * Mm, p.RearPrincipalPlane!.Value, gate, w + " ppk");

        double conjugateGate = gate;
        if (file.Lens.ObjectAtInfinity)
        {
            const double standIn = 1e10 * Mm;   // RayOptics' object distance for infinity
            double efl = p.EffectiveFocalLength!.Value;
            conjugateGate += 2 * efl * efl / standIn;
        }
        Close(file.Paraxial("enp_dist") * Mm, p.EntrancePupilPosition!.Value, conjugateGate, w + " enp_dist");
        Close(2 * file.Paraxial("enp_radius") * Mm, p.EntrancePupilDiameter!.Value, conjugateGate, w + " 2·enp_radius");
        Close((file.Paraxial("exp_dist") - file.Paraxial("img_dist")) * Mm + tLast, p.ExitPupilPosition!.Value,
            conjugateGate, w + " exp_dist − img_dist + t / exit pupil");
        Close(2 * file.Paraxial("exp_radius") * Mm, Math.Abs(p.ExitPupilDiameter!.Value), conjugateGate, w + " 2·exp_radius");
        Close(file.Paraxial("img_dist") * Mm, p.ParaxialImageDistance!.Value, conjugateGate, w + " img_dist / paraxial image");
    }

    // ---- Thin films ----

    /// <summary>
    /// OP-15's engine against tmm.coh_tmm for every stored stack, wavelength, angle and
    /// polarization: the complex amplitudes r and t (so their phases too), R, T, and the
    /// absorbed fraction as tmm's power_entering − T. Both use n + ik with k ≥ 0 and the same
    /// amplitude convention (docs/Optics-Conventions.md §12), so no mapping is needed. Beyond the
    /// critical angle tmm's T is a rounding residue (about 1e-16) where OpenSim's is exactly 0.
    /// The gate, 1e-12 on quantities of order 1, is set by double rounding through up to nine
    /// layers, as in ThinFilmTests (which holds two cases of the older tmm copy to 1e-13).
    /// </summary>
    [ReferenceFact]
    public void ThinFilms_MatchTmm()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ReferenceFiles.PathOf("tmm_stacks.json")));
        var root = doc.RootElement;
        var stacks = root.GetProperty("stacks");
        const double gate = 1e-12;
        int count = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("stack").GetString()!;
            var stack = stacks.GetProperty(name);
            var layers = stack.GetProperty("layers").EnumerateArray()
                .Select(l => new ThinFilmLayer(new Complex(l[0].GetDouble(), l[1].GetDouble()), l[2].GetDouble())).ToArray();
            var sub = stack.GetProperty("substrate");
            double lambda = c.GetProperty("wavelength_nm").GetDouble();
            double degrees = c.GetProperty("angle_deg").GetDouble();
            var result = ThinFilm.Compute(stack.GetProperty("incident").GetDouble(), layers,
                new Complex(sub[0].GetDouble(), sub[1].GetDouble()), lambda, degrees * Math.PI / 180);
            foreach (var (pol, ours) in new[] { ("s", result.S), ("p", result.P) })
            {
                var e = c.GetProperty(pol);
                string where = $"tmm {name} {lambda} nm {degrees}° {pol}";
                var r = new Complex(e.GetProperty("r")[0].GetDouble(), e.GetProperty("r")[1].GetDouble());
                var t = new Complex(e.GetProperty("t")[0].GetDouble(), e.GetProperty("t")[1].GetDouble());
                Assert.True(Complex.Abs(ours.R - r) <= gate, $"{where}: r expected {r}, got {ours.R}");
                Assert.True(Complex.Abs(ours.T - t) <= gate, $"{where}: t expected {t}, got {ours.T}");
                Close(e.GetProperty("R").GetDouble(), ours.Reflectance, gate, where + " R");
                Close(e.GetProperty("T").GetDouble(), ours.Transmittance, gate, where + " T");
                Close(e.GetProperty("power_entering").GetDouble() - e.GetProperty("T").GetDouble(), ours.Absorptance, gate, where + " A");
                count++;
            }
        }
        Assert.True(count >= 60, $"only {count} thin-film cases compared");
    }

    // ---- Helpers ----

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    private static void Near(Vector3D expected, Vector3D actual, double tolerance, string what)
    {
        double d = Math.Max(Math.Abs(actual.X - expected.X), Math.Max(Math.Abs(actual.Y - expected.Y), Math.Abs(actual.Z - expected.Z)));
        Assert.True(d <= tolerance,
            $"{what}: expected ({expected.X:R}, {expected.Y:R}, {expected.Z:R}), got ({actual.X:R}, {actual.Y:R}, {actual.Z:R}), largest difference {d:R}, allowed {tolerance:R}.");
    }
}

/// <summary>A fact skipped, with the folder named, when the OP-102 reference files are absent.</summary>
public sealed class ReferenceFactAttribute : FactAttribute
{
    public ReferenceFactAttribute()
    {
        if (ReferenceFiles.MissingReason() is { } reason) Skip = reason;
    }
}

/// <summary>A theory skipped, with the folder named, when the OP-102 reference files are absent.</summary>
public sealed class ReferenceTheoryAttribute : TheoryAttribute
{
    public ReferenceTheoryAttribute()
    {
        if (ReferenceFiles.MissingReason() is { } reason) Skip = reason;
    }
}

/// <summary>
/// The OP-102 reference files (OpenSim.Tests/References/OP-102), found by walking up from the
/// test assembly's folder to the source tree, and their reader. Lengths in the files are
/// millimetres and are returned in meters; JSON null is an infinite radius or object distance.
/// </summary>
internal static class ReferenceFiles
{
    public static readonly string[] Programs = { "optiland", "rayoptics" };
    public static readonly string[] Lenses = { "singlet", "doublet_finite", "triplet", "parabola", "two_mirror" };

    private static readonly Lazy<string?> Folder = new(Find);

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.FullName, "OpenSim.Tests", "References", "OP-102"),
                         Path.Combine(dir.FullName, "References", "OP-102"),
                     })
                if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> ExpectedFiles() =>
        Programs.SelectMany(p => Lenses.Select(l => $"{p}_{l}.json")).Append("tmm_stacks.json");

    /// <summary>Null when every file is present; otherwise why the tests are skipped.</summary>
    public static string? MissingReason()
    {
        var folder = Folder.Value;
        if (folder is null)
            return "OP-102 reference files not found: no OpenSim.Tests/References/OP-102 folder above "
                + AppContext.BaseDirectory + ". Regenerate them with the scripts in that folder (README.md).";
        var missing = ExpectedFiles().Where(f => !File.Exists(Path.Combine(folder, f))).ToArray();
        return missing.Length == 0
            ? null
            : $"OP-102 reference files missing in {folder}: {string.Join(", ", missing)}. Regenerate them with the scripts there (README.md).";
    }

    public static string PathOf(string fileName) => Path.Combine(Folder.Value!, fileName);

    public static LensFile Load(string program, string lens)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(PathOf($"{program}_{lens}.json")));
        return LensFile.Read(doc.RootElement);
    }
}

internal sealed record ReferenceHit(Vector3D Position, Vector3D Direction, double OpticalPath);

internal sealed record ReferenceRay(double Hx, double Hy, double Px, double Py,
    Vector3D LaunchPosition, Vector3D LaunchDirection, IReadOnlyList<ReferenceHit> Surfaces);

/// <summary>One program's file for one lens: the lens and system it describes, its paraxial
/// values (millimetres, under the program's own names) and its rays (meters).</summary>
internal sealed class LensFile
{
    private const double Mm = 1e-3;
    private readonly Dictionary<string, double> _paraxial = new();

    public required SequentialLens Lens { get; init; }
    public required OpticalSystemData System { get; init; }
    public required double WavelengthMicrometers { get; init; }
    public required IReadOnlyList<ReferenceRay> Rays { get; init; }

    /// <summary>A paraxial value as the file writes it (millimetres for lengths).</summary>
    public double Paraxial(string name) => _paraxial.TryGetValue(name, out var v)
        ? v
        : throw new KeyNotFoundException($"The reference file has no paraxial value '{name}'.");

    public static LensFile Read(JsonElement root)
    {
        if (root.GetProperty("units").GetString() != "mm")
            throw new InvalidDataException("OP-102 reference files are written in millimetres.");
        var surfaces = root.GetProperty("surfaces").EnumerateArray().ToArray();
        var list = new List<SequentialSurface>();
        for (int j = 0; j < surfaces.Length; j++)
        {
            var s = surfaces[j];
            double? radius = Nullable(s.GetProperty("radius"));
            double? thickness = Nullable(s.GetProperty("thickness"));
            bool mirror = s.GetProperty("mirror").GetBoolean();
            double? index = Nullable(s.GetProperty("index"));
            list.Add(new SequentialSurface
            {
                Curvature = radius is double r ? OpticsConventions.CurvatureFromRadius(r * Mm) : 0,
                Conic = s.GetProperty("conic").GetDouble(),
                Thickness = thickness is double t ? t * Mm : double.PositiveInfinity,
                Medium = mirror || index is null ? ParaxialMedium.Air : ParaxialMedium.Constant(index.Value),
                IsStop = s.GetProperty("stop").GetBoolean(),
                IsMirror = mirror,
            });
        }
        var lens = new SequentialLens { Surfaces = list };

        string fieldType = root.GetProperty("field_type").GetString()!;
        bool angle = fieldType == "angle";
        if (!angle && fieldType != "object_height")
            throw new InvalidDataException($"Unknown field type '{fieldType}' in an OP-102 reference file.");
        double wavelength = root.GetProperty("wavelength_um").GetDouble();
        var system = new OpticalSystemData
        {
            Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = root.GetProperty("epd").GetDouble() * Mm },
            Fields = new FieldSet
            {
                Type = angle ? FieldType.Angle : FieldType.ObjectHeight,
                Points = root.GetProperty("fields").EnumerateArray()
                    .Select(f => new FieldPoint { Y = angle ? f.GetDouble() : f.GetDouble() * Mm }).ToArray(),
            },
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = wavelength } } },
        };

        var rays = new List<ReferenceRay>();
        foreach (var r in root.GetProperty("rays").EnumerateArray())
        {
            var launch = r.GetProperty("launch");
            var hits = r.GetProperty("surfaces").EnumerateArray().Select(h => new ReferenceHit(
                new Vector3D(h[0].GetDouble() * Mm, h[1].GetDouble() * Mm, h[2].GetDouble() * Mm),
                new Vector3D(h[3].GetDouble(), h[4].GetDouble(), h[5].GetDouble()),
                h[6].GetDouble() * Mm)).ToArray();
            rays.Add(new ReferenceRay(
                r.GetProperty("hx").GetDouble(), r.GetProperty("hy").GetDouble(),
                r.GetProperty("px").GetDouble(), r.GetProperty("py").GetDouble(),
                new Vector3D(launch[0].GetDouble() * Mm, launch[1].GetDouble() * Mm, launch[2].GetDouble() * Mm),
                new Vector3D(launch[3].GetDouble(), launch[4].GetDouble(), launch[5].GetDouble()),
                hits));
        }

        var file = new LensFile { Lens = lens, System = system, WavelengthMicrometers = wavelength, Rays = rays };
        foreach (var p in root.GetProperty("paraxial").EnumerateObject())
            file._paraxial[p.Name] = p.Value.GetDouble();
        return file;
    }

    private static double? Nullable(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDouble();
}
