using System.Numerics;
using OpenSim.Optics.PhysicalOptics;

namespace OpenSim.Optics.FiberCoupling;

/// <summary>Where the receiving fiber of a POP fiber-coupling integral is centred (help,
/// "Physical Optics Propagation", Fiber Data tab, "Fiber Position").</summary>
public enum PopFiberPosition { ChiefRay, Vertex }

/// <summary>The receiving fiber of a POP fiber-coupling integral, in meters and degrees: the
/// fiber mode is any initial-beam type the help allows ("The fiber mode may also be defined using
/// all the same options allowed for defining initial beams", "Defining the Fiber Mode"), built on
/// the end beam's grid, with its own decenter, and tilted by a linear phase.</summary>
public sealed record PopFiberDefinition
{
    public PopBeamDefinition Mode { get; init; } = new();
    public double TiltX { get; init; }
    public double TiltY { get; init; }
    public PopFiberPosition Position { get; init; } = PopFiberPosition.ChiefRay;
    public bool IgnorePolarization { get; init; }
}

/// <summary>The result of <see cref="PopFiberCoupling.Compute"/>.</summary>
/// <param name="SystemEfficiency">The beam power at the end surface over its power at the start.</param>
/// <param name="ReceiverEfficiency">|∫Fr·W*|²/(∫|Fr|²·∫|W|²) on the end beam's grid.</param>
/// <param name="AmplitudeX">The normalised overlap ∫Fr·W*/√(∫|Fr|²·∫|W|²) of the Ex field
/// (|AmplitudeX|² = receiver efficiency).</param>
public sealed record PopFiberCouplingResult(double SystemEfficiency, double ReceiverEfficiency, Complex AmplitudeX, IReadOnlyList<string> Notes)
{
    public double Coupling => SystemEfficiency * ReceiverEfficiency;
}

/// <summary>
/// Fiber coupling of a physical optics propagation (OP-50; docs/Optics-Conventions.md §68), from
/// the help's "Computing Fiber Coupling", "Where the Integral is Computed", "Defining the Fiber
/// Mode", "Decenters and Tilts" and "Choosing the Location for the Receiving Fiber" (saved under
/// plans/sources/optics/op-49-help/ and op-50-help/). The receiver efficiency is the help's
/// normalised overlap T = |∫∫Fr·W′|²/(∫∫Fr·Fr′·∫∫W·W′) of the fiber mode with the beam at the
/// end surface, the integral computed where the end surface is ("the fiber mode parameters must
/// correspond to the mode at the receiving fiber"); the system efficiency is "the fraction of beam
/// power that transmits from the starting surface to the surface at which the fiber coupling is
/// computed"; the total is their product.
/// <para>
/// Both fields are taken with their reference phase put back (<see cref="PopField.TrueField"/>),
/// so a curved beam meets a waist-shaped mode as it physically does. Tilt is the help's "linear
/// phase shift": the mode is multiplied by e^{+ik·(αt·x + βt·y)} (the propagation's physics
/// convention, a wave e^{+ikz}; k = 2π·n/λ in the end medium) with (αt, βt) the transverse
/// direction cosines of the axis tilted about x and then y (<see cref="GaussianFiberMode.TiltedAxis"/>).
/// The propagation is scalar (OP-49 refuses polarized propagation), so the beam is unpolarized and
/// only the x field is used (help: "If the beam is not polarized, then the fiber mode is not
/// polarized and only the X-direction of the E field of the fiber mode is used"). The beam
/// follows an on-axis chief ray along the axis of an axial lens (OP-49's scope), so the chief ray
/// meets the end surface at its vertex and the two fiber positions coincide.
/// </para>
/// </summary>
public static class PopFiberCoupling
{
    public static PopFiberCouplingResult Compute(PopRun run, PopFiberDefinition fiber, LensUnit unit)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(fiber);
        const string who = "Physical optics fiber coupling";
        var beam = run.Field;
        var type = fiber.Mode.Type;
        if (type == PopBeamType.Multimode)
            throw new NotSupportedException($"{who}: a multimode (ZMM) fiber mode is an incoherent sum of modes, not one coherent mode to overlap with; choose another fiber type.");
        if (Math.Abs(fiber.TiltX) >= 90 || Math.Abs(fiber.TiltY) >= 90 || !double.IsFinite(fiber.TiltX) || !double.IsFinite(fiber.TiltY))
            throw new ArgumentException($"{who}: the fiber tilts must be inside ±90° (they are {fiber.TiltX}° and {fiber.TiltY}°).");
        var notes = new List<string>();
        var mode = PopBeams.Create(fiber.Mode, beam.Nx, beam.Ny, beam.WidthX, beam.WidthY, beam.Pilot.AirWavelength, beam.Pilot.Index, unit, notes);
        if (mode.Nx != beam.Nx || mode.Ny != beam.Ny
            || Math.Abs(mode.Dx - beam.Dx) > 1e-9 * beam.Dx || Math.Abs(mode.Dy - beam.Dy) > 1e-9 * beam.Dy)
            throw new ArgumentException($"{who}: the fiber mode file's grid ({mode.Nx} x {mode.Ny}, spacing {mode.Dx:g6} x {mode.Dy:g6} m) is not the end beam's ({beam.Nx} x {beam.Ny}, spacing {beam.Dx:g6} x {beam.Dy:g6} m); the overlap is taken sample by sample, so they must match.");
        var (ax, ay, _) = GaussianFiberMode.TiltedAxis(fiber.TiltX, fiber.TiltY, 1);
        double k = beam.Wavenumber;
        Complex overlap = Complex.Zero;
        double fiberPower = 0, beamPower = 0;
        for (int r = 0; r < beam.Ny; r++)
        {
            double y = beam.Y(r);
            for (int c = 0; c < beam.Nx; c++)
            {
                double x = beam.X(c);
                var f = mode.TrueField(r, c);
                if (ax != 0 || ay != 0) f *= Complex.FromPolarCoordinates(1, k * (ax * x + ay * y));
                var w = beam.TrueField(r, c);
                overlap += f * Complex.Conjugate(w);
                fiberPower += f.Real * f.Real + f.Imaginary * f.Imaginary;
                beamPower += w.Real * w.Real + w.Imaginary * w.Imaginary;
            }
        }
        if (!(beamPower > 0)) throw new InvalidOperationException($"{who}: the beam at the end surface has no power.");
        if (!(fiberPower > 0)) throw new InvalidOperationException($"{who}: the fiber mode has no power on the end beam's grid (is it decentered off the array?).");
        double initial = run.Initial.Power;
        if (!(initial > 0)) throw new InvalidOperationException($"{who}: the initial beam has no power.");
        var amplitude = overlap / Math.Sqrt(fiberPower * beamPower);
        double t = amplitude.Magnitude * amplitude.Magnitude;
        notes.Add($"Fiber mode: {type} on the end beam's {beam.Nx} x {beam.Ny} grid, tilted to axis direction cosines ({ax:g6}, {ay:g6}); position {(fiber.Position == PopFiberPosition.ChiefRay ? "chief ray" : "vertex")} (the same point here: the beam follows an on-axis chief ray along the axis).");
        notes.Add("Receiver efficiency T = |Σ Fr·W*|²/(Σ|Fr|²·Σ|W|²) over the end beam's samples, both with their reference phase put back; system efficiency = end power / start power.");
        if (fiber.IgnorePolarization)
            notes.Add("Ignore polarization: the propagation is scalar, so the fiber mode is unpolarized either way.");
        return new PopFiberCouplingResult(beam.Power / initial, t, amplitude, notes);
    }
}
