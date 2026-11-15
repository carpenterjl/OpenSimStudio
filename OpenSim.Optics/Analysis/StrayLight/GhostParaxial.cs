using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis.StrayLight;

/// <summary>A paraxial ray through a ghost system: height at each surface's vertex plane and
/// reduced slope ν = n·u after it (signed index, §11), meters.</summary>
public sealed record GhostParaxialRay(IReadOnlyList<double> Heights, IReadOnlyList<double> ReducedSlopes, IReadOnlyList<double> SignedIndices)
{
    /// <summary>The slope u = ν/n after surface <paramref name="g"/>.</summary>
    public double Slope(int g) => ReducedSlopes[g] / SignedIndices[g];
}

/// <summary>
/// The y–ν trace of a ghost system (OP-46). The ghost lens's own paraxial model would place its
/// entrance pupil from the ghost's stop flag, which is not where the original's is when the stop
/// lies after the first bounce; the ghost is therefore traced with the original's marginal and
/// chief rays as they enter surface 1 (height there and reduced slope in object space), with the
/// same equations as <see cref="ParaxialSystem.Trace"/>: transfer y′ = y + (t/n)·ν, surface
/// ν′ = ν − y·c·(n′ − n), signed indices that change sign at each mirror (§11). Curvatures are the
/// paraxial ones of <see cref="SequentialLens.ToParaxialLens"/> (an asphere's r² term included).
/// A surface with a thin-lens power or an ABCD matrix (OP-27's paraxial surfaces) is refused: how
/// such a surface acts on light going the other way is not modelled here.
/// </summary>
public sealed class GhostParaxial
{
    private readonly double[] _curvature, _thickness, _signed;

    public GhostSystem Ghost { get; }

    /// <summary>The ghost's paraxial model at an air wavelength (µm) in an environment.</summary>
    public GhostParaxial(GhostSystem ghost, double airMicrometers, OpticsEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(ghost);
        ArgumentNullException.ThrowIfNull(environment);
        Ghost = ghost;
        var paraxial = ghost.Lens.ToParaxialLens();
        int count = paraxial.Surfaces.Count, image = count - 1;
        _curvature = new double[count];
        _thickness = new double[count];
        _signed = new double[count];
        double sign = 1;
        for (int g = 0; g < count; g++)
        {
            var s = paraxial.Surfaces[g];
            if (s.ThinLensPower != 0 || s.SlopeMatrix is not null)
                throw new ArgumentException($"Ghost focus: surface {ghost.Origins[g].Original} has a thin-lens power or an ABCD matrix (a paraxial surface type); how it acts on light going the other way is not modelled, so its ghosts are not built.");
            _curvature[g] = g == 0 || g == image ? 0 : s.Curvature;
            _thickness[g] = s.Thickness;
            if (g == image) { _signed[g] = _signed[g - 1]; continue; }
            if (g > 0 && s.IsMirror)
            {
                sign = -sign;
                _signed[g] = sign * Math.Abs(_signed[g - 1]);
                continue;
            }
            double n = s.Medium.IndexAt(airMicrometers, environment);
            if (!double.IsFinite(n) || !(n > 0))
                throw new InvalidOperationException($"Ghost focus: the index after surface {ghost.Origins[g].Original} is {n} at {airMicrometers} µm; it must be positive.");
            _signed[g] = sign * n;
        }
    }

    /// <summary>The index of the last surface before the ghost's image.</summary>
    public int Last => _curvature.Length - 2;

    /// <summary>The thickness from the last surface to the ghost's image surface (signed, m).</summary>
    public double ImageThickness => _thickness[Last];

    /// <summary>Signed index after each ghost surface.</summary>
    public IReadOnlyList<double> SignedIndices => _signed;

    /// <summary>Traces a ray given by its height on surface 1 and its reduced slope in object space.</summary>
    public GhostParaxialRay Trace(double height, double reducedSlope)
    {
        int count = _curvature.Length;
        var h = new double[count];
        var v = new double[count];
        v[0] = reducedSlope;
        h[1] = height;
        for (int g = 1; g < count; g++)
        {
            if (g > 1) h[g] = h[g - 1] + _thickness[g - 1] / _signed[g - 1] * v[g - 1];
            v[g] = v[g - 1] - h[g] * _curvature[g] * (_signed[g] - _signed[g - 1]);
        }
        h[0] = double.NaN;
        return new GhostParaxialRay(h, v, _signed);
    }

    /// <summary>The power of the ghost system, 1/m: −ν after the last surface of the ray entering
    /// at height 1 parallel to the axis (the C element of its matrix, as <see cref="ParaxialSystem.Power"/>).</summary>
    public double Power() => -Trace(1, 0).ReducedSlopes[Last];

    /// <summary>
    /// Where <paramref name="ray"/> crosses the axis after the last surface, measured along z from
    /// the ghost's image surface (positive toward +z), meters; null when it leaves parallel to the
    /// axis. For the marginal ray this is the ghost's paraxial focus; for the chief ray its exit
    /// pupil (the ghost image of the pupil).
    /// </summary>
    public double? AxisCrossingFromImage(GhostParaxialRay ray)
    {
        double u = ray.Slope(Last);
        if (u == 0) return null;
        return -ray.Heights[Last] / u - _thickness[Last];
    }

    /// <summary>The height of <paramref name="ray"/> at a distance <paramref name="fromImage"/>
    /// from the ghost's image surface along z.</summary>
    public double HeightAt(GhostParaxialRay ray, double fromImage) =>
        ray.Heights[Last] + ray.Slope(Last) * (fromImage + _thickness[Last]);
}
