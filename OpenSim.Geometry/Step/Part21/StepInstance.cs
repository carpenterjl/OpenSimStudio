namespace OpenSim.Geometry.Step.Part21;

/// <summary>One typed record inside an instance: keyword plus its argument list.</summary>
public sealed record StepRecord(string Keyword, IReadOnlyList<StepValue> Args);

/// <summary>
/// One <c>#id = …;</c> entry from the DATA section. A simple instance holds exactly one
/// record; a complex (multi-type) instance <c>#5=(A(…) B(…) C(…));</c> holds several.
/// Complex instances are first-class because real exporters use them for rational
/// B-splines and unit contexts — resolvers must look records up by keyword and never
/// assume their order.
/// </summary>
public sealed record StepInstance(int Id, IReadOnlyList<StepRecord> Records, int Line)
{
    /// <summary>True when this is a complex (multi-record) instance.</summary>
    public bool IsComplex => Records.Count > 1;

    /// <summary>The single record's keyword for a simple instance; the first keyword otherwise.</summary>
    public string Keyword => Records[0].Keyword;

    /// <summary>The record with the given keyword, or null when this instance does not carry it.</summary>
    public StepRecord? Find(string keyword)
    {
        foreach (var r in Records)
            if (r.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>True when the instance carries a record with the given keyword.</summary>
    public bool Has(string keyword) => Find(keyword) is not null;

    /// <summary>
    /// The single record of a simple instance; throws (naming the #id) when called on a
    /// complex instance, where record order must not be assumed.
    /// </summary>
    public StepRecord Single() => Records.Count == 1
        ? Records[0]
        : throw new StepImportException(
            $"#{Id} is a complex instance ({Records.Count} records); look the record up by keyword");
}
