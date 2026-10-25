using OpenSim.Core.Model;
using OpenSim.Pcb.Extrude;

namespace OpenSim.Pcb.Import;

/// <summary>A copper-layer row of the stackup panel: the layer's order (1 = top), its copper
/// thickness [m] and, when the board file names its layers, the name shown beside "L&lt;n&gt;".</summary>
public sealed record PanelCopperRow(int LayerOrder, double ThicknessMeters, string? Name = null);

/// <summary>A dielectric-gap row of the stackup panel: the copper layer above the gap, and the
/// gap's thickness [m], relative permittivity εr and loss tangent tanδ.</summary>
public sealed record PanelGapRow(int UpperLayerOrder, double ThicknessMeters,
    double RelativePermittivity, double LossTangent);

/// <summary>The rows the stackup panel shows for a freshly imported board, and the value the
/// board-thickness box takes (the file's when it carries a stackup, else the box as it was).</summary>
public sealed record PanelSeed(double BoardThickness, IReadOnlyList<PanelCopperRow> Layers,
    IReadOnlyList<PanelGapRow> Gaps);

/// <summary>
/// The stackup panel's plumbing, as pure functions over plain rows: how an imported board
/// seeds the rows (<see cref="Seed"/>), and how the rows become the one stackup every board
/// analysis reads — the <see cref="NetMeshOptions"/> the mesher, chain builder, SI,
/// capacitance and antenna paths take (<see cref="ToMeshOptions"/>), and the
/// <see cref="PcbStackupSettings"/> persisted with the project and handed to the board
/// thermal mesher (<see cref="ToSettings"/>). The WPF view model only turns its observable
/// rows into these records and back, so the plumbing is pinned here, outside the app.
///
/// <para>Index contract: a gap is keyed by the copper layer ABOVE it, and the persisted lists
/// are indexed by (order − 1), so <see cref="Seed"/> reads back exactly what
/// <see cref="ToSettings"/> wrote.</para>
/// </summary>
public static class StackupPanel
{
    /// <summary>εr of a gap no row and no file describes (FR4).</summary>
    public const double DefaultPermittivity = 4.4;

    /// <summary>tanδ of a gap no row and no file describes (FR4).</summary>
    public const double DefaultLossTangent = 0.02;

    /// <summary>
    /// The rows for a freshly imported board. Every copper layer of the board gets a row —
    /// including one with no copper on it, since it still occupies its place in the stack
    /// and without rows for it and its gaps the z model would fall back to a whole-board
    /// gap there. Copper thickness comes from the file's stackup (IPC-2581) where it lists
    /// the layer, else from the copper box (<paramref name="copperThickness"/>, when
    /// positive). One gap row sits between each adjacent pair, seeded from the file where it
    /// has one, else by splitting the board thickness evenly across the gaps; the materials
    /// come from the file or default to FR4. Layer names are shown only for boards whose
    /// file carries a stackup (IPC-2581); Gerber layers read "L&lt;n&gt;".
    ///
    /// <para><paramref name="saved"/> is the stackup a reopened project persisted
    /// (<see cref="ToSettings"/>): when the file carries no stackup of its own and the saved
    /// one has rows, the rows come from it at the same (order − 1) index, so a re-imported
    /// board shows the panel as it was saved. The file always wins over it; the saved board
    /// thickness does not replace the box (the box already holds it, possibly edited since);
    /// the rows follow layer order (L1 first), not layer identity, so saved rows past the
    /// board's layers are ignored, copper layers past the saved rows take the copper box and
    /// gaps past them an even share of the board box; a saved stackup without rows changes
    /// nothing.</para>
    /// </summary>
    public static PanelSeed Seed(PcbBoard board, double copperThickness, double boardThickness,
        PcbStackupSettings? saved = null)
    {
        var file = board.Stackup;
        var rows = file ?? (saved is not null
            && (saved.CopperLayerThicknesses is { Count: > 0 } || saved.DielectricGapThicknesses is { Count: > 0 }) ? saved : null);
        var layerNames = board.Layers
            .Where(l => l.CopperOrder > 0)
            .GroupBy(l => l.CopperOrder)
            .ToDictionary(g => g.Key, g => g.First().FileName);
        var orders = board.Islands.Select(i => i.LayerOrder)
            .Concat(layerNames.Keys)
            .Distinct().OrderBy(o => o).ToList();

        var layers = new List<PanelCopperRow>(orders.Count);
        foreach (int order in orders)
        {
            double thickness =
                rows is not null && order - 1 < rows.CopperLayerThicknesses.Count
                    ? rows.CopperLayerThicknesses[order - 1]
                    : copperThickness > 0 ? copperThickness : PcbStackup.DefaultCopperThickness;
            layers.Add(new PanelCopperRow(order, thickness, file is null ? null : layerNames.GetValueOrDefault(order)));
        }

        if (file is not null && file.DielectricGapThicknesses.Count > 0)
            boardThickness = file.BoardThickness;
        int gapCount = Math.Max(1, orders.Count - 1);
        double defaultGap = boardThickness / gapCount;
        var gaps = new List<PanelGapRow>(Math.Max(0, orders.Count - 1));
        for (int i = 0; i + 1 < orders.Count; i++)
        {
            int index = orders[i] - 1;
            gaps.Add(new PanelGapRow(orders[i],
                rows is not null && index < rows.DielectricGapThicknesses.Count
                    ? rows.DielectricGapThicknesses[index] : defaultGap,
                rows is not null && index < rows.DielectricGapPermittivities.Count
                    ? rows.DielectricGapPermittivities[index] : DefaultPermittivity,
                rows is not null && index < rows.DielectricGapLossTangents.Count
                    ? rows.DielectricGapLossTangents[index] : DefaultLossTangent));
        }
        return new PanelSeed(boardThickness, layers, gaps);
    }

