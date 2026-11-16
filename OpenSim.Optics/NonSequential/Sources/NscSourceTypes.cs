using System.Globalization;
using OpenSim.Optics.NonSequential.Editing;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// The source types (plan item OP-77): their descriptors for the Non-Sequential Component
/// Editor's catalogue (<see cref="NscObjectTypeDescriptor"/>, joined in
/// <see cref="NscObjectCatalogue.Default"/>), and the saved-row reader that turns a type
/// name and its parameters (lens units) into an <see cref="NscSource"/>.
/// <para>
/// Every descriptor lists the parameters in OpticStudio's order, 1–5 the common ones; Source
/// Radial's variable-length tail of intensities (as many as its parameter 10 says) is shown as
/// <see cref="RadialIntensityColumns"/> columns "Intensity 1" ... each defaulting to 1, and saved
/// intensities past them are kept. Sources are surfaces (not volumes) with one face, face 0.
/// <see cref="NscObjectTypeDescriptor.ZplCode"/> is the type's code in ZPL's
/// SETNSCPROPERTY / NPRO type table (help, "SETNSCPROPERTY keywords"). A type not built carries
/// <see cref="NscObjectTypeDescriptor.NotBuiltReason"/> naming the item that builds it.
/// </para>
/// <para>
/// Defaults: parameters 1–5 take the values the help's Sources screenshot shows on a source row
/// (100 layout rays, 1,000,000 analysis rays, power 1, wavenumber 0, colour 0; help, "Sources");
/// Source Diode's take its parameter screenshots (divergences 0, super-Gaussian factors 0.01,
/// widths 0; help, "Source Diode"). The help lists no other defaults; the others here are this
/// build's own, chosen to give a valid source.
/// </para>
/// </summary>
public static class NscSourceTypes
{
    private static NscParameterDescriptor P(string name, double value = 0, bool length = false) =>
        new(name, value, length ? NscParameterKind.Length : NscParameterKind.Number);

    private static readonly NscParameterDescriptor[] Common =
    {
        P("# Layout Rays", 100), P("# Analysis Rays", 1_000_000), P("Power", 1), P("Wavenumber", 0), P("Color #", 0),
    };

    private static IReadOnlyList<NscParameterDescriptor> With(params NscParameterDescriptor[] rest) => Common.Concat(rest).ToArray();

    private static readonly NscParameterDescriptor[] EllipseParameters =
    {
        P("X Half Width", 1, true), P("Y Half Width", 1, true), P("Source Distance", 0, true), P("Cosine Exponent", 0),
        P("Gaussian Gx", 0), P("Gaussian Gy", 0), P("Source X", 0, true), P("Source Y", 0, true),
        P("Min X Half Width", 0, true), P("Min Y Half Width", 0, true),
    };

    private static readonly NscParameterDescriptor[] VolumeParameters = { P("X Half Width", 1, true), P("Y Half Width", 1, true), P("Z Half Width", 1, true) };

    /// <summary>Source Radial's fixed parameters (1–10) before its intensities.</summary>
    public const int RadialFixedParameters = 10;

    /// <summary>The intensity columns Source Radial's descriptor shows (its default of 5 points
    /// needs 5; 22 fills the editor's 32 parameter columns). Saved intensities past them are kept.</summary>
    public const int RadialIntensityColumns = 22;

    private static NscObjectTypeDescriptor Built(string name, string code, IReadOnlyList<NscParameterDescriptor> parameters, string summary) => new()
    {
        TypeName = name, Category = NscObjectCategory.Source, Parameters = parameters,
        Faces = new Dictionary<int, string> { [0] = "Source" }, IsVolume = false, ZplCode = code, Summary = summary,
        Source = $"OpticStudio 2025 R2 help, \"Summary of NSC Sources\" and \"{name}\"", PlanItem = "OP-77",
        SavedGeometry = (p, unit) => Create(name, p, unit),
    };

    private static NscObjectTypeDescriptor Refused(string name, string code, IReadOnlyList<NscParameterDescriptor> parameters, string summary, string planItem, string reason) => new()
    {
        TypeName = name, Category = NscObjectCategory.Source, Parameters = parameters,
        Faces = new Dictionary<int, string> { [0] = "Source" }, IsVolume = false, ZplCode = code.Length == 0 ? null : code, Summary = summary,
        Source = "OpticStudio 2025 R2 help, \"Summary of NSC Sources\"", PlanItem = planItem, NotBuiltReason = reason,
    };

