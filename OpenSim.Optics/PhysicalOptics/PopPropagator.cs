using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>One operator the propagation applied, for the propagation report.</summary>
/// <param name="Where">The gap or surface ("surface 3 to 4", "surface 3").</param>
/// <param name="Operator">"PTP", "WTS", "STW" or "Surface".</param>
/// <param name="Distance">The distance of the step along the travel, meters (0 for a surface).</param>
/// <param name="Dx">The x sample spacing after the step, meters.</param>
/// <param name="Dy">The y sample spacing after the step, meters.</param>
/// <param name="Reference">The reference after the step.</param>
public sealed record PopStep(string Where, string Operator, double Distance, double Dx, double Dy, PopReference Reference);

/// <summary>
/// The three free-space operators of physical optics propagation and the four cases they make
/// (OP-49; docs/Optics-Conventions.md §66). The operators are those the help gives from
/// Lawrence's "Optical Modeling" (OpticStudio 2025 R2 help, "Angular Spectrum Propagation",
/// "Fresnel Diffraction", "Propagating In and Out of the Rayleigh Range"), and the scheme is the
/// one POPPY implements (POPPY 1.2.0, BSD, poppy/fresnel.py, FresnelWavefront._propagate_ptp,
/// _propagate_wts, _propagate_stw and propagate_fresnel):
/// <list type="bullet">
/// <item>PTP(Δz) = FF⁻¹[T(Δz)·FF[E]], T = exp(−iπλΔzρ²), ρ² = ξ² + η²: the angular spectrum in
/// its paraxial form, spacing unchanged, the field referenced to a plane.</item>
/// <item>WTS(Δz): from the pilot's waist to a sphere of radius Δz, the Fresnel integral
/// E₂ = 1/(iλΔz)·q(r₂,Δz)·∬E₁·q(r₁,Δz)·exp(−i2π(x₁x₂ + y₁y₂)/(λΔz)), q(r, Δz) = exp(iπr²/(λΔz)),
/// with the stored field E₂/q(r₂, Δz). The spacing becomes Δx₂ = λ|Δz|/(nx·Δx₁).</item>
/// <item>STW: the reverse, from a sphere back to the waist; the incoming field's reference
/// q(r₁, −Δz) cancels the integral's q(r₁, Δz).</item>
/// </list>
/// The cases are II = PTP(z₂ − z₁), IO = WTS(z₂ − z₀)·PTP(z₀ − z₁), OI = PTP(z₂ − z₀)·STW(z₀ − z₁),
/// OO = WTS(z₂ − z₀)·STW(z₀ − z₁), z₀ the pilot's waist, "inside" meaning within the pilot's
/// Rayleigh range at the end point (POPPY's planar_range). The discrete sums keep the integrals'
/// factors (Δx·Δy/(iλΔz)), so each operator conserves the power exactly up to rounding
/// (Parseval), and λ is the wavelength in the medium.
/// </summary>
public static class PopPropagator
{
    /// <summary>Propagates <paramref name="field"/> by <paramref name="distance"/> along the
    /// travel (negative: a virtual step back), choosing the case from the pilot beam. Each
    /// operator applied is added to <paramref name="log"/> under <paramref name="where"/>.</summary>
    public static PopField Propagate(PopField field, double distance, string where, ICollection<PopStep>? log = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!double.IsFinite(distance))
            throw new ArgumentException($"Physical optics, {where}: the distance to propagate is {distance}; it must be finite.");
        if (distance == 0) return field;
        var end = field.Pilot.Propagated(distance);
        bool inside = end.IsInsideRayleighRange;
        double z1 = field.Pilot.DistanceFromWaist;
        if (field.Reference == PopReference.Plane)
        {
            if (inside) return PlaneToPlane(field, distance, where, log);
            var atWaist = PlaneToPlane(field, -z1, where, log);
            return WaistToSphere(atWaist, end.DistanceFromWaist, where, log);
        }
        var waist = SphereToWaist(field, where, log);
        return inside
            ? PlaneToPlane(waist, end.DistanceFromWaist, where, log)
            : WaistToSphere(waist, end.DistanceFromWaist, where, log);
    }

    /// <summary>PTP(Δz): the paraxial angular spectrum; the field stays referenced to a plane.</summary>
    public static PopField PlaneToPlane(PopField field, double dz, string where, ICollection<PopStep>? log = null)
    {
        if (field.Reference != PopReference.Plane)
            throw new InvalidOperationException($"Physical optics, {where}: PTP needs a field referenced to a plane.");
        if (dz == 0) return field;
        int nx = field.Nx, ny = field.Ny;
        double lambda = field.Pilot.MediumWavelength;
        var spectrum = Fft2D.Forward(field.Samples);
        double fx0 = 1 / (nx * field.Dx), fy0 = 1 / (ny * field.Dy);
        var phaseX = new Complex[nx];
        var phaseY = new Complex[ny];
        for (int c = 0; c < nx; c++)
        {
            double f = Signed(c, nx) * fx0;
            phaseX[c] = Complex.FromPolarCoordinates(1, -Math.PI * lambda * dz * f * f);
        }
        for (int r = 0; r < ny; r++)
        {
            double f = Signed(r, ny) * fy0;
            phaseY[r] = Complex.FromPolarCoordinates(1, -Math.PI * lambda * dz * f * f);
        }
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++) spectrum[r, c] *= phaseY[r] * phaseX[c];
        var result = new PopField(Fft2D.Inverse(spectrum), field.Dx, field.Dy, field.Pilot.Propagated(dz), PopReference.Plane);
        log?.Add(new PopStep(where, "PTP", dz, result.Dx, result.Dy, PopReference.Plane));
        return result;
    }

    /// <summary>WTS(Δz): from the pilot's waist (the field must be there, referenced to a
    /// plane) to a sphere of radius Δz.</summary>
    public static PopField WaistToSphere(PopField field, double dz, string where, ICollection<PopStep>? log = null)
    {
        if (field.Reference != PopReference.Plane)
            throw new InvalidOperationException($"Physical optics, {where}: WTS needs a field referenced to a plane at the pilot's waist.");
        if (dz == 0)
            throw new InvalidOperationException($"Physical optics, {where}: WTS over zero distance has no sphere to reference.");
        CheckAtWaist(field, where, "WTS");
        int nx = field.Nx, ny = field.Ny;
        double lambda = field.Pilot.MediumWavelength;
        var input = new Complex[ny, nx];
        for (int r = 0; r < ny; r++)
        {
            double y = field.Y(r);
            for (int c = 0; c < nx; c++)
            {
                double x = field.X(c);
                input[r, c] = field.Samples[r, c] * Complex.FromPolarCoordinates(1, Math.PI * (x * x + y * y) / (lambda * dz));
            }
        }
        var output = CentredTransform(input, forward: dz > 0);
        var factor = new Complex(0, -field.Dx * field.Dy / (lambda * dz));   // Δx·Δy/(iλΔz)
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++) output[r, c] *= factor;
        double dx2 = lambda * Math.Abs(dz) / (nx * field.Dx), dy2 = lambda * Math.Abs(dz) / (ny * field.Dy);
        var result = new PopField(output, dx2, dy2, field.Pilot.Propagated(dz), PopReference.Sphere);
        log?.Add(new PopStep(where, "WTS", dz, dx2, dy2, PopReference.Sphere));
        return result;
    }

    /// <summary>STW: from a field referenced to a sphere centred on the pilot's waist back to
    /// that waist, where it is referenced to a plane.</summary>
    public static PopField SphereToWaist(PopField field, string where, ICollection<PopStep>? log = null)
    {
        if (field.Reference != PopReference.Sphere)
            throw new InvalidOperationException($"Physical optics, {where}: STW needs a field referenced to a sphere.");
        int nx = field.Nx, ny = field.Ny;
        double lambda = field.Pilot.MediumWavelength;
        double dz = -field.Pilot.DistanceFromWaist;   // z0 − z1
        if (dz == 0)
            throw new InvalidOperationException($"Physical optics, {where}: a field referenced to a sphere of zero radius cannot be propagated.");
        var output = CentredTransform(field.Samples, forward: dz > 0);
        double dx0 = lambda * Math.Abs(dz) / (nx * field.Dx), dy0 = lambda * Math.Abs(dz) / (ny * field.Dy);
        var factor = new Complex(0, -field.Dx * field.Dy / (lambda * dz));
        int cx = nx / 2, cy = ny / 2;
        for (int r = 0; r < ny; r++)
        {
            double y = (r - cy) * dy0;
            for (int c = 0; c < nx; c++)
            {
                double x = (c - cx) * dx0;
                output[r, c] *= factor * Complex.FromPolarCoordinates(1, Math.PI * (x * x + y * y) / (lambda * dz));
            }
        }
        var result = new PopField(output, dx0, dy0, field.Pilot.Propagated(dz), PopReference.Plane);
        log?.Add(new PopStep(where, "STW", dz, dx0, dy0, PopReference.Plane));
        return result;
    }

    /// <summary>Σ a[k₁]·exp(∓2πi·k₁k₂/N) over centred indices k₁, k₂ ∈ [−N/2, N/2) in both
    /// directions: minus for <paramref name="forward"/>. Unnormalised both ways.</summary>
    private static Complex[,] CentredTransform(Complex[,] data, bool forward)
    {
        int ny = data.GetLength(0), nx = data.GetLength(1);
        var origin = Fft2D.InverseShift(data);
        Complex[,] transformed;
        if (forward) transformed = Fft2D.Forward(origin);
        else
        {
            transformed = Fft2D.Inverse(origin);
            double n = (double)nx * ny;
            for (int r = 0; r < ny; r++)
                for (int c = 0; c < nx; c++) transformed[r, c] *= n;
        }
        return Fft2D.Shift(transformed);
    }

    /// <summary>The signed frequency index of DFT bin <paramref name="k"/> of <paramref name="n"/>.</summary>
    internal static int Signed(int k, int n) => k < n / 2 ? k : k - n;

    private static void CheckAtWaist(PopField field, string where, string op)
    {
        double z = field.Pilot.DistanceFromWaist;
        if (Math.Abs(z) > 1e-9 * field.Pilot.RayleighRange)
            throw new InvalidOperationException(
                $"Physical optics, {where}: {op} must start at the pilot beam's waist, but the beam is {z} m from it.");
    }
}
