using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.NonSequential.Scattering;

namespace OpenSim.Optics.Scripting.Zpl;

// wire-16: SETNSCPROPERTY's and NPRO's Coat/Scatter and Scatter To codes on OP-80's saved fields
// (OpticsNscFaceSettings.ScatterModel ... ThinWindowScattering, OpticsNscObjectSettings
// .ScatterToMethod/.ScatterToList/.ScatterTargets), following the code table of the help page
// SETNSCPROPERTY (keywords) (plans/sources/optics/op-99-help/; docs/Optics-Conventions.md §56):
// 7 scatter mode (0 none, 1 Lambertian, 2 Gaussian, 3 ABg, 4 User Defined, 5 BSDF, 6 ABg File),
// 8 scatter fraction, 9 number of scatter rays, 10 Gaussian sigma or BSDF sample orientation
// angle, 11 and 12 reflect and transmit ABg data names, 31 and 32 reflect and transmit BSDF files,
// 33 and 34 reflect and transmit ABg File files, 37 Thin Window Scattering, 151 the Scatter To
// method (0 scatter to list, 1 importance sampling), 152 one importance-sampling target
// ("ray object size limit"), 153 the Scatter To list. Refused by name: 4 (User Defined scattering
// loads a DLL), 6 (scatter profiles), 21–28 (the scatter DLL and its data), NPRO 152 (the help
// does not say how NPRO picks the target ray).

public static partial class ZplLibrary
{
    private static readonly HashSet<int> ScatterFaceCodes = new() { 7, 8, 9, 10, 11, 12, 31, 32, 33, 34, 37 };
    private static readonly HashSet<int> ScatterToCodes = new() { 151, 152, 153 };

    /// <summary>Code 7's numbers in the help's order, as OP-80 saves them.</summary>
    private static readonly string[] ScatterModeCodes = { "None", "Lambertian", "Gaussian", "ABg", "User Defined", "BSDF", "ABg File" };

    private const string ScatterCodesNote = "on a face's Coat/Scatter: 7 scatter mode (0 none, 1 Lambertian, 2 Gaussian, 3 ABg, 5 BSDF, 6 ABg File; 4 User Defined is refused), 8 scatter fraction, 9 number of scatter rays (0 to 300), 10 Gaussian sigma (Gaussian) or sample orientation angle (BSDF), 11 and 12 reflect and transmit ABg data names (ABg), 31 and 32 reflect and transmit BSDF files, 33 and 34 reflect and transmit ABGF files (full paths), 37 Thin Window Scattering; on the object's Scatter To: 151 method (0 scatter to list, 1 importance sampling), 152 an importance-sampling target \"ray object size limit\" (SETNSCPROPERTY only), 153 the Scatter To list";

    private static OpticsNscFaceSettings ScatterFace(OpticsNscObjectSettings o, int f) =>
        o.Faces?.FirstOrDefault(x => (x.Face ?? 0) == f) ?? new OpticsNscFaceSettings { Face = f };

    private static NscScatterModel ScatterModelOf(OpticsNscFaceSettings face, int number, int f, string who) =>
        ProjectNscScatter.ModelOf(face.ScatterModel)
        ?? throw new ZplRuntimeException($"{who}: object {number}, face {f}: the saved scatter model '{face.ScatterModel}' is not one of {string.Join(", ", ProjectNscScatter.ModelNames)}.");

    /// <summary>Codes 10–12 and 31–34 hold a model's own data: the face's model must be that model.</summary>
    private static void ScatterNeeds(OpticsNscFaceSettings face, int number, int f, int code, string who, params NscScatterModel[] models)
    {
        var model = ScatterModelOf(face, number, f, who);
        if (Array.IndexOf(models, model) >= 0) return;
        string wanted = string.Join(" or ", models.Select(x => ProjectNscScatter.ModelNames[(int)x]));
        throw new ZplRuntimeException($"{who}: code {code} is {wanted} scatter data; object {number}, face {f} scatters by {ProjectNscScatter.ModelNames[(int)model]} (set its scatter mode, code 7, first).");
    }

