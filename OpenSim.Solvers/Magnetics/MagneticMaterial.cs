using OpenSim.Core.Model;

namespace OpenSim.Solvers.Magnetics;

/// <summary>
/// A B–H curve: H(B) through the given points by monotone cubic (Fritsch–Carlson) interpolation,
/// so dH/dB is continuous and Newton's method sees a smooth reluctivity. Above the last point the
/// material is taken as saturated, dH/dB = 1/µ₀; below the first point the curve runs straight
/// to the origin.
/// </summary>
public sealed class BhCurve
{
    private readonly double[] _b, _h, _slope, _w;

    /// <summary>Points (H [A/m], B [T]) with both strictly increasing; the origin is added when
    /// absent.</summary>
    public BhCurve(IReadOnlyList<(double H, double B)> points)
    {
        var list = points.OrderBy(p => p.B).ToList();
        if (list.Count == 0 || list[0].B > 0) list.Insert(0, (0, 0));
        for (int i = 1; i < list.Count; i++)
            if (!(list[i].B > list[i - 1].B && list[i].H > list[i - 1].H))
                throw new ArgumentException("A B–H curve needs H and B both strictly increasing.", nameof(points));
        if (list.Count < 2) throw new ArgumentException("A B–H curve needs at least one point besides the origin.", nameof(points));
        _b = list.Select(p => p.B).ToArray();
        _h = list.Select(p => p.H).ToArray();
        int n = _b.Length;
        var secant = new double[n - 1];
        for (int i = 0; i < n - 1; i++) secant[i] = (_h[i + 1] - _h[i]) / (_b[i + 1] - _b[i]);
        _slope = new double[n];
        _slope[0] = secant[0];
        _slope[n - 1] = secant[n - 2];
        for (int i = 1; i < n - 1; i++)
            _slope[i] = secant[i - 1] * secant[i] <= 0 ? 0 : 2 / (1 / secant[i - 1] + 1 / secant[i]);
        // Cumulative energy density w = ∫H dB at the knots — the Hermite cubic's exact integral.
        _w = new double[n];
        for (int i = 0; i + 1 < n; i++)
        {
            double dx = _b[i + 1] - _b[i];
            _w[i + 1] = _w[i] + dx * (0.5 * (_h[i] + _h[i + 1]) + dx * (_slope[i] - _slope[i + 1]) / 12);
        }
    }

    public IReadOnlyList<double> B => _b;
    public IReadOnlyList<double> H => _h;

    /// <summary>H(B) and dH/dB at flux density magnitude <paramref name="b"/> ≥ 0.</summary>
    public (double H, double Slope) Evaluate(double b)
    {
        b = Math.Abs(b);
        int n = _b.Length;
        if (b >= _b[n - 1])
            return (_h[n - 1] + (b - _b[n - 1]) / MagneticConstants.Mu0, 1 / MagneticConstants.Mu0);
        int i = Array.BinarySearch(_b, b);
        if (i >= 0) return (_h[i], _slope[i]);
        i = ~i - 1;
        double dx = _b[i + 1] - _b[i], t = (b - _b[i]) / dx;
        double h00 = (1 + 2 * t) * (1 - t) * (1 - t), h10 = t * (1 - t) * (1 - t);
        double h01 = t * t * (3 - 2 * t), h11 = t * t * (t - 1);
        double value = h00 * _h[i] + h10 * dx * _slope[i] + h01 * _h[i + 1] + h11 * dx * _slope[i + 1];
        double d00 = 6 * t * t - 6 * t, d10 = 3 * t * t - 4 * t + 1, d01 = -6 * t * t + 6 * t, d11 = 3 * t * t - 2 * t;
        double slope = (d00 * _h[i] + d01 * _h[i + 1]) / dx + d10 * _slope[i] + d11 * _slope[i + 1];
        return (value, slope);
    }

    /// <summary>Energy density w(B) = ∫₀^B H dB [J/m³] — the exact integral of the interpolant;
    /// above the last point the saturated tail H_sat + (B − B_sat)/µ₀ is integrated too.</summary>
    public double EnergyDensity(double b)
    {
        b = Math.Abs(b);
        int n = _b.Length;
        if (b >= _b[n - 1])
        {
            double db = b - _b[n - 1];
            return _w[n - 1] + _h[n - 1] * db + 0.5 * db * db / MagneticConstants.Mu0;
        }
        int i = Array.BinarySearch(_b, b);
        if (i >= 0) return _w[i];
        i = ~i - 1;
        double dx = _b[i + 1] - _b[i], t = (b - _b[i]) / dx;
        double t2 = t * t, t3 = t2 * t, t4 = t3 * t;
        // Antiderivatives of the Hermite basis functions, zero at t = 0.
        double q00 = 0.5 * t4 - t3 + t;
        double q10 = 0.25 * t4 - 2.0 / 3 * t3 + 0.5 * t2;
        double q01 = -0.5 * t4 + t3;
        double q11 = 0.25 * t4 - t3 / 3;
        return _w[i] + dx * (q00 * _h[i] + q10 * dx * _slope[i] + q01 * _h[i + 1] + q11 * dx * _slope[i + 1]);
    }

    /// <summary>The energy-equivalent effective reluctivity for a sinusoidal flux of amplitude
    /// <paramref name="bAmp"/>: the linear material storing the same cycle-average energy,
    /// ν_eff = (8/(π·B̂²))·∫₀^{π/2} w(B̂·sin θ) dθ. Equal to ν where the curve is linear; used by
    /// the time-harmonic solve (fundamental only — harmonics of B are not resolved).</summary>
    public double EffectiveReluctivity(double bAmp)
    {
        bAmp = Math.Abs(bAmp);
        if (bAmp < 1e-9 * _b[^1]) return _slope[0];
        const int n = 64;
        double h = Math.PI / 2 / n, sum = 0;
        for (int i = 0; i <= n; i++)
        {
            double coeff = i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2;
            sum += coeff * EnergyDensity(bAmp * Math.Sin(i * h));
        }
        return 8 * (sum * h / 3) / (Math.PI * bAmp * bAmp);
    }

