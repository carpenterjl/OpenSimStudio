using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>One of the six axis-aligned faces of the CFD domain box.</summary>
public enum BoxFace { XMin, XMax, YMin, YMax, ZMin, ZMax }

/// <summary>
/// What a CFD domain-box face does to the flow.
/// </summary>
public enum FlowFaceKind
{
    /// <summary>No-slip solid wall (enclosure boundary).</summary>
    Wall,

    /// <summary>Uniform prescribed velocity into the domain.</summary>
    InletVelocity,

    /// <summary>Constant-pressure outflow: p is pinned, velocity leaves freely
    /// (zero normal gradient). Also the honest "open to ambient" boundary for
    /// natural-convection problems, where entrainment may flow IN through it.</summary>
    OutletPressure,

    /// <summary>Free-slip mirror: zero normal velocity, zero tangential shear. The
    /// far-lateral boundary of an external flow, where the domain is cut far enough
    /// away that nothing crosses it.</summary>
    Symmetry
}

/// <summary>
/// A rectangular opening on a domain-box face — an enclosure inlet or outlet. The rest
/// of the face keeps the face's own kind (typically <see cref="FlowFaceKind.Wall"/>).
/// A fan is modelled as a uniform-velocity inlet patch; fan curves are a named deferral.
/// </summary>
public sealed record FlowOpening
{
    /// <summary>The box face the opening sits on.</summary>
    public required BoxFace Face { get; init; }

    /// <summary>In-plane rectangle bounds in WORLD coordinates. The two in-plane axes
    /// are the remaining world axes in x→y→z order: an X face uses (u, v) = (y, z),
    /// a Y face (x, z), a Z face (x, y).</summary>
    public required double UMin { get; init; }
    public required double UMax { get; init; }
    public required double VMin { get; init; }
    public required double VMax { get; init; }

    /// <summary>Only <see cref="FlowFaceKind.InletVelocity"/> or
    /// <see cref="FlowFaceKind.OutletPressure"/> make sense on an opening.</summary>
    public FlowFaceKind Kind { get; init; } = FlowFaceKind.InletVelocity;

    /// <summary>Uniform inlet velocity [m/s]; ignored for pressure outlets.</summary>
    public Vector3D Velocity { get; init; } = new(0, 0, 0);

    /// <summary>
    /// Temperature of the stream entering through this opening [K]; null = the ambient
    /// (the reference temperature the energy equation is initialized at). An inlet that
    /// is HOTTER than the surroundings is the whole point of a heat exchanger, so the
    /// two cannot be the same number — before this existed the ambient was simultaneously
    /// the inlet stream, the Boussinesq reference and the radiative surroundings.
    /// Ignored on a pressure outlet, where the fluid leaves at whatever it has become
    /// (backflow through an outlet still arrives at the ambient).
    /// </summary>
    public double? Temperature { get; init; }
}

/// <summary>
/// Which length scale the Reynolds guard is honest about.
/// </summary>
public enum FlowRegime
{
    /// <summary>Flow AROUND a body inside a larger fluid box: the obstacle's largest
    /// extent is the scale, which is what an external-flow Reynolds number means.</summary>
    External = 0,

    /// <summary>Flow THROUGH a passage inside a solid: the scale is the hydraulic
    /// diameter D_h = 4·V_fluid/A_wetted, which for a duct of any cross-section is
    /// exactly 4A/P. Using the solid's extent there would report the Reynolds number of
    /// a body that is not in the flow at all - on a 300 mm block with a 20 mm bore it
    /// reads 15× high and refuses a perfectly laminar case.</summary>
    Internal = 1
}

/// <summary>
/// Settings for the Stage 2 CFD solve: where the fluid domain is, how finely it is
/// gridded, and what its outer boundaries do. Null on a project means "no CFD" — the
/// Stage 1 correlation environment keeps working untouched.
/// <para>
/// The grid is uniform Cartesian with CUBIC cells (one <see cref="CellSize"/>): the MAC
/// projection discretization is exact for the linear shear/channel benchmark flows on
/// such grids, and the pressure Poisson stays the 7-point SPD Laplacian the house CG
/// solves. Stretched/adaptive grids are a named deferral.
/// </para>
/// </summary>
public sealed record CfdSettings
{
    /// <summary>The fluid domain box; null = auto from the solid bounds and the flow
    /// direction (see <see cref="ResolveGrid"/> — the choice is logged, never silent).</summary>
    public Aabb? DomainBox { get; init; }

