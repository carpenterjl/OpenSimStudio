using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Polarization;

/// <summary>Options of a polarization trace.</summary>
public sealed record PolarizationOptions
{
    /// <summary>How a Jones vector becomes a launch field.</summary>
    public PolarizationReference Reference { get; init; } = PolarizationReference.XAxis;

    /// <summary>Convert layer-coating phases to the ray convention (<see cref="PolarizationTracer"/>).</summary>
    public bool ConvertThinFilmPhase { get; init; } = true;

    /// <summary>Apply bulk absorption (<see cref="BulkAbsorption"/>).</summary>
    public bool BulkAbsorption { get; init; } = true;

    /// <summary>The options a system polarization implies.</summary>
    public static PolarizationOptions From(SystemPolarization polarization, bool bulkAbsorption = true)
    {
        ArgumentNullException.ThrowIfNull(polarization);
        return new PolarizationOptions
        {
            Reference = polarization.Reference, ConvertThinFilmPhase = polarization.ConvertThinFilmPhase,
            BulkAbsorption = bulkAbsorption,
        };
    }
}

/// <summary>
/// What happened to the field at one surface. Vectors and fields are in surface 1's frame
/// (the global frame of docs/Optics-Conventions.md §13); <see cref="LocalField"/> rotates a
/// field into the surface's own frame. Amplitude coefficients are power-normalised and in this
/// program's complex convention (§21).
/// </summary>
public sealed record PolarizedSurface
{
    public required int Surface { get; init; }
    public required SurfaceHit Hit { get; init; }

    /// <summary>Internal transmittance (power) of the segment ending on this surface.</summary>
    public double BulkTransmittance { get; init; } = 1;

    /// <summary>The field arriving at the surface, after the bulk absorption of the segment.</summary>
    public RayField FieldIn { get; init; }

    /// <summary>The field leaving the surface (equal to <see cref="FieldIn"/> where there is
    /// no interface: an ignored surface, the image surface, or a ray that failed here).</summary>
    public RayField FieldOut { get; init; }

    /// <summary>True when the surface acted on the field (refracted or reflected it).</summary>
    public bool IsInterface { get; init; }

    /// <summary>The coating's name, null for an uncoated surface.</summary>
    public string? CoatingName { get; init; }

    /// <summary>The coating (or bare interface) response; null where there is no interface.</summary>
    public CoatingResponse? Response { get; init; }

    /// <summary>The s and p amplitude coefficients in the thin-film ("field") convention.</summary>
    public Complex FieldAmplitudeS { get; init; } = Complex.One;
    public Complex FieldAmplitudeP { get; init; } = Complex.One;

    /// <summary>The coefficients applied to the field: the ray-equivalent ones when the
    /// conversion is on, the field ones otherwise.</summary>
    public Complex AmplitudeS { get; init; } = Complex.One;
    public Complex AmplitudeP { get; init; } = Complex.One;

    /// <summary>The physical thickness of the coating's layers (meters) that the conversion
    /// used; zero for an uncoated surface or an ideal or table coating.</summary>
    public double CoatingThickness { get; init; }

    /// <summary>ŝ, p̂ before and p̂ after (zero vectors where there is no interface).</summary>
    public Vector3D S { get; init; }
    public Vector3D PIn { get; init; }
    public Vector3D POut { get; init; }

    /// <summary>This interface's matrix (identity where there is none).</summary>
    public required PolarizationMatrix Matrix { get; init; }

    /// <summary>The product of every matrix and bulk factor from the launch to here.</summary>
    public required PolarizationMatrix Cumulative { get; init; }

    /// <summary>|E_out|² here over |E_out|² at the previous surface: the bulk transmittance of
    /// the material before this surface times this surface's transmission (OpticStudio's
    /// "relative transmission"; help, "Transmission").</summary>
    public double RelativeTransmission { get; init; }

    /// <summary>|E_out|², the power left of the launched unit power (OpticStudio's "total
    /// transmission").</summary>
    public double TotalTransmission { get; init; }

    /// <summary>A global field written in this surface's local frame.</summary>
    public RayField LocalField(SequentialTrace trace, RayField globalField) =>
        globalField.RotatedBack(trace.GlobalPlacement(Surface));
}

/// <summary>A ray traced with its field: the geometric path, the launch, every surface, and
/// the final ray carrying its field (image-surface frame) and intensity.</summary>
public sealed record PolarizedPath
{
    public required RayPath Path { get; init; }
    public required JonesVector Jones { get; init; }

