using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.EnclosedEnergy;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Merit;

// OP-55A: MTF data (help: MTF Data), PSF/Strehl, encircled energy, relative illumination and the
// best-fit sphere: OP-39's MtfEngine, OP-38's Huygens PSF run, OP-41's enclosed energy, OP-37's
// relative illumination and OP-43's best-fit sphere.
public static partial class ImagingOperands
{
    private const string MtfCategory = "MTF data";
    private const string MtfPage = "MTF Data; Optimization Operands Alphabetically";

    private enum Pick { Tangential, Sagittal, Average, Minimum, Maximum }

    private static double Of(MtfValue v, Pick pick) => pick switch
    {
        Pick.Tangential => v.Tangential,
        Pick.Sagittal => v.Sagittal,
        Pick.Average => v.Average,
        Pick.Minimum => Math.Min(v.Tangential, v.Sagittal),
        _ => Math.Max(v.Tangential, v.Sagittal),
    };

    private static string Label(Pick pick) => pick switch
    {
        Pick.Tangential => "tangential",
        Pick.Sagittal => "sagittal",
        Pick.Average => "average of tangential and sagittal",
        Pick.Minimum => "smaller of tangential and sagittal",
        _ => "larger of tangential and sagittal",
    };

    private static IEnumerable<MeritOperandDefinition> ImageQualityDefinitions()
    {
        // Diffraction and geometric MTF: the minimum and maximum of MtfOperands' cached value.
        yield return MtfPair("MTFN", "Diffraction MTF minimum", Pick.Minimum, geometric: false);
        yield return MtfPair("MTFX", "Diffraction MTF maximum", Pick.Maximum, geometric: false);
        yield return MtfPair("GMTN", "Geometric MTF minimum", Pick.Minimum, geometric: true);
        yield return MtfPair("GMTX", "Geometric MTF maximum", Pick.Maximum, geometric: true);

        // Square-wave MTF (FFT).
        yield return SquareWave("MSWT", "Square-wave MTF tangential", Pick.Tangential);
        yield return SquareWave("MSWS", "Square-wave MTF sagittal", Pick.Sagittal);
        yield return SquareWave("MSWA", "Square-wave MTF average", Pick.Average);
        yield return SquareWave("MSWN", "Square-wave MTF minimum", Pick.Minimum);
        yield return SquareWave("MSWX", "Square-wave MTF maximum", Pick.Maximum);

        // Huygens MTF.
        yield return Huygens("MTHT", "Huygens MTF tangential", Pick.Tangential);
        yield return Huygens("MTHS", "Huygens MTF sagittal", Pick.Sagittal);
        yield return Huygens("MTHA", "Huygens MTF average", Pick.Average);
        yield return Huygens("MTHN", "Huygens MTF minimum", Pick.Minimum);
        yield return Huygens("MTHX", "Huygens MTF maximum", Pick.Maximum);

        yield return new MeritOperandDefinition
        {
            Code = "STRH", Name = "Strehl ratio (Huygens)", Category = "PSF/Strehl ratio data", Source = AlphaPage + "; Huygens PSF",
            Description = "Strehl ratio of field Field at wavelength Wave (0: polychromatic) by OP-38's Huygens PSF: pupil and image grids 32·2^(Samp−1), image delta half the help's default λF/√n_p (the help: \"2x smaller than the nominal default\"), centred on the chief ray. Pol? and All Conf? 1 are not built.",
            Int1 = "Samp", Int2 = "Wave", Data = new[] { "Field", "Pol?", "All Conf?" },
            Compute = c =>
            {
                int grid = Grid(c.Row.Int1);
                var waves = WaveRows(c, c.Row.Int2);
                int field = FieldRow(c, c.Row.DataAt(1));
                NotBuiltFlag(c.Row.DataAt(2), "Pol?", "polarization");
                NotBuiltFlag(c.Row.DataAt(3), "All Conf?", "the sum over configurations");
                return For(c).Get(("strh", grid, c.Row.Int2, field), () => Engine($"the Huygens Strehl ratio of field {field + 1}", () =>
                {
                    int centre = waves.Length > 1 ? c.System.Wavelengths.PrimaryIndex : waves[0];
                    var options = PsfRuns.Options(false, true, false);
                    var pupil = PsfEngine.BuildHuygens(c.Lens, c.System, field, centre, grid, options, null, false, CancellationToken.None);
                    double longest = waves.Max(w => c.System.Wavelengths.Wavelengths[w].Micrometers);
                    double centreUm = c.System.Wavelengths.Wavelengths[centre].Micrometers;
                    double lambda = waves.Length > 1 ? pupil.Wavelength * (longest / centreUm) : pupil.Wavelength;
                    double delta = PsfEngine.DefaultHuygensDelta(lambda, pupil.FNumber, grid) / 2;
                    return PsfRuns.Huygens(c.Lens, c.System, field, waves, grid, grid, delta, false, true, false, null, CancellationToken.None).Strehl;
                }));
            },
        };

        yield return Diffraction("DENC", "Diffraction encircled energy (distance)", distance: true);
        yield return Diffraction("DENF", "Diffraction encircled energy (fraction)", distance: false);
        yield return Geometric("GENC", "Geometric encircled energy (distance)", distance: true);
        yield return Geometric("GENF", "Geometric encircled energy (fraction)", distance: false);

        yield return Illumination("RELI", "Relative illumination", effectiveF: false);
        yield return Illumination("EFNO", "Effective F/#", effectiveF: true);

        yield return new MeritOperandDefinition
        {
            Code = "BFSD", Name = "Best fit sphere data", Category = "Best fit sphere data", Source = AlphaPage + "; Sag Table",
            Description = "Minimum-volume best-fit sphere of surface Surf over MinR to MaxR (Data1, Data2, lens units; both 0: 0 to the semi-diameter), by the Sag Table's fit. Data (Int2): 0 curvature (1/lens unit), 1 radius, 2 vertex offset, 3 maximum depth to remove, 4 volume to remove (cubic lens units), 5 maximum slope difference, 6 RMS depth to remove, 7 RMS slope difference.",
            Int1 = "Surf", Int2 = "Data", Data = new[] { "MinR", "MaxR" },
            Compute = BestFit,
        };
    }

