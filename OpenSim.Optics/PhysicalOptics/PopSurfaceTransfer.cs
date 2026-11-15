using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>How the phase a surface adds is found (docs/Optics-Conventions.md §66).</summary>
public enum PopTransfer
{
    /// <summary>From the real ray trace of a ray launched along the local z axis at every sample
    /// (the help's "probing rays", "Propagation Through Arbitrary Optical Surfaces").</summary>
    RayBased,

    /// <summary>The paraxial thin-element phase −φ·r²/2 of the surface's first-order power.</summary>
    ParaxialThinLens,
}

/// <summary>
/// Carries a <see cref="PopField"/> across one surface of an axial lens (OP-49;
/// docs/Optics-Conventions.md §66): the aperture clips it, the surface adds its optical path,
/// the pilot beam goes through the surface's paraxial power, and the field is re-referenced to
/// the new pilot (a plane inside its Rayleigh range, a sphere outside).
/// <para>
/// The surface is crossed on its vertex plane: the stored field before it is the field on the
/// plane tangent to the surface at its vertex in the medium before, and the field after it is
/// on the same plane in the medium after (the help's "the beam data is shown after the beam
/// refracts into, or reflects from, the end surface").
/// </para>
/// </summary>
public static class PopSurfaceTransfer
{
    /// <summary>The first-order power φ of surface <paramref name="j"/> for a beam travelling
    /// along local +z (<paramref name="sense"/> = 1) or −z (−1): the quadratic coefficient of the
    /// optical path it adds, OPL = −φ·r²/2, so φ = sense·(n′ − n)·c for a refraction,
    /// −2·sense·n·c for a mirror, plus a thin-lens power (the Paraxial surface). Derived in docs
    /// §66 from the sag c·r²/2.</summary>
    public static double Power(SequentialSurface s, int j, double sense, double indexBefore, double indexAfter)
    {
        if (s.IsIgnored || s.IsCoordinateBreak) return 0;
        var shape = Sequential.Composite.CompositeSurfaces.EffectiveShape(s);
        var eq = shape?.ParaxialEquivalent(s) ?? new ParaxialShapeEquivalent { Curvature = s.Curvature };
        if (eq.SlopeMatrix is not null)
            throw new NotSupportedException($"Physical optics: surface {j} ({shape!.TypeName}) is an ABCD block, which has no phase for physical optics to apply (help, \"Propagation Through Arbitrary Optical Surfaces\": the ABCD matrix surface does \"not allow POP analysis at all\").");
        if (eq.DiffractivePowerPerWavelength != 0)
            throw new NotSupportedException($"Physical optics: surface {j} ({shape!.TypeName}) has diffractive power; physical optics through diffractive surfaces is not built.");
        double c = eq.Curvature;
        double phi = s.IsMirror ? -2 * sense * indexBefore * c : sense * (indexAfter - indexBefore) * c;
        return phi + eq.ThinLensPower;
    }

    /// <summary>
    /// The field just after surface <paramref name="j"/> of <paramref name="trace"/>'s lens.
    /// <paramref name="sense"/> is +1 when the beam travels along the surface's local +z, −1
    /// after an odd number of mirrors. Samples whose probing ray is vignetted, misses the surface
    /// or is totally reflected are set to zero, and <paramref name="warnings"/> says how many
    /// missed or were totally reflected. The image surface clips by its aperture and adds no
    /// phase.
    /// </summary>
    public static PopField Apply(PopField field, SequentialTrace trace, int j, double sense, PopTransfer mode,
        ICollection<string> warnings, int? maxDegreeOfParallelism = 1)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(trace);
        var lens = trace.Lens;
        var s = lens.Surfaces[j];
        bool image = j == lens.ImageIndex;
        if (s.IsIgnored || s.IsCoordinateBreak) return field;
        double nIn = trace.Indices[j - 1];
        double nOut = image ? nIn : trace.Indices[j];
        double phi = image ? 0 : Power(s, j, sense, nIn, nOut);
        var pilot = field.Pilot.ThroughPower(phi, nOut);
        var reference = pilot.IsInsideRayleighRange ? PopReference.Plane : PopReference.Sphere;
        double inverseOld = field.Reference == PopReference.Sphere ? 1 / field.ReferenceRadius : 0;
        double inverseNew = reference == PopReference.Sphere ? 1 / pilot.DistanceFromWaist : 0;
        double k0 = 2 * Math.PI / field.Pilot.AirWavelength;
        double semi = trace.SemiDiameters[j];
        var aperture = s.Aperture;

        int nx = field.Nx, ny = field.Ny;
        var shape = Sequential.Composite.CompositeSurfaces.EffectiveShape(s);
        bool rays = mode == PopTransfer.RayBased && !image;
        double vertexPath = 0;
        if (rays)
        {
            var vertex = Probe(trace, s, shape, j, 0, 0, sense, nIn, nOut);
            if (vertex.Status != RayStatus.Ok)
                throw new InvalidOperationException($"Physical optics: the probing ray along the axis fails at surface {j} ({vertex.Status}); the beam cannot be carried across it.");
            vertexPath = vertex.Path;
        }

