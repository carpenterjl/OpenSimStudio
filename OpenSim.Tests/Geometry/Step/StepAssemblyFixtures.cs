namespace OpenSim.Tests.Geometry.Step;

/// <summary>
/// Programmatic AP214 ASSEMBLY fixtures: the product structure
/// (PRODUCT → PRODUCT_DEFINITION → PRODUCT_DEFINITION_SHAPE → SHAPE_DEFINITION_REPRESENTATION)
/// plus NEXT_ASSEMBLY_USAGE_OCCURRENCE occurrences placed through
/// CONTEXT_DEPENDENT_SHAPE_REPRESENTATION → ITEM_DEFINED_TRANSFORMATION, exactly as real
/// exporters write it. Every part is authored at its own origin so the PLACEMENT is the
/// only thing that moves it — a resolver that ignored transforms would stack the parts.
/// </summary>
internal static class StepAssemblyFixtures
{
    /// <summary>A placement frame in the parent representation: origin plus the Z and X
    /// direction cosines (axis-aligned values keep the resulting rotation exact).</summary>
    internal readonly record struct Frame(
        (double x, double y, double z) Origin,
        (double x, double y, double z) ZAxis,
        (double x, double y, double z) XAxis)
    {
        public static Frame At(double x, double y, double z) =>
            new((x, y, z), (0, 0, 1), (1, 0, 0));

        /// <summary>Rotation by +90° about Z (X→Y, Y→−X) followed by the translation.</summary>
        public static Frame RotatedZ90(double x, double y, double z) =>
            new((x, y, z), (0, 0, 1), (0, 1, 0));
    }

    /// <summary>Emits APPLICATION_CONTEXT + the two definition contexts once per file.</summary>
    private static (int Product, int Definition) Contexts(StepFixtures.Builder b)
    {
        int app = b.Add($"APPLICATION_CONTEXT('automotive design')");
        int product = b.Add($"PRODUCT_CONTEXT('',#{app},'mechanical')");
        int definition = b.Add($"PRODUCT_DEFINITION_CONTEXT('part definition',#{app},'design')");
        return (product, definition);
    }

    /// <summary>PRODUCT … SHAPE_DEFINITION_REPRESENTATION for one product, given the
    /// representation that holds (or stands for) its shape.</summary>
    private static int EmitProduct(StepFixtures.Builder b, (int Product, int Definition) contexts,
        string id, string name, int representation)
    {
        int product = b.Add($"PRODUCT('{id}','{name}','',(#{contexts.Product}))");
        int formation = b.Add($"PRODUCT_DEFINITION_FORMATION('','',#{product})");
        int definition = b.Add($"PRODUCT_DEFINITION('design','',#{formation},#{contexts.Definition})");
        int shape = b.Add($"PRODUCT_DEFINITION_SHAPE('','',#{definition})");
        b.Add($"SHAPE_DEFINITION_REPRESENTATION(#{shape},#{representation})");
        return definition;
    }

