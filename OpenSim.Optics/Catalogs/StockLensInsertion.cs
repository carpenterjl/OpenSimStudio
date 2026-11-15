using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.Optics.Catalogs;

// OP-16: Insert Lens (docs/Optics-Conventions.md section 53).

/// <summary>The insert file options of Insert Lens (OpticStudio 2025 R2 help, File tab › Lens
/// File Group › Insert Lens; plans/sources/optics/op-94-help/Insert_Lens.html).</summary>
public sealed record InsertLensOptions
{
    /// <summary>The help's "Ignore Object": "the object thickness of the lens being inserted will
    /// be ignored". When false, a finite non-zero object thickness is kept as a plane dummy surface
    /// in front of the inserted lens (this program's reading, section 53).</summary>
    public bool IgnoreObject { get; init; }

    /// <summary>The help's "Change Sign of Surface Geometry in Mirror Space": in mirror space (an
    /// odd number of mirrors before the insertion point) it "will reverse the sign of radii and
    /// thicknesses for all inserted surfaces"; outside mirror space it does nothing.</summary>
    public bool ChangeSignInMirrorSpace { get; init; } = true;

    /// <summary>Turns the inserted lens round with Reverse Elements (OP-51,
    /// <see cref="ReverseElements"/>) — this program's addition, for a lens used the other way round.</summary>
    public bool Reverse { get; init; }
}

/// <summary>The current lens with a lens file inserted.</summary>
/// <param name="Settings">The optics record with the new surfaces; every surface number it held renumbered.</param>
/// <param name="First">The first inserted surface.</param>
/// <param name="Last">The last inserted surface.</param>
/// <param name="Notes">What was changed or left out, one line each.</param>
public sealed record InsertLensResult(OpticsSettings Settings, int First, int Last, IReadOnlyList<string> Notes);

