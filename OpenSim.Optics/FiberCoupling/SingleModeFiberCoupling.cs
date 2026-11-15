using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.FiberCoupling;

/// <summary>What one single-mode coupling computation is asked to do (lengths meters, angles
/// degrees); the settings of OpticStudio's Single Mode Coupling (help, "Single Mode Coupling").</summary>
public sealed record SingleModeCouplingRequest
{
    /// <summary>0-based field row.</summary>
    public int Field { get; init; }
    /// <summary>0-based wavelength row.</summary>
    public int Wavelength { get; init; }
    /// <summary>The pupil grid, n × n.</summary>
    public int Sampling { get; init; } = 64;
    public SourceFiber Source { get; init; } = new();
    /// <summary>The receiving fiber's numerical aperture on x and y in image space.</summary>
    public double ReceiverNaX { get; init; } = 0.1;
    public double ReceiverNaY { get; init; } = 0.1;
    /// <summary>Tilt about x, then about the new y, degrees.</summary>
    public double TiltX { get; init; }
    public double TiltY { get; init; }
    /// <summary>The receiving fiber's shift from its nominal position, image surface frame, meters.</summary>
    public double DecenterX { get; init; }
    public double DecenterY { get; init; }
    public double DecenterZ { get; init; }
    /// <summary>Centre the receiving fiber on the chief ray's image intercept instead of the image
    /// surface's vertex.</summary>
    public bool AlignReceiverToChiefRay { get; init; }
    /// <summary>Use the Huygens PSF for the beam at the fiber instead of the direct ray-based integration.</summary>
    public bool UseHuygens { get; init; }
    /// <summary>Polarization-dependent transmission (not built: refused).</summary>
    public bool UsePolarization { get; init; }
}

