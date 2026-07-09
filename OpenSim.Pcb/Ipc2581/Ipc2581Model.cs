using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// One entry of the IPC-2581 layer array, in physical stackup order (top of board first).
/// All dimensions are SI meters. <see cref="CopperOrder"/> is 1..N top→bottom for
/// conductor layers (SIGNAL / PLANE / CONDUCTOR / MIXED) and null for everything else,
/// matching the <c>CopperIsland.LayerOrder</c> convention used by the Gerber pipeline.
/// </summary>
public sealed record Ipc2581Layer(
    string Name,
    string Function,
    string Side,
    string Polarity,
    double Thickness,
    int Sequence,
    int? CopperOrder)
{
    public bool IsConductor => CopperOrder is not null;

    /// <summary>Whether an IPC-2581 layerFunction denotes copper.</summary>
    public static bool IsConductorFunction(string function) =>
        function.ToUpperInvariant() is "SIGNAL" or "PLANE" or "CONDUCTOR" or "MIXED";

    /// <summary>Whether an IPC-2581 layerFunction denotes a dielectric (core/prepreg/adhesive).</summary>
    public static bool IsDielectricFunction(string function) =>
        function.ToUpperInvariant() is "DIELCORE" or "DIELPREG" or "DIELADHV" or "DIELBASE" or "DIELCOAT";
}

/// <summary>A stroked trace polyline (a Line, or an Arc pre-tessellated to chords) on one layer.</summary>
public sealed record Ipc2581Trace(string LayerRef, IReadOnlyList<Point2> Path, double Width);

/// <summary>A filled Contour/Polygon (pour, plane region) on one layer; Cutouts are holes.</summary>
public sealed record Ipc2581Fill(string LayerRef, Polygon2 Shape);

/// <summary>A flashed pad primitive on one layer (standalone SMD pad or a padstack's landing pad).</summary>
public sealed record Ipc2581PadFlash(string LayerRef, Point2 Center, Polygon2 Shape);

/// <summary>
/// A drilled hole from a <c>LayerHole</c>: mechanical drill parameters plus the conductor
/// layers on which its padstack flashes a landing pad — the exact (file-declared) set of
/// layers a plated barrel electrically joins, replacing the Gerber pipeline's geometric
/// annular-ring inference.
/// </summary>
public sealed record Ipc2581Hole(
    string Name,
    Point2 Position,
    double Diameter,
    bool Plated,
    string SpanFrom,
    string SpanTo,
    IReadOnlyList<string> PadLayers);

/// <summary>
/// All geometry belonging to one electrical net, separated by feature type (never
/// flattened). Features that declare no net land in the shared "No Net" bucket.
/// </summary>
public sealed class Ipc2581Net
{
    /// <summary>The net name features without an explicit <c>net</c> attribute default to.</summary>
    public const string NoNet = "No Net";

    public required string Name { get; init; }
    public List<Ipc2581Trace> Traces { get; } = new();
    public List<Ipc2581Fill> Fills { get; } = new();
    public List<Ipc2581PadFlash> Pads { get; } = new();
    public List<Ipc2581Hole> Holes { get; } = new();
}

/// <summary>
/// The parsed IPC-2581 design: board profile (outer boundary), the ordered layer array,
/// and the net dictionary (net name → typed geometry references). All coordinates are SI
/// meters, converted from the file's declared units at parse time.
/// </summary>
public sealed class Ipc2581Board
{
    public required IReadOnlyList<Polygon2> Profile { get; init; }
    public required IReadOnlyList<Ipc2581Layer> Layers { get; init; }
    public required IReadOnlyDictionary<string, Ipc2581Net> Nets { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Conductor layers in stackup order (CopperOrder 1..N).</summary>
    public IReadOnlyList<Ipc2581Layer> ConductorLayers =>
        Layers.Where(l => l.IsConductor).OrderBy(l => l.CopperOrder).ToList();
}
