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

    public static SymmetricTensor operator +(SymmetricTensor a, SymmetricTensor b) =>
        new(a.XX + b.XX, a.YY + b.YY, a.ZZ + b.ZZ, a.XY + b.XY, a.YZ + b.YZ, a.ZX + b.ZX);

    public static SymmetricTensor operator *(SymmetricTensor a, double s) =>
        new(a.XX * s, a.YY * s, a.ZZ * s, a.XY * s, a.YZ * s, a.ZX * s);
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
}