/// <summary>The result of <see cref="SingleModeFiberCoupling.Compute"/>.</summary>
public sealed record SingleModeCouplingResult
{
    /// <summary>S: the source power that passes the system over all the source power.</summary>
    public required double SystemEfficiency { get; init; }
    /// <summary>T: the normalised overlap |∫Fr·W*|²/(∫|Fr|²·∫|W|²).</summary>
    public required double ReceiverEfficiency { get; init; }
    /// <summary>S·T.</summary>
    public double Coupling => SystemEfficiency * ReceiverEfficiency;
    /// <summary>S·T with every phase dropped: the most any wavefront could couple with these amplitudes.</summary>
    public required double MaximumCoupling { get; init; }
    /// <summary>√S·η, η the normalised overlap ∫Fr·W*/√(∫|Fr|²·∫|W|²); |A|² = S·T.</summary>
    public required Complex AmplitudeCoupling { get; init; }
    /// <summary>The receiving fiber's centre, image surface frame, meters.</summary>
    public required Vector3D FiberCentre { get; init; }
    /// <summary>The receiving fiber's axis direction cosines on the image surface's x and y.</summary>
    public required (double Alpha, double Beta) FiberAxis { get; init; }
    public required double WaistX { get; init; }
    public required double WaistY { get; init; }
    public required FiberPupil Pupil { get; init; }
    /// <summary>The Huygens image of the beam on the fiber's plane (Huygens only).</summary>
    public PsfImage? Image { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Single-mode fiber coupling (OP-50; docs/Optics-Conventions.md §68), OpticStudio's Single Mode
/// Coupling read from its 2025 R2 help ("Single Mode Coupling", "Computing Fiber Coupling",
/// saved under plans/sources/optics/op-50-help/ and op-49-help/).
/// <para>
/// The help defines the system efficiency S, the power that passes the system over all the
/// power the source radiates, and the receiver efficiency
/// T = |∫∫Fr·W′ dx dy|² / (∫∫Fr·Fr′ dx dy · ∫∫W·W′ dx dy), W the wave from the system and Fr the
/// fiber mode (′ the complex conjugate); the coupling is S·T. Both are computed from the traced
/// pupil (<see cref="FiberPupil"/>).
/// </para>
/// <para>
/// <b>The wave as plane waves (derived).</b> Each lit pupil node is one plane wave of the beam
/// converging on the image, travelling along the ray's direction d with direction cosines
/// (α, β) on the image surface's x and y. Power is conserved along a ray tube: the node carries
/// P·h² of the source's power, spread over J·h² of direction space (J the image Jacobian), so
/// the spectral amplitude is √(P/J) and the phase is that of the ray at the fiber's centre C,
/// 2π·(OPD_abs + n·d·(Q − C)/λ) in turns (Q the ray's image intercept: OP-38's Huygens phase).
/// By Parseval the overlap in the fiber's plane equals the overlap of the spectra:
/// ∫Fr·W* dx dy ∝ Σ F(α, β)·√(P·J)·e^{−iφ}·h² and ∫|W|² ∝ Σ P·h², with
/// ∫|F|² = π·NAx·NAy/(2n²) (<see cref="GaussianFiberMode"/>), so
/// T = 2n²·h²·|Σ F·√(P·J)·e^{−iφ}|² / (π·NAx·NAy·Σ P). That sum is the ray-based ("fast")
/// algorithm.
/// </para>
/// <para>
/// <b>Huygens.</b> With "Use Huygens Integral" the beam is first summed on a grid in the fiber's
/// plane by OP-38's Huygens sum (<see cref="PsfEngine.Huygens"/>, the same plane waves), and the
/// overlap is the grid sum of the fiber mode exp(−u²/ω0x² − v²/ω0y²)·e^{−2πi·n·(αt·u + βt·v)/λ}
/// times the conjugate field. The two are the same integral evaluated in two spaces (Parseval);
/// they differ by the image grid's quadrature, which is chosen here to be exact to rounding (see
/// <see cref="ImageGrid"/>). OpticStudio's fast algorithm "cannot be computed accurately" in some
/// systems and then returns zero; that failure test is not documented and is not reproduced.
/// </para>
/// </summary>
public static class SingleModeFiberCoupling
{
    public static SingleModeCouplingResult Compute(SequentialLens lens, OpticalSystemData system, SingleModeCouplingRequest request,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string who = "Single mode fiber coupling";
        if (request.UsePolarization)
            throw new NotSupportedException($"{who}: \"Use polarization\" (coating and Fresnel transmission in the system efficiency) is not built. Clear the option.");
        var pupil = FiberPupil.Trace(lens, system, request.Field, request.Wavelength, request.Sampling, request.Source, who, cancellation);
        double n = pupil.ImageIndex;
        GaussianFiberMode.Check(request.ReceiverNaX, request.ReceiverNaY, n, $"{who}, receiving fiber");
        foreach (var (v, what) in new[] { (request.TiltX, "tilt about X"), (request.TiltY, "tilt about Y"), (request.DecenterX, "decenter X"), (request.DecenterY, "decenter Y"), (request.DecenterZ, "decenter Z") })
            if (!double.IsFinite(v)) throw new ArgumentException($"{who}: the receiving fiber's {what} is not finite.");
        if (Math.Abs(request.TiltX) >= 90 || Math.Abs(request.TiltY) >= 90)
            throw new ArgumentException($"{who}: the receiving fiber's tilts must be inside ±90° (they are {request.TiltX}° and {request.TiltY}°).");

        var chief = pupil.ChiefRay;
        double sense = chief.Direction.Z >= 0 ? 1 : -1;
        var axis = GaussianFiberMode.TiltedAxis(request.TiltX, request.TiltY, sense);
        var mode = new GaussianFiberMode(request.ReceiverNaX, request.ReceiverNaY, axis.X, axis.Y);
        var nominal = request.AlignReceiverToChiefRay ? chief.Position : Vector3D.Zero;
        var centre = nominal + new Vector3D(request.DecenterX, request.DecenterY, request.DecenterZ);
        double lambda = pupil.AirWavelength, h = pupil.Step, scale = n / lambda;

        double sumP = 0;
        Complex overlap = Complex.Zero;
        double magnitude = 0;
        foreach (var node in pupil.Nodes)
        {
            if (!node.Lit) continue;
            var d = node.Ray.Direction;
            double phase = node.PathWaves + scale * Vector3D.Dot(d, node.Ray.Position - centre);
            double f = mode.Spectrum(d.X, d.Y, n);
            double c = Math.Sqrt(node.Power * node.ImageJacobian);
            overlap += f * c * PsfEngine.Turn(-phase);
            magnitude += f * c;
            sumP += node.Power;
        }
        if (!(sumP > 0))
            throw new InvalidOperationException($"{who} (field {request.Field + 1}, wavelength {request.Wavelength + 1}): no source power reaches the image.");
        double systemEfficiency = sumP * h * h / pupil.SourcePower;
        double norm = 2 * n * n * h * h / (Math.PI * mode.NaX * mode.NaY * sumP);

        var notes = new List<string>(pupil.Notes)
        {
            $"Receiving fiber: Gaussian of NA {mode.NaX:g6} × {mode.NaY:g6} (waist λ/(π·NA) = {mode.WaistX(lambda) * 1e6:g6} × {mode.WaistY(lambda) * 1e6:g6} µm), centre ({centre.X * 1e3:g8}, {centre.Y * 1e3:g8}, {centre.Z * 1e3:g8}) mm in the image surface's frame, axis direction cosines ({axis.X:g6}, {axis.Y:g6}).",
            "Receiver efficiency T = |∫Fr·W*|²/(∫|Fr|²·∫|W|²) by Parseval over the rays' plane waves; system efficiency S = transmitted over radiated source power; the maximum coupling drops every phase.",
        };
        var warnings = new List<string>(pupil.Warnings);
        PsfImage? image = null;
        if (request.UseHuygens)
        {
            (overlap, image) = HuygensOverlap(pupil, mode, centre, notes, warnings, cancellation);
        }
        double t = norm * overlap.Magnitude * overlap.Magnitude;
        double tMax = norm * magnitude * magnitude;
        var eta = overlap * Math.Sqrt(norm);
        return new SingleModeCouplingResult
        {
            SystemEfficiency = systemEfficiency, ReceiverEfficiency = t, MaximumCoupling = systemEfficiency * tMax,
            AmplitudeCoupling = eta * Math.Sqrt(systemEfficiency), FiberCentre = centre, FiberAxis = (axis.X, axis.Y),
            WaistX = mode.WaistX(lambda), WaistY = mode.WaistY(lambda), Pupil = pupil, Image = image, Notes = notes, Warnings = warnings,
        };
    }

    /// <summary>
    /// The image grid of the Huygens overlap, in the fiber's plane: (2K + 1)² points spaced Δ on
    /// each axis, reaching ±5 waists. The fiber mode is down to e^{−25} at the edge. The grid sum
    /// of a band-limited integrand is its integral exactly when the sampling frequency 1/Δ
    /// exceeds the integrand's highest spatial frequency (Poisson summation): the beam's plane
    /// waves reach n·α_max/λ and the fiber's spectrum, centred on n·αt/λ, is down to e^{−25} at
    /// 5·NA/λ from it, so Δ = 0.9·λ/(n·α_max + n·|αt| + 5·NA) on each axis.
    /// </summary>
    public static (double[] U, double[] V) ImageGrid(FiberPupil pupil, GaussianFiberMode mode)
    {
        double n = pupil.ImageIndex, lambda = pupil.AirWavelength;
        double amaxX = 0, amaxY = 0;
        foreach (var node in pupil.Nodes)
        {
            if (!node.Lit) continue;
            amaxX = Math.Max(amaxX, Math.Abs(node.Ray.Direction.X));
            amaxY = Math.Max(amaxY, Math.Abs(node.Ray.Direction.Y));
        }
        double[] Axis(double amax, double at, double na, double waist)
        {
            double delta = 0.9 * lambda / (n * amax + n * Math.Abs(at) + 5 * na);
            int k = (int)Math.Ceiling(5 * waist / delta);
            if (2 * k + 1 > 1025)
                throw new InvalidOperationException($"Single mode fiber coupling, Huygens: the fiber's plane needs {2 * k + 1} points across (5 waists at {delta * 1e6:g4} µm spacing), more than 1025; use the ray-based integration.");
            var axis = new double[2 * k + 1];
            for (int i = 0; i < axis.Length; i++) axis[i] = (i - k) * delta;
            return axis;
        }
        return (Axis(amaxX, mode.AxisAlpha, mode.NaX, mode.WaistX(lambda)), Axis(amaxY, mode.AxisBeta, mode.NaY, mode.WaistY(lambda)));
    }

    /// <summary>The overlap sum Σ F·√(P·J)·e^{−iφ} recovered from the Huygens image: the grid sum
    /// of Fr·W* times Δu·Δv, over the constant that turns the Huygens amplitude into the field
    /// (derived in the class remarks: the grid sum equals (n/λ)²·h²·π·ω0x·ω0y times the
    /// spectral sum, and the Huygens amplitude is the ray sum over Σc).</summary>
    private static (Complex Overlap, PsfImage Image) HuygensOverlap(FiberPupil pupil, GaussianFiberMode mode, Vector3D centre,
        List<string> notes, List<string> warnings, CancellationToken cancellation)
    {
        double n = pupil.ImageIndex, lambda = pupil.AirWavelength;
        var rays = new List<HuygensRay>();
        double maxSlopeX = 0, maxSlopeY = 0;
        foreach (var node in pupil.Nodes)
        {
            if (!node.Lit) continue;
            rays.Add(new HuygensRay(node.Ray.Position, node.Ray.Direction, node.PathWaves, Math.Sqrt(node.Power * node.ImageJacobian)));
            maxSlopeX = Math.Max(maxSlopeX, Math.Abs(node.AlphaPx));
            maxSlopeY = Math.Max(maxSlopeY, Math.Abs(node.BetaPy));
        }
        var huygens = new HuygensPupil
        {
            Rays = rays, Wavelength = lambda, ImageIndex = n, Centre = centre, AxisX = Vector3D.UnitX, AxisY = Vector3D.UnitY,
            NodeSpacing = pupil.Step,
        };
        var (us, vs) = ImageGrid(pupil, mode);
        var image = PsfEngine.Huygens(huygens, us, vs, null, cancellation);
        double sumC = rays.Sum(r => r.Weight);
        double du = us.Length > 1 ? us[1] - us[0] : 0, dv = vs.Length > 1 ? vs[1] - vs[0] : 0;
        double wx = mode.WaistX(lambda), wy = mode.WaistY(lambda);
        Complex sum = Complex.Zero;
        for (int r = 0; r < vs.Length; r++)
            for (int c = 0; c < us.Length; c++)
            {
                double u = us[c], v = vs[r];
                var fiber = Math.Exp(-u * u / (wx * wx) - v * v / (wy * wy)) * PsfEngine.Turn(-n * (mode.AxisAlpha * u + mode.AxisBeta * v) / lambda);
                sum += fiber * Complex.Conjugate(image.Amplitude![r, c]);
            }
        var overlap = sum * sumC * du * dv / (Math.PI * wx * wy);

        // The ray sum repeats with period λ/(n·|∂α/∂P|·h) (OP-38's sampling rule); the window must fit inside it.
        double periodX = lambda / (n * maxSlopeX * pupil.Step), periodY = lambda / (n * maxSlopeY * pupil.Step);
        double widthX = us[^1] - us[0], widthY = vs[^1] - vs[0];
        if (widthX >= periodX || widthY >= periodY)
            warnings.Add($"Huygens: the fiber's window ({widthX * 1e6:g4} × {widthY * 1e6:g4} µm) is wider than the period of the pupil sum ({periodX * 1e6:g4} × {periodY * 1e6:g4} µm), so copies of the beam alias into it. Increase the pupil sampling.");
        notes.Add($"Huygens: the beam summed by OP-38's Huygens sum on {us.Length} × {vs.Length} points spaced {du * 1e6:g4} × {dv * 1e6:g4} µm in the plane through the fiber's centre normal to the image surface's z (±5 fiber waists), then overlapped with the fiber mode on that grid.");
        return (overlap, image);
    }
}