    // ------------------------------------------------------------------ MTF

    private static MeritOperandDefinition MtfPair(string code, string name, Pick pick, bool geometric) => new()
    {
        Code = code, Name = name, Category = MtfCategory, Source = MtfPage,
        Description = $"The {Label(pick)} {(geometric ? "geometric" : "diffraction")} MTF; columns, sampling and refusals as {(geometric ? "GMTA" : "MTFA")} (MtfOperands, the same cached value).",
        Int1 = "Samp", Int2 = "Wave",
        Data = geometric ? new[] { "Field", "Freq", "!Scl", "Grid" } : new[] { "Field", "Freq", "Grid", "Data Type" },
        Compute = c => Of(MtfOperands.MtfOf(c, geometric), pick),
    };

    private static void MtfField(MeritRowContext c, out int field, out double frequency)
    {
        field = Whole(c.Row.DataAt(1), "Field");
        if (field == 0) throw new MeritOperandException("Field 0 (the diffraction-limited MTF) is not built; give a field number.");
        field = FieldRow(c, field);
        frequency = c.Row.DataAt(2);
        if (!(frequency >= 0) || !double.IsFinite(frequency))
            throw new MeritOperandException($"Freq {F(frequency)} must be finite and not negative.");
    }

    private static MeritOperandDefinition SquareWave(string code, string name, Pick pick) => new()
    {
        Code = code, Name = name, Category = MtfCategory, Source = MtfPage,
        Description = $"The {Label(pick)} square-wave response of field Field at Freq (MTF units), wavelength Wave (0: polychromatic), by OP-39's FFT engine on a 32·2^(Samp−1) pupil grid (MTFA's columns; Grid read, Data Type 0 only).",
        Int1 = "Samp", Int2 = "Wave", Data = new[] { "Field", "Freq", "Grid", "Data Type" },
        Compute = c =>
        {
            int grid = Grid(c.Row.Int1);
            var waves = WaveRows(c, c.Row.Int2);
            MtfField(c, out int field, out double frequency);
            if (c.Row.DataAt(4) != 0) throw new MeritOperandException($"Data Type {F(c.Row.DataAt(4))} is not built; only 0 (the modulation) is.");
            var v = For(c).Get(("msw", grid, c.Row.Int2, field, frequency), () => Engine($"the square-wave MTF of field {field + 1}", () =>
                MtfEngine.Evaluate(c.Lens, c.System, field, waves, frequency, new MtfOptions { Method = MtfMethod.Fft, Sampling = grid, SquareWave = true })));
            return Of(v, pick);
        },
    };

