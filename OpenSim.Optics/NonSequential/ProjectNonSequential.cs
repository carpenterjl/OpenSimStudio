using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.NonSequential;

/// <summary>
/// The non-sequential scene a project saves (<see cref="OpticsNonSequentialSettings"/>, OP-70)
/// turned into an <see cref="NscScene"/> and the base <see cref="NscTraceSettings"/>, lengths
/// converted from the lens unit. The object types read are OP-70's Sphere (radius, "Is
/// Volume?"), Rectangle (x and y half widths) and Standard Lens (OpticStudio's nine parameters, a
/// radius of 0 flat), OP-77's source types and OP-79's detector types (<see cref="BuiltTypes"/>);
/// any other type is refused by name (the object library is plan items OP-72 to OP-75).
/// </summary>
public static class ProjectNonSequential
{
    /// <summary>Saved names of the object types this build reads.</summary>
    public static IReadOnlyList<string> BuiltTypes { get; } = new[] { "Sphere", "Rectangle", "Standard Lens" }
        .Concat(NscSourceTypes.BuiltNames)                 // OP-77: the source types
        .Concat(Detectors.NscDetectorTypes.BuiltNames)     // OP-79: the detector types
        .ToArray();

    /// <summary>The scene of the snapshot, or null when it has no non-sequential objects. Throws
    /// <see cref="ArgumentException"/> listing every problem, each naming the object.</summary>
    public static NscScene? Scene(OpticsAnalysisContext context, LensUnit unit)
    {
        ArgumentNullException.ThrowIfNull(context);
        var saved = context.System.NonSequential;
        if (saved?.Objects is not { Count: > 0 } rows) return null;
        var problems = new List<string>();
        var objects = new List<NscObject>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            string where = $"Object {i + 1}";
            var o = ObjectFrom(row, where, unit, context, problems);
            if (o is not null) objects.Add(o);
        }
        var background = ParaxialMedium.Air;
        if (!string.IsNullOrWhiteSpace(saved.BackgroundMaterial))
        {
            var m = MaterialFrom(saved.BackgroundMaterial, "The background", context, problems);
            if (m is { Kind: NscMaterialKind.Refractive, Medium: { } medium }) background = medium;
            else if (m is not null) problems.Add("The background: it must be blank, a glass or an index (not MIRROR or ABSORB).");
        }
        if (problems.Count > 0)
            throw new ArgumentException("Non-sequential objects refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        return new NscScene { Objects = objects, Background = background };
    }

    /// <summary>The saved non-sequential controls on top of <paramref name="baseline"/>.</summary>
    public static NscTraceSettings Settings(OpticsNonSequentialSettings? saved, LensUnit unit, NscTraceSettings baseline)
    {
        if (saved is null) return baseline;
        return baseline with
        {
            MaxIntersectionsPerRay = saved.MaxIntersectionsPerRay ?? baseline.MaxIntersectionsPerRay,
            MaxSegmentsPerRay = saved.MaxSegmentsPerRay ?? baseline.MaxSegmentsPerRay,
            MaxNestedObjects = saved.MaxNestedObjects ?? baseline.MaxNestedObjects,
            MinimumRelativeIntensity = saved.MinimumRelativeIntensity ?? baseline.MinimumRelativeIntensity,
            MinimumAbsoluteIntensity = saved.MinimumAbsoluteIntensity ?? baseline.MinimumAbsoluteIntensity,
            GlueDistance = saved.GlueDistance is double g ? OpticsConventions.ToMeters(g, unit) : baseline.GlueDistance,
            LensUnit = unit,
        };
    }

