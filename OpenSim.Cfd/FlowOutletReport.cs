using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

/// <summary>
/// What crosses one opening (or one whole box face) of the flow domain.
/// </summary>
/// <param name="Face">The box face the port sits on.</param>
/// <param name="Label">"Opening 2 (XMax)" for a named patch, "Face XMax" otherwise.</param>
/// <param name="Kind">Whether the net flux is in or out.</param>
/// <param name="Area">Open area actually crossed [m²] — the staircase area of the fluid
/// faces on the port, NOT the rectangle the user typed. A bore mouth inscribed in a
/// rectangle is smaller than the rectangle, and reporting the rectangle would inflate
/// every derived mass flow.</param>
/// <param name="VolumeFlow">Signed volume flow, positive INTO the domain [m³/s].</param>
/// <param name="MassFlow">ρ·VolumeFlow [kg/s].</param>
/// <param name="MixedTemperature">The mass-flow-weighted (mixing-cup) mean temperature
/// of what crosses [K]. This is the number a heat-exchanger report quotes; an unweighted
/// face average is a DIFFERENT number — it counts the slow near-wall fluid, which carries
/// almost no enthalpy, as heavily as the fast core.</param>
public sealed record FlowPortFlux(BoxFace Face, string Label, FlowFaceKind Kind,
    double Area, double VolumeFlow, double MassFlow, double MixedTemperature);

/// <summary>
/// The mass and enthalpy ledger of a flow solution's open boundaries: what came in, what
/// left, and the heat the fluid carried away between the two.
/// <para>
/// The enthalpy balance Q = Σ ṁ·c_p·T over every port is an IDENTITY of the converged
/// energy equation (the conservative upwind flux form conserves enthalpy to solver
/// tolerance), so it is gated as one rather than tolerated as an estimate.
/// </para>
/// </summary>
/// <param name="Ports">Every port, inlets and outlets, in canonical face order.</param>
/// <param name="HeatRemoved">Net enthalpy the stream carried OUT [W]: Σ_out ṁ·c_p·T −
/// Σ_in ṁ·c_p·T. Positive means the fluid left hotter than it arrived, i.e. it picked
/// heat up from the solid.</param>
public sealed record FlowOutletReport(IReadOnlyList<FlowPortFlux> Ports, double HeatRemoved)
{
    /// <summary>Ports the fluid leaves through (net outflow).</summary>
    public IEnumerable<FlowPortFlux> Outlets => Ports.Where(p => p.VolumeFlow < 0);

    /// <summary>Ports the fluid arrives through (net inflow).</summary>
    public IEnumerable<FlowPortFlux> Inlets => Ports.Where(p => p.VolumeFlow > 0);

    /// <summary>One log line per port plus the enthalpy line — what the reference reports
    /// of this world quote, and the numbers a reader needs to check the balance.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var p in Ports)
            // A port nothing crosses has no mixing-cup temperature — there is no stream to
            // take the mean of. That happens honestly: a bore mouth is a CIRCLE and the
            // detected opening rectangle bounds its tessellation, so a cell or two of the
            // real mouth can fall outside the rectangle and stay wall. Saying "no flow"
            // beats printing NaN at a reader.
            yield return double.IsNaN(p.MixedTemperature)
                ? $"{p.Label}: no flow across {p.Area * 1e6:F0} mm² (it is wall)."
                : $"{p.Label}: {(p.VolumeFlow > 0 ? "in" : "out")} " +
                  $"{Math.Abs(p.MassFlow):G4} kg/s over {p.Area * 1e6:F0} mm², " +
                  $"mixing-cup T = {p.MixedTemperature:F2} K.";
        yield return $"Stream enthalpy balance: the fluid carried {HeatRemoved:G4} W " +
                     "out of the domain (positive = picked up from the solid).";
    }
}

