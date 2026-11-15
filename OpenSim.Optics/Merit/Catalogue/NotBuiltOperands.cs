// OP-55B
using System.Text.RegularExpressions;

namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>An OpticStudio operand code this build does not register: what the help says it
/// computes, why it is not built, and the plan item it waits for (null when no item covers it).</summary>
/// <param name="Code">The code, or for a numbered family the help's pattern ("InGT", "PnGT").</param>
/// <param name="Category">The help's category.</param>
/// <param name="Description">What the help says the operand computes, in one sentence.</param>
/// <param name="Reason">Why it is not built.</param>
/// <param name="WaitsFor">The plan item it waits for ("OP-70"), or null.</param>
public sealed record NotBuiltOperand(string Code, string Category, string Description, string Reason, string? WaitsFor)
{
    /// <summary>"needs OP-70: the non-sequential engine is not built" or the reason alone.</summary>
    public string Why => WaitsFor is null ? Reason : $"needs {WaitsFor}: {Reason}";
}

/// <summary>
/// Every operand code of OpticStudio's summary table (help: Optimization Operands Summary Table,
/// 2025 R2, saved under plans/sources/optics/op-54-help/) that <see cref="MeritOperandRegistry.CreateDefault"/>
/// does not register, with the help's description (from the category pages saved under
/// op-54-help and plans/sources/optics/op-55b-help/) and the reason. Numbered families are kept
/// as the help writes them: the gradient-index InGT, InLT, InVA (n = 1 to 6, the help's six
/// points) and the obsolete PnGT, PnLT, PnVA (help: Obsolete Operands; n one digit). OP-55A's
/// not-built codes stay in its own file and join through <see cref="Imaging"/>.
/// </summary>
public static class NotBuiltOperands
{
    private const string Nsc = "the non-sequential ray-trace engine and its objects are not built";

    /// <summary>The category of OP-55A's entries, whose reasons come from
    /// <see cref="Imaging"/> without a description of their own.</summary>
    public const string ImagingCategory = "Imaging and ray operands (OP-55A)";

    /// <summary>OP-55A's not-built imaging and ray codes (code → reason), kept in its own file.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Imaging => ImagingOperands.NotBuilt;

    /// <summary>Every entry: OP-55B's in the summary table's category order, then OP-55A's
    /// (<see cref="ImagingCategory"/>, empty description).</summary>
    public static IReadOnlyList<NotBuiltOperand> All { get; } = Build()
        .Concat(Imaging.Select(kv => new NotBuiltOperand(kv.Key, ImagingCategory, "", kv.Value, null)))
        .ToArray();

