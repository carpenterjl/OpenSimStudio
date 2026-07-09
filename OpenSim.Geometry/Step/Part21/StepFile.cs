namespace OpenSim.Geometry.Step.Part21;

/// <summary>
/// A parsed Part 21 exchange structure: the header fields the importer cares about plus
/// the DATA-section instance table. The table is a <see cref="SortedDictionary{TKey,TValue}"/>
/// so every enumeration walks instances in ascending #id order — the determinism backbone
/// for face/edge numbering and therefore for reproducible meshes.
/// </summary>
public sealed class StepFile
{
    /// <summary>Schema name from FILE_SCHEMA with the version braces stripped, e.g. "AUTOMOTIVE_DESIGN". Informational only — never gated on.</summary>
    public string? SchemaName { get; }

    /// <summary>Model name from FILE_NAME.</summary>
    public string? ModelName { get; }

    /// <summary>Originating system from FILE_NAME (useful in log messages about exporter quirks).</summary>
    public string? OriginatingSystem { get; }

    /// <summary>All DATA-section instances keyed (and enumerated) by ascending #id.</summary>
    public SortedDictionary<int, StepInstance> Instances { get; }

    public StepFile(string? schemaName, string? modelName, string? originatingSystem,
        SortedDictionary<int, StepInstance> instances)
    {
        SchemaName = schemaName;
        ModelName = modelName;
        OriginatingSystem = originatingSystem;
        Instances = instances;
    }

    /// <summary>The instance <paramref name="id"/> refers to; throws naming the id when missing.</summary>
    public StepInstance Get(int id) => Instances.TryGetValue(id, out var inst)
        ? inst
        : throw new StepImportException($"instance #{id} is referenced but not defined in the file");
}
