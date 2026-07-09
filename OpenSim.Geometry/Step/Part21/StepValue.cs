namespace OpenSim.Geometry.Step.Part21;

/// <summary>
/// One parameter value in a Part 21 record — the tagged union of everything ISO 10303-21
/// allows in an argument position. The <c>AsXxx</c> accessors throw with the actual value
/// kind in the message so a schema-resolution mistake names what was found, not just what
/// was expected; callers add the owning #id.
/// </summary>
public abstract record StepValue
{
    /// <summary>Unset attribute (<c>$</c>).</summary>
    public sealed record Null : StepValue
    {
        public static readonly Null Instance = new();
        public override string ToString() => "$";
    }

    /// <summary>Attribute derived from a supertype (<c>*</c>).</summary>
    public sealed record Derived : StepValue
    {
        public static readonly Derived Instance = new();
        public override string ToString() => "*";
    }

    public sealed record Integer(long Value) : StepValue
    {
        public override string ToString() => Value.ToString();
    }

    public sealed record Real(double Value) : StepValue
    {
        public override string ToString() => Value.ToString("R");
    }

    public sealed record Text(string Value) : StepValue
    {
        public override string ToString() => $"'{Value}'";
    }

    /// <summary>Enumeration literal; stored without the surrounding dots (".MILLI." → "MILLI").</summary>
    public sealed record Enumeration(string Value) : StepValue
    {
        public override string ToString() => $".{Value}.";
    }

    /// <summary>Reference to another instance (<c>#id</c>).</summary>
    public sealed record Reference(int Id) : StepValue
    {
        public override string ToString() => $"#{Id}";
    }

    public sealed record ValueList(IReadOnlyList<StepValue> Items) : StepValue
    {
        public override string ToString() => $"({Items.Count} items)";
    }

    /// <summary>Typed parameter, e.g. <c>LENGTH_MEASURE(25.4)</c>.</summary>
    public sealed record Typed(string Keyword, IReadOnlyList<StepValue> Args) : StepValue
    {
        public override string ToString() => $"{Keyword}(…)";
    }

    public bool IsNull => this is Null;

    /// <summary>Numeric value; Part 21 permits an integer literal where a real is expected.</summary>
    public double AsReal() => this switch
    {
        Real r => r.Value,
        Integer i => i.Value,
        _ => throw Mismatch("a real number")
    };

    public long AsInt() => this is Integer i ? i.Value : throw Mismatch("an integer");

    public string AsText() => this is Text t ? t.Value : throw Mismatch("a string");

    public string AsEnum() => this is Enumeration e ? e.Value : throw Mismatch("an enumeration");

    public int AsRef() => this is Reference r ? r.Id : throw Mismatch("an instance reference");

    public IReadOnlyList<StepValue> AsList() =>
        this is ValueList l ? l.Items : throw Mismatch("a list");

    private StepImportException Mismatch(string expected) =>
        new($"expected {expected}, found {this}");
}
