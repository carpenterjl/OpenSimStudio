using System.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// The two-pass mesh check: the same model solved on two mesh densities, and how far the
/// input impedance moved between them. A result that changes by more than a few percent
/// when the mesh is refined is a property of the mesh, not of the antenna.
/// </summary>
public static class MeshConvergence
{
    /// <summary>|ΔZ|/|Z| at or below this counts as converged.</summary>
    public const double ConvergedFraction = 0.05;

    /// <summary>|Z_fine − Z_coarse| / |Z_fine|.</summary>
    public static double RelativeChange(Complex coarse, Complex fine) =>
        fine.Magnitude > 0 ? (fine - coarse).Magnitude / fine.Magnitude : double.PositiveInfinity;

    public static bool IsConverged(Complex coarse, Complex fine) =>
        RelativeChange(coarse, fine) <= ConvergedFraction;

    /// <summary>Three passes on meshes refined by the same ratio each time: the order at which the
    /// result converges, read from the two changes, and Richardson's estimate of the value at an
    /// infinitely fine mesh. Null order when the changes do not shrink (no convergence to read).</summary>
    public sealed record ThreePass(Complex Extrapolated, double? Order, double FineError);

    /// <summary>
    /// With Z(h) = Z∞ + C·hᵖ and meshes h, h/r, h/r²: the changes Z₁ − Z₂ and Z₂ − Z₃ shrink by rᵖ,
    /// so p = ln(|Z₁ − Z₂|/|Z₂ − Z₃|)/ln r, and Z∞ = Z₃ + (Z₃ − Z₂)/(rᵖ − 1). <see cref="ThreePass.FineError"/>
    /// is |Z₃ − Z∞|/|Z∞|: how far the finest mesh still is from the limit.
    /// </summary>
    public static ThreePass Richardson(Complex coarse, Complex medium, Complex fine, double ratio)
    {
        if (!(ratio > 1)) throw new ArgumentOutOfRangeException(nameof(ratio), "Each mesh must be finer than the last.");
        double first = (coarse - medium).Magnitude, second = (medium - fine).Magnitude;
        if (!(second > 0) || !(first > second))
            return new ThreePass(fine, null, double.NaN);
        double order = Math.Log(first / second) / Math.Log(ratio);
        var limit = fine + (fine - medium) / (Math.Pow(ratio, order) - 1);
        return new ThreePass(limit, order, limit.Magnitude > 0 ? (fine - limit).Magnitude / limit.Magnitude : double.NaN);
    }

    /// <summary>The line shown to the user for a three-pass check.</summary>
    public static string Describe(Complex coarse, Complex medium, Complex fine, int fineUnknowns, double ratio)
    {
        var r = Richardson(coarse, medium, fine, ratio);
        string Z(Complex z) => $"{z.Real:g4} {(z.Imaginary >= 0 ? "+" : "−")} j{Math.Abs(z.Imaginary):g4} Ω";
        return r.Order is { } order
            ? $"Mesh check over three densities (finest {fineUnknowns} unknowns): Zin converges at order {order:g2} " +
              $"to an estimated {Z(r.Extrapolated)}; the finest mesh is {r.FineError:P1} from it."
            : $"Mesh check over three densities (finest {fineUnknowns} unknowns): Zin {Z(coarse)}, {Z(medium)}, {Z(fine)} " +
              "does not settle as the mesh is refined, so no limit can be estimated — raise the elements per wavelength.";
    }

    /// <summary>The line shown to the user.</summary>
    public static string Describe(Complex coarse, int coarseUnknowns, Complex fine, int fineUnknowns)
    {
        double change = RelativeChange(coarse, fine);
        string fineText = $"{fine.Real:g4} {(fine.Imaginary >= 0 ? "+" : "−")} j{Math.Abs(fine.Imaginary):g4} Ω";
        return $"Mesh check: on a finer mesh ({fineUnknowns} unknowns against {coarseUnknowns}) " +
               $"Zin = {fineText}, a change of {change:P1}" +
               (change <= ConvergedFraction
                   ? " — converged at this density."
                   : $" — NOT converged (more than {ConvergedFraction:P0}): raise the elements per " +
                     "wavelength; near a resonance a small frequency shift moves Zin a great deal.");
    }
}
