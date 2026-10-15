using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

/// <summary>
/// The flow solver's whole state at one instant — every velocity component, the pressure
/// and the fluid temperature — to come back to with
/// <see cref="IncompressibleFlowSolver.RestoreState"/>.
/// </summary>
public sealed class FlowSnapshotState
{
    internal FlowSnapshotState(double[][] velocity, double[] phi, double[] pressure, double[] phiSolution,
        double[]? temperature, double[]? temperatureSolution)
    {
        Velocity = velocity;
        Phi = phi;
        Pressure = pressure;
        PhiSolution = phiSolution;
        Temperature = temperature;
        TemperatureSolution = temperatureSolution;
    }

    internal double[][] Velocity { get; }
    internal double[] Phi { get; }
    internal double[] Pressure { get; }
    internal double[] PhiSolution { get; }
    internal double[]? Temperature { get; }
    internal double[]? TemperatureSolution { get; }
}

public sealed partial class IncompressibleFlowSolver
{
    // ================================================================ real-time momentum

    /// <summary>A copy of the whole fluid state: velocities, pressure, temperature.</summary>
    public FlowSnapshotState SaveState() => new(
        _components.Select(c => (double[])c.Values.Clone()).ToArray(),
        (double[])_phi.Clone(), (double[])_pressure.Clone(), (double[])_phiSolution.Clone(),
        _thermal is null ? null : (double[])_t.Clone(),
        _thermal is null ? null : (double[])_tSolution.Clone());

    /// <summary>Puts back a state taken with <see cref="SaveState"/>.</summary>
    public void RestoreState(FlowSnapshotState state)
    {
        for (int a = 0; a < 3; a++) Array.Copy(state.Velocity[a], _components[a].Values, _components[a].Count);
        Array.Copy(state.Phi, _phi, _phi.Length);
        Array.Copy(state.Pressure, _pressure, _pressure.Length);
        Array.Copy(state.PhiSolution, _phiSolution, _phiSolution.Length);
        if (_thermal is not null)
        {
            Array.Copy(state.Temperature!, _t, _t.Length);
            Array.Copy(state.TemperatureSolution!, _tSolution, _tSolution.Length);
        }
    }

    /// <summary>
    /// Puts the fluid at rest: every unknown velocity and the pressure to zero, the
    /// boundary values (inlets, moving walls) as the settings give them — an impulsive
    /// start from the next <see cref="AdvanceFlow"/>.
    /// </summary>
    public void ResetVelocity()
    {
        foreach (var comp in _components)
            for (int n = 0; n < comp.ActiveCount; n++) comp.Values[comp.ActiveFaces[n]] = 0;
        Array.Clear(_phi);
        Array.Clear(_pressure);
        Array.Clear(_phiSolution);
        _seeded = true;
    }

    /// <summary>
    /// Marches momentum AND energy over real time: the same projection step the steady march
    /// takes (implicit viscosity and diffusion, explicit upwind advection, exact
    /// divergence-free correction), with a time step that divides the interval exactly —
    /// first order in time. The buoyancy force is re-evaluated from the temperature every
    /// step, so the flow follows the heating. The step starts where the steady march's does
    /// — the advective CFL at the current peak and inlet speeds, or the diffusive scale
    /// h²/(2·max(ν, α)) for a fluid at rest — and if the flow accelerates past the CFL the
    /// rest of the interval is re-divided. (The free-fall speed √(|g|·β·ΔT·L) is not a
    /// bound to start from: a viscous plume runs far below it, and sizing every step to it
    /// took hundreds of times the steps the flow needs.) Once a step changes neither the flow nor the temperature (the same
    /// per-flow-time test the steady march uses), the rest of the interval would change
    /// nothing either, and the march stops there.
    /// </summary>
    /// <param name="maxStep">The largest time step allowed [s] — what a time-accuracy study
    /// sets; the CFL may ask for less.</param>
    public EnergyMarch AdvanceFlow(double duration, double[]? wallFaceTemperatures,
        double maxStep = double.PositiveInfinity, CancellationToken cancellationToken = default)
    {
        if (!(duration > 0))
            throw new ArgumentOutOfRangeException(nameof(duration), $"The flow march needs a positive duration (got {duration}).");
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
        if (_thermal is not null) _tScale = EnergyScale();

        double diffusiveSpeed = Math.Max(_nu, _thermal is null ? 0 : _alpha) / _h;
        double StepFor(double remaining)
        {
            double speed = Math.Max(MaxAbsFaceSpeed(), Math.Max(ReferenceSpeed(), diffusiveSpeed));
            double cfl = speed > 0 ? 0.5 * _h / speed : double.PositiveInfinity;
            double target = Math.Min(maxStep, cfl);
            if (double.IsPositiveInfinity(target)) target = remaining;
            int count = Math.Max(1, (int)Math.Ceiling(remaining / target * (1 - 1e-12)));
            return remaining / count;
        }

        double saved = _dt;
        double left = duration;
        int taken = 0, planned = 0;
        bool settled = false;
        try
        {
            _dt = StepFor(left);
            planned = (int)Math.Round(left / _dt);
            foreach (var comp in _components) BuildHelmholtz(comp);
            if (_thermal is not null) BuildEnergySystem();
            while (left > 1e-12 * duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double speed = MaxAbsFaceSpeed();
                if (speed * _dt > 0.5 * _h * (1 + 1e-9))
                {
                    // The flow has sped up past the CFL: divide what is left again, with
                    // the same ×2 headroom the steady march keeps.
                    double before = _dt;
                    _dt = left / Math.Max(1, (int)Math.Ceiling(left / (0.25 * _h / speed)));
                    planned = taken + (int)Math.Round(left / _dt);
                    foreach (var comp in _components) BuildHelmholtz(comp);
                    if (_thermal is not null) BuildEnergySystem();
                    _notes.Add($"Flow march: step cut from {before:G3} s to {_dt:G3} s by the advective CFL " +
                               $"(|u|max = {speed:G3} m/s).");
                }
                double residual = Step(cancellationToken, marchEnergy: _thermal is not null);
                left -= _dt;
                taken++;
                if (residual < _settings.SteadyTolerance)
                {
                    settled = true;
                    break;
                }
            }
        }
        finally
        {
            _dt = saved;
            foreach (var comp in _components) BuildHelmholtz(comp);
            if (_thermal is not null) BuildEnergySystem();
        }
        return new EnergyMarch(taken, Math.Max(planned, taken), settled);
    }

