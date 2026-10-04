using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

/// <summary>
/// What one <see cref="IncompressibleFlowSolver.AdvanceEnergy"/> call did.
/// </summary>
/// <param name="Steps">Sub-steps actually taken.</param>
/// <param name="Planned">Sub-steps the CFL asked for over the marched WINDOW (the
/// interval, or four fluid transits, whichever is shorter).</param>
/// <param name="Settled">True when the field stopped changing before the interval ran
/// out — the solid step is long against the fluid residence time, so the fluid is
/// quasi-steady over it.</param>
public readonly record struct EnergyMarch(int Steps, int Planned, bool Settled);

/// <summary>
/// First-party incompressible laminar flow solver: Chorin projection on the staggered
/// MAC grid, marched in pseudo-time to steady state.
/// <para>
/// Per step: (1) predictor — explicit first-order upwind advection (monotone; the
/// second-order limited scheme is a named refinement) plus IMPLICIT viscosity, one SPD
/// Helmholtz solve per velocity component through the house Jacobi-CG; (2) pressure
/// Poisson ∇²φ = ∇·u*/Δt on the 7-point SPD Laplacian (outlet faces pin the level via a
/// half-cell Dirichlet ghost; a fully closed enclosure is pure-Neumann — net inflow is
/// checked and a typed failure if unbalanced, the level pinned by reducing out one cell,
/// exactly the fixed-support precedent); (3) correction u ← u* − Δt∇φ using the SAME
/// face-gradient expressions the Poisson operator was assembled from, so the corrected
/// field's discrete divergence is zero to CG tolerance BY CONSTRUCTION — mass
/// conservation is an identity here, not an aspiration, and it is gated as one.
/// </para>
/// <para>
/// Boundary conventions worth knowing: a velocity component tangential to a Dirichlet
/// boundary (wall or inlet) is enforced through the standard half-cell reflected ghost —
/// EXACT for linear profiles, which is what makes Couette a machine-precision gate; an
/// <see cref="FlowFaceKind.InletVelocity"/> face whose velocity is tangential (zero
/// normal component) is therefore a MOVING NO-SLIP WALL, which is how a lid-driven
/// cavity is expressed without a dedicated face kind. Solid (voxel) boundaries impose
/// no-penetration exactly on the shared face, and no-slip through the SAME half-cell
/// reflected ghost a domain wall uses: a tangential face whose neighbour one cell over
/// lies inside the solid sees the wall at the shared cell boundary, h/2 away (see
/// <see cref="TransverseNeighbourKind"/>). The velocity wall therefore sits where the
/// thermal wall already sat. Only a stair-step CORNER — a neighbour that is itself a
/// no-penetration face — keeps its known zero at distance h, the first-order
/// staircase treatment.
/// </para>
/// Determinism: assembly, CG and reductions are sequential in canonical (k, j, i) face
/// order; the explicit sweeps (advection, correction) are pure per-entry functions of
/// the previous state with disjoint writes, so the march is bitwise identical at any
/// degree of parallelism.
/// </summary>
public sealed class IncompressibleFlowSolver
{
    private readonly CartesianGrid _grid;
    private readonly VoxelizedDomain _domain;
    private readonly CfdSettings _settings;
    private readonly double _nu;       // kinematic viscosity [m²/s]
    private readonly double _rho;      // density [kg/m³]
    private readonly double _alpha;    // thermal diffusivity [m²/s]
    private readonly double _beta;     // thermal expansion [1/K]
    private readonly Vector3D _bodyAcceleration;
    private readonly FlowThermalOptions? _thermal;
    private readonly int _dop;
    private readonly double _h;
    private readonly List<string> _notes = new();

    /// <summary>Relative tolerance handed to the inner CG solves. The default keeps the
    /// projection's divergence identity near machine level; loosen only with a measured
    /// reason.</summary>
    public double LinearTolerance { get; init; } = 1e-10;

    private readonly Component[] _components;
    private readonly int[] _cellMap;          // fluid cell → Poisson unknown; −1 solid; −2 pinned
    private readonly int _pinnedCell = -1;    // flat cell index pinned to φ = 0 (enclosure)
    private readonly bool _hasOutlet;
    private readonly int _poissonN;
    private readonly CsrMatrix _poisson;
    private readonly int[] _poissonCells;     // unknown id → flat cell index
    private readonly double[] _phi;           // full cell array of the last projection
    private readonly double[] _phiSolution;   // unknown-sized warm-start buffer
    private double _dt;

    // ---- Energy equation (null thermal options ⇒ none of this is touched; the
    //      momentum path is bitwise the flow-only solver — a pinned gate).
    private readonly int[] _tMap = null!;     // fluid cell → energy unknown (no pinning:
                                              // the 1/Δt mass term regularizes, the
                                              // transient-thermal precedent)
    private readonly int[] _tCells = null!;   // unknown id → flat cell index
    private readonly int _tN;
    private readonly double[] _t = null!;     // full cell temperature array [K]
    private readonly double[] _tPrev = null!;
    private readonly double[] _tSolution = null!;
    private readonly Dictionary<int, int> _wallFaceOfCellDir = null!; // cell*6+dir → wall face
    private CsrMatrix? _tMatrix;
    private double[] _tRhsFixed = null!;
    private double[]? _wallTemps;             // per WallFace [K]; null = adiabatic solid walls
    private double _tScale = 1;
    private bool _seeded;

    /// <summary>One velocity component on its staggered face grid.</summary>
    private sealed class Component
    {
        public int Axis = -1;
        public int D0, D1, D2;                // face-grid dims
        public int Count;
        public int[] Map = null!;             // face → active id, or −1 (known)
        public double[] Values = null!;       // FULL face array; known entries imposed once
        public int[] ActiveFaces = null!;     // active id → flat face index
        public int ActiveCount;
        public CsrMatrix? Helmholtz;          // for the current Δt
        public double[] RhsFixed = null!;     // Δt-independent RHS part per active face
        public double[] Adv = null!;          // advection term per active face
        public double[] Rhs = null!;
        public double[] Star = null!;         // predictor solution (warm-started)
        public double[] Prev = null!;         // previous Values copy for the march residual
        public TransverseKind[] Transverse = null!; // per face: what it is to a transverse neighbour

        public int Dim(int d) => d == 0 ? D0 : d == 1 ? D1 : D2;
        public int Stride(int d) => d == 0 ? 1 : d == 1 ? D0 : D0 * D1;
        public int Flat(int i, int j, int k) => i + D0 * (j + D1 * k);

        public void Decode(int f, out int i, out int j, out int k)
        {
            i = f % D0;
            int rest = f / D0;
            j = rest % D1;
            k = rest / D1;
        }
    }

