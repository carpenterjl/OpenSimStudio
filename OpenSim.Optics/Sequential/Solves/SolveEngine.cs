using OpenSim.Core.Numerics;
using OpenSim.Optics.Layout;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential.Solves;

/// <summary>
/// Applies solves to a <see cref="SequentialLens"/> (OP-29, docs/Optics-Conventions.md §19).
/// <para>
/// <b>Order.</b> Solves are evaluated "sequentially from the first surface to the image surface,
/// in this order: curvature, thickness, glass, clear semi-diameter or semi-diameter, conic,
/// parameters (in order left to right), TCE" (OpticStudio 2025 R2 help, "Restrictions"). Each
/// solve sees the lens with every earlier solve of the pass applied. With
/// <see cref="SolveOptions.Iterate"/> the pass repeats until a whole pass leaves every solved
/// cell where it found it (to <see cref="SolveOptions.RelativeTolerance"/>), so solves that
/// read later surfaces converge; a pass that changes nothing is a fixed point, because the
/// next pass would start from the same lens.
/// </para>
/// <para>
/// <b>A pure function.</b> <see cref="Apply"/> takes the lens as entered and returns a new one;
/// nothing is cached between calls. A cell driven by a solve is recomputed from the other cells
/// every time, whatever value it holds, and the material offset is applied to the glass as
/// entered, so applying the solves to an already solved lens gives the same lens. This is how
/// solves take part in the optimizer's derivatives (OP-29's plan text; the optimizer is not
/// built): the optimizer changes a variable cell of the lens as entered, calls
/// <see cref="Apply"/> again, and differentiates the merit of the solved lens, so every
/// solved cell follows the variable. <see cref="SolvedCells"/> lists the cells a solve set
/// drives, which must not be variables.
/// </para>
/// <para>
/// <b>Failures.</b> A solve that cannot be met (a ray parallel to the axis, equal indices, a
/// surface past its hemisphere, a real ray that misses) leaves its cell unchanged and reports
/// why, naming the surface, column and solve; the result then has <see cref="SolveResult.Succeeded"/>
/// false, and the passes stop. Nothing is clamped or guessed.
/// </para>
/// </summary>
public static class SolveEngine
{
    /// <summary>Applies <paramref name="solves"/> to <paramref name="lens"/> with
    /// <paramref name="system"/>; see the class remarks.</summary>
    public static SolveResult Apply(SequentialLens lens, OpticalSystemData system, IEnumerable<SurfaceSolve> solves,
        SolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(solves);
        options ??= new SolveOptions();
        var list = solves.ToList();
        var problems = Validate(lens, list, options);
        // Stable sort by cell: evaluation order.
        var ordered = list.Where(s => s is not null).Select((s, i) => (s, i)).OrderBy(p => p.s.Cell).ThenBy(p => p.i).Select(p => p.s).ToList();
        if (problems.Count > 0)
            return new SolveResult
            {
                Lens = lens,
                Statuses = ordered.Select(s => new SolveStatus { Solve = s, Error = "Not applied: the solve set has problems." }).ToArray(),
                Errors = problems,
            };

        var run = new Run(lens, system, options);
        var current = lens;
        int maxPasses = options.Iterate ? options.MaxPasses : 1;
        var statuses = new SolveStatus[ordered.Count];
        var changing = new List<SurfaceCell>();
        for (int pass = 1; pass <= maxPasses; pass++)
        {
            var errors = new List<string>();
            changing.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                var solve = ordered[i];
                var before = current;
                try
                {
                    (current, statuses[i]) = run.ApplyOne(current, solve);
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or ArithmeticException)
                {
                    string message = $"{Where(solve)}: {e.Message}";
                    statuses[i] = new SolveStatus { Solve = solve, Error = message };
                    errors.Add(message);
                    continue;
                }
                if (!SameCell(before, current, solve.Cell, options)) changing.Add(solve.Cell);
            }
            if (errors.Count > 0)
                return new SolveResult { Lens = current, Statuses = statuses, Errors = errors, Passes = pass };
            if (!options.Iterate || changing.Count == 0)
                return new SolveResult { Lens = current, Statuses = statuses, Errors = Array.Empty<string>(), Passes = pass };
        }
        return new SolveResult
        {
            Lens = current, Statuses = statuses, Passes = maxPasses,
            Errors = new[]
            {
                $"Solves did not converge in {maxPasses} passes; still changing: {string.Join("; ", changing)}. "
                + "Solves that depend on each other may have no common solution (for example a marginal ray angle solve on the last surface with an F/# aperture; use the entrance pupil diameter, as OpticStudio's \"Restrictions\" advises).",
            },
        };
    }

    /// <summary>The cells <paramref name="solves"/> drive, in evaluation order.</summary>
    public static IReadOnlyList<SurfaceCell> SolvedCells(IEnumerable<SurfaceSolve> solves)
    {
        ArgumentNullException.ThrowIfNull(solves);
        return solves.Select(s => s.Cell).OrderBy(c => c).ToArray();
    }

    /// <summary>
    /// Every problem of a solve set that needs no tracing, one actionable line each: surfaces
    /// and references out of range, two solves on one cell, pickups from a later cell
    /// ("pickup solves may only pickup values from prior surfaces, or from the same surface if
    /// the source column precedes the target column", "Restrictions"), a marginal ray height
    /// solve before the stop (also "Restrictions"), and parameters out of their ranges.
    /// </summary>
    public static IReadOnlyList<string> Validate(SequentialLens lens, IReadOnlyList<SurfaceSolve> solves, SolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(solves);
        var problems = new List<string>();
        if (lens.Surfaces is null || lens.Surfaces.Count < 3)
        {
            problems.Add("Solves: the lens needs the object surface, at least one surface and the image surface.");
            return problems;
        }
        int image = lens.ImageIndex, last = lens.LastSurfaceIndex, stop = lens.StopIndex;
        var seen = new Dictionary<SurfaceCell, SurfaceSolve>();
        for (int i = 0; i < solves.Count; i++)
        {
            var solve = solves[i];
            if (solve is null) { problems.Add($"Solves: entry {i} is missing."); continue; }
            int s = solve.Surface;
            string where = Where(solve);
            void Add(string text) => problems.Add($"{where}: {text}");
            void Finite(double v, string name) { if (!double.IsFinite(v)) Add($"the {name} must be a finite number (it is {v})."); }
            void Range(int v, int lo, int hi, string name)
            {
                if (v < lo || v > hi) Add($"the {name} must be a surface from {lo} to {hi} (it is {v}).");
            }

            // Which surfaces may hold the solve.
            var (min, max) = AllowedSurfaces(solve, last, image);
            if (s < min || s > max)
            {
                Add($"this solve can only be placed on surfaces {min} to {max} (the lens has surfaces 0 to {image}).");
                continue;
            }
            if (solve.Column == SolveColumn.Parameter && solve.ParameterIndex < 0)
                Add($"the parameter number must be 0 or more (it is {solve.ParameterIndex}).");
            if (!seen.TryAdd(solve.Cell, solve))
                Add($"the cell already has a {seen[solve.Cell].Name.ToLowerInvariant()} solve; a cell takes one solve.");

            switch (solve)
            {
                case MarginalRayAngleSolve m: Finite(m.Angle, "angle"); break;
                case ChiefRayAngleSolve c: Finite(c.Angle, "angle"); break;
                case ElementPowerSolve e:
                    Finite(e.Power, "power");
                    if (s < 2) Add("the element power solve sits on the second surface of an element, so it needs a surface number of 2 or more.");
                    break;
                case ConcentricWithSurfaceSolve c: Range(c.Of, 0, s - 1, "surface to be concentric to (it must precede this one)"); break;
                case ConcentricWithRadiusSolve c: Range(c.Of, 1, s - 1, "surface to be concentric with (it must precede this one)"); break;
                case FNumberSolve f:
                    Finite(f.FNumber, "F/#");
                    if (f.FNumber == 0) Add("the F/# must not be zero.");
                    break;
                case MarginalRayHeightSolve m:
                    Finite(m.Height, "height");
                    Finite(m.PupilZone, "pupil zone");
                    if (!(Math.Abs(m.PupilZone) <= 1)) Add($"the pupil zone must be between −1 and 1 (it is {m.PupilZone}).");
                    if (stop > 0 && s < stop)
                        Add($"a marginal ray height solve cannot be placed before the stop (surface {stop}): it would move the entrance pupil the marginal ray is defined by (OpticStudio's \"Restrictions\").");
                    break;
                case ChiefRayHeightSolve c: Finite(c.Height, "height"); break;
                case EdgeThicknessSolve e:
                    Finite(e.EdgeThickness, "edge thickness");
                    if (!double.IsFinite(e.RadialHeight) || e.RadialHeight < 0) Add($"the radial height must be zero (the mechanical semi-diameter) or positive (it is {e.RadialHeight}).");
                    break;
                case OpticalPathDifferenceSolve o:
                    Finite(o.Waves, "OPD");
                    if (!(Math.Abs(o.PupilZone) <= 1) || o.PupilZone == 0)
                        Add($"the pupil zone must be between −1 and 1 and not 0 (zone 0 is the chief ray, whose OPD is zero by definition); it is {o.PupilZone}.");
                    break;
                case PositionSolve p: Range(p.ReferenceSurface, 0, image, "reference surface"); Finite(p.Length, "length"); break;
                case CompensatorSolve c: Range(c.ReferenceSurface, 0, s - 1, "reference surface (it must precede this one)"); Finite(c.Sum, "sum"); break;
                case CenterOfCurvatureSolve c: Range(c.Of, 1, s, "surface whose centre of curvature is used (this one or an earlier one)"); break;
                case ModelGlassSolve g:
                    if (!double.IsFinite(g.Nd) || g.Nd <= 1) Add($"the model glass needs nd above 1 (it is {g.Nd}).");
                    if (!double.IsFinite(g.Vd) || g.Vd <= 0) Add($"the model glass needs a positive Vd (it is {g.Vd}).");
                    Finite(g.DeltaPgF, "ΔPgF");
                    break;
                case GlassOffsetSolve o: Finite(o.DeltaNd, "index offset"); Finite(o.DeltaVd, "Abbe offset"); break;
                case PickupSolve p: ValidatePickup(p, image, Add); break;
                case ParameterChiefRaySolve c:
                    if (c.Parameter < 0 || c.Parameter > 3) Add("the chief ray solve works only on the first four parameters (0 to 3) of a coordinate break.");
                    break;
            }
        }
        return problems;
    }

    private static void ValidatePickup(PickupSolve p, int image, Action<string> add)
    {
        if (p.FromSurface < 0 || p.FromSurface > image)
        {
            add($"the source surface must be from 0 to {image} (it is {p.FromSurface}).");
            return;
        }
        if (!double.IsFinite(p.Scale)) add($"the scale factor must be a finite number (it is {p.Scale}).");
        if (!double.IsFinite(p.Offset)) add($"the offset must be a finite number (it is {p.Offset}).");
        var source = p.Source;
        if (source.Column == SolveColumn.Parameter && source.Parameter < 0)
            add($"the source parameter number must be 0 or more (it is {source.Parameter}).");
        switch (p.Target)
        {
            case SolveColumn.Material:
            case SolveColumn.Tce:
                if (source.Column != p.Target) add($"a {SolveColumns.Label(p.Target)} pickup copies the same column of another surface; it cannot read the {SolveColumns.Label(source.Column, source.Parameter)}.");
                if (p.Scale != 1 || p.Offset != 0) add($"a {SolveColumns.Label(p.Target)} pickup copies the value; it takes no scale factor or offset.");
                break;
            case SolveColumn.Curvature:
                if (source.Column != SolveColumn.Curvature) add("a curvature pickup reads a curvature (the help does not say how a length becomes a curvature).");
                if (p.Offset != 0) add("a curvature pickup takes a scale factor but no offset (OpticStudio's \"Curvature Solves\").");
                break;
            default:
                if (source.Column is SolveColumn.Material or SolveColumn.Tce)
                    add($"a {SolveColumns.Label(p.Target)} pickup reads radius, thickness, conic, semi-diameter or a parameter (OpticStudio's column codes), not the {SolveColumns.Label(source.Column)}.");
                else if (source.Column == SolveColumn.Curvature)
                    add($"a {SolveColumns.Label(p.Target)} pickup cannot read a curvature (the help does not say how a curvature becomes a length).");
                if (p.Offset != 0 && p.Target is SolveColumn.SemiDiameter or SolveColumn.Conic)
                    add($"a {SolveColumns.Label(p.Target)} pickup takes a scale factor but no offset (OpticStudio's solve summary).");
                break;
        }
        if (source.CompareTo(p.Cell) >= 0)
            add($"the source ({source}) must come before the target in evaluation order: a prior surface, or this surface with an earlier column (OpticStudio's \"Restrictions\").");
    }

    private static (int Min, int Max) AllowedSurfaces(SurfaceSolve solve, int last, int image) => solve switch
    {
        MarginalRayAngleSolve or ChiefRayAngleSolve or MarginalRayNormalSolve or ChiefRayNormalSolve
            or AplanaticSolve or ElementPowerSolve or FNumberSolve => (1, last),
        ConcentricWithSurfaceSolve or ConcentricWithRadiusSolve => (1, image),
        MarginalRayHeightSolve or ChiefRayHeightSolve or EdgeThicknessSolve or OpticalPathDifferenceSolve
            or CenterOfCurvatureSolve or PupilPositionSolve => (1, last),
        PositionSolve or CompensatorSolve => (0, last),
        ModelGlassSolve or SubstituteGlassSolve or GlassOffsetSolve => (0, last),
        AutomaticSemiDiameterSolve or MaximumSemiDiameterSolve => (0, image),
        ParameterChiefRaySolve => (1, last),
        PickupSolve p => p.Target switch
        {
            SolveColumn.Curvature => (1, image),
            SolveColumn.Thickness => (0, last),
            SolveColumn.Material => (0, last),
            SolveColumn.SemiDiameter => (0, image),
            SolveColumn.Conic or SolveColumn.Parameter => (1, image),
            _ => (0, image),
        },
        // OP-99: a macro solve on a thickness stops before the image; the others reach it.
        ZplMacroSolve z => z.Target == SolveColumn.Thickness ? (0, last) : (z.Target == SolveColumn.Curvature ? 1 : 0, image),
        _ => throw new NotSupportedException($"The solve type {solve.GetType().Name} is not known to the solve engine."),
    };

    private static string Where(SurfaceSolve solve) =>
        $"Surface {solve.Surface}, {SolveColumns.Label(solve.Column, solve.ParameterIndex)}, {solve.Name.ToLowerInvariant()} solve";

    private static bool TryGetParameter(SequentialSurface s, int number, out double value)
    {
        if (s.Shape is IParameterizedShape p)
        {
            value = number < p.ParameterCount ? p.GetParameter(number) : 0;
            return number < p.ParameterCount;
        }
        return SurfaceTypes.TryGetParameter(s, number, out value);
    }

    private static bool SameCell(SequentialLens a, SequentialLens b, SurfaceCell cell, SolveOptions o)
    {
        var x = a.Surfaces[cell.Surface];
        var y = b.Surfaces[cell.Surface];
        if (ReferenceEquals(x, y)) return true;
        bool Close(double p, double q) => p == q || Math.Abs(p - q) <= o.RelativeTolerance * Math.Max(Math.Abs(p), Math.Abs(q)) + o.AbsoluteTolerance;
        switch (cell.Column)
        {
            case SolveColumn.Curvature: return Close(x.Curvature, y.Curvature);
            case SolveColumn.Thickness: return Close(x.Thickness, y.Thickness);
            case SolveColumn.Conic: return Close(x.Conic, y.Conic);
            case SolveColumn.Tce: return Close(x.ThermalExpansion, y.ThermalExpansion);
            case SolveColumn.SemiDiameter:
                return x.SemiDiameter is double p ? y.SemiDiameter is double q && Close(p, q) : y.SemiDiameter is null;
            case SolveColumn.Material:
                return x.IsMirror == y.IsMirror && x.Medium.Index == y.Medium.Index
                    && ReferenceEquals(x.Medium.Material, y.Medium.Material);
            case SolveColumn.Parameter:
                return TryGetParameter(x, cell.Parameter, out double px) && TryGetParameter(y, cell.Parameter, out double py) && Close(px, py);
            default: return false;
        }
    }

    /// <summary>
    /// The OPD, in primary waves, of the real ray at Py = <paramref name="pupilZone"/> of the
    /// on-axis field at the primary wavelength, as the optical path difference solve measures
    /// it: both the ray and the on-axis chief ray are carried from the last surface to the
    /// reference sphere centred on the chief ray's intercept with the image surface and passing
    /// through the centre of the paraxial exit pupil, and OPD = chief path − ray path
    /// (docs/Optics-Conventions.md §6), divided by the primary wavelength in the system's air
    /// (the optical paths are relative to that air, §13). Apertures are ignored. Throws when
    /// the exit pupil is at infinity or on the image, or when a ray fails.
    /// </summary>
    public static double OnAxisOpdWaves(SequentialLens lens, OpticalSystemData system, double pupilZone)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        return Run.OpdWaves(lens, system, paraxial, pupilZone);
    }

    private sealed class SolveFailure : InvalidOperationException
    {
        public SolveFailure(string message) : base(message) { }
    }

    /// <summary>One application: the system, the options, the lens as entered, and the
    /// materials made for it (so a pass that changes nothing leaves the same material object).</summary>
    private sealed class Run
    {
        private static readonly TraceOptions NoApertures = new() { IgnoreApertures = true };

        private readonly SequentialLens _entered;
        private readonly OpticalSystemData _system;
        private readonly SolveOptions _options;
        private readonly Dictionary<SurfaceSolve, OpticalMaterial> _materials = new();
        private (SequentialLens Lens, ParaxialSystem System)? _paraxial;
        private (SequentialLens Lens, SemiDiameterReport Report)? _sizing;

        public Run(SequentialLens entered, OpticalSystemData system, SolveOptions options)
        {
            _entered = entered;
            _system = system;
            _options = options;
        }

        private static SolveFailure Fail(string message) => new(message);

        private ParaxialSystem Paraxial(SequentialLens lens)
        {
            if (_paraxial is { } p && ReferenceEquals(p.Lens, lens)) return p.System;
            var system = ParaxialSystem.Create(lens.ToParaxialLens(), _system);
            _paraxial = (lens, system);
            return system;
        }

        private SemiDiameterReport Sizing(SequentialLens lens)
        {
            if (_sizing is { } s && ReferenceEquals(s.Lens, lens)) return s.Report;
            var report = SemiDiameterSizing.Compute(lens, _system, maxDegreeOfParallelism: 1);
            _sizing = (lens, report);
            return report;
        }

        private static SequentialLens With(SequentialLens lens, int j, SequentialSurface surface)
        {
            var surfaces = lens.Surfaces.ToArray();
            surfaces[j] = surface;
            return lens with { Surfaces = surfaces };
        }

        public (SequentialLens Lens, SolveStatus Status) ApplyOne(SequentialLens lens, SurfaceSolve solve)
        {
            int s = solve.Surface;
            var surface = lens.Surfaces[s];
            SolveStatus Done(double? value, string? note = null) => new() { Solve = solve, Value = value, Note = note };
            (SequentialLens, SolveStatus) Number(double value)
            {
                if (!double.IsFinite(value)) throw Fail($"the solve gives {value}, which is not a finite number.");
                return (With(lens, s, Write(surface, solve.Cell, value)), Done(value));
            }

            switch (solve)
            {
                // ---- Curvature ----
                case MarginalRayAngleSolve m:
                {
                    var p = Paraxial(lens);
                    return Number(Vertex(lens, s, CurvatureForSlope(lens, s, p, p.MarginalRay, m.Angle, "marginal"), Step(p, s)));
                }
                case ChiefRayAngleSolve c:
                {
                    var p = Paraxial(lens);
                    return Number(Vertex(lens, s, CurvatureForSlope(lens, s, p, Chief(p), c.Angle, "chief"), Step(p, s)));
                }
                case FNumberSolve f:
                {
                    // "The marginal ray angle exiting the surface is −1/2F" ("Curvature Solves").
                    var p = Paraxial(lens);
                    return Number(Vertex(lens, s, CurvatureForSlope(lens, s, p, p.MarginalRay, -1 / (2 * f.FNumber), "marginal"), Step(p, s)));
                }
                case MarginalRayNormalSolve:
                {
                    var p = Paraxial(lens);
                    return Number(Vertex(lens, s, NormalCurvature(lens, s, p.MarginalRay, "marginal"), 0));
                }
                case ChiefRayNormalSolve:
                {
                    var p = Paraxial(lens);
                    return Number(Vertex(lens, s, NormalCurvature(lens, s, Chief(p), "chief"), 0));
                }
                case AplanaticSolve:
                {
                    var p = Paraxial(lens);
                    return Number(Vertex(lens, s, AplanaticCurvature(lens, s, p), Step(p, s)));
                }
                case ElementPowerSolve e:
                {
                    var p = Paraxial(lens);
                    return Number(Vertex(lens, s, ElementPowerCurvature(lens, s, p, e.Power), Step(p, s)));
                }
                case ConcentricWithSurfaceSolve c:
                {
                    // Centre of curvature at the vertex of surface Of: R = z(Of) − z(s) = −Σ t.
                    double d = Sum(lens, c.Of, s - 1);
                    if (d == 0) throw Fail($"surface {c.Of} has its vertex on this surface's vertex, so the radius would be zero.");
                    return Number(-1 / d + 0.0);
                }
                case ConcentricWithRadiusSolve c:
                {
                    // Centre of curvature at that of surface Of: z(s) + R = z(Of) + R(Of).
                    var of = lens.Surfaces[c.Of];
                    if (of.IsIgnored) throw Fail($"surface {c.Of} is ignored, so it has no centre of curvature.");
                    if (of.Curvature == 0) return Number(0);   // both centres at infinity: a plane
                    double r = 1 / of.Curvature - Sum(lens, c.Of, s - 1);
                    if (r == 0) throw Fail($"the centre of curvature of surface {c.Of} lies on this surface's vertex, so the radius would be zero.");
                    return Number(1 / r);
                }

                // ---- Thickness ----
                case MarginalRayHeightSolve m when m.PupilZone == 0:
                {
                    var p = Paraxial(lens);
                    return Number(ThicknessForHeight(lens, s, p.MarginalRay, m.Height, "marginal"));
                }
                case MarginalRayHeightSolve m:
                {
                    // A real ray: its height on the next vertex plane z = t of this surface's frame,
                    // y = y_s + (t − z_s)·M/N, set to the target.
                    var hit = RealHit(lens, s, 0, m.PupilZone);
                    var d = hit.Direction;
                    if (d.Y == 0) throw Fail($"the real ray at Py = {m.PupilZone} leaves the surface parallel to the axis, so no thickness changes its height.");
                    return Number(hit.Position.Z + (m.Height - hit.Position.Y) * d.Z / d.Y);
                }
                case ChiefRayHeightSolve c:
                {
                    var p = Paraxial(lens);
                    return Number(ThicknessForHeight(lens, s, Chief(p), c.Height, "chief"));
                }
                case EdgeThicknessSolve e:
                {
                    // E = Z(s+1) − Z(s) + T, both sags at the same height ("Edge Thickness").
                    double r = e.RadialHeight > 0 ? e.RadialHeight : Sizing(lens).Mechanical[s];
                    double zs = Sag(lens, s, r), zn = Sag(lens, s + 1, r);
                    return Number(e.EdgeThickness - zn + zs);
                }
                case OpticalPathDifferenceSolve o:
                    return Number(ThicknessByRoot(lens, s, l => OpdWaves(l, _system, Paraxial(l), o.PupilZone) - o.Waves,
                        $"an OPD of {o.Waves:g6} waves at Py = {o.PupilZone:g6}"));
                case PositionSolve p:
                {
                    int r = p.ReferenceSurface;
                    double t = r == s ? p.Length
                        : r < s ? p.Length - Sum(lens, r, s - 1)
                        : p.Length - Sum(lens, s + 1, r - 1);
                    return Number(t);
                }
                case CompensatorSolve c:
                    return Number(c.Sum - lens.Surfaces[c.ReferenceSurface].Thickness);
                case CenterOfCurvatureSolve c:
                {
                    var of = lens.Surfaces[c.Of];
                    if (of.IsIgnored) throw Fail($"surface {c.Of} is ignored, so it has no centre of curvature.");
                    if (of.Curvature == 0) throw Fail($"surface {c.Of} is a plane; its centre of curvature is at infinity.");
                    return Number(1 / of.Curvature - Sum(lens, c.Of, s - 1));
                }
                case PupilPositionSolve:
                    return Number(PupilThickness(lens, s, Paraxial(lens)));

                // ---- Material ----
                case ModelGlassSolve g:
                {
                    var fit = _options.ModelGlass
                        ?? throw Fail("a model glass needs a model-glass fit trained on the active catalogs (SolveOptions.ModelGlass).");
                    if (!_materials.TryGetValue(solve, out var glass))
                        _materials[solve] = glass = fit.Create(g.Nd, g.Vd, g.DeltaPgF,
                            FormattableString.Invariant($"MODEL {g.Nd:g7}/{g.Vd:g5}/{g.DeltaPgF:g4}"));
                    return (With(lens, s, surface with { Medium = ParaxialMedium.Of(glass), IsMirror = false }), Done(null));
                }
                case SubstituteGlassSolve g:
                {
                    var entered = _entered.Surfaces[s];
                    if (entered.IsMirror || entered.Medium.Material is null)
                        throw Fail("glass substitution needs a catalog glass on the surface.");
                    if (g.Catalog.Length > 0 && _options.Catalogs is { } catalogs
                        && !catalogs.Any(c => string.Equals(c.Name, g.Catalog, StringComparison.OrdinalIgnoreCase)))
                        throw Fail($"the catalog '{g.Catalog}' is not among the active catalogs ({string.Join(", ", catalogs.Select(c => c.Name))}).");
                    // OP-59: at least one glass must meet the Glass Substitution Template. OpticStudio
                    // removes the solve when none does ("Material Solves"); here it fails, naming why.
                    if (_options.Catalogs is { } active)
                    {
                        var wavelengths = _system.Wavelengths.Wavelengths.Select(w => w.Micrometers).ToArray();
                        var pool = GlassSubstitutionTemplate.Candidates(active, wavelengths, _options.SubstitutionTemplate, g.Catalog.Length > 0 ? g.Catalog : null);
                        if (pool.Count == 0)
                            throw Fail($"no glass of {(g.Catalog.Length > 0 ? $"the catalog '{g.Catalog}'" : "the active catalogs")} is free for substitution, valid at every wavelength"
                                + $"{(_options.SubstitutionTemplate is { UseTemplate: true } ? " and within the Glass Substitution Template" : "")}.");
                        return (lens, Done(null, $"A flag for glass substitution (GlassSubstitution): the glass is unchanged; {pool.Count} glass{(pool.Count == 1 ? "" : "es")} may replace it."));
                    }
                    return (lens, Done(null, "A flag for glass substitution (GlassSubstitution): the glass is unchanged."));
                }
                case GlassOffsetSolve o:
                {
                    var entered = _entered.Surfaces[s];
                    if (entered.IsMirror) throw Fail("a mirror has no glass to offset.");
                    var material = entered.Medium.Material
                        ?? throw Fail("an index offset needs a catalog glass on the surface; a constant index is changed by editing it.");
                    if (!_materials.TryGetValue(solve, out var glass))
                        _materials[solve] = glass = GlassOffsets.Apply(material, o.DeltaNd, o.DeltaVd);
                    return (With(lens, s, surface with { Medium = ParaxialMedium.Of(glass), IsMirror = false }), Done(null));
                }

                // ---- Semi-diameter ----
                case AutomaticSemiDiameterSolve:
                    return (With(lens, s, surface with { SemiDiameter = null }), Done(null));
                case MaximumSemiDiameterSolve:
                {
                    var probe = With(lens, s, surface with { SemiDiameter = null });
                    double value = Sizing(probe).Clear[s];
                    var (solved, status) = Number(value);
                    return (solved, status with { Note = "One configuration: the maximum over configurations is the automatic value (multiple configurations, OP-30, are not built)." });
                }

                // ---- Pickups ----
                case PickupSolve p when p.Target == SolveColumn.Material:
                {
                    var from = lens.Surfaces[p.FromSurface];
                    return (With(lens, s, surface with { Medium = from.Medium, IsMirror = from.IsMirror }), Done(null));
                }
                case PickupSolve p:
                    return Number(p.Offset + p.Scale * Read(lens, p.Source));

                case ParameterChiefRaySolve:
                    throw new NotSupportedException("the chief ray parameter solve is not built in this program (it needs a real chief ray solved through the coordinate break's decenter or tilt).");

                // OP-99: the ZPL macro solve (Scripting/Zpl/ZplMacroSolves.cs).
                case ZplMacroSolve z:
                    return Number(Scripting.Zpl.ZplMacroSolves.Evaluate(lens, _system, z));

                default:
                    throw new NotSupportedException($"the solve type {solve.GetType().Name} is not known to the solve engine.");
            }
        }

        // ---- Cells ----

        private double Read(SequentialLens lens, SurfaceCell cell)
        {
            var s = lens.Surfaces[cell.Surface];
            return cell.Column switch
            {
                SolveColumn.Curvature => s.Curvature,
                SolveColumn.Thickness => s.Thickness,
                SolveColumn.Conic => s.Conic,
                SolveColumn.Tce => s.ThermalExpansion,
                SolveColumn.SemiDiameter => s.SemiDiameter ?? Sizing(lens).Clear[cell.Surface],
                SolveColumn.Parameter => GetParameter(s, cell),
                _ => throw Fail($"the {SolveColumns.Label(cell.Column)} is not a number."),
            };
        }

        private static SequentialSurface Write(SequentialSurface s, SurfaceCell cell, double value) => cell.Column switch
        {
            SolveColumn.Curvature => s with { Curvature = value },
            SolveColumn.Thickness => s with { Thickness = value },
            SolveColumn.Conic => s with { Conic = value },
            SolveColumn.Tce => s with { ThermalExpansion = value },
            SolveColumn.SemiDiameter => value >= 0 ? s with { SemiDiameter = value }
                : throw Fail($"the solve gives a semi-diameter of {value:g6} m; it must be zero or positive."),
            SolveColumn.Parameter => SetParameter(s, cell, value),
            _ => throw Fail($"the {SolveColumns.Label(cell.Column)} is not a number."),
        };

        /// <summary>A parameter through the shape's <see cref="IParameterizedShape"/>, or else
        /// through the registry of saved surface types (<see cref="SurfaceTypes"/>: a registered
        /// shape's parameter columns, a coordinate break's six values), TC-6.</summary>
        private static double GetParameter(SequentialSurface s, SurfaceCell cell)
        {
            if (s.Shape is IParameterizedShape shape)
            {
                if (cell.Parameter >= shape.ParameterCount)
                    throw Fail($"surface {cell.Surface} ({s.Shape!.TypeName}) has parameters 0 to {shape.ParameterCount - 1}; there is no parameter {cell.Parameter}.");
                return shape.GetParameter(cell.Parameter);
            }
            if (SurfaceTypes.TryGetParameter(s, cell.Parameter, out double value)) return value;
            throw Fail(NoParameter(s, cell));
        }

        private static SequentialSurface SetParameter(SequentialSurface s, SurfaceCell cell, double value)
        {
            if (s.Shape is IParameterizedShape shape)
            {
                if (cell.Parameter >= shape.ParameterCount)
                    throw Fail($"surface {cell.Surface} ({s.Shape!.TypeName}) has parameters 0 to {shape.ParameterCount - 1}; there is no parameter {cell.Parameter}.");
                return s with { Shape = shape.WithParameter(cell.Parameter, value) };
            }
            if (!SurfaceTypes.TryGetParameter(s, cell.Parameter, out _)) throw Fail(NoParameter(s, cell));
            return SurfaceTypes.WithParameter(s, cell.Parameter, value);
        }

        private static string NoParameter(SequentialSurface s, SurfaceCell cell)
        {
            string type = s.IsCoordinateBreak ? SurfaceTypes.CoordinateBreak : s.Shape?.TypeName ?? "Standard";
            int count = SurfaceTypes.ParameterCount(s);
            return count == 0
                ? $"surface {cell.Surface} ({type}) has no parameters a solve can use."
                : $"surface {cell.Surface} ({type}) has no parameter {cell.Parameter}.";
        }

        /// <summary>Σ t_j for j = first … last (0 when last &lt; first); fails on an infinite one.</summary>
        private static double Sum(SequentialLens lens, int first, int last)
        {
            double sum = 0;
            for (int j = first; j <= last; j++) sum += lens.Surfaces[j].Thickness;
            if (!double.IsFinite(sum)) throw Fail($"the thicknesses of surfaces {first} to {last} do not add to a finite length (the object is at infinity).");
            return sum;
        }

        // ---- Paraxial rays ----

        private static ParaxialRay Chief(ParaxialSystem p)
        {
            if (p.FullField == 0)
                throw Fail("every field is on the axis, so the paraxial chief ray is zero; add an off-axis field.");
            return p.ChiefRay;
        }

        /// <summary>A value is zero when it is within 2⁻⁴⁰ of the largest entry of its kind
        /// along the ray (rounding in the trace, not a real height or slope).</summary>
        private static bool Negligible(double value, IReadOnlyList<double> along)
        {
            double scale = 0;
            for (int j = 1; j < along.Count; j++) if (double.IsFinite(along[j])) scale = Math.Max(scale, Math.Abs(along[j]));
            return Math.Abs(value) <= scale * Math.ScaleB(1.0, -40);
        }

        private static void RequireNotIgnored(SequentialLens lens, int s)
        {
            if (lens.Surfaces[s].IsIgnored) throw Fail("the surface is ignored, so its curvature has no effect on rays.");
        }

        /// <summary>
        /// The curvature that makes the ray leave with slope u′. Paraxial refraction with signed
        /// indices (Conventions §11) is ν′ = ν − y·c·(n′ − n), ν = n·u; so
        /// c = (ν − n′·u′)/(y·(n′ − n)). The height at the surface does not depend on its own
        /// curvature.
        /// </summary>
        /// <summary>The signed index step n′ − n at surface <paramref name="s"/>.</summary>
        private static double Step(ParaxialSystem p, int s) => p.SignedIndices[s] - p.SignedIndices[s - 1];

        /// <summary>
        /// The vertex curvature to enter so the surface acts paraxially as a surface of curvature
        /// <paramref name="paraxialCurvature"/> (TC-6). The curvature solves above find the
        /// curvature the paraxial engine must see; a surface with a <see cref="SurfaceShape"/>
        /// adds its own first-order action (an asphere's 2·α1, a thin-lens power) through
        /// <see cref="SurfaceShape.ParaxialEquivalent"/>, which is linear in the vertex curvature.
        /// The engine sees c·Δn + φ_thin (ParaxialSystem), so with <paramref name="indexStep"/> =
        /// Δn the target is c_eff + φ_thin/Δn; with 0 (the normal solves, which are geometric)
        /// it is c_eff alone. The map is evaluated at two curvatures and inverted; a shape whose
        /// paraxial action does not change with the curvature, or acts by a matrix, fails here.
        /// </summary>
        private static double Vertex(SequentialLens lens, int s, double paraxialCurvature, double indexStep)
        {
            var surface = lens.Surfaces[s];
            if (surface.Shape is not { } shape) return paraxialCurvature;
            ParaxialShapeEquivalent At(double c) => shape.ParaxialEquivalent(surface with { Curvature = c });
            double Seen(ParaxialShapeEquivalent e) => indexStep == 0 ? e.Curvature : e.Curvature + e.ThinLensPower / indexStep;
            var e0 = At(0);
            if (e0.SlopeMatrix is not null)
                throw Fail($"the {shape.TypeName} surface acts on paraxial rays by its ABCD matrix, so its curvature cannot be solved.");
            double f0 = Seen(e0), slope = Seen(At(1)) - f0;
            if (slope == 0 || !double.IsFinite(slope))
                throw Fail($"the {shape.TypeName} surface's paraxial action does not depend on its curvature, so this solve cannot set it.");
            return (paraxialCurvature - f0) / slope;
        }

        private static double CurvatureForSlope(SequentialLens lens, int s, ParaxialSystem p, ParaxialRay ray, double slope, string name)
        {
            RequireNotIgnored(lens, s);
            double n = p.SignedIndices[s - 1], n1 = p.SignedIndices[s];
            if (n1 == n) throw Fail($"the indices before and after the surface are equal ({Math.Abs(n):g6}), so its curvature cannot bend the {name} ray.");
            double y = ray.Heights[s];
            if (Negligible(y, ray.Heights)) throw Fail($"the paraxial {name} ray crosses the axis at this surface, so no curvature changes its angle.");
            return (ray.ReducedSlopes[s - 1] - n1 * slope) / (y * (n1 - n));
        }

        /// <summary>
        /// The surface normal to the ray: the incident ray, y(z) = y + u·z from the vertex, passes
        /// through the centre of curvature z = R. So y + u·R = 0 and c = −u/y; a ray parallel to
        /// the axis gives a plane.
        /// </summary>
        private static double NormalCurvature(SequentialLens lens, int s, ParaxialRay ray, string name)
        {
            RequireNotIgnored(lens, s);
            double y = ray.Heights[s];
            if (Negligible(y, ray.Heights)) throw Fail($"the paraxial {name} ray crosses the axis at this surface, so every curvature is normal to it.");
            return -ray.Slopes[s - 1] / y + 0.0;
        }

        /// <summary>
        /// The aplanatic curvature for the marginal ray. The condition is u′/n′ = u/n with signed
        /// indices: then ν′ = n′u′ = n′²u/n, and from ν′ = ν − y·c·(n′ − n),
        /// c·y·(n′ − n) = u·(n² − n′²)/n, so c = −u·(n + n′)/(n·y). This puts the axial object
        /// point (where the incident ray meets the axis, l = −y/u from the vertex) at
        /// l = R·(n + n′)/n, i.e. R·n′/n from the centre of curvature, and its image at R·n/n′ from
        /// the centre: the pair of points a sphere images without spherical aberration at any
        /// aperture, which the gate test checks with real rays. A mirror (n′ = −n) gives a plane.
        /// </summary>
        private static double AplanaticCurvature(SequentialLens lens, int s, ParaxialSystem p)
        {
            RequireNotIgnored(lens, s);
            var ray = p.MarginalRay;
            double n = p.SignedIndices[s - 1], n1 = p.SignedIndices[s];
            double y = ray.Heights[s];
            if (Negligible(y, ray.Heights)) throw Fail("the paraxial marginal ray crosses the axis at this surface, so no curvature makes it aplanatic there.");
            return -ray.Slopes[s - 1] * (n + n1) / (n * y) + 0.0;
        }

        /// <summary>
        /// The element power solve: φ = φ₁ + c₂(n₃ − n₂)·(1 − φ₁·t₂/n₂) with φ₁ = c₁(n₂ − n₁) the
        /// power of the surface before ("Curvature Solves", rearranged), signed indices as the
        /// paraxial engine uses them, primary wavelength. So c₂ = (φ − φ₁)/((n₃ − n₂)(1 − φ₁t₂/n₂)).
        /// </summary>
        private static double ElementPowerCurvature(SequentialLens lens, int s, ParaxialSystem p, double power)
        {
            RequireNotIgnored(lens, s);
            double phi1 = p.Powers[s - 1];
            double n2 = p.SignedIndices[s - 1], n3 = p.SignedIndices[s];
            if (n3 == n2) throw Fail($"the indices before and after the surface are equal ({Math.Abs(n2):g6}), so its curvature cannot set the element's power (OpticStudio ignores the solve here).");
            double t = lens.Surfaces[s - 1].Thickness;
            double reach = 1 - phi1 * t / n2;
            if (reach == 0) throw Fail($"surface {s - 1} focuses at this surface's vertex (1 − φ₁t/n = 0), so this surface's curvature cannot change the element's power.");
            return (power - phi1) / ((n3 - n2) * reach);
        }

        /// <summary>The thickness that brings the ray to height h on the next vertex plane:
        /// y′ = y + t·u (the transfer of Conventions §11 with u = ν/n), so t = (h − y)/u.</summary>
        private static double ThicknessForHeight(SequentialLens lens, int s, ParaxialRay ray, double height, string name)
        {
            double u = ray.Slopes[s];
            if (Negligible(u, ray.Slopes)) throw Fail($"the paraxial {name} ray leaves the surface parallel to the axis, so no thickness changes its height.");
            return (height - ray.Heights[s]) / u;
        }

        /// <summary>
        /// The pupil position in the space after the surface: where a chief ray crosses the
        /// axis, t = −ȳ/ū. The help traces real differential rays about the central field's chief
        /// ray; on a rotationally symmetric lens with no tilts (all this program has until OP-21)
        /// that chief ray is the axis and the differential ray about it is the paraxial chief
        /// ray, so the unit paraxial chief ray gives the same position. It is traced here
        /// whatever the fields: through the entrance-pupil centre with unit slope, or parallel
        /// to the axis when the entrance pupil is at infinity.
        /// </summary>
        private static double PupilThickness(SequentialLens lens, int s, ParaxialSystem p)
        {
            var chief = p.EntrancePupilAtInfinity ? p.Trace(1, 0) : p.Trace(-p.EntrancePupilPosition!.Value, p.SignedIndices[0]);
            double u = chief.Slopes[s];
            if (Negligible(u, chief.Slopes)) throw Fail("the chief ray leaves the surface parallel to the axis, so the pupil of that space is at infinity.");
            return -chief.Heights[s] / u;
        }

        // ---- Real rays ----

        private SurfaceHit RealHit(SequentialLens lens, int s, double hy, double py)
        {
            var trace = SequentialTrace.Create(lens, _system);
            var launcher = new RayLauncher(lens, _system);
            var path = trace.TracePath(launcher.Launch(trace, 0, hy, 0, py), NoApertures);
            foreach (var hit in path.Hits)
                if (hit.Surface == s && hit.Status == RayStatus.Ok) return hit;
            throw Fail($"the real ray at Hy = {hy:g6}, Py = {py:g6} did not pass surface {s}: {RayStatuses.Label(path.Status)} at surface {path.FailedAtSurface}.");
        }

        private static double Sag(SequentialLens lens, int j, double r)
        {
            var s = lens.Surfaces[j];
            double z = s.IsIgnored ? 0 : s.Shape is { } shape ? shape.Sag(0, r, s) : new ConicSag(s.Curvature, s.Conic).SagAtRadius(r);
            if (!double.IsFinite(z))
                throw Fail($"surface {j} is not defined at a radial height of {r:g6} m (beyond its hemisphere or the edge of its shape), so the edge thickness has no meaning there.");
            return z;
        }

        /// <summary>
        /// The thickness of surface <paramref name="s"/> at which <paramref name="residual"/> is
        /// zero, for solves with no closed form: steps of growing size either side of the
        /// present thickness until the residual changes sign, then Brent's method to the last
        /// bit (<see cref="RootFinding.Brent"/>). Fails when no sign change is found.
        /// </summary>
        private static double ThicknessByRoot(SequentialLens lens, int s, Func<SequentialLens, double> residual, string what)
        {
            var surface = lens.Surfaces[s];
            double t0 = surface.Thickness;
            double f0 = residual(lens);   // throws with its own message when the lens cannot be evaluated
            if (f0 == 0) return t0;
            if (double.IsNaN(f0)) throw Fail($"{what} cannot be evaluated at the present thickness.");
            double F(double t)
            {
                try { return residual(With(lens, s, surface with { Thickness = t })); }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException) { return double.NaN; }
            }
            double scale = Math.Max(Math.Abs(t0), 1e-3);
            double h = scale * 1e-6, limit = scale * 1e4;
            bool up = true, down = true;
            while (h <= limit && (up || down))
            {
                foreach (int side in new[] { 1, -1 })
                {
                    if (side > 0 ? !up : !down) continue;
                    double t = t0 + side * h, f = F(t);
                    if (double.IsNaN(f)) { if (side > 0) up = false; else down = false; continue; }
                    if ((f > 0) != (f0 > 0) || f == 0)
                        return RootFinding.Brent(F, t0, t);
                }
                h *= 2;
            }
            throw Fail($"no thickness between {t0 - h / 2:g6} and {t0 + h / 2:g6} m gives {what} (or rays fail before one is found).");
        }

        /// <summary>See <see cref="OnAxisOpdWaves"/>.</summary>
        public static double OpdWaves(SequentialLens lens, OpticalSystemData system, ParaxialSystem paraxial, double zone)
        {
            if (paraxial.ExitPupilAtInfinity || paraxial.ExitPupilPosition is not double zx)
                throw Fail("the exit pupil is at infinity, so there is no reference sphere (a plane reference is OP-35's).");
            var trace = SequentialTrace.Create(lens, system);
            var launcher = new RayLauncher(lens, system);
            int last = lens.LastSurfaceIndex;
            var chief = trace.TracePath(launcher.Launch(trace, 0, 0, 0, 0), NoApertures);
            var ray = trace.TracePath(launcher.Launch(trace, 0, 0, 0, zone), NoApertures);
            if (!chief.Final.IsOk) throw Fail($"the on-axis chief ray fails: {RayStatuses.Label(chief.Status)} at surface {chief.FailedAtSurface}.");
            if (!ray.Final.IsOk) throw Fail($"the on-axis ray at Py = {zone:g6} fails: {RayStatuses.Label(ray.Status)} at surface {ray.FailedAtSurface}.");

            var place = trace.GlobalPlacement(last);
            var centre = chief.Hits[chief.Hits.Count - 1].GlobalPosition;                        // the chief ray's image point
            var pupil = place.Apply(new Vector3D(0, 0, zx));                   // centre of the exit pupil
            double radius2 = (pupil - centre).LengthSquared;
            if (radius2 == 0) throw Fail("the exit pupil lies on the image point, so the reference sphere has no radius.");
            double index = trace.Indices[last];

            double PathToSphere(RayPath path)
            {
                var hit = path.Hits.First(h => h.Surface == last);
                var q = hit.GlobalPosition;
                var d = place.ApplyDirection(hit.Direction);
                // |q + s·d − centre|² = ρ²: s² + 2b·s + (|w|² − ρ²) = 0, w = q − centre.
                var w = q - centre;
                double b = Vector3D.Dot(w, d), c = w.LengthSquared - radius2;
                double disc = b * b - c;
                if (disc < 0) throw Fail("a ray misses the reference sphere.");
                double root = Math.Sqrt(disc);
                double best = double.NaN, bestDistance = double.PositiveInfinity;
                foreach (double s in new[] { -b - root, -b + root })
                {
                    var x = q + d * s;
                    // The sheet of the sphere on the exit pupil's side of the centre.
                    if (Vector3D.Dot(x - centre, pupil - centre) <= 0) continue;
                    double distance = (x - pupil).Length;
                    if (distance < bestDistance) { best = s; bestDistance = distance; }
                }
                if (double.IsNaN(best)) throw Fail("a ray meets the reference sphere only on the far side of the image point.");
                return hit.OpticalPath + index * best;
            }

            double opd = OpticsConventions.OpticalPathDifference(PathToSphere(chief), PathToSphere(ray));
            return OpticsConventions.InWaves(opd, OpticsConventions.MicrometersToMeters(trace.AirMicrometers));
        }
    }
}
