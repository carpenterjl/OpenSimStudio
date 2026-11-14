using System.Numerics;

namespace OpenSim.Optics.Paraxial;

/// <summary>
/// The input beam in one transverse plane (x or y): the waist radius of the beam as it is (the
/// 1/e² intensity radius, meters), the waist's position as a signed distance along z from
/// surface 1's vertex (negative in front of surface 1, as every object-space position of
/// docs/Optics-Conventions.md §11), and the beam quality M² ≥ 1. With M² &gt; 1 the waist is
/// the real beam's; the embedded Gaussian has the waist W0/M (§14).
/// </summary>
public readonly record struct GaussianBeamPlane(double WaistRadius, double WaistPosition, double MSquared = 1)
{
    /// <summary>Adds one line per problem, naming the plane.</summary>
    internal void Validate(string plane, ICollection<string> problems)
    {
        if (!double.IsFinite(WaistRadius) || !(WaistRadius > 0))
            problems.Add($"Gaussian beam, {plane}: the waist radius must be a positive number (it is {WaistRadius}).");
        if (!double.IsFinite(WaistPosition))
            problems.Add($"Gaussian beam, {plane}: the waist position must be a number (it is {WaistPosition}).");
        if (!double.IsFinite(MSquared) || !(MSquared >= 1))
            problems.Add($"Gaussian beam, {plane}: M² must be a number of at least 1 (it is {MSquared}); 1 is a perfect Gaussian.");
    }
}

/// <summary>
/// The input beam in both transverse planes. Different x and y values give a simple
/// astigmatic beam, whose two planes propagate independently through the rotationally
/// symmetric paraxial system (§14). A generally astigmatic (twisted) beam is not modelled.
/// </summary>
public sealed record GaussianBeamInput(GaussianBeamPlane X, GaussianBeamPlane Y)
{
    /// <summary>A round beam: the same waist, position and M² in x and y.</summary>
    public static GaussianBeamInput Round(double waistRadius, double waistPosition, double mSquared = 1)
    {
        var plane = new GaussianBeamPlane(waistRadius, waistPosition, mSquared);
        return new GaussianBeamInput(plane, plane);
    }

    public bool IsRound => X == Y;

    /// <summary>Every problem, one actionable line each; empty when the beam can be traced.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        X.Validate("x", problems);
        Y.Validate("y", problems);
        return problems;
    }
}

/// <summary>
/// The beam in one plane at one point of the system, derived from the reduced complex beam
/// parameter q̂ = q/n (n the signed index of the space, §14). Lengths are meters, angles
/// radians. The sizes are the real beam's (the embedded Gaussian's times M) unless named
/// embedded.
/// </summary>
public sealed record GaussianBeamState
{
    /// <summary>q̂ = q/n with n the signed index: the quantity the ABCD matrices of
    /// <see cref="ParaxialMatrix"/> act on by q̂′ = (A·q̂ + B)/(C·q̂ + D).</summary>
    public required Complex ReducedQ { get; init; }

    /// <summary>The signed index of the space the beam is in (negative after an odd number of
    /// mirrors, §11).</summary>
    public required double SignedIndex { get; init; }

    public required double MSquared { get; init; }

    /// <summary>The beam radius (1/e² intensity), M times the embedded one.</summary>
    public required double BeamRadius { get; init; }

    /// <summary>The embedded Gaussian's beam radius.</summary>
    public required double EmbeddedBeamRadius { get; init; }

    /// <summary>The waist radius of the beam in this space, M times the embedded one.</summary>
    public required double WaistRadius { get; init; }

    public required double EmbeddedWaistRadius { get; init; }

    /// <summary>The waist's position in this space as a signed distance along z from this
    /// point (negative toward −z), the same sign rule as a thickness: −n·Re q̂.</summary>
    public required double WaistOffset { get; init; }

    /// <summary>The distance the beam has travelled past its waist, along its own direction
    /// of travel (negative before the waist): Re q = |n|·Re q̂.</summary>
    public required double DistanceFromWaist { get; init; }

    /// <summary>The Rayleigh range in this space, Im q = |n|·Im q̂; the same for the real and
    /// the embedded beam.</summary>
    public required double RayleighRange { get; init; }

    /// <summary>The far-field half-angle divergence (1/e² radius) of the real beam: M times
    /// the embedded beam's w0/z_R.</summary>
    public required double Divergence { get; init; }

