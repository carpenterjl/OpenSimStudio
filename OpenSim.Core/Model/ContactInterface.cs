namespace OpenSim.Core.Model;

/// <summary>
/// One quadrature point of a thermal contact interface, written as the temperature JUMP it
/// measures: <c>ΔT = Σ Sᵢ·T(Nodeᵢ)</c>. Node0 is the source node (S0 = +1) and Node1..3 are
/// the nodes of the opposing surface triangle it projects onto, carrying the negated
/// barycentric weights, so ΔT is exactly "my temperature minus the temperature of the point
/// I touch". <see cref="Weight"/> is the surface measure this point represents [m²].
///
/// The contact stiffness of one point is <c>h_c·Weight·s·sᵀ</c> with s the coefficient
/// vector — a positive multiple of a rank-1 Gram matrix, hence positive semi-definite, so
/// the assembled system stays SPD for the shared conjugate-gradient solver no matter how
/// badly the two surface meshes match (the same argument that picks all-positive quadrature
/// rules for the consistent mass matrices).
/// </summary>
public readonly record struct ContactStamp(
    int Node0, int Node1, int Node2, int Node3,
    double S0, double S1, double S2, double S3,
    double Weight);

/// <summary>
/// A detected thermal contact between two bodies of an assembly: the quadrature stamps that
/// couple them and the interfacial conductance h_c [W/(m²·K)] applied across the gap
/// (q = h_c·ΔT). Non-conformal touching bodies exchange NO heat without this — the merged
/// mesh shares no nodes across bodies by design.
///
/// The conductance lives on the interface, not in the detection settings, so re-solving with
/// a different joint quality never re-runs detection.
/// </summary>
public sealed record ContactInterface
{
    /// <summary>Index of the lower-numbered body (= its region id in the merged mesh).</summary>
    public required int BodyA { get; init; }

    /// <summary>Index of the higher-numbered body.</summary>
    public required int BodyB { get; init; }

    /// <summary>Jump stamps, in detection order (ascending source triangle, then vertex).</summary>
    public required IReadOnlyList<ContactStamp> Stamps { get; init; }

    /// <summary>Interfacial conductance h_c [W/(m²·K)]; must be positive.</summary>
    public required double Conductance { get; init; }

    /// <summary>
    /// The area actually coupled [m²] — the honestly measured Σ of the stamp weights, which
    /// is the mean of the two sides' paired areas. It is NOT assumed equal to either face's
    /// area: quadrature points that find no partner are dropped and counted.
    /// </summary>
    public required double CoupledArea { get; init; }

    /// <summary>Paired area seen from body A's surface [m²].</summary>
    public required double AreaA { get; init; }

    /// <summary>Paired area seen from body B's surface [m²].</summary>
    public required double AreaB { get; init; }

    /// <summary>
    /// Quadrature points on partially-contacting triangles that found no opposing surface.
    /// A large count against a small <see cref="CoupledArea"/> means the two surfaces only
    /// graze each other — worth reporting, never worth silently ignoring.
    /// </summary>
    public int UnpairedPoints { get; init; }

    /// <summary>Throws when the interface cannot be assembled against a mesh of this size.</summary>
    public void Validate(int nodeCount)
    {
        if (Conductance <= 0)
            throw new InvalidOperationException(
                $"Thermal contact between bodies {BodyA} and {BodyB}: the contact conductance " +
                $"must be positive (got {Conductance:g4} W/(m²·K)).");
        if (CoupledArea <= 0)
            throw new InvalidOperationException(
                $"Thermal contact between bodies {BodyA} and {BodyB} has no coupled area.");
        foreach (var s in Stamps)
        {
            if (s.Weight <= 0)
                throw new InvalidOperationException(
                    $"Thermal contact between bodies {BodyA} and {BodyB} carries a non-positive " +
                    "quadrature weight; the coupling would not be positive semi-definite.");
            if (OutOfRange(s.Node0) || OutOfRange(s.Node1) || OutOfRange(s.Node2) || OutOfRange(s.Node3))
                throw new InvalidOperationException(
                    $"Thermal contact between bodies {BodyA} and {BodyB} references a node outside " +
                    $"the mesh ({nodeCount} nodes) — it was detected on a different mesh.");
        }

        bool OutOfRange(int node) => node < 0 || node >= nodeCount;
    }

    /// <summary>This interface with a different conductance — the "re-solve with a better
    /// joint" path, which must not re-run detection.</summary>
    public ContactInterface WithConductance(double conductance) =>
        this with { Conductance = conductance };
}
