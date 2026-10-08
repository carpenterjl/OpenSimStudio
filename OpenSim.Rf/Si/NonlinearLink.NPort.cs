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
/// <param name="WarmupPeriods">Periods actually run before the reported one: the requested
/// count, raised to cover the channel memory and then until the circuit settled.</param>
/// <param name="SettlingResidualVolts">The largest change at any node between the reported
/// period and the one before it: how far from the periodic steady state the result still is.</param>
public sealed record NonlinearNPortResult(
    double TimeStepSeconds, double[][] NearVolts, double[][] FarVolts,
    int ChannelMemorySamples, double TailEnergyFraction, double SkippedEntryFloor,
    int WarmupPeriods, double SettlingResidualVolts);

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
    /// <para><b>Port capacitance.</b> Every element's C_comp (a driver's die capacitance, a
    /// receiver's load capacitance) is a LINEAR shunt at its port, so it belongs to the linear
    /// part: it is added to the reduction, Z′ = (Z⁻¹ + Y_C)⁻¹, and the time stepping never
    /// integrates it separately. Y_C is the TRAPEZOIDAL-rule capacitor on this sampling grid,
    /// (2C/Δt)·j·tan(ωΔt/2) — jωC to second order in ωΔt — and not jωC itself: a continuous
    /// capacitor's response is not band-limited, so sampling it to Nyquist leaves a
    /// discontinuity there and a slowly decaying, non-causal FIR (measured: 8191 taps and a
    /// 1.6 % error until the warm-up outlasted them). The trapezoidal form is periodic in
    /// frequency and causal in time, and being solved together with the line it has none of
    /// the sample-to-sample ringing the trapezoidal rule shows when stepped explicitly.
    /// It used to be stepped by backward Euler, which is first order: at 32 samples per UI a
    /// 5 pF load's time constant came out 12–23 % long.</para>
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
    /// primed and the result is the periodic steady state rather than a turn-on transient. A
    /// minimum: raised to cover the channel memory (reported in the result).</param>
    /// <param name="tailEnergyBound">Fraction of an entry's energy allowed to fall outside the
    /// truncated FIR.</param>
    /// <param name="referenceSiemens">The reduction's conditioning conductance.</param>
    /// <param name="maxDegreeOfParallelism">Bin-level parallelism for the FIR build.</param>
    public static NonlinearNPortResult SolveNPort(
        MtlNetwork network, IReadOnlyList<INonlinearDriver> near, IReadOnlyList<INonlinearDriver> far,
        int periodSamples, double dt, int warmupPeriods = 4, double tailEnergyBound = 1e-6,
        double referenceSiemens = 1 / 50.0, int? maxDegreeOfParallelism = null)
    {
        if (dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        // The slowest the line can be over the band the FIR is built on: its lowest bin and
        // Nyquist (a dispersive dielectric and internal inductance are slower at the low end).
        double delay = Math.Max(network.LongestOneWayDelaySeconds(1 / (MinimumChannelFft * dt)),
            network.LongestOneWayDelaySeconds(1 / (2 * dt)));
        return SolveNPort(network.ConductorCount, network.TransferImpedance, near, far,
            periodSamples, dt, warmupPeriods, tailEnergyBound, referenceSiemens,
            maxDegreeOfParallelism, delay);
    }

    /// <summary>
    /// The same engine on any channel that can give its reference-terminated transfer impedance
    /// (frequency [Hz], reference conductance [S]) → 2N×2N, ports 0..N−1 near and N..2N−1 far —
    /// a measured or imported channel as well as a line model. The function is called at every
    /// bin from DC to the Nyquist frequency 1/(2·dt) and must be defined over that whole range.
    /// <paramref name="oneWayDelaySeconds"/> is the channel's longest one-way delay, from which
    /// the FIR window is sized; 0 (unknown) leaves it at the minimum window.
    /// </summary>
    public static NonlinearNPortResult SolveNPort(
        int lineCount, Func<double, double, Complex[,]> transferImpedance,
        IReadOnlyList<INonlinearDriver> near, IReadOnlyList<INonlinearDriver> far,
        int periodSamples, double dt, int warmupPeriods = 4, double tailEnergyBound = 1e-6,
        double referenceSiemens = 1 / 50.0, int? maxDegreeOfParallelism = null,
        double oneWayDelaySeconds = 0)
    {
        int n = lineCount;
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(lineCount));
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
        // The DFT length bounds the channel's MEMORY, not the pattern: the time stepping below
        // convolves with the truncated FIR and runs for as many samples as the pattern has, so a
        // period longer than the FFT (PRBS-9 and PRBS-11 at 32 samples per UI) is an ordinary
        // case. What it must hold is the impulse response itself, or the part past the window
        // wraps around onto its start (SI-15): see ChannelWindow.
        int fft = ChannelWindow(oneWayDelaySeconds, dt);
        var spectra = new Complex[ports * ports][];
        for (int e = 0; e < spectra.Length; e++) spectra[e] = new Complex[fft];

        var capacitance = new double[ports];
        bool anyCapacitance = false;
        for (int p = 0; p < ports; p++)
        {
            capacitance[p] = Math.Max(0, elements[p].CompCapacitanceFarads);
            anyCapacitance |= capacitance[p] > 0;
        }

        int half = fft / 2;
        var slots = new Complex[half + 1][];
        try
        {
            Parallel.For(0, half + 1,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 }, m =>
                {
                    double f = m / (fft * dt);
                    var z = transferImpedance(f, referenceSiemens);
                    var flat = new Complex[ports * ports];
                    if (anyCapacitance && m > 0)
                    {
                        // Z′ = (Z⁻¹ + Y_C)⁻¹ = (I + Z·Y_C)⁻¹ Z, column by column, with the
                        // trapezoidal capacitor Y_C = j·(2C/Δt)·tan(ωΔt/2).
                        double omega = 2 / dt * Math.Tan(Math.PI * f * dt);
                        var system = new ComplexDenseMatrix(ports, ports);
                        for (int i = 0; i < ports; i++)
                            for (int j = 0; j < ports; j++)
                                system[i, j] = (i == j ? Complex.One : Complex.Zero)
                                    + z[i, j] * new Complex(0, omega * capacitance[j]);
                        var lu = ComplexLu.Factor(system, 1);
                        var column = new Complex[ports];
                        for (int j = 0; j < ports; j++)
                        {
                            for (int i = 0; i < ports; i++) column[i] = z[i, j];
                            var solved = lu.Solve(column);
                            for (int i = 0; i < ports; i++) flat[i * ports + j] = solved[i];
                        }
                    }
                    else
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
        // The run is reported once it is the periodic steady state, which takes two things
        // (SI-15). The warm-up has to outlast the FIR, or the reported period still carries the
        // turn-on transient through the history sum: four periods of an 8-bit pattern are 1024
        // samples at 32 per UI, against a memory that can run to thousands. And the CIRCUIT has
        // to have settled, which the FIR length does not measure: the reduction is terminated
        // in the reference, the circuit in its own elements, and a 30 Ω driver on a 75 Ω line
        // keeps 43 % of every echo. So the requested count is a minimum, raised to cover the
        // memory, and periods then keep running until one repeats the last.
        int warmup = Math.Max(warmupPeriods, (memory + periodSamples - 1) / periodSamples);
        int lastPeriod = warmup + Math.Max(1,
            SettlingSamplesPerMemory * Math.Max(memory, periodSamples) / periodSamples);

        var v = new double[ports];
        var vPrev = new double[ports];
        var hist = new double[ports];
        var resid = new double[ports];
        var step = new double[ports];
        var jac = new double[ports * ports];
        var pivot = new int[ports];
        var trial = new double[ports];
        // The port "source" current the channel sees: element current + the reference term.
        // (No capacitor current: the port capacitances are inside z.) A ring of memory + 1
        // samples, written twice (at w and w + ring) so the last `memory` samples are always
        // one contiguous run ending at w + ring − 1; before the first sample it holds zeros,
        // which is the quiet line the run starts from.
        int ring = memory + 1;
        var driveHistory = new double[ports][];
        for (int p = 0; p < ports; p++) driveHistory[p] = new double[2 * ring];
        var current = new double[ports][];
        var previous = new double[ports][];
        for (int p = 0; p < ports; p++)
        {
            current[p] = new double[periodSamples];
            previous[p] = new double[periodSamples];
        }

        double[] z0 = new double[ports * ports];
        for (int e = 0; e < taps.Length; e++) z0[e] = taps[e][0];

        int step_n = 0, period = 0, quietPeriods = 0;
        int periodsPerMemory = Math.Max(1, (memory + periodSamples - 1) / periodSamples);
        double settling = double.PositiveInfinity;
        for (; ; period++)
        {
            for (int s = 0; s < periodSamples; s++, step_n++)
            {
                // The element schedules are PERIODIC, so time wraps within one period — the
                // same convention the single-line engine uses. Feeding absolute time instead
                // would run every driver off the end of its schedule during warm-up and hold it
                // at the last sample forever, which reads as a driver that never switches.
                double t = s * dt;
                int w = step_n % ring;

                // History term: everything the channel remembers from earlier samples.
                for (int i = 0; i < ports; i++)
                {
                    double sum = 0;
                    for (int j = 0; j < ports; j++)
                    {
                        var tap = taps[i * ports + j];
                        var drive = driveHistory[j];
                        for (int k = 1; k <= memory; k++) sum += tap[k] * drive[w + ring - k];
                    }
                    hist[i] = sum;
                }

                // v already holds the previous sample's solution, which IS the warm start.
                SolveNode(elements, z0, hist, v, vPrev, dt, t, referenceSiemens, ports,
                    resid, step, jac, pivot, trial, step_n);

                for (int i = 0; i < ports; i++)
                {
                    var (cur, _) = elements[i].Evaluate(v[i], t);
                    double drive = cur + referenceSiemens * v[i];
                    driveHistory[i][w] = drive;
                    driveHistory[i][w + ring] = drive;
                    current[i][s] = v[i];
                }
                Array.Copy(v, vPrev, ports);
            }

            if (period >= warmup)
            {
                double change = 0, scale = 0;
                for (int i = 0; i < ports; i++)
                    for (int s = 0; s < periodSamples; s++)
                    {
                        change = Math.Max(change, Math.Abs(current[i][s] - previous[i][s]));
                        scale = Math.Max(scale, Math.Abs(current[i][s]));
                    }
                // One quiet period is not enough. A line whose round trip is many periods long
                // changes only when an echo arrives, so between arrivals two periods agree
                // exactly while the circuit is still far from settled. What fixes everything
                // that follows is the last `memory` samples (they are the whole of the history
                // sum), so the run is settled once every period across a memory span repeated
                // the one before it.
                if (change <= SettledFraction * scale)
                {
                    settling = quietPeriods == 0 ? change : Math.Max(settling, change);
                    quietPeriods++;
                }
                else
                {
                    settling = change;
                    quietPeriods = 0;
                }
                if (quietPeriods >= periodsPerMemory || period >= lastPeriod) break;
            }
            (current, previous) = (previous, current);
        }

        var nearOut = new double[n][];
        var farOut = new double[n][];
        for (int i = 0; i < n; i++)
        {
            nearOut[i] = current[i];
            farOut[i] = current[n + i];
        }
        return new NonlinearNPortResult(dt, nearOut, farOut, memory, tailFraction, skippedFloor,
            period, settling);
    }

    /// <summary>A period counts as the steady state when no node moved by more than this
    /// fraction of the largest node voltage since the period before.</summary>
    private const double SettledFraction = 1e-6;

    /// <summary>How long the run may go on settling past the warm-up, in channel memories (or
    /// periods, if longer). A circuit still moving after that is reported with its residual
    /// rather than run on without bound.</summary>
    private const int SettlingSamplesPerMemory = 32;

    /// <summary>The smallest channel DFT, and the largest this engine will build (one complex
    /// spectrum per port pair at this length).</summary>
    internal const int MinimumChannelFft = 8192, MaximumChannelFft = 1 << 18;

    /// <summary>Round trips of the longest line the FIR window must hold. Reference-terminated,
    /// an echo loses (1 − |Γ_ref|) of itself at every return to the near end: a 75 Ω or a 33 Ω
    /// line under the 50 Ω reference is down to 0.2⁸ ≈ 3·10⁻⁶ after eight.</summary>
    private const int RoundTripsInWindow = 8;

    /// <summary>The DFT length: a power of two, at least <see cref="MinimumChannelFft"/>, and at
    /// least <see cref="RoundTripsInWindow"/> round trips of the channel. A fixed window was
    /// silently too short for a line longer than about a thousand samples: the echoes past its
    /// end came back at the start of the FIR.</summary>
    private static int ChannelWindow(double oneWayDelaySeconds, double dt)
    {
        double needed = RoundTripsInWindow * 2 * Math.Max(0, oneWayDelaySeconds) / dt;
        if (!(needed <= MaximumChannelFft))
            throw new ArgumentException(
                $"The channel's round trip is {2 * oneWayDelaySeconds / dt:F0} samples at this "
                + $"step; holding {RoundTripsInWindow} of them needs a {needed:F0}-point DFT, more "
                + $"than the {MaximumChannelFft} this engine builds. Use a coarser sample step or "
                + "a shorter channel.");
        int fft = MinimumChannelFft;
        while (fft < needed) fft *= 2;
        return fft;
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
                portCurrent[j] = cur + g * x[j];
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

            // Jacobian: I − z0·(diag(dI/dV) + g·I).
            // Each port's conductance is evaluated ONCE and reused down its column.
            for (int j = 0; j < ports; j++)
            {
                var (_, cond) = elements[j].Evaluate(v[j], t);
                portConductance[j] = cond + g;
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
