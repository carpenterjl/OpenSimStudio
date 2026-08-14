using OpenSim.Geometry.Step.Part21;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// Reads the AP203/AP214 PRODUCT STRUCTURE and returns every solid occurrence placed in
/// the root frame.
/// <para>
/// The chain an assembly is written as:
/// PRODUCT ← PRODUCT_DEFINITION_FORMATION ← PRODUCT_DEFINITION ← PRODUCT_DEFINITION_SHAPE
/// → SHAPE_DEFINITION_REPRESENTATION → SHAPE_REPRESENTATION (which holds the solids).
/// Parent/child links are NEXT_ASSEMBLY_USAGE_OCCURRENCE records; each one's placement
/// arrives through its own PRODUCT_DEFINITION_SHAPE →
/// CONTEXT_DEPENDENT_SHAPE_REPRESENTATION → the complex
/// SHAPE_REPRESENTATION_RELATIONSHIP &amp;&amp; REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION →
/// ITEM_DEFINED_TRANSFORMATION, whose two AXIS2_PLACEMENT_3Ds give the frame pair.
/// </para>
/// <para>
/// A NAUO whose placement chain is missing or malformed is a HARD failure naming the #id:
/// falling back to the identity there would not be a degraded import, it would be a part
/// silently sitting in the wrong place — indistinguishable, in the viewport, from a
/// correct one. A file with no product structure at all is different, and is handled: the
/// solids are authored in the global frame, so identity IS their placement, and a note
/// says so.
/// </para>
/// Traversal is deterministic (roots by ascending PRODUCT_DEFINITION #id, children by
/// ascending NAUO #id), which fixes the body order — and therefore the region ids of the
/// merged assembly mesh.
/// </summary>
internal sealed class StepAssemblyResolver
{
    private readonly StepFile _file;
    private readonly StepEntityResolver _resolver;
    private readonly List<string> _notes;

    /// <summary>Guards against a cyclic NAUO graph (a corrupt file can describe an
    /// assembly that contains itself).</summary>
    private const int MaxDepth = 64;

    public StepAssemblyResolver(StepFile file, StepEntityResolver resolver, List<string> notes)
    {
        _file = file;
        _resolver = resolver;
        _notes = notes;
    }

    /// <summary>Every solid occurrence, in deterministic tree order.</summary>
    /// <param name="allSolidIds">Solid ids discovered by <see cref="StepEntityResolver.ResolveSolids"/>,
    /// used to spot solids the product structure never reaches.</param>
    public IReadOnlyList<StepPlacedSolid> Resolve(IReadOnlyList<int> allSolidIds)
    {
        var repSolids = MapRepresentationsToSolids(allSolidIds);
        var repLinks = MapRepresentationLinks();
        var definitionShape = MapProductDefinitionsToShapes();

        var placed = new List<StepPlacedSolid>();
        var used = new HashSet<int>();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);

        var children = MapAssemblyChildren(out var childDefinitions);
        var occurrencePlacements = MapOccurrencePlacements();
        var roots = definitionShape.Keys.Where(pd => !childDefinitions.Contains(pd)).OrderBy(id => id).ToList();

        if (roots.Count == 0)
        {
            // No product structure (or none we can anchor on): the solids are written in
            // the file's global frame, which IS their placement.
            foreach (int solidId in allSolidIds)
                placed.Add(new StepPlacedSolid(solidId, UniqueName(names, SolidName(solidId)),
                    StepRigidTransform.Identity, PlacedByAssembly: false));
            _notes.Add("no assembly structure found; solids import in the file's global frame");
            return placed;
        }

        foreach (int root in roots)
            Walk(root, StepRigidTransform.Identity, ProductName(root), 0);

        foreach (int solidId in allSolidIds)
        {
            if (used.Contains(solidId)) continue;
            // A solid the product structure never references. Its own coordinates are the
            // global frame, unlike a NAUO with a broken placement chain.
            placed.Add(new StepPlacedSolid(solidId, UniqueName(names, SolidName(solidId)),
                StepRigidTransform.Identity, PlacedByAssembly: false));
            _notes.Add($"solid #{solidId} is not referenced by the product structure; " +
                       "imported in the file's global frame");
        }
        return placed;

