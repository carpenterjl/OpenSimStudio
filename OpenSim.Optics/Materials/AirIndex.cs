using System.Globalization;

namespace OpenSim.Optics.Materials;

/// <summary>
/// The absolute refractive index of air (relative to vacuum) by three formulas, and the
/// conversion between a wavelength in air and in vacuum (plan item OP-13). Every formula takes
/// the <b>vacuum</b> wavelength in micrometers, as both of its sources define it.
/// <para>
/// Sources, every constant copied from them:
/// <list type="bullet">
/// <item><b>Ciddor</b> and <b>modified Edlén</b>: J. A. Stone and J. H. Zimmerman, NIST
/// Engineering Metrology Toolbox, "Refractive Index of Air", Documentation, Appendix A
/// (https://emtoolbox.nist.gov/Wavelength/Documentation.asp): saturation vapour pressure
/// A1–A13 with Table A1 (K1…K10), humidity A15–A19, Ciddor A21–A41, Edlén A42–A49. NIST's
/// calculation differs from Ciddor's own paper only in the vapour-pressure formula and the
/// 1998 CODATA gas constant (A30), "a few parts in 10¹⁰" (Section III).</item>
/// <item><b>SCHOTT TIE-19</b>, "Temperature Coefficient of the Refractive Index", Version
/// August 2023, equations (8), (9) and (10), with P0 = 0.101325·10⁶ Pa (page 3).</item>
/// </list>
/// </para>
/// <para>
/// A vacuum (pressure exactly zero) has index 1 exactly in every model, whatever the wavelength
/// and temperature. Otherwise each model checks its inputs against the range its source states
/// and refuses, naming the model, the input and the range, rather than extrapolate
/// (<see cref="Check"/>).
/// </para>
/// </summary>
public static class AirIndex
{
    /// <summary>Standard atmospheric pressure, Pa: TIE-19's P0 = 0.101325·10⁶ Pa (page 3), and
    /// the NIST documentation's "conventionally taken to be 101.325 kPa" (Section IV,
    /// "Pressure"). Also the pressure one atmosphere of the environment editor means.</summary>
    public const double StandardPressurePascals = 101325;

    /// <summary>The index of air by <paramref name="model"/> at <paramref name="vacuumMicrometers"/>
    /// in <paramref name="air"/>. Throws, naming what is out of range, when an input lies outside
    /// the model's stated range (<see cref="Check"/>).</summary>
    public static double Absolute(AirIndexModel model, double vacuumMicrometers, AirConditions air)
    {
        if (Check(model, vacuumMicrometers, air) is { } problem)
            throw new ArgumentOutOfRangeException(nameof(air), problem);
        return Unchecked(model, vacuumMicrometers, air);
    }

