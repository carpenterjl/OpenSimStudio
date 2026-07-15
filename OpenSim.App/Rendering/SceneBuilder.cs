using System.Windows.Media;
using System.Windows.Media.Media3D;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Results;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.App.Rendering;

/// <summary>
/// Translates core geometry/mesh/result data into WPF 3D models. This is the only
/// place where simulation data meets the rendering stack.
/// </summary>
public static class SceneBuilder
{
    /// <summary>One selectable GeometryModel3D per geometric face, keyed by face id.</summary>
    public static Dictionary<int, GeometryModel3D> BuildFaceModels(TriangleMesh geometry, Color baseColor)
    {
        var byFace = new Dictionary<int, MeshGeometry3D>();
        for (int t = 0; t < geometry.Triangles.Count; t++)
        {
            int face = geometry.TriangleFaceIds[t];
            if (!byFace.TryGetValue(face, out var mesh))
                byFace[face] = mesh = new MeshGeometry3D();
            var tri = geometry.Triangles[t];
            foreach (int vi in new[] { tri.A, tri.B, tri.C })
            {
                var v = geometry.Vertices[vi];
                mesh.TriangleIndices.Add(mesh.Positions.Count);
                mesh.Positions.Add(new Point3D(v.X, v.Y, v.Z));
            }
        }

        var models = new Dictionary<int, GeometryModel3D>();
        foreach (var (face, mesh) in byFace)
        {
            mesh.Freeze();
            var material = new DiffuseMaterial(new SolidColorBrush(baseColor));
            models[face] = new GeometryModel3D(mesh, material) { BackMaterial = material };
        }
        return models;
    }

    /// <summary>Fallback copper-layer separation [m] when no stackup z is known.</summary>
    private const double LayerZSpacing = 1e-4;

    /// <summary>
    /// Distinct preview color per copper layer, so the viewport and the layers panel
    /// swatches agree on which outline sits on which layer. Cycles past 8 layers.
    /// </summary>
    public static Color LayerColor(int layerOrder) =>
        LayerPalette[((layerOrder - 1) % LayerPalette.Length + LayerPalette.Length) % LayerPalette.Length];

    private static readonly Color[] LayerPalette =
    {
        Color.FromRgb(0xE8, 0x8A, 0x3A),                         // L1: copper orange
        Color.FromRgb(0x4F, 0xA3, 0xE0),                         // L2: blue
        Color.FromRgb(0x7B, 0xC9, 0x6B),                         // L3: green
        Color.FromRgb(0xD0, 0x6B, 0xC9),                         // L4: magenta
        Color.FromRgb(0xE0, 0xD0, 0x4F),                         // L5: yellow
        Color.FromRgb(0x4F, 0xE0, 0xC9),                         // L6: teal
        Color.FromRgb(0xE0, 0x6B, 0x6B),                         // L7: red
        Color.FromRgb(0x9B, 0x8A, 0xE8),                         // L8: violet
    };

    /// <summary>
    /// Outline segments of copper islands for the full-board preview, one line pair per
    /// polygon edge. Each island sits at the mid-height of its copper layer from
    /// <paramref name="layerZ"/> (the same stackup z the mesher uses), so the preview
    /// shows layers where the mesh will actually be; without a map, an even fallback
    /// spacing with L1 on top. Rendering only outlines keeps a 1000-island board cheap.
    /// </summary>
    public static Point3DCollection BuildIslandOutlines(IEnumerable<CopperIsland> islands,
        IReadOnlyDictionary<int, double>? layerZ = null)
    {
        var pts = new Point3DCollection();
        foreach (var island in islands)
        {
            double z = layerZ is not null && layerZ.TryGetValue(island.LayerOrder, out double zi)
                ? zi
                : -island.LayerOrder * LayerZSpacing;            // fallback: L1 (top) highest
            AddRing(pts, island.Shape.Outer, z);
            foreach (var hole in island.Shape.Holes)
                AddRing(pts, hole, z);
        }
        return pts;
    }

