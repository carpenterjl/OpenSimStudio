using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Volume;

/// <summary>What one volume event did to the ray (plan item OP-81).</summary>
public readonly record struct NscVolumeOutcome
{
    /// <summary>The new direction (unit).</summary>
    public Vector3D Direction { get; init; }

    /// <summary>The outgoing ray's power as a fraction of the incoming; 0 terminates the branch.</summary>
    public double Factor { get; init; }

    /// <summary>The new wavelength, µm in the system's air; null when it does not change.</summary>
    public double? Micrometers { get; init; }

    /// <summary>True for photoluminescence: the lost part (1 − <see cref="Factor"/>) is conversion
    /// loss and the new ray is unpolarized; false for scattering: the lost part is absorbed and
    /// the polarization state turns with the direction.</summary>
    public bool Photoluminescence { get; init; }

    /// <summary>True when nothing happened (a phosphor event on a ray that may not change
    /// wavelength): the ray goes on unchanged and the event is not a bulk scatter.</summary>
    public bool Passed { get; init; }

    /// <summary>A plain sentence for the segment's fate.</summary>
    public string Fate { get; init; }
}

/// <summary>
/// One object's volume physics prepared at one wavelength (plan item OP-81;
/// docs/Optics-Conventions.md §77): the mean free path L of all events together and how an event
/// turns the ray. A free path is −L·ln(1 − u) ("Setting this expression [p(x) = 1 − e^(−μx), μ = 1/M]
/// to a randomly selected value between 0.0 and 1.0, then solving for x", help "Bulk scattering");
/// an event happens when it is shorter than the distance to the next face. Immutable; shared by
/// threads.
/// </summary>
public sealed class NscVolumeAt
{
    private readonly NscVolumePhysics _v;
    private readonly NscPhaseFunction? _phase;     // angle or DLL scattering, or the phosphor host's Mie
    private readonly double _lPl, _lMie;           // phosphor: photoluminescence and Mie mean free paths (m)
    private readonly double _transmission = 1;

    /// <summary>The wavelength this was prepared at, µm in the system's air.</summary>
    public double AirMicrometers { get; }

    /// <summary>The mean free path of every event together, meters; +∞ for none.</summary>
    public double MeanFreePath { get; }

    public NscPhaseFunction? Phase => _phase;