    private static MeritOperandDefinition Huygens(string code, string name, Pick pick) => new()
    {
        Code = code, Name = name, Category = MtfCategory, Source = MtfPage + "; Huygens MTF",
        Description = $"The {Label(pick)} Huygens MTF of field Field at Freq (MTF units), wavelength Wave (0: polychromatic), by OP-39's Huygens engine with pupil and image grids 32·2^(Samp−1) and image delta Ima Delta µm (0: the help's default). Pol? and All Conf? 1 are not built.",
        Int1 = "Samp", Int2 = "Wave", Data = new[] { "Field", "Freq", "Pol?", "All Conf?", "Ima Delta" },
        Compute = c =>
        {
            int grid = Grid(c.Row.Int1);
            var waves = WaveRows(c, c.Row.Int2);
            MtfField(c, out int field, out double frequency);
            NotBuiltFlag(c.Row.DataAt(3), "Pol?", "polarization");
            NotBuiltFlag(c.Row.DataAt(4), "All Conf?", "the sum over configurations");
            double delta = c.Row.DataAt(5);
            if (!(delta >= 0) || !double.IsFinite(delta)) throw new MeritOperandException($"Ima Delta {F(delta)} must be 0 or a positive number of micrometers.");
            var v = For(c).Get(("mth", grid, c.Row.Int2, field, frequency, delta), () => Engine($"the Huygens MTF of field {field + 1}", () =>
                MtfEngine.Evaluate(c.Lens, c.System, field, waves, frequency,
                    new MtfOptions { Method = MtfMethod.Huygens, Sampling = grid, ImageSampling = grid, ImageDelta = delta * 1e-6 })));
            return Of(v, pick);
        },
    };

    // ------------------------------------------------------------------ enclosed energy

    private static EnclosedEnergyType EeType(double value)
    {
        int t = Whole(value, "Type");
        return t switch
        {
            1 => EnclosedEnergyType.Encircled,
            2 => EnclosedEnergyType.XOnly,
            3 => EnclosedEnergyType.YOnly,
            4 => EnclosedEnergyType.Ensquared,
            _ => throw new MeritOperandException($"Type {t} must be 1 (encircled), 2 (x only), 3 (y only) or 4 (ensquared)."),
        };
    }

    /// <summary>The smallest size (meters) at which a non-decreasing <paramref name="fraction"/>
    /// reaches <paramref name="target"/>, by bisection between 0 and <paramref name="high"/>
    /// (doubled while the fraction there is short, at most 40 times).</summary>
    private static double SizeAt(Func<double, double> fraction, double target, double high, bool extend, string what)
    {
        if (!(target > 0 && target < 1)) throw new MeritOperandException($"Frac {F(target)} must lie strictly between 0 and 1.");
        if (!(high > 0)) high = 1e-9;
        int doublings = 0;
        while (fraction(high) < target)
        {
            if (!extend || ++doublings > 40)
                throw new MeritOperandException($"the {what} does not reach {F(target)} within {F(high * 1e6)} µm (the largest size the sampling allows); raise the sampling.");
            high *= 2;
        }
        double low = 0;
        for (int i = 0; i < 200 && high - low > 1e-12 * high; i++)
        {
            double mid = 0.5 * (low + high);
            if (fraction(mid) >= target) high = mid; else low = mid;
        }
        return high;
    }

