namespace OpenSim.Optics.Coatings;

/// <summary>
/// A coating's response to one polarization: intensity reflection, transmission and absorption,
/// and the phases (radians, in (−π, π]) of the reflected and transmitted amplitudes in
/// OpticStudio's phase convention (index n − ik, propagation factor e^(−2πiτ/λ); its help,
/// "Defining Coatings" and "Definition of Polarization Terms"). A thin-film amplitude computed in
/// this program's own convention (n + ik, as tmm) is reported with the opposite phase, −arg;
/// docs/Optics-Conventions.md section 12.
/// </summary>
public readonly record struct CoatingPolarizationResponse(double R, double T, double A, double ReflectionPhase, double TransmissionPhase);

/// <summary>
/// A coating's response at one wavelength and angle, s and p, and the quantities OpticStudio's
/// coating plots show, defined as its help "Definition of Polarization Terms" defines them:
/// diattenuation D = |T_s − T_p| / (T_s + T_p) (and the same with R for reflection), retardance
/// in transmission φ_p − φ_s and in reflection φ_p − φ_s + π. Retardances are wrapped to
/// (−π, π]; a diattenuation whose denominator is zero is zero.
/// </summary>
public readonly record struct CoatingResponse(CoatingPolarizationResponse S, CoatingPolarizationResponse P)
{
    public double AverageReflection => (S.R + P.R) / 2;
    public double AverageTransmission => (S.T + P.T) / 2;
    public double AverageAbsorption => (S.A + P.A) / 2;

    public double ReflectionDiattenuation => Diattenuation(S.R, P.R);
    public double TransmissionDiattenuation => Diattenuation(S.T, P.T);

    public double ReflectionRetardance => Wrap(P.ReflectionPhase - S.ReflectionPhase + Math.PI);
    public double TransmissionRetardance => Wrap(P.TransmissionPhase - S.TransmissionPhase);

    private static double Diattenuation(double s, double p) => s + p > 0 ? Math.Abs(s - p) / (s + p) : 0;

    /// <summary>An angle wrapped to (−π, π].</summary>
    public static double Wrap(double radians)
    {
        double w = Math.IEEERemainder(radians, 2 * Math.PI);
        return w <= -Math.PI ? w + 2 * Math.PI : w;
    }
}