    public IncompressibleFlowSolver(VoxelizedDomain domain, CfdSettings settings,
        FluidState fluid, Vector3D bodyAcceleration = default,
        FlowThermalOptions? thermal = null, int maxDegreeOfParallelism = -1)
    {
        _grid = domain.Grid;
        _domain = domain;
        _settings = settings;
        _nu = fluid.KinematicViscosity;
        _rho = fluid.Density;
        _alpha = fluid.ThermalDiffusivity;
        _beta = fluid.ThermalExpansion;
        _bodyAcceleration = bodyAcceleration;
        _thermal = thermal;
        _dop = maxDegreeOfParallelism;
        _h = _grid.H;
        if (_nu <= 0 || double.IsNaN(_nu))
            throw new InvalidOperationException($"Kinematic viscosity must be positive (got {_nu}).");
        if (_thermal is not null && (_alpha <= 0 || double.IsNaN(_alpha)))
            throw new InvalidOperationException(
                $"Thermal diffusivity must be positive for an energy solve (got {_alpha}).");

        // ---- Reynolds guard: laminar only, loudly.
        double uRef = ReferenceSpeed();
        double lRef = CharacteristicLength();
        double re = uRef * lRef / _nu;
        _notes.Add($"Re = {re:G3} (U = {uRef:G3} m/s, L = {lRef:G3} m, ν = {_nu:G3} m²/s).");
        if (re > 2e4)
            throw new InvalidOperationException(
                $"Re = {re:G3} is far beyond the laminar band this solver honestly covers — " +
                "a laminar steady solution is fiction there. Reduce the velocity or use the " +
                "Stage 1 correlation environment, which covers turbulent regimes empirically.");
        if (re > 2e3)
            _notes.Add($"WARNING: Re = {re:G3} is above the typical laminar band (~2×10³); " +
                       "expect transitional physics this laminar solver cannot represent.");

        // ---- Face classification per component.
        _components = new Component[3];
        for (int a = 0; a < 3; a++)
            _components[a] = BuildComponent(a);

        // ---- Poisson: one unknown per fluid cell; enclosures pin the first fluid cell.
        //      (A sealed pocket DISCONNECTED from the pinned region leaves a singular but
        //      CONSISTENT sub-block — its velocities are all fixed 0, so its RHS is 0 and
        //      CG keeps it at 0; per-pocket pinning is a named refinement.)
        _hasOutlet = HasAnyOutletFace();
        _cellMap = new int[_grid.CellCount];
        Array.Fill(_cellMap, -1);
        int id = 0;
        int pinned = -1;
        for (int c = 0; c < _grid.CellCount; c++)
        {
            if (!_grid.IsFluidCell(c)) continue;
            if (!_hasOutlet && pinned < 0) { pinned = c; _cellMap[c] = -2; continue; }
            _cellMap[c] = id++;
        }
        _pinnedCell = pinned;
        _poissonN = id;
        _poissonCells = new int[id];
        for (int c = 0; c < _grid.CellCount; c++)
            if (_cellMap[c] >= 0) _poissonCells[_cellMap[c]] = c;
        _poisson = AssemblePoisson();
        _phi = _grid.AllocateCellField();
        _phiSolution = new double[_poissonN];
        if (!_hasOutlet)
        {
            _notes.Add("Closed domain: pressure level pinned at one cell (pure-Neumann Poisson).");
            CheckEnclosureCompatibility();
        }

        // ---- Energy equation setup (skipped entirely without thermal options).
        if (_thermal is not null)
        {
            _tMap = new int[_grid.CellCount];
            Array.Fill(_tMap, -1);
            int tid = 0;
            for (int c = 0; c < _grid.CellCount; c++)
                if (_grid.IsFluidCell(c)) _tMap[c] = tid++;
            _tN = tid;
            _tCells = new int[tid];
            for (int c = 0; c < _grid.CellCount; c++)
                if (_tMap[c] >= 0) _tCells[_tMap[c]] = c;
            _t = _grid.AllocateCellField();
            _tPrev = _grid.AllocateCellField();
            _tSolution = new double[_tN];
            Array.Fill(_t, _thermal.AmbientTemperature);
            for (int n = 0; n < _tN; n++) _tSolution[n] = _thermal.AmbientTemperature;

            // Wall-face lookup: (fluid cell, direction toward the solid) → wall face
            // index, the seam the conjugate exchange rides on. dir = axis·2 + (1 when the
            // solid sits on the + side).
            _wallFaceOfCellDir = new Dictionary<int, int>(domain.WallFaces.Count);
            for (int wf = 0; wf < domain.WallFaces.Count; wf++)
            {
                var face = domain.WallFaces[wf];
                int dir = face.Axis * 2 + (face.SolidIsHighSide ? 1 : 0);
                _wallFaceOfCellDir[face.FluidCell * 6 + dir] = wf;
            }
        }
    }

    // ================================================================ public API

    /// <summary>Everything this solver chose or warned about so far — available before
    /// the march so a construction-time note (the Reynolds line) can be read without
    /// paying for a solve.</summary>
    public IReadOnlyList<string> Notes => _notes;


    /// <summary>
    /// Marches to steady state. Typed failure when the march does not converge within
    /// <see cref="CfdSettings.MaxSteps"/> — with the residual story, because an
    /// oscillating residual usually means the flow is genuinely unsteady.
    /// </summary>
    /// <param name="wallFaceTemperatures">Per-<see cref="WallFace"/> solid surface
    /// temperature [K] (the conjugate exchange input), parallel to the voxelized
    /// domain's wall-face list; null = adiabatic solid walls. Requires thermal options.</param>
    /// <param name="progress">Called every few steps with (step, residual) — a march of
    /// thousands of pseudo-time steps with no heartbeat is indistinguishable from a hang
    /// (found live, on the first end-to-end run).</param>
    /// <param name="marchEnergy">Whether the energy equation marches with the momentum.
    /// False solves the ISOTHERMAL flow: the temperature field is left at its initial
    /// state and the march converges on velocity alone.
    /// <para>
    /// This is what a TRANSIENT wants. Its frozen momentum field is defined at the
    /// INITIAL state, where the fluid is uniform, so the Boussinesq force is identically
    /// zero — marching the energy equation first would freeze the momentum at the wrong
    /// (final) temperature state instead. It also matters practically: a heated passage
    /// at Ra ~ 1e7 has NO steady state, so a march that waits for the coupled field to
    /// settle waits forever, while the isothermal duct flow it actually needs settles in
    /// about one transit.
    /// </para></param>
    public FlowSolution SolveSteady(double[]? wallFaceTemperatures = null,
        CancellationToken cancellationToken = default,
        Action<int, double>? progress = null,
        bool marchEnergy = true)
    {
        if (wallFaceTemperatures is not null)
        {
            if (_thermal is null)
                throw new InvalidOperationException(
                    "Wall temperatures need an energy equation — construct the solver with thermal options.");
            if (wallFaceTemperatures.Length != _domain.WallFaces.Count)
                throw new ArgumentException(
                    $"Expected one wall temperature per wall face ({_domain.WallFaces.Count}), " +
                    $"got {wallFaceTemperatures.Length}.", nameof(wallFaceTemperatures));
        }
        _wallTemps = wallFaceTemperatures;
        if (_thermal is not null)
        {
            // Nothing imposes a temperature ⇒ T stays at ambient up to numeric dust; an
            // infinite scale (inside EnergyScale) keeps that dust out of the march
            // residual instead of dividing by an arbitrary epsilon. An OPENING's stream
            // temperature counts as imposed, exactly like a face or a wall: reading only
            // the face temperatures made an inlet-driven case scale-free, the residual
            // then ignored the temperature entirely, and the march stopped as soon as the
            // VELOCITY settled — with the thermal front still halfway down the channel.
            double span = EnergyScale();
            _tScale = span;
            if (_thermal.IncludeBuoyancy && _beta * span > 0.1)
                _notes.Add($"WARNING: β·ΔT = {_beta * span:G3} > 0.1 — the Boussinesq " +
                           "linearization is stretched; treat buoyancy magnitudes as approximate.");
        }

        double uRef = Math.Max(ReferenceSpeed(), 1e-30);
        // Initial Δt from the advective CFL at the reference speed; a quiescent start
        // (buoyancy/body-force-driven flows) falls back to the diffusive scale.
        _dt = 0.5 * _h / Math.Max(uRef, Math.Max(_nu, _thermal is null ? 0 : _alpha) / _h);
        foreach (var comp in _components) BuildHelmholtz(comp);
        if (_thermal is not null) BuildEnergySystem();

        // Seed active faces with the inlet velocity ONCE: a deterministic initial guess
        // that shortens external-flow marches and is projected consistent on the first
        // step. A REPEATED SolveSteady (the conjugate outer loop) keeps the previous
        // field instead — that warm start is most of the outer loop's speed.
        if (!_seeded)
        {
            _seeded = true;
            for (int a = 0; a < 3; a++)
            {
                var comp = _components[a];
                double seed = Axis(_settings.InletVelocity, a);
                for (int n = 0; n < comp.ActiveCount; n++)
                    comp.Values[comp.ActiveFaces[n]] = seed;
            }
        }

        var history = new List<double>();
        double residual = double.PositiveInfinity;
        int step = 0;
        while (step < _settings.MaxSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            step++;

            // CFL guard: the explicit upwind advection needs Δt ≤ 0.5·h/|u|max. When
            // violated, shrink to HALF the requirement — the hysteresis matters as much
            // as the bound: shrinking to exactly the requirement re-triggers on every
            // step of a developing flow (|u|max creeps up each step), and every trigger
            // rebuilds three Helmholtz systems, so the march crawls at matrix-assembly
            // speed (found live as a "hang" on the first 54k-cell run). With the ×2
            // headroom, a rebuild only recurs when the peak speed doubles again.
            double maxSpeed = MaxAbsFaceSpeed();
            double dtNeeded = 0.5 * _h / Math.Max(maxSpeed, 1e-30);
            if (_dt > dtNeeded)
            {
                _dt = 0.5 * dtNeeded;
                foreach (var comp in _components) BuildHelmholtz(comp);
                if (_thermal is not null) BuildEnergySystem();
                _notes.Add($"Step {step}: Δt reduced to {_dt:G3} s by the advective CFL " +
                           $"(|u|max = {maxSpeed:G3} m/s).");
            }

            residual = Step(cancellationToken, marchEnergy);
            history.Add(residual);
            // Step 1 reports immediately: the very first step carries the expensive
            // cold-start CG solves, and a silent first minute reads as a hang.
            if (step == 1 || step % 20 == 0) progress?.Invoke(step, residual);
            if (residual < _settings.SteadyTolerance) break;
        }

        if (residual >= _settings.SteadyTolerance)
            throw new InvalidOperationException(BuildNonConvergenceStory(history));

        double maxDiv = MaxDivergence();
        var (inflow, outflow) = BoundaryFlux();
        _notes.Add($"Steady in {step} steps: residual {residual:G3}, " +
                   $"max |∇·u| = {maxDiv:G3} 1/s, in {inflow:G4} / out {outflow:G4} m³/s.");

        var pressure = _grid.AllocateCellField();
        for (int c = 0; c < pressure.Length; c++) pressure[c] = _rho * _phi[c];
        return new FlowSolution(_grid,
            (double[])_components[0].Values.Clone(),
            (double[])_components[1].Values.Clone(),
            (double[])_components[2].Values.Clone(),
            pressure, step, residual, maxDiv, inflow, outflow, _notes.ToList(),
            _thermal is null ? null : (double[])_t.Clone());
    }