    /// <summary>
    /// The options every board analysis shares, built from the panel: per-layer copper and
    /// per-gap thickness and materials keyed by layer order, the copper box as the fallback
    /// for a layer without a row, and the board-thickness box as the fallback for a gap
    /// without one. Its <see cref="NetMeshOptions.Stackup"/> is the object the SI,
    /// capacitance and antenna paths read. A row at or below zero is carried as typed;
    /// every reader of these options goes through <see cref="BoardStackup"/>, which falls
    /// back to the box (copper and gap thickness, εr) or FR4 (tanδ) for such a row.
    /// </summary>
    public static NetMeshOptions ToMeshOptions(double copperThickness, double boardThickness,
        IReadOnlyCollection<PanelCopperRow> layers, IReadOnlyCollection<PanelGapRow> gaps,
        double targetEdgeLength, double viaPlatingThickness)
    {
        Validate(layers, gaps);
        return new NetMeshOptions
        {
            TargetEdgeLength = targetEdgeLength,
            CopperThickness = copperThickness,
            LayerThickness = layers.ToDictionary(l => l.LayerOrder, l => l.ThicknessMeters),
            DielectricGapThickness = gaps.ToDictionary(g => g.UpperLayerOrder, g => g.ThicknessMeters),
            DielectricGapPermittivity = gaps.ToDictionary(g => g.UpperLayerOrder, g => g.RelativePermittivity),
            DielectricGapLossTangent = gaps.ToDictionary(g => g.UpperLayerOrder, g => g.LossTangent),
            DefaultDielectricThickness = boardThickness,
            ViaPlatingThickness = viaPlatingThickness
        };
    }

    /// <summary>
    /// The stackup persisted with the project and read by the board thermal mesher: the two
    /// boxes, plus per-layer copper and per-gap thickness, εr and tanδ as lists indexed by
    /// (order − 1), running to the highest row. A layer order without a row takes the copper
    /// box; a gap order without a row takes an even share of the board thickness and FR4 —
    /// a benign default, since <see cref="Seed"/> gives every gap a row. With no rows at
    /// all only the two boxes are kept, and the lists stay empty. The readers of these
    /// lists (the board thermal mesher, the serializer) take the values as they are, so a
    /// row at or below zero is replaced here by the same fallback <see cref="BoardStackup"/>
    /// applies for the other consumers: the copper box, the gap's even share, εr 4.4,
    /// tanδ 0.02 — every analysis then sees one stackup even for a blank or mistyped row.
    /// </summary>
    public static PcbStackupSettings ToSettings(double copperThickness, double boardThickness,
        IReadOnlyCollection<PanelCopperRow> layers, IReadOnlyCollection<PanelGapRow> gaps)
    {
        Validate(layers, gaps);
        var settings = new PcbStackupSettings { CopperThickness = copperThickness, BoardThickness = boardThickness };

        if (layers.Count > 0)
        {
            var copper = new double[layers.Max(l => l.LayerOrder)];
            Array.Fill(copper, copperThickness);
            foreach (var l in layers)
                if (l.ThicknessMeters > 0) copper[l.LayerOrder - 1] = l.ThicknessMeters;
            settings = settings with { CopperLayerThicknesses = copper };
        }

        if (gaps.Count > 0)
        {
            int count = gaps.Max(g => g.UpperLayerOrder);
            var thickness = new double[count];
            var permittivity = new double[count];
            var loss = new double[count];
            Array.Fill(thickness, boardThickness / count);
            Array.Fill(permittivity, DefaultPermittivity);
            Array.Fill(loss, DefaultLossTangent);
            foreach (var g in gaps)
            {
                if (g.ThicknessMeters > 0) thickness[g.UpperLayerOrder - 1] = g.ThicknessMeters;
                if (g.RelativePermittivity > 0) permittivity[g.UpperLayerOrder - 1] = g.RelativePermittivity;
                if (g.LossTangent >= 0) loss[g.UpperLayerOrder - 1] = g.LossTangent;
            }
            settings = settings with
            {
                DielectricGapThicknesses = thickness,
                DielectricGapPermittivities = permittivity,
                DielectricGapLossTangents = loss
            };
        }
        return settings;
    }

    private static void Validate(IReadOnlyCollection<PanelCopperRow> layers, IReadOnlyCollection<PanelGapRow> gaps)
    {
        foreach (var l in layers)
            if (l.LayerOrder < 1)
                throw new ArgumentOutOfRangeException(nameof(layers), l.LayerOrder, "A copper layer order is 1 (top) or more.");
        foreach (var g in gaps)
            if (g.UpperLayerOrder < 1)
                throw new ArgumentOutOfRangeException(nameof(gaps), g.UpperLayerOrder, "A gap's upper copper layer order is 1 (top) or more.");
        if (layers.Select(l => l.LayerOrder).Distinct().Count() != layers.Count)
            throw new ArgumentException("Two copper rows share a layer order.", nameof(layers));
        if (gaps.Select(g => g.UpperLayerOrder).Distinct().Count() != gaps.Count)
            throw new ArgumentException("Two gap rows share an upper layer order.", nameof(gaps));
    }
}
