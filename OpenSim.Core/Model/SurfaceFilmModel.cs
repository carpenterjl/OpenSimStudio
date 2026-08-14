namespace OpenSim.Core.Model;

/// <summary>
/// How strongly every exterior triangle of a mesh exchanges heat with its surroundings,
/// as a surface film coefficient h [W/(m²·K)] driving toward one reference temperature.
/// <para>
/// This record is the seam between the two fidelity stages of the environment. Stage 1
/// fills it from convection correlations plus the factored radiation coefficient; a later
/// CFD stage fills the SAME record from the wall heat flux of a resolved flow field. The
/// thermal solvers only ever see this, so the plumbing — the Robin matrix terms, the
/// ambient load, the reported film-coefficient field — is written once and reused verbatim.
/// </para>
/// A coefficient of <see cref="double.NaN"/> means the triangle is NOT wetted by the
/// environment: either it lies on a face the user claimed with their own boundary
/// condition, or (in a later stage) it is not exposed to the fluid. NaN rather than zero
/// on purpose — zero is a legitimate physical value (a perfectly insulated wetted face),
/// and the two must not be confused when the film model is summarized or rendered.
/// </summary>
public sealed record SurfaceFilmModel
{
    /// <summary>Film coefficient per boundary triangle [W/(m²·K)], parallel to and the same
    /// length as <see cref="FeMesh.BoundaryTriangles"/>; NaN where not wetted.</summary>
    public required IReadOnlyList<double> TriangleFilmCoefficient { get; init; }

    /// <summary>The temperature the film drives toward [K] — the ambient in Stage 1.</summary>
    public required double ReferenceTemperature { get; init; }

    /// <summary>Where these coefficients came from, printed with the solve assumptions.</summary>
    public required string Origin { get; init; }

    /// <summary>Whether the environment exchanges heat through this boundary triangle.</summary>
    public bool IsWetted(int triangleIndex) =>
        !double.IsNaN(TriangleFilmCoefficient[triangleIndex]);

    /// <summary>How many boundary triangles the environment acts on.</summary>
    public int WettedCount => TriangleFilmCoefficient.Count(h => !double.IsNaN(h));

    /// <summary>The film-coefficient span over the wetted triangles [W/(m²·K)]; (0, 0)
    /// when nothing is wetted.</summary>
    public (double Min, double Max) CoefficientRange()
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double h in TriangleFilmCoefficient)
        {
            if (double.IsNaN(h)) continue;
            if (h < min) min = h;
            if (h > max) max = h;
        }
        return double.IsPositiveInfinity(min) ? (0, 0) : (min, max);
    }
}
