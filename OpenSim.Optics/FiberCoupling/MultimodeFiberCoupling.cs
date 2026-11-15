using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.FiberCoupling;

/// <summary>What one multimode coupling estimate is asked to do (lengths meters, angles degrees).</summary>
public sealed record MultimodeCouplingRequest
{
    public int Field { get; init; }
    public int Wavelength { get; init; }
    public int Sampling { get; init; } = 128;
    public SourceFiber Source { get; init; } = new() { Ignore = true };
    /// <summary>The fiber's acceptance NA; 0 to take √(n_core² − n_cladding²).</summary>
    public double FiberNa { get; init; } = 0.2;
    public double CoreIndex { get; init; }
    public double CladdingIndex { get; init; }
    /// <summary>The core's radius, meters.</summary>
    public double CoreRadius { get; init; } = 25e-6;
    public double DecenterX { get; init; }
    public double DecenterY { get; init; }
    public double TiltX { get; init; }
    public double TiltY { get; init; }
    public bool AlignToChiefRay { get; init; }
}

/// <summary>The result of <see cref="MultimodeFiberCoupling.Compute"/>.</summary>
public sealed record MultimodeCouplingResult
{
    /// <summary>The source power the core accepts over all the source power.</summary>
    public required double Efficiency { get; init; }
    /// <summary>The source power reaching the image over all the source power.</summary>
    public required double SystemEfficiency { get; init; }
    public required double NumericalAperture { get; init; }
    public required int Accepted { get; init; }
    public required int Lit { get; init; }
    public required int Total { get; init; }
    public required Vector3D CoreCentre { get; init; }
    public required FiberPupil Pupil { get; init; }
    /// <summary>For each lit node: its image intercept relative to the core centre (x, y, meters)
    /// and whether the core accepts it.</summary>
    public required IReadOnlyList<(double X, double Y, bool Accepted)> Intercepts { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// The geometric multimode estimate (OP-50; docs/Optics-Conventions.md §68). OpticStudio's
/// Multi-Mode Coupling is the Geometric Image Analysis with its NA setting: "Place a circular
/// aperture at or just before the image surface with the appropriate maximum radial aperture
/// representing the core size. Then set the NA ... to the maximum acceptance NA of the fiber.
/// The percent efficiency will then be calculated by summing all the rays that pass the core
/// aperture within the specified NA", with NA = √(n_i² − n_o²) for a core of index n_i in a
/// cladding of index n_o (help, "Multi-Mode Coupling", saved under
/// plans/sources/optics/op-50-help/). Here the core is a disc of the given radius on the image
/// surface, centred on its vertex or the chief ray plus a decenter, and a ray is accepted when
/// it lands in the disc and n·sin θ ≤ NA with θ its angle to the fiber's axis (the image z axis,
/// tilted as the single-mode receiver is). Each ray weighs the source power its node carries
/// (<see cref="FiberPupil"/>), so the source fiber or the apodization is honoured.
/// </summary>
public static class MultimodeFiberCoupling
{
    public static MultimodeCouplingResult Compute(SequentialLens lens, OpticalSystemData system, MultimodeCouplingRequest request,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string who = "Multimode fiber coupling";
        double na = request.FiberNa;
        if (na == 0)
        {
            if (!(request.CoreIndex > request.CladdingIndex) || !(request.CladdingIndex > 0))
                throw new ArgumentException($"{who}: with the NA 0, the core index ({request.CoreIndex}) must exceed the cladding index ({request.CladdingIndex}) and both be positive, so NA = √(n_core² − n_cladding²).");
            na = Math.Sqrt(request.CoreIndex * request.CoreIndex - request.CladdingIndex * request.CladdingIndex);
        }
        if (!(na > 0) || !double.IsFinite(na))
            throw new ArgumentException($"{who}: the fiber NA must be positive (it is {na}).");
        if (!(request.CoreRadius > 0) || !double.IsFinite(request.CoreRadius))
            throw new ArgumentException($"{who}: the core radius must be positive (it is {request.CoreRadius} m).");
        if (Math.Abs(request.TiltX) >= 90 || Math.Abs(request.TiltY) >= 90)
            throw new ArgumentException($"{who}: the fiber's tilts must be inside ±90° (they are {request.TiltX}° and {request.TiltY}°).");
        var pupil = FiberPupil.Trace(lens, system, request.Field, request.Wavelength, request.Sampling, request.Source, who, cancellation);
        var chief = pupil.ChiefRay;
        double sense = chief.Direction.Z >= 0 ? 1 : -1;
        var (axX, axY, axZ) = GaussianFiberMode.TiltedAxis(request.TiltX, request.TiltY, sense);
        var axis = new Vector3D(axX, axY, axZ);
        var centre = (request.AlignToChiefRay ? new Vector3D(chief.Position.X, chief.Position.Y, 0) : Vector3D.Zero)
            + new Vector3D(request.DecenterX, request.DecenterY, 0);
        double n = pupil.ImageIndex, h = pupil.Step;
        double accepted = 0, lit = 0;
        int count = 0, litCount = 0;
        var intercepts = new List<(double, double, bool)>();
        foreach (var node in pupil.Nodes)
        {
            if (!node.Lit) continue;
            litCount++;
            lit += node.Power;
            double x = node.Ray.Position.X - centre.X, y = node.Ray.Position.Y - centre.Y;
            double sine = Vector3D.Cross(node.Ray.Direction, axis).Length;
            bool forward = Vector3D.Dot(node.Ray.Direction, axis) > 0;
            bool ok = forward && x * x + y * y <= request.CoreRadius * request.CoreRadius && n * sine <= na;
            if (ok) { accepted += node.Power; count++; }
            intercepts.Add((x, y, ok));
        }
        var notes = new List<string>(pupil.Notes)
        {
            $"Core: radius {request.CoreRadius * 1e3:g6} mm centred at ({centre.X * 1e3:g8}, {centre.Y * 1e3:g8}) mm on the image surface; acceptance NA {na:g6}{(request.FiberNa == 0 ? $" = √({request.CoreIndex:g6}² − {request.CladdingIndex:g6}²)" : "")}, angles measured from the fiber's axis ({axis.X:g6}, {axis.Y:g6}, {axis.Z:g6}).",
            "Geometric estimate: the source power of the rays that land in the core within the NA over all the source power (help, \"Multi-Mode Coupling\"). It ignores diffraction and the fiber's mode structure.",
        };
        return new MultimodeCouplingResult
        {
            Efficiency = accepted * h * h / pupil.SourcePower, SystemEfficiency = lit * h * h / pupil.SourcePower,
            NumericalAperture = na, Accepted = count, Lit = litCount, Total = pupil.Nodes.Count, CoreCentre = centre,
            Pupil = pupil, Intercepts = intercepts, Notes = notes,
        };
    }
}
