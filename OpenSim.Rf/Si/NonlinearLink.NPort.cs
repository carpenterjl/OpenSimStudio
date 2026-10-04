using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Si;

/// <summary>One periodic steady-state solution of an N-line link with nonlinear elements at
/// every port: the last period's node voltages at both ends of every line.</summary>
/// <param name="TimeStepSeconds">Sample step.</param>
/// <param name="NearVolts">Per line, the near-end node voltage over one period.</param>
/// <param name="FarVolts">Per line, the far-end node voltage over one period.</param>
/// <param name="ChannelMemorySamples">The truncated matrix-FIR length actually used.</param>
/// <param name="TailEnergyFraction">The worst retained entry's discarded tail energy.</param>
/// <param name="SkippedEntryFloor">Entries whose total energy fell below this fraction of the
/// largest entry's were treated as zero and not given memory of their own.</param>
public sealed record NonlinearNPortResult(
    double TimeStepSeconds, double[][] NearVolts, double[][] FarVolts,
    int ChannelMemorySamples, double TailEnergyFraction, double SkippedEntryFloor);

public static partial class NonlinearLink
{
    /// <summary>
    /// The N-port nonlinear transient: any number of coupled lines, with an arbitrary nonlinear
    /// element at every one of the 2N ports.
    ///
    /// <para><b>Why this is not the single-line engine generalized.</b> The scalar path reduces
    /// the channel to two FIRs with the receiver admittance BAKED IN, which works only because
    /// the far end is linear and alone. Here every port is an unknown, so the channel is reduced
    /// instead to a reference-terminated transfer impedance
    /// (<see cref="MtlNetwork.TransferImpedance"/>) — a matrix FIR z[k] — and all 2N node
    /// voltages are solved together at each sample:</para>
    /// <code>
    ///   V[n] = z[0]·(I_elem(V[n], t) + g_ref·V[n]) + hist[n]
    /// </code>
    /// <para>The reference conductance appears on both sides and cancels identically; it exists
    /// only to keep the reduction well-conditioned at DC and at degenerate lengths.</para>
    ///
    /// <para>Crosstalk is not a separate mechanism here: the off-diagonal entries of z carry it,
    /// so a quiet victim's clamps respond to the aggressor's coupled energy in the same solve.
    /// That is what the linear engine could already do and the scalar nonlinear one could
    /// not.</para>
    /// </summary>
    /// <param name="network">The coupled channel.</param>
    /// <param name="near">One element per line at the near end (drivers, or quiet loads).</param>
    /// <param name="far">One element per line at the far end (receivers).</param>
    /// <param name="periodSamples">Samples in one repetition period.</param>
    /// <param name="dt">Sample step.</param>
    /// <param name="warmupPeriods">Periods run before the reported one, so the FIR history is
    /// primed and the result is the periodic steady state rather than a turn-on transient.</param>
    /// <param name="tailEnergyBound">Fraction of an entry's energy allowed to fall outside the
    /// truncated FIR.</param>
    /// <param name="referenceSiemens">The reduction's conditioning conductance.</param>
    /// <param name="maxDegreeOfParallelism">Bin-level parallelism for the FIR build.</param>
    public static NonlinearNPortResult SolveNPort(
        MtlNetwork network, IReadOnlyList<INonlinearDriver> near, IReadOnlyList<INonlinearDriver> far,
        int periodSamples, double dt, int warmupPeriods = 4, double tailEnergyBound = 1e-6,
        double referenceSiemens = 1 / 50.0, int? maxDegreeOfParallelism = null)
    {
        int n = network.ConductorCount;
        if (near.Count != n || far.Count != n)
            throw new ArgumentException(
                $"This channel has {n} conductor(s); supply one near and one far element per "
                + $"line (got {near.Count} near, {far.Count} far).");
        if (periodSamples <= 0) throw new ArgumentOutOfRangeException(nameof(periodSamples));
        if (dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        if (warmupPeriods < 0) throw new ArgumentOutOfRangeException(nameof(warmupPeriods));

        int ports = 2 * n;
        var elements = new INonlinearDriver[ports];
        for (int i = 0; i < n; i++) { elements[i] = near[i]; elements[n + i] = far[i]; }

        // ---- Channel reduction: matrix FIR z[k] from the reference-terminated impedance. ----
        // The same channel-FFT length the single-line path uses: long enough that the
        // channel's impulse response has decayed well inside it at every practical length.
        // It bounds the channel's MEMORY, not the pattern: the time stepping below convolves
        // with the truncated FIR and runs for as many samples as the pattern has, so a period
        // longer than the FFT (PRBS-9 and PRBS-11 at 32 samples per UI) is an ordinary case.
        const int fft = 8192;
        var spectra = new Complex[ports * ports][];
        for (int e = 0; e < spectra.Length; e++) spectra[e] = new Complex[fft];

        int half = fft / 2;
        var slots = new Complex[half + 1][];
        try
        {
            Parallel.For(0, half + 1,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 }, m =>
                {
                    double f = m / (fft * dt);
                    var z = network.TransferImpedance(f, referenceSiemens);
                    var flat = new Complex[ports * ports];
                    for (int i = 0; i < ports; i++)
                        for (int j = 0; j < ports; j++) flat[i * ports + j] = z[i, j];
                    slots[m] = flat;                     // ordered slot — bitwise at any DOP
                });
        }
        catch (AggregateException e) { throw e.InnerExceptions[0]; }

        for (int m = 0; m <= half; m++)
            for (int e = 0; e < spectra.Length; e++)
            {
                spectra[e][m] = slots[m][e];
                if (m > 0 && m < half) spectra[e][fft - m] = Complex.Conjugate(slots[m][e]);
            }

        var taps = new double[spectra.Length][];
        for (int e = 0; e < spectra.Length; e++)
        {
            var time = Fft.Inverse(spectra[e]);
            var real = new double[fft];
            for (int k = 0; k < fft; k++) real[k] = time[k].Real;
            taps[e] = real;
        }

        int memory = TruncationLengthMatrix(taps, tailEnergyBound,
            out double tailFraction, out double skippedFloor);

        // ---- Time stepping: Newton on all 2N node voltages per sample. ----
        int total = (warmupPeriods + 1) * periodSamples;
        var history = new double[ports][];
        for (int p = 0; p < ports; p++) history[p] = new double[total];

        var v = new double[ports];
        var vPrev = new double[ports];
        var hist = new double[ports];
        var resid = new double[ports];
        var step = new double[ports];
        var jac = new double[ports * ports];
        var pivot = new int[ports];
        var trial = new double[ports];
        // The port "source" current the channel sees: element current + the reference term.
        var driveHistory = new double[ports][];
        for (int p = 0; p < ports; p++) driveHistory[p] = new double[total];

        double[] z0 = new double[ports * ports];
        for (int e = 0; e < taps.Length; e++) z0[e] = taps[e][0];

        for (int step_n = 0; step_n < total; step_n++)
        {
            // The element schedules are PERIODIC, so time wraps within one period — the same
            // convention the single-line engine uses. Feeding absolute time instead would run
            // every driver off the end of its schedule during warm-up and hold it at the last
            // sample forever, which reads as a driver that never switches.
            double t = (step_n % periodSamples) * dt;

            // History term: everything the channel remembers from earlier samples.
            for (int i = 0; i < ports; i++)
            {
                double sum = 0;
                for (int k = 1; k <= memory && k <= step_n; k++)
                {
                    var tap = taps;
                    for (int j = 0; j < ports; j++)
                        sum += tap[i * ports + j][k] * driveHistory[j][step_n - k];
                }
                hist[i] = sum;
            }

            // v already holds the previous sample's solution, which IS the warm start.
            SolveNode(elements, z0, hist, v, vPrev, dt, t, referenceSiemens, ports,
                resid, step, jac, pivot, trial, step_n);

            for (int i = 0; i < ports; i++)
            {
                var (cur, _) = elements[i].Evaluate(v[i], t);
                double cComp = elements[i].CompCapacitanceFarads;
                double icap = cComp > 0 ? -cComp * (v[i] - vPrev[i]) / dt : 0;
                driveHistory[i][step_n] = cur + icap + referenceSiemens * v[i];
                history[i][step_n] = v[i];
            }
            Array.Copy(v, vPrev, ports);
        }

        var nearOut = new double[n][];
        var farOut = new double[n][];
        int start = warmupPeriods * periodSamples;
        for (int i = 0; i < n; i++)
        {
            nearOut[i] = history[i][start..(start + periodSamples)];
            farOut[i] = history[n + i][start..(start + periodSamples)];
        }
        return new NonlinearNPortResult(dt, nearOut, farOut, memory, tailFraction, skippedFloor);
    }

