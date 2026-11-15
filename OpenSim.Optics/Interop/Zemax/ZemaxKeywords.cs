namespace OpenSim.Optics.Interop.Zemax;

/// <summary>
/// The keyword tables of the Zemax lens file reader and writer (OP-94, docs/Optics-Conventions.md
/// section 52). The format is not published by its vendor; the keyword meanings here come from
/// two permissively licensed readers, read as documentation and cited by file and function:
/// <list type="bullet">
/// <item>Optiland 0.6.3 (MIT), optiland/fileio/zemax/reader/parser.py (<c>ZemaxDataParser</c>,
/// its operand table and handlers) and writer/encoder.py (<c>ZemaxFileEncoder</c>);</item>
/// <item>RayOptics 0.9.10 (BSD 3-clause), rayoptics/zemax/zmxread.py (<c>process_line</c>,
/// <c>handle_types_and_params</c>, <c>handle_aperture_data</c>, <c>pupil_data</c>,
/// <c>field_spec_data</c>, <c>ZmxGlassHandler</c>).</item>
/// </list>
/// Where a meaning is read from the OpticStudio-written files those readers ship as test data
/// (rayoptics/zemax/tests/*.zmx, rayoptics/elem/tests/ACL3026U-Zemax(ZMX).zmx) and not from a
/// reader's code, the entry says "observed" and docs/Optics-Conventions.md section 10 item 48
/// lists it as not yet checked against OpticStudio itself.
/// </summary>
public static class ZemaxKeywords
{
    /// <summary>
    /// Surface type codes (the TYPE line) and this program's type names (null is Standard).
    /// STANDARD, EVENASPH, ODDASPHE, COORDBRK, TOROIDAL: Optiland parser.py
    /// <c>_read_surf_type</c>. XASPHERE, XOSPHERE, PARAXIAL: RayOptics zmxread.py
    /// <c>handle_types_and_params</c> (XASPHERE and XOSPHERE both read there as a radial
    /// polynomial whose XDAT 1 is the number of terms, 2 the normalization radius, 3 on the
    /// coefficients; which one is the odd form is read from the names, observed). QED_TYPE:
    /// observed in rayoptics/zemax/tests/ASL5040-UV-Zemax(ZMX).zmx, whose XDAT 1–9 (type 0,
    /// 6 terms, normalization radius 27, six coefficients) match the Q-Type Asphere's extra data
    /// (type, maximum term, normalization radius, A0 …) of OpticStudio's help page.
    /// <para>
    /// OP-25's types (POLYNOMI, XPOLYNOM, FZERNSAG, SZERNSAG, AZERNSAG, OFFAXISCF, RADNURBS): no
    /// lens-file reader names them, so their codes are copied from the surface-type name table of
    /// OpticStudio 2025 R2 help, "SETSURFACEPROPERTY, SURP" (the ZPL TYPE names, which the page
    /// says the Prescription Report lists). That these names are also the .zmx TYPE codes is a
    /// reading: the nine codes above, taken from the readers and files, are all spelled as in that
    /// table. Docs/Optics-Conventions.md section 10 item 48 lists it as not yet checked against an
    /// OpticStudio-written file. Their PARM/XDAT numbers map generically through the saved layouts
    /// (PARM n is parameter n, XDAT n is parameter n + 12), as for the types above.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Code, string? TypeName)> SurfaceTypes { get; } = new (string, string?)[]
    {
        ("STANDARD", null),
        ("EVENASPH", "Even Asphere"),
        ("ODDASPHE", "Odd Asphere"),
        ("XASPHERE", "Extended Asphere"),
        ("XOSPHERE", "Extended Odd Asphere"),
        ("QED_TYPE", "Q-Type Asphere"),
        ("TOROIDAL", "Toroidal"),
        ("PARAXIAL", "Paraxial"),
        ("COORDBRK", Sequential.SurfaceTypes.CoordinateBreak),
        // OP-25: codes from OpticStudio 2025 R2 help, "SETSURFACEPROPERTY, SURP", surface-type name table.
        ("POLYNOMI", "Polynomial"),
        ("XPOLYNOM", "Extended Polynomial"),
        ("FZERNSAG", "Zernike Fringe Sag"),
        ("SZERNSAG", "Zernike Standard Sag"),
        ("AZERNSAG", "Zernike Annular Standard Sag"),
        ("OFFAXISCF", "Off-Axis Conic Freeform"),
        ("RADNURBS", "Radial NURBS"),
    };

    /// <summary>
    /// Types this program has whose TYPE code is known (same help table) but which the lens file
    /// cannot carry, each with the reason the reader and writer give. Grid Sag (GRID_SAG): its
    /// grid is a separate .DAT file and no source this build has shows how, or whether, a .zmx
    /// stores it, so a Grid Sag is refused by name both ways rather than written without its grid.
    /// </summary>
    public static IReadOnlyList<(string Code, string TypeName, string Reason)> Refused { get; } = new[]
    {
        ("GRID_SAG", Sequential.Freeform.FreeformLayouts.GridSag,
            "a Grid Sag's grid is a separate .DAT data file and no source shows how a .zmx stores it, so the surface is not carried by the lens file; import the grid in the Lens Data Editor instead"),
    };

