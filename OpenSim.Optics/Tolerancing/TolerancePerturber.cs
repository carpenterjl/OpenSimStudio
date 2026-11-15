using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.Tolerancing;

/// <summary>What a perturbation is set to besides its value.</summary>
public sealed record ToleranceApplyOptions
{
    /// <summary>The direction of a radial operand (TSDR, TEDR, TARR, TRLR), degrees counter-clockwise
    /// from +x. The default 90° is +y: the help sets "the +Y axis direction" for TARR and TRLR in the
    /// sensitivity analysis, and this program uses the same for TSDR and TEDR, whose pages give none.</summary>
    public double DirectionDegrees { get; init; } = 90;

    /// <summary>TIRR's astigmatism angle θ (the Irregular surface's parameter 8), degrees; 0 puts it
    /// along y. The Monte Carlo analysis draws it "randomly between 0 and 360 degrees".</summary>
    public double AstigmatismAngleDegrees { get; init; }

    /// <summary>Maps the row's surface numbers (as the table names them, on the nominal lens) to the
    /// surfaces of the record being perturbed, when earlier changes inserted surfaces
    /// (<see cref="ToleranceChange.MapSurface"/>). Null is the identity.</summary>
    public Func<int, int>? SurfaceMap { get; init; }
}

/// <summary>
/// One applied perturbation: the perturbed record, what was done, and how to take it back out.
/// <see cref="Remove"/> deletes the surfaces the change inserted, renumbers everything back and
/// restores the values it changed from the copies it kept, so removing it from
/// <see cref="Perturbed"/> gives the record it was applied to, bitwise.
/// </summary>
public sealed class ToleranceChange
{
    private readonly int[] _map;
    private readonly int[] _inserted;
    private readonly IReadOnlyDictionary<int, OpticsSurfaceSettings> _restore;
    private readonly bool _tableChanged;
    private readonly OpticsMultiConfigSettings? _table;

    internal ToleranceChange(OpticsSettings perturbed, string description, int[] map, int[] inserted,
        IReadOnlyDictionary<int, OpticsSurfaceSettings> restore, bool tableChanged, OpticsMultiConfigSettings? table)
    {
        Perturbed = perturbed;
        Description = description;
        _map = map;
        _inserted = inserted;
        _restore = restore;
        _tableChanged = tableChanged;
        _table = table;
    }

    /// <summary>The perturbed record.</summary>
    public OpticsSettings Perturbed { get; }

    /// <summary>What was changed, in words: the row, the operand, the value and the surfaces.</summary>
    public string Description { get; }

    /// <summary>The surfaces this change inserted, numbered in <see cref="Perturbed"/>.</summary>
    public IReadOnlyList<int> InsertedSurfaces => _inserted;

    /// <summary>The number in <see cref="Perturbed"/> of surface <paramref name="before"/> of the
    /// record the change was applied to.</summary>
    public int MapSurface(int before) => before >= 0 && before < _map.Length ? _map[before] : before;

    /// <summary>The record without this change. <paramref name="perturbed"/> is <see cref="Perturbed"/>
    /// or a record built from it by later changes that were removed first (last in, first out).</summary>
    public OpticsSettings Remove(OpticsSettings perturbed)
    {
        ArgumentNullException.ThrowIfNull(perturbed);
        var s = _inserted.Length > 0 ? SavedLensSurgery.Remove(perturbed, _inserted) : perturbed;
        if (_restore.Count > 0)
        {
            var list = s.Surfaces!.ToList();
            foreach (var (j, original) in _restore) list[j] = original;
            s = s with { Surfaces = list };
        }
        if (_tableChanged) s = s with { MultiConfiguration = _table };
        return s;
    }
}

/// <summary>
/// Applies tolerance rows to copies of the saved lens (plan item OP-63, docs/Optics-Conventions.md
/// §36): each operand's rule, read from its OpticStudio 2025 R2 help page, sets the perturbation
/// to a given value. Direct values (radius, curvature, fringes, thickness, conic, semi-diameter,
/// parameters, index and Abbe, user-defined breaks, multi-configuration values) are changed in
/// place; surface decenters, tilts, TIR and TIRR convert a Standard surface to the Irregular type;
/// element decenters, tilts and rolls wrap the group in coordinate breaks.
/// <para>
/// <b>Element wrap.</b> For a group First–Last (and a pivot P in the frame where First's own tilt
/// starts, lens units) the surfaces inserted are: before First, a zero coordinate break (the return
/// target) and, when P ≠ 0, a break to P (decenter x, y, thickness z), the perturbing break(s), a
/// break back from P; after Last, a break returning to the target (orientation and XYZ, tilt then
/// decenter); then one "ghost" break per group surface that repeats the group's own steps — its
/// thickness picked up from the surface's, a break's values picked up from the break, a surface's
/// own tilts and decenters copied — so every surface after the group stays where it was, whatever
/// the solves and compensators later make of those thicknesses. The help describes OpticStudio's
/// layout only as breaks before and after the group with a dummy "to return to the front vertex";
/// these surfaces are this program's equivalent.
/// </para>
/// <para>
/// The rules read the nominal lens for what the row does not change: clear semi-diameters (a
/// fixed one, or the automatic one the nominal lens sizes, in its current configuration) and the
/// primary wavelength. A row this version cannot apply throws <see cref="NotSupportedException"/>
/// with the reason; a row that does not fit the lens throws <see cref="ArgumentException"/> naming
/// the row, the operand and the surface.
/// </para>
/// </summary>
public sealed class TolerancePerturber
{
    private readonly OpticsSettings _nominal;
    private readonly IReadOnlyList<OpticalCatalog> _catalogs;
    private readonly Lazy<IReadOnlyList<double>> _clear;

    public TolerancePerturber(OpticsSettings nominal, IReadOnlyList<OpticalCatalog> catalogs)
    {
        _nominal = nominal ?? throw new ArgumentNullException(nameof(nominal));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        if (nominal.Surfaces is not { Count: >= 3 })
            throw new ArgumentException("Tolerancing needs a lens with the object, at least one surface and the image.");
        _clear = new Lazy<IReadOnlyList<double>>(SizeNominal);
    }

