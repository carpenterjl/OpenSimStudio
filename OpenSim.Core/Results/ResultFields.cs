using OpenSim.Core.Numerics;

namespace OpenSim.Core.Results;

/// <summary>Where a field's values live.</summary>
public enum FieldLocation
{
    Node,
    Element
}

/// <summary>
/// A named result field produced by a solver and consumed by visualization.
/// Values are addressed by node or element index depending on <see cref="Location"/>.
/// </summary>
public interface IResultField
{
    string Name { get; }

    /// <summary>Physical unit for display, e.g. "m", "Pa", "K".</summary>
    string Unit { get; }

    FieldLocation Location { get; }

    /// <summary>Number of value slots (node count or element count).</summary>
    int Count { get; }

    /// <summary>The scalar used for color mapping (the value itself, or a magnitude/invariant).</summary>
    double GetScalar(int index);
}

/// <summary>A scalar field, one value per node.</summary>
public sealed class NodalScalarField : IResultField
{
    private readonly double[] _values;

    public NodalScalarField(string name, string unit, double[] values)
    {
        Name = name;
        Unit = unit;
        _values = values;
    }

    public string Name { get; }
    public string Unit { get; }
    public FieldLocation Location => FieldLocation.Node;
    public int Count => _values.Length;
    public double GetScalar(int index) => _values[index];
    public IReadOnlyList<double> Values => _values;
}

/// <summary>A vector field, one 3-vector per node; the mapped scalar is the magnitude.</summary>
public sealed class NodalVectorField : IResultField
{
    private readonly Vector3D[] _values;

    public NodalVectorField(string name, string unit, Vector3D[] values)
    {
        Name = name;
        Unit = unit;
        _values = values;
    }

    public string Name { get; }
    public string Unit { get; }
    public FieldLocation Location => FieldLocation.Node;
    public int Count => _values.Length;
    public double GetScalar(int index) => _values[index].Length;
    public Vector3D GetVector(int index) => _values[index];
    public IReadOnlyList<Vector3D> Values => _values;
}

/// <summary>A scalar field, one value per element (e.g. power density).</summary>
public sealed class ElementScalarField : IResultField
{
    private readonly double[] _values;

    public ElementScalarField(string name, string unit, double[] values)
    {
        Name = name;
        Unit = unit;
        _values = values;
    }

    public string Name { get; }
    public string Unit { get; }
    public FieldLocation Location => FieldLocation.Element;
    public int Count => _values.Length;
    public double GetScalar(int index) => _values[index];
    public IReadOnlyList<double> Values => _values;
}

/// <summary>
/// A symmetric second-order tensor (stress/strain) in Voigt-style component storage.
/// </summary>
public readonly record struct SymmetricTensor(double XX, double YY, double ZZ, double XY, double YZ, double ZX)
{
    /// <summary>Von Mises equivalent (for stress tensors) [same unit as components].</summary>
    public double VonMises()
    {
        double dXY = XX - YY, dYZ = YY - ZZ, dZX = ZZ - XX;
        return Math.Sqrt(0.5 * (dXY * dXY + dYZ * dYZ + dZX * dZX)
                         + 3.0 * (XY * XY + YZ * YZ + ZX * ZX));
    }

    /// <summary>Sum of the diagonal — the first invariant.</summary>
    public double Trace() => XX + YY + ZZ;

    /// <summary>
    /// Equivalent (von Mises) elastic strain for a STRAIN tensor:
    /// ε_eq = 1/(1+ν)·√(½[(ε₁−ε₂)² + (ε₂−ε₃)² + (ε₃−ε₁)²]).
    /// <para>
    /// It is <see cref="VonMises"/>/(1+ν) EXACTLY, and only because this type stores TENSOR
    /// shear components (ε_xy = γ_xy/2, as the assemblers emit — see
    /// <c>Tet4Assembler.ElementStrain</c>, which carries the explicit ½). With engineering
    /// shear the same expression would be silently wrong by a factor on the shear terms.
    /// </para>
    /// <para>
    /// For isotropic linear elasticity this makes E·ε_eq ≡ σ_vm identically, since the
    /// deviatoric strain is s/2G and G = E/(2(1+ν)). That identity is the sharpest available
    /// check on both quantities, and it is gated.
    /// </para>
    /// </summary>
    public double EquivalentStrain(double poissonRatio)
    {
        if (poissonRatio <= -1)
            throw new ArgumentOutOfRangeException(nameof(poissonRatio), poissonRatio,
                "Poisson ratio must exceed -1; 1 + v is the equivalent-strain denominator.");
        return VonMises() / (1 + poissonRatio);
    }

    /// <summary>
    /// Principal values, DESCENDING (σ₁ ≥ σ₂ ≥ σ₃ — the engineering convention, opposite to
    /// the ascending order the eigensolver returns). Computed with the shared symmetric
    /// Jacobi solver rather than a closed form: the closed form loses precision badly when
    /// two principals are nearly equal, which is exactly the state a uniaxial or hydrostatic
    /// element is in.
    /// </summary>
    public (double S1, double S2, double S3) Principals()
    {
        var matrix = new[]
        {
            new[] { XX, XY, ZX },
            new[] { XY, YY, YZ },
            new[] { ZX, YZ, ZZ }
        };
        var (values, _) = Numerics.JacobiEigenSolver.Solve(matrix);
        return (values[2], values[1], values[0]);
    }

    /// <summary>Maximum shear stress, (σ₁ − σ₃)/2 — the Tresca half-difference.</summary>
    public double MaxShear()
    {
        var (s1, _, s3) = Principals();
        return 0.5 * (s1 - s3);
    }

    /// <summary>One named component or invariant, for a per-component result view.</summary>
    public double Component(TensorComponent component) => component switch
    {
        TensorComponent.XX => XX,
        TensorComponent.YY => YY,
        TensorComponent.ZZ => ZZ,
        TensorComponent.XY => XY,
        TensorComponent.YZ => YZ,
        TensorComponent.ZX => ZX,
        TensorComponent.VonMises => VonMises(),
        TensorComponent.MaxPrincipal => Principals().S1,
        TensorComponent.MidPrincipal => Principals().S2,
        TensorComponent.MinPrincipal => Principals().S3,
        TensorComponent.MaxShear => MaxShear(),
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unknown tensor component.")
    };

    public static SymmetricTensor operator +(SymmetricTensor a, SymmetricTensor b) =>
        new(a.XX + b.XX, a.YY + b.YY, a.ZZ + b.ZZ, a.XY + b.XY, a.YZ + b.YZ, a.ZX + b.ZX);

    public static SymmetricTensor operator *(SymmetricTensor a, double s) =>
        new(a.XX * s, a.YY * s, a.ZZ * s, a.XY * s, a.YZ * s, a.ZX * s);
}