    /// <summary>Cubic cell edge [m]; 0 = auto (smallest solid extent / 24, coarsened
    /// if needed to respect <see cref="MaxCells"/> — logged).</summary>
    public double CellSize { get; init; }

    /// <summary>Refusal line for the grid size: a user-set <see cref="CellSize"/> that
    /// would exceed this is a typed failure NAMING the cell size that fits — never a
    /// silent multi-gigabyte allocation.</summary>
    public int MaxCells { get; init; } = 3_000_000;

    /// <summary>What each domain-box face does. Default is a closed box (all walls) —
    /// the conservative enclosure; external-flow setups come from
    /// <see cref="ForExternalFlow"/>.</summary>
    public FlowFaceKind XMinFace { get; init; } = FlowFaceKind.Wall;
    public FlowFaceKind XMaxFace { get; init; } = FlowFaceKind.Wall;
    public FlowFaceKind YMinFace { get; init; } = FlowFaceKind.Wall;
    public FlowFaceKind YMaxFace { get; init; } = FlowFaceKind.Wall;
    public FlowFaceKind ZMinFace { get; init; } = FlowFaceKind.Wall;
    public FlowFaceKind ZMaxFace { get; init; } = FlowFaceKind.Wall;

    /// <summary>Uniform velocity on every <see cref="FlowFaceKind.InletVelocity"/>
    /// face [m/s].</summary>
    public Vector3D InletVelocity { get; init; } = new(0, 0, 0);

    /// <summary>Enclosure inlets/outlets — rectangular patches on box faces.</summary>
    public IReadOnlyList<FlowOpening> Openings { get; init; } = Array.Empty<FlowOpening>();

    /// <summary>
    /// The fluid the CFD resolves, by <see cref="FluidLibrary"/> name. Null - every
    /// external-flow case - means "the surroundings", i.e. the environment's own fluid,
    /// and then the CFD IS the environment: correlations have nothing left to add on a
    /// wetted face and only radiation joins the film. A NAMED fluid marks an internal
    /// circuit (water through a copper block) whose surroundings are still unresolved,
    /// so the Stage 1 correlations keep carrying the outer surface.
    /// </summary>
    public string? FluidName { get; init; }

    /// <summary>
    /// A fluid given explicitly rather than by library name — the same escape hatch
    /// <see cref="EnvironmentSettings.CustomFluid"/> is, and the one a benchmark with
    /// exactly-known constant properties needs. It wins over <see cref="FluidName"/> and,
    /// like a name, it marks the CFD as resolving a circuit rather than the surroundings.
    /// </summary>
    public FluidProperties? CustomFluid { get; init; }

    /// <summary>The CFD working fluid, or null when the CFD resolves the surroundings and
    /// the environment supplies it. Throws when a NAME is given that no library fluid
    /// carries — a silent fallback to air inside a water circuit is unthinkable.</summary>
    public FluidProperties? ResolveFluid()
    {
        if (CustomFluid is not null) return CustomFluid;
        if (FluidName is null) return null;
        return FluidLibrary.Find(FluidName) ?? throw new InvalidOperationException(
            $"No fluid named {FluidName} is in the library " +
            $"(have: {string.Join(", ", FluidLibrary.All.Select(f => f.Name))}).");
    }

    /// <summary>Which characteristic length the Reynolds guard uses. External (the
    /// default) keeps every existing case byte-identical.</summary>
    public FlowRegime Regime { get; init; } = FlowRegime.External;

    /// <summary>True when the CFD resolves the surroundings themselves rather than a
    /// separate internal circuit - see <see cref="FluidName"/>.</summary>
    public bool ResolvesSurroundings => FluidName is null && CustomFluid is null;

    /// <summary>Steady-state convergence: relative change of the velocity field per
    /// pseudo-time step below which the flow is converged.</summary>
    public double SteadyTolerance { get; init; } = 1e-5;