    /// <summary>Every source type of OpticStudio's list (help, "Summary of NSC Sources"), built
    /// or refused.</summary>
    public static IReadOnlyList<NscObjectTypeDescriptor> Descriptors { get; } = new[]
    {
        Built("Source Point", "NSC_SRCP", With(P("Cone Angle", 0)), "A point radiating into a cone of 0–180°."),
        Built("Source Ray", "NSC_SRAY", With(P("X Cosine", 0), P("Y Cosine", 0), P("Z Cosine", 1), P("Random Seed", 0)), "A point emitting along given direction cosines."),
        Built("Source Ellipse", "NSC_SRCE", With(EllipseParameters), "A flat ellipse (optionally annular): point, cosine or Gaussian directions."),
        Built("Source Rectangle", "NSC_SRCR", With(EllipseParameters), "A flat rectangle with Source Ellipse's parameters."),
        Built("Source Gaussian", "NSC_SGAU", With(P("Beam Size", 1, true), P("Position", 0, true)), "A Gaussian beam from an apparent point."),
        Built("Source Diode", "NSC_SDIO", With(
            P("Astigmatism", 0, true), P("X-Divergence", 0), P("X-SuperGauss", 0.01), P("Y-Divergence", 0), P("Y-SuperGauss", 0.01),
            P("Number X", 1), P("Number Y", 1), P("Delta X", 0, true), P("Delta Y", 0, true),
            P("X-Width", 0, true), P("X-Sigma", 0, true), P("X-Width Hx", 0.01), P("Y-Width", 0, true), P("Y-Sigma", 0, true), P("Y-Width Hy", 0.01)),
            "A diode or diode array with super-Gaussian far field."),
        Built("Source Two Angle", "NSC_SR2A", With(P("X Half Width", 1, true), P("Y Half Width", 1, true), P("X Half Angle", 10), P("Y Half Angle", 10),
            P("Spatial Shape", 0), P("Angular Shape", 0), P("Uniform Angle", 0)), "A rectangle or ellipse emitting into a cone with distinct X and Y angles."),
        Built("Source Radial", "NSC_SRAD", With(new[] { P("X Half Width", 1, true), P("Y Half Width", 1, true), P("Min Angle", 0), P("Max Angle", 90), P("Number of Points", 5) }
                .Concat(Enumerable.Range(1, RadialIntensityColumns).Select(i => P($"Intensity {i}", 1))).ToArray()),
            "An axisymmetric emitter from intensity-versus-angle data."),
        Built("Source Filament", "NSC_SHLX", With(P("Length", 1, true), P("Radius", 0.5, true), P("Turns", 5)), "A helical filament."),
        Built("Source Tube", "NSC_STUB", With(P("Length", 1, true), P("Radius", 0.5, true)), "The wall of a cylindrical tube."),
        Built("Source Volume Cylinder", "NSC_VSRC", With(VolumeParameters), "A uniform elliptical-cylinder volume."),
        Built("Source Volume Ellipse", "NSC_VSRE", With(VolumeParameters), "A uniform ellipsoidal volume."),
        Built("Source Volume Rectangle", "NSC_VSRR", With(VolumeParameters), "A uniform box volume."),
        Refused("Source Object", "NSC_SOBJ", With(P("Parent Object #", 0), P("Chord Tolerance", 0, true), P("Cosine Factor", 0)),
            "Another object's surface as the emitter.", "OP-72",
            "Source Object is not built: it needs an area-uniform sampler over its parent's faces, which OP-70's Standard Lens (and the object library, plan item OP-72) does not provide."),
        Refused("Source Imported", "NSC_SIMP", Common, "An imported CAD shape as the emitter.", "OP-75",
            "Source Imported is not built: it emits from an imported CAD shape (CAD parts in the non-sequential engine are plan item OP-75)."),
        Refused("Source File", "NSC_SFIL", Common, "Rays listed in a file.", "OP-78",
            "Source File is not built: source ray files are plan item OP-78."),
        Refused("Source IESNA File", "NSC_SIES", Common, "Lamp data in an IESNA file.", "OP-78",
            "Source IESNA File is not built: IESNA LM-63 files are plan item OP-78."),
        Refused("Source EULUMDAT File", "NSC_SEUL", Common, "Lamp data in a EULUMDAT file.", "OP-78",
            "Source EULUMDAT File is not built: EULUMDAT files are plan item OP-78."),
        Refused("Source DLL", "NSC_SDLL", Common, "A source from an external program.", "OP-100",
            "Source DLL is not built: user-defined sources are plan item OP-100 (plug-in assemblies)."),
        Refused("Source Diffractive", "NSC_SDIF", Common, "The far-field diffraction pattern of a user-defined aperture.", "OP-100",
            "Source Diffractive is not built: it needs a user-defined aperture's far-field pattern (user-defined aperture files are plan item OP-100)."),
        Refused("Radiant Source Model", "", Common, "A measured source in Radiant's proprietary format.", "OP-77",
            "Radiant Source Model is not built: its files are a proprietary measured format with no reader possible without the vendor (plan item OP-77, Research)."),
    };

