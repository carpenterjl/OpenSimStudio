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
                      "use assembly import to load all bodies with their placements");

        notes.Add($"solid #{bestId}: {bestMesh.Vertices.Count} vertices, " +
                  $"{bestMesh.Triangles.Count} triangles, {bestMesh.FaceCount} faces");
        return new StepImportReport(bestMesh, notes);
    }

    /// <summary>
    /// Imports EVERY body of an assembly, each placed in the root frame.
    /// <para>
    /// This is deliberately OFF the <see cref="IGeometryImporter"/> seam, which returns one
    /// mesh: forcing a multi-body result through it would make every other importer
    /// (STL, primitives) pretend to be an assembly importer. The concrete type is what the
    /// DI container hands the view model, exactly as for <see cref="ImportWithNotes"/>.
    /// </para>
    /// Each distinct solid is tessellated ONCE and instanced by transform, so a fastener
    /// used forty times costs one tessellation.
    /// </summary>
    public StepAssemblyReport ImportAssembly(string filePath) =>
        ImportAssemblyText(File.ReadAllText(filePath));

    /// <summary>Assembly import from STEP text (the file-less entry point tests drive).</summary>
    public StepAssemblyReport ImportAssemblyText(string text)
    {
        var notes = new List<string>();
        var file = Part21Parser.Parse(text);
        var units = StepUnits.Resolve(file);
        notes.Add(FormattableString.Invariant($"length unit: {units.MetersPerUnit} m per model unit"));

        var resolver = new StepEntityResolver(file, units.MetersPerUnit);
        var solids = resolver.ResolveSolids();
        var placed = new StepAssemblyResolver(file, resolver, notes)
            .Resolve(solids.Select(s => s.Id).ToList());

        // One tessellation per distinct solid; instances differ only by placement.
        var tessellated = new Dictionary<int, (TriangleMesh Mesh, double Volume)>();
        var bodies = new List<StepAssemblyBody>(placed.Count);
        foreach (var occurrence in placed)
        {
            if (!tessellated.TryGetValue(occurrence.SolidId, out var baseMesh))
            {
                var solid = solids.First(s => s.Id == occurrence.SolidId);
                baseMesh = SolidTessellator.Tessellate(solid, _options, notes, units.UncertaintyMeters);
                tessellated[occurrence.SolidId] = baseMesh;
            }
            var mesh = MeshTransformer.Apply(baseMesh.Mesh, occurrence.Transform);
            bodies.Add(new StepAssemblyBody(occurrence.Name, mesh, baseMesh.Volume,
                occurrence.SolidId, occurrence.PlacedByAssembly));
        }

        int instanced = bodies.Count - tessellated.Count;
        notes.Add($"assembly: {bodies.Count} bod{(bodies.Count == 1 ? "y" : "ies")} from " +
                  $"{tessellated.Count} distinct solid{(tessellated.Count == 1 ? "" : "s")}" +
                  (instanced > 0 ? $" ({instanced} instanced)" : ""));
        return new StepAssemblyReport(bodies, notes);
    }
}
