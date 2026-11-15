namespace OpenSim.Optics.Sequential.Solves;

/// <summary>
/// A solve: a rule that computes one cell of the lens data from a condition (OP-29).
/// <see cref="SolveEngine"/> applies a set of them to a <see cref="SequentialLens"/>.
/// <para>
/// Each solve's definition is OpticStudio's, read from its 2025 R2 help on ansyshelp.ansys.com
/// on 2026-11-14 (pages "Summary of Solves", "Curvature Solves", "Thickness Solves",
/// "Edge Thickness", "Material Solves", "Clear Semi-Diameter or Semi-Diameter Solves",
/// "Conic Solves", "TCE Solve", "Parameter Solves", "Restrictions" and "Integer Codes for
/// Column Numbers"; saved in plans/sources/optics/solves/). Where the help leaves a detail
/// open, the record says which reading this code takes (docs/Optics-Conventions.md §19).
/// Lengths are meters, curvatures 1/m, paraxial angles are slopes u = dy/dz in the system frame
/// (Conventions §11), and every paraxial solve uses the primary wavelength.
/// </para>
/// <para>
/// "Fixed" is no solve (the cell keeps its value), and "Variable" is an optimizer flag, not a
/// solve; neither has a record. The ZPL macro solve needs the macro interpreter (OP-99) and is
/// not built.
/// </para>
/// </summary>
public abstract record SurfaceSolve
{
    /// <summary>The surface whose cell the solve sets (0 is the object surface).</summary>
    public required int Surface { get; init; }

    /// <summary>The column the solve sets.</summary>
    public abstract SolveColumn Column { get; }

    /// <summary>The parameter number when <see cref="Column"/> is
    /// <see cref="SolveColumn.Parameter"/>; otherwise 0.</summary>
    public virtual int ParameterIndex => 0;

    /// <summary>The solve's name as OpticStudio's solve list gives it.</summary>
    public abstract string Name { get; }

    /// <summary>The letter OpticStudio shows in the cell ("" where the summary gives none).</summary>
    public abstract string Code { get; }

    /// <summary>The cell this solve drives.</summary>
    public SurfaceCell Cell => new(Surface, Column, Column == SolveColumn.Parameter ? ParameterIndex : 0);
}

// ---------------------------------------------------------------- Curvature solves

/// <summary>Curvature: marginal ray angle. Sets the curvature so the paraxial marginal ray
/// leaves the surface with slope <see cref="Angle"/> ("a marginal ray exit angle from the last
/// surface of −.1 … 20 divided by 2 divided by 100", "Curvature Solves").</summary>
public sealed record MarginalRayAngleSolve : SurfaceSolve
{
    /// <summary>The paraxial slope after the surface, u′ = dy/dz.</summary>
    public required double Angle { get; init; }
    public override SolveColumn Column => SolveColumn.Curvature;
    public override string Name => "Marginal ray angle";
    public override string Code => "M";
}

/// <summary>Curvature: chief ray angle. As <see cref="MarginalRayAngleSolve"/> with the
/// paraxial chief ray of the full field ("Curvature Solves").</summary>
public sealed record ChiefRayAngleSolve : SurfaceSolve
{
    /// <summary>The paraxial chief-ray slope after the surface.</summary>
    public required double Angle { get; init; }
    public override SolveColumn Column => SolveColumn.Curvature;
    public override string Name => "Chief ray angle";
    public override string Code => "C";
}

/// <summary>Curvature: marginal ray normal. "Force the surface to be normal to the paraxial
/// marginal ray" (an image-centred surface): the incident marginal ray passes through the
/// centre of curvature ("Curvature Solves").</summary>
public sealed record MarginalRayNormalSolve : SurfaceSolve
{
    public override SolveColumn Column => SolveColumn.Curvature;
    public override string Name => "Marginal ray normal";
    public override string Code => "N";
}

/// <summary>Curvature: chief ray normal (a pupil-centred surface): the incident paraxial chief
/// ray passes through the centre of curvature ("Curvature Solves").</summary>
public sealed record ChiefRayNormalSolve : SurfaceSolve
{
    public override SolveColumn Column => SolveColumn.Curvature;
    public override string Name => "Chief ray normal";
    public override string Code => "N";
}

