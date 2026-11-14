using OpenSim.Core.Model;

namespace OpenSim.Optics.Sequential.LensEditing;

/// <summary>
/// Make Double Pass: makes surface <c>k</c> a mirror and sends the light back through the
/// surfaces before it (OpticStudio 2025 R2 help, Lens Data Editor Toolbar › Make Double Pass,
/// plans/sources/optics/ansyshelp-op31/Make_Double_Pass.html: "The 'Reflect At Surface' becomes
/// a mirror. All surface data beyond this surface is lost, all data preceding this surface is
/// reflected and copied with pick-up solves to create a double pass system", and "this feature
/// will remove any solves").
/// <para>
/// The rule (derived from section 3's signs, not copied): surfaces 0…k are kept, k's material
/// becomes MIRROR and its thickness −t(k−1); then for j = k−1 down to 1 a copy of surface j
/// follows, with the same radius and conic (the same surface in space: a radius's sign does not
/// depend on the mirrors before it), the thickness −t(j−1) (thickness changes sign after a
/// mirror) and the material that was before surface j (the medium the returning light enters).
/// The copies pick their radius and conic up from the originals and their thickness from the
/// original gap with scale −1; they are never the stop. The image surface follows the copy of
/// surface 1 at −t0, so with a finite object it lies on the object surface; with the object at
/// infinity the thickness is 0 (the image at surface 1's vertex plane) and a note says so.
/// Every solve is removed; variables and pickups among the kept surfaces stay. A mirror before
/// k, or any surface type but Standard in 1…k, is refused (the help warns of special surfaces
/// and coordinate breaks).
/// </para>
/// </summary>
public static class MakeDoublePass
{
    public static LensEditResult Apply(IReadOnlyList<OpticsSurfaceSettings> surfaces, int reflectAt)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        SurfaceFlags.CheckRange("Make Double Pass", surfaces, reflectAt, reflectAt, 1, surfaces.Count - 2);
        int k = reflectAt;
        for (int j = 1; j <= k; j++)
        {
            if (!SavedSurfaces.IsStandard(surfaces[j]))
                throw new ArgumentException($"Make Double Pass: surface {j} is a '{surfaces[j].Type}' surface; only Standard surfaces can be copied in this build.");
            if (j < k && SavedSurfaces.IsMirror(surfaces[j]))
                throw new ArgumentException($"Make Double Pass: surface {j} is already a mirror; reflect at a surface before it, or remove the mirror.");
        }

        var notes = new List<string>();
        int solves = 0;
        OpticsSurfaceSettings NoSolves(OpticsSurfaceSettings s)
        {
            solves += s.Flags?.Count(f => SurfaceFlags.IsKind(f, SavedSurfaces.SolveFlag)) ?? 0;
            return SurfaceFlags.Without(s, f => SurfaceFlags.IsKind(f, SavedSurfaces.SolveFlag)
                || (SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) && (f.PickupSurface < 0 || f.PickupSurface > k)));
        }
        (double Thickness, bool Infinite) Back(int gap)
        {
            var g = surfaces[gap];
            if (g.ThicknessIsInfinite)
            {
                notes.Add("The object is at infinity, so the returning light cannot be sent back to it: the image is placed at surface 1's vertex plane (thickness 0).");
                return (0, false);
            }
            return (-g.Thickness, false);
        }

        var result = new List<OpticsSurfaceSettings>();
        for (int j = 0; j < k; j++) result.Add(NoSolves(surfaces[j]));

        var (tk, _) = Back(k - 1);
        var mirror = NoSolves(surfaces[k]) with
        {
            Material = SavedSurfaces.Mirror, Index = null, ModelGlass = null, Thickness = tk, ThicknessIsInfinite = false
        };
        mirror = SurfaceFlags.Without(mirror, f => f.Column is SavedSurfaces.ThicknessColumn or SavedSurfaces.MaterialColumn);
        if (!surfaces[k - 1].ThicknessIsInfinite)
            mirror = SurfaceFlags.With(mirror, SavedSurfaces.ThicknessColumn, SurfaceFlags.Pickup(SavedSurfaces.ThicknessColumn, k - 1, SavedSurfaces.ThicknessColumn, -1));
        result.Add(mirror);

        for (int j = k - 1; j >= 1; j--)
        {
            var original = surfaces[j];
            var before = surfaces[j - 1];
            var (t, _) = Back(j - 1);
            var copy = original with
            {
                Thickness = t,
                ThicknessIsInfinite = false,
                Material = before.Material,
                Index = before.Index,
                ModelGlass = before.ModelGlass,
                IsStop = false,
                Flags = null,
            };
            copy = SurfaceFlags.With(copy, SavedSurfaces.RadiusColumn, SurfaceFlags.Pickup(SavedSurfaces.RadiusColumn, j, SavedSurfaces.RadiusColumn));
            copy = SurfaceFlags.With(copy, SavedSurfaces.ConicColumn, SurfaceFlags.Pickup(SavedSurfaces.ConicColumn, j, SavedSurfaces.ConicColumn));
            if (!before.ThicknessIsInfinite)
                copy = SurfaceFlags.With(copy, SavedSurfaces.ThicknessColumn, SurfaceFlags.Pickup(SavedSurfaces.ThicknessColumn, j - 1, SavedSurfaces.ThicknessColumn, -1));
            result.Add(copy);
        }

        var image = surfaces[^1] with { Flags = null, IsStop = false };
        result.Add(image);

        notes.Insert(0, $"Reflected at surface {k}: {k - 1} surface{(k - 1 == 1 ? "" : "s")} copied for the return pass; the data after surface {k} was replaced.");
        if (solves > 0) notes.Add($"{solves} solve{(solves == 1 ? "" : "s")} removed.");
        return new LensEditResult(result, notes.Distinct().ToArray());
    }
}
