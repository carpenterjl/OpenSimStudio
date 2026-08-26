using OpenSim.Pcb.Import;

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
/// <para><b>Stated assumptions.</b> The BOTTOM copper layer is taken as the antenna's infinite
/// PEC ground plane, and no other copper is in the model — only the selected net's own island
/// radiates. That is the standard microstrip idealization, and it is logged rather than
/// assumed silently, because a board whose bottom layer is not a plane will not behave this
/// way.</para>
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
        double defaultEpsR, double defaultTanD, double defaultBoardThicknessMeters)
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

        var settings = board.Stackup;
        int numGaps = copperLayers - 1;
        double evenH = defaultBoardThicknessMeters / numGaps;

        // Gap i (top-down, between copper orders i+1 and i+2) → its material.
        (double EpsR, double TanD, double H, bool FromFile) Gap(int i)
        {
            double h = evenH, eps = defaultEpsR, tan = defaultTanD;
            bool fromFile = false;
            if (settings is not null)
            {
                if (i < settings.DielectricGapThicknesses.Count)
                { h = settings.DielectricGapThicknesses[i]; fromFile = true; }
                if (i < settings.DielectricGapPermittivities.Count)
                    eps = settings.DielectricGapPermittivities[i];
                if (i < settings.DielectricGapLossTangents.Count)
                    tan = settings.DielectricGapLossTangents[i];
            }
            return (eps, tan, h, fromFile);
        }

        // Ground-up: gap (numGaps-1) sits on the ground, gap 0 is under the top copper.
        var layers = new List<LayeredStackup.Layer>(numGaps);
        bool anyFromFile = false;
        for (int i = numGaps - 1; i >= 0; i--)
        {
            var g = Gap(i);
            if (g.H <= 0)
                throw new ArgumentException(
                    $"The dielectric gap between L{i + 1} and L{i + 2} has non-positive "
                    + $"thickness ({g.H:g3} m) — the stackup cannot be built.", nameof(board));
            if (g.EpsR < 1)
                throw new ArgumentException(
                    $"The dielectric gap between L{i + 1} and L{i + 2} has εr {g.EpsR:g3} < 1.",
                    nameof(board));
            layers.Add(new LayeredStackup.Layer(g.EpsR, Math.Max(g.TanD, 0), g.H));
            anyFromFile |= g.FromFile;
        }

        int sourceInterface = copperLayers - 1 - layerOrder;
        bool coplanarTop = sourceInterface == numGaps - 1;
        var stackup = new LayeredStackup(layers);

        var notes = new List<string>
        {
            $"Board stackup: L{layerOrder} of {copperLayers} copper layers; the BOTTOM layer "
            + $"(L{copperLayers}) is modelled as the infinite PEC ground plane, and no other "
            + "copper is present in the model — only the selected net radiates.",
            $"Dielectric gaps ({(anyFromFile ? "from the board stackup" : "defaults")}), ground "
            + "upward: " + string.Join(", ", stackup.Layers.Select(
                l => $"εr {l.RelativePermittivity:g3} / tanδ {l.LossTangent:g3} / "
                     + $"{l.ThicknessMeters * 1e3:g3} mm")) + ".",
        };
        notes.Add(coplanarTop
            ? "The net is on the top copper layer, so the metal is coplanar at the slab top "
              + "(no dielectric cover)."
            : $"The net is buried: {numGaps - 1 - sourceInterface} dielectric gap(s) lie ABOVE "
              + "the metal, so this solves as a covered patch (the cover loads the resonance "
              + "downward relative to the same trace on the top layer).");

        return new BoardAntennaStackup(stackup, sourceInterface, coplanarTop, notes);
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
}