/// <summary>Curvature: aplanatic. "Force the surface to be aplanatic with respect to the
/// paraxial marginal ray" ("Curvature Solves"). The condition used is u′/n′ = u/n of the
/// marginal ray (signed indices), derived in <see cref="SolveEngine"/>.</summary>
public sealed record AplanaticSolve : SurfaceSolve
{
    public override SolveColumn Column => SolveColumn.Curvature;
    public override string Name => "Aplanatic";
    public override string Code => "A";
}

/// <summary>Curvature: element power. Sets c₂ of this surface (the second of two adjacent
/// surfaces) so φ = c₁(n₂ − n₁) + c₂(n₃ − n₂) − c₁(n₂ − n₁)·c₂(n₃ − n₂)·t₂/n₂ equals
/// <see cref="Power"/> ("Curvature Solves", its equation). OpticStudio ignores the solve on
/// surfaces below 2 or when n₃ = n₂; this code refuses both with a message.</summary>
public sealed record ElementPowerSolve : SurfaceSolve
{
    /// <summary>The element's power, 1/m.</summary>
    public required double Power { get; init; }
    public override SolveColumn Column => SolveColumn.Curvature;
    public override string Name => "Element power";
    public override string Code => "X";
}

/// <summary>Curvature: concentric with surface. The centre of curvature is placed at the vertex
/// of surface <see cref="Of"/>, which must precede this one ("Curvature Solves").</summary>
public sealed record ConcentricWithSurfaceSolve : SurfaceSolve
{
    public required int Of { get; init; }
    public override SolveColumn Column => SolveColumn.Curvature;
    public override string Name => "Concentric with surface";
    public override string Code => "S";
}

/// <summary>Curvature: concentric with radius. The centre of curvature is placed at the centre
/// of curvature of surface <see cref="Of"/>, which must precede this one ("Curvature Solves").
/// A plane surface <see cref="Of"/> makes this surface a plane (both centres at infinity).</summary>
public sealed record ConcentricWithRadiusSolve : SurfaceSolve
{
    public required int Of { get; init; }
    public override SolveColumn Column => SolveColumn.Curvature;
    public override string Name => "Concentric with radius";
    public override string Code => "R";
}

/// <summary>Curvature: F/#. "The marginal ray angle exiting the surface is −1/2F where F is the
/// paraxial F/#" ("Curvature Solves").</summary>
public sealed record FNumberSolve : SurfaceSolve
{
    public required double FNumber { get; init; }
    public override SolveColumn Column => SolveColumn.Curvature;
    public override string Name => "F/#";
    public override string Code => "F";
}

// ---------------------------------------------------------------- Thickness solves

/// <summary>Thickness: marginal ray height. Sets the thickness so the marginal ray is at
/// <see cref="Height"/> on the tangent (vertex) plane of the next surface. Pupil zone 0 uses
/// the paraxial marginal ray; any other zone (−1 to 1) a real ray from the on-axis field at
/// Py = zone, primary wavelength ("Thickness Solves"). Not allowed on a surface before the stop
/// ("Restrictions").</summary>
public sealed record MarginalRayHeightSolve : SurfaceSolve
{
    public double Height { get; init; }
    public double PupilZone { get; init; }
    public override SolveColumn Column => SolveColumn.Thickness;
    public override string Name => "Marginal ray height";
    public override string Code => "M";
}

/// <summary>Thickness: chief ray height. As the paraxial marginal ray height solve with the
/// paraxial chief ray of the full field ("Thickness Solves").</summary>
public sealed record ChiefRayHeightSolve : SurfaceSolve
{
    public double Height { get; init; }
    public override SolveColumn Column => SolveColumn.Thickness;
    public override string Name => "Chief ray height";
    public override string Code => "C";
}

/// <summary>Thickness: edge thickness. Sets T so E = Z(next) − Z(this) + T equals
/// <see cref="EdgeThickness"/>, both sags taken at the same radial height on +y ("Edge
/// Thickness": for the solve, the next surface's sag is taken at this surface's height). A
/// radial height of 0 uses this surface's mechanical semi-diameter ("Thickness Solves"; the
/// summary table says the clear semi-diameter, see Conventions §19).</summary>
public sealed record EdgeThicknessSolve : SurfaceSolve
{
    public required double EdgeThickness { get; init; }
    /// <summary>The radial height, meters; 0 for the mechanical semi-diameter.</summary>
    public double RadialHeight { get; init; }
    public override SolveColumn Column => SolveColumn.Thickness;
    public override string Name => "Edge thickness";
    public override string Code => "E";
}

