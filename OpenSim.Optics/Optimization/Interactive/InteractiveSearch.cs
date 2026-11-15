using OpenSim.Core.Model;
using OpenSim.Core.Numerics.Optimization;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization.Interactive;

// OP-60: what Quick Focus and Quick Adjust share (docs/Optics-Conventions.md §44).

/// <summary>
/// The one-variable search of Quick Focus and Quick Adjust: the variable is x₀ + t·h with h a
/// first step chosen by the caller, and <see cref="BrentMinimizer.LineSearch"/> finds the t of
/// the lowest criterion (unit steps either way from t = 0, the bracket widened by the golden ratio
/// while the criterion falls, then Brent's localmin). A trial value whose criterion cannot be
/// computed (a ray fails) counts as +∞, a wall the search turns back from; the start must have a
/// value. The search stops when Brent's bracket around the best point is no wider than
/// 2·(1.49e-8·|t| + 1e-11) on either side, so the minimum of a unimodal criterion lies within
/// <see cref="Tolerance"/> of the answer.
/// </summary>
internal static class InteractiveSearch
{
    /// <summary>The search range in first steps either way: wide enough never to bind in practice
    /// (the bracket stops growing at the first rise or failed trial).</summary>
    public const double Span = 1e6;

    public sealed record Outcome(double T, double Value, double StartValue, int Evaluations, int Failed, string? FirstFailure);

    public static Outcome Minimize(Func<double, (double Value, string? Failure)> criterion, string who, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var (v0, why0) = criterion(0);
        int evaluations = 1, failed = 0;
        string? first = null;
        if (!double.IsFinite(v0))
            throw new InvalidOperationException($"{who}: the criterion has no value for the system as it is, so there is nothing to start from: {why0}");
        double F(double t)
        {
            cancellation.ThrowIfCancellationRequested();
            evaluations++;
            var (v, why) = criterion(t);
            if (double.IsFinite(v)) return v;
            failed++;
            first ??= why;
            return double.PositiveInfinity;
        }
        var (t, value) = BrentMinimizer.LineSearch(F, -Span, Span, v0);
        if (!(value <= v0)) (t, value) = (0, v0);
        return new Outcome(t, value, v0, evaluations, failed, first);
    }

    /// <summary>How far the minimum of a unimodal criterion can lie from the answer t (in the
    /// variable's own unit, step h): Brent's localmin stops when the bracket reaches no further
    /// than 2·tol from the best point, tol = 1.49e-8·|t| + 1e-11 (in t).</summary>
    public static double Tolerance(double t, double step) =>
        2 * (BrentMinimizer.LineSearchRelativeTolerance * Math.Abs(t) + BrentMinimizer.LineSearchAbsoluteTolerance) * Math.Abs(step);

    /// <summary>The length scale of a lens for first steps: the sum of its finite thicknesses from
    /// surface 1 to the one before the image, or 1 mm when that is zero.</summary>
    public static double LengthScale(SequentialLens lens)
    {
        double sum = 0;
        for (int j = 1; j <= lens.LastSurfaceIndex; j++)
            if (double.IsFinite(lens.Surfaces[j].Thickness)) sum += Math.Abs(lens.Surfaces[j].Thickness);
        return sum > 0 ? sum : 1e-3;
    }

    /// <summary>The field rows with a positive weight and their weights.</summary>
    public static List<(int Row, double Weight)> WeightedFields(OpticalSystemData system, string who)
    {
        var list = new List<(int, double)>();
        for (int f = 0; f < system.Fields.Points.Count; f++)
            if (system.Fields.Points[f].Weight > 0) list.Add((f, system.Fields.Points[f].Weight));
        if (list.Count == 0) throw new ArgumentException($"{who}: every field has weight zero, so there is nothing to average.");
        return list;
    }

    /// <summary>√(Σ w_f v_f² / Σ w_f): the weighted RMS over fields of the fields' RMS values,
    /// which equals the RMS of all rays pooled with each field's pupil weights normalised; a
    /// single field returns its own value unchanged.</summary>
    public static double CombineFields(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        if (values.Count == 1) return values[0];
        double sw = 0, s = 0;
        for (int i = 0; i < values.Count; i++) { sw += weights[i]; s += weights[i] * values[i] * values[i]; }
        return Math.Sqrt(s / sw);
    }
}

