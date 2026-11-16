using OpenSim.Core.Numerics;
using OpenSim.Optics.Polarization;

namespace OpenSim.Optics.NonSequential.Detectors;

/// <summary>
/// What the tracer hands a detector object at every hit (plan item OP-79; docs/Optics-Conventions.md
/// §73): the hit point and the ray's unit direction in the detector's local frame (meters), the
/// ray's power at the hit (source units, after bulk absorption on the way), the optical path from
/// the launch point (meters, indices relative to the system's air), the index of the medium the
/// ray arrives through, the trace wavelength (µm in the system's air), the ray's coherency matrix
/// when the polarization trace runs (scene frame; null otherwise), the launched ray's number and
/// whether the branch is a ghost.
/// </summary>
public readonly record struct NscDetectorHit(
    int ObjectNumber, long RayNumber, Vector3D LocalPoint, Vector3D LocalDirection, double Power,
    double OpticalPath, double MediumIndex, double AirMicrometers, CoherencyMatrix? Polarization, bool Ghost);

/// <summary>
/// A detector object's shape (an <see cref="NscGeometry"/>) implements this to receive the hits
/// on it. The tracer calls <see cref="CreateAccumulator"/> once per 64-ray chunk and once for the
/// run total, records every hit into the chunk's accumulator, ends each launched ray, and adds the
/// chunks into the total in chunk order, so the result is the same to the bit at any thread
/// count. Whether the ray then passes, reflects or stops is the object's material: blank is a
/// transparent detector, MIRROR reflects, ABSORB absorbs and the ledger counts it as detected
/// (help, "Detector Rectangle Object": "Detector rectangles may be reflective, transparent, or
/// absorbing if the material is set to "MIRROR", blank, or "ABSORB", respectively").
/// </summary>
public interface INscDetector
{
    /// <summary>An empty accumulator for object <paramref name="objectNumber"/> (1-based) at the
    /// trace wavelength (µm in the system's air).</summary>
    NscDetectorAccumulator CreateAccumulator(int objectNumber, double airMicrometers);
}

/// <summary>Running sums of one detector over some rays (see <see cref="INscDetector"/>).</summary>
public abstract class NscDetectorAccumulator
{
    /// <summary>Adds one hit.</summary>
    public abstract void Record(in NscDetectorHit hit);

    /// <summary>Closes the current launched ray: its per-pixel totals enter the sums of squares
    /// the noise estimate needs.</summary>
    public abstract void EndRay();

    /// <summary>Adds another accumulator of the same detector (a chunk, in chunk order).</summary>
    public abstract void Add(NscDetectorAccumulator other);

    /// <summary>The detector's data after <paramref name="launchedRays"/> launched rays.</summary>
    public abstract NscDetectorData Build(long launchedRays);
}

/// <summary>The accumulators of every detector object of a scene, for one chunk or the total.</summary>
internal sealed class NscDetectorSet
{
    private readonly NscDetectorAccumulator?[] _byObject;
    private readonly List<NscDetectorAccumulator> _list = new();

    private NscDetectorSet(int objectCount, int[] objects, NscDetectorAccumulator[] accumulators)
    {
        _byObject = new NscDetectorAccumulator?[objectCount];
        for (int i = 0; i < objects.Length; i++) { _byObject[objects[i]] = accumulators[i]; _list.Add(accumulators[i]); }
    }

    /// <summary>The set for <paramref name="objects"/>, or null when none is a detector.</summary>
    public static NscDetectorSet? For(IReadOnlyList<NscObject> objects, double airMicrometers)
    {
        var indices = new List<int>();
        var accumulators = new List<NscDetectorAccumulator>();
        for (int i = 0; i < objects.Count; i++)
            if (objects[i].Geometry is INscDetector d)
            {
                indices.Add(i);
                accumulators.Add(d.CreateAccumulator(i + 1, airMicrometers));
            }
        return indices.Count == 0 ? null : new NscDetectorSet(objects.Count, indices.ToArray(), accumulators.ToArray());
    }

    public void Record(int objectIndex, in NscDetectorHit hit) => _byObject[objectIndex]!.Record(hit);

    public void EndRay()
    {
        foreach (var a in _list) a.EndRay();
    }

    public void Add(NscDetectorSet other)
    {
        for (int i = 0; i < _list.Count; i++) _list[i].Add(other._list[i]);
    }

    public IReadOnlyList<NscDetectorData> Build(long launchedRays) => _list.Select(a => a.Build(launchedRays)).ToArray();
}
