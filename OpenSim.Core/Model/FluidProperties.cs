namespace OpenSim.Core.Model;

/// <summary>
/// A fluid's transport properties evaluated at one temperature, plus the dimensionless
/// groups every convection correlation is written in.
/// </summary>
/// <param name="Density">ρ [kg/m³].</param>
/// <param name="DynamicViscosity">µ [Pa·s].</param>
/// <param name="ThermalConductivity">k [W/(m·K)].</param>
/// <param name="SpecificHeat">c_p [J/(kg·K)].</param>
/// <param name="ThermalExpansion">β [1/K] — the buoyancy coefficient of natural convection.</param>
public readonly record struct FluidState(double Density, double DynamicViscosity,
    double ThermalConductivity, double SpecificHeat, double ThermalExpansion)
{
    /// <summary>ν = µ/ρ [m²/s].</summary>
    public double KinematicViscosity => DynamicViscosity / Density;

    /// <summary>α = k/(ρ·c_p) [m²/s].</summary>
    public double ThermalDiffusivity => ThermalConductivity / (Density * SpecificHeat);

    /// <summary>Pr = µ·c_p/k, dimensionless.</summary>
    public double Prandtl => DynamicViscosity * SpecificHeat / ThermalConductivity;
}

/// <summary>
/// A fluid as a temperature-tabulated property set.
/// <para>
/// Properties are evaluated at the FILM temperature (½(T_surface + T_ambient)), which is
/// how the convection correlations were correlated in the first place: air's viscosity
/// and conductivity move ~25% over a 100 K excess, so evaluating at ambient instead of
/// the film would misstate h by several percent — larger than the discretization error
/// the rest of the pipeline works to keep down.
/// </para>
/// Values between table rows interpolate linearly; outside the table they CLAMP to the
/// end row (extrapolating a fitted property table is worse than saying "outside the
/// table" — <see cref="Covers"/> lets the caller log exactly that).
/// </summary>
public sealed record FluidProperties
{
    /// <summary>Display name, e.g. "Air".</summary>
    public required string Name { get; init; }

    /// <summary>Table temperatures [K], strictly ascending. A single entry means the
    /// fluid is modelled with constant properties.</summary>
    public required double[] Temperatures { get; init; }

    /// <summary>ρ [kg/m³] per table temperature.</summary>
    public required double[] Density { get; init; }

    /// <summary>µ [Pa·s] per table temperature.</summary>
    public required double[] DynamicViscosity { get; init; }

    /// <summary>k [W/(m·K)] per table temperature.</summary>
    public required double[] ThermalConductivity { get; init; }

    /// <summary>c_p [J/(kg·K)] per table temperature.</summary>
    public required double[] SpecificHeat { get; init; }

    /// <summary>β [1/K] per table temperature. Null means IDEAL GAS: β = 1/T exactly,
    /// which is what air is — tabulating it would only add rounding to a closed form.</summary>
    public double[]? ThermalExpansion { get; init; }

    /// <summary>Where the numbers come from, shown with the solve assumptions.</summary>
    public string? Source { get; init; }

    /// <summary>The temperature span the table actually covers [K].</summary>
    public (double Min, double Max) TemperatureRange =>
        (Temperatures[0], Temperatures[^1]);

    /// <summary>True when <paramref name="temperature"/> lies inside the table; the solver
    /// logs one warning per solve when it does not, rather than silently using clamped
    /// end-row properties.</summary>
    public bool Covers(double temperature) =>
        temperature >= Temperatures[0] && temperature <= Temperatures[^1];

    /// <summary>Properties at <paramref name="temperature"/> [K], linearly interpolated
    /// and clamped to the table ends.</summary>
    public FluidState AtTemperature(double temperature)
    {
        Validate();
        int n = Temperatures.Length;
        double beta;
        if (n == 1)
        {
            beta = ThermalExpansion is null ? 1.0 / Math.Max(temperature, 1e-6) : ThermalExpansion[0];
            return new FluidState(Density[0], DynamicViscosity[0],
                ThermalConductivity[0], SpecificHeat[0], beta);
        }

        int i = 0;
        while (i < n - 2 && temperature > Temperatures[i + 1]) i++;
        double t0 = Temperatures[i], t1 = Temperatures[i + 1];
        double f = Math.Clamp((temperature - t0) / (t1 - t0), 0, 1);
        beta = ThermalExpansion is null
            ? 1.0 / Math.Max(temperature, 1e-6)                      // ideal gas, exactly
            : Lerp(ThermalExpansion, i, f);
        return new FluidState(
            Lerp(Density, i, f), Lerp(DynamicViscosity, i, f),
            Lerp(ThermalConductivity, i, f), Lerp(SpecificHeat, i, f), beta);

        static double Lerp(double[] v, int i, double f) => v[i] + (v[i + 1] - v[i]) * f;
    }

    /// <summary>A fluid modelled with constant properties — the honest option for a
    /// working fluid whose table the user does not have.</summary>
    public static FluidProperties Constant(string name, double density, double dynamicViscosity,
        double thermalConductivity, double specificHeat, double thermalExpansion,
        double referenceTemperature = 293.15, string? source = null) =>
        new()
        {
            Name = name,
            Temperatures = new[] { referenceTemperature },
            Density = new[] { density },
            DynamicViscosity = new[] { dynamicViscosity },
            ThermalConductivity = new[] { thermalConductivity },
            SpecificHeat = new[] { specificHeat },
            ThermalExpansion = new[] { thermalExpansion },
            Source = source ?? "User-entered constant properties"
        };

    private void Validate()
    {
        int n = Temperatures.Length;
        if (n == 0)
            throw new InvalidOperationException($"Fluid '{Name}' has an empty property table.");
        if (Density.Length != n || DynamicViscosity.Length != n
            || ThermalConductivity.Length != n || SpecificHeat.Length != n
            || (ThermalExpansion is not null && ThermalExpansion.Length != n))
            throw new InvalidOperationException(
                $"Fluid '{Name}' property columns must all have {n} entries, one per table temperature.");
        for (int i = 1; i < n; i++)
            if (Temperatures[i] <= Temperatures[i - 1])
                throw new InvalidOperationException(
                    $"Fluid '{Name}' table temperatures must ascend strictly ({Temperatures[i - 1]} → {Temperatures[i]}).");
    }
}
