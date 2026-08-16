using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

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
/// no-penetration exactly on the shared face and no-slip at the neighbouring parallel
/// face — the stated first-order staircase treatment.
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
    public FlowSolution SolveSteady(double[]? wallFaceTemperatures = null,
        CancellationToken cancellationToken = default,
        Action<int, double>? progress = null)
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
            double span = _thermal.BoundarySpan();
            if (_wallTemps is not null)
                foreach (double t in _wallTemps)
                    span = Math.Max(span, Math.Abs(t - _thermal.AmbientTemperature));
            // Nothing imposes a temperature ⇒ T stays at ambient up to numeric dust; an
            // infinite scale keeps that dust out of the march residual instead of
            // dividing by an arbitrary epsilon.
            _tScale = span > 0 ? span : double.PositiveInfinity;
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

            residual = Step(cancellationToken);
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

    // ================================================================ one projection step

    private double Step(CancellationToken ct)
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
        if (_thermal is not null)
            AdvanceTemperature(ct);

        // March residual: largest velocity change this step relative to the field scale,
        // combined with the temperature change relative to the imposed span.
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
        double residual = maxDelta / scale;
        if (_thermal is not null)
        {
            double maxDeltaT = 0;
            for (int n = 0; n < _tN; n++)
            {
                int c = _tCells[n];
                double d = Math.Abs(_t[c] - _tPrev[c]);
                if (d > maxDeltaT) maxDeltaT = d;
            }
            residual = Math.Max(residual, maxDeltaT / _tScale);
        }
        return residual;
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
        if (_thermal!.FaceTemperature(d, high) is { } explicitT) return explicitT;
        var (kind, _) = BoundaryAt(d, high, _grid.CellCenter(i, j, k));
        return kind == FlowFaceKind.InletVelocity ? _thermal.AmbientTemperature : null;
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
            tUp = _thermal!.FaceTemperature(d, s > 0) ?? _thermal.AmbientTemperature;
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
                        var (kind, velocity) = BoundaryAt(axis, high, FaceCenter(comp, i, j, k));
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
        comp.RhsFixed = new double[comp.ActiveCount];
        comp.Adv = new double[comp.ActiveCount];
        comp.Rhs = new double[comp.ActiveCount];
        comp.Star = new double[comp.ActiveCount];
        return comp;
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
                        var (kind, velocity) = BoundaryAt(d, s > 0, FaceCenter(comp, i, j, k));
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
            return comp.Values[comp.Flat(i + (d == 0 ? s : 0), j + (d == 1 ? s : 0),
                k + (d == 2 ? s : 0))];
        if (d == comp.Axis) return self; // outlet normal direction: zero gradient
        var (kind, velocity) = BoundaryAt(d, s > 0, FaceCenter(comp, i, j, k));
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
                        var (kind, _) = BoundaryAt(d, s > 0, _grid.CellCenter(i, j, k));
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

    private double CharacteristicLength()
    {
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

    private (FlowFaceKind Kind, Vector3D Velocity) BoundaryAt(int axis, bool high, Vector3D pos)
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
        foreach (var opening in _settings.Openings)
        {
            if (opening.Face != face) continue;
            if (uCoord >= opening.UMin && uCoord <= opening.UMax
                && vCoord >= opening.VMin && vCoord <= opening.VMax)
                return (opening.Kind, opening.Velocity);
        }
        return (_settings.FaceKind(face), _settings.InletVelocity);
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
