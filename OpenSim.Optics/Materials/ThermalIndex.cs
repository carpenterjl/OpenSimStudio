using System.Globalization;

namespace OpenSim.Optics.Materials;

/// <summary>
/// A glass's index at a temperature and pressure other than its catalog's (plan item OP-13),
/// after SCHOTT TIE-19, "Temperature Coefficient of the Refractive Index", Version August 2023:
/// <list type="number">
/// <item>The catalog index n_rel(λ, T0) is relative to air at the glass's reference temperature
/// T0 (the TD line's last number) and the catalog's reference pressure
/// (<see cref="CatalogReferencePressurePascals"/>), at a wavelength measured in that air
/// (<see cref="OpticalMaterial.RelativeIndex"/>).</item>
/// <item>Absolute at T0: n_abs(λ, T0) = n_rel(λ, T0) · n_air(λ, T0, P_ref) (equation (5)).</item>
/// <item>Shifted to the system temperature T by equations (3) and (4):
/// n_abs(λ, T) = n_abs(λ, T0) + Δn_abs(λ, T), with
/// Δn_abs = (n² − 1)/(2n) · (D0 ΔT + D1 ΔT² + D2 ΔT³ + (E0 ΔT + E1 ΔT²)/(λ² − λtk²)), ΔT = T − T0,
/// λ the vacuum wavelength in µm, and n = n_rel(λ, T0), which TIE-19 (page 2) says "can be used
/// for n(λ, T0) in equation (2) with sufficient accuracy".</item>
/// <item>Referred to the system's air: n_rel(λ, T, P) = n_abs(λ, T) / n_air(λ, T, P) (equation (5)).</item>
/// </list>
/// Both air indices come from the same <see cref="AirIndexModel"/>, so at the reference
/// conditions they are the same number and the chain returns the catalog index exactly; in a
/// vacuum (P = 0) the system air index is 1 and the result is the absolute index.
/// <para>
/// A glass without a TD line has no dn/dT model and no stated reference temperature: its index
/// is taken as relative to air at 20 °C, TIE-19's T0 (equation (2), "T0 Reference temperature
/// (20 °C)"), with Δn_abs = 0, so its absolute index does not change with temperature and its
/// relative index moves only with the air. <see cref="ThermalIndexResult.HasThermalData"/> says so.
/// </para>
/// <para>
/// Where the two open readers differ from TIE-19 (the sign of λtk, the wavelength in equation
/// (3), the reference pressure), this follows TIE-19; docs/Optics-Conventions.md §10 lists the
/// differences.
/// </para>
/// </summary>
public static class ThermalIndex
{
    /// <summary>
    /// The pressure catalog indices are relative to: P0 = 0.101325·10⁶ Pa (TIE-19 page 3). TIE-19
    /// page 2 states the catalog values "apply for an air pressure of 0.10133·10⁶ Pa", which is
    /// P0 rounded to five figures; P0 is taken, as AGFFileReader.jl does (1 atm,
    /// <c>src/constants.jl</c> <c>PRESSURE_REF = 1.0</c>). Read literally, 0.10133·10⁶ Pa would
    /// move a relative index by about 1.3·10⁻⁸ (see docs/Optics-Conventions.md §10).
    /// </summary>
    public const double CatalogReferencePressurePascals = AirIndex.StandardPressurePascals;

    /// <summary>TIE-19's T0, the reference temperature taken for a glass without a TD line (°C).</summary>
    public const double DefaultReferenceTemperatureCelsius = 20;

    /// <summary>The glass's reference temperature: its TD line's, or 20 °C without one.</summary>
    public static double ReferenceTemperature(OpticalMaterial material) =>
        material.Thermal?.ReferenceTemperatureCelsius ?? DefaultReferenceTemperatureCelsius;

    /// <summary>The air the glass's catalog index is relative to: the reference temperature and
    /// pressure, with the system air's CO2 and humidity (a catalog states no composition).</summary>
    public static AirConditions ReferenceAir(OpticalMaterial material, AirConditions system) =>
        system with { TemperatureCelsius = ReferenceTemperature(material), PressurePascals = CatalogReferencePressurePascals };

    /// <summary>
    /// The glass at <paramref name="airMicrometers"/>, a wavelength measured in the system's air
    /// (as OpticStudio's wavelengths are). The vacuum wavelength is found first
    /// (<see cref="AirIndex.VacuumFromAir"/>); the catalog formula is evaluated at the wavelength
    /// in the reference air, λ_air · n_air(system)/n_air(reference), so at the reference
    /// conditions it is evaluated at <paramref name="airMicrometers"/> itself.
    /// </summary>
    public static ThermalIndexResult AtAirWavelength(OpticalMaterial material, double airMicrometers,
        AirIndexModel model, AirConditions system)
    {
        double vacuum = AirIndex.VacuumFromAir(model, airMicrometers, system);
        var (nSystem, nReference) = AirPair(material, vacuum, model, system);
        return Evaluate(material, vacuum, airMicrometers * (nSystem / nReference), nSystem, nReference, system);
    }

