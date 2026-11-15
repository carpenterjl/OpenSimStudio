using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Tolerancing;

/// <summary>
/// Inserting and removing saved surfaces while every surface number the rest of the record holds
/// follows its surface: pickup sources, the surface arguments of solves (concentric with surface or
/// radius, position, compensator, center of curvature), tilt pick-ups, coordinate returns, the
/// surface arguments of multi-configuration rows (and the values of CRSR and STPS, which are
/// surface numbers), and the Surf arguments of merit function rows. The tolerance table itself is
/// not renumbered: <see cref="ToleranceChange.MapSurface"/> carries its numbers instead.
/// </summary>
internal static class SavedLensSurgery
{
    private static readonly Lazy<MeritOperandRegistry> Merit = new(MeritOperandRegistry.CreateDefault);

    /// <summary>The solves whose first parameter is a surface number (SavedSolves).</summary>
    private static readonly HashSet<string> SurfaceSolves = new(StringComparer.Ordinal)
    {
        "Concentric with surface", "Concentric with radius", "Position", "Compensator", "Center of curvature",
    };

    /// <summary>Builds a new surface list from the old one, original surfaces copied in order and
    /// new ones added between them.</summary>
    internal sealed class Builder
    {
        private readonly IReadOnlyList<OpticsSurfaceSettings> _old;
        private readonly int[] _map;
        private readonly List<OpticsSurfaceSettings> _surfaces = new();
        private readonly List<int> _inserted = new();

        public Builder(IReadOnlyList<OpticsSurfaceSettings> old)
        {
            _old = old;
            _map = new int[old.Count];
            Array.Fill(_map, -1);
        }

        /// <summary>Copies old surface <paramref name="old"/>; returns its new number.</summary>
        public int Copy(int old)
        {
            _map[old] = _surfaces.Count;
            _surfaces.Add(_old[old]);
            return _surfaces.Count - 1;
        }

        public void CopyRange(int first, int last)
        {
            for (int j = first; j <= last; j++) Copy(j);
        }

        /// <summary>Adds a new surface, written in the new numbering; returns its number.</summary>
        public int Add(OpticsSurfaceSettings s)
        {
            _inserted.Add(_surfaces.Count);
            _surfaces.Add(s);
            return _surfaces.Count - 1;
        }

        /// <summary>The new number of an old surface already copied.</summary>
        public int NewIndex(int old) => _map[old] >= 0 ? _map[old]
            : throw new InvalidOperationException($"Surface {old} has not been placed yet.");

        /// <summary>The record with the new list; the copied surfaces, the multi-configuration rows
        /// and the merit rows renumbered.</summary>
        public (OpticsSettings Settings, int[] Map, int[] Inserted) Build(OpticsSettings settings)
        {
            if (_map.Any(m => m < 0)) throw new InvalidOperationException("Every original surface must be copied.");
            var inserted = new HashSet<int>(_inserted);
            int Map(int k) => k >= 0 && k < _map.Length ? _map[k] : k;
            var list = _surfaces.Select((s, i) => inserted.Contains(i) ? s : Renumber(s, Map, i)).ToList();
            var result = settings with
            {
                Surfaces = list,
                MultiConfiguration = RenumberTable(settings.MultiConfiguration, Map, _map.Length),
                MeritFunction = RenumberMerit(settings.MeritFunction, Map, _map.Length),
            };
            return (result, (int[])_map.Clone(), _inserted.ToArray());
        }
    }

    /// <summary>Removes surfaces <paramref name="inserted"/> (numbers in <paramref name="settings"/>)
    /// and renumbers the rest back. Throws when a remaining surface names a removed one.</summary>
    public static OpticsSettings Remove(OpticsSettings settings, IReadOnlyCollection<int> inserted)
    {
        var surfaces = settings.Surfaces ?? throw new InvalidOperationException("The record has no surfaces.");
        var drop = new HashSet<int>(inserted);
        var map = new int[surfaces.Count];
        int n = 0;
        for (int i = 0; i < surfaces.Count; i++) map[i] = drop.Contains(i) ? -1 : n++;
        int Map(int k)
        {
            if (k < 0 || k >= map.Length) return k;
            return map[k] >= 0 ? map[k]
                : throw new InvalidOperationException($"Removing the tolerance's surfaces: something still names surface {k}, which the tolerance inserted.");
        }
        var list = new List<OpticsSurfaceSettings>();
        for (int i = 0; i < surfaces.Count; i++)
            if (!drop.Contains(i)) list.Add(Renumber(surfaces[i], Map, i));
        return settings with
        {
            Surfaces = list,
            MultiConfiguration = RenumberTable(settings.MultiConfiguration, Map, surfaces.Count),
            MeritFunction = RenumberMerit(settings.MeritFunction, Map, surfaces.Count),
        };
    }