    // ================================================================ fans

    /// <summary>Most secant steps the fan operating point may take.</summary>
    public const int MaxFanIterations = 25;

    /// <summary>The fan's pressure and the system's must agree to this fraction of the
    /// fan's shut-off pressure.</summary>
    public const double FanTolerance = 1e-3;

    private int FanOpening()
    {
        int found = -1;
        for (int n = 0; n < _settings.Openings.Count; n++)
        {
            if (_settings.Openings[n].Fan is null) continue;
            if (_settings.Openings[n].Kind != FlowFaceKind.InletVelocity)
                throw new InvalidOperationException($"Opening {n + 1} carries a fan curve but is not an inlet.");
            if (found >= 0)
                throw new InvalidOperationException(
                    "More than one opening carries a fan curve; one fan per case is solved (the operating point of " +
                    "several fans together is not).");
            found = n;
        }
        return found;
    }

    /// <summary>The domain-boundary cells an opening owns: (cell, the next cell inward, or −1).</summary>
    private List<(int Cell, int Inner)> OpeningCells(int index)
    {
        var opening = _settings.Openings[index];
        int axis = opening.Face switch
        {
            BoxFace.XMin or BoxFace.XMax => 0,
            BoxFace.YMin or BoxFace.YMax => 1,
            _ => 2
        };
        bool high = opening.Face is BoxFace.XMax or BoxFace.YMax or BoxFace.ZMax;
        var cells = new List<(int, int)>();
        int n0 = AxisCells(axis);
        int planeIndex = high ? n0 - 1 : 0;
        int inward = high ? -1 : 1;
        for (int k = 0; k < _grid.Nz; k++)
            for (int j = 0; j < _grid.Ny; j++)
                for (int i = 0; i < _grid.Nx; i++)
                {
                    int own = axis == 0 ? i : axis == 1 ? j : k;
                    if (own != planeIndex || !_grid.IsFluid(i, j, k)) continue;
                    var center = _grid.CellCenter(i, j, k);
                    var (u, v) = axis switch { 0 => (center.Y, center.Z), 1 => (center.X, center.Z), _ => (center.X, center.Y) };
                    if (_settings.OpeningAt(opening.Face, u, v) != index) continue;
                    int c = _grid.CellIndex(i, j, k);
                    int ii = i + (axis == 0 ? inward : 0), jj = j + (axis == 1 ? inward : 0), kk = k + (axis == 2 ? inward : 0);
                    bool inside = ii >= 0 && ii < _grid.Nx && jj >= 0 && jj < _grid.Ny && kk >= 0 && kk < _grid.Nz
                                  && _grid.IsFluid(ii, jj, kk);
                    cells.Add((c, inside ? _grid.CellIndex(ii, jj, kk) : -1));
                }
        return cells;
    }

    /// <summary>Static pressure on an opening's plane: per boundary cell the straight line
    /// through it and the next cell inward, read half a cell out; the mean over the opening.</summary>
    private double OpeningPressure(List<(int Cell, int Inner)> cells)
    {
        double sum = 0;
        foreach (var (cell, inner) in cells)
            sum += _rho * (inner >= 0 ? 1.5 * _pressure[cell] - 0.5 * _pressure[inner] : _pressure[cell]);
        return sum / cells.Count;
    }

