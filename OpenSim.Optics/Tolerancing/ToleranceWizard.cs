using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.Tolerancing;

// OP-66: the Tolerance Wizard (docs/Optics-Conventions.md §51). Source: OpticStudio 2025 R2 help,
// "Tolerance Wizard" (plans/sources/optics/op-64-help/Tolerance_Wizard.html), its figure of the
// dialog (plans/sources/optics/op-66-help/images/ToleranceWizard.png) and its equation image
// (op-66-help/images/eq_Tolerance_Wizard_1.svg, read as θx = atan(Δy/(2S))); "TTHI: Tolerance on
// Thickness", "TABB: Tolerance on Abbe", "COMP: Define Compensator" (op-63-help).

/// <summary>How the wizard's radius tolerance is given (help, "Tolerance Wizard", "Radius": "a fixed
/// distance in lens units, a percentage of the nominal radius or by fringes of power at the test wavelength").</summary>
public enum ToleranceRadiusUnit
{
    LensUnits = 0,
    Percent = 1,
    Fringes = 2,
}

/// <summary>How the wizard's surface tilt is given (help, "Tilt (TIR) X/Y": "in either lens units or degrees").</summary>
public enum ToleranceTiltUnit
{
    /// <summary>Total indicator runout in lens units (TIRX, TIRY on a Standard surface).</summary>
    LensUnits = 0,
    Degrees = 1,
}

/// <summary>
/// The Tolerance Wizard's settings (help, "Tolerance Wizard"). Each tolerance is written as Min = −v,
/// Max = +v. The defaults are the values the help page's figure of the dialog shows
/// (images/ToleranceWizard.png: Radius 0.2 millimeters, Thickness 0.2, Decenter X and Y 0.2, Tilt X and Y
/// 0.2 millimeters, S + A Irregularity 0.2 fringes, Zernike Irregularity unchecked, element Decenter X and
/// Y 0.2, element Tilt X and Y 0.2 degrees, Index 0.001, Abbe 1 %, Start At Row 1, Test Wavelength 0.633,
/// Use Focus Compensation checked); the figure's lens is in millimeters, so its lengths are read as lens
/// units. Whether those are OpticStudio's Reset values or the Generic/Precision preset the figure also
/// shows cannot be told from the page (§10 item 47). The figure gives no range for the focus
/// compensator; <see cref="FocusCompensatorRange"/> has no default.
/// </summary>
public sealed record ToleranceWizardOptions
{
    public bool Radius { get; init; } = true;
    public ToleranceRadiusUnit RadiusUnit { get; init; } = ToleranceRadiusUnit.LensUnits;
    public double RadiusValue { get; init; } = 0.2;
    public bool Thickness { get; init; } = true;
    public double ThicknessValue { get; init; } = 0.2;
    public bool DecenterX { get; init; } = true;
    public double DecenterXValue { get; init; } = 0.2;
    public bool DecenterY { get; init; } = true;
    public double DecenterYValue { get; init; } = 0.2;
    public bool TiltX { get; init; } = true;
    public ToleranceTiltUnit TiltXUnit { get; init; } = ToleranceTiltUnit.LensUnits;
    public double TiltXValue { get; init; } = 0.2;
    public bool TiltY { get; init; } = true;
    public ToleranceTiltUnit TiltYUnit { get; init; } = ToleranceTiltUnit.LensUnits;
    public double TiltYValue { get; init; } = 0.2;

    /// <summary>"S + A Irreg": TIRR on each Standard surface, fringes at the test wavelength.</summary>
    public bool Irregularity { get; init; } = true;
    public double IrregularityFringes { get; init; } = 0.2;

    public bool ElementDecenterX { get; init; } = true;
    public double ElementDecenterXValue { get; init; } = 0.2;
    public bool ElementDecenterY { get; init; } = true;
    public double ElementDecenterYValue { get; init; } = 0.2;
    public bool ElementTiltX { get; init; } = true;
    public double ElementTiltXDegrees { get; init; } = 0.2;
    public bool ElementTiltY { get; init; } = true;
    public double ElementTiltYDegrees { get; init; } = 0.2;

    /// <summary>TIND, a change in index.</summary>
    public bool Index { get; init; } = true;
    public double IndexValue { get; init; } = 0.001;