/// <summary>Thickness: optical path difference. Adjusts the thickness until the OPD of the real
/// ray at Py = <see cref="PupilZone"/> of the on-axis field, primary wavelength, measured at the
/// exit pupil, is <see cref="Waves"/> primary waves ("Thickness Solves").</summary>
public sealed record OpticalPathDifferenceSolve : SurfaceSolve
{
    public double Waves { get; init; }
    public required double PupilZone { get; init; }
    public override SolveColumn Column => SolveColumn.Thickness;
    public override string Name => "Optical path difference";
    public override string Code => "O";
}

/// <summary>Thickness: position. Keeps the z distance from <see cref="ReferenceSurface"/>
/// ("Thickness Solves"): a reference before this surface makes the thicknesses from it through
/// this one sum to <see cref="Length"/>; a reference after makes those from this surface up to
/// it sum to the length; the same surface sets the thickness to the length.</summary>
public sealed record PositionSolve : SurfaceSolve
{
    public required int ReferenceSurface { get; init; }
    public required double Length { get; init; }
    public override SolveColumn Column => SolveColumn.Thickness;
    public override string Name => "Position";
    public override string Code => "T";
}

/// <summary>Thickness: compensator. T = S − R with R the thickness of
/// <see cref="ReferenceSurface"/>, which must precede this one ("Thickness Solves").</summary>
public sealed record CompensatorSolve : SurfaceSolve
{
    public required int ReferenceSurface { get; init; }
    /// <summary>S, the sum of the two thicknesses.</summary>
    public required double Sum { get; init; }
    public override SolveColumn Column => SolveColumn.Thickness;
    public override string Name => "Compensator";
    public override string Code => "S";
}

/// <summary>Thickness: center of curvature. Places the next surface's vertex at the centre of
/// curvature of surface <see cref="Of"/> (this surface or one before it) ("Thickness Solves").</summary>
public sealed record CenterOfCurvatureSolve : SurfaceSolve
{
    public required int Of { get; init; }
    public override SolveColumn Column => SolveColumn.Thickness;
    public override string Name => "Center of curvature";
    public override string Code => "X";
}

/// <summary>Thickness: pupil position. Places the next surface at the pupil (the stop's image)
/// in the space after this surface ("Thickness Solves").</summary>
public sealed record PupilPositionSolve : SurfaceSolve
{
    public override SolveColumn Column => SolveColumn.Thickness;
    public override string Name => "Pupil position";
    public override string Code => "U";
}

// ---------------------------------------------------------------- Material solves

/// <summary>Material: model. The medium becomes a model glass of <see cref="Nd"/>,
/// <see cref="Vd"/> and <see cref="DeltaPgF"/> ("Material Solves"), made by this program's
/// model-glass stand-in (<see cref="OpenSim.Optics.Materials.ModelGlassFit"/>, not
/// OpticStudio's formula), which <see cref="SolveOptions.ModelGlass"/> supplies.</summary>
public sealed record ModelGlassSolve : SurfaceSolve
{
    public required double Nd { get; init; }
    public required double Vd { get; init; }
    public double DeltaPgF { get; init; }
    public override SolveColumn Column => SolveColumn.Material;
    public override string Name => "Model";
    public override string Code => "";
}

/// <summary>Material: substitute. Not a value solve: it lets global optimization change the
/// glass, from <see cref="Catalog"/> or every active catalog when empty ("Material Solves").
/// The cell keeps its glass; with <see cref="SolveOptions.Catalogs"/> the engine checks that at
/// least one glass meets <see cref="SolveOptions.SubstitutionTemplate"/> (OP-59) and fails when
/// none does. The search itself is <see cref="OpenSim.Optics.Optimization.GlassSubstitution"/>.</summary>
public sealed record SubstituteGlassSolve : SurfaceSolve
{
    public string Catalog { get; init; } = "";
    public override SolveColumn Column => SolveColumn.Material;
    public override string Name => "Substitute";
    public override string Code => "S";
}

/// <summary>Material: offset. Moves the glass's nd by <see cref="DeltaNd"/> and Vd by
/// <see cref="DeltaVd"/> ("Material Solves"), through this program's offset rule
/// (<see cref="OpenSim.Optics.Materials.GlassOffsets"/>, not OpticStudio's model-glass
/// difference; Conventions §10 item 7). Needs a catalog glass on the surface as entered.</summary>
public sealed record GlassOffsetSolve : SurfaceSolve
{
    public double DeltaNd { get; init; }
    public double DeltaVd { get; init; }
    public override SolveColumn Column => SolveColumn.Material;
    public override string Name => "Offset";
    public override string Code => "O";
}