    /// <summary>Sets every inlet face's normal velocity from the current settings and
    /// rebuilds what depends on the inlet values.</summary>
    private void ImposeInletValues()
    {
        foreach (var comp in _components)
        {
            int axis = comp.Axis;
            for (int k = 0; k < comp.D2; k++)
                for (int j = 0; j < comp.D1; j++)
                    for (int i = 0; i < comp.D0; i++)
                    {
                        int own = axis == 0 ? i : axis == 1 ? j : k;
                        if (own != 0 && own != comp.Dim(axis) - 1) continue;
                        bool high = own != 0;
                        int ci = i, cj = j, ck = k;
                        if (high) { if (axis == 0) ci--; else if (axis == 1) cj--; else ck--; }
                        if (!_grid.IsFluid(ci, cj, ck)) continue;
                        int f = comp.Flat(i, j, k);
                        var (kind, velocity, _) = BoundaryAt(axis, high, FaceCenter(comp, i, j, k));
                        if (kind == FlowFaceKind.InletVelocity) comp.Values[f] = Axis(velocity, axis);
                    }
            BuildHelmholtz(comp);
        }
        if (_thermal is not null) BuildEnergySystem();
    }

    /// <summary>
    /// The steady flow driven by a fan: the volume flow Q through the fan's opening is
    /// adjusted (secant steps on Δp_system(Q) − Δp_fan(Q)) until the static pressure the
    /// system needs at the opening, against the outlets' zero, is what the fan gives at Q.
    /// Each step re-marches from the last flow, so the later ones are short.
    /// </summary>
    private FlowSolution SolveFanOperatingPoint(int index, double[]? wallFaceTemperatures,
        CancellationToken cancellationToken, Action<int, double>? progress, bool marchEnergy)
    {
        var opening = _settings.Openings[index];
        var fan = opening.Fan!;
        fan.Validate();
        if (!_hasOutlet)
            throw new InvalidOperationException("A fan needs an outlet to push against; the domain is closed.");
        double directionLength = opening.Velocity.Length;
        if (!(directionLength > 0))
            throw new InvalidOperationException($"Opening {index + 1}: a fan inlet needs a velocity to give its direction.");
        var direction = opening.Velocity * (1 / directionLength);
        var cells = OpeningCells(index);
        if (cells.Count == 0) throw new InvalidOperationException($"Opening {index + 1} covers no fluid cell.");
        double area = cells.Count * _h * _h;
        int axis = opening.Face switch
        {
            BoxFace.XMin or BoxFace.XMax => 0,
            BoxFace.YMin or BoxFace.YMax => 1,
            _ => 2
        };
        double normal = Math.Abs(Axis(direction, axis));
        if (normal < 1e-9) throw new InvalidOperationException($"Opening {index + 1}: the fan blows along the face, not into the domain.");

        FlowSolution? solution = null;
        int marched = 0;
        double Mismatch(double flow)
        {
            double speed = flow / (area * normal);
            var openings = _settings.Openings.ToArray();
            openings[index] = opening with { Velocity = direction * speed };
            _settings = _settings with { Openings = openings };
            ImposeInletValues();
            solution = MarchSteady(wallFaceTemperatures, cancellationToken, progress, marchEnergy);
            marched += solution.Steps;
            return OpeningPressure(cells) - fan.PressureAt(flow);
        }

        double shutoff = fan.ShutoffPressure, free = fan.FreeDelivery;
        double q0 = 0.5 * free, g0 = Mismatch(q0);
        double q1 = g0 > 0 ? 0.6 * q0 : Math.Min(1.4 * q0, 0.95 * free), g1 = Mismatch(q1);
        int iterations = 2;
        while (Math.Abs(g1) > FanTolerance * shutoff)
        {
            if (iterations >= MaxFanIterations)
                throw new InvalidOperationException(
                    $"The fan's operating point was not found in {iterations} steps (the system and the fan still " +
                    $"differ by {g1:G3} Pa at {q1:G4} m³/s).");
            double next = g1 == g0 ? q1 * (g1 > 0 ? 0.9 : 1.1) : q1 - g1 * (q1 - q0) / (g1 - g0);
            next = Math.Clamp(next, 0.05 * q1, Math.Max(2 * q1, 1.5 * free));
            (q0, g0) = (q1, g1);
            q1 = next;
            g1 = Mismatch(q1);
            iterations++;
        }
        double pressure = fan.PressureAt(q1);
        _notes.Add($"Fan on opening {index + 1}: operating point {q1:G4} m³/s ({q1 * 3600:G4} m³/h) at {pressure:G4} Pa " +
                   $"static rise ({iterations} flows tried, {marched} march steps; the system and the curve agree to " +
                   $"{Math.Abs(g1):G2} Pa)." + (q1 > fan.Points[^1].FlowCubicMetresPerSecond
                       ? " The point lies past the curve's last entry, on its last segment run on." : ""));
        return solution! with { Notes = _notes.ToList() };
    }
}