    private static NscObject? ObjectFrom(OpticsNscObjectSettings row, string where, LensUnit unit,
        OpticsAnalysisContext context, List<string> problems)
    {
        string type = (row.Type ?? "").Trim();
        var p = row.Parameters ?? new List<double>();
        double L(int i) => OpticsConventions.ToMeters(p[i], unit);
        NscGeometry? geometry = null;
        try
        {
            switch (type)
            {
                case "Sphere":
                    if (!Need(2)) return null;
                    geometry = new NscSphere(L(0), p[1] != 0);
                    break;
                case "Rectangle":
                    if (!Need(2)) return null;
                    geometry = new NscRectangle(L(0), L(1));
                    break;
                case "Standard Lens":
                    if (!Need(9)) return null;
                    geometry = new NscStandardLens(L(0), p[1], L(2), L(3), L(4), L(5), p[6], L(7), L(8));
                    break;
                // OP-77: source objects (types and parameters in Sources/NscSourceTypes.cs)
                case var t when NscSourceTypes.Find(t) is not null:
                {
                    var props = ProjectNscSources.Properties(row.Source, unit, where, problems);
                    if (props is null) return null;
                    geometry = NscSourceTypes.Create(t, p, unit, props);
                    break;
                }
                // end OP-77

                // OP-79: detector objects (Detector Surface and Detector Volume are refused by name).
                case var detector when Detectors.NscDetectorTypes.IsDetectorType(detector):
                    geometry = Detectors.NscDetectorTypes.Create(detector, p, v => OpticsConventions.ToMeters(v, unit), row);
                    break;
                default:
                    problems.Add($"{where}: the object type '{type}' is not built; this build reads {string.Join(", ", BuiltTypes)}, and the object library is plan items OP-72 to OP-75.");
                    return null;
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            problems.Add($"{where}: {e.Message}");
            return null;
        }
        var material = MaterialFrom(row.Material, where, context, problems);
        if (material is null) return null;
        Dictionary<int, NscFace>? faces = null;
        foreach (var f in row.Faces ?? new List<OpticsNscFaceSettings>())
        {
            faces ??= new Dictionary<int, NscFace>();
            var behaviour = (f.FaceIs ?? "Object Default").Trim() switch
            {
                "" or "Object Default" => NscFaceBehaviour.ObjectDefault,
                "Reflective" => NscFaceBehaviour.Reflective,
                "Absorbing" => NscFaceBehaviour.Absorbing,
                var other => (NscFaceBehaviour)(-1),
            };
            if ((int)behaviour < 0) { problems.Add($"{where}, face {f.Face ?? 0}: \"Face Is\" '{f.FaceIs}' is not Object Default, Reflective or Absorbing."); continue; }
            faces[f.Face ?? 0] = new NscFace { FaceIs = behaviour, Coating = string.IsNullOrWhiteSpace(f.Coating) ? null : f.Coating.Trim() };
        }
        return new NscObject
        {
            Geometry = geometry,
            Placement = new NscPlacement(
                OpticsConventions.ToMeters(row.X ?? 0, unit), OpticsConventions.ToMeters(row.Y ?? 0, unit), OpticsConventions.ToMeters(row.Z ?? 0, unit),
                row.TiltX ?? 0, row.TiltY ?? 0, row.TiltZ ?? 0, row.GlobalXyzRotationOrder ?? false),
            ReferenceObject = row.ReferenceObject ?? 0,
            InsideOf = row.InsideOf ?? 0,
            Material = material,
            FaceProperties = faces,
            IsDetectingStandIn = row.DetectingStandIn ?? false,
            Comment = row.Comment ?? "",
        };

        bool Need(int count)
        {
            if (p.Count >= count) return true;
            problems.Add($"{where}: a {type} needs {count} parameters; {p.Count} are saved.");
            return false;
        }
    }

    private static NscMaterial? MaterialFrom(string? text, string where, OpticsAnalysisContext context, List<string> problems)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return NscMaterial.Blank;
        if (string.Equals(t, "MIRROR", StringComparison.OrdinalIgnoreCase)) return NscMaterial.Mirror;
        if (string.Equals(t, "ABSORB", StringComparison.OrdinalIgnoreCase)) return NscMaterial.Absorb;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
        {
            if (!(n > 0) || !double.IsFinite(n)) { problems.Add($"{where}: an index must be positive (it is {t})."); return null; }
            return NscMaterial.Of(ParaxialMedium.Constant(n));
        }
        var glass = context.FindGlass(t);
        if (glass is null) { problems.Add($"{where}: the glass '{t}' is not in the active catalogs."); return null; }
        return NscMaterial.Of(ParaxialMedium.Of(glass));
    }
}