    /// <summary>The names of the built source types.</summary>
    public static IReadOnlyList<string> BuiltNames { get; } = Descriptors.Where(d => d.IsBuilt).Select(d => d.TypeName).ToArray();

    /// <summary>The descriptor of <paramref name="typeName"/>, or null when it is not a source type.</summary>
    public static NscObjectTypeDescriptor? Find(string? typeName) =>
        Descriptors.FirstOrDefault(d => string.Equals(d.TypeName, typeName?.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// The source a saved row describes: <paramref name="parameters"/> in OpticStudio's order
    /// (lengths in <paramref name="unit"/>), the Sources tab in <paramref name="properties"/>
    /// (lengths already in meters). Throws <see cref="ArgumentException"/> for bad values and
    /// <see cref="NotSupportedException"/> for a type that is not built, naming the plan item.
    /// </summary>
    public static NscSource Create(string typeName, IReadOnlyList<double> parameters, LensUnit unit, NscSourceProperties? properties = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var d = Find(typeName) ?? throw new ArgumentException($"'{typeName}' is not a source type.");
        if (!d.IsBuilt) throw new NotSupportedException(d.NotBuiltReason);
        int required = d.TypeName == "Source Radial" ? RadialFixedParameters : d.Parameters.Count;
        if (parameters.Count < required)
            throw new ArgumentException($"a {d.TypeName} needs {required} parameters; {parameters.Count} are saved.");
        var p = parameters;
        double L(int i) => OpticsConventions.ToMeters(p[i], unit);
        int I(int i)
        {
            double v = p[i];
            if (v != Math.Floor(v) || !double.IsFinite(v) || Math.Abs(v) > int.MaxValue)
                throw new ArgumentException($"{d.TypeName}: parameter {i + 1} ({d.Parameters[Math.Min(i, d.Parameters.Count - 1)].Name}) must be a whole number; it is {v.ToString("g9", CultureInfo.InvariantCulture)}.");
            return (int)v;
        }
        bool Flag(int i)
        {
            int v = I(i);
            if (v != 0 && v != 1) throw new ArgumentException($"{d.TypeName}: parameter {i + 1} ({d.Parameters[i].Name}) must be 0 or 1; it is {v}.");
            return v == 1;
        }
        var common = new NscSourceCommon { LayoutRays = I(0), AnalysisRays = I(1), Power = p[2], Wavenumber = I(3), Color = I(4) };
        var props = properties;
        return d.TypeName switch
        {
            "Source Point" => new NscSourcePoint(p[5], common, props),
            "Source Ray" => new NscSourceRay(p[5], p[6], p[7], I(8), common, props),
            "Source Ellipse" => new NscSourceEllipse(L(5), L(6), L(7), p[8], p[9], p[10],
                p[7] == 0 ? p[11] : L(11), p[7] == 0 ? p[12] : L(12), L(13), L(14), common, props),
            "Source Rectangle" => new NscSourceRectangle(L(5), L(6), L(7), p[8], p[9], p[10],
                p[7] == 0 ? p[11] : L(11), p[7] == 0 ? p[12] : L(12), L(13), L(14), common, props),
            "Source Gaussian" => new NscSourceGaussian(L(5), L(6), common, props),
            "Source Diode" => new NscSourceDiode(L(5), p[6], p[7], p[8], p[9], I(10), I(11), L(12), L(13),
                L(14), L(15), p[16], L(17), L(18), p[19], common, props),
            "Source Two Angle" => new NscSourceTwoAngle(L(5), L(6), p[7], p[8], Flag(9), Flag(10), Flag(11), common, props),
            "Source Radial" => Radial(),
            "Source Filament" => new NscSourceFilament(L(5), L(6), p[7], common, props),
            "Source Tube" => new NscSourceTube(L(5), L(6), common, props),
            "Source Volume Cylinder" => new NscSourceVolume(NscVolumeShape.Cylinder, L(5), L(6), L(7), common, props),
            "Source Volume Ellipse" => new NscSourceVolume(NscVolumeShape.Ellipse, L(5), L(6), L(7), common, props),
            "Source Volume Rectangle" => new NscSourceVolume(NscVolumeShape.Rectangle, L(5), L(6), L(7), common, props),
            _ => throw new NotSupportedException($"{d.TypeName} is not built."),
        };

        NscSource Radial()
        {
            int n = I(9);
            if (n < 5 || n > 181) throw new ArgumentException($"Source Radial: the number of points must be between 5 and 181; it is {n}.");
            if (p.Count < 10 + n) throw new ArgumentException($"Source Radial: {n} points need {10 + n} parameters; {p.Count} are saved.");
            return new NscSourceRadial(L(5), L(6), p[7], p[8], p.Skip(10).Take(n).ToArray(), common, props);
        }
    }
}
