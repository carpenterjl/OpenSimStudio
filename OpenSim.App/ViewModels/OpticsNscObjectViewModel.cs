using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Core.Model;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Editing;

namespace OpenSim.App.ViewModels;

/// <summary>A drawing resolution of the single-object viewer: rings and segments of its mesh.
/// OpticStudio's Draw section names Standard, Medium, High and Presentation (help, "Draw (object
/// properties, non-sequential component editor)") without giving counts; the counts are this
/// program's.</summary>
public sealed record NscDrawResolution(string Name, int Rings, int Segments)
{
    public static IReadOnlyList<NscDrawResolution> All { get; } = new NscDrawResolution[]
    {
        new("Standard", 8, 24), new("Medium", 16, 48), new("High", 32, 96), new("Presentation", 64, 192),
    };

    public override string ToString() => Name;
}

/// <summary>
/// View Current Object (plan item OP-71; help, "View Current Object": "a simplified version of
/// the NSC Shaded Model, and is for viewing one object at a time"): one object's mesh in its own
/// frame, lens units, each face its own colour. The draw settings it honours are the viewer's own
/// (resolution, opacity, local axes) and are not saved: there is no non-sequential layout plot
/// yet to read them. Picking a face by clicking it is not built.
/// </summary>
public sealed partial class OpticsNscObjectViewModel : ObservableObject
{
    private readonly NscGeometry _geometry;

    public OpticsNscObjectViewModel(int objectNumber, NscObjectTypeDescriptor type, NscGeometry geometry, string? comment)
    {
        ObjectNumber = objectNumber;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        Comment = comment;
        _resolution = NscDrawResolution.All[1];
        Mesh = NscObjectMesher.Mesh(_geometry, _resolution.Rings, _resolution.Segments);
    }

    public int ObjectNumber { get; }
    public NscObjectTypeDescriptor Type { get; }
    public string? Comment { get; }

    /// <summary>"Object 3: Standard Lens (comment)".</summary>
    public string Title => $"Object {ObjectNumber}: {Type.TypeName}{(string.IsNullOrWhiteSpace(Comment) ? "" : $" ({Comment})")}";

    /// <summary>The faces, "Face 1: Front", in face order.</summary>
    public IReadOnlyList<string> Faces => Type.Faces.OrderBy(f => f.Key).Select(f => $"Face {f.Key}: {f.Value}").ToArray();

    /// <summary>The mesh, object frame, lens units; face ids are face numbers.</summary>
    [ObservableProperty] private TriangleMesh _mesh = null!;

    public IReadOnlyList<NscDrawResolution> Resolutions => NscDrawResolution.All;

    [ObservableProperty] private NscDrawResolution _resolution;

    /// <summary>Opacity in percent (help: below 100 % the object is partly transparent).</summary>
    [ObservableProperty] private double _opacityPercent = 100;

    /// <summary>Draw the local x, y and z axes at the local vertex (help, "Draw Local Axis").</summary>
    [ObservableProperty] private bool _showLocalAxes = true;

    partial void OnResolutionChanged(NscDrawResolution value)
    {
        if (value is null) return;
        Mesh = NscObjectMesher.Mesh(_geometry, value.Rings, value.Segments);
    }
}