    /// <summary>How many fluid transits count as "the fluid has caught up with the walls"
    /// when a solid time step is much longer than the passage's residence time. Four is
    /// 98% of an exponential approach, and the settling test normally stops earlier.</summary>
    public const double TransitsToQuasiSteady = 4.0;

    /// <summary>
    /// Resets the fluid temperature field to one uniform value, the way a transient
    /// starts. The velocity field is untouched: a conjugate transient wants the flow
    /// ALREADY settled (momentum settles in L/U, a fraction of a second here) and the
    /// temperature at its initial state, which is exactly a steady solve followed by
    /// this call.
    /// </summary>
    public void ResetTemperature(double value)
    {
        if (_thermal is null)
            throw new InvalidOperationException(
                "This solver carries no energy equation; there is no temperature to reset.");
        Array.Fill(_t, value);
        for (int n = 0; n < _tN; n++) _tSolution[n] = value;
    }

    /// <summary>A copy of the fluid temperature field, to come back to with
    /// <see cref="RestoreTemperature"/> — what lets a caller repeat one
    /// <see cref="AdvanceEnergy"/> interval with different wall temperatures.</summary>
    public double[] SaveTemperature()
    {
        if (_thermal is null)
            throw new InvalidOperationException(
                "This solver carries no energy equation; there is no temperature to save.");
        return (double[])_t.Clone();
    }

    /// <summary>Puts back a field taken with <see cref="SaveTemperature"/>.</summary>
    public void RestoreTemperature(double[] saved)
    {
        if (_thermal is null)
            throw new InvalidOperationException(
                "This solver carries no energy equation; there is no temperature to restore.");
        if (saved.Length != _t.Length)
            throw new ArgumentException(
                $"Expected a saved field of {_t.Length} cells, got {saved.Length}.", nameof(saved));
        Array.Copy(saved, _t, _t.Length);
        for (int n = 0; n < _tN; n++) _tSolution[n] = _t[_tCells[n]];
    }

    /// <summary>
    /// Marches the fluid energy equation over REAL time on the frozen velocity field:
    /// the fluid half of a time-accurate conjugate transient. Advection is explicit
    /// upwind, so the sub-step obeys the same CFL the steady march does; diffusion stays
    /// implicit, so nothing else constrains it. The sub-step is chosen to divide the
    /// requested duration EXACTLY, because a partial last step would silently integrate a
    /// different interval than the caller asked for.
    /// <para>
    /// Why the flow is not re-solved: over one solid time step the momentum field is
    /// stationary to far better than the thermal field moves (t_flow = L/U against the
    /// front transit), which is the frozen-flow contract stated on the study. What this
    /// method removes is the far cruder assumption that the fluid TEMPERATURE is frozen
    /// too — a thermal front travelling down a channel is the whole phenomenon.
    /// </para>
    /// </summary>
    /// <param name="duration">Real time to advance [s].</param>
    /// <param name="wallFaceTemperatures">Solid surface temperature per wall face [K],
    /// or null for adiabatic walls; parallel to the domain wall-face list.</param>
    /// <returns>How the march went: sub-steps taken, sub-steps planned, and whether the
    /// field settled before the interval ran out.</returns>
    public EnergyMarch AdvanceEnergy(double duration, double[]? wallFaceTemperatures,
        CancellationToken cancellationToken = default)
    {
        if (_thermal is null)
            throw new InvalidOperationException(
                "A time-accurate energy march needs an energy equation — construct the solver " +
                "with thermal options.");
        if (!(duration > 0))
            throw new ArgumentOutOfRangeException(nameof(duration),
                $"The energy march needs a positive duration (got {duration}).");
        if (wallFaceTemperatures is not null
            && wallFaceTemperatures.Length != _domain.WallFaces.Count)
            throw new ArgumentException(
                $"Expected one wall temperature per wall face ({_domain.WallFaces.Count}), " +
                $"got {wallFaceTemperatures.Length}.", nameof(wallFaceTemperatures));

        _wallTemps = wallFaceTemperatures;
        double speed = Math.Max(MaxAbsFaceSpeed(), 1e-30);
        double cfl = 0.5 * _h / speed;

        // A solid time step far longer than the fluid RESIDENCE time asks for something
        // the fluid cannot supply: it has already reached the state those wall
        // temperatures imply, and every further sub-step reproduces it. Four transits is
        // where an exponential approach has 98% arrived, so the window is capped there
        // and the settling test below usually stops it sooner. Without the cap a 1e4 s
        // solid step over a 75 s passage marches ten thousand sub-steps to arrive where
        // it stood after three hundred.
        //
        // The residence time is V_fluid / Q, the EXACT mean residence of a flow-through
        // domain — not domain-extent/speed, which under-reads badly on a folded passage:
        // a 1.4 m serpentine inside a 0.3 m block would claim to flush five times faster
        // than it does, and the cap would then truncate a march that had not finished.
        // With no through-flow at all (a sealed enclosure, a buoyant plume) there is no
        // residence time and the extent/speed bound is the honest fallback.
        var (inflow, _) = BoundaryFlux();
        double fluidVolume = _domain.FluidCellCount * _h * _h * _h;
        double transit = inflow > 0
            ? fluidVolume / inflow
            : _h * Math.Max(_grid.Nx, Math.Max(_grid.Ny, _grid.Nz)) / speed;
        double window = Math.Min(duration, TransitsToQuasiSteady * transit);
        int steps = Math.Max(1, (int)Math.Ceiling(window / cfl));
        double saved = _dt;
        _dt = window / steps;
        BuildEnergySystem();
        int taken = 0;
        bool settled = false;
        try
        {
            // Scale for the settling test: the largest excursion anything imposes on this
            // fluid. Without it a field already at the ambient would divide by ~0.
            double scale = EnergyScale();
            for (int n = 0; n < steps; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AdvanceTemperature(cancellationToken);
                taken++;
                // A solid time step MUCH longer than the fluid residence time leaves the
                // fluid quasi-steady: once it stops moving, integrating the rest of the
                // interval changes nothing, and marching it anyway would cost thousands of
                // solves per solid step. Stopping there is exact to the same tolerance the
                // steady march converges to, not an approximation.
                // Per transit, not per sub-step — the same reading as the steady march.
                if (MaxTemperatureChange() / scale * Math.Max(transit / _dt, 1.0)
                    >= _settings.SteadyTolerance) continue;
                settled = true;
                break;
            }
        }
        finally
        {
            // Leave the solver exactly as it was found: a later steady march must not
            // inherit this call’s time step.
            _dt = saved;
            BuildEnergySystem();
        }
        return new EnergyMarch(taken, steps, settled);
    }