        var rowsOut = ParallelSlots.Compute(ny, maxDegreeOfParallelism, r =>
        {
            var row = new Complex[nx];
            int missed = 0;
            double y = field.Y(r);
            for (int c = 0; c < nx; c++)
            {
                var u = field.Samples[r, c];
                if (u == Complex.Zero) continue;
                double x = field.X(c);
                double path;
                if (rays)
                {
                    var probe = Probe(trace, s, shape, j, x, y, sense, nIn, nOut);
                    if (probe.Status != RayStatus.Ok)
                    {
                        if (probe.Status != RayStatus.Vignetted) missed++;
                        continue;
                    }
                    path = probe.Path - vertexPath;
                }
                else
                {
                    if (aperture is not null && !aperture.Passes(x, y, semi)) continue;
                    path = -phi * (x * x + y * y) / 2;
                }
                double r2 = x * x + y * y;
                double phase = k0 * (path + nIn * r2 * inverseOld / 2 - nOut * r2 * inverseNew / 2);
                row[c] = u * Complex.FromPolarCoordinates(1, phase);
            }
            return (row, missed);
        });
        var samples = new Complex[ny, nx];
        int lost = 0;
        for (int r = 0; r < ny; r++)
        {
            lost += rowsOut[r].missed;
            for (int c = 0; c < nx; c++) samples[r, c] = rowsOut[r].row[c];
        }
        if (lost > 0)
            warnings.Add($"Physical optics, surface {j}: the probing rays of {lost} samples carrying power missed the surface or were totally internally reflected; those samples are set to zero.");
        return new PopField(samples, field.Dx, field.Dy, pilot, reference);
    }

    /// <summary>The result of one probing ray: its status and the optical path it gives.</summary>
    internal readonly record struct ProbeResult(RayStatus Status, double Path);

    /// <summary>
    /// One probing ray: launched at (x, y, 0) along the travel (0, 0, sense), carried to the
    /// surface (closed-form conic or the shape's own intersection), clipped by the aperture,
    /// refracted, reflected or redirected as the real trace does (§13), and carried back along
    /// its new direction to the vertex plane z = 0. The optical path, in the indices relative to
    /// the system's air, is n·(to the surface) + the shape's extra path + n′·(back to the plane),
    /// moved from where the ray lands on the plane back to where it was launched along the
    /// outgoing wavefront's slope n′·(L, M) (to first order in the landing shift; docs §66).
    /// </summary>
    internal static ProbeResult Probe(SequentialTrace trace, SequentialSurface s, SurfaceShape? shape, int j,
        double x, double y, double sense, double nIn, double nOut)
    {
        var d = new Vector3D(0, 0, sense);
        Vector3D point;
        double delta;
        bool met = shape is null
            ? ConicIntersection.Intersect(x, y, d, s.Curvature, s.Conic, out point, out delta)
            : shape.Intersect(x, y, d, s, out point, out delta);
        if (!met || !double.IsFinite(delta)) return new ProbeResult(RayStatus.MissedSurface, 0);
        if (s.Aperture is { } aperture && !aperture.Passes(point.X, point.Y, trace.SemiDiameters[j]))
            return new ProbeResult(RayStatus.Vignetted, 0);
        if (j == trace.Lens.ImageIndex) return new ProbeResult(RayStatus.Ok, 0);
        var normal = shape is null ? ConicIntersection.Normal(point, s.Curvature, s.Conic) : shape.Normal(point, s);
        double nAfter = s.IsMirror ? nIn : nOut;
        double extra = 0;
        Vector3D after;
        if (shape is not null && shape.TryRedirect(new SurfaceRedirectInput
            {
                Surface = s, SurfaceIndex = j, Point = point, Incoming = d, Normal = normal,
                IndexBefore = nIn, IndexAfter = nAfter, VacuumWavelength = trace.VacuumWavelength,
                AirWavelength = OpticsConventions.MicrometersToMeters(trace.AirMicrometers),
            }, out var redirected, out extra, out var failure))
        {
            if (failure != RayStatus.Ok) return new ProbeResult(failure, 0);
            after = redirected / redirected.Length;
        }
        else if (s.IsMirror)
        {
            after = OpticsConventions.Reflect(d, normal);
            after /= after.Length;
        }
        else if (!ConicIntersection.Refract(d, normal, nIn, nOut, out after))
            return new ProbeResult(RayStatus.TotalInternalReflection, 0);
        if (after.Z == 0) return new ProbeResult(RayStatus.MissedSurface, 0);
        double back = -point.Z / after.Z;
        double path = nIn * delta + extra + nAfter * back;
        double landX = point.X + back * after.X, landY = point.Y + back * after.Y;
        path += nAfter * ((x - landX) * after.X + (y - landY) * after.Y);
        return new ProbeResult(RayStatus.Ok, path);
    }
}
