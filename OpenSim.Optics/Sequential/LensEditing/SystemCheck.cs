using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential.LensEditing;

/// <summary>How serious a <see cref="SystemCheck"/> finding is.</summary>
public enum CheckSeverity
{
    /// <summary>A flaw the analyses cannot work around: they will refuse the lens.</summary>
    Error,

    /// <summary>A setting to review: the analyses run, but may not mean what was intended.</summary>
    Warning,
}

/// <summary>One finding of <see cref="SystemCheck"/>.</summary>
public sealed record CheckFinding(CheckSeverity Severity, string Message)
{
    public override string ToString() => $"{(Severity == CheckSeverity.Error ? "Error" : "Warning")}: {Message}";
}

/// <summary>
/// System Check: tests the lens for common errors and suspicious settings, each reported as an
/// error or a warning, as OpticStudio's tool does ("Errors should be considered serious flaws
/// … Warnings are less severe, and indicate conditions that should be reviewed"; OpticStudio
/// 2025 R2 help, Setup tab › Diagnostics › System Check,
/// plans/sources/optics/ansyshelp-op31/System_Check.html). The help does not list OpticStudio's
/// checks, so the list here is this program's own:
/// <list type="bullet">
/// <item>Errors: every problem of the system data (<see cref="OpticalSystemData.Validate"/>) and
/// of the surfaces (<see cref="SavedSurfaces.ToLens"/>: unknown surface types, glasses not in
/// the active catalogs, the stop, signs and numbers).</item>
/// <item>Warnings: a coating name not in the lens's coating catalog; a solve stored on a cell
/// (solves are not evaluated in this build, the cell's value is traced); a pickup whose cell
/// does not hold source × scale + offset, or whose source is missing; a glass that does not
/// cover a system wavelength; and the chief ray and the four marginal rays (Px, Py = ±1) of
/// every field and wavelength that fail to reach the image, with the reason and the surface.</item>
/// </list>
/// </summary>
public static class SystemCheck
{
    public static IReadOnlyList<CheckFinding> Run(IReadOnlyList<OpticsSurfaceSettings> surfaces, LensEditContext context)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(context);
        var findings = new List<CheckFinding>();
        var notes = new List<string>();
        var system = context.System(notes);
        foreach (var n in notes) findings.Add(new(CheckSeverity.Warning, n));
        foreach (var p in system.Validate()) findings.Add(new(CheckSeverity.Error, p));
        CheckCells(surfaces, context, findings);

        SequentialLens? lens = null;
        try { lens = context.ToLens(surfaces); }
        catch (ArgumentException ex)
        {
            foreach (var line in ex.Message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                findings.Add(new(CheckSeverity.Error, line));
        }
        if (lens is not null && findings.All(f => f.Severity != CheckSeverity.Error))
        {
            try { TraceRays(lens, system, findings); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or ArithmeticException)
            {
                findings.Add(new(CheckSeverity.Error, $"The rays cannot be traced: {ex.Message}"));
            }
        }
        return findings;
    }

