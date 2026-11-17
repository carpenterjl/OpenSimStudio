using System.Globalization;

namespace OpenSim.Optics.NonSequential.Scattering;

/// <summary>The scatter models of OpticStudio's Coat/Scatter section (help, "Scattering Models":
/// "none, Lambertian, Gaussian, ABg, ABg File, BSDF, and user defined").</summary>
public enum NscScatterModel
{
    None = 0,
    Lambertian = 1,
    Gaussian = 2,
    ABg = 3,
    ABgFile = 4,
    Bsdf = 5,
    /// <summary>A scatter DLL: refused by name (no DLLs are loaded by this program).</summary>
    UserDefined = 6,
}

/// <summary>
/// One face's scattering (plan item OP-80; docs/Optics-Conventions.md §74; help, "Coat/Scatter",
/// "Scattering Models", "Fraction to Scatter and Number of Scatter Rays", "Thin Window
/// Scattering"). <see cref="Fraction"/> is used by Lambertian, Gaussian and BSDF (the ABg models
/// take their fraction from their TIS). Lambertian and Gaussian scatter the reflected and the
/// refracted specular ray alike; ABg, ABg File and BSDF have separate data for reflection and
/// transmission (null: that specular ray does not scatter).
/// </summary>
public sealed record NscFaceScatter
{
    public NscScatterModel Model { get; init; }

    /// <summary>"Number Of Rays": 0 to 300 ("If the number of scatter rays is set to zero, then the
    /// fraction to scatter is ignored and no scattering occurs. The maximum number of scatter rays
    /// is 300"). Used only with ray splitting. This program's default 1.</summary>
    public int NumberOfRays { get; init; } = 1;

    /// <summary>"Scatter Fraction", 0 to 1.</summary>
    public double Fraction { get; init; }

    /// <summary>Gaussian σ, dimensionless, 0 &lt; σ ≤ 5 ("the maximum allowed value of σ is 5.0").</summary>
    public double Sigma { get; init; }

    /// <summary>ABg: the reflected and transmitted profiles (from the project's ABg catalog).</summary>
    public NscAbgProfile? AbgReflect { get; init; }
    public NscAbgProfile? AbgTransmit { get; init; }

    /// <summary>ABg File: the parsed ABGF files with their profiles.</summary>
    public NscAbgSum? AbgFileReflect { get; init; }
    public NscAbgSum? AbgFileTransmit { get; init; }

    /// <summary>BSDF: the parsed tabular BSDF files.</summary>
    public NscBsdfTable? BsdfReflect { get; init; }
    public NscBsdfTable? BsdfTransmit { get; init; }

    /// <summary>BSDF "Angle": the rotation (degrees, counter-clockwise about the local z axis) of
    /// the surface in the system relative to the sample as measured.</summary>
    public double BsdfAngleDegrees { get; init; }

    /// <summary>"Thin Window Scattering": on a volume, scatter in reflection only when entering
    /// and in transmission only when leaving.</summary>
    public bool ThinWindow { get; init; }

