namespace OpenSim.Core.Interfaces;

public enum PowerProfileKind
{
    /// <summary>The sources as given, all the time.</summary>
    Constant,

    /// <summary>On for a fraction of every period, off for the rest.</summary>
    PulseTrain,

    /// <summary>A list of (time, scale) points joined by straight lines.</summary>
    Table
}

/// <summary>
/// How the heat sources of a transient solve vary in time: a factor on every heat flow and
/// volumetric source (a convection condition's ambient is not a source and is not scaled).
/// A plain record of numbers, so it travels with the settings.
/// </summary>
public sealed record PowerProfile
{
    public PowerProfileKind Kind { get; init; } = PowerProfileKind.Constant;

    /// <summary>Pulse train: period [s].</summary>
    public double Period { get; init; }

    /// <summary>Pulse train: fraction of the period the power is on, 0…1.</summary>
    public double DutyCycle { get; init; } = 0.5;

    /// <summary>Pulse train: factor while on.</summary>
    public double OnScale { get; init; } = 1.0;

    /// <summary>Pulse train: factor while off.</summary>
    public double OffScale { get; init; }

    /// <summary>Pulse train: time of the first switch-on [s]; off before it.</summary>
    public double Delay { get; init; }

    /// <summary>Table: rising times [s]. Before the first the first scale holds, after
    /// the last the last.</summary>
    public IReadOnlyList<double> Times { get; init; } = Array.Empty<double>();

    /// <summary>Table: the factor at each time.</summary>
    public IReadOnlyList<double> Scales { get; init; } = Array.Empty<double>();

    public static PowerProfile Pulse(double period, double dutyCycle, double onScale = 1, double offScale = 0,
        double delay = 0) => new()
    {
        Kind = PowerProfileKind.PulseTrain, Period = period, DutyCycle = dutyCycle,
        OnScale = onScale, OffScale = offScale, Delay = delay
    };

    public static PowerProfile Table(IReadOnlyList<double> times, IReadOnlyList<double> scales) =>
        new() { Kind = PowerProfileKind.Table, Times = times, Scales = scales };

    public void Validate()
    {
        switch (Kind)
        {
            case PowerProfileKind.PulseTrain:
                if (!(Period > 0)) throw new InvalidOperationException("The pulse period must be positive.");
                if (DutyCycle is < 0 or > 1 || double.IsNaN(DutyCycle))
                    throw new InvalidOperationException("The duty cycle must lie between 0 and 1.");
                if (Delay < 0) throw new InvalidOperationException("The pulse delay cannot be negative.");
                break;
            case PowerProfileKind.Table:
                if (Times.Count == 0 || Times.Count != Scales.Count)
                    throw new InvalidOperationException("A power table needs as many scales as times, and at least one.");
                for (int i = 1; i < Times.Count; i++)
                    if (!(Times[i] > Times[i - 1]))
                        throw new InvalidOperationException("The power table's times must rise.");
                break;
        }
    }

    /// <summary>The factor at a time.</summary>
    public double At(double time)
    {
        switch (Kind)
        {
            case PowerProfileKind.PulseTrain:
                double u = time - Delay;
                if (u < 0) return OffScale;
                double phase = u - Math.Floor(u / Period) * Period;
                return phase < DutyCycle * Period ? OnScale : OffScale;
            case PowerProfileKind.Table:
                if (time <= Times[0]) return Scales[0];
                if (time >= Times[^1]) return Scales[^1];
                int hi = 1;
                while (Times[hi] < time) hi++;
                double w = (time - Times[hi - 1]) / (Times[hi] - Times[hi - 1]);
                return Scales[hi - 1] + w * (Scales[hi] - Scales[hi - 1]);
            default:
                return 1.0;
        }
    }

    /// <summary>
    /// The mean factor over an interval — exactly, whatever falls inside it. A time step
    /// that uses this delivers the profile's energy to the last digit even when a pulse
    /// edge lies inside the step.
    /// </summary>
    public double Mean(double from, double to)
    {
        if (!(to > from)) return At(from);
        return Kind == PowerProfileKind.Constant ? 1.0 : (Integral(to) - Integral(from)) / (to - from);
    }

    /// <summary>∫₀ᵗ of the factor.</summary>
    private double Integral(double time)
    {
        if (Kind == PowerProfileKind.PulseTrain)
        {
            double u = time - Delay, on = 0;
            if (u > 0)
            {
                double cycles = Math.Floor(u / Period);
                on = cycles * DutyCycle * Period + Math.Min(u - cycles * Period, DutyCycle * Period);
            }
            return OffScale * time + (OnScale - OffScale) * on;
        }
        // Table, from the first listed time (the constant before it adds the same to both
        // ends of any difference only when both lie before it, handled by the first branch).
        double t0 = Times[0];
        if (time <= t0) return Scales[0] * (time - t0);
        double sum = 0;
        for (int i = 1; i < Times.Count; i++)
        {
            if (time >= Times[i])
            {
                sum += 0.5 * (Scales[i - 1] + Scales[i]) * (Times[i] - Times[i - 1]);
                continue;
            }
            double w = (time - Times[i - 1]) / (Times[i] - Times[i - 1]);
            double at = Scales[i - 1] + w * (Scales[i] - Scales[i - 1]);
            return sum + 0.5 * (Scales[i - 1] + at) * (time - Times[i - 1]);
        }
        return sum + Scales[^1] * (time - Times[^1]);
    }

    /// <summary>The shortest stretch the profile holds one state for [s] — what a time step
    /// has to be shorter than to follow it; infinite for a constant.</summary>
    public double ShortestInterval()
    {
        switch (Kind)
        {
            case PowerProfileKind.PulseTrain:
                double on = DutyCycle * Period, off = (1 - DutyCycle) * Period;
                return on > 0 && off > 0 ? Math.Min(on, off) : Period;
            case PowerProfileKind.Table:
                double shortest = double.PositiveInfinity;
                for (int i = 1; i < Times.Count; i++) shortest = Math.Min(shortest, Times[i] - Times[i - 1]);
                return shortest;
            default:
                return double.PositiveInfinity;
        }
    }

    public string Describe() => Kind switch
    {
        PowerProfileKind.PulseTrain =>
            $"pulse train, period {Period:g4} s, {DutyCycle * 100:g3} % on at ×{OnScale:g3}, off at ×{OffScale:g3}" +
            (Delay > 0 ? $", first pulse at {Delay:g4} s" : ""),
        PowerProfileKind.Table => $"table of {Times.Count} points from {Times[0]:g4} s to {Times[^1]:g4} s",
        _ => "constant"
    };
}
