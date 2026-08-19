using System.Globalization;
using System.Text;
using OpenSim.Core.Model;
using OpenSim.Core.Results;

namespace OpenSim.Core.PostProcessing;

/// <summary>
/// Everything a result export needs to describe ITSELF, so a CSV read a year later still
/// says what was solved. Assembled by the caller because only the app knows the project
/// name and analysis kind; the solver knows neither.
/// </summary>
public sealed record ResultReportContext
{
    public required string ProjectName { get; init; }
    public required string BodyName { get; init; }
    public required string Analysis { get; init; }
    public Material? Material { get; init; }
    public IReadOnlyList<BoundaryCondition> BoundaryConditions { get; init; } = Array.Empty<BoundaryCondition>();
    public string? FrameLabel { get; init; }

    /// <summary>Free-form lines appended to the preamble — solver notes, stated caps.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Renders FE result fields as CSV, following the DC-net report precedent: a
/// '#'-prefixed preamble carrying what was solved (once, never repeated per row) and the
/// stated definitions, then an RFC-4180 table.
///
/// There is NO timestamp, on purpose — the output is a deterministic function of the
/// solve, so two exports of the same result compare byte for byte and a gate can say so.
///
/// Two shapes, because the Ansys reports carry both:
/// <list type="bullet">
/// <item><description><see cref="WriteSummary"/> — one row per field with its min, max and
/// both averages. This is the table a report quotes.</description></item>
/// <item><description><see cref="WriteField"/> — one row per node or element, for plotting
/// or an independent check.</description></item>
/// </list>
/// Units are the field own SI units; no friendly scaling, because a CSV is read by tools.
/// </summary>
public static class ResultReportCsv
{
    private const string Eol = "\r\n";

    public static string WriteSummary(IReadOnlyList<IResultField> fields, FeMesh mesh,
        ResultReportContext context)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var sb = new StringBuilder();
        Preamble(sb, mesh, context);
        sb.Append("# average = unweighted mean over the field own entities (nodes for a nodal field,");
        sb.Append(" elements for an element field), which is what commercial FE reports quote;").Append(Eol);
        sb.Append("# volume-weighted average weights each entity by the volume it represents,");
        sb.Append(" so it does not change when the mesh is refined unevenly.").Append(Eol);

        sb.Append("Field,Unit,Location,Count,Min,Max,Average,Volume-weighted average,Min at,Max at").Append(Eol);
        foreach (var field in fields)
        {
            if (field.Count == 0) continue;
            var s = FieldStatistics.Compute(field, mesh);
            sb.Append(Escape(field.Name)).Append(',');
            sb.Append(Escape(field.Unit)).Append(',');
            sb.Append(field.Location == FieldLocation.Node ? "node" : "element").Append(',');
            sb.Append(s.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
            Number(sb, s.Min).Append(',');
            Number(sb, s.Max).Append(',');
            Number(sb, s.Mean).Append(',');
            Number(sb, s.VolumeWeightedMean).Append(',');
            sb.Append(s.MinIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(s.MaxIndex.ToString(CultureInfo.InvariantCulture)).Append(Eol);
        }
        return sb.ToString();
    }

    public static string WriteField(IResultField field, FeMesh mesh, ResultReportContext context)
    {
        ArgumentNullException.ThrowIfNull(field);
        var sb = new StringBuilder();
        Preamble(sb, mesh, context);
        sb.Append(CultureInfo.InvariantCulture, $"# field: {field.Name} [{field.Unit}]").Append(Eol);

        bool nodal = field.Location == FieldLocation.Node;
        sb.Append(nodal ? "Node,X (m),Y (m),Z (m)," : "Element,Centroid X (m),Centroid Y (m),Centroid Z (m),");
        sb.Append(Escape($"{field.Name} ({field.Unit})")).Append(Eol);

        for (int i = 0; i < field.Count; i++)
        {
            var p = nodal ? NodePosition(mesh, i) : ElementCentroid(mesh, i);
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',');
            Number(sb, p.X).Append(',');
            Number(sb, p.Y).Append(',');
            Number(sb, p.Z).Append(',');
            Number(sb, field.GetScalar(i)).Append(Eol);
        }
        return sb.ToString();
    }

    private static void Preamble(StringBuilder sb, FeMesh mesh, ResultReportContext context)
    {
        sb.Append("# OpenSim Studio — result report").Append(Eol);
        sb.Append(CultureInfo.InvariantCulture, $"# project: {context.ProjectName}").Append(Eol);
        sb.Append(CultureInfo.InvariantCulture, $"# body: {context.BodyName}").Append(Eol);
        sb.Append(CultureInfo.InvariantCulture, $"# analysis: {context.Analysis}").Append(Eol);
        if (context.FrameLabel is { Length: > 0 } frame)
            sb.Append(CultureInfo.InvariantCulture, $"# frame: {frame}").Append(Eol);
        sb.Append(CultureInfo.InvariantCulture,
            $"# mesh: {mesh.NodeCount} nodes, {mesh.ElementCount} elements, " +
            $"{(mesh.IsQuadratic ? "TET10 (quadratic)" : "TET4 (linear)")}").Append(Eol);

        if (context.Material is { } material)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"# material: {material.Name}, E = {material.YoungsModulus:G6} Pa, " +
                $"v = {material.PoissonRatio:G6}, rho = {material.Density:G6} kg/m3").Append(Eol);
            if (material.YieldStrength is { } yield)
                sb.Append(CultureInfo.InvariantCulture, $"# yield strength: {yield:G6} Pa").Append(Eol);
            if (material.UltimateTensileStrength is { } ultimate)
                sb.Append(CultureInfo.InvariantCulture,
                    $"# ultimate tensile strength: {ultimate:G6} Pa").Append(Eol);
        }

        foreach (var bc in context.BoundaryConditions)
            sb.Append(CultureInfo.InvariantCulture,
                $"# condition: {bc.Name} ({bc.GetType().Name}) on {bc.ScopeSummary}").Append(Eol);

        foreach (var note in context.Notes)
            sb.Append(CultureInfo.InvariantCulture, $"# {note}").Append(Eol);
    }

    private static Numerics.Vector3D NodePosition(FeMesh mesh, int index) =>
        index < mesh.NodeCount ? mesh.Nodes[index] : new Numerics.Vector3D(0, 0, 0);

    private static Numerics.Vector3D ElementCentroid(FeMesh mesh, int index)
    {
        if (index >= mesh.ElementCount) return new Numerics.Vector3D(0, 0, 0);
        var e = mesh.Elements[index];
        return (mesh.Nodes[e.N0] + mesh.Nodes[e.N1] + mesh.Nodes[e.N2] + mesh.Nodes[e.N3]) * 0.25;
    }

    /// <summary>Round-trip ("R") formatting: an export is data, not a display.</summary>
    private static StringBuilder Number(StringBuilder sb, double value) =>
        sb.Append(value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>RFC-4180 minimal quoting, matching the DC-net report.</summary>
    private static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
