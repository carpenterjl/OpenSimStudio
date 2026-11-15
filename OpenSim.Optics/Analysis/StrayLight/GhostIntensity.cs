using System.Numerics;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis.StrayLight;

/// <summary>
/// Reflectance and transmittance of an original surface met by a ghost path (OP-46): its coating
/// from OP-15's catalog, or the uncoated Fresnel interface when it has none
/// (<see cref="CoatingEvaluator.Evaluate"/> with a null coating), unpolarized (the mean of s and
/// p). The light meets the interface from the side it travels from: forward, from the medium
/// before the surface into the one after it; backward, the other way. The coating's layers are
/// reversed when the light goes from glass to air (OpticStudio's rule as OP-51's polarization
/// trace applies it). The image surface's far side is its own medium (normally the same as the
/// space before it, so an uncoated image surface reflects nothing).
/// </summary>
public sealed class GhostIntensity
{
    private readonly SequentialLens _lens;
    private readonly CoatingCatalog _catalog;
    private readonly Coating? _reflector;
    private readonly ParaxialMedium[] _media;
    private readonly double _micrometers, _primaryMicrometers;
    private readonly OpticsEnvironment _environment;

    /// <summary>The ghost reflector coating applied to every bounce, or null for the surfaces' own.</summary>
    public string? ReflectorCoating => _reflector?.Name;

    /// <param name="catalog">The coating catalog; <see cref="CoatingCatalog.Empty"/> when there is none,
    /// in which case a surface naming a coating is refused.</param>
    /// <param name="reflectorCoating">OpticStudio's Ghost Reflector Coating: a coating name applied
    /// to the bounce surfaces instead of their own, or blank. The help's I.xx shorthand ("I.99"
    /// reflects 1 %) is accepted when the catalog has no coating of that name.</param>
    public GhostIntensity(SequentialLens lens, OpticalSystemData system, int wavelengthRow, CoatingCatalog catalog, string? reflectorCoating)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(catalog);
        _lens = lens;
        _catalog = catalog;
        _media = lens.EffectiveMedia();
        _micrometers = system.Wavelengths.Wavelengths[wavelengthRow].Micrometers;
        _primaryMicrometers = system.Wavelengths.Wavelengths[system.Wavelengths.PrimaryIndex].Micrometers;
        _environment = system.Environment;
        if (!string.IsNullOrWhiteSpace(reflectorCoating))
        {
            string name = reflectorCoating.Trim();
            _reflector = catalog.FindCoating(name);
            if (_reflector is null && IdealCoating.TryParseSimpleName(name, out double t))
                _reflector = new IdealCoating { Name = name, Transmission = t, IsSimple = true };
            if (_reflector is null)
                throw new ArgumentException($"Ghost focus: the ghost reflector coating '{name}' is not in the coating catalog and is not an I.xx ideal coating name.");
        }
    }

    /// <summary>The unpolarized reflectance of original surface <paramref name="surface"/> for light
    /// arriving forward or backward at <paramref name="angleDegrees"/> in the incident medium, with
    /// the ghost reflector coating when one is set.</summary>
    public double Reflectance(int surface, bool forward, double angleDegrees = 0) =>
        Evaluate(surface, forward, angleDegrees, _reflector ?? OwnCoating(surface), mirror: false).AverageReflection;

    /// <summary>The unpolarized transmittance of original surface <paramref name="surface"/>
    /// crossed forward or backward (its own coating, never the reflector coating). For an original
    /// mirror on the path, the mirror's reflectance (the help's uncoated mirror, OP-15).</summary>
    public double Transmittance(int surface, bool forward, double angleDegrees = 0)
    {
        var s = _lens.Surfaces[surface];
        if (s.IsMirror) return Evaluate(surface, forward, angleDegrees, OwnCoating(surface), mirror: true).AverageReflection;
        return Evaluate(surface, forward, angleDegrees, OwnCoating(surface), mirror: false).AverageTransmission;
    }

    /// <summary>True when original surface <paramref name="surface"/> is an index boundary or a
    /// mirror (anything else transmits everything).</summary>
    public bool Acts(int surface)
    {
        var s = _lens.Surfaces[surface];
        if (s.IsIgnored || s.IsCoordinateBreak || surface == 0) return false;
        if (s.IsMirror) return true;
        if (surface == _lens.ImageIndex) return false;
        if (s.Coating is { } c && c.Trim().Length > 0) return true;
        return Index(_media[surface - 1]) != Index(_media[surface]);
    }

    private double Index(ParaxialMedium m)
    {
        double n = m.IndexAt(_micrometers, _environment);
        if (!double.IsFinite(n) || !(n > 0))
            throw new InvalidOperationException($"Ghost focus: a medium's index is {n} at {_micrometers} µm; it must be positive.");
        return n;
    }

    private Coating? OwnCoating(int surface)
    {
        if (_lens.Surfaces[surface].Coating is not { } name || name.Trim().Length == 0) return null;
        return _catalog.FindCoating(name.Trim()) ?? throw new InvalidOperationException(
            $"Ghost focus: surface {surface} names coating '{name.Trim()}', which is not in the coating catalog" +
            (_catalog.Coatings.Count == 0 ? " (the catalog is empty or was not supplied)." : $" (it has: {string.Join(", ", _catalog.Coatings.Select(c => c.Name))})."));
    }

    private CoatingResponse Evaluate(int surface, bool forward, double angleDegrees, Coating? coating, bool mirror)
    {
        int image = _lens.ImageIndex;
        ParaxialMedium before = _media[surface - 1];
        ParaxialMedium after = surface == image ? _lens.Surfaces[image].Medium : _media[surface];
        var (incident, other) = forward ? (before, after) : (after, before);
        double nIn = Index(incident), nOut = Index(other);
        try
        {
            return CoatingEvaluator.Evaluate(_catalog, coating, new CoatingConditions
            {
                Incident = new Complex(nIn, 0),
                Substrate = new Complex(nOut, 0),
                IsMirror = mirror,
                ReverseLayers = !mirror && GhostSystems.IsGlass(incident) && !GhostSystems.IsGlass(other),
                Micrometers = _micrometers,
                PrimaryMicrometers = _primaryMicrometers,
                AngleDegrees = angleDegrees,
            });
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException($"Ghost focus: surface {surface}{(coating is null ? "" : $" (coating '{coating.Name}')")}: {e.Message}", e);
        }
    }
}
