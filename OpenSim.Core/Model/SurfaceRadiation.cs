namespace OpenSim.Core.Model;

/// <summary>
/// Radiation exchanged among a set of faces that see each other — fins of a heatsink, two
/// boards, the walls of an enclosure and what is inside it. Each face is a grey diffuse
/// surface; the view factors between its triangles are computed from the geometry, other
/// surfaces of the mesh may block a view, and whatever part of a triangle's view the set
/// does not take sees a black surrounding at <see cref="AmbientTemperature"/>.
/// <para>
/// It is an exchange, not a boundary condition: a face in the set may also carry a
/// convection condition, and an environment still convects from it (the environment's own
/// radiation, view factor 1 to its ambient, is replaced by this exchange on these faces).
/// </para>
/// </summary>
public sealed record SurfaceRadiation
{
    public required string Name { get; init; }

    /// <summary>The faces that radiate to each other.</summary>
    public required IReadOnlyList<int> FaceIds { get; init; }

    /// <summary>Temperature of what lies beyond the set [K] (the room through an
    /// opening): the remainder of every triangle's view goes to a black surrounding at
    /// this temperature. In a closed enclosure that remainder is zero.</summary>
    public required double AmbientTemperature { get; init; }

    /// <summary>One emissivity for every face of the set; null takes each face's material
    /// emissivity.</summary>
    public double? Emissivity { get; init; }

    /// <summary>Whether the mesh's other surfaces may block the view between two
    /// triangles (true), or every pair facing each other sees each other whole (false —
    /// right for convex arrangements, and cheaper).</summary>
    public bool Obstruction { get; init; } = true;
}
