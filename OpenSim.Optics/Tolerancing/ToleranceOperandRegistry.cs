namespace OpenSim.Optics.Tolerancing;

/// <summary>
/// Every tolerance and control operand of OpticStudio's Tolerance Data Editor, read from the
/// OpticStudio 2025 R2 help pages "Tolerance Operands", "Tolerance Operands Summary Table",
/// "Tolerance Control Operands Summary Table" and each operand's own page (saved under
/// plans/sources/optics/op-63-help/). Each definition names its page. The integer columns follow
/// the summary tables; where a page lists inputs the tables leave out (ISOA to ISOD: "Units" and
/// "Statistics"), Int2 is Units and Int3 Statistics, in the order the ISOB and ISOC pages list
/// them (the ISOD page lists Statistics before Units; this program's reading,
/// docs/Optics-Conventions.md §36).
/// </summary>
public static class ToleranceOperandRegistry
{
    /// <summary>The unused row (TOFF), the type of a new row.</summary>
    public const string Off = "TOFF";

    private const string NscReason = "non-sequential components are not built in this version (OP-70 on).";
    private const string CoatingReason = "the per-surface coating multipliers and index and extinction offsets (OpticStudio's Surface Properties › Coating tab) are not held by this version's saved lens.";
    private const string CompositeReason = "it adds a Zernike Standard Sag surface as a Composite add-on, and neither the Zernike Standard Sag surface (OP-25) nor composite surfaces (OP-32) are built in this version.";

