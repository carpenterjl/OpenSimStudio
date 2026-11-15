// OP-55B
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Solves;

namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// Parameter data, glass data, element positions, system data, multi-configuration data,
/// paraxial Gaussian beam data and TCE data (help pages: Constraints on Parameter Data,
/// Constraints on Glass Data, Constraints on Element Positions, Changing System Data,
/// Multi-Configuration (Zoom) Data, Gaussian Beam Data with Paraxial Gaussian Beam, Thermal
/// Coefficient of Expansion Data). GCOS and RGLA are OP-59's, CONF OP-54's. Not built: IMSF,
/// PRIM, SVIG, CVIG, FDMO, FDRE (they change the system data for the rows after them), the skew
/// Gaussian beam operands (OP-48 did not build the skew beam); see <see cref="NotBuiltOperands"/>.
/// </summary>
public static partial class ConstraintOperands
{
    private static IEnumerable<MeritOperandDefinition> ParameterData()
    {
        const string page = "Constraints on Parameter Data; Understanding Boundary Operands";
        foreach (var (code, name, kind) in new[] { ("PMGT", "Parameter greater than", Kind.AtLeast), ("PMLT", "Parameter less than", Kind.AtMost), ("PMVA", "Parameter value", Kind.Value) })
            yield return Make(code, name, ParameterCategory, page,
                $"Parameter Param (numbered from 0, as the lens data editor) of surface Surf, in the parameter's own unit with lengths in lens units{Bounded(kind)}.",
                c => Apply(kind, ParameterValue(c, c.Surface(c.Row.Int1), c.Row.Int2), c.Row.Target),
                int1: "Surf", int2: "Param", bound: kind != Kind.Value);
    }

    /// <summary>Parameter <paramref name="number"/> of surface j as the lens data editor shows it:
    /// read as the parameter solves read it (the shape's own parameters, else the registry of
    /// saved surface types, a coordinate break's six values), then from SI to lens units by the
    /// length power its layout declares (<see cref="SurfaceParameterInfo.LengthPower"/>).</summary>
    internal static double ParameterValue(MeritRowContext c, int j, int number)
    {
        var s = c.Lens.Surfaces[j];
        string type = s.IsCoordinateBreak ? SurfaceTypes.CoordinateBreak : s.Shape?.TypeName ?? "Standard";
        double si;
        if (s.Shape is IParameterizedShape shape)
        {
            if (number < 0 || number >= shape.ParameterCount)
                throw new MeritOperandException($"surface {j} ({type}) has parameters 0 to {shape.ParameterCount - 1}; there is no parameter {number}.");
            si = shape.GetParameter(number);
        }
        else if (!SurfaceTypes.TryGetParameter(s, number, out si))
            throw new MeritOperandException(SurfaceTypes.ParameterCount(s) == 0
                ? $"surface {j} ({type}) has no parameters."
                : $"surface {j} ({type}) has no parameter {number}.");
        var info = SurfaceTypes.Find(type)?.Parameter(number)
            ?? throw new MeritOperandException($"surface {j} ({type}): parameter {number}'s unit is not declared by its type's layout, so it cannot be shown in lens units.");
        return si / SurfaceTypes.Factor(info.LengthPower, OpticsConventions.ToMeters(1, c.Unit));
    }