    /// <summary>Reluctivity ν = H/B and dν/d(B²) at <paramref name="b"/>; at B = 0, ν is the
    /// initial slope.</summary>
    public (double Nu, double DNuDB2) Reluctivity(double b)
    {
        b = Math.Abs(b);
        var (h, slope) = Evaluate(b);
        if (b < 1e-9 * _b[^1]) return (_slope[0], 0);
        double nu = h / b;
        return (nu, (slope * b - h) / (2 * b * b * b));
    }
}

/// <summary>Steinmetz core-loss law, P_v = k·f^α·B̂^β [W/m³] with f in Hz and B̂ the peak flux
/// density in T.</summary>
public sealed record SteinmetzCoefficients(double K, double Alpha, double Beta)
{
    public double LossDensity(double frequencyHz, double peakB) =>
        peakB <= 0 ? 0 : K * Math.Pow(frequencyHz, Alpha) * Math.Pow(peakB, Beta);

    /// <summary>k_i of the improved generalized Steinmetz equation (iGSE): the factor that makes
    /// the time-domain form k_i·|dB/dt|^α·(ΔB)^(β−α) reproduce <see cref="LossDensity"/> exactly
    /// for a sinusoid. Needs α ≥ 1.</summary>
    public double IgseCoefficient
    {
        get
        {
            if (!(Alpha >= 1)) throw new InvalidOperationException("iGSE needs α ≥ 1.");
            // ∫₀^{2π} |cos θ|^α dθ = 4·∫₀^{π/2} cos^α θ dθ, by Simpson's rule.
            const int n = 256;
            double h = Math.PI / 2 / n, sum = 0;
            for (int i = 0; i <= n; i++)
            {
                double coeff = i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2;
                sum += coeff * Math.Pow(Math.Cos(i * h), Alpha);
            }
            double integral = 4 * (sum * h / 3);
            return K / (Math.Pow(2 * Math.PI, Alpha - 1) * Math.Pow(2, Beta - Alpha) * integral);
        }
    }

    /// <summary>iGSE loss density [W/m³] for a non-sinusoidal periodic flux:
    /// P = (k_i/T)·(ΔB)^(β−α)·Σ|dB/dt|^α·Δt over one period. <paramref name="waveform"/> is one
    /// period of (phase, relative value) with phases strictly increasing in [0, 1), closed back to
    /// the first point, piecewise linear; it is scaled so its largest |value| is
    /// <paramref name="peakB"/>. Exactly <see cref="LossDensity"/> for a sinusoid; minor loops are
    /// not separated out, so a waveform with intermediate reversals is priced on its major loop's
    /// ΔB alone.</summary>
    public double IgseLossDensity(double frequencyHz, double peakB, IReadOnlyList<(double Phase, double Value)> waveform)
    {
        if (!(frequencyHz > 0)) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (peakB <= 0) return 0;
        if (waveform is null || waveform.Count < 2)
            throw new ArgumentException("A flux waveform needs at least two points over one period.", nameof(waveform));
        if (waveform[0].Phase < 0 || waveform[^1].Phase >= 1)
            throw new ArgumentException("Waveform phases lie in [0, 1) over one period.", nameof(waveform));
        for (int i = 1; i < waveform.Count; i++)
            if (!(waveform[i].Phase > waveform[i - 1].Phase))
                throw new ArgumentException("Waveform phases must be strictly increasing.", nameof(waveform));
        double maxAbs = waveform.Max(p => Math.Abs(p.Value));
        if (!(maxAbs > 0)) throw new ArgumentException("The flux waveform is zero.", nameof(waveform));
        double scale = peakB / maxAbs;
        double deltaB = (waveform.Max(p => p.Value) - waveform.Min(p => p.Value)) * scale;
        double t = 1 / frequencyHz;
        double sum = 0;
        for (int i = 0; i < waveform.Count; i++)
        {
            var p0 = waveform[i];
            var p1 = i + 1 < waveform.Count ? waveform[i + 1] : (waveform[0].Phase + 1, waveform[0].Value);
            double dt = (p1.Item1 - p0.Phase) * t;
            double slope = (p1.Item2 - p0.Value) * scale / dt;
            sum += Math.Pow(Math.Abs(slope), Alpha) * dt;
        }
        return IgseCoefficient * Math.Pow(deltaB, Beta - Alpha) * sum / t;
    }
}

/// <summary>A material for the 2D magnetic solve: relative permeability (or a B–H curve),
/// conductivity (eddy currents in a time-harmonic solve), and an optional core-loss law.</summary>
public sealed record MagneticMaterial(string Name, double RelativePermeability = 1, double Conductivity = 0)
{
    public BhCurve? Curve { get; init; }
    public SteinmetzCoefficients? Steinmetz { get; init; }

    public bool IsNonlinear => Curve is not null;

    public static MagneticMaterial Air { get; } = new("Air");

    /// <summary>From a library material: µr (1 when unset) and σ (0 when unset).</summary>
    public static MagneticMaterial FromMaterial(Material material) =>
        new(material.Name, material.RelativePermeability ?? 1, material.ElectricalConductivity ?? 0);
}

public static class MagneticConstants
{
    public const double Mu0 = 4e-7 * Math.PI;
}