    private static void CheckCells(IReadOnlyList<OpticsSurfaceSettings> surfaces, LensEditContext context, List<CheckFinding> findings)
    {
        for (int j = 0; j < surfaces.Count; j++)
        {
            var s = surfaces[j];
            string where = SavedSurfaces.Where(j, surfaces.Count);
            if (s.Coating is { Length: > 0 } coating && context.CoatingNames is { } names
                && !names.Contains(coating, StringComparer.OrdinalIgnoreCase))
                findings.Add(new(CheckSeverity.Warning, $"{where}: the coating '{coating}' is not in the lens's coating catalog."));
            foreach (var f in s.Flags ?? new List<OpticsCellFlagSettings>())
            {
                if (SurfaceFlags.IsKind(f, SavedSurfaces.SolveFlag))
                {
                    if (f.Column == SavedSurfaces.SemiDiameterColumn && f.Solve == ApertureTools.MaximumSolve) continue;
                    findings.Add(new(CheckSeverity.Warning, $"{where}, {f.Column}: a '{f.Solve}' solve is stored but solves are not evaluated in this build; the cell's value is traced."));
                }
                else if (SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag))
                    CheckPickup(surfaces, j, f, where, findings);
            }
        }
    }

    private static void CheckPickup(IReadOnlyList<OpticsSurfaceSettings> surfaces, int j, OpticsCellFlagSettings f, string where, List<CheckFinding> findings)
    {
        if (f.PickupSurface < 0 || f.PickupSurface >= surfaces.Count)
        {
            findings.Add(new(CheckSeverity.Warning, $"{where}, {f.Column}: the pickup's source surface {f.PickupSurface} does not exist."));
            return;
        }
        if (Value(surfaces[j], f.Column) is not double target || Value(surfaces[f.PickupSurface], f.PickupColumn) is not double source)
            return;   // text cells (material, coating) and unknown columns are not compared
        double expected = source * f.PickupScale + f.PickupOffset;
        if (expected == target || (double.IsInfinity(expected) && double.IsInfinity(target))) return;
        if (Math.Abs(expected - target) <= 1e-12 * Math.Max(1, Math.Abs(expected))) return;
        findings.Add(new(CheckSeverity.Warning, string.Create(CultureInfo.InvariantCulture,
            $"{where}, {f.Column}: picks up surface {f.PickupSurface}'s {f.PickupColumn} × {f.PickupScale:G8} + {f.PickupOffset:G8} = {expected:G10}, but holds {target:G10}; pickups are not evaluated in this build, the cell's value is traced.")));
    }

    /// <summary>A numeric cell's value in lens units (radius: infinity for a plane), or null.</summary>
    public static double? Value(OpticsSurfaceSettings s, string? column) => column switch
    {
        SavedSurfaces.RadiusColumn => SavedSurfaces.IsPlane(s) ? double.PositiveInfinity : s.Radius,
        SavedSurfaces.ThicknessColumn => s.ThicknessIsInfinite ? double.PositiveInfinity : s.Thickness,
        SavedSurfaces.SemiDiameterColumn => s.SemiDiameter,
        SavedSurfaces.ChipZoneColumn => s.ChipZone,
        SavedSurfaces.MechanicalSemiDiameterColumn => s.MechanicalSemiDiameter,
        SavedSurfaces.ConicColumn => s.Conic,
        SavedSurfaces.TceColumn => s.Tce,
        { } c when c.StartsWith("Par", StringComparison.Ordinal) && int.TryParse(c.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            => SavedSurfaces.Parameter(s, n),
        _ => null,
    };

    private static void TraceRays(SequentialLens lens, OpticalSystemData system, List<CheckFinding> findings)
    {
        int wavelengths = system.Wavelengths.Wavelengths.Count, fields = system.Fields.Points.Count;
        for (int j = 1; j < lens.Surfaces.Count; j++)
        {
            if (lens.Surfaces[j - 1].Medium.Material is not { } glass || lens.Surfaces[j - 1].IsMirror) continue;
            foreach (var w in system.Wavelengths.Wavelengths)
                if (!glass.Covers(w.Micrometers) && glass.ValidRange is { } r)
                    findings.Add(new(CheckSeverity.Warning, string.Create(CultureInfo.InvariantCulture,
                        $"{SavedSurfaces.Where(j - 1, lens.Surfaces.Count)}: {glass.Name} is defined from {r.Min:G6} to {r.Max:G6} µm, which does not cover {w.Micrometers:G6} µm.")));
        }
        RayLauncher launcher;
        try { launcher = new RayLauncher(lens, system); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            findings.Add(new(CheckSeverity.Error, $"The rays cannot be launched: {ex.Message}"));
            return;
        }
        IReadOnlyList<double> semiDiameters = SemiDiameterSizing.Compute(lens, system).Clear;
        var pupil = new (double Px, double Py, string Name)[] { (0, 0, "chief"), (0, 1, "marginal +y"), (0, -1, "marginal −y"), (1, 0, "marginal +x"), (-1, 0, "marginal −x") };
        for (int w = 0; w < wavelengths; w++)
        {
            SequentialTrace trace;
            try { trace = SequentialTrace.Create(lens, system, w, semiDiameters); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                findings.Add(new(CheckSeverity.Error, $"Wavelength {w + 1}: the lens cannot be traced: {ex.Message}"));
                continue;
            }
            for (int f = 0; f < fields; f++)
            {
                var (hx, hy) = system.Fields.Normalized(f);
                foreach (var (px, py, name) in pupil)
                {
                    var ray = trace.Trace(launcher.Launch(trace, hx, hy, px, py));
                    if (!ray.IsOk)
                        findings.Add(new(CheckSeverity.Warning,
                            $"Field {f + 1}, wavelength {w + 1}: the {name} ray stops at surface {ray.FailedAtSurface} ({RayStatuses.Label(ray.Status)})."));
                }
            }
        }
    }
}
