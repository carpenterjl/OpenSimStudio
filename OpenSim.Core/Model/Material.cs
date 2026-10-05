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

    /// <summary>Electrical conductivity σ [S/m], at
    /// <see cref="ResistivityReferenceTemperature"/> when a temperature coefficient is set.</summary>
    public double? ElectricalConductivity { get; init; }

    /// <summary>
    /// Temperature coefficient of resistivity α [1/K]: ρ(T) = ρ_ref·(1 + α·(T − T_ref)),
    /// the linear law that holds for metals around room temperature (copper: 0.00393 /K
    /// about 20 °C, so a 50 K rise is 20 % more resistance). Null means the conductivity
    /// is taken as constant — which is what every solve that does not ask for
    /// <see cref="ElectricalConductivityAt"/> assumes anyway.
    /// </summary>
    public double? ResistivityTemperatureCoefficient { get; init; }

    /// <summary>The temperature [K] at which <see cref="ElectricalConductivity"/> holds.</summary>
    public double ResistivityReferenceTemperature { get; init; } = 293.15;

    /// <summary>
    /// σ at a temperature [K], by the linear resistivity law; the constant
    /// <see cref="ElectricalConductivity"/> when no coefficient is set. The resistivity
    /// factor is floored at 1 % so a negative coefficient extrapolated far outside its
    /// range cannot produce a negative or infinite conductivity.
    /// </summary>
    public double ElectricalConductivityAt(double kelvin)
    {
        double reference = ElectricalConductivity
            ?? throw new InvalidOperationException($"Material '{Name}' has no electrical conductivity.");
        if (ResistivityTemperatureCoefficient is not { } alpha) return reference;
        return reference / Math.Max(0.01, 1 + alpha * (kelvin - ResistivityReferenceTemperature));
    }

    /// <summary>Relative permittivity ε_r [-].</summary>
    public double? RelativePermittivity { get; init; }

    /// <summary>Relative permeability μ_r [-].</summary>
    public double? RelativePermeability { get; init; }

    /// <summary>
    /// Dielectric strength [V/m]: the field at which the material breaks down. Null means
    /// "not characterised" — an electrostatic solve then reports the field but no stress
    /// ratio, rather than comparing against a number nobody entered. Published values are
    /// for a thin test specimen and fall with thickness, temperature and age, so a library
    /// value is a typical figure, not a specification.
    /// </summary>
    public double? DielectricStrength { get; init; }

    /// <summary>
    /// Total hemispherical emissivity ε [-], in [0, 1]. Null means "unknown", which is a
    /// hard failure for a radiating solve rather than a default: ε spans 0.03 (polished
    /// aluminium) to 0.95 (paint) on the SAME metal depending only on its finish, so a
    /// silent 0.9 would be a fabricated 30× on the dominant heat path in a vacuum.
    /// </summary>
    public double? Emissivity { get; init; }

    /// <summary>
    /// Yield strength [Pa] — the onset of permanent deformation, and the denominator of a
    /// safety factor. Null means "not characterised", which is honest for a brittle material
    /// that has no yield point at all (alumina, glass, silicon) as well as for one whose
    /// value was simply never entered; a safety factor is then not offered rather than
    /// invented. Values are strongly temper- and process-dependent, so a library number is
    /// a typical value, not a specification.
    /// </summary>
    public double? YieldStrength { get; init; }

    /// <summary>
    /// Ultimate tensile strength [Pa] — the stress at fracture. Null means the same as for
    /// <see cref="YieldStrength"/>. This is the threshold a "stress beyond UTS" view paints
    /// against, which is why it is a material property rather than a display setting.
    /// </summary>
    public double? UltimateTensileStrength { get; init; }

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

    /// <summary>Throws if the material cannot radiate to the surroundings (needs a known
    /// emissivity). Only called when the environment has radiation switched on.</summary>
    public void ValidateRadiative()
    {
        if (Emissivity is null)
            throw new InvalidOperationException(
                $"Material '{Name}': emissivity is not set, and radiation is switched on for this " +
                "environment. Set the surface emissivity (0.03 for polished metal, 0.2–0.4 for " +
                "machined metal, 0.85–0.95 for paint, plastic or anodizing), or switch radiation off.");
        if (Emissivity is < 0 or > 1)
            throw new InvalidOperationException(
                $"Material '{Name}': emissivity {Emissivity} must lie in [0, 1].");
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
        if (YieldStrength is <= 0)
            throw new InvalidOperationException($"Material '{Name}': yield strength must be positive.");
        if (UltimateTensileStrength is <= 0)
            throw new InvalidOperationException(
                $"Material '{Name}': ultimate tensile strength must be positive.");
        // Ordering is physics, not preference: a material cannot fracture before it yields.
        if (YieldStrength is { } yield && UltimateTensileStrength is { } ultimate && yield > ultimate)
            throw new InvalidOperationException(
                $"Material '{Name}': yield strength {yield:g4} Pa exceeds the ultimate tensile " +
                $"strength {ultimate:g4} Pa.");
    }
}