    private static readonly ToleranceOperandDefinition[] List = Build();
    private static readonly Dictionary<string, ToleranceOperandDefinition> ByCode =
        List.ToDictionary(d => d.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every operand, the editor's order: TOFF first, then the help's summary order.</summary>
    public static IReadOnlyList<ToleranceOperandDefinition> All => List;

    /// <summary>The operand of <paramref name="code"/> (ignoring case and surrounding blanks), or null.</summary>
    public static ToleranceOperandDefinition? Find(string? code) =>
        code is not null && ByCode.TryGetValue(code.Trim(), out var d) ? d : null;

    /// <summary>The operand of <paramref name="code"/>; throws <see cref="ArgumentException"/> naming it when unknown.</summary>
    public static ToleranceOperandDefinition Get(string? code) => Find(code)
        ?? throw new ArgumentException($"'{code}' is not a tolerance operand this version knows.");

    /// <summary>The operands whose code or name contains <paramref name="text"/> (ignoring case).</summary>
    public static IReadOnlyList<ToleranceOperandDefinition> Search(string text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return Array.Empty<ToleranceOperandDefinition>();
        return List.Where(d => d.Code.Contains(t, StringComparison.OrdinalIgnoreCase)
                               || d.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                               || d.Description.Contains(t, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static ToleranceOperandDefinition[] Build()
    {
        var list = new List<ToleranceOperandDefinition>();
        void Add(string code, string name, ToleranceCategory category, string source, string description,
            string? int1 = null, string? int2 = null, string? int3 = null, string? unit = null,
            bool minIsNegativeMax = false, bool nonNegative = false, bool element = false, bool radial = false, string? notApplied = null) =>
            list.Add(new ToleranceOperandDefinition
            {
                Code = code, Name = name, Category = category, Source = "OpticStudio 2025 R2 help, \"" + source + "\"",
                Description = description, Int1 = int1, Int2 = int2, Int3 = int3, Unit = unit,
                MinIsNegativeMax = minIsNegativeMax, NonNegative = nonNegative, IsElement = element, IsRadial = radial,
                NotAppliedReason = notApplied,
            });
        const ToleranceCategory S = ToleranceCategory.Surface, E = ToleranceCategory.Element,
            U = ToleranceCategory.UserDefined, N = ToleranceCategory.NonSequential, C = ToleranceCategory.Control;

        Add(Off, "Tolerance off", C, "TOFF: Tolerance Off (can be used for comments)",
            "A place holder that has no effect on the tolerance analysis; may hold a comment.");

        // ---- Surface tolerances.
        const string isoUnits = "Units: 0 nanometers, 1 fringes at TWAV, 2 micrometers";
        const string isoStats = "Statistics: 0 always Max, 1 Gaussian";
        Add("ISOA", "Tolerance on P-V power", S, "ISOA",
            "Peak-to-valley power added to the surface (ISO 10110-5 drawing note 3/A(B/C), A), as a Zernike Standard Sag composite add-on.",
            "Surf", isoUnits, unit: "by Units", minIsNegativeMax: true, notApplied: CompositeReason);
        Add("ISOB", "Tolerance on total P-V irregularity", S, "ISOB",
            "Peak-to-valley irregularity added to the surface (ISO 10110-5, B), Zernike Standard terms 5 to 37 as a composite add-on.",
            "Surf", isoUnits, isoStats, "by Units", minIsNegativeMax: true, notApplied: CompositeReason);
        Add("ISOC", "Tolerance on P-V rotationally symmetric irregularity", S, "ISOC",
            "Peak-to-valley rotationally symmetric irregularity (ISO 10110-5, C), Zernike Standard terms 11, 22, 37 and 56 as a composite add-on.",
            "Surf", isoUnits, isoStats, "by Units", minIsNegativeMax: true, notApplied: CompositeReason);
        Add("ISOD", "Tolerance on RMS total irregularity", S, "ISOD",
            "RMS irregularity added to the surface (ISO 10110-5, D), Zernike Standard terms 5 to 37 as a composite add-on.",
            "Surf", isoUnits, isoStats, "by Units", minIsNegativeMax: true, notApplied: CompositeReason);
        Add("TRAD", "Tolerance on radius", S, "TRAD: Tolerance on Radius",
            "The radius of curvature: Min and Max in lens units (Code 0) or in percent of the nominal radius (Code 1).",
            "Surf", "Code: 0 lens units, 1 percent", unit: "lens units or percent");
        Add("TCUR", "Tolerance on curvature", S, "TCUR: Tolerance on Curvature",
            "The curvature: Min and Max in inverse lens units.", "Surf", unit: "1/lens units");
        Add("TFRN", "Tolerance on fringes", S, "TFRN: Tolerance on Fringes",
            "The curvature, as fringes of a double-pass Newton's-rings test at TWAV over the clear semi-diameter: ΔZ = λN/2, ΔC = 2ΔZ/r².",
            "Surf", unit: "fringes at TWAV");
        Add("TTHI", "Tolerance on thickness", S, "TTHI: Tolerance on Thickness",
            "The thickness after Surf; the change is taken out of the adjustment surface's thickness unless Adjust equals Surf.",
            "Surf", "Adjust", unit: "lens units");
        Add("TCON", "Tolerance on conic", S, "TCON: Tolerance on Conic", "The conic constant (dimensionless).", "Surf", unit: "dimensionless");
        Add("TSDI", "Tolerance on semi-diameter", S, "TSDI: Tolerance on Clear Semi-Diameter or Semi-Diameter",
            "The clear semi-diameter or semi-diameter.", "Surf", unit: "lens units");
        Add("TSDR", "Tolerance on surface radial decenter", S, "TSDX, TSDY, TSDR: Tolerance on Surface Decenters",
            "Decenter of a Standard surface in a random radial direction, by converting it to the Irregular type.",
            "Surf", unit: "lens units", nonNegative: true, radial: true);
        Add("TSDX", "Tolerance on surface x-decenter", S, "TSDX, TSDY, TSDR: Tolerance on Surface Decenters",
            "Decenter of a Standard surface in x, by converting it to the Irregular type.", "Surf", unit: "lens units");
        Add("TSDY", "Tolerance on surface y-decenter", S, "TSDX, TSDY, TSDR: Tolerance on Surface Decenters",
            "Decenter of a Standard surface in y, by converting it to the Irregular type.", "Surf", unit: "lens units");
        Add("TSTX", "Tolerance on surface tilt about x", S, "TSTX, TSTY: Tolerance on Surface Tilts",
            "Tilt of a Standard surface about its local x axis, by converting it to the Irregular type.", "Surf", unit: "degrees");
        Add("TSTY", "Tolerance on surface tilt about y", S, "TSTX, TSTY: Tolerance on Surface Tilts",
            "Tilt of a Standard surface about its local y axis, by converting it to the Irregular type.", "Surf", unit: "degrees");
        Add("TIRX", "Tolerance on surface TIR in x", S, "TIRX, TIRY: Tolerance on Surface TIR",
            "Wedge of a Standard surface as total indicator runout along x (twice the sag change at the semi-diameter); tilt about y = atan(TIR/2 / S).",
            "Surf", unit: "lens units");
        Add("TIRY", "Tolerance on surface TIR in y", S, "TIRX, TIRY: Tolerance on Surface TIR",
            "Wedge of a Standard surface as total indicator runout along y; tilt about x = atan(TIR/2 / S).", "Surf", unit: "lens units");
        Add("TIRR", "Tolerance on surface irregularity", S, "TIRR: Tolerance on Surface Irregularity",
            "Irregularity of a Standard surface, half spherical and half astigmatic, in double-pass fringes at TWAV, by converting it to the Irregular type.",
            "Surf", unit: "fringes at TWAV");
        Add("TEXI", "Tolerance on irregularity, Fringe Zernike", S, "TEXI: Tolerance on Surface Irregularity Using the Fringe Zernike Model",
            "Irregularity as Fringe Zernike terms Min term to Max term on a Zernike Fringe Sag surface; each coefficient c = (λ/2)·f/√n.",
            "Surf", "Max term (3–37)", "Min term (2–Max)", "fringes at TWAV", minIsNegativeMax: true,
            notApplied: "it converts the surface to a Zernike Fringe Sag surface, which is not built in this version (OP-25).");
        Add("TEZI", "Tolerance on irregularity, Standard Zernike", S, "TEZI: Tolerance on Surface Irregularity Using the Standard Zernike Model",
            "Irregularity as Standard Zernike terms Min term to Max term (Zernike Standard Sag, or the Toroidal surface's own terms); Max is the exact RMS in lens units.",
            "Surf", "Max term (3–231)", "Min term (2–Max)", "lens units (RMS)", minIsNegativeMax: true);
        Add("TPAI", "Tolerance on inverse of parameter", S, "TPAI: Tolerance on the Inverse of Parameter Data",
            "A surface parameter, perturbed on its inverse: P′ = 1/(1/P + Δ).", "Surf", "Parameter", unit: "inverse of the parameter's unit");
        Add("TPAR", "Tolerance on parameter", S, "TPAR: Tolerance on Parameter Data",
            "A surface parameter (aspheric coefficients and more).", "Surf", "Parameter", unit: "the parameter's unit");
        Add("TIND", "Tolerance on index", S, "TIND: Tolerance on Index",
            "The index of refraction of the medium after Surf (the d-light index of a model or catalog glass).", "Surf", unit: "index");
        Add("TABB", "Tolerance on Abbe", S, "TABB: Tolerance on Abbe",
            "The Abbe number Vd of the medium after Surf (model or catalog glass; ignored otherwise).", "Surf", unit: "Abbe number");
        Add("TCMU", "Tolerance on coating multiplier", S, "TCMU: Tolerance on Coating Multiplier",
            "The coating multiplier of a layer: a tolerance on the layer's thickness.", "Surf", "Layer", notApplied: CoatingReason);
        Add("TCIO", "Tolerance on coating index offset", S, "TCIO: Tolerance on Coating Index Offset",
            "The real index offset of a coating layer.", "Surf", "Layer", notApplied: CoatingReason);
        Add("TCEO", "Tolerance on coating extinction offset", S, "TCEO: Tolerance on Coating Extinction Offset",
            "The extinction offset of a coating layer.", "Surf", "Layer", notApplied: CoatingReason);

        // ---- Element tolerances.
        const string decenters = "TEDX, TEDY, TEDR: Tolerance on Element Decenters";
        const string tilts = "TETX, TETY, TETZ: Tolerance on Element Tilts";
        const string rolls = "TARX, TARY, TARR: Tolerance on Roll Angles";
        const string rollTir = "TRLX, TRLY, TRLR: Tolerance on Roll TIR";
        Add("TEDR", "Tolerance on element radial decenter", E, decenters, "Decenter of the group Surf1–Surf2 in a random radial direction.",
            "First surf", "Last surf", unit: "lens units", nonNegative: true, element: true, radial: true);
        Add("TEDX", "Tolerance on element x-decenter", E, decenters, "Decenter of the group Surf1–Surf2 in x.", "First surf", "Last surf", unit: "lens units", element: true);
        Add("TEDY", "Tolerance on element y-decenter", E, decenters, "Decenter of the group Surf1–Surf2 in y.", "First surf", "Last surf", unit: "lens units", element: true);
        Add("TETX", "Tolerance on element tilt about x", E, tilts, "Tilt of the group Surf1–Surf2 about x, about the pivot (MPVT; the front vertex by default).",
            "First surf", "Last surf", unit: "degrees", element: true);
        Add("TETY", "Tolerance on element tilt about y", E, tilts, "Tilt of the group Surf1–Surf2 about y, about the pivot.", "First surf", "Last surf", unit: "degrees", element: true);
        Add("TETZ", "Tolerance on element tilt about z", E, tilts, "Tilt of the group Surf1–Surf2 about z, about the pivot.", "First surf", "Last surf", unit: "degrees", element: true);
        Add("TARR", "Tolerance on radial roll angle", E, rolls, "Roll of the group about the center of curvature of RollSurf in a random direction (+y for sensitivity).",
            "First surf", "Last surf", "Roll surf", "degrees", nonNegative: true, element: true, radial: true);
        Add("TARX", "Tolerance on roll angle in x", E, rolls, "Roll of the group about the center of curvature of RollSurf along x.",
            "First surf", "Last surf", "Roll surf", "degrees", element: true);
        Add("TARY", "Tolerance on roll angle in y", E, rolls, "Roll of the group about the center of curvature of RollSurf along y.",
            "First surf", "Last surf", "Roll surf", "degrees", element: true);
        Add("TRLR", "Tolerance on radial roll (TIR)", E, rollTir, "Roll about the center of curvature of RollSurf given as the TIR of the other (read) surface, in a random direction (+y for sensitivity).",
            "First surf", "Last surf", "Roll surf", "lens units", nonNegative: true, element: true, radial: true);
        Add("TRLX", "Tolerance on roll (TIR) in x", E, rollTir, "Roll along x given as the read surface's TIR z(+x) − z(−x) at its clear semi-diameter.",
            "First surf", "Last surf", "Roll surf", "lens units", element: true);
        Add("TRLY", "Tolerance on roll (TIR) in y", E, rollTir, "Roll along y given as the read surface's TIR z(+y) − z(−y) at its clear semi-diameter.",
            "First surf", "Last surf", "Roll surf", "lens units", element: true);

        // ---- User-defined tolerances.
        const string user = "TUDX, TUDY, TUTX, TUTY, TUTZ: Tolerance on User Defined Tilts & Decenters";
        Add("TUDX", "Tolerance on user-defined decenter x", U, user, "Decenter x of the existing coordinate break Surf.", "Surf", unit: "lens units");
        Add("TUDY", "Tolerance on user-defined decenter y", U, user, "Decenter y of the existing coordinate break Surf.", "Surf", unit: "lens units");
        Add("TUTX", "Tolerance on user-defined tilt x", U, user, "Tilt about x of the existing coordinate break Surf.", "Surf", unit: "degrees");
        Add("TUTY", "Tolerance on user-defined tilt y", U, user, "Tilt about y of the existing coordinate break Surf.", "Surf", unit: "degrees");
        Add("TUTZ", "Tolerance on user-defined tilt z", U, user, "Tilt about z of the existing coordinate break Surf.", "Surf", unit: "degrees");

        // ---- Non-sequential components.
        const string nsc = "TNPS, TNPA, TNMA: Tolerances on Non-sequential Data";
        Add("TNMA", "Tolerance on NSC material", N, nsc, "Nd (Data 1) or Vd (Data 2) of an NSC object's material.", "Surf", "Object", "Data", notApplied: NscReason);
        Add("TNPS", "Tolerance on NSC position", N, nsc, "Position (1–3) or tilt (4–6) of an NSC object.", "Surf", "Object", "Data", notApplied: NscReason);
        Add("TNPA", "Tolerance on NSC parameter", N, nsc, "A parameter of an NSC object.", "Surf", "Object", "Parameter", notApplied: NscReason);

        // ---- Multi-configuration.
        Add("TMCO", "Tolerance on multi-configuration data", ToleranceCategory.MultiConfiguration, "TMCO: Tolerance on Multi-Configuration Data",
            "A numerical value of the Multi-Configuration Editor.", "Operand", "Config", unit: "the operand's unit");

        // ---- Control operands.
        const string controls = "Tolerance Control Operands Summary Table";
        Add("CEDV", "Extra data compensator (deprecated)", C, controls, "Sets an extra data value as a compensator; deprecated and replaced by CPAR.", "Surf", "Extra data", unit: "the value's unit");
        Add("CMCO", "Multi-configuration compensator", C, "CMCO: Define Multi-Configuration Operand Compensator",
            "Sets a multi-configuration value as a compensator; Min and Max bound its change.", "Operand", "Config", unit: "the operand's unit");
        Add("CNPA", "NSC parameter compensator", C, controls, "Sets a non-sequential parameter as a compensator.", "Object", "Parameter", "Surf", notApplied: NscReason);
        Add("CNPS", "NSC position compensator", C, controls, "Sets a non-sequential position as a compensator.", "Object", "Code", "Surf", notApplied: NscReason);
        Add("COMM", "Comment", C, controls, "Prints the comment (or a blank line) in the tolerance report.");
        Add("COMP", "Compensator", C, "COMP: Define Compensator",
            "Sets a thickness (Code 0), curvature (1) or conic (2) as a compensator; Min and Max bound its change.", "Surf", "Code", unit: "the compensator's unit");
        Add("CPAR", "Parameter compensator", C, "CPAR: Define Parameter Compensator",
            "Sets a surface parameter as a compensator; Min and Max bound its change.", "Surf", "Parameter", unit: "the parameter's unit");
        Add("MPVT", "Mechanical pivot point", C, "MPVT: Define Mechanical Pivot Point",
            "The pivot of the element tilts and decenters that follow: 0 front vertex, 1 rear vertex, 2 front mechanical edge, 3 rear mechanical edge, 4 geometric center.", "Pivot at");
        Add("SAVE", "Save the lens", C, "SAVE: Save Sensitivity Analysis Lenses",
            "Saves the lenses used for the row before (file number 1–9999; 0 saves none).", "File number");
        Add("SEED", "Seed", C, "SEED: Seed the Random Number Generator",
            "Seeds the Monte Carlo random numbers of the rows that follow; 0 is a random seed.", "Seed");
        Add("STAT", "Statistics", C, "STAT: Define Statistics",
            "The Monte Carlo distribution of the rows that follow: 0 normal, 1 uniform, 2 parabolic, 3 user defined; Int2 the standard deviations to the extremes (normal, at most 10).",
            "Type", "Std devs");
        Add("TWAV", "Test wavelength", C, "TWAV: Test Wavelength",
            "The test wavelength in micrometers (in the Min column) of the fringe operands that follow.", unit: "micrometers (Min)");
        return list.ToArray();
    }
}