    /// <summary>The wavefront's radius of curvature, positive when it diverges (centre of
    /// curvature behind the beam along its direction of travel), infinite at a waist.</summary>
    public required double WavefrontRadius { get; init; }

    /// <summary>The Gouy phase accumulated since surface 1, radians: the sum over the spaces
    /// crossed of atan(z₂/z_R) − atan(z₁/z_R). Positive along the direction of travel.</summary>
    public required double GouyPhase { get; init; }

    /// <summary>The state of q̂ in a space of signed index <paramref name="signedIndex"/> at a
    /// wavelength (meters) measured in the system's air, which is the wavelength in a medium of
    /// relative index 1 (§2), so in a medium of relative index n it is λ/n.</summary>
    public static GaussianBeamState From(Complex reducedQ, double signedIndex, double wavelength, double mSquared, double gouyPhase)
    {
        double n = Math.Abs(signedIndex);
        double z = n * reducedQ.Real, zR = n * reducedQ.Imaginary;
        if (!(zR > 0) || !double.IsFinite(zR) || !double.IsFinite(z))
            throw new InvalidOperationException(
                $"Gaussian beam: the complex beam parameter became {reducedQ} (it must have a positive, finite imaginary part); the system's matrices are not those of a lossless paraxial system.");
        double lambda = wavelength / n;
        double w0 = Math.Sqrt(lambda * zR / Math.PI);
        double ratio = z / zR;
        double w = w0 * Math.Sqrt(1 + ratio * ratio);
        double m = Math.Sqrt(mSquared);
        return new GaussianBeamState
        {
            ReducedQ = reducedQ,
            SignedIndex = signedIndex,
            MSquared = mSquared,
            BeamRadius = m * w,
            EmbeddedBeamRadius = w,
            WaistRadius = m * w0,
            EmbeddedWaistRadius = w0,
            WaistOffset = -signedIndex * reducedQ.Real,
            DistanceFromWaist = z,
            RayleighRange = zR,
            Divergence = m * w0 / zR,
            WavefrontRadius = z == 0 ? double.PositiveInfinity : z + zR * (zR / z),
            GouyPhase = gouyPhase,
        };
    }
}

/// <summary>Beam radii sampled along the system, for the beam-size plot.</summary>
public sealed record GaussianBeamProfile
{
    /// <summary>Distance along the axis from surface 1, unfolded at mirrors (the sum of
    /// |thickness|), meters.</summary>
    public required IReadOnlyList<double> Path { get; init; }
    public required IReadOnlyList<double> RadiusX { get; init; }
    public required IReadOnlyList<double> RadiusY { get; init; }
    public required IReadOnlyList<double> EmbeddedRadiusX { get; init; }
    public required IReadOnlyList<double> EmbeddedRadiusY { get; init; }
    /// <summary>The path coordinate of each surface, surface 1 first, the image surface last.</summary>
    public required IReadOnlyList<double> SurfacePath { get; init; }
}

/// <summary>
/// A paraxial Gaussian beam carried surface by surface through a <see cref="ParaxialSystem"/>
/// (OP-48; docs/Optics-Conventions.md §14). The reduced beam parameter q̂ = q/n goes through
/// the same transfers and surface powers as the y-nu rays: a transfer adds t/n, a surface of
/// power φ makes 1/q̂′ = 1/q̂ − φ. The x and y planes are carried independently.
/// </summary>
public sealed class GaussianBeamTrace
{
    private readonly Complex[] _qx, _qy;

    public ParaxialSystem Model { get; }
    public GaussianBeamInput Input { get; }

    /// <summary>The wavelength in meters, in the system's air (the table's wavelength).</summary>
    public double Wavelength { get; }

    /// <summary>The beam at each surface in x: entry 0 is the input beam arriving at surface 1's
    /// vertex plane, in object space; entry j (1 to the last surface) the beam just after
    /// surface j; the image surface's entry the beam arriving at it.</summary>
    public IReadOnlyList<GaussianBeamState> X { get; }

    /// <summary>The same in y.</summary>
    public IReadOnlyList<GaussianBeamState> Y { get; }

    private GaussianBeamTrace(ParaxialSystem model, GaussianBeamInput input)
    {
        Model = model;
        Input = input;
        Wavelength = OpticsConventions.MicrometersToMeters(model.WavelengthMicrometers);
        (X, _qx) = Carry(input.X);
        (Y, _qy) = Carry(input.Y);
    }