    private static MeritOperandDefinition Diffraction(string code, string name, bool distance) => new()
    {
        Code = code, Name = name, Category = "Encircled energy", Source = AlphaPage + "; Diffraction (enclosed energy)",
        Description = (distance ? "Distance, µm, at which the diffraction enclosed energy reaches Frac" : "Fraction of the diffraction enclosed energy within Dist µm")
            + " of the reference point, field Field, wavelength Wave (0: polychromatic), on a 32·2^(Samp−1) pupil grid by OP-41's engine (FFT kernel). Type 1 encircled, 2 x only, 3 y only, 4 ensquared (half-widths); Refp 0 chief ray, 1 centroid, 2 vertex. Huygens (Refp 3 to 5) and its I Samp and I Delta are not built; afocal units are not built.",
        Int1 = "Samp", Int2 = "Wave", Data = new[] { "Field", distance ? "Frac" : "Dist", "Type", "Refp", "I Samp", "I Delta" },
        Compute = c =>
        {
            RefuseAfocal(c, code);
            int grid = Grid(c.Row.Int1);
            var waves = WaveRows(c, c.Row.Int2);
            int field = FieldRow(c, c.Row.DataAt(1));
            var type = EeType(c.Row.DataAt(3));
            int refp = Whole(c.Row.DataAt(4), "Refp");
            if (refp is >= 3 and <= 5) throw new MeritOperandException($"Refp {refp} (Huygens enclosed energy) is not built; use 0 (chief ray), 1 (centroid) or 2 (vertex).");
            if (refp is < 0 or > 2) throw new MeritOperandException($"Refp {refp} must be 0 (chief ray), 1 (centroid) or 2 (vertex).");
            var reference = (EnclosedEnergyReference)refp;
            var setup = For(c).Get(("denc", grid, c.Row.Int2, field, refp), () => Engine($"the diffraction enclosed energy of field {field + 1}", () =>
                DiffractionEnclosedEnergy.Prepare(c.Lens, c.System, field, waves, grid, reference)));
            if (!distance)
            {
                double dist = c.Row.DataAt(2);
                if (!(dist >= 0) || !double.IsFinite(dist)) throw new MeritOperandException($"Dist {F(dist)} must be 0 or a positive number of micrometers.");
                double size = dist * 1e-6;
                if (size > setup.MaximumSize(type))
                    throw new MeritOperandException($"Dist {F(dist)} µm is beyond the largest size the sampling allows ({F(setup.MaximumSize(type) * 1e6)} µm); raise the sampling.");
                return setup.Fraction(type, size);
            }
            return SizeAt(s => setup.Fraction(type, s), c.Row.DataAt(2), setup.MaximumSize(type), extend: false, "diffraction enclosed energy") * 1e6;
        },
    };

