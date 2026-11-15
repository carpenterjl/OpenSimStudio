using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.FiberCoupling;

/// <summary>The source side of a fiber-coupling computation: a Gaussian source fiber in object
/// space (the help's two-fiber model) or, when <see cref="Ignore"/>, the pupil lit by the system
/// apodization (the one-fiber model).</summary>
public sealed record SourceFiber
{
    /// <summary>Ignore the source fiber: the pupil is lit by the system apodization and every
    /// efficiency is relative to the power entering the entrance pupil.</summary>
    public bool Ignore { get; init; }

    /// <summary>The source fiber's numerical aperture on x and y in object space.</summary>
    public double NaX { get; init; } = 0.1;
    public double NaY { get; init; } = 0.1;

    /// <summary>The source fiber's rotation from its nominal orientation, degrees, in the xz and
    /// yz planes.</summary>
    public double AngleX { get; init; }
    public double AngleY { get; init; }

    /// <summary>Point the source fiber along the chief ray instead of the object z axis.</summary>
    public bool AlignToChiefRay { get; init; }
}

/// <summary>One pupil node of a <see cref="FiberPupil"/>.</summary>
/// <param name="Px">Normalised pupil x.</param>
/// <param name="Py">Normalised pupil y.</param>
/// <param name="Lit">The ray reaches the image surface.</param>
/// <param name="Ray">The ray at the image surface (its frame), or where it failed.</param>
/// <param name="PathWaves">(chief path − ray path) to the image surface in waves of the
/// wavelength in the system's air; NaN when not lit.</param>
/// <param name="Power">P, the source power per unit pupil area (Px, Py) the node carries:
/// the apodization squared (one fiber) or the source fiber's |a|² times the object-space
/// Jacobian (two fibers). Set for every node, lit or not.</param>
/// <param name="ImageJacobian">J = |∂(α, β)/∂(Px, Py)| of the image-space direction cosines on
/// the image surface's x and y; NaN when not lit.</param>
/// <param name="AlphaPx">∂α/∂Px (image space), for the Huygens period check.</param>
/// <param name="BetaPy">∂β/∂Py (image space).</param>
public readonly record struct FiberPupilNode(double Px, double Py, bool Lit, Ray Ray, double PathWaves, double Power,
    double ImageJacobian, double AlphaPx, double BetaPy);

/// <summary>
/// The traced pupil of a fiber-coupling computation (OP-50; docs/Optics-Conventions.md §68): an
/// n × n grid of nodes from −1 to 1 (those inside the unit circle, OP-38's PSF grid), each ray
/// traced to the image surface with its path relative to the chief ray, the power it carries
/// from the source, and the Jacobian of its image-space direction cosines against the pupil
/// coordinates. The Jacobians are central differences of rays launched δ = 1e-5 away in Px and
/// Py and traced with the apertures ignored (so a node at the rim has both neighbours): the
/// difference error is O(δ²) ≈ 1e-10 relative and the rounding about 1e-16/δ ≈ 1e-11.
/// Rows of the grid are traced in parallel, each into its own slot, so the result is bitwise
/// the same at any thread count.
/// </summary>
public sealed class FiberPupil
{
    /// <summary>The step of the difference rays, in normalised pupil units.</summary>
    public const double Delta = 1e-5;

    private FiberPupil() { }