    /// <summary>
    /// Null when the model may be evaluated at these inputs, else one actionable line saying what
    /// is out of range. A vacuum is always valid. The ranges are the sources':
    /// <list type="bullet">
    /// <item>Ciddor and modified Edlén: vacuum wavelength 300–1700 nm and temperature −40 to
    /// +100 °C (NIST documentation Section IV, "Vacuum Wavelength" and "Temperature": the
    /// ranges the NIST page accepts, Ciddor's proposed validity); pressure 10–140 kPa (Section
    /// IV, "Pressure": what the page accepts; it warns outside 60–120 kPa); relative humidity
    /// 0–100 %; CO2 0–2000 µmol/mol (Section IV, "CO2 Concentration": what the page accepts
    /// for Ciddor).</item>
    /// <item>TIE-19 states no range for air. Its formula has poles: equation (9) at λ² = 1/146
    /// and 1/41 µm², so λ must exceed 1/√41 µm (0.156 µm); equation (8) at
    /// 1 + 3.4785·10⁻³ (T − 15 °C) = 0, so T must exceed 15 − 1/3.4785·10⁻³ °C (−272.48 °C).</item>
    /// </list>
    /// </summary>
    public static string? Check(AirIndexModel model, double vacuumMicrometers, AirConditions air)
    {
        string name = AirIndexModels.Names.Label(model);
        if (!double.IsFinite(air.PressurePascals) || air.PressurePascals < 0)
            return $"{name}: the pressure must be zero (a vacuum) or positive (it is {N(air.PressurePascals)} Pa).";
        if (!double.IsFinite(air.TemperatureCelsius))
            return $"{name}: the temperature is not a number.";
        if (!double.IsFinite(vacuumMicrometers) || vacuumMicrometers <= 0)
            return $"{name}: the wavelength must be positive (it is {N(vacuumMicrometers)} µm).";
        if (air.IsVacuum) return null;
        switch (model)
        {
            case AirIndexModel.Schott:
                if (!(41 * vacuumMicrometers * vacuumMicrometers - 1 > 0))
                    return $"{name}: TIE-19 equation (9) has a pole at λ = 1/√41 µm (0.156 µm); {N(vacuumMicrometers)} µm (vacuum) is at or below it.";
                if (!(1 + 3.4785e-3 * (air.TemperatureCelsius - 15) > 0))
                    return $"{name}: TIE-19 equation (8) has a pole at {N(15 - 1 / 3.4785e-3)} °C; {N(air.TemperatureCelsius)} °C is at or below it.";
                return null;
            case AirIndexModel.Ciddor:
            case AirIndexModel.ModifiedEdlen:
                string choose = " Choose the SCHOTT TIE-19 (OpticStudio-compatible) air model, which states no range, to go outside it.";
                if (vacuumMicrometers < 0.3 || vacuumMicrometers > 1.7)
                    return $"{name}: {N(vacuumMicrometers)} µm (vacuum) is outside the equation's range, 0.3–1.7 µm (NIST EMToolbox documentation, Section IV).{choose}";
                if (air.TemperatureCelsius < -40 || air.TemperatureCelsius > 100)
                    return $"{name}: {N(air.TemperatureCelsius)} °C is outside the equation's range, −40 to +100 °C (NIST EMToolbox documentation, Section IV).{choose}";
                if (air.PressurePascals < 10e3 || air.PressurePascals > 140e3)
                    return $"{name}: {N(air.PressurePascals / 1000)} kPa is outside 10–140 kPa, the pressures NIST's calculator accepts (Section IV), and is not a vacuum.{choose}";
                if (!double.IsFinite(air.RelativeHumidityPercent) || air.RelativeHumidityPercent < 0 || air.RelativeHumidityPercent > 100)
                    return $"{name}: the relative humidity must be 0–100 % (it is {N(air.RelativeHumidityPercent)} %).";
                if (model == AirIndexModel.Ciddor &&
                    (!double.IsFinite(air.CarbonDioxideMicromolesPerMole) || air.CarbonDioxideMicromolesPerMole < 0 || air.CarbonDioxideMicromolesPerMole > 2000))
                    return $"{name}: the CO2 mole fraction must be 0–2000 µmol/mol, the range NIST's calculator accepts (it is {N(air.CarbonDioxideMicromolesPerMole)}).";
                return null;
            default:
                return $"Air index model {model} is not known.";
        }
    }

    /// <summary>The vacuum wavelength of light whose wavelength in <paramref name="air"/> is
    /// <paramref name="airMicrometers"/>: λvac = n(λvac)·λair (NIST documentation, equation (1)).
    /// The index depends on λvac itself, so the equation is solved by fixed-point iteration from
    /// λvac = λair; each step multiplies the error by about λ·dn/dλ (below 10⁻⁵ in the range of
    /// every model), so it settles in a few steps. Throws when an input is out of the model's
    /// range or the iteration has not settled in 50 steps.</summary>
    public static double VacuumFromAir(AirIndexModel model, double airMicrometers, AirConditions air)
    {
        if (air.IsVacuum) return airMicrometers;
        double vacuum = airMicrometers;
        for (int step = 0; step < 50; step++)
        {
            double next = airMicrometers * Absolute(model, vacuum, air);
            if (next == vacuum) return next;
            vacuum = next;
        }
        // Rounding can leave the fixed point alternating between two neighbouring doubles.
        double last = airMicrometers * Absolute(model, vacuum, air);
        if (Math.Abs(last - vacuum) <= 2 * (Math.BitIncrement(vacuum) - vacuum)) return last;
        throw new InvalidOperationException(
            $"{AirIndexModels.Names.Label(model)}: the vacuum wavelength of {N(airMicrometers)} µm in air did not settle in 50 steps (last {N(vacuum)}, {N(last)}).");
    }

    /// <summary>The wavelength in <paramref name="air"/> of light of vacuum wavelength
    /// <paramref name="vacuumMicrometers"/>: λair = λvac / n(λvac).</summary>
    public static double AirFromVacuum(AirIndexModel model, double vacuumMicrometers, AirConditions air) =>
        air.IsVacuum ? vacuumMicrometers : vacuumMicrometers / Absolute(model, vacuumMicrometers, air);

