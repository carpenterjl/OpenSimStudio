using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.MultiConfig;

/// <summary>
/// Every multi-configuration operand of OpticStudio's table, by code (OpticStudio 2025 R2 help,
/// "Multi-Configuration Operands", plans/sources/optics/op-30-help/Multi_Configuration_Operands.html;
/// 116 codes, GPEX and GPEY obsolete). Codes, argument meanings and the integer codes of the
/// type operands (APTP 0–10, SATP 0–5, FLTP 0–4, APDT 0–2, RAAM 0–2, MTFU 0–1, CAOR/CBOR 0–1,
/// CROR 0–3) are the help's; the enumerations of OpenSim.Optics carry the same integers
/// (<see cref="ApertureType"/>, <see cref="FieldType"/>, <see cref="ApodizationType"/>,
/// <see cref="RayAimingMode"/>, <see cref="MtfUnit"/>, <see cref="TiltDecenterOrder"/>,
/// <see cref="CoordinateReturnMode"/>).
/// <para>
/// Values are in the saved record's units: lens units for lengths, 1/lens unit for CRVT,
/// degrees for tilts and angles, micrometers for wavelengths, °C and atmospheres. Surface
/// numbers count from the object (0), field and wavelength numbers from 1, as in OpticStudio.
/// Setting a surface cell (CRVT, THIC, GLSS, CONN, SDIA, …) removes a solve or pickup the Lens
/// Data Editor has on that cell, keeping a variable flag, so the operand's value is the one
/// traced in that configuration (docs/Optics-Conventions.md section 24).
/// </para>
/// </summary>
public static class MultiConfigOperands
{
    /// <summary>OpticStudio's limit on operand strings (help: Comment About Operands that Define
    /// Character Strings).</summary>
    public const int MaxTextLength = 32;

    /// <summary>OpticStudio's limit on the number of configurations (help: Defining the Number of
    /// Configurations: "The maximum number of configurations allowed in a lens file is 160").</summary>
    public const int MaxConfigurations = 160;

    /// <summary>The unused row's code, the default for a new row.</summary>
    public const string Off = "MOFF";

    private static readonly Dictionary<string, MultiConfigOperand> ByCode = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<MultiConfigOperand> List = new();

    /// <summary>Every operand in the help's (alphabetical) order.</summary>
    public static IReadOnlyList<MultiConfigOperand> All => List;

    /// <summary>The operands this build applies.</summary>
    public static IEnumerable<MultiConfigOperand> Supported => List.Where(o => o.IsSupported);

    /// <summary>The operand with this code (any case), or null when the help has none.</summary>
    public static MultiConfigOperand? Find(string? code) =>
        ByCode.TryGetValue(string.IsNullOrWhiteSpace(code) ? Off : code.Trim(), out var op) ? op : null;

    // ------------------------------------------------------------------ registry

    private static readonly string[] Surface1 = { "Surface" };
    private static readonly string[] Field1 = { "Field" };
    private static readonly string[] Wave1 = { "Wavelength" };
    private static readonly string[] None = Array.Empty<string>();