/// <summary>
/// Insert Lens: the surfaces of a lens file put into the current lens before surface
/// <c>at</c> ("New surfaces will be inserted into the lens prescription to make room for the new
/// data"; help, Insert Lens). The file's lens surfaces (1 to N − 2) are inserted; its image
/// surface is not, so the last inserted surface keeps the file's distance to its image. The
/// current lens's data is not disturbed: its stop stays (the inserted surfaces' stop flags are
/// cleared), and every surface number it holds — pickups, surface solves, tilt pickups,
/// coordinate returns, multi-configuration and merit rows — follows its surface
/// (<see cref="SavedLensSurgery"/>). Lengths are converted from the file's lens unit to the
/// current one with OP-51's lens-unit change (<see cref="ScaleLens.ConvertUnits"/>).
/// </summary>
public static class StockLensInsertion
{
    public static InsertLensResult Insert(OpticsSettings current, OpticsSettings lens, int at, InsertLensOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(lens);
        options ??= new InsertLensOptions();
        IReadOnlyList<OpticsSurfaceSettings> target = current.Surfaces ?? throw new ArgumentException("Insert Lens: the current system has no surfaces.");
        if (target.Count < 2) throw new ArgumentException("Insert Lens: the current lens needs at least the object and the image.");
        if (at < 1 || at > target.Count - 1)
            throw new ArgumentException($"Insert Lens: the lens can be inserted before surface 1 to {target.Count - 1} (the image); {at} is outside that.");
        IReadOnlyList<OpticsSurfaceSettings> file = lens.Surfaces ?? throw new ArgumentException("Insert Lens: the lens file has no surfaces.");
        if (file.Count < 3) throw new ArgumentException("Insert Lens: the lens file has no surfaces between its object and image.");

        var notes = new List<string>();
        var unit = ProjectLens.LensUnitOf(current.SystemData);
        var fileUnit = ProjectLens.LensUnitOf(lens.SystemData);
        if (unit != fileUnit)
        {
            double factor = OpticsConventions.MetersPer(fileUnit) / OpticsConventions.MetersPer(unit);
            var converted = ScaleLens.ConvertUnits(file, factor);
            file = converted.Surfaces;
            notes.Add($"The lens file's lengths were converted from {ConventionNames.Symbol(fileUnit)} to {ConventionNames.Symbol(unit)}.");
            notes.AddRange(converted.Notes);
        }

        int m = file.Count;
        var obj = file[0];
        bool dummy = false;
        if (obj.ThicknessIsInfinite)
        {
            if (!options.IgnoreObject) notes.Add("The lens file's object is at infinity; its object thickness was not inserted.");
        }
        else if (!options.IgnoreObject && obj.Thickness != 0) dummy = true;
        else if (options.IgnoreObject && obj.Thickness != 0) notes.Add("The lens file's object thickness was ignored.");

        int offset = dummy ? 1 : 0;
        int first = at;
        int last = at + offset + m - 3;
        int Map(int k) => k >= 1 && k <= m - 2 ? at + offset + k - 1
            : throw new ArgumentException($"Insert Lens: a surface of the lens file names its surface {k}, which is not inserted (only surfaces 1 to {m - 2} are); remove that pickup or solve from the file first.");

        var block = new List<OpticsSurfaceSettings>();
        if (dummy)
        {
            string title = lens.SystemData?.Title is { Length: > 0 } t ? t : "the inserted lens";
            block.Add(new OpticsSurfaceSettings { Comment = $"Object space of {title}", Thickness = obj.Thickness });
            notes.Add(FormattableString.Invariant($"The lens file's object thickness ({obj.Thickness:G10}) was kept as dummy surface {at}."));
        }
        int droppedPickups = 0;
        string? oldStop = null;
        for (int k = 1; k <= m - 2; k++)
        {
            var s = file[k];
            if (s.Flags is { Count: > 0 } flags)
            {
                var kept = flags.Where(f => !(string.Equals(f.Kind, SavedSurfaces.PickupFlag, StringComparison.Ordinal) && (f.PickupSurface < 1 || f.PickupSurface > m - 2))).ToList();
                droppedPickups += flags.Count - kept.Count;
                s = s with { Flags = kept.Count > 0 ? kept : null };
            }
            if (s.IsStop) oldStop = $"{k}";
            s = SavedLensSurgery.Renumber(s with { IsStop = false }, Map, at + offset + k - 1);
            block.Add(s);
        }
        if (droppedPickups > 0)
            notes.Add($"{droppedPickups} pickup{(droppedPickups == 1 ? "" : "s")} from the lens file's object or image surface removed; those cells are now fixed.");
        if (oldStop is not null)
            notes.Add($"The lens file's stop (its surface {oldStop}) was not kept; the current lens's stop stays where it is.");

        int mirrors = 0;
        for (int j = 1; j < at; j++) if (SavedSurfaces.IsMirror(target[j])) mirrors++;
        if (mirrors % 2 == 1)
        {
            if (options.ChangeSignInMirrorSpace)
            {
                for (int i = 0; i < block.Count; i++)
                {
                    var s = block[i];
                    block[i] = s with { Radius = s.Radius is double r ? -r : null, Thickness = s.ThicknessIsInfinite ? s.Thickness : -s.Thickness };
                }
                notes.Add($"Inserted in mirror space ({mirrors} mirrors before surface {at}): the inserted radii and thicknesses changed sign.");
            }
            else notes.Add($"Inserted in mirror space ({mirrors} mirrors before surface {at}) without changing the sign of the inserted radii and thicknesses.");
        }

        var builder = new SavedLensSurgery.Builder(target);
        builder.CopyRange(0, at - 1);
        foreach (var s in block) builder.Add(s);
        builder.CopyRange(at, target.Count - 1);
        var (settings, _, _) = builder.Build(current);

        if (options.Reverse)
        {
            int a = first + offset;
            if (a < last)
            {
                var reversed = ReverseElements.Apply(settings.Surfaces!, a, last);
                settings = settings with { Surfaces = reversed.Surfaces.ToList() };
                notes.AddRange(reversed.Notes);
            }
        }

        if (lens.Materials?.ActiveCatalogs is { Count: > 0 } wanted && current.Materials?.ActiveCatalogs is { Count: > 0 } active)
        {
            var missing = wanted.Where(w => !active.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                notes.Add($"The lens file's glass catalog{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not among the system's active catalogs; add {(missing.Count == 1 ? "it" : "them")} if its glasses are not found.");
        }
        notes.Insert(0, $"Surfaces {first} to {last} inserted{(lens.SystemData?.Title is { Length: > 0 } name ? $" from '{name}'" : "")}.");
        return new InsertLensResult(settings, first, last, notes);
    }
}