    private static IEnumerable<MeritOperandDefinition> GlassData()
    {
        const string page = "Constraints on Glass Data";
        yield return Make("GTCE", "Glass TCE", GlassCategory, page,
            "Thermal coefficient of expansion α (−30 to +70 °C, 10⁻⁶/K) the glass catalog lists for the glass after surface Surf; for a surface without a catalog glass see TCVA.",
            c =>
            {
                int j = c.Surface(c.Row.Int1);
                var m = ReportText.SpaceMedia(c.Lens)[j].Material
                    ?? throw new MeritOperandException($"the medium after surface {j} is not a catalog glass; use TCVA for the surface's own TCE.");
                return m.ThermalExpansion ?? throw new MeritOperandException($"{m.Who}: the catalog lists no thermal expansion coefficient.");
            }, int1: "Surf");

        yield return Make("INDX", "Index of refraction", GlassCategory, page,
            "Index of refraction, relative to the system's air, of the space after surface Surf at wavelength Wave, as the trace uses it.",
            c =>
            {
                int j = c.Surface(c.Row.Int1);
                return c.Paraxial(c.Wavelength(c.Row.Int2)).Indices[j];
            }, int1: "Surf", int2: "Wave");

        yield return GlassRange("MNAB", "Minimum Abbe number", "Abbe number Vd", true, p => p.Vd);
        yield return GlassRange("MXAB", "Maximum Abbe number", "Abbe number Vd", false, p => p.Vd);
        yield return GlassRange("MNIN", "Minimum index at d-light", "index Nd", true, p => p.Nd);
        yield return GlassRange("MXIN", "Maximum index at d-light", "index Nd", false, p => p.Nd);
        yield return GlassRange("MNPD", "Minimum partial dispersion deviation", "deviation of the partial dispersion ΔPg,F", true, p => p.DeltaPgF);
        yield return GlassRange("MXPD", "Maximum partial dispersion deviation", "deviation of the partial dispersion ΔPg,F", false, p => p.DeltaPgF);
    }

    /// <summary>MNAB, MXAB, MNIN, MXIN, MNPD, MXPD: over the surfaces Surf1 to Surf2 whose medium
    /// is a glass (catalog or model; air, constant indices, mirrors and coordinate breaks are
    /// skipped, as RGLA does), the glass-map quantity (the catalog's printed nd, Vd and ΔPg,F,
    /// else computed: <see cref="GlassDistance.TryPoint"/>) bounded by the target as MNCT is.</summary>
    private static MeritOperandDefinition GlassRange(string code, string name, string what, bool atLeast, Func<GlassPoint, double?> quantity) =>
        Make(code, name, GlassCategory, "Constraints on Glass Data; Constraints on Lens Data (MNPD); Understanding Boundary Operands",
            $"Each {what} of the glasses after surfaces Surf1 to Surf2 bounded {(atLeast ? "below" : "above")} by the target; the value is the target {(atLeast ? "less" : "plus")} the sum of the violations.",
            c =>
            {
                var (a, b) = Range(c);
                var q = new List<double>();
                for (int j = a; j <= b && j < c.Lens.ImageIndex; j++)
                {
                    var s = c.Lens.Surfaces[j];
                    if (s.IsMirror || s.IsCoordinateBreak || s.Medium.Material is not { } material) continue;
                    if (!GlassDistance.TryPoint(material, out var point, out string why))
                        throw new MeritOperandException($"the glass after surface {j} has no nd and Vd: {why}");
                    q.Add(quantity(point!) ?? throw new MeritOperandException($"the glass after surface {j} ({material.Who}) has no {what} (its data does not reach the g line)."));
                }
                if (q.Count == 0) throw new MeritOperandException($"surfaces {a} to {b} have no glass.");
                return RangeBound(atLeast, c.Row.Target, q);
            }, int1: "Surf1", int2: "Surf2", bound: true);