    /// <summary>TABB, "percentage of the catalog Abbe value".</summary>
    public bool Abbe { get; init; } = true;
    public double AbbePercent { get; init; } = 1;

    /// <summary>"Start At Row": 1 replaces every row; n &gt; 1 keeps rows 1 to n − 1 and writes the
    /// new rows from row n (rows from n on are replaced); −1 appends after the last row.</summary>
    public int StartAtRow { get; init; } = 1;

    /// <summary>"Test Wavelength", micrometers, written as a TWAV row before the new rows.</summary>
    public double TestWavelength { get; init; } = 0.633;

    /// <summary>"Start At Surface".</summary>
    public int StartSurface { get; init; } = 1;

    /// <summary>"Stop At Surface"; null for the surface before the image.</summary>
    public int? StopSurface { get; init; }

    /// <summary>"Use Focus Comp": a COMP row on the thickness before the image.</summary>
    public bool FocusCompensator { get; init; } = true;

    /// <summary>The focus compensator's ± range in lens units; null writes Min = Max = 0 and says so (no
    /// default from a source).</summary>
    public double? FocusCompensatorRange { get; init; }
}

/// <summary>What <see cref="ToleranceWizard.Generate"/> made: the whole table, the rows it wrote
/// (1-based, in the table), and notes on surfaces it passed over and why.</summary>
public sealed record ToleranceWizardResult(ToleranceTable Table, int FirstRow, int RowCount, IReadOnlyList<string> Notes);

