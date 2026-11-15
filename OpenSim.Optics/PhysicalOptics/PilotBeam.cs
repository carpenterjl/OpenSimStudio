using System.Numerics;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>
/// The pilot beam of physical optics propagation (OP-49; docs/Optics-Conventions.md §66): an
/// ideal Gaussian beam carried beside the sampled field to choose the propagator and to place
/// the reference sphere (OpticStudio 2025 R2 help, "The Pilot Beam": "a pilot beam is used to
/// assist the physical optics propagation algorithm in determining which propagation algorithm
/// to select ... The properties of the pilot beam are then used to determine if the actual
/// distribution is inside or outside the Rayleigh range").
/// <para>
/// It is held as the complex beam parameter q = z + i·z_R of OP-48 (<see cref="GaussianBeamState"/>,
/// Siegman's convention 1/q = 1/R − iλ/(πw²)), in the medium the beam is in: z is the distance
/// the beam has travelled past the pilot's waist along its direction of travel (negative before
/// the waist), z_R the Rayleigh range in the medium. <see cref="Index"/> is the medium's index
/// relative to the system's air (always positive here; the direction of travel is carried by
/// the propagation engine, so a mirror needs no sign).
/// </para>
/// </summary>
public readonly record struct PilotBeam
{
    /// <param name="q">z + i·z_R in the medium, meters; z_R must be positive.</param>
    /// <param name="index">The medium's index relative to the system's air (positive).</param>
    /// <param name="airWavelength">The wavelength in the system's air, meters.</param>
    public PilotBeam(Complex q, double index, double airWavelength)
    {
        if (!(q.Imaginary > 0) || !double.IsFinite(q.Imaginary) || !double.IsFinite(q.Real))
            throw new InvalidOperationException(
                $"Pilot beam: the beam parameter became {q} (its Rayleigh range must be positive and finite).");
        if (!(index > 0) || !double.IsFinite(index))
            throw new ArgumentOutOfRangeException(nameof(index), index, "Pilot beam: the medium's index must be positive.");
        if (!(airWavelength > 0) || !double.IsFinite(airWavelength))
            throw new ArgumentOutOfRangeException(nameof(airWavelength), airWavelength, "Pilot beam: the wavelength must be positive.");
        Q = q;
        Index = index;
        AirWavelength = airWavelength;
    }

    /// <summary>A pilot beam whose waist of radius <paramref name="waist"/> lies
    /// <paramref name="distanceFromWaist"/> behind the beam (z past the waist).</summary>
    public static PilotBeam FromWaist(double waist, double distanceFromWaist, double index, double airWavelength)
    {
        if (!(waist > 0) || !double.IsFinite(waist))
            throw new ArgumentOutOfRangeException(nameof(waist), waist, "Pilot beam: the waist radius must be positive.");
        double zR = Math.PI * waist * waist * index / airWavelength;
        return new PilotBeam(new Complex(distanceFromWaist, zR), index, airWavelength);
    }

    /// <summary>z + i·z_R in the medium, meters.</summary>
    public Complex Q { get; }

    /// <summary>The medium's index relative to the system's air.</summary>
    public double Index { get; }

    /// <summary>The wavelength in the system's air, meters.</summary>
    public double AirWavelength { get; }

    /// <summary>The wavelength in the medium, λ/n.</summary>
    public double MediumWavelength => AirWavelength / Index;

    /// <summary>The distance past the waist along the travel (negative before the waist).</summary>
    public double DistanceFromWaist => Q.Real;

    public double RayleighRange => Q.Imaginary;

    /// <summary>The waist radius, √(λ_n·z_R/π) (help, "The Pilot Beam": z_R = πω0²/λ).</summary>
    public double WaistRadius => Math.Sqrt(MediumWavelength * RayleighRange / Math.PI);

    /// <summary>ω(z) = ω0·√(1 + (z/z_R)²).</summary>
    public double BeamRadius
    {
        get { double r = DistanceFromWaist / RayleighRange; return WaistRadius * Math.Sqrt(1 + r * r); }
    }

    /// <summary>R(z) = z + z_R²/z; infinite at the waist.</summary>
    public double PhaseRadius => DistanceFromWaist == 0 ? double.PositiveInfinity
        : DistanceFromWaist + RayleighRange * (RayleighRange / DistanceFromWaist);

    /// <summary>atan(z/z_R), the help's Gouy shift.</summary>
    public double GouyPhase => Math.Atan(DistanceFromWaist / RayleighRange);

    /// <summary>The far-field half-angle divergence, atan(ω0/z_R) (help, "The Pilot Beam").</summary>
    public double Divergence => Math.Atan(WaistRadius / RayleighRange);

    /// <summary>True when the beam is inside the pilot's Rayleigh range, |z| &lt; z_R: the field
    /// is then referenced to a plane and propagated by the angular spectrum.</summary>
    public bool IsInsideRayleighRange => Math.Abs(DistanceFromWaist) < RayleighRange;

    /// <summary>The pilot after a step of <paramref name="distance"/> along the travel.</summary>
    public PilotBeam Propagated(double distance) => new(Q + distance, Index, AirWavelength);

    /// <summary>
    /// The pilot after a thin element of optical power <paramref name="power"/> (1/m, the
    /// quadratic coefficient of the optical path the element adds, OPL = −φ·r²/2) into a medium
    /// of index <paramref name="indexAfter"/>: n′/q′ = n/q − φ. This is OP-48's reduced-q rule
    /// 1/q̂′ = 1/q̂ − φ with q̂ = q/n (<see cref="GaussianBeamTrace"/>).
    /// </summary>
    public PilotBeam ThroughPower(double power, double indexAfter)
    {
        var reduced = Q / Index;
        var after = GaussianBeamTrace.Transform(new ParaxialMatrix(1, 0, -power, 1), reduced);
        return new PilotBeam(after * indexAfter, indexAfter, AirWavelength);
    }

    /// <summary>OP-48's state of this pilot (waist, radius, Rayleigh range, wavefront radius).</summary>
    public GaussianBeamState State(double gouyPhase = 0) =>
        GaussianBeamState.From(Q / Index, Index, AirWavelength, 1, gouyPhase);
}
