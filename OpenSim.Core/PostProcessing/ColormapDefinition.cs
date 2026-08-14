using System.Text.Json.Serialization;

namespace OpenSim.Core.PostProcessing;

/// <summary>
/// One color anchor on a colormap: an RGB triple at a normalized position on the
/// 0..1 colormap axis (the same axis <see cref="FieldScale.Normalize"/> produces).
/// </summary>
/// <param name="Position">Position on the colormap axis, 0..1.</param>
public readonly record struct ColormapStop(double Position, byte R, byte G, byte B);

/// <summary>
/// A user-editable colormap: an ordered list of color stops spanning the whole 0..1
/// axis, sampled by linear RGB interpolation (or quantized into bands when
/// <see cref="Discrete"/>).
/// <para>
/// The invariants (at least two stops, strictly ascending positions, first exactly 0
/// and last exactly 1) are enforced in the constructor and every edit returns a NEW
/// definition rather than mutating: a colormap is display state shared by the scene,
/// the legend and the section cut, and a half-edited map would color those three
/// differently. Invalid input THROWS instead of being silently reordered or clamped —
/// a colormap that quietly disagrees with its legend is worse than a loud failure.
/// </para>
/// </summary>
public sealed record ColormapDefinition
{
    /// <summary>Display name; also the key under which a user map is stored.</summary>
    public string Name { get; }

    /// <summary>Color anchors, strictly ascending in position, spanning [0, 1].</summary>
    public IReadOnlyList<ColormapStop> Stops { get; }

    /// <summary>When true the map is quantized into one flat band per stop interval
    /// (banded contour coloring) instead of a continuous gradient.</summary>
    public bool Discrete { get; }

    [JsonConstructor]
    public ColormapDefinition(string name, IReadOnlyList<ColormapStop> stops, bool discrete = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count < 2)
            throw new ArgumentException($"Colormap '{name}' needs at least 2 stops, got {stops.Count}.", nameof(stops));
        for (int i = 1; i < stops.Count; i++)
            if (stops[i].Position <= stops[i - 1].Position)
                throw new ArgumentException(
                    $"Colormap '{name}' stop {i} at {stops[i].Position} is not above stop {i - 1} at " +
                    $"{stops[i - 1].Position} — stops must be strictly ascending.", nameof(stops));
        if (stops[0].Position != 0.0 || stops[^1].Position != 1.0)
            throw new ArgumentException(
                $"Colormap '{name}' must span the whole axis: first stop at 0 and last at 1, got " +
                $"{stops[0].Position} … {stops[^1].Position}.", nameof(stops));

