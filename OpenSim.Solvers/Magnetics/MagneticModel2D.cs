namespace OpenSim.Solvers.Magnetics;

/// <summary>Planar (x, y; current along z, per metre of depth) or axisymmetric (r, z; current
/// around the axis, the first coordinate is the radius r ≥ 0).</summary>
public enum MagneticGeometry { Planar, Axisymmetric }

/// <summary>A sides of the rectangular domain.</summary>
[Flags]
public enum DomainSides { None = 0, Left = 1, Right = 2, Bottom = 4, Top = 8 }

/// <summary>An axis-aligned rectangle of one material, optionally carrying current. Later regions
/// take precedence where they overlap earlier ones.</summary>
/// <param name="X0">Lower first coordinate (x, or r).</param>
/// <param name="Y0">Lower second coordinate (y, or z).</param>
public sealed record MagneticRegion(string Name, double X0, double X1, double Y0, double Y1, MagneticMaterial Material)
{
    public double Area => (X1 - X0) * (Y1 - Y0);
}

/// <summary>A stranded winding: <see cref="Turns"/> turns spread uniformly over its regions, each
/// region with a direction (+1 out of the plane / counter-clockwise about the axis, −1 the return).
/// The current density in a region is N·I/(total area of the regions with that sign).</summary>
public sealed record StrandedCoil(string Name, IReadOnlyList<(string Region, int Direction)> Regions, double Turns, double Current);

/// <summary>A solid conductor: the named regions form ONE conductor carrying a total current
/// (complex amplitude in a time-harmonic solve) with the field inside free to crowd — skin and
/// proximity effect. In a planar model the conductor runs along z; in an axisymmetric one it is a
/// ring. Turns of a winding in series are separate conductors given the same current.</summary>
public sealed record SolidConductor(string Name, IReadOnlyList<string> Regions, System.Numerics.Complex Current);

/// <summary>
/// A 2D magnetic model: a rectangular domain of background material (air) with rectangular
/// regions in it, windings and solid conductors, meshed on a graded rectilinear grid (every
/// region edge is a grid line; spacing starts at <see cref="MinElement"/> at those lines and grows
/// by <see cref="Growth"/> per element to <see cref="MaxElement"/>), each cell cut into two linear
/// triangles. The vector potential is zero on the domain's outer boundary except on sides listed
/// in <see cref="NaturalSides"/> (zero normal derivative — a symmetry plane where the field runs
/// along the side); the axis of an axisymmetric model is always zero.
/// </summary>
public sealed class MagneticModel2D
{
    public required MagneticGeometry Geometry { get; init; }
    public required double DomainX0 { get; init; }
    public required double DomainX1 { get; init; }
    public required double DomainY0 { get; init; }
    public required double DomainY1 { get; init; }
    public MagneticMaterial Background { get; init; } = MagneticMaterial.Air;
    public List<MagneticRegion> Regions { get; } = new();
    public List<StrandedCoil> Coils { get; } = new();
    public List<SolidConductor> Conductors { get; } = new();
    public DomainSides NaturalSides { get; init; }
    public required double MaxElement { get; init; }
    public double? MinElement { get; init; }
    public double Growth { get; init; } = 1.3;

    /// <summary>Extra grid lines (first coordinate, second coordinate) — to resolve a point of
    /// interest or a skin depth inside a region.</summary>
    public List<double> ExtraLinesX { get; } = new();
    public List<double> ExtraLinesY { get; } = new();

    public MagneticRegion Region(string name) =>
        Regions.FirstOrDefault(r => r.Name == name) ?? throw new ArgumentException($"No region named '{name}'.");

    internal void Validate()
    {
        if (!(DomainX1 > DomainX0 && DomainY1 > DomainY0)) throw new ArgumentException("The domain is empty.");
        if (Geometry == MagneticGeometry.Axisymmetric && DomainX0 < 0)
            throw new ArgumentException("An axisymmetric domain starts at r ≥ 0.");
        if (!(MaxElement > 0)) throw new ArgumentException("MaxElement must be positive.");
        foreach (var r in Regions)
        {
            if (!(r.X1 > r.X0 && r.Y1 > r.Y0)) throw new ArgumentException($"Region '{r.Name}' is empty.");
            if (r.X0 < DomainX0 || r.X1 > DomainX1 || r.Y0 < DomainY0 || r.Y1 > DomainY1)
                throw new ArgumentException($"Region '{r.Name}' extends outside the domain.");
        }
        if (Regions.Select(r => r.Name).Distinct().Count() != Regions.Count)
            throw new ArgumentException("Region names must be distinct.");
        var claimed = new HashSet<string>();
        foreach (var coil in Coils)
            foreach (var (name, dir) in coil.Regions)
            {
                Region(name);
                if (dir != 1 && dir != -1) throw new ArgumentException($"Coil '{coil.Name}': direction must be ±1.");
                if (!claimed.Add(name)) throw new ArgumentException($"Region '{name}' belongs to two sources.");
            }
        foreach (var c in Conductors)
            foreach (var name in c.Regions)
            {
                if (!(Region(name).Material.Conductivity > 0))
                    throw new ArgumentException($"Conductor '{c.Name}': region '{name}' has no conductivity.");
                if (!claimed.Add(name)) throw new ArgumentException($"Region '{name}' belongs to two sources.");
            }
    }

    internal double[] GridLines(bool first)
    {
        var raw = new List<double> { first ? DomainX0 : DomainY0, first ? DomainX1 : DomainY1 };
        foreach (var r in Regions) { raw.Add(first ? r.X0 : r.Y0); raw.Add(first ? r.X1 : r.Y1); }
        raw.AddRange(first ? ExtraLinesX : ExtraLinesY);
        double span = first ? DomainX1 - DomainX0 : DomainY1 - DomainY0;
        double tol = 1e-12 * span;
        raw.Sort();
        var unique = new List<double>();
        foreach (double v in raw) if (unique.Count == 0 || v - unique[^1] > tol) unique.Add(v);
        double hMin = Math.Min(MinElement ?? MaxElement, MaxElement);
        var lines = new List<double> { unique[0] };
        for (int i = 1; i < unique.Count; i++)
        {
            double gap = unique[i] - unique[i - 1];
            var steps = new List<double>();
            double u = 0;
            while (u < gap - 1e-9 * gap)
            {
                double d = Math.Min(u, gap - u);
                double size = Math.Min(MaxElement, hMin + (Growth - 1) * d);
                steps.Add(size);
                u += size;
            }
            double stretch = gap / steps.Sum();
            double at = unique[i - 1];
            for (int k = 0; k + 1 < steps.Count; k++) { at += steps[k] * stretch; lines.Add(at); }
            lines.Add(unique[i]);
        }
        return lines.ToArray();
    }
}