    /// <summary>Every problem, one line each, prefixed by <paramref name="where"/>.</summary>
    public void Validate(string where, List<string> problems)
    {
        string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(Model)) { problems.Add($"{where}: unknown scatter model {(int)Model}."); return; }
        if (Model == NscScatterModel.None) return;
        if (Model == NscScatterModel.UserDefined)
        {
            problems.Add($"{where}: User Defined scattering (a scatter DLL) is not built; no DLLs are loaded. Use Lambertian, Gaussian, ABg, ABg File or BSDF.");
            return;
        }
        if (NumberOfRays < 0 || NumberOfRays > 300)
            problems.Add($"{where}: the number of scatter rays must lie in 0–300 (help, \"Fraction to Scatter and Number of Scatter Rays\"); it is {NumberOfRays}.");
        if (Model is NscScatterModel.Lambertian or NscScatterModel.Gaussian or NscScatterModel.Bsdf && !(Fraction >= 0 && Fraction <= 1))
            problems.Add($"{where}: the scatter fraction must lie in 0–1; it is {F(Fraction)}.");
        if (Model == NscScatterModel.Gaussian && !(Sigma > 0 && Sigma <= 5))
            problems.Add($"{where}: the Gaussian σ must lie in (0, 5] (help, \"Available Scatter Models\": \"the maximum allowed value of σ is 5.0\"); it is {F(Sigma)}.");
        if (Model == NscScatterModel.ABg && AbgReflect is null && AbgTransmit is null)
            problems.Add($"{where}: ABg scattering names no reflect or transmit profile.");
        if (Model == NscScatterModel.ABgFile && AbgFileReflect is null && AbgFileTransmit is null)
            problems.Add($"{where}: ABg File scattering names no reflect or transmit ABGF file.");
        if (Model == NscScatterModel.Bsdf && BsdfReflect is null && BsdfTransmit is null)
            problems.Add($"{where}: BSDF scattering names no reflect or transmit BSDF file.");
        if (!double.IsFinite(BsdfAngleDegrees)) problems.Add($"{where}: the BSDF angle must be finite.");
        AbgReflect?.Validate($"{where}, ABg reflect '{AbgReflect.Name}'", problems);
        AbgTransmit?.Validate($"{where}, ABg transmit '{AbgTransmit.Name}'", problems);
    }

    /// <summary>A plain description for notes.</summary>
    public string Describe()
    {
        string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
        string rays = $"{NumberOfRays} scatter ray{(NumberOfRays == 1 ? "" : "s")} when splitting";
        string thin = ThinWindow ? ", thin window" : "";
        return Model switch
        {
            NscScatterModel.None => "no scattering",
            NscScatterModel.Lambertian => $"Lambertian, fraction {F(Fraction)}, {rays}{thin}",
            NscScatterModel.Gaussian => $"Gaussian σ = {F(Sigma)}, fraction {F(Fraction)}, {rays}{thin}",
            NscScatterModel.ABg => $"ABg reflect {AbgReflect?.Name ?? "none"}, transmit {AbgTransmit?.Name ?? "none"}, {rays}{thin}",
            NscScatterModel.ABgFile => $"ABg File reflect {AbgFileReflect?.Name ?? "none"}, transmit {AbgFileTransmit?.Name ?? "none"}, {rays}{thin}",
            NscScatterModel.Bsdf => $"BSDF reflect {BsdfReflect?.Name ?? "none"}, transmit {BsdfTransmit?.Name ?? "none"}, angle {F(BsdfAngleDegrees)}°, fraction {F(Fraction)}, {rays}{thin}",
            _ => "User Defined (not built)",
        };
    }
}

/// <summary>The Scatter To method of an object (help, "Scatter To").</summary>
public enum NscScatterToMethod
{
    None = 0,
    /// <summary>"Scatter To List": scattered rays only meet the listed objects and the scatterer.</summary>
    List = 1,
    /// <summary>"Importance Sampling": scattered rays aimed at target spheres, energy corrected.</summary>
    ImportanceSampling = 2,
}

/// <summary>One importance-sampling target sphere (help, "Importance Sampling"): centred on the
/// origin of <see cref="Object"/> (1-based; 0 is "None", that ray scatters by the BSDF), radius
/// meters, and the largest solid angle (steradians) it may subtend at the scatter point for
/// importance sampling to be used.</summary>
public readonly record struct NscScatterTarget(int Object, double Radius, double Limit);

/// <summary>An object's Scatter To section (help, "Scatter To", "The Scatter To List",
/// "Importance Sampling").</summary>
public sealed record NscScatterTo
{
    public NscScatterToMethod Method { get; init; }

    /// <summary>The Scatter To list: 1-based object numbers.</summary>
    public IReadOnlyList<int> Objects { get; init; } = Array.Empty<int>();

    /// <summary>Up to 6 target spheres, ray 1 first ("Up to 6 independent target spheres may be
    /// defined for each object").</summary>
    public IReadOnlyList<NscScatterTarget> Targets { get; init; } = Array.Empty<NscScatterTarget>();

    public void Validate(string where, int objectCount, List<string> problems)
    {
        if (!Enum.IsDefined(Method)) { problems.Add($"{where}: unknown Scatter To method {(int)Method}."); return; }
        if (Method == NscScatterToMethod.List)
            foreach (int k in Objects)
                if (k < 1 || k > objectCount) problems.Add($"{where}: the Scatter To list names object {k}; objects are 1 to {objectCount}.");
        if (Method == NscScatterToMethod.ImportanceSampling)
        {
            if (Targets.Count > 6) problems.Add($"{where}: {Targets.Count} importance-sampling targets; at most 6 (help, \"Importance Sampling\").");
            for (int i = 0; i < Targets.Count; i++)
            {
                var t = Targets[i];
                if (t.Object == 0) continue;   // "None"
                if (t.Object < 1 || t.Object > objectCount) problems.Add($"{where}: importance-sampling target {i + 1} names object {t.Object}; objects are 1 to {objectCount}.");
                if (!(t.Radius > 0) || !double.IsFinite(t.Radius)) problems.Add($"{where}: importance-sampling target {i + 1}: the size (radius) must be positive.");
                if (!(t.Limit > 0) || !double.IsFinite(t.Limit)) problems.Add($"{where}: importance-sampling target {i + 1}: the limit must be a positive solid angle (steradians).");
            }
        }
    }
}
