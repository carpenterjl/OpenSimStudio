using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>
/// The editor's check of the object list (plan item OP-71): every problem the scene would be
/// refused for that can be found without the glass catalogs, one line each naming the object.
/// Each row's shape is built from its descriptor and the rows are handed to
/// <see cref="NscScene.Validate"/>, so the rules are the scene's own (references and Inside Of
/// earlier, hollow spheres not refractive, the stand-in on surfaces only, face numbers the type
/// has). A glass name is not looked up here (the trace does that, with the active catalogs); a
/// number is read as an index. A type the catalogue does not know is named as not built.
/// </summary>
public static class NscObjectCheck
{
    public static IReadOnlyList<string> Problems(IReadOnlyList<OpticsNscObjectSettings> rows, NscObjectCatalogue catalogue, LensUnit unit,
        IReadOnlyCollection<string>? coatingNames = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(catalogue);
        var problems = new List<string>();
        if (rows.Count == 0) return problems;
        var objects = new List<NscObject>();
        bool complete = true;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i] ?? new OpticsNscObjectSettings();
            string where = $"Object {i + 1}";
            var d = catalogue.Find(row.Type);
            if (d is null)
            {
                problems.Add($"{where}: {catalogue.NotBuiltMessage(row.Type ?? "")} It is kept and saved, not traced.");
                complete = false;
                continue;
            }
            NscGeometry geometry;
            try { geometry = d.Build(row.Parameters, unit); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                problems.Add($"{where} ({d.TypeName}): {e.Message}");
                complete = false;
                continue;
            }
            if (coatingNames is not null)
                foreach (var f in row.Faces ?? new List<OpticsNscFaceSettings>())
                    if (!string.IsNullOrWhiteSpace(f.Coating) && !coatingNames.Contains(f.Coating.Trim(), StringComparer.OrdinalIgnoreCase))
                        problems.Add($"{where}, face {f.Face ?? 0}: the coating '{f.Coating.Trim()}' is not in the coating catalog.");
            Dictionary<int, NscFace>? faces = null;
            foreach (var f in row.Faces ?? new List<OpticsNscFaceSettings>())
            {
                faces ??= new Dictionary<int, NscFace>();
                var behaviour = (f.FaceIs ?? "").Trim() switch
                {
                    "" or "Object Default" => NscFaceBehaviour.ObjectDefault,
                    "Reflective" => NscFaceBehaviour.Reflective,
                    "Absorbing" => NscFaceBehaviour.Absorbing,
                    _ => (NscFaceBehaviour)(-1),
                };
                if ((int)behaviour < 0) problems.Add($"{where}, face {f.Face ?? 0}: \"Face Is\" '{f.FaceIs}' is not Object Default, Reflective or Absorbing.");
                faces[f.Face ?? 0] = new NscFace { FaceIs = (int)behaviour < 0 ? NscFaceBehaviour.ObjectDefault : behaviour };
            }
            objects.Add(new NscObject
            {
                Geometry = geometry,
                Placement = NscObjectPlacement.Local(row),
                ReferenceObject = row.ReferenceObject ?? 0,
                InsideOf = row.InsideOf ?? 0,
                Material = Material(row.Material, where, problems),
                FaceProperties = faces,
                IsDetectingStandIn = row.DetectingStandIn ?? false,
            });
        }
        // The scene's own rules, when every row could be built (object numbers must line up).
        if (complete) problems.AddRange(new NscScene { Objects = objects }.Validate());
        return problems;
    }

    private static NscMaterial Material(string? text, string where, List<string> problems)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return NscMaterial.Blank;
        if (string.Equals(t, "MIRROR", StringComparison.OrdinalIgnoreCase)) return NscMaterial.Mirror;
        if (string.Equals(t, "ABSORB", StringComparison.OrdinalIgnoreCase)) return NscMaterial.Absorb;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
        {
            if (!(n > 0) || !double.IsFinite(n)) { problems.Add($"{where}: an index must be positive (it is {t})."); return NscMaterial.Blank; }
            return NscMaterial.Of(ParaxialMedium.Constant(n));
        }
        // A glass: refractive; looked up by the trace with the active catalogs.
        return NscMaterial.Blank;
    }
}