    /// <summary>
    /// The largest temperature excursion from the ambient that anything imposes on this
    /// fluid: face temperatures, wall temperatures, OPENING stream temperatures, and the
    /// current field. This is the scale every relative temperature test divides by, so it
    /// must see every source — an inlet whose temperature it could not see made the
    /// residual blind to the energy equation altogether.
    /// Positive infinity when nothing imposes anything, which keeps numeric dust out of a
    /// relative test rather than dividing by an epsilon.
    /// </summary>
    private double EnergyScale()
    {
        double span = _thermal!.BoundarySpan();
        if (_wallTemps is not null)
            foreach (double t in _wallTemps)
                span = Math.Max(span, Math.Abs(t - _thermal.AmbientTemperature));
        foreach (var opening in _settings.Openings)
            if (opening.Temperature is { } t)
                span = Math.Max(span, Math.Abs(t - _thermal.AmbientTemperature));
        for (int n = 0; n < _tN; n++)
            span = Math.Max(span, Math.Abs(_t[_tCells[n]] - _thermal.AmbientTemperature));
        return span > 0 ? span : double.PositiveInfinity;
    }

    /// <summary>Largest change the last energy sub-step made [K].</summary>
    private double MaxTemperatureChange()
    {
        double max = 0;
        for (int n = 0; n < _tN; n++)
        {
            int c = _tCells[n];
            double d = Math.Abs(_t[c] - _tPrev[c]);
            if (d > max) max = d;
        }
        return max;
    }

    /// <summary>The current state as a <see cref="FlowSolution"/> — the same record the
    /// steady march returns, so every downstream consumer (films, ledgers, overlays)
    /// works on a transient snapshot unchanged.</summary>
    public FlowSolution Snapshot(int steps, double residual)
    {
        var pressure = _grid.AllocateCellField();
        for (int c = 0; c < pressure.Length; c++) pressure[c] = _rho * _phi[c];
        var (inflow, outflow) = BoundaryFlux();
        return new FlowSolution(_grid,
            (double[])_components[0].Values.Clone(),
            (double[])_components[1].Values.Clone(),
            (double[])_components[2].Values.Clone(),
            pressure, steps, residual, MaxDivergence(), inflow, outflow, _notes.ToList(),
            _thermal is null ? null : (double[])_t.Clone());
    }

    // ================================================================ one projection step

    private double Step(CancellationToken ct, bool marchEnergy = true)
    {
        // 1. Explicit advection of the current field (pure function of state; parallel
        //    with disjoint writes — bitwise at any DOP).
        foreach (var comp in _components) ComputeAdvection(comp);

        // 2. Implicit-viscosity predictor per component.
        var cg = new ConjugateGradientSolver { Tolerance = LinearTolerance };
        bool buoyant = _thermal is { IncludeBuoyancy: true };
        foreach (var comp in _components)
        {
            if (comp.ActiveCount == 0) continue;
            for (int n = 0; n < comp.ActiveCount; n++)
            {
                int f = comp.ActiveFaces[n];
                comp.Rhs[n] = comp.RhsFixed[n] + comp.Values[f] / _dt - comp.Adv[n];
                if (buoyant)
                    comp.Rhs[n] += Buoyancy(comp, f);
                comp.Star[n] = comp.Values[f];        // warm start from the current field
            }
            var result = cg.Solve(comp.Helmholtz!, comp.Rhs, comp.Star, ct);
            if (!result.Converged)
                throw new InvalidOperationException(
                    $"Viscous predictor CG did not converge (component {comp.Axis}, " +
                    $"residual {result.ResidualNorm:G3}).");
        }

        // Copy the previous field for the march residual, then impose the predictor.
        foreach (var comp in _components)
        {
            Array.Copy(comp.Values, comp.Prev, comp.Count);
            for (int n = 0; n < comp.ActiveCount; n++)
                comp.Values[comp.ActiveFaces[n]] = comp.Star[n];
        }

        // 3. Pressure projection.
        if (_poissonN > 0)
        {
            var rhs = new double[_poissonN];
            // The assembled operator is −∇² (positive definite: diag +, off-diag −), so
            // ∇²φ = ∇·u*/Δt is fed as MINUS the divergence. The wrong sign here is not a
            // small error: the "correction" then doubles the divergence every step
            // (found live as an exact 2^steps blow-up on the Poiseuille gate).
            //
            // NO mean projection in the pinned (enclosure) case, ON PURPOSE: with the
            // level pinned by REDUCTION the remaining system is nonsingular and solvable
            // for any RHS — the correction zeroes the divergence at every unknown cell
            // exactly, and the pinned cell's follows by telescoping against the (checked)
            // zero net boundary flux. Subtracting the unknowns' mean — the recipe for an
            // UNPINNED pure-Neumann solve — actively breaks this one: it smears the
            // pinned cell's divergence over the whole field (found live as a −60 K fake
            // cooling of a passive temperature field).
            for (int n = 0; n < _poissonN; n++)
                rhs[n] = -Divergence(_poissonCells[n]) / _dt;
            var cgP = new ConjugateGradientSolver { Tolerance = LinearTolerance };
            var result = cgP.Solve(_poisson, rhs, _phiSolution, ct);
            if (!result.Converged)
                throw new InvalidOperationException(
                    $"Pressure Poisson CG did not converge (residual {result.ResidualNorm:G3}).");
            for (int n = 0; n < _poissonN; n++) _phi[_poissonCells[n]] = _phiSolution[n];
            if (_pinnedCell >= 0) _phi[_pinnedCell] = 0;

            // 4. Correct the active faces with the operator's own face gradients.
            foreach (var comp in _components) CorrectComponent(comp);
        }

        // 5. Advance the fluid temperature with the corrected (divergence-free) field —
        //    conservative upwind advection + implicit diffusion.
        if (_thermal is not null && marchEnergy)
            AdvanceTemperature(ct);

        // March residual: the largest velocity change relative to the field scale, combined
        // with the temperature change relative to the imposed span — each expressed PER
        // FLOW TIME, not per step. A mode relaxing with time constant τ moves by Δt/τ of
        // what is left each step, so a per-step change below the tolerance only says the
        // remaining error is below tolerance·τ/Δt: on a fine grid, where Δt shrinks with
        // the cell and τ does not, a slow mode passed while still far from steady. Scaled
        // by (flow time)/Δt the test reads "what is left changes by less than the
        // tolerance over one flow time" whatever the cell size.
        double scale = Math.Max(MaxAbsFaceSpeed(), Math.Max(ReferenceSpeed(), 1e-30));
        double maxDelta = 0;
        foreach (var comp in _components)
        {
            for (int n = 0; n < comp.ActiveCount; n++)
            {
                int f = comp.ActiveFaces[n];
                double d = Math.Abs(comp.Values[f] - comp.Prev[f]);
                if (d > maxDelta) maxDelta = d;
            }
        }
        double residual = maxDelta / scale * FlowTime(scale, _nu) / _dt;
        if (_thermal is not null && marchEnergy)
        {
            double maxDeltaT = 0;
            for (int n = 0; n < _tN; n++)
            {
                int c = _tCells[n];
                double d = Math.Abs(_t[c] - _tPrev[c]);
                if (d > maxDeltaT) maxDeltaT = d;
            }
            residual = Math.Max(residual, maxDeltaT / _tScale * FlowTime(scale, _alpha) / _dt);
        }
        return residual;
    }

    /// <summary>
    /// The time one field needs to cross the domain: the quicker of being carried (L/U)
    /// and diffusing (L²/diffusivity) over the longest extent L. The march residual is a
    /// change per this time (see <see cref="Step"/>); never less than one step.
    /// </summary>
    private double FlowTime(double speed, double diffusivity)
    {
        double length = _h * Math.Max(_grid.Nx, Math.Max(_grid.Ny, _grid.Nz));
        double carried = length / Math.Max(speed, 1e-30);
        double diffused = length * length / Math.Max(diffusivity, 1e-30);
        return Math.Max(Math.Min(carried, diffused), _dt);
    }

    // ================================================================ energy equation