    /// <summary>The glass at a vacuum wavelength (what a <see cref="Ray"/> carries), in µm.</summary>
    public static ThermalIndexResult AtVacuumWavelength(OpticalMaterial material, double vacuumMicrometers,
        AirIndexModel model, AirConditions system)
    {
        var (nSystem, nReference) = AirPair(material, vacuumMicrometers, model, system);
        return Evaluate(material, vacuumMicrometers, vacuumMicrometers / nReference, nSystem, nReference, system);
    }

    /// <summary>TIE-19 equation (3): Δn_abs(λ, T) for a glass whose index at T0 is
    /// <paramref name="indexAtReference"/>, at vacuum wavelength λ (µm) and ΔT = T − T0 (K).
    /// Throws when λ² = λtk², where the expression has its pole.</summary>
    public static double AbsoluteIndexChange(ThermalData td, double indexAtReference, double vacuumMicrometers, double deltaT)
    {
        double n = indexAtReference;
        double denominator = vacuumMicrometers * vacuumMicrometers - td.LambdaTk * td.LambdaTk;
        double change = (n * n - 1) / (2 * n) *
            (td.D0 * deltaT + td.D1 * deltaT * deltaT + td.D2 * deltaT * deltaT * deltaT
             + (td.E0 * deltaT + td.E1 * deltaT * deltaT) / denominator);
        if (!double.IsFinite(change))
            throw new ArgumentOutOfRangeException(nameof(vacuumMicrometers), vacuumMicrometers,
                $"TIE-19 equation (3) is not finite at {N(vacuumMicrometers)} µm: λtk = {N(td.LambdaTk)} µm puts its pole there.");
        return change;
    }

    /// <summary>TIE-19 equation (2): dn_abs/dT (per K) at vacuum wavelength λ (µm) and ΔT = T − T0.</summary>
    public static double AbsoluteIndexTemperatureCoefficient(ThermalData td, double indexAtReference, double vacuumMicrometers, double deltaT)
    {
        double n = indexAtReference;
        double denominator = vacuumMicrometers * vacuumMicrometers - td.LambdaTk * td.LambdaTk;
        return (n * n - 1) / (2 * n) *
            (td.D0 + 2 * td.D1 * deltaT + 3 * td.D2 * deltaT * deltaT + (td.E0 + 2 * td.E1 * deltaT) / denominator);
    }

    private static (double System, double Reference) AirPair(OpticalMaterial material, double vacuum, AirIndexModel model, AirConditions system)
    {
        double nSystem = AirIndex.Absolute(model, vacuum, system);
        var reference = ReferenceAir(material, system);
        // The same inputs give the same number, bit for bit: the reference conditions return the catalog index exactly.
        double nReference = reference == system ? nSystem : AirIndex.Absolute(model, vacuum, reference);
        return (nSystem, nReference);
    }

    private static ThermalIndexResult Evaluate(OpticalMaterial material, double vacuum, double formulaMicrometers,
        double nSystem, double nReference, AirConditions system)
    {
        double catalog = material.RelativeIndex(formulaMicrometers);
        double deltaT = system.TemperatureCelsius - ReferenceTemperature(material);
        double change = 0;
        if (material.Thermal is { } td && deltaT != 0)
        {
            try { change = AbsoluteIndexChange(td, catalog, vacuum, deltaT); }
            catch (ArgumentOutOfRangeException e) { throw new InvalidOperationException($"{material.Who}: {e.Message.Split(" (Parameter")[0]}", e); }
        }
        double absolute = catalog * nReference + change;
        // Written so that equal air indices give exactly the catalog index (ratio 1, change 0)
        // and a vacuum (nSystem = 1) gives exactly the absolute index.
        double relative = catalog * (nReference / nSystem) + change / nSystem;
        return new ThermalIndexResult(relative, absolute, catalog, formulaMicrometers, vacuum, nSystem, nReference,
            ReferenceTemperature(material), material.Thermal is not null);
    }

    private static string N(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}

/// <summary>A glass's index at a temperature and pressure, with the steps that gave it.</summary>
/// <param name="Relative">The index relative to the system's air: what refraction at an air
/// interface uses, and what OpticStudio reports.</param>
/// <param name="Absolute">The index relative to vacuum at the system temperature.</param>
/// <param name="Catalog">The catalog index at the reference conditions, n_rel(λ, T0).</param>
/// <param name="FormulaMicrometers">The wavelength the catalog formula was evaluated at: the
/// wavelength in the reference air, µm.</param>
/// <param name="VacuumMicrometers">The vacuum wavelength, µm.</param>
/// <param name="SystemAirIndex">The absolute index of the system's air.</param>
/// <param name="ReferenceAirIndex">The absolute index of the catalog's reference air.</param>
/// <param name="ReferenceTemperatureCelsius">T0: the TD line's, or 20 °C without one.</param>
/// <param name="HasThermalData">False when the glass has no TD line, so no dn/dT was applied.</param>
public readonly record struct ThermalIndexResult(double Relative, double Absolute, double Catalog, double FormulaMicrometers,
    double VacuumMicrometers, double SystemAirIndex, double ReferenceAirIndex, double ReferenceTemperatureCelsius, bool HasThermalData);