/// <summary>
/// The Tolerance Wizard (plan item OP-66; OpticStudio 2025 R2 help, "Tolerance Wizard"): default
/// tolerance rows for the surfaces Start to Stop of the saved lens.
/// <para>
/// <b>Rows written</b>, in this order: TWAV (the test wavelength), then for each surface in turn its
/// radius, thickness, decenters, tilts and irregularity, then the element rows of each lens group, then
/// the index and Abbe rows, then the focus compensator. The help gives no order; this is this program's.
/// </para>
/// <list type="bullet">
/// <item>Radius: "only placed on surfaces which have optical power, which excludes dummy surfaces which
/// have the same index on both sides" (a mirror has power); TRAD Code 0 (lens units) or 1 (percent), or
/// TFRN (fringes); "If the surface is plano, then the default tolerance is specified as a variation in
/// fringes, even if another option is selected".</item>
/// <item>Thickness: TTHI on "each vertex separation", the adjustment surface "the first air space which
/// follows the surface being toleranced" (help, "TTHI"); read here as the first surface after it whose
/// following medium is air; none (the last air space) leaves Int2 = Int1, no adjustment.</item>
/// <item>Decenter X/Y: "TSDX and TSDY for Standard surface decenters, and TEDX and TEDY for non-Standard
/// surfaces", on the surfaces with power.</item>
/// <item>Tilt X/Y: TSTX/TSTY (degrees) or TIRX/TIRY (lens units) on a Standard surface; TETX/TETY in
/// degrees on another, a TIR converted by θ = atan(Δ/(2S)) (the help's equation), S the clear
/// semi-diameter, "a length of total indicator runout in the x direction corresponds to a tilt about the
/// y axis".</item>
/// <item>S + A Irreg: TIRR "on each Standard surface type" with power.</item>
/// <item>Element decenter and tilt: TEDX, TEDY, TETX, TETY on "each lens group": a run of surfaces from
/// one whose following medium is a glass to the first after it whose following medium is air.</item>
/// <item>Index and Abbe: TIND and TABB on each surface whose following medium is a glass; TABB's value is
/// the percentage of the glass's Vd (model glass, or the catalog's printed Vd); a constant index has no
/// Abbe number ("Otherwise, TABB is ignored"), so no TABB row is written for it.</item>
/// <item>Use Focus Comp: COMP on the thickness before the image (Code 0).</item>
/// </list>
/// <para>
/// Cells a solve or pickup sets are passed over with a note, since the perturber refuses them (§36);
/// coordinate breaks are passed over. Not built: the vendor presets (the help names Commercial,
/// Precision, High Precision and Cell Phone Lens grades but gives no numbers), the Zernike irregularity
/// (TEZI on a Standard surface needs the Zernike Standard Sag surface, OP-25), Save/Load of named
/// settings files (the settings are saved with the lens instead).
/// </para>
/// </summary>
public static class ToleranceWizard
{
    /// <summary>The default tolerance rows for <paramref name="lens"/> placed into <paramref name="existing"/>.</summary>
    /// <exception cref="ArgumentException">Settings out of range (surfaces, start row, a negative value).</exception>
    public static ToleranceWizardResult Generate(OpticsSettings lens, ToleranceTable existing, IReadOnlyList<OpticalCatalog> catalogs,
        ToleranceWizardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(catalogs);
        var o = options ?? new ToleranceWizardOptions();
        var surfaces = lens.Surfaces ?? throw new ArgumentException("Tolerance Wizard: the lens has no surfaces.");
        int image = surfaces.Count - 1;
        if (image < 2) throw new ArgumentException("Tolerance Wizard: the lens needs the object, at least one surface and the image.");
        int start = o.StartSurface, stop = o.StopSurface ?? image - 1;
        if (start < 1 || stop > image - 1 || start > stop)
            throw new ArgumentException($"Tolerance Wizard: Start and Stop At Surface must lie within 1 to {image - 1} with Start not after Stop (they are {start} and {stop}).");
        if (o.StartAtRow != -1 && (o.StartAtRow < 1 || o.StartAtRow > existing.Rows.Count + 1))
            throw new ArgumentException($"Tolerance Wizard: Start At Row must be −1 (append) or 1 to {existing.Rows.Count + 1}, not {o.StartAtRow}.");
        Check(o);

        var notes = new List<string>();
        var rows = new List<ToleranceOperandRow>();
        void Add(string code, int i1, int i2, double v, string comment = "") =>
            rows.Add(new ToleranceOperandRow { Code = code, Int1 = i1, Int2 = i2, Min = -v, Max = v, Comment = comment });

        TolerancePerturber? perturber = null;
        double Semi(int j) => (perturber ??= new TolerancePerturber(lens, catalogs)).ClearSemiDiameter(j);

        rows.Add(new ToleranceOperandRow { Code = "TWAV", Min = o.TestWavelength });

        for (int j = start; j <= stop; j++)
        {
            var s = surfaces[j];
            if (IsBreak(s)) { notes.Add($"Surface {j} is a coordinate break; no surface tolerances."); continue; }
            bool power = HasPower(surfaces, j);
            bool standard = SavedSurfaces.IsStandard(s);

            if (o.Radius && power)
            {
                if (Fixed(s, SavedSurfaces.RadiusColumn) is { } why) notes.Add($"Surface {j}: no radius tolerance, its radius is set by {why}.");
                else if (SavedSurfaces.IsPlane(s) || o.RadiusUnit == ToleranceRadiusUnit.Fringes)
                {
                    if (o.RadiusUnit != ToleranceRadiusUnit.Fringes) PlanoNote(o, j, notes);
                    Add("TFRN", j, 0, o.RadiusValue);
                }
                else Add("TRAD", j, o.RadiusUnit == ToleranceRadiusUnit.Percent ? 1 : 0, o.RadiusValue);
            }

            if (o.Thickness && !s.ThicknessIsInfinite && j < image)
            {
                if (Fixed(s, SavedSurfaces.ThicknessColumn) is { } why) notes.Add($"Surface {j}: no thickness tolerance, its thickness is set by {why}.");
                else
                {
                    int adjust = j;
                    for (int k = j + 1; k < image; k++)
                        if (SavedSurfaces.IsAir(surfaces[k]) && !IsBreak(surfaces[k])) { adjust = k; break; }
                    if (adjust != j && (surfaces[adjust].ThicknessIsInfinite || Fixed(surfaces[adjust], SavedSurfaces.ThicknessColumn) is not null))
                    {
                        notes.Add($"Surface {j}: the adjustment air space {adjust} has a thickness the tolerance cannot change (infinite, or set by a solve or pickup), so the row has no adjustment.");
                        adjust = j;
                    }
                    Add("TTHI", j, adjust, o.ThicknessValue);
                }
            }

            if (power)
            {
                if (o.DecenterX) { if (standard) Add("TSDX", j, 0, o.DecenterXValue); else Add("TEDX", j, j, o.DecenterXValue); }
                if (o.DecenterY) { if (standard) Add("TSDY", j, 0, o.DecenterYValue); else Add("TEDY", j, j, o.DecenterYValue); }
                if (o.TiltX) Tilt(j, s, standard, x: true, o.TiltXUnit, o.TiltXValue);
                if (o.TiltY) Tilt(j, s, standard, x: false, o.TiltYUnit, o.TiltYValue);
                if (o.Irregularity)
                {
                    if (standard) Add("TIRR", j, 0, o.IrregularityFringes);
                    else notes.Add($"Surface {j} is a '{s.Type}' surface; S + A irregularity (TIRR) is written only on Standard surfaces (help).");
                }
            }
        }

        // Element tolerances on each lens group.
        if (o.ElementDecenterX || o.ElementDecenterY || o.ElementTiltX || o.ElementTiltY)
            foreach (var (first, last) in Groups(surfaces, start, stop))
            {
                if (o.ElementDecenterX) Add("TEDX", first, last, o.ElementDecenterXValue);
                if (o.ElementDecenterY) Add("TEDY", first, last, o.ElementDecenterYValue);
                if (o.ElementTiltX) Add("TETX", first, last, o.ElementTiltXDegrees);
                if (o.ElementTiltY) Add("TETY", first, last, o.ElementTiltYDegrees);
            }

        // Index tolerances.
        for (int j = start; j <= stop; j++)
        {
            var s = surfaces[j];
            if (!IsGlass(s)) continue;
            if (Fixed(s, SavedSurfaces.MaterialColumn) is { } why && !(SurfaceFlags.Of(s, SavedSurfaces.MaterialColumn) is { Solve: "Offset" }))
            {
                notes.Add($"Surface {j}: no index or Abbe tolerance, its material is set by {why}.");
                continue;
            }
            if (o.Index) Add("TIND", j, 0, o.IndexValue);
            if (o.Abbe)
            {
                double? vd = s.ModelGlass?.Vd;
                if (vd is null && s.Index is null && !string.IsNullOrWhiteSpace(s.Material))
                    vd = catalogs.Select(c => c.Find(s.Material.Trim())).FirstOrDefault(m => m is not null)?.Vd?.Value;
                if (vd is double v) Add("TABB", j, 0, Math.Abs(v) * o.AbbePercent / 100);
                else if (s.ModelGlass is null && s.Index is not null && string.IsNullOrWhiteSpace(s.Material))
                    notes.Add($"Surface {j}: a constant index has no Abbe number (help, \"TABB\": \"Otherwise, TABB is ignored\"); no TABB row.");
                else notes.Add($"Surface {j}: the glass '{s.Material}' is not in the active catalogs, so its Abbe number is not known; no TABB row.");
            }
        }

        if (o.FocusCompensator)
        {
            int j = image - 1;
            if (Fixed(surfaces[j], SavedSurfaces.ThicknessColumn) is { } why)
                notes.Add($"Use Focus Comp: the thickness of surface {j} is set by {why}; a COMP row on it would be refused, so none is written.");
            else
            {
                double r = o.FocusCompensatorRange ?? 0;
                rows.Add(new ToleranceOperandRow { Code = "COMP", Int1 = j, Int2 = 0, Min = -r, Max = r });
                if (o.FocusCompensatorRange is null)
                    notes.Add($"Use Focus Comp: the COMP row on surface {j} has Min = Max = 0, since no range was given (the help gives none). Paraxial focus compensation ignores COMP rows; Optimize holds it near its nominal (soft bounds) until you enter its Min and Max.");
            }
        }

        // Place the rows.
        var all = existing.Rows.ToList();
        int at = o.StartAtRow == -1 ? all.Count : o.StartAtRow - 1;
        all.RemoveRange(at, all.Count - at);
        all.AddRange(rows);
        if (o.StartAtRow > 1 && o.StartAtRow <= existing.Rows.Count)
            notes.Add($"Rows {o.StartAtRow} to {existing.Rows.Count} were replaced by the new rows (Start At Row {o.StartAtRow}).");
        return new ToleranceWizardResult(new ToleranceTable(all, existing.IrregularityModel), at + 1, rows.Count, notes);

        void Tilt(int j, OpticsSurfaceSettings s, bool standard, bool x, ToleranceTiltUnit unit, double v)
        {
            if (standard)
            {
                if (unit == ToleranceTiltUnit.Degrees) Add(x ? "TSTX" : "TSTY", j, 0, v);
                else Add(x ? "TIRX" : "TIRY", j, 0, v);
                return;
            }
            if (unit == ToleranceTiltUnit.Degrees) { Add(x ? "TETX" : "TETY", j, j, v); return; }
            // A TIR along x is a tilt about y (help): θ = atan(Δ/(2S)).
            double semi = Semi(j);
            if (!(semi > 0))
            {
                notes.Add($"Surface {j}: its clear semi-diameter is zero, so the TIR {F(v)} cannot be turned into a tilt; no tilt row.");
                return;
            }
            double degrees = Math.Atan(v / (2 * semi)) * 180 / Math.PI;
            Add(x ? "TETY" : "TETX", j, j, degrees, $"TIR {F(v)} in {(x ? "x" : "y")} over S = {F(semi)}");
        }
    }

