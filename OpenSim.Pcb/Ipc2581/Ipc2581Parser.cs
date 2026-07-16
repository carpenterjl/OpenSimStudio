using System.Globalization;
using System.Xml;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// Streaming IPC-2581 (revision B) parser: a single forward pass with
/// <see cref="XmlReader"/> so multi-layer boards (10⁵+ trace segments) never build a DOM.
/// Non-conductor subtrees (BOM, components, paste/legend/document layers) are skipped
/// wholesale. Produces the <see cref="Ipc2581Board"/> net dictionary; mapping into the
/// engine's <c>PcbBoard</c> is <see cref="Ipc2581BoardBuilder"/>'s job.
///
/// Error policy: structural problems (malformed XML, unknown units, missing board
/// profile) throw <see cref="InvalidDataException"/> with an actionable message; a bad
/// number or unsupported feature produces a warning naming the element (and line, when
/// available) and skips only that feature.
/// </summary>
public sealed class Ipc2581Parser
{
    private double _scale;                                       // file units → meters; 0 = not yet known
    private readonly List<string> _warnings = new();
    private readonly Ipc2581PrimitiveDictionary _dictionary = new();
    private readonly Dictionary<string, Ipc2581Net> _nets = new();

    // Layer declarations and stackup rows are merged into the final layer array once
    // both sections have streamed past.
    private readonly Dictionary<string, (string Function, string Side, string Polarity)> _layerDecls = new();
    private readonly List<string> _layerDeclOrder = new();
    // KiCad-dialect support: a DRILL layer declares its span as a <Span> child of its
    // <Layer>, and <PadStackDef> (not the Altium-style <PadStack>) declares which
    // conductor layers a padstack lands pads on.
    private readonly Dictionary<string, (string From, string To)> _drillSpans = new();
    private readonly Dictionary<string, List<string>> _padstackPadLayers = new();
    private readonly List<Ipc2581Component> _components = new();
    private readonly List<(string LayerRef, double Thickness, int Sequence, int Group)> _stackupRows = new();
    private int _stackupGroupIndex;

    private List<Polygon2>? _profile;

