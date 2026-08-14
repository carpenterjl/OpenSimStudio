namespace OpenSim.Core.Model;

/// <summary>
/// The built-in fluids an environment can be filled with.
/// <para>
/// The tables are the standard reference property tables for dry air at 1 atm and for
/// saturated liquid water (Incropera &amp; DeWitt, <i>Fundamentals of Heat and Mass
/// Transfer</i>, Tables A.4 and A.6). They are accurate to about a percent — well inside
/// the ±10–20% band the convection correlations themselves carry, so the fluid data is
/// never the limiting error in a Stage 1 environment solve.
/// </para>
/// Anything else is entered as a CUSTOM constant-property fluid: shipping a named "oil"
/// or "coolant" whose numbers came from nowhere in particular would put a plausible
/// fiction where the user expects data.
/// </summary>
public static class FluidLibrary
{
    private const string AirSource = "Dry air at 1 atm — Incropera, Table A.4";
    private const string WaterSource = "Saturated liquid water — Incropera, Table A.6";

    /// <summary>Dry air at 1 atm, 250–600 K. β is not tabulated: air is an ideal gas, so
    /// β = 1/T exactly.</summary>
    public static FluidProperties Air { get; } = new()
    {
        Name = "Air",
        Source = AirSource,
        Temperatures = new[] { 250.0, 300.0, 350.0, 400.0, 450.0, 500.0, 550.0, 600.0 },
        Density = new[] { 1.3947, 1.1614, 0.9950, 0.8711, 0.7740, 0.6964, 0.6329, 0.5804 },
        DynamicViscosity = new[]
        {
            159.6e-7, 184.6e-7, 208.2e-7, 230.1e-7, 250.7e-7, 270.1e-7, 288.4e-7, 305.8e-7
        },
        ThermalConductivity = new[]
        {
            22.3e-3, 26.3e-3, 30.0e-3, 33.8e-3, 37.3e-3, 40.7e-3, 43.9e-3, 46.9e-3
        },
        SpecificHeat = new[] { 1006.0, 1007.0, 1009.0, 1014.0, 1021.0, 1030.0, 1040.0, 1051.0 }
        // ThermalExpansion left null ⇒ ideal gas.
    };

    /// <summary>Saturated liquid water, 275–370 K. β IS tabulated here and is negative
    /// below ~277 K — water's density maximum is real physics, and a 1/T stand-in would
    /// get the buoyancy direction wrong there.</summary>
    public static FluidProperties Water { get; } = new()
    {
        Name = "Water",
        Source = WaterSource,
        Temperatures = new[] { 275.0, 280.0, 290.0, 300.0, 310.0, 320.0, 330.0, 340.0, 350.0, 360.0, 370.0 },
        Density = new[]
        {
            999.9, 1000.0, 998.8, 996.5, 993.0, 989.1, 984.3, 979.0, 973.7, 967.1, 960.6
        },
        DynamicViscosity = new[]
        {
            1652e-6, 1422e-6, 1080e-6, 855e-6, 695e-6, 577e-6, 489e-6, 420e-6, 365e-6, 324e-6, 289e-6
        },
        ThermalConductivity = new[]
        {
            0.574, 0.582, 0.598, 0.613, 0.628, 0.640, 0.650, 0.660, 0.668, 0.674, 0.679
        },
        SpecificHeat = new[]
        {
            4211.0, 4198.0, 4184.0, 4179.0, 4178.0, 4180.0, 4184.0, 4188.0, 4195.0, 4203.0, 4214.0
        },
        ThermalExpansion = new[]
        {
            -32.74e-6, 46.04e-6, 174.0e-6, 276.1e-6, 361.9e-6, 436.7e-6,
            504.0e-6, 566.0e-6, 624.2e-6, 697.9e-6, 728.7e-6
        }
    };

    /// <summary>The named fluids, in menu order.</summary>
    public static IReadOnlyList<FluidProperties> All { get; } = new[] { Air, Water };

    /// <summary>Looks a built-in fluid up by name; null when the name is a user's custom
    /// fluid (which is carried by value on the environment settings, not by name).</summary>
    public static FluidProperties? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