    /// <summary>Boussinesq force −g·β·(T̄ − T_ambient) at one velocity face, T̄ averaged
    /// from the adjacent fluid cells (one cell at an outlet boundary face).</summary>
    private double Buoyancy(Component comp, int face)
    {
        comp.Decode(face, out int i, out int j, out int k);
        int a = comp.Axis;
        int own = a == 0 ? i : a == 1 ? j : k;
        double tFace;
        if (own == 0)
            tFace = _t[_grid.CellIndex(i, j, k)];
        else if (own == comp.Dim(a) - 1)
            tFace = _t[CellBehind(i, j, k, a)];
        else
            tFace = 0.5 * (_t[CellBehind(i, j, k, a)] + _t[_grid.CellIndex(i, j, k)]);
        return -Axis(_thermal!.Gravity, comp.Axis) * _beta * (tFace - _thermal.AmbientTemperature);
    }

    /// <summary>Assembles (1/Δt − α∇²) over the fluid cells plus the Δt-independent
    /// Dirichlet contributions (heated domain faces, inlet-at-ambient, solid wall
    /// temperatures). Sealed adiabatic enclosures need no pinning — the 1/Δt mass term
    /// regularizes, the transient-thermal precedent.</summary>
    private void BuildEnergySystem()
    {
        double invH2 = _alpha / (_h * _h);
        int n = _tN;
        var rowPtr = new int[n + 1];
        var cols = new List<int>(7 * n);
        var vals = new List<double>(7 * n);
        var entries = new List<(int Col, double Val)>(7);
        _tRhsFixed = new double[n];

        for (int row = 0; row < n; row++)
        {
            int c = _tCells[row];
            DecodeCell(c, out int i, out int j, out int k);
            double diag = 1.0 / _dt;
            double rhsFixed = 0;
            entries.Clear();

            for (int d = 0; d < 3; d++)
            {
                int own = d == 0 ? i : d == 1 ? j : k;
                for (int s = -1; s <= 1; s += 2)
                {
                    int nd = own + s;
                    if (nd >= 0 && nd < AxisCells(d))
                    {
                        int nc = c + s * CellStride(d);
                        int nid = _tMap[nc];
                        if (nid >= 0)
                        {
                            diag += invH2;
                            entries.Add((nid, -invH2));
                        }
                        else if (_wallTemps is not null
                                 && _wallFaceOfCellDir.TryGetValue(c * 6 + d * 2 + (s > 0 ? 1 : 0),
                                     out int wallFace))
                        {
                            // Solid neighbour with a known surface temperature: Dirichlet
                            // at the shared face, half a cell away.
                            diag += 2 * invH2;
                            rhsFixed += 2 * invH2 * _wallTemps[wallFace];
                        }
                        // solid neighbour without a wall temperature: adiabatic
                    }
                    else
                    {
                        double? tD = BoundaryTemperature(d, s > 0, i, j, k);
                        if (tD is { } value)
                        {
                            diag += 2 * invH2;
                            rhsFixed += 2 * invH2 * value;
                        }
                        // adiabatic / zero-gradient otherwise
                    }
                }
            }

            _tRhsFixed[row] = rhsFixed;
            entries.Add((row, diag));
            entries.Sort((x, y) => x.Col.CompareTo(y.Col));
            rowPtr[row] = cols.Count;
            foreach (var (col, v) in entries) { cols.Add(col); vals.Add(v); }
        }
        rowPtr[n] = cols.Count;
        _tMatrix = n == 0 ? null
            : CsrMatrix.FromArrays(n, n, rowPtr, cols.ToArray(), vals.ToArray());
    }

    /// <summary>The Dirichlet temperature of a domain-boundary face, or null (adiabatic).
    /// An explicit face temperature wins; otherwise an inlet is Dirichlet at ambient
    /// (that is the incoming stream's temperature).</summary>
    private double? BoundaryTemperature(int d, bool high, int i, int j, int k)
    {
        var (kind, _, openingT) = BoundaryAt(d, high, _grid.CellCenter(i, j, k));
        if (openingT is { } streamT) return streamT;
        if (_thermal!.FaceTemperature(d, high) is { } explicitT) return explicitT;
        return kind == FlowFaceKind.InletVelocity ? _thermal.AmbientTemperature : null;
    }

    /// <summary>The temperature a stream ARRIVING through a domain-boundary face carries:
    /// an opening's own temperature, else the face's explicit temperature, else the
    /// ambient. Same precedence and same lookup as <see cref="BoundaryTemperature"/>.</summary>
    private double InflowTemperature(int d, bool high, int i, int j, int k)
    {
        var (_, _, openingT) = BoundaryAt(d, high, _grid.CellCenter(i, j, k));
        return openingT ?? _thermal!.FaceTemperature(d, high) ?? _thermal.AmbientTemperature;
    }

    private void AdvanceTemperature(CancellationToken ct)
    {
        if (_tN == 0) return;
        Array.Copy(_t, _tPrev, _t.Length);

        // Conservative upwind advection: with the projected field discretely
        // divergence-free, the flux form conserves enthalpy to solver tolerance and the
        // upwinding keeps T monotone (no over/undershoot beyond the imposed bounds).
        var adv = new double[_tN];
        var options = new ParallelOptions { MaxDegreeOfParallelism = _dop };
        Parallel.For(0, _tN, options, row =>
        {
            int c = _tCells[row];
            DecodeCell(c, out int i, out int j, out int k);
            var u = _components[0].Values;
            var v = _components[1].Values;
            var w = _components[2].Values;
            double flux = 0;
            flux += FaceFlux(u[_grid.UIndex(i + 1, j, k)], c, 0, +1, i, j, k);
            flux -= FaceFlux(u[_grid.UIndex(i, j, k)], c, 0, -1, i, j, k);
            flux += FaceFlux(v[_grid.VIndex(i, j + 1, k)], c, 1, +1, i, j, k);
            flux -= FaceFlux(v[_grid.VIndex(i, j, k)], c, 1, -1, i, j, k);
            flux += FaceFlux(w[_grid.WIndex(i, j, k + 1)], c, 2, +1, i, j, k);
            flux -= FaceFlux(w[_grid.WIndex(i, j, k)], c, 2, -1, i, j, k);
            adv[row] = flux / _h;
        });

        var rhs = new double[_tN];
        for (int row = 0; row < _tN; row++)
            rhs[row] = _tRhsFixed[row] + _t[_tCells[row]] / _dt - adv[row];

        var cg = new ConjugateGradientSolver { Tolerance = LinearTolerance };
        var result = cg.Solve(_tMatrix!, rhs, _tSolution, ct);
        if (!result.Converged)
            throw new InvalidOperationException(
                $"Fluid energy CG did not converge (residual {result.ResidualNorm:G3}).");
        for (int row = 0; row < _tN; row++) _t[_tCells[row]] = _tSolution[row];
    }

    /// <summary>Advective flux u_face·T_upwind through one face of a cell (the face on
    /// side s of direction d). Wall faces carry exactly zero velocity, so solid
    /// neighbours never contribute; boundary inflow arrives at the boundary temperature
    /// (explicit face temperature, else ambient).</summary>
    private double FaceFlux(double faceVelocity, int cell, int d, int s, int i, int j, int k)
    {
        if (faceVelocity == 0) return 0;
        int own = d == 0 ? i : d == 1 ? j : k;
        int nd = own + s;
        // Outflow from this cell (velocity pointing away on side s): upwind = this cell.
        bool outflow = s > 0 ? faceVelocity > 0 : faceVelocity < 0;
        double tUp;
        if (outflow)
            tUp = _t[cell];
        else if (nd >= 0 && nd < AxisCells(d))
            tUp = _t[cell + s * CellStride(d)];    // interior (fluid) neighbour upstream
        else
            tUp = InflowTemperature(d, s > 0, i, j, k);
        return faceVelocity * tUp;
    }

    // ================================================================ component setup