        void Walk(int productDefinition, StepRigidTransform toRoot, string occurrenceName, int depth)
        {
            if (depth > MaxDepth)
                throw new StepImportException(
                    $"#{productDefinition}: assembly nesting exceeds {MaxDepth} levels — the " +
                    "NEXT_ASSEMBLY_USAGE_OCCURRENCE graph is cyclic");

            if (definitionShape.TryGetValue(productDefinition, out int repId))
            {
                foreach (int solidId in SolidsOfRepresentation(repId, repSolids, repLinks))
                {
                    used.Add(solidId);
                    placed.Add(new StepPlacedSolid(solidId, UniqueName(names, occurrenceName),
                        toRoot, PlacedByAssembly: true));
                }
                // Instancing inside one representation (MAPPED_ITEM / REPRESENTATION_MAP).
                foreach (var (mappedRep, mapTransform, mapId) in MappedItems(repId))
                {
                    var composed = StepRigidTransform.Compose(toRoot, mapTransform);
                    foreach (int solidId in SolidsOfRepresentation(mappedRep, repSolids, repLinks))
                    {
                        used.Add(solidId);
                        placed.Add(new StepPlacedSolid(solidId, UniqueName(names, occurrenceName),
                            composed, PlacedByAssembly: true));
                    }
                    if (!repSolids.ContainsKey(mappedRep))
                        _notes.Add($"#{mapId}: mapped representation #{mappedRep} carries no solid");
                }
            }

            if (!children.TryGetValue(productDefinition, out var edges)) return;
            foreach (var edge in edges.OrderBy(e => e.NauoId))
            {
                var placement = PlacementOf(edge, definitionShape, occurrencePlacements);
                Walk(edge.Child, StepRigidTransform.Compose(toRoot, placement),
                    edge.Name ?? ProductName(edge.Child), depth + 1);
            }
        }
    }

    // ---------------- product structure ----------------

    private sealed record AssemblyEdge(int NauoId, int Parent, int Child, string? Name);

    private Dictionary<int, List<AssemblyEdge>> MapAssemblyChildren(out HashSet<int> childDefinitions)
    {
        var map = new Dictionary<int, List<AssemblyEdge>>();
        childDefinitions = new HashSet<int>();
        foreach (var (id, inst) in _file.Instances)
        {
            var rec = inst.Find("NEXT_ASSEMBLY_USAGE_OCCURRENCE");
            if (rec is null) continue;
            if (rec.Args.Count < 5)
                throw new StepImportException(
                    $"#{id} NEXT_ASSEMBLY_USAGE_OCCURRENCE has {rec.Args.Count} arguments (5 expected)");
            int parent = rec.Args[3].AsRef();
            int child = rec.Args[4].AsRef();
            string? designator = rec.Args.Count > 5 && rec.Args[5] is StepValue.Text t && t.Value.Length > 0
                ? t.Value
                : null;
            if (!map.TryGetValue(parent, out var list)) map[parent] = list = new List<AssemblyEdge>();
            list.Add(new AssemblyEdge(id, parent, child, designator));
            childDefinitions.Add(child);
        }
        return map;
    }

    /// <summary>PRODUCT_DEFINITION #id → the SHAPE_REPRESENTATION its shape resolves to.</summary>
    private Dictionary<int, int> MapProductDefinitionsToShapes()
    {
        var map = new Dictionary<int, int>();
        foreach (var (id, inst) in _file.Instances)
        {
            var sdr = inst.Find("SHAPE_DEFINITION_REPRESENTATION")
                      ?? inst.Find("PROPERTY_DEFINITION_REPRESENTATION");
            if (sdr is null || sdr.Args.Count < 2) continue;
            if (sdr.Args[0] is not StepValue.Reference definitionRef
                || sdr.Args[1] is not StepValue.Reference repRef) continue;

            var pds = _file.Get(definitionRef.Id).Find("PRODUCT_DEFINITION_SHAPE");
            if (pds is null || pds.Args.Count < 3 || pds.Args[2] is not StepValue.Reference target) continue;

            // The shape's definition is the PRODUCT_DEFINITION itself for a part/assembly
            // shape; for an OCCURRENCE shape it is the NAUO, which is the placement chain
            // handled separately.
            if (!_file.Get(target.Id).Has("PRODUCT_DEFINITION")) continue;
            if (!map.ContainsKey(target.Id)) map[target.Id] = repRef.Id;   // first by ascending #id wins
        }
        return map;
    }

    /// <summary>Representation #id → the solids listed directly among its items.</summary>
    private Dictionary<int, List<int>> MapRepresentationsToSolids(IReadOnlyList<int> allSolidIds)
    {
        var solids = new HashSet<int>(allSolidIds);
        var map = new Dictionary<int, List<int>>();
        foreach (var (id, inst) in _file.Instances)
        {
            var rep = FindRepresentation(inst);
            if (rep is null || rep.Args.Count < 2 || rep.Args[1] is not StepValue.ValueList items) continue;
            foreach (var item in items.Items)
            {
                if (item is not StepValue.Reference r || !solids.Contains(r.Id)) continue;
                if (!map.TryGetValue(id, out var list)) map[id] = list = new List<int>();
                if (!list.Contains(r.Id)) list.Add(r.Id);
            }
        }
        return map;
    }

    /// <summary>
    /// Representations tied together by a plain SHAPE_REPRESENTATION_RELATIONSHIP — no
    /// transformation, so the two are the same shape seen through two records. Exporters
    /// routinely put a part's placeholder SHAPE_REPRESENTATION on the product and the
    /// actual solid in a linked ADVANCED_BREP_SHAPE_REPRESENTATION; without following
    /// these links the part would import as empty.
    /// </summary>
    private Dictionary<int, List<int>> MapRepresentationLinks()
    {
        var links = new Dictionary<int, List<int>>();
        foreach (var (_, inst) in _file.Instances)
        {
            var rel = inst.Find("SHAPE_REPRESENTATION_RELATIONSHIP");
            if (rel is null) continue;
            if (inst.Has("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION")) continue;  // that one MOVES things
            var core = rel.Args.Count >= 4 ? rel : inst.Find("REPRESENTATION_RELATIONSHIP");
            if (core is null || core.Args.Count < 4) continue;
            if (core.Args[2] is not StepValue.Reference a || core.Args[3] is not StepValue.Reference b) continue;
            Add(a.Id, b.Id);
            Add(b.Id, a.Id);
        }
        return links;

        void Add(int from, int to)
        {
            if (!links.TryGetValue(from, out var list)) links[from] = list = new List<int>();
            if (!list.Contains(to)) list.Add(to);
        }
    }

    /// <summary>Solids of a representation and of everything linked to it without a
    /// transformation, in ascending #id order.</summary>
    private static IReadOnlyList<int> SolidsOfRepresentation(int repId,
        Dictionary<int, List<int>> repSolids, Dictionary<int, List<int>> repLinks)
    {
        var seen = new HashSet<int> { repId };
        var stack = new Stack<int>();
        stack.Push(repId);
        var result = new List<int>();
        while (stack.Count > 0)
        {
            int current = stack.Pop();
            if (repSolids.TryGetValue(current, out var list))
                foreach (int solid in list)
                    if (!result.Contains(solid)) result.Add(solid);
            if (!repLinks.TryGetValue(current, out var neighbours)) continue;
            foreach (int n in neighbours)
                if (seen.Add(n)) stack.Push(n);
        }
        result.Sort();
        return result;
    }

    /// <summary>MAPPED_ITEMs among a representation's items: an instanced sub-representation
    /// placed by its (origin, target) frame pair.</summary>
    private IEnumerable<(int MappedRep, StepRigidTransform Transform, int MapId)> MappedItems(int repId)
    {
        var rep = FindRepresentation(_file.Get(repId));
        if (rep is null || rep.Args.Count < 2 || rep.Args[1] is not StepValue.ValueList items) yield break;
        foreach (var item in items.Items)
        {
            if (item is not StepValue.Reference r) continue;
            var mapped = _file.Get(r.Id).Find("MAPPED_ITEM");
            if (mapped is null || mapped.Args.Count < 3) continue;
            var source = _file.Get(mapped.Args[1].AsRef());
            var repMap = source.Find("REPRESENTATION_MAP")
                         ?? throw new StepImportException(
                             $"#{r.Id} MAPPED_ITEM: mapping source #{source.Id} is not a REPRESENTATION_MAP");
            var origin = _resolver.Placement(repMap.Args[0].AsRef());
            var target = _resolver.Placement(mapped.Args[2].AsRef());
            yield return (repMap.Args[1].AsRef(), StepRigidTransform.FromFrames(origin, target, r.Id), r.Id);
        }
    }

    // ---------------- placement of one assembly edge ----------------

    /// <summary>NAUO #id → (the relationship instance that places it, the CDSR #id).
    /// Built once: an assembly with hundreds of occurrences would otherwise rescan the
    /// whole instance table per edge.</summary>
    private Dictionary<int, (int RelationId, int CdsrId)> MapOccurrencePlacements()
    {
        var map = new Dictionary<int, (int, int)>();
        foreach (var (id, inst) in _file.Instances)
        {
            var cdsr = inst.Find("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION");
            if (cdsr is null || cdsr.Args.Count < 2) continue;
            if (cdsr.Args[0] is not StepValue.Reference relation
                || cdsr.Args[1] is not StepValue.Reference pdsRef) continue;
            var pds = _file.Get(pdsRef.Id).Find("PRODUCT_DEFINITION_SHAPE");
            if (pds is null || pds.Args.Count < 3 || pds.Args[2] is not StepValue.Reference definition) continue;
            if (!map.ContainsKey(definition.Id)) map[definition.Id] = (relation.Id, id);
        }
        return map;
    }

    private StepRigidTransform PlacementOf(AssemblyEdge edge, Dictionary<int, int> definitionShape,
        Dictionary<int, (int RelationId, int CdsrId)> placements)
    {
        if (!placements.TryGetValue(edge.NauoId, out var placement))
            throw new StepImportException(
                $"#{edge.NauoId} NEXT_ASSEMBLY_USAGE_OCCURRENCE has no CONTEXT_DEPENDENT_SHAPE_REPRESENTATION — " +
                "the occurrence's placement is missing and cannot be guessed");
        return TransformOfRelation(_file.Get(placement.RelationId), edge, definitionShape, placement.CdsrId);
    }

    private StepRigidTransform TransformOfRelation(StepInstance relation, AssemblyEdge edge,
        Dictionary<int, int> definitionShape, int cdsrId)
    {
        var withTransform = relation.Find("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION")
                            ?? throw new StepImportException(
                                $"#{relation.Id}: occurrence #{edge.NauoId} is placed by a relationship " +
                                "without REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION");
        var core = relation.Find("REPRESENTATION_RELATIONSHIP")
                   ?? relation.Find("SHAPE_REPRESENTATION_RELATIONSHIP")
                   ?? throw new StepImportException(
                       $"#{relation.Id}: relationship for occurrence #{edge.NauoId} carries no " +
                       "REPRESENTATION_RELATIONSHIP record");
        if (core.Args.Count < 4)
            throw new StepImportException(
                $"#{relation.Id}: REPRESENTATION_RELATIONSHIP has {core.Args.Count} arguments (4 expected)");

        var operatorInst = _file.Get(withTransform.Args[0].AsRef());
        if (operatorInst.Has("CARTESIAN_TRANSFORMATION_OPERATOR_3D"))
            throw new StepUnsupportedEntityException(operatorInst.Id, "CARTESIAN_TRANSFORMATION_OPERATOR_3D",
                $"placement of occurrence #{edge.NauoId} (scaled placements are not supported)");
        var idt = operatorInst.Find("ITEM_DEFINED_TRANSFORMATION")
                  ?? throw new StepUnsupportedEntityException(operatorInst.Id, operatorInst.Keyword,
                      $"placement operator of occurrence #{edge.NauoId}");

        var frame1 = _resolver.Placement(idt.Args[2].AsRef());
        var frame2 = _resolver.Placement(idt.Args[3].AsRef());

        // rep_1 → rep_2 is the transform's direction. Which of the two is the CHILD decides
        // whether we use it as written or reversed; exporters differ, so this is read off
        // the file rather than assumed.
        int rep1 = core.Args[2].AsRef(), rep2 = core.Args[3].AsRef();
        bool haveChildRep = definitionShape.TryGetValue(edge.Child, out int childRep);
        bool childIsRep1 = haveChildRep && childRep == rep1;
        bool childIsRep2 = haveChildRep && childRep == rep2;
        if (!childIsRep1 && !childIsRep2)
            _notes.Add($"#{cdsrId}: occurrence #{edge.NauoId} relates representations #{rep1} → #{rep2}, " +
                       "neither of which is the child's own shape; taken as written");

        return childIsRep2
            ? StepRigidTransform.FromFrames(frame2, frame1, operatorInst.Id)
            : StepRigidTransform.FromFrames(frame1, frame2, operatorInst.Id);
    }

    // ---------------- names ----------------

    private static StepRecord? FindRepresentation(StepInstance inst) =>
        inst.Find("ADVANCED_BREP_SHAPE_REPRESENTATION")
        ?? inst.Find("MANIFOLD_SURFACE_SHAPE_REPRESENTATION")
        ?? inst.Find("GEOMETRICALLY_BOUNDED_WIREFRAME_SHAPE_REPRESENTATION")
        ?? inst.Find("SHAPE_REPRESENTATION");

    private string ProductName(int productDefinition)
    {
        var pd = _file.Get(productDefinition).Find("PRODUCT_DEFINITION");
        if (pd is null || pd.Args.Count < 3 || pd.Args[2] is not StepValue.Reference formationRef)
            return $"Product #{productDefinition}";
        var formation = _file.Get(formationRef.Id).Find("PRODUCT_DEFINITION_FORMATION");
        if (formation is null || formation.Args.Count < 3 || formation.Args[2] is not StepValue.Reference productRef)
            return $"Product #{productDefinition}";
        var product = _file.Get(productRef.Id).Find("PRODUCT");
        if (product is null) return $"Product #{productDefinition}";
        string? name = product.Args.Count > 1 && product.Args[1] is StepValue.Text n && n.Value.Length > 0
            ? n.Value
            : product.Args.Count > 0 && product.Args[0] is StepValue.Text i && i.Value.Length > 0
                ? i.Value
                : null;
        return name ?? $"Product #{productDefinition}";
    }

    private string SolidName(int solidId)
    {
        var inst = _file.Get(solidId);
        var rec = inst.Find("MANIFOLD_SOLID_BREP") ?? inst.Find("BREP_WITH_VOIDS");
        if (rec is not null && rec.Args.Count > 0 && rec.Args[0] is StepValue.Text t && t.Value.Length > 0)
            return t.Value;
        return $"Solid #{solidId}";
    }

    /// <summary>Two instances of the same part share a name; the second and later get a
    /// ":2", ":3"… suffix so the body list stays unambiguous.</summary>
    private static string UniqueName(Dictionary<string, int> seen, string name)
    {
        if (!seen.TryGetValue(name, out int count))
        {
            seen[name] = 1;
            return name;
        }
        seen[name] = ++count;
        return $"{name}:{count}";
    }
}