    static MultiConfigOperands()
    {
        // ---- not supported: no target in OpenSim today ----
        const string inventor = "Autodesk Inventor and Creo Parametric parts have no equivalent in OpenSim (plan section 3).";
        const string nsc = "non-sequential objects are not built (OP-70 on).";
        Unsupported("AICN", "iPartFactory number of an Autodesk Inventor part", MultiConfigValueKind.Integer, new[] { "Surface", "Object" }, inventor);
        Unsupported("CPCN", "Family table instance number of a Creo Parametric part", MultiConfigValueKind.Integer, new[] { "Surface", "Object" }, inventor);
        Unsupported("FCMM", "Field comment", MultiConfigValueKind.Text, Field1, "the saved fields have no comment in this build.");
        Unsupported("GCRS", "Global coordinate reference surface", MultiConfigValueKind.Integer, None, "the global coordinate reference surface is not part of this build's saved system data.");
        Unsupported("GPEX", "Obsolete", MultiConfigValueKind.Number, None, "obsolete in OpticStudio; the help gives it no meaning.");
        Unsupported("GPEY", "Obsolete", MultiConfigValueKind.Number, None, "obsolete in OpticStudio; the help gives it no meaning.");
        Unsupported("GQPO", "Gaussian quadrature obscuration of the default merit function", MultiConfigValueKind.Number, None, "the default merit function's pupil sampling is not saved with the system (OP-54).");
        Unsupported("HLGV", "Hologram construction-file variable", MultiConfigValueKind.Number, new[] { "Surface", "File", "Variable" }, "the Optically Fabricated Hologram surface is not built.");
        Unsupported("IGTO", "Ignore tolerance operands from line to line", MultiConfigValueKind.Integer, new[] { "First operand", "Last operand" }, "the tolerance analyses that would read it are not built (OP-63 builds the operands only).");
        Unsupported("MCHI", "Huygens integral method", MultiConfigValueKind.Integer, None, "the Huygens integral method is not part of this build's saved system data.");
        Unsupported("NCOM", "Non-sequential object comment", MultiConfigValueKind.Text, new[] { "Surface", "Object" }, nsc);
        Unsupported("NCOT", "Non-sequential object face coating", MultiConfigValueKind.Text, new[] { "Surface", "Object", "Face" }, nsc);
        Unsupported("NGLS", "Non-sequential object material", MultiConfigValueKind.Text, new[] { "Surface", "Object" }, nsc);
        Unsupported("NPAR", "Non-sequential object parameter", MultiConfigValueKind.Number, new[] { "Surface", "Object", "Parameter" }, nsc);
        Unsupported("NPOS", "Non-sequential object position", MultiConfigValueKind.Number, new[] { "Surface", "Object", "Position" }, nsc);
        Unsupported("NPRO", "Non-sequential object property", MultiConfigValueKind.Number, new[] { "Surface", "Object", "Property" }, nsc);
        Unsupported("OPDR", "OPD reference", MultiConfigValueKind.Integer, None, "the OPD reference is not part of this build's saved system data.");
        Unsupported("PXAR", "Physical optics: use X-axis reference", MultiConfigValueKind.Integer, Surface1, "physical optics settings are not built.");
        Unsupported("UDAF", "User-defined aperture file", MultiConfigValueKind.Text, Surface1, "user-defined apertures are saved as vertex lists, not as aperture files.");

        // ---- no target by design ----
        Add(new MultiConfigOperand(Off, "Unused row; may hold a comment", MultiConfigValueKind.Number, None) { HasNoTarget = true });
        Add(new MultiConfigOperand("HOLD", "Holds data without effect", MultiConfigValueKind.Number, None) { HasNoTarget = true });
        Add(new MultiConfigOperand("CWGT", "Configuration weight, relative to the other configurations", MultiConfigValueKind.Number, None) { HasNoTarget = true });
        Add(new MultiConfigOperand("PUCN", "Picks up the rows below from a previous configuration", MultiConfigValueKind.Integer, None) { HasNoTarget = true });

        // ---- surface data ----
        // OP-32: the composite add-on placement (help: "Decenter value in X direction for composite
        // add-on surface", and so for CSDY, CSTX, CSTY, CSTZ). OpticStudio's add-on is its own
        // surface row; here it is an entry of its base's list, so the operand takes the base
        // surface and the add-on's number (1-based) in its list.
        CompositeAddOnNumber("CSDX", "Composite add-on surface decenter x", a => a.DecenterX, (a, v) => a with { DecenterX = v });
        CompositeAddOnNumber("CSDY", "Composite add-on surface decenter y", a => a.DecenterY, (a, v) => a with { DecenterY = v });
        CompositeAddOnNumber("CSTX", "Composite add-on surface tilt x", a => a.TiltX, (a, v) => a with { TiltX = v });
        CompositeAddOnNumber("CSTY", "Composite add-on surface tilt y", a => a.TiltY, (a, v) => a with { TiltY = v });
        CompositeAddOnNumber("CSTZ", "Composite add-on surface tilt z", a => a.TiltZ, (a, v) => a with { TiltZ = v });

        SurfaceNumber("CRVT", "Curvature", SavedSurfaces.RadiusColumn,
            s => SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value,
            (s, v) => s with { Radius = v == 0 ? null : 1 / v });
        Add(new MultiConfigOperand("THIC", "Thickness", MultiConfigValueKind.Number, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(Surf(sys, n, "THIC") is { } s && s.ThicknessIsInfinite ? double.PositiveInfinity : Surf(sys, n, "THIC").Thickness),
            Setter = (sys, n, _, _, v) =>
            {
                if (double.IsNaN(v.Number) || double.IsNegativeInfinity(v.Number)) throw new ArgumentException($"THIC: the thickness must be a number or +infinity, not {v}.");
                return WithSurface(sys, n, "THIC", s => DropFlags(double.IsPositiveInfinity(v.Number)
                    ? s with { ThicknessIsInfinite = true }
                    : s with { Thickness = v.Number, ThicknessIsInfinite = false }, SavedSurfaces.ThicknessColumn));
            },
            Targets = sys => SurfacesWhere(sys, (j, s, count) => j < count - 1),
        });
        SurfaceText("GLSS", "Glass", SavedSurfaces.MaterialColumn,
            s => s.Material ?? "",
            (s, t) => t.Trim().Length == 0 ? s with { Material = null, Index = null, ModelGlass = null }
                : s with { Material = t.Trim(), Index = null, ModelGlass = null },
            s => !string.IsNullOrWhiteSpace(s.Material));
        SurfaceNumber("CONN", "Conic constant", SavedSurfaces.ConicColumn, s => s.Conic, (s, v) => s with { Conic = v });
        SurfaceNumber("SDIA", "Clear semi-diameter or semi-diameter", SavedSurfaces.SemiDiameterColumn,
            s => s.SemiDiameter ?? throw new ArgumentException("the clear semi-diameter is automatic and has no fixed value to read."),
            (s, v) => v < 0 ? throw new ArgumentException($"SDIA: a semi-diameter cannot be negative ({Inv(v)}).") : s with { SemiDiameter = v },
            s => s.SemiDiameter is not null && !HasSolve(s, SavedSurfaces.SemiDiameterColumn));
        SurfaceNumber("MCSD", "Mechanical semi-diameter", SavedSurfaces.MechanicalSemiDiameterColumn,
            s => s.MechanicalSemiDiameter ?? throw new ArgumentException("the mechanical semi-diameter is automatic and has no fixed value to read."),
            (s, v) => v < 0 ? throw new ArgumentException($"MCSD: a semi-diameter cannot be negative ({Inv(v)}).") : s with { MechanicalSemiDiameter = v },
            s => s.MechanicalSemiDiameter is not null && !HasSolve(s, SavedSurfaces.MechanicalSemiDiameterColumn));
        SurfaceNumber("CHZN", "Chip zone", SavedSurfaces.ChipZoneColumn, s => s.ChipZone, (s, v) => s with { ChipZone = v }, s => s.ChipZone != 0);
        SurfaceNumber("TCEX", "Thermal coefficient of expansion (1e-6/°C)", SavedSurfaces.TceColumn, s => s.Tce, (s, v) => s with { Tce = v }, s => s.Tce != 0);
        SurfaceText("COTN", "Coating", SavedSurfaces.CoatingColumn, s => s.Coating ?? "",
            (s, t) => s with { Coating = t.Trim().Length == 0 ? null : t.Trim() }, s => s.Coating is not null);
        SurfaceText("MCOM", "Surface comment", SavedSurfaces.CommentColumn, s => s.Comment ?? "",
            (s, t) => s with { Comment = t.Length == 0 ? null : t }, s => !string.IsNullOrEmpty(s.Comment));

        // Parameters: PRAM (surface, parameter) and the obsolete PAR1 … PAR8 (help: "Obsolete,
        // use PRAM instead"), which act on parameter 1 … 8.
        Add(new MultiConfigOperand("PRAM", "Parameter value", MultiConfigValueKind.Number, new[] { "Surface", "Parameter" })
        {
            Getter = (sys, n, p, _) => MultiConfigValue.Of(GetParameter(sys, n, p, "PRAM")),
            Setter = (sys, n, p, _, v) => SetParameter(sys, n, p, v.Number, "PRAM"),
            Targets = ParameterTargets,
        });
        for (int k = 1; k <= 8; k++)
        {
            int p = k;
            string code = "PAR" + k.ToString(CultureInfo.InvariantCulture);
            Add(new MultiConfigOperand(code, $"Parameter {k} (obsolete; PRAM)", MultiConfigValueKind.Number, Surface1)
            {
                Getter = (sys, n, _, _) => MultiConfigValue.Of(GetParameter(sys, n, p, code)),
                Setter = (sys, n, _, _, v) => SetParameter(sys, n, p, v.Number, code),
            });
        }

        // Model glass.
        ModelGlass("MIND", "Model glass index", m => m.Nd, (m, v) => m with { Nd = v });
        ModelGlass("MABB", "Model glass Abbe number", m => m.Vd, (m, v) => m with { Vd = v });
        ModelGlass("MDPG", "Model glass dPgF", m => m.DeltaPgF, (m, v) => m with { DeltaPgF = v });

        // Flags and the stop.
        SurfaceFlag("IGNR", "Ignore this surface (0 consider, 1 ignore)", s => s.IsIgnored, (s, b) => s with { IsIgnored = b });
        SurfaceFlag("SDRW", "Do not draw this surface (0 draw, 1 do not draw)", s => s.DoNotDraw, (s, b) => s with { DoNotDraw = b });
        SurfaceFlag("SRTS", "Skip rays to this surface (0 draw rays, 1 skip)", s => s.SkipRaysTo, (s, b) => s with { SkipRaysTo = b });
        Add(new MultiConfigOperand("IGNM", "Ignore a range of surfaces (0 consider, 1 ignore)", MultiConfigValueKind.Integer, new[] { "First surface", "Last surface" })
        {
            Getter = (sys, a, b, _) =>
            {
                Range(sys, a, b, "IGNM");
                var states = Enumerable.Range(a, b - a + 1).Select(j => Surf(sys, j, "IGNM").IsIgnored).Distinct().ToArray();
                return states.Length == 1 ? MultiConfigValue.Of(states[0] ? 1 : 0)
                    : throw new ArgumentException($"IGNM: surfaces {a} to {b} are partly ignored, so the range has no single value.");
            },
            Setter = (sys, a, b, _, v) =>
            {
                Range(sys, a, b, "IGNM");
                bool ignore = Bool01(v, "IGNM");
                for (int j = a; j <= b; j++) sys = WithSurface(sys, j, "IGNM", s => s with { IsIgnored = ignore });
                return sys;
            },
        });
        Add(new MultiConfigOperand("STPS", "Stop surface number", MultiConfigValueKind.Integer, None)
        {
            Getter = (sys, _, _, _) =>
            {
                var list = SurfacesOf(sys, "STPS");
                int stop = list.FindIndex(s => s?.IsStop == true);
                return stop >= 0 ? MultiConfigValue.Of(stop) : throw new ArgumentException("STPS: the lens has no stop surface.");
            },
            Setter = (sys, _, _, _, v) =>
            {
                var list = SurfacesOf(sys, "STPS");
                int stop = (int)v.Number;
                // "The stop can be moved to any valid surface number (excluding the object and image surfaces)".
                if (stop < 1 || stop > list.Count - 2)
                    throw new ArgumentException($"STPS: the stop must be one of surfaces 1 to {list.Count - 2} (not the object or the image), not {stop}.");
                return sys with { Surfaces = list.Select((s, j) => (s ?? new()) with { IsStop = j == stop }).ToList() };
            },
            Targets = Single,
        });

        // Surface apertures.
        Add(new MultiConfigOperand("APTP", "Surface aperture type (0 none … 10 floating)", MultiConfigValueKind.Integer, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(ApertureCode(Surf(sys, n, "APTP").Aperture)),
            Setter = (sys, n, _, _, v) => WithSurface(sys, n, "APTP", s => s with { Aperture = WithApertureCode(s.Aperture, (int)v.Number) }),
            Targets = sys => SurfacesWhere(sys, (_, s, _) => s.Aperture is not null),
        });
        ApertureValue("APMN", "Surface aperture minimum (first parameter)", first: true);
        ApertureValue("APMX", "Surface aperture maximum (second parameter)", first: false);
        ApertureDecenter("APDX", "Surface aperture x-decenter", a => a.DecenterX, (a, v) => a with { DecenterX = v });
        ApertureDecenter("APDY", "Surface aperture y-decenter", a => a.DecenterY, (a, v) => a with { DecenterY = v });

        // Tilt and decenter before (CB…) and after (CA…) the surface.
        foreach (bool before in new[] { true, false })
        {
            string p = before ? "CB" : "CA", where = before ? "before" : "after";
            Tilt(p + "DX", $"Tilt/decenter {where} surface: decenter x", before, t => t.DecenterX, (t, v) => t with { DecenterX = v });
            Tilt(p + "DY", $"Tilt/decenter {where} surface: decenter y", before, t => t.DecenterY, (t, v) => t with { DecenterY = v });
            Tilt(p + "TX", $"Tilt/decenter {where} surface: tilt x", before, t => t.TiltX, (t, v) => t with { TiltX = v });
            Tilt(p + "TY", $"Tilt/decenter {where} surface: tilt y", before, t => t.TiltY, (t, v) => t with { TiltY = v });
            Tilt(p + "TZ", $"Tilt/decenter {where} surface: tilt z", before, t => t.TiltZ, (t, v) => t with { TiltZ = v });
            string code = p + "OR";
            Add(new MultiConfigOperand(code, $"Tilt/decenter {where} surface: order (0 decenter then tilt, 1 tilt then decenter)", MultiConfigValueKind.Integer, Surface1)
            {
                Getter = (sys, n, _, _) => MultiConfigValue.Of(OrderCode(TiltOf(Surf(sys, n, code), before)?.Order, code)),
                Setter = (sys, n, _, _, v) =>
                {
                    int o = (int)v.Number;
                    if (o is not (0 or 1)) throw new ArgumentException($"{code}: the order is 0 (decenter then tilt) or 1 (tilt then decenter), not {o}.");
                    return WithSurface(sys, n, code, s => WithTilt(s, before, t => t with { Order = o == 0 ? null : SavedSurfaces.OrderName(TiltDecenterOrder.TiltThenDecenter) }));
                },
                Targets = sys => SurfacesWhere(sys, (_, s, _) => TiltOf(s, before) is not null),
            });
        }

        // Coordinate return of a coordinate break.
        Add(new MultiConfigOperand("CROR", "Coordinate return orientation (0 none, 1 orientation only, 2 XY, 3 XYZ)", MultiConfigValueKind.Integer, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of((int)ReturnMode(Break(sys, n, "CROR").CoordinateReturn?.Mode, "CROR")),
            Setter = (sys, n, _, _, v) =>
            {
                Break(sys, n, "CROR");
                int m = (int)v.Number;
                if (!Enum.IsDefined(typeof(CoordinateReturnMode), m)) throw new ArgumentException($"CROR: the orientation is 0 to 3, not {m}.");
                return WithSurface(sys, n, "CROR", s => s with { CoordinateReturn = (s.CoordinateReturn ?? new()) with { Mode = SavedSurfaces.ReturnModeName((CoordinateReturnMode)m) } });
            },
            Targets = sys => SurfacesWhere(sys, (_, s, _) => IsBreak(s) && s.CoordinateReturn is not null),
        });
        Add(new MultiConfigOperand("CRSR", "Coordinate return surface", MultiConfigValueKind.Integer, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(Break(sys, n, "CRSR").CoordinateReturn?.Surface ?? 0),
            Setter = (sys, n, _, _, v) =>
            {
                Break(sys, n, "CRSR");
                int target = (int)v.Number;
                if (target < 0 || target >= n) throw new ArgumentException($"CRSR: the return surface must be a prior surface (0 to {n - 1}), not {target}.");
                return WithSurface(sys, n, "CRSR", s => s with { CoordinateReturn = (s.CoordinateReturn ?? new()) with { Surface = target } });
            },
            Targets = sys => SurfacesWhere(sys, (_, s, _) => IsBreak(s) && s.CoordinateReturn is not null),
        });

        // Solve parameters (help: "Summary of Solves", plans/sources/optics/solves: a pickup's
        // parameters are surface, scale factor, offset; a solve's are its own, in order).
        SolveParameter("CSP1", "Curvature solve parameter 1", SavedSurfaces.RadiusColumn, 1);
        SolveParameter("CSP2", "Curvature solve parameter 2", SavedSurfaces.RadiusColumn, 2);
        SolveParameter("TSP1", "Thickness solve parameter 1", SavedSurfaces.ThicknessColumn, 1);
        SolveParameter("TSP2", "Thickness solve parameter 2", SavedSurfaces.ThicknessColumn, 2);
        SolveParameter("TSP3", "Thickness solve parameter 3", SavedSurfaces.ThicknessColumn, 3);
        SolveParameter("PSP1", "Parameter solve parameter 1 (the pickup surface)", null, 1);
        SolveParameter("PSP2", "Parameter solve parameter 2 (the scale factor)", null, 2);
        SolveParameter("PSP3", "Parameter solve parameter 3 (the offset)", null, 3);

        // ---- system data ----
        SystemFlag("AFOC", "Afocal image space", s => s.AfocalImageSpace, (s, b) => s with { AfocalImageSpace = b });
        SystemFlag("TELE", "Telecentric in object space", s => s.TelecentricObjectSpace, (s, b) => s with { TelecentricObjectSpace = b });
        SystemNumber("APDF", "Apodization factor", s => s.ApodizationFactor, (s, v) => s with { ApodizationFactor = v });
        SystemChoice("APDT", "Apodization type (0 none, 1 Gaussian, 2 cosine cubed)", ApodizationTypes.Names, OpticalSystemData.Default.Aperture.Apodization,
            s => s.Apodization, (s, n) => s with { Apodization = n });
        SystemChoice("SATP", "System aperture type (0 EPD, 1 image F/#, 2 object NA, 3 float by stop, 4 paraxial working F/#, 5 object cone angle)",
            ApertureTypes.Names, OpticalSystemData.Default.Aperture.Type, s => s.ApertureType, (s, n) => s with { ApertureType = n });
        Add(new MultiConfigOperand("APER", "System aperture value (float by stop size: the stop's semi-diameter)", MultiConfigValueKind.Number, None)
        {
            Getter = (sys, _, _, _) => MultiConfigValue.Of(FloatsByStop(sys)
                ? Surf(sys, StopOf(sys, "APER"), "APER").SemiDiameter ?? throw new ArgumentException("APER: the aperture floats by stop size and the stop's semi-diameter is automatic.")
                : Sys(sys).ApertureValue),
            Setter = (sys, _, _, _, v) =>
            {
                Finite(v, "APER");
                return FloatsByStop(sys)
                    ? WithSurface(sys, StopOf(sys, "APER"), "APER", s => DropFlags(s with { SemiDiameter = v.Number }, SavedSurfaces.SemiDiameterColumn))
                    : WithSys(sys, s => s with { ApertureValue = v.Number });
            },
            Targets = Single,
        });
        SystemChoice("FLTP", "Field type (0 angle, 1 object height, 2 paraxial image height, 3 real image height, 4 theodolite)",
            FieldTypes.Names, OpticalSystemData.Default.Fields.Type, s => s.FieldType, (s, n) => s with { FieldType = n });
        SystemChoice("RAAM", "Ray aiming (0 off, 1 paraxial, 2 real)", RayAimingModes.Names, OpticalSystemData.Default.RayAiming.Mode,
            s => s.RayAiming, (s, n) => s with { RayAiming = n });
        SystemChoice("MTFU", "MTF units (0 cycles/mm, 1 cycles/mrad)", MtfUnits.Names, OpticalSystemData.Default.Units.MtfUnit,
            s => s.MtfUnit, (s, n) => s with { MtfUnit = n });
        SystemNumber("TEMP", "Temperature (°C)", s => s.TemperatureCelsius, (s, v) => s with { TemperatureCelsius = v });
        SystemNumber("PRES", "Air pressure (atmospheres)", s => s.PressureAtmospheres,
            (s, v) => v < 0 ? throw new ArgumentException($"PRES: the pressure cannot be negative ({Inv(v)}).") : s with { PressureAtmospheres = v });
        SystemNumber("PSHX", "Ray aiming x pupil shift", s => s.PupilShiftX ?? 0, (s, v) => s with { PupilShiftX = v });
        SystemNumber("PSHY", "Ray aiming y pupil shift", s => s.PupilShiftY ?? 0, (s, v) => s with { PupilShiftY = v });
        SystemNumber("PSHZ", "Ray aiming z pupil shift", s => s.PupilShiftZ ?? 0, (s, v) => s with { PupilShiftZ = v });
        SystemNumber("PSCX", "Ray aiming x pupil compress", s => s.PupilCompressX ?? 0, (s, v) => s with { PupilCompressX = v });
        SystemNumber("PSCY", "Ray aiming y pupil compress", s => s.PupilCompressY ?? 0, (s, v) => s with { PupilCompressY = v });
        Add(new MultiConfigOperand("LTTL", "Lens title", MultiConfigValueKind.Text, None)
        {
            Getter = (sys, _, _, _) => MultiConfigValue.Of(Sys(sys).Title ?? ""),
            Setter = (sys, _, _, _, v) => WithSys(sys, s => s with { Title = v.Text }),
            Targets = Single,
        });
        Add(new MultiConfigOperand("PRWV", "Primary wavelength number", MultiConfigValueKind.Integer, None)
        {
            Getter = (sys, _, _, _) => MultiConfigValue.Of(Sys(sys).PrimaryWavelength),
            Setter = (sys, _, _, _, v) =>
            {
                int w = (int)v.Number, count = Waves(Sys(sys)).Count;
                if (w < 1 || w > count) throw new ArgumentException($"PRWV: the primary wavelength must be one of 1 to {count}, not {w}.");
                return WithSys(sys, s => s with { PrimaryWavelength = w });
            },
            Targets = Single,
        });

        // Polarization.
        PolarizationNumber("GPJX", "Global Jones vector Jx", p => p.Jx, (p, v) => p with { Jx = v });
        PolarizationNumber("GPJY", "Global Jones vector Jy", p => p.Jy, (p, v) => p with { Jy = v });
        PolarizationNumber("GPPX", "Global polarization phase x (degrees)", p => p.XPhaseDegrees, (p, v) => p with { XPhaseDegrees = v });
        PolarizationNumber("GPPY", "Global polarization phase y (degrees)", p => p.YPhaseDegrees, (p, v) => p with { YPhaseDegrees = v });
        Add(new MultiConfigOperand("GPIU", "Global polarization is unpolarized (1) or polarized (0)", MultiConfigValueKind.Integer, None)
        {
            Getter = (sys, _, _, _) => MultiConfigValue.Of(Polarization(sys).Unpolarized ? 1 : 0),
            Setter = (sys, _, _, _, v) => { bool u = Bool01(v, "GPIU"); return WithSys(sys, s => s with { Polarization = Polarization(sys) with { Unpolarized = u } }); },
            Targets = Single,
        });

        // Fields and wavelengths.
        FieldNumber("XFIE", "X-field value", f => f.X, (f, v) => f with { X = v });
        FieldNumber("YFIE", "Y-field value", f => f.Y, (f, v) => f with { Y = v });
        FieldNumber("FLWT", "Field weight", f => f.Weight, (f, v) => v < 0 ? throw new ArgumentException($"FLWT: a weight cannot be negative ({Inv(v)}).") : f with { Weight = v });
        FieldNumber("FVDX", "Vignetting factor VDX", f => f.DecenterX, (f, v) => f with { DecenterX = v });
        FieldNumber("FVDY", "Vignetting factor VDY", f => f.DecenterY, (f, v) => f with { DecenterY = v });
        FieldNumber("FVCX", "Vignetting factor VCX", f => f.CompressionX, (f, v) => f with { CompressionX = v });
        FieldNumber("FVCY", "Vignetting factor VCY", f => f.CompressionY, (f, v) => f with { CompressionY = v });
        FieldNumber("FTAN", "Tangential angle TAN (degrees)", f => f.TiltDegrees, (f, v) => f with { TiltDegrees = v });
        // "Vignetting factor VAN. This command is obsolete. See FTAN." Acts on the same angle.
        FieldNumber("FVAN", "Vignetting factor VAN (obsolete; FTAN)", f => f.TiltDegrees, (f, v) => f with { TiltDegrees = v }, addAll: false);
        WaveNumber("WAVE", "Wavelength (µm)", w => w.Micrometers,
            (w, v) => v <= 0 ? throw new ArgumentException($"WAVE: a wavelength must be positive ({Inv(v)} µm).") : w with { Micrometers = v });
        WaveNumber("WLWT", "Wavelength weight", w => w.Weight,
            (w, v) => v < 0 ? throw new ArgumentException($"WLWT: a weight cannot be negative ({Inv(v)}).") : w with { Weight = v });

        List.Sort((a, b) => string.CompareOrdinal(a.Code, b.Code));
    }