    /// <summary>Cap on pseudo-time steps for the steady march (typed failure past it,
    /// naming the residual — an oscillating residual suggests unsteady physics).</summary>
    public int MaxSteps { get; init; } = 4000;

    /// <summary>The face kind of one box face.</summary>
    public FlowFaceKind FaceKind(BoxFace face) => face switch
    {
        BoxFace.XMin => XMinFace,
        BoxFace.XMax => XMaxFace,
        BoxFace.YMin => YMinFace,
        BoxFace.YMax => YMaxFace,
        BoxFace.ZMin => ZMinFace,
        BoxFace.ZMax => ZMaxFace,
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    };

    /// <summary>True when any face or opening lets fluid cross the domain boundary —
    /// a fully closed box needs the pressure level pinned differently (pure-Neumann
    /// Poisson) and a compatibility check on the net inflow.</summary>
    public bool HasOpenBoundary =>
        AllFaces.Any(f => FaceKind(f) is FlowFaceKind.InletVelocity or FlowFaceKind.OutletPressure)
        || Openings.Count > 0;

    private static readonly BoxFace[] AllFaces =
        { BoxFace.XMin, BoxFace.XMax, BoxFace.YMin, BoxFace.YMax, BoxFace.ZMin, BoxFace.ZMax };

    /// <summary>
    /// The resolved uniform grid: domain box, cell size and cell counts, plus the notes
    /// explaining every automatic choice.
    /// </summary>
    /// <param name="Domain">The gridded box. Its max corner is the requested min corner
    /// plus a whole number of cells — snapped OUTWARD so cells stay exactly cubic.</param>
    public sealed record ResolvedGrid(Aabb Domain, double CellSize,
        int CellsX, int CellsY, int CellsZ, IReadOnlyList<string> Notes)
    {
        public long CellCount => (long)CellsX * CellsY * CellsZ;
    }

    /// <summary>
    /// Resolves the domain box, cell size and cell counts against the solid bounds.
    /// <para>
    /// Auto domain: per axis, margins of 2·L upstream and 5·L wake along the flow
    /// (L = the solid's extent on that axis, floored at 20% of its largest extent so
    /// thin bodies still get room), 2·L on both sides of a crossflow axis. With no flow
    /// the box is 2·L all around. Auto cell size: smallest solid extent / 24 — the body,
    /// not the domain, is what must be resolved. Either automatic value is coarsened to
    /// respect <see cref="MaxCells"/> with a note; an EXPLICIT cell size that busts the
    /// budget is a typed failure naming the cell size that fits.
    /// </para>
    /// </summary>
    /// <param name="solidBounds">Bounding box of all solid bodies.</param>
    /// <param name="flowVelocity">The free-stream velocity the margins are built for.</param>
    public ResolvedGrid ResolveGrid(Aabb solidBounds, Vector3D flowVelocity)
    {
        var notes = new List<string>();

        Aabb domain;
        if (DomainBox is { } userBox)
        {
            // The domain must INTERSECT the solids, not contain them. An internal-flow
            // domain is deliberately a SUBSET of the solid — the channel and nothing
            // else — and forcing it to swallow the whole block would drag every
            // external pocket into the flow as a sealed cavity and multiply the cell
            // budget by the volume ratio. Solid outside the domain is invisible to the
            // flow and keeps its ordinary FE boundary conditions; how much of it there is
            // gets SAID, because that is exactly what a reader needs to judge the model.
            double overlap = OverlapVolume(userBox, solidBounds);
            if (overlap <= 0)
                throw new InvalidOperationException(
                    "The CFD domain box does not overlap the solid bodies at all " +
                    $"(bodies span {Fmt(solidBounds)}, domain is {Fmt(userBox)}). " +
                    "Move or grow the domain box, or clear it to use the automatic domain.");
            var solidSize = solidBounds.Size;
            double solidVolume = solidSize.X * solidSize.Y * solidSize.Z;
            if (solidVolume > 0 && overlap < solidVolume * (1 - 1e-9))
                notes.Add($"CFD domain {Fmt(userBox)} covers {100 * overlap / solidVolume:F1}% of " +
                          "the solid bounding box; solid outside the domain is not seen by the " +
                          "flow and keeps its FE boundary conditions (the internal-flow case).");
            domain = userBox;
        }
        else
        {
            domain = AutoDomain(solidBounds, flowVelocity, notes);
        }

        double h = CellSize;
        bool autoH = h <= 0;
        if (autoH)
        {
            double minExtent = MinPositiveExtent(solidBounds);
            h = minExtent / 24.0;
            notes.Add($"Auto cell size: {h:G4} m (smallest solid extent {minExtent:G4} m / 24).");
        }
        else if (h <= 0 || double.IsNaN(h))
        {
            throw new InvalidOperationException($"Cell size must be positive (got {h}).");
        }

        // Budget check against the UNSNAPPED extents (snapping adds at most one cell per
        // axis — irrelevant at these counts, and using the final count would make the
        // "cell size that fits" self-referential).
        var size = domain.Size;
        double fitH = Math.Cbrt(size.X * size.Y * size.Z / MaxCells);
        long Count(double hh) =>
            (long)CellsAlong(size.X, hh) * CellsAlong(size.Y, hh) * CellsAlong(size.Z, hh);
        if (Count(h) > MaxCells)
        {
            if (!autoH)
                throw new InvalidOperationException(
                    $"Cell size {h:G4} m needs {Count(h):N0} cells — over the {MaxCells:N0}-cell budget. " +
                    $"A cell size of about {fitH:G4} m fits this domain.");
            // Auto sizing coarsens to fit; nudge up until the integer cell count obeys.
            while (Count(fitH) > MaxCells) fitH *= 1.01;
            notes.Add($"Auto cell size coarsened {h:G4} → {fitH:G4} m to respect the " +
                      $"{MaxCells:N0}-cell budget.");
            h = fitH;
        }

        int nx = CellsAlong(size.X, h), ny = CellsAlong(size.Y, h), nz = CellsAlong(size.Z, h);
        var snappedMax = new Vector3D(
            domain.Min.X + nx * h, domain.Min.Y + ny * h, domain.Min.Z + nz * h);
        var snapped = new Aabb(domain.Min, snappedMax);
        return new ResolvedGrid(snapped, h, nx, ny, nz, notes);
    }

