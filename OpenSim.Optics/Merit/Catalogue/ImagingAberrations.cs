using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Merit;

// OP-55A: aberration operands (help: Aberrations; Optimization Operands Alphabetically): the
// Seidel sums (SeidelEngine, §32), Petzval, paraxial lateral colour, OP-37's longitudinal
// aberration crossings and Coddington field curvature, OP-40's RMS core, the Zernike fit of
// OP-36's FieldZernike, and the on-axis boresight error.
public static partial class ImagingOperands
{
    private const string AberrationCategory = "Aberrations";

    private enum SeidelKind { S1, S2, S3, S4, S5 }

    private static IEnumerable<MeritOperandDefinition> AberrationDefinitions()
    {
        yield return Seidel("SPHA", "Spherical aberration (Seidel)", SeidelKind.S1, "spherical aberration S1");
        yield return Seidel("COMA", "Coma (Seidel)", SeidelKind.S2, "coma S2");
        yield return Seidel("ASTI", "Astigmatism (Seidel)", SeidelKind.S3, "astigmatism S3");
        yield return Seidel("FCUR", "Field curvature (Seidel)", SeidelKind.S4, "field curvature S4");
        yield return Seidel("DIST", "Distortion (Seidel; percent for Surf 0)", SeidelKind.S5, "distortion S5");

        yield return new MeritOperandDefinition
        {
            Code = "PETZ", Name = "Petzval radius", Category = AberrationCategory, Source = AlphaPage,
            Description = "Petzval radius of curvature (the inverse of the Petzval sum, signed as a radius), lens units, at wavelength Wave (SeidelEngine).",
            Int2 = "Wave",
            Compute = c =>
            {
                double r = SeidelOf(c, c.Wavelength(c.Row.Int2) - 1).PetzvalRadius;
                return double.IsFinite(r) ? c.Length(r) : throw new MeritOperandException("the Petzval sum is zero, so the Petzval radius is infinite; use PETC.");
            },
        };
        yield return new MeritOperandDefinition
        {
            Code = "PETC", Name = "Petzval curvature", Category = AberrationCategory, Source = AlphaPage,
            Description = "Petzval curvature (1 over the Petzval radius), 1/lens unit, at wavelength Wave (SeidelEngine).",
            Int2 = "Wave",
            Compute = c =>
            {
                double r = SeidelOf(c, c.Wavelength(c.Row.Int2) - 1).PetzvalRadius;
                return double.IsInfinity(r) ? 0 : c.Curvature(1 / r);
            },
        };

        yield return new MeritOperandDefinition
        {
            Code = "LACL", Name = "Lateral colour (paraxial)", Category = AberrationCategory, Source = AlphaPage,
            Description = "y(Minw) − y(Maxw) of the full-field paraxial chief rays at the image surface, lens units (focal systems; each wavelength's chief ray through its own paraxial pupil centre, as Lateral Color's paraxial form).",
            Int1 = "Minw", Int2 = "Maxw",
            Compute = c =>
            {
                RefuseAfocal(c, "LACL");
                var a = c.Paraxial(c.Wavelength(c.Row.Int1, "Minw"));
                var b = c.Paraxial(c.Wavelength(c.Row.Int2, "Maxw"));
                return c.Length(a.ImageSurfaceChiefHeight - b.ImageSurfaceChiefHeight);
            },
        };
        yield return new MeritOperandDefinition
        {
            Code = "AXCL", Name = "Axial colour", Category = AberrationCategory, Source = AlphaPage + "; Longitudinal Aberration",
            Description = "z(Wave1) − z(Wave2), lens units: the axis crossings from the image surface of the on-axis marginal ray of zone Zone (Data1; 0 for the paraxial marginal ray, else the real ray Py = Zone), by Longitudinal Aberration's engine. Focal systems.",
            Int1 = "Wave1", Int2 = "Wave2", Data = new[] { "Zone" },
            Compute = c =>
            {
                double zone = Zone(c.Row.DataAt(1));
                double a = Crossing(c, c.Wavelength(c.Row.Int1, "Wave1") - 1, zone);
                double b = Crossing(c, c.Wavelength(c.Row.Int2, "Wave2") - 1, zone);
                return c.Length(a - b);
            },
        };
        yield return new MeritOperandDefinition
        {
            Code = "LONA", Name = "Longitudinal aberration", Category = AberrationCategory, Source = AlphaPage + "; Longitudinal Aberration",
            Description = "Distance from the image surface to the axis crossing of the on-axis marginal ray of zone Zone (Data1; 0 paraxial, else the real ray Py = Zone) at wavelength Wave, positive along the light, lens units (Longitudinal Aberration's engine). Focal systems.",
            Int2 = "Wave", Data = new[] { "Zone" },
            Compute = c => c.Length(Crossing(c, c.Wavelength(c.Row.Int2) - 1, Zone(c.Row.DataAt(1)))),
        };

        yield return Rms("RSCE", "RMS spot radius (centroid, Gaussian quadrature)", spot: true, centroid: true, gaussian: true);
        yield return Rms("RSCH", "RMS spot radius (chief ray, Gaussian quadrature)", spot: true, centroid: false, gaussian: true);
        yield return Rms("RSRE", "RMS spot radius (centroid, rectangular array)", spot: true, centroid: true, gaussian: false);
        yield return Rms("RSRH", "RMS spot radius (chief ray, rectangular array)", spot: true, centroid: false, gaussian: false);
        yield return Rms("RWCE", "RMS wavefront error (centroid, Gaussian quadrature)", spot: false, centroid: true, gaussian: true);
        yield return Rms("RWCH", "RMS wavefront error (chief ray, Gaussian quadrature)", spot: false, centroid: false, gaussian: true);
        yield return Rms("RWRE", "RMS wavefront error (centroid, rectangular array)", spot: false, centroid: true, gaussian: false);
        yield return Rms("RWRH", "RMS wavefront error (chief ray, rectangular array)", spot: false, centroid: false, gaussian: false);

        yield return FieldCurvature("FCGS", "Field curvature sagittal (generalized)", sagittal: true);
        yield return FieldCurvature("FCGT", "Field curvature tangential (generalized)", sagittal: false);

        yield return new MeritOperandDefinition
        {
            Code = "ZERN", Name = "Zernike coefficient", Category = AberrationCategory, Source = AlphaPage,
            Description = "Zernike coefficient Term (Int1, ≥ 1), waves, of the OPD of field Field (Data2) at wavelength Wave against the chief-ray reference sphere, fitted over a 32·2^(Samp−1) pupil grid (Data1) with max(11, Term) terms: Type (Data3) 0 Fringe (Term ≤ 37) or 1 Standard (Term ≤ 231). Annular (Type 2), Epsilon (Data4), Vertex? 1 (Data5) and Term ≤ 0 are not built.",
            Int1 = "Term", Int2 = "Wave", Data = new[] { "Samp", "Field", "Type", "Epsilon", "Vertex?" },
            Compute = Zernike,
        };

        yield return new MeritOperandDefinition
        {
            Code = "BSER", Name = "Boresight error", Category = AberrationCategory, Source = AlphaPage,
            Description = "Radial image-surface coordinate of the on-axis chief ray (Hx = Hy = Px = Py = 0) at wavelength Wave divided by the paraxial effective focal length at Wave (dimensionless).",
            Int2 = "Wave",
            Compute = c =>
            {
                int wave = c.Wavelength(c.Row.Int2);
                var path = For(c).Path(wave, 0, 0, 0, 0);
                if (!path.Final.IsOk)
                    throw new MeritOperandException($"the on-axis chief ray failed: {RayStatuses.Label(path.Status)} at surface {path.FailedAtSurface}.");
                double f = c.Paraxial(wave).EffectiveFocalLength ?? throw new MeritOperandException("the system is afocal; it has no effective focal length.");
                var p = path.Final.Position;
                return Math.Sqrt(p.X * p.X + p.Y * p.Y) / f;
            },
        };
    }