    private Component BuildComponent(int axis)
    {
        var comp = new Component
        {
            Axis = axis,
            D0 = _grid.Nx + (axis == 0 ? 1 : 0),
            D1 = _grid.Ny + (axis == 1 ? 1 : 0),
            D2 = _grid.Nz + (axis == 2 ? 1 : 0)
        };
        comp.Count = comp.D0 * comp.D1 * comp.D2;
        comp.Map = new int[comp.Count];
        comp.Values = new double[comp.Count];
        comp.Prev = new double[comp.Count];
        Array.Fill(comp.Map, -1);

        var active = new List<int>();
        for (int k = 0; k < comp.D2; k++)
            for (int j = 0; j < comp.D1; j++)
                for (int i = 0; i < comp.D0; i++)
                {
                    int f = comp.Flat(i, j, k);
                    int own = axis == 0 ? i : axis == 1 ? j : k;
                    if (own == 0 || own == comp.Dim(axis) - 1)
                    {
                        // Face ON a domain plane. Its single interior cell: same coords
                        // for the low plane, one cell inward for the high plane.
                        bool high = own != 0;
                        int ci = i, cj = j, ck = k;
                        if (high)
                        {
                            if (axis == 0) ci--; else if (axis == 1) cj--; else ck--;
                        }
                        if (!_grid.IsFluid(ci, cj, ck))
                            continue; // solid pressed against the boundary: face stays 0
                        var (kind, velocity, _) = BoundaryAt(axis, high, FaceCenter(comp, i, j, k));
                        switch (kind)
                        {
                            case FlowFaceKind.InletVelocity:
                                comp.Values[f] = Axis(velocity, axis);
                                break;
                            case FlowFaceKind.OutletPressure:
                                active.Add(f);
                                break;
                            // Wall and Symmetry: normal velocity 0 (already the default).
                        }
                    }
                    else
                    {
                        int li = i, lj = j, lk = k;
                        if (axis == 0) li--; else if (axis == 1) lj--; else lk--;
                        if (_grid.IsFluid(li, lj, lk) && _grid.IsFluid(i, j, k))
                            active.Add(f);
                    }
                }

        comp.ActiveFaces = active.ToArray();
        comp.ActiveCount = active.Count;
        for (int n = 0; n < active.Count; n++) comp.Map[active[n]] = n;
        comp.Transverse = new TransverseKind[comp.Count];
        for (int f = 0; f < comp.Count; f++)
            comp.Transverse[f] = TransverseNeighbourKind(comp, f);
        comp.RhsFixed = new double[comp.ActiveCount];
        comp.Adv = new double[comp.ActiveCount];
        comp.Rhs = new double[comp.ActiveCount];
        comp.Star = new double[comp.ActiveCount];
        return comp;
    }

    /// <summary>What a face is to the viscous stencil of a neighbouring ACTIVE face of the
    /// same component one cell over in a TRANSVERSE direction (the direction along the
    /// component's own axis never asks: an inactive neighbour there is the no-penetration
    /// face on a solid's surface, a genuine zero at distance h).</summary>
    private enum TransverseKind
    {
        /// <summary>An unknown of the Helmholtz system.</summary>
        Active,
        /// <summary>A face with a known value at its own position, distance h: a
        /// prescribed inlet face, a wall/symmetry face, or a no-penetration face on a
        /// stair-step corner (exactly one of its cells is solid — the velocity really is
        /// zero THERE, and the wall plane between it and the active face is only half
        /// covered by solid).</summary>
        KnownValue,
        /// <summary>A face lying inside a solid: every cell it touches along its own axis
        /// is solid. The physical no-slip wall is then the cell boundary between the
        /// active face's fluid cells and this face's solid cells — HALF a cell from the
        /// active face — so it is imposed the way a domain wall is, through the reflected
        /// ghost u_ghost = −u, never as a known zero a full cell out (that widened every
        /// voxel passage by one cell and under-predicted Δp and shear by ≈50 % on a
        /// 2-cell bore).</summary>
        InsideSolid
    }

    /// <summary>
    /// The single classifier both the Helmholtz assembly and the advection ghost read
    /// (<see cref="BuildHelmholtz"/>, <see cref="NeighbourValue"/>), so the two can never
    /// disagree about where a wall is. Never indexes outside the grid: a face on a domain
    /// plane has ONE interior cell and only that cell is read — so a solid pressed against
    /// a pressure-outlet plane classifies its plane faces <see cref="TransverseKind.InsideSolid"/>
    /// too, the same geometry as in the interior (the solid's face is the cell boundary
    /// h/2 from the outlet face).
    /// </summary>
    private TransverseKind TransverseNeighbourKind(Component comp, int nf)
    {
        if (comp.Map[nf] >= 0) return TransverseKind.Active;
        comp.Decode(nf, out int i, out int j, out int k);
        int a = comp.Axis;
        int own = a == 0 ? i : a == 1 ? j : k;
        // The cell AHEAD of the face along its axis shares the face's coordinates and
        // exists unless the face is on the high domain plane; the cell BEHIND is one
        // back and exists unless the face is on the low plane.
        bool aheadExists = own < AxisCells(a);
        bool behindExists = own > 0;
        bool aheadSolid = aheadExists && !_grid.IsFluid(i, j, k);
        bool behindSolid = behindExists && !_grid.IsFluidCell(CellBehind(i, j, k, a));
        bool allSolid = (!aheadExists || aheadSolid) && (!behindExists || behindSolid);
        return allSolid ? TransverseKind.InsideSolid : TransverseKind.KnownValue;
    }

    /// <summary>Assembles (1/Δt − ν∇²) for one component's active faces, and the
    /// Δt-independent RHS pieces (known-neighbour and Dirichlet-ghost terms, body force).</summary>
    private void BuildHelmholtz(Component comp)
    {
        double invH2 = 1.0 / (_h * _h);
        int n = comp.ActiveCount;
        if (n == 0) { comp.Helmholtz = null; return; }
        var rowPtr = new int[n + 1];
        var cols = new List<int>(7 * n);
        var vals = new List<double>(7 * n);
        var entries = new List<(int Col, double Val)>(7);

        for (int row = 0; row < n; row++)
        {
            int f = comp.ActiveFaces[row];
            comp.Decode(f, out int i, out int j, out int k);
            double diag = 1.0 / _dt;
            double rhsFixed = Axis(_bodyAcceleration, comp.Axis);
            entries.Clear();

            for (int d = 0; d < 3; d++)
            {
                int own = d == 0 ? i : d == 1 ? j : k;
                for (int s = -1; s <= 1; s += 2)
                {
                    int nd = own + s;
                    if (nd >= 0 && nd < comp.Dim(d))
                    {
                        int nf = f + s * comp.Stride(d);
                        int nid = comp.Map[nf];
                        if (d != comp.Axis && comp.Transverse[nf] == TransverseKind.InsideSolid)
                        {
                            // The neighbouring face lies inside a solid: the no-slip wall
                            // is the shared cell boundary half a cell away, imposed through
                            // the reflected ghost u_ghost = −u — the domain-wall branch
                            // below, verbatim, and no RHS term.
                            diag += 2 * _nu * invH2;
                            continue;
                        }
                        diag += _nu * invH2;
                        if (nid >= 0)
                            entries.Add((nid, -_nu * invH2));
                        else
                            rhsFixed += _nu * invH2 * comp.Values[nf]; // known face (0 or inlet)
                    }
                    else if (d != comp.Axis)
                    {
                        // Half-cell ghost across the domain plane transverse to this
                        // component: Dirichlet boundaries reflect (2V − u); Neumann
                        // boundaries (symmetry, outlet) mirror — no term.
                        var (kind, velocity, _) = BoundaryAt(d, s > 0, FaceCenter(comp, i, j, k));
                        if (kind is FlowFaceKind.Wall or FlowFaceKind.InletVelocity)
                        {
                            diag += 2 * _nu * invH2;
                            if (kind == FlowFaceKind.InletVelocity)
                                rhsFixed += 2 * _nu * invH2 * Axis(velocity, comp.Axis);
                        }
                    }
                    // d == axis out of grid: an outlet face's zero-gradient — no term.
                }
            }

            comp.RhsFixed[row] = rhsFixed;
            entries.Add((row, diag));
            entries.Sort((x, y) => x.Col.CompareTo(y.Col));
            rowPtr[row] = cols.Count;
            foreach (var (c, v) in entries) { cols.Add(c); vals.Add(v); }
        }
        rowPtr[n] = cols.Count;
        comp.Helmholtz = CsrMatrix.FromArrays(n, n, rowPtr, cols.ToArray(), vals.ToArray());
    }

    // ================================================================ advection