    private static IEnumerable<MeritOperandDefinition> ElementPositions()
    {
        const string page = "Constraints on Element Positions; Global vertex report (OP-21)";
        const string frame = " In the global frame: surface 1's local frame, the Global vertex report's default reference (OpticStudio's default global coordinate reference surface is surface 1).";
        foreach (var (code, letter, axis) in new[] { ("GLCX", "x", 0), ("GLCY", "y", 1), ("GLCZ", "z", 2) })
            yield return Make(code, $"Global vertex {letter}", PositionCategory, page, $"Global vertex {letter} coordinate of surface Surf, lens units." + frame,
                c =>
                {
                    var t = Placement(c).RelativeTo(c.Surface(c.Row.Int1), 1).Translation;
                    return c.Length(axis switch { 0 => t.X, 1 => t.Y, _ => t.Z });
                }, int1: "Surf");
        foreach (var (code, letter, axis) in new[] { ("GLCA", "x", 0), ("GLCB", "y", 1), ("GLCC", "z", 2) })
            yield return Make(code, $"Global orientation {letter}", PositionCategory, page,
                $"Global {letter} component of the orientation vector of surface Surf: its local z axis (R{axis + 1}3)." + frame,
                c =>
                {
                    var z = Placement(c).RelativeTo(c.Surface(c.Row.Int1), 1).Col2;
                    return axis switch { 0 => z.X, 1 => z.Y, _ => z.Z };
                }, int1: "Surf");
        yield return Make("GLCR", "Global rotation matrix", PositionCategory, page,
            "Component of the global rotation matrix R of surface Surf (global = vertex + R·local): Data 1 to 9 return R11, R12, R13, R21 … R33." + frame,
            c =>
            {
                var t = Placement(c).RelativeTo(c.Surface(c.Row.Int1), 1);
                int k = Flag(c.Row.Int2, "Data (Int2)", 1, 9, "1 R11, 2 R12 … 9 R33");
                int row = (k - 1) / 3, col = (k - 1) % 3;
                var column = col switch { 0 => t.Col0, 1 => t.Col1, _ => t.Col2 };
                return row switch { 0 => column.X, 1 => column.Y, _ => column.Z };
            }, int1: "Surf", int2: "Data");
    }

    private static IEnumerable<MeritOperandDefinition> SystemData()
    {
        yield return Make("WLEN", "Wavelength", SystemCategory, "Changing System Data",
            "Wavelength Wave in micrometers, as the wavelength table holds it (measured in the system's air).",
            c => c.System.Wavelengths.Wavelengths[c.Wavelength(c.Row.Int2) - 1].Micrometers, int2: "Wave");
    }

    private static IEnumerable<MeritOperandDefinition> MultiConfigurationData()
    {
        const string page = "Multi-Configuration (Zoom) Data; Understanding Boundary Operands";
        foreach (var (code, name, kind) in new[] { ("MCOG", "Multi-configuration value greater than", Kind.AtLeast), ("MCOL", "Multi-configuration value less than", Kind.AtMost), ("MCOV", "Multi-configuration value", Kind.Value) })
            yield return Make(code, name, MultiConfigCategory, page,
                $"Value of row Op# of the multi-configuration table in configuration Cfg#, pickups evaluated, in the editor's units{Bounded(kind)}.",
                c =>
                {
                    var configs = c.Configurations
                        ?? throw new MeritOperandException("this evaluation was not given its configurations, so the multi-configuration table cannot be read.");
                    return Apply(kind, configs.MultiConfigValue(c.Row.Int1, c.Row.Int2), c.Row.Target);
                }, int1: "Op#", int2: "Cfg#", bound: kind != Kind.Value);
    }

    private enum BeamQuantity { Divergence, Position, Radius, Size, Waist, Rayleigh }

    private static IEnumerable<MeritOperandDefinition> GaussianBeamData()
    {
        yield return Beam("GBPD", "Paraxial Gaussian beam divergence", "far-field half-angle divergence (1/e² radius), radians", BeamQuantity.Divergence);
        yield return Beam("GBPP", "Paraxial Gaussian beam position", "position, the distance from the waist to the surface (negative when the waist lies after it), lens units", BeamQuantity.Position);
        yield return Beam("GBPR", "Paraxial Gaussian beam radius of curvature", "wavefront radius of curvature, positive when diverging, lens units", BeamQuantity.Radius);
        yield return Beam("GBPS", "Paraxial Gaussian beam size", "beam size (1/e² radius, M times the embedded beam's), lens units", BeamQuantity.Size);
        yield return Beam("GBPW", "Paraxial Gaussian beam waist", "waist radius (M times the embedded beam's), lens units", BeamQuantity.Waist);
        yield return Beam("GBPZ", "Paraxial Gaussian beam Rayleigh range", "Rayleigh range, lens units", BeamQuantity.Rayleigh);
    }

