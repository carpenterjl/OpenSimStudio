using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Scattering;

/// <summary>A face's scattering at one wavelength: the distributions for the reflected and the
/// transmitted specular ray (null: that ray does not scatter), the number of scatter rays and the
/// thin-window flag.</summary>
public sealed record NscFaceScatterModel(NscScatterDistribution? Reflect, NscScatterDistribution? Transmit, int Rays, bool ThinWindow, string Description);

/// <summary>What one scatter event did to a specular ray of power E: the specular ray's power
/// left, the power absorbed (the part of the fraction a tabular BSDF's TIS does not scatter),
/// the power not traced (importance sampling's correction and dropped draws; it may be negative
/// for one event, its expectation is the scattered power outside the target cones) and whether
/// the ray scattered. E = Specular + Absorbed + NotTraced + Σ scattered rays' powers.</summary>
public readonly record struct NscScatterOutcome(double Specular, double Absorbed, double NotTraced, bool Scattered);

/// <summary>
/// The scattering of a non-sequential trace at one wavelength (plan item OP-80;
/// docs/Optics-Conventions.md §74): every object's faces' models and its Scatter To section,
/// and the event that turns one specular ray into the specular remainder and scattered rays.
/// <list type="bullet">
/// <item><b>Without ray splitting</b> (and with simple splitting): "the decision to scatter or
/// not to scatter is made by the generation of a single random number"; a scattered ray carries
/// "All of the energy" (times the tabular BSDF's TIS).</item>
/// <item><b>With ray splitting</b>: "The specular ray will receive a fraction of the original
/// energy equal to (1.0 − f) ... The remaining energy will be divided equally among the one or
/// more scattered rays"; zero scatter rays: no scattering.</item>
/// <item><b>Importance sampling</b> (help, "Importance Sampling"): scattered ray k ≤ the number of
/// targets is aimed uniformly into the cone of target sphere k if the sphere's solid angle Ω_k
/// does not exceed its limit, and carries E′ = E_s·BSDF·cos θ/TIS·Ω_k, E_s the whole scattered
/// energy (the help's E′ = E·(BSDF/TIS)·Ω·cos θ with E the scattered energy; for one ray and one
/// target the two agree). Where targeted cones overlap, Ω_k is replaced by 1/Σ_j(1/Ω_j) over the
/// cones holding the direction (the balance heuristic), so overlaps are not counted twice. The
/// remaining rays scatter by the BSDF with E_s divided among them, and a draw inside a targeted
/// cone is dropped (that region is counted by the aimed rays). This makes the energy reaching any
/// region an unbiased estimate; it is this program's reading of "Energy normalization is complex"
/// when the numbers of rays and targets differ.</item>
/// </list>
/// Immutable and safe to share between threads.
/// </summary>
public sealed class NscScatterer
{
    private readonly Dictionary<(int Object, int Face), NscFaceScatterModel> _faces = new();
    private readonly (Vector3D Centre, double Radius, double Limit, int Object)[][] _targets;
    private readonly int[]?[] _scatterTo;
    private readonly RigidTransform[] _global;

    /// <summary>The scatterer of a scene at <paramref name="micrometers"/>, or null when no face
    /// scatters. Throws <see cref="InvalidOperationException"/> naming the object and face when a
    /// model cannot be built (an ABg row with an invalid B, a Gaussian σ out of range).</summary>
    public static NscScatterer? For(IReadOnlyList<NscObject> objects, RigidTransform[] global, double micrometers)
    {
        bool any = objects.Any(o => o.FaceProperties?.Values.Any(f => f.Scatter is { Model: not NscScatterModel.None }) == true);
        return any ? new NscScatterer(objects, global, micrometers) : null;
    }