    private static double Unchecked(AirIndexModel model, double vacuumMicrometers, AirConditions air)
    {
        if (air.IsVacuum) return 1.0;
        return model switch
        {
            AirIndexModel.Ciddor => Ciddor(vacuumMicrometers, air.TemperatureCelsius, air.PressurePascals,
                air.CarbonDioxideMicromolesPerMole, air.RelativeHumidityPercent),
            AirIndexModel.Schott => Schott(vacuumMicrometers, air.TemperatureCelsius, air.PressurePascals),
            AirIndexModel.ModifiedEdlen => ModifiedEdlen(vacuumMicrometers, air.TemperatureCelsius, air.PressurePascals, air.RelativeHumidityPercent),
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Not an air index model.")
        };
    }

    // ---- SCHOTT TIE-19 (Version August 2023), equations (8)–(10) ----

    /// <summary>TIE-19 equations (8) and (9): the index of air at vacuum wavelength λ (µm),
    /// temperature T (°C) and pressure P (Pa), with P0 = 101325 Pa. No range check.</summary>
    public static double Schott(double vacuumMicrometers, double temperatureCelsius, double pressurePascals)
    {
        double l2 = vacuumMicrometers * vacuumMicrometers;
        // (9): n_air(λ, 15 °C, P0) − 1 = 10⁻⁸ (6432.8 + 2949810 λ²/(146 λ² − 1) + 25540 λ²/(41 λ² − 1))
        double refractivity15 = 1e-8 * (6432.8 + 2949810 * l2 / (146 * l2 - 1) + 25540 * l2 / (41 * l2 - 1));
        // (8): n_air(λ, T, P) = 1 + (n_air(λ, 15 °C, P0) − 1) / (1 + 3.4785·10⁻³ (T − 15)) · P/P0
        return 1 + refractivity15 / (1 + 3.4785e-3 * (temperatureCelsius - 15)) * (pressurePascals / StandardPressurePascals);
    }

    /// <summary>TIE-19 equation (10): dn_air/dT = −0.00367 (n_air − 1)/(1 + 0.00367 T), per °C.
    /// Its constants are not those of equation (8): this is the source's own expression, which
    /// agrees with the derivative of (8) to about 3·10⁻⁶ of its value (the tests measure it).</summary>
    public static double SchottTemperatureCoefficient(double vacuumMicrometers, double temperatureCelsius, double pressurePascals)
    {
        double n = Schott(vacuumMicrometers, temperatureCelsius, pressurePascals);
        return -0.00367 * (n - 1) / (1 + 0.00367 * temperatureCelsius);
    }

    // ---- NIST EMToolbox, Appendix A ----

    /// <summary>Table A1 (from Huang, reference 9 of the NIST documentation): K1…K10, stored from index 1.</summary>
    private static readonly double[] K =
    {
        double.NaN,
        1.16705214528E+03, -7.24213167032E+05, -1.70738469401E+01, 1.20208247025E+04, -3.23255503223E+06,
        1.49151086135E+01, -4.82326573616E+03, 4.05113405421E+05, -2.38555575678E-01, 6.50175348448E+02
    };

    /// <summary>
    /// Saturation vapour pressure, Pa, at <paramref name="celsius"/>: over water by A1–A7 (IAPWS,
    /// Table A1) at 0 °C and above, over ice by A8–A13 below 0 °C. NIST says to use A1–A7 "when
    /// t &gt; 0" and A8–A13 "for t &lt; 0" (A-II); at exactly 0 °C it says neither, and water is
    /// taken here.
    /// </summary>
    public static double SaturationVaporPressure(double celsius)
    {
        if (celsius >= 0)
        {
            double T = celsius + 273.15;                              // A1
            double omega = T + K[9] / (T - K[10]);                    // A2
            double a = omega * omega + K[1] * omega + K[2];           // A3
            double b = K[3] * omega * omega + K[4] * omega + K[5];    // A4
            double c = K[6] * omega * omega + K[7] * omega + K[8];    // A5
            double x = -b + Math.Sqrt(b * b - 4 * a * c);             // A6
            return 1e6 * Math.Pow(2 * c / x, 4);                      // A7
        }
        else
        {
            const double a1 = -13.928169, a2 = 34.7078238;           // A8, A9
            double T = celsius + 273.15;                              // A10
            double theta = T / 273.16;                                // A11
            double y = a1 * (1 - Math.Pow(theta, -1.5)) + a2 * (1 - Math.Pow(theta, -1.25)); // A12
            return 611.657 * Math.Exp(y);                             // A13
        }
    }