/// <summary>The project record side of Quick Focus and Quick Adjust: the current
/// configuration's solved lens, the check that the adjusted cell is the user's own (no solve,
/// pickup or multi-configuration operand drives it), and writing the new value into the cell.</summary>
internal static class InteractiveProject
{
    public sealed record Built(SequentialLens Lens, OpticalSystemData System, LensUnit Unit, OpticsSettings Current, List<string> Notes);

    public static Built Build(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, string who)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        if (OpticsModes.TryParseSavedName(optics.Mode, out var mode) && mode == OpticsMode.NonSequential)
            throw new InvalidOperationException($"{who}: the system is in non-sequential mode; NSC Quick Focus and the non-sequential tools are not built. Switch to sequential mode.");
        var current = MultiConfiguration.CurrentSettings(optics, catalogs);
        var solution = ProjectLens.Solve(current, catalogs)
            ?? throw new InvalidOperationException($"{who}: the lens has no surfaces.");
        var lens = solution.LensOrThrow();
        if (optics.Surfaces is null || optics.Surfaces.Count != lens.Surfaces.Count)
            throw new InvalidOperationException($"{who}: the lens has {lens.Surfaces.Count} surfaces but the record {optics.Surfaces?.Count ?? 0}.");
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(current.SystemData, notes);
        if (system.Validate() is { Count: > 0 } invalid)
            throw new ArgumentException($"{who}: the system data are not valid: {string.Join(" ", invalid)}");
        if (MultiConfiguration.ConfigurationCount(optics) > 1)
            notes.Add($"Evaluated in the current configuration ({MultiConfiguration.CurrentConfiguration(optics)}) only.");
        return new Built(lens, system, solution.Unit, current, notes);
    }

    /// <summary>Refuses a cell that a solve or pickup sets, or that the multi-configuration table
    /// sets in the current configuration: the new value would be overwritten.</summary>
    public static void CheckOwnCell(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, int surface, string column, string who)
    {
        var s = optics.Surfaces![surface];
        string where = SavedSurfaces.Where(surface, optics.Surfaces.Count);
        var current = MultiConfiguration.CurrentSettings(optics, catalogs);
        var solution = ProjectLens.Solve(current, catalogs);
        if (solution?.Solves.FirstOrDefault(c => c.Surface == surface && c.Column == column) is { } cell)
            throw new InvalidOperationException($"{who}: the {column.ToLowerInvariant()} of {where} is set by a {cell.Solve.Name.ToLowerInvariant()} solve, which would overwrite the adjusted value. Remove the solve first.");
        OpticsSettings probe;
        bool kept;
        if (column == SavedSurfaces.ThicknessColumn)
        {
            if (s.ThicknessIsInfinite)
                throw new InvalidOperationException($"{who}: the thickness of {where} is infinite; give it a finite value first.");
            double value = s.Thickness + 1;
            probe = WithSurface(optics, surface, s with { Thickness = value });
            kept = MultiConfiguration.CurrentSettings(probe, catalogs).Surfaces![surface].Thickness == value;
        }
        else
        {
            double value = s.Radius is double r ? 2 * r : 100;
            probe = WithSurface(optics, surface, s with { Radius = value });
            kept = MultiConfiguration.CurrentSettings(probe, catalogs).Surfaces![surface].Radius == value;
        }
        if (!kept)
            throw new InvalidOperationException($"{who}: the {column.ToLowerInvariant()} of {where} is set by the multi-configuration table in the current configuration, so the Lens Data Editor's value is not the one traced. Adjust it in the Multi-Configuration Editor instead.");
    }

    public static OpticsSettings WithSurface(OpticsSettings optics, int surface, OpticsSurfaceSettings s)
    {
        var surfaces = optics.Surfaces!.ToList();
        surfaces[surface] = s;
        return optics with { Surfaces = surfaces };
    }
}