    private static void PlanoNote(ToleranceWizardOptions o, int j, List<string> notes)
    {
        // "If the surface is plano, then the default tolerance is specified as a variation in fringes,
        // even if another option is selected." The help does not say which number of fringes; the
        // Radius box's number is read as fringes (this program's reading, §10 item 47).
        notes.Add($"Surface {j} is plano: its radius tolerance is TFRN (help), the Radius value {F(o.RadiusValue)} read as fringes (the help names no value; this program's reading).");
    }

    private static void Check(ToleranceWizardOptions o)
    {
        void NonNegative(bool on, double v, string name)
        {
            if (on && !(v >= 0 && double.IsFinite(v))) throw new ArgumentException($"Tolerance Wizard: {name} must be zero or more, not {F(v)}.");
        }
        NonNegative(o.Radius, o.RadiusValue, "the radius tolerance");
        NonNegative(o.Thickness, o.ThicknessValue, "the thickness tolerance");
        NonNegative(o.DecenterX, o.DecenterXValue, "Decenter X");
        NonNegative(o.DecenterY, o.DecenterYValue, "Decenter Y");
        NonNegative(o.TiltX, o.TiltXValue, "Tilt X");
        NonNegative(o.TiltY, o.TiltYValue, "Tilt Y");
        NonNegative(o.Irregularity, o.IrregularityFringes, "the S + A irregularity");
        NonNegative(o.ElementDecenterX, o.ElementDecenterXValue, "the element Decenter X");
        NonNegative(o.ElementDecenterY, o.ElementDecenterYValue, "the element Decenter Y");
        NonNegative(o.ElementTiltX, o.ElementTiltXDegrees, "the element Tilt X");
        NonNegative(o.ElementTiltY, o.ElementTiltYDegrees, "the element Tilt Y");
        NonNegative(o.Index, o.IndexValue, "the index tolerance");
        NonNegative(o.Abbe, o.AbbePercent, "the Abbe tolerance");
        NonNegative(o.FocusCompensator && o.FocusCompensatorRange is not null, o.FocusCompensatorRange ?? 0, "the focus compensator's range");
        if (!(o.TestWavelength > 0 && double.IsFinite(o.TestWavelength)))
            throw new ArgumentException($"Tolerance Wizard: the test wavelength must be above zero (micrometers), not {F(o.TestWavelength)}.");
        if (!Enum.IsDefined(o.RadiusUnit) || !Enum.IsDefined(o.TiltXUnit) || !Enum.IsDefined(o.TiltYUnit))
            throw new ArgumentException("Tolerance Wizard: a unit is not one of its choices.");
    }

