using OpenSim.Core.Model;
using OpenSim.Pcb.Extrude;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Pcb.Import;

/// <summary>Options for importing a whole board archive.</summary>
public sealed record BoardImportOptions
{
    /// <summary>Target FE edge length [m]. 0 = auto (board diagonal / 40).</summary>
    public double TargetEdgeLength { get; init; }

    /// <summary>Stackup thicknesses.</summary>
    public PcbStackupSettings Stackup { get; init; } = new();

    public string BoardMaterialName { get; init; } = "FR4 (PCB laminate)";
    public string CopperMaterialName { get; init; } = "Copper (annealed)";

    /// <summary>
    /// Optional copper layer file to overlay as a conductive region on top of the board.
    /// Skipped with a warning if its cleaned outline exceeds <see cref="MaxCopperVertices"/>.
    /// </summary>
    public string? CopperLayerFile { get; init; }

    /// <summary>
    /// Complexity guard: copper layers above this cleaned-vertex count are not meshed.
    /// A full Altium plane/signal layer (thousands of disconnected features) is not
    /// something the conformal tet mesher can digest in reasonable time, so it is skipped
    /// with a warning and the board is meshed on its own.
    /// </summary>
    public int MaxCopperVertices { get; init; } = 2500;
}

/// <summary>The outcome of a board-archive import.</summary>
public sealed record BoardImportResult(
    Body Body,
    IReadOnlyList<BoardLayer> Layers,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Imports a full fabrication archive (ZIP or folder): classifies every layer, fills the
/// board profile into a solid domain, subtracts drilled holes, and meshes it. Copper
/// layers are parsed and reported; overlaying one as a conductive region is optional and
/// complexity-guarded, because a full Altium signal layer (tens of thousands of vertices)
/// is not something the tet mesher can conformally digest yet.
/// </summary>
public sealed class PcbBoardImporter
{
    private readonly IPolygonOps _ops = new ClipperPolygonOps();

    public BoardImportResult ImportArchive(string archivePath, BoardImportOptions? options = null)
    {
        options ??= new BoardImportOptions();
        var warnings = new List<string>();

        var files = PcbArchive.Read(archivePath);
        if (files.Count == 0)
            throw new InvalidOperationException("No Gerber or drill files were found in the archive.");
        var layers = files.Select(f => GerberLayerClassifier.Classify(f.Name, f.Text)).ToList();
        var byName = files.ToDictionary(f => f.Name, f => f.Text);

        // 1. Board outline from the Profile layer.
        var profile = layers.FirstOrDefault(l => l.Type == GerberLayerType.Profile);
        if (profile is null)
            throw new InvalidOperationException(
                "No board outline (Profile) layer was found. Include the profile/outline Gerber.");
        var board = BuildBoardRegion(byName[profile.FileName], warnings);
        double edge = options.TargetEdgeLength > 0 ? options.TargetEdgeLength : AutoEdge(board);

        // 2. Subtract drilled holes and routed slots — but only those the mesh can
        //    actually resolve. Features much narrower than the element size become
        //    degenerate sub-mesh loops that just wreck triangulation, so they are
        //    dropped (noted in the log). For a slot the WIDTH is what must resolve.
        var drillFeatures = layers.Where(l => l.Type == GerberLayerType.Drill)
            .Select(l => DrillExtractor.Extract(byName[l.FileName])).ToList();
        var allHoles = drillFeatures.SelectMany(f => f.Holes).ToList();
        var allSlots = drillFeatures.SelectMany(f => f.Slots).ToList();
        double minHole = edge;
        var cuts = allHoles.Where(h => h.Diameter >= minHole)
            .Select(h => ApertureShapes.Circle(h.Center, h.Diameter / 2, 5e-6))
            .Concat(allSlots.Where(s => s.Diameter >= minHole)
                .Select(s => ApertureShapes.Capsule(s.Start, s.End, s.Diameter / 2, 5e-6)))
            .ToList();
        if (cuts.Count > 0)
            board = _ops.Difference(board, cuts).ToList();
        string slotNote = allSlots.Count > 0
            ? $", {allSlots.Count(s => s.Diameter >= minHole)} of {allSlots.Count} slots"
            : "";
        warnings.Add($"Board: {board.Count} outline region(s); " +
                     $"{allHoles.Count(h => h.Diameter >= minHole)} of {allHoles.Count} drilled holes{slotNote} " +
                     $"subtracted (features below {minHole * 1e3:g2} mm dropped as sub-mesh).");

        // 3. Optionally overlay a copper layer as a conductive region.
        var body = new Body { Name = "PCB board" };
        var regionMaterials = new Dictionary<int, string>();
        var copperImage = ResolveCopperOverlay(options, layers, byName, warnings);

        PlanarMesh planar;
        if (copperImage is not null)
        {
            planar = new PlanarMesher().Mesh(new[]
            {
                new PlanarRegion(PcbStackup.CopperRegion, copperImage),                 // priority
                new PlanarRegion(PcbStackup.DielectricRegion, board)
            }, edge);
            body.Mesh = new PcbMeshGenerator().Generate(planar,
                PcbStackup.CopperOnBoard(options.Stackup.CopperThickness, options.Stackup.BoardThickness));
            body.Geometry = PolygonExtruder.Extrude(planar, PcbStackup.DielectricRegion, 0, options.Stackup.BoardThickness);
            regionMaterials[PcbStackup.CopperRegion] = options.CopperMaterialName;
            regionMaterials[PcbStackup.DielectricRegion] = options.BoardMaterialName;
        }
        else
        {
            // Board only: a single FR4 slab (region 0), meshable and thermally solvable.
            planar = new PlanarMesher().Mesh(new[] { new PlanarRegion(PcbStackup.CopperRegion, board) }, edge);
            body.Mesh = new PcbMeshGenerator().GenerateCopperOnly(planar, options.Stackup.BoardThickness);
            body.Geometry = PolygonExtruder.Extrude(planar, PcbStackup.CopperRegion, 0, options.Stackup.BoardThickness);
            regionMaterials[PcbStackup.CopperRegion] = options.BoardMaterialName;
        }

        body.RegionMaterialNames = regionMaterials;
        body.GeometrySource = $"PCB archive: {Path.GetFileName(archivePath)}";

        warnings.Add($"Meshed board: {body.Mesh.ElementCount} elements at {edge * 1e3:g3} mm edge length.");
        return new BoardImportResult(body, layers, warnings);
    }

    /// <summary>Fills the (usually stroked) profile outline into solid board polygons.</summary>
    private List<Polygon2> BuildBoardRegion(string profileText, List<string> warnings)
    {
        var doc = new GerberParser().Parse(profileText);
        var image = new LayerImageBuilder(_ops).Build(doc);
        if (image.Polygons.Count == 0)
            throw new InvalidOperationException("The board outline produced no geometry.");

        // A stroked outline is a thin ring (its net area is far below the enclosed area).
        // Fill it by keeping only outer contours and re-unioning; a genuinely filled
        // region passes through unchanged.
        var outerRings = image.Polygons.Select(p => (IReadOnlyList<Point2>)p.Outer).ToList();
        var filled = _ops.Union(outerRings).ToList();
        double ringArea = image.TotalArea();
        double filledArea = filled.Sum(p => p.Area());
        if (filledArea > ringArea * 2)
            warnings.Add($"Board outline was stroked; filled to {filledArea * 1e6:g4} mm² enclosed area.");
        return filled;
    }

    private IReadOnlyList<Polygon2>? ResolveCopperOverlay(BoardImportOptions options,
        List<BoardLayer> layers, Dictionary<string, string> byName, List<string> warnings)
    {
        if (options.CopperLayerFile is null) return null;
        var layer = layers.FirstOrDefault(l => l.FileName == options.CopperLayerFile);
        if (layer is null)
        {
            warnings.Add($"Copper layer '{options.CopperLayerFile}' not found in the archive; board only.");
            return null;
        }

        var doc = new GerberParser().Parse(byName[layer.FileName]);
        var image = new LayerImageBuilder(_ops).Build(doc);
        int verts = image.Polygons.Sum(p => p.Outer.Count + p.Holes.Sum(h => h.Count));
        if (verts > options.MaxCopperVertices)
        {
            warnings.Add($"Copper layer '{layer.FileName}' has {verts} vertices (> {options.MaxCopperVertices} cap); " +
                         "meshing board only. Simplify or pick a smaller layer to include copper.");
            return null;
        }
        return image.Polygons;
    }

    private static double AutoEdge(IReadOnlyList<Polygon2> polygons)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var poly in polygons)
            foreach (var p in poly.Outer)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
        double diag = Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2));
        return Math.Max(diag / 40, 2e-4);
    }
}
