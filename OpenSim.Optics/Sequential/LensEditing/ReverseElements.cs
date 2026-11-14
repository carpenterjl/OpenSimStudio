using OpenSim.Core.Model;

namespace OpenSim.Optics.Sequential.LensEditing;

/// <summary>
/// Reverse Elements: turns the group of surfaces <c>first</c>…<c>last</c> round, as OpticStudio's
/// tool of that name does ("Reverses a lens element or group", settings First Surface and Last
/// Surface; OpticStudio 2025 R2 help, Setup tab › Lens Data Editor Toolbar › Reverse Elements,
/// plans/sources/optics/ansyshelp-op31/Reverse_Elements.html).
/// <para>
/// The rule (derived here, not copied): surface <c>first + i</c> of the result is the old
/// surface <c>last − i</c> with its radius negated (section 3: the centre of curvature changes
/// side); the thickness and material after it are those after old surface <c>last − 1 − i</c>,
/// and after the new last surface those after the old <c>last</c>, so the gap after the group
/// and the gap before it stay where they were. Everything else (comment, conic, semi-diameters,
/// aperture, coating, stop, flags of the other columns) moves with its surface.
/// </para>
/// <para>
/// The help says solves on data affected by the reversal are removed: solves and pickups on the
/// radius, thickness and material cells inside the group are removed (a variable moves with its
/// value), pickups elsewhere that read a moved surface follow it, and those that read a
/// reversed radius, thickness or material are removed. The help warns of mirrors, coordinate
/// breaks, tilts and non-standard surfaces in the group; this tool refuses mirrors and any
/// surface type but Standard there instead of guessing.
/// </para>
/// </summary>
public static class ReverseElements
{
    private static readonly string[] Reversed = { SavedSurfaces.RadiusColumn, SavedSurfaces.ThicknessColumn, SavedSurfaces.MaterialColumn };

    public static LensEditResult Apply(IReadOnlyList<OpticsSurfaceSettings> surfaces, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        SurfaceFlags.CheckRange("Reverse Elements", surfaces, first, last, 1, surfaces.Count - 2);
        if (first == last) throw new ArgumentException("Reverse Elements: the group needs at least two surfaces.");
        for (int j = first; j <= last; j++)
        {
            if (!SavedSurfaces.IsStandard(surfaces[j]))
                throw new ArgumentException($"Reverse Elements: surface {j} is a '{surfaces[j].Type}' surface; only Standard surfaces can be reversed in this build.");
            if (SavedSurfaces.IsMirror(surfaces[j]))
                throw new ArgumentException($"Reverse Elements: surface {j} is a mirror; a group with a mirror cannot be reversed.");
        }

        int n = last - first + 1;
        var result = surfaces.ToList();
        int dropped = 0;
        for (int i = 0; i < n; i++)
        {
            var moved = surfaces[last - i];
            var gap = i < n - 1 ? surfaces[last - 1 - i] : surfaces[last];
            var s = SurfaceFlags.Without(moved, f => IsReversedCell(f.Column));
            s = s with
            {
                Radius = moved.Radius is double r ? -r : null,
                Thickness = gap.Thickness,
                ThicknessIsInfinite = gap.ThicknessIsInfinite,
                Material = gap.Material,
                Index = gap.Index,
                ModelGlass = gap.ModelGlass,
            };
            // A variable radius stays variable (its value moved with it); thickness and material
            // variables come from the gap they belong to.
            if (SurfaceFlags.Of(moved, SavedSurfaces.RadiusColumn) is { } rf && SurfaceFlags.IsKind(rf, SavedSurfaces.VariableFlag))
                s = SurfaceFlags.With(s, SavedSurfaces.RadiusColumn, rf);
            if (SurfaceFlags.Of(gap, SavedSurfaces.ThicknessColumn) is { } tf && SurfaceFlags.IsKind(tf, SavedSurfaces.VariableFlag))
                s = SurfaceFlags.With(s, SavedSurfaces.ThicknessColumn, tf);
            dropped += (moved.Flags?.Count(f => IsReversedCell(f.Column) && !SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag)) ?? 0);
            result[first + i] = s;
        }

        // Pickups anywhere that read a surface of the group follow it, unless they read a
        // reversed cell.
        for (int j = 0; j < result.Count; j++)
        {
            var s = result[j];
            if (s.Flags is null) continue;
            var flags = new List<OpticsCellFlagSettings>();
            foreach (var f in s.Flags)
            {
                if (!SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) || f.PickupSurface < first || f.PickupSurface > last)
                {
                    flags.Add(f);
                    continue;
                }
                if (IsReversedCell(f.PickupColumn)) { dropped++; continue; }
                flags.Add(f with { PickupSurface = first + last - f.PickupSurface });
            }
            result[j] = s with { Flags = flags.Count > 0 ? flags : null };
        }

        var notes = new List<string> { $"Surfaces {first} to {last} reversed." };
        if (dropped > 0) notes.Add($"{dropped} solve{(dropped == 1 ? "" : "s")} or pickup{(dropped == 1 ? "" : "s")} on reversed radii, thicknesses or materials removed; those cells are now fixed.");
        return new LensEditResult(result, notes);
    }

    private static bool IsReversedCell(string? column) => column is not null && Reversed.Contains(column);
}
