using System.Globalization;
using System.Text;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Optimization.Interactive;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: the keywords and functions bound to the optics engines: ray tracing (RAYTRACE and the
// ray functions), the lens data (SETSURFACEPROPERTY, SPRO and the surface functions), the system
// data (SETSYSTEMPROPERTY, SYPR and the field and wavelength functions), the merit function
// (SETOPERAND, OPER, OPEV, MFCN), variables and optimization (SETVAR, OPTIMIZE, QUICKFOCUS),
// analyses to text (GETTEXTFILE) and lens files (LOADLENS, SAVELENS). Lengths are in lens units,
// as the editors show them; the lens a macro works on is the settings record
// (docs/Optics-Conventions.md §47), rebuilt for every trace, so a change takes effect at once
// whether or not UPDATE follows.

public static partial class ZplLibrary
{
    private static void RegisterLens()
    {
        RegisterRays();
        RegisterSurfaces();
        RegisterSystem();
        RegisterMerit();
        RegisterFilesAndAnalyses();
        RegisterLensNotBound();
        RegisterBeamsAndImages();   // wire-13: after the not-bound notes, so its own notes stand
        RegisterFiberCadAndObjects();   // wire-14: FICL, EXPORTCAD and the non-sequential objects (ZplObjectsLibrary.cs)
    }

    // ================================================================ Rays

    private static void RegisterRays()
    {
        Kw("RAYTRACE", "Traces a real ray (hx, hy, px, py [, wavelength]) through the current configuration (OpticsScripting.TraceRay); RAYE and RAYV as the help defines them.", (m, a) =>
        {
            if (a.Count is < 4 or > 5) throw new ZplRuntimeException("RAYTRACE takes hx, hy, px, py and optionally the wavelength number.");
            double hx = Unit(a.Num(m, 0, "hx"), "hx"), hy = Unit(a.Num(m, 1, "hy"), "hy");
            double px = Unit(a.Num(m, 2, "px"), "px"), py = Unit(a.Num(m, 3, "py"), "py");
            int w = a.Int(m, 4, "the wavelength number", 0);
            int count = Waves(m).Count;
            if (a.Has(4) && (w < 1 || w > count)) throw new ZplRuntimeException($"RAYTRACE: the wavelength number must be from 1 to {count}, not {w}.");
            m.Session.Ray = OpticsScripting.TraceRay(m.Session.System, m.Session.Catalogs, hx, hy, px, py, w);
            m.Session.RayUnit = m.Session.Unit;
        });
        Fn0("RAYE", "RAYTRACE's error code: 0, +n missed surface n, −n total internal reflection at n, −9999 not launched.", m => N(LastRay(m).Error));
        Fn0("RAYV", "The surface at which the ray was vignetted, or 0.", m => N(LastRay(m).Vignetted));
        Fn("RAYX", 1, 1, "x of the ray's intercept with the surface, local coordinates, lens units.", (m, v) => N(Len(m, Hit(m, v).Position.X)));
        Fn("RAYY", 1, 1, "y of the intercept, local, lens units.", (m, v) => N(Len(m, Hit(m, v).Position.Y)));
        Fn("RAYZ", 1, 1, "z of the intercept, local, lens units.", (m, v) => N(Len(m, Hit(m, v).Position.Z)));
        Fn("RAYL", 1, 1, "x direction cosine of the ray after the surface, local.", (m, v) => N(Hit(m, v).Direction.X));
        Fn("RAYM", 1, 1, "y direction cosine after the surface, local.", (m, v) => N(Hit(m, v).Direction.Y));
        Fn("RAYN", 1, 1, "z direction cosine after the surface, local.", (m, v) => N(Hit(m, v).Direction.Z));
        Fn("RANX", 1, 1, "x direction cosine of the surface normal at the intercept, local; the normal's sense is +z at the vertex (Conventions §56).", (m, v) => N(Hit(m, v).Normal.X));
        Fn("RANY", 1, 1, "y direction cosine of the surface normal (see RANX).", (m, v) => N(Hit(m, v).Normal.Y));
        Fn("RANZ", 1, 1, "z direction cosine of the surface normal (see RANX).", (m, v) => N(Hit(m, v).Normal.Z));
        Fn("RAYT", 1, 1, "Path length from the previous surface to the surface, lens units (may be negative).", (m, v) => N(Len(m, Hit(m, v).SegmentLength)));
        Fn("RAYO", 1, 1, "Optical path length from the previous surface (path times index), lens units.", (m, v) => N(Len(m, Hit(m, v).SegmentOpticalPath)));
        FunctionNotBound("OPDC", "Not bound: the OPD of one RAYTRACE ray needs the chief ray and the exit pupil reference sphere the help does not define for a single ray.");
        FunctionNotBound("OPTH", "Not bound: the help's path origin (and the phase of diffractive surfaces) is not defined for this program's launch plane.");
        foreach (var g in new[] { "RAGX", "RAGY", "RAGZ", "RAGL", "RAGM", "RAGN" })
            FunctionNotBound(g, "Not bound: global coordinates here are referred to surface 1 before its tilt (Conventions §13), and OpticStudio's global reference surface is not read.");
        NotBound("RAYTRACEX", "Not bound: launching a ray at an arbitrary surface is not built.");
        NotBound("POLTRACE", "Not bound.");
        NotBound("POLDEFINE", "Not bound.");
        Kw("PARAXIAL", "PARAXIAL OFF is accepted (real rays are always traced); PARAXIAL ON fails: paraxial RAYTRACE is not bound.", (m, a) =>
        {
            a.Exactly(1, "PARAXIAL");
            switch (a.Word(0))
            {
                case "OFF": return;
                case "ON": throw new ZplRuntimeException("PARAXIAL ON is not bound in this version: RAYTRACE traces real rays only.");
                default: throw new ZplRuntimeException($"PARAXIAL takes ON or OFF, not '{a.Text(0)}'.");
            }
        });
        Fn0("PMOD", "0: paraxial mode is never on (PARAXIAL ON is not bound).", m => N(0));
    }

    private static double Unit(double v, string what) => v >= -1 && v <= 1 ? v
        : throw new ZplRuntimeException($"RAYTRACE: {what} = {F(v)}; normalised coordinates must be from −1 to 1.");

    private static ScriptRay LastRay(ZplInterpreter m) => m.Session.Ray
        ?? throw new ZplRuntimeException("no ray has been traced; call RAYTRACE first.");

    private static SurfaceHit Hit(ZplInterpreter m, ZplValue[] v)
    {
        var ray = LastRay(m);
        int s = Int(v, 0, "the surface number");
        foreach (var h in ray.Path.Hits) if (h.Surface == s) return h;
        int last = ray.Path.Hits.Count > 0 ? ray.Path.Hits[^1].Surface : 0;
        throw new ZplRuntimeException($"the last ray has no data at surface {s}: it was traced to surface {last} (RAYE = {ray.Error}, RAYV = {ray.Vignetted}).");
    }

    private static double Len(ZplInterpreter m, double meters) => OpticsConventions.FromMeters(meters, m.Session.RayUnit);

    // ================================================================ Surfaces

    /// <summary>SETSURFACEPROPERTY's mnemonics (help, SETSURFACEPROPERTY, SURP).</summary>
    private static readonly Dictionary<string, int> SurfaceMnemonics = new(StringComparer.Ordinal)
    {
        ["TYPE"] = 0, ["COMM"] = 1, ["CURV"] = 2, ["THIC"] = 3, ["GLAS"] = 4, ["CONI"] = 5, ["SDIA"] = 6, ["TCE"] = 7, ["COAT"] = 8,
        ["SDLL"] = 9, ["PARM"] = 10, ["EDVA"] = 11, ["CODE"] = 17, ["GLAN"] = 18, ["ATYP"] = 20, ["APP1"] = 21, ["APP2"] = 22,
        ["APDX"] = 23, ["APDY"] = 24, ["UDA"] = 25, ["APPU"] = 26, ["CHZN"] = 27, ["MCSD"] = 28, ["BOR"] = 60, ["BDX"] = 61,
        ["BDY"] = 62, ["BTX"] = 63, ["BTY"] = 64, ["BTZ"] = 65, ["APU"] = 66, ["AOR"] = 70, ["ADX"] = 71, ["ADY"] = 72, ["ATX"] = 73,
        ["ATY"] = 74, ["ATZ"] = 75,
    };

    /// <summary>The surface names of SETSURFACEPROPERTY's "Surface names" table (help,
    /// SETSURFACEPROPERTY, SURP): the type as the help names it and its code. A code sets the
    /// type when this program builds the type of that name (<see cref="SurfaceTypes"/>).</summary>
    private static readonly (string Type, string Code)[] SurfaceNames =
    {
        ("Standard", "STANDARD"), ("Even Asphere", "EVENASPH"), ("Odd Asphere", "ODDASPHE"), ("Paraxial", "PARAXIAL"),
        ("Paraxial XY", "PARAX_XY"), ("Biconic", "BICONICX"), ("Toroidal Grating", "TOROGRAT"), ("Cubic Spline", "CUSPLINE"),
        ("Hologram 1", "HOLOGRM1"), ("Hologram 2", "HOLOGRM2"), ("Coordinate Break", "COORDBRK"), ("Polynomial", "POLYNOMI"),
        ("Fresnel", "FRESNELS"), ("ABCD", "ABCDSURF"), ("Alternate Even", "ALTERNAT"), ("Alternate Odd", "ALTERNAO"),
        ("Diffraction Grating", "DGRATING"), ("Conjugate", "CONJUGAT"), ("Tilted", "TILTSURF"), ("Irregular", "IRREGULA"),
        ("Gradient 1", "GRINSUR1"), ("Gradient 2", "GRINSUR2"), ("Gradient 3", "GRINSUR3"), ("Gradient 4", "GRINSUR4"),
        ("Gradient 5", "GRINSUR5"), ("Gradient 6", "GRINSUR6"), ("Gradient 7", "GRINSUR7"), ("Gradium", "GRINSUR8"),
        ("Gradient 9", "GRINSUR9"), ("Gradient 10", "GRINSU10"), ("Gradient 12", "GRINSU12"), ("Retro Reflect", "RETROREF"),
        ("Toroidal", "TOROIDAL"), ("Zernike Fringe Sag", "FZERNSAG"), ("Zernike Fringe Phase", "FZERNPHA"),
        ("Zernike Standard Sag", "SZERNSAG"), ("Zernike Standard Phase", "SZERNPHA"), ("Zernike Annular Phase", "AZERNPHA"),
        ("Biconic Zernike", "BICONICZ"), ("Extended Polynomial", "XPOLYNOM"), ("Binary Optic 1", "BINARY_1"),
        ("Binary Optic 2", "BINARY_2"), ("Binary Optic 3", "BINARY_3"), ("Binary Optic 4", "BINARY_4"),
        ("Extended Cubic Spline", "XCUSPLIN"), ("Extended Asphere", "XASPHERE"), ("Extended Odd Asphere", "XOSPHERE"),
        ("Variable Line Space Grating", "VARLSGRT"), ("Elliptical Grating 1", "ELLIGRAT"), ("Elliptical Grating 2", "ELLIGRA2"),
        ("Superconic", "SUPERCON"), ("Extended Fresnel", "XFRESNEL"), ("Cylinder Fresnel", "CFRESNEL"), ("Grid Sag", "GRID_SAG"),
        ("Grid Phase", "GRID_PHA"), ("Generalized Fresnel", "GEN_FRES"), ("Periodic", "PERIODIC"), ("Toroidal Hologram", "TOROHOLO"),
        ("Atmospheric Refraction", "ATMOSPHR"), ("Zone Plate", "ZONEPLAT"), ("Q-Type Asphere", "QED_TYPE"), ("Jones Matrix", "JONESMAT"),
        ("Zernike Annular Standard Sag", "AZERNSAG"), ("Chebyshev Polynomial", "CHEBYSHV"), ("Black Box Lens", "BLACKBOX"),
        ("Slide", "SLIDESRF"), ("User defined", "USERSURF"), ("Birefringent In", "BIRE__IN"), ("Birefringent Out", "BIRE_OUT"),
        ("Optically Fabricated Hologram", "OFABHOL1"), ("Radial NURBS", "RADNURBS"), ("Toroidal NURBS", "TORNURBS"),
        ("Extended Toroidal Grating", "TOROGRAX"), ("Radial Grating", "RGRATING"), ("Odd Cosine", "ODDCOSIN"),
        ("Non-Sequential Component", "NONSEQCO"), ("Data", "DATASURF"), ("Off-Axis Conic Freeform", "OFFAXISCF"),
        ("Q-Type Freeform", "QFREEFORM"),
    };

    private static void RegisterSurfaces()
    {
        Kw("SETSURFACEPROPERTY", "Sets a surface property by code or mnemonic; bound codes: " + SurfaceCodesNote, SetSurfaceProperty);
        Kw("SURP", "Shorthand for SETSURFACEPROPERTY (same codes).", SetSurfaceProperty);
        Fn("SPRO", 2, 2, "A surface property by integer code (as SETSURFACEPROPERTY's bound codes); string values go to $BUFFER() and 0 is returned.", (m, v) =>
            GetSurfaceProperty(m, Int(v, 0, "the surface"), Int(v, 1, "the code"), null));
        Fn("SPRX", 3, 3, "As SPRO with value2: code 10 gives parameter value2.", (m, v) =>
            GetSurfaceProperty(m, Int(v, 0, "the surface"), Int(v, 1, "the code"), Num(v, 2, "value2")));

        Fn0("NSUR", "The image surface's number (surfaces are 0 to NSUR(); the help's Example Macro 1 reads the image intercept as RAYX(NSUR())).", m => N(m.Session.ImageSurface));
        Fn("CURV", 1, 1, "Curvature (1/lens unit; 0 for a plane).", (m, v) => N(Curvature(m.Session.Surface(Num(v, 0, "the surface"), "CURV"))));
        Fn("RADI", 1, 1, "Radius in lens units; 0 for an infinite radius.", (m, v) =>
        {
            var s = m.Session.Surface(Num(v, 0, "the surface"), "RADI");
            return N(SavedSurfaces.IsPlane(s) ? 0 : s.Radius!.Value);
        });
        Fn("THIC", 1, 1, "Thickness in lens units; an infinite thickness fails.", (m, v) =>
        {
            int j = m.Session.SurfaceNumber(Num(v, 0, "the surface"), "THIC");
            var s = m.Session.Surfaces[j];
            if (s.ThicknessIsInfinite) throw new ZplRuntimeException($"surface {j}'s thickness is infinite.");
            return N(s.Thickness);
        });
        Fn("CONI", 1, 1, "Conic constant.", (m, v) => N(m.Session.Surface(Num(v, 0, "the surface"), "CONI").Conic));
        Fn("SDIA", 1, 1, "Clear semi-diameter in lens units; an automatic one as the trace sizes it (SemiDiameterSizing).", (m, v) =>
            N(SemiDiameter(m, m.Session.SurfaceNumber(Num(v, 0, "the surface"), "SDIA"))));
        Fn("MCSD", 1, 1, "Mechanical semi-diameter; an automatic one is the clear semi-diameter plus the chip zone.", (m, v) =>
        {
            int j = m.Session.SurfaceNumber(Num(v, 0, "the surface"), "MCSD");
            var s = m.Session.Surfaces[j];
            return N(s.MechanicalSemiDiameter ?? SemiDiameter(m, j) + s.ChipZone);
        });
        Fn("CHZN", 1, 1, "Chip zone in lens units.", (m, v) => N(m.Session.Surface(Num(v, 0, "the surface"), "CHZN").ChipZone));
        Fn("PARM", 2, 2, "Parameter n of surface s (PARM(n, s)), in its column's unit.", (m, v) =>
        {
            int n = Int(v, 0, "the parameter number");
            if (n < 1) throw new ZplRuntimeException($"parameter numbers start at 1, not {n}.");
            return N(SavedSurfaces.Value(m.Session.Surface(Num(v, 1, "the surface"), "PARM"), n));
        });
        Fn("INDX", 1, 1, "Index of the medium after the surface at the primary wavelength, relative to the system's air (as the trace uses it).", (m, v) =>
        {
            int j = m.Session.SurfaceNumber(Num(v, 0, "the surface"), "INDX");
            var data = SystemDataSettings.FromSettings(m.Session.Current.SystemData, new List<string>());
            return N(SequentialTrace.Create(m.Session.Lens, data).Indices[j]);
        });
        Fn("SURC", 1, 1, "The first surface whose comment matches A$ (case ignored), or −1.", (m, v) => N(Comment(m, Str(v, 0, "A$"), 1)));
        Fn("SRCN", 2, 2, "The n-th surface whose comment matches A$ (backwards for negative n; the count for n = 0), or −1.", (m, v) => N(Comment(m, Str(v, 0, "A$"), Int(v, 1, "n"))));
        Fn("$COMMENT", 1, 1, "The surface's comment.", (m, v) => S(m.Session.Surface(Num(v, 0, "the surface"), "$COMMENT").Comment ?? ""));
        Fn("$GLASS", 1, 1, "The surface's glass name (empty for air; a constant index or model glass fails: it has no name).", (m, v) =>
        {
            var s = m.Session.Surface(Num(v, 0, "the surface"), "$GLASS");
            if (s.Index is not null || s.ModelGlass is not null) throw new ZplRuntimeException("the surface has a constant index or a model glass, which has no glass name.");
            return S(s.Material ?? "");
        });
        Fn("$COAT", 1, 1, "The surface's coating name (empty when uncoated).", (m, v) => S(m.Session.Surface(Num(v, 0, "the surface"), "$COAT").Coating ?? ""));
        Fn("APMN", 1, 1, "Aperture: circular minimum radius; rectangular or elliptical x half-width; spider arm width; 0 without an aperture.", (m, v) => N(ApertureValue(m, v, 0)));
        Fn("APMX", 1, 1, "Aperture: circular maximum radius; rectangular or elliptical y half-width; spider arm count; 0 without an aperture.", (m, v) => N(ApertureValue(m, v, 1)));
        Fn("APXD", 1, 1, "Aperture x decenter (0 without an aperture).", (m, v) => N(m.Session.Surface(Num(v, 0, "the surface"), "APXD").Aperture?.DecenterX ?? 0));
        Fn("APYD", 1, 1, "Aperture y decenter (0 without an aperture).", (m, v) => N(m.Session.Surface(Num(v, 0, "the surface"), "APYD").Aperture?.DecenterY ?? 0));
        FunctionNotBound("APTP", "Not bound: the help does not list the aperture type codes.");

        Kw("STOPSURF", "Makes surface n the stop (not the object or image).", (m, a) =>
        {
            a.Exactly(1, "STOPSURF");
            int n = m.Session.SurfaceNumber(a.Num(m, 0, "STOPSURF's surface"), "STOPSURF");
            if (n == 0 || n == m.Session.ImageSurface) throw new ZplRuntimeException("STOPSURF: the object and image surfaces cannot be the stop.");
            m.Session.RequireSingleConfiguration("STOPSURF");
            var list = m.Session.System.Surfaces!.Select((s, j) => s with { IsStop = j == n }).ToList();
            m.Session.System = m.Session.System with { Surfaces = list };
        });
        Kw("INSERT", "Inserts a new Standard surface (thickness 0, air) at n (1 to the image surface); every surface number held elsewhere follows (pickups, solves, merit and multi-configuration rows).", (m, a) =>
        {
            a.Exactly(1, "INSERT");
            int n = a.Int(m, 0, "INSERT's surface");
            int image = m.Session.ImageSurface;
            if (n < 1 || n > image) throw new ZplRuntimeException($"INSERT: the new surface must be from 1 to {image} (before the image), not {n}.");
            var old = m.Session.System.Surfaces!;
            var b = new SavedLensSurgery.Builder(old);
            b.CopyRange(0, n - 1);
            b.Add(new OpticsSurfaceSettings());
            b.CopyRange(n, old.Count - 1);
            m.Session.System = b.Build(m.Session.System).Settings;
        });
        Kw("DELETE", "Deletes surface n (not the object or image); fails when something still names it (a pickup, solve or row).", (m, a) =>
        {
            a.Exactly(1, "DELETE");
            int n = m.Session.SurfaceNumber(a.Num(m, 0, "DELETE's surface"), "DELETE");
            if (n == 0 || n == m.Session.ImageSurface) throw new ZplRuntimeException("DELETE: the object and image surfaces cannot be deleted.");
            m.Session.System = SavedLensSurgery.Remove(m.Session.System, new[] { n });
        });
        // Wave 11: OP-32's composite add-ons (docs/Optics-Conventions.md §58). Add-ons are a list on
        // the base here, not rows, so the surface numbers are this program's (CompositeSurfaces).
        Kw("COMPOSITEON", "Makes surface n (1 to the surface two before the image) a composite add-on of surface n + 1 (above the add-ons that surface has) and removes it from the surface list; refused for the stop, a non-zero thickness, a change of medium, a type the composite table does not list, or row settings an add-on cannot hold. Every surface number held elsewhere follows.", (m, a) =>
        {
            a.Exactly(1, "COMPOSITEON");
            int n = m.Session.SurfaceNumber(a.Num(m, 0, "COMPOSITEON's surface"), "COMPOSITEON");
            m.Session.System = Sequential.Composite.CompositeSurfaces.MakeAddOn(m.Session.System, n);
        });
        Kw("COMPOSITEOFF", "Surface n is the base here (add-ons are not numbered as surfaces): its top add-on becomes surface n again (thickness 0, the medium of surface n − 1) and the base moves to n + 1; refused when the add-on is decentered or tilted. Every surface number held elsewhere follows.", (m, a) =>
        {
            a.Exactly(1, "COMPOSITEOFF");
            int n = m.Session.SurfaceNumber(a.Num(m, 0, "COMPOSITEOFF's surface"), "COMPOSITEOFF");
            m.Session.System = Sequential.Composite.CompositeSurfaces.MakeSurface(m.Session.System, n);
        });
        Kw("SOLVETYPE", "Sets a solve: " + SolveCodesNote + " Pickup arguments: surface, scale, offset, column (Integer Codes for Column Numbers); a macro solve's argument is the macro's file name.", SolveType);
        Kw("SETVAR", "Makes a cell variable (status ≠ 0) or removes its variable flag (0): R, T, C, D (TCE) and Pn.", (m, a) =>
        {
            if (a.Count != 3) throw new ZplRuntimeException("SETVAR takes surf, code, status (codes M, N and O, which take a fourth argument, are not bound).");
            int j = m.Session.SurfaceNumber(a.Num(m, 0, "SETVAR's surface"), "SETVAR");
            string code = a.Word(1) ?? throw new ZplRuntimeException($"SETVAR: '{a.Text(1)}' is not a code (R, T, C, D, Pn).");
            string column = code switch
            {
                "R" => SavedSurfaces.RadiusColumn,
                "T" => SavedSurfaces.ThicknessColumn,
                "C" => SavedSurfaces.ConicColumn,
                "D" => SavedSurfaces.TceColumn,
                _ when code.Length > 1 && code[0] == 'P' && int.TryParse(code[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1 => SavedSurfaces.ParameterColumn(p),
                _ => throw new ZplRuntimeException($"SETVAR code {code} is not bound in this version (R, T, C, D and Pn are)."),
            };
            bool on = a.Num(m, 2, "SETVAR's status") != 0;
            var s = m.Session.System.Surfaces![j];
            s = on ? SurfaceFlags.With(s, column, SurfaceFlags.Variable(column))
                   : SurfaceFlags.Without(s, f => f.Column == column && SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag));
            m.Session.SetSurface(j, s, "SETVAR");
        });
        Kw("REMOVEVARIABLES", "Fixes every variable cell of the lens data and the multi-configuration table.", (m, a) =>
        {
            a.Max(0, "REMOVEVARIABLES");
            var sys = m.Session.System;
            var list = sys.Surfaces?.Select(s => SurfaceFlags.Without(s, f => SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag))).ToList();
            var table = sys.MultiConfiguration;
            if (table?.Operands is { } ops)
                table = table with
                {
                    Operands = ops.Select(o => o with
                    {
                        Values = o.Values?.Select(v => v.Solve == MultiConfiguration.Variable ? v with { Solve = null } : v).ToList(),
                    }).ToList(),
                };
            m.Session.System = sys with { Surfaces = list, MultiConfiguration = table };
        });
        Kw("GETVARDATA", "Puts the variables in VECn: [0] the count, then per variable the type code (1 curvature, 2 thickness, 3 conic, 4–6 model nd/Vd/ΔPgF, 7 TCE, 8 parameter, 10 multi-configuration), surface (operand), parameter (configuration), object (0) and value.", (m, a) =>
        {
            a.Exactly(1, "GETVARDATA");
            var vec = Vector(m, a.Int(m, 0, "GETVARDATA's vector"), "GETVARDATA");
            var variables = LensVariables.Find(m.Session.System);
            var values = LensVariables.Values(m.Session.System, variables);
            if (5 * variables.Count > m.Session.VectorSize) throw new ZplRuntimeException($"GETVARDATA: {variables.Count} variables need {5 * variables.Count + 1} vector entries; the vectors hold {m.Session.VectorSize + 1} (SETVECSIZE).");
            vec[0] = variables.Count;
            for (int q = 1; q <= variables.Count; q++)
            {
                var x = variables[q - 1];
                (int code, int surface, int parameter) = x.Kind switch
                {
                    LensVariableKind.Curvature => (1, x.Surface, 0),
                    LensVariableKind.Thickness => (2, x.Surface, 0),
                    LensVariableKind.Conic => (3, x.Surface, 0),
                    LensVariableKind.ModelIndex => (4, x.Surface, 0),
                    LensVariableKind.ModelAbbe => (5, x.Surface, 0),
                    LensVariableKind.ModelPartialDispersion => (6, x.Surface, 0),
                    LensVariableKind.Tce => (7, x.Surface, 0),
                    LensVariableKind.Parameter => (8, x.Surface, SavedSurfaces.ParameterNumber(x.Column) ?? 0),
                    LensVariableKind.Configuration => (10, x.Cell!.Value.Operand, x.Cell.Value.Configuration),
                    _ => throw new ZplRuntimeException($"GETVARDATA: the variable {x.Label} has no type code in the help's table."),
                };
                vec[5 * q - 4] = code; vec[5 * q - 3] = surface; vec[5 * q - 2] = parameter; vec[5 * q - 1] = 0; vec[5 * q] = values[q - 1];
            }
        });
    }

    private const string SolveCodesNote = "CF CV CP CZ, TF TV TP TZ, KF KP KZ, HF HP, SA SU SZ, PF_p PV_p PP_p";

    private const string SurfaceCodesNote = "0 TYPE (the surface names this program builds), 1 COMM, 2 CURV, 3 THIC, 4 GLAS, 5 CONI, 6 SDIA, 7 TCE, 8 COAT, 10 PARM, 14 row color, 16 ignore, 23 APDX, 24 APDY, 27 CHZN, 28 MCSD, 60–65 tilt/decenter before, 66 APU, 70–75 after, 76–77 coordinate return, 90–92 draw flags. Not on a lens with a multi-configuration table.";

    private static int SurfaceCode(ZplInterpreter m, ZplArgs a)
    {
        if (a.Word(1) is { } word && SurfaceMnemonics.TryGetValue(word, out int code)) return code;
        return a.Int(m, 1, "the property code");
    }

    private static void SetSurfaceProperty(ZplInterpreter m, ZplArgs a)
    {
        if (a.Count is < 3 or > 4) throw new ZplRuntimeException("SETSURFACEPROPERTY takes surface, code, value1 [, value2].");
        var ses = m.Session;
        int j = ses.SurfaceNumber(a.Num(m, 0, "the surface"), "SETSURFACEPROPERTY");
        int code = SurfaceCode(m, a);
        var s = ses.System.Surfaces![j];
        var v1 = a.Value(m, 2, "value1");
        double Num1() => v1.IsString ? throw new ZplRuntimeException($"code {code} takes a number, not the string \"{v1.Text}\".") : v1.Number;
        string Str1() => v1.IsString ? v1.Text! : throw new ZplRuntimeException($"code {code} takes a string (in quotes or a string variable).");
        bool Flag1() { double x = Num1(); return x == 0 ? false : x == 1 ? true : throw new ZplRuntimeException($"code {code} takes 0 or 1, not {F(x)}."); }
        bool cb = string.Equals(s.Type, SurfaceTypes.CoordinateBreak, StringComparison.Ordinal);
        OpticsTiltDecenterSettings Before() => cb ? throw new ZplRuntimeException("a coordinate break takes no tilt/decenter before it (its parameters are the break).") : s.TiltBefore ?? new();
        OpticsTiltDecenterSettings After() => s.TiltAfter ?? new();
        string Order() => Int(Num1(), "the order") switch { 0 => SavedSurfaces.OrderName(TiltDecenterOrder.DecenterThenTilt), 1 => SavedSurfaces.OrderName(TiltDecenterOrder.TiltThenDecenter), var o => throw new ZplRuntimeException($"the order must be 0 (decenter then tilt) or 1 (tilt then decenter), not {o}.") };
        switch (code)
        {
            case 0:
            {
                string name = Str1().Trim().ToUpperInvariant();
                string? type = TypeOfCode(name);
                s = s with { Type = type == SavedSurfaces.StandardType ? null : type };
                break;
            }
            case 1: s = s with { Comment = Str1() }; break;
            case 2: { double c = Num1(); s = s with { Radius = c == 0 ? null : 1 / c }; break; }
            case 3: s = s with { Thickness = Num1(), ThicknessIsInfinite = false }; break;
            case 4:
            {
                string g = Str1().Trim();
                if (g.Length > 0 && !string.Equals(g, SavedSurfaces.Mirror, StringComparison.OrdinalIgnoreCase) && ses.Catalogs.Count > 0
                    && ProjectLens.FindGlass(g, ses.Catalogs) is null)
                    throw new ZplRuntimeException($"the glass '{g}' is not in the active catalogs ({string.Join(", ", ses.Catalogs.Select(c => c.Name))}).");
                s = s with { Material = g.Length == 0 ? null : g, Index = null, ModelGlass = null };
                s = SurfaceFlags.Without(s, f => f.Column == SavedSurfaces.MaterialColumn && !SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag));
                break;
            }
            case 5: s = s with { Conic = Num1() }; break;
            case 6:
            {
                double x = Num1();
                s = SurfaceFlags.With(s with { SemiDiameter = x >= 0 ? x : null }, SavedSurfaces.SemiDiameterColumn, null);
                break;
            }
            case 7: s = s with { Tce = Num1() }; break;
            case 8: { string c = Str1().Trim(); s = s with { Coating = c.Length == 0 ? null : c }; break; }
            case 10:
            {
                if (!a.Has(3)) throw new ZplRuntimeException("code 10 (PARM) needs value2, the parameter number.");
                int n = a.Int(m, 3, "the parameter number");
                if (n < 1) throw new ZplRuntimeException($"parameter numbers start at 1, not {n}.");
                s = WithParameter(s, n, Num1());
                break;
            }
            case 14: { int c = Int(Num1(), "the row color"); s = s with { RowColor = c == 0 ? null : c }; break; }
            case 16: s = s with { IsIgnored = Flag1() }; break;
            case 23 or 24:
            {
                var ap = s.Aperture ?? throw new ZplRuntimeException($"surface {j} has no aperture to decenter.");
                s = s with { Aperture = code == 23 ? ap with { DecenterX = Num1() } : ap with { DecenterY = Num1() } };
                break;
            }
            case 27: s = s with { ChipZone = Num1() }; break;
            case 28:
            {
                double x = Num1();
                s = SurfaceFlags.With(s with { MechanicalSemiDiameter = x >= 0 ? x : null }, SavedSurfaces.MechanicalSemiDiameterColumn, null);
                break;
            }
            case 60: s = s with { TiltBefore = Before() with { Order = Order() } }; break;
            case 61: s = s with { TiltBefore = Before() with { DecenterX = Num1() } }; break;
            case 62: s = s with { TiltBefore = Before() with { DecenterY = Num1() } }; break;
            case 63: s = s with { TiltBefore = Before() with { TiltX = Num1() } }; break;
            case 64: s = s with { TiltBefore = Before() with { TiltY = Num1() } }; break;
            case 65: s = s with { TiltBefore = Before() with { TiltZ = Num1() } }; break;
            case 66:
            {
                // "0 for explicit, 1/2 for pickup/reverse current surface, 3/4 for pickup/reverse
                // current surface minus 1, 5/6 … minus 2, etc."
                int k = Int(Num1(), "the after status");
                if (k < 0) throw new ZplRuntimeException($"the after status must not be negative ({k}).");
                if (k == 0) { s = s with { TiltAfter = After() with { Mode = null, Surface = 0 } }; break; }
                int back = (k - 1) / 2;
                bool reverse = k % 2 == 0;
                if (back == 0)
                    s = s with { TiltAfter = After() with { Mode = SavedSurfaces.AfterModeName(reverse ? TiltAfterKind.ReverseThisSurface : TiltAfterKind.PickUpThisSurface), Surface = 0 } };
                else
                {
                    if (j - back < 0) throw new ZplRuntimeException($"status {k} picks up surface {j - back}, which does not exist.");
                    s = s with { TiltAfter = After() with { Mode = SavedSurfaces.AfterModeName(reverse ? TiltAfterKind.ReverseSurface : TiltAfterKind.PickUpSurface), Surface = j - back } };
                }
                break;
            }
            case 70: s = s with { TiltAfter = After() with { Order = Order() } }; break;
            case 71: s = s with { TiltAfter = After() with { DecenterX = Num1() } }; break;
            case 72: s = s with { TiltAfter = After() with { DecenterY = Num1() } }; break;
            case 73: s = s with { TiltAfter = After() with { TiltX = Num1() } }; break;
            case 74: s = s with { TiltAfter = After() with { TiltY = Num1() } }; break;
            case 75: s = s with { TiltAfter = After() with { TiltZ = Num1() } }; break;
            case 76:
            {
                if (!cb) throw new ZplRuntimeException("code 76 (coordinate return) is valid only on a coordinate break.");
                var mode = Int(Num1(), "the coordinate return status") switch
                {
                    0 => CoordinateReturnMode.None,
                    1 => CoordinateReturnMode.OrientationOnly,
                    2 => CoordinateReturnMode.OrientationXY,
                    3 => CoordinateReturnMode.OrientationXYZ,
                    var x => throw new ZplRuntimeException($"the coordinate return status must be 0 to 3, not {x}."),
                };
                s = s with { CoordinateReturn = (s.CoordinateReturn ?? new()) with { Mode = SavedSurfaces.ReturnModeName(mode) } };
                break;
            }
            case 77:
            {
                if (!cb) throw new ZplRuntimeException("code 77 (coordinate return to surface) is valid only on a coordinate break.");
                int to = ses.SurfaceNumber(Num1(), "the coordinate return surface");
                s = s with { CoordinateReturn = (s.CoordinateReturn ?? new()) with { Surface = to } };
                break;
            }
            case 90: s = s with { HideRaysTo = Flag1() }; break;
            case 91: s = s with { SkipRaysTo = Flag1() }; break;
            case 92: s = s with { DoNotDraw = Flag1() }; break;
            default:
                throw new ZplRuntimeException($"SETSURFACEPROPERTY code {code} is not bound in this version (bound: {SurfaceCodesNote})");
        }
        ses.SetSurface(j, s, "SETSURFACEPROPERTY");
    }

    private static ZplValue GetSurfaceProperty(ZplInterpreter m, int surface, int code, double? value2)
    {
        var ses = m.Session;
        int j = ses.SurfaceNumber(surface, "SPRO");
        var s = ses.Surfaces[j];
        ZplValue Text(string t) { ses.Buffer = t; return N(0); }
        var before = s.TiltBefore ?? new OpticsTiltDecenterSettings();
        var after = s.TiltAfter ?? new OpticsTiltDecenterSettings();
        double Order(OpticsTiltDecenterSettings t) => t.Order == SavedSurfaces.OrderName(TiltDecenterOrder.TiltThenDecenter) ? 1 : 0;
        switch (code)
        {
            case 0: return Text(CodeOfType(s.Type));
            case 1: return Text(s.Comment ?? "");
            case 2: return N(Curvature(s));
            case 3:
                if (s.ThicknessIsInfinite) throw new ZplRuntimeException($"surface {j}'s thickness is infinite.");
                return N(s.Thickness);
            case 4:
                if (s.Index is not null || s.ModelGlass is not null) throw new ZplRuntimeException("the surface has a constant index or a model glass, which has no glass name.");
                return Text(s.Material ?? "");
            case 5: return N(s.Conic);
            case 6: return N(SemiDiameter(m, j));
            case 7: return N(s.Tce);
            case 8: return Text(s.Coating ?? "");
            case 10:
                if (value2 is not double n) throw new ZplRuntimeException("code 10 (PARM) needs value2: use SPRX(surf, 10, n).");
                return N(SavedSurfaces.Value(s, Int(n, "the parameter number")));
            case 14: return N(s.RowColor ?? 0);
            case 16: return N(s.IsIgnored ? 1 : 0);
            case 23: return N(s.Aperture?.DecenterX ?? 0);
            case 24: return N(s.Aperture?.DecenterY ?? 0);
            case 27: return N(s.ChipZone);
            case 28: return N(s.MechanicalSemiDiameter ?? SemiDiameter(m, j) + s.ChipZone);
            case 60: return N(Order(before));
            case 61: return N(before.DecenterX);
            case 62: return N(before.DecenterY);
            case 63: return N(before.TiltX);
            case 64: return N(before.TiltY);
            case 65: return N(before.TiltZ);
            case 66:
            {
                string mode = after.Mode ?? SavedSurfaces.AfterModeName(TiltAfterKind.Explicit);
                if (mode == SavedSurfaces.AfterModeName(TiltAfterKind.Explicit)) return N(0);
                if (mode == SavedSurfaces.AfterModeName(TiltAfterKind.PickUpThisSurface)) return N(1);
                if (mode == SavedSurfaces.AfterModeName(TiltAfterKind.ReverseThisSurface)) return N(2);
                int back = j - after.Surface;
                return N(mode == SavedSurfaces.AfterModeName(TiltAfterKind.PickUpSurface) ? 2 * back + 1 : 2 * back + 2);
            }
            case 70: return N(Order(after));
            case 71: return N(after.DecenterX);
            case 72: return N(after.DecenterY);
            case 73: return N(after.TiltX);
            case 74: return N(after.TiltY);
            case 75: return N(after.TiltZ);
            case 76:
            {
                string mode = s.CoordinateReturn?.Mode ?? SavedSurfaces.ReturnModeName(CoordinateReturnMode.None);
                for (int k = 0; k <= 3; k++)
                    if (mode == SavedSurfaces.ReturnModeName((CoordinateReturnMode)k)) return N(k);
                throw new ZplRuntimeException($"the coordinate return '{mode}' has no code.");
            }
            case 77: return N(s.CoordinateReturn?.Surface ?? 0);
            case 90: return N(s.HideRaysTo ? 1 : 0);
            case 91: return N(s.SkipRaysTo ? 1 : 0);
            case 92: return N(s.DoNotDraw ? 1 : 0);
            default: throw new ZplRuntimeException($"code {code} is not bound in this version (bound: {SurfaceCodesNote})");
        }
    }

    private static string? TypeOfCode(string code)
    {
        foreach (var (type, c) in SurfaceNames)
        {
            if (!string.Equals(c, code, StringComparison.Ordinal)) continue;
            if (type == SavedSurfaces.StandardType) return type;
            var layout = SurfaceTypes.Find(type) ?? throw new ZplRuntimeException($"the surface type {code} ({type}) is not built in this version.");
            return layout.Name;
        }
        throw new ZplRuntimeException($"'{code}' is not a surface type name of the help's table (STANDARD, EVENASPH, COORDBRK, …).");
    }

    private static string CodeOfType(string? type)
    {
        string t = type ?? SavedSurfaces.StandardType;
        foreach (var (name, c) in SurfaceNames)
            if (string.Equals(name, t, StringComparison.OrdinalIgnoreCase)) return c;
        throw new ZplRuntimeException($"the surface type '{t}' has no code in the help's table.");
    }

    private static double Curvature(OpticsSurfaceSettings s) => SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value;

    internal static OpticsSurfaceSettings WithParameter(OpticsSurfaceSettings s, int n, double value)
    {
        var entry = new OpticsSurfaceParameterSettings { Number = n, Value = value };
        if (SavedSurfaces.IsExtraData(s, n))
            return s with { ExtraData = (s.ExtraData ?? new()).Where(p => p.Number != n).Append(entry).OrderBy(p => p.Number).ToList() };
        return s with { Parameters = (s.Parameters ?? new()).Where(p => p.Number != n).Append(entry).OrderBy(p => p.Number).ToList() };
    }

    private static double SemiDiameter(ZplInterpreter m, int j)
    {
        var s = m.Session.Surfaces[j];
        if (s.SemiDiameter is double sd) return sd;
        var data = SystemDataSettings.FromSettings(m.Session.Current.SystemData, new List<string>());
        var clear = SemiDiameterSizing.Compute(m.Session.Lens, data).Clear;
        return OpticsConventions.FromMeters(clear[j], m.Session.Unit);
    }

    private static int Comment(ZplInterpreter m, string text, int n)
    {
        var list = m.Session.Surfaces;
        var matches = Enumerable.Range(0, list.Count).Where(j => string.Equals(list[j].Comment ?? "", text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (n == 0) return matches.Count;
        if (n > 0) return n <= matches.Count ? matches[n - 1] : -1;
        return -n <= matches.Count ? matches[matches.Count + n] : -1;
    }

    private static double ApertureValue(ZplInterpreter m, ZplValue[] v, int which)
    {
        var ap = m.Session.Surface(Num(v, 0, "the surface"), which == 0 ? "APMN" : "APMX").Aperture;
        if (ap is null) return 0;
        return ap.Type switch
        {
            SavedSurfaces.CircularAperture => which == 0 ? ap.MinRadius : ap.MaxRadius,
            SavedSurfaces.RectangularAperture or SavedSurfaces.EllipticalAperture => which == 0 ? ap.HalfWidthX : ap.HalfWidthY,
            SavedSurfaces.SpiderAperture => which == 0 ? ap.ArmWidth : ap.ArmCount,
            _ => throw new ZplRuntimeException($"the help defines no {(which == 0 ? "APMN" : "APMX")} value for a '{ap.Type}' aperture."),
        };
    }

    private static void SolveType(ZplInterpreter m, ZplArgs a)
    {
        if (a.Count < 2) throw new ZplRuntimeException("SOLVETYPE takes surf, CODE and the solve's arguments.");
        int j = m.Session.SurfaceNumber(a.Num(m, 0, "SOLVETYPE's surface"), "SOLVETYPE");
        string code = a.Word(1) ?? throw new ZplRuntimeException($"SOLVETYPE: '{a.Text(1)}' is not a solve code.");
        string column;
        char kind;
        int underscore = code.IndexOf('_');
        if (underscore == 2 && code[0] == 'P' && int.TryParse(code[3..], NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1)
        {
            column = SavedSurfaces.ParameterColumn(p);
            kind = code[1];
        }
        else
        {
            column = code.Length == 2 ? code[0] switch
            {
                'C' => SavedSurfaces.RadiusColumn,
                'T' => SavedSurfaces.ThicknessColumn,
                'K' => SavedSurfaces.ConicColumn,
                'H' => SavedSurfaces.TceColumn,
                'S' => SavedSurfaces.SemiDiameterColumn,
                _ => "",
            } : "";
            kind = code.Length == 2 ? code[1] : '?';
        }
        bool semi = column == SavedSurfaces.SemiDiameterColumn;
        bool known = column.Length > 0 && "FVPAUZ".Contains(kind)
            && (semi ? kind is 'A' or 'U' or 'Z' : kind is not ('A' or 'U'))
            && !(kind == 'Z' && (column == SavedSurfaces.TceColumn || column.StartsWith("Par", StringComparison.Ordinal)));
        if (!known)
            throw new ZplRuntimeException($"SOLVETYPE code {code} is not bound in this version (bound: {SolveCodesNote}).");
        if (kind == 'V' && (column == SavedSurfaces.TceColumn || column == SavedSurfaces.ConicColumn))
            throw new ZplRuntimeException($"SOLVETYPE {code}: the help lists no variable code for this column (its codes are fixed and pickup; use SETVAR).");
        var s = m.Session.System.Surfaces![j];
        switch (kind)
        {
            case 'F': s = SurfaceFlags.With(s, column, null); break;
            case 'V': s = SurfaceFlags.With(s, column, SurfaceFlags.Variable(column)); break;
            case 'A': s = SurfaceFlags.With(s with { SemiDiameter = null }, column, null); break;
            case 'U': s = SurfaceFlags.With(s with { SemiDiameter = SemiDiameter(m, j) }, column, null); break;
            case 'Z':
            {
                // "Macro name" is the macro solve's one parameter (Summary of Solves).
                string macro = a.FileName(m, 2, "the macro name");
                s = SurfaceFlags.With(s, column, new OpticsCellFlagSettings { Column = column, Kind = SavedSurfaces.SolveFlag, Solve = Sequential.Solves.ZplMacroSolve.SavedName, Macro = macro });
                break;
            }
            case 'P':
            {
                // Summary of Solves: curvature and conic pickups take surface, scale factor, (none),
                // column; thickness and parameter pickups surface, scale, offset, column; the TCE
                // pickup the surface alone.
                int from = m.Session.SurfaceNumber(a.Num(m, 2, "the pickup's surface"), "SOLVETYPE pickup");
                double scale = a.Num(m, 3, "the scale factor", 1);
                double offset = a.Num(m, 4, "the offset", 0);
                int col = a.Int(m, 5, "the column", 0);
                bool takesOffset = column == SavedSurfaces.ThicknessColumn || column.StartsWith("Par", StringComparison.Ordinal);
                if (!takesOffset && offset != 0) throw new ZplRuntimeException($"SOLVETYPE {code}: this pickup takes no offset (its third argument must be 0).");
                if (column == SavedSurfaces.TceColumn && (a.Count > 3)) throw new ZplRuntimeException("SOLVETYPE HP takes the surface only.");
                string source = col switch
                {
                    0 => column,
                    1 => SavedSurfaces.RadiusColumn,
                    2 => SavedSurfaces.ThicknessColumn,
                    3 => SavedSurfaces.ConicColumn,
                    4 => SavedSurfaces.SemiDiameterColumn,
                    >= 6 and <= 17 => SavedSurfaces.ParameterColumn(col - 5),
                    5 => throw new ZplRuntimeException("column 5 is parameter 0, which this program does not have."),
                    _ => throw new ZplRuntimeException($"the column number must be 0 to 17 (Integer Codes for Column Numbers), not {col}."),
                };
                s = SurfaceFlags.With(s, column, new OpticsCellFlagSettings
                {
                    Column = column, Kind = SavedSurfaces.PickupFlag, PickupSurface = from,
                    PickupColumn = source == column ? null : source, PickupScale = scale, PickupOffset = offset,
                });
                break;
            }
        }
        m.Session.SetSurface(j, s, "SOLVETYPE");
    }

    // ================================================================ System data

    private static void RegisterSystem()
    {
        Kw("SETSYSTEMPROPERTY", "Sets a system property by code; bound codes: " + SystemCodesNote, SetSystemProperty);
        Kw("SYSP", "Shorthand for SETSYSTEMPROPERTY (same codes).", SetSystemProperty);
        Fn("SYPR", 1, 1, "A system property by code (SETSYSTEMPROPERTY's bound codes that take no value2); strings go to $BUFFER() and 0 is returned.", (m, v) => GetSystemProperty(m, Int(v, 0, "the code")));
        Kw("PWAV", "Sets the primary wavelength number.", (m, a) =>
        {
            a.Exactly(1, "PWAV");
            int n = a.Int(m, 0, "the wavelength number");
            int count = Waves(m).Count;
            if (n < 1 || n > count) throw new ZplRuntimeException($"PWAV: the wavelength number must be from 1 to {count}, not {n}.");
            SetSys(m, d => d with { PrimaryWavelength = n }, "PWAV");
        });
        Kw("SETAIM", "Ray aiming off (0) or on (1, paraxial aiming: GETSYSTEMDATA's code 1 is \"paraxial\").", (m, a) =>
        {
            a.Exactly(1, "SETAIM");
            int st = a.Int(m, 0, "SETAIM's state");
            if (st is not (0 or 1)) throw new ZplRuntimeException($"SETAIM takes 0 or 1, not {st}.");
            SetSys(m, d => d with { RayAiming = RayAimingModes.Names.SavedName(st == 0 ? RayAimingMode.Off : RayAimingMode.Paraxial) }, "SETAIM");
        });
        Kw("UPDATE", "Checks that the lens builds (its solves and pickups evaluate); ALL, EDITORS and MACROS do the same (no windows are open during a run). UPDATE n fails: there are no numbered windows.", (m, a) =>
        {
            a.Max(1, "UPDATE");
            if (a.Count == 1 && a.Word(0) is not ("ALL" or "EDITORS" or "MACROS"))
                throw new ZplRuntimeException("UPDATE n (a window number) is not bound: macros here open no numbered windows.");
            if (m.Session.System.Surfaces is { Count: > 0 }) _ = m.Session.Lens;
        });
        Kw("SETCONFIG", "Makes configuration n current.", (m, a) =>
        {
            a.Exactly(1, "SETCONFIG");
            int n = a.Int(m, 0, "the configuration");
            var sys = m.Session.System;
            int count = MultiConfiguration.ConfigurationCount(sys);
            if (n < 1 || n > count) throw new ZplRuntimeException($"SETCONFIG: the configuration must be from 1 to {count}, not {n}.");
            if (sys.MultiConfiguration is { } table) m.Session.System = sys with { MultiConfiguration = table with { CurrentConfiguration = n } };
        });
        Fn0("CONF", "The current configuration number.", m => N(MultiConfiguration.CurrentConfiguration(m.Session.System)));
        Fn0("NCON", "The number of configurations.", m => N(MultiConfiguration.ConfigurationCount(m.Session.System)));

        Fn0("NFLD", "The number of fields.", m => N(Fields(m).Count));
        Fn0("NWAV", "The number of wavelengths.", m => N(Waves(m).Count));
        Fn0("PWAV", "The primary wavelength number.", m => N(Sys(m).PrimaryWavelength));
        Fn("WAVL", 1, 1, "Wavelength in micrometers.", (m, v) => N(Wave(m, v).Micrometers));
        Fn("WWGT", 1, 1, "Wavelength weight.", (m, v) => N(Wave(m, v).Weight));
        Fn("FLDX", 1, 1, "Field x (degrees or lens units, by the field type).", (m, v) => N(Field(m, v).X));
        Fn("FLDY", 1, 1, "Field y.", (m, v) => N(Field(m, v).Y));
        Fn("FWGT", 1, 1, "Field weight.", (m, v) => N(Field(m, v).Weight));
        Fn("FVDX", 1, 1, "Field vignetting decenter x.", (m, v) => N(Field(m, v).DecenterX));
        Fn("FVDY", 1, 1, "Field vignetting decenter y.", (m, v) => N(Field(m, v).DecenterY));
        Fn("FVCX", 1, 1, "Field vignetting compression x.", (m, v) => N(Field(m, v).CompressionX));
        Fn("FVCY", 1, 1, "Field vignetting compression y.", (m, v) => N(Field(m, v).CompressionY));
        Fn("FTAN", 1, 1, "Field tangential angle (degrees).", (m, v) => N(Field(m, v).TiltDegrees));
        FunctionNotBound("FVAN", "Obsolete in the help (FTAN is bound).");
        Fn0("FTYP", "Field type: 0 angle, 1 object height, 2 paraxial image height, 3 real image height, 4 theodolite angle.", m => N((int)FieldTypeOf(m)));
        Fn0("MAXF", "The largest radial field (degrees or lens units).", m => N(Fields(m).Count == 0 ? 0 : Fields(m).Max(f => Math.Sqrt(f.X * f.X + f.Y * f.Y))));
        Fn0("ATYP", "Aperture type: 0 entrance pupil diameter, 1 image space F/#, 2 object space NA, 3 float by stop size; another type fails (the help lists these four).", m =>
        {
            var t = ApertureTypeOf(m);
            return (int)t <= 3 ? N((int)t) : throw new ZplRuntimeException($"the aperture type {ApertureTypes.Names.Label(t)} has no code in the help's ATYP list.");
        });
        Fn0("AVAL", "Aperture value.", m => N(Sys(m).ApertureValue));
        Fn0("UNIT", "Lens unit: 0 mm, 1 cm, 2 in, 3 m.", m => N((int)m.Session.Unit));
        Fn0("$UNITS", "MM, CM, IN or M.", m => S(m.Session.Unit switch { LensUnit.Millimeter => "MM", LensUnit.Centimeter => "CM", LensUnit.Inch => "IN", _ => "M" }));
        Fn0("$LENSNAME", "The lens title.", m => S(Sys(m).Title ?? ""));
        Fn("$NOTE", 1, 1, "Line n of the notes (a line ends at a newline or after 100 characters); empty past the end.", (m, v) =>
        {
            int n = Int(v, 0, "the line number");
            var lines = NoteLines(Sys(m).Notes ?? "");
            return S(n >= 1 && n <= lines.Count ? lines[n - 1] : "");
        });
        Fn("$GLASSCATALOG", 1, 1, "The i-th active glass catalog; all names separated by spaces for i < 1.", (m, v) =>
        {
            int i = Int(v, 0, "i");
            var names = m.Session.System.Materials?.ActiveCatalogs is { Count: > 0 } active ? active : m.Session.Catalogs.Select(c => c.Name).ToList();
            if (i < 1) return S(string.Join(" ", names));
            return i <= names.Count ? S(names[i - 1]) : throw new ZplRuntimeException($"there are {names.Count} catalogs, not {i}.");
        });
    }

    private const string SystemCodesNote = "4 adjust index, 10 aperture type (0–3), 11 aperture value, 12 apodization type, 13 apodization factor, 14 telecentric, 16 title, 17 notes, 18 afocal, 23 glass catalogs, 24 temperature, 25 pressure, 30 lens units, 35 MTF units, 70 ray aiming (1 paraxial, 2 real), 71–73 pupil shift, 75 robust aiming, 76 scale shift by field, 77–78 pupil compress, 100 field type, 101 number of fields, 102–109 field data, 110 field normalization, 200 primary wavelength, 201 number of wavelengths, 202–203 wavelength and weight.";

    private static OpticsSystemSettings Sys(ZplInterpreter m)
    {
        var d = m.Session.Current.SystemData ?? new OpticsSystemSettings();
        if (d.Fields is null || d.Wavelengths is null)
        {
            var normal = SystemDataSettings.ToSettings(SystemDataSettings.FromSettings(d, new List<string>()));
            d = d with { Fields = d.Fields ?? normal.Fields, Wavelengths = d.Wavelengths ?? normal.Wavelengths };
        }
        return d;
    }

    private static List<OpticsFieldSettings> Fields(ZplInterpreter m) => Sys(m).Fields!;
    private static List<OpticsWavelengthSettings> Waves(ZplInterpreter m) => Sys(m).Wavelengths!;

    private static OpticsFieldSettings Field(ZplInterpreter m, ZplValue[] v)
    {
        int i = Int(v, 0, "the field number");
        var f = Fields(m);
        return i >= 1 && i <= f.Count ? f[i - 1] : throw new ZplRuntimeException($"field {i} does not exist; there are {f.Count}.");
    }

    private static OpticsWavelengthSettings Wave(ZplInterpreter m, ZplValue[] v)
    {
        int i = Int(v, 0, "the wavelength number");
        var w = Waves(m);
        return i >= 1 && i <= w.Count ? w[i - 1] : throw new ZplRuntimeException($"wavelength {i} does not exist; there are {w.Count}.");
    }

    private static FieldType FieldTypeOf(ZplInterpreter m) =>
        SystemDataSettings.Pick(FieldTypes.Names, Sys(m).FieldType, FieldType.Angle, "field type", new List<string>());

    private static ApertureType ApertureTypeOf(ZplInterpreter m) =>
        SystemDataSettings.Pick(ApertureTypes.Names, Sys(m).ApertureType, ApertureType.EntrancePupilDiameter, "aperture type", new List<string>());

    private static List<string> NoteLines(string notes)
    {
        var lines = new List<string>();
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            string rest = raw;
            do { lines.Add(rest.Length > 100 ? rest[..100] : rest); rest = rest.Length > 100 ? rest[100..] : ""; } while (rest.Length > 0);
        }
        return lines;
    }

    /// <summary>Changes the system data of the lens as entered (with the field and wavelength
    /// lists filled in).</summary>
    private static void SetSys(ZplInterpreter m, Func<OpticsSystemSettings, OpticsSystemSettings> change, string who)
    {
        m.Session.RequireSingleConfiguration(who);
        var d = m.Session.System.SystemData ?? new OpticsSystemSettings();
        if (d.Fields is null || d.Wavelengths is null)
        {
            var normal = SystemDataSettings.ToSettings(SystemDataSettings.FromSettings(d, new List<string>()));
            d = d with { Fields = d.Fields ?? normal.Fields, Wavelengths = d.Wavelengths ?? normal.Wavelengths };
        }
        m.Session.System = m.Session.System with { SystemData = change(d) };
    }

    private static void SetSystemProperty(ZplInterpreter m, ZplArgs a)
    {
        if (a.Count is < 2 or > 3) throw new ZplRuntimeException("SETSYSTEMPROPERTY takes code, value1 [, value2].");
        int code = a.Int(m, 0, "the property code");
        var v1 = a.Value(m, 1, "value1");
        double Num1() => v1.IsString ? throw new ZplRuntimeException($"code {code} takes a number, not the string \"{v1.Text}\".") : v1.Number;
        string Str1() => v1.IsString ? v1.Text! : throw new ZplRuntimeException($"code {code} takes a string.");
        bool Flag1() { double x = Num1(); return x == 0 ? false : x == 1 ? true : throw new ZplRuntimeException($"code {code} takes 0 or 1, not {F(x)}."); }
        int Code1(int max, string what) { int c = Int(Num1(), what); return c >= 0 && c <= max ? c : throw new ZplRuntimeException($"{what} must be 0 to {max}, not {c}."); }
        double Num2() => a.Has(2) ? a.Num(m, 2, "value2") : throw new ZplRuntimeException($"code {code} needs value2.");
        const string who = "SETSYSTEMPROPERTY";
        switch (code)
        {
            case 4: { bool b = Flag1(); SetSys(m, d => d with { AdjustIndexToEnvironment = b }, who); break; }
            case 10: { var t = (ApertureType)Code1(3, "the aperture type code (0 EPD, 1 F/#, 2 NA, 3 float by stop size; ATYP)"); SetSys(m, d => d with { ApertureType = ApertureTypes.Names.SavedName(t) }, who); break; }
            case 11: { double x = Num1(); SetSys(m, d => d with { ApertureValue = x }, who); break; }
            case 12: { var t = (ApodizationType)Code1(2, "the apodization type (0 uniform, 1 Gaussian, 2 cosine cubed)"); SetSys(m, d => d with { Apodization = ApodizationTypes.Names.SavedName(t) }, who); break; }
            case 13: { double x = Num1(); SetSys(m, d => d with { ApodizationFactor = x }, who); break; }
            case 14: { bool b = Flag1(); SetSys(m, d => d with { TelecentricObjectSpace = b }, who); break; }
            case 16: { string t = Str1(); SetSys(m, d => d with { Title = t }, who); break; }
            case 17: { string t = Str1(); SetSys(m, d => d with { Notes = t }, who); break; }
            case 18: { bool b = Flag1(); SetSys(m, d => d with { AfocalImageSpace = b }, who); break; }
            case 23:
            {
                var names = Str1().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
                m.Session.System = m.Session.System with { Materials = (m.Session.System.Materials ?? new OpticsMaterialsSettings()) with { ActiveCatalogs = names } };
                break;
            }
            case 24: { double x = Num1(); SetSys(m, d => d with { TemperatureCelsius = x }, who); break; }
            case 25: { double x = Num1(); SetSys(m, d => d with { PressureAtmospheres = x }, who); break; }
            case 30: { var u = (LensUnit)Code1(3, "the lens unit code (0 mm, 1 cm, 2 in, 3 m)"); SetSys(m, d => d with { LensUnit = ConventionNames.LensUnits.SavedName(u) }, who); break; }
            case 35: { var u = (MtfUnit)Code1(1, "the MTF unit code (0 cycles/mm, 1 cycles/mrad)"); SetSys(m, d => d with { MtfUnit = MtfUnits.Names.SavedName(u) }, who); break; }
            case 70:
            {
                // "Use 0 for off, 1 for on, 2 for aberrated" (SETSYSTEMPROPERTY); GETSYSTEMDATA names
                // the same codes "0 for off, 1 for paraxial, and 2 for real".
                var mode = (RayAimingMode)Code1(2, "the ray aiming code");
                SetSys(m, d => d with { RayAiming = RayAimingModes.Names.SavedName(mode) }, who);
                break;
            }
            case 71: { double x = Num1(); SetSys(m, d => d with { PupilShiftX = x }, who); break; }
            case 72: { double x = Num1(); SetSys(m, d => d with { PupilShiftY = x }, who); break; }
            case 73: { double x = Num1(); SetSys(m, d => d with { PupilShiftZ = x }, who); break; }
            case 75: { bool b = Flag1(); SetSys(m, d => d with { RobustRayAiming = b }, who); break; }
            case 76: { bool b = Flag1(); SetSys(m, d => d with { ScalePupilShiftByField = b }, who); break; }
            case 77: { double x = Num1(); SetSys(m, d => d with { PupilCompressX = x }, who); break; }
            case 78: { double x = Num1(); SetSys(m, d => d with { PupilCompressY = x }, who); break; }
            case 100: { var t = (FieldType)Code1(4, "the field type code (FTYP)"); SetSys(m, d => d with { FieldType = FieldTypes.Names.SavedName(t) }, who); break; }
            case 101:
            {
                int n = Int(Num1(), "the number of fields");
                if (n < 1) throw new ZplRuntimeException($"the number of fields must be at least 1, not {n}.");
                SetSys(m, d => d with { Fields = Resize(d.Fields!, n, () => new OpticsFieldSettings()) }, who);
                break;
            }
            case >= 102 and <= 109:
            {
                int i = Int(Num1(), "the field number");
                double x = Num2();
                SetSys(m, d =>
                {
                    var list = d.Fields!.ToList();
                    if (i < 1 || i > list.Count) throw new ZplRuntimeException($"field {i} does not exist; there are {list.Count} (code 101 sets the number).");
                    var f = list[i - 1];
                    list[i - 1] = code switch
                    {
                        102 => f with { X = x }, 103 => f with { Y = x }, 104 => f with { Weight = x },
                        105 => f with { DecenterX = x }, 106 => f with { DecenterY = x },
                        107 => f with { CompressionX = x }, 108 => f with { CompressionY = x },
                        _ => f with { TiltDegrees = x },
                    };
                    return d with { Fields = list };
                }, who);
                break;
            }
            case 110:
            {
                var t = Code1(1, "the field normalization (0 radial, 1 rectangular)") == 0 ? FieldNormalization.Radial : FieldNormalization.Rectangular;
                SetSys(m, d => d with { FieldNormalization = ConventionNames.FieldNormalizations.SavedName(t) }, who);
                break;
            }
            case 200:
            {
                int n = Int(Num1(), "the primary wavelength");
                SetSys(m, d => n >= 1 && n <= d.Wavelengths!.Count ? d with { PrimaryWavelength = n }
                    : throw new ZplRuntimeException($"the primary wavelength must be from 1 to {d.Wavelengths!.Count}, not {n}."), who);
                break;
            }
            case 201:
            {
                int n = Int(Num1(), "the number of wavelengths");
                if (n < 1) throw new ZplRuntimeException($"the number of wavelengths must be at least 1, not {n}.");
                SetSys(m, d => d.PrimaryWavelength <= n ? d with { Wavelengths = Resize(d.Wavelengths!, n, () => new OpticsWavelengthSettings()) }
                    : throw new ZplRuntimeException($"the primary wavelength is {d.PrimaryWavelength}; set a primary of at most {n} (code 200) before reducing the wavelengths to {n}."), who);
                break;
            }
            case 202 or 203:
            {
                int i = Int(Num1(), "the wavelength number");
                double x = Num2();
                SetSys(m, d =>
                {
                    var list = d.Wavelengths!.ToList();
                    if (i < 1 || i > list.Count) throw new ZplRuntimeException($"wavelength {i} does not exist; there are {list.Count} (code 201 sets the number).");
                    list[i - 1] = code == 202 ? list[i - 1] with { Micrometers = x } : list[i - 1] with { Weight = x };
                    return d with { Wavelengths = list };
                }, who);
                break;
            }
            default:
                throw new ZplRuntimeException($"SETSYSTEMPROPERTY code {code} is not bound in this version (bound: {SystemCodesNote})");
        }
    }

    private static ZplValue GetSystemProperty(ZplInterpreter m, int code)
    {
        var d = Sys(m);
        ZplValue Text(string t) { m.Session.Buffer = t; return N(0); }
        ZplValue B(bool? b) => N(b == true ? 1 : 0);
        var notes = new List<string>();
        switch (code)
        {
            case 4: return B(d.AdjustIndexToEnvironment);
            case 10:
            {
                var t = ApertureTypeOf(m);
                return (int)t <= 3 ? N((int)t) : throw new ZplRuntimeException($"the aperture type {ApertureTypes.Names.Label(t)} has no code in the help's list.");
            }
            case 11: return N(d.ApertureValue);
            case 12: return N((int)SystemDataSettings.Pick(ApodizationTypes.Names, d.Apodization, ApodizationType.Uniform, "apodization", notes));
            case 13: return N(d.ApodizationFactor);
            case 14: return B(d.TelecentricObjectSpace);
            case 16: return Text(d.Title ?? "");
            case 17: return Text(d.Notes ?? "");
            case 18: return B(d.AfocalImageSpace);
            case 23: return Text(string.Join(" ", m.Session.System.Materials?.ActiveCatalogs ?? new List<string>()));
            case 24: return N(d.TemperatureCelsius);
            case 25: return N(d.PressureAtmospheres);
            case 30: return N((int)m.Session.Unit);
            case 35: return N((int)SystemDataSettings.Pick(MtfUnits.Names, d.MtfUnit, MtfUnit.CyclesPerMillimeter, "MTF unit", notes));
            case 70: return N((int)SystemDataSettings.Pick(RayAimingModes.Names, d.RayAiming, RayAimingMode.Off, "ray aiming", notes));
            case 71: return N(d.PupilShiftX ?? 0);
            case 72: return N(d.PupilShiftY ?? 0);
            case 73: return N(d.PupilShiftZ ?? 0);
            case 75: return B(d.RobustRayAiming);
            case 76: return B(d.ScalePupilShiftByField);
            case 77: return N(d.PupilCompressX ?? 0);
            case 78: return N(d.PupilCompressY ?? 0);
            case 100: return N((int)FieldTypeOf(m));
            case 101: return N(d.Fields!.Count);
            case 110: return N(SystemDataSettings.Pick(ConventionNames.FieldNormalizations, d.FieldNormalization, FieldNormalization.Radial, "field normalization", notes) == FieldNormalization.Radial ? 0 : 1);
            case 200: return N(d.PrimaryWavelength);
            case 201: return N(d.Wavelengths!.Count);
            case >= 102 and <= 109 or 202 or 203:
                throw new ZplRuntimeException($"code {code} takes value2, which SYPR does not support (help, SYPR); use FLDX, FLDY, FWGT, FVDX, FVDY, FVCX, FVCY, FTAN, WAVL or WWGT.");
            default: throw new ZplRuntimeException($"code {code} is not bound in this version (bound: {SystemCodesNote})");
        }
    }

    private static List<T> Resize<T>(List<T> list, int n, Func<T> make)
    {
        var r = list.Take(n).ToList();
        while (r.Count < n) r.Add(make());
        return r;
    }

    // ================================================================ Merit function and optimization

    private static readonly Lazy<MeritOperandRegistry> Registry = new(MeritOperandRegistry.CreateDefault);

    private static void RegisterMerit()
    {
        Fn("ONUM", 1, 1, "This program's number of a merit operand code (its place in the operand registry, from 1), or 0 for an unknown code. The numbers are not OpticStudio's.", (m, v) => N(OperandNumber(Str(v, 0, "the operand name"))));
        Fn("OCOD", 1, 1, "The same number as ONUM, for OPEV and OPEW.", (m, v) => N(OperandNumber(Str(v, 0, "the operand name"))));
        Fn("OPEV", 7, 7, "The value of operand code with int1, int2, data1–data4, evaluated alone on the current lens (configuration CONF()).", (m, v) => N(EvaluateOne(m, v, 4)));
        Fn("OPEW", 9, 9, "As OPEV with data1–data6.", (m, v) => N(EvaluateOne(m, v, 6)));
        Fn0("MFCN", "Evaluates the merit function over every configuration and returns its value; a row that cannot be computed fails, naming it.", m =>
        {
            var e = OpticsScripting.EvaluateMerit(m.Session.System, m.Session.Catalogs);
            if (e.HasFailures) throw new ZplRuntimeException("the merit function has rows that cannot be computed: " + string.Join("; ", e.Errors));
            m.Session.LastMerit = (m.Session.Version, e);
            return N(e.Value);
        });
        Fn("OPER", 2, 2, "Merit Function Editor data: column 1 type (number as ONUM; the code in $BUFFER()), 2 int1 (a BLNK's comment in $BUFFER()), 3 int2, 4–7 data1–4, 8 target, 9 weight, 10 value and 11 contribution (%) as of the last MFCN on the same lens, 12–13 data5–6.", (m, v) => Oper(m, Int(v, 0, "the row"), Int(v, 1, "the column")));
        Kw("SETOPERAND", "Sets a Merit Function Editor cell: column 1 type (ONUM number), 2 int1, 3 int2, 4–7 data1–4, 8 target, 9 weight, 10 comment, 11 type by name, 12–13 data5–6.", (m, a) =>
        {
            a.Exactly(3, "SETOPERAND");
            int row = a.Int(m, 0, "the row"), col = a.Int(m, 1, "the column");
            var rows = MeritRows(m);
            if (row < 1 || row > rows.Count) throw new ZplRuntimeException($"SETOPERAND: row {row} does not exist; the merit function has {rows.Count} rows (INSERTMFO adds one).");
            var r = rows[row - 1];
            var value = a.Value(m, 2, "the value");
            double Number() => value.IsString ? throw new ZplRuntimeException($"column {col} takes a number.") : value.Number;
            string Text() => value.IsString ? value.Text! : throw new ZplRuntimeException($"column {col} takes a string.");
            List<double> Data(int k, double x) { var d = (r.Data ?? new List<double>()).ToList(); while (d.Count < 6) d.Add(0); d[k] = x; return d; }
            r = col switch
            {
                1 => r with { Type = OperandCode(Int(Number(), "the operand number")) },
                2 => r with { Int1 = Int(Number(), "int1") },
                3 => r with { Int2 = Int(Number(), "int2") },
                >= 4 and <= 7 => r with { Data = Data(col - 4, Number()) },
                8 => r with { Target = Number() },
                9 => r with { Weight = Number() },
                10 => r with { Comment = Text() },
                11 => r with { Type = Registry.Value.Find(Text())?.Code ?? throw new ZplRuntimeException("SETOPERAND: " + Merit.Catalogue.MeritOperandCatalogue.Unknown(Text())) },
                12 or 13 => r with { Data = Data(col - 8, Number()) },
                _ => throw new ZplRuntimeException($"SETOPERAND: column {col} cannot be set (1 to 13; the value and contribution are computed)."),
            };
            rows[row - 1] = r;
            SetMerit(m, rows);
        });
        Kw("INSERTMFO", "Inserts a BLNK row at row (1 to the row count + 1).", (m, a) =>
        {
            a.Exactly(1, "INSERTMFO");
            int row = a.Int(m, 0, "the row");
            var rows = MeritRows(m);
            if (row < 1 || row > rows.Count + 1) throw new ZplRuntimeException($"INSERTMFO: the row must be from 1 to {rows.Count + 1}, not {row}.");
            rows.Insert(row - 1, new OpticsMeritOperandSettings { Type = "BLNK" });
            SetMerit(m, rows);
        });
        Kw("DELETEMFO", "Deletes a row, or every row (ALL).", (m, a) =>
        {
            a.Exactly(1, "DELETEMFO");
            var rows = MeritRows(m);
            if (a.Word(0) == "ALL") rows.Clear();
            else
            {
                int row = a.Int(m, 0, "the row");
                if (row < 1 || row > rows.Count) throw new ZplRuntimeException($"DELETEMFO: row {row} does not exist; the merit function has {rows.Count} rows.");
                rows.RemoveAt(row - 1);
            }
            SetMerit(m, rows);
        });
        Kw("OPTIMIZE", "Local optimization (LocalOptimizer): cycles 0 automatic or 1–99 fixed; algorithm 0 damped least squares, 1 orthogonal descent.", (m, a) =>
        {
            a.Max(2, "OPTIMIZE");
            int cycles = a.Int(m, 0, "the number of cycles", 0);
            int algorithm = a.Int(m, 1, "the algorithm", 0);
            if (cycles < 0 || cycles > 99) throw new ZplRuntimeException($"OPTIMIZE: the number of cycles must be from 0 (automatic) to 99, not {cycles}.");
            if (algorithm is not (0 or 1)) throw new ZplRuntimeException($"OPTIMIZE: the algorithm must be 0 (damped least squares) or 1 (orthogonal descent), not {algorithm}.");
            var result = LocalOptimizer.Run(m.Session.System, m.Session.Catalogs, new LocalOptimizationOptions
            {
                Cycles = cycles == 0 ? LocalOptimizationCycles.Automatic : LocalOptimizationCycles.Fixed,
                CycleCount = cycles == 0 ? 5 : cycles,
                Algorithm = algorithm == 0 ? LocalOptimizationAlgorithm.DampedLeastSquares : LocalOptimizationAlgorithm.OrthogonalDescent,
                CancellationToken = m.Session.CancellationToken,
            });
            m.Session.System = result.Optimized;
        });
        Kw("QUICKFOCUS", "Quick Focus (OP-60): mode 0 RMS spot radius, 1 spot x, 2 spot y, 3 wavefront; centroid 0 chief ray, 1 centroid.", (m, a) =>
        {
            a.Max(2, "QUICKFOCUS");
            int mode = a.Int(m, 0, "the mode", 0);
            int centroid = a.Int(m, 1, "the centroid flag", 0);
            if (mode is < 0 or > 3) throw new ZplRuntimeException($"QUICKFOCUS: the mode must be 0 to 3, not {mode}.");
            if (centroid is not (0 or 1)) throw new ZplRuntimeException($"QUICKFOCUS: the centroid flag must be 0 or 1, not {centroid}.");
            m.Session.System = QuickFocus.Run(m.Session.System, m.Session.Catalogs,
                new QuickFocusOptions { Criterion = (QuickFocusCriterion)mode, UseCentroid = centroid == 1 }, m.Session.CancellationToken).Settings;
        });
    }

    private static int OperandNumber(string name)
    {
        var all = Registry.Value.All;
        for (int i = 0; i < all.Count; i++)
            if (string.Equals(all[i].Code, name.Trim(), StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    private static string OperandCode(int number)
    {
        var all = Registry.Value.All;
        return number >= 1 && number <= all.Count ? all[number - 1].Code
            : throw new ZplRuntimeException($"{number} is not an operand number (ONUM gives them; 1 to {all.Count}).");
    }

    private static List<OpticsMeritOperandSettings> MeritRows(ZplInterpreter m) =>
        m.Session.System.MeritFunction?.Operands?.ToList() ?? new List<OpticsMeritOperandSettings>();

    private static void SetMerit(ZplInterpreter m, List<OpticsMeritOperandSettings> rows) =>
        m.Session.System = m.Session.System with { MeritFunction = new OpticsMeritFunctionSettings { Operands = rows } };

    private static double EvaluateOne(ZplInterpreter m, ZplValue[] v, int dataCount)
    {
        string code = OperandCode(Int(v, 0, "the operand code"));
        var def = Registry.Value.Get(code);
        if (def.Control != MeritControl.None) throw new ZplRuntimeException($"{code} is a control operand and has no value.");
        if (def.References is not null) throw new ZplRuntimeException($"{code} reads other rows of the merit function, so it cannot be evaluated alone (help, OPEV).");
        var data = new double[dataCount];
        for (int k = 0; k < dataCount; k++) data[k] = Num(v, 3 + k, $"data{k + 1}");
        var row = MeritOperandRow.Of(code, Int(v, 1, "int1"), Int(v, 2, "int2"), 0, 0, data);
        var sys = m.Session.System;
        if (sys.Surfaces is not { Count: > 0 }) throw new ZplRuntimeException("the lens has no surfaces.");
        var configs = new ProjectMeritConfigurations(sys, m.Session.Catalogs);
        var e = MeritEvaluator.Evaluate(new MeritFunction(new[] { row }), configs, Registry.Value,
            new MeritEvaluationOptions { StartConfiguration = MultiConfiguration.CurrentConfiguration(sys) });
        var r = e.Rows[0];
        if (r.Error is not null) throw new ZplRuntimeException(r.Error);
        return r.Value;
    }

    private static ZplValue Oper(ZplInterpreter m, int row, int col)
    {
        var rows = MeritRows(m);
        if (row < 1 || row > rows.Count) throw new ZplRuntimeException($"row {row} does not exist; the merit function has {rows.Count} rows.");
        var r = rows[row - 1];
        double D(int k) => r.Data is { } d && k < d.Count ? d[k] : 0;
        switch (col)
        {
            case 1: m.Session.Buffer = r.Type ?? ""; return N(OperandNumber(r.Type ?? ""));
            case 2:
                if (string.Equals(r.Type, "BLNK", StringComparison.OrdinalIgnoreCase)) { m.Session.Buffer = r.Comment ?? ""; return N(0); }
                return N(r.Int1);
            case 3: return N(r.Int2);
            case >= 4 and <= 7: return N(D(col - 4));
            case 8: return N(r.Target);
            case 9: return N(r.Weight);
            case 10 or 11:
            {
                if (m.Session.LastMerit is not { } last || last.Version != m.Session.Version)
                    throw new ZplRuntimeException("the value and contribution are those of the last MFCN(), and the lens or merit function has changed since (or MFCN() was not called).");
                var rr = last.Evaluation.Rows[row - 1];
                return N(col == 10 ? rr.Value : rr.Contribution);
            }
            case 12 or 13: return N(D(col - 8));
            default: throw new ZplRuntimeException($"column {col} is not a column of OPER (1 to 13).");
        }
    }

    // ================================================================ Analyses and lens files

    /// <summary>GETTEXTFILE's analysis codes (help, String Codes) mapped to this program's
    /// analyses of the same name.</summary>
    private static readonly Dictionary<string, string> AnalysisCodes = new(StringComparer.Ordinal)
    {
        ["Ray"] = "RayAberration", ["Opd"] = "OpdFan", ["Pab"] = "PupilAberration", ["Spt"] = "StandardSpot",
        ["Stf"] = "ThroughFocusSpot", ["Sff"] = "FullFieldSpot", ["Sma"] = "MatrixSpot", ["Mtf"] = "FftMtf",
        ["Tfm"] = "FftThroughFocusMtf", ["Smf"] = "FftSurfaceMtf", ["Mth"] = "FftMtfVsField", ["Fmm"] = "FftMtfMap",
        ["Gmm"] = "GeometricMtfMap", ["Hmf"] = "HuygensMtf", ["Hsm"] = "HuygensSurfaceMtf", ["Hmh"] = "HuygensMtfVsField",
        ["Gtf"] = "GeometricMtf", ["Tfg"] = "GeometricThroughFocusMtf", ["Htf"] = "HuygensThroughFocusMtf",
        ["Gvf"] = "GeometricMtfVsField", ["Fps"] = "FftPsf", ["Pcs"] = "FftPsfCrossSection", ["Lsf"] = "FftLineEdgeSpread",
        ["Hps"] = "HuygensPsf", ["Hcs"] = "HuygensPsfCrossSection", ["Wfm"] = "WavefrontMap", ["Int"] = "Interferogram",
        ["Ffa"] = "FullFieldAberration", ["Foa"] = "FoucaultAnalysis", ["Srs"] = "SurfaceSag", ["Scv"] = "SurfaceCurvature",
        ["Ssc"] = "SurfaceSagCrossSection", ["Scc"] = "SurfaceCurvatureCrossSection", ["Rms"] = "RmsVsField",
        ["Rmw"] = "RmsVsWavelength", ["Rmf"] = "RmsVsFocus", ["Rfm"] = "RmsFieldMap", ["Enc"] = "DiffractionEnclosedEnergy",
        ["Gee"] = "GeometricEnclosedEnergy", ["Lin"] = "GeometricLineEdgeSpread", ["Xse"] = "ExtendedSourceEnclosedEnergy",
        ["Rel"] = "RelativeIllumination", ["Foo"] = "FootprintDiagram", ["Fcd"] = "FieldCurvatureDistortion",
        ["Grd"] = "GridDistortion", ["Lon"] = "LongitudinalAberration", ["Lat"] = "LateralColor", ["Agm"] = "AthermalGlassMap",
        ["Car"] = "CardinalPoints", ["Cfs"] = "ChromaticFocalShift", ["Dis"] = "DispersionVsWavelength",
        ["Gbp"] = "ParaxialGaussianBeam", ["Gcp"] = "GlassCompare", ["Gft"] = "GlassFit", ["Gho"] = "GhostFocusGenerator",
        ["Gmp"] = "GlassMap", ["Iht"] = "IncidentAngleVsImageHeight", ["Pal"] = "PowerFieldMap",
        ["Pha"] = "PolarizationPhaseAberration", ["Pmp"] = "PolarizationPupilMap", ["Pol"] = "PolarizationRayTrace",
        ["Ppm"] = "PowerPupilMap", ["Pre"] = "PrescriptionData", ["Ptf"] = "PolarizationTransmissionFan",
        ["Rtr"] = "SingleRayTrace", ["Sag"] = "SagTable", ["Sdi"] = "SeidelDiagram", ["Sei"] = "SeidelCoefficients",
        ["Ssg"] = "SystemSummaryGraphic", ["Sur"] = "SurfaceData", ["Sys"] = "SystemData", ["Tra"] = "PolarizationTransmission",
        ["Vig"] = "VignettingPlot", ["Yni"] = "YniContributions", ["Yyb"] = "YYbarDrawing",
        // wire-13: Universal Plot (OP-45), physical optics (OP-49) and image simulation (OP-42).
        ["Uni"] = "UniversalPlot1D", ["Un2"] = "UniversalPlot2D", ["Pop"] = "PhysicalOpticsPropagation", ["Bfv"] = "BeamFileViewer",
        ["Sim"] = "ImageSimulation", ["Ima"] = "GeometricImageAnalysis", ["Ibm"] = "GeometricBitmapImageAnalysis",
        ["Dim"] = "PartiallyCoherentImageAnalysis", ["Pci"] = "PartiallyCoherentImageAnalysis", ["Xdi"] = "ExtendedDiffractionImageAnalysis",
        ["Imv"] = "ImaBimFileViewer", ["Jbv"] = "BitmapFileViewer",
    };

    /// <summary>GETTEXTFILE's analysis codes and the analysis each runs, for the documentation.</summary>
    public static IReadOnlyDictionary<string, string> TextFileCodes => AnalysisCodes;

    private static void RegisterFilesAndAnalyses()
    {
        Kw("GETTEXTFILE", "Writes an analysis's text (its default settings) to a file (UTF-8): the 3-letter code of the help's String Codes for the analyses this program has of that name (Conventions §56); no code is the single ray trace (Rtr), as the help says. A settings file is not read or written (fails).", (m, a) =>
        {
            if (a.Count is < 1 or > 4) throw new ZplRuntimeException("GETTEXTFILE takes textfilename, type [, settingsfilename, flag].");
            string path = ZplSession.Resolve(a.FileName(m, 0, "the text file name"), m.Session.MacroFolder, "macro folder");
            string code = a.Has(1) ? a.FileName(m, 1, "the analysis code").Trim() : "Rtr";   // case sensitive: kept as written
            if (a.Has(2)) throw new ZplRuntimeException("GETTEXTFILE: settings files are not read or written in this version (OpticStudio's .CFG files are not this program's settings); give the text file and type only.");
            if (!AnalysisCodes.TryGetValue(code, out var id))
                throw new ZplRuntimeException($"GETTEXTFILE: the analysis code '{code}' is not one this program maps (codes are case sensitive; the mapped ones are listed in docs/Optics-User-Guide.md, \"ZPL macros\").");
            var result = OpticsScripting.Analyze(m.Session.System, m.Session.Catalogs, id);
            File.WriteAllText(path, result.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        });
        Kw("LOADLENS", "Replaces the lens with a .zmx or .zar (OP-94 reader), .osslens or .ossproj file; a name without a folder is in the lens folder. Appending (appendflag ≠ 0) and session files are not bound.", (m, a) =>
        {
            if (a.Count is < 1 or > 3) throw new ZplRuntimeException("LOADLENS takes filename [, appendflag, session].");
            if (a.Has(1) && a.Num(m, 1, "appendflag") != 0) throw new ZplRuntimeException("LOADLENS with an append flag is not bound in this version.");
            if (a.Has(2) && a.Num(m, 2, "session") != 0) throw new ZplRuntimeException("LOADLENS: session files are not built in this program.");
            string path = ZplSession.Resolve(a.FileName(m, 0, "the lens file"), m.Session.LensFolder, "lens folder");
            if (!File.Exists(path)) throw new ZplRuntimeException($"LOADLENS: the file '{path}' does not exist.");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            OpticsSettings optics = ext switch
            {
                ".zmx" or ".zar" => ZemaxLensFiles.Import(path, m.Session.Catalogs.Count > 0 ? m.Session.Catalogs : null).Optics,
                var e when e == OpticsFileSerializer.FileExtension => new OpticsFileSerializer().Load(path).Optics ?? new OpticsSettings(),
                ".ossproj" => new ProjectSerializer().Load(path).Optics ?? throw new ZplRuntimeException($"LOADLENS: the project '{path}' has no optical system."),
                _ => throw new ZplRuntimeException($"LOADLENS: '{ext}' files are not read (.zmx, .zar, {OpticsFileSerializer.FileExtension}, .ossproj are)."),
            };
            m.Session.System = optics;
            m.Session.LensPath = path;
        });
        Kw("SAVELENS", "Saves the lens as .zmx (OP-94 writer) or .osslens; without a name, to the lens's own file. Session files are not bound.", (m, a) =>
        {
            a.Max(2, "SAVELENS");
            if (a.Has(1) && a.Num(m, 1, "session") != 0) throw new ZplRuntimeException("SAVELENS: session files are not built in this program.");
            string path = a.Has(0) ? ZplSession.Resolve(a.FileName(m, 0, "the lens file"), m.Session.LensFolder, "lens folder")
                : m.Session.LensPath ?? throw new ZplRuntimeException("SAVELENS without a name needs the lens's own file, and it has none.");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".zmx") ZemaxLensFiles.Export(m.Session.System, path);
            else if (ext == OpticsFileSerializer.FileExtension)
                new OpticsFileSerializer().Save(new OpticsLensFile { Name = Path.GetFileNameWithoutExtension(path), Optics = m.Session.System }, path);
            else throw new ZplRuntimeException($"SAVELENS: '{ext}' files are not written (.zmx and {OpticsFileSerializer.FileExtension} are).");
            m.Session.LensPath = path;
        });
    }

    private static double[] Vector(ZplInterpreter m, int n, string who) => n is >= 1 and <= 4 ? m.Session.Vectors[n - 1]
        : throw new ZplRuntimeException($"{who}: the vector must be 1 to 4, not {n}.");

    // ================================================================ Not bound

    private static void RegisterLensNotBound()
    {
        NotBound("COMPOSITEOFFAXISAPERTUREON", "Not bound: its OpticStudio 2025 R2 help page repeats COMPOSITEON's title, syntax and discussion, so what it does is not given (it may be the Composite page's \"Set Tilt/Decenter to follow Base surface aperture\", which is not built).");
        // wire-14: the non-sequential keywords that OP-70's core does not reach, each with the plan item it waits for.
        foreach (var k in new[] { "INSERTOBJECT", "DELETEOBJECT" })
            NotBound(k, "Not bound: inserting and deleting objects (renumbering the references to them) is the Non-Sequential Component Editor's, plan item OP-71.");
        foreach (var k in new[] { "NSTR", "NSTR2" })
            NotBound(k, "Not bound: a trace starts from the system's source objects, which are plan item OP-77 (OP-70's source stand-in is for code, not a saved object); saving rays to ZRD and PAF files is OP-83.");
        foreach (var k in new[] { "LOADDETECTOR", "SAVEDETECTOR", "SETDETECTOR" })
            NotBound(k, "Not bound: detector objects and their data are plan item OP-79.");
        NotBound("GETNSCMTF", "Not bound: it reads detector objects, plan item OP-79 (the non-sequential image-quality analyses are OP-87).");
        NotBound("MAKEFACETLIST", "Not bound: imported CAD parts are plan item OP-75.");
        NotBound("NSLT", "Not bound: Lightning Trace is plan item OP-85.");
        NotBound("RELOADOBJECTS", "Not bound: objects read from files (polygon objects, CAD parts) are plan items OP-74 and OP-75; OP-70's objects are parametric, so there is nothing to reload.");
        foreach (var k in new[] { "ZRDAPPEND", "ZRDFILTER", "ZRDPLAYBACK", "ZRDSAVERAYS", "ZRDSUM" })
            NotBound(k, "Not bound: the ray database (ZRD) files and filter strings are plan item OP-83.");
        // wire-13: POP, the ZBF keywords, IMA, IMASHOW, IMASUM, XDIFFIA and IMAE are in ZplBeamLibrary.cs.
        NotBound("DEFAULTMERIT", "Not bound: its arguments map onto the Optimization Wizard (OpticsScripting.WithDefaultMerit), but \"any existing default merit function will be deleted\" needs the help's rule for which rows are the old default, which it does not give.");
        NotBound("HAMMER", "Not bound: the Hammer search (OP-58) runs by time, not by cycles.");
        NotBound("TOLERANCE", "Not bound.");
        NotBound("QUICKSENSITIVITY", "Not bound.");
        NotBound("TESTPLATEFIT", "Not bound.");
        NotBound("GLASSTEMPLATE", "Not bound.");
        NotBound("GLOBALTOLOCAL", "Not bound.");
        NotBound("LOCALTOGLOBAL", "Not bound.");
        NotBound("SETVIG", "Not bound.");
        NotBound("SETAIMDATA", "Not bound (SETAIM and SETSYSTEMPROPERTY 70–78 are).");
        NotBound("SCATTER", "Not bound: surface scattering is not built.");
        NotBound("LOADARCHIVE", "Not bound (LOADLENS reads a .zar).");
        NotBound("SAVEARCHIVE", "Not bound.");
        NotBound("LOADCATALOG", "Not bound: catalogs are loaded once per run.");
        NotBound("LOADMERIT", "Not bound: OpticStudio's .MF file layout is not published.");
        NotBound("SAVEMERIT", "Not bound: OpticStudio's .MF file layout is not published.");
        NotBound("LOADTOLERANCE", "Not bound.");
        NotBound("SAVETOLERANCE", "Not bound.");
        NotBound("IMPORTEXTRADATA", "Not bound.");
        foreach (var k in new[] { "INSERTCONFIG", "DELETECONFIG", "INSERTMCO", "DELETEMCO", "SETMCOPERAND" })
            NotBound(k, "Not bound: the multi-configuration editor's keywords are not bound (SETCONFIG is).");
        foreach (var k in new[] { "INSERTTOL", "DELETETOL", "SETTOL" })
            NotBound(k, "Not bound: the tolerance editor's keywords are not bound.");
        foreach (var k in new[] { "GETSYSTEMDATA", "GETEXTRADATA", "GETGLASSDATA", "GETMTF", "GETPSF", "GETLSF", "GETZERNIKE", "GETDENCUSER1D", "GETMTFUSER1D" })
            NotBound(k, "Not bound (GETTEXTFILE writes the analyses' text).");

        // wire-14: NOBJ, NPAR, NPOS, NPRO and OBJC are bound in ZplObjectsLibrary.cs.
        FunctionNotBound("LOST", "Not bound: it reads the most recent NSTR trace, and NSTR waits for source objects (plan item OP-77).");
        foreach (var f in new[] { "NSDD", "NSDE" })
            FunctionNotBound(f, "Not bound: detector data follows an NSTR trace, which waits for source objects (OP-77); detector objects are OP-79 (OP-70's detecting stand-in counts flux only).");
        foreach (var f in new[] { "NSDC", "NSDP" })
            FunctionNotBound(f, "Not bound: detector rectangle (coherent) and detector polar objects are plan item OP-79.");
        foreach (var f in new[] { "GLCA", "GLCB", "GLCC", "GLCM", "GLCX", "GLCY", "GLCZ" })
            FunctionNotBound(f, "Not bound: OpticStudio's global coordinate reference surface is not read (see RAGX).");
        FunctionNotBound("EDGE", "Not bound.");
        FunctionNotBound("SAGG", "Not bound.");
        FunctionNotBound("GABB", "Not bound.");
        FunctionNotBound("GIND", "Not bound.");
        FunctionNotBound("GPAR", "Not bound.");
        FunctionNotBound("GNUM", "Not bound: glass numbers depend on OpticStudio's catalog order.");
        FunctionNotBound("MAXG", "Not bound.");
        FunctionNotBound("GRIN", "Not bound.");
        FunctionNotBound("GTEM", "Not bound.");
        FunctionNotBound("ISCS", "Not bound.");
        FunctionNotBound("ISMS", "Not bound.");
        FunctionNotBound("APOI", "Not bound.");
        // wire-14: FICL is bound in ZplObjectsLibrary.cs.
        FunctionNotBound("RELI", "Not bound.");
        FunctionNotBound("MCON", "Not bound.");
        FunctionNotBound("MCOP", "Not bound.");
        FunctionNotBound("SOLM", "Not bound.");
        FunctionNotBound("SOLV", "Not bound.");
        FunctionNotBound("TOLV", "Not bound.");
        FunctionNotBound("TMAS", "Not bound.");
        FunctionNotBound("STDD", "Obsolete in the help.");
        FunctionNotBound("$TOLCOMMENT", "Not bound.");
        FunctionNotBound("$TOLOPERAND", "Not bound.");
    }
}