    /// <summary>Prepares <paramref name="physics"/> for a host of index <paramref name="hostIndex"/>
    /// (relative to the system's air) at <paramref name="airMicrometers"/>. Throws
    /// <see cref="InvalidOperationException"/> naming <paramref name="where"/>.</summary>
    public NscVolumeAt(NscVolumePhysics physics, double airMicrometers, double hostIndex, string where)
    {
        _v = physics ?? throw new ArgumentNullException(nameof(physics));
        AirMicrometers = airMicrometers;
        _lPl = _lMie = double.PositiveInfinity;
        try
        {
            switch (physics.Model)
            {
                case NscVolumeModel.AngleScattering:
                    _phase = new NscConePhase(physics.AngleDegrees);
                    MeanFreePath = physics.MeanPath;
                    break;
                case NscVolumeModel.DllDefined:
                    switch (NscVolumePhysics.DllOf(physics.Dll))
                    {
                        case NscBulkDll.HenyeyGreenstein:
                            _phase = new NscHenyeyGreensteinPhase(physics.Parameter(1));
                            MeanFreePath = physics.MeanPath;
                            break;
                        case NscBulkDll.Rayleigh:
                            // The help's P(θ) = 3(1 + cos²θ)/(8λ⁴): the event rate scales as λ⁻⁴, and the
                            // Mean Path belongs to the Reference Wavelength, so L(λ) = M·(λ/λ_ref)⁴.
                            _phase = NscRayleighPhase.Instance;
                            MeanFreePath = physics.MeanPath * Math.Pow(airMicrometers / physics.Parameter(1), 4);
                            _transmission = physics.Parameter(2);
                            break;
                        case NscBulkDll.Mie:
                        {
                            var (phase, l) = MiePath(physics.DllMie, airMicrometers, hostIndex);
                            _phase = phase;
                            MeanFreePath = l;
                            break;
                        }
                        default:
                            throw new InvalidOperationException($"the bulk-scatter DLL '{physics.Dll}' is not built.");
                    }
                    break;
                case NscVolumeModel.PhosphorsAndFluorescence:
                {
                    var p = physics.Phosphor ?? throw new InvalidOperationException("Phosphors and Fluorescence has no spectra.");
                    if (p.Model == NscPhotoluminescenceModel.Standard)
                    {
                        _lPl = p.PhotoluminescenceMeanFreePath(airMicrometers);
                        if (p.ConsiderMie) (_phase, _lMie) = MiePath(p.Mie!, airMicrometers, hostIndex);
                        // L_tot = L_Mie·L_PL/(L_Mie + L_PL) with Mie, L_PL otherwise (help).
                        MeanFreePath = double.IsPositiveInfinity(_lMie) ? _lPl
                            : double.IsPositiveInfinity(_lPl) ? _lMie
                            : _lMie * _lPl / (_lMie + _lPl);
                    }
                    else
                    {
                        (_phase, _lMie) = MiePath(p.Mie!, airMicrometers, hostIndex);
                        MeanFreePath = _lMie;
                    }
                    break;
                }
                default:
                    MeanFreePath = double.PositiveInfinity;
                    break;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{where}, Volume Physics at {airMicrometers.ToString("g9", CultureInfo.InvariantCulture)} µm: {e.Message}", e);
        }
        if (!(MeanFreePath > 0))
            throw new InvalidOperationException($"{where}, Volume Physics at {airMicrometers.ToString("g9", CultureInfo.InvariantCulture)} µm: the mean free path is {MeanFreePath}; it must be positive.");
    }

    /// <summary>
    /// Mie particles in a host: relative index m = n_p/n_host, size parameter x = 2π·n_host·r/λ (λ in
    /// the system's air, indices relative to it), and the mean free path 1/(N·C_sca) with
    /// C_sca = Q_sca·πr² and N the number density (derived), or the given mean free path.
    /// </summary>
    internal static (NscMiePhase Phase, double MeanFreePath) MiePath(NscMieParticles particles, double airMicrometers, double hostIndex)
    {
        double r = particles.RadiusMicrometers * 1e-6;
        var mie = new NscMie(particles.Index / hostIndex, 2 * Math.PI * hostIndex * particles.RadiusMicrometers / airMicrometers);
        double l = particles.UseMeanFreePath ? particles.MeanFreePath
            : 1 / (particles.DensityPerCm3 * 1e6 * mie.Qsca * Math.PI * r * r);
        return (new NscMiePhase(mie), l);
    }

    /// <summary>A free path, meters (+∞ when nothing can happen, without drawing).</summary>
    public double FreePath(SplitMix64 rng) =>
        double.IsPositiveInfinity(MeanFreePath) ? double.PositiveInfinity : -MeanFreePath * Math.Log(1 - rng.NextDouble());

    /// <summary>
    /// The event at the end of a free path for a ray of direction <paramref name="d"/>.
    /// <paramref name="wavelengthNumber"/> is the ray's system wavelength number (0 for none),
    /// <paramref name="system"/> the system wavelengths (µm, row 1 first) a shift reads, and
    /// <paramref name="mayShift"/> false for rays of sources whose colour model is not System
    /// Wavelengths (help: they "will not undergo wavelength shift"). Throws
    /// <see cref="InvalidOperationException"/> when a spectrum does not cover the wavelength.
    /// </summary>
    public NscVolumeOutcome Event(Vector3D d, int wavelengthNumber, IReadOnlyList<double>? system, bool mayShift, SplitMix64 rng)
    {
        double um = AirMicrometers;
        if (_v.Model == NscVolumeModel.PhosphorsAndFluorescence)
        {
            var p = _v.Phosphor!;
            bool luminesce;
            if (p.Model == NscPhotoluminescenceModel.Standard)
                // "If r < L_tot / L_PL then the ray undergoes photoluminescence. Otherwise it Mie scatters."
                luminesce = double.IsPositiveInfinity(_lMie) || rng.NextDouble() < MeanFreePath / _lPl;
            else
                // Basic: "If the wavelength of the ray is within the range of the Absorption Spectrum and
                // it bulk scatters, then it will always undergo Photoluminescence".
                luminesce = p.Absorption.Covers(um);
            if (luminesce)
                return mayShift ? Photoluminescence(p, d, um, rng)
                    : new NscVolumeOutcome { Direction = d, Factor = 1, Passed = true, Fate = "no photoluminescence for a ray whose source's colour model is not System Wavelengths: passed unchanged" };
            return Scatter(d, null, rng, "Mie scattered");
        }
        double? shifted = null;
        if (_v.WavelengthShift is { } shift && mayShift && wavelengthNumber > 0 && system is not null)
        {
            int to = shift.Choose(wavelengthNumber, rng.NextDouble());
            if (to != wavelengthNumber)
            {
                if (to > system.Count)
                    throw new InvalidOperationException($"the wavelength shift names wavelength {to}; the system has {system.Count}.");
                shifted = system[to - 1];
            }
        }
        return Scatter(d, shifted, rng, _v.Model == NscVolumeModel.AngleScattering ? "bulk scattered (Angle Scattering)" : $"bulk scattered ({_v.Dll})");
    }

    private NscVolumeOutcome Scatter(Vector3D d, double? shifted, SplitMix64 rng, string what)
    {
        double mu = _phase!.SampleCosine(rng.NextDouble());
        var nd = NscPhaseFunction.Turn(d, mu, rng.NextDouble());
        string fate = what + (shifted is double w ? $", wavelength shifted to {w.ToString("g9", CultureInfo.InvariantCulture)} µm" : "")
            + (_transmission < 1 ? $", transmission {_transmission.ToString("g6", CultureInfo.InvariantCulture)}" : "");
        return new NscVolumeOutcome { Direction = nd, Factor = _transmission, Micrometers = shifted, Fate = fate };
    }

    /// <summary>
    /// Re-emission (help, "Phosphors &amp; Fluorescence"): "A photon will re-emit if the quantum
    /// yield for the input wavelength is non-zero ... Otherwise ... the ray is terminated"; the new
    /// wavelength is drawn from the emission spectrum; I_emitted = I_absorbed·Q(λ_in)·λ_in/λ_emitted
    /// (Standard) or I_incident·A(λ_in)·Q(λ_in)·λ_in/λ_downconverted (Basic); "re-emitted in a random
    /// direction, over a full sphere"; "this model will randomly polarize rays".
    /// </summary>
    private NscVolumeOutcome Photoluminescence(NscPhosphor p, Vector3D d, double um, SplitMix64 rng)
    {
        double q = p.QuantumYield.At(um);
        if (!(q > 0))
            return new NscVolumeOutcome { Direction = d, Factor = 0, Photoluminescence = true, Fate = "absorbed by the phosphor, not re-emitted (quantum yield 0)" };
        double? emitted = p.Emission.Sample(um, rng.NextDouble());
        if (emitted is not double em)
            return new NscVolumeOutcome { Direction = d, Factor = 0, Photoluminescence = true, Fate = "absorbed by the phosphor, not re-emitted (no emission at a longer wavelength)" };
        double factor = q * um / em;
        if (p.Model == NscPhotoluminescenceModel.Basic) factor *= p.Absorption.At(um);
        double mu = 1 - 2 * rng.NextDouble();
        var nd = NscPhaseFunction.Turn(d, mu, rng.NextDouble());
        return new NscVolumeOutcome
        {
            Direction = nd, Factor = factor, Micrometers = em, Photoluminescence = true,
            Fate = $"photoluminescence: re-emitted at {em.ToString("g9", CultureInfo.InvariantCulture)} µm with {factor.ToString("g6", CultureInfo.InvariantCulture)} of the power",
        };
    }
}