    private void ComputeAdvection(Component comp)
    {
        if (comp.ActiveCount == 0) return;
        var options = new ParallelOptions { MaxDegreeOfParallelism = _dop };
        Parallel.For(0, comp.ActiveCount, options, nIdx =>
        {
            int f = comp.ActiveFaces[nIdx];
            comp.Decode(f, out int i, out int j, out int k);
            double self = comp.Values[f];
            double adv = 0;

            for (int d = 0; d < 3; d++)
            {
                double q = d == comp.Axis ? self : CrossVelocityAt(comp, i, j, k, d);
                if (q == 0) continue;
                double lo = NeighbourValue(comp, i, j, k, d, -1, self);
                double hi = NeighbourValue(comp, i, j, k, d, +1, self);
                // First-order upwind: difference against the side the flow comes FROM.
                adv += q > 0 ? q * (self - lo) / _h : q * (hi - self) / _h;
            }
            comp.Adv[nIdx] = adv;
        });
    }

    /// <summary>The value of this component one face over in direction d — the stored
    /// value in-grid, or the boundary ghost when the stencil leaves the face grid.</summary>
    private double NeighbourValue(Component comp, int i, int j, int k, int d, int s, double self)
    {
        int own = d == 0 ? i : d == 1 ? j : k;
        if (own + s >= 0 && own + s < comp.Dim(d))
        {
            int nf = comp.Flat(i + (d == 0 ? s : 0), j + (d == 1 ? s : 0), k + (d == 2 ? s : 0));
            if (d != comp.Axis && comp.Transverse[nf] == TransverseKind.InsideSolid)
                return -self;              // no-slip wall at the shared cell boundary: reflected ghost
            return comp.Values[nf];
        }
        if (d == comp.Axis) return self; // outlet normal direction: zero gradient
        var (kind, velocity, _) = BoundaryAt(d, s > 0, FaceCenter(comp, i, j, k));
        if (kind is FlowFaceKind.Wall or FlowFaceKind.InletVelocity)
        {
            double vt = kind == FlowFaceKind.InletVelocity ? Axis(velocity, comp.Axis) : 0;
            return 2 * vt - self;      // reflected Dirichlet ghost
        }
        return self;                   // symmetry / outlet: mirrored (zero gradient)
    }