    /// <summary>Damped Newton on F(V) = V − z0·(I_elem(V) + g·V) − hist. The per-step system is
    /// PIECEWISE-LINEAR (monotone tables into a passive channel), so within one region
    /// combination this converges in a single step; damping exists to break the corner cycling
    /// that a pure Newton can fall into when an iterate straddles a table breakpoint.</summary>
    private static void SolveNode(
        INonlinearDriver[] elements, double[] z0, double[] hist, double[] v, double[] vPrev,
        double dt, double t, double g, int ports,
        double[] resid, double[] step, double[] jac, int[] pivot, double[] trial, int sampleIndex)
    {
        const int maxIterations = 60;
        const int maxHalvings = 40;

        // One evaluation per element per residual, not one per (i, j) pair: the port currents
        // depend only on their own node, so evaluating them inside the matrix loop would repeat
        // every table lookup `ports` times in the engine's hottest loop.
        var portCurrent = new double[ports];
        double Residual(double[] x, double[] into)
        {
            for (int j = 0; j < ports; j++)
            {
                var (cur, _) = elements[j].Evaluate(x[j], t);
                double cComp = elements[j].CompCapacitanceFarads;
                double icap = cComp > 0 ? -cComp * (x[j] - vPrev[j]) / dt : 0;
                portCurrent[j] = cur + icap + g * x[j];
            }
            double norm = 0;
            for (int i = 0; i < ports; i++)
            {
                double sum = 0;
                for (int j = 0; j < ports; j++) sum += z0[i * ports + j] * portCurrent[j];
                into[i] = x[i] - sum - hist[i];
                norm += into[i] * into[i];
            }
            return Math.Sqrt(norm);
        }

        var portConductance = new double[ports];
        double norm0 = Residual(v, resid);
        for (int iter = 0; iter < maxIterations; iter++)
        {
            if (norm0 < 1e-12) return;

            // Jacobian: I − z0·(diag(dI/dV) + g·I), with C_comp's backward-Euler conductance.
            // Each port's conductance is evaluated ONCE and reused down its column.
            for (int j = 0; j < ports; j++)
            {
                var (_, cond) = elements[j].Evaluate(v[j], t);
                double cComp = elements[j].CompCapacitanceFarads;
                portConductance[j] = cond - (cComp > 0 ? cComp / dt : 0) + g;
            }
            for (int i = 0; i < ports; i++)
                for (int j = 0; j < ports; j++)
                    jac[i * ports + j] = (i == j ? 1.0 : 0.0)
                        - z0[i * ports + j] * portConductance[j];

            DenseLu lu;
            try { lu = DenseLu.FactorInPlace(jac, ports, pivot); }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException(
                    $"The nonlinear node Jacobian is singular at sample {sampleIndex} "
                    + $"(t = {t:g4} s): {e.Message} A degenerate channel admittance or a flat "
                    + "V-I table region can do this.", e);
            }
            lu.Solve(resid, step);

            // Trust region. A protection clamp's conducting segment is orders of magnitude
            // stiffer than the pull stages, so a Newton step taken from the flat side of that
            // knee can be enormous — and halving an enormous step still lands far outside the
            // region the Jacobian describes, which is how a descent direction turns into an
            // apparent stall. Capping the step's ∞-norm keeps each iterate inside a range where
            // the piecewise-linear model is meaningful; the direction is unchanged, so this
            // costs iterations, never accuracy.
            double stepMax = 0;
            for (int i = 0; i < ports; i++) stepMax = Math.Max(stepMax, Math.Abs(step[i]));
            const double trustVolts = 1.0;
            if (stepMax > trustVolts)
            {
                double shrink = trustVolts / stepMax;
                for (int i = 0; i < ports; i++) step[i] *= shrink;
            }

            double lambda = 1.0;
            bool accepted = false;
            for (int h = 0; h < maxHalvings; h++)
            {
                double moved = 0;
                for (int i = 0; i < ports; i++)
                {
                    trial[i] = v[i] - lambda * step[i];
                    moved = Math.Max(moved, Math.Abs(lambda * step[i]));
                }
                double norm = Residual(trial, resid);
                // A step that no longer moves the solution is convergence, not failure: the
                // iterate is sitting on a table breakpoint where the residual cannot improve.
                if (norm < norm0 || norm < 1e-12 || moved < 1e-14 * (1 + Math.Abs(v[0])))
                {
                    Array.Copy(trial, v, ports);
                    norm0 = norm;
                    accepted = true;
                    break;
                }
                lambda *= 0.5;
            }
            if (!accepted)
            {
                // Restore the residual at the un-stepped point before reporting.
                Residual(v, resid);
                throw new InvalidOperationException(
                    $"The nonlinear node solve stalled at sample {sampleIndex} (t = {t:g4} s): "
                    + $"no damped Newton step reduced the residual ({norm0:e3}) after "
                    + $"{maxHalvings} halvings. The usual cause is an ACTIVE element — a V-I "
                    + "table whose current has the wrong sign for its voltage excursion, which "
                    + "makes it a negative resistance with no stable operating point (a clamp "
                    + "table entered with the opposite sign convention does exactly this).");
            }
        }
        throw new InvalidOperationException(
            $"The nonlinear node solve did not converge at sample {sampleIndex} "
            + $"(t = {t:g4} s) in {maxIterations} iterations (residual {norm0:e3}).");
    }

    /// <summary>FIR length for a MATRIX impulse response: each entry is truncated against its
    /// OWN energy, and the memory is the worst cut. Entries whose total energy is negligible
    /// against the largest are skipped entirely — otherwise a numerically-zero coupling term
    /// would demand memory for its own rounding noise.</summary>
    private static int TruncationLengthMatrix(double[][] taps, double bound,
        out double tailFraction, out double skippedFloor)
    {
        double Total(double[] x) { double s = 0; foreach (var v in x) s += v * v; return s; }
        var totals = taps.Select(Total).ToArray();
        double largest = totals.Max();
        skippedFloor = 1e-12;
        double floor = skippedFloor * largest;

        int memory = 0;
        tailFraction = 0;
        for (int e = 0; e < taps.Length; e++)
        {
            if (totals[e] <= floor) continue;
            var x = taps[e];
            double acc = 0;
            int cut = 0;
            for (int i = x.Length - 1; i >= 0; i--)
            {
                acc += x[i] * x[i];
                if (acc > bound * totals[e]) { cut = Math.Min(i + 1, x.Length - 1); break; }
            }
            memory = Math.Max(memory, cut);
        }
        for (int e = 0; e < taps.Length; e++)
        {
            if (totals[e] <= floor) continue;
            double tail = 0;
            for (int i = memory + 1; i < taps[e].Length; i++) tail += taps[e][i] * taps[e][i];
            tailFraction = Math.Max(tailFraction, tail / totals[e]);
        }
        return memory;
    }
}
