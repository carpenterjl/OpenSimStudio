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
