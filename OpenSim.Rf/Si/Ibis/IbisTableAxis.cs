namespace OpenSim.Rf.Si.Ibis;

/// <summary>
/// The voltage axis an IBIS I-V table is tabulated on, and the chain-rule sign that turns the
/// table's slope into dI/dV_out.
///
/// <para>IBIS ([Pullup]/[Pulldown]/[GND Clamp]/[POWER Clamp] keyword, "Other Notes") tabulates
/// the [Pullup] and [POWER Clamp] tables "Vcc relative": <c>Vtable = Vcc − Voutput</c>, where
/// Vcc is the rail named by [Pullup Reference] / [POWER Clamp Reference] or, failing those,
/// [Voltage Range]. The [Pulldown] and [GND Clamp] tables are referenced to their rail the other
/// way round, <c>Vtable = Voutput − Vref</c> ([Pulldown Reference] / [GND Clamp Reference],
/// default 0 V). ECL model types are the one exception: their [Pulldown] is ALSO Vcc relative
/// ("in BOTH of these cases, the data is referenced to the Vcc supply voltage, using the
/// equation: Vtable = Vcc − Voutput"). Currents are positive INTO the component in every
/// table.</para>
///
/// <para>Every consumer of a table goes through this type, so the axis convention lives in one
/// place. Reading a supply-referenced table at <c>V_out − Vcc</c> instead of
/// <c>Vcc − V_out</c> mirrors the whole characteristic: a 100 Ω pull-up to 3.3 V that should
/// SOURCE 33 mA into a grounded pad sinks it instead (defect D3).</para>
/// </summary>
internal static class IbisTableAxis
{
    /// <summary>Table voltage and dV_table/dV_out for a "Vcc relative" table
    /// (<c>Vtable = rail − vOut</c>, chain-rule sign −1).</summary>
    public static (double VTable, double Sign) SupplyReferenced(double rail, double vOut) =>
        (rail - vOut, -1.0);

    /// <summary>Table voltage and dV_table/dV_out for a rail-referenced table
    /// (<c>Vtable = vOut − rail</c>, chain-rule sign +1).</summary>
    public static (double VTable, double Sign) GroundReferenced(double rail, double vOut) =>
        (vOut - rail, +1.0);

    /// <summary>The table's current into the pad and its slope with respect to the PAD voltage
    /// (the table slope times the axis sign).</summary>
    public static (double I, double G) Eval(PwlTable table, (double VTable, double Sign) axis)
    {
        var (i, g) = table.Eval(axis.VTable);
        return (i, g * axis.Sign);
    }

    /// <summary><see cref="Eval"/> on the <see cref="SupplyReferenced"/> axis.</summary>
    public static (double I, double G) EvalSupplyReferenced(PwlTable table, double rail, double vOut) =>
        Eval(table, SupplyReferenced(rail, vOut));

    /// <summary><see cref="Eval"/> on the <see cref="GroundReferenced"/> axis.</summary>
    public static (double I, double G) EvalGroundReferenced(PwlTable table, double rail, double vOut) =>
        Eval(table, GroundReferenced(rail, vOut));
}