    /// <summary>The launch vectors and field, surface 1's frame.</summary>
    public required Vector3D LaunchS { get; init; }
    public required Vector3D LaunchP { get; init; }
    public required RayField LaunchField { get; init; }

    /// <summary>One entry per surface hit after the object surface.</summary>
    public required IReadOnlyList<PolarizedSurface> Surfaces { get; init; }

    /// <summary><see cref="RayPath.Final"/> with <see cref="Ray.Field"/> set (in the frame of
    /// the image surface, or of the surface the ray failed at) and <see cref="Ray.Intensity"/>
    /// = |E|² there.</summary>
    public required Ray Final { get; init; }

    /// <summary>The power transmitted to the image surface; zero for a ray that failed.</summary>
    public double Transmission => Final.IsOk ? Final.Intensity : 0;

    /// <summary>The system's matrix from the launch to the last surface reached.</summary>
    public PolarizationMatrix Cumulative => Surfaces.Count > 0 ? Surfaces[^1].Cumulative : PolarizationMatrix.Identity;
}

/// <summary>
/// The polarization ray trace (OP-51): the electric field carried along a real ray of
/// <see cref="SequentialTrace"/>, built on the surface-by-surface record of
/// <see cref="SequentialTrace.TracePath"/> without touching the trace loop. Built once per
/// trace (lens, system and wavelength), coating catalog and options; immutable and safe to
/// share between threads. The conventions are docs/Optics-Conventions.md §21.
/// <para>
/// <b>Field.</b> The launch field is Jx·Ŝ + Jy·P̂ (<see cref="JonesVector.ToField"/>), unit
/// power. It is scaled so that |E|² is the ray's power relative to launch at every point:
/// amplitude coefficients are power-normalised, a_t = √T·e^(i·arg t), a_r = √R·e^(i·arg r), with
/// T and R the coating engine's power fractions (which carry the n cos θ factors) and arg the
/// phase of the field amplitude in this program's convention (OP-15's phases are OpticStudio's,
/// the opposite sign). The propagation phase 2π·OPL/λ is not in the field; it is the ray's
/// optical path. So the phase of a field component is the polarization phase alone, which is
/// what the phase-aberration analysis plots.
/// </para>
/// <para>
/// <b>Each surface.</b> The segment arriving is attenuated by the bulk absorption of its medium
/// (amplitude e^(−ατ/2)); then, at a refracting or reflecting surface, the field goes through
/// P = a_s ŝŝᵀ + a_p p̂_out p̂_inᵀ + k̂_out k̂_inᵀ (<see cref="PolarizationMatrix"/>). The s and
/// p coefficients come from <see cref="CoatingEvaluator"/> at the angle of incidence, between the
/// indices before and after (relative to the system's air, like the coating file's): the
/// bare Fresnel interface when the surface has no coating, aluminium on an uncoated mirror
/// (OpticStudio help, "Properties of Uncoated Surfaces"), layers reversed from glass to air
/// (help, "Specifying Coatings on Surfaces": glass is any medium but index-1 air). The s/p
/// frame is the one in which a bare interface has r_p = −r_s at normal incidence, which is the
/// sign convention of OP-15's p amplitudes (derived in PolarizationTraceTests).
/// </para>
/// <para>
/// <b>Ray-equivalent phase.</b> A thin-film amplitude relates the field at the coating's outer
/// face to the field at the substrate, measured along the normal; the trace goes straight to the
/// substrate with the coating taken as grown into the space before the surface (help, "Convert
/// Thin Film Phase to Ray Equivalent"). A plane wave in the incident medium (index n0, angle θ0)
/// gains 2π·n0·D·cos θ0/λ in phase between the planes z = −D and z = 0 at a fixed transverse
/// point, D the coating's physical thickness. The ray already carries the field to z = 0, so
/// the field there is t·e^(−iψ) times the ray's, ψ = 2π·n0·D·cos θ0/λ; a reflected wave crosses
/// the layer twice, r·e^(−2iψ) (derived; the help states the idea, not the formula). With the
/// conversion on, these are the coefficients applied. Only layer coatings have a thickness;
/// on a mirror the last layer is the substrate and does not count.
/// </para>
/// </summary>
public sealed class PolarizationTracer
{
    private readonly CoatingCatalog _catalog;
    private readonly Coating?[] _coatings;
    private readonly ParaxialMedium[] _media;
    private readonly double[] _alpha;
    private readonly double _primaryMicrometers;

