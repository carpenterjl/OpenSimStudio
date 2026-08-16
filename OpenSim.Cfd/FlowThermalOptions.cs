using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

/// <summary>
/// Turns the flow solve into a coupled flow + fluid-energy solve: temperature is
/// transported (upwind advection, implicit diffusion) and — when gravity is set —
/// fed back into the momentum equations as the Boussinesq buoyancy force
/// −g·β·(T − T_ambient). Null on the solver means "no energy equation": the momentum
/// path is bitwise the flow-only solver, which is a pinned gate.
/// </summary>
public sealed record FlowThermalOptions
{
    /// <summary>Ambient (reference) temperature [K]: the initial fluid temperature, the
    /// temperature of anything flowing IN through inlets and backflow, and the Boussinesq
    /// reference the buoyancy force is measured from.</summary>
    public required double AmbientTemperature { get; init; }

    /// <summary>Fixed temperature [K] of each domain-box face; null = adiabatic.
    /// An <see cref="OpenSim.Core.Model.FlowFaceKind.InletVelocity"/> face without an
    /// explicit temperature is Dirichlet at ambient (the incoming stream's temperature),
    /// every other null face is adiabatic / zero-gradient.</summary>
    public double? XMinTemperature { get; init; }
    public double? XMaxTemperature { get; init; }
    public double? YMinTemperature { get; init; }
    public double? YMaxTemperature { get; init; }
    public double? ZMinTemperature { get; init; }
    public double? ZMaxTemperature { get; init; }

    /// <summary>Gravity [m/s²]. Zero (the default) disables buoyancy ENTIRELY — the
    /// momentum right-hand sides are not even touched with a zero term, so the
    /// flow-only bitwise pin holds trivially.</summary>
    public Vector3D Gravity { get; init; } = default;

    /// <summary>Whether the Boussinesq force is active.</summary>
    public bool IncludeBuoyancy => Gravity.Length > 0;

    /// <summary>The face temperature for one axis/side, or null (adiabatic).</summary>
    public double? FaceTemperature(int axis, bool high) => axis switch
    {
        0 => high ? XMaxTemperature : XMinTemperature,
        1 => high ? YMaxTemperature : YMinTemperature,
        _ => high ? ZMaxTemperature : ZMinTemperature
    };

    /// <summary>The largest temperature excursion any boundary imposes relative to
    /// ambient [K] — the scale the march residual and the Boussinesq-validity note use.
    /// Zero when nothing is imposed (wall temperatures may still widen it at solve time).</summary>
    public double BoundarySpan()
    {
        double span = 0;
        foreach (var t in new[] { XMinTemperature, XMaxTemperature, YMinTemperature,
                     YMaxTemperature, ZMinTemperature, ZMaxTemperature })
            if (t is { } value)
                span = Math.Max(span, Math.Abs(value - AmbientTemperature));
        return span;
    }
}