    private NscScatterer(IReadOnlyList<NscObject> objects, RigidTransform[] global, double micrometers)
    {
        _global = global;
        _targets = new (Vector3D, double, double, int)[objects.Count][];
        _scatterTo = new int[]?[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            string where = $"Object {i + 1} ({o.Geometry.TypeName})";
            if (o.FaceProperties is { } faces)
                foreach (var (face, props) in faces)
                {
                    if (props.Scatter is not { Model: not NscScatterModel.None } s) continue;
                    try { _faces[(i, face)] = Model(s, micrometers); }
                    catch (ArgumentException e) { throw new InvalidOperationException($"{where}, face {face}: {e.Message}", e); }
                }
            var to = o.ScatterTo;
            _targets[i] = to is { Method: NscScatterToMethod.ImportanceSampling }
                ? to.Targets.Select(t => (t.Object > 0 ? global[t.Object - 1].Translation : default, t.Radius, t.Limit, t.Object)).ToArray()
                : Array.Empty<(Vector3D, double, double, int)>();
            if (to is { Method: NscScatterToMethod.List })
                _scatterTo[i] = to.Objects.Select(k => k - 1).Append(i).Distinct().OrderBy(k => k).ToArray();
        }
    }

    /// <summary>A face's scattering at <paramref name="micrometers"/> (µm in air). Throws
    /// <see cref="ArgumentException"/> when a model cannot be built.</summary>
    public static NscFaceScatterModel Model(NscFaceScatter s, double micrometers)
    {
        ArgumentNullException.ThrowIfNull(s);
        NscScatterDistribution? reflect = null, transmit = null;
        switch (s.Model)
        {
            case NscScatterModel.Lambertian:
                reflect = transmit = new NscLambertianScatter(s.Fraction);
                break;
            case NscScatterModel.Gaussian:
                reflect = transmit = new NscGaussianScatter(s.Fraction, s.Sigma);
                break;
            case NscScatterModel.ABg:
                if (s.AbgReflect is { } ar) reflect = new NscAbgScatter(ar, micrometers);
                if (s.AbgTransmit is { } at) transmit = new NscAbgScatter(at, micrometers);
                break;
            case NscScatterModel.ABgFile:
                if (s.AbgFileReflect is { } fr) reflect = new NscAbgSumScatter(fr, micrometers);
                if (s.AbgFileTransmit is { } ft) transmit = new NscAbgSumScatter(ft, micrometers);
                break;
            case NscScatterModel.Bsdf:
                if (s.BsdfReflect is { } br) reflect = new NscTableScatter(br, s.Fraction, s.BsdfAngleDegrees);
                if (s.BsdfTransmit is { } bt) transmit = new NscTableScatter(bt, s.Fraction, s.BsdfAngleDegrees);
                break;
            case NscScatterModel.UserDefined:
                throw new ArgumentException("User Defined scattering (a scatter DLL) is not built.");
        }
        return new NscFaceScatterModel(reflect, transmit, s.NumberOfRays, s.ThinWindow, s.Describe());
    }

    /// <summary>The model of a face (0-based object), or null.</summary>
    public NscFaceScatterModel? Face(int obj, int face) => _faces.TryGetValue((obj, face), out var m) ? m : null;

    /// <summary>The objects (0-based, ascending, the scatterer included) a ray scattered from
    /// <paramref name="obj"/> may meet, or null when it has no Scatter To list.</summary>
    public int[]? ScatterToList(int obj) => _scatterTo[obj];

    /// <summary>Descriptions of every scattering face, for notes.</summary>
    public IEnumerable<string> Describe() =>
        _faces.OrderBy(p => p.Key).Select(p => $"object {p.Key.Object + 1} face {p.Key.Face}: {p.Value.Description}");

