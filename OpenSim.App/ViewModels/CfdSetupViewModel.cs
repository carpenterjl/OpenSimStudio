using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The CFD case: where the fluid domain is, what fluid fills it, how finely it is
/// gridded, and which holes are the inlet and the outlet.
/// <para>
/// Two domain shapes cover the workflows this product has. EXTERNAL wraps a box around
/// the bodies and lets the environment velocity set the face kinds — a part in a
/// draught, which is what <see cref="CfdSettings.ForExternalFlow"/> has always built.
/// INTERNAL fits the box to a body marked <see cref="BodyRole.FluidRegion"/> — a passage
/// bored through metal, where every face is a wall except the bore mouths. The mouths are
/// DETECTED from the fluid body's own geometry rather than typed, so the numbers cannot
/// drift away from the model on screen.
/// </para>
/// </summary>
public partial class CfdSetupViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;

    public CfdSetupViewModel(ProjectSession session, ILogService log)
    {
        _session = session;
        _log = log;
        FluidNames = FluidLibrary.All.Select(f => f.Name).ToList();
        _fluidName = FluidLibrary.Water.Name;
        _session.BodiesChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFluidBody));
            OnPropertyChanged(nameof(FluidBodyName));
        };
        // Opening a project must bring its CFD case back with it, the way the environment
        // panel already re-reads Project.Environment: a case that saves but does not
        // RELOAD is only half persisted, and the second run would silently use defaults.
        _session.GeometryReplaced += (_, _) => LoadFromProject();
        LoadFromProject();
    }

    /// <summary>Re-reads the project's stored CFD case into the panel.</summary>
    private void LoadFromProject()
    {
        var cfd = _session.Project.Cfd;
        if (cfd is null) return;
        DomainMode = cfd.Regime == FlowRegime.Internal ? 1 : 0;
        if (cfd.FluidName is { } name && FluidNames.Contains(name)) FluidName = name;
        Openings.Clear();
        for (int n = 0; n < cfd.Openings.Count; n++)
        {
            var opening = cfd.Openings[n];
            // A stored opening carries no detection provenance (the fluid body may not
            // even be in the file), so it is rebuilt as its own candidate: same face,
            // same rectangle, and an area taken from that rectangle rather than invented.
            double area = (opening.UMax - opening.UMin) * (opening.VMax - opening.VMin);
            var candidate = new FlowOpeningCandidate(opening.Face,
                opening.UMin, opening.UMax, opening.VMin, opening.VMax, area,
                CenterOf(opening), Math.Sqrt(4 * area / Math.PI));
            var row = new FlowOpeningRow(candidate, n + 1)
            {
                Role = opening.Kind == FlowFaceKind.InletVelocity ? 1 : 2,
                Speed = opening.Velocity.Length,
                Temperature = opening.Temperature ?? _session.Project.Environment?.AmbientTemperature ?? 293.15
            };
            Openings.Add(row);
        }
        if (Openings.Count > 0)
            Status = $"{Openings.Count} opening(s) restored from the project file.";
    }

    private static Vector3D CenterOf(FlowOpening opening)
    {
        double u = 0.5 * (opening.UMin + opening.UMax);
        double v = 0.5 * (opening.VMin + opening.VMax);
        return opening.Face switch
        {
            BoxFace.XMin or BoxFace.XMax => new Vector3D(0, u, v),
            BoxFace.YMin or BoxFace.YMax => new Vector3D(u, 0, v),
            _ => new Vector3D(u, v, 0)
        };
    }

    /// <summary>0 = external (a box around the bodies), 1 = internal (fitted to the fluid
    /// body). Internal is only meaningful once a body carries the fluid role.</summary>
    [ObservableProperty] private int _domainMode;

    /// <summary>Whether the internal-flow controls apply.</summary>
    public bool IsInternal => DomainMode == 1;

    partial void OnDomainModeChanged(int value) => OnPropertyChanged(nameof(IsInternal));

    /// <summary>The working fluid inside the domain, by library name. For an INTERNAL
    /// circuit this is genuinely a different fluid from the surroundings (water in the
    /// passage, air outside), which is the whole reason the two are separate settings.</summary>
    [ObservableProperty] private string _fluidName;

    /// <summary>How far the fitted domain reaches past the fluid body [mm], clamped to
    /// the solid bounds. Without a margin the passage would touch the domain wall along
    /// its tangent lines, and those cells would see an ADIABATIC box face instead of the
    /// metal — a silent hole in the heat path. Clamping is what keeps the bore mouths
    /// exactly on the box faces where the openings live.</summary>
    [ObservableProperty] private double _wallMarginMillimetres = 5;

    /// <summary>Detected ports the user assigns roles to.</summary>
    public ObservableCollection<FlowOpeningRow> Openings { get; } = new();

    /// <summary>The library fluids the picker offers.</summary>
    public IReadOnlyList<string> FluidNames { get; }

    /// <summary>Whether the project carries a body marked as a fluid volume.</summary>
    public bool HasFluidBody => FluidBody is not null;

    /// <summary>The fluid body's name, for the panel's status line.</summary>
    public string FluidBodyName => FluidBody?.Name ?? "none";

    private Body? FluidBody => _session.Bodies.FirstOrDefault(b => b.Role == BodyRole.FluidRegion);

    /// <summary>What the last detect/apply did, shown under the openings list.</summary>
    [ObservableProperty] private string _status =
        "Mark the fluid volume in the Bodies list, then detect its openings.";

    /// <summary>
    /// Finds every place the fluid body reaches the fitted domain box and lists them.
    /// </summary>
    [RelayCommand]
    private void DetectOpenings()
    {
        var fluid = FluidBody;
        if (fluid?.Geometry is null)
        {
            Status = "No fluid body: set a body's role to 'Fluid volume' in the Bodies list first.";
            _log.Append("CFD: " + Status);
            return;
        }
        if (ResolveDomain() is not { } domain)
        {
            Status = "No solid bodies to fit the domain against.";
            _log.Append("CFD: " + Status);
            return;
        }

        var found = FlowOpeningDetector.Detect(fluid.Geometry, domain);
        Openings.Clear();
        for (int n = 0; n < found.Count; n++) Openings.Add(new FlowOpeningRow(found[n], n + 1));

        if (found.Count == 0)
        {
            Status = "The fluid body does not reach any face of the domain box — a sealed " +
                     "passage has nothing to flow through. Check the wall margin.";
        }
        else
        {
            // The two ports furthest apart along the domain's longest axis are the natural
            // inlet/outlet pair; seeding them saves two clicks and is trivially overridden.
            Status = $"{found.Count} opening(s) found. Set one as the inlet and one as the outlet.";
            if (found.Count == 2)
            {
                Openings[0].Role = 1;
                Openings[1].Role = 2;
                Status = $"2 openings found: {found[0].Describe()} seeded as the INLET, " +
                         $"{found[1].Describe()} as the OUTLET.";
            }
        }
        _log.Append("CFD: " + Status);
        foreach (var row in Openings)
            _log.Append($"  {row.Label}, open area {row.Candidate.Area * 1e6:F0} mm².");
    }

    /// <summary>The domain box for the current mode, or null when it cannot be formed.</summary>
    public Aabb? ResolveDomain()
    {
        var solids = _session.Bodies.Where(b => b.Role == BodyRole.Solid && b.Geometry is not null)
            .ToList();
        if (solids.Count == 0) return null;
        var solidBounds = Union(solids.Select(b => b.Geometry!.Bounds));
        if (DomainMode == 0) return null;                 // external: the solver auto-fits

        var fluid = FluidBody;
        if (fluid?.Geometry is null) return null;
        // Expand for a wall of metal around the passage, then clamp to the solid: where
        // the passage already reaches the block's face (the bore mouths), the clamp pins
        // the domain face exactly there, which is where the openings must sit.
        double margin = Math.Max(0, WallMarginMillimetres) * 1e-3;
        return Intersect(fluid.Geometry.Bounds.Expanded(margin), solidBounds);
    }

    /// <summary>
    /// Writes the configured case onto the project. Returns null (with a logged reason)
    /// when the case is incomplete, so the caller can refuse the solve rather than run a
    /// silently wrong one.
    /// </summary>
    public CfdSettings? Build(double cellSize, Vector3D environmentVelocity)
    {
        var baseline = new CfdSettings { CellSize = cellSize };
        if (DomainMode == 0)
            return CfdSettings.ForExternalFlow(environmentVelocity, baseline);

        if (ResolveDomain() is not { } domain)
        {
            _log.Append("CFD: an internal-flow domain needs a body marked as the fluid volume " +
                        "and at least one solid body.");
            return null;
        }
        var openings = Openings.Select(o => o.Build()).OfType<FlowOpening>().ToList();
        if (openings.Count == 0)
        {
            _log.Append("CFD: no opening is assigned a role — detect the openings and set an " +
                        "inlet and an outlet. A sealed passage has no flow.");
            return null;
        }
        if (!openings.Any(o => o.Kind == FlowFaceKind.InletVelocity))
        {
            _log.Append("CFD: no inlet is assigned — nothing drives the flow.");
            return null;
        }
        if (!openings.Any(o => o.Kind == FlowFaceKind.OutletPressure))
        {
            _log.Append("CFD: no outlet is assigned — an inlet with nowhere to go is an " +
                        "incompressible impossibility, and the solver refuses it by name.");
            return null;
        }
        return CfdSettings.ForInternalFlow(domain, openings, FluidName, baseline);
    }

    private static Aabb Union(IEnumerable<Aabb> boxes)
    {
        var points = new List<Vector3D>();
        foreach (var b in boxes) { points.Add(b.Min); points.Add(b.Max); }
        return Aabb.FromPoints(points);
    }

    private static Aabb Intersect(Aabb a, Aabb b) => new(
        new Vector3D(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y),
            Math.Max(a.Min.Z, b.Min.Z)),
        new Vector3D(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y),
            Math.Min(a.Max.Z, b.Max.Z)));
}