    /// <summary>The mole fraction of water vapour at <paramref name="relativeHumidityPercent"/>:
    /// A16–A17 (enhancement factor) and A19.</summary>
    public static double WaterVaporMoleFraction(double celsius, double pressurePascals, double relativeHumidityPercent)
    {
        if (relativeHumidityPercent == 0) return 0;
        const double alpha = 1.00062, beta = 3.14e-8, gamma = 5.60e-7;                       // A16
        double f = alpha + beta * pressurePascals + gamma * celsius * celsius;               // A17
        return relativeHumidityPercent / 100 * f * SaturationVaporPressure(celsius) / pressurePascals; // A19
    }

    /// <summary>The Ciddor equation, A21–A41: vacuum wavelength λ (µm), temperature t (°C),
    /// pressure p (Pa), CO2 x_CO2 (µmol/mol), relative humidity (%). No range check.</summary>
    public static double Ciddor(double vacuumMicrometers, double celsius, double pressurePascals,
        double carbonDioxideMicromolesPerMole, double relativeHumidityPercent)
    {
        if (pressurePascals == 0) return 1.0;
        double t = celsius, p = pressurePascals, xco2 = carbonDioxideMicromolesPerMole;
        double xv = WaterVaporMoleFraction(t, p, relativeHumidityPercent);
        const double w0 = 295.235, w1 = 2.6422, w2 = -0.03238, w3 = 0.004028;                 // A21
        const double k0 = 238.0185, k1 = 5792105, k2 = 57.362, k3 = 167917;                   // A22
        const double a0 = 1.58123e-6, a1 = -2.9331e-8, a2 = 1.1043e-10;                       // A23
        const double b0 = 5.707e-6, b1 = -2.051e-8;                                           // A24
        const double c0 = 1.9898e-4, c1 = -2.376e-6;                                          // A25
        const double d = 1.83e-11, e = -0.765e-8;                                             // A26
        const double pR1 = 101325, TR1 = 288.15;                                              // A27
        const double Za = 0.9995922115;                                                       // A28
        const double rhoVs = 0.00985938;                                                      // A29
        const double R = 8.314472, Mv = 0.018015;                                             // A30
        double S = 1 / (vacuumMicrometers * vacuumMicrometers);                               // A31
        double ras = 1e-8 * (k1 / (k0 - S) + k3 / (k2 - S));                                  // A32
        double rvs = 1.022e-8 * (w0 + w1 * S + w2 * S * S + w3 * S * S * S);                 // A33
        double Ma = 0.0289635 + 1.2011e-8 * (xco2 - 400);                                     // A34
        double raxs = ras * (1 + 5.34e-7 * (xco2 - 450));                                     // A35
        double T = t + 273.15;                                                                // A36
        double pT = p / T;
        double Zm = 1 - pT * (a0 + a1 * t + a2 * t * t + (b0 + b1 * t) * xv + (c0 + c1 * t) * xv * xv)
                      + pT * pT * (d + e * xv * xv);                                          // A37
        double rhoAxs = pR1 * Ma / (Za * R * TR1);                                            // A38
        double rhoV = xv * p * Mv / (Zm * R * T);                                             // A39
        double rhoA = (1 - xv) * p * Ma / (Zm * R * T);                                       // A40
        return 1 + rhoA / rhoAxs * raxs + rhoV / rhoVs * rvs;                                 // A41
    }

    /// <summary>The modified Edlén equation, A42–A49: vacuum wavelength λ (µm), temperature t
    /// (°C), pressure p (Pa), relative humidity (%), turned into the water vapour partial
    /// pressure by A15. No range check.</summary>
    public static double ModifiedEdlen(double vacuumMicrometers, double celsius, double pressurePascals, double relativeHumidityPercent)
    {
        if (pressurePascals == 0) return 1.0;
        double t = celsius, p = pressurePascals;
        double pv = relativeHumidityPercent == 0 ? 0 : relativeHumidityPercent / 100 * SaturationVaporPressure(t); // A15
        const double A = 8342.54, B = 2406147;                                               // A42
        const double C = 15998, D = 96095.43;                                                 // A43
        const double E = 0.601, F = 0.00972, G = 0.003661;                                    // A44
        double S = 1 / (vacuumMicrometers * vacuumMicrometers);                               // A45
        double ns = 1 + 1e-8 * (A + B / (130 - S) + C / (38.9 - S));                         // A46
        double X = (1 + 1e-8 * (E - F * t) * p) / (1 + G * t);                               // A47
        double ntp = 1 + p * (ns - 1) * X / D;                                                // A48
        return ntp - 1e-10 * (292.75 / (t + 273.15)) * (3.7345 - 0.0401 * S) * pv;            // A49
    }

    private static string N(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
