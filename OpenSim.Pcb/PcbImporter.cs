using OpenSim.Core.Model;
using OpenSim.Pcb.Excellon;
using OpenSim.Pcb.Extrude;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Pcb;

/// <summary>Inputs for a PCB import. Paths are optional except the copper layer.</summary>
public sealed record PcbImportRequest
{
    /// <summary>Copper layer Gerber file (RS-274X). Required.</summary>
    public required string CopperGerberPath { get; init; }

    /// <summary>Board outline Gerber file. When present, a dielectric region is meshed under the copper.</summary>
    public string? OutlineGerberPath { get; init; }

    /// <summary>Excellon drill file. When present, drilled holes are removed from the copper.</summary>
    public string? DrillPath { get; init; }

    /// <summary>Stackup thicknesses.</summary>
    public PcbStackupSettings Stackup { get; init; } = new();

    /// <summary>Target FE edge length [m]. 0 = auto (derived from the copper feature size).</summary>
    public double TargetEdgeLength { get; init; }

    /// <summary>Material-library names for the two regions.</summary>
    public string CopperMaterialName { get; init; } = "Copper (annealed)";
    public string BoardMaterialName { get; init; } = "FR4 (PCB laminate)";
}

/// <summary>The result of a PCB import: a ready-to-solve body plus any parser warnings.</summary>
public sealed record PcbImportResult(Body Body, IReadOnlyList<string> Warnings, PlanarMesh PlanarMesh);

/// <summary>
/// Reconstructs a meshed board body from Gerber/Excellon files: parse → copper image
/// (drills subtracted) → conformal planar triangulation → extruded tet mesh. With an
/// outline a multi-region copper-on-board mesh is produced (for coupled thermal); copper
/// only otherwise (all the DC electrical solve needs, avoiding the copper/FR4 σ spread).
/// </summary>
public sealed class PcbImporter
{
    public string FormatName => "PCB (Gerber + Excellon)";

    public PcbImportResult Import(PcbImportRequest request)
    {
        var warnings = new List<string>();
        var ops = new ClipperPolygonOps();

        var copperDoc = new GerberParser().ParseFile(request.CopperGerberPath);
        DrillFile? drills = request.DrillPath is null ? null : new ExcellonParser().ParseFile(request.DrillPath);
        var copperImage = new LayerImageBuilder(ops).Build(copperDoc, drills);
        warnings.AddRange(copperImage.Warnings);
        if (copperImage.Polygons.Count == 0)
            throw new InvalidOperationException("The copper layer contains no geometry after processing.");

        double edge = request.TargetEdgeLength > 0 ? request.TargetEdgeLength : AutoEdgeLength(copperImage);

        var body = new Body { Name = "PCB" };
        PlanarMesh planar;
        FeMesh mesh;

        if (request.OutlineGerberPath is null)
        {
            // Copper-only staging path.
            var region = new PlanarRegion(PcbStackup.CopperRegion, copperImage.Polygons);
            planar = new PlanarMesher().Mesh(new[] { region }, edge);
            mesh = new PcbMeshGenerator().GenerateCopperOnly(planar, request.Stackup.CopperThickness);
            body.Geometry = PolygonExtruder.Extrude(planar, PcbStackup.CopperRegion, 0, request.Stackup.CopperThickness);
            body.RegionMaterialNames = new Dictionary<int, string>
            {
                [PcbStackup.CopperRegion] = request.CopperMaterialName
            };
        }
        else
        {
            var outlineDoc = new GerberParser().ParseFile(request.OutlineGerberPath);
            var outlineImage = new LayerImageBuilder(ops).Build(outlineDoc);
            warnings.AddRange(outlineImage.Warnings);
            if (outlineImage.Polygons.Count == 0)
                throw new InvalidOperationException("The board outline contains no geometry.");

            var copperRegion = new PlanarRegion(PcbStackup.CopperRegion, copperImage.Polygons);
            var boardRegion = new PlanarRegion(PcbStackup.DielectricRegion, outlineImage.Polygons);
            planar = new PlanarMesher().Mesh(new[] { copperRegion, boardRegion }, edge);   // copper priority
            mesh = new PcbMeshGenerator().Generate(planar,
                PcbStackup.CopperOnBoard(request.Stackup.CopperThickness, request.Stackup.BoardThickness));
            body.Geometry = PolygonExtruder.Extrude(planar, PcbStackup.CopperRegion,
                request.Stackup.BoardThickness, request.Stackup.BoardThickness + request.Stackup.CopperThickness);
            body.RegionMaterialNames = new Dictionary<int, string>
            {
                [PcbStackup.CopperRegion] = request.CopperMaterialName,
                [PcbStackup.DielectricRegion] = request.BoardMaterialName
            };
        }

        body.Mesh = mesh;
        body.GeometrySource = $"PCB import: {Path.GetFileName(request.CopperGerberPath)}";
        return new PcbImportResult(body, warnings, planar);
    }

    /// <summary>Auto edge length: a fraction of the copper bounding-box diagonal, floored by feature size.</summary>
    private static double AutoEdgeLength(LayerImage image)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var polygon in image.Polygons)
            foreach (var p in polygon.Outer)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
        double diag = Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2));
        return Math.Max(diag / 40, 1e-4);
    }
}
