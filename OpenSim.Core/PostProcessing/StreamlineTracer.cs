using OpenSim.Core.Numerics;

namespace OpenSim.Core.PostProcessing;

/// <summary>
/// Traces streamlines through a velocity field by classic fixed-step RK4. The field is a
/// sampler delegate — null means "outside the flow" (past the domain, inside a solid) and
/// terminates the trace — so the tracer stays pure Core math, independent of whatever
/// grid or solver produced the velocities.
/// <para>
/// The step is ARC-LENGTH controlled: each RK4 step advances ≈ <c>stepLength</c> along
/// the line regardless of the local speed (Δt = stepLength / |u|), so streamlines are
/// resolved uniformly in space — a time-fixed step would race through fast regions and
/// crawl in slow ones, which is exactly backwards for a display polyline.
/// </para>
/// </summary>
public static class StreamlineTracer
{
    /// <summary>
    /// Traces from <paramref name="seed"/> along the field. Returns the polyline points
    /// (starting at the seed); a single point means the seed was already outside the
    /// flow or at rest.
    /// </summary>
    /// <param name="velocity">Field sampler; null = outside the flow (terminates).</param>
    /// <param name="stepLength">Spatial step per RK4 step [m].</param>
    /// <param name="maxSteps">Step cap, so a closed recirculation cannot loop forever.</param>
    /// <param name="minSpeed">Speed below which the trace stops [m/s] — a stagnation
    /// point is an END of a streamline, not an infinite dwell.</param>
    public static IReadOnlyList<Vector3D> Trace(Vector3D seed,
        Func<Vector3D, Vector3D?> velocity, double stepLength, int maxSteps,
        double minSpeed = 1e-12)
    {
        if (stepLength <= 0)
            throw new ArgumentException("Step length must be positive.", nameof(stepLength));

        var points = new List<Vector3D> { seed };
        var x = seed;
        for (int step = 0; step < maxSteps; step++)
        {
            var u0 = velocity(x);
            if (u0 is null) break;
            double speed = u0.Value.Length;
            if (speed < minSpeed) break;

            double dt = stepLength / speed;
            // RK4: any stage leaving the flow terminates the trace at the last point —
            // half-stepping into a solid has no meaningful velocity to sample.
            var k1 = u0.Value;
            var u2 = velocity(x + k1 * (dt / 2));
            if (u2 is null) break;
            var k2 = u2.Value;
            var u3 = velocity(x + k2 * (dt / 2));
            if (u3 is null) break;
            var k3 = u3.Value;
            var u4 = velocity(x + k3 * dt);
            if (u4 is null) break;
            var k4 = u4.Value;

            x += (k1 + k2 * 2 + k3 * 2 + k4) * (dt / 6.0);
            points.Add(x);
        }
        return points;
    }
}
