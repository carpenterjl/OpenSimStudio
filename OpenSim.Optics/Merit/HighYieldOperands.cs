using System.Globalization;
using OpenSim.Optics.Optimization.HighYield;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

// OP-62: the High-Yield operand HYLD (docs/Optics-Conventions.md §50).

/// <summary>
/// HYLD, "High-Yield contribution of a real ray at a surface defined by Surf at the wavelength
/// defined by Wave. This is calculated from the incident or exit ray cosine, the surface normal
/// and the indices of refraction before and after the surface" (OpticStudio 2025 R2 help,
/// Constraints on Real Ray Data, saved under plans/sources/optics/op-62-help/). The value is
/// <see cref="HighYieldPenalty"/>'s τ = |n − n′|(1 − R·N) (Zemax Knowledgebase KA-01837).
/// <para>
/// Columns: Surf in Int1, Wave in Int2, Hx, Hy, Px and Py in Data1 to Data4. The help gives the
/// names (and "See Hx, Hy, Px, and Py"); the order Surf, Wave, Hx, Hy, Px, Py is that of the
/// Merit Function Editor in KA-01837's screenshot (KA-01837_9_MFE.png), and the same layout as
/// the other real-ray operands of the registry (REAX...).
/// </para>
/// </summary>
public static class HighYieldOperands
{
    public const string Code = "HYLD";

    /// <summary>The HYLD definition.</summary>
    public static MeritOperandDefinition HighYield() => new()
    {
        Code = Code,
        Name = "High-yield contribution",
        Category = "Constraints on real ray data",
        Source = "Constraints on Real Ray Data; Zemax Knowledgebase KA-01837",
        Description = "High-Yield penalty τ = |n − n′|(1 − R·N) of the real ray at surface Surf (R the incident ray when n′ > n, else the refracted ray); "
            + "the ray is at wavelength Wave, field (Hx, Hy) and pupil (Px, Py). Dimensionless; zero for a ray along the normal.",
        Int1 = "Surf", Int2 = "Wave", Data = new[] { "Hx", "Hy", "Px", "Py" },
        Ray = RayOf,
        Compute = Compute,
    };

    private static MeritRayRequest RayOf(MeritOperandRow row) => new(row.Int2, row.DataAt(1), row.DataAt(2), row.DataAt(3), row.DataAt(4));

    private static double Compute(MeritRowContext c)
    {
        int surface = c.Surface(c.Row.Int1);
        c.Wavelength(c.Row.Int2);
        if (surface == 0)
            throw new MeritOperandException("Surf 0 is the object surface; the High-Yield term is defined at a refracting surface (1 or later).");
        string ray = $"the ray (Hx {F(c.Row.DataAt(1))}, Hy {F(c.Row.DataAt(2))}, Px {F(c.Row.DataAt(3))}, Py {F(c.Row.DataAt(4))}, wavelength {c.Row.Int2})";
        var path = c.Ray(RayOf(c.Row));
        if (!path.Final.IsOk && path.FailedAtSurface <= surface)
            throw new MeritOperandException($"{ray} failed: {RayStatuses.Label(path.Status)} at surface {path.FailedAtSurface}.");
        var hit = path.Hits.FirstOrDefault(h => h.Surface == surface)
            ?? throw new MeritOperandException($"{ray} has no point on surface {surface}.");
        try
        {
            return HighYieldPenalty.Tau(hit, c.Lens.Surfaces[surface].IsMirror);
        }
        catch (ArgumentException e)
        {
            throw new MeritOperandException($"{ray}: {e.Message}");
        }
    }

    private static string F(double x) => x.ToString("G6", CultureInfo.InvariantCulture);
}