    private static readonly Dictionary<string, NotBuiltOperand> ByCode =
        All.Where(e => !IsFamily(e.Code)).ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);

    private static bool IsFamily(string code) => code.Any(char.IsAsciiLetterLower);

    /// <summary>The entry for <paramref name="code"/> (ignoring case; I3GT finds InGT), or null.</summary>
    public static NotBuiltOperand? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        string c = code.Trim().ToUpperInvariant();
        if (ByCode.TryGetValue(c, out var e)) return e;
        var m = Regex.Match(c, "^I([1-6])(GT|LT|VA)$");
        if (m.Success) return All.First(x => x.Code == "In" + m.Groups[2].Value);
        m = Regex.Match(c, "^P([0-9])(GT|LT|VA)$");
        if (m.Success) return All.First(x => x.Code == "Pn" + m.Groups[2].Value);
        return null;
    }

    private static IReadOnlyList<NotBuiltOperand> Build()
    {
        var list = new List<NotBuiltOperand>();
        void Add(string code, string category, string description, string reason, string? waitsFor) =>
            list.Add(new NotBuiltOperand(code, category, description, reason, waitsFor));

        // ---- Constraints on lens data (help: Constraints on Lens Data) ----
        const string lens = "Constraints on lens data";
        const string stats = "OP-43's surface maps do not expose the statistics this operand returns (RMS, PV, extremes and their locations over its Samp grid), nor off-axis coordinates or the Base/Composite sag removal";
        Add("DCRV", lens, "Curvature data of a surface: RMS, PV, minimum or maximum curvature over the surface and where they occur, or the best-fit sphere and off-axis frame.", stats, null);
        Add("DSAG", lens, "Sag data of a surface: RMS, PV, minimum or maximum sag over the surface and where they occur, or the best-fit sphere and off-axis frame.", stats, null);
        Add("DSLP", lens, "Slope data of a surface: RMS, PV, minimum or maximum slope over the surface and where they occur, or the best-fit sphere and off-axis frame.", stats, null);
        Add("DPHS", lens, "Phase data in waves of a phase surface: RMS, PV, minimum or maximum phase over the surface, where they occur, or the constant, tilt and power removed.", "OP-43's Surface Phase analysis (its statistics over the surface and the fitted-term removal) is not built", "OP-43");
        Add("QSLP", lens, "Phase slope data of a phase surface: RMS, PV, minimum or maximum phase slope over the surface, where they occur, or the terms removed.", "OP-43's Phase Slope analysis (its statistics over the surface and the fitted-term removal) is not built", "OP-43");

        // ---- Constraints on lens properties (TSAG is on the category page, not the summary table) ----
        Add("TSAG", "Constraints on lens properties", "Sag of a surface measured from an arbitrary reference point along an arbitrary tilted direction.", "the sag along a tilted direction from an arbitrary point is not built", null);

        // ---- TrueFreeForm ----
        Add("GOPT", "Constraints on TrueFreeForm surface data", "Grid optimization constraints of a TrueFreeForm surface: minimum or maximum Z or Z increment, returned as a value or a bound.", "the TrueFreeForm surface is not built", "OP-25");

        // ---- Changing system data ----
        const string sys = "Changing system data";
        const string evaluator = "it changes the system data for the rows after it, and the merit evaluator (OP-54) evaluates every row on the system as entered; no plan item covers it yet";
        Add("IMSF", sys, "Makes another surface the image surface for the rows after it, optionally refocused by a paraxial focus solve on a dummy surface.", evaluator, null);
        Add("PRIM", sys, "Changes the primary wavelength to Wave for the rows after it.", evaluator, null);
        Add("SVIG", sys, "Sets the vignetting factors of the current configuration for the rows after it, at high, medium or low precision.", evaluator, null);
        Add("CVIG", sys, "Clears the vignetting factors for the rows after it in the current configuration.", evaluator, null);
        Add("FDMO", sys, "Temporarily modifies a field's coordinates and vignetting factors for the rows after it.", evaluator, null);
        Add("FDRE", sys, "Restores a field modified by FDMO.", evaluator, null);

        // ---- General math ----
        Add("EQUA", "General math operands", "Holds the rows Op#1 to Op#2 to one value within the target: the sum of each row's departure from their average where it exceeds the target.",
            "the help gives a value that is zero when the rows agree but does not say how that value relates to the target, so its residual cannot be read off the page (OP-54's reading)", null);

        // ---- Gaussian beam (skew) ----
        const string beam = "Gaussian beam data";
        const string skew = "OP-48 built the paraxial Gaussian beam only; the skew Gaussian beam about a real chief ray is not built";
        Add("GBSD", beam, "Skew Gaussian beam divergence in the space after a surface, the beam aligned along a field's chief ray.", skew, "OP-48");
        Add("GBSP", beam, "Skew Gaussian beam position, the distance from the waist to the surface.", skew, "OP-48");
        Add("GBSR", beam, "Skew Gaussian beam radius of curvature in the space after a surface.", skew, "OP-48");
        Add("GBSS", beam, "Skew Gaussian beam size in the space after a surface.", skew, "OP-48");
        Add("GBSW", beam, "Skew Gaussian beam waist in the space after a surface.", skew, "OP-48");

        // ---- Gradient index ----
        const string grin = "Gradient index control operands";
        const string noGrin = "gradient-index surfaces are not built";
        Add("DLTN", grin, "Difference between the greatest and least index on axis of a gradient-index surface at a wavelength.", noGrin, "OP-28");
        Add("GRMN", grin, "Lower bound on the index of a gradient-index surface, checked at six points.", noGrin, "OP-28");
        Add("GRMX", grin, "Upper bound on the index of a gradient-index surface, checked at six points.", noGrin, "OP-28");
        Add("InGT", grin, "Lower bound on the index of a gradient-index surface at point n (1 front vertex, 2 front +y top, 3 front +x side, 4 rear vertex, 5 rear +y top, 6 rear +x side): I1GT to I6GT.", noGrin, "OP-28");
        Add("InLT", grin, "Upper bound on the index of a gradient-index surface at point n (see InGT): I1LT to I6LT.", noGrin, "OP-28");
        Add("InVA", grin, "Index of a gradient-index surface at point n (see InGT): I1VA to I6VA.", noGrin, "OP-28");
        Add("LPTD", grin, "Keeps the slope of the axial gradient-index profile from changing sign inside the component.", noGrin, "OP-28");

        // ---- Fiber coupling and physical optics ----
        Add("FICL", "Fiber coupling operands", "Single-mode fiber coupling efficiency (fast or Huygens algorithm, power or amplitude coupling).", "fiber coupling is not built", "OP-50");
        Add("FICP", "Fiber coupling operands", "Fiber coupling computed by Physical Optics Propagation with its saved settings.", "physical optics propagation and fiber coupling are not built", "OP-50");
        Add("POPD", "Physical Optics Propagation (POP) results", "Physical Optics Propagation data: fiber coupling, power, peak irradiance, pilot beam, centroid, beam widths, M², moments, enclosed power.", "physical optics propagation is not built", "OP-49");
        Add("POPI", "Physical Optics Propagation (POP) results", "Physical Optics Propagation irradiance, field or phase of one pixel of the beam.", "physical optics propagation is not built", "OP-49");

        // ---- User defined ----
        Add("UDOC", "User defined operands", "A value computed by an externally compiled program written with the ZOS-API.",
            "OP-98's scripting surface drives this program from outside but defines no operand that calls back into user code; user operands wait for the plug-in assemblies", "OP-100");

        // ---- Non-sequential object data ----
        const string nso = "Constraints on non-sequential object data";
        Add("FREZ", nso, "Shape data of a Freeform Z object (z and y extremes and increments, volume, monotonic deviation, slopes), as a value or a bound.", Nsc, "OP-72");
        foreach (var (code, what) in new[] { ("NPGT", "parameter greater than"), ("NPLT", "parameter less than"), ("NPVA", "parameter value") })
            Add(code, nso, $"Non-sequential object {what}.", Nsc, "OP-71");
        foreach (char axis in "XYZ")
        {
            Add($"NP{axis}G", nso, $"Non-sequential object position {char.ToLowerInvariant(axis)} greater than.", Nsc, "OP-71");
            Add($"NP{axis}L", nso, $"Non-sequential object position {char.ToLowerInvariant(axis)} less than.", Nsc, "OP-71");
            Add($"NP{axis}V", nso, $"Non-sequential object position {char.ToLowerInvariant(axis)} value.", Nsc, "OP-71");
        }
        Add("NSRM", nso, "Non-sequential object rotation matrix component.", Nsc, "OP-71");
        foreach (char axis in "XYZ")
        {
            Add($"NT{axis}G", nso, $"Non-sequential object tilt about {char.ToLowerInvariant(axis)} greater than.", Nsc, "OP-71");
            Add($"NT{axis}L", nso, $"Non-sequential object tilt about {char.ToLowerInvariant(axis)} less than.", Nsc, "OP-71");
            Add($"NT{axis}V", nso, $"Non-sequential object tilt about {char.ToLowerInvariant(axis)} value.", Nsc, "OP-71");
        }

        // ---- Non-sequential ray tracing and detectors ----
        const string nsr = "Non-sequential ray tracing and detector operands";
        Add("NPAF", nsr, "Names a PAF file for the NSTR trace to save.", Nsc, "OP-70");
        Add("NSDC", nsr, "Non-sequential coherent detector data (real, imaginary, amplitude or power).", "detectors are not built", "OP-79");
        Add("NSDD", nsr, "Non-sequential incoherent detector data: pixel, total, extreme, centroid, RMS or geometric MTF values.", "detectors are not built", "OP-79");
        Add("NSDE", nsr, "Non-sequential Detector Color object data: power, lumens, chromaticity, tristimulus, CRI, CCT.", "detectors are not built", "OP-79");
        Add("NSDP", nsr, "Non-sequential Detector Polar object data.", "detectors are not built", "OP-79");
        Add("NSLT", nsr, "Non-sequential LightningTrace: traces mesh rays as the LightningTrace analysis defines them.", "the noise-free fast illumination solve is not built", "OP-85");
        Add("NSRA", nsr, "Non-sequential single ray trace from a source: data of one segment of the ray.", Nsc, "OP-70");
        Add("NSRW", nsr, "Non-sequential roadway lighting data, after an NSTW trace.", "roadway lighting is not built", "OP-89");
        Add("NSST", nsr, "Traces one sequential ray into a non-sequential surface and returns data about it there.", "mixed mode (non-sequential surfaces in a sequential system) is not built", "OP-86");
        Add("NSTR", nsr, "Non-sequential trace from a source, filling the detectors.", Nsc, "OP-70");
        Add("NSTW", nsr, "Non-sequential roadway lighting ray trace for the NSC Roadway Merit Function.", "roadway lighting is not built", "OP-89");
        Add("NSRD", nsr, "Names a ZRD ray database file for the NSTR trace to save.", "the ray database is not built", "OP-83");

        // ---- Optically fabricated holograms ----
        Add("CMFV", "Constraints on construction optics for optically fabricated holograms", "Merit function value, or one row's value, of a construction system of an optically fabricated hologram.",
            "the Optically Fabricated Hologram surface is not built (OP-26 built Hologram 1 and 2 and the Toroidal Hologram)", "OP-26");

        // ---- Coatings and polarization ----
        const string coat = "Constraints on optical coatings, polarization ray trace data";
        const string perSurface = "this version's saved lens holds no per-surface, per-layer coating multipliers or index and extinction offsets (the coating analyses take them as settings only), as for the TCMU, TCIO and TCEO tolerances";
        Add("CMGT", coat, "Lower bound on the coating multiplier of layer Layr of surface Surf.", perSurface, "OP-15");
        Add("CMLT", coat, "Upper bound on the coating multiplier of layer Layr of surface Surf.", perSurface, "OP-15");
        Add("CMVA", coat, "Coating multiplier of layer Layr of surface Surf.", perSurface, "OP-15");
        Add("CEGT", coat, "Lower bound on the coating extinction offset of layer Layr of surface Surf.", perSurface, "OP-15");
        Add("CELT", coat, "Upper bound on the coating extinction offset of layer Layr of surface Surf.", perSurface, "OP-15");
        Add("CEVA", coat, "Coating extinction offset of layer Layr of surface Surf.", perSurface, "OP-15");
        Add("CIGT", coat, "Lower bound on the coating index offset of layer Layr of surface Surf.", perSurface, "OP-15");
        Add("CILT", coat, "Upper bound on the coating index offset of layer Layr of surface Surf.", perSurface, "OP-15");
        Add("CIVA", coat, "Coating index offset of layer Layr of surface Surf.", perSurface, "OP-15");
        Add("CODA", coat, "Coating data of a polarized ray at a surface: intensity reflectance, transmittance, absorption, amplitude coefficients, relative transmitted intensity, output E field, phases, polarization ellipse.",
            "OP-51's polarization ray trace holds the per-surface response and field, but CODA's twenty data codes (their reference frames, the relative intensities 0 and 8, the ellipse) have not been mapped onto it and checked", "OP-51");

        // ---- Obsolete (help: Obsolete Operands) ----
        Add("PnGT", "Obsolete operands", "Obsolete: parameter n greater than.", "obsolete in OpticStudio; use PMGT (parameter Param of surface Surf)", null);
        Add("PnLT", "Obsolete operands", "Obsolete: parameter n less than.", "obsolete in OpticStudio; use PMLT", null);
        Add("PnVA", "Obsolete operands", "Obsolete: parameter n value.", "obsolete in OpticStudio; use PMVA", null);
        return list;
    }
}