    public required IReadOnlyList<FiberPupilNode> Nodes { get; init; }
    /// <summary>[row, column] of each node in the n × n grid (row 0 at Py = −1).</summary>
    public required IReadOnlyList<(int Row, int Column)> Index { get; init; }
    public required int Sampling { get; init; }
    /// <summary>The node spacing h = 2/(n − 1).</summary>
    public double Step => 2.0 / (Sampling - 1);
    public required Ray ChiefRay { get; init; }
    /// <summary>The image-space index.</summary>
    public required double ImageIndex { get; init; }
    /// <summary>The object-space index.</summary>
    public required double ObjectIndex { get; init; }
    /// <summary>λ in the system's air, meters.</summary>
    public required double AirWavelength { get; init; }
    /// <summary>The source's total power in the same units as Σ P·h²: π·NAx·NAy/(2n²) for a
    /// source fiber (its whole Gaussian), Σ P·h² over every node for the one-fiber model.</summary>
    public required double SourcePower { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Traces the pupil of 0-based <paramref name="field"/> and
    /// <paramref name="wavelength"/>. <paramref name="who"/> names the caller in every
    /// message.</summary>
    public static FiberPupil Trace(SequentialLens lens, OpticalSystemData system, int field, int wavelength, int sampling,
        SourceFiber source, string who, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(source);
        if (field < 0 || field >= system.Fields.Points.Count)
            throw new ArgumentException($"{who}: field {field + 1} is not one of the {system.Fields.Points.Count} fields.");
        if (wavelength < 0 || wavelength >= system.Wavelengths.Wavelengths.Count)
            throw new ArgumentException($"{who}: wavelength {wavelength + 1} is not one of the {system.Wavelengths.Wavelengths.Count} wavelengths.");
        if (sampling < 8 || sampling > 4096)
            throw new ArgumentException($"{who}: the pupil sampling must be 8 to 4096 (it is {sampling}).");
        if (system.Aperture.AfocalImageSpace)
            throw new InvalidOperationException($"{who}: afocal image space has no focus to put a fiber at; turn afocal image space off.");
        if (!source.Ignore && lens.ObjectAtInfinity)
            throw new InvalidOperationException($"{who}: a source fiber needs the object at a finite distance (the object is at infinity); check \"Ignore source fiber\" to light the pupil by the system apodization instead.");
        var limitations = OpdEngine.Limitations(lens);
        if (limitations.Count > 0)
            throw new InvalidOperationException($"{who}: the optical path through this lens is approximate or undefined, so the coupling would be too:"
                + Environment.NewLine + string.Join(Environment.NewLine, limitations));

        IReadOnlyList<double>? semi = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semi = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        var trace = SequentialTrace.Create(lens, system, wavelength, semi);
        var launcher = new RayLauncher(lens, system);
        var (hx, hy) = system.Fields.Normalized(field);
        int count = lens.Surfaces.Count;
        string where = $"{who} (field {field + 1}, wavelength {wavelength + 1})";
        double airWave = OpticsConventions.MicrometersToMeters(trace.AirMicrometers);
        double nImage = trace.Indices[lens.ImageIndex];
        double nObject = trace.Indices[0];

        var chiefSegments = new double[count];
        var chiefLaunch = launcher.Launch(trace, hx, hy, 0, 0);
        var chief = trace.Trace(chiefLaunch, chiefSegments, default);
        if (!chief.IsOk)
            throw new InvalidOperationException($"{where}: the chief ray fails ({RayStatuses.Label(chief.Status)} at surface {chief.FailedAtSurface}).");

        // The source: the apodization, or the source fiber's Gaussian about its axis.
        Func<double, double>? apodization = source.Ignore ? PsfEngine.Apodization(lens, system, who) : null;
        GaussianFiberMode sourceMode = default;
        if (!source.Ignore)
        {
            GaussianFiberMode.Check(source.NaX, source.NaY, nObject, $"{who}, source fiber");
            var nominal = source.AlignToChiefRay ? chiefLaunch.Direction.Normalized() : Vector3D.UnitZ;
            if (!(nominal.Z > 0))
                throw new InvalidOperationException($"{where}: the chief ray does not travel toward +z in object space, so the source fiber cannot point along it.");
            if (Math.Abs(source.AngleX) >= 90 || Math.Abs(source.AngleY) >= 90)
                throw new ArgumentException($"{who}: the source fiber angles must be inside ±90° (they are {source.AngleX}° and {source.AngleY}°).");
            // Slopes add: the axis's x/z slope is the nominal one plus tan(X angle), y likewise.
            var axis = new Vector3D(nominal.X / nominal.Z + Math.Tan(source.AngleX * Math.PI / 180),
                nominal.Y / nominal.Z + Math.Tan(source.AngleY * Math.PI / 180), 1).Normalized();
            sourceMode = new GaussianFiberMode(source.NaX, source.NaY, axis.X, axis.Y);
        }

        var (pupil, index) = PsfEngine.Nodes(sampling);
        int n = sampling;
        var rows = new List<int>[n];
        for (int r = 0; r < n; r++) rows[r] = new List<int>();
        for (int j = 0; j < pupil.Count; j++) rows[index[j].Row].Add(j);
        var unapertured = new TraceOptions { IgnoreApertures = true };

        var slots = ParallelSlots.Compute(n, null, r =>
        {
            var segments = new double[count];
            var result = new List<(int J, FiberPupilNode Node, string? Failure)>();
            foreach (int j in rows[r])
            {
                cancellation.ThrowIfCancellationRequested();
                var (px, py) = pupil[j];
                var launch = launcher.Launch(trace, hx, hy, px, py);
                var ray = trace.Trace(launch, segments, default);
                double power;
                if (source.Ignore) { double a = apodization!(Math.Sqrt(px * px + py * py)); power = a * a; }
                else
                {
                    // Object-space Jacobian of the direction cosines, by central differences of launches.
                    Vector3D L(double x, double y) => launcher.Launch(trace, hx, hy, x, y).Direction.Normalized();
                    var dxp = L(px + Delta, py); var dxm = L(px - Delta, py);
                    var dyp = L(px, py + Delta); var dym = L(px, py - Delta);
                    double jo = Math.Abs((dxp.X - dxm.X) * (dyp.Y - dym.Y) - (dyp.X - dym.X) * (dxp.Y - dxm.Y)) / (4 * Delta * Delta);
                    var d0 = launch.Direction.Normalized();
                    double a = sourceMode.Spectrum(d0.X, d0.Y, nObject);
                    power = a * a * jo;
                }
                if (!ray.IsOk)
                {
                    result.Add((j, new FiberPupilNode(px, py, false, ray, double.NaN, power, double.NaN, double.NaN, double.NaN),
                        $"{RayStatuses.Label(ray.Status)} at surface {ray.FailedAtSurface}"));
                    continue;
                }
                double difference = 0;
                for (int s = 0; s < count; s++) difference += segments[s] - chiefSegments[s];
                double pathWaves = -difference / airWave;

                Vector3D? D(double x, double y)
                {
                    var t = trace.Trace(launcher.Launch(trace, hx, hy, x, y), unapertured);
                    return t.IsOk ? t.Direction : null;
                }
                var ip = D(px + Delta, py); var im = D(px - Delta, py);
                var jp = D(px, py + Delta); var jm = D(px, py - Delta);
                var (aPx, bPx) = Partial(ip, im, ray.Direction);
                var (aPy, bPy) = Partial(jp, jm, ray.Direction);
                if (double.IsNaN(aPx) || double.IsNaN(aPy))
                    throw new InvalidOperationException($"{where}: the rays next to pupil point ({px:g6}, {py:g6}) fail, so the spread of directions there (the Jacobian) cannot be found. Reduce the aperture or the sampling.");
                double ji = Math.Abs(aPx * bPy - aPy * bPx);
                result.Add((j, new FiberPupilNode(px, py, true, ray, pathWaves, power, ji, aPx, bPy), null));
            }
            return result;
        }, cancellation);

        var nodes = new FiberPupilNode[pupil.Count];
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var slot in slots)
            foreach (var (j, node, failure) in slot)
            {
                nodes[j] = node;
                if (failure is not null) failures[failure] = failures.TryGetValue(failure, out int m) ? m + 1 : 1;
            }

        double h = 2.0 / (n - 1);
        double sourcePower = source.Ignore ? nodes.Sum(x => x.Power) * h * h : sourceMode.SpectralPower(nObject);
        var warnings = new List<string>();
        int lit = nodes.Count(x => x.Lit);
        if (lit == 0) throw new InvalidOperationException($"{where}: no ray of the {nodes.Length}-ray pupil grid reaches the image surface.");
        if (failures.Count > 0)
            warnings.Add($"{nodes.Length - lit} of {nodes.Length} pupil rays carry no light: " + string.Join("; ", failures.Select(f => $"{f.Value} × {f.Key}")) + ".");
        var notes = new List<string>
        {
            $"Pupil {n} × {n} nodes from −1 to 1 ({nodes.Length} inside the unit circle, {lit} reaching the image), spacing h = 2/(n − 1); integrals are sums over the nodes times h².",
            source.Ignore
                ? $"One-fiber model: the pupil is lit by the system apodization ({ApodizationTypes.Names.Label(system.Aperture.Apodization)}); efficiencies are relative to the power entering the entrance pupil."
                : $"Two-fiber model: a Gaussian source fiber of NA {source.NaX:g6} × {source.NaY:g6} at the object point, axis direction cosines ({sourceMode.AxisAlpha:g6}, {sourceMode.AxisBeta:g6}); its whole power is π·NAx·NAy/(2n²).",
            $"Direction-space Jacobians by central differences of rays δ = {Delta:g2} away in the pupil, traced with the apertures ignored.",
        };
        return new FiberPupil
        {
            Nodes = nodes, Index = index, Sampling = n, ChiefRay = chief, ImageIndex = nImage, ObjectIndex = nObject,
            AirWavelength = airWave, SourcePower = sourcePower, Notes = notes, Warnings = warnings,
        };
    }

    /// <summary>(∂α, ∂β) along one pupil axis from the rays a step either side: central when both
    /// are traced, one-sided against the centre ray when one fails; NaN when both fail.</summary>
    private static (double Alpha, double Beta) Partial(Vector3D? plus, Vector3D? minus, Vector3D centre)
    {
        if (plus is Vector3D p && minus is Vector3D m) return ((p.X - m.X) / (2 * Delta), (p.Y - m.Y) / (2 * Delta));
        if (plus is Vector3D q) return ((q.X - centre.X) / Delta, (q.Y - centre.Y) / Delta);
        if (minus is Vector3D s) return ((centre.X - s.X) / Delta, (centre.Y - s.Y) / Delta);
        return (double.NaN, double.NaN);
    }
}