    private static void Add(MultiConfigOperand op)
    {
        if (!ByCode.TryAdd(op.Code, op)) throw new InvalidOperationException($"Operand {op.Code} is registered twice.");
        List.Add(op);
    }

    private static void Unsupported(string code, string description, MultiConfigValueKind kind, string[] args, string reason) =>
        Add(new MultiConfigOperand(code, description, kind, args) { NotSupportedReason = reason });

    // ------------------------------------------------------------------ builders

    private static void SurfaceNumber(string code, string description, string column,
        Func<OpticsSurfaceSettings, double> get, Func<OpticsSurfaceSettings, double, OpticsSurfaceSettings> set,
        Func<OpticsSurfaceSettings, bool>? hasData = null) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(Wrap(code, n, () => get(Surf(sys, n, code)))),
            Setter = (sys, n, _, _, v) => { Finite(v, code); return WithSurface(sys, n, code, s => DropFlags(Wrap(code, n, () => set(s, v.Number)), column)); },
            Targets = sys => SurfacesWhere(sys, (_, s, _) => hasData?.Invoke(s) ?? true),
        });

    // OP-32
    private static readonly string[] SurfaceAddOn = { "Surface", "Add-on" };

    private static void CompositeAddOnNumber(string code, string description,
        Func<OpticsCompositeAddOnSettings, double> get, Func<OpticsCompositeAddOnSettings, double, OpticsCompositeAddOnSettings> set) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, SurfaceAddOn)
        {
            Getter = (sys, n, k, _) => MultiConfigValue.Of(get(AddOnOf(Surf(sys, n, code), n, k, code))),
            Setter = (sys, n, k, _, v) =>
            {
                Finite(v, code);
                return WithSurface(sys, n, code, s =>
                {
                    var list = s.AddOns!.ToList();
                    list[k - 1] = set(AddOnOf(s, n, k, code), v.Number);
                    return s with { AddOns = list };
                });
            },
            Targets = sys =>
            {
                var surfaces = sys.Surfaces ?? new List<OpticsSurfaceSettings>();
                var targets = new List<(int, int, int)>();
                for (int j = 0; j < surfaces.Count; j++)
                    for (int k = 1; k <= (surfaces[j]?.AddOns?.Count ?? 0); k++)
                        targets.Add((j, k, 0));
                return targets;
            },
        });

    private static OpticsCompositeAddOnSettings AddOnOf(OpticsSurfaceSettings s, int n, int k, string code)
    {
        int count = s.AddOns?.Count ?? 0;
        if (k < 1 || k > count)
            throw new ArgumentException($"{code}: surface {n} has {count} composite add-on{(count == 1 ? "" : "s")}; add-on {k} does not exist (they count from 1).");
        return s.AddOns![k - 1] ?? new OpticsCompositeAddOnSettings();
    }

    private static void SurfaceText(string code, string description, string column,
        Func<OpticsSurfaceSettings, string> get, Func<OpticsSurfaceSettings, string, OpticsSurfaceSettings> set,
        Func<OpticsSurfaceSettings, bool> hasData) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Text, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(get(Surf(sys, n, code))),
            Setter = (sys, n, _, _, v) => WithSurface(sys, n, code, s => DropFlags(set(s, v.Text!), column)),
            Targets = sys => SurfacesWhere(sys, (_, s, _) => hasData(s)),
        });

    private static void SurfaceFlag(string code, string description, Func<OpticsSurfaceSettings, bool> get, Func<OpticsSurfaceSettings, bool, OpticsSurfaceSettings> set) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Integer, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(get(Surf(sys, n, code)) ? 1 : 0),
            Setter = (sys, n, _, _, v) => { bool b = Bool01(v, code); return WithSurface(sys, n, code, s => set(s, b)); },
            Targets = sys => SurfacesWhere(sys, (_, s, _) => get(s)),
        });

    private static void ModelGlass(string code, string description, Func<OpticsModelGlassSettings, double> get,
        Func<OpticsModelGlassSettings, double, OpticsModelGlassSettings> set) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(get(Surf(sys, n, code).ModelGlass
                ?? throw new ArgumentException($"{code}: surface {n} has no model glass."))),
            // A surface without a model glass gets one with the record's defaults, replacing its material.
            Setter = (sys, n, _, _, v) =>
            {
                Finite(v, code);
                return WithSurface(sys, n, code, s => DropFlags(s with { ModelGlass = set(s.ModelGlass ?? new(), v.Number), Material = null, Index = null }, SavedSurfaces.MaterialColumn));
            },
            Targets = sys => SurfacesWhere(sys, (_, s, _) => s.ModelGlass is not null),
        });

    private static void ApertureValue(string code, string description, bool first) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, Surface1)
        {
            Getter = (sys, n, _, _) =>
            {
                var a = DefinedAperture(Surf(sys, n, code), n, code);
                return MultiConfigValue.Of(a.Type switch
                {
                    SavedSurfaces.CircularAperture => first ? a.MinRadius : a.MaxRadius,
                    SavedSurfaces.RectangularAperture or SavedSurfaces.EllipticalAperture => first ? a.HalfWidthX : a.HalfWidthY,
                    SavedSurfaces.SpiderAperture => first ? a.ArmWidth : a.ArmCount,
                    _ => throw NoApertureParameter(a, n, code),
                });
            },
            Setter = (sys, n, _, _, v) =>
            {
                Finite(v, code);
                double x = v.Number;
                return WithSurface(sys, n, code, s =>
                {
                    var a = DefinedAperture(s, n, code);
                    return s with
                    {
                        Aperture = a.Type switch
                        {
                            SavedSurfaces.CircularAperture => first ? a with { MinRadius = x } : a with { MaxRadius = x },
                            SavedSurfaces.RectangularAperture or SavedSurfaces.EllipticalAperture => first ? a with { HalfWidthX = x } : a with { HalfWidthY = x },
                            // "The spider is defined by the width of each arm, and by the number of
                            // arms" (help: Aperture surface properties): first the width, then the count.
                            SavedSurfaces.SpiderAperture => first ? a with { ArmWidth = x }
                                : Math.Round(x) == x && x >= 1 ? a with { ArmCount = (int)x }
                                : throw new ArgumentException($"{code}: a spider's number of arms is a whole number of at least 1, not {Inv(x)}."),
                            _ => throw NoApertureParameter(a, n, code),
                        }
                    };
                });
            },
            Targets = sys => SurfacesWhere(sys, (_, s, _) => s.Aperture?.Type is SavedSurfaces.CircularAperture or SavedSurfaces.RectangularAperture
                or SavedSurfaces.EllipticalAperture or SavedSurfaces.SpiderAperture),
        });

    private static ArgumentException NoApertureParameter(OpticsSurfaceApertureSettings a, int n, string code) =>
        new($"{code}: surface {n}'s {a.Type} aperture has no parameter for {code} (OpticStudio's {code} does not control a user aperture's scale; a floating aperture has none).");

    private static void ApertureDecenter(string code, string description, Func<OpticsSurfaceApertureSettings, double> get,
        Func<OpticsSurfaceApertureSettings, double, OpticsSurfaceApertureSettings> set) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(get(DefinedAperture(Surf(sys, n, code), n, code))),
            Setter = (sys, n, _, _, v) => { Finite(v, code); return WithSurface(sys, n, code, s => s with { Aperture = set(DefinedAperture(s, n, code), v.Number) }); },
            Targets = sys => SurfacesWhere(sys, (_, s, _) => s.Aperture is not null),
        });

    private static void Tilt(string code, string description, bool before, Func<OpticsTiltDecenterSettings, double> get,
        Func<OpticsTiltDecenterSettings, double, OpticsTiltDecenterSettings> set) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, Surface1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(TiltOf(Surf(sys, n, code), before) is { } t ? get(t) : 0),
            Setter = (sys, n, _, _, v) => { Finite(v, code); return WithSurface(sys, n, code, s => WithTilt(s, before, t => set(t, v.Number))); },
            Targets = sys => SurfacesWhere(sys, (_, s, _) => TiltOf(s, before) is not null),
        });

    private static void SolveParameter(string code, string description, string? column, int index) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, column is null ? new[] { "Surface", "Parameter" } : Surface1)
        {
            Getter = (sys, n, p, _) =>
            {
                var f = DrivingFlag(Surf(sys, n, code), n, ColumnOf(column, p, code), code);
                return MultiConfigValue.Of(Pickup(f) ? index switch
                {
                    1 => f.PickupSurface,
                    2 => f.PickupScale,
                    _ => f.PickupOffset,
                } : f.SolveParameters is { } list && index - 1 < list.Count ? list[index - 1] : 0);
            },
            Setter = (sys, n, p, _, v) =>
            {
                Finite(v, code);
                string col = ColumnOf(column, p, code);
                return WithSurface(sys, n, code, s =>
                {
                    var f = DrivingFlag(s, n, col, code);
                    OpticsCellFlagSettings changed;
                    if (Pickup(f))
                    {
                        if (index == 1 && (Math.Round(v.Number) != v.Number || v.Number < 0))
                            throw new ArgumentException($"{code}: the pickup surface is a whole number of 0 or more, not {v}.");
                        if (index == 3 && col == SavedSurfaces.RadiusColumn)
                            throw new ArgumentException($"{code}: a curvature pickup has no offset (help: Summary of Solves).");
                        changed = index switch
                        {
                            1 => f with { PickupSurface = (int)v.Number },
                            2 => f with { PickupScale = v.Number },
                            _ => f with { PickupOffset = v.Number },
                        };
                    }
                    else
                    {
                        var list = (f.SolveParameters ?? new List<double>()).ToList();
                        while (list.Count < index) list.Add(0);
                        list[index - 1] = v.Number;
                        changed = f with { SolveParameters = list };
                    }
                    return s with { Flags = s.Flags!.Select(x => ReferenceEquals(x, f) ? changed : x).ToList() };
                });
            },
            Targets = column is null ? null : sys => SurfacesWhere(sys, (_, s, _) => s.Flags?.Any(f => f.Column == column && (Pickup(f) || IsSolve(f))) == true),
        });

    private static void SystemNumber(string code, string description, Func<OpticsSystemSettings, double> get, Func<OpticsSystemSettings, double, OpticsSystemSettings> set) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, None)
        {
            Getter = (sys, _, _, _) => MultiConfigValue.Of(get(Sys(sys))),
            Setter = (sys, _, _, _, v) => { Finite(v, code); return WithSys(sys, s => set(s, v.Number)); },
            Targets = Single,
        });

    private static void SystemFlag(string code, string description, Func<OpticsSystemSettings, bool> get, Func<OpticsSystemSettings, bool, OpticsSystemSettings> set) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Integer, None)
        {
            Getter = (sys, _, _, _) => MultiConfigValue.Of(get(Sys(sys)) ? 1 : 0),
            Setter = (sys, _, _, _, v) => { bool b = Bool01(v, code); return WithSys(sys, s => set(s, b)); },
            Targets = Single,
        });

    private static void SystemChoice<T>(string code, string description, SavedNameTable<T> names, T fallback,
        Func<OpticsSystemSettings, string?> get, Func<OpticsSystemSettings, string, OpticsSystemSettings> set) where T : struct, Enum =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Integer, None)
        {
            Getter = (sys, _, _, _) =>
            {
                var notes = new List<string>();
                var value = SystemDataSettings.Pick(names, get(Sys(sys)), fallback, code, notes);
                if (notes.Count > 0) throw new ArgumentException($"{code}: {notes[0]}");
                return MultiConfigValue.Of(Convert.ToInt32(value, CultureInfo.InvariantCulture));
            },
            Setter = (sys, _, _, _, v) =>
            {
                int i = (int)v.Number;
                var match = names.All.Where(x => Convert.ToInt32(x, CultureInfo.InvariantCulture) == i).ToArray();
                if (match.Length == 0)
                    throw new ArgumentException($"{code}: {i} is not one of the codes {string.Join(", ", names.All.Select(x => $"{Convert.ToInt32(x, CultureInfo.InvariantCulture)} ({names.Label(x)})"))}.");
                return WithSys(sys, s => set(s, names.SavedName(match[0])));
            },
            Targets = Single,
        });

    private static void PolarizationNumber(string code, string description, Func<OpticsPolarizationSettings, double> get,
        Func<OpticsPolarizationSettings, double, OpticsPolarizationSettings> set) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, None)
        {
            Getter = (sys, _, _, _) => MultiConfigValue.Of(get(Polarization(sys))),
            Setter = (sys, _, _, _, v) => { Finite(v, code); return WithSys(sys, s => s with { Polarization = set(Polarization(sys), v.Number) }); },
            Targets = Single,
        });

    private static void FieldNumber(string code, string description, Func<OpticsFieldSettings, double> get,
        Func<OpticsFieldSettings, double, OpticsFieldSettings> set, bool addAll = true) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, Field1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(get(Fields(Sys(sys))[Index(n, Fields(Sys(sys)).Count, "field", code)])),
            Setter = (sys, n, _, _, v) =>
            {
                Finite(v, code);
                var list = Fields(Sys(sys)).ToList();
                int i = Index(n, list.Count, "field", code);
                list[i] = set(list[i], v.Number);
                return WithSys(sys, s => s with { Fields = list });
            },
            Targets = addAll ? sys => Enumerable.Range(1, Fields(Sys(sys)).Count).Select(i => (i, 0, 0)) : null,
        });

    private static void WaveNumber(string code, string description, Func<OpticsWavelengthSettings, double> get,
        Func<OpticsWavelengthSettings, double, OpticsWavelengthSettings> set) =>
        Add(new MultiConfigOperand(code, description, MultiConfigValueKind.Number, Wave1)
        {
            Getter = (sys, n, _, _) => MultiConfigValue.Of(get(Waves(Sys(sys))[Index(n, Waves(Sys(sys)).Count, "wavelength", code)])),
            Setter = (sys, n, _, _, v) =>
            {
                Finite(v, code);
                var list = Waves(Sys(sys)).ToList();
                int i = Index(n, list.Count, "wavelength", code);
                list[i] = set(list[i], v.Number);
                return WithSys(sys, s => s with { Wavelengths = list });
            },
            Targets = sys => Enumerable.Range(1, Waves(Sys(sys)).Count).Select(i => (i, 0, 0)),
        });

    // ------------------------------------------------------------------ helpers

    private static readonly OpticsSystemSettings DefaultSystem = SystemDataSettings.ToSettings(OpticalSystemData.Default);

    private static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static IEnumerable<(int, int, int)> Single(OpticsSettings _) => new[] { (0, 0, 0) };

    private static void Finite(MultiConfigValue v, string code)
    {
        if (!double.IsFinite(v.Number)) throw new ArgumentException($"{code} takes a finite number, not {v}.");
    }

    private static bool Bool01(MultiConfigValue v, string code) => v.Number switch
    {
        0 => false,
        1 => true,
        _ => throw new ArgumentException($"{code} is 0 or 1, not {v}."),
    };

    private static T Wrap<T>(string code, int n, Func<T> f)
    {
        try { return f(); }
        catch (ArgumentException ex) when (!ex.Message.StartsWith(code, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{code}: surface {n}: {ex.Message}", ex);
        }
    }

    private static List<OpticsSurfaceSettings> SurfacesOf(OpticsSettings sys, string code) =>
        sys.Surfaces is { Count: > 0 } list ? list : throw new ArgumentException($"{code}: the lens has no surfaces.");

    private static OpticsSurfaceSettings Surf(OpticsSettings sys, int n, string code)
    {
        var list = SurfacesOf(sys, code);
        if (n < 0 || n >= list.Count) throw new ArgumentException($"{code}: surface {n} does not exist (the lens has surfaces 0 to {list.Count - 1}).");
        return list[n] ?? new OpticsSurfaceSettings();
    }

    private static OpticsSettings WithSurface(OpticsSettings sys, int n, string code, Func<OpticsSurfaceSettings, OpticsSurfaceSettings> change)
    {
        var s = Surf(sys, n, code);
        var list = sys.Surfaces!.ToList();
        list[n] = change(s);
        return sys with { Surfaces = list };
    }

    private static void Range(OpticsSettings sys, int a, int b, string code)
    {
        int count = SurfacesOf(sys, code).Count;
        if (a < 0 || b >= count || a > b) throw new ArgumentException($"{code}: the range {a} to {b} is not within surfaces 0 to {count - 1} in order.");
    }

    private static IEnumerable<(int, int, int)> SurfacesWhere(OpticsSettings sys, Func<int, OpticsSurfaceSettings, int, bool> keep)
    {
        var list = sys.Surfaces ?? new List<OpticsSurfaceSettings>();
        for (int j = 0; j < list.Count; j++)
            if (keep(j, list[j] ?? new(), list.Count)) yield return (j, 0, 0);
    }

    private static bool Pickup(OpticsCellFlagSettings f) => string.Equals(f.Kind, SavedSurfaces.PickupFlag, StringComparison.Ordinal);
    private static bool IsSolve(OpticsCellFlagSettings f) => string.Equals(f.Kind, SavedSurfaces.SolveFlag, StringComparison.Ordinal);

    private static bool HasSolve(OpticsSurfaceSettings s, string column) =>
        s.Flags?.Any(f => f.Column == column && (Pickup(f) || IsSolve(f))) == true;

    /// <summary>The surface without the solves and pickups on <paramref name="column"/> (a
    /// variable flag stays): the operand's value is then the cell's value.</summary>
    internal static OpticsSurfaceSettings DropFlags(OpticsSurfaceSettings s, string column)
    {
        if (s.Flags is not { Count: > 0 } flags || !flags.Any(f => f.Column == column && !IsVariable(f))) return s;
        var kept = flags.Where(f => f.Column != column || IsVariable(f)).ToList();
        return s with { Flags = kept.Count == 0 ? null : kept };
    }

    private static bool IsVariable(OpticsCellFlagSettings f) => string.Equals(f.Kind, SavedSurfaces.VariableFlag, StringComparison.Ordinal);

    private static string ColumnOf(string? column, int parameter, string code)
    {
        if (column is not null) return column;
        if (parameter < 1) throw new ArgumentException($"{code}: the parameter number must be 1 or more, not {parameter}.");
        return SavedSurfaces.ParameterColumn(parameter);
    }

    private static OpticsCellFlagSettings DrivingFlag(OpticsSurfaceSettings s, int n, string column, string code) =>
        s.Flags?.LastOrDefault(f => f.Column == column && (Pickup(f) || IsSolve(f)))
        ?? throw new ArgumentException($"{code}: surface {n}'s {column} cell has no solve or pickup for {code} to control.");

    private static double GetParameter(OpticsSettings sys, int n, int p, string code)
    {
        var s = Surf(sys, n, code);
        CheckParameter(s, n, p, code);
        return SavedSurfaces.Value(s, p);
    }

    private static OpticsSettings SetParameter(OpticsSettings sys, int n, int p, double v, string code)
    {
        if (!double.IsFinite(v)) throw new ArgumentException($"{code} takes a finite number, not {Inv(v)}.");
        return WithSurface(sys, n, code, s =>
        {
            CheckParameter(s, n, p, code);
            var entry = new OpticsSurfaceParameterSettings { Number = p, Value = v };
            s = SavedSurfaces.IsExtraData(s, p)
                ? s with { ExtraData = Replace(s.ExtraData, entry) }
                : s with { Parameters = Replace(s.Parameters, entry) };
            return DropFlags(s, SavedSurfaces.ParameterColumn(p));
        });

        static List<OpticsSurfaceParameterSettings> Replace(List<OpticsSurfaceParameterSettings>? list, OpticsSurfaceParameterSettings entry)
        {
            var copy = (list ?? new()).Where(e => e.Number != entry.Number).ToList();
            copy.Add(entry);
            return copy.OrderBy(e => e.Number).ToList();
        }
    }

    private static void CheckParameter(OpticsSurfaceSettings s, int n, int p, string code)
    {
        if (p < 1) throw new ArgumentException($"{code}: the parameter number must be 1 or more, not {p}.");
        if (SavedSurfaces.IsStandard(s)) throw new ArgumentException($"{code}: surface {n} is a {SavedSurfaces.StandardType} surface, which has no parameters.");
        if (SurfaceTypes.Find(s.Type) is { } layout && layout.Parameter(p) is null)
            throw new ArgumentException($"{code}: a {layout.Name} surface (surface {n}) has no parameter {p}.");
    }

    private static IEnumerable<(int, int, int)> ParameterTargets(OpticsSettings sys)
    {
        var list = sys.Surfaces ?? new List<OpticsSurfaceSettings>();
        for (int j = 0; j < list.Count; j++)
        {
            var s = list[j];
            if (s is null || SavedSurfaces.IsStandard(s)) continue;
            var layout = SurfaceTypes.Find(s.Type);
            foreach (int p in (s.Parameters ?? new()).Concat(s.ExtraData ?? new()).Select(e => e.Number).Distinct().Order())
                if (p >= 1 && (layout is null || layout.Parameter(p) is not null)) yield return (j, p, 0);
        }
    }

    private static OpticsSurfaceApertureSettings DefinedAperture(OpticsSurfaceSettings s, int n, string code) =>
        s.Aperture ?? throw new ArgumentException($"{code}: surface {n} has no surface aperture (the operand needs a defined aperture, not a semi-diameter).");

    // APTP: "0-10 for none, circular aperture, circular obscuration, spider, rectangular aperture,
    // rectangular obscuration, elliptical aperture, elliptical obscuration, user aperture, user
    // obscuration, and floating aperture; respectively."
    private static readonly (string Type, bool Obscuration)[] ApertureCodes =
    {
        ("", false),
        (SavedSurfaces.CircularAperture, false), (SavedSurfaces.CircularAperture, true),
        (SavedSurfaces.SpiderAperture, false),
        (SavedSurfaces.RectangularAperture, false), (SavedSurfaces.RectangularAperture, true),
        (SavedSurfaces.EllipticalAperture, false), (SavedSurfaces.EllipticalAperture, true),
        (SavedSurfaces.UserDefinedAperture, false), (SavedSurfaces.UserDefinedAperture, true),
        (SavedSurfaces.FloatingAperture, false),
    };

    private static int ApertureCode(OpticsSurfaceApertureSettings? a)
    {
        if (a is null) return 0;
        for (int i = 1; i < ApertureCodes.Length; i++)
            if (ApertureCodes[i].Type == a.Type && (ApertureCodes[i].Obscuration == a.IsObscuration || a.Type is SavedSurfaces.SpiderAperture or SavedSurfaces.FloatingAperture))
                return i;
        throw new ArgumentException($"APTP: the aperture type '{a.Type}' is not one this version knows.");
    }

    private static OpticsSurfaceApertureSettings? WithApertureCode(OpticsSurfaceApertureSettings? a, int code)
    {
        if (code < 0 || code >= ApertureCodes.Length) throw new ArgumentException($"APTP: the aperture type is 0 to {ApertureCodes.Length - 1}, not {code}.");
        if (code == 0) return null;
        var (type, obscuration) = ApertureCodes[code];
        return (a ?? new OpticsSurfaceApertureSettings()) with { Type = type, IsObscuration = obscuration };
    }

    private static OpticsTiltDecenterSettings? TiltOf(OpticsSurfaceSettings s, bool before) => before ? s.TiltBefore : s.TiltAfter;

    private static OpticsSurfaceSettings WithTilt(OpticsSurfaceSettings s, bool before, Func<OpticsTiltDecenterSettings, OpticsTiltDecenterSettings> change) =>
        before ? s with { TiltBefore = change(s.TiltBefore ?? new()) } : s with { TiltAfter = change(s.TiltAfter ?? new()) };

    private static int OrderCode(string? order, string code) => order switch
    {
        null or "" => 0,
        _ when order == SavedSurfaces.OrderName(TiltDecenterOrder.DecenterThenTilt) => 0,
        _ when order == SavedSurfaces.OrderName(TiltDecenterOrder.TiltThenDecenter) => 1,
        _ => throw new ArgumentException($"{code}: the saved order '{order}' is not one this version knows."),
    };

    private static bool IsBreak(OpticsSurfaceSettings s) => string.Equals(s.Type, SurfaceTypes.CoordinateBreak, StringComparison.OrdinalIgnoreCase);

    private static OpticsSurfaceSettings Break(OpticsSettings sys, int n, string code)
    {
        var s = Surf(sys, n, code);
        return IsBreak(s) ? s : throw new ArgumentException($"{code}: surface {n} is not a {SurfaceTypes.CoordinateBreak}; only a coordinate break has a coordinate return.");
    }

    private static CoordinateReturnMode ReturnMode(string? name, string code)
    {
        if (string.IsNullOrWhiteSpace(name)) return CoordinateReturnMode.None;
        return Enum.TryParse(name.Trim(), out CoordinateReturnMode m) && Enum.IsDefined(m) ? m
            : throw new ArgumentException($"{code}: the saved coordinate return '{name}' is not one this version knows.");
    }

    private static OpticsSystemSettings Sys(OpticsSettings sys) => sys.SystemData ?? DefaultSystem;

    private static OpticsSettings WithSys(OpticsSettings sys, Func<OpticsSystemSettings, OpticsSystemSettings> change) =>
        sys with { SystemData = change(Sys(sys)) };

    private static List<OpticsFieldSettings> Fields(OpticsSystemSettings s) => s.Fields ?? DefaultSystem.Fields!;
    private static List<OpticsWavelengthSettings> Waves(OpticsSystemSettings s) => s.Wavelengths ?? DefaultSystem.Wavelengths!;

    private static OpticsPolarizationSettings Polarization(OpticsSettings sys) =>
        Sys(sys).Polarization ?? SystemDataSettings.PolarizationToSettings(SystemPolarization.Default);

    private static int Index(int n, int count, string what, string code)
    {
        if (n < 1 || n > count) throw new ArgumentException($"{code}: {what} {n} does not exist (the system has {what}s 1 to {count}).");
        return n - 1;
    }

    private static bool FloatsByStop(OpticsSettings sys)
    {
        var notes = new List<string>();
        return SystemDataSettings.Pick(ApertureTypes.Names, Sys(sys).ApertureType, OpticalSystemData.Default.Aperture.Type, "Aperture type", notes)
            == ApertureType.FloatByStopSize;
    }

    private static int StopOf(OpticsSettings sys, string code)
    {
        int stop = SurfacesOf(sys, code).FindIndex(s => s?.IsStop == true);
        return stop >= 0 ? stop : throw new ArgumentException($"{code}: the aperture floats by stop size but the lens has no stop surface.");
    }
}
