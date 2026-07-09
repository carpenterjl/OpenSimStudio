using System.Text.Json.Serialization;

namespace OpenSim.Core.Numerics;

/// <summary>An immutable double-precision 3D vector used throughout geometry, meshing and solvers.</summary>
public readonly struct Vector3D : IEquatable<Vector3D>
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    [JsonConstructor]
    public Vector3D(double x, double y, double z)
    {
        X = x; Y = y; Z = z;
    }

    public static readonly Vector3D Zero = new(0, 0, 0);
    public static readonly Vector3D UnitX = new(1, 0, 0);
    public static readonly Vector3D UnitY = new(0, 1, 0);
    public static readonly Vector3D UnitZ = new(0, 0, 1);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;

    public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3D operator -(Vector3D a) => new(-a.X, -a.Y, -a.Z);
    public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vector3D operator *(double s, Vector3D a) => a * s;
    public static Vector3D operator /(Vector3D a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vector3D Cross(Vector3D a, Vector3D b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static double Distance(Vector3D a, Vector3D b) => (a - b).Length;
    public static double DistanceSquared(Vector3D a, Vector3D b) => (a - b).LengthSquared;

    /// <summary>Returns a unit-length copy. Throws if the vector is (numerically) zero.</summary>
    public Vector3D Normalized()
    {
        double len = Length;
        if (len < 1e-300)
            throw new InvalidOperationException("Cannot normalize a zero-length vector.");
        return this / len;
    }

    /// <summary>Component access by index (0=X, 1=Y, 2=Z).</summary>
    public double this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public bool Equals(Vector3D other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is Vector3D v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(Vector3D a, Vector3D b) => a.Equals(b);
    public static bool operator !=(Vector3D a, Vector3D b) => !a.Equals(b);

    public override string ToString() => $"({X:g6}, {Y:g6}, {Z:g6})";
}