// ---------------------------------------------------------------- Semi-diameter solves

/// <summary>Semi-diameter: automatic. The clear semi-diameter passes every ray of every field
/// ("Clear Semi-Diameter or Semi-Diameter Solves"); the cell is set to automatic (null) and
/// <see cref="SemiDiameterSizing"/> sizes it.</summary>
public sealed record AutomaticSemiDiameterSolve : SurfaceSolve
{
    public override SolveColumn Column => SolveColumn.SemiDiameter;
    public override string Name => "Automatic";
    public override string Code => "";
}

/// <summary>Semi-diameter: maximum. The largest automatic semi-diameter over all configurations
/// ("Clear Semi-Diameter or Semi-Diameter Solves"), written as a fixed value. Multiple
/// configurations are OP-30 and not built, so today it is the automatic value of the one
/// configuration.</summary>
public sealed record MaximumSemiDiameterSolve : SurfaceSolve
{
    public override SolveColumn Column => SolveColumn.SemiDiameter;
    public override string Name => "Maximum";
    public override string Code => "M";
}

// ---------------------------------------------------------------- Pickups

/// <summary>
/// A pickup on any column: the target takes O + S·V, V the value of
/// <see cref="FromColumn"/> (the target column when null) on <see cref="FromSurface"/>
/// (OpticStudio 2025 R2 help, the pickup sections of each solve page). The columns differ as
/// the help does:
/// <list type="bullet">
/// <item>curvature: scale and column, no offset; the scale applies to the curvature, not the
/// radius ("Curvature Solves"); a curvature picks up only a curvature, because the help does
/// not say how a length would become one;</item>
/// <item>thickness and parameter: scale, offset and column (T = O + S·V);</item>
/// <item>semi-diameter and conic: scale and column, no offset;</item>
/// <item>material and TCE: the value itself, no scale, offset or other column; a material
/// pickup copies the medium and the MIRROR flag.</item>
/// </list>
/// The source must come before the target in evaluation order: a prior surface, or the same
/// surface with an earlier column ("Restrictions"). Columns that hold a length are those of
/// "Integer Codes for Column Numbers" (radius, thickness, conic, semi-diameter, parameters).
/// </summary>
public sealed record PickupSolve : SurfaceSolve
{
    /// <summary>The column this pickup sets.</summary>
    public required SolveColumn Target { get; init; }

    /// <summary>The parameter number set when <see cref="Target"/> is a parameter.</summary>
    public int TargetParameter { get; init; }

    public required int FromSurface { get; init; }

    /// <summary>The source column, or null for the target column.</summary>
    public SolveColumn? FromColumn { get; init; }

    /// <summary>The source parameter number when the source column is a parameter (by default
    /// the target's).</summary>
    public int? FromParameter { get; init; }

    public double Scale { get; init; } = 1;
    public double Offset { get; init; }

    public override SolveColumn Column => Target;
    public override int ParameterIndex => TargetParameter;
    public override string Name => "Pickup";
    public override string Code => "P";

    /// <summary>The source cell.</summary>
    public SurfaceCell Source
    {
        get
        {
            var column = FromColumn ?? Target;
            int parameter = column == SolveColumn.Parameter ? FromParameter ?? TargetParameter : 0;
            return new SurfaceCell(FromSurface, column, parameter);
        }
    }
}

// ---------------------------------------------------------------- Parameter solves

/// <summary>Parameter: chief ray. Works only on coordinate-break surfaces' first four
/// parameters, setting a decenter or tilt so the real chief ray of a field and wavelength is
/// centred or leaves at zero angle ("Parameter Solves"). The solve is defined but not built:
/// the engine reports it as an error naming the cell.</summary>
public sealed record ParameterChiefRaySolve : SurfaceSolve
{
    public required int Parameter { get; init; }
    /// <summary>The field number (1-based as OpticStudio numbers them).</summary>
    public int Field { get; init; } = 1;
    /// <summary>The wavelength number (1-based); 0 for the primary.</summary>
    public int Wavelength { get; init; }
    public override SolveColumn Column => SolveColumn.Parameter;
    public override int ParameterIndex => Parameter;
    public override string Name => "Chief ray";
    public override string Code => "C";
}