    /// <summary>Cross-component advecting velocity at a face of another component:
    /// the average of the four surrounding faces (two at a domain-boundary face).</summary>
    private double CrossVelocityAt(Component comp, int i, int j, int k, int d)
    {
        var cross = _components[d];
        int a = comp.Axis;
        int own = a == 0 ? i : a == 1 ? j : k;
        double sum = 0;
        int count = 0;
        for (int cs = -1; cs <= 0; cs++)
        {
            int c = own + cs;
            if (c < 0 || c >= AxisCells(a)) continue;
            int ci = a == 0 ? c : i, cj = a == 1 ? c : j, ck = a == 2 ? c : k;
            for (int ds = 0; ds <= 1; ds++)
            {
                int fi = ci + (d == 0 ? ds : 0);
                int fj = cj + (d == 1 ? ds : 0);
                int fk = ck + (d == 2 ? ds : 0);
                sum += cross.Values[cross.Flat(fi, fj, fk)];
                count++;
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    // ================================================================ projection

    private CsrMatrix AssemblePoisson()
    {
        double invH2 = 1.0 / (_h * _h);
        int n = _poissonN;
        if (n == 0)
            return CsrMatrix.FromArrays(0, 0, new[] { 0 }, Array.Empty<int>(), Array.Empty<double>());
        var rowPtr = new int[n + 1];
        var cols = new List<int>(7 * n);
        var vals = new List<double>(7 * n);
        var entries = new List<(int Col, double Val)>(7);

        for (int row = 0; row < n; row++)
        {
            int c = _poissonCells[row];
            DecodeCell(c, out int i, out int j, out int k);
            double diag = 0;
            entries.Clear();

            for (int d = 0; d < 3; d++)
            {
                int own = d == 0 ? i : d == 1 ? j : k;
                for (int s = -1; s <= 1; s += 2)
                {
                    int nd = own + s;
                    if (nd >= 0 && nd < AxisCells(d))
                    {
                        int nc = c + s * CellStride(d);
                        int nid = _cellMap[nc];
                        if (nid >= 0) { diag += invH2; entries.Add((nid, -invH2)); }
                        else if (nid == -2) diag += invH2;   // pinned neighbour: known φ = 0
                        // solid neighbour: Neumann, no term
                    }
                    else
                    {
                        var (kind, _, _) = BoundaryAt(d, s > 0, _grid.CellCenter(i, j, k));
                        if (kind == FlowFaceKind.OutletPressure)
                            diag += 2 * invH2;               // Dirichlet 0 at the half-cell ghost
                        // inlet/wall/symmetry: Neumann, no term
                    }
                }
            }

            entries.Add((row, diag));
            entries.Sort((x, y) => x.Col.CompareTo(y.Col));
            rowPtr[row] = cols.Count;
            foreach (var (col, v) in entries) { cols.Add(col); vals.Add(v); }
        }
        rowPtr[n] = cols.Count;
        return CsrMatrix.FromArrays(n, n, rowPtr, cols.ToArray(), vals.ToArray());
    }

    /// <summary>Discrete divergence of the current face field at one fluid cell [1/s].</summary>
    private double Divergence(int cell)
    {
        DecodeCell(cell, out int i, out int j, out int k);
        var u = _components[0].Values;
        var v = _components[1].Values;
        var w = _components[2].Values;
        return (u[_grid.UIndex(i + 1, j, k)] - u[_grid.UIndex(i, j, k)]
              + v[_grid.VIndex(i, j + 1, k)] - v[_grid.VIndex(i, j, k)]
              + w[_grid.WIndex(i, j, k + 1)] - w[_grid.WIndex(i, j, k)]) / _h;
    }

    /// <summary>u ← u* − Δt·∇φ on the active faces, with the gradient written EXACTLY as
    /// the Poisson operator's flux for that face — the adjointness that zeroes the
    /// discrete divergence.</summary>
    private void CorrectComponent(Component comp)
    {
        if (comp.ActiveCount == 0) return;
        int a = comp.Axis;
        var options = new ParallelOptions { MaxDegreeOfParallelism = _dop };
        Parallel.For(0, comp.ActiveCount, options, nIdx =>
        {
            int f = comp.ActiveFaces[nIdx];
            comp.Decode(f, out int i, out int j, out int k);
            int own = a == 0 ? i : a == 1 ? j : k;

            double phiLow, phiHigh;
            if (own == 0)
            {
                // Outlet boundary face on the low domain plane: ghost = −φ_interior.
                phiHigh = _phi[_grid.CellIndex(i, j, k)];
                phiLow = -phiHigh;
            }
            else if (own == comp.Dim(a) - 1)
            {
                phiLow = _phi[CellBehind(i, j, k, a)];
                phiHigh = -phiLow;
            }
            else
            {
                phiLow = _phi[CellBehind(i, j, k, a)];
                phiHigh = _phi[_grid.CellIndex(i, j, k)];
            }
            comp.Values[f] -= _dt * (phiHigh - phiLow) / _h;
        });
    }

    private int CellBehind(int i, int j, int k, int axis) => axis switch
    {
        0 => _grid.CellIndex(i - 1, j, k),
        1 => _grid.CellIndex(i, j - 1, k),
        _ => _grid.CellIndex(i, j, k - 1)
    };

    // ================================================================ diagnostics

    private double MaxDivergence()
    {
        double max = 0;
        for (int c = 0; c < _grid.CellCount; c++)
        {
            if (!_grid.IsFluidCell(c)) continue;
            double d = Math.Abs(Divergence(c));
            if (d > max) max = d;
        }
        return max;
    }

    private (double Inflow, double Outflow) BoundaryFlux()
    {
        double area = _h * _h;
        double inflow = 0, outflow = 0;
        void Tally(double signedInto)
        {
            if (signedInto > 0) inflow += signedInto * area;
            else outflow -= signedInto * area;
        }
        var u = _components[0].Values;
        var v = _components[1].Values;
        var w = _components[2].Values;
        for (int k = 0; k < _grid.Nz; k++)
            for (int j = 0; j < _grid.Ny; j++)
            {
                if (_grid.IsFluid(0, j, k)) Tally(u[_grid.UIndex(0, j, k)]);
                if (_grid.IsFluid(_grid.Nx - 1, j, k)) Tally(-u[_grid.UIndex(_grid.Nx, j, k)]);
            }
        for (int k = 0; k < _grid.Nz; k++)
            for (int i = 0; i < _grid.Nx; i++)
            {
                if (_grid.IsFluid(i, 0, k)) Tally(v[_grid.VIndex(i, 0, k)]);
                if (_grid.IsFluid(i, _grid.Ny - 1, k)) Tally(-v[_grid.VIndex(i, _grid.Ny, k)]);
            }
        for (int j = 0; j < _grid.Ny; j++)
            for (int i = 0; i < _grid.Nx; i++)
            {
                if (_grid.IsFluid(i, j, 0)) Tally(w[_grid.WIndex(i, j, 0)]);
                if (_grid.IsFluid(i, j, _grid.Nz - 1)) Tally(-w[_grid.WIndex(i, j, _grid.Nz)]);
            }
        return (inflow, outflow);
    }

    private void CheckEnclosureCompatibility()
    {
        // In a closed domain the prescribed boundary/known fluxes must balance — the
        // pure-Neumann Poisson has no solution otherwise, and the projection would
        // silently manufacture mass at the pinned cell.
        double net = 0, gross = 0;
        for (int c = 0; c < _grid.CellCount; c++)
        {
            if (!_grid.IsFluidCell(c)) continue;
            double div = Divergence(c) * _h * _h * _h;
            net += div;
            gross += Math.Abs(div);
        }
        if (Math.Abs(net) > 1e-10 * Math.Max(gross, 1e-30))
            throw new InvalidOperationException(
                $"Closed CFD domain with unbalanced net inflow ({net:G3} m³/s): a sealed box " +
                "cannot absorb net mass. Balance the inlet/outlet openings or open a face.");
    }

    private string BuildNonConvergenceStory(List<double> history)
    {
        double last = history[^1];
        string trend;
        if (history.Count >= 8)
        {
            int quarter = history.Count / 4;
            double recentMin = history.Skip(history.Count - quarter).Min();
            double earlierMin = history.Skip(history.Count - 2 * quarter).Take(quarter).Min();
            trend = recentMin >= 0.999 * earlierMin
                ? " The residual has stopped decreasing — the flow is likely inherently " +
                  "unsteady at this Reynolds number (vortex shedding); a steady solution " +
                  "does not exist to converge to."
                : " The residual is still falling; raise MaxSteps to continue the march.";
        }
        else trend = string.Empty;
        return $"Steady flow march did not converge in {_settings.MaxSteps} steps " +
               $"(residual {last:G3}, tolerance {_settings.SteadyTolerance:G3})." + trend;
    }

    // ================================================================ helpers

    private double MaxAbsFaceSpeed()
    {
        double max = 0;
        foreach (var comp in _components)
            foreach (double x in comp.Values)
            {
                double a = Math.Abs(x);
                if (a > max) max = a;
            }
        return max;
    }

    private double ReferenceSpeed()
    {
        double s = _settings.InletVelocity.Length;
        foreach (var opening in _settings.Openings)
            if (opening.Kind == FlowFaceKind.InletVelocity)
                s = Math.Max(s, opening.Velocity.Length);
        return s;
    }

    /// <summary>
    /// D_h = 4·V_fluid/A_wetted, measured on the grid the solve actually runs on:
    /// 4·(N_fluid·h³)/(N_wall·h²) = 4·h·N_fluid/N_wall. For a duct of any cross-section
    /// that is exactly 4A/P, the textbook hydraulic diameter. The staircase wall area
    /// EXCEEDS the smooth one (a circle's voxel perimeter is 4/π of its true one), so this
    /// reads slightly BELOW the true D_h — conservative for a laminar guard, and said.
    /// </summary>
    private double HydraulicDiameter()
    {
        int wetted = _domain.WallFaces.Count;
        if (wetted == 0 || _domain.FluidCellCount == 0)
            throw new InvalidOperationException(
                "An internal-flow domain needs fluid cells bounded by solid walls; this grid " +
                "has none. Refine the cell size below the passage width, or use the external " +
                "flow regime.");
        return 4.0 * _h * _domain.FluidCellCount / wetted;
    }

    private double CharacteristicLength()
    {
        // Internal flow: the passage, not the block it is bored through.
        if (_settings.Regime == FlowRegime.Internal) return HydraulicDiameter();

        // Bodies present: the largest solid extent (the obstacle scale); otherwise the
        // largest domain extent (cavity/channel scale).
        int minI = int.MaxValue, minJ = int.MaxValue, minK = int.MaxValue;
        int maxI = int.MinValue, maxJ = int.MinValue, maxK = int.MinValue;
        bool any = false;
        for (int k = 0; k < _grid.Nz; k++)
            for (int j = 0; j < _grid.Ny; j++)
                for (int i = 0; i < _grid.Nx; i++)
                {
                    if (_grid.IsFluid(i, j, k)) continue;
                    any = true;
                    if (i < minI) minI = i;
                    if (j < minJ) minJ = j;
                    if (k < minK) minK = k;
                    if (i > maxI) maxI = i;
                    if (j > maxJ) maxJ = j;
                    if (k > maxK) maxK = k;
                }
        if (any)
            return _h * Math.Max(maxI - minI + 1, Math.Max(maxJ - minJ + 1, maxK - minK + 1));
        return Math.Max(_grid.Nx, Math.Max(_grid.Ny, _grid.Nz)) * _h;
    }

    /// <summary>
    /// What the domain boundary does at one point of one box face: the face's kind and
    /// inlet velocity, plus the stream temperature when an OPENING states one. The
    /// temperature rides along because both the diffusive Dirichlet term and the
    /// advective inflow term must read the same boundary state — they used to reach it
    /// through two different lookups, and the advective one (the dominant term at an
    /// inlet) could not see an opening at all. The rule itself lives on
    /// <see cref="CfdSettings.BoundaryAt"/>, shared with <see cref="InflowState"/> so the
    /// state the fluid properties are evaluated at is the state this solver injects.
    /// </summary>
    private (FlowFaceKind Kind, Vector3D Velocity, double? Temperature) BoundaryAt(
        int axis, bool high, Vector3D pos)
    {
        var face = axis switch
        {
            0 => high ? BoxFace.XMax : BoxFace.XMin,
            1 => high ? BoxFace.YMax : BoxFace.YMin,
            _ => high ? BoxFace.ZMax : BoxFace.ZMin
        };
        var (uCoord, vCoord) = axis switch
        {
            0 => (pos.Y, pos.Z),
            1 => (pos.X, pos.Z),
            _ => (pos.X, pos.Y)
        };
        return _settings.BoundaryAt(face, uCoord, vCoord);
    }

    private bool HasAnyOutletFace()
    {
        for (int k = 0; k < _grid.Nz; k++)
            for (int j = 0; j < _grid.Ny; j++)
                for (int i = 0; i < _grid.Nx; i++)
                {
                    if (!_grid.IsFluid(i, j, k)) continue;
                    if (i == 0 && IsOutlet(0, false, i, j, k)) return true;
                    if (i == _grid.Nx - 1 && IsOutlet(0, true, i, j, k)) return true;
                    if (j == 0 && IsOutlet(1, false, i, j, k)) return true;
                    if (j == _grid.Ny - 1 && IsOutlet(1, true, i, j, k)) return true;
                    if (k == 0 && IsOutlet(2, false, i, j, k)) return true;
                    if (k == _grid.Nz - 1 && IsOutlet(2, true, i, j, k)) return true;
                }
        return false;

        bool IsOutlet(int axis, bool high, int i, int j, int k) =>
            BoundaryAt(axis, high, _grid.CellCenter(i, j, k)).Kind == FlowFaceKind.OutletPressure;
    }

    private static double Axis(Vector3D v, int axis) => axis switch
    {
        0 => v.X, 1 => v.Y, _ => v.Z
    };

    private int AxisCells(int axis) => axis switch
    {
        0 => _grid.Nx, 1 => _grid.Ny, _ => _grid.Nz
    };

    private int CellStride(int axis) => axis switch
    {
        0 => 1, 1 => _grid.Nx, _ => _grid.Nx * _grid.Ny
    };

    private void DecodeCell(int c, out int i, out int j, out int k)
    {
        i = c % _grid.Nx;
        int rest = c / _grid.Nx;
        j = rest % _grid.Ny;
        k = rest / _grid.Ny;
    }

    private Vector3D FaceCenter(Component comp, int i, int j, int k) => comp.Axis switch
    {
        0 => _grid.UFaceCenter(i, j, k),
        1 => _grid.VFaceCenter(i, j, k),
        _ => _grid.WFaceCenter(i, j, k)
    };
}
