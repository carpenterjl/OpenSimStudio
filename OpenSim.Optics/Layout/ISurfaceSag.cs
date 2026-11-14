namespace OpenSim.Optics.Layout;

/// <summary>
/// The shape of an optical surface for drawing: the sag z(x, y), in meters, along the surface's
/// local z from the plane through its vertex (docs/Optics-Conventions.md §3). The layout code
/// only samples this function, so any surface form a later item adds (aspheres in OP-23,
/// freeforms in OP-25) draws without change by implementing it.
/// </summary>
public interface ISurfaceSag
{
    /// <summary>The sag at local (x, y), meters. NaN where the surface is not defined (outside
    /// the domain of a conic's square root, for example).</summary>
    double Sag(double x, double y);

    /// <summary>The largest radial distance from the vertex at which <see cref="Sag"/> is
    /// defined in every direction; +infinity when it is defined everywhere.</summary>
    double MaxDefinedRadius { get; }
}