    /// <summary>
    /// The copper-outline preview as one frozen ribbon mesh (display-only geometry —
    /// solvable copper is never touched). LinesVisual3D re-tessellates every segment on
    /// every camera move, which makes a 300k-segment board preview unusable; a frozen
    /// triangle mesh costs zero per-frame geometry work. Built at z = 0 so a per-layer
    /// transform can move it when the stackup is edited, and fully frozen so it can be
    /// constructed on the import thread and shared across recomposes.
    /// </summary>
    public static GeometryModel3D BuildIslandOutlineRibbons(IEnumerable<CopperIsland> islands,
        Color color, double halfWidth)
    {
        var mesh = new MeshGeometry3D();
        var positions = mesh.Positions;
        var indices = mesh.TriangleIndices;
        void AddRibbon(IReadOnlyList<Point2> ring)
        {
            foreach (var q in OutlineRibbon.Quads(ring, halfWidth))
            {
                int b = positions.Count;
                positions.Add(new Point3D(q.A0.X, q.A0.Y, 0));
                positions.Add(new Point3D(q.A1.X, q.A1.Y, 0));
                positions.Add(new Point3D(q.B1.X, q.B1.Y, 0));
                positions.Add(new Point3D(q.B0.X, q.B0.Y, 0));
                indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
                indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);
            }
        }
        foreach (var island in islands)
        {
            AddRibbon(island.Shape.Outer);
            foreach (var hole in island.Shape.Holes)
                AddRibbon(hole);
        }
        mesh.Freeze();