    private static bool IsBreak(OpticsSurfaceSettings s) => string.Equals(s.Type, SurfaceTypes.CoordinateBreak, StringComparison.OrdinalIgnoreCase);

    /// <summary>A glass after the surface: not air, not a mirror.</summary>
    private static bool IsGlass(OpticsSurfaceSettings s) => !SavedSurfaces.IsAir(s) && !SavedSurfaces.IsMirror(s);

    /// <summary>"Surfaces which have optical power": a mirror, or a surface with different media on its two sides.</summary>
    private static bool HasPower(IReadOnlyList<OpticsSurfaceSettings> surfaces, int j)
    {
        var s = surfaces[j];
        if (SavedSurfaces.IsMirror(s)) return true;
        return !SameMedium(Before(surfaces, j), s);
    }

    /// <summary>The surface whose following medium lies before surface j (mirrors pass the medium back; read
    /// here as the nearest earlier surface that is not a mirror).</summary>
    private static OpticsSurfaceSettings Before(IReadOnlyList<OpticsSurfaceSettings> surfaces, int j)
    {
        for (int k = j - 1; k > 0; k--)
            if (!SavedSurfaces.IsMirror(surfaces[k])) return surfaces[k];
        return surfaces[0];
    }

    private static bool SameMedium(OpticsSurfaceSettings a, OpticsSurfaceSettings b)
    {
        if (SavedSurfaces.IsAir(a) || SavedSurfaces.IsAir(b)) return SavedSurfaces.IsAir(a) && SavedSurfaces.IsAir(b);
        return string.Equals(a.Material?.Trim(), b.Material?.Trim(), StringComparison.OrdinalIgnoreCase)
               && a.Index == b.Index && Equals(a.ModelGlass, b.ModelGlass);
    }

