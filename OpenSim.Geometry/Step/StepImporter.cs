using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Geometry.Step.Part21;
using OpenSim.Geometry.Step.Schema;
using OpenSim.Geometry.Step.Tessellate;

namespace OpenSim.Geometry.Step;

/// <summary>
/// First-party STEP (ISO 10303-21, AP203/AP214/AP242 B-rep core) importer. Produces a
/// welded, outward-oriented <see cref="TriangleMesh"/> whose face ids are the NATIVE STEP
/// faces. Watertightness is achieved by construction: every EDGE_CURVE is sampled exactly
/// once and both adjacent faces consume the identical 3D points. Everything the importer
/// decides on the user's behalf (largest of several solids, ignored assembly transforms,
/// orientation repair) is reported through <see cref="StepImportReport.Notes"/>.
/// </summary>
public sealed class StepImporter : IGeometryImporter
{
    private readonly StepImportOptions _options;

    public StepImporter(StepImportOptions? options = null) => _options = options ?? StepImportOptions.Default;

    public string FormatName => "STEP";

    public IReadOnlyList<string> FileExtensions { get; } = new[] { ".step", ".stp" };

    public TriangleMesh Import(string filePath) => ImportWithNotes(filePath).Mesh;

    /// <summary>Imports and returns the mesh together with the advisory notes for the log panel.</summary>
    public StepImportReport ImportWithNotes(string filePath) => ImportText(File.ReadAllText(filePath));

    /// <summary>Import from STEP text (the file-less entry point tests drive).</summary>
    public StepImportReport ImportText(string text)
    {
        var notes = new List<string>();
        var file = Part21Parser.Parse(text);
        var units = StepUnits.Resolve(file);
        string unitNote = FormattableString.Invariant($"length unit: {units.MetersPerUnit} m per model unit");
        if (units.UncertaintyMeters is double u)
            unitNote += FormattableString.Invariant($", stated accuracy {u} m");
        notes.Add(unitNote);

        var resolver = new StepEntityResolver(file, units.MetersPerUnit);
        if (resolver.HasAssemblyTransforms())
            notes.Add("assembly placement transforms are ignored in v1; solids import in their local frames");

        var solids = resolver.ResolveSolids();
        TriangleMesh? bestMesh = null;
        double bestVolume = double.MinValue;
        int bestId = 0;
        foreach (var solid in solids)
        {
            var (mesh, volume) = SolidTessellator.Tessellate(solid, _options, notes, units.UncertaintyMeters);
            if (bestMesh is null || volume > bestVolume)
            {
                bestMesh = mesh;
                bestVolume = volume;
                bestId = solid.Id;
            }
        }
        if (bestMesh is null)
            throw new StepGeometryException("no solid could be tessellated"); // unreachable: ResolveSolids throws first
        if (solids.Count > 1)
            notes.Add($"file contains {solids.Count} solids; imported the largest (#{bestId}) — " +
                      "multi-body assemblies are planned for Phase 4");

        notes.Add($"solid #{bestId}: {bestMesh.Vertices.Count} vertices, " +
                  $"{bestMesh.Triangles.Count} triangles, {bestMesh.FaceCount} faces");
        return new StepImportReport(bestMesh, notes);
    }
}
