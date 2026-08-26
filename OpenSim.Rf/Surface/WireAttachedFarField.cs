using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// Far field of a solved wire-attached sheet: the radiation vector of the WHOLE structure, which
/// is the sum of three currents that the solve already keeps apart —
///
/// <code>
///   N(r̂) = N_wire(with the junction coefficient at the contact node)
///         + N_sheet(the RAW, un-folded edge currents)
///         + I_J · N_junction(the 1/ρ disc + its half-RWG continuations)
/// </code>
///
/// <para>Summing them is the whole of the composition, because a radiation vector is linear in
/// the current. The one thing that must NOT happen is double counting: the solution's
/// <c>EdgeCurrents</c> already carry the junction's transported current folded onto the fan outer
/// edges for generic consumers, so the far field reads <c>RawEdgeCurrents</c> and adds the
/// junction's own transform exactly — the fold is a mesh-scale approximation of a current whose
/// transform is available in closed form, and the power ledger is precisely the gate that can
/// tell the difference.</para>
///
/// <para>Free space, so the full sphere is integrated: there is no image half-space here (a
/// wire-attached sheet over a PEC ground is refused by the solver).</para>
/// </summary>
public static class WireAttachedFarField
{
    public static FarFieldPattern Compute(SurfaceStructure surface, WireStructure wire,
        WireAttachedSolution solution, int thetaCount = 32, int phiCount = 64)
    {
        double omega = 2 * Math.PI * solution.FrequencyHz;
        double k = omega / RfConstants.SpeedOfLight;

        var junction = WireSurfaceJunction.Attach(wire, surface);
        var fan = junction.Fan;
        // The fan carries the junction unknown with the SAME sign the assembly gave it — the
        // disc runs inward when the wire's half hat carries current away from the contact.
        Complex junctionCurrent =
            junction.DiscSign * solution.WireCurrents[junction.WireBasis];

        var wireSolution = new MomSolution(
            solution.FrequencyHz, solution.InputImpedance, solution.WireCurrents);
        // (the wire leg itself keeps its natural orientation: the port is the thin-wire one)
        var sheetSolution = new SurfaceMomSolution(
            solution.FrequencyHz, solution.InputImpedance, solution.RawEdgeCurrents);

        return FarFieldEvaluator.IntegratePattern(omega, hemisphere: false, thetaCount, phiCount,
            direction =>
            {
                var nw = FarFieldEvaluator.RadiationVector(wire, wireSolution, k, direction);
                var ns = SurfaceFarFieldEvaluator.RadiationVector(surface, sheetSolution, k, direction);
                var nj = fan.CurrentTransform3D(surface, k, direction);
                return (nw.X + ns.X + junctionCurrent * nj.Jx,
                        nw.Y + ns.Y + junctionCurrent * nj.Jy,
                        nw.Z + ns.Z + junctionCurrent * nj.Jz);
            });
    }
}