    /// <summary>
    /// External-flow boundary policy: faces the flow enters become velocity inlets,
    /// faces it leaves become pressure outlets, transverse faces are symmetry planes.
    /// Zero velocity (a body in still fluid) opens EVERY face as a pressure outlet —
    /// the natural-convection plume must leave somewhere and entrainment must get in.
    /// </summary>
    public static CfdSettings ForExternalFlow(Vector3D velocity, CfdSettings? baseline = null)
    {
        var s = baseline ?? new CfdSettings();
        if (velocity.Length <= 0)
            return s with
            {
                XMinFace = FlowFaceKind.OutletPressure, XMaxFace = FlowFaceKind.OutletPressure,
                YMinFace = FlowFaceKind.OutletPressure, YMaxFace = FlowFaceKind.OutletPressure,
                ZMinFace = FlowFaceKind.OutletPressure, ZMaxFace = FlowFaceKind.OutletPressure,
                InletVelocity = new Vector3D(0, 0, 0)
            };

        double tol = 1e-9 * velocity.Length;
        (FlowFaceKind Low, FlowFaceKind High) Along(double component) =>
            component > tol ? (FlowFaceKind.InletVelocity, FlowFaceKind.OutletPressure)
            : component < -tol ? (FlowFaceKind.OutletPressure, FlowFaceKind.InletVelocity)
            : (FlowFaceKind.Symmetry, FlowFaceKind.Symmetry);

        var x = Along(velocity.X);
        var y = Along(velocity.Y);
        var z = Along(velocity.Z);
        return s with
        {
            XMinFace = x.Low, XMaxFace = x.High,
            YMinFace = y.Low, YMaxFace = y.High,
            ZMinFace = z.Low, ZMaxFace = z.High,
            InletVelocity = velocity
        };
    }