    private double PerUnit => OpticsConventions.MetersPer(ProjectLens.LensUnitOf(_nominal.SystemData));

    private IReadOnlyList<double> SizeNominal()
    {
        var lens = MultiConfiguration.LensFor(_nominal, MultiConfiguration.CurrentConfiguration(_nominal), _catalogs)
            ?? throw new ArgumentException("The nominal lens has no surfaces.");
        var data = SystemDataSettings.FromSettings(_nominal.SystemData, new List<string>());
        var report = SemiDiameterSizing.Compute(lens, data);
        return report.Clear.Select(m => m / PerUnit).ToArray();
    }

    /// <summary>The clear semi-diameter of nominal surface <paramref name="surface"/> in lens units:
    /// the fixed value, or the automatic one sized on the nominal lens.</summary>
    public double ClearSemiDiameter(int surface)
    {
        var s = _nominal.Surfaces![surface];
        if (s.SemiDiameter is double sd && sd > 0) return sd;
        return _clear.Value[surface];
    }

    /// <summary>The test wavelength of a row, micrometers: TWAV's, or the primary wavelength.</summary>
    public double TestWavelengthMicrometers(ToleranceRowContext context)
    {
        if (context.TestWavelengthMicrometers is double w)
        {
            if (!(w > 0) || !double.IsFinite(w)) throw new ArgumentException($"TWAV: the test wavelength is {w}; it must be a positive number of micrometers.");
            return w;
        }
        var data = SystemDataSettings.FromSettings(_nominal.SystemData, new List<string>());
        return data.Wavelengths.Primary.Micrometers;
    }