    public SequentialTrace Trace { get; }
    public PolarizationOptions Options { get; }

    /// <summary>
    /// The tracer for <paramref name="trace"/>. Every surface's coating name is looked up in
    /// <paramref name="catalog"/> here, and bulk absorption is evaluated for every space at the
    /// trace's wavelength; a name the catalog lacks, or a glass whose IT data do not cover the
    /// wavelength, throws naming the surface.
    /// </summary>
    public PolarizationTracer(SequentialTrace trace, CoatingCatalog catalog, PolarizationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(catalog);
        Trace = trace;
        _catalog = catalog;
        Options = options ?? new PolarizationOptions();
        var surfaces = trace.Lens.Surfaces;
        _coatings = new Coating?[surfaces.Count];
        for (int j = 1; j < surfaces.Count - 1; j++)
        {
            if (surfaces[j].Coating is not { } name || name.Trim().Length == 0) continue;
            _coatings[j] = catalog.FindCoating(name.Trim()) ?? throw new InvalidOperationException(
                $"Polarization ray trace: surface {j} names coating '{name.Trim()}', which is not in the coating catalog. " +
                (catalog.Coatings.Count == 0 ? "The catalog is empty: add the coating in the Coatings panel or import a catalog."
                    : $"The catalog has: {string.Join(", ", catalog.Coatings.Select(c => c.Name))}."));
        }
        _media = trace.Lens.EffectiveMedia();
        _alpha = new double[surfaces.Count];
        if (Options.BulkAbsorption)
            for (int j = 0; j < surfaces.Count - 1; j++)
            {
                try { _alpha[j] = BulkAbsorption.Coefficient(_media[j], trace.AirMicrometers); }
                catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException)
                {
                    throw new InvalidOperationException(
                        $"Polarization ray trace: bulk absorption of {(j == 0 ? "object space" : $"the space after surface {j}")}: {e.Message}", e);
                }
            }
        _primaryMicrometers = trace.System.Wavelengths.Primary.Micrometers;
    }

    /// <summary>The intensity attenuation coefficient (1/m) used for the space after each surface.</summary>
    public IReadOnlyList<double> AbsorptionCoefficients => _alpha;

    /// <summary>Traces <paramref name="launch"/> (surface 1's frame) and its field.</summary>
    public PolarizedPath TraceRay(Ray launch, JonesVector jones, TraceOptions options = default) =>
        TracePath(Trace.TracePath(launch, options), jones);

    /// <summary>The field along an already traced path (from this tracer's trace).</summary>
    public PolarizedPath TracePath(RayPath path, JonesVector jones)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Hits.Count == 0)
            throw new ArgumentException("The path has no hits: its launch ray had already failed.", nameof(path));
        if (!(jones.Intensity > 0))
            throw new ArgumentException("The Jones vector is zero.", nameof(jones));
        double norm = Math.Sqrt(jones.Intensity);
        jones = new JonesVector(jones.X / norm, jones.Y / norm);

        var surfaces = Trace.Lens.Surfaces;
        int image = Trace.Lens.ImageIndex;
        var k0 = path.Hits[0].Direction;      // surface 1's frame at launch
        var (launchS, launchP) = JonesVector.LaunchBasis(k0, Options.Reference);
        var field = jones.ToField(k0, Options.Reference);
        var cumulative = PolarizationMatrix.Identity;
        double previousPower = field.NormSquared;
        var records = new List<PolarizedSurface>(path.Hits.Count);

        for (int h = 1; h < path.Hits.Count; h++)
        {
            var hit = path.Hits[h];
            int j = hit.Surface;
            var placement = Trace.GlobalPlacement(j);

            // Bulk absorption over the segment arriving (in the space after surface j − 1). The
            // launch segment of an object at infinity starts on an arbitrary plane and is skipped.
            double bulk = 1;
            double alpha = _alpha[j - 1];
            if (alpha != 0 && !(j == 1 && Trace.Lens.ObjectAtInfinity) && hit.SegmentLength != 0)
            {
                double amplitude = Math.Exp(-alpha * hit.SegmentLength / 2);
                if (!double.IsFinite(amplitude))
                    throw new InvalidOperationException(
                        $"Polarization ray trace: the virtual segment to surface {j} runs back through an opaque medium, so its internal transmittance is not finite.");
                bulk = amplitude * amplitude;
                field = Scale(field, amplitude);
                cumulative = cumulative.Scaled(amplitude);
            }
            var fieldIn = field;

            var s = surfaces[j];
            bool interfaceHere = hit.Status == RayStatus.Ok && j < image && !s.IsIgnored;
            if (!interfaceHere)
            {
                double powerHere = field.NormSquared;
                records.Add(new PolarizedSurface
                {
                    Surface = j, Hit = hit, BulkTransmittance = bulk, FieldIn = fieldIn, FieldOut = field,
                    Matrix = PolarizationMatrix.Identity, Cumulative = cumulative,
                    RelativeTransmission = previousPower > 0 ? powerHere / previousPower : 0, TotalTransmission = powerHere,
                });
                previousPower = powerHere;
                continue;
            }

            var kIn = Unit(placement.ApplyDirection(hit.IncomingDirection));
            var kOut = Unit(placement.ApplyDirection(hit.Direction));
            var eta = Unit(placement.ApplyDirection(hit.Normal));
            var (sHat, pIn, sOut, pOut) = Frames(kIn, kOut, eta);

            var coating = _coatings[j];
            var conditions = Conditions(j, hit);
            CoatingResponse response;
            try { response = CoatingEvaluator.Evaluate(_catalog, coating, conditions); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Polarization ray trace, surface {j}{(coating is null ? "" : $" (coating '{coating.Name}')")}: {e.Message}", e);
            }
            bool mirror = s.IsMirror;
            var fs = Amplitude(mirror ? response.S.R : response.S.T, mirror ? response.S.ReflectionPhase : response.S.TransmissionPhase);
            var fp = Amplitude(mirror ? response.P.R : response.P.T, mirror ? response.P.ReflectionPhase : response.P.TransmissionPhase);
            Complex aS = fs, aP = fp;
            double thickness = 0;
            if (Options.ConvertThinFilmPhase && coating is LayerCoating layers)
            {
                var (stack, _) = CoatingEvaluator.Stack(_catalog, layers, conditions);
                double micrometers = stack.Sum(l => l.Thickness);
                thickness = micrometers * 1e-6;
                double psi = 2 * Math.PI * hit.IndexBefore * micrometers * Math.Cos(hit.AngleOfIncidenceDegrees * Math.PI / 180)
                    / Trace.AirMicrometers;
                var shift = Complex.FromPolarCoordinates(1, -(mirror ? 2 : 1) * psi);
                aS *= shift;
                aP *= shift;
            }

            var matrix = PolarizationMatrix.Interface(sHat, pIn, kIn, sOut, pOut, kOut, aS, aP);
            field = matrix.Apply(field);
            cumulative = matrix * cumulative;
            double power = field.NormSquared;
            records.Add(new PolarizedSurface
            {
                Surface = j, Hit = hit, BulkTransmittance = bulk, FieldIn = fieldIn, FieldOut = field, IsInterface = true,
                CoatingName = coating?.Name, Response = response,
                FieldAmplitudeS = fs, FieldAmplitudeP = fp, AmplitudeS = aS, AmplitudeP = aP, CoatingThickness = thickness,
                S = sHat, PIn = pIn, POut = pOut, Matrix = matrix, Cumulative = cumulative,
                RelativeTransmission = previousPower > 0 ? power / previousPower : 0, TotalTransmission = power,
            });
            previousPower = power;
        }

        int finalSurface = path.Final.IsOk ? image : path.Final.FailedAtSurface;
        var local = field.RotatedBack(Trace.GlobalPlacement(Math.Clamp(finalSurface, 0, image)));
        var final = path.Final with { Field = local, Intensity = field.NormSquared };
        return new PolarizedPath
        {
            Path = path, Jones = jones, LaunchS = launchS, LaunchP = launchP, LaunchField = jones.ToField(k0, Options.Reference),
            Surfaces = records, Final = final,
        };
    }

    /// <summary>
    /// The power a traced ray carries to the image surface, relative to launch, for the system
    /// polarization: polarized, the transmission of its Jones vector; unpolarized, the mean of
    /// (1, 0) and (0, 1) (OpticStudio help, "Unpolarized"). Zero for a ray that failed. This is
    /// the call through which other analyses can apply real transmission.
    /// </summary>
    public double Transmission(RayPath path, SystemPolarization polarization)
    {
        ArgumentNullException.ThrowIfNull(polarization);
        if (!path.Final.IsOk) return 0;
        if (!polarization.Unpolarized) return TracePath(path, polarization.ToJones()).Transmission;
        return (TracePath(path, JonesVector.AlongS).Transmission + TracePath(path, JonesVector.AlongP).Transmission) / 2;
    }

    /// <summary>
    /// The power-normalised s and p amplitudes of reflection and of transmission at surface
    /// <paramref name="j"/> of the lens for an angle of incidence (degrees), between the trace's
    /// indices before and after it, in the thin-film convention (no ray conversion). Exposed for
    /// tests and reports.
    /// </summary>
    public (Complex Rs, Complex Rp, Complex Ts, Complex Tp) InterfaceAmplitudes(int j, double angleDegrees)
    {
        var hit = new SurfaceHit
        {
            Surface = j, AngleOfIncidenceDegrees = angleDegrees,
            IndexBefore = Trace.Indices[j - 1], IndexAfter = Trace.Indices[j],
        };
        var r = CoatingEvaluator.Evaluate(_catalog, _coatings[j], Conditions(j, hit));
        return (Amplitude(r.S.R, r.S.ReflectionPhase), Amplitude(r.P.R, r.P.ReflectionPhase),
            Amplitude(r.S.T, r.S.TransmissionPhase), Amplitude(r.P.T, r.P.TransmissionPhase));
    }

    private CoatingConditions Conditions(int j, SurfaceHit hit)
    {
        var s = Trace.Lens.Surfaces[j];
        var unit = Trace.System.Units.LensUnit;
        bool mirror = s.IsMirror;
        double radius = s.Radius;
        return new CoatingConditions
        {
            Incident = hit.IndexBefore,
            Substrate = mirror ? Complex.One : hit.IndexAfter,
            IsMirror = mirror,
            ReverseLayers = !mirror && IsGlass(_media[j - 1]) && !IsGlass(_media[j]),
            Micrometers = Trace.AirMicrometers,
            PrimaryMicrometers = _primaryMicrometers,
            AngleDegrees = hit.AngleOfIncidenceDegrees,
            X = OpticsConventions.FromMeters(hit.Position.X, unit),
            Y = OpticsConventions.FromMeters(hit.Position.Y, unit),
            SurfaceRadius = double.IsInfinity(radius) ? double.PositiveInfinity : OpticsConventions.FromMeters(radius, unit),
        };
    }

    /// <summary>Glass in the sense of OpticStudio's layer-order rule: anything but air of index 1.</summary>
    private static bool IsGlass(ParaxialMedium m) => m.Material is not null || m.Index != 1.0;

    /// <summary>√(power)·e^(−i·phase) for an OpticStudio-convention phase.</summary>
    private static Complex Amplitude(double power, double opticStudioPhase) =>
        Complex.FromPolarCoordinates(Math.Sqrt(Math.Max(0, power)), -opticStudioPhase);

    /// <summary>
    /// ŝ, p̂_in and the exit pair ŝ_out, p̂_out. ŝ = k̂_in × η̂ normalised; at normal incidence
    /// (|k̂_in × η̂| below 1e-12) any unit vector at right angles to k̂_in serves, since there the
    /// s and p coefficients describe the same isotropic action (a_p = a_s in transmission and
    /// a_p = −a_s in reflection in this frame, so P does not depend on the choice). ŝ_out = ŝ
    /// for any surface that keeps the ray in its plane of incidence (Snell's law and the law of
    /// reflection do); for a surface that does not (a redirecting shape of OP-27) ŝ_out is the
    /// part of ŝ at right angles to k̂_out, which keeps the field transverse.
    /// </summary>
    private static (Vector3D S, Vector3D PIn, Vector3D SOut, Vector3D POut) Frames(Vector3D kIn, Vector3D kOut, Vector3D eta)
    {
        var s = Vector3D.Cross(kIn, eta);
        if (s.Length < 1e-12)
        {
            var a = Math.Abs(kIn.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitY;
            s = a - kIn * Vector3D.Dot(a, kIn);
        }
        s /= s.Length;
        var pIn = Unit(Vector3D.Cross(kIn, s));
        var sOut = s;
        double along = Vector3D.Dot(s, kOut);
        if (Math.Abs(along) > 1e-12) sOut = Unit(s - kOut * along);
        var pOut = Unit(Vector3D.Cross(kOut, sOut));
        return (s, pIn, sOut, pOut);
    }

    private static Vector3D Unit(Vector3D v) => v / v.Length;

    private static RayField Scale(RayField e, double f) => new(e.X * f, e.Y * f, e.Z * f);
}