    /// <summary>Lens groups within [start, stop]: from a surface followed by glass to the first later surface
    /// followed by air.</summary>
    internal static IReadOnlyList<(int First, int Last)> Groups(IReadOnlyList<OpticsSurfaceSettings> surfaces, int start, int stop)
    {
        var groups = new List<(int, int)>();
        int j = start;
        while (j <= stop)
        {
            if (!IsGlass(surfaces[j]) || IsBreak(surfaces[j])) { j++; continue; }
            int k = j + 1;
            while (k <= stop && !SavedSurfaces.IsAir(surfaces[k])) k++;
            if (k > stop) break;   // the group runs past Stop: not a whole group in the range
            groups.Add((j, k));
            j = k + 1;
        }
        return groups;
    }

    /// <summary>"a solve" or "a pickup" when the cell is set by one; null for a fixed or variable cell.</summary>
    private static string? Fixed(OpticsSurfaceSettings s, string column) => SurfaceFlags.Of(s, column) is { } f && !SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag)
        ? SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) ? "a pickup" : $"a '{f.Solve}' solve"
        : null;

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>The wizard's options to and from the files' <see cref="OpticsToleranceWizardSettings"/>.</summary>
public static class ToleranceWizardSettings
{
    /// <summary>The saved form; null when every option is the default.</summary>
    public static OpticsToleranceWizardSettings? ToSettings(ToleranceWizardOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var d = new ToleranceWizardOptions();
        T? N<T>(T v, T dv) where T : struct => EqualityComparer<T>.Default.Equals(v, dv) ? null : v;
        var s = new OpticsToleranceWizardSettings
        {
            Radius = N(o.Radius, d.Radius), RadiusUnit = o.RadiusUnit == d.RadiusUnit ? null : o.RadiusUnit.ToString(), RadiusValue = N(o.RadiusValue, d.RadiusValue),
            Thickness = N(o.Thickness, d.Thickness), ThicknessValue = N(o.ThicknessValue, d.ThicknessValue),
            DecenterX = N(o.DecenterX, d.DecenterX), DecenterXValue = N(o.DecenterXValue, d.DecenterXValue),
            DecenterY = N(o.DecenterY, d.DecenterY), DecenterYValue = N(o.DecenterYValue, d.DecenterYValue),
            TiltX = N(o.TiltX, d.TiltX), TiltXUnit = o.TiltXUnit == d.TiltXUnit ? null : o.TiltXUnit.ToString(), TiltXValue = N(o.TiltXValue, d.TiltXValue),
            TiltY = N(o.TiltY, d.TiltY), TiltYUnit = o.TiltYUnit == d.TiltYUnit ? null : o.TiltYUnit.ToString(), TiltYValue = N(o.TiltYValue, d.TiltYValue),
            Irregularity = N(o.Irregularity, d.Irregularity), IrregularityFringes = N(o.IrregularityFringes, d.IrregularityFringes),
            ElementDecenterX = N(o.ElementDecenterX, d.ElementDecenterX), ElementDecenterXValue = N(o.ElementDecenterXValue, d.ElementDecenterXValue),
            ElementDecenterY = N(o.ElementDecenterY, d.ElementDecenterY), ElementDecenterYValue = N(o.ElementDecenterYValue, d.ElementDecenterYValue),
            ElementTiltX = N(o.ElementTiltX, d.ElementTiltX), ElementTiltXDegrees = N(o.ElementTiltXDegrees, d.ElementTiltXDegrees),
            ElementTiltY = N(o.ElementTiltY, d.ElementTiltY), ElementTiltYDegrees = N(o.ElementTiltYDegrees, d.ElementTiltYDegrees),
            Index = N(o.Index, d.Index), IndexValue = N(o.IndexValue, d.IndexValue),
            Abbe = N(o.Abbe, d.Abbe), AbbePercent = N(o.AbbePercent, d.AbbePercent),
            StartAtRow = N(o.StartAtRow, d.StartAtRow), TestWavelength = N(o.TestWavelength, d.TestWavelength),
            StartSurface = N(o.StartSurface, d.StartSurface), StopSurface = o.StopSurface,
            FocusCompensator = N(o.FocusCompensator, d.FocusCompensator), FocusCompensatorRange = o.FocusCompensatorRange,
        };
        return s == new OpticsToleranceWizardSettings() ? null : s;
    }