    /// <summary>
    /// Applies row <paramref name="rowIndex"/> (0-based) of <paramref name="table"/> to
    /// <paramref name="current"/> at <paramref name="value"/> (in the operand's unit; normally Min or
    /// Max). The record may already carry earlier changes; then give their surface map in the options.
    /// </summary>
    public ToleranceChange Apply(OpticsSettings current, ToleranceTable table, int rowIndex, double value, ToleranceApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(table);
        if (rowIndex < 0 || rowIndex >= table.Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex), rowIndex, $"The tolerance table has rows 1 to {table.Rows.Count}.");
        options ??= new ToleranceApplyOptions();
        var row = table.Rows[rowIndex];
        var d = ToleranceOperandRegistry.Get(row.Code);
        string who = $"Tolerance row {rowIndex + 1} ({d.Code})";
        if (d.IsControl)
            throw new ArgumentException($"{who}: a control row; it perturbs nothing.");
        if (d.NotAppliedReason is { } why)
            throw new NotSupportedException($"{who} cannot be applied: {why}");
        if (!double.IsFinite(value)) throw new ArgumentException($"{who}: the value {value} is not a finite number.");
        if (current.Surfaces is not { Count: >= 3 } surfaces)
            throw new ArgumentException($"{who}: the lens has no surfaces to perturb.");
        var context = table.ContextAt(rowIndex);
        var job = new Job(this, current, row, d, who, value, options, context, table.IrregularityModel);
        return job.Run();
    }

    // ------------------------------------------------------------------ one application

    private sealed class Job
    {
        private readonly TolerancePerturber _p;
        private readonly OpticsSettings _current;
        private readonly List<OpticsSurfaceSettings> _surfaces;
        private readonly ToleranceOperandRow _row;
        private readonly ToleranceOperandDefinition _d;
        private readonly string _who;
        private readonly double _v;
        private readonly ToleranceApplyOptions _o;
        private readonly ToleranceRowContext _context;
        private readonly IrregularityModel _model;
        private readonly Dictionary<int, OpticsSurfaceSettings> _restore = new();

        public Job(TolerancePerturber p, OpticsSettings current, ToleranceOperandRow row, ToleranceOperandDefinition d, string who,
            double value, ToleranceApplyOptions options, ToleranceRowContext context, IrregularityModel model)
        {
            _p = p;
            _current = current;
            _surfaces = current.Surfaces!.ToList();
            _row = row;
            _d = d;
            _who = who;
            _v = value;
            _o = options;
            _context = context;
            _model = model;
        }

        private int Count => _surfaces.Count;
        private string V => F(_v);

        private static string F(double x) => x.ToString("G9", CultureInfo.InvariantCulture);

        private ArgumentException Fail(string what) => new($"{_who}: {what}");

        /// <summary>A nominal surface number checked against the nominal lens, and its number now.</summary>
        private (int Nominal, int Now) Surface(int n, string label, bool allowImage = false)
        {
            int last = _p._nominal.Surfaces!.Count - (allowImage ? 1 : 2);
            if (n < 1 || n > last)
                throw Fail($"{label} is {n}; it must be a surface from 1 to {last}{(allowImage ? " (the image included)" : " (not the object or the image)")}.");
            int now = _o.SurfaceMap?.Invoke(n) ?? n;
            if (now < 1 || now >= Count) throw Fail($"{label} {n} maps to surface {now}, which the lens does not have.");
            return (n, now);
        }

        private ToleranceChange Edited(string description)
        {
            var map = Enumerable.Range(0, Count).ToArray();
            return new ToleranceChange(_current with { Surfaces = _surfaces }, description, map, Array.Empty<int>(), _restore, false, null);
        }

        private void Set(int j, OpticsSurfaceSettings s)
        {
            if (!_restore.ContainsKey(j)) _restore[j] = _surfaces[j];
            _surfaces[j] = s;
        }

        /// <summary>Refuses a cell whose value a solve, a pickup or a multi-configuration row sets,
        /// since the perturbation would be overwritten.</summary>
        private void CheckCell(int j, string column, string label)
        {
            if (SurfaceFlags.Of(_surfaces[j], column) is { } f && !SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag)
                && !(SurfaceFlags.IsKind(f, SavedSurfaces.SolveFlag) && f.Solve is SavedSolves.Automatic && column == SavedSurfaces.SemiDiameterColumn))
                throw Fail($"surface {j}'s {label} is set by a {(SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) ? "pickup" : $"'{f.Solve}' solve")}, which would overwrite the tolerance; remove it first.");
            if (_current.MultiConfiguration?.Operands is { } rows)
                for (int r = 0; r < rows.Count; r++)
                {
                    var op = rows[r];
                    if (op is null || op.Number1 != j) continue;
                    string? target = (op.Type ?? "").ToUpperInvariant() switch
                    {
                        "CRVT" => SavedSurfaces.RadiusColumn,
                        "THIC" => SavedSurfaces.ThicknessColumn,
                        "CONN" => SavedSurfaces.ConicColumn,
                        "SDIA" => SavedSurfaces.SemiDiameterColumn,
                        "GLSS" => SavedSurfaces.MaterialColumn,
                        "PRAM" => SavedSurfaces.ParameterColumn(op.Number2),
                        _ => null,
                    };
                    if (target == column)
                        throw Fail($"surface {j}'s {label} is set by multi-configuration row {r + 1} ({op.Type}), which would overwrite the tolerance; tolerance that row's values with TMCO instead.");
                }
        }

        /// <summary>Refuses a cell the rule reads from the saved lens when a solve or pickup sets
        /// it, since the traced value would differ from the saved one.</summary>
        private void ExactCell(int j, string column, string label)
        {
            if (SurfaceFlags.Of(_surfaces[j], column) is { } f && !SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag))
                throw Fail($"surface {j}'s {label} is set by a {(SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) ? "pickup" : $"'{f.Solve}' solve")}; the rule reads the saved value, which the trace does not use. Remove the {(SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) ? "pickup" : "solve")} first.");
        }

        public ToleranceChange Run() => _d.Code switch
        {
            "TRAD" => Radius(),
            "TCUR" => Curvature(),
            "TFRN" => Fringes(),
            "TTHI" => Thickness(),
            "TCON" => Conic(),
            "TSDI" => SemiDiameter(),
            "TSDX" or "TSDY" or "TSDR" or "TSTX" or "TSTY" or "TIRX" or "TIRY" or "TIRR" => Irregular(),
            "TEZI" => Tezi(),
            "TPAR" or "TPAI" => Parameter(),
            "TIND" or "TABB" => Material(),
            "TUDX" or "TUDY" or "TUTX" or "TUTY" or "TUTZ" => UserBreak(),
            "TMCO" => MultiConfigValue(),
            "TEDX" or "TEDY" or "TEDR" or "TETX" or "TETY" or "TETZ" or "TARX" or "TARY" or "TARR" or "TRLX" or "TRLY" or "TRLR" => Element(),
            _ => throw new NotSupportedException($"{_who}: no perturbation rule for this operand."),
        };

        // ---- radius, curvature, fringes

        private ToleranceChange Radius()
        {
            var (_, j) = Surface(_row.Int1, "Surf", allowImage: true);
            CheckCell(j, SavedSurfaces.RadiusColumn, "radius");
            var s = _surfaces[j];
            if (SavedSurfaces.IsPlane(s)) throw Fail($"surface {j} is a plane, which has no radius to change; use TCUR or TFRN.");
            double r = s.Radius!.Value, r2 = _row.Int2 switch
            {
                0 => r + _v,
                1 => r * (1 + _v / 100),
                _ => throw Fail($"Code (Int2) is {_row.Int2}; it must be 0 (lens units) or 1 (percent)."),
            };
            if (r2 == 0 || !double.IsFinite(r2)) throw Fail($"the radius {F(r)} changed by {V} is {F(r2)}, not a radius.");
            Set(j, s with { Radius = r2 });
            return Edited($"{_who}: surface {j} radius {F(r)} → {F(r2)}.");
        }

        private ToleranceChange SetCurvature(int j, double dc, string what)
        {
            CheckCell(j, SavedSurfaces.RadiusColumn, "radius");
            var s = _surfaces[j];
            double c = SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value, c2 = c + dc;
            Set(j, s with { Radius = c2 == 0 ? null : 1 / c2 });
            return Edited($"{_who}: surface {j} curvature {F(c)} → {F(c2)} per lens unit{what}.");
        }

        private ToleranceChange Curvature()
        {
            var (_, j) = Surface(_row.Int1, "Surf", allowImage: true);
            return SetCurvature(j, _v, "");
        }

        private double TestWavelengthInLensUnits() => _p.TestWavelengthMicrometers(_context) * 1e-6 / _p.PerUnit;

        private ToleranceChange Fringes()
        {
            // ΔZ = (λ/2)·N and ΔC = 2·ΔZ/r² ("TFRN: Tolerance on Fringes").
            var (n, j) = Surface(_row.Int1, "Surf", allowImage: true);
            double r = _p.ClearSemiDiameter(n);
            if (!(r > 0)) throw Fail($"surface {n}'s clear semi-diameter is {F(r)}; fringes need a positive one.");
            double dz = TestWavelengthInLensUnits() / 2 * _v;
            return SetCurvature(j, 2 * dz / (r * r), $" ({V} fringes at {F(_p.TestWavelengthMicrometers(_context))} µm over the semi-diameter {F(r)})");
        }

        // ---- thickness, conic, semi-diameter

        private ToleranceChange Thickness()
        {
            var (_, j) = Surface(_row.Int1, "Surf");
            CheckCell(j, SavedSurfaces.ThicknessColumn, "thickness");
            var s = _surfaces[j];
            if (s.ThicknessIsInfinite) throw Fail($"surface {j}'s thickness is infinite.");
            Set(j, s with { Thickness = s.Thickness + _v });
            string text = $"{_who}: surface {j} thickness {F(s.Thickness)} → {F(s.Thickness + _v)}";
            if (_row.Int2 != _row.Int1)
            {
                var (_, a) = Surface(_row.Int2, "Adjust");
                CheckCell(a, SavedSurfaces.ThicknessColumn, "thickness");
                var t = _surfaces[a];
                if (t.ThicknessIsInfinite) throw Fail($"the adjustment surface {a}'s thickness is infinite.");
                Set(a, t with { Thickness = t.Thickness - _v });
                text += $", adjustment surface {a} {F(t.Thickness)} → {F(t.Thickness - _v)}";
            }
            return Edited(text + ".");
        }

        private ToleranceChange Conic()
        {
            var (_, j) = Surface(_row.Int1, "Surf", allowImage: true);
            CheckCell(j, SavedSurfaces.ConicColumn, "conic");
            var s = _surfaces[j];
            Set(j, s with { Conic = s.Conic + _v });
            return Edited($"{_who}: surface {j} conic {F(s.Conic)} → {F(s.Conic + _v)}.");
        }

        private ToleranceChange SemiDiameter()
        {
            var (n, j) = Surface(_row.Int1, "Surf", allowImage: true);
            CheckCell(j, SavedSurfaces.SemiDiameterColumn, "semi-diameter");
            var s = _surfaces[j];
            double sd = s.SemiDiameter is double fixedSd && fixedSd > 0 ? fixedSd : _p.ClearSemiDiameter(n);
            double sd2 = sd + _v;
            if (!(sd2 > 0)) throw Fail($"the semi-diameter {F(sd)} changed by {V} is not positive.");
            var t = SurfaceFlags.Without(s, f => f.Column == SavedSurfaces.SemiDiameterColumn) with { SemiDiameter = sd2 };
            Set(j, t);
            return Edited($"{_who}: surface {j} semi-diameter {F(sd)}{(s.SemiDiameter is null ? " (automatic)" : "")} → {F(sd2)} (fixed).");
        }

        // ---- the Irregular surface

        private static OpticsSurfaceSettings WithValue(OpticsSurfaceSettings s, int number, double value)
        {
            bool extra = SavedSurfaces.IsExtraData(s, number);
            var list = ((extra ? s.ExtraData : s.Parameters) ?? new List<OpticsSurfaceParameterSettings>())
                .Where(p => p.Number != number).ToList();
            list.Add(new OpticsSurfaceParameterSettings { Number = number, Value = value });
            list.Sort((a, b) => a.Number.CompareTo(b.Number));
            return extra ? s with { ExtraData = list } : s with { Parameters = list };
        }

        private ToleranceChange Irregular()
        {
            // "TSDX, TSDY, TSDR", "TSTX, TSTY", "TIRX, TIRY", "TIRR": the surface "must be a Standard
            // surface type" and is "first converted from a Standard to an Irregular surface type"
            // ("Tolerancing with the Irregular Surface Type"). A surface an earlier row already made
            // Irregular takes the new term added to its own, so tilt, decenter and irregularity
            // combine as the help says the Monte Carlo analysis does.
            var (n, j) = Surface(_row.Int1, "Surf");
            var s = _surfaces[j];
            bool irregular = string.Equals(s.Type, "Irregular", StringComparison.OrdinalIgnoreCase);
            if (!SavedSurfaces.IsStandard(s) && !irregular)
                throw Fail($"surface {j} is a '{s.Type}' surface; {_d.Code} needs a Standard surface (use the element operands TEDX, TEDY, TETX, TETY for other types).");
            if (s.TiltBefore is not null || s.TiltAfter is not null)
                throw Fail($"surface {j} has its own tilt or decenter; the Irregular surface's tilt would be added to it in an order the help does not give.");
            var t = irregular ? s : s with { Type = "Irregular", Parameters = null, ExtraData = null };
            double Get(int k) => irregular ? SavedSurfaces.Value(s, k) : 0;
            string text;
            switch (_d.Code)
            {
                case "TSDX": t = WithValue(t, 1, Get(1) + _v); text = $"decenter x {V}"; break;
                case "TSDY": t = WithValue(t, 2, Get(2) + _v); text = $"decenter y {V}"; break;
                case "TSDR":
                {
                    var (sin, cos) = OpticsConventions.SinCosDegrees(_o.DirectionDegrees);
                    t = WithValue(WithValue(t, 1, Get(1) + _v * cos), 2, Get(2) + _v * sin);
                    text = $"decenter {V} toward {F(_o.DirectionDegrees)}° from +x";
                    break;
                }
                case "TSTX": t = WithValue(t, 3, Get(3) + _v); text = $"tilt about x {V}°"; break;
                case "TSTY": t = WithValue(t, 4, Get(4) + _v); text = $"tilt about y {V}°"; break;
                case "TIRX" or "TIRY":
                {
                    // θ = atan(ΔZ/S) with ΔZ = TIR/2 at the semi-diameter S; "a sag along Y implies a
                    // rotation about the X axis, and a sag along X implies a rotation about the Y axis".
                    double sd = _p.ClearSemiDiameter(n);
                    if (!(sd > 0)) throw Fail($"surface {n}'s clear semi-diameter is {F(sd)}; TIR needs a positive one.");
                    double theta = Math.Atan(_v / 2 / sd) * 180 / Math.PI;
                    int k = _d.Code == "TIRX" ? 4 : 3;
                    t = WithValue(t, k, Get(k) + theta);
                    text = $"TIR {V} over the semi-diameter {F(sd)}: tilt about {(k == 4 ? "y" : "x")} {F(theta)}°";
                    break;
                }
                default:   // TIRR
                {
                    // r_max of the Irregular terms is the clear semi-diameter, which must then be fixed.
                    double sd = s.SemiDiameter is double fixedSd && fixedSd > 0 ? fixedSd : _p.ClearSemiDiameter(n);
                    if (!(sd > 0)) throw Fail($"surface {n}'s clear semi-diameter is {F(sd)}; irregularity needs a positive one.");
                    if (s.SemiDiameter is null)
                    {
                        CheckCell(j, SavedSurfaces.SemiDiameterColumn, "semi-diameter");
                        t = SurfaceFlags.Without(t, f => f.Column == SavedSurfaces.SemiDiameterColumn) with { SemiDiameter = sd };
                    }
                    double term = IrregularityModels.TirrTerm(_v, TestWavelengthInLensUnits(), _model);
                    double theta = _o.AstigmatismAngleDegrees;
                    if (irregular && (Get(6) != 0 || Get(7) != 0) && Get(8) != theta)
                        throw Fail($"surface {j} already holds astigmatism or coma at θ = {F(Get(8))}°; a second TIRR at θ = {F(theta)}° cannot be added to it.");
                    t = WithValue(WithValue(WithValue(t, 5, Get(5) + term), 6, Get(6) + term), 8, theta);
                    text = $"irregularity {V} fringes at {F(_p.TestWavelengthMicrometers(_context))} µm ({(_model == IrregularityModel.OpticStudio ? "OpticStudio's formula" : "peak-to-valley with power removed")}): Zs = Za = {F(term)} lens units, θ = {F(theta)}°"
                           + (s.SemiDiameter is null ? $", semi-diameter fixed at {F(sd)}" : "");
                    break;
                }
            }
            Set(j, t);
            return Edited($"{_who}: surface {j} {(irregular ? "Irregular" : "Standard → Irregular")}, {text}.");
        }

        private ToleranceChange Tezi()
        {
            // "TEZI": a Toroidal surface keeps its own Zernike Standard terms, which "must be zero" at
            // nominal; the normalization radius is the clear semi-diameter; for the sensitivity
            // analysis every term from Min to Max gets the same coefficient, their root sum of squares
            // the RMS. Standard and Even Asphere surfaces would become Zernike Standard Sag (OP-25).
            var (n, j) = Surface(_row.Int1, "Surf");
            var s = _surfaces[j];
            int max = _row.Int2, min = _row.Int3;
            if (max < 3 || max > 231) throw Fail($"the maximum term (Int2) is {max}; it must be from 3 to 231.");
            if (min < 2 || min > max) throw Fail($"the minimum term (Int3) is {min}; it must be from 2 to the maximum term {max}.");
            if (!string.Equals(s.Type, "Toroidal", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{_who} cannot be applied to surface {j} ('{s.Type ?? SavedSurfaces.StandardType}'): it converts the surface to a Zernike Standard Sag surface, which is not built in this version (OP-25); only a Toroidal surface, which holds the terms itself, is toleranced.");
            for (int k = 15; k <= 245; k++)
                if (SavedSurfaces.Value(s, k) != 0)
                    throw Fail($"surface {j}'s Zernike term {k - 14} is {F(SavedSurfaces.Value(s, k))}; TEZI on a Toroidal surface needs every nominal Zernike term to be zero.");
            double sd = _p.ClearSemiDiameter(n);
            if (!(sd > 0)) throw Fail($"surface {n}'s clear semi-diameter is {F(sd)}; the Zernike normalization radius needs a positive one.");
            int terms = max - min + 1;
            double c = IrregularityModels.TeziCoefficient(_v, terms);
            var t = WithValue(WithValue(s, 13, Math.Max(max, SavedSurfaces.Value(s, 13))), 14, sd);
            for (int k = min; k <= max; k++) t = WithValue(t, 14 + k, c);
            Set(j, t);
            return Edited($"{_who}: surface {j} Toroidal Zernike terms {min}–{max} each {F(c)} lens units (RMS {V}), normalization radius {F(sd)}.");
        }

        // ---- parameters

        private ToleranceChange Parameter()
        {
            var (_, j) = Surface(_row.Int1, "Surf");
            var s = _surfaces[j];
            int number = _row.Int2;
            var layout = SurfaceTypes.Find(s.Type);
            var info = layout?.Parameter(number);
            if (SavedSurfaces.IsStandard(s) || info is null)
                throw Fail($"surface {j} ({s.Type ?? SavedSurfaces.StandardType}) has no parameter {number}.");
            if (info.IsInteger) throw Fail($"surface {j}'s parameter {number} ({info.Name}) is a whole number, not a value to tolerance.");
            string column = SavedSurfaces.ParameterColumn(number);
            CheckCell(j, column, $"parameter {number}");
            double p = SavedSurfaces.Value(s, number), p2;
            if (_d.Code == "TPAR") p2 = p + _v;
            else
            {
                // P′ = 1/(1/P + Δ) ("TPAI: Tolerance on the Inverse of Parameter Data").
                if (p == 0) throw Fail($"surface {j}'s parameter {number} is zero, whose inverse is infinite.");
                double inv = 1 / p + _v;
                if (inv == 0) throw Fail($"1/P + Δ is zero for surface {j}'s parameter {number}, so P′ would be infinite.");
                p2 = 1 / inv;
            }
            Set(j, WithValue(s, number, p2));
            return Edited($"{_who}: surface {j} parameter {number} ({info.Name}) {F(p)} → {F(p2)}.");
        }

        // ---- index and Abbe

        private ToleranceChange Material()
        {
            // "TIND: Tolerance on Index", "TABB: Tolerance on Abbe".
            var (_, j) = Surface(_row.Int1, "Surf");
            var s = _surfaces[j];
            bool index = _d.Code == "TIND";
            if (SavedSurfaces.IsMirror(s)) throw Fail($"surface {j} is a mirror; it has no index.");
            if (SavedSurfaces.IsAir(s)) throw Fail($"the medium after surface {j} is the system's air; give it a material to tolerance its {(index ? "index" : "Abbe number")}.");
            CheckCell(j, SavedSurfaces.MaterialColumn, "material");
            if (s.ModelGlass is { } m)
            {
                var m2 = index ? m with { Nd = m.Nd + _v } : m with { Vd = m.Vd + _v };
                if (!(m2.Nd > 1) || !(m2.Vd > 0)) throw Fail($"the model glass would have nd {F(m2.Nd)} and Vd {F(m2.Vd)}; nd must exceed 1 and Vd be positive.");
                Set(j, s with { ModelGlass = m2 });
                return Edited($"{_who}: surface {j} model glass {(index ? $"nd {F(m.Nd)} → {F(m2.Nd)}" : $"Vd {F(m.Vd)} → {F(m2.Vd)}")}.");
            }
            if (s.Index is double n && string.IsNullOrWhiteSpace(s.Material))
            {
                if (!index)
                    return Edited($"{_who}: surface {j} has a constant index, which has no dispersion; TABB is ignored (\"Otherwise, TABB is ignored\").");
                Set(j, s with { Index = n + _v });
                return Edited($"{_who}: surface {j} index {F(n)} → {F(n + _v)} (a constant offset).");
            }
            // A catalog glass: "if ... all defined wavelengths are between 0.3 and 2.5 micrometers,
            // the TIND is interpreted to be a change in the d-light index"; written as a glass offset
            // solve (ΔNd, ΔVd) on the material.
            var data = SystemDataSettings.FromSettings(_current.SystemData, new List<string>());
            if (data.Wavelengths.Wavelengths.Any(w => w.Micrometers < 0.3 || w.Micrometers > 2.5))
                throw new NotSupportedException($"{_who}: a wavelength lies outside 0.3–2.5 µm, where OpticStudio applies {(index ? "a constant index offset to a catalog glass" : "no Abbe change")}; the saved lens cannot hold a constant offset on a catalog glass in this version.");
            var existing = SurfaceFlags.Of(s, SavedSurfaces.MaterialColumn);
            if (existing is not null && !(SurfaceFlags.IsKind(existing, SavedSurfaces.SolveFlag) && existing.Solve == "Offset"))
                throw Fail($"surface {j}'s material has a {existing.Kind} flag; remove it first.");
            double dn = existing?.SolveParameters is { Count: > 0 } p0 ? p0[0] : 0;
            double dv = existing?.SolveParameters is { Count: > 1 } p1 ? p1[1] : 0;
            if (index) dn += _v; else dv += _v;
            var flag = new OpticsCellFlagSettings
            {
                Column = SavedSurfaces.MaterialColumn, Kind = SavedSurfaces.SolveFlag, Solve = "Offset", SolveParameters = new List<double> { dn, dv },
            };
            Set(j, SurfaceFlags.With(s, SavedSurfaces.MaterialColumn, flag));
            return Edited($"{_who}: surface {j} glass {s.Material} offset ΔNd {F(dn)}, ΔVd {F(dv)} (this program's glass offset rule, docs/Optics-Conventions.md §36).");
        }

        // ---- user-defined breaks and multi-configuration values

        private ToleranceChange UserBreak()
        {
            // "TUDX, TUDY, TUTX, TUTY, TUTZ": Int1 must already be a coordinate break.
            var (_, j) = Surface(_row.Int1, "Surf");
            var s = _surfaces[j];
            if (!string.Equals(s.Type, SurfaceTypes.CoordinateBreak, StringComparison.OrdinalIgnoreCase))
                throw Fail($"surface {j} is not a coordinate break; {_d.Code} tolerances a break's value and inserts none.");
            int number = _d.Code switch { "TUDX" => 1, "TUDY" => 2, "TUTX" => 3, "TUTY" => 4, _ => 5 };
            CheckCell(j, SavedSurfaces.ParameterColumn(number), $"parameter {number}");
            if (s.CoordinateReturn is { Mode: not (null or "None") })
                throw Fail($"surface {j}'s values are solved by its coordinate return; the tolerance would be overwritten.");
            double p = SavedSurfaces.Value(s, number);
            Set(j, WithValue(s, number, p + _v));
            return Edited($"{_who}: coordinate break {j} parameter {number} {F(p)} → {F(p + _v)}.");
        }

        private ToleranceChange MultiConfigValue()
        {
            // "TMCO": Int1 the operand row, Int2 the configuration.
            var table = _current.MultiConfiguration;
            if (table?.Operands is not { Count: > 0 } rows) throw Fail("the lens has no multi-configuration table.");
            int r = _row.Int1, c = _row.Int2;
            if (r < 1 || r > rows.Count) throw Fail($"the operand row (Int1) is {r}; the table has rows 1 to {rows.Count}.");
            int configs = Math.Max(1, table.ConfigurationCount);
            if (c < 1 || c > configs) throw Fail($"the configuration (Int2) is {c}; the table has configurations 1 to {configs}.");
            var op = rows[r - 1];
            var known = MultiConfigOperands.Find(op?.Type);
            if (known is null || known.Kind != MultiConfigValueKind.Number)
                throw Fail($"multi-configuration row {r} ({op?.Type ?? MultiConfigOperands.Off}) does not hold a real number.");
            var values = (op!.Values ?? new List<OpticsConfigValueSettings>()).ToList();
            while (values.Count < c) values.Add(new OpticsConfigValueSettings());
            var cell = values[c - 1];
            if (cell.IsInfinite) throw Fail($"multi-configuration row {r}, configuration {c} is infinite.");
            if (cell.Solve is "Pickup" or "ThermalPickup") throw Fail($"multi-configuration row {r}, configuration {c} is a {cell.Solve}, which would overwrite the tolerance.");
            values[c - 1] = cell with { Value = cell.Value + _v };
            var newRows = rows.ToList();
            newRows[r - 1] = op with { Values = values };
            var perturbed = _current with { MultiConfiguration = table with { Operands = newRows } };
            return new ToleranceChange(perturbed, $"{_who}: multi-configuration row {r} ({op.Type}), configuration {c}: {F(cell.Value)} → {F(cell.Value + _v)}.",
                Enumerable.Range(0, Count).ToArray(), Array.Empty<int>(), _restore, true, table);
        }

        // ---- element decenters, tilts and rolls

        private ToleranceChange Element()
        {
            var (n1, first) = Surface(_row.Int1, "First surf");
            var (n2, last) = Surface(_row.Int2, "Last surf");
            if (last < first) throw Fail($"the last surface ({_row.Int2}) comes before the first ({_row.Int1}).");
            string code = _d.Code;
            var breaks = new List<OpticsTiltDecenterSettings>();
            (double X, double Y, double Z) pivot = (0, 0, 0);
            string text;
            if (code.StartsWith("TED", StringComparison.Ordinal))
            {
                double dx = 0, dy = 0;
                if (code == "TEDX") dx = _v;
                else if (code == "TEDY") dy = _v;
                else
                {
                    var (sin, cos) = OpticsConventions.SinCosDegrees(_o.DirectionDegrees);
                    dx = _v * cos;
                    dy = _v * sin;
                }
                breaks.Add(new OpticsTiltDecenterSettings { DecenterX = dx, DecenterY = dy });
                text = $"decentered by ({F(dx)}, {F(dy)}) lens units";
            }
            else if (code.StartsWith("TET", StringComparison.Ordinal))
            {
                pivot = Pivot(n1, n2, first, last);
                breaks.Add(code switch
                {
                    "TETX" => new OpticsTiltDecenterSettings { TiltX = _v },
                    "TETY" => new OpticsTiltDecenterSettings { TiltY = _v },
                    _ => new OpticsTiltDecenterSettings { TiltZ = _v },
                });
                text = $"tilted {V}° about {code[3..].ToLowerInvariant()} about the {PivotName(_context.Pivot)} ({F(pivot.X)}, {F(pivot.Y)}, {F(pivot.Z)})";
            }
            else
            {
                // Rolls: "rotation of an element about the center of curvature of one of its faces";
                // RollSurf (Int3) "must be equal to either Surf1 or Surf2".
                var (nr, roll) = Surface(_row.Int3, "Roll surf");
                if (nr != n1 && nr != n2) throw Fail($"the roll surface ({_row.Int3}) must be the first or the last surface of the group ({_row.Int1} or {_row.Int2}).");
                if (code.StartsWith("TRL", StringComparison.Ordinal) && n1 == n2) throw Fail("Surf1 must not equal Surf2 (the TIR is read on the other surface).");
                var z = StraightPositions(first, last, "a roll");
                var rs = _surfaces[roll];
                if (!SavedSurfaces.IsStandard(rs) || SavedSurfaces.IsPlane(rs))
                    throw Fail($"the roll surface {roll} must be a spherical Standard surface (a cylindrical one is not built).");
                ExactCell(roll, SavedSurfaces.RadiusColumn, "radius");
                double zc = z[roll - first] + rs.Radius!.Value;
                pivot = (0, 0, zc);
                double direction = code[3] == 'X' ? 0 : code[3] == 'Y' ? 90 : _o.DirectionDegrees;
                double alpha;
                if (code.StartsWith("TAR", StringComparison.Ordinal))
                {
                    alpha = _v;
                    text = $"rolled {V}° toward {F(direction)}° from +x about the center of curvature of surface {roll} (z = {F(zc)})";
                }
                else
                {
                    int read = roll == first ? last : first;
                    int nRead = roll == first ? n2 : n1;
                    var s = _surfaces[read];
                    if (!SavedSurfaces.IsStandard(s)) throw Fail($"the read surface {read} must be a Standard surface to read its TIR.");
                    ExactCell(read, SavedSurfaces.RadiusColumn, "radius");
                    ExactCell(read, SavedSurfaces.ConicColumn, "conic");
                    double h = _p.ClearSemiDiameter(nRead);
                    double c = SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value;
                    alpha = RollGeometry.AngleForTir(z[read - first], c, s.Conic, zc, h, _v) * 180 / Math.PI;
                    text = $"rolled {F(alpha)}° toward {F(direction)}° from +x about the center of curvature of surface {roll} (z = {F(zc)}), TIR {V} on surface {read} at ±{F(h)}";
                }
                double psi = direction - 90;
                if (psi != 0) breaks.Add(new OpticsTiltDecenterSettings { TiltZ = psi });
                breaks.Add(new OpticsTiltDecenterSettings { TiltX = alpha });
                if (psi != 0) breaks.Add(new OpticsTiltDecenterSettings { TiltZ = -psi });
            }
            return Wrap(first, last, pivot, breaks, $"{_who}: surfaces {first}–{last} {text}.");
        }

        private static string PivotName(TolerancePivot p) => p switch
        {
            TolerancePivot.RearVertex => "rear vertex",
            TolerancePivot.FrontMechanicalEdge => "front mechanical edge",
            TolerancePivot.RearMechanicalEdge => "rear mechanical edge",
            TolerancePivot.GeometricCenter => "geometric center",
            _ => "front vertex",
        };

        /// <summary>The vertex z of each group surface from the first's, lens units, for a group
        /// without breaks or surface tilts (its axis is straight).</summary>
        private double[] StraightPositions(int first, int last, string what)
        {
            var z = new double[last - first + 1];
            for (int j = first; j <= last; j++)
            {
                var s = _surfaces[j];
                if (string.Equals(s.Type, SurfaceTypes.CoordinateBreak, StringComparison.OrdinalIgnoreCase) || s.TiltBefore is not null || (s.TiltAfter is not null && j < last))
                    throw Fail($"{what} needs a group with a straight axis, but surface {j} is a coordinate break or has its own tilt.");
                if (j < last)
                {
                    if (s.ThicknessIsInfinite) throw Fail($"surface {j}'s thickness is infinite.");
                    ExactCell(j, SavedSurfaces.ThicknessColumn, "thickness");
                    if (_current.MultiConfiguration?.Operands?.FindIndex(o => o is not null && o.Number1 == j && string.Equals(o.Type, "THIC", StringComparison.OrdinalIgnoreCase)) is int mc and >= 0)
                        throw Fail($"surface {j}'s thickness is set by multi-configuration row {mc + 1}; the pivot is placed from the saved thickness, which that row overrides.");
                    z[j - first + 1] = z[j - first] + s.Thickness;
                }
            }
            return z;
        }

        /// <summary>The MPVT pivot in the group's start frame ("MPVT: Define Mechanical Pivot Point").
        /// The rear vertex is Surf2's; a mechanical edge is read as the axial point at the sag of the
        /// surface's mechanical semi-diameter; the geometric center as the midpoint of the two vertices
        /// (this program's readings, docs/Optics-Conventions.md §36).</summary>
        private (double, double, double) Pivot(int n1, int n2, int first, int last)
        {
            var kind = _context.Pivot;
            if (kind == TolerancePivot.FrontVertex) return (0, 0, 0);
            var z = StraightPositions(first, last, $"the pivot at the {PivotName(kind)}");
            double zl = z[^1];
            double Edge(int j, int nominal)
            {
                var s = _surfaces[j];
                if (!SavedSurfaces.IsStandard(s)) throw Fail($"the {PivotName(kind)} pivot needs surface {j} to be a Standard surface.");
                double clear = s.SemiDiameter is double sd && sd > 0 ? sd : _p.ClearSemiDiameter(nominal);
                double mech = s.MechanicalSemiDiameter is double m && m > 0 ? Math.Max(m, clear + s.ChipZone) : clear + s.ChipZone;
                double c = SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value;
                double sag = RollGeometry.ConicSag(c, s.Conic, mech);
                if (double.IsNaN(sag)) throw Fail($"surface {j} does not reach its mechanical semi-diameter {F(mech)}.");
                return sag;
            }
            return kind switch
            {
                TolerancePivot.RearVertex => (0, 0, zl),
                TolerancePivot.GeometricCenter => (0, 0, zl / 2),
                TolerancePivot.FrontMechanicalEdge => (0, 0, Edge(first, n1)),
                _ => (0, 0, zl + Edge(last, n2)),
            };
        }

        private static OpticsSurfaceSettings Break(OpticsTiltDecenterSettings t, double thickness, string comment, int order = 0) => new()
        {
            Type = SurfaceTypes.CoordinateBreak,
            Comment = comment,
            Thickness = thickness,
            Parameters = new List<OpticsSurfaceParameterSettings>
            {
                new() { Number = 1, Value = t.DecenterX }, new() { Number = 2, Value = t.DecenterY },
                new() { Number = 3, Value = t.TiltX }, new() { Number = 4, Value = t.TiltY }, new() { Number = 5, Value = t.TiltZ },
                new() { Number = 6, Value = order },
            },
        };

        private static int OrderFlag(string? order) => order == nameof(TiltDecenterOrder.TiltThenDecenter) ? 1 : 0;

        private static OpticsTiltDecenterSettings Reversed(OpticsTiltDecenterSettings t) => new()
        {
            DecenterX = -t.DecenterX, DecenterY = -t.DecenterY, TiltX = -t.TiltX, TiltY = -t.TiltY, TiltZ = -t.TiltZ,
            Order = OrderFlag(t.Order) == 0 ? nameof(TiltDecenterOrder.TiltThenDecenter) : nameof(TiltDecenterOrder.DecenterThenTilt),
        };

        /// <summary>The "after" tilt of surface <paramref name="j"/> as values, its pick-up modes resolved.</summary>
        private OpticsTiltDecenterSettings? AfterValues(int j)
        {
            var s = _surfaces[j];
            if (s.TiltAfter is not { } a) return null;
            OpticsTiltDecenterSettings? Before(int k) => k >= 0 && k < Count ? _surfaces[k].TiltBefore : null;
            OpticsTiltDecenterSettings Zero() => new();
            return a.Mode switch
            {
                null or "Explicit" => a with { Mode = null, Surface = 0 },
                "PickUpThisSurface" => s.TiltBefore ?? Zero(),
                "ReverseThisSurface" => s.TiltBefore is { } b ? Reversed(b) : Zero(),
                "PickUpSurface" => Before(a.Surface) ?? Zero(),
                "ReverseSurface" => Before(a.Surface) is { } c ? Reversed(c) : Zero(),
                _ => throw Fail($"surface {j}'s tilt-after mode '{a.Mode}' is not one this version knows."),
            };
        }

        private ToleranceChange Wrap(int first, int last, (double X, double Y, double Z) pivot, List<OpticsTiltDecenterSettings> breaks, string description)
        {
            for (int j = first; j <= last; j++)
            {
                var s = _surfaces[j];
                if (s.CoordinateReturn is { Mode: not (null or "None") })
                    throw Fail($"surface {j} in the group is a coordinate break with a coordinate return, which the group's ghost path cannot repeat; tolerance it with TUDX … TUTZ instead.");
                if (s.ThicknessIsInfinite) throw Fail($"surface {j} in the group has an infinite thickness.");
            }
            var b = new SavedLensSurgery.Builder(_surfaces);
            b.CopyRange(0, first - 1);
            const string tag = "Tolerance: ";
            int target = b.Add(Break(new OpticsTiltDecenterSettings(), 0, tag + "return target"));
            bool pivoted = pivot != (0, 0, 0);
            if (pivoted) b.Add(Break(new OpticsTiltDecenterSettings { DecenterX = pivot.X, DecenterY = pivot.Y }, pivot.Z, tag + "to pivot"));
            foreach (var t in breaks) b.Add(Break(t, 0, tag + _d.Code));
            if (pivoted) b.Add(Break(new OpticsTiltDecenterSettings { DecenterX = -pivot.X, DecenterY = -pivot.Y }, -pivot.Z, tag + "from pivot"));
            b.CopyRange(first, last);
            b.Add(Break(new OpticsTiltDecenterSettings(), 0, tag + "return", order: 1) with
            {
                CoordinateReturn = new OpticsCoordinateReturnSettings { Mode = nameof(CoordinateReturnMode.OrientationXYZ), Surface = target },
            });
            for (int j = first; j <= last; j++)
            {
                var s = _surfaces[j];
                int source = b.NewIndex(j);
                var thickness = SurfaceFlags.Pickup(SavedSurfaces.ThicknessColumn, source, SavedSurfaces.ThicknessColumn);
                if (string.Equals(s.Type, SurfaceTypes.CoordinateBreak, StringComparison.OrdinalIgnoreCase))
                {
                    var ghost = Break(new OpticsTiltDecenterSettings(), s.Thickness, tag + $"repeats surface {j}") with
                    {
                        Parameters = Enumerable.Range(1, 6).Select(k => new OpticsSurfaceParameterSettings { Number = k, Value = SavedSurfaces.Value(s, k) }).ToList(),
                    };
                    var flags = Enumerable.Range(1, 6).Select(k => SurfaceFlags.Pickup(SavedSurfaces.ParameterColumn(k), source, SavedSurfaces.ParameterColumn(k))).ToList();
                    flags.Add(thickness);
                    b.Add(ghost with { Flags = flags });
                    continue;
                }
                if (s.TiltBefore is { } before)
                    b.Add(Break(before, 0, tag + $"repeats surface {j}'s tilt before", OrderFlag(before.Order)));
                var after = AfterValues(j) ?? new OpticsTiltDecenterSettings();
                b.Add(Break(after, s.Thickness, tag + $"repeats surface {j}", OrderFlag(after.Order)) with { Flags = new List<OpticsCellFlagSettings> { thickness } });
            }
            b.CopyRange(last + 1, Count - 1);
            var (settings, map, inserted) = b.Build(_current with { Surfaces = _surfaces });
            return new ToleranceChange(settings, description + $" {inserted.Length} surfaces inserted.", map, inserted,
                new Dictionary<int, OpticsSurfaceSettings>(), false, null);
        }
    }
}
