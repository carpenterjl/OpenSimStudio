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
    private readonly Ipc2581Diagnostics _diag = new();
    private readonly Ipc2581PrimitiveDictionary _dictionary = new();
    private readonly Ipc2581StyleDictionary _styles = new();
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
    private readonly Ipc2581UserDictionary _userDictionary = new();
    private readonly Dictionary<string, Ipc2581Package> _packages = new();
    private readonly Dictionary<string, Ipc2581BackdrillSpec> _backdrillSpecs = new();
    private readonly List<Ipc2581Backdrill> _backdrills = new();

    // Slots aggregate by name: Altium repeats one named SlotCavity in every LayerFeature
    // it passes through — the outline is read once, the conductor layers accumulate.
    private sealed class SlotAccumulator
    {
        public bool Plated;
        public IReadOnlyList<Point2>? Outline;
        public readonly List<string> ConductorLayers = new();
        public string SpanFrom = "", SpanTo = "";
    }
    private readonly Dictionary<string, SlotAccumulator> _slots = new();
    private readonly List<string> _slotOrder = new();
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
            Warnings = _diag.SealedWarnings(),
            Notes = _diag.SealedNotes(),
            Components = _components,
            Packages = _packages,
            Backdrills = _backdrills,
            Slots = BuildSlots()
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
                        _diag.Warn($"IPC-2581: file declares revision '{revision}'; this importer " +
                                      "targets revision B and reads shared constructs only.");
                    break;
                case "DictionaryStandard":
                    // The dictionary declares its own units, independent of CadHeader.
                    double dictScale = ReadUnits(reader) ?? _scale;
                    if (dictScale <= 0)
                        throw new InvalidDataException(
                            "IPC-2581: DictionaryStandard has no readable units and no CadHeader " +
                            "units were seen before it; cannot convert primitive dimensions.");
                    _dictionary.Read(reader, dictScale, _diag);
                    break;
                case "DictionaryLineDesc":
                    // Geometry-critical, not cosmetic: Cadence routes every trace width
                    // through LineDescRef into this dictionary.
                    double lineScale = ReadUnits(reader) ?? _scale;
                    if (lineScale <= 0)
                        throw new InvalidDataException(
                            "IPC-2581: DictionaryLineDesc has no readable units and no CadHeader " +
                            "units were seen before it; cannot convert stroke widths.");
                    _styles.ReadLineDescDictionary(reader, lineScale, _diag);
                    break;
                case "DictionaryFillDesc":
                    _styles.ReadFillDescDictionary(reader, _diag);
                    break;
                case "CadHeader":
                    _scale = ReadUnits(reader) ?? _scale;
                    ReadCadHeader(reader);
                    break;
                case "Spec":
                    // Standalone specs (some exporters place them outside CadHeader).
                    ReadSpec(reader);
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
                    ReadComponent(reader);
                    break;
                case "Package":
                    ReadPackage(reader);
                    break;
                case "DictionaryUser":
                    ReadDictionaryUser(reader);
                    break;
                // Whole subtrees with nothing the geometry engine needs. (Content is NOT
                // skipped — it wraps DictionaryStandard.) LogicalNet/PhyNet carry the
                // netlist as pin references — net attribution already rides on every
                // geometry Set, so these are consumed explicitly (their children must
                // never leak into this dispatch loop). Spec blocks are backdrill/
                // impedance fabrication specs (a named follow-up models backdrills).
                case "Bom" or "LogisticHeader" or "HistoryRecord"
                    or "DictionaryColor" or "LogicalNet" or "PhyNetGroup" or "PhyNet"
                    or "Certification" or "Avl" or "AvlHeader":
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

    /// <summary>Walks the CadHeader for <c>Spec</c> blocks (Cadence stores its backdrill
    /// specs there); everything else in the header streams past unread.</summary>
    private void ReadCadHeader(XmlReader reader)
    {
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
            if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "Spec")
                ReadSpec(sub);
    }

    /// <summary>
    /// Reads one <c>Spec</c>: a spec carrying <c>Backdrill</c> children is a backdrill
    /// spec (START_LAYER / MUST_NOT_CUT_LAYER / MAX_STUB_LENGTH properties) and is
    /// registered for the drill reader; any other spec kind (impedance, material…)
    /// carries nothing the geometry engine needs and streams past.
    /// </summary>
    private void ReadSpec(XmlReader reader)
    {
        string? name = reader.GetAttribute("name");
        string? startLayer = null;
        var mustNotCut = new List<string>();
        double? maxStub = null;
        bool isBackdrill = false;

        using var sub = reader.ReadSubtree();
        sub.Read();
        string? backdrillType = null;
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "Backdrill")
            {
                isBackdrill = true;
                backdrillType = sub.GetAttribute("type");
            }
            else if (sub.LocalName == "Property" && backdrillType is not null)
            {
                switch (backdrillType.ToUpperInvariant())
                {
                    case "START_LAYER":
                        startLayer = sub.GetAttribute("text") ?? startLayer;
                        break;
                    case "MUST_NOT_CUT_LAYER":
                        string? layer = sub.GetAttribute("text");
                        if (layer is not null) mustNotCut.Add(layer);
                        break;
                    case "MAX_STUB_LENGTH":
                        // Property values carry their own unit attribute (MM observed);
                        // the value is informational (severing uses layers, not depths).
                        if (PolyShapeReader.TryAttr(sub, "value", 1e-3, out double stub))
                            maxStub = stub;
                        break;
                }
            }
        }
        if (isBackdrill && name is not null)
            _backdrillSpecs[name] = new Ipc2581BackdrillSpec(name, startLayer, mustNotCut, maxStub);
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
            _diag.Warn($"IPC-2581: conductor layer '{name}' is NEGATIVE polarity, which is not " +
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
                _diag.Warn("IPC-2581: the Stackup section references only names that match no " +
                              "declared <Layer> (StackupGroup refs?); physical layer order falls " +
                              "back to declaration order and may not match the real stack.");
            // Declared layers absent from the stackup still belong in the array (unordered tail).
            foreach (var name in _layerDeclOrder.Where(n => ordered.All(o => o.Name != n)))
                ordered.Add((name, 0, int.MaxValue));
        }
        else
        {
            // Data genuinely absent from the file (Cadence Allegro exports omit the
            // Stackup section entirely) — an informational note, not a warning.
            _diag.Note("IPC-2581: no Stackup section found; layer order follows declaration " +
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
            _diag.Warn("IPC-2581: no conductor (SIGNAL/PLANE) layers were declared.");
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
                outer = PolyShapeReader.ReadRing(sub, scale, _diag);
            }
            else if (sub.LocalName == "Cutout")
            {
                var hole = PolyShapeReader.ReadRing(sub, scale, _diag);
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
                    pads.AddRange(ReadLayerPad(sub, scale));
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
            _diag.Warn($"IPC-2581: LayerHole '{name}' has a malformed position or diameter" +
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

    private IReadOnlyList<Ipc2581PadFlash> ReadLayerPad(XmlReader reader, double scale)
    {
        string layerRef = reader.GetAttribute("layerRef") ?? "";
        var (location, rotation, mirror, primitiveRef, inline, componentRef, pin) =
            ReadFlashBody(reader, scale);
        if (location is null)
        {
            _diag.Warn($"IPC-2581: LayerPad on '{layerRef}' has no Location; pad skipped.");
            return Array.Empty<Ipc2581PadFlash>();
        }
        var shapes = ResolveFlash(primitiveRef, inline, location.Value, rotation, mirror);
        if (shapes is null) return Array.Empty<Ipc2581PadFlash>();
        return shapes
            .Select(s => new Ipc2581PadFlash(layerRef, location.Value, s, componentRef, pin))
            .ToList();
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
        Ipc2581BackdrillSpec? backdrillSpec = null;
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
                backdrillSpec = null;                            // per-Set state
            }
            else if (sub.LocalName == "SpecRef")
            {
                // A backdrill Set carries a SpecRef to its backdrill spec — that is
                // what distinguishes stub-removal holes from real drills.
                string? id = sub.GetAttribute("id");
                if (id is not null) _backdrillSpecs.TryGetValue(id, out backdrillSpec);
            }
            else if (sub.LocalName == "SlotCavity")
            {
                // A routed slot in the drill layer (the Cadence placement) — its span
                // comes from the drill layer's own declaration.
                ReadSlotCavity(sub, scale, conductorLayer: null, spanFrom, spanTo);
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
                    _diag.Warn($"IPC-2581: Hole '{name}' on '{layerRef}' has a malformed position " +
                                  $"or diameter{PolyShapeReader.LinePosition(sub)}; hole skipped.");
                    continue;
                }
                if (backdrillSpec is not null)
                {
                    // A backdrill is stub REMOVAL: it severs coincident vias over its
                    // span (builder) and must never become a hole/via of its own.
                    _backdrills.Add(new Ipc2581Backdrill(new Point2(x, y), diameter,
                        spanFrom, spanTo, backdrillSpec));
                    continue;
                }
                // Pad layers: the referenced padstack's declaration when known, else the
                // drill span endpoints, else every conductor layer (a through hole).
                // The two fallbacks are geometric guesses the builder refines by
                // coincident same-net copper; a declared list is exact and stays.
                List<string> layers;
                Ipc2581PadLayersSource source;
                if (padLayers is { Count: > 0 })
                {
                    layers = padLayers;
                    source = Ipc2581PadLayersSource.PadStackDef;
                }
                else if (spanFrom.Length > 0)
                {
                    layers = new List<string> { spanFrom, spanTo };
                    source = Ipc2581PadLayersSource.SpanEndpoints;
                }
                else
                {
                    layers = _layerDeclOrder
                        .Where(n => Ipc2581Layer.IsConductorFunction(_layerDecls[n].Function)).ToList();
                    source = Ipc2581PadLayersSource.AllConductors;
                }
                (net ?? Net(null)).Holes.Add(new Ipc2581Hole(name, new Point2(x, y), diameter,
                    plated, spanFrom, spanTo, layers) { Source = source });
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
    private void ReadFeatureContainer(XmlReader reader, double scale, string layerRef,
        Ipc2581Net net, Point2? initialLocation = null)
    {
        // User-dictionary entries are a local frame whose flashes default to the origin
        // (KiCad writes bare primitives with no Location there); board-level containers
        // start with no location so a flash without one fails loudly.
        Point2? location = initialLocation;
        double rotation = 0;
        bool mirror = false;

        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            switch (sub.LocalName)
            {
                case "Features" or "UserSpecial":
                    // Nested containers inherit the running location (a user entry's
                    // UserSpecial inherits the entry's local origin).
                    ReadFeatureContainer(sub, scale, layerRef, net, location);
                    break;
                case "Marking":
                    // A Marking is geometry drawn on its layer (refdes text figures,
                    // logos) — copper on a conductor layer is copper. Transparent
                    // container: its own Location/Xform running state applies inside.
                    ReadFeatureContainer(sub, scale, layerRef, net, location);
                    break;
                case "Xform":
                    PolyShapeReader.TryAttr(sub, "rotation", 1.0, out rotation);
                    mirror = string.Equals(sub.GetAttribute("mirror"), "true",
                        StringComparison.OrdinalIgnoreCase);
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
                case "Polyline":
                    ReadPolyline(sub, scale, layerRef, net);
                    break;
                case "Contour":
                    ReadContourFill(sub, scale, layerRef, net);
                    break;
                case "Polygon":
                {
                    var style = new RingStyle();
                    var ring = PolyShapeReader.ReadRing(sub, scale, _diag,
                        other => CaptureRingStyle(other, scale, layerRef, style));
                    if (ring is not null)
                        AddRingCopper(layerRef, net, ring, style);
                    break;
                }
                case "Circle" or "RectCenter" or "RectRound" or "RectCham" or "RectCorner"
                    or "Oval" or "Donut" or "Diamond" or "Triangle" or "Ellipse" or "Hexagon"
                    or "Octagon" or "Butterfly" or "Thermal" or "Moire":
                    FlashInline(sub, scale, layerRef, net, location, rotation, mirror);
                    break;
                case "StandardPrimitiveRef":
                    FlashRef(sub, layerRef, net, location, rotation, mirror);
                    break;
                case "UserPrimitiveRef":
                {
                    string id = sub.GetAttribute("id") ?? "";
                    if (location is null)
                        _diag.Warn($"IPC-2581: UserPrimitiveRef '{id}' on '{layerRef}' has no " +
                                   "preceding Location; skipped.");
                    else if (!_userDictionary.Flash(id, layerRef, net,
                                 new Ipc2581Transform(location.Value, rotation, mirror)))
                        _diag.Warn($"IPC-2581: UserPrimitiveRef '{id}' not found in the " +
                                   "dictionary; flash skipped.");
                    break;
                }
                case "Pad":
                    // KiCad-style pad instance: self-contained Location/Xform/primitive
                    // ref (the padstackDefRef only matters for drill spans, handled in
                    // the drill LayerFeature).
                    ReadPadInstance(sub, scale, layerRef, net);
                    break;
                case "LocalFiducial":
                    // A fiducial is real copper: a self-contained Location + primitive
                    // ref, structurally a Pad instance without a PinRef.
                    ReadPadInstance(sub, scale, layerRef, net);
                    break;
                case "SlotCavity":
                    // A routed slot occurrence on a conductor layer (Altium repeats one
                    // named slot per layer it passes through) — aggregated by name.
                    ReadSlotCavity(sub, scale, conductorLayer: layerRef, spanFrom: "", spanTo: "");
                    break;
                case "LineDesc" or "FillDesc" or "ColorRef":
                    break;                                       // style-only elements
                case "NonstandardAttribute" or "Textual":
                    // Metadata, not geometry: NonstandardAttribute is a name/value tag
                    // (Cadence attaches tens of thousands), Textual is font text with no
                    // outline geometry. Consumed so their children never reach this
                    // dispatch — and silently, they declare nothing the model loses.
                    Consume(sub);
                    break;
                default:
                    _diag.Warn($"IPC-2581: unsupported feature <{sub.LocalName}> on layer " +
                                  $"'{layerRef}'{PolyShapeReader.LinePosition(sub)}; skipped.");
                    Consume(sub);
                    break;
            }
        }
    }

    private void FlashInline(XmlReader reader, double scale, string layerRef, Ipc2581Net net,
        Point2? location, double rotation, bool mirror)
    {
        if (location is null)
        {
            _diag.Warn($"IPC-2581: <{reader.LocalName}> flash on '{layerRef}' has no preceding " +
                          "Location; skipped.");
            return;
        }
        var primitive = Ipc2581PrimitiveDictionary.TryReadPrimitive(reader, scale, _diag);

        // An inline primitive may carry its own style children (KiCad user figures write
        // <Circle><FillDesc fillProperty="HOLLOW"/><LineDesc/></Circle>): HOLLOW turns
        // the shape from a filled pad into a stroked outline. Contour is the one
        // primitive whose reader already consumed the subtree (styles included).
        var style = new RingStyle();
        if (primitive is not Ipc2581PrimitiveDictionary.ContourPrimitive && !reader.IsEmptyElement)
        {
            using var sub = reader.ReadSubtree();
            sub.Read();
            while (sub.Read())
                if (sub.NodeType == XmlNodeType.Element)
                    CaptureRingStyle(sub, scale, layerRef, style);
        }
        if (primitive is null) return;

        var pieces = Ipc2581PrimitiveDictionary.Flash(primitive, location.Value, rotation, mirror);
        if (style.FillProperty.Equals("HOLLOW", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var piece in pieces)
            {
                StrokeClosedRing(layerRef, net, piece.Outer, style.StrokeWidth);
                foreach (var hole in piece.Holes)
                    StrokeClosedRing(layerRef, net, hole, style.StrokeWidth);
            }
            return;
        }
        foreach (var piece in pieces)
            net.Pads.Add(new Ipc2581PadFlash(layerRef, location.Value, piece));
    }

    private void FlashRef(XmlReader reader, string layerRef, Ipc2581Net net,
        Point2? location, double rotation, bool mirror)
    {
        string id = reader.GetAttribute("id") ?? "";
        if (location is null)
        {
            _diag.Warn($"IPC-2581: StandardPrimitiveRef '{id}' on '{layerRef}' has no preceding " +
                          "Location; skipped.");
            return;
        }
        var shapes = _dictionary.Flash(id, location.Value, rotation, _diag, mirror);
        if (shapes is null) return;
        foreach (var piece in shapes)
            net.Pads.Add(new Ipc2581PadFlash(layerRef, location.Value, piece));
    }

    /// <summary>
    /// A KiCad-style <c>Pad</c> instance inside a conductor Set: its Location/Xform and
    /// primitive ref are self-contained (unlike bare flashes, which inherit the running
    /// Set state), so it reuses the LayerPad body reader. A <c>PinRef</c> child carries
    /// the component pin the flash belongs to — captured for pad naming.
    /// </summary>
    private void ReadPadInstance(XmlReader reader, double scale, string layerRef, Ipc2581Net net)
    {
        var (location, rotation, mirror, primitiveRef, inline, componentRef, pin) =
            ReadFlashBody(reader, scale);
        if (location is null)
        {
            _diag.Warn($"IPC-2581: Pad on '{layerRef}' has no Location" +
                          $"{PolyShapeReader.LinePosition(reader)}; pad skipped.");
            return;
        }
        var shapes = ResolveFlash(primitiveRef, inline, location.Value, rotation, mirror);
        if (shapes is null) return;
        foreach (var piece in shapes)
            net.Pads.Add(new Ipc2581PadFlash(layerRef, location.Value, piece, componentRef, pin));
    }

    /// <summary>Reads a <c>Component</c> element: the identity attributes (refdes and the
    /// part / footprint-package names its pads inherit) plus the placement (side,
    /// location, rotation, mirror) — the transform the test-side oracle instantiates
    /// LandPattern pads under to pin the mirror convention empirically.</summary>
    private void ReadComponent(XmlReader reader)
    {
        string? refDes = reader.GetAttribute("refDes");
        if (refDes is null)
        {
            Consume(reader);
            return;
        }
        string? packageRef = reader.GetAttribute("packageRef");
        string? part = reader.GetAttribute("part");
        string? layerRef = reader.GetAttribute("layerRef");

        double scale = GeometryScale();
        Point2? location = null;
        double rotation = 0;
        bool mirror = false;
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
                    mirror = string.Equals(sub.GetAttribute("mirror"), "true",
                        StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }
        _components.Add(new Ipc2581Component(refDes, packageRef, part)
        {
            LayerRef = layerRef,
            Location = location,
            RotationDeg = rotation,
            Mirror = mirror,
        });
    }

    /// <summary>
    /// Captures a <c>Package</c>'s LandPattern pad list (pin → local placement +
    /// primitive ref). The pads are NOT flashed into copper — the conductor
    /// LayerFeatures already carry every placed pad — but the list feeds the model
    /// (and the transform oracle that instantiates it under each Component placement).
    /// Outline/Marking/AssemblyDrawing children are documentation and stream past.
    /// </summary>
    private void ReadPackage(XmlReader reader)
    {
        string? name = reader.GetAttribute("name");
        if (name is null)
        {
            Consume(reader);
            return;
        }
        double scale = GeometryScale();
        var pads = new List<Ipc2581PackagePad>();
        using var sub = reader.ReadSubtree();
        sub.Read();
        bool inLandPattern = false;
        while (sub.Read())
        {
            if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "LandPattern")
                inLandPattern = true;
            else if (sub.NodeType == XmlNodeType.EndElement && sub.LocalName == "LandPattern")
                inLandPattern = false;
            else if (sub.NodeType == XmlNodeType.Element && inLandPattern && sub.LocalName == "Pad")
            {
                var (location, rotation, mirror, primitiveRef, _, _, pin) = ReadFlashBody(sub, scale);
                if (location is not null)
                    pads.Add(new Ipc2581PackagePad(pin, location.Value, rotation, mirror, primitiveRef));
            }
        }
        _packages[name] = new Ipc2581Package(name, pads);
    }

    /// <summary>
    /// One <c>SlotCavity</c> occurrence: the outline is read once per NAME (repeats are
    /// the same slot passing through another layer — their conductor layers accumulate);
    /// a drill-layer occurrence contributes the drill span instead of a layer.
    /// </summary>
    private void ReadSlotCavity(XmlReader reader, double scale, string? conductorLayer,
        string spanFrom, string spanTo)
    {
        string name = reader.GetAttribute("name") ?? $"slot@{PolyShapeReader.LinePosition(reader)}";
        bool plated = string.Equals(reader.GetAttribute("platingStatus"), "PLATED",
            StringComparison.OrdinalIgnoreCase);

        if (!_slots.TryGetValue(name, out var slot))
        {
            _slots[name] = slot = new SlotAccumulator();
            _slotOrder.Add(name);
        }
        slot.Plated |= plated;
        if (conductorLayer is not null && !slot.ConductorLayers.Contains(conductorLayer))
            slot.ConductorLayers.Add(conductorLayer);
        if (spanFrom.Length > 0) (slot.SpanFrom, slot.SpanTo) = (spanFrom, spanTo);

        if (slot.Outline is not null)
        {
            Consume(reader);                                     // repeat occurrence
            return;
        }
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "Polygon") continue;
            slot.Outline = PolyShapeReader.ReadRing(sub, scale, _diag);
            break;
        }
        if (slot.Outline is null)
            _diag.Warn($"IPC-2581: SlotCavity '{name}' has no readable Outline polygon; " +
                       "the slot is ignored.");
    }

    private IReadOnlyList<Ipc2581SlotCavity> BuildSlots() =>
        _slotOrder
            .Select(name => (Name: name, Slot: _slots[name]))
            .Where(s => s.Slot.Outline is not null)
            .Select(s => new Ipc2581SlotCavity(s.Name, s.Slot.Plated, s.Slot.Outline!,
                s.Slot.ConductorLayers, s.Slot.SpanFrom, s.Slot.SpanTo))
            .ToList();

    /// <summary>
    /// Parses <c>DictionaryUser</c>: each <c>EntryUser</c> body is read by the SAME
    /// feature reader the conductor Sets use, into a scratch net with an empty layer
    /// ref — local coordinates, later placed by <c>UserPrimitiveRef</c>.
    /// </summary>
    private void ReadDictionaryUser(XmlReader reader)
    {
        double scale = ReadUnits(reader) ?? _scale;
        if (scale <= 0)
            throw new InvalidDataException(
                "IPC-2581: DictionaryUser has no readable units and no CadHeader units " +
                "were seen before it; cannot convert user-figure geometry.");

        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "EntryUser") continue;
            string? id = sub.GetAttribute("id");
            if (string.IsNullOrEmpty(id))
            {
                _diag.Warn($"IPC-2581: EntryUser without an id skipped{PolyShapeReader.LinePosition(sub)}.");
                continue;
            }
            var scratch = new Ipc2581Net { Name = id };
            ReadFeatureContainer(sub, scale, layerRef: "", scratch, new Point2(0, 0));
            _userDictionary.Add(id, scratch);
        }
    }

    private (Point2? Location, double Rotation, bool Mirror, string? PrimitiveRef,
        Ipc2581PrimitiveDictionary.Primitive? Inline, string? ComponentRef, string? Pin)
        ReadFlashBody(XmlReader reader, double scale)
    {
        Point2? location = null;
        double rotation = 0;
        bool mirror = false;
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
                    mirror = string.Equals(sub.GetAttribute("mirror"), "true",
                        StringComparison.OrdinalIgnoreCase);
                    break;
                case "StandardPrimitiveRef":
                    primitiveRef = sub.GetAttribute("id");
                    break;
                case "PinRef":
                    componentRef = sub.GetAttribute("componentRef");
                    pin = sub.GetAttribute("pin");
                    break;
                default:
                    inline = Ipc2581PrimitiveDictionary.TryReadPrimitive(sub, scale, _diag) ?? inline;
                    break;
            }
        }
        return (location, rotation, mirror, primitiveRef, inline, componentRef, pin);
    }

    private IReadOnlyList<Polygon2>? ResolveFlash(string? primitiveRef,
        Ipc2581PrimitiveDictionary.Primitive? inline, Point2 location, double rotation, bool mirror)
    {
        if (primitiveRef is not null)
            return _dictionary.Flash(primitiveRef, location, rotation, _diag, mirror);
        if (inline is not null)
            return Ipc2581PrimitiveDictionary.Flash(inline, location, rotation, mirror);
        _diag.Warn("IPC-2581: pad flash has neither a StandardPrimitiveRef nor an inline " +
                      "primitive; skipped.");
        return null;
    }

    /// <summary>The fill/stroke style captured off a ring's child refs (Cadence nests
    /// FillDescRef/LineDescRef INSIDE the Polygon element).</summary>
    private sealed class RingStyle
    {
        public string FillProperty = "FILL";
        public double StrokeWidth;
    }

    private void CaptureRingStyle(XmlReader sub, double scale, string layerRef, RingStyle style)
    {
        switch (sub.LocalName)
        {
            case "FillDesc":
                style.FillProperty = sub.GetAttribute("fillProperty") ?? style.FillProperty;
                break;
            case "FillDescRef":
                string? id = sub.GetAttribute("id");
                if (id is not null) style.FillProperty = _styles.FillProperty(id, _diag, layerRef);
                break;
            default:
                double w = style.StrokeWidth;
                if (TryReadLineStyle(sub, scale, layerRef, ref w)) style.StrokeWidth = w;
                break;
        }
    }

    /// <summary>
    /// Deposits one styled ring as copper. FILL (the default) pours the enclosed region;
    /// <b>HOLLOW strokes the CLOSED ring outline</b> at its LineDesc width — filling a
    /// hollow ring would fabricate copper across the enclosed area (a short hazard);
    /// VOID declares no copper at all. HATCH/MESH pour patterns are filled solid with a
    /// warning (a conservative approximation of declared content).
    /// </summary>
    private void AddRingCopper(string layerRef, Ipc2581Net net, IReadOnlyList<Point2> ring,
        RingStyle style)
    {
        switch (style.FillProperty.ToUpperInvariant())
        {
            case "HOLLOW":
                StrokeClosedRing(layerRef, net, ring, style.StrokeWidth);
                break;
            case "VOID":
                _diag.Note($"IPC-2581: VOID ring on '{layerRef}' declares no copper; skipped.");
                break;
            case "HATCH" or "MESH":
                _diag.Warn($"IPC-2581: {style.FillProperty} fill on '{layerRef}' poured solid " +
                           "(hatch pattern not modeled).");
                net.Fills.Add(new Ipc2581Fill(layerRef, new Polygon2(ring)));
                break;
            default:
                net.Fills.Add(new Ipc2581Fill(layerRef, new Polygon2(ring)));
                break;
        }
    }

    private void StrokeClosedRing(string layerRef, Ipc2581Net net, IReadOnlyList<Point2> ring,
        double width)
    {
        if (double.IsNaN(width)) return;                         // unresolved ref — already warned
        if (width <= 0)
        {
            _diag.Note($"IPC-2581: zero-width trace on '{layerRef}' skipped (unmeshable copper).");
            return;
        }
        var path = new List<Point2>(ring.Count + 1);
        path.AddRange(ring);
        path.Add(ring[0]);                                       // close the outline stroke
        net.Traces.Add(new Ipc2581Trace(layerRef, path, width));
    }

    /// <summary>A Contour fill: one Polygon outer with Cutout holes → a copper pour
    /// region — unless the ring's style says HOLLOW, in which case every ring (outer and
    /// cutouts) is stroked as an outline instead.</summary>
    private void ReadContourFill(XmlReader reader, double scale, string layerRef, Ipc2581Net net)
    {
        IReadOnlyList<Point2>? outer = null;
        var holes = new List<IReadOnlyList<Point2>>();
        var style = new RingStyle();
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "Polygon")
                outer ??= PolyShapeReader.ReadRing(sub, scale, _diag,
                    other => CaptureRingStyle(other, scale, layerRef, style));
            else if (sub.LocalName == "Cutout")
            {
                var hole = PolyShapeReader.ReadRing(sub, scale, _diag);
                if (hole is not null) holes.Add(hole);
            }
            else
                CaptureRingStyle(sub, scale, layerRef, style);   // Contour-level style refs
        }
        if (outer is null) return;

        if (style.FillProperty.Equals("HOLLOW", StringComparison.OrdinalIgnoreCase))
        {
            StrokeClosedRing(layerRef, net, outer, style.StrokeWidth);
            foreach (var hole in holes)
                StrokeClosedRing(layerRef, net, hole, style.StrokeWidth);
            return;
        }
        if (style.FillProperty.Equals("VOID", StringComparison.OrdinalIgnoreCase))
        {
            _diag.Note($"IPC-2581: VOID ring on '{layerRef}' declares no copper; skipped.");
            return;
        }
        if (style.FillProperty.ToUpperInvariant() is "HATCH" or "MESH")
            _diag.Warn($"IPC-2581: {style.FillProperty} fill on '{layerRef}' poured solid " +
                       "(hatch pattern not modeled).");
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
            _diag.Warn($"IPC-2581: Line on '{layerRef}' has malformed coordinates" +
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
            _diag.Warn($"IPC-2581: Arc on '{layerRef}' has malformed coordinates" +
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
    /// Reads the stroke width from a LineDesc/LineDescRef child. A zero/negative width on
    /// a conductor layer is unmeshable copper, so the trace is dropped with a note
    /// (widthOk = false). Consumes the element's children, so call it after reading the
    /// element's attributes.
    /// </summary>
    private double ReadLineWidth(XmlReader reader, double scale, string layerRef, out bool widthOk)
    {
        double width = 0;
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            TryReadLineStyle(sub, scale, layerRef, ref width);
        }
        widthOk = width > 0;
        if (!widthOk && !double.IsNaN(width))
            // A zero-width stroke declares no copper AREA — dropping it loses nothing
            // physical, so this is informational, not a skipped-content warning.
            // (An unresolved LineDescRef already carries its own warning — NaN.)
            _diag.Note($"IPC-2581: zero-width trace on '{layerRef}' skipped (unmeshable copper).");
        return width;
    }

    /// <summary>
    /// Handles a stroke-style child: an inline <c>LineDesc</c> or a <c>LineDescRef</c>
    /// into the style dictionary (the Cadence dialect routes ALL widths through refs).
    /// Returns true when the element was a style element. An unresolved ref is a warning
    /// — the width stays unresolved and the caller drops the stroke.
    /// </summary>
    private bool TryReadLineStyle(XmlReader sub, double scale, string layerRef, ref double width)
    {
        switch (sub.LocalName)
        {
            case "LineDesc":
                PolyShapeReader.TryAttr(sub, "lineWidth", scale, out width);
                WarnNonRoundCaps(sub.GetAttribute("lineEnd"), layerRef);
                return true;
            case "LineDescRef":
                string id = sub.GetAttribute("id") ?? "";
                if (_styles.TryGetLineDesc(id, out double w, out string? end))
                {
                    width = w;
                    WarnNonRoundCaps(end, layerRef);
                }
                else
                {
                    _diag.Warn($"IPC-2581: LineDescRef '{id}' on '{layerRef}' not found in the " +
                               "dictionary; the stroke width is unresolved and the draw is skipped.");
                    width = double.NaN;                          // already warned — no extra note
                }
                return true;
            default:
                return false;
        }
    }

    private void WarnNonRoundCaps(string? lineEnd, string layerRef)
    {
        if (lineEnd is not null && !lineEnd.Equals("ROUND", StringComparison.OrdinalIgnoreCase)
                                && !lineEnd.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            _diag.Warn($"IPC-2581: lineEnd '{lineEnd}' on '{layerRef}' stroked with round caps.");
    }

    /// <summary>
    /// A <c>Polyline</c> draw: an OPEN stroked path (PolyBegin + steps) — the construct
    /// Cadence routes nearly all copper traces through (66k+ on a real board), with the
    /// width resolved from the LineDesc/LineDescRef child. Arc steps tessellate exactly
    /// like ring arcs; the path lands in <see cref="Ipc2581Net.Traces"/>, so centerlines
    /// flow to the PEEC estimator through the same collection as Line/Arc draws.
    /// </summary>
    private void ReadPolyline(XmlReader reader, double scale, string layerRef, Ipc2581Net net)
    {
        var path = new List<Point2>();
        double width = 0;
        bool valid = true;

        using (var sub = reader.ReadSubtree())
        {
            sub.Read();
            while (sub.Read())
            {
                if (sub.NodeType != XmlNodeType.Element) continue;
                switch (sub.LocalName)
                {
                    case "PolyBegin" or "PolyStepSegment":
                        if (PolyShapeReader.TryAttr(sub, "x", scale, out double x)
                            && PolyShapeReader.TryAttr(sub, "y", scale, out double y))
                            path.Add(new Point2(x, y));
                        else valid = false;
                        break;
                    case "PolyStepCurve":
                        if (path.Count > 0
                            && PolyShapeReader.TryAttr(sub, "x", scale, out double ex)
                            && PolyShapeReader.TryAttr(sub, "y", scale, out double ey)
                            && PolyShapeReader.TryAttr(sub, "centerX", scale, out double cx)
                            && PolyShapeReader.TryAttr(sub, "centerY", scale, out double cy))
                        {
                            bool clockwise = string.Equals(sub.GetAttribute("clockwise"), "true",
                                StringComparison.OrdinalIgnoreCase);
                            PolyShapeReader.AppendArc(path, path[^1], new Point2(ex, ey),
                                new Point2(cx, cy), clockwise);
                        }
                        else valid = false;
                        break;
                    default:
                        TryReadLineStyle(sub, scale, layerRef, ref width);
                        break;
                }
            }
        }

        if (!valid)
        {
            _diag.Warn($"IPC-2581: Polyline on '{layerRef}' has malformed coordinates" +
                       $"{PolyShapeReader.LinePosition(reader)}; skipped.");
            return;
        }
        if (double.IsNaN(width)) return;                         // unresolved ref — already warned
        if (width <= 0)
        {
            _diag.Note($"IPC-2581: zero-width trace on '{layerRef}' skipped (unmeshable copper).");
            return;
        }
        if (path.Count >= 2)
            net.Traces.Add(new Ipc2581Trace(layerRef, path, width));
    }
}
