using System.Collections.Concurrent;
using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Optics.NonSequential.Scattering;
using OpenSim.Optics.NonSequential.Volume;
using OpenSim.Optics.Polarization;

namespace OpenSim.Optics.NonSequential;

// OP-81: volume physics in the non-sequential tracer (docs/Optics-Conventions.md §77). A ray
// inside a refractive volume with Volume Physics draws a free path at every segment; a path
// shorter than the distance to the next face ends the segment at a volume event (bulk scatter or
// photoluminescence), marked B. A wavelength change mid-tree is carried by the ray (Pending.Wave):
// indices, bulk absorption, coatings, surface scatter models, detectors and the ray database all
// see the new wavelength. With no volume physics nothing here runs and no random number is drawn.
public sealed partial class NscTracer
{
    /// <summary>What depends on the wavelength: the indices and bulk absorption of every object, the
    /// background index, the surface scatter models and the prepared volume physics.</summary>
    private sealed class WaveOptics
    {
        public required double AirMicrometers;
        public required double[] Index, Alpha;
        public required double BackgroundIndex;
        public NscScatterer? Scatter;
        public required NscVolumeAt?[] Volume;
        /// <summary>The system wavelength number (1-based), 0 when not a system wavelength.</summary>
        public int WavelengthNumber;
    }

    private WaveOptics _primary = null!;
    private readonly ConcurrentDictionary<double, Lazy<WaveOptics>> _shifted = new();

    /// <summary>Wavelengths whose optics are kept once built; beyond this they are rebuilt on use
    /// (the same values, so results do not depend on it).</summary>
    private const int MaxCachedWavelengths = 1024;

    /// <summary>Object <paramref name="objectNumber"/>'s volume physics prepared at the trace
    /// wavelength (mean free path, phase function); null for none.</summary>
    public NscVolumeAt? Volume(int objectNumber) => _primary.Volume[objectNumber - 1];

    private WaveOptics PrimaryOptics()
    {
        var system = Settings.SystemWavelengths;
        for (int i = 0; i < _objects.Length; i++)
            if (_objects[i].VolumePhysics?.WavelengthShift is { Transitions.Count: > 0 } shift)
            {
                string where = $"Object {i + 1} ({_objects[i].Geometry.TypeName}), wavelength shift \"{shift.Text}\"";
                if (system is null)
                    throw new ArgumentException($"Non-sequential trace refused: {where}: the shift numbers system wavelengths, and the trace was given none (NscTraceSettings.SystemWavelengths; traces through the sources fill it).");
                foreach (var t in shift.Transitions)
                    if (t.In > system.Count || t.Out > system.Count)
                        throw new ArgumentException($"Non-sequential trace refused: {where}: it names wavelength {Math.Max(t.In, t.Out)}; the system has {system.Count}.");
            }
        return new WaveOptics
        {
            AirMicrometers = AirMicrometers, Index = _index, Alpha = _alpha, BackgroundIndex = _backgroundIndex,
            Scatter = _scatter, Volume = Volumes(AirMicrometers, _index), WavelengthNumber = NumberOf(AirMicrometers),
        };
    }

    private int NumberOf(double micrometers)
    {
        var system = Settings.SystemWavelengths;
        if (system is null) return 0;
        for (int i = 0; i < system.Count; i++) if (system[i] == micrometers) return i + 1;
        return 0;
    }

    private NscVolumeAt?[] Volumes(double micrometers, double[] index)
    {
        var r = new NscVolumeAt?[_objects.Length];
        for (int i = 0; i < _objects.Length; i++)
            if (_objects[i].VolumePhysics is { Model: not NscVolumeModel.None } v)
                r[i] = new NscVolumeAt(v, micrometers, index[i], $"Object {i + 1} ({_objects[i].Geometry.TypeName})");
        return r;
    }

    /// <summary>The optics at another wavelength. Throws <see cref="InvalidOperationException"/>
    /// naming the wavelength and the object whose data does not cover it.</summary>
    private WaveOptics OpticsAt(double micrometers)
    {
        if (micrometers == AirMicrometers) return _primary;
        if (_shifted.TryGetValue(micrometers, out var known)) return known.Value;
        var lazy = new Lazy<WaveOptics>(() => BuildOptics(micrometers), LazyThreadSafetyMode.ExecutionAndPublication);
        if (_shifted.Count < MaxCachedWavelengths) lazy = _shifted.GetOrAdd(micrometers, lazy);
        return lazy.Value;
    }

    private WaveOptics BuildOptics(double um)
    {
        try
        {
            int n = _objects.Length;
            var index = new double[n];
            var alpha = new double[n];
            for (int i = 0; i < n; i++)
            {
                var o = _objects[i];
                index[i] = double.NaN;
                if (o.Geometry.IsVolume && o.Material.Kind == NscMaterialKind.Refractive)
                {
                    string where = $"Object {i + 1} ({o.Geometry.TypeName})";
                    var medium = o.Material.Medium ?? Paraxial.ParaxialMedium.Air;
                    index[i] = IndexOf(medium, where, um);
                    if (Settings.AccountsEnergy) alpha[i] = AlphaOf(medium, where, um);
                }
            }
            return new WaveOptics
            {
                AirMicrometers = um, Index = index, Alpha = alpha,
                BackgroundIndex = IndexOf(Scene.Background, "The background medium", um),
                Scatter = NscScatterer.For(_objects, _global, um),
                Volume = Volumes(um, index), WavelengthNumber = NumberOf(um),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"the scene's optics at the shifted wavelength {um.ToString("g9", CultureInfo.InvariantCulture)} µm: {e.Message}", e);
        }
    }