    /// <summary>NPRO's value of a Coat/Scatter or Scatter To code; text goes to $BUFFER and 0 is returned.</summary>
    private static ZplValue ScatterProperty(ZplInterpreter m, OpticsNscObjectSettings o, int number, int code, double faceArg)
    {
        const string who = "NPRO";
        ZplValue Text(string t) { m.Session.Buffer = t; return ZplValue.Of(0); }
        if (ScatterToCodes.Contains(code))
        {
            NscType(o, number, who);
            return code switch
            {
                151 => ZplValue.Of(string.Equals((o.ScatterToMethod ?? "").Trim(), "Importance Sampling", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                153 => Text(o.ScatterToList ?? ""),
                _ => throw new ZplRuntimeException($"{who}: code 152 (an importance-sampling target) is not bound for NPRO: the help does not say which target ray NPRO returns or how it encodes the four values."),
            };
        }
        int f = NscFace(o, number, faceArg, who);
        var face = ScatterFace(o, f);
        var model = ScatterModelOf(face, number, f, who);
        switch (code)
        {
            case 7: return ZplValue.Of(Array.IndexOf(ScatterModeCodes, ProjectNscScatter.ModelNames[(int)model]));
            case 8: return ZplValue.Of(face.ScatterFraction ?? 0);
            case 9: return ZplValue.Of(face.ScatterRays ?? 1);   // ProjectNscScatter's default
            case 10:
                ScatterNeeds(face, number, f, code, who, NscScatterModel.Gaussian, NscScatterModel.Bsdf);
                return ZplValue.Of(model == NscScatterModel.Gaussian ? face.ScatterSigma ?? 0 : face.ScatterAngle ?? 0);
            case 11 or 12:
                ScatterNeeds(face, number, f, code, who, NscScatterModel.ABg);
                return Text((code == 11 ? face.ScatterReflect : face.ScatterTransmit) ?? "");
            case 31 or 32:
                ScatterNeeds(face, number, f, code, who, NscScatterModel.Bsdf);
                return Text((code == 31 ? face.ScatterReflect : face.ScatterTransmit) ?? "");
            case 33 or 34:
                ScatterNeeds(face, number, f, code, who, NscScatterModel.ABgFile);
                return Text((code == 33 ? face.ScatterReflect : face.ScatterTransmit) ?? "");
            default: return ZplValue.Of(face.ThinWindowScattering == true ? 1 : 0);   // 37
        }
    }

    /// <summary>SETNSCPROPERTY's Coat/Scatter and Scatter To codes.</summary>
    private static OpticsNscObjectSettings WithScatterProperty(OpticsNscObjectSettings o, int number, int code, double faceArg, ZplValue value)
    {
        const string who = "SETNSCPROPERTY";
        double Number(string what) => value.IsString
            ? throw new ZplRuntimeException($"{who}: code {code} ({what}) takes a number, not \"{value.Text}\".")
            : double.IsFinite(value.Number) ? value.Number : throw new ZplRuntimeException($"{who}: {what} is not a finite number.");
        string Text() => value.IsString ? value.Text!.Trim() : ZplInterpreter.Num(value.Number);
        if (ScatterToCodes.Contains(code))
        {
            NscType(o, number, who);
            switch (code)
            {
                case 151:
                {
                    int k = Int(Number("the Scatter To method"), $"{who}: the Scatter To method");
                    if (k is not (0 or 1)) throw new ZplRuntimeException($"{who}: the Scatter To method is {k}; it must be 0 (scatter to list) or 1 (importance sampling).");
                    return o with { ScatterToMethod = k == 0 ? "Scatter To List" : "Importance Sampling" };
                }
                case 152:
                {
                    // "a string listing the ray number, the object number, the size, and the limit value, all separated by spaces"
                    var parts = Text().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    double[] v = new double[4];
                    if (parts.Length != 4 || !parts.Select((p, i) => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])).All(ok => ok))
                        throw new ZplRuntimeException($"{who}: code 152 takes \"ray object size limit\" (four numbers separated by spaces, e.g. \"3 6 3.5 0.6\"), not \"{Text()}\".");
                    int ray = Int(v[0], $"{who}: the target's ray number"), target = Int(v[1], $"{who}: the target's object");
                    if (ray is < 1 or > 6) throw new ZplRuntimeException($"{who}: the target's ray number is {ray}; importance sampling has targets for rays 1 to 6.");
                    if (target < 0) throw new ZplRuntimeException($"{who}: the target's object is {target}; it must be 0 (none) or an object number.");
                    var list = new List<OpticsNscScatterTargetSettings>(o.ScatterTargets ?? new List<OpticsNscScatterTargetSettings>());
                    while (list.Count < ray) list.Add(new OpticsNscScatterTargetSettings { Object = 0 });
                    list[ray - 1] = new OpticsNscScatterTargetSettings { Object = target, Size = v[2], Limit = v[3] };
                    return o with { ScatterTargets = list };
                }
                default:
                {
                    var tokens = Text().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var t in tokens)
                        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) || k < 1)
                            throw new ZplRuntimeException($"{who}: the Scatter To list entry '{t}' is not an object number (\"a string listing the object numbers to scatter to delimited by spaces\").");
                    return o with { ScatterToList = tokens.Length == 0 ? null : string.Join(" ", tokens) };
                }
            }
        }

        int f = NscFace(o, number, faceArg, who);
        var faces = new List<OpticsNscFaceSettings>(o.Faces ?? new List<OpticsNscFaceSettings>());
        int at = faces.FindIndex(x => (x.Face ?? 0) == f);
        var face = at < 0 ? new OpticsNscFaceSettings { Face = f } : faces[at];
        string FullPath(string what)
        {
            string t = Text();
            if (t.Length == 0) return t;
            if (!Path.IsPathRooted(t))
                throw new ZplRuntimeException($"{who}: the {what} '{t}' has no folder; OpticStudio looks in its data folder, which this program does not have, so give the full path.");
            return t;
        }
        switch (code)
        {
            case 7:
            {
                int k = Int(Number("the scatter mode"), $"{who}: the scatter mode");
                if (k == 4) throw new ZplRuntimeException($"{who}: scatter mode 4 (User Defined) is not built: it loads a scatter DLL, and no DLLs are loaded.");
                if (k < 0 || k >= ScatterModeCodes.Length) throw new ZplRuntimeException($"{who}: the scatter mode is {k}; it must be 0 (none), 1 (Lambertian), 2 (Gaussian), 3 (ABg), 5 (BSDF) or 6 (ABg File).");
                face = face with { ScatterModel = ScatterModeCodes[k] };
                break;
            }
            case 8: face = face with { ScatterFraction = Number("the scatter fraction") }; break;
            case 9:
            {
                int k = Int(Number("the number of scatter rays"), $"{who}: the number of scatter rays");
                // "The maximum number of scatter rays is 300" (help, "Fraction to Scatter and Number of Scatter Rays").
                if (k is < 0 or > 300) throw new ZplRuntimeException($"{who}: the number of scatter rays is {k}; it must be 0 to 300.");
                face = face with { ScatterRays = k };
                break;
            }
            case 10:
                ScatterNeeds(face, number, f, code, who, NscScatterModel.Gaussian, NscScatterModel.Bsdf);
                face = ScatterModelOf(face, number, f, who) == NscScatterModel.Gaussian
                    ? face with { ScatterSigma = Number("the Gaussian sigma") }
                    : face with { ScatterAngle = Number("the sample orientation angle") };
                break;
            case 11 or 12:
            {
                ScatterNeeds(face, number, f, code, who, NscScatterModel.ABg);
                string t = Text();
                face = code == 11 ? face with { ScatterReflect = t.Length == 0 ? null : t } : face with { ScatterTransmit = t.Length == 0 ? null : t };
                break;
            }
            case 31 or 32:
            {
                ScatterNeeds(face, number, f, code, who, NscScatterModel.Bsdf);
                string t = FullPath("BSDF file");
                face = code == 31 ? face with { ScatterReflect = t.Length == 0 ? null : t } : face with { ScatterTransmit = t.Length == 0 ? null : t };
                break;
            }
            case 33 or 34:
            {
                ScatterNeeds(face, number, f, code, who, NscScatterModel.ABgFile);
                string t = FullPath("ABGF file");
                face = code == 33 ? face with { ScatterReflect = t.Length == 0 ? null : t } : face with { ScatterTransmit = t.Length == 0 ? null : t };
                break;
            }
            default:   // 37
                face = face with { ThinWindowScattering = Flag(Number("the Thin Window Scattering flag"), $"{who}: the Thin Window Scattering flag") };
                break;
        }
        if (at < 0) faces.Add(face);
        else faces[at] = face;
        return o with { Faces = faces };
    }
}