    private static MeritOperandDefinition Geometric(string code, string name, bool distance) => new()
    {
        Code = code, Name = name, Category = "Encircled energy", Source = AlphaPage + "; Geometric (enclosed energy)",
        Description = (distance ? "Distance, µm, at which the geometric enclosed energy reaches Frac" : "Fraction of the geometric enclosed energy within Dist µm")
            + " of the reference point at the image, field Field, wavelength Wave (0: polychromatic), rays on a 32·2^(Samp−1) pupil grid (OP-41's engine). Type 1 encircled, 2 x only, 3 y only, 4 ensquared; Refp 0 chief ray, 1 centroid, 2 vertex, 3 middle. No Diff Lim 0 multiplies by the Airy disk's enclosed energy at the reference field's real working F/# (encircled only, as the analysis does); afocal units are not built.",
        Int1 = "Samp", Int2 = "Wave", Data = new[] { "Field", distance ? "Frac" : "Dist", "Type", "Refp", "No Diff Lim" },
        Compute = c =>
        {
            RefuseAfocal(c, code);
            int grid = Grid(c.Row.Int1);
            var waves = WaveRows(c, c.Row.Int2);
            int field = FieldRow(c, c.Row.DataAt(1));
            var type = EeType(c.Row.DataAt(3));
            int refp = Whole(c.Row.DataAt(4), "Refp");
            if (refp is < 0 or > 3) throw new MeritOperandException($"Refp {refp} must be 0 (chief ray), 1 (centroid), 2 (vertex) or 3 (middle).");
            bool multiply = c.Row.DataAt(5) == 0;
            if (multiply && type != EnclosedEnergyType.Encircled)
                throw new MeritOperandException("No Diff Lim 0 scales by the diffraction limit, which applies to the encircled type only (the Airy curve is radial); set No Diff Lim to 1 for x only, y only or ensquared.");
            var spot = For(c).Get(("genc", grid, c.Row.Int2, field, refp), () => Engine($"the geometric enclosed energy of field {field + 1}", () =>
                GeometricEnclosedEnergy.Trace(c.Lens, c.System, field, waves, grid, (EnclosedEnergyReference)refp, null, 1, CancellationToken.None)));
            Func<double, double> fraction = s => spot.Fraction(type, s);
            if (multiply)
            {
                var airy = For(c).Get(("airy", c.Row.Int2), () => Engine("the diffraction limit", () =>
                    EnclosedEnergyRuns.AiryParameters(c.Lens, c.System, waves, code)));
                fraction = s => spot.Fraction(type, s) * AiryEnclosedEnergy.AtRadius(s, airy.Lambda, airy.FNumber);
            }
            if (!distance)
            {
                double dist = c.Row.DataAt(2);
                if (!(dist >= 0) || !double.IsFinite(dist)) throw new MeritOperandException($"Dist {F(dist)} must be 0 or a positive number of micrometers.");
                return fraction(dist * 1e-6);
            }
            return SizeAt(fraction, c.Row.DataAt(2), spot.MaximumDistance(type), extend: true, "geometric enclosed energy") * 1e6;
        },
    };

    // ------------------------------------------------------------------ relative illumination

    private static MeritOperandDefinition Illumination(string code, string name, bool effectiveF) => new()
    {
        Code = code, Name = name, Category = "Relative illumination", Source = AlphaPage + "; Relative Illumination",
        Description = (effectiveF ? "Effective F/# √(π/(4A)) of field Field" : "Relative illumination A(field)/A(0, 0) of field Field against the on-axis point (may exceed 1)")
            + ", A the projected solid angle of the exit pupil by OP-37's relative illumination engine at wavelength Wave, Samp its ray density (5 to 400); vignetting factors are replaced by apertures (the help: \"always removed\"). The field must lie on the x or y axis (the engine scans along an axis). Pol? 1 is not built.",
        Int1 = "Samp", Int2 = "Wave", Data = new[] { "Field", "Pol?" },
        Compute = c =>
        {
            int samp = c.Row.Int1;
            if (samp < 5 || samp > 400) throw new MeritOperandException($"Samp {samp} must be 5 to 400 (the relative illumination's ray density; the help: the minimum is 5).");
            int row = c.Wavelength(c.Row.Int2) - 1;
            int field = FieldRow(c, c.Row.DataAt(1));
            NotBuiltFlag(c.Row.DataAt(2), "Pol?", "polarization");
            RefuseAfocal(c, code);
            var setup = For(c).Get("scan-illumination", () => Engine("the relative illumination setup", () =>
            {
                var lens = c.Lens;
                var system = c.System;
                if (system.Fields.Points.Any(p => p.IsVignetted))
                {
                    var r = VignettingTools.ReplaceWithApertures(lens, system);
                    lens = r.Lens;
                    system = r.System;
                }
                var notes = new List<string>();
                system = FieldScanCommon.ScanSystem(system, realImageHeightAsParaxial: false, notes);
                return FieldScanCommon.Prepare(lens, system, notes, CancellationToken.None);
            }));
            var point = setup.System.Fields.Points[field];
            if (point.X != 0 && point.Y != 0)
                throw new MeritOperandException($"field {field + 1} is off both axes; the relative illumination engine scans along the x or y axis only.");
            var scan = point.X != 0 ? (point.X > 0 ? FieldScan.PlusX : FieldScan.MinusX) : (point.Y >= 0 ? FieldScan.PlusY : FieldScan.MinusY);
            double value = Math.Abs(point.X != 0 ? point.X : point.Y);
            double scale = setup.ScanScale(scan);
            double h = value == 0 ? 0 : value / scale;
            var result = For(c).Get(("reli", samp, row, scan, h), () => Engine("the relative illumination", () =>
                RelativeIllumination.Compute(setup, row, scan, samp, new[] { 0, h }, null, CancellationToken.None)));
            if (effectiveF) return result.EffectiveFNumber[1];
            double a0 = result.ProjectedSolidAngle[0];
            return a0 > 0 ? result.ProjectedSolidAngle[1] / a0 : throw new MeritOperandException("the on-axis point receives no light, so the relative illumination is undefined.");
        },
    };