        // Emissive over black diffuse: the ribbon reads as a self-lit line, independent
        // of the scene lights, exactly like LinesVisual3D did.
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var material = new MaterialGroup
        {
            Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) }
        };
        material.Freeze();
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    /// <summary>Outline segments of the board profile at the given z (stack bottom).</summary>
    public static Point3DCollection BuildOutline(IEnumerable<Polygon2> outline, double z = 0)
    {
        var pts = new Point3DCollection();
        foreach (var polygon in outline)
        {
            AddRing(pts, polygon.Outer, z);
            foreach (var hole in polygon.Holes)
                AddRing(pts, hole, z);
        }
        return pts;
    }

    private static void AddRing(Point3DCollection pts, IReadOnlyList<Point2> ring, double z)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            pts.Add(new Point3D(a.X, a.Y, z));
            pts.Add(new Point3D(b.X, b.Y, z));
        }
    }

    /// <summary>Unique boundary edges of the FE mesh, for wireframe display.</summary>
    public static Point3DCollection BuildBoundaryEdges(FeMesh mesh)
    {
        var edges = new HashSet<(int, int)>();
        void Add(int a, int b) => edges.Add(a < b ? (a, b) : (b, a));
        foreach (var bt in mesh.BoundaryTriangles)
        {
            Add(bt.A, bt.B);
            Add(bt.B, bt.C);
            Add(bt.C, bt.A);
        }
        var points = new Point3DCollection(edges.Count * 2);
        foreach (var (a, b) in edges)
        {
            var pa = mesh.Nodes[a];
            var pb = mesh.Nodes[b];
            points.Add(new Point3D(pa.X, pa.Y, pa.Z));
            points.Add(new Point3D(pb.X, pb.Y, pb.Z));
        }
        return points;
    }

    /// <summary>
    /// Result of scalar-field preparation: per-node scalars plus the data min/max.
    /// Element fields are volume-averaged to nodes for smooth contours.
    /// </summary>
    public sealed record NodalScalars(double[] Values, double Min, double Max);

    /// <summary>
    /// The scalar interval mapped onto the colormap — usually the field's full
    /// [Min, Max], or a user-clamped Max that spends the whole gradient on the low
    /// range while values above it saturate at the top color. Skin, section cut and
    /// contours must all receive the SAME range so one legend describes them all.
    /// </summary>
    public readonly record struct ScalarRange(double Min, double Max)
    {
        /// <summary>Colormap coordinate, clamped to [0, 1] so out-of-range values saturate.</summary>
        public double Normalize(double value)
        {
            double range = Max - Min;
            if (range <= 0) return 0;
            return Math.Clamp((value - Min) / range, 0, 1);
        }
    }

    public static NodalScalars NodalizeField(FeMesh mesh, IResultField field)
    {
        double[] values;
        if (field.Location == FieldLocation.Node)
        {
            values = new double[mesh.NodeCount];
            for (int i = 0; i < mesh.NodeCount; i++)
                values[i] = field.GetScalar(i);
        }
        else
        {
            values = new double[mesh.NodeCount];
            var weight = new double[mesh.NodeCount];
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                double v = field.GetScalar(e);
                double w = Math.Abs(mesh.ElementVolume(e));
                foreach (int n in mesh.GetElementNodes(e))   // incl. mids when quadratic
                {
                    values[n] += v * w;
                    weight[n] += w;
                }
            }
            for (int i = 0; i < values.Length; i++)
                if (weight[i] > 0)
                    values[i] /= weight[i];
        }

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double v in values)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        if (min > max) (min, max) = (0, 0);
        return new NodalScalars(values, min, max);
    }

    /// <summary>
    /// The boundary skin colored by a nodal scalar (mapped through the colormap
    /// texture), optionally deformed by a displacement field, and optionally clipped
    /// by a section plane (whole triangles beyond it are hidden — the ragged edge is
    /// covered by the colored cut face from <see cref="BuildSectionModel"/>).
    /// </summary>
    public static GeometryModel3D BuildResultModel(FeMesh mesh, NodalScalars scalars,
        ScalarRange displayRange, ColormapKind colormap, NodalVectorField? displacement,
        double deformScale, SectionPlane? clip = null)
    {
        var geometry3D = new MeshGeometry3D();

        var deformed = DeformedNodes(mesh, displacement, deformScale);
        var dispVectors = DeformedDisplacement(mesh, displacement, deformScale);
        var positions = new Point3D[mesh.NodeCount];
        for (int i = 0; i < mesh.NodeCount; i++)
            positions[i] = new Point3D(deformed[i].X, deformed[i].Y, deformed[i].Z);

        // Shared vertices keep the color interpolation continuous across triangles.
        var nodeToVertex = new Dictionary<int, int>();
        int MapNode(int n)
        {
            if (!nodeToVertex.TryGetValue(n, out int idx))
            {
                idx = geometry3D.Positions.Count;
                geometry3D.Positions.Add(positions[n]);
                double u = displayRange.Normalize(scalars.Values[n]);
                geometry3D.TextureCoordinates.Add(new System.Windows.Point(u, 0.5));
                nodeToVertex[n] = idx;
            }
            return idx;
        }

        foreach (var bt in mesh.BoundaryTriangles)
        {
            if (clip is { } plane
                && !SectionCutter.IsTriangleVisible(mesh, bt, plane, dispVectors, 1.0))
                continue;
            geometry3D.TriangleIndices.Add(MapNode(bt.A));
            geometry3D.TriangleIndices.Add(MapNode(bt.B));
            geometry3D.TriangleIndices.Add(MapNode(bt.C));
        }
        geometry3D.Freeze();

        var brush = Colormap.CreateBrush(colormap);
        var material = new DiffuseMaterial(brush);
        return new GeometryModel3D(geometry3D, material) { BackMaterial = material };
    }

    /// <summary>
    /// The section-plane cross-section, colored through the SAME display-range
    /// normalization and colormap as the skin so one legend describes both.
    /// </summary>
    public static GeometryModel3D BuildSectionModel(FeMesh mesh, NodalScalars scalars,
        ScalarRange displayRange, ColormapKind colormap, NodalVectorField? displacement,
        double deformScale, SectionPlane plane)
    {
        var cut = SectionCutter.Cut(mesh, plane, scalars.Values,
            DeformedDisplacement(mesh, displacement, deformScale), 1.0);

        var geometry3D = new MeshGeometry3D();
        void AddVertex(OpenSim.Core.Numerics.Vector3D p, double s)
        {
            geometry3D.TriangleIndices.Add(geometry3D.Positions.Count);
            geometry3D.Positions.Add(new Point3D(p.X, p.Y, p.Z));
            geometry3D.TextureCoordinates.Add(new System.Windows.Point(displayRange.Normalize(s), 0.5));
        }
        foreach (var t in cut)
        {
            AddVertex(t.P0, t.S0);
            AddVertex(t.P1, t.S1);
            AddVertex(t.P2, t.S2);
        }
        geometry3D.Freeze();

        var material = new DiffuseMaterial(Colormap.CreateBrush(colormap));
        return new GeometryModel3D(geometry3D, material) { BackMaterial = material };
    }

    /// <summary>Contour line segments on the (optionally clipped) skin as point pairs.
    /// Levels span <paramref name="displayRange"/>, not the data range, so contours
    /// redistribute with the user's colormap clamp.</summary>
    public static Point3DCollection BuildContourSegments(FeMesh mesh, NodalScalars scalars,
        ScalarRange displayRange, NodalVectorField? displacement, double deformScale,
        int levelCount, SectionPlane? clip)
    {
        var segments = IsoLineExtractor.Extract(mesh, scalars.Values,
            DeformedDisplacement(mesh, displacement, deformScale), 1.0,
            levelCount, displayRange.Min, displayRange.Max, clip);
        var points = new Point3DCollection(segments.Count * 2);
        foreach (var (a, b) in segments)
        {
            points.Add(new Point3D(a.X, a.Y, a.Z));
            points.Add(new Point3D(b.X, b.Y, b.Z));
        }
        points.Freeze();
        return points;
    }

    // ------------------------------------------------------------------
    // RF / antenna visuals: free-space field maps and radiation lobes. These are not
    // FeMesh results — the sampling grid lives in air — but they use the SAME colormap
    // brush + texture-coordinate scheme, so the legend infrastructure applies as-is.
    // ------------------------------------------------------------------

    /// <summary>|E| spans decades: colormap coordinate u = 1 + log₁₀(v/max)/decades,
    /// clamped — the top color is the field maximum, each decade below slides down.</summary>
    private static double LogNormalize(double value, double max, int decades)
    {
        if (value <= 0 || max <= 0) return 0;
        return Math.Clamp(1 + Math.Log10(value / max) / decades, 0, 1);
    }

    /// <summary>
    /// The near-field vector map as one batched arrow mesh: every sample point gets an
    /// arrow along the t = 0 field snapshot Re(E), sized by log-scaled |E| (peak phasor)
    /// and colored through the same colormap texture as FE results. One frozen
    /// MeshGeometry3D holds all arrows — thousands of Visual3Ds would melt the viewport.
    /// Samples beyond <paramref name="maxArrows"/> are strided deterministically.
    /// </summary>
    public static GeometryModel3D BuildVectorFieldModel(OpenSim.Rf.FieldMap map,
        ColormapKind colormap, double arrowLength, int decades = 3, int maxArrows = 8192)
    {
        double max = map.Magnitude.Count > 0 ? map.Magnitude.Max() : 0;
        int stride = Math.Max(1, (map.Points.Count + maxArrows - 1) / maxArrows);

        var geometry3D = new MeshGeometry3D();
        for (int i = 0; i < map.Points.Count; i += stride)
        {
            var direction = map.Snapshot[i];
            if (direction.Length <= 0 || map.Magnitude[i] <= 0) continue;
            double u = LogNormalize(map.Magnitude[i], max, decades);
            // A floor keeps the weakest drawn arrows legible; color carries the value.
            double length = arrowLength * (0.15 + 0.85 * u);
            AddArrow(geometry3D, map.Points[i], direction.Normalized(), length, u);
        }
        geometry3D.Freeze();
        var material = new DiffuseMaterial(Colormap.CreateBrush(colormap));
        return new GeometryModel3D(geometry3D, material) { BackMaterial = material };
    }

    /// <summary>
    /// A planar |E| heatmap: the probe samples a row-major (nx × ny) grid on an axis
    /// slice; adjacent samples become shared-vertex quads so the color interpolates
    /// continuously, log-normalized like the arrows (one legend for both).
    /// </summary>
    public static GeometryModel3D BuildFieldSliceModel(OpenSim.Rf.FieldMap map,
        int nx, int ny, ColormapKind colormap, int decades = 3)
    {
        if (map.Points.Count != nx * ny)
            throw new ArgumentException($"Field map has {map.Points.Count} points, expected {nx}×{ny}.");
        double max = map.Magnitude.Count > 0 ? map.Magnitude.Max() : 0;

        var geometry3D = new MeshGeometry3D();
        for (int i = 0; i < map.Points.Count; i++)
        {
            var p = map.Points[i];
            geometry3D.Positions.Add(new Point3D(p.X, p.Y, p.Z));
            geometry3D.TextureCoordinates.Add(new System.Windows.Point(
                LogNormalize(map.Magnitude[i], max, decades), 0.5));
        }
        for (int y = 0; y < ny - 1; y++)
            for (int x = 0; x < nx - 1; x++)
            {
                int v00 = y * nx + x, v10 = v00 + 1, v01 = v00 + nx, v11 = v01 + 1;
                geometry3D.TriangleIndices.Add(v00);
                geometry3D.TriangleIndices.Add(v10);
                geometry3D.TriangleIndices.Add(v11);
                geometry3D.TriangleIndices.Add(v00);
                geometry3D.TriangleIndices.Add(v11);
                geometry3D.TriangleIndices.Add(v01);
            }
        geometry3D.Freeze();
        var material = new DiffuseMaterial(Colormap.CreateBrush(colormap));
        return new GeometryModel3D(geometry3D, material) { BackMaterial = material };
    }

    /// <summary>
    /// The board field overlay (SIwave-style): a semi-transparent |field| heatmap plane
    /// hovering over the PCB / structure, colored through the shared colormap brush with
    /// a user-controlled <see cref="OpenSim.Core.PostProcessing.FieldScale"/> (linear or
    /// log, auto or explicit range) and opacity. Same shared-vertex quad mesh as the
    /// slice heatmap; the copper ribbons underneath stay opaque, so the overlay reads as
    /// a translucent film over the board. Frozen; both faces drawn.
    /// </summary>
    /// <param name="values">The per-point scalar to color by — |E| or |H| picked by the
    /// caller (the map carries both since Stage S7); must be parallel to
    /// <see cref="OpenSim.Rf.FieldMap.Points"/>.</param>
    public static GeometryModel3D BuildFieldOverlayModel(OpenSim.Rf.FieldMap map,
        IReadOnlyList<double> values, int nx, int ny, ColormapKind colormap,
        OpenSim.Core.PostProcessing.FieldScale scale, double opacity)
    {
        if (map.Points.Count != nx * ny)
            throw new ArgumentException($"Field map has {map.Points.Count} points, expected {nx}×{ny}.");

        var geometry3D = new MeshGeometry3D();
        for (int i = 0; i < map.Points.Count; i++)
        {
            var p = map.Points[i];
            geometry3D.Positions.Add(new Point3D(p.X, p.Y, p.Z));
            geometry3D.TextureCoordinates.Add(new System.Windows.Point(
                scale.Normalize(values[i]), 0.5));
        }
        for (int y = 0; y < ny - 1; y++)
            for (int x = 0; x < nx - 1; x++)
            {
                int v00 = y * nx + x, v10 = v00 + 1, v01 = v00 + nx, v11 = v01 + 1;
                geometry3D.TriangleIndices.Add(v00);
                geometry3D.TriangleIndices.Add(v10);
                geometry3D.TriangleIndices.Add(v11);
                geometry3D.TriangleIndices.Add(v00);
                geometry3D.TriangleIndices.Add(v11);
                geometry3D.TriangleIndices.Add(v01);
            }
        geometry3D.Freeze();

        // The shared gradient brush, made translucent: Brush.Opacity multiplies every
        // stop's alpha uniformly, so the colormap keeps its hue ramp under transparency.
        var brush = Colormap.CreateBrush(colormap).Clone();
        brush.Opacity = Math.Clamp(opacity, 0, 1);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        var model = new GeometryModel3D(geometry3D, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    /// <summary>
    /// A board field overlay masked to the outline (SI Stage S10): the same textured quad
    /// heatmap as <see cref="BuildFieldOverlayModel"/>, but only the lattice cells whose four
    /// corners all sit inside the board are painted (<paramref name="inside"/> from
    /// <see cref="OpenSim.Core.PostProcessing.OverlayGrid.InteriorMask"/>). Composed one per
    /// copper-layer z into a <see cref="Model3DGroup"/> so the field reads over each layer's
    /// copper. The colormap <paramref name="scale"/> is shared across layers (pooled range)
    /// so colors compare layer-to-layer.
    /// </summary>
    public static GeometryModel3D BuildMaskedFieldOverlayModel(OpenSim.Rf.FieldMap map,
        IReadOnlyList<double> values, int nx, int ny, bool[] inside, ColormapKind colormap,
        OpenSim.Core.PostProcessing.FieldScale scale, double opacity)
    {
        if (map.Points.Count != nx * ny)
            throw new ArgumentException($"Field map has {map.Points.Count} points, expected {nx}×{ny}.");
        if (inside.Length != nx * ny)
            throw new ArgumentException($"Mask has {inside.Length} entries, expected {nx}×{ny}.");

        var geometry3D = new MeshGeometry3D();
        for (int i = 0; i < map.Points.Count; i++)
        {
            var p = map.Points[i];
            geometry3D.Positions.Add(new Point3D(p.X, p.Y, p.Z));
            geometry3D.TextureCoordinates.Add(new System.Windows.Point(scale.Normalize(values[i]), 0.5));
        }
        foreach (var (v00, v10, v11, v01) in
                 OpenSim.Core.PostProcessing.OverlayGrid.InteriorQuads(inside, nx, ny))
        {
            geometry3D.TriangleIndices.Add(v00);
            geometry3D.TriangleIndices.Add(v10);
            geometry3D.TriangleIndices.Add(v11);
            geometry3D.TriangleIndices.Add(v00);
            geometry3D.TriangleIndices.Add(v11);
            geometry3D.TriangleIndices.Add(v01);
        }
        geometry3D.Freeze();

        var brush = Colormap.CreateBrush(colormap).Clone();
        brush.Opacity = Math.Clamp(opacity, 0, 1);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        var model = new GeometryModel3D(geometry3D, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    /// <summary>The RWG surface colored by log₁₀|J| at each triangle centroid over
    /// three decades (the near-field arrows' normalization, one mental legend for
    /// both). Flat-shaded — vertices are duplicated per triangle so each carries one
    /// texture coordinate into the colormap brush; frozen, both faces drawn.</summary>
    public static GeometryModel3D BuildSurfaceCurrentModel(
        OpenSim.Rf.Surface.SurfaceStructure surface,
        OpenSim.Rf.Surface.SurfaceMomSolution solution, ColormapKind colormap)
    {
        const int decades = 3;
        int count = surface.Triangles.Count;
        var magnitudes = new double[count];
        double peak = 0;
        for (int t = 0; t < count; t++)
        {
            var centroid = surface.TriangleCentroids[t];
            double area = surface.TriangleAreas[t];
            System.Numerics.Complex jx = default, jy = default, jz = default;
            foreach (var (basis, sign, opposite) in surface.TriangleSupports[t])
            {
                var coefficient = solution.EdgeCurrents[basis]
                    * (sign * surface.Edges[basis].Length / (2 * area));
                var rho = centroid - surface.Vertices[opposite];
                jx += coefficient * rho.X;
                jy += coefficient * rho.Y;
                jz += coefficient * rho.Z;
            }
            magnitudes[t] = Math.Sqrt(jx.Magnitude * jx.Magnitude
                + jy.Magnitude * jy.Magnitude + jz.Magnitude * jz.Magnitude);
            peak = Math.Max(peak, magnitudes[t]);
        }
        if (peak <= 0) peak = 1;

        var geometry3D = new MeshGeometry3D();
        for (int t = 0; t < count; t++)
        {
            double normalized = magnitudes[t] <= 0
                ? 0
                : Math.Clamp(1 + Math.Log10(magnitudes[t] / peak) / decades, 0, 1);
            var (a, b, c) = surface.Triangles[t];
            foreach (var v in new[] { surface.Vertices[a], surface.Vertices[b], surface.Vertices[c] })
            {
                geometry3D.TriangleIndices.Add(geometry3D.Positions.Count);
                geometry3D.Positions.Add(new Point3D(v.X, v.Y, v.Z));
                geometry3D.TextureCoordinates.Add(new System.Windows.Point(normalized, 0.5));
            }
        }
        geometry3D.Freeze();
        var material = new DiffuseMaterial(Colormap.CreateBrush(colormap));
        material.Freeze();
        var model = new GeometryModel3D(geometry3D, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    /// <summary>A translucent disk marking the antenna solver's infinite PEC ground
    /// plane — the modeling assumption made visible. The disk is display-only (the
    /// plane itself is infinite and never meshed); frozen, both faces drawn.</summary>
    public static GeometryModel3D BuildGroundPlaneModel(double centerX, double centerY,
        double surfaceZ, double radius)
    {
        const int segments = 48;
        var geometry3D = new MeshGeometry3D();
        geometry3D.Positions.Add(new Point3D(centerX, centerY, surfaceZ));
        for (int i = 0; i < segments; i++)
        {
            double angle = 2 * Math.PI * i / segments;
            geometry3D.Positions.Add(new Point3D(
                centerX + radius * Math.Cos(angle),
                centerY + radius * Math.Sin(angle),
                surfaceZ));
        }
        for (int i = 0; i < segments; i++)
        {
            geometry3D.TriangleIndices.Add(0);
            geometry3D.TriangleIndices.Add(1 + i);
            geometry3D.TriangleIndices.Add(1 + (i + 1) % segments);
        }
        geometry3D.Freeze();

        var brush = new SolidColorBrush(Color.FromArgb(56, 0x7B, 0xA0, 0x5B));
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        var model = new GeometryModel3D(geometry3D, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    /// <summary>
    /// The far-field lobe: a spherical surface r(θ,φ) = scale·U/U_max around the
    /// structure center, colored by the same normalized gain. The pattern's θ grid
    /// stops short of the poles (Gauss nodes), so pole caps close the surface with the
    /// adjacent ring's average — for a dipole both poles are nulls and collapse to the
    /// center, which is exactly what the doughnut should do.
    /// </summary>
    public static GeometryModel3D BuildFarFieldLobe(OpenSim.Rf.FarFieldPattern pattern,
        OpenSim.Core.Numerics.Vector3D center, double scale, ColormapKind colormap)
    {
        int thetaCount = pattern.ThetaRadians.Count;
        int phiCount = pattern.PhiRadians.Count;
        double max = 0;
        foreach (double u in pattern.IntensityWattsPerSteradian) max = Math.Max(max, u);
        if (max <= 0) max = 1;

        var geometry3D = new MeshGeometry3D();
        int Vertex(double theta, double phi, double gain)
        {
            double r = scale * gain;
            geometry3D.Positions.Add(new Point3D(
                center.X + r * Math.Sin(theta) * Math.Cos(phi),
                center.Y + r * Math.Sin(theta) * Math.Sin(phi),
                center.Z + r * Math.Cos(theta)));
            geometry3D.TextureCoordinates.Add(new System.Windows.Point(gain, 0.5));
            return geometry3D.Positions.Count - 1;
        }

        var ringStart = new int[thetaCount];
        for (int ti = 0; ti < thetaCount; ti++)
        {
            ringStart[ti] = geometry3D.Positions.Count;
            for (int pi = 0; pi < phiCount; pi++)
                Vertex(pattern.ThetaRadians[ti], pattern.PhiRadians[pi],
                    pattern.IntensityWattsPerSteradian[ti, pi] / max);
        }
        void Quad(int a, int b, int c, int d)
        {
            geometry3D.TriangleIndices.Add(a);
            geometry3D.TriangleIndices.Add(b);
            geometry3D.TriangleIndices.Add(c);
            geometry3D.TriangleIndices.Add(a);
            geometry3D.TriangleIndices.Add(c);
            geometry3D.TriangleIndices.Add(d);
        }
        for (int ti = 0; ti < thetaCount - 1; ti++)
            for (int pi = 0; pi < phiCount; pi++)
            {
                int next = (pi + 1) % phiCount;
                Quad(ringStart[ti] + pi, ringStart[ti] + next,
                     ringStart[ti + 1] + next, ringStart[ti + 1] + pi);
            }

        // Pole caps: Gauss θ nodes are ordered from θ ≈ π (u = −1) to θ ≈ 0.
        double CapGain(int ring)
        {
            double sum = 0;
            for (int pi = 0; pi < phiCount; pi++)
                sum += pattern.IntensityWattsPerSteradian[ring, pi] / max;
            return sum / phiCount;
        }
        int south = Vertex(Math.PI, 0, CapGain(0));
        int north = Vertex(0, 0, CapGain(thetaCount - 1));
        for (int pi = 0; pi < phiCount; pi++)
        {
            int next = (pi + 1) % phiCount;
            geometry3D.TriangleIndices.Add(south);
            geometry3D.TriangleIndices.Add(ringStart[0] + next);
            geometry3D.TriangleIndices.Add(ringStart[0] + pi);
            geometry3D.TriangleIndices.Add(north);
            geometry3D.TriangleIndices.Add(ringStart[thetaCount - 1] + pi);
            geometry3D.TriangleIndices.Add(ringStart[thetaCount - 1] + next);
        }
        geometry3D.Freeze();
        var material = new DiffuseMaterial(Colormap.CreateBrush(colormap));
        return new GeometryModel3D(geometry3D, material) { BackMaterial = material };
    }

    /// <summary>The antenna wires as line pairs for a LinesVisual3D preview.</summary>
    public static Point3DCollection BuildWirePath(IReadOnlyList<OpenSim.Rf.WireSegment> wires)
    {
        var points = new Point3DCollection(wires.Count * 2);
        foreach (var wire in wires)
        {
            points.Add(new Point3D(wire.A.X, wire.A.Y, wire.A.Z));
            points.Add(new Point3D(wire.B.X, wire.B.Y, wire.B.Z));
        }
        points.Freeze();
        return points;
    }

    /// <summary>One arrow (4-sided shaft prism + pyramid head) appended to the shared
    /// mesh, colored by texture coordinate <paramref name="u"/>.</summary>
    private static void AddArrow(MeshGeometry3D geometry3D, OpenSim.Core.Numerics.Vector3D origin,
        OpenSim.Core.Numerics.Vector3D direction, double length, double u)
    {
        // Orthonormal frame around the arrow axis.
        var axis = direction;
        var reference = Math.Abs(axis.Z) < 0.9
            ? OpenSim.Core.Numerics.Vector3D.UnitZ
            : OpenSim.Core.Numerics.Vector3D.UnitX;
        var side = OpenSim.Core.Numerics.Vector3D.Cross(axis, reference).Normalized();
        var up = OpenSim.Core.Numerics.Vector3D.Cross(axis, side);

        double shaftLength = 0.7 * length, shaftRadius = 0.045 * length, headRadius = 0.12 * length;
        var basePoint = origin - axis * (length / 2);            // centered on the sample
        var neck = basePoint + axis * shaftLength;
        var tip = basePoint + axis * length;

        int start = geometry3D.Positions.Count;
        void Add(OpenSim.Core.Numerics.Vector3D p)
        {
            geometry3D.Positions.Add(new Point3D(p.X, p.Y, p.Z));
            geometry3D.TextureCoordinates.Add(new System.Windows.Point(u, 0.5));
        }
        // 0..3 shaft base ring, 4..7 shaft neck ring, 8..11 head ring, 12 tip, 13 base center.
        foreach (var ring in new[] { (basePoint, shaftRadius), (neck, shaftRadius), (neck, headRadius) })
            for (int i = 0; i < 4; i++)
            {
                double angle = Math.PI * (2 * i + 1) / 4;
                Add(ring.Item1 + side * (ring.Item2 * Math.Cos(angle)) + up * (ring.Item2 * Math.Sin(angle)));
            }
        Add(tip);
        Add(basePoint);

        void Tri(int a, int b, int c)
        {
            geometry3D.TriangleIndices.Add(start + a);
            geometry3D.TriangleIndices.Add(start + b);
            geometry3D.TriangleIndices.Add(start + c);
        }
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            Tri(i, next, 4 + next);                               // shaft side
            Tri(i, 4 + next, 4 + i);
            Tri(8 + i, 8 + next, 12);                             // head side to tip
            Tri(13, next, i);                                     // base cap
            Tri(4 + i, 4 + next, 8 + next);                       // neck ring to head ring
            Tri(4 + i, 8 + next, 8 + i);
        }
    }

    /// <summary>Node positions with the displacement (× scale) applied.</summary>
    private static OpenSim.Core.Numerics.Vector3D[] DeformedNodes(FeMesh mesh,
        NodalVectorField? displacement, double deformScale)
    {
        var result = new OpenSim.Core.Numerics.Vector3D[mesh.NodeCount];
        for (int i = 0; i < mesh.NodeCount; i++)
            result[i] = displacement is null
                ? mesh.Nodes[i]
                : mesh.Nodes[i] + displacement.GetVector(i) * deformScale;
        return result;
    }

    /// <summary>
    /// Pre-scaled displacement vectors for the Core post-processing helpers (they take
    /// displacement + scale; passing the scaled vectors with scale 1 keeps one source
    /// of truth for the deformation here). Null when there is no displacement.
    /// </summary>
    private static OpenSim.Core.Numerics.Vector3D[]? DeformedDisplacement(FeMesh mesh,
        NodalVectorField? displacement, double deformScale)
    {
        if (displacement is null) return null;
        var result = new OpenSim.Core.Numerics.Vector3D[mesh.NodeCount];
        for (int i = 0; i < mesh.NodeCount; i++)
            result[i] = displacement.GetVector(i) * deformScale;
        return result;
    }
}