    /// <summary>The options of saved settings; null is the defaults. An unknown unit name keeps the default
    /// and is named in <paramref name="notes"/>.</summary>
    public static ToleranceWizardOptions FromSettings(OpticsToleranceWizardSettings? saved, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var d = new ToleranceWizardOptions();
        if (saved is null) return d;
        TEnum Parse<TEnum>(string? name, TEnum fallback, string what) where TEnum : struct, Enum
        {
            if (name is null) return fallback;
            if (Enum.TryParse<TEnum>(name, true, out var v) && !int.TryParse(name, out _) && Enum.IsDefined(v)) return v;
            notes.Add($"Tolerance Wizard: the {what} unit '{name}' is not one this version knows; '{fallback}' is used.");
            return fallback;
        }
        return new ToleranceWizardOptions
        {
            Radius = saved.Radius ?? d.Radius, RadiusUnit = Parse(saved.RadiusUnit, d.RadiusUnit, "radius"), RadiusValue = saved.RadiusValue ?? d.RadiusValue,
            Thickness = saved.Thickness ?? d.Thickness, ThicknessValue = saved.ThicknessValue ?? d.ThicknessValue,
            DecenterX = saved.DecenterX ?? d.DecenterX, DecenterXValue = saved.DecenterXValue ?? d.DecenterXValue,
            DecenterY = saved.DecenterY ?? d.DecenterY, DecenterYValue = saved.DecenterYValue ?? d.DecenterYValue,
            TiltX = saved.TiltX ?? d.TiltX, TiltXUnit = Parse(saved.TiltXUnit, d.TiltXUnit, "Tilt X"), TiltXValue = saved.TiltXValue ?? d.TiltXValue,
            TiltY = saved.TiltY ?? d.TiltY, TiltYUnit = Parse(saved.TiltYUnit, d.TiltYUnit, "Tilt Y"), TiltYValue = saved.TiltYValue ?? d.TiltYValue,
            Irregularity = saved.Irregularity ?? d.Irregularity, IrregularityFringes = saved.IrregularityFringes ?? d.IrregularityFringes,
            ElementDecenterX = saved.ElementDecenterX ?? d.ElementDecenterX, ElementDecenterXValue = saved.ElementDecenterXValue ?? d.ElementDecenterXValue,
            ElementDecenterY = saved.ElementDecenterY ?? d.ElementDecenterY, ElementDecenterYValue = saved.ElementDecenterYValue ?? d.ElementDecenterYValue,
            ElementTiltX = saved.ElementTiltX ?? d.ElementTiltX, ElementTiltXDegrees = saved.ElementTiltXDegrees ?? d.ElementTiltXDegrees,
            ElementTiltY = saved.ElementTiltY ?? d.ElementTiltY, ElementTiltYDegrees = saved.ElementTiltYDegrees ?? d.ElementTiltYDegrees,
            Index = saved.Index ?? d.Index, IndexValue = saved.IndexValue ?? d.IndexValue,
            Abbe = saved.Abbe ?? d.Abbe, AbbePercent = saved.AbbePercent ?? d.AbbePercent,
            StartAtRow = saved.StartAtRow ?? d.StartAtRow, TestWavelength = saved.TestWavelength ?? d.TestWavelength,
            StartSurface = saved.StartSurface ?? d.StartSurface, StopSurface = saved.StopSurface,
            FocusCompensator = saved.FocusCompensator ?? d.FocusCompensator, FocusCompensatorRange = saved.FocusCompensatorRange,
        };
    }
}