    /// <summary>The volume event at distance <paramref name="free"/> along ray <paramref name="r"/>.</summary>
    private void VolumeEvent(Pending r, WaveOptics wave, NscVolumeAt volume, (double Index, double Alpha, int Object) medium,
        double free, int number, long rayNumber, NscLaunchRay launch, NscLedgerBuilder ledger, List<NscSegment>? segments,
        List<string> errors, Stack<Pending> stack, SplitMix64 rng)
    {
        var s = Settings;
        double intensity = r.Intensity;
        var phi = r.Phi;
        if (medium.Alpha > 0)
        {
            double f = Math.Exp(-medium.Alpha * free);
            ledger.Absorbed.Add(intensity * (1 - f));
            intensity *= f;
            phi = phi?.Scaled(f);
        }
        var point = r.Origin + r.Direction * free;
        double optical = r.Optical + medium.Index * free;
        int level = r.Level + 1;
        NscSegment? seg = null;
        if (segments is not null)
        {
            seg = new NscSegment
            {
                Number = number, Parent = r.Parent, Level = level, Inside = medium.Object, Hit = 0, Face = 0,
                Position = point, Direction = r.Direction, Intensity = intensity, PathLength = free, OpticalPath = optical,
                Index = medium.Index,
                Events = (r.Ghost ? NscEvents.Ghost : NscEvents.None) | (r.Scattered ? NscEvents.ScatteredFromParent : 0),
                AirMicrometers = r.Wave?.AirMicrometers, WavelengthNumber = r.Wave?.WavelengthNumber,
            };
            segments.Add(seg);
        }
        int segIndex = segments is null ? 0 : segments.Count - 1;

        void Error(string message, double energy)
        {
            ledger.Errors.Add(energy);
            ledger.ErrorRays++;
            errors.Add($"ray {rayNumber}, segment {number}, inside object {medium.Object} ({_objects[medium.Object - 1].Geometry.TypeName}) at {point}: {message}");
            if (seg is not null) { seg.Events |= NscEvents.Error | NscEvents.Terminated; seg.Fate = "error: " + message; }
        }

        if (!double.IsFinite(point.X + point.Y + point.Z) || !double.IsFinite(intensity))
        {
            Error("the volume event point is not a finite number.", intensity);
            return;
        }
        if (level >= s.MaxIntersectionsPerRay)
        {
            ledger.LimitTerminated.Add(intensity);
            ledger.LimitTerminatedRays++;
            if (seg is not null) { seg.Events |= NscEvents.Terminated; seg.Fate = $"stopped: {s.MaxIntersectionsPerRay} intersections reached"; }
            return;
        }
        NscVolumeOutcome outcome;
        try { outcome = volume.Event(r.Direction, wave.WavelengthNumber, s.SystemWavelengths, !launch.FixedWavelength, rng); }
        catch (InvalidOperationException e)
        {
            Error($"volume physics: {e.Message}", intensity);
            return;
        }
        if (outcome.Passed)
        {
            if (seg is not null) seg.Fate = outcome.Fate;
            stack.Push(new Pending
            {
                Origin = point, Direction = r.Direction, Intensity = intensity, Phi = phi, Inside = r.Inside, Level = level,
                Parent = segIndex, Optical = optical, Wave = r.Wave, VolumeDone = r.VolumeDone,
            });
            return;
        }
        ledger.VolumeEvents++;
        if (outcome.Photoluminescence) ledger.ConversionEvents++;
        if (seg is not null) { seg.Events |= NscEvents.BulkScattered; seg.Fate = outcome.Fate; }
        double power = intensity * outcome.Factor;
        double lost = intensity - power;
        if (outcome.Photoluminescence) ledger.ConversionLoss.Add(lost);
        else ledger.Absorbed.Add(lost);
        if (!(power > 0))
        {
            if (seg is not null) seg.Events |= NscEvents.Terminated;
            return;
        }
        var next = wave;
        if (outcome.Micrometers is double um && um != wave.AirMicrometers)
        {
            try { next = OpticsAt(um); }
            catch (InvalidOperationException e)
            {
                Error(e.Message, power);
                return;
            }
        }
        if (outcome.Photoluminescence) ledger.Converted.Add(power);
        CoherencyMatrix? state = null;
        if (phi is not null)
            state = outcome.Photoluminescence ? Unpolarized(outcome.Direction, power)
                : RotatedState(phi, r.Direction, outcome.Direction).Scaled(outcome.Factor);
        stack.Push(new Pending
        {
            Origin = point, Direction = outcome.Direction, Intensity = power, Phi = state, Inside = r.Inside, Level = level,
            Parent = segIndex, Optical = optical,
            Wave = ReferenceEquals(next, _primary) ? null : next,
            VolumeDone = r.VolumeDone || launch.BulkScatter == Sources.NscSourceBulkScatter.Once,
        });
    }

    private void VolumeNotes(List<string> notes)
    {
        var lines = new List<string>();
        for (int i = 0; i < _objects.Length; i++)
            if (_objects[i].VolumePhysics is { } v && v.Model != NscVolumeModel.None)
            {
                var at = _primary.Volume[i];
                string mfp = at is null || double.IsPositiveInfinity(at.MeanFreePath) ? "no events at this wavelength"
                    : $"mean free path {at.MeanFreePath.ToString("g6", CultureInfo.InvariantCulture)} m at this wavelength";
                lines.Add($"object {i + 1}: {v.Describe()}; {mfp}");
            }
        if (lines.Count == 0) return;
        notes.Add("Volume physics (free paths −L·ln(1 − u), an event where the path ends before the next face; the ray's wavelength, when shifted, sets indices, absorption, coatings, scatter models and detector weights): "
            + string.Join("; ", lines) + ".");
    }
}