    /// <summary>The refusal reason of a TYPE code or type name in <see cref="Refused"/>, or null.</summary>
    public static string? RefusalOf(string codeOrTypeName)
    {
        foreach (var (c, t, why) in Refused)
            if (string.Equals(c, codeOrTypeName, StringComparison.OrdinalIgnoreCase) || string.Equals(t, codeOrTypeName, StringComparison.OrdinalIgnoreCase)) return why;
        return null;
    }

    /// <summary>This program's type name of a TYPE code, or false when the code is not read.</summary>
    public static bool TryTypeName(string code, out string? typeName)
    {
        foreach (var (c, t) in SurfaceTypes)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) { typeName = t; return true; }
        typeName = null;
        return false;
    }

    /// <summary>The TYPE code of a saved type name (null or "Standard" is STANDARD), or null
    /// when the type has no code this build writes.</summary>
    public static string? CodeOf(string? typeName)
    {
        if (typeName is null || string.Equals(typeName, Sequential.SavedSurfaces.StandardType, StringComparison.Ordinal)) return "STANDARD";
        foreach (var (c, t) in SurfaceTypes)
            if (t is not null && string.Equals(t, typeName, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>
    /// Keywords read and deliberately not used, each with the reason the import notes give.
    /// The list is RayOptics' own "not supported" list in zmxread.py <c>process_line</c> where
    /// a keyword is a display or program setting, plus keywords seen in the vendor files
    /// RayOptics ships (MEMA, OEMA, OMMA, HYPR, AUTH) that neither reader interprets.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Ignored { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["VERS"] = "the writing program's version",
        ["PFIL"] = "not interpreted by the reference readers",
        ["LANG"] = "the user-interface language",
        ["GFAC"] = "not interpreted by the reference readers",
        ["RAIM"] = "ray aiming is NOT read: its fields are not interpreted by the reference readers; set ray aiming in the system data",
        ["PUSH"] = "not interpreted by the reference readers",
        ["SDMA"] = "not interpreted by the reference readers",
        ["ROPD"] = "not interpreted by the reference readers",
        ["PICB"] = "not interpreted by the reference readers",
        ["POLS"] = "the system polarization is NOT read: its fields are not interpreted by the reference readers",
        ["GLRS"] = "not interpreted by the reference readers",
        ["GSTD"] = "not interpreted by the reference readers",
        ["NSCD"] = "non-sequential settings",
        ["COFN"] = "the coating and data file names (the coatings themselves are not in the lens file)",
        ["OPDX"] = "not interpreted by the reference readers",
        ["HIDE"] = "drawing settings",
        ["MIRR"] = "the mirror substrate drawing settings",
        ["SLAB"] = "the editor's surface label",
        ["POPS"] = "physical optics settings",
        ["FIMP"] = "not interpreted by the reference readers",
        ["MAZH"] = "not interpreted by the reference readers",
        ["MEMA"] = "not interpreted by the reference readers (the mechanical semi-diameter is not read)",
        ["OEMA"] = "not interpreted by the reference readers",
        ["OMMA"] = "not interpreted by the reference readers",
        ["HYPR"] = "not interpreted by the reference readers",
        ["AUTH"] = "the author field",
    };

    /// <summary>Notes on the keywords read only in part, emitted once per keyword used.</summary>
    public static IReadOnlyDictionary<string, string> PartlyRead { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UNIT"] = "UNIT: only the lens unit (its first field) is read; the source, analysis and MTF unit fields are not.",
        ["FTYP"] = "FTYP: the field type, telecentric flag, field count, wavelength count and afocal flag are read (Optiland parser.py _read_config_data); its other fields are not.",
        ["ENVD"] = "ENVD: the first two fields are read as the temperature in °C and the pressure in atmospheres (observed, not from a reader); the third is not read.",
        ["NOTE"] = "NOTE: the text after the first field of every NOTE line is joined, in order, into the notes.",
        ["DIAM"] = "DIAM: the value and the solve code (0 automatic, 1 fixed, 2 pickup) are read; its other fields are not (observed).",
        ["GLAS"] = "GLAS: the name, the solve code (0 fixed, 1 model, 2 pickup) and, for a model glass, nd, Vd and ΔPgF are read; the other fields are not.",
        ["CURV"] = "CURV: the curvature and the solve code (0 fixed, 1 variable, 4 pickup with its surface and scale) are read.",
        ["XDAT"] = "XDAT: the number and value are read; a non-zero solve field is not.",
        ["FLAP"] = "FLAP: read as a floating aperture, which takes the surface's semi-diameter; its fields are not stored.",
    };

    /// <summary>The keywords the reader interprets (for the documentation and the tests).</summary>
    public static IReadOnlyList<string> Read { get; } = new[]
    {
        "MODE", "NAME", "NOTE", "UNIT", "ENVD", "ENPD", "FNUM", "OBNA", "FLOA", "FTYP",
        "XFLN", "YFLN", "XFLD", "YFLD", "FWGN", "FWGT", "VDXN", "VDYN", "VCXN", "VCYN", "VANN",
        "WAVM", "WAVL", "WWGT", "PWAV", "GCAT",
        "SURF", "TYPE", "COMM", "STOP", "CURV", "DISZ", "CONI", "GLAS", "DIAM", "PARM", "XDAT",
        "PZUP", "PPAR", "VPAR", "VDSZ", "CLAP", "FLAP", "OBDC", "COAT",
        "MNUM", "MOFF", "TOL", "BLNK",
    };
}
