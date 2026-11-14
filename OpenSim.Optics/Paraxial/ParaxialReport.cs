using OpenSim.Core.PostProcessing;

namespace OpenSim.Optics.Paraxial;

/// <summary>
/// The paraxial model as report sections: System data, ABCD matrix, Cardinal points, Paraxial
/// ray data and, with more than one wavelength, focal data by wavelength. Lengths are listed in
/// the system's lens unit.
/// </summary>
public static class ParaxialReport
{
    public static IReadOnlyList<ReportSection> Sections(ParaxialSystem model, IReadOnlyList<ParaxialSystem>? perWavelength = null,
        bool cardinalPoints = true, bool rayData = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        var unit = model.System.Units.LensUnit;
        string u = UnitName(unit);
        string L(double? meters) => meters is double m ? $"{StudyReport.Number(OpticsConventions.FromMeters(m, unit))} {u}" : "at infinity";
        string N(double? value) => value is double v ? StudyReport.Number(v) : "undefined";
        var lens = model.Lens;
        int last = lens.LastSurfaceIndex;

        bool angleField = FieldTypes.IsAngle(model.System.Fields.Type);
        var pairs = new List<(string, string)>
        {
            ("Wavelength", $"{StudyReport.Number(model.WavelengthMicrometers)} µm (row {model.WavelengthIndex + 1})"),
            ("Surfaces", $"{lens.Surfaces.Count} (object, {lens.Surfaces.Count - 2} surfaces, image); stop on surface {lens.StopIndex}"),
            ("Object distance", lens.ObjectAtInfinity ? "infinity" : L(lens.Surfaces[0].Thickness)),
            ("Object space index", StudyReport.Number(model.Indices[0])),
            ("Image space index", StudyReport.Number(model.Indices[last])),
            ("Afocal", model.IsAfocal ? "yes (no power)" : "no"),
            ("Effective focal length", model.IsAfocal ? "infinite (afocal)" : L(model.EffectiveFocalLength)),
            ("Back focal length", model.IsAfocal ? "undefined (afocal)" : L(model.BackFocalLength)),
            ("Front focal length", model.IsAfocal ? "undefined (afocal)" : L(model.FrontFocalLength)),
            ("Total track", L(model.TotalTrack)),
            ("Image space F/#", N(model.ImageSpaceFNumber)),
            ("Paraxial working F/#", model.ImageAtInfinity ? "undefined (image at infinity)" : N(model.WorkingFNumber)),
            ("Object space NA", StudyReport.Number(model.ObjectNumericalAperture)),
            ("Image space NA", StudyReport.Number(model.ImageNumericalAperture)),
            ("Entrance pupil diameter", model.EntrancePupilAtInfinity ? "undefined (pupil at infinity)" : L(model.EntrancePupilDiameter)),
            ("Entrance pupil position", L(model.EntrancePupilPosition)),
            ("Exit pupil diameter", model.ExitPupilAtInfinity ? "undefined (pupil at infinity)" : L(model.ExitPupilDiameter)),
            ("Exit pupil position", L(model.ExitPupilPosition)),
            ("Paraxial image distance", L(model.ParaxialImageDistance)),
            ("Image surface distance", L(lens.Surfaces[last].Thickness)),
            ("Paraxial image height", model.ImageAtInfinity ? "undefined (image at infinity)" : L(model.ParaxialImageHeight)),
            ("Chief ray height at the image surface", L(model.ImageSurfaceChiefHeight)),
            ("Lateral magnification", model.ImageAtInfinity ? "infinite (image at infinity)" : N(model.LateralMagnification)),
            ("Angular magnification", N(model.AngularMagnification)),
            ("Lagrange invariant", L(model.LagrangeInvariant)),
            ("Full field", angleField ? $"{StudyReport.Number(model.FullField)}°" : L(model.FullField)),
        };
        var sections = new List<ReportSection>
        {
            new("System data")
            {
                Pairs = pairs,
                Lines = new[]
                {
                    "Object-space positions are measured from surface 1's vertex, image-space positions from the last surface's vertex, along z (negative after an odd number of mirrors).",
                }.Concat(model.Notes).ToArray()
            }
        };

        var m = model.SystemMatrix;
        double per = OpticsConventions.MetersPer(unit);
        sections.Add(new ReportSection("ABCD matrix")
        {
            Pairs = new[]
            {
                ("A", StudyReport.Number(m.A, "g10")),
                ("B", $"{StudyReport.Number(m.B / per, "g10")} {u}"),
                ("C", $"{StudyReport.Number(m.C * per, "g10")} 1/{u}"),
                ("D", StudyReport.Number(m.D, "g10")),
                ("AD − BC", StudyReport.Number(m.Determinant, "g15")),
            },
            Lines = new[] { "From just before surface 1 to just after the last surface, acting on (y, n·u) with n the signed index; the power is −C." }
        });

        if (cardinalPoints)
        {
            if (model.IsAfocal)
                sections.Add(new ReportSection("Cardinal points") { Lines = new[] { "The system is afocal: it has no focal, principal or nodal points." } });
            else
                sections.Add(new ReportSection("Cardinal points")
                {
                    TableHeaders = new[] { "Point", "Object space (from surface 1)", "Image space (from last surface)" },
                    TableRows = new IReadOnlyList<string>[]
                    {
                        new[] { "Focal", L(model.FrontFocalLength), L(model.BackFocalLength) },
                        new[] { "Principal", L(model.FrontPrincipalPlane), L(model.RearPrincipalPlane) },
                        new[] { "Nodal", L(model.FrontNodalPoint), L(model.RearNodalPoint) },
                        new[] { "Focal length", L(model.ObjectSpaceFocalLength), L(model.ImageSpaceFocalLength) },
                    }
                });
        }

        if (rayData)
        {
            var mr = model.MarginalRay;
            var cr = model.ChiefRay;
            var h = model.LagrangeInvariants();
            var rows = new List<IReadOnlyList<string>>();
            for (int j = 0; j < lens.Surfaces.Count; j++)
            {
                string name = j == 0 ? "OBJ" : j == lens.ImageIndex ? "IMA" : j == lens.StopIndex ? $"{j} (stop)" : j.ToString();
                if (j == 0 && lens.ObjectAtInfinity)
                    rows.Add(new[] { name, "infinite", StudyReport.Number(mr.Slopes[0]), "infinite", StudyReport.Number(cr.Slopes[0]), StudyReport.Number(model.Indices[0]), "—" });
                else
                    rows.Add(new[]
                    {
                        name, L(mr.Heights[j]), StudyReport.Number(mr.Slopes[j]), L(cr.Heights[j]), StudyReport.Number(cr.Slopes[j]),
                        StudyReport.Number(model.Indices[j]), L(h[j]),
                    });
            }
            sections.Add(new ReportSection("Paraxial ray data")
            {
                TableHeaders = new[] { "Surface", "Marginal y", "Marginal u", "Chief y", "Chief u", "Index after", "Lagrange H" },
                TableRows = rows,
                Lines = new[] { "Heights at each vertex plane; slopes u = dy/dz after the surface in the system's frame; H = n·(ū·y − u·ȳ) with the signed index." }
            });
        }

        if (perWavelength is { Count: > 1 })
            sections.Add(new ReportSection("Focal data by wavelength")
            {
                TableHeaders = new[] { "Wavelength (µm)", "Effective focal length", "Back focal length", "Paraxial image distance" },
                TableRows = perWavelength.Select(w => (IReadOnlyList<string>)new[]
                {
                    StudyReport.Number(w.WavelengthMicrometers),
                    w.IsAfocal ? "afocal" : L(w.EffectiveFocalLength),
                    w.IsAfocal ? "afocal" : L(w.BackFocalLength),
                    L(w.ParaxialImageDistance),
                }).ToArray()
            });
        return sections;
    }

    /// <summary>What the paraxial numbers rest on, for the report's assumptions.</summary>
    public static IReadOnlyList<string> Assumptions(ParaxialSystem model) => new[]
    {
        "First-order (paraxial) optics: surfaces are their vertex curvatures; conics, aspheric terms and apertures other than the stop's do not enter.",
        "Indices are relative to the system's air. " + (model.System.Environment.AdjustIndexToEnvironment
            ? "Glass indices are adjusted to the system temperature and pressure (SCHOTT TIE-19)."
            : "Glass indices are catalog values at each catalog's reference conditions."),
        model.System.Environment.AirModelDescription,
        "Paraxial angles: an angle field or object cone angle θ launches the slope tan θ; an object space NA launches tan(asin(NA/n)); NA = n·sin(atan u).",
        "Effective focal length is 1/power, positive for a converging lens or mirror; not yet compared with OpticStudio's sign for mirror systems (docs/Optics-Conventions.md §10).",
    };

    internal static string UnitName(LensUnit unit) => unit switch
    {
        LensUnit.Millimeter => "mm",
        LensUnit.Centimeter => "cm",
        LensUnit.Inch => "in",
        LensUnit.Meter => "m",
        _ => unit.ToString()
    };
}
