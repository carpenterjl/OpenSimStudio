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

/// <summary>A flashed pad primitive on one layer (standalone SMD pad or a padstack's
/// landing pad). KiCad-dialect <c>Pad</c> instances carry a <c>PinRef</c> child naming
/// the component pin the flash belongs to — captured so reports can name pads by
/// refdes.pin instead of synthesized coordinates; null when the file doesn't say.</summary>
public sealed record Ipc2581PadFlash(string LayerRef, Point2 Center, Polygon2 Shape,
    string? ComponentRef = null, string? Pin = null);

/// <summary>A placed component from the <c>Component</c> element: the refdes and the
/// part / footprint-package identity its pads inherit, plus the placement (side,
/// location, rotation, mirror) — pad GEOMETRY still comes from the conductor
/// LayerFeatures; the placement exists for the model and the transform oracle.</summary>
public sealed record Ipc2581Component(string RefDes, string? PackageRef, string? Part)
{
    /// <summary>The mounting-side layer name (e.g. TOP/BOTTOM); null when undeclared.</summary>
    public string? LayerRef { get; init; }

    public Point2? Location { get; init; }
    public double RotationDeg { get; init; }
    public bool Mirror { get; init; }

    /// <summary>The component's Xform placement (identity when no Location was declared).</summary>
    public Ipc2581Transform Transform =>
        new(Location ?? new Point2(0, 0), RotationDeg, Mirror);
}

/// <summary>Where an <see cref="Ipc2581Hole"/>'s pad-layer list came from — the builder
/// only geometrically refines the two FALLBACK provenances (a file-declared list is
/// exact and stays untouched, keeping the KiCad/Altium paths byte-for-byte).</summary>
public enum Ipc2581PadLayersSource
{
    /// <summary>The padstack's own LayerPad declarations (Altium) — exact.</summary>
    DeclaredPads,
    /// <summary>A referenced PadStackDef's pad-layer list (KiCad) — exact.</summary>
    PadStackDef,
    /// <summary>Fallback: the drill span's two endpoints — refinable by coincident copper.</summary>
    SpanEndpoints,
    /// <summary>Fallback: every conductor layer — refinable by coincident copper.</summary>
    AllConductors,
}

/// <summary>
/// A drilled hole from a <c>LayerHole</c>: mechanical drill parameters plus the conductor
/// layers on which its padstack flashes a landing pad — the exact (file-declared) set of
/// layers a plated barrel electrically joins, replacing the Gerber pipeline's geometric
/// annular-ring inference. <see cref="Source"/> records whether the list is declared or
/// a fallback the builder may refine.
/// </summary>
public sealed record Ipc2581Hole(
    string Name,
    Point2 Position,
    double Diameter,
    bool Plated,
    string SpanFrom,
    string SpanTo,
    IReadOnlyList<string> PadLayers)
{
    public Ipc2581PadLayersSource Source { get; init; } = Ipc2581PadLayersSource.DeclaredPads;
}

/// <summary>A backdrill hole instance: position/diameter from its DRILL-layer feature,
/// the severed span from that layer's declaration, and the governing spec (whose
/// MUST_NOT_CUT layers are protected). Never a via — it removes connectivity.</summary>
public sealed record Ipc2581Backdrill(
    Point2 Position,
    double Diameter,
    string SpanFrom,
    string SpanTo,
    Ipc2581BackdrillSpec? Spec);

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

    /// <summary>Declared-but-skipped/approximated content — a conforming file imports
    /// with zero warnings (see <see cref="Ipc2581Diagnostics"/> for the severity contract).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Informational: absent data defaulted, empty geometry, summaries/timings.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Placed components (refdes → part/package identity); empty when the file
    /// carries no Component elements (or an exporter omits them).</summary>
    public IReadOnlyList<Ipc2581Component> Components { get; init; } = Array.Empty<Ipc2581Component>();

    /// <summary>Footprint packages by name (LandPattern pad lists); empty when absent.</summary>
    public IReadOnlyDictionary<string, Ipc2581Package> Packages { get; init; } =
        new Dictionary<string, Ipc2581Package>();

    /// <summary>Backdrill holes (stub removals) — they sever coincident vias' bridged
    /// layers over their span and never create copper, vias, or nets of their own.</summary>
    public IReadOnlyList<Ipc2581Backdrill> Backdrills { get; init; } = Array.Empty<Ipc2581Backdrill>();

    /// <summary>Routed slots/cavities, aggregated by name across their per-layer occurrences.</summary>
    public IReadOnlyList<Ipc2581SlotCavity> Slots { get; init; } = Array.Empty<Ipc2581SlotCavity>();

    /// <summary>Conductor layers in stackup order (CopperOrder 1..N).</summary>
    public IReadOnlyList<Ipc2581Layer> ConductorLayers =>
        Layers.Where(l => l.IsConductor).OrderBy(l => l.CopperOrder).ToList();
}
