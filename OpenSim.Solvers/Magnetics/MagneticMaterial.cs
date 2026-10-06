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
    private readonly double[] _b, _h, _slope;

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
