namespace OpenSim.Core.Numerics;

/// <summary>Axis-aligned bounding box.</summary>
public readonly struct Aabb
{
    public Vector3D Min { get; }
    public Vector3D Max { get; }

    public Aabb(Vector3D min, Vector3D max)
    {
        Min = min; Max = max;
    }

    public Vector3D Center => (Min + Max) * 0.5;
    public Vector3D Size => Max - Min;

    /// <summary>Length of the box diagonal — a convenient global size scale for tolerances.</summary>
    public double Diagonal => Size.Length;

    public bool Contains(Vector3D p) =>
        p.X >= Min.X && p.X <= Max.X &&
        p.Y >= Min.Y && p.Y <= Max.Y &&
        p.Z >= Min.Z && p.Z <= Max.Z;

    public Aabb Expanded(double margin) =>
        new(new Vector3D(Min.X - margin, Min.Y - margin, Min.Z - margin),
            new Vector3D(Max.X + margin, Max.Y + margin, Max.Z + margin));

    /// <summary>Computes the bounding box of a point set. Throws on an empty set.</summary>
    public static Aabb FromPoints(IEnumerable<Vector3D> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        bool any = false;
        foreach (var p in points)
        {
            any = true;
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Z < minZ) minZ = p.Z;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
            if (p.Z > maxZ) maxZ = p.Z;
        }
        if (!any)
            throw new ArgumentException("Cannot compute bounds of an empty point set.", nameof(points));
        return new Aabb(new Vector3D(minX, minY, minZ), new Vector3D(maxX, maxY, maxZ));
    }
}
