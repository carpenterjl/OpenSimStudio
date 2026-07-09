namespace OpenSim.Core.Model;

/// <summary>
/// An engineering material. Mechanical properties are required for the static solver;
/// thermal and electrical properties are carried for future solvers and may be null.
/// All values in SI units.
/// </summary>
public sealed record Material
{
    public required string Name { get; init; }

    /// <summary>Young's modulus E [Pa].</summary>
    public required double YoungsModulus { get; init; }

    /// <summary>Poisson's ratio ν [-].</summary>
    public required double PoissonRatio { get; init; }

    /// <summary>Density ρ [kg/m³].</summary>
    public required double Density { get; init; }

    /// <summary>Thermal conductivity k [W/(m·K)].</summary>
    public double? ThermalConductivity { get; init; }

    /// <summary>Specific heat capacity c_p [J/(kg·K)].</summary>
    public double? SpecificHeat { get; init; }

    /// <summary>Electrical conductivity σ [S/m].</summary>
    public double? ElectricalConductivity { get; init; }

    /// <summary>Relative permittivity ε_r [-].</summary>
    public double? RelativePermittivity { get; init; }

    /// <summary>Relative permeability μ_r [-].</summary>
    public double? RelativePermeability { get; init; }

    /// <summary>Display colour as #RRGGBB.</summary>
    public string Color { get; init; } = "#B0B0B0";

    /// <summary>
    /// True for materials shipped with the application. Built-ins cannot be deleted from
    /// the user library; a user material may shadow one by name. Defaults to false so
    /// old project files and user JSON deserialize as user-defined.
    /// </summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>Throws if the material cannot be used for DC electrical conduction.</summary>
    public void ValidateElectrical()
    {
        if (ElectricalConductivity is not > 0)
            throw new InvalidOperationException(
                $"Material '{Name}': electrical conductivity must be set and positive for an electrical solve. " +
                "Assign a conductive material (e.g. copper) or set ElectricalConductivity.");
    }

    /// <summary>Throws if the material cannot be used for steady-state heat conduction.</summary>
    public void ValidateThermal()
    {
        if (ThermalConductivity is not > 0)
            throw new InvalidOperationException(
                $"Material '{Name}': thermal conductivity must be set and positive for a thermal solve. " +
                "Set ThermalConductivity on the material.");
    }

    /// <summary>Throws if the material cannot be used for transient heat conduction
    /// (needs the volumetric heat capacity ρ·c_p on top of conductivity).</summary>
    public void ValidateThermalTransient()
    {
        ValidateThermal();
        if (SpecificHeat is not > 0)
            throw new InvalidOperationException(
                $"Material '{Name}': specific heat must be set and positive for a transient thermal solve. " +
                "Set SpecificHeat on the material.");
        if (Density <= 0)
            throw new InvalidOperationException(
                $"Material '{Name}': density must be positive for a transient thermal solve.");
    }

    /// <summary>Throws if the mechanical properties are physically invalid.</summary>
    public void ValidateMechanical()
    {
        if (YoungsModulus <= 0)
            throw new InvalidOperationException($"Material '{Name}': Young's modulus must be positive.");
        if (PoissonRatio <= -1 || PoissonRatio >= 0.5)
            throw new InvalidOperationException($"Material '{Name}': Poisson's ratio must lie in (-1, 0.5).");
        if (Density <= 0)
            throw new InvalidOperationException($"Material '{Name}': density must be positive.");
    }
}