    // ------------------------------------------------------------------ best-fit sphere

    private static double BestFit(MeritRowContext c)
    {
        int surf = c.Surface(c.Row.Int1);
        int data = c.Row.Int2;
        if (data < 0 || data > 7) throw new MeritOperandException($"Data {data} must be 0 to 7.");
        if (surf < 1 || surf > c.Lens.ImageIndex) throw new MeritOperandException($"Surf {surf} must be 1 to {c.Lens.ImageIndex}.");
        var s = c.Lens.Surfaces[surf];
        if (s.IsCoordinateBreak) throw new MeritOperandException($"surface {surf} is a coordinate break, which has no shape.");
        double rMinU = c.Row.DataAt(1), rMaxU = c.Row.DataAt(2);
        var fit = For(c).Get(("bfsd", surf, rMinU, rMaxU), () =>
        {
            double sd = s.SemiDiameter ?? Engine("the semi-diameters", () => SemiDiameterSizing.Compute(c.Lens, c.System).Clear[surf]);
            if (!(sd > 0) || !double.IsFinite(sd))
                throw new MeritOperandException($"surface {surf}'s semi-diameter is {F(c.Length(sd))}; it must be positive.");
            double rMin = OpticsConventions.ToMeters(rMinU, c.Unit), rMax = OpticsConventions.ToMeters(rMaxU, c.Unit);
            if (rMin == 0 && rMax == 0) rMax = sd;
            if (!(rMin >= 0) || !(rMax > rMin)) throw new MeritOperandException($"MinR {F(rMinU)} and MaxR {F(rMaxU)} must satisfy 0 ≤ MinR < MaxR.");
            var probe = new SurfaceProbe(s, SurfaceShapeAnalysisBase<SagTableSettings>.DifferenceStepFraction * sd);
            return Engine($"the best-fit sphere of surface {surf}", () =>
            {
                int air = BestFitSphere.AirSign(c.Lens, c.System, surf, false, out _);
                return BestFitSphere.Fit(probe, rMin, rMax, BestFitSphereCriterion.MinimumVolume, air, $"BFSD, surface {surf}");
            });
        });
        double unit = c.Length(1);
        return data switch
        {
            0 => c.Curvature(fit.Curvature),
            1 => double.IsFinite(fit.Radius) ? c.Length(fit.Radius) : throw new MeritOperandException("the best-fit sphere is a plane; its radius is infinite (use Data 0, the curvature)."),
            2 => c.Length(fit.VertexOffset),
            3 => c.Length(fit.MaxDepthToRemove),
            4 => fit.VolumeToRemove * unit * unit * unit,
            5 => fit.MaxSlopeDeviation,
            6 => c.Length(fit.RmsDepthToRemove),
            _ => fit.RmsSlopeDeviation,
        };
    }
}