    public Ipc2581Board Parse(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream);
    }

    public Ipc2581Board Parse(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            ReadDocument(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(
                $"The file is not well-formed IPC-2581 XML (line {ex.LineNumber}): {ex.Message}", ex);
        }

        if (_profile is null || _profile.Count == 0)
            throw new InvalidDataException(
                "The IPC-2581 file has no board Profile (outer boundary). A <Profile> element " +
                "with a closed polygon is required to establish the board dimensions.");

        return new Ipc2581Board
        {
            Profile = _profile,
            Layers = BuildLayerArray(),
            Nets = _nets,
            Warnings = _warnings,
            Components = _components
        };
    }

    private void ReadDocument(XmlReader reader)
    {
        bool sawRoot = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "IPC-2581":
                    sawRoot = true;
                    string? revision = reader.GetAttribute("revision");
                    if (revision is not null && !string.Equals(revision, "B", StringComparison.OrdinalIgnoreCase))
                        _warnings.Add($"IPC-2581: file declares revision '{revision}'; this importer " +
                                      "targets revision B and reads shared constructs only.");
                    break;
                case "DictionaryStandard":
                    // The dictionary declares its own units, independent of CadHeader.
                    double dictScale = ReadUnits(reader) ?? _scale;
                    if (dictScale <= 0)
                        throw new InvalidDataException(
                            "IPC-2581: DictionaryStandard has no readable units and no CadHeader " +
                            "units were seen before it; cannot convert primitive dimensions.");
                    _dictionary.Read(reader, dictScale, _warnings);
                    break;
                case "CadHeader":
                    _scale = ReadUnits(reader) ?? _scale;
                    Consume(reader);
                    break;
                case "Layer":
                    ReadLayerDecl(reader);
                    break;
                case "StackupGroup":
                    // Not consumed — its StackupLayer children stream past this loop. The
                    // group index keeps rows ordered even when each group restarts its
                    // sequence numbers at 1 (rigid-flex exports do this).
                    _stackupGroupIndex++;
                    break;
                case "StackupLayer":
                    ReadStackupLayer(reader);
                    break;
                case "Profile":
                    ReadProfile(reader);
                    break;
                case "PadStack":
                    ReadPadStack(reader);
                    break;
                case "PadStackDef":
                    ReadPadStackDef(reader);
                    break;
                case "LayerFeature":
                    ReadLayerFeature(reader);
                    break;
                case "Component":
                    // Only the identity attributes matter (refdes → part/package for pad
                    // naming in reports); the placement subtree is consumed unread.
                    ReadComponent(reader);
                    break;
                // Whole subtrees with nothing the geometry engine needs. (Content is NOT
                // skipped — it wraps DictionaryStandard.)
                case "Bom" or "Package" or "LogisticHeader" or "HistoryRecord"
                    or "DictionaryColor" or "DictionaryLineDesc" or "DictionaryFillDesc"
                    or "DictionaryUser":
                    Consume(reader);
                    break;
            }
        }
        if (!sawRoot)
            throw new InvalidDataException("The file has no <IPC-2581> root element; not an IPC-2581 document.");
    }

    /// <summary>
    /// Consumes the current element's whole subtree, leaving the reader on its end tag —
    /// unlike <see cref="XmlReader.Skip"/>, which advances PAST it and would make an
    /// enclosing <c>while (reader.Read())</c> loop swallow the following sibling.
    /// </summary>
    private static void Consume(XmlReader reader)
    {
        using var sub = reader.ReadSubtree();
        while (sub.Read()) { }
    }

    /// <summary>Units attribute → meters-per-unit, or null when absent/unreadable.</summary>
    private double? ReadUnits(XmlReader reader)
    {
        string? units = reader.GetAttribute("units");
        if (units is null) return null;
        double? scale = units.ToUpperInvariant() switch
        {
            "MILLIMETER" => 1e-3,
            "MICRON" => 1e-6,
            "INCH" => 25.4e-3,
            _ => null
        };
        if (scale is null)
            throw new InvalidDataException($"IPC-2581: unsupported units '{units}' " +
                                           "(expected MILLIMETER, MICRON, or INCH).");
        return scale;
    }

    private double GeometryScale()
    {
        if (_scale <= 0)
            throw new InvalidDataException(
                "IPC-2581: geometry appeared before any units declaration (CadHeader or " +
                "DictionaryStandard); cannot convert coordinates.");
        return _scale;
    }

    private void ReadLayerDecl(XmlReader reader)
    {
        string? name = reader.GetAttribute("name");
        if (string.IsNullOrEmpty(name)) return;
        string function = reader.GetAttribute("layerFunction") ?? "DOCUMENT";
        string side = reader.GetAttribute("side") ?? "NONE";
        string polarity = reader.GetAttribute("polarity") ?? "POSITIVE";
        if (Ipc2581Layer.IsConductorFunction(function)
            && polarity.Equals("NEGATIVE", StringComparison.OrdinalIgnoreCase))
            _warnings.Add($"IPC-2581: conductor layer '{name}' is NEGATIVE polarity, which is not " +
                          "supported — its features are imported as drawn (positive) copper.");
        if (!_layerDecls.ContainsKey(name))
            _layerDeclOrder.Add(name);
        _layerDecls[name] = (function, side, polarity);

        // KiCad declares a drill layer's copper span as a <Span> child (e.g. the
        // "F.Cu_B.Cu" through-hole layer spans F.Cu → B.Cu).
        if (!reader.IsEmptyElement)
        {
            using var sub = reader.ReadSubtree();
            sub.Read();
            while (sub.Read())
            {
                if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "Span") continue;
                string from = sub.GetAttribute("fromLayer") ?? "";
                string to = sub.GetAttribute("toLayer") ?? "";
                if (from.Length > 0 && to.Length > 0)
                    _drillSpans[name] = (from, to);
            }
        }
    }

    private void ReadStackupLayer(XmlReader reader)
    {
        string? layerRef = reader.GetAttribute("layerOrGroupRef");
        if (string.IsNullOrEmpty(layerRef)) return;
        PolyShapeReader.TryAttr(reader, "thickness", GeometryScale(), out double thickness);
        int sequence = int.TryParse(reader.GetAttribute("sequence"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int s) ? s : _stackupRows.Count + 1;
        _stackupRows.Add((layerRef, thickness, sequence, _stackupGroupIndex));
    }

    /// <summary>
    /// Merges layer declarations and stackup rows into the physical layer array. Stackup
    /// sequence wins for ordering; layers without a stackup row (or when the stackup is
    /// missing entirely) fall back to declaration order with zero thickness.
    /// </summary>
    private IReadOnlyList<Ipc2581Layer> BuildLayerArray()
    {
        var ordered = new List<(string Name, double Thickness, int Sequence)>();
        if (_stackupRows.Count > 0)
        {
            foreach (var row in _stackupRows.OrderBy(r => r.Group).ThenBy(r => r.Sequence))
                if (_layerDecls.ContainsKey(row.LayerRef))
                    ordered.Add((row.LayerRef, row.Thickness, row.Sequence));
            if (ordered.Count == 0)
                _warnings.Add("IPC-2581: the Stackup section references only names that match no " +
                              "declared <Layer> (StackupGroup refs?); physical layer order falls " +
                              "back to declaration order and may not match the real stack.");
            // Declared layers absent from the stackup still belong in the array (unordered tail).
            foreach (var name in _layerDeclOrder.Where(n => ordered.All(o => o.Name != n)))
                ordered.Add((name, 0, int.MaxValue));
        }
        else
        {
            _warnings.Add("IPC-2581: no Stackup section found; layer order follows declaration " +
                          "order and thicknesses are unknown (defaults will be used).");
            for (int i = 0; i < _layerDeclOrder.Count; i++)
                ordered.Add((_layerDeclOrder[i], 0, i + 1));
        }

        var layers = new List<Ipc2581Layer>();
        int copperOrder = 0;
        foreach (var (name, thickness, sequence) in ordered)
        {
            var (function, side, polarity) = _layerDecls[name];
            int? order = Ipc2581Layer.IsConductorFunction(function) ? ++copperOrder : null;
            layers.Add(new Ipc2581Layer(name, function, side, polarity, thickness, sequence, order));
        }
        if (copperOrder == 0)
            _warnings.Add("IPC-2581: no conductor (SIGNAL/PLANE) layers were declared.");
        return layers;
    }

    private void ReadProfile(XmlReader reader)
    {
        double scale = GeometryScale();
        var profile = new List<Polygon2>();
        IReadOnlyList<Point2>? outer = null;
        var holes = new List<IReadOnlyList<Point2>>();

        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "Polygon")
            {
                // A new outer starts a new board region (multi-board panels).
                if (outer is not null)
                {
                    profile.Add(new Polygon2(outer, holes));
                    holes = new List<IReadOnlyList<Point2>>();
                }
                outer = PolyShapeReader.ReadRing(sub, scale, _warnings);
            }
            else if (sub.LocalName == "Cutout")
            {
                var hole = PolyShapeReader.ReadRing(sub, scale, _warnings);
                if (hole is not null) holes.Add(hole);
            }
        }
        if (outer is not null)
            profile.Add(new Polygon2(outer, holes));
        _profile = profile;
    }

    private Ipc2581Net Net(string? name)
    {
        string key = string.IsNullOrWhiteSpace(name) ? Ipc2581Net.NoNet : name;
        if (!_nets.TryGetValue(key, out var net))
            _nets[key] = net = new Ipc2581Net { Name = key };
        return net;
    }

    // ---------------- PadStack: holes + landing pads ----------------

    private void ReadPadStack(XmlReader reader)
    {
        double scale = GeometryScale();
        var net = Net(reader.GetAttribute("net"));

        using var sub = reader.ReadSubtree();
        sub.Read();
        // A padstack's pads all share its drill(s); collect pads first, then attach the
        // pad layers to each hole record.
        var pads = new List<Ipc2581PadFlash>();
        var holes = new List<Ipc2581Hole>();

        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            switch (sub.LocalName)
            {
                case "LayerHole":
                    var hole = ReadLayerHole(sub, scale);
                    if (hole is not null) holes.Add(hole);
                    break;
                case "LayerPad":
                    var pad = ReadLayerPad(sub, scale);
                    if (pad is not null) pads.Add(pad);
                    break;
            }
        }

        net.Pads.AddRange(pads);
        var padLayers = pads.Select(p => p.LayerRef).Distinct().ToList();
        foreach (var hole in holes)
            net.Holes.Add(hole with { PadLayers = padLayers });
    }

    private Ipc2581Hole? ReadLayerHole(XmlReader reader, double scale)
    {
        string name = reader.GetAttribute("name") ?? "";
        string plating = reader.GetAttribute("platingStatus") ?? "PLATED";
        bool plated = plating.ToUpperInvariant() is "PLATED" or "VIA";
        if (!PolyShapeReader.TryAttr(reader, "x", scale, out double x)
            || !PolyShapeReader.TryAttr(reader, "y", scale, out double y)
            || !PolyShapeReader.TryAttr(reader, "diameter", scale, out double diameter)
            || diameter <= 0)
        {
            _warnings.Add($"IPC-2581: LayerHole '{name}' has a malformed position or diameter" +
                          $"{PolyShapeReader.LinePosition(reader)}; hole skipped.");
            Consume(reader);
            return null;
        }

        string spanFrom = "", spanTo = "";
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "Span")
            {
                spanFrom = sub.GetAttribute("fromLayer") ?? "";
                spanTo = sub.GetAttribute("toLayer") ?? "";
            }
        }
        return new Ipc2581Hole(name, new Point2(x, y), diameter, plated, spanFrom, spanTo,
            Array.Empty<string>());
    }

    private Ipc2581PadFlash? ReadLayerPad(XmlReader reader, double scale)
    {
        string layerRef = reader.GetAttribute("layerRef") ?? "";
        var (location, rotation, primitiveRef, inline, componentRef, pin) = ReadFlashBody(reader, scale);
        if (location is null)
        {
            _warnings.Add($"IPC-2581: LayerPad on '{layerRef}' has no Location; pad skipped.");
            return null;
        }
        var shape = ResolveFlash(primitiveRef, inline, location.Value, rotation);
        return shape is null ? null
            : new Ipc2581PadFlash(layerRef, location.Value, shape, componentRef, pin);
    }

    /// <summary>
    /// A KiCad-style <c>PadStackDef</c>: only the pad layer list is needed — it tells
    /// <see cref="Ipc2581BoardBuilder"/> which conductor layers a plated hole referencing
    /// this padstack electrically joins. Pad shapes are NOT taken from the definition;
    /// each <c>Pad</c> instance in a LayerFeature carries its own primitive ref.
    /// </summary>
    private void ReadPadStackDef(XmlReader reader)
    {
        string? name = reader.GetAttribute("name");
        var layers = new List<string>();
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "PadstackPadDef") continue;
            string? layerRef = sub.GetAttribute("layerRef");
            if (!string.IsNullOrEmpty(layerRef) && !layers.Contains(layerRef))
                layers.Add(layerRef);
        }
        if (!string.IsNullOrEmpty(name))
            _padstackPadLayers[name] = layers;
    }

    /// <summary>
    /// Reads a KiCad-style drill LayerFeature: per-net Sets of <c>Hole</c> elements. The
    /// span comes from the drill layer's own declaration; the pad layers from the Set's
    /// referenced padstack definition (falling back to the span endpoints, which is what
    /// a through via lands on anyway).
    /// </summary>
    private void ReadDrillLayerFeature(XmlReader reader, string layerRef)
    {
        double scale = GeometryScale();
        var (spanFrom, spanTo) = _drillSpans.TryGetValue(layerRef, out var span) ? span : ("", "");

        Ipc2581Net? net = null;
        List<string>? padLayers = null;
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "Set")
            {
                net = Net(sub.GetAttribute("net"));
                string? geometry = sub.GetAttribute("geometry");
                padLayers = geometry is not null && _padstackPadLayers.TryGetValue(geometry, out var pl)
                    ? pl : null;
            }
            else if (sub.LocalName == "Hole")
            {
                string name = sub.GetAttribute("name") ?? "";
                string plating = sub.GetAttribute("platingStatus") ?? "PLATED";
                bool plated = plating.ToUpperInvariant() is "PLATED" or "VIA";
                if (!PolyShapeReader.TryAttr(sub, "x", scale, out double x)
                    || !PolyShapeReader.TryAttr(sub, "y", scale, out double y)
                    || !PolyShapeReader.TryAttr(sub, "diameter", scale, out double diameter)
                    || diameter <= 0)
                {
                    _warnings.Add($"IPC-2581: Hole '{name}' on '{layerRef}' has a malformed position " +
                                  $"or diameter{PolyShapeReader.LinePosition(sub)}; hole skipped.");
                    continue;
                }
                // Pad layers: the referenced padstack's declaration when known, else the
                // drill span endpoints, else every conductor layer (a through hole).
                var layers = padLayers is { Count: > 0 } ? padLayers
                    : spanFrom.Length > 0 ? new List<string> { spanFrom, spanTo }
                    : _layerDeclOrder.Where(n => Ipc2581Layer.IsConductorFunction(_layerDecls[n].Function)).ToList();
                (net ?? Net(null)).Holes.Add(new Ipc2581Hole(name, new Point2(x, y), diameter,
                    plated, spanFrom, spanTo, layers));
            }
        }
    }

    // ---------------- LayerFeature: traces, fills, flashes per net ----------------

    private void ReadLayerFeature(XmlReader reader)
    {
        string layerRef = reader.GetAttribute("layerRef") ?? "";
        bool known = _layerDecls.TryGetValue(layerRef, out var decl);
        if (known && decl.Function.Equals("DRILL", StringComparison.OrdinalIgnoreCase))
        {
            ReadDrillLayerFeature(reader, layerRef);             // KiCad puts Hole elements here
            return;
        }
        if (!known || !Ipc2581Layer.IsConductorFunction(decl.Function))
        {
            Consume(reader);                                     // paste/legend/document layers: whole subtree
            return;
        }

        double scale = GeometryScale();
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "Set") continue;
            var net = Net(sub.GetAttribute("net"));
            ReadFeatureContainer(sub, scale, layerRef, net);
        }
    }

    /// <summary>
    /// Reads geometry children of a Set / Features / UserSpecial container, tracking the
    /// running Xform/Location state that IPC-2581 applies to subsequent flashes.
    /// </summary>
    private void ReadFeatureContainer(XmlReader reader, double scale, string layerRef, Ipc2581Net net)
    {
        Point2? location = null;
        double rotation = 0;

        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            switch (sub.LocalName)
            {
                case "Features" or "UserSpecial":
                    ReadFeatureContainer(sub, scale, layerRef, net);
                    break;
                case "Xform":
                    PolyShapeReader.TryAttr(sub, "rotation", 1.0, out rotation);
                    if (string.Equals(sub.GetAttribute("mirror"), "true", StringComparison.OrdinalIgnoreCase))
                        _warnings.Add($"IPC-2581: Xform mirror on '{layerRef}' is not supported and ignored.");
                    break;
                case "Location":
                    if (PolyShapeReader.TryAttr(sub, "x", scale, out double lx)
                        && PolyShapeReader.TryAttr(sub, "y", scale, out double ly))
                        location = new Point2(lx, ly);
                    break;
                case "Line":
                    ReadLine(sub, scale, layerRef, net);
                    break;
                case "Arc":
                    ReadArc(sub, scale, layerRef, net);
                    break;
                case "Contour":
                    ReadContourFill(sub, scale, layerRef, net);
                    break;
                case "Polygon":
                    var ring = PolyShapeReader.ReadRing(sub, scale, _warnings);
                    if (ring is not null)
                        net.Fills.Add(new Ipc2581Fill(layerRef, new Polygon2(ring)));
                    break;
                case "Circle" or "RectCenter" or "RectRound" or "RectCham" or "Oval" or "Donut":
                    FlashInline(sub, scale, layerRef, net, location, rotation);
                    break;
                case "StandardPrimitiveRef":
                    FlashRef(sub, layerRef, net, location, rotation);
                    break;
                case "Pad":
                    // KiCad-style pad instance: self-contained Location/Xform/primitive
                    // ref (the padstackDefRef only matters for drill spans, handled in
                    // the drill LayerFeature).
                    ReadPadInstance(sub, scale, layerRef, net);
                    break;
                case "LineDesc" or "FillDesc" or "ColorRef":
                    break;                                       // style-only elements
                default:
                    _warnings.Add($"IPC-2581: unsupported feature <{sub.LocalName}> on layer " +
                                  $"'{layerRef}'{PolyShapeReader.LinePosition(sub)}; skipped.");
                    Consume(sub);
                    break;
            }
        }
    }

    private void FlashInline(XmlReader reader, double scale, string layerRef, Ipc2581Net net,
        Point2? location, double rotation)
    {
        if (location is null)
        {
            _warnings.Add($"IPC-2581: <{reader.LocalName}> flash on '{layerRef}' has no preceding " +
                          "Location; skipped.");
            return;
        }
        var primitive = Ipc2581PrimitiveDictionary.TryReadPrimitive(reader, scale, _warnings);
        if (primitive is null) return;
        net.Pads.Add(new Ipc2581PadFlash(layerRef, location.Value,
            Ipc2581PrimitiveDictionary.Flash(primitive, location.Value, rotation)));
    }

    private void FlashRef(XmlReader reader, string layerRef, Ipc2581Net net,
        Point2? location, double rotation)
    {
        string id = reader.GetAttribute("id") ?? "";
        if (location is null)
        {
            _warnings.Add($"IPC-2581: StandardPrimitiveRef '{id}' on '{layerRef}' has no preceding " +
                          "Location; skipped.");
            return;
        }
        var shape = _dictionary.Flash(id, location.Value, rotation, _warnings);
        if (shape is not null)
            net.Pads.Add(new Ipc2581PadFlash(layerRef, location.Value, shape));
    }

    /// <summary>
    /// A KiCad-style <c>Pad</c> instance inside a conductor Set: its Location/Xform and
    /// primitive ref are self-contained (unlike bare flashes, which inherit the running
    /// Set state), so it reuses the LayerPad body reader. A <c>PinRef</c> child carries
    /// the component pin the flash belongs to — captured for pad naming.
    /// </summary>
    private void ReadPadInstance(XmlReader reader, double scale, string layerRef, Ipc2581Net net)
    {
        var (location, rotation, primitiveRef, inline, componentRef, pin) = ReadFlashBody(reader, scale);
        if (location is null)
        {
            _warnings.Add($"IPC-2581: Pad on '{layerRef}' has no Location" +
                          $"{PolyShapeReader.LinePosition(reader)}; pad skipped.");
            return;
        }
        var shape = ResolveFlash(primitiveRef, inline, location.Value, rotation);
        if (shape is not null)
            net.Pads.Add(new Ipc2581PadFlash(layerRef, location.Value, shape, componentRef, pin));
    }

    /// <summary>Reads a <c>Component</c> element's identity attributes (refdes and the
    /// part / footprint-package names its pads inherit); the placement subtree holds
    /// nothing else the geometry engine needs.</summary>
    private void ReadComponent(XmlReader reader)
    {
        string? refDes = reader.GetAttribute("refDes");
        if (refDes is not null)
            _components.Add(new Ipc2581Component(refDes,
                reader.GetAttribute("packageRef"), reader.GetAttribute("part")));
        Consume(reader);
    }

    private (Point2? Location, double Rotation, string? PrimitiveRef,
        Ipc2581PrimitiveDictionary.Primitive? Inline, string? ComponentRef, string? Pin)
        ReadFlashBody(XmlReader reader, double scale)
    {
        Point2? location = null;
        double rotation = 0;
        string? primitiveRef = null;
        string? componentRef = null;
        string? pin = null;
        Ipc2581PrimitiveDictionary.Primitive? inline = null;

        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            switch (sub.LocalName)
            {
                case "Location":
                    if (PolyShapeReader.TryAttr(sub, "x", scale, out double x)
                        && PolyShapeReader.TryAttr(sub, "y", scale, out double y))
                        location = new Point2(x, y);
                    break;
                case "Xform":
                    PolyShapeReader.TryAttr(sub, "rotation", 1.0, out rotation);
                    break;
                case "StandardPrimitiveRef":
                    primitiveRef = sub.GetAttribute("id");
                    break;
                case "PinRef":
                    componentRef = sub.GetAttribute("componentRef");
                    pin = sub.GetAttribute("pin");
                    break;
                default:
                    inline = Ipc2581PrimitiveDictionary.TryReadPrimitive(sub, scale, _warnings) ?? inline;
                    break;
            }
        }
        return (location, rotation, primitiveRef, inline, componentRef, pin);
    }

    private Polygon2? ResolveFlash(string? primitiveRef,
        Ipc2581PrimitiveDictionary.Primitive? inline, Point2 location, double rotation)
    {
        if (primitiveRef is not null)
            return _dictionary.Flash(primitiveRef, location, rotation, _warnings);
        if (inline is not null)
            return Ipc2581PrimitiveDictionary.Flash(inline, location, rotation);
        _warnings.Add("IPC-2581: pad flash has neither a StandardPrimitiveRef nor an inline " +
                      "primitive; skipped.");
        return null;
    }

    /// <summary>A Contour fill: one Polygon outer with Cutout holes → a copper pour region.</summary>
    private void ReadContourFill(XmlReader reader, double scale, string layerRef, Ipc2581Net net)
    {
        IReadOnlyList<Point2>? outer = null;
        var holes = new List<IReadOnlyList<Point2>>();
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "Polygon")
                outer ??= PolyShapeReader.ReadRing(sub, scale, _warnings);
            else if (sub.LocalName == "Cutout")
            {
                var hole = PolyShapeReader.ReadRing(sub, scale, _warnings);
                if (hole is not null) holes.Add(hole);
            }
        }
        if (outer is not null)
            net.Fills.Add(new Ipc2581Fill(layerRef, new Polygon2(outer, holes)));
    }

    private void ReadLine(XmlReader reader, double scale, string layerRef, Ipc2581Net net)
    {
        bool ok = PolyShapeReader.TryAttr(reader, "startX", scale, out double x0)
                  & PolyShapeReader.TryAttr(reader, "startY", scale, out double y0)
                  & PolyShapeReader.TryAttr(reader, "endX", scale, out double x1)
                  & PolyShapeReader.TryAttr(reader, "endY", scale, out double y1);
        double width = ReadLineWidth(reader, scale, layerRef, out bool widthOk);
        if (!ok)
        {
            _warnings.Add($"IPC-2581: Line on '{layerRef}' has malformed coordinates" +
                          $"{PolyShapeReader.LinePosition(reader)}; skipped.");
            return;
        }
        if (!widthOk) return;
        net.Traces.Add(new Ipc2581Trace(layerRef,
            new[] { new Point2(x0, y0), new Point2(x1, y1) }, width));
    }

    private void ReadArc(XmlReader reader, double scale, string layerRef, Ipc2581Net net)
    {
        bool ok = PolyShapeReader.TryAttr(reader, "startX", scale, out double x0)
                  & PolyShapeReader.TryAttr(reader, "startY", scale, out double y0)
                  & PolyShapeReader.TryAttr(reader, "endX", scale, out double x1)
                  & PolyShapeReader.TryAttr(reader, "endY", scale, out double y1)
                  & PolyShapeReader.TryAttr(reader, "centerX", scale, out double cx)
                  & PolyShapeReader.TryAttr(reader, "centerY", scale, out double cy);
        bool clockwise = string.Equals(reader.GetAttribute("clockwise"), "true",
            StringComparison.OrdinalIgnoreCase);
        double width = ReadLineWidth(reader, scale, layerRef, out bool widthOk);
        if (!ok)
        {
            _warnings.Add($"IPC-2581: Arc on '{layerRef}' has malformed coordinates" +
                          $"{PolyShapeReader.LinePosition(reader)}; skipped.");
            return;
        }
        if (!widthOk) return;

        var from = new Point2(x0, y0);
        var path = new List<Point2> { from };
        PolyShapeReader.AppendArc(path, from, new Point2(x1, y1), new Point2(cx, cy), clockwise);
        // A full-circle arc (start == end) drops its duplicate endpoint; re-close the
        // stroke path so the pen returns to the start.
        if (path.Count >= 2 && (path[^1] - path[0]).Length > 1e-12
            && Math.Abs((from - new Point2(x1, y1)).Length) < 1e-12)
            path.Add(from);
        net.Traces.Add(new Ipc2581Trace(layerRef, path, width));
    }

    /// <summary>
    /// Reads the LineDesc child's width. A zero/negative width on a conductor layer is
    /// unmeshable copper, so the trace is dropped with a warning (widthOk = false).
    /// Consumes the element's children, so call it after reading the element's attributes.
    /// </summary>
    private double ReadLineWidth(XmlReader reader, double scale, string layerRef, out bool widthOk)
    {
        double width = 0;
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "LineDesc") continue;
            PolyShapeReader.TryAttr(sub, "lineWidth", scale, out width);
            string? end = sub.GetAttribute("lineEnd");
            if (end is not null && !end.Equals("ROUND", StringComparison.OrdinalIgnoreCase)
                                && !end.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                _warnings.Add($"IPC-2581: lineEnd '{end}' on '{layerRef}' stroked with round caps.");
        }
        widthOk = width > 0;
        if (!widthOk)
            _warnings.Add($"IPC-2581: zero-width trace on '{layerRef}' skipped (unmeshable copper).");
        return width;
    }
}