/// <summary>
/// A component or invariant of a symmetric tensor field. Shear components are TENSOR
/// components (σ_xy, ε_xy = γ_xy/2), matching the storage in <see cref="SymmetricTensor"/>.
/// </summary>
public enum TensorComponent
{
    VonMises,
    XX, YY, ZZ, XY, YZ, ZX,
    MaxPrincipal, MidPrincipal, MinPrincipal,
    MaxShear
}

/// <summary>A tensor field, one symmetric tensor per element; the mapped scalar is von Mises.</summary>
public sealed class ElementTensorField : IResultField
{
    private readonly SymmetricTensor[] _values;

    public ElementTensorField(string name, string unit, SymmetricTensor[] values)
    {
        Name = name;
        Unit = unit;
        _values = values;
    }

    public string Name { get; }
    public string Unit { get; }
    public FieldLocation Location => FieldLocation.Element;
    public int Count => _values.Length;
    public double GetScalar(int index) => _values[index].VonMises();
    public SymmetricTensor GetTensor(int index) => _values[index];
    public IReadOnlyList<SymmetricTensor> Values => _values;

    /// <summary>
    /// A view of this field showing one component or invariant. A view rather than N more
    /// fields: emitting every component of every tensor would put a dozen extra rows in the
    /// results list for the one a user actually wants to see.
    /// </summary>
    public TensorComponentField View(TensorComponent component) => new(this, component);
}

/// <summary>
/// One component or invariant of an <see cref="ElementTensorField"/>, presented as an
/// ordinary result field so every consumer — colour mapping, statistics, export — works on
/// it unchanged. It holds the source field by reference and computes on demand, so no
/// component costs memory until it is asked for.
/// </summary>
public sealed class TensorComponentField : IResultField
{
    private readonly ElementTensorField _source;
    private readonly TensorComponent _component;

    public TensorComponentField(ElementTensorField source, TensorComponent component)
    {
        _source = source;
        _component = component;
        Name = component == TensorComponent.VonMises ? source.Name : $"{source.Name} — {Describe(component)}";
    }

    public string Name { get; }
    public string Unit => _source.Unit;
    public FieldLocation Location => _source.Location;
    public int Count => _source.Count;
    public double GetScalar(int index) => _source.GetTensor(index).Component(_component);

    private static string Describe(TensorComponent component) => component switch
    {
        TensorComponent.XX => "XX",
        TensorComponent.YY => "YY",
        TensorComponent.ZZ => "ZZ",
        TensorComponent.XY => "XY (shear)",
        TensorComponent.YZ => "YZ (shear)",
        TensorComponent.ZX => "ZX (shear)",
        TensorComponent.MaxPrincipal => "max principal",
        TensorComponent.MidPrincipal => "mid principal",
        TensorComponent.MinPrincipal => "min principal",
        TensorComponent.MaxShear => "max shear",
        _ => component.ToString()
    };
}
