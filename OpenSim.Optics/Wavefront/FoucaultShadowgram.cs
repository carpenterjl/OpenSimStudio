using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Core.Plotting;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Wavefront;

/// <summary>Which half-plane the knife blocks (OpticStudio help, "Foucault Analysis
/// (Wavefront)", 2025 R2: left, right, up and down are −x, +x, +y and −y of the image surface's
/// local frame).</summary>
public enum KnifeEdge
{
    /// <summary>Blocks x ≤ position.</summary>
    VerticalLeft = 0,
    /// <summary>Blocks x ≥ position.</summary>
    VerticalRight = 1,
    /// <summary>Blocks y ≥ position.</summary>
    HorizontalAbove = 2,
    /// <summary>Blocks y ≤ position.</summary>
    HorizontalBelow = 3,
}

/// <summary>A computed knife-edge shadowgram.</summary>
public sealed record FoucaultShadowgramResult
{
    /// <summary>Relative intensity over the normalised pupil (columns Px, rows Py, both −1 to 1),
    /// 1 where the knife takes nothing away on average; NaN outside the pupil or where the ray
    /// failed.</summary>
    public required GridField Intensity { get; init; }

    /// <summary>The OPD the pupil field was built from (chief-ray reference).</summary>
    public required OpdResult Opd { get; init; }

    /// <summary>The image-plane sample spacing and count on each axis (meters).</summary>
    public required double SpacingX { get; init; }
    public required double SpacingY { get; init; }
    public required int SamplesX { get; init; }
    public required int SamplesY { get; init; }

    /// <summary>The direction-cosine pupil scale on each axis: the image-space ray direction's x
    /// (y) component minus the chief ray's, per unit Px (Py), fitted to the traced rays.</summary>
    public required double ScaleX { get; init; }
    public required double ScaleY { get; init; }

    /// <summary>The fraction of the image-plane energy (inside the computed window) the knife lets through.</summary>
    public required double PassedFraction { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>The pupil nodes per side actually used: the requested sampling, or more when the
    /// aberration needs more so the image field does not alias (the notes then say so).</summary>
    public int Sampling { get; init; }
}

/// <summary>
/// The Foucault knife-edge test (OP-35), as OpticStudio's help page "Foucault Analysis
/// (Wavefront)" describes its method: "computing the diffraction based complex amplitude PSF at
/// focus …; then a portion of the complex amplitude is vignetted by the simulated knife edge,
/// and the remaining complex amplitude is propagated back to the near field".
/// <para>
/// <b>Method.</b> The pupil field is A·e^{2πi·W} on an n × n grid of normalised pupil nodes
/// (A = 1 where the ray reaches the image, 0 elsewhere; W the OPD in waves against the chief
/// ray). A pupil node stands for the image-space direction d_c + (a_x·Px, a_y·Py), a_x and a_y
/// fitted to the traced rays' directions by least squares. Derivation of the kernel (Welford was
/// not on hand): a wave aimed at the image point C + δ has, against the sphere centred on C,
/// OPD ≈ n·Δd·δ (Δd the pupil point's direction minus the chief ray's), so the image field
/// U(x) = Σ A·e^{2πi(W − n·Δd·x/λ)} peaks at x = δ, where the ray lands. U is evaluated by the
/// matrix Fourier transform (<see cref="MatrixFourierTransform"/>) on a window of ±(1.5 × the
/// geometric spread of the rays + 16 λ/(n·a)) about the chief ray, sampled at λ/(4n·a) (half the
/// spacing at which the pupil's replicas would touch); the knife zeroes its half-plane, and the
/// inverse sum returns to the pupil. The intensity is divided by the mean of the same
/// reconstruction without the knife, so window truncation cancels out.
/// </para>
/// <para>
/// Not built from the help page: the reference (measured) shadowgram bitmap, its decenter and
/// scale, the difference display and the FOUC operand; polarization.
/// </para>
/// </summary>
public static class FoucaultShadowgram
{
    /// <summary>The image window may hold at most this many samples per axis.</summary>
    public const int MaxImageSamples = 4097;

    /// <summary>The pupil grid may be raised to at most this many nodes per side.</summary>
    public const int MaxPupilSamples = 1025;

