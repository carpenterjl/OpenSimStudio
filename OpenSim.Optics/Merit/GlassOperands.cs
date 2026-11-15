using System.Globalization;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Merit;

/// <summary>
/// OP-59's glass operands, registered in <see cref="MeritOperandRegistry.CreateDefault"/> under
/// "// OP-59". Read from OpticStudio 2025 R2's help, saved under
/// <c>plans/sources/optics/op-59-help/</c>: Constraints on Glass Data (both definitions),
/// Optimizing Using Model Glasses (RGLA's distance, weights and boundary behaviour) and
/// Description of Catalog Data (relative cost; −1 means no data). Which of Int1, Int2 and Data1–3
/// hold Surf1, Surf2, Wn, Wa and Wp the help does not say; integers first, then reals in the order
/// the help names them, is this program's reading (docs/Optics-Conventions.md §10 item 37).
/// </summary>
public static class GlassOperands
{
    private const string Category = "Constraints on glass data";

    /// <summary>
    /// RGLA, reasonable glass. For each surface Surf1 to Surf2 whose medium is a glass (a model
    /// glass or a catalog glass; air, constant indices and mirrors are skipped), d is the RGLA
    /// distance (<see cref="GlassDistance"/>) to the nearest glass of the active catalogs that is
    /// not flagged Exclude Substitution. The help defines the value for one glass: the target
    /// while the nearest glass is within it, else the distance. For a range this program sums the
    /// excesses, as MNCT does (help: Understanding Boundary Operands): value = T + Σ max(0, d − T),
    /// which is the help's value for one glass. Fails naming why when the range has no glass, a
    /// glass has no nd and Vd, or (with W_p ≠ 0) no ΔP_g,F.
    /// </summary>
    public static MeritOperandDefinition ReasonableGlass() => new()
    {
        Code = "RGLA", Name = "Reasonable glass", Category = Category, Source = "Constraints on Glass Data; Optimizing Using Model Glasses",
        Description = "Distance on the glass map, √(Wn·ΔNd² + Wa·ΔVd² + Wp·ΔPgF²), from the glass of each surface Surf1 to Surf2 to the nearest glass of the active catalogs; "
            + "the target while every glass is within it, else the target plus the excess. Wn, Wa, Wp of zero are 1, 1E-04 and 1E+02.",
        Int1 = "Surf1", Int2 = "Surf2", Data = new[] { "Wn", "Wa", "Wp" }, IsBound = true,
        Compute = c =>
        {
            var catalogs = c.Catalogs
                ?? throw new MeritOperandException("RGLA compares with the active glass catalogs, and this evaluation was not given them.");
            int a = c.Surface(c.Row.Int1, "Surf1"), b = c.Surface(c.Row.Int2, "Surf2");
            if (a > b) throw new MeritOperandException($"Surf1 ({a}) is after Surf2 ({b}).");
            var (wn, wa, wp) = GlassDistance.Weights(c.Row.DataAt(1), c.Row.DataAt(2), c.Row.DataAt(3));
            if (!(wn >= 0) || !(wa >= 0) || !(wp >= 0))
                throw new MeritOperandException($"the weights Wn {F(wn)}, Wa {F(wa)}, Wp {F(wp)} must not be negative.");
            var pool = new List<GlassPoint>();
            foreach (var catalog in catalogs)
                foreach (var g in catalog.Materials)
                    if (!g.ExcludeSubstitution && GlassDistance.TryPoint(g, out var p, out _) && (wp == 0 || p!.DeltaPgF is not null))
                        pool.Add(p!);
            if (pool.Count == 0)
                throw new MeritOperandException($"no glass of the active catalogs ({string.Join(", ", catalogs.Select(k => k.Name))}) has the nd, Vd{(wp == 0 ? "" : " and ΔPgF")} to compare with.");
            double target = c.Row.Target, value = target;
            int glasses = 0;
            for (int j = a; j <= b; j++)
            {
                var surface = c.Lens.Surfaces[j];
                if (surface.IsMirror || surface.Medium.Material is not { } material) continue;
                glasses++;
                if (!GlassDistance.TryPoint(material, out var own, out string why))
                    throw new MeritOperandException($"the glass after surface {j} has no nd and Vd: {why}");
                if (wp != 0 && own!.DeltaPgF is null)
                    throw new MeritOperandException($"the glass after surface {j} ({material.Who}) has no ΔPgF (its range does not reach the g line); set Wp to compare nd and Vd only.");
                double nearest = double.PositiveInfinity;
                foreach (var p in pool) nearest = Math.Min(nearest, GlassDistance.Distance(own!, p, wn, wa, wp)!.Value);
                if (nearest > target) value += nearest - target;
            }
            if (glasses == 0) throw new MeritOperandException($"surfaces {a} to {b} have no glass.");
            return value;
        },
    };

    /// <summary>GCOS, glass cost: the relative cost the catalog lists for the glass after surface
    /// Surf. Fails when that medium is not a catalog glass or the catalog records no cost (absent
    /// or −1).</summary>
    public static MeritOperandDefinition GlassCost() => new()
    {
        Code = "GCOS", Name = "Glass cost", Category = Category, Source = "Constraints on Glass Data",
        Description = "The relative cost factor the glass catalog lists for the glass on surface Surf.",
        Int1 = "Surf",
        Compute = c =>
        {
            int j = c.Surface(c.Row.Int1);
            var surface = c.Lens.Surfaces[j];
            if (surface.IsMirror || surface.Medium.Material is not { } material || material.Catalog.Length == 0)
                throw new MeritOperandException($"the medium after surface {j} is not a catalog glass, so it has no catalog cost.");
            return material.RelativeCost is double cost && cost >= 0 ? cost
                : throw new MeritOperandException($"{material.Who}: the catalog records no relative cost.");
        },
    };

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
