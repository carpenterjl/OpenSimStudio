using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Pdn;

/// <summary>
/// A place where current enters the plane pair: a via, or a patch of given outline. The
/// current is spread uniformly over the patch and the port voltage is the mean over it, so
/// a port's own (spreading) inductance depends on its size, as it does on the board.
/// </summary>
/// <param name="RadiusMeters">Radius of a round port (a via barrel). Ignored when
/// <see cref="Outline"/> is given.</param>
public sealed record PlanePort(string Name, Point2 Center, double RadiusMeters)
{
    /// <summary>The patch outline, when it is not a disc.</summary>
    public IReadOnlyList<Point2>? Outline { get; init; }

    /// <summary>A rectangular patch, the port of the analytic cavity model.</summary>
    public static PlanePort Rectangle(string name, Point2 center, double sizeX, double sizeY) =>
        new(name, center, 0.5 * Math.Sqrt(sizeX * sizeX + sizeY * sizeY))
        {
            Outline = new[]
            {
                new Point2(center.X - sizeX / 2, center.Y - sizeY / 2),
                new Point2(center.X + sizeX / 2, center.Y - sizeY / 2),
                new Point2(center.X + sizeX / 2, center.Y + sizeY / 2),
                new Point2(center.X - sizeX / 2, center.Y + sizeY / 2)
            }
        };
}

/// <summary>Two parallel copper planes and the dielectric between them.</summary>
public sealed record PlanePairSpec
{
    /// <summary>Where both planes have copper (the cavity), as polygons with holes.</summary>
    public required IReadOnlyList<Polygon2> Shape { get; init; }

    /// <summary>Dielectric thickness between the planes [m].</summary>
    public required double SeparationMeters { get; init; }

    public double RelativePermittivity { get; init; } = 4.4;
    public double LossTangent { get; init; } = 0.02;

    /// <summary>Djordjevic–Sarkar dielectric: εr and tan δ are the values at
    /// <see cref="DielectricReferenceHz"/> and ε′ rises toward low frequency as a flat loss
    /// tangent requires. Off: one complex ε at every frequency.</summary>
    public bool WidebandDielectric { get; init; } = true;

    public double DielectricReferenceHz { get; init; } = 1e9;

    public double UpperCopperThicknessMeters { get; init; } = 35e-6;
    public double LowerCopperThicknessMeters { get; init; } = 35e-6;
    public double ConductivitySiemensPerMeter { get; init; } = 5.8e7;

    public required IReadOnlyList<PlanePort> Ports { get; init; }

    /// <summary>Target edge length of the mesh away from the ports [m]; 0 chooses it from
    /// <see cref="MaxFrequencyHz"/> and the plane size.</summary>
    public double MeshEdgeMeters { get; init; }

    /// <summary>The highest frequency the mesh has to carry (used when
    /// <see cref="MeshEdgeMeters"/> is 0).</summary>
    public double MaxFrequencyHz { get; init; } = 1e9;

    /// <summary>The fringe of the open edges, the same all round (an <see cref="EdgeFringe"/>
    /// with one pair, layers 0 and 1); null leaves the edges bare.</summary>
    public EdgeFringe? Fringe { get; init; }

    /// <summary>The pair as a two-layer <see cref="PlaneStackSpec"/>: the upper plane is layer 0,
    /// the lower layer 1, and both have the shape of the cavity.</summary>
    public PlaneStackSpec ToStack()
    {
        var fringe = Fringe;
        return new PlaneStackSpec
        {
            Layers = new[]
            {
                new StackLayer
                {
                    Name = "upper plane", CopperThicknessMeters = UpperCopperThicknessMeters,
                    Plates = new[] { new StackPlate("upper plane", Shape) },
                    GapBelowMeters = SeparationMeters, GapPermittivity = RelativePermittivity, GapLossTangent = LossTangent
                },
                new StackLayer
                {
                    Name = "lower plane", CopperThicknessMeters = LowerCopperThicknessMeters,
                    Plates = new[] { new StackPlate("lower plane", Shape) }
                }
            },
            Ports = Ports.Select(p => new StackPort(p, 0, 1)).ToList(),
            WidebandDielectric = WidebandDielectric,
            DielectricReferenceHz = DielectricReferenceHz,
            ConductivitySiemensPerMeter = ConductivitySiemensPerMeter,
            MeshEdgeMeters = MeshEdgeMeters,
            MaxFrequencyHz = MaxFrequencyHz,
            Fringe = fringe is null ? null : _ => fringe
        };
    }
}

/// <summary>A resonance of the bare plane pair with open edges.</summary>
/// <param name="PortCoupling">For each port, the mode's voltage at the port relative to the
/// mode's largest: near 0 the port sits on a node of the mode and neither excites nor sees it.</param>
public sealed record PlaneMode(double FrequencyHz, IReadOnlyList<double> PortCoupling);

