using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;

namespace OpenSim.Optics.NonSequential.Scattering;

/// <summary>
/// The saved scattering of a project (plan item OP-80) turned into the scene's
/// <see cref="NscFaceScatter"/> and <see cref="NscScatterTo"/>: the ABg catalog saved with the
/// non-sequential system, each face's Coat/Scatter fields, each object's Scatter To section.
/// ABGF and BSDF files are read from the full paths saved on the face (OpticStudio looks in its
/// &lt;data&gt;\ABg_Data and &lt;data&gt;\Scatterdata folders; this program has no data folder).
/// </summary>
public static class ProjectNscScatter
{
    /// <summary>The saved model names, in <see cref="NscScatterModel"/> order.</summary>
    public static IReadOnlyList<string> ModelNames { get; } = new[] { "None", "Lambertian", "Gaussian", "ABg", "ABg File", "BSDF", "User Defined" };

    /// <summary>The saved Scatter To method names, in <see cref="NscScatterToMethod"/> order.</summary>
    public static IReadOnlyList<string> ScatterToNames { get; } = new[] { "None", "Scatter To List", "Importance Sampling" };

    /// <summary>The project's ABg catalog (empty when none is saved).</summary>
    public static NscAbgCatalog Catalog(OpticsNonSequentialSettings? saved) =>
        saved?.AbgCatalog is not { } list ? NscAbgCatalog.Empty : new NscAbgCatalog
        {
            Profiles = list.Select(p => new NscAbgProfile
            {
                Name = (p.Name ?? "").Trim(),
                WavelengthMicrometers = p.Wavelength ?? 0,
                Rows = (p.Rows ?? new List<OpticsAbgRowSettings>()).Select(r => new NscAbgRow(r.Angle ?? 0, r.A ?? 0, r.B ?? 0, r.G ?? 0)).ToArray(),
            }).ToArray(),
        };

    /// <summary>The catalog as saved rows.</summary>
    public static List<OpticsAbgProfileSettings> ToSettings(NscAbgCatalog catalog) =>
        catalog.Profiles.Select(p => new OpticsAbgProfileSettings
        {
            Name = p.Name,
            Wavelength = p.WavelengthMicrometers,
            Rows = p.Rows.Select(r => new OpticsAbgRowSettings { Angle = r.AngleDegrees, A = r.A, B = r.B, G = r.G }).ToList(),
        }).ToList();