    /// <summary>
    /// Internal-flow boundary policy: every box face is a WALL, and the openings carry
    /// the whole inlet/outlet story. This is the natural shape for a passage bored
    /// through a solid — the domain box is a subset of the block, its faces are inside
    /// metal almost everywhere, and only the bore mouths are open.
    /// </summary>
    /// <param name="domain">The gridded box — typically the fluid body's bounds.</param>
    /// <param name="openings">Inlet and outlet patches on the box faces.</param>
    /// <param name="fluidName">The working fluid's library name; it is what marks this
    /// as a circuit distinct from the surroundings (see <see cref="FluidName"/>).</param>
    /// <param name="baseline">Cell size / budget / tolerances to keep.</param>
    public static CfdSettings ForInternalFlow(Aabb domain, IReadOnlyList<FlowOpening> openings,
        string fluidName, CfdSettings? baseline = null)
    {
        if (openings.Count == 0)
            throw new InvalidOperationException(
                "An internal-flow domain needs at least one opening — a sealed passage " +
                "has no flow, and every face of the box is a wall.");
        var s = baseline ?? new CfdSettings();
        return s with
        {
            DomainBox = domain,
            XMinFace = FlowFaceKind.Wall, XMaxFace = FlowFaceKind.Wall,
            YMinFace = FlowFaceKind.Wall, YMaxFace = FlowFaceKind.Wall,
            ZMinFace = FlowFaceKind.Wall, ZMaxFace = FlowFaceKind.Wall,
            InletVelocity = new Vector3D(0, 0, 0),
            Openings = openings.ToArray(),
            Regime = FlowRegime.Internal,
            FluidName = fluidName
        };
    }

    /// <summary>Volume of the intersection of two boxes (0 when they miss).</summary>
    private static double OverlapVolume(Aabb a, Aabb b)
    {
        double dx = Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X);
        double dy = Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y);
        double dz = Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z);
        if (dx <= 0 || dy <= 0 || dz <= 0) return 0;
        return dx * dy * dz;
    }

    private Aabb AutoDomain(Aabb solid, Vector3D flow, List<string> notes)
    {
        var ext = solid.Size;
        double maxExtent = Math.Max(ext.X, Math.Max(ext.Y, ext.Z));
        if (maxExtent <= 0)
            throw new InvalidOperationException(
                "The solid bodies have zero extent — nothing to build a CFD domain around.");

        // Thin bodies (a plate's thickness axis) still need room to flow around:
        // floor each axis's margin scale at 20% of the largest extent.
        double L(double e) => Math.Max(e, 0.2 * maxExtent);

        double speed = flow.Length;
        (double Low, double High) Margins(double e, double component)
        {
            double l = L(e);
            if (speed <= 0 || Math.Abs(component) <= 1e-9 * speed) return (2 * l, 2 * l);
            return component > 0 ? (2 * l, 5 * l) : (5 * l, 2 * l);
        }

        var mx = Margins(ext.X, flow.X);
        var my = Margins(ext.Y, flow.Y);
        var mz = Margins(ext.Z, flow.Z);
        var box = new Aabb(
            new Vector3D(solid.Min.X - mx.Low, solid.Min.Y - my.Low, solid.Min.Z - mz.Low),
            new Vector3D(solid.Max.X + mx.High, solid.Max.Y + my.High, solid.Max.Z + mz.High));
        notes.Add(speed > 0
            ? $"Auto CFD domain: solid bounds + 2L upstream / 5L wake / 2L crossflow → {Fmt(box)}."
            : $"Auto CFD domain: solid bounds + 2L on every side (no imposed flow) → {Fmt(box)}.");
        return box;
    }

    private static double MinPositiveExtent(Aabb box)
    {
        var s = box.Size;
        double min = double.PositiveInfinity;
        foreach (double e in new[] { s.X, s.Y, s.Z })
            if (e > 0 && e < min) min = e;
        if (double.IsPositiveInfinity(min))
            throw new InvalidOperationException(
                "The solid bodies have zero extent — nothing to size CFD cells against.");
        return min;
    }

    private static int CellsAlong(double extent, double h) =>
        Math.Max(1, (int)Math.Ceiling(extent / h - 1e-9));

    private static string Fmt(Aabb b) =>
        $"[{b.Min.X:G4}, {b.Min.Y:G4}, {b.Min.Z:G4}] – [{b.Max.X:G4}, {b.Max.Y:G4}, {b.Max.Z:G4}] m";
}