        Name = name;
        Stops = stops.ToArray();
        Discrete = discrete;
    }

    /// <summary>The color at colormap coordinate <paramref name="u"/> (clamped to 0..1).
    /// Continuous maps interpolate linearly in RGB between the bracketing stops; discrete
    /// maps return the continuous color at the containing band's MIDPOINT, so every band is
    /// a distinct color drawn from the same map (quantizing to the band's lower stop would
    /// drop the top color entirely).</summary>
    public (byte R, byte G, byte B) Sample(double u)
    {
        u = Math.Clamp(u, 0, 1);
        if (double.IsNaN(u)) u = 0;
        int band = BandIndex(u);
        if (Discrete)
            u = 0.5 * (Stops[band].Position + Stops[band + 1].Position);
        var a = Stops[band];
        var b = Stops[band + 1];
        double span = b.Position - a.Position;
        double t = span > 0 ? (u - a.Position) / span : 0;
        return (Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));
    }

    /// <summary>Index of the stop interval containing <paramref name="u"/>: the returned
    /// band spans Stops[i] … Stops[i+1], with u = 1 belonging to the last band.</summary>
    public int BandIndex(double u)
    {
        for (int i = Stops.Count - 2; i >= 0; i--)
            if (u >= Stops[i].Position)
                return i;
        return 0;
    }

    private static byte Lerp(byte a, byte b, double t) =>
        (byte)Math.Clamp(Math.Round(a + (b - a) * t), 0, 255);

    /// <summary>Moves an interior stop, clamped strictly between its neighbours (the
    /// endpoints are pinned at 0 and 1 — moving them would leave part of the axis
    /// uncolored). Moving an endpoint throws.</summary>
    public ColormapDefinition WithStopMoved(int index, double position)
    {
        RequireInterior(index, "move");
        double lo = Stops[index - 1].Position, hi = Stops[index + 1].Position;
        // A stop must stay strictly between its neighbours; nudge by a hair rather than
        // rejecting a drag that overshoots — dragging is a continuous gesture.
        double eps = Math.Max((hi - lo) * 1e-6, double.Epsilon);
        double p = Math.Clamp(position, lo + eps, hi - eps);
        var stops = Stops.ToArray();
        stops[index] = stops[index] with { Position = p };
        return new ColormapDefinition(Name, stops, Discrete);
    }

    /// <summary>Recolors one stop (any stop, including the endpoints).</summary>
    public ColormapDefinition WithStopColor(int index, byte r, byte g, byte b)
    {
        if (index < 0 || index >= Stops.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"Colormap '{Name}' has {Stops.Count} stops.");
        var stops = Stops.ToArray();
        stops[index] = stops[index] with { R = r, G = g, B = b };
        return new ColormapDefinition(Name, stops, Discrete);
    }

    /// <summary>Adds a stop at <paramref name="position"/> taking the color the map
    /// already has there — inserting a stop must not change how the map looks.</summary>
    public ColormapDefinition WithStopAdded(double position)
    {
        if (!(position > 0 && position < 1))
            throw new ArgumentOutOfRangeException(nameof(position), position,
                "A new stop must lie strictly inside the axis (0 and 1 are already taken).");
        foreach (var s in Stops)
            if (s.Position == position)
                throw new ArgumentException(
                    $"Colormap '{Name}' already has a stop at {position}.", nameof(position));
        var (r, g, b) = Sample(position);
        var stops = Stops.ToList();
        int insert = stops.FindIndex(s => s.Position > position);
        stops.Insert(insert, new ColormapStop(position, r, g, b));
        return new ColormapDefinition(Name, stops, Discrete);
    }

    /// <summary>Removes an interior stop. Endpoints and the two-stop minimum are
    /// protected — the axis must stay fully spanned.</summary>
    public ColormapDefinition WithStopRemoved(int index)
    {
        RequireInterior(index, "remove");
        var stops = Stops.ToList();
        stops.RemoveAt(index);
        return new ColormapDefinition(Name, stops, Discrete);
    }

    /// <summary>Same stops, continuous ↔ banded.</summary>
    public ColormapDefinition WithDiscrete(bool discrete) =>
        new(Name, Stops, discrete);

    /// <summary>Same stops under a different name (the "save as" of the editor).</summary>
    public ColormapDefinition WithName(string name) =>
        new(name, Stops, Discrete);

    private void RequireInterior(int index, string verb)
    {
        if (index <= 0 || index >= Stops.Count - 1)
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"Cannot {verb} stop {index} of colormap '{Name}': the first and last stops are " +
                "pinned at 0 and 1 so the map always spans the whole axis.");
    }

    // Records compare IReadOnlyList by reference; colormaps are compared by VALUE
    // (the view model asks "did the map change?" of freshly built copies).
    public bool Equals(ColormapDefinition? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Name != other.Name || Discrete != other.Discrete || Stops.Count != other.Stops.Count)
            return false;
        for (int i = 0; i < Stops.Count; i++)
            if (!Stops[i].Equals(other.Stops[i]))
                return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Discrete);
        foreach (var s in Stops) hash.Add(s);
        return hash.ToHashCode();
    }

    // ------------------------------------------------------------------
    // Built-in presets. Rainbow and Viridis are BYTE-IDENTICAL to the two hardcoded
    // maps the app shipped before colormaps became editable — every existing result
    // screenshot, RF overlay and legend keeps its exact colors.
    // ------------------------------------------------------------------

    public static ColormapDefinition Rainbow { get; } = new("Rainbow", new ColormapStop[]
    {
        new(0.00, 0, 0, 255),
        new(0.25, 0, 255, 255),
        new(0.50, 0, 255, 0),
        new(0.75, 255, 255, 0),
        new(1.00, 255, 0, 0)
    });

    public static ColormapDefinition Viridis { get; } = new("Viridis", new ColormapStop[]
    {
        new(0.00, 68, 1, 84),
        new(0.25, 59, 82, 139),
        new(0.50, 33, 145, 140),
        new(0.75, 94, 201, 98),
        new(1.00, 253, 231, 37)
    });

    public static ColormapDefinition Grayscale { get; } = new("Grayscale", new ColormapStop[]
    {
        new(0.00, 0, 0, 0),
        new(1.00, 255, 255, 255)
    });

    /// <summary>A diverging blue–white–red map: the natural choice for temperature
    /// fields read against an ambient, where the middle of the range is the neutral
    /// state rather than "half way to hot".</summary>
    public static ColormapDefinition CoolWarm { get; } = new("Cool-warm", new ColormapStop[]
    {
        new(0.00, 59, 76, 192),
        new(0.25, 144, 178, 254),
        new(0.50, 221, 221, 221),
        new(0.75, 245, 156, 125),
        new(1.00, 180, 4, 38)
    });

    /// <summary>The built-in maps, in menu order.</summary>
    public static IReadOnlyList<ColormapDefinition> Presets { get; } =
        new[] { Rainbow, Viridis, CoolWarm, Grayscale };
}