    // ------------------------------------------------------------------ Seidel

    private static SeidelResult SeidelOf(MeritRowContext c, int row) =>
        For(c).Get(("seidel", row), () => Engine("the Seidel sums", () => SeidelEngine.Compute(c.Lens, c.System, row)));

    private static MeritOperandDefinition Seidel(string code, string name, SeidelKind kind, string what) => new()
    {
        Code = code, Name = name, Category = AberrationCategory, Source = AlphaPage + "; Seidel Coefficients",
        Description = $"Seidel {what} of surface Surf (0: the sum over the system), in waves at wavelength Wave: the Seidel coefficient (the Seidel Coefficients page's {code}) over the wavelength (SeidelEngine)."
            + (kind == SeidelKind.S5 ? " Surf 0 gives the real distortion in percent at the largest +y field, F-tan(θ) (Field Curvature and Distortion's engine), or with Absolute (Data1) 1 the real minus reference height in lens units." : ""),
        Int1 = "Surf", Int2 = "Wave", Data = kind == SeidelKind.S5 ? new[] { "Absolute" } : Array.Empty<string?>(),
        Compute = c =>
        {
            int surf = c.Surface(c.Row.Int1);
            int row = c.Wavelength(c.Row.Int2) - 1;
            if (kind == SeidelKind.S5)
            {
                int absolute = Whole(c.Row.DataAt(1), "Absolute");
                if (absolute is not (0 or 1)) throw new MeritOperandException($"Absolute {absolute} must be 0 (percent) or 1 (length).");
                if (surf == 0) return RealDistortion(c, row, absolute == 1);
                if (absolute != 0) throw new MeritOperandException("Absolute 1 applies with Surf 0 only (the help).");
            }
            var r = SeidelOf(c, row);
            if (surf > r.Surfaces.Count) throw new MeritOperandException($"Surf {surf} is the image surface; the Seidel sums run over surfaces 1 to {r.Surfaces.Count}.");
            var t = surf == 0 ? r.Sum : r.Surfaces[surf - 1].Terms;
            double s = kind switch { SeidelKind.S1 => t.S1, SeidelKind.S2 => t.S2, SeidelKind.S3 => t.S3, SeidelKind.S4 => t.S4, _ => t.S5 };
            return s / (r.WavelengthMicrometers * 1e-6);
        },
    };

