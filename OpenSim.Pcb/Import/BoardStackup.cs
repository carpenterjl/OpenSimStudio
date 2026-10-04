namespace OpenSim.Pcb.Import;

/// <summary>
/// The board stackup as every board analysis reads it: copper thickness per layer, and
/// thickness, εr and tanδ per dielectric gap. One object, built once per run from the
/// stackup panel (<see cref="NetMeshOptions.Stackup"/>) or, where no panel is involved,
/// from the board file (<see cref="FromBoard"/>). The mesher and the trace-chain builder
/// take the z model from it, the SI and capacitance extractors the substrate and the
/// trace copper, and the board antenna its grounded slab stack — so an edit to one row
/// reaches all of them, and none of them carries a private default.
///
/// <para>Copper layer orders run top-down (1 = top). A gap is keyed by the copper layer
/// ABOVE it: gap <c>k</c> lies between layers <c>k</c> and <c>k+1</c>.</para>
/// </summary>
public sealed record BoardStackup
{
    /// <summary>Copper thickness [m] of layers not listed in <see cref="LayerThickness"/>.</summary>
    public double DefaultCopperThickness { get; init; } = 35e-6;

    /// <summary>Copper thickness [m] by layer order.</summary>
    public IReadOnlyDictionary<int, double>? LayerThickness { get; init; }

    /// <summary>Dielectric thickness [m] of gaps not listed in <see cref="GapThickness"/>.</summary>
    public double DefaultGapThickness { get; init; } = 1.6e-3;

    /// <summary>Dielectric thickness [m] by upper copper layer order.</summary>
    public IReadOnlyDictionary<int, double>? GapThickness { get; init; }

    /// <summary>εr of gaps not listed in <see cref="GapPermittivity"/>.</summary>
    public double DefaultPermittivity { get; init; } = 4.4;

    /// <summary>Relative permittivity by upper copper layer order.</summary>
    public IReadOnlyDictionary<int, double>? GapPermittivity { get; init; }

    /// <summary>tanδ of gaps not listed in <see cref="GapLossTangent"/>.</summary>
    public double DefaultLossTangent { get; init; } = 0.02;

    /// <summary>Loss tangent by upper copper layer order.</summary>
    public IReadOnlyDictionary<int, double>? GapLossTangent { get; init; }

    /// <summary>Where the numbers came from, for the assumption lines a result prints
    /// ("from the stackup panel", "from the board stackup", "default").</summary>
    public string Source { get; init; } = "default";

    public double CopperThicknessOf(int layer) =>
        LayerThickness is not null && LayerThickness.TryGetValue(layer, out double t) && t > 0
            ? t : DefaultCopperThickness;

    public double GapThicknessOf(int upperLayer) =>
        GapThickness is not null && GapThickness.TryGetValue(upperLayer, out double t) && t > 0
            ? t : DefaultGapThickness;

    public double PermittivityOf(int upperLayer) =>
        GapPermittivity is not null && GapPermittivity.TryGetValue(upperLayer, out double e) && e > 0
            ? e : DefaultPermittivity;

    public double LossTangentOf(int upperLayer) =>
        GapLossTangent is not null && GapLossTangent.TryGetValue(upperLayer, out double t) && t >= 0
            ? t : DefaultLossTangent;

    /// <summary>
    /// The stackup a board carries by itself: the file's per-layer and per-gap values where
    /// it has them (IPC-2581), and for the rest the supplied defaults with the board
    /// thickness split evenly across the gaps. This is what an analysis uses when it is
    /// not handed a stackup — the library and test path; the application always hands one.
    /// </summary>
    public static BoardStackup FromBoard(PcbBoard board,
        double defaultBoardThickness = 1.6e-3, double defaultPermittivity = 4.4,
        double defaultLossTangent = 0.02, double defaultCopperThickness = 35e-6)
    {
        var file = board.Stackup;
        int layerCount = board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder) : 1;
        int gapCount = file is not null && file.DielectricGapThicknesses.Count > 0
            ? file.DielectricGapThicknesses.Count
            : Math.Max(0, layerCount - 1);

        var copper = new Dictionary<int, double>();
        var thickness = new Dictionary<int, double>();
        var permittivity = new Dictionary<int, double>();
        var loss = new Dictionary<int, double>();
        bool anyFromFile = false;
        if (file is not null)
        {
            for (int i = 0; i < file.CopperLayerThicknesses.Count; i++)
                copper[i + 1] = file.CopperLayerThicknesses[i];
            for (int i = 0; i < file.DielectricGapThicknesses.Count; i++)
            {
                thickness[i + 1] = file.DielectricGapThicknesses[i];
                anyFromFile = true;
            }
            for (int i = 0; i < file.DielectricGapPermittivities.Count; i++)
                permittivity[i + 1] = file.DielectricGapPermittivities[i];
            for (int i = 0; i < file.DielectricGapLossTangents.Count; i++)
                loss[i + 1] = file.DielectricGapLossTangents[i];
        }

        return new BoardStackup
        {
            DefaultCopperThickness = defaultCopperThickness,
            LayerThickness = copper,
            DefaultGapThickness = defaultBoardThickness / Math.Max(1, gapCount),
            GapThickness = thickness,
            DefaultPermittivity = defaultPermittivity,
            GapPermittivity = permittivity,
            DefaultLossTangent = defaultLossTangent,
            GapLossTangent = loss,
            Source = anyFromFile ? "from the board stackup" : "default"
        };
    }
}
