using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Rf.Si;

/// <summary>
/// A linear R∥C load as a NODE ELEMENT, so the linear receiver is a first-class participant in
/// the N-port engine rather than something folded into the channel reduction. That is what lets
/// an all-linear run through the nonlinear engine be compared against the exact linear solver:
/// both see the same topology, and any difference is the engine's, not the model's.
///
/// <para>Sign convention matches <see cref="INonlinearDriver"/>: current INTO the line.</para>
/// </summary>
public sealed class LinearLoadElement : INonlinearDriver
{
    private readonly double _g;

    /// <param name="loadOhms">Shunt resistance; <see cref="double.PositiveInfinity"/> for an open.</param>
    /// <param name="loadCapacitanceFarads">Shunt capacitance, carried in the channel reduction
    /// exactly as a driver's C_comp is.</param>
    public LinearLoadElement(double loadOhms, double loadCapacitanceFarads = 0)
    {
        if (loadOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(loadOhms),
                $"Receiver resistance must be positive (got {loadOhms} Ω); use "
                + "PositiveInfinity for an open. A shorted node has no finite voltage to solve "
                + "for in a nodal formulation.");
        if (loadCapacitanceFarads < 0)
            throw new ArgumentOutOfRangeException(nameof(loadCapacitanceFarads));
        _g = double.IsPositiveInfinity(loadOhms) ? 0 : 1 / loadOhms;
        CompCapacitanceFarads = loadCapacitanceFarads;
    }

    public double CompCapacitanceFarads { get; }

    // A shunt conductance draws current OUT of the line: I_into_line = −g·V.
    public (double Current, double Conductance) Evaluate(double v, double t) => (-_g * v, -_g);
}

/// <summary>
/// An IBIS RECEIVER as a node element: the protection clamps ([GND Clamp] / [POWER Clamp]) plus
/// the die capacitance, and optionally a parallel termination resistance.
///
/// <para>The clamps are exactly the tables the driver already evaluates, at exactly the same
/// rails — a receiver's clamp diodes are the same devices, they simply are not accompanied by a
/// switching output stage. Before this they were parsed, gated by the parser tests, and applied
/// at the DRIVER only, so a receiver never clamped: an overshoot that a real part would clip at
/// a diode drop above the rail was reported at its full height.</para>
///
/// <para>This element is only reachable through the N-port engine. The single-line scalar path
/// bakes a LINEAR admittance into the channel reduction, which a nonlinear far end cannot be
/// expressed through — that is precisely why the N-port reduction exists.</para>
/// </summary>
public sealed class IbisReceiverElement : INonlinearDriver
{
    private readonly PwlTable _gc, _pc;
    private readonly double _gcRail, _pcRail, _g;

    public IbisReceiverElement(IbisModel model, IbisCornerSelection corner,
        double terminationOhms = double.PositiveInfinity)
    {
        if (terminationOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(terminationOhms),
                "Receiver termination must be positive (PositiveInfinity for none).");
        _gc = PwlTable.FromTable(model.GndClamp, corner);
        _pc = PwlTable.FromTable(model.PowerClamp, corner);
        _gcRail = model.GndClampRailAt(corner);
        _pcRail = model.PowerClampRailAt(corner);
        _g = double.IsPositiveInfinity(terminationOhms) ? 0 : 1 / terminationOhms;
        CompCapacitanceFarads = model.CComp.At(corner) ?? 0;
    }

    public double CompCapacitanceFarads { get; }

    public (double Current, double Conductance) Evaluate(double v, double t)
    {
        // IBIS currents are INTO the pad; the into-line current is their negative, the same
        // convention IbisDriver uses. Each clamp is referenced to its own rail (Stage B2), on
        // its own axis: the GND clamp against V − V_gc, the POWER clamp "Vcc relative"
        // (V_pc − V), with the chain-rule sign folded into G by IbisTableAxis.
        var (igc, ggc) = IbisTableAxis.EvalGroundReferenced(_gc, _gcRail, v);
        var (ipc, gpc) = IbisTableAxis.EvalSupplyReferenced(_pc, _pcRail, v);
        return (-(igc + ipc) - _g * v, -(ggc + gpc) - _g);
    }
}
