using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Meshing;

namespace OpenSim.App.ViewModels;

/// <summary>Mesh settings + generation for the generic (non-PCB) workflow. The PCB net
/// mesher reads <see cref="TargetEdgeLength"/> too, so both workflows share one knob.</summary>
public partial class MeshingViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private readonly IMeshGenerator _meshGenerator;

    public MeshingViewModel(ProjectSession session, ILogService log, IMeshGenerator meshGenerator)
    {
        _session = session;
        _log = log;
        _meshGenerator = meshGenerator;
        session.MeshChanged += (_, _) => RefreshMeshInfo();
        session.GeometryReplaced += (_, _) => PrefillDivisions();
        session.ActiveBodyChanged += (_, _) => PrefillDivisions();
    }

    /// <summary>The meshers offered by the picker, with the labels the user reads.</summary>
    public IReadOnlyList<MeshMethodOption> MeshMethods { get; } = new[]
    {
        new MeshMethodOption(MeshMethod.Delaunay, "Delaunay (any geometry)"),
        new MeshMethodOption(MeshMethod.StructuredLattice, "Structured lattice (box)")
    };

    /// <summary>The element families a structured lattice can build.</summary>
    public IReadOnlyList<ElementShapeOption> ElementShapes { get; } = new[]
    {
        new ElementShapeOption(ElementShape.Hexahedral, "Hexahedra (HEX20)"),
        new ElementShapeOption(ElementShape.Tetrahedral, "Tetrahedra (TET4 / TET10)")
    };

    [ObservableProperty] private double _targetEdgeLength; // 0 = auto
    [ObservableProperty] private bool _autoEdgeLength = true;
    [ObservableProperty] private string _meshInfo = "No mesh";

    /// <summary>Generate TET10 (quadratic) elements — fixes TET4's bending stiffness.
    /// Structural solves only; the electrical/thermal solvers require linear meshes.</summary>
    [ObservableProperty] private bool _quadraticElements;

    /// <summary>The mesher to run. Delaunay is the default, so nothing about an existing
    /// project changes until the user asks for something else.</summary>
    [ObservableProperty] private MeshMethod _meshMethod = MeshMethod.Delaunay;

    /// <summary>
    /// The element family a structured lattice builds. Hexahedra are the default HERE — the
    /// mapped mesher exists to reproduce what a commercial code lays on a block, and that is
    /// a 20-node hexahedron. It applies only to the lattice; Delaunay always builds
    /// tetrahedra, and a project saved with no shape at all still loads as tetrahedral.
    /// </summary>
    [ObservableProperty] private ElementShape _elementShape = ElementShape.Hexahedral;

    [ObservableProperty] private int _latticeNx = 10;
    [ObservableProperty] private int _latticeNy = 10;
    [ObservableProperty] private int _latticeNz = 10;

    /// <summary>Whether the lattice division boxes apply — they are meaningless to Delaunay.</summary>
    public bool IsStructured => MeshMethod == MeshMethod.StructuredLattice;

    partial void OnMeshMethodChanged(MeshMethod value)
    {
        OnPropertyChanged(nameof(IsStructured));
        OnPropertyChanged(nameof(QuadraticLabel));
        OnPropertyChanged(nameof(QuadraticOrderIsAChoice));
        if (value == MeshMethod.StructuredLattice) PrefillDivisions();
    }

    partial void OnElementShapeChanged(ElementShape value)
    {
        // HEX20 is the only hexahedron on offer, so choosing hexahedra chooses quadratic
        // order with it rather than leaving a combination the mesher would have to refuse.
        if (value == ElementShape.Hexahedral) QuadraticElements = true;
        OnPropertyChanged(nameof(QuadraticLabel));
        OnPropertyChanged(nameof(QuadraticOrderIsAChoice));
    }

    /// <summary>Names the element the current choices actually produce.</summary>
    public string QuadraticLabel => IsStructured && ElementShape == ElementShape.Hexahedral
        ? "Quadratic elements (HEX20)"
        : "Quadratic elements (TET10)";

    /// <summary>Hexahedra are HEX20 only, so the order checkbox is not the user's to clear
    /// there — it is shown ticked and disabled rather than silently overridden.</summary>
    public bool QuadraticOrderIsAChoice => !(IsStructured && ElementShape == ElementShape.Hexahedral);

    /// <summary>
    /// Fills the division boxes with what the current element size implies for this body, so
    /// switching to the lattice starts somewhere sensible rather than at a leftover number.
    /// It is also the only usable path on a large part: the Detail slider stops at 2 mm, far
    /// finer than a part measured in tens of centimetres can afford.
    /// </summary>
    private void PrefillDivisions()
    {
        if (_session.Body.Geometry is not { } geometry) return;
        var bounds = geometry.Bounds;
        double h = TargetEdgeLength > 0 ? TargetEdgeLength : bounds.Diagonal / 15.0;
        var size = bounds.Size;
        LatticeNx = StructuredLatticeMeshGenerator.DivisionsFor(size.X, h);
        LatticeNy = StructuredLatticeMeshGenerator.DivisionsFor(size.Y, h);
        LatticeNz = StructuredLatticeMeshGenerator.DivisionsFor(size.Z, h);
    }

    /// <summary>
    /// The settings a mesh run uses. <see cref="MeshSettings.Method"/> stays NULL for
    /// Delaunay rather than being set explicitly: null is what every project written before
    /// methods existed carries, so the default path and its saved form are the same thing.
    /// </summary>
    private MeshSettings BuildSettings(bool perBodyDivisions) => new()
    {
        TargetEdgeLength = TargetEdgeLength,
        ElementOrder = QuadraticElements ? ElementOrder.Quadratic : ElementOrder.Linear,
        Method = MeshMethod == MeshMethod.Delaunay ? null : MeshMethod,
        Divisions = MeshMethod == MeshMethod.StructuredLattice && !perBodyDivisions
            ? new LatticeDivisions(LatticeNx, LatticeNy, LatticeNz)
            : null,
        // Null for tetrahedra, exactly as Method is null for Delaunay: it is what every
        // project written before hexahedra existed carries.
        Shape = MeshMethod == MeshMethod.StructuredLattice && ElementShape == ElementShape.Hexahedral
            ? ElementShape.Hexahedral
            : null
    };

    /// <summary>Slider value [m]; setting it turns Auto off. Defaults to 0.3 mm when auto.</summary>
    public double EdgeLengthSlider
    {
        get => TargetEdgeLength > 0 ? TargetEdgeLength : 3e-4;
        set { AutoEdgeLength = false; TargetEdgeLength = value; }
    }

    public string EdgeLengthDisplay => AutoEdgeLength || TargetEdgeLength <= 0
        ? "auto"
        : $"{TargetEdgeLength * 1e3:g3} mm";

    partial void OnAutoEdgeLengthChanged(bool value)
    {
        if (value) TargetEdgeLength = 0;
        else if (TargetEdgeLength <= 0) TargetEdgeLength = 3e-4;
        OnPropertyChanged(nameof(EdgeLengthSlider));
        OnPropertyChanged(nameof(EdgeLengthDisplay));
    }

    partial void OnTargetEdgeLengthChanged(double value)
    {
        OnPropertyChanged(nameof(EdgeLengthSlider));
        OnPropertyChanged(nameof(EdgeLengthDisplay));
    }

    [RelayCommand]
    private async Task GenerateMeshAsync()
    {
        var body = _session.Body;
        if (body.Geometry is null)
        {
            _log.Append("Create or import geometry before meshing.");
            return;
        }
        _session.IsBusy = true;
        _session.StatusText = "Meshing…";
        try
        {
            var settings = BuildSettings(perBodyDivisions: false);
            body.MeshSettings = settings;
            var geometry = body.Geometry;
            var mesh = await Task.Run(() => _meshGenerator.Generate(geometry, settings));
            body.Mesh = mesh;

            _session.RaiseMeshChanged();
            _log.Append($"Mesh generated: {mesh.NodeCount} nodes, {mesh.ElementCount} tetrahedra.");
            _session.StatusText = "Mesh ready";
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally { _session.IsBusy = false; }
    }

    /// <summary>
    /// Meshes every body of the project with the current settings — an assembly solve needs
    /// all of them meshed, and meshing them one selection at a time is the same work done by
    /// hand. Each body is meshed on its OWN geometry (so "auto" edge length still follows
    /// each part's size); a failure names the body and leaves the rest alone.
    /// </summary>
    [RelayCommand]
    private async Task MeshAllBodiesAsync()
    {
        var bodies = _session.Bodies.ToList();
        if (bodies.Count == 0) return;

        // Divisions are derived per body here: one division triple cannot fit parts of
        // different sizes, and silently applying a 100-cell count to a 2 mm part would be
        // worse than deriving it from that part's own extent.
        var settings = BuildSettings(perBodyDivisions: true);
        _session.IsBusy = true;
        try
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                if (body.Role == BodyRole.FluidRegion)
                {
                    _log.Append($"Body '{body.Name}' is a fluid volume, not material — not meshed " +
                                "(it defines the flow domain instead).");
                    continue;
                }
                if (body.Geometry is not { } geometry)
                {
                    _log.Append($"Body '{body.Name}' has no geometry — skipped.");
                    continue;
                }
                _session.StatusText = $"Meshing {i + 1}/{bodies.Count}: {body.Name}…";
                _session.ProgressFraction = (double)i / bodies.Count;
                try
                {
                    body.MeshSettings = settings;
                    body.Mesh = await Task.Run(() => _meshGenerator.Generate(geometry, settings));
                    _log.Append($"Mesh '{body.Name}': {body.Mesh.NodeCount:N0} nodes, " +
                                $"{body.Mesh.ElementCount:N0} tetrahedra.");
                }
                catch (Exception ex)
                {
                    _log.Append($"Mesh '{body.Name}' failed: {ex.Message}");
                }
            }
            _session.RaiseMeshChanged();
            _session.StatusText = "Meshes ready";
        }
        finally
        {
            _session.IsBusy = false;
            _session.ProgressFraction = 0;
        }
    }

    /// <summary>Recomputes the info readout from the session body's mesh.</summary>
    private void RefreshMeshInfo()
    {
        var mesh = _session.Body.Mesh;
        if (mesh is null)
        {
            MeshInfo = "No mesh";
            return;
        }
        var stats = MeshQuality.Compute(mesh);
        MeshInfo = $"{stats.NodeCount} nodes, {stats.ElementCount} elements\n" +
                   $"volume {stats.TotalVolume:g4} m³\n" +
                   $"quality avg {stats.AverageQuality:f3}, min {stats.MinQuality:f4}\n" +
                   $"edges {stats.MinEdgeLength:g3}–{stats.MaxEdgeLength:g3} m";
    }
}