    /// <summary>Traces <paramref name="input"/> through <paramref name="model"/> at the model's
    /// wavelength. Throws <see cref="ArgumentException"/> listing every problem of the input.</summary>
    public static GaussianBeamTrace Create(ParaxialSystem model, GaussianBeamInput input)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(input);
        var problems = input.Validate();
        if (problems.Count > 0)
            throw new ArgumentException("Gaussian beam refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        return new GaussianBeamTrace(model, input);
    }

    /// <summary>The reduced beam parameter at surface 1's vertex plane in object space for an
    /// input plane: q = −z_w + i·z_R with z_R = π·W0²·n/(M²·λ), divided by the object-space
    /// index n (positive: no mirror comes before surface 1).</summary>
    public static Complex InputReducedQ(GaussianBeamPlane plane, double objectIndex, double wavelength)
    {
        double zR = Math.PI * plane.WaistRadius * plane.WaistRadius * objectIndex / (plane.MSquared * wavelength);
        return new Complex(-plane.WaistPosition, zR) / objectIndex;
    }

    /// <summary>q̂′ = (A·q̂ + B)/(C·q̂ + D).</summary>
    public static Complex Transform(ParaxialMatrix m, Complex reducedQ) => (m.A * reducedQ + m.B) / (m.C * reducedQ + m.D);

    private (GaussianBeamState[] States, Complex[] Q) Carry(GaussianBeamPlane plane)
    {
        var lens = Model.Lens;
        int count = lens.Surfaces.Count, image = lens.ImageIndex;
        var n = Model.SignedIndices;
        var states = new GaussianBeamState[count];
        var qs = new Complex[count];
        var q = InputReducedQ(plane, n[0], Wavelength);
        double gouy = 0;
        qs[0] = q;
        states[0] = GaussianBeamState.From(q, n[0], Wavelength, plane.MSquared, 0);
        for (int j = 1; j < count; j++)
        {
            if (j > 1)
            {
                double before = Math.Atan(q.Real / q.Imaginary);
                q += lens.Surfaces[j - 1].Thickness / n[j - 1];
                gouy += Math.Atan(q.Real / q.Imaginary) - before;
            }
            if (j < image && Model.Powers[j] != 0) q = Transform(ParaxialMatrix.Refraction(Model.Powers[j]), q);
            qs[j] = q;
            states[j] = GaussianBeamState.From(q, n[j], Wavelength, plane.MSquared, gouy);
        }
        return (states, qs);
    }

    /// <summary>The beam radii at <paramref name="pointsPerGap"/> + 1 evenly spaced points of
    /// every gap from surface 1 to the image surface (both ends included).</summary>
    public GaussianBeamProfile Profile(int pointsPerGap)
    {
        if (pointsPerGap < 1)
            throw new ArgumentOutOfRangeException(nameof(pointsPerGap), pointsPerGap, "Gaussian beam profile: at least one interval per gap is needed.");
        var lens = Model.Lens;
        var n = Model.SignedIndices;
        int image = lens.ImageIndex;
        var path = new List<double>();
        var rx = new List<double>();
        var ry = new List<double>();
        var ex = new List<double>();
        var ey = new List<double>();
        var surfacePath = new List<double> { 0 };
        double start = 0;
        for (int j = 1; j < image; j++)
        {
            double t = lens.Surfaces[j].Thickness, reduced = t / n[j];
            for (int k = 0; k <= pointsPerGap; k++)
            {
                double f = (double)k / pointsPerGap;
                var sx = GaussianBeamState.From(_qx[j] + f * reduced, n[j], Wavelength, Input.X.MSquared, 0);
                var sy = GaussianBeamState.From(_qy[j] + f * reduced, n[j], Wavelength, Input.Y.MSquared, 0);
                path.Add(start + f * Math.Abs(t));
                rx.Add(sx.BeamRadius);
                ry.Add(sy.BeamRadius);
                ex.Add(sx.EmbeddedBeamRadius);
                ey.Add(sy.EmbeddedBeamRadius);
            }
            start += Math.Abs(t);
            surfacePath.Add(start);
        }
        return new GaussianBeamProfile
        {
            Path = path, RadiusX = rx, RadiusY = ry, EmbeddedRadiusX = ex, EmbeddedRadiusY = ey, SurfacePath = surfacePath,
        };
    }
}
