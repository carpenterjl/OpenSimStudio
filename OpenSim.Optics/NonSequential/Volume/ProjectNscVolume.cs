using OpenSim.Core.Model;

namespace OpenSim.Optics.NonSequential.Volume;

/// <summary>
/// The saved Volume Physics section of an object (<see cref="OpticsNscVolumePhysicsSettings"/>,
/// plan item OP-81) turned into an <see cref="NscVolumePhysics"/>, lengths from the lens unit.
/// Spectrum files are read from the full paths saved on the object (OpticStudio looks in its
/// "Objects\Phosphors and Fluorescence Files" folder; this program has no data folder). The
/// deprecated excitation spectrum (ZEX) and DLLs other than the help's sample DLLs are refused by
/// name.
/// </summary>
public static class ProjectNscVolume
{
    /// <summary>Saved model names, in <see cref="NscVolumeModel"/> order.</summary>
    public static IReadOnlyList<string> ModelNames { get; } = new[] { "None", "Angle Scattering", "DLL Defined Scattering", "Phosphors and Fluorescence" };

    /// <summary>Saved photoluminescence model names, in <see cref="NscPhotoluminescenceModel"/> order.</summary>
    public static IReadOnlyList<string> PhotoluminescenceNames { get; } = new[] { "Standard", "Basic" };

    public static NscVolumeModel? ModelOf(string? name)
    {
        string t = (name ?? "").Trim();
        if (t.Length == 0) return NscVolumeModel.None;
        int i = ModelNames.ToList().FindIndex(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : (NscVolumeModel)i;
    }

    /// <summary>The object's volume physics; null for none or on a problem (added to
    /// <paramref name="problems"/>, naming the object).</summary>
    public static NscVolumePhysics? Physics(OpticsNscVolumePhysicsSettings? saved, LensUnit unit, string where, List<string> problems)
    {
        if (saved is null) return null;
        where += ", Volume Physics";
        var model = ModelOf(saved.Model);
        if (model is null)
        {
            problems.Add($"{where}: the model '{saved.Model}' is not one of {string.Join(", ", ModelNames)}.");
            return null;
        }
        NscWavelengthShift? shift = null;
        if (!string.IsNullOrWhiteSpace(saved.WavelengthShift))
        {
            try { shift = NscWavelengthShift.Parse(saved.WavelengthShift); }
            catch (FormatException e) { problems.Add($"{where}: {e.Message}"); return null; }
        }
        if (model == NscVolumeModel.None && shift is null) return null;
        var physics = new NscVolumePhysics
        {
            Model = model.Value,
            MeanPath = OpticsConventions.ToMeters(saved.MeanPath ?? 0, unit),
            AngleDegrees = saved.Angle ?? 0,
            Dll = string.IsNullOrWhiteSpace(saved.Dll) ? null : saved.Dll.Trim(),
            DllParameters = saved.DllParameters?.ToArray() ?? Array.Empty<double>(),
            WavelengthShift = shift,
        };
        if (model == NscVolumeModel.PhosphorsAndFluorescence)
        {
            string pl = (saved.PhotoluminescenceModel ?? "Standard").Trim();
            int m = PhotoluminescenceNames.ToList().FindIndex(n => string.Equals(n, pl, StringComparison.OrdinalIgnoreCase));
            if (m < 0) { problems.Add($"{where}: the photoluminescence model '{saved.PhotoluminescenceModel}' is not Standard or Basic."); return null; }
            int before = problems.Count;
            var absorption = Read(saved.AbsorptionFile, ".ZAS", "absorption spectrum", t => NscSpectrumTable.Parse(t.Text, t.Name, 5));
            var emission = Read(saved.EmissionFile, ".ZES", "emission spectrum", t => NscEmissionTable.Parse(t.Text, t.Name));
            var yield = Read(saved.QuantumYieldFile, ".ZQE", "quantum yield spectrum", t => NscSpectrumTable.Parse(t.Text, t.Name, 1));
            if (problems.Count > before) return null;
            bool useMfp = saved.UseMieMeanFreePath ?? false;
            physics = physics with
            {
                Phosphor = new NscPhosphor
                {
                    Model = (NscPhotoluminescenceModel)m,
                    Absorption = absorption!, Emission = emission!, QuantumYield = yield!,
                    ExtinctionCoefficient = saved.ExtinctionCoefficient ?? 0,
                    ExtinctionMicrometers = saved.ExtinctionWavelength ?? 0,
                    DensityPerCm3 = saved.PhosphorDensity ?? 0,
                    ConsiderMie = saved.ConsiderMie ?? false,
                    Mie = saved.ParticleIndex is null && saved.ParticleRadius is null && saved.ParticleDensity is null && saved.MieMeanFreePath is null ? null
                        : new NscMieParticles
                        {
                            Index = saved.ParticleIndex ?? 1.5, RadiusMicrometers = saved.ParticleRadius ?? 0,
                            DensityPerCm3 = saved.ParticleDensity ?? 0, UseMeanFreePath = useMfp,
                            MeanFreePath = OpticsConventions.ToMeters(saved.MieMeanFreePath ?? 0, unit),
                        },
                },
            };
        }
        return physics;

        T? Read<T>(string? path, string extension, string what, Func<(string Text, string Name), T> parse) where T : class
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                problems.Add($"{where}: no {what} file; \"a spectrum file must be set\" for each of the absorption, emission and quantum yield spectra (help).");
                return null;
            }
            path = path.Trim();
            if (string.Equals(Path.GetExtension(path), ".ZEX", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{where}: \"{Path.GetFileName(path)}\" is an excitation spectrum (ZEX), which the help deprecates \"in favor of the Quantum Yield spectrum\"; it is not built. Give a ZQE file.");
                return null;
            }
            try
            {
                if (!Path.IsPathRooted(path)) throw new ArgumentException($"\"{path}\" is not a full path; give the full path of the {extension} file.");
                if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"\"{Path.GetFileName(path)}\" does not have the {extension} extension of an {what} file.");
                if (!File.Exists(path)) throw new IOException($"the file \"{path}\" does not exist.");
                return parse((File.ReadAllText(path), Path.GetFileName(path)));
            }
            catch (Exception e) when (e is FormatException or IOException or ArgumentException or UnauthorizedAccessException)
            {
                problems.Add($"{where}, {what}: {e.Message}");
                return null;
            }
        }
    }
}