    /// <summary>
    /// The paraxial Gaussian beam operands over OP-48's <see cref="GaussianBeamTrace"/>, in the
    /// space after surface Surf at wavelength Wave. UseX not zero is the x beam, else the y beam.
    /// W0 is the embedded (TEM00) waist in lens units and S1toW the distance from surface 1 to the
    /// waist, negative when the waist lies before surface 1 (help: Paraxial Gaussian Beam, "Waist
    /// Size" and "Surf 1 to Waist"); the input beam is therefore a waist of W0·M at S1toW with
    /// M² = M2 Factor. An M2 Factor of 0 (an empty column) is read as 1, a perfect Gaussian.
    /// </summary>
    private static MeritOperandDefinition Beam(string code, string name, string what, BeamQuantity q) =>
        Make(code, name, BeamCategory, "Gaussian Beam Data; Paraxial Gaussian Beam",
            $"Paraxial Gaussian beam {what}, in the space after surface Surf at wavelength Wave; the x beam when UseX is not 0, else the y beam; input embedded waist W0 (lens units) at S1toW from surface 1 with M² = M2 Factor (0 read as 1).",
            c =>
            {
                int j = c.Surface(c.Row.Int1);
                var model = c.Paraxial(c.Wavelength(c.Row.Int2));
                bool useX = c.Row.DataAt(1) != 0;
                double w0 = OpticsConventions.ToMeters(c.Row.DataAt(2), c.Unit);
                double s1 = OpticsConventions.ToMeters(c.Row.DataAt(3), c.Unit);
                double m2 = c.Row.DataAt(4) == 0 ? 1 : c.Row.DataAt(4);
                if (!(m2 >= 1)) throw new MeritOperandException($"M2 Factor (Data4) is {F(m2)}; it must be at least 1 (0 is read as 1).");
                if (!(w0 > 0)) throw new MeritOperandException($"W0 (Data2) is {F(c.Row.DataAt(2))}; the waist must be positive.");
                var plane = new GaussianBeamPlane(w0 * Math.Sqrt(m2), s1, m2);
                var trace = GaussianBeamTrace.Create(model, new GaussianBeamInput(plane, plane));
                var states = useX ? trace.X : trace.Y;
                if (j >= states.Count) throw new MeritOperandException($"the beam has no state after surface {j}.");
                var st = states[j];
                return q switch
                {
                    BeamQuantity.Divergence => st.Divergence,
                    BeamQuantity.Position => c.Length(st.DistanceFromWaist),
                    BeamQuantity.Radius => double.IsFinite(st.WavefrontRadius) ? c.Length(st.WavefrontRadius)
                        : throw new MeritOperandException($"the beam is at its waist after surface {j}, so its wavefront radius is infinite."),
                    BeamQuantity.Size => c.Length(st.BeamRadius),
                    BeamQuantity.Waist => c.Length(st.WaistRadius),
                    _ => c.Length(st.RayleighRange),
                };
            }, int1: "Surf", int2: "Wave", data: new[] { "UseX", "W0", "S1toW", "M2 Factor" });

    private static IEnumerable<MeritOperandDefinition> ThermalData()
    {
        const string page = "Thermal Coefficient of Expansion Data; Understanding Boundary Operands";
        foreach (var (code, name, kind) in new[] { ("TCGT", "TCE greater than", Kind.AtLeast), ("TCLT", "TCE less than", Kind.AtMost), ("TCVA", "TCE value", Kind.Value) })
            yield return Make(code, name, ThermalCategory, page,
                $"The TCE column of surface Surf (10⁻⁶/K, as the lens data editor holds it){Bounded(kind)}. For a glass's own TCE see GTCE.",
                c => Apply(kind, c.Lens.Surfaces[c.Surface(c.Row.Int1)].ThermalExpansion, c.Row.Target),
                int1: "Surf", bound: kind != Kind.Value);
    }
}