/// <summary>
/// Builds the <see cref="FlowOutletReport"/> from a converged solution.
/// <para>
/// Every domain-boundary face of a fluid cell is visited once, in canonical
/// (axis, k, j, i) order, and attributed to the OPENING that contains its center when
/// there is one, otherwise to the box face itself — the same point-in-opening rule the
/// solver's own boundary lookup uses, so the ledger and the solve can never disagree
/// about which port a face belongs to.
/// </para>
/// </summary>
public static class FlowOutletReporter
{
    /// <param name="flow">The converged solution (must carry a temperature field:
    /// a mixing-cup temperature of a flow without an energy equation is not a number).</param>
    /// <param name="settings">The settings the solve ran with — the openings live there.</param>
    /// <param name="density">Fluid density [kg/m³].</param>
    /// <param name="specificHeat">Fluid specific heat [J/(kg·K)].</param>
    public static FlowOutletReport Build(FlowSolution flow, CfdSettings settings,
        double density, double specificHeat)
    {
        if (flow.Temperature is null)
            throw new InvalidOperationException(
                "A mixing-cup temperature needs the fluid energy equation; this solution " +
                "carries no temperature field.");

        var grid = flow.Grid;
        double h = grid.H, area = h * h;
        var accum = new Dictionary<string, Port>();

        // Signed contribution of one boundary face. `outward` is the outward normal's
        // sign along the axis; a face velocity times it is positive when fluid LEAVES.
        void Add(int axis, int i, int j, int k, double faceVelocity, int outward, Vector3D center)
        {
            double outwardFlux = faceVelocity * outward;          // m/s, + = leaving
            var (kind, label) = Classify(settings, axis, outward > 0, center);
            var port = Get(accum, label, Face(axis, outward > 0), kind);
            port.Area += area;
            // Volume flow is reported INTO the domain, so it is minus the outward flux.
            port.Volume += -outwardFlux * area;
            // Upwind temperature: leaving fluid carries the interior cell's temperature,
            // arriving fluid the boundary's own.
            double t = outwardFlux > 0
                ? flow.Temperature[grid.CellIndex(i, j, k)]
                : BoundaryTemperature(settings, axis, outward > 0, center, flow, grid, i, j, k);
            port.EnthalpyRate += -outwardFlux * area * t;         // ∝ ṁ·T, into the domain
            port.MagnitudeRate += Math.Abs(outwardFlux) * area;
            port.MixWeighted += Math.Abs(outwardFlux) * area * t;
        }

        for (int k = 0; k < grid.Nz; k++)
            for (int j = 0; j < grid.Ny; j++)
                for (int i = 0; i < grid.Nx; i++)
                {
                    if (!grid.IsFluid(i, j, k)) continue;
                    if (i == 0)
                        Add(0, i, j, k, flow.U[grid.UIndex(0, j, k)], -1, grid.UFaceCenter(0, j, k));
                    if (i == grid.Nx - 1)
                        Add(0, i, j, k, flow.U[grid.UIndex(grid.Nx, j, k)], +1,
                            grid.UFaceCenter(grid.Nx, j, k));
                    if (j == 0)
                        Add(1, i, j, k, flow.V[grid.VIndex(i, 0, k)], -1, grid.VFaceCenter(i, 0, k));
                    if (j == grid.Ny - 1)
                        Add(1, i, j, k, flow.V[grid.VIndex(i, grid.Ny, k)], +1,
                            grid.VFaceCenter(i, grid.Ny, k));
                    if (k == 0)
                        Add(2, i, j, k, flow.W[grid.WIndex(i, j, 0)], -1, grid.WFaceCenter(i, j, 0));
                    if (k == grid.Nz - 1)
                        Add(2, i, j, k, flow.W[grid.WIndex(i, j, grid.Nz)], +1,
                            grid.WFaceCenter(i, j, grid.Nz));
                }

        var ports = new List<FlowPortFlux>();
        double enthalpyOut = 0;
        foreach (var port in accum.Values.OrderBy(p => (int)p.Face).ThenBy(p => p.Label,
                     StringComparer.Ordinal))
        {
            // The mixing-cup mean weights by |flux|, NOT by the signed one: a port with
            // balanced in- and outflow (a natural-convection opening) then still reports
            // the temperature of what actually crossed, instead of dividing a cancelled
            // enthalpy by a near-zero net flow.
            double mixed = port.MagnitudeRate > 0
                ? port.MixWeighted / port.MagnitudeRate
                : double.NaN;
            ports.Add(new FlowPortFlux(port.Face, port.Label, port.Kind, port.Area,
                port.Volume, density * port.Volume, mixed));
            enthalpyOut -= density * specificHeat * port.EnthalpyRate;
        }

        return new FlowOutletReport(ports, enthalpyOut);
    }

    private sealed class Port
    {
        public BoxFace Face;
        public string Label = "";
        public FlowFaceKind Kind;
        public double Area, Volume, EnthalpyRate, MagnitudeRate, MixWeighted;
    }

    private static Port Get(Dictionary<string, Port> map, string label, BoxFace face,
        FlowFaceKind kind)
    {
        if (!map.TryGetValue(label, out var port))
            map[label] = port = new Port { Face = face, Label = label, Kind = kind };
        return port;
    }

    private static BoxFace Face(int axis, bool high) => axis switch
    {
        0 => high ? BoxFace.XMax : BoxFace.XMin,
        1 => high ? BoxFace.YMax : BoxFace.YMin,
        _ => high ? BoxFace.ZMax : BoxFace.ZMin
    };

    /// <summary>The port a boundary face belongs to: the first opening containing its
    /// center, else the box face. Mirrors the solver's own lookup exactly.</summary>
    private static (FlowFaceKind Kind, string Label) Classify(CfdSettings settings, int axis,
        bool high, Vector3D pos)
    {
        var face = Face(axis, high);
        var (u, v) = InPlane(axis, pos);
        for (int n = 0; n < settings.Openings.Count; n++)
        {
            var opening = settings.Openings[n];
            if (opening.Face != face) continue;
            if (u >= opening.UMin && u <= opening.UMax && v >= opening.VMin && v <= opening.VMax)
                return (opening.Kind, $"Opening {n + 1} ({face})");
        }
        return (settings.FaceKind(face), $"Face {face}");
    }

    private static (double U, double V) InPlane(int axis, Vector3D pos) => axis switch
    {
        0 => (pos.Y, pos.Z),
        1 => (pos.X, pos.Z),
        _ => (pos.X, pos.Y)
    };

    /// <summary>The temperature arriving through a boundary face: an opening's stream
    /// temperature, else the interior cell's own value (a zero-gradient outlet's
    /// backflow, which is what the solver advects there too).</summary>
    private static double BoundaryTemperature(CfdSettings settings, int axis, bool high,
        Vector3D pos, FlowSolution flow, CartesianGrid grid, int i, int j, int k)
    {
        var face = Face(axis, high);
        var (u, v) = InPlane(axis, pos);
        foreach (var opening in settings.Openings)
        {
            if (opening.Face != face) continue;
            if (u >= opening.UMin && u <= opening.UMax && v >= opening.VMin && v <= opening.VMax)
                return opening.Temperature ?? flow.Temperature![grid.CellIndex(i, j, k)];
        }
        return flow.Temperature![grid.CellIndex(i, j, k)];
    }
}