    /// <summary>The model a saved name names (ignoring case); null when unknown.</summary>
    public static NscScatterModel? ModelOf(string? name)
    {
        string t = (name ?? "").Trim();
        if (t.Length == 0) return NscScatterModel.None;
        int i = ModelNames.ToList().FindIndex(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : (NscScatterModel)i;
    }

    /// <summary>A saved face's scattering; null for none. Problems name the face.</summary>
    public static NscFaceScatter? Face(OpticsNscFaceSettings f, OpticsAnalysisContext context, string where, List<string> problems)
    {
        var model = ModelOf(f.ScatterModel);
        if (model is null)
        {
            problems.Add($"{where}: the scatter model '{f.ScatterModel}' is not one of {string.Join(", ", ModelNames)}.");
            return null;
        }
        if (model == NscScatterModel.None) return null;
        if (model == NscScatterModel.UserDefined)
        {
            problems.Add($"{where}: User Defined scattering (a scatter DLL) is not built; no DLLs are loaded.");
            return null;
        }
        var face = new NscFaceScatter
        {
            Model = model.Value,
            NumberOfRays = f.ScatterRays ?? 1,
            Fraction = f.ScatterFraction ?? 0,
            Sigma = f.ScatterSigma ?? 0,
            BsdfAngleDegrees = f.ScatterAngle ?? 0,
            ThinWindow = f.ThinWindowScattering ?? false,
        };
        string? reflect = string.IsNullOrWhiteSpace(f.ScatterReflect) ? null : f.ScatterReflect.Trim();
        string? transmit = string.IsNullOrWhiteSpace(f.ScatterTransmit) ? null : f.ScatterTransmit.Trim();
        switch (model.Value)
        {
            case NscScatterModel.ABg:
            case NscScatterModel.ABgFile:
            {
                var catalog = Catalog(context.System.NonSequential);
                foreach (var p in catalog.Validate()) problems.Add($"{where}: {p}");
                if (model == NscScatterModel.ABg)
                {
                    NscAbgProfile? Find(string? name, string side)
                    {
                        if (name is null) return null;
                        var found = catalog.Find(name);
                        if (found is null) problems.Add($"{where}: the ABg {side} profile '{name}' is not in the project's ABg catalog.");
                        return found;
                    }
                    face = face with { AbgReflect = Find(reflect, "reflect"), AbgTransmit = Find(transmit, "transmit") };
                }
                else
                {
                    NscAbgSum? Read(string? path, string side)
                    {
                        if (path is null) return null;
                        try { return NscAbgSum.Parse(ReadFile(path, ".ABGF"), Path.GetFileName(path), catalog); }
                        catch (Exception e) when (e is FormatException or IOException or ArgumentException or UnauthorizedAccessException)
                        {
                            problems.Add($"{where}, ABg File {side}: {e.Message}");
                            return null;
                        }
                    }
                    face = face with { AbgFileReflect = Read(reflect, "reflect"), AbgFileTransmit = Read(transmit, "transmit") };
                }
                break;
            }
            case NscScatterModel.Bsdf:
            {
                NscBsdfTable? Read(string? path, string side)
                {
                    if (path is null) return null;
                    try { return NscBsdfTable.Parse(ReadFile(path, ".BSDF"), Path.GetFileName(path)); }
                    catch (Exception e) when (e is FormatException or IOException or ArgumentException or UnauthorizedAccessException)
                    {
                        problems.Add($"{where}, BSDF {side}: {e.Message}");
                        return null;
                    }
                }
                face = face with { BsdfReflect = Read(reflect, "reflect"), BsdfTransmit = Read(transmit, "transmit") };
                break;
            }
        }
        return face;
    }

    private static string ReadFile(string path, string extension)
    {
        if (!Path.IsPathRooted(path)) throw new ArgumentException($"\"{path}\" is not a full path; give the full path of the {extension} file.");
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"\"{Path.GetFileName(path)}\" does not have the {extension} extension the help requires.");
        if (!File.Exists(path)) throw new IOException($"the file \"{path}\" does not exist.");
        return File.ReadAllText(path);
    }

    /// <summary>A saved object's Scatter To section; null for none. Sizes convert from lens units.</summary>
    public static NscScatterTo? ScatterTo(OpticsNscObjectSettings row, LensUnit unit, string where, List<string> problems)
    {
        string method = (row.ScatterToMethod ?? "").Trim();
        int m = method.Length == 0 ? 0 : ScatterToNames.ToList().FindIndex(n => string.Equals(n, method, StringComparison.OrdinalIgnoreCase));
        if (m < 0) { problems.Add($"{where}: the Scatter To method '{row.ScatterToMethod}' is not one of {string.Join(", ", ScatterToNames)}."); return null; }
        if (m == 0) return null;
        var objects = new List<int>();
        foreach (var token in (row.ScatterToList ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k)) objects.Add(k);
            else problems.Add($"{where}: the Scatter To list entry '{token}' is not an object number.");
        }
        var targets = (row.ScatterTargets ?? new List<OpticsNscScatterTargetSettings>())
            .Select(t => new NscScatterTarget(t.Object ?? 0, OpticsConventions.ToMeters(t.Size ?? 0, unit), t.Limit ?? 0)).ToList();
        while (targets.Count > 0 && targets[^1].Object == 0) targets.RemoveAt(targets.Count - 1);   // trailing "None" rows
        return new NscScatterTo { Method = (NscScatterToMethod)m, Objects = objects, Targets = targets };
    }
}
