using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Resolves a board net's copper layer into the grounded dielectric stackup an antenna solve
/// needs: which gaps lie under the metal (the substrate), which lie over it (the cover), and
/// which interface the current sheet sits on.
///
/// <para>Before this existed, every board net — top layer or buried — was solved over ONE
/// homogeneous slab built from two panel scalars, silently. A net on an inner layer of a
/// four-layer board is physically a COVERED patch (dielectric above the metal as well as
/// below), and the multi-layer TLGF has been able to solve that since Stage F2b; only the
/// board's own per-gap εr/tanδ was never carried across. It always existed on
/// <see cref="OpenSim.Core.Model.PcbStackupSettings"/> — the same data
/// <c>BoardCoupledExtractor.ResolveSubstrate</c> reads for the SI track.</para>
///
/// <para><b>Coordinate reversal.</b> Board copper orders run TOP-DOWN (L1 is the top layer) and
/// gap <c>i</c> sits between copper orders <c>i+1</c> and <c>i+2</c>; an antenna stackup is
/// built GROUND-UP. So the gap list reverses, and a net on copper order <c>k</c> of an
/// <c>N</c>-layer board sits at interface <c>N−1−k</c> — the top interface for k = 1 (a
/// coplanar-top patch, the case that shipped) and interface 0 for the layer just above the
/// ground.</para>
///
/// <para><b>Which layer is the ground.</b> When the net's footprint is supplied, the ground is
/// the NEAREST copper layer below the net whose copper lies under (nearly) the whole footprint —
/// the plane the fields actually terminate on; the gaps and copper beneath it are shielded and
/// left out. A top-layer trace over an L2 plane is then solved over the L1–L2 gap, not the full
/// board thickness. When no layer below covers the footprint, or no footprint is supplied, the
/// BOTTOM copper layer is taken as the ground, and the note says so, because a board whose
/// bottom layer is not a plane will not behave this way. Either way the ground is an infinite
/// PEC plane and no other copper is in the model — only the selected net's own island
/// radiates.</para>
/// </summary>
public sealed record BoardAntennaStackup(
    LayeredStackup Stackup,
    int SourceInterface,
    bool IsCoplanarTop,
    IReadOnlyList<string> Assumptions)
{
    /// <summary>True when the resolved stackup is a plain single slab with the metal on top —
    /// the pre-existing <see cref="SubstrateStackup"/> case. The caller keeps using that path
    /// so top-layer board nets stay bitwise what they have always been.</summary>
    public bool IsSingleSlabTop => IsCoplanarTop && Stackup.Layers.Count == 1;

    /// <summary>Resolve the stackup for a net on copper layer <paramref name="layerOrder"/>
    /// (1 = top). Throws a typed failure naming the reason when the board cannot support an
    /// antenna model — a guessed ground plane would be a wrong answer, not a rough one.</summary>
    /// <param name="board">The imported board (its <c>Stackup</c> supplies per-gap εr/tanδ/h).</param>
    /// <param name="layerOrder">The net's copper layer order, 1 = top.</param>
    /// <param name="defaultEpsR">εr for gaps the board does not specify.</param>
    /// <param name="defaultTanD">tanδ for gaps the board does not specify.</param>
    /// <param name="defaultBoardThicknessMeters">Total thickness to split evenly when the board
    /// lists no per-gap thicknesses.</param>
    public static BoardAntennaStackup Resolve(PcbBoard board, int layerOrder,
        double defaultEpsR, double defaultTanD, double defaultBoardThicknessMeters) =>
        Resolve(board, layerOrder, BoardStackup.FromBoard(board, defaultBoardThicknessMeters,
            defaultEpsR, defaultTanD));

    /// <summary>Fraction of the net's footprint a lower layer's copper must lie under to be
    /// the ground plane: a plane with antipads and a few slots still qualifies, a layer of
    /// routed traces does not.</summary>
    public const double GroundCoverageFraction = 0.9;

    /// <summary>Resolve the stackup for a net on copper layer <paramref name="layerOrder"/>
    /// over the given <paramref name="stackup"/> (the one the rest of the board workflow
    /// reads). With <paramref name="footprint"/> — the net's own island — the ground is the
    /// nearest lower layer whose copper covers it; without, the bottom layer.</summary>
    public static BoardAntennaStackup Resolve(PcbBoard board, int layerOrder,
        BoardStackup stackup, Polygon2? footprint = null)
    {
        int copperLayers = board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder) : 1;
        if (copperLayers < 2)
            throw new ArgumentException(
                "A board antenna needs a reference plane: this board has a single copper layer, "
                + "so there is no ground for the layered Green's function. Use the free-space "
                + "wire/surface path, or import a board with a ground layer.", nameof(board));
        if (layerOrder < 1 || layerOrder > copperLayers)
            throw new ArgumentOutOfRangeException(nameof(layerOrder),
                $"Copper layer order {layerOrder} is outside this board's L1..L{copperLayers}.");
        if (layerOrder == copperLayers)
            throw new ArgumentException(
                $"The net sits on L{layerOrder}, the bottom copper layer, which this model uses "
                + "as the infinite PEC ground plane — a radiator cannot be its own reference. "
                + "Select a net on another layer.", nameof(layerOrder));

        // The ground: the nearest layer below the net with copper under its footprint.
        int groundLayer = copperLayers;
        bool groundFound = false;
        if (footprint is not null)
        {
            for (int candidate = layerOrder + 1; candidate <= copperLayers; candidate++)
            {
                if (CoverageOf(board, candidate, footprint) < GroundCoverageFraction) continue;
                groundLayer = candidate;
                groundFound = true;
                break;
            }
        }
        int numGaps = groundLayer - 1;

        // Ground-up: gap (groundLayer − 1) sits on the ground, gap 1 is under the top copper.
        var layers = new List<LayeredStackup.Layer>(numGaps);
        for (int gap = numGaps; gap >= 1; gap--)
        {
            double h = stackup.GapThicknessOf(gap);
            double eps = stackup.PermittivityOf(gap);
            if (h <= 0)
                throw new ArgumentException(
                    $"The dielectric gap between L{gap} and L{gap + 1} has non-positive "
                    + $"thickness ({h:g3} m) — the stackup cannot be built.", nameof(board));
            if (eps < 1)
                throw new ArgumentException(
                    $"The dielectric gap between L{gap} and L{gap + 1} has εr {eps:g3} < 1.",
                    nameof(board));
            layers.Add(new LayeredStackup.Layer(eps, Math.Max(stackup.LossTangentOf(gap), 0), h));
        }

        int sourceInterface = groundLayer - 1 - layerOrder;
        bool coplanarTop = sourceInterface == numGaps - 1;
        var resolved = new LayeredStackup(layers);

        string groundNote = groundFound
            ? $"L{groundLayer} is the nearest layer below the net with copper under its whole "
              + "footprint, so it is modelled as the infinite PEC ground plane"
              + (groundLayer < copperLayers
                  ? $"; the {copperLayers - groundLayer} copper layer(s) beneath it are shielded and left out"
                  : "")
            : footprint is not null
                ? $"no copper layer below L{layerOrder} lies under the net's footprint, so the "
                  + $"BOTTOM layer (L{copperLayers}) is modelled as the infinite PEC ground plane "
                  + "— check that this is the board's real reference"
                : $"the BOTTOM layer (L{copperLayers}) is modelled as the infinite PEC ground plane";
        var notes = new List<string>
        {
            $"Board stackup: L{layerOrder} of {copperLayers} copper layers; {groundNote}, and no other "
            + "copper is present in the model — only the selected net radiates.",
            $"Dielectric gaps ({(stackup.Source == "default" ? "defaults" : stackup.Source)}), ground "
            + "upward: " + string.Join(", ", resolved.Layers.Select(
                l => $"εr {l.RelativePermittivity:g3} / tanδ {l.LossTangent:g3} / "
                     + $"{l.ThicknessMeters * 1e3:g3} mm")) + ".",
        };
        notes.Add(coplanarTop
            ? "The net is on the top copper layer, so the metal is coplanar at the slab top "
              + "(no dielectric cover)."
            : $"The net is buried: {numGaps - 1 - sourceInterface} dielectric gap(s) lie ABOVE "
              + "the metal, so this solves as a covered patch (the cover loads the resonance "
              + "downward relative to the same trace on the top layer).");

        return new BoardAntennaStackup(resolved, sourceInterface, coplanarTop, notes);
    }

    /// <summary>Fraction of <paramref name="footprint"/>'s area that has copper of
    /// <paramref name="layer"/> under it.</summary>
    private static double CoverageOf(PcbBoard board, int layer, Polygon2 footprint)
    {
        double area = footprint.Area();
        if (area <= 0) return 0;
        var copper = board.Islands.Where(i => i.LayerOrder == layer)
            .SelectMany(i => Polygon2.OrientedRings(i.Shape)).ToList();
        if (copper.Count == 0) return 0;
        return new ClipperPolygonOps().Intersect(new[] { footprint }, copper).Sum(p => p.Area()) / area;
    }

    /// <summary>Non-throwing form for UI paths that report a failure string rather than
    /// catching. Returns false with <paramref name="failure"/> set to the typed reason.</summary>
    public static bool TryResolve(PcbBoard board, int layerOrder,
        double defaultEpsR, double defaultTanD, double defaultBoardThicknessMeters,
        out BoardAntennaStackup? resolved, out string failure)
    {
        try
        {
            resolved = Resolve(board, layerOrder, defaultEpsR, defaultTanD,
                defaultBoardThicknessMeters);
            failure = "";
            return true;
        }
        catch (ArgumentException e)
        {
            resolved = null;
            failure = e.Message.Split(" (Parameter")[0];
            return false;
        }
    }

    /// <summary>Non-throwing form of the <see cref="BoardStackup"/> overload.</summary>
    public static bool TryResolve(PcbBoard board, int layerOrder, BoardStackup stackup,
        Polygon2? footprint, out BoardAntennaStackup? resolved, out string failure)
    {
        try
        {
            resolved = Resolve(board, layerOrder, stackup, footprint);
            failure = "";
            return true;
        }
        catch (ArgumentException e)
        {
            resolved = null;
            failure = e.Message.Split(" (Parameter")[0];
            return false;
        }
    }
}