    // ------------------------------------------------------------------ field scans (OP-37)

    private static void RefuseAfocal(MeritRowContext c, string code)
    {
        if (c.System.Aperture.AfocalImageSpace)
            throw new MeritOperandException($"the system is in afocal image space mode; the afocal form of {code} (angles or diopters) is not built.");
    }

    private static double Zone(double zone) =>
        zone is >= 0 and <= 1 ? zone : throw new MeritOperandException($"Zone {F(zone)} must lie in 0 to 1.");

    /// <summary>Longitudinal Aberration's crossing (meters) of zone <paramref name="zone"/> at a
    /// 0-based row, with the vignetting factors cleared as that analysis does.</summary>
    private static double Crossing(MeritRowContext c, int row, double zone)
    {
        RefuseAfocal(c, "the longitudinal aberration");
        var setup = For(c).Get("scan-cleared", () => Engine("the field-scan setup", () =>
            FieldScanCommon.Prepare(c.Lens, VignettingTools.Clear(c.System), new List<string>(), CancellationToken.None)));
        return For(c).Get(("crossing", row, zone), () =>
        {
            var failures = new List<string>();
            double v = Engine("the longitudinal aberration", () => LongitudinalAberrationAnalysis.Crossings(setup, new[] { row }, new[] { zone }, failures, CancellationToken.None))[0][0];
            return double.IsFinite(v) ? v : throw new MeritOperandException(string.Join(" ", failures));
        });
    }

    /// <summary>The field-curvature scan setup: vignetting factors cleared, fields normalised
    /// radially, real image height read as paraxial (as Field Curvature and Distortion does).</summary>
    private static FieldScanSetup CurvatureSetup(MeritRowContext c) =>
        For(c).Get("scan-curvature", () => Engine("the field-scan setup", () =>
        {
            var notes = new List<string>();
            var system = FieldScanCommon.ScanSystem(VignettingTools.Clear(c.System), realImageHeightAsParaxial: true, notes);
            return FieldScanCommon.Prepare(c.Lens, system, notes, CancellationToken.None);
        }));