    /// <param name="sampling">n: an n × n grid of pupil nodes; at least 8.</param>
    /// <param name="knife">Which half-plane is blocked.</param>
    /// <param name="position">The knife's position relative to the chief ray's image point, along
    /// x for a vertical knife and y for a horizontal one, meters.</param>
    /// <param name="options">The OPD options; the reference point is always the chief ray.</param>
    public static FoucaultShadowgramResult Compute(SequentialLens lens, OpticalSystemData system, int field, int wavelength,
        int sampling, KnifeEdge knife, double position, OpdOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        if (sampling < 8) throw new ArgumentException($"Foucault analysis: the sampling must be at least 8 (it is {sampling}).", nameof(sampling));
        if (!double.IsFinite(position)) throw new ArgumentException("Foucault analysis: the knife position must be finite.", nameof(position));
        if (system.Aperture.AfocalImageSpace)
            throw new InvalidOperationException("Foucault analysis: the knife edge sits at a focus, and afocal image space has none; turn afocal image space off.");
        options = (options ?? new OpdOptions()) with { ReferencePoint = OpdReferencePoint.ChiefRay, ReferenceToPrimary = false };

        string where = $"Foucault analysis (field {field + 1}, wavelength {wavelength + 1})";
        int n = sampling;
        double[] nodes;
        List<(double, double)> pupil;
        List<(int Row, int Column)> index;
        OpdResult opd;
        double ax, ay, lambda, nImage;
        double[] xs, ys;
        double dx, dy;
        string? raised = null;
        for (int attempt = 0; ; attempt++)
        {
            nodes = new double[n];
            for (int k = 0; k < n; k++) nodes[k] = OpdEngine.Node(k, n);
            pupil = new List<(double, double)>();
            index = new List<(int Row, int Column)>();
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                    if (nodes[k] * nodes[k] + nodes[i] * nodes[i] <= 1)
                    {
                        pupil.Add((nodes[k], nodes[i]));
                        index.Add((i, k));
                    }
            opd = OpdEngine.Compute(lens, system, field, wavelength, pupil, options, cancellation);

            // Pupil scale from the rays' directions, and the geometric spread at the image.
            var c = opd.ChiefRay;
            double sxx = 0, sx = 0, syy = 0, sy = 0, spreadX = 0, spreadY = 0;
            int valid = 0;
            foreach (var s in opd.Samples)
            {
                if (!s.IsValid) continue;
                valid++;
                sxx += s.Px * s.Px; sx += (s.Ray.Direction.X - c.Direction.X) * s.Px;
                syy += s.Py * s.Py; sy += (s.Ray.Direction.Y - c.Direction.Y) * s.Py;
                spreadX = Math.Max(spreadX, Math.Abs(s.Ray.Position.X - c.Position.X));
                spreadY = Math.Max(spreadY, Math.Abs(s.Ray.Position.Y - c.Position.Y));
            }
            if (valid == 0 || sxx == 0 || syy == 0)
                throw new InvalidOperationException($"{where}: too few rays reach the image to span the pupil in x and y.");
            ax = sx / sxx; ay = sy / syy;
            if (!(Math.Abs(ax) > 0) || !(Math.Abs(ay) > 0))
                throw new InvalidOperationException($"{where}: the image-space ray directions do not change across the pupil (scale {ax:g3}, {ay:g3}), so there is no focus.");

            lambda = OpticsConventions.MicrometersToMeters(opd.AirMicrometers);
            nImage = opd.ImageIndex;
            double l = lambda, ni = nImage;
            (double[] Coordinates, double Spacing) Window(double a, double spread, string axis)
            {
                double spacing = l / (4 * ni * Math.Abs(a));
                double half = 1.5 * spread + 16 * l / (ni * Math.Abs(a));
                int m = 2 * (int)Math.Ceiling(half / spacing) + 1;
                if (m > MaxImageSamples)
                    throw new InvalidOperationException(
                        $"{where}: the rays spread {spread:g3} m in {axis} at the image, which needs {m} image samples (at most {MaxImageSamples}); the aberration is too large for a diffraction shadowgram—bring the image nearer focus or use a smaller field.");
                var coordinates = new double[m];
                for (int p = 0; p < m; p++) coordinates[p] = (p - (m - 1) / 2) * spacing;
                return (coordinates, spacing);
            }
            (xs, dx) = Window(ax, spreadX, "x");
            (ys, dy) = Window(ay, spreadY, "y");

            // A pupil sampled every Δp makes the image field periodic with period
            // λ/(n·|a|·Δp); the window ±half holds the field only if that period is at least
            // 2·half, else the spot's replicas overlap it (aliasing). So Δp ≤ λ/(2n·|a|·half).
            int needed = 0;
            foreach (var (a, coordinates) in new[] { (ax, xs), (ay, ys) })
            {
                double half = coordinates[^1];
                double step = lambda / (2 * nImage * Math.Abs(a) * half);
                needed = Math.Max(needed, (int)Math.Ceiling(2 / step) + 1);
            }
            if (needed <= n) break;
            if (needed > MaxPupilSamples)
                throw new InvalidOperationException(
                    $"{where}: the aberration needs {needed} pupil samples per side so the image field does not alias (at most {MaxPupilSamples}); bring the image nearer focus or use a smaller field.");
            if (attempt >= 3)
                throw new InvalidOperationException($"{where}: the pupil sampling needed did not settle ({n} then {needed}).");
            // 10 % more than needed, because the finer grid reaches rays nearer the pupil edge
            // and so a slightly larger spread.
            n = Math.Min(MaxPupilSamples, needed + (needed + 9) / 10);
            raised = $"Pupil sampling raised from {sampling} to {n} per side so the image field does not alias (a pupil step Δp needs λ/(n·a·Δp) ≥ the window width).";
        }

        // Pupil field, rows Px (k), columns Py (i).
        var field_ = new Complex[n, n];
        for (int j = 0; j < pupil.Count; j++)
        {
            var s = opd.Samples[j];
            if (!s.IsValid) continue;
            var (row, column) = index[j];
            double phase = 2 * Math.PI * (s.Waves - Math.Floor(s.Waves));
            field_[column, row] = new Complex(Math.Cos(phase), Math.Sin(phase));
        }
        cancellation.ThrowIfCancellationRequested();
        var fx = xs.Select(x => nImage * ax * x / lambda).ToArray();
        var fy = ys.Select(y => nImage * ay * y / lambda).ToArray();
        var image = MatrixFourierTransform.Transform(field_, nodes, nodes, fx, fy, maxDegreeOfParallelism: null);
        cancellation.ThrowIfCancellationRequested();

        var blocked = new Complex[xs.Length, ys.Length];
        double total = 0, passed = 0;
        for (int p = 0; p < xs.Length; p++)
            for (int q = 0; q < ys.Length; q++)
            {
                var u = image[p, q];
                double e = u.Real * u.Real + u.Imaginary * u.Imaginary;
                total += e;
                bool stop = knife switch
                {
                    KnifeEdge.VerticalLeft => xs[p] <= position,
                    KnifeEdge.VerticalRight => xs[p] >= position,
                    KnifeEdge.HorizontalAbove => ys[q] >= position,
                    _ => ys[q] <= position,
                };
                if (!stop) { blocked[p, q] = u; passed += e; }
            }
        var back = nodes.Select(v => nImage * ax * v / lambda).ToArray();
        var backY = nodes.Select(v => nImage * ay * v / lambda).ToArray();
        var full = MatrixFourierTransform.Transform(image, xs, ys, back, backY, inverse: true, maxDegreeOfParallelism: null);
        cancellation.ThrowIfCancellationRequested();
        var cut = MatrixFourierTransform.Transform(blocked, xs, ys, back, backY, inverse: true, maxDegreeOfParallelism: null);

        double mean = 0;
        int counted = 0;
        for (int j = 0; j < pupil.Count; j++)
        {
            if (!opd.Samples[j].IsValid) continue;
            var (row, column) = index[j];
            mean += Complex.Abs(full[column, row]) * Complex.Abs(full[column, row]);
            counted++;
        }
        mean /= counted;
        var values = new double[n, n];
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) values[i, k] = double.NaN;
        for (int j = 0; j < pupil.Count; j++)
        {
            if (!opd.Samples[j].IsValid) continue;
            var (row, column) = index[j];
            var v = cut[column, row];
            values[row, column] = (v.Real * v.Real + v.Imaginary * v.Imaginary) / mean;
        }

        return new FoucaultShadowgramResult
        {
            Intensity = new GridField(values, -1, 1, -1, 1),
            Opd = opd, SpacingX = dx, SpacingY = dy, SamplesX = xs.Length, SamplesY = ys.Length,
            ScaleX = ax, ScaleY = ay, PassedFraction = total > 0 ? passed / total : 0,
            Notes = new[]
            {
                $"Image window {xs.Length} × {ys.Length} samples at {dx:g4} × {dy:g4} m, centred on the chief ray; pupil {n} × {n} nodes.",
                $"Pupil scale (direction cosine per unit pupil coordinate) fitted to the rays: {ax:g6} in x, {ay:g6} in y.",
                $"The knife passes {(total > 0 ? passed / total : 0):P2} of the energy in the window.",
            }.Concat(raised is null ? Array.Empty<string>() : new[] { raised }).ToArray(),
            Sampling = n,
        };
    }
}