    /// <summary>An assembly node: a representation holding only its own origin frame.</summary>
    private static (int Definition, int Representation) EmitAssemblyNode(StepFixtures.Builder b,
        (int Product, int Definition) contexts, int unitContext, string name)
    {
        int axis = b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0));
        int rep = b.Add($"SHAPE_REPRESENTATION('{name}',(#{axis}),#{unitContext})");
        return (EmitProduct(b, contexts, name, name, rep), rep);
    }

    /// <summary>A leaf part: a box authored at the local origin inside its own
    /// ADVANCED_BREP_SHAPE_REPRESENTATION.</summary>
    private static (int Definition, int Representation) EmitBoxPart(StepFixtures.Builder b,
        (int Product, int Definition) contexts, int unitContext, string name,
        double dx, double dy, double dz)
    {
        int solid = StepFixtures.EmitBox(b, (0, 0, 0), dx, dy, dz);
        int rep = b.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION('{name}',(#{solid}),#{unitContext})");
        return (EmitProduct(b, contexts, name, name, rep), rep);
    }

    /// <summary>One occurrence: the NAUO plus the placement chain that carries the child's
    /// origin frame onto <paramref name="target"/> in the parent's representation.</summary>
    private static void EmitOccurrence(StepFixtures.Builder b, int parentDefinition, int parentRep,
        int childDefinition, int childRep, string? designator, Frame target, bool withPlacement = true)
    {
        string tag = designator is null ? "$" : $"'{designator}'";
        int nauo = b.Add(
            $"NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','','',#{parentDefinition},#{childDefinition},{tag})");
        if (!withPlacement) return;

        int shape = b.Add($"PRODUCT_DEFINITION_SHAPE('','',#{nauo})");
        int origin = b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0));
        int placed = b.Placement(b.Point(target.Origin.x, target.Origin.y, target.Origin.z),
            target.ZAxis, target.XAxis);
        int transform = b.Add($"ITEM_DEFINED_TRANSFORMATION('','',#{origin},#{placed})");
        int relation = b.Add(
            $"(REPRESENTATION_RELATIONSHIP('','',#{childRep},#{parentRep})REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#{transform})SHAPE_REPRESENTATION_RELATIONSHIP())");
        b.Add($"CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#{relation},#{shape})");
    }

    /// <summary>An assembly of one box part per entry, each placed by its own frame.</summary>
    public static string Boxes(StepFixtures.Unit unit, params (string Name, double Size, Frame Where)[] parts)
    {
        var b = new StepFixtures.Builder();
        int unitContext = b.AddUnits(unit);
        var contexts = Contexts(b);
        var root = EmitAssemblyNode(b, contexts, unitContext, "Assembly");
        foreach (var (name, size, where) in parts)
        {
            var part = EmitBoxPart(b, contexts, unitContext, name, size, size, size);
            EmitOccurrence(b, root.Definition, root.Representation, part.Definition, part.Representation,
                designator: null, where);
        }
        return b.Render();
    }

    /// <summary>One 1×2×3 mm box placed by <paramref name="where"/> — the placement gate's
    /// fixture: the same topology under two different frames.</summary>
    public static string SingleBox(Frame where, StepFixtures.Unit unit = StepFixtures.Unit.Millimetre)
    {
        var b = new StepFixtures.Builder();
        int unitContext = b.AddUnits(unit);
        var contexts = Contexts(b);
        var root = EmitAssemblyNode(b, contexts, unitContext, "Assembly");
        var part = EmitBoxPart(b, contexts, unitContext, "Block", 1, 2, 3);
        EmitOccurrence(b, root.Definition, root.Representation, part.Definition, part.Representation,
            designator: null, where);
        return b.Render();
    }

    /// <summary>The SAME part used <paramref name="count"/> times, 10 mm apart in x.</summary>
    public static string InstancedPart(int count)
    {
        var b = new StepFixtures.Builder();
        int unitContext = b.AddUnits(StepFixtures.Unit.Millimetre);
        var contexts = Contexts(b);
        var root = EmitAssemblyNode(b, contexts, unitContext, "Assembly");
        var part = EmitBoxPart(b, contexts, unitContext, "Screw", 1, 1, 1);
        for (int i = 0; i < count; i++)
            EmitOccurrence(b, root.Definition, root.Representation, part.Definition, part.Representation,
                designator: null, Frame.At(10 * i, 0, 0));
        return b.Render();
    }

    /// <summary>Root → sub-assembly at <paramref name="outer"/> → box at <paramref name="inner"/>.
    /// The box must land at outer ∘ inner.</summary>
    public static string Nested(Frame outer, Frame inner)
    {
        var b = new StepFixtures.Builder();
        int unitContext = b.AddUnits(StepFixtures.Unit.Millimetre);
        var contexts = Contexts(b);
        var root = EmitAssemblyNode(b, contexts, unitContext, "Assembly");
        var sub = EmitAssemblyNode(b, contexts, unitContext, "Subassembly");
        var part = EmitBoxPart(b, contexts, unitContext, "Block", 1, 2, 3);
        EmitOccurrence(b, root.Definition, root.Representation, sub.Definition, sub.Representation,
            designator: "SUB", outer);
        EmitOccurrence(b, sub.Definition, sub.Representation, part.Definition, part.Representation,
            designator: "BLK", inner);
        return b.Render();
    }

    /// <summary>Two occurrences of the same part carrying reference designators.</summary>
    public static string WithDesignators(string first, string second)
    {
        var b = new StepFixtures.Builder();
        int unitContext = b.AddUnits(StepFixtures.Unit.Millimetre);
        var contexts = Contexts(b);
        var root = EmitAssemblyNode(b, contexts, unitContext, "Assembly");
        var part = EmitBoxPart(b, contexts, unitContext, "Block", 1, 1, 1);
        EmitOccurrence(b, root.Definition, root.Representation, part.Definition, part.Representation,
            first, Frame.At(0, 0, 0));
        EmitOccurrence(b, root.Definition, root.Representation, part.Definition, part.Representation,
            second, Frame.At(10, 0, 0));
        return b.Render();
    }

    /// <summary>An occurrence with NO placement chain — the file states the part is used
    /// but never where it goes.</summary>
    public static string MissingPlacement()
    {
        var b = new StepFixtures.Builder();
        int unitContext = b.AddUnits(StepFixtures.Unit.Millimetre);
        var contexts = Contexts(b);
        var root = EmitAssemblyNode(b, contexts, unitContext, "Assembly");
        var part = EmitBoxPart(b, contexts, unitContext, "Block", 1, 1, 1);
        EmitOccurrence(b, root.Definition, root.Representation, part.Definition, part.Representation,
            designator: null, Frame.At(0, 0, 0), withPlacement: false);
        return b.Render();
    }

    /// <summary>An occurrence placed by CARTESIAN_TRANSFORMATION_OPERATOR_3D, which may
    /// carry a SCALE and so is refused rather than approximated.</summary>
    public static string ScaledPlacement()
    {
        var b = new StepFixtures.Builder();
        int unitContext = b.AddUnits(StepFixtures.Unit.Millimetre);
        var contexts = Contexts(b);
        var root = EmitAssemblyNode(b, contexts, unitContext, "Assembly");
        var part = EmitBoxPart(b, contexts, unitContext, "Block", 1, 1, 1);
        int nauo = b.Add($"NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','','',#{root.Definition},#{part.Definition},$)");
        int shape = b.Add($"PRODUCT_DEFINITION_SHAPE('','',#{nauo})");
        int axis1 = b.Direction(1, 0, 0);
        int axis2 = b.Direction(0, 1, 0);
        int op = b.Add(
            $"CARTESIAN_TRANSFORMATION_OPERATOR_3D('','',#{axis1},#{axis2},#{b.Point(0, 0, 0)},2.0,$)");
        int relation = b.Add(
            $"(REPRESENTATION_RELATIONSHIP('','',#{part.Representation},#{root.Representation})REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#{op})SHAPE_REPRESENTATION_RELATIONSHIP())");
        b.Add($"CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#{relation},#{shape})");
        return b.Render();
    }

    /// <summary>
    /// A part whose product shape representation holds no geometry, linked to the
    /// ADVANCED_BREP_SHAPE_REPRESENTATION that does by a plain (transform-free)
    /// SHAPE_REPRESENTATION_RELATIONSHIP — the layout most exporters actually write.
    /// </summary>
    public static string LinkedRepresentation(Frame where)
    {
        var b = new StepFixtures.Builder();
        int unitContext = b.AddUnits(StepFixtures.Unit.Millimetre);
        var contexts = Contexts(b);
        var root = EmitAssemblyNode(b, contexts, unitContext, "Assembly");

        int axis = b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0));
        int placeholder = b.Add($"SHAPE_REPRESENTATION('Block',(#{axis}),#{unitContext})");
        int definition = EmitProduct(b, contexts, "Block", "Block", placeholder);
        int solid = StepFixtures.EmitBox(b, (0, 0, 0), 1, 2, 3);
        int brep = b.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION('Block',(#{solid}),#{unitContext})");
        b.Add($"SHAPE_REPRESENTATION_RELATIONSHIP('','',#{placeholder},#{brep})");

        EmitOccurrence(b, root.Definition, root.Representation, definition, placeholder,
            designator: null, where);
        return b.Render();
    }

    /// <summary>A representation that instances another through MAPPED_ITEM /
    /// REPRESENTATION_MAP rather than through the assembly tree.</summary>
    public static string MappedItem(Frame where)
    {
        var b = new StepFixtures.Builder();
        int unitContext = b.AddUnits(StepFixtures.Unit.Millimetre);
        var contexts = Contexts(b);

        int solid = StepFixtures.EmitBox(b, (0, 0, 0), 1, 2, 3);
        int mappedRep = b.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION('Block',(#{solid}),#{unitContext})");
        int mapOrigin = b.Placement(b.Point(0, 0, 0), (0, 0, 1), (1, 0, 0));
        int map = b.Add($"REPRESENTATION_MAP(#{mapOrigin},#{mappedRep})");
        int target = b.Placement(b.Point(where.Origin.x, where.Origin.y, where.Origin.z),
            where.ZAxis, where.XAxis);
        int item = b.Add($"MAPPED_ITEM('',#{map},#{target})");
        int rootRep = b.Add($"SHAPE_REPRESENTATION('Assembly',(#{item}),#{unitContext})");
        EmitProduct(b, contexts, "Assembly", "Assembly", rootRep);
        return b.Render();
    }
}