    // OP-16: internal (was private) so Insert Lens renumbers the inserted surfaces with the same rules.
    /// <summary>The surface with every surface number it holds mapped.</summary>
    internal static OpticsSurfaceSettings Renumber(OpticsSurfaceSettings s, Func<int, int> map, int where)
    {
        var r = s;
        if (s.Flags is { Count: > 0 } flags && flags.Any(f => NamesSurface(f)))
            r = r with
            {
                Flags = flags.Select(f =>
                {
                    if (string.Equals(f.Kind, SavedSurfaces.PickupFlag, StringComparison.Ordinal))
                        return f with { PickupSurface = map(f.PickupSurface) };
                    if (IsSurfaceSolve(f) && f.SolveParameters is { Count: > 0 } p)
                    {
                        var q = p.ToList();
                        q[0] = map((int)q[0]);
                        return f with { SolveParameters = q };
                    }
                    return f;
                }).ToList(),
            };
        if (s.TiltAfter is { Mode: "PickUpSurface" or "ReverseSurface" } after)
            r = r with { TiltAfter = after with { Surface = map(after.Surface) } };
        if (s.CoordinateReturn is { } ret && ret.Mode is not (null or "None"))
            r = r with { CoordinateReturn = ret with { Surface = map(ret.Surface) } };
        return r;
    }

    private static bool IsSurfaceSolve(OpticsCellFlagSettings f) =>
        string.Equals(f.Kind, SavedSurfaces.SolveFlag, StringComparison.Ordinal) && f.Solve is { } name && SurfaceSolves.Contains(name)
        && f.SolveParameters is { Count: > 0 } p && Math.Round(p[0]) == p[0];

    private static bool NamesSurface(OpticsCellFlagSettings f) =>
        string.Equals(f.Kind, SavedSurfaces.PickupFlag, StringComparison.Ordinal) || IsSurfaceSolve(f);

    private static OpticsMultiConfigSettings? RenumberTable(OpticsMultiConfigSettings? table, Func<int, int> map, int count)
    {
        if (table?.Operands is not { Count: > 0 } rows) return table;
        int M(int k) => k >= 0 && k < count ? map(k) : k;
        return table with
        {
            Operands = rows.Select(row =>
            {
                if (MultiConfigOperands.Find(row.Type) is not { } op) return row;
                bool S(int i) => i < op.Arguments.Count && op.Arguments[i].Contains("surface", StringComparison.OrdinalIgnoreCase);
                var r = row with
                {
                    Number1 = S(0) ? M(row.Number1) : row.Number1,
                    Number2 = S(1) ? M(row.Number2) : row.Number2,
                    Number3 = S(2) ? M(row.Number3) : row.Number3,
                };
                if (op.Code is "CRSR" or "STPS" && r.Values is { } values)
                    r = r with
                    {
                        Values = values.Select(v => v.Text is not null || Math.Round(v.Value) != v.Value ? v
                            : v with { Value = M((int)v.Value) }).ToList(),
                    };
                return r;
            }).ToList(),
        };
    }

    private static OpticsMeritFunctionSettings? RenumberMerit(OpticsMeritFunctionSettings? merit, Func<int, int> map, int count)
    {
        if (merit?.Operands is not { Count: > 0 } rows) return merit;
        int M(int k) => k >= 0 && k < count ? map(k) : k;
        return merit with
        {
            Operands = rows.Select(row =>
            {
                if (row.Type is null || Merit.Value.Find(row.Type) is not { } d) return row;
                bool S(string? label) => label is not null && label.StartsWith("Surf", StringComparison.Ordinal);
                return row with
                {
                    Int1 = S(d.Int1) ? M(row.Int1) : row.Int1,
                    Int2 = S(d.Int2) ? M(row.Int2) : row.Int2,
                };
            }).ToList(),
        };
    }
}