    /// <summary>
    /// One scatter event. <paramref name="specular"/> is the specular ray's unit direction,
    /// <paramref name="normal"/> the face normal (either orientation), <paramref name="incident"/>
    /// the incoming ray's direction, <paramref name="power"/> the specular ray's power. Scattered
    /// rays (direction, power) are appended to <paramref name="rays"/>. Throws
    /// <see cref="InvalidOperationException"/> when a model refuses (an ABg TIS ≥ 1).
    /// </summary>
    public NscScatterOutcome Scatter(NscScatterDistribution distribution, NscFaceScatterModel model, int obj,
        Vector3D point, Vector3D normal, Vector3D incident, Vector3D specular, double power, bool split,
        SplitMix64 rng, List<(Vector3D Direction, double Power)> rays)
    {
        // The tangent frame: n on the specular side, u the object's local +x projected.
        var n = Vector3D.Dot(specular, normal) >= 0 ? normal : -normal;
        var x = _global[obj].ApplyDirection(Vector3D.UnitX);
        var u = x - n * Vector3D.Dot(x, n);
        if (u.Length < 1e-6) { var y = _global[obj].ApplyDirection(Vector3D.UnitY); u = y - n * Vector3D.Dot(y, n); }
        u /= u.Length;
        var v = Vector3D.Cross(n, u);

        double cosI = Math.Min(1, Math.Abs(Vector3D.Dot(incident, normal)));
        // Sample rotation: the incident ray's direction along the surface, counter-clockwise from
        // the object's local +x, minus 90°, so that a ray in the local YZ plane heading +y is 0°
        // (KB "Zemax Tabular BSDF data file format": "if an incident ray is in the YZ local plane ...
        // the sample rotation would be considered equal to 0").
        var local = _global[obj].InverseApplyDirection(incident);
        double rotation = Math.Abs(local.X) + Math.Abs(local.Y) > 1e-12 ? Math.Atan2(local.Y, local.X) * 180 / Math.PI - 90 : 0;
        var inc = new NscScatterIncidence(Vector3D.Dot(specular, u), Vector3D.Dot(specular, v), Math.Acos(cosI) * 180 / Math.PI, rotation);

        double f = distribution.Fraction(inc);
        if (!(f > 0) || (split && model.Rays == 0)) return new NscScatterOutcome(power, 0, 0, false);
        double w = distribution.Weight(inc);
        int count;
        double scattered, specularLeft, absorbed;
        if (split)
        {
            count = model.Rays;
            specularLeft = power * (1 - f);
            scattered = power * f * w;
            absorbed = power * f * (1 - w);
        }
        else
        {
            if (rng.NextDouble() >= f) return new NscScatterOutcome(power, 0, 0, false);
            count = 1;
            specularLeft = 0;
            scattered = power * w;
            absorbed = power * (1 - w);
        }

        // Targets usable for this event: ray k (k < count) aims at target k.
        var targets = _targets[obj];
        int aimed = Math.Min(count, targets.Length);
        Span<bool> used = stackalloc bool[6];
        Span<double> cosAlpha = stackalloc double[6];
        Span<double> omega = stackalloc double[6];
        Span<Vector3D> axis = stackalloc Vector3D[6];
        int usedCount = 0;
        for (int k = 0; k < aimed; k++)
        {
            if (targets[k].Object == 0) continue;   // "None": this ray scatters by the BSDF
            var to = targets[k].Centre - point;
            double d = to.Length;
            if (!(d > targets[k].Radius)) continue;   // inside the sphere: no cone
            double sinA = targets[k].Radius / d;
            double ca = Math.Sqrt(Math.Max(0, 1 - sinA * sinA));
            double om = 2 * Math.PI * (1 - ca);
            if (om > targets[k].Limit) continue;
            used[k] = true; cosAlpha[k] = ca; omega[k] = om; axis[k] = to / d; usedCount++;
        }

        double sent = 0;
        // Aimed rays.
        for (int k = 0; k < aimed; k++)
        {
            if (!used[k]) continue;
            var dir = NscScatterMath.InCone(axis[k], cosAlpha[k], rng);
            double c = Vector3D.Dot(dir, n);
            if (!(c > 0)) continue;
            double q = 0;
            for (int j = 0; j < aimed; j++)
                if (used[j] && Vector3D.Dot(dir, axis[j]) >= cosAlpha[j]) q += 1 / omega[j];
            double density = distribution.Density(inc, Vector3D.Dot(dir, u), Vector3D.Dot(dir, v));
            double e = scattered * density * c / q;
            if (!(e > 0)) continue;
            rays.Add((dir, e));
            sent += e;
        }
        // Rays scattered by the BSDF.
        int free = count - usedCount;
        if (free > 0)
        {
            double each = scattered / free;
            for (int k = 0; k < free; k++)
            {
                var (bx, by) = distribution.Sample(inc, rng);
                var dir = NscScatterMath.FromProjection(bx, by, u, v, n);
                bool inCone = false;
                for (int j = 0; j < aimed && !inCone; j++)
                    inCone = used[j] && Vector3D.Dot(dir, axis[j]) >= cosAlpha[j];
                if (inCone) continue;
                rays.Add((dir, each));
                sent += each;
            }
        }
        return new NscScatterOutcome(specularLeft, absorbed, scattered - sent, true);
    }

    internal static string Num(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