/// <summary>
/// The impedance of a plane pair between its ports, by finite elements on the plane's real
/// outline — the two-layer case of <see cref="PlaneStack"/>, which does the solving.
///
/// <para>Between two planes a distance d apart, with d far below a wavelength, the field is
/// a voltage v(x, y) across the gap and a sheet current in the planes:</para>
/// <code>
///   ∇v = −Z_sq·K            Z_sq = jωµ₀d + Z_s,upper + Z_s,lower      [Ω per square]
///   ∇·K = −Y·v + J          Y = jωε/d                                 [S per area]
/// </code>
/// <para>so ∇·(∇v/Z_sq) − Y·v = −J, with no current leaving through the open edge (∂v/∂n = 0)
/// unless <see cref="PlanePairSpec.Fringe"/> gives the edge its fringe. On quadratic triangles
/// this is (K/Z_sq + Y·M)·v = p·I with K the stiffness and M the mass matrix of the mesh, both
/// real and assembled once; a frequency only changes the two scalars. Z_s is the surface
/// impedance of a plane of finite thickness, (k/σ)·coth(k·t) with k = (1 + j)/δ, which is the
/// DC sheet resistance at low frequency and (1 + j)/(σδ) at high.</para>
///
/// <para>A port injects its current uniformly over its patch and reads the mean voltage over
/// it (the patch is meshed conformally, with rings of nodes grading out to the target edge
/// length), which makes Z reciprocal by construction and is the same port the analytic
/// cavity model uses.</para>
/// </summary>
public sealed class PlanePair
{
    private const double Mu0 = 4e-7 * Math.PI;
    private const double Epsilon0 = 8.8541878128e-12;

    private readonly PlaneStack _stack;

    public PlanePairSpec Spec { get; }
    public PlanarMesh Mesh => _stack.Mesh;
    public double AreaSquareMeters => _stack.AreaSquareMeters;
    public double MeshEdgeMeters => _stack.MeshEdgeMeters;

    /// <summary>Unknowns of the solve: the mesh's corners and one node per edge.</summary>
    public int NodeCount => _stack.UnknownCount;

    /// <summary>Half-bandwidth of the system in its solve order (the solve costs n·b²).</summary>
    public int HalfBandwidth => _stack.HalfBandwidth;

    /// <summary>The solver underneath.</summary>
    public PlaneStack Stack => _stack;

    /// <summary>ε₀εr·A/d at the reference frequency: what the pair is at low frequency
    /// (without the fringe).</summary>
    public double StaticCapacitanceFarads =>
        Epsilon0 * Spec.RelativePermittivity * AreaSquareMeters / Spec.SeparationMeters;

    public PlanePair(PlanePairSpec spec)
    {
        Spec = spec;
        if (!(spec.SeparationMeters > 0))
            throw new ArgumentException("The plane separation must be positive.");
        if (!(spec.RelativePermittivity >= 1))
            throw new ArgumentException("The relative permittivity must be at least 1.");
        if (spec.Shape.Count == 0)
            throw new ArgumentException("The plane pair has no area.");
        if (spec.Ports.Count == 0)
            throw new ArgumentException("The plane pair needs at least one port.");
        _stack = new PlaneStack(spec.ToStack());
    }

    /// <summary>Series impedance of the pair per square [Ω] and shunt admittance per area [S/m²].</summary>
    public (Complex SeriesPerSquare, Complex ShuntPerArea) Constants(double frequencyHz)
    {
        if (!(frequencyHz > 0))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz),
                "The plane-pair impedance is defined for frequencies above zero (at DC the pair is an open circuit).");
        double omega = 2 * Math.PI * frequencyHz;
        Complex series = new Complex(0, omega * Mu0 * Spec.SeparationMeters)
            + SurfaceImpedance(omega, Spec.UpperCopperThicknessMeters)
            + SurfaceImpedance(omega, Spec.LowerCopperThicknessMeters);
        Complex permittivity = Spec.WidebandDielectric && Spec.LossTangent > 0
            ? Spec.RelativePermittivity * (1 + Spec.LossTangent * new WidebandDebye(Spec.DielectricReferenceHz).Shape(frequencyHz))
            : Spec.RelativePermittivity * new Complex(1, -Spec.LossTangent);
        Complex shunt = new Complex(0, omega) * Epsilon0 * permittivity / Spec.SeparationMeters;
        return (series, shunt);
    }

    /// <summary>(k/σ)·coth(k·t): the impedance per square of a plane carrying its current on
    /// one face, with the field decaying through the thickness.</summary>
    private Complex SurfaceImpedance(double omega, double thickness)
    {
        double sigma = Spec.ConductivitySiemensPerMeter;
        if (!(sigma > 0) || double.IsInfinity(sigma)) return Complex.Zero;
        if (!(thickness > 0)) return Complex.Zero;
        Complex k = Complex.Sqrt(new Complex(0, omega * Mu0 * sigma));
        Complex kt = k * thickness;
        // coth(x) = 1/x + x/3 − … for small x; the direct form for the rest.
        Complex coth = kt.Magnitude < 1e-3 ? 1 / kt + kt / 3 : 1 / Complex.Tanh(kt);
        return k / sigma * coth;
    }

    /// <summary>The port impedance matrix [Ω] at one frequency.</summary>
    public Complex[,] Impedance(double frequencyHz)
    {
        Constants(frequencyHz);
        return _stack.Impedance(frequencyHz);
    }

    /// <summary>The port impedance matrix at each frequency, solved in parallel.</summary>
    public IReadOnlyList<Complex[,]> Sweep(IReadOnlyList<double> frequenciesHz,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        _stack.Sweep(frequenciesHz, progress, cancellationToken);

    /// <summary>
    /// The lowest resonances of the bare pair with no loss (open edges, or the fringe when it
    /// has one). The uniform mode (the pair as a capacitor) is left out.
    /// </summary>
    public IReadOnlyList<PlaneMode> Modes(int count, CancellationToken cancellationToken = default) =>
        _stack.Modes(count, cancellationToken);
}