    private static FieldCurvaturePoint CurvaturePoint(MeritRowContext c, int row, FieldScan scan, double h, DistortionReference reference)
    {
        RefuseAfocal(c, "the field curvature and distortion");
        var setup = CurvatureSetup(c);
        if (!(setup.ScanScale(scan) > 0))
            throw new MeritOperandException("every field is on the axis along the scan, so there is no field to scan.");
        return For(c).Get(("curvature", row, scan, h, reference), () =>
        {
            var curve = Engine("the field curvature", () => FieldCurvatureDistortion.Compute(setup, new[] { row }, scan, new[] { h }, reference, CancellationToken.None))[0];
            return curve.Points[0];
        });
    }

    private static double RealDistortion(MeritRowContext c, int row, bool absolute)
    {
        var setup = CurvatureSetup(c);
        if (setup.System.Fields.Type == FieldType.Angle && setup.ScanScale(FieldScan.PlusY) > 89)
            throw new MeritOperandException("the largest field is beyond 89°, where F-tan(θ) distortion is not defined.");
        var p = CurvaturePoint(c, row, FieldScan.PlusY, 1, DistortionReference.FTanTheta);
        return absolute ? c.Length(p.RealHeight - p.ReferenceHeight) : p.DistortionPercent;
    }

    private static MeritOperandDefinition FieldCurvature(string code, string name, bool sagittal) => new()
    {
        Code = code, Name = name, Category = AberrationCategory, Source = AlphaPage + "; Field Curvature and Distortion",
        Description = $"{(sagittal ? "Sagittal" : "Tangential")} focus from the image surface along its z, lens units, at field (Hx, Hy) and wavelength Wave, by Coddington's equations along the real chief ray (Field Curvature and Distortion's engine). The field must lie on the x or y axis (the engine scans along an axis).",
        Int2 = "Wave", Data = new[] { "Hx", "Hy" },
        Compute = c =>
        {
            int row = c.Wavelength(c.Row.Int2) - 1;
            double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2);
            var (fx, fy) = OpticsConventions.DenormalizeField((hx, hy), c.System.Fields.Scale);
            if (fx != 0 && fy != 0)
                throw new MeritOperandException($"the field (Hx {F(hx)}, Hy {F(hy)}) is off both axes; the field curvature engine scans along the x or y axis only.");
            var scan = fx != 0 ? (fx > 0 ? FieldScan.PlusX : FieldScan.MinusX) : (fy >= 0 ? FieldScan.PlusY : FieldScan.MinusY);
            var setup = CurvatureSetup(c);
            double scale = setup.ScanScale(scan);
            double value = Math.Abs(fx != 0 ? fx : fy);
            double h = value == 0 ? 0 : value / scale;
            var p = CurvaturePoint(c, row, scan, h, DistortionReference.FTanTheta);
            double v = sagittal ? p.Sagittal : p.Tangential;
            return double.IsFinite(v) ? c.Length(v) : throw new MeritOperandException("Coddington's equations could not be traced along the chief ray at this field.");
        },
    };

    // ------------------------------------------------------------------ RMS (OP-40)

    private static MeritOperandDefinition Rms(string code, string name, bool spot, bool centroid, bool gaussian) => new()
    {
        Code = code, Name = name, Category = AberrationCategory, Source = AlphaPage + (gaussian ? "; Gaussian Quadrature" : "; Rectangular Array"),
        Description = (spot ? "RMS spot radius, lens units," : "RMS wavefront error, waves,")
            + (centroid ? (spot ? " about the weighted centroid" : " with piston and tilt removed") : (spot ? " about the chief ray" : " with piston removed"))
            + $" of field (Hx, Hy) at wavelength Wave (0: polychromatic), by OP-40's RMS core over "
            + (gaussian ? "Gaussian quadrature of Ring rings (1 to 20) and 4 × Ring arms (unvignetted pupils)." : "a rectangular array of Samp × Samp rays per pupil quadrant (2·Samp across); failed rays are left out (vignetting considered)."),
        Int1 = gaussian ? "Ring" : "Samp", Int2 = "Wave", Data = new[] { "Hx", "Hy" },
        Compute = c =>
        {
            int density = c.Row.Int1;
            PupilQuadratureRule rule;
            if (gaussian)
            {
                if (density < 1 || density > PupilQuadrature.MaximumRings)
                    throw new MeritOperandException($"Ring {density} must be 1 to {PupilQuadrature.MaximumRings}.");
                rule = PupilQuadrature.Gaussian(density);
            }
            else
            {
                if (density < 1 || density > 500) throw new MeritOperandException($"Samp {density} must be 1 to 500 (2·Samp rays across, at most 1001).");
                rule = PupilQuadrature.Rectangular(2 * density);
            }
            var rows = WaveRows(c, c.Row.Int2);
            RefuseAfocal(c, code);
            var setup = For(c).Get("rms-setup", () => Engine("the RMS setup", () =>
                RmsCore.Prepare(c.Lens, c.System, removeVignettingFactors: false, code, new List<string>(), CancellationToken.None)));
            double hx = c.Row.DataAt(1), hy = c.Row.DataAt(2);
            var (fx, fy) = OpticsConventions.DenormalizeField((hx, hy), c.System.Fields.Scale);
            var point = new RmsPoint { FieldX = fx, FieldY = fy, Rows = rows, Polychromatic = c.Row.Int2 == 0, Label = $"Hx {F(hx)}, Hy {F(hy)}" };
            var quantity = spot ? RmsQuantity.SpotRadius : RmsQuantity.WavefrontRms;
            var v = For(c).Get(("rms", gaussian, density, c.Row.Int2, hx, hy, spot, centroid), () => Engine("the RMS", () =>
                RmsCore.Evaluate(setup, point, quantity, centroid ? RmsReference.Centroid : RmsReference.ChiefRay, rule, CancellationToken.None)));
            if (!v.Succeeded) throw new MeritOperandException($"the RMS of field (Hx {F(hx)}, Hy {F(hy)}) has no value: {v.Failure}");
            return spot ? c.Length(v.Value) : v.Value;
        },
    };

    // ------------------------------------------------------------------ Zernike

    private static double Zernike(MeritRowContext c)
    {
        int term = c.Row.Int1;
        if (term <= 0) throw new MeritOperandException($"Term {term}: the extra fit data (P-V, RMS, variance, Strehl, fit errors) are not built; give a term number 1 or more.");
        int row = c.Wavelength(c.Row.Int2) - 1;
        int grid = Grid(Whole(c.Row.DataAt(1), "Samp"));
        int field = FieldRow(c, c.Row.DataAt(2));
        int type = Whole(c.Row.DataAt(3), "Type");
        if (type == 2) throw new MeritOperandException("Type 2 (annular Zernike) is not built.");
        if (type is not (0 or 1)) throw new MeritOperandException($"Type {type} must be 0 (Fringe) or 1 (Standard).");
        if (c.Row.DataAt(4) != 0) throw new MeritOperandException("Epsilon applies to annular coefficients, which are not built; set it to 0.");
        NotBuiltFlag(c.Row.DataAt(5), "Vertex?", "the OPD referenced to the surface vertex");
        int maxTerm = type == 0 ? 37 : 231;
        if (term > maxTerm) throw new MeritOperandException($"Term {term} exceeds {maxTerm}, the last {(type == 0 ? "Fringe" : "Standard")} term the help offers.");
        int count = Math.Max(11, term);
        var fit = For(c).Get(("zern", row, grid, field, type, count), () =>
        {
            var basis = new ZernikeBasis(type == 0 ? ZernikeOrdering.Fringe : ZernikeOrdering.Standard, count);
            var pupil = PsfEngine.Nodes(grid).Pupil;
            var options = new OpdOptions { Reference = OpdReference.ExitPupil, ReferencePoint = OpdReferencePoint.ChiefRay };
            var r = Engine("the Zernike fit", () => FieldZernike.Fit(c.Lens, c.System, field, row, basis, pupil, options, CancellationToken.None));
            return r.Fit is null ? throw new MeritOperandException($"the Zernike fit of field {field + 1} failed: {r.Failure}.") : r;
        });
        return fit.Z(term);
    }
}
